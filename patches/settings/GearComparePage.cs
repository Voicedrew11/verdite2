using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// <see cref="GearCompare"/>'s switch, under Gameplay. The mod's five position
/// sliders did not come across: the panel's place was settled by eye, so it is
/// fixed. See "Comparing gear on the equip prompt" in docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class GearComparePage : IPatchPage
{
    public string Id => "gearcompare";

    public string Title => "";

    public int Order => 25;

    public void Draw()
    {
        bool on = GearCompare.Enabled;
        if (ImGui.Checkbox("Compare gear", ref on))
        {
            GearCompare.Enabled = on;
            PatchSettings.Set(GearCompare.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Equipping or buying shows every stat that would change, now and after, beside the Yes/No prompt.");
    }
}
