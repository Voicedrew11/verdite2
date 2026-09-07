using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The map's two switches, under **Gameplay** rather than Video.
///
/// A map is not a choice about how the picture is made — it is a thing the game
/// did not have and now does, which is the same test auto reload passes and the
/// reason the port added the Gameplay section at all. It gets its own "Map"
/// heading rather than joining auto reload's, because the two share nothing but
/// the tab.
///
/// **This page carried eleven controls and carries two.** Everything that went
/// went for one of two reasons: it was not a choice (the style, the height ramp,
/// the sight-blocking tint, the marker layer, the save points, the pause, the
/// floor, the player marker), or it belongs somewhere else (the pad button, now
/// under Input with the other bindings). The corner minimap and its six knobs are
/// gone for a third reason — the picture has never been judged by eye, so it is a
/// comparison rather than a feature, and N still opens it for the session. See
/// "Five map controls that were not choices" and "What the Map page is down to"
/// in docs/PATCHES_AND_MODS.md; the mechanism is patches/Map.cs and the three
/// viewports are patches/MapFullscreen.cs, patches/MapPanel.cs and
/// patches/MapOverlay.cs.
/// </summary>
public sealed class MapPage : IPatchPage
{
    public string Id => "map";
    public string Title => "Map";

    public void Draw()
    {
        bool on = Map.Enabled;
        if (ImGui.Checkbox("Map", ref on))
        {
            Map.SetEnabled(on);
            PatchSettings.Set(Map.OnKey, on);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A map of the area you are in. M opens it; the world stops while it is up.");

        if (!Map.Enabled) return;

        // Fog of war: patches/MapFog.cs. Off by default, for the reason the whole
        // port uses -- the picture has not been judged by eye. It keeps a control
        // because it is a real choice: a player who wants the whole floor plan the
        // moment they walk in is not asking for a defect.
        //
        // What used to be indented under it does not. "Only what you could see"
        // is the shadowcast gate, and without it the fog paints rooms through the
        // wall beside a doorway into a store that never forgets -- a defect, not a
        // taste, so it is on and KF2_MAP_FOG_LOS=0 is the comparison. Forget and
        // Reveal are instruments and moved to the docked MapPanel with the others.
        bool fog = MapFog.Enabled;
        if (ImGui.Checkbox("Fog of war", ref fog))
        {
            MapFog.SetEnabled(fog);
            PatchSettings.Set(MapFog.OnKey, fog);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fill the map in as you go, instead of showing the whole area at once. " +
                             "Only what you actually had a clear view of.");
    }
}
