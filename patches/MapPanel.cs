using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2;

/// <summary>
/// The full map: the whole 80x80 grid of the loaded area, centred on the player,
/// with pan, zoom and a hover readout.
///
/// **The readout is the instrument, not decoration.** It names the tile under the
/// cursor and prints all ten of its bytes, which is the only way short of a
/// person looking at the screen to settle what the record's unattributed fields
/// mean — whether the floor plan should be drawn from the model index at +0 (the
/// renderer's own test, which is what it uses now) or from the collision bytes at
/// +2 and +3, and whether bit 0x80 of +4 is the wall it behaves like in the
/// visibility flood or the "see through" docs/WIDESCREEN.md calls it.
///
/// Registered from patches/Map.cs on RuntimeReadyEvent, with a menu entry —
/// panels do not auto-populate the menu bar, MainMenuBar declares every built-in
/// one by hand, so without the entry the map would be hotkey-only.
/// </summary>
public sealed class MapPanel : IPanel
{
    public static readonly MapPanel Instance = new();
    MapPanel() { }

    public string Name => "kf2map";
    public string TitleKey => "menu.game.map";

    /// <summary>Gated on the feature switch as well as on the window's own state,
    /// so turning the map off under Gameplay takes the window with it.</summary>
    bool _open;
    public bool IsOpen
    {
        get => _open && Map.Enabled;
        set => _open = value;
    }

    float _zoom = 7f;          // pixels a tile
    Vector2 _pan;              // in pixels, from the centred position
    bool _follow = true;
    bool _grid = true;

    public void Draw()
    {
        bool open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(560, 620), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(this.Title(), ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;

        // Reads the game directly: a panel draws inside the game's own VSync call
        // (LibEtc.VSync -> PresentFrame -> HostWindow.Present -> DrawPanels), all
        // one thread, so this is the memory the game left there.
        if (!Map.Refresh())
        {
            ImGui.TextDisabled("No area loaded.");
            ImGui.TextWrapped("The map appears once a save is loaded or a new game has started.");
            ImGui.End();
            return;
        }

        DrawToolbar();
        DrawCanvas();
        ImGui.End();
    }

    void DrawToolbar()
    {
        ImGui.Text($"Area {Map.Area}");
        ImGui.SameLine();
        ImGui.TextDisabled($"| tile {Map.TileOf(Map.PlayerX)},{Map.TileOf(Map.PlayerZ)} " +
                           $"| {(Map.HalfOffset == 0 ? "lower" : "upper")} floor " +
                           $"| {Map.Occupied} halves");

        ImGui.SetNextItemWidth(160);
        ImGui.SliderFloat("Zoom", ref _zoom, 2f, 24f, "%.1f px/tile");

        ImGui.SameLine();
        ImGui.Checkbox("Follow", ref _follow);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keep the player centred. Drag the map to look elsewhere; " +
                             "that turns this off.");

        ImGui.SameLine();
        ImGui.Checkbox("Grid", ref _grid);

        ImGui.SameLine();
        bool shade = Map.Shade;
        if (ImGui.Checkbox("Height", ref shade))
        {
            Map.Shade = shade;
            Settings.PatchSettings.Set(Map.ShadeKey, shade);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shade each tile by its height byte, so stairs and ledges read.");
    }

    void DrawCanvas()
    {
        var size = ImGui.GetContentRegionAvail();
        if (size.X < 16 || size.Y < 16) return;

        var p0 = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(p0, new Vector2(p0.X + size.X, p0.Y + size.Y), MapRender.Ground);

        // An invisible button owns the region, so dragging pans rather than moving
        // the window and the wheel zooms rather than scrolling it.
        ImGui.InvisibleButton("##kf2mapcanvas", size,
                              ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var d = ImGui.GetIO().MouseDelta;
            if (d.X != 0 || d.Y != 0) { _pan += d; _follow = false; }
        }

        if (hovered && ImGui.GetIO().MouseWheel != 0)
            _zoom = Math.Clamp(_zoom * (1f + ImGui.GetIO().MouseWheel * 0.12f), 2f, 24f);

        if (_follow) _pan = Vector2.Zero;

        // The grid's top-left corner in screen space: the player put at the middle
        // of the canvas, plus the pan. Its row is Map.RowF, not the tile — screen
        // Y runs along -Z so that the map is the plane seen from above.
        var centre = new Vector2(p0.X + size.X * 0.5f, p0.Y + size.Y * 0.5f);
        var origin = new Vector2(
            centre.X - Map.TileF(Map.PlayerX) * _zoom + _pan.X,
            centre.Y - Map.RowF(Map.PlayerZ) * _zoom + _pan.Y);

        // Only the tiles that can land in the canvas — 6,400 rects is affordable
        // but pointless when the viewport holds a hundred.
        int x0 = (int)MathF.Floor((p0.X - origin.X) / _zoom);
        int z0 = (int)MathF.Floor((p0.Y - origin.Y) / _zoom);
        int x1 = (int)MathF.Ceiling((p0.X + size.X - origin.X) / _zoom);
        int z1 = (int)MathF.Ceiling((p0.Y + size.Y - origin.Y) / _zoom);

        dl.PushClipRect(p0, new Vector2(p0.X + size.X, p0.Y + size.Y), true);
        MapRender.Draw(dl, origin, _zoom, x0, z0, x1, z1,
                       Map.HalfOffset, Map.Shade, Map.Walls, _grid, MapFog.Predicate);
        MapRender.DrawPlayer(dl, origin, _zoom, MathF.Max(5f, _zoom * 0.7f));
        dl.PopClipRect();

        if (hovered) Readout(origin);
    }

    /// <summary>The ten bytes of the tile under the cursor. See the class comment:
    /// this is how the record's unattributed fields get settled.</summary>
    void Readout(Vector2 origin)
    {
        var mp = ImGui.GetIO().MousePos;
        int tx = (int)MathF.Floor((mp.X - origin.X) / _zoom);
        int row = (int)MathF.Floor((mp.Y - origin.Y) / _zoom);
        if ((uint)tx >= Map.Span || (uint)row >= Map.Span) return;
        int tz = Map.RowOf(row);

        ImGui.BeginTooltip();
        ImGui.Text($"tile {tx},{tz}   world {tx * Map.TileUnits}..{(tx + 1) * Map.TileUnits - 1} x " +
                   $"{tz * Map.TileUnits}..{(tz + 1) * Map.TileUnits - 1}");

        for (int half = 0; half < Map.Stride; half += Map.HalfBytes)
        {
            byte model = Map.Byte(tx, tz, half + Map.Model);
            byte h     = Map.Byte(tx, tz, half + Map.HeightByte);
            byte coll  = Map.Byte(tx, tz, half + Map.Collide);
            byte shape = Map.Byte(tx, tz, half + Map.Shape);
            byte flags = Map.Byte(tx, tz, half + Map.Flags);

            ImGui.Separator();
            ImGui.Text($"{(half == 0 ? "lower" : "upper")}: " +
                       $"model {model:X2} height {h:X2} coll {coll:X2} shape {shape:X2} flags {flags:X2}");
            ImGui.TextDisabled(model >= Map.NotDrawn
                ? "  not drawn"
                : $"  floor Y {-(h << 7)}{((flags & Map.StopsFlood) != 0 ? ", stops the visibility flood" : "")}");
        }
        ImGui.EndTooltip();
    }
}
