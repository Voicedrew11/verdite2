using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The true-color switch, under Video, sharing the "Enhancements" heading with the
/// dither, perspective and sub-pixel switches — each one a choice about how
/// faithful the picture should be to the hardware, and none of them deserving a
/// rule and a name of its own.
///
/// It belongs beside the dither because the two are the console's two answers to
/// the same 15-bit banding: the dither hides it with a crosshatch, true color
/// removes it by keeping eight bits. The mechanism is the GL backend's render-
/// target format and the fragment shader; see "True color" in NOTES.md.
/// </summary>
public sealed class TrueColorPage : IPatchPage
{
    public string Id => "truecolor";
    public string Title => "Enhancements";

    public void Draw()
    {
        bool on = TrueColor.Enabled;
        if (ImGui.Checkbox("True color (24-bit)", ref on))
        {
            TrueColor.SetEnabled(on);
            PatchSettings.Set(TrueColor.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Renders shading at 24-bit, so fog gradients no longer band into " +
                             "steps — without the dither crosshatch. Off gives the console's own " +
                             "15-bit output. Textures are unchanged either way.");
    }
}
