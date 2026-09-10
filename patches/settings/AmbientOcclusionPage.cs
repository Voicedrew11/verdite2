using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The ambient-occlusion switch, under Video ▸ Enhancements with perspective
/// correction, sub-pixel positioning and the shading combo — each one a choice
/// about how faithful the picture should be to the hardware.
///
/// **One checkbox and nothing else.** The radius, the strength, the bias and the
/// sample count are all real knobs and all of them are the port's question to
/// answer rather than the player's: "how far does a wall reach to shade the floor,
/// in the game's world units" is not a thing anyone can be asked. They live on the
/// console under <c>KF2_AO_RADIUS</c> and friends, with the measurement that would
/// justify moving one, exactly as the map's five controls that were not choices
/// came off the Gameplay page.
/// </summary>
public sealed class AmbientOcclusionPage : IPatchPage
{
    public string Id => "ao";
    public string Title => "Enhancements";
    public int Order => 23;

    public void Draw()
    {
        bool on = AmbientOcclusion.Enabled;
        if (ImGui.Checkbox("Ambient occlusion", ref on))
        {
            AmbientOcclusion.SetEnabled(on);
            PatchSettings.Set(AmbientOcclusion.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shades the corners and the places surfaces meet.");
    }
}
