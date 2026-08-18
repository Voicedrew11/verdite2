using ImGuiNET;
using RecompOne.Runtime.Host;

namespace Kf2.Settings;

/// <summary>
/// The mouse knobs, under Input, below the stick ones — the same argument that
/// put those there, applied to the other half of a keyboard-and-mouse setup.
///
/// Two things on this page are not settings and are the reason it is shaped the
/// way it is. The **capture line** says whether the pointer is locked right now
/// and which key changes that: mouse look does nothing at all until it is, so a
/// player who switched it on and saw no change needs that sentence before they
/// need a sensitivity. And the **buttons** are named as pad buttons rather than
/// as actions, because that is what they are — the game's own control-config
/// screen decides what Cross does, and this page would be lying if it said
/// "Attack".
///
/// See "Mouse look" in NOTES.md.
/// </summary>
public sealed class MousePage : IPatchPage
{
    public string Id => "mouse";
    public string Title => "Mouse look";

    public void Draw()
    {
        bool was = Mouse.Enabled;
        Check("Mouse look", Mouse.OnKey, ref Mouse.Enabled,
              "Turns and looks with the mouse, through the same per-frame velocities the sticks " +
              "drive — so the game's own movement code, collision and pitch limit are untouched. " +
              "The pointer has to be captured before anything happens, and the mouse buttons only " +
              "reach the game while it is.");
        if (was && !Mouse.Enabled) Mouse.SetCaptured(false);

        ImGui.BeginDisabled(!Mouse.Enabled);

        Slider("Turn sensitivity", Mouse.TurnKey, ref Mouse.TurnSens, 0.1f, 5f,
               "At 1.0 a quarter turn takes about 600 pixels of movement. This is in window " +
               "pixels, so a larger window turns a little slower for the same movement of the hand.");
        Slider("Look sensitivity", Mouse.LookKey, ref Mouse.LookSens, 0.1f, 5f);
        Check("Invert look Y", Mouse.InvertKey, ref Mouse.InvertY);

        ImGui.Spacing();
        Note("Mouse buttons press pad buttons — what each one then does is whatever the game's " +
             "own control configuration says, exactly as for a controller.");
        Button("Left button", Mouse.LeftKey, ref Mouse.LeftButton);
        Button("Right button", Mouse.RightKey, ref Mouse.RightButton);
        Button("Middle button", Mouse.MiddleKey, ref Mouse.MiddleButton);

        ImGui.Spacing();
        CaptureKey();

        ImGui.EndDisabled();

        if (!HostWindow.MouseAvailable)
            Note("No mouse is attached to the window, so nothing here will do anything.");
        else if (!Mouse.Enabled)
            Note("Mouse look is off; the pointer stays a pointer.");
        else
            Note(Mouse.Captured
                ? $"The pointer is captured — press {Mouse.CaptureKey} to get it back."
                : $"The pointer is free — press {Mouse.CaptureKey} with the game in front to capture it. " +
                  "Opening any of these settings gives it back on its own.");
    }

    /// <summary>
    /// Which pad button a mouse button presses. "None" is index 0 and is a real
    /// choice — a player who wants the mouse for looking only.
    /// </summary>
    static void Button(string label, string key, ref int index)
    {
        var names = Mouse.PadButtons.Select(b => b.Name).ToArray();
        if (ImGui.Combo(label, ref index, names, names.Length)) PatchSettings.Set(key, index);
    }

    /// <summary>
    /// The capture key, as a short list rather than as a binding widget: the
    /// runtime's own "press a key" capture belongs to its pad-binding table and is
    /// internal to it, and a page that offers every key on the keyboard would let
    /// someone bind capture to a key the game already uses and lock themselves in.
    /// </summary>
    static void CaptureKey()
    {
        var keys = Mouse.CaptureKeys;
        var names = keys.Select(k => k.ToString()).ToArray();
        int index = Array.IndexOf(keys, Mouse.CaptureKey);
        if (index < 0) index = 0;

        if (ImGui.Combo("Capture key", ref index, names, names.Length))
        {
            Mouse.CaptureKey = keys[index];
            PatchSettings.Set(Mouse.CaptureKeyKey, (int)Mouse.CaptureKey);
        }
        Tip("Locks the pointer to the window and hides it, and gives it back again. " +
            "The game's own keyboard controls are Z X A S Q W E R F G, Enter, right shift and " +
            "the arrows; F1 and F11 belong to the port.");
    }

    static void Check(string label, string key, ref bool value, string? tip = null)
    {
        if (ImGui.Checkbox(label, ref value)) PatchSettings.Set(key, value);
        Tip(tip);
    }

    static void Slider(string label, string key, ref float value, float min, float max, string? tip = null)
    {
        if (ImGui.SliderFloat(label, ref value, min, max, "%.2f", ImGuiSliderFlags.AlwaysClamp))
            PatchSettings.Set(key, value);
        Tip(tip);
    }

    static void Tip(string? tip)
    {
        if (tip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
