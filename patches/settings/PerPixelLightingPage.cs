using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The per-pixel lighting switch, under Video ▸ Enhancements after anisotropic
/// filtering. One checkbox: the curve and the light model are the game's own.
/// </summary>
public sealed class PerPixelLightingPage : IPatchPage
{
    public string Id => "perpixel";
    public string Title => "Enhancements";
    public int Order => 25;

    public void Draw()
    {
        bool on = PerPixelLighting.Enabled;
        if (ImGui.Checkbox("Per-pixel lighting", ref on))
        {
            PerPixelLighting.SetEnabled(on);
            PatchSettings.Set(PerPixelLighting.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lights and darkens surfaces at every pixel instead of blending between their corners.");
    }
}
