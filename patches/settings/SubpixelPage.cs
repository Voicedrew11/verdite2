using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The sub-pixel switch, under Video, sharing the "Enhancements" heading with the
/// dither, perspective and Z-buffer switches — four checkboxes, each one a choice about how
/// faithful the picture should be to the hardware, and none of them deserving a
/// rule and a name of its own.
///
/// It belongs beside perspective correction because the two recover the two halves
/// of the same discarded number, and a player who wants one usually wants the
/// other. The measurement that says the fraction is real belongs on the console
/// under <c>KF2_SUBPIXEL_PROBE=1</c>, not here; see "Sub-pixel vertex positioning"
/// in NOTES.md.
/// </summary>
public sealed class SubpixelPage : IPatchPage
{
    public string Id => "subpixel";
    public string Title => "Enhancements";

    public void Draw()
    {
        bool on = Subpixel.Enabled;
        if (ImGui.Checkbox("Sub-pixel vertex positioning", ref on))
        {
            Subpixel.SetEnabled(on);
            PatchSettings.Set(Subpixel.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stops geometry twitching and shimmering as you move: an edge drifts " +
                             "smoothly instead of snapping a whole pixel at a time. Off gives the " +
                             "console's own whole-pixel vertices.");
    }
}
