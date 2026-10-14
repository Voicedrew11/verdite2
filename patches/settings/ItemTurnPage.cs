using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// <see cref="ItemTurn"/>'s two switches, under Gameplay, the gyro dimmed while
/// item turning is off. See "Turning a picked-up item" and "Turning the item preview" in
/// docs/INPUT.md.
/// </summary>
public sealed class ItemTurnPage : IPatchPage
{
    public string Id => "itemturn";

    public string Title => "";

    public int Order => 30;

    public void Draw()
    {
        bool on = ItemTurn.Enabled;
        if (ImGui.Checkbox("Turn a held item", ref on))
        {
            ItemTurn.SetEnabled(on);
            PatchSettings.Set(ItemTurn.OnKey, on);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When you pick up an item, move the mouse or push the right stick to turn it; in the inventory and the shops, drag its model or push the right stick. It spins again when you let go.");

        ImGui.BeginDisabled(!ItemTurn.Enabled);
        bool gyro = ItemTurn.UseGyro;
        if (ImGui.Checkbox("Gyro turns it too", ref gyro))
        {
            ItemTurn.SetGyro(gyro);
            PatchSettings.Set(ItemTurn.GyroKey, gyro);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Turn and tip the pad and the item turns with it. Needs a pad with a gyroscope.");
        ImGui.EndDisabled();
    }
}
