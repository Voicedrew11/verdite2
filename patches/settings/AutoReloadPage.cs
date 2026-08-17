using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// Auto reload's three knobs, under Gameplay — which is where the mod's gear
/// button used to be the only way to reach them.
///
/// The mod's panel opened with a paragraph explaining that nothing is restored by
/// hand; a settings section is a list of switches, so that became the tooltips
/// and the reasoning stayed in <see cref="AutoReload"/> and in NOTES.md. What did
/// come across whole is the *Simulate death* button: dying on purpose is the hard
/// part of testing this, and the alternative is waiting for the attract demo to
/// kill itself.
/// </summary>
public sealed class AutoReloadPage : IPatchPage
{
    public string Id => "autoreload";
    public string Title => "Auto reload";

    public void Draw()
    {
        bool on = AutoReload.Enabled;
        if (ImGui.Checkbox("Reload the last save on death", ref on))
        {
            AutoReload.SetEnabled(on);
            PatchSettings.Set(AutoReload.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Calls the game's own loader and its own post-load area re-entry, so a " +
                             "death costs a couple of seconds instead of the menu, the load screen " +
                             "and the slot. Nothing is restored by hand.");

        float delay = AutoReload.Delay;
        if (ImGui.SliderFloat("Delay after death (s)", ref delay, 0f, 10f))
        {
            AutoReload.SetDelay(delay);
            PatchSettings.Set(AutoReload.DelayKey, AutoReload.Delay);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How much of the game's own death sequence plays before the reload. " +
                             "Zero reloads the frame you die, which reads as a glitch rather than " +
                             "as a death.");

        int slot = AutoReload.Slot;
        if (ImGui.Combo("Save slot", ref slot, "Last used\0Slot 1\0Slot 2\0Slot 3\0"))
        {
            AutoReload.SetSlot(slot);
            PatchSettings.Set(AutoReload.SlotKey, AutoReload.Slot);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("\"Last used\" is the game's own record at 0x8006E5D4, which both " +
                             "saving and loading write. It is empty until you have saved or loaded " +
                             "once, and a death before that is left alone. Pinning a slot ignores " +
                             "where you actually saved.");

        ImGui.Spacing();
        Note(AutoReload.Deaths == 0
            ? AutoReload.Status
            : $"{AutoReload.Status} — {AutoReload.Deaths} death(s) seen, {AutoReload.Reloads} reload(s)");

        if (ImGui.Button("Simulate death"))
            AutoReload.Simulate();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Zeroes HP and calls the game's own death latch, exactly as the damage " +
                             "path does. Does nothing unless an area is running.");
    }

    /// <summary>Wrapped and dimmed. TextDisabled does not wrap, and unwrapped prose
    /// runs straight out of the settings window.</summary>
    static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
