using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// Auto reload's two knobs, under Gameplay — which is where the mod's gear
/// button used to be the only way to reach them.
///
/// The mod's panel opened with a paragraph explaining that nothing is restored by
/// hand; a settings section is a list of switches, so none of that came across —
/// the reasoning is in <see cref="AutoReload"/> and in <c>docs/</c>, and the
/// tooltips say only what each control does (see <see cref="IPatchPage.Draw"/>).
///
/// **The Simulate death button and the death census went with it.** They are
/// instruments — dying on purpose is the hard part of testing this, which is a
/// sentence about testing rather than about playing — and this port already knows
/// where its instruments go: the same argument that moved Forget and Reveal to the
/// docked MapPanel. Nothing is lost. <c>AutoReload.Simulate</c> is still the
/// shell's <c>kill</c> verb (<c>patches/AgentServer.cs</c>) and the MCP
/// <c>kf2_kill</c> tool, the attract demo still kills itself unattended, and
/// <c>AutoReload.Status</c> — including the two failures a player could see, "no
/// save to reload" and "slot N would not load" — is still printed on stdout every
/// time it changes.
/// </summary>
public sealed class AutoReloadPage : IPatchPage
{
    public string Id => "autoreload";

    /// <summary>No heading of its own; the section's own rule says Gameplay. See
    /// <see cref="IPatchPage.Title"/>.</summary>
    public string Title => "";

    public int Order => 20;

    static readonly string[] Slots = ["Last used", "Slot 1", "Slot 2", "Slot 3"];

    public void Draw()
    {
        bool on = AutoReload.Enabled;
        if (ImGui.Checkbox("Reload the last save on death", ref on))
        {
            AutoReload.SetEnabled(on);
            PatchSettings.Set(AutoReload.OnKey, AutoReload.Enabled);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Puts you back at your last save instead of the menus.");

        // "Delay after death" was a 0-10 s slider and is a fixed 2 s. It is not a
        // choice: at 0 the reload lands inside the death animation and reads as a
        // glitch, and at 10 the player has had time to reach for the menu this
        // patch exists to save them from. See AutoReload.Delay;
        // KF2_AUTORELOAD_DELAY is the comparison.

        // Dimmed and indented rather than hidden: a control that disappears reads
        // as a setting that was lost, which is MapButtonPage's rule for the pad
        // button and FrameSmoothingPage's for the smoothing tick.
        ImGui.Indent();
        ImGui.BeginDisabled(!AutoReload.Enabled);

        int slot = AutoReload.Slot;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Save slot", ref slot, Slots, Slots.Length))
        {
            AutoReload.SetSlot(slot);
            PatchSettings.Set(AutoReload.SlotKey, AutoReload.Slot);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Which save to reload. \"Last used\" follows where you saved.");

        ImGui.EndDisabled();
        ImGui.Unindent();
    }
}
