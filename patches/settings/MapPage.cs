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

    static readonly string[] Corners = ["Top left", "Top right", "Bottom left", "Bottom right"];
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

            int corner = System.Math.Clamp(Map.MinimapCorner, 0, 3);
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Corner", ref corner, Corners, Corners.Length))
            {
                Map.MinimapCorner = corner;
                PatchSettings.Set(Map.CornerKey, corner);
            }

            int size = Map.MinimapSize;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderInt("Size", ref size, 80, 480, "%d px"))
            {
                Map.MinimapSize = size;
                PatchSettings.Set(Map.SizeKey, size);
            }

            int radius = Map.MinimapRadius;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderInt("Range", ref radius, 3, 40, "%d tiles"))
            {
                Map.MinimapRadius = radius;
                PatchSettings.Set(Map.RadiusKey, radius);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Tiles either side of you. A tile is 2048 world units.");

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
