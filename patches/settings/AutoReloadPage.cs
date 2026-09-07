using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// Auto reload's three knobs, under Gameplay — which is where the mod's gear
/// button used to be the only way to reach them.
///
/// The mod's panel opened with a paragraph explaining that nothing is restored by
/// hand; a settings section is a list of switches, so none of that came across —
/// the reasoning is in <see cref="AutoReload"/> and in <c>docs/</c>, and the
/// tooltips say only what each control does (see <see cref="IPatchPage.Draw"/>).
/// What did come across whole is the *Simulate death* button: dying on purpose is
/// the hard part of testing this, and the alternative is waiting for the attract
/// demo to kill itself.
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
            ImGui.SetTooltip("Puts you back at your last save instead of the menus.");

        float delay = AutoReload.Delay;
        if (ImGui.SliderFloat("Delay after death (s)", ref delay, 0f, 10f))
        {
            AutoReload.SetDelay(delay);
            PatchSettings.Set(AutoReload.DelayKey, AutoReload.Delay);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How much of the death plays before the reload.");

        int slot = AutoReload.Slot;
        if (ImGui.Combo("Save slot", ref slot, "Last used\0Slot 1\0Slot 2\0Slot 3\0"))
        {
            AutoReload.SetSlot(slot);
            PatchSettings.Set(AutoReload.SlotKey, AutoReload.Slot);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which save to reload. \"Last used\" follows where you saved.");

        ImGui.Spacing();
        Note(AutoReload.Deaths == 0
            ? AutoReload.Status
            : $"{AutoReload.Status} — {AutoReload.Deaths} death(s) seen, {AutoReload.Reloads} reload(s)");

        if (ImGui.Button("Simulate death"))
            AutoReload.Simulate();

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Kills you, for testing. Does nothing outside an area.");
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
