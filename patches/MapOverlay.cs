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
/// here rather than tested inside Draw.
///
/// **North-up, matching the full map.** A rotating minimap would disagree with
/// the full map about which way the area faces, and a maze is easier to hold in
/// your head when north stays put.
///
/// Off by default. The mechanism is measured but the picture is not — nobody has
/// yet judged whether the size, the corner or the radius are usable in play —
/// and this repo's rule for a picture nobody has looked at is that it defaults
/// off. N toggles it; the knobs are under Settings > Gameplay > Map.
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
        get => Map.Enabled && Map.Minimap;
        set { }
    }

    const float Pad = 12f;

    public void Draw()
    {
        if (!Map.Refresh()) return;

        float side = Math.Clamp(Map.MinimapSize, 80, 640) * Theme.Scale;
        var vp = ImGui.GetMainViewport();
        var work = vp.WorkPos;
        var wsz = vp.WorkSize;

        float x = (Map.MinimapCorner & 1) == 0 ? work.X + Pad : work.X + wsz.X - side - Pad;
        float y = (Map.MinimapCorner & 2) == 0 ? work.Y + Pad : work.Y + wsz.Y - side - Pad;

        ImGui.SetNextWindowPos(new Vector2(x, y));
        ImGui.SetNextWindowSize(new Vector2(side, side));
        ImGui.SetNextWindowBgAlpha(0.72f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoDocking;

        if (ImGui.Begin("##kf2minimap", flags))
        {
            var p0 = ImGui.GetWindowPos();
            var p1 = new Vector2(p0.X + side, p0.Y + side);
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(p0, p1, MapRender.Ground);

            int radius = Math.Clamp(Map.MinimapRadius, 3, 40);
            float cell = side / (radius * 2f + 1f);

            // Tile (0,0)'s corner, placed so the player's sub-tile position sits in
            // the middle: the map slides continuously rather than jumping a tile.
            var centre = new Vector2(p0.X + side * 0.5f, p0.Y + side * 0.5f);
            var origin = new Vector2(centre.X - Map.TileF(Map.PlayerX) * cell,
                                     centre.Y - Map.RowF(Map.PlayerZ) * cell);

            // The window is the rows around the player's, not the tiles: screen Y
            // runs along -Z, so that the map is the plane seen from above.
            int cx = Map.TileOf(Map.PlayerX), cz = Map.RowOf(Map.TileOf(Map.PlayerZ));

            dl.PushClipRect(p0, p1, true);
            MapRender.Draw(dl, origin, cell,
                           cx - radius - 1, cz - radius - 1, cx + radius + 1, cz + radius + 1,
                           Map.HalfOffset, Map.Shade, Map.Walls, false, null);
            MapRender.DrawPlayer(dl, origin, cell, MathF.Max(5f, cell * 0.7f));
            dl.PopClipRect();

            dl.AddRect(p0, p1, ImGui.GetColorU32(ImGuiCol.Border));
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }
}
