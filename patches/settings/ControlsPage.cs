using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// <see cref="Analog.Tank"/>, under Gameplay: 2D, the twin sticks, or 3D, one stick
/// as tank controls (the left stick walking and turning as the D-pad does, the
/// right stick unused). The first row of the tab, as in Verdite3, since it decides
/// what the rest of a pad player's settings mean. The same key and the same text as
/// CONTROLS on the in-game PORT SETTINGS page (<see cref="PortSettings.Controls"/>).
/// See "2D and 3D controls" in docs/INPUT.md.
/// </summary>
public sealed class ControlsPage : IPatchPage
{
    public string Id => "controls";

    /// <summary>No heading of its own; the section's rule says Gameplay.</summary>
    public string Title => "";

    public int Order => 5;

    /// <summary>Analog's hooks are what read it, so the row greys with them.</summary>
    public IReadOnlyList<string> Patches => ["kf2.analog"];

    public void Draw()
    {
        var row = PortSettings.Controls;
        string[] names = row.Names!;
        int choice = Analog.Tank ? 1 : 0;

        // Locked by KF2_TANK, as the game's page refuses it: the variable wins
        // again at the next boot, so a choice made here would not last.
        ImGui.BeginDisabled(row.LockedBy is not null);
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo(row.Label, ref choice, names, names.Length))
        {
            Analog.Tank = choice != 0;
            PatchSettings.Set(Analog.ControlsKey, Analog.Tank);
        }
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(row.LockedBy is { } env ? $"{row.Tip}\n\nSet by {env} for this run." : row.Tip);
    }
}
