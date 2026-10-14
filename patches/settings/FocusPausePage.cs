using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// Verdite Core's <see cref="FocusPause"/> switch, under Gameplay. See "Pausing
/// when the window loses focus" in docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class FocusPausePage : IPatchPage
{
    public string Id => "focuspause";

    public string Title => "";

    public int Order => 15;

    public void Draw()
    {
        bool on = FocusPause.Enabled;
        if (ImGui.Checkbox("Pause in the background", ref on))
        {
            FocusPause.SetEnabled(on);
            PatchSettings.Set(FocusPause.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The game stops and goes quiet while its window is not the one in front.");
    }
}
