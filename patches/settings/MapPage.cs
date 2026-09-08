using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The map, under **Gameplay** rather than Video — **one combo where there were
/// two checkboxes**.
///
/// A map is not a choice about how the picture is made — it is a thing the game
/// did not have and now does, which is the same test auto reload passes and the
/// reason the port added the Gameplay section at all.
///
/// **This page carried eleven controls, then two, and now draws one widget.**
/// Everything that went went for one of two reasons: it was not a choice (the
/// style, the height ramp, the sight-blocking tint, the marker layer, the save
/// points, the pause, the floor, the player marker), or it belongs somewhere else
/// (the pad button, now under Input with the other bindings). The corner minimap
/// and its six knobs are gone for a third reason — the picture has never been
/// judged by eye, so it is a comparison rather than a feature, and N still opens
/// it for the session. See "Five map controls that were not choices" and "What the
/// Map page is down to" in docs/PATCHES_AND_MODS.md; the mechanism is
/// patches/Map.cs and the three viewports are patches/MapFullscreen.cs,
/// patches/MapPanel.cs and patches/MapOverlay.cs.
///
/// **What is left is one question, so it is asked once.** The map and fog of war
/// were two ticks, and two ticks cross into four states carrying three meanings:
/// fog on with the map off is nobody's answer to anything. That is the argument
/// <see cref="ShadingPage"/> made about dither and true colour, and the fix is the
/// same — three entries, and the fourth state stops being reachable:
///
/// <list type="table">
/// <item><term>Off</term><description>no map at all; M and the pad button do nothing</description></item>
/// <item><term>Whole area</term><description>the floor plan the moment you walk in</description></item>
/// <item><term>Fill in as you go</term><description>fog of war — only what you had a clear view of</description></item>
/// </list>
///
/// Fog keeps its place among them because it is a real choice: a player who wants
/// the whole floor plan is not asking for a defect. What used to be indented under
/// it does not appear here at all — "only what you could see" is the shadowcast
/// gate, and without it the fog paints rooms through the wall beside a doorway
/// into a store that never forgets, which is a defect rather than a taste, so it
/// is on and KF2_MAP_FOG_LOS=0 is the comparison. Forget and Reveal are
/// instruments and live on the docked MapPanel with the others.
///
/// Both patches keep their own key (<c>kf2.map.on</c>, <c>kf2.map.fog</c>) and
/// their own environment variable (<c>KF2_MAP</c>, <c>KF2_MAP_FOG</c>), so merging
/// the control strands no config and takes no comparison off the console. The map
/// is read as the master — off means Off, whatever the fog says — and any click
/// writes both patches, harmonising the pair.
/// </summary>
public sealed class MapPage : IPatchPage
{
    public string Id => "map";

    /// <summary>No heading of its own: the section's rule already says Gameplay,
    /// and two rules in a row over one combo is a rule for its own sake.</summary>
    public string Title => "";

    public int Order => 10;

    const int Off = 0;
    const int Whole = 1;
    const int Fog = 2;

    static readonly string[] Labels = ["Off", "Whole area", "Fill in as you go"];

    public void Draw()
    {
        int index = Index();

        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Map", ref index, Labels, Labels.Length)) Apply(index);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A map of the area you are in: the whole floor plan, or only where " +
                             "you have been. M opens it; the world stops while it is up.");
    }

    /// <summary>The state the two patches are actually in, folded onto the three
    /// entries. The map wins, so the fourth state — only reachable from
    /// <c>KF2_MAP=0 KF2_MAP_FOG=1</c> — opens as Off rather than earning an entry
    /// of its own; one click on any entry then makes the pair consistent.</summary>
    static int Index()
    {
        if (!Map.Enabled) return Off;
        return MapFog.Enabled ? Fog : Whole;
    }

    /// <summary>Both patches and both keys, from the one combo. Each patch's own
    /// <c>SetEnabled</c> is what decides, so the key is written from what the patch
    /// ended up with rather than from what was asked for.</summary>
    static void Apply(int index)
    {
        Map.SetEnabled(index != Off);
        PatchSettings.Set(Map.OnKey, Map.Enabled);

        MapFog.SetEnabled(index == Fog);
        PatchSettings.Set(MapFog.OnKey, MapFog.Enabled);
    }
}
