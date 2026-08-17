using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The dither switch, under Video beside vsync and render scale — which is
/// where the mod's gear button used to be the only way to reach it.
///
/// One checkbox and a tooltip: the trade it makes is worth a sentence, and the
/// counters that justify it belong on the console under
/// <c>KF2_NODITHER_PROBE=1</c>, not in a settings window. It shares the
/// "Enhancements" heading rather than taking one of its own, since a heading over
/// a single checkbox is just the checkbox's label written twice. See "Dithering"
/// in NOTES.md.
/// </summary>
public sealed class NoDitherPage : IPatchPage
{
    public string Id => "nodither";
    public string Title => "Enhancements";

    public void Draw()
    {
        bool off = NoDither.Enabled;
        if (ImGui.Checkbox("Dither off", ref off))
        {
            NoDither.SetEnabled(off);
            PatchSettings.Set(NoDither.OnKey, off);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes the 4x4 crosshatch over shaded surfaces; the 15-bit frame " +
                             "buffer bands in those gradients instead.");
    }
}
