using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// <see cref="NpcDialogue"/>'s switches, under Gameplay: the mod's two settings, and the
/// switch the mod had in the Mods panel. See "NPC dialogue" in docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class NpcDialoguePage : IPatchPage
{
    public string Id => "npcdialogue";

    public string Title => "";

    public int Order => 27;

    public IReadOnlyList<string> Patches => ["kf2.npcdialogue.port"];

    public void Draw()
    {
        bool stoodDown = NpcDialogue.StoodDown;
        ImGui.BeginDisabled(stoodDown);

        bool on = NpcDialogue.Enabled;
        if (ImGui.Checkbox("NPC dialogue", ref on))
        {
            NpcDialogue.Enabled = on;
            PatchSettings.Set(NpcDialogue.OnKey, on);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(stoodDown
                ? "The NPC Dialogue mod is enabled in the Mods panel and does this itself."
                : "While an NPC's line is up, Interact goes straight to their next line and Cancel ends the conversation. " +
                  "Shopkeepers and the fortune teller are left as they are.");

        ImGui.BeginDisabled(!on);
        bool skip = NpcDialogue.SkipAnimations;
        if (ImGui.Checkbox("No animations between lines", ref skip))
        {
            NpcDialogue.SkipAnimations = skip;
            PatchSettings.Set(NpcDialogue.SkipKey, skip);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("The NPC stays in their talking pose until the conversation ends. Off: every line plays both animations.");

        bool guard = NpcDialogue.GuardMenu;
        if (ImGui.Checkbox("Cancel does not open the menu", ref guard))
        {
            NpcDialogue.GuardMenu = guard;
            PatchSettings.Set(NpcDialogue.GuardKey, guard);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Cancel is also the menu button, so ending a conversation with it would open the menu a moment later.");
        ImGui.EndDisabled();

        ImGui.EndDisabled();
    }
}
