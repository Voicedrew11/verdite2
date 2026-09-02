using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2;

/// <summary>
/// The corner minimap: the tiles around the player, north-up, over the game
/// picture.
///
/// An <c>IFloatingPanel</c> rather than an <c>IPanel</c> because that interface
/// exists precisely for a panel that should not count towards the dockspace's
/// open-panel layout — this one is chrome pinned to a corner, not a window
/// someone docks.
///
/// <c>IsOpen</c> is the *feature switch*, because <c>PanelManager.DrawPanels</c>
/// only calls <c>Draw</c> on an open panel: the minimap is on when the setting is
/// on and an area is running, and closed the rest of the time. That is what keeps
/// it off the title screen and out of a load, and it is why the setting is read
/// here rather than tested inside Draw. **The full-screen map closes it too** — a
/// corner minimap over a map of the same area, drawn from the same tables at a
/// different scale, is two answers to one question.
///
/// **North-up, matching the full map.** A rotating minimap would disagree with
/// the full map about which way the area faces, and a maze is easier to hold in
/// your head when north stays put.
///
/// Off by default. The mechanism is measured but the picture is not — nobody has
/// yet judged whether the size, the corner or the radius are usable in play —
/// and this repo's rule for a picture nobody has looked at is that it defaults
/// off. N toggles it; the knobs — corner, size, range, **shape** (square or
/// circle) and **opacity** — are under Settings > Gameplay > Map. Shape and
/// opacity default to the picture that shipped, a fully opaque square, for the
/// same reason: neither has been judged by eye.
/// </summary>
public sealed class MapOverlay : IFloatingPanel
{
    public static readonly MapOverlay Instance = new();
    MapOverlay() { }

    public string Name => "kf2minimap";

    /// <summary>
    /// Purely derived: the minimap is on when the setting is on and the feature
    /// is, and <c>PanelManager.DrawPanels</c> skips a closed panel, so this is
    /// both the switch and the "only while an area is running" test.
    ///
    /// **The setter is deliberately a no-op.** The runtime persists every panel's
    /// open state in <c>interface.ini</c> under <c>Panels.&lt;name&gt;</c> and writes
    /// it back through <c>ApplyViewToPanels</c> and <c>ResetView</c> — so a setter
    /// that wrote the setting would give the minimap two stores that disagree, and
    /// "Reset view" would silently turn it on or off. The setting is the one
    /// store; N and the Gameplay page are how it moves.
    /// </summary>
    public bool IsOpen
    {
        get => Map.Enabled && Map.Minimap && !MapFullscreen.Instance.IsOpen;
        set { }
    }

    /// <summary>The gap the minimap keeps from the edges it is pinned to, in
    /// screen pixels: the setting scaled the same way the size is, so the two
    /// stay in proportion as the interface scale moves. Clamped rather than
    /// trusted, since it is a saved number and a huge one would push the map off
    /// the screen with no control left on it to bring it back.</summary>
    static float Pad => Math.Clamp(Map.MinimapPad, 0, 400) * Theme.Scale;

    public void Draw()
    {
        if (!Map.Refresh()) return;

        float side = Math.Clamp(Map.MinimapSize, 80, 640) * Theme.Scale;
        var vp = ImGui.GetMainViewport();
        var work = vp.WorkPos;
        var wsz = vp.WorkSize;

        // 0..3 were a bitmask (bit 0 the right edge, bit 1 the bottom) and the
        // numbering is kept because it is what a player's interface.ini already
        // holds; 4 is centred on the top edge, which no bit can say, so the
        // anchor is switched on rather than masked. An unknown value falls back
        // to the shipped top-right rather than landing off-screen.
        float pad = Pad;
        float x = Map.MinimapCorner switch
        {
            0 or 2 => work.X + pad,
            4      => work.X + (wsz.X - side) * 0.5f,
            _      => work.X + wsz.X - side - pad,
        };
        float y = Map.MinimapCorner is 2 or 3
            ? work.Y + wsz.Y - side - pad
            : work.Y + pad;

        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(side, side));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        // **NoBringToFrontOnFocus is deliberately absent, and that is the whole
        // difference between an overlay and nothing at all.** ImGui reads that
        // flag once, in CreateNewWindow, and a window carrying it is pushed to
        // the *front of g.Windows* -- the back of the display order -- rather
        // than appended to the end of it. This window also has NoInputs and
        // NoFocusOnAppearing, so nothing ever focuses it and it has no second
        // route forward; and OutputPanel.Draw pushes an **opaque** black WindowBg
        // and fills the dockspace. So the minimap was drawn, correctly, every
        // frame, underneath the game picture. The full map is an ordinary docked
        // panel and never saw it, which is why the map worked and the overlay did
        // not. Without the flag the window is appended instead: it lands above
        // everything that already exists, the dockspace host cannot displace it
        // (##DockHost carries the flag itself, so focusing a docked panel does
        // not bring the dock tree forward), and a popup opened later is created
        // after it and still covers it, which is what should happen.
        //
        // **NoBackground, and the ground is drawn by hand instead.** The window's
        // own background is a rectangle ImGui fills before the draw list runs, so
        // it can be neither round nor faded independently of the tiles — a circle
        // would sit inside a visible translucent square, and the opacity setting
        // would have two stores that disagree. One fill, in the shape and at the
        // opacity the settings ask for, is the whole of the map's ground.
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (ImGui.Begin("##kf2minimap", flags))
        {
            var p0 = ImGui.GetWindowPos();
            var p1 = new Vector2(p0.X + side, p0.Y + side);
            var dl = ImGui.GetWindowDrawList();

            float alpha = Math.Clamp(Map.MinimapOpacity, 0.15f, 1f);
            bool round = Map.MinimapShape == 1;

            var centre = new Vector2(p0.X + side * 0.5f, p0.Y + side * 0.5f);
            float r = side * 0.5f;

            uint ground = MapRender.Fade(MapRender.Ground, alpha);
            if (round) dl.AddCircleFilled(centre, r, ground, 0);
            else       dl.AddRectFilled(p0, p1, ground);

            int radius = Math.Clamp(Map.MinimapRadius, 3, 40);
            float cell = side / (radius * 2f + 1f);

            // Tile (0,0)'s corner, placed so the player's sub-tile position sits in
            // the middle: the map slides continuously rather than jumping a tile.
            var origin = new Vector2(centre.X - Map.TileF(Map.PlayerX) * cell,
                                     centre.Y - Map.RowF(Map.PlayerZ) * cell);

            // The window is the rows around the player's, not the tiles: screen Y
            // runs along -Z, so that the map is the plane seen from above.
            int cx = Map.TileOf(Map.PlayerX), cz = Map.RowOf(Map.TileOf(Map.PlayerZ));

            dl.PushClipRect(p0, p1, true);
            MapRender.Draw(dl, origin, cell,
                           cx - radius - 1, cz - radius - 1, cx + radius + 1, cz + radius + 1,
                           Map.HalfOffset, Map.Shade, Map.Walls, false, MapFog.Predicate,
                           alpha, round ? centre : null, round ? r : 0f);
            // Markers do not take the opacity: the setting is there so the
            // *ground* stops hiding the game, and a creature you cannot see is the
            // one thing an overlay map is for. Same exemption the arrow has.
            MapRender.DrawMarkers(dl, origin, cell, Map.HalfOffset, MapFog.Predicate,
                                  Math.Clamp(cell * 0.35f, 2.5f, 7f),
                                  round ? centre : null, round ? r : 0f);
            MapRender.DrawPlayer(dl, origin, cell, MathF.Max(5f, cell * 0.7f), Map.PlayerDot);
            dl.PopClipRect();

            // The border keeps the map's own edge legible against whatever the game
            // is drawing behind it, so it fades with the rest.
            uint border = MapRender.Fade(ImGui.GetColorU32(ImGuiCol.Border), alpha);
            if (round) dl.AddCircle(centre, r, border, 0, 1.5f);
            else       dl.AddRect(p0, p1, border);
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }
}
