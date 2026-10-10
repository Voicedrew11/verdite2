using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// <see cref="MenuWorld.FadeVBlanks"/>, under Gameplay: how many vblanks each step
/// of a sign's or a message's fade is shown for. x1 is the game's own speed. See
/// "The fade's length" in docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class MessageFadePage : IPatchPage
{
    public string Id => "messagefade";

    public string Title => "";

    public int Order => 40;

    public void Draw()
    {
        // Dimmed rather than hidden while the world is not drawn behind messages:
        // the length applies only where MenuWorld takes the fade.
        ImGui.BeginDisabled(!MenuWorld.Enabled);

        int vblanks = MenuWorld.FadeVBlanks;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderInt("Message fade length", ref vblanks, 1, MenuWorld.MaxFadeVBlanks,
                            vblanks == 1 ? "x1 (original)" : "x%d"))
        {
            MenuWorld.SetFadeVBlanks(vblanks);
            PatchSettings.Set(MenuWorld.FadeKey, MenuWorld.FadeVBlanks);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("How long signs and messages take to fade in and out. x1 is the game's own speed.");

        ImGui.EndDisabled();
    }
}
