using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2;

/// <summary>
/// The full-screen map: the whole area at once, over the game, opened with the
/// pad's touchpad button or with M.
///
/// **This is the map a player opens, and patches/MapPanel.cs is the instrument.**
/// The docked panel exists to settle what a tile record's unattributed bytes mean
/// — it has a toolbar, a zoom slider, a hover readout printing all ten bytes, and
/// a title bar to dock it by. None of that is something you want to look at with
/// a controller in your hands halfway down a corridor, and a windowed panel over
/// a 320x240 picture reads as a debugger rather than as part of the game. This
/// one has no chrome at all: the game dims, the area's floor plan is laid over
/// it, and the same button puts it away.
///
/// **It fits the area rather than following the player, until the area will not
/// fit legibly** — which is the difference between a map and a minimap. The view
/// is scaled to the occupied extent of the drawn half (<c>Map.Extents</c>,
/// computed with the four-a-second grid copy) and centred on that box, so nothing
/// moves as you walk except the dot and the picture is stable enough to read a
/// route off. A view that slid under the player would be the minimap again, only
/// larger.
///
/// **The floor on the scale is why that is only "until".** The extent was
/// expected to be a fraction of the 80x80 grid and measured as the whole of it:
/// areas 0 and 1 both run x 0..79, z 0..79 on both halves, so fitting the extent
/// is fitting the grid, and in a small window that is a few pixels a tile. Below
/// <see cref="MinCell"/> the fit is abandoned and the map is centred on the
/// player's **tile** instead — the same quantisation the dot has, so the picture
/// steps a square at a time rather than sliding.
///
/// **It takes no input.** The window carries <c>NoInputs</c>, so the mouse still
/// reaches the game and the ImGui menus behind it, and there is nothing on it to
/// click: no zoom, no pan, no toolbar. Everything it draws is decided by the
/// settings under Gameplay ▸ Map, which the other two viewports already share.
///
/// The palette, the tiles, the markers and the player are patches/MapRender.cs,
/// so this is a third viewport rather than a third map — it differs from the
/// minimap only in its rectangle, its scale and its scrim.
///
/// Session-only, deliberately: it is registered after
/// <c>ConfigManager.ApplyViewToPanels</c> has run, so a saved open state is never
/// applied and the map cannot come up over the boot logo.
///
/// **Never judged by eye**: whether the fitted scale is readable in a large area,
/// whether the scrim is dark enough to read the tiles over a bright scene, and
/// whether the whole-area view is what a player wants over a view centred on
/// themselves.
/// </summary>
public sealed class MapFullscreen : IFloatingPanel
{
    public static readonly MapFullscreen Instance = new();
    MapFullscreen() { }

    public string Name => "kf2mapfull";
    public string TitleKey => "menu.game.mapfs";

    /// <summary>Gated on the feature switch as well as on its own state, so
    /// turning the map off under Gameplay takes the view with it.</summary>
    bool _open;
    public bool IsOpen
    {
        get => _open && Map.Enabled;
        set => _open = value;
    }

    /// <summary>How opaque the scrim over the game is. The map is a screen you
    /// stop to read rather than a heads-up overlay — that is what separates it
    /// from the minimap, which has its own opacity setting for the opposite
    /// reason — so the game is dimmed nearly out rather than shown through.</summary>
    const float Scrim = 0.93f;

    /// <summary>Pixels a tile, before the interface scale. The lower bound is
    /// where the fit gives up and the view centres on the player instead; the
    /// upper stops a one-room area from filling the screen with four enormous
    /// squares.</summary>
    const float MinCell = 6f, MaxCell = 22f;

    public void Draw()
    {
        if (!Map.Refresh()) return;

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.Pos);
        ImGui.SetNextWindowSize(vp.Size);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        // NoBringToFrontOnFocus is absent for the reason patches/MapOverlay.cs
        // records at length: a window carrying it is created at the *front* of
        // g.Windows, which is the back of the display order, and would be drawn
        // underneath the dockspace's opaque background. NoBackground because the
        // scrim is drawn by hand, at one opacity, in one place.
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (ImGui.Begin("##kf2mapfull", flags))
        {
            var p0 = ImGui.GetWindowPos();
            var p1 = new Vector2(p0.X + vp.Size.X, p0.Y + vp.Size.Y);
            var dl = ImGui.GetWindowDrawList();

            dl.AddRectFilled(p0, p1, MapRender.Fade(MapRender.Ground, Scrim));

            float margin = 24f * Theme.Scale;
            float header = ImGui.GetFontSize() * 2.2f;
            var c0 = new Vector2(p0.X + margin, p0.Y + margin + header);
            var c1 = new Vector2(p1.X - margin, p1.Y - margin - header);

            if (c1.X - c0.X >= 32 && c1.Y - c0.Y >= 32)
            {
                DrawArea(dl, c0, c1);
                Chrome(dl, p0, p1, margin);
            }
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// <summary>The area, scaled to fit the content rectangle — or, where that
    /// would be too fine to read, at the floor scale and centred on the player's
    /// tile. See the class comment.</summary>
    void DrawArea(ImDrawListPtr dl, Vector2 c0, Vector2 c1)
    {
        int half = Map.HalfOffset;
        var ext = Map.Extents[half / Map.HalfBytes];

        // A half with nothing drawn in it is a legitimate view — the settings can
        // pin the floor you are not on — so fall back to the whole grid rather
        // than to a division by zero.
        int x0 = ext.Any ? ext.X0 : 0, x1 = ext.Any ? ext.X1 : Map.Span - 1;
        int z0 = ext.Any ? ext.Z0 : 0, z1 = ext.Any ? ext.Z1 : Map.Span - 1;

        // One tile of air round the plan, so the outermost wall is not flush with
        // the edge of the screen.
        float wide = x1 - x0 + 3, tall = z1 - z0 + 3;
        float lo = MinCell * Theme.Scale, hi = MaxCell * Theme.Scale;

        float fit = MathF.Min((c1.X - c0.X) / wide, (c1.Y - c0.Y) / tall);
        float cell = Math.Clamp(fit, lo, hi);

        var centre = new Vector2((c0.X + c1.X) * 0.5f, (c0.Y + c1.Y) * 0.5f);

        // Where the view is centred, in the two coordinates MapRender works in: a
        // tile number along X and a *screen row* down the page, because the map is
        // the plane seen from above and screen Y runs along -Z (Map.RowF). The
        // centre of tiles Z0..Z1 is therefore row Span - (Z0 + Z1 + 1) / 2, not
        // (Z0 + Z1) / 2 — getting that wrong mirrors the map, which is the defect
        // this patch was corrected for once already.
        float xc, rc;
        if (fit >= lo)
        {
            xc = (x0 + x1 + 1) * 0.5f;
            rc = Map.Span - (z0 + z1 + 1) * 0.5f;
        }
        else
        {
            // The area will not fit at a legible scale, so the player is the
            // centre — on their tile rather than their position, so the map steps
            // a square at a time like the dot on it does.
            xc = Map.TileOf(Map.PlayerX) + 0.5f;
            rc = Map.RowOf(Map.TileOf(Map.PlayerZ)) + 0.5f;
        }

        var origin = new Vector2(centre.X - xc * cell, centre.Y - rc * cell);

        // What can land in the content rectangle, taken from the origin rather
        // than from the extent: the two agree while the whole area fits and do not
        // once the view is centred on the player, and drawing 6,400 tiles to fill
        // a window that holds a few hundred is the thing this avoids.
        int wx0 = (int)MathF.Floor((c0.X - origin.X) / cell);
        int wr0 = (int)MathF.Floor((c0.Y - origin.Y) / cell);
        int wx1 = (int)MathF.Ceiling((c1.X - origin.X) / cell);
        int wr1 = (int)MathF.Ceiling((c1.Y - origin.Y) / cell);

        dl.PushClipRect(c0, c1, true);
        MapRender.Draw(dl, origin, cell, wx0, wr0, wx1, wr1,
                       half, Map.Shade, Map.Walls, cell >= 8f, MapFog.Predicate);
        MapRender.DrawMarkers(dl, origin, cell, half, MapFog.Predicate,
                              Math.Clamp(cell * 0.35f, 3f, 9f));
        MapRender.DrawPlayer(dl, origin, cell, MathF.Max(5f, cell * 0.7f), Map.PlayerDot);
        dl.PopClipRect();
    }

    /// <summary>Two lines of text and nothing else: what area this is, and how to
    /// put it away. Drawn straight onto the list because the window has no layout
    /// — <c>NoInputs</c> means there is nothing for ImGui's cursor to serve.</summary>
    void Chrome(ImDrawListPtr dl, Vector2 p0, Vector2 p1, float margin)
    {
        uint text = ImGui.GetColorU32(ImGuiCol.Text);
        uint dim  = MapRender.Fade(text, 0.45f);

        string title = $"Area {Map.Area}  ·  {(Map.HalfOffset == 0 ? "lower" : "upper")} floor";
        dl.AddText(new Vector2(p0.X + margin, p0.Y + margin * 0.6f), text, title);

        string hint = Map.PadButton == Map.PadNone
            ? "M to close"
            : $"{Map.PadName(Map.PadButton)} or M to close";
        var size = ImGui.CalcTextSize(hint);
        dl.AddText(new Vector2((p0.X + p1.X - size.X) * 0.5f, p1.Y - margin * 0.6f - size.Y),
                   dim, hint);
    }
}
