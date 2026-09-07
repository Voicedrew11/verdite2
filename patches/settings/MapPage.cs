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

    // 0 native, 1 blueprint: the stored value, the game's own board first
    // because it is the default. See Map.Style.
    static readonly string[] Styles =
        ["The game's own map", "Blueprint"];

    // 0 dot, 1 arrow: the stored value, dot first because it is the default.
    static readonly string[] Marks =
        ["Marker in the square you are in", "Arrow showing your heading"];

    // The pad button that opens the full-screen map, and the SDL index each entry
    // stores. Not every pad has every one of them -- a controller with no touchpad
    // never sends button 20 -- and nothing here can ask the pad what it has, so
    // they are offered as a list rather than probed.
    static readonly string[] PadButtons =
        ["Touchpad (DualSense / DualShock 4)", "L3 (left stick click)", "R3 (right stick click)",
         "Select / Back", "None"];
    static readonly int[] PadValues =
        [Map.PadTouchpad, Map.PadL3, Map.PadR3, Map.PadSelect, Map.PadNone];
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
            ImGui.SetTooltip("A map of the area you are in. M opens it, N toggles the minimap.");

        if (!Map.Enabled) return;

        // The full-screen map's controller binding. It comes off the raw host
        // gamepad rather than the PSX pad, which is the only reason the touchpad
        // is reachable at all -- the PS1 had no such button, so nothing downstream
        // has a slot for it.
        int padIdx = System.Array.IndexOf(PadValues, Map.PadButton);
        if (padIdx < 0) padIdx = PadValues.Length - 1;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Open the full-screen map with", ref padIdx, PadButtons, PadButtons.Length))
            Map.SetPadButton(PadValues[padIdx]);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The pad button that opens the map. M always does too.");

        // How the whole map is drawn, on every viewport at once. Native is the
        // default: see Map.Style and MapRender.DrawNative.
        int style = System.Math.Clamp(Map.Style, 0, Styles.Length - 1);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Style", ref style, Styles, Styles.Length))
            Map.SetStyle(style);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How the map looks: the game's own map screen, or a blueprint.");

        // How the player is drawn, on every viewport at once. The dot is the
        // default: see Map.PlayerMark and MapRender.DrawPlayerDot.
        int mark = System.Math.Clamp(Map.PlayerMark, 0, Marks.Length - 1);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("You are here", ref mark, Marks, Marks.Length))
            Map.SetPlayerMark(mark);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How much the map tells you: your square, or your exact heading.");

        // Whether the map stops the game. Beside the pad binding rather than down
        // with the minimap's knobs, because it is a property of the full-screen
        // map those two controls are about.
        bool pause = Map.Pause;
        if (ImGui.Checkbox("Pause while the full-screen map is open", ref pause))
        {
            Map.Pause = pause;
            PatchSettings.Set(Map.PauseKey, pause);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The world stands still while the full map is up.");

        bool mini = Map.Minimap;
        if (ImGui.Checkbox("Corner minimap", ref mini)) Map.SetMinimap(mini);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A small map in the corner, always up. N toggles it.");

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
                ImGui.SetTooltip("How far the minimap sits from the edge of the screen.");

            int shape = System.Math.Clamp(Map.MinimapShape, 0, 1);
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Shape", ref shape, Shapes, Shapes.Length))
            {
                Map.MinimapShape = shape;
                PatchSettings.Set(Map.ShapeKey, shape);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Square, or a circle with the corners cut off.");

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
                ImGui.SetTooltip("How large the minimap is drawn.");

            int radius = Map.MinimapRadius;
            ImGui.SetNextItemWidth(180);
            if (ImGui.SliderInt("Range", ref radius, 3, 40, "%d tiles"))
            {
                Map.MinimapRadius = radius;
                PatchSettings.Set(Map.RadiusKey, radius);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("How much of the area around you the minimap shows.");

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
                ImGui.SetTooltip("How solid the minimap is over the game. Your marker stays visible.");

            ImGui.Unindent();
        }

        bool shade = Map.Shade;
        if (ImGui.Checkbox("Shade by height", ref shade))
        {
            Map.Shade = shade;
            PatchSettings.Set(Map.ShadeKey, shade);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Colour the floor by height, so stairs and ledges stand out.");

        // The marker layer: patches/MapMarkers.cs. On by default, because unlike
        // the minimap it adds information to a picture that has been judged rather
        // than a picture of its own -- and because a map that shows the maze but
        // not what is standing in it is the smaller half of the feature.
        // **Outside the block below, and deliberately.** A save point is not one
        // of the marker classes: it is independent of "Objects" — a player who
        // turns the prop squares off to unclutter the plan is exactly the player
        // who still wants to find a save point — and independent of the layer
        // switch itself, which is the same argument one level up. Nesting it
        // under "show what is in the area" is what made the S's invisible for
        // anyone who had that off.
        bool saves = MapMarkers.Saves;
        if (ImGui.Checkbox("Save points", ref saves))
        {
            MapMarkers.Saves = saves;
            PatchSettings.Set(MapMarkers.SavesKey, saves);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Marks the rooms you can save in with an S.");

        bool markers = MapMarkers.Enabled;
        if (ImGui.Checkbox("Show what is in the area", ref markers))
        {
            MapMarkers.Enabled = markers;
            PatchSettings.Set(MapMarkers.OnKey, markers);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show creatures, props and effects on the map.");

        if (MapMarkers.Enabled)
        {
            ImGui.Indent();

            bool creatures = MapMarkers.Creatures;
            if (ImGui.Checkbox("Creatures", ref creatures))
            {
                MapMarkers.Creatures = creatures;
                PatchSettings.Set(MapMarkers.CreaturesKey, creatures);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Red triangles, where you can see right now.");

            bool objects = MapMarkers.Objects;
            if (ImGui.Checkbox("Objects", ref objects))
            {
                MapMarkers.Objects = objects;
                PatchSettings.Set(MapMarkers.ObjectsKey, objects);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Blue squares: doors, levers, chests and other props.");

            bool effects = MapMarkers.Effects;
            if (ImGui.Checkbox("Effects and projectiles", ref effects))
            {
                MapMarkers.Effects = effects;
                PatchSettings.Set(MapMarkers.EffectsKey, effects);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Purple diamonds: spells in flight.");

            bool sprites = MapMarkers.Sprites;
            if (ImGui.Checkbox("Billboard sprites", ref sprites))
            {
                MapMarkers.Sprites = sprites;
                PatchSettings.Set(MapMarkers.SpritesKey, sprites);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Amber dots: torches and flames.");

            bool facing = MapMarkers.Facing;
            if (ImGui.Checkbox("Creature facing", ref facing))
            {
                MapMarkers.Facing = facing;
                PatchSettings.Set(MapMarkers.FacingKey, facing);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A spoke showing which way each creature is facing.");

            ImGui.Unindent();
        }

        bool walls = Map.Walls;
        if (ImGui.Checkbox("Mark sight-blocking tiles", ref walls))
        {
            Map.Walls = walls;
            PatchSettings.Set(Map.WallsKey, walls);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Darken the tiles that block your view.");

        // Fog of war: patches/MapFog.cs. Off by default, for the reason the whole
        // port uses -- the picture has not been judged by eye.
        bool fog = MapFog.Enabled;
        if (ImGui.Checkbox("Fog of war", ref fog))
        {
            MapFog.SetEnabled(fog);
            PatchSettings.Set(MapFog.OnKey, fog);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide the parts of the area you have not seen yet.");

        if (MapFog.Enabled)
        {
            ImGui.Indent();

            // The gate. On by default: the cull grid it filters is a *culling*
            // test and over-reports by design, and without this the fog paints
            // rooms through the wall beside a doorway -- measured, 110 of 136 lit
            // cells in area 7 sitting behind a wall mass the player cannot see
            // past. The switch is here because it is the one comparison a player
            // can make by eye in one session, beside the two buttons below.
            bool sight = MapFog.LineOfSight;
            if (ImGui.Checkbox("Only what you could see", ref sight))
            {
                MapFog.SetLineOfSight(sight);
                PatchSettings.Set(MapFog.LosKey, sight);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Only fill in what you had a clear view of, not what you walked past.");

            if (ImGui.Button("Forget this area")) MapFog.ForgetArea();
            ImGui.SameLine();
            if (ImGui.Button("Reveal this area")) MapFog.RevealArea();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Fill in this area's whole map, or wipe it back to unexplored.");
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
            ImGui.SetTooltip("Which of the two stacked floors the map shows.");
    }
}
