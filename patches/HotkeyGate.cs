using ImGuiNET;

namespace Kf2;

/// <summary>
/// When the port's hotkeys stand down. A key typed into a text field reaches the
/// <c>KeyboardEvent</c> bus as well, so every letter hotkey waits while ImGui has
/// one focused; the ones that act on the game also wait while the remaster editor
/// is open.
/// </summary>
static class HotkeyGate
{
    public static bool Typing
        => ImGui.GetCurrentContext() != nint.Zero && ImGui.GetIO().WantTextInput;

    public static bool Editing => Typing || Remaster.Editor.Open;
}
