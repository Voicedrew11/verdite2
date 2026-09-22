using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The ambient-occlusion setting, under Video ▸ Enhancements with perspective
/// correction, sub-pixel positioning and the shading combo — each one a choice
/// about how faithful the picture should be to the hardware.
///
/// **One slider with fixed positions: Off, Low, Medium, High.** Off and a quality are one question, as
/// the shading combo's were. Off writes the old <c>kf2.ao.on</c> key, so a config
/// saved with the checkbox reads the same; a quality turns it on and saves both.
/// Quality is a cost a player can judge on their own machine; the radius, the
/// strength and the bias are the port's question to answer rather than the
/// player's, and live on the console under <c>KF2_AO_RADIUS</c> and friends.
/// </summary>
public sealed class AmbientOcclusionPage : IPatchPage
{
    public string Id => "ao";
    public string Title => "Enhancements";
    public int Order => 23;

    static readonly string[] Labels = ["Off", "Low", "Medium", "High"];

    public void Draw()
    {
        int index = AmbientOcclusion.Enabled ? (int)AmbientOcclusion.CurrentQuality + 1 : 0;
        if (ImGui.SliderInt("SSAO", ref index, 0, Labels.Length - 1, Labels[index],
                            ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoInput))
        {
            bool on = index > 0;
            AmbientOcclusion.SetEnabled(on);
            PatchSettings.Set(AmbientOcclusion.OnKey, on);
            if (on)
            {
                AmbientOcclusion.SetQuality((AmbientOcclusion.Quality)(index - 1));
                PatchSettings.Set(AmbientOcclusion.QualityKey, index - 1);
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shades corners. Lower is cheaper.");
    }
}
