using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The map's knobs, under **Gameplay** rather than Video.
///
/// A map is not a choice about how the picture is made — it is a thing the game
/// did not have and now does, which is the same test auto reload passes and the
/// reason the port added the Gameplay section at all. It gets its own "Map"
/// heading rather than joining auto reload's, because the two share nothing but
/// the tab.
///
/// The mechanism is patches/Map.cs; the two viewports are patches/MapPanel.cs and
/// patches/MapOverlay.cs.
/// </summary>
public sealed class MapPage : IPatchPage
{
    public string Id => "map";
    public string Title => "Map";

    // The order is the stored value's, not a tidy one: 0..3 are the bitmask the
    // minimap shipped with and are already in players' interface.ini, so "Top
    // centre" is appended as 4 rather than slotted in beside the other two top
    // entries.
    static readonly string[] Corners =
        ["Top left", "Top right", "Bottom left", "Bottom right", "Top centre"];
    static readonly string[] Shapes  = ["Square", "Circle"];
    static readonly string[] Floors  = ["Follow the player", "Lower", "Upper"];

    public void Draw()
    {
        bool on = Map.Enabled;
        if (ImGui.Checkbox("Map", ref on))
        {
            Map.SetEnabled(on);
            PatchSettings.Set(Map.OnKey, on);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A map of the area you are in, drawn from the game's own 80x80 tile " +
                             "grid. M opens the full map, N toggles the corner minimap.");

        if (!Map.Enabled) return;

        bool mini = Map.Minimap;
        if (ImGui.Checkbox("Corner minimap", ref mini)) Map.SetMinimap(mini);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Always on over the game picture while you are in an area. N toggles it.");

        if (Map.Minimap)
        {
            ImGui.Indent();

            int corner = System.Math.Clamp(Map.MinimapCorner, 0, Corners.Length - 1);
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Position", ref corner, Corners, Corners.Length))
            {
                Map.MinimapCorner = corner;
                PatchSettings.Set(Map.CornerKey, corner);
            }

            int pad = Map.MinimapPad;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderInt("Edge padding", ref pad, 0, 200, "%d px"))
            {
                Map.MinimapPad = pad;
                PatchSettings.Set(Map.PadKey, pad);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How far the minimap stands off the edges it is pinned to. " +
                                 "Scaled with the interface, like the size. Top centre spends " +
                                 "it on the top edge only.");

            int shape = System.Math.Clamp(Map.MinimapShape, 0, 1);
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Shape", ref shape, Shapes, Shapes.Length))
            {
                Map.MinimapShape = shape;
                PatchSettings.Set(Map.ShapeKey, shape);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A circle cuts the corners off the same view. The tiles are cut " +
                                 "to the disc a tile at a time, so its edge is stepped by up to " +
                                 "one tile.");

            // The range is MapOverlay's own clamp, not a tidier number: a
            // slider that stops short of what the code allows is a control that
            // cannot reach a legal setting, and 480 against a clamp of 640 was
            // exactly that.
            int size = Map.MinimapSize;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderInt("Size", ref size, 80, 640, "%d px"))
            {
                Map.MinimapSize = size;
                PatchSettings.Set(Map.SizeKey, size);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The minimap's side in logical pixels, scaled by the interface " +
                                 "scale — so a large scale draws it larger than this says.");

            int radius = Map.MinimapRadius;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderInt("Range", ref radius, 3, 40, "%d tiles"))
            {
                Map.MinimapRadius = radius;
                PatchSettings.Set(Map.RadiusKey, radius);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Tiles either side of you. A tile is 2048 world units.");

            // Opacity is the ground and the tiles, not the arrow: see
            // MapRender.DrawPlayer. 1 is what shipped, so the default changes
            // nothing until a player asks it to.
            float opacity = Map.MinimapOpacity;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderFloat("Opacity", ref opacity, 0.15f, 1f, "%.2f"))
            {
                Map.MinimapOpacity = opacity;
                PatchSettings.Set(Map.OpacityKey, opacity);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How solid the map is drawn over the game. The player's arrow " +
                                 "stays fully visible whatever this says.");

            ImGui.Unindent();
        }

        bool shade = Map.Shade;
        if (ImGui.Checkbox("Shade by height", ref shade))
        {
            Map.Shade = shade;
            PatchSettings.Set(Map.ShadeKey, shade);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Colour each tile by its height byte, so stairs, ledges and the two " +
                             "stacked floors read at a glance.");

        bool walls = Map.Walls;
        if (ImGui.Checkbox("Mark sight-blocking tiles", ref walls))
        {
            Map.Walls = walls;
            PatchSettings.Set(Map.WallsKey, walls);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Darken the tiles the game's own visibility flood stops at. Whether " +
                             "that bit means a wall is not settled — the full map's hover readout " +
                             "shows the raw bytes.");

        // Fog of war: patches/MapFog.cs. Off by default, for the reason the whole
        // port uses -- the picture has not been judged by eye.
        bool fog = MapFog.Enabled;
        if (ImGui.Checkbox("Fog of war", ref fog))
        {
            MapFog.SetEnabled(fog);
            PatchSettings.Set(MapFog.OnKey, fog);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show only the tiles you have seen. It remembers what the game's own " +
                             "visibility grid showed you, per save slot, and keeps it between " +
                             "sessions in a file beside the memory card.");

        if (MapFog.Enabled)
        {
            ImGui.Indent();
            if (ImGui.Button("Forget this area")) MapFog.ForgetArea();
            ImGui.SameLine();
            if (ImGui.Button("Reveal this area")) MapFog.RevealArea();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("For looking at the two pictures side by side without walking " +
                                 "the area twice. Both are written to the store immediately.");
            ImGui.Unindent();
        }

        // A tile record holds two stacked floors and the game says which one you
        // are on (u16[0x801D9C8E]). Pinning one is for looking at the other.
        int floor = System.Math.Clamp(Map.Floor + 1, 0, 2);
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Floor", ref floor, Floors, Floors.Length))
        {
            Map.Floor = floor - 1;
            PatchSettings.Set(Map.FloorKey, Map.Floor);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each tile holds two stacked floors. By default the map shows the one " +
                             "you are standing on.");
    }
}
