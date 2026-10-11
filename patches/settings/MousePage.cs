using ImGuiNET;
using RecompOne.Runtime.Host;

namespace Kf2.Settings;

/// <summary>
/// The mouse knobs, under Input, below the stick ones — the same argument that
/// put those there, applied to the other half of a keyboard-and-mouse setup.
///
/// Two things on this page are not settings and are the reason it is shaped the
/// way it is. The **capture line** says whether the pointer is locked right now
/// and how that changes: mouse look does nothing at all until it is, so a player
/// who switched it on and saw no change needs that sentence before they need a
/// sensitivity. And the **buttons** are named as pad buttons rather than
/// as actions, because that is what they are — the game's own control-config
/// screen decides what Cross does, and this page would be lying if it said
/// "Attack". The Gamepad tab's table now names the *defaults* in a column of its
/// own, under a header and a note that say they are defaults, which is the
/// qualification this page used to carry by staying silent; the note below points
/// there rather than repeating it.
///
/// See "Mouse look" in NOTES.md.
/// </summary>
public sealed class MousePage : IPatchPage
{
    public string Id => "mouse";
    /// <summary>No heading of its own: it is the whole of a tab already called
    /// Mouse. See <see cref="IPatchPage.Title"/>.</summary>
    public string Title => "";

    public void Draw()
    {
        bool was = Mouse.Enabled;
        Check("Mouse look", Mouse.OnKey, ref Mouse.Enabled,
              "Turns and looks with the mouse, through the same per-frame velocities the sticks " +
              "drive — so the game's own movement code, collision and pitch limit are untouched. " +
              "Click the game to capture the pointer; the mouse buttons only reach the game while " +
              "it is captured, and the click that captures is not a press.");
        if (was && !Mouse.Enabled) Mouse.SetCaptured(false);

        ImGui.BeginDisabled(!Mouse.Enabled);

        Slider("Turn sensitivity", Mouse.TurnKey, ref Mouse.TurnSens, 0.1f, 5f,
               "At 1.0 a quarter turn takes about 600 pixels of movement. This is in window " +
               "pixels, so a larger window turns a little slower for the same movement of the hand.");
        Slider("Look sensitivity", Mouse.LookKey, ref Mouse.LookSens, 0.1f, 5f);
        Check("Invert look Y", Mouse.InvertKey, ref Mouse.InvertY);
        bool aim = MouseAim.Enabled;
        Check("Mouse aim", MouseAim.OnKey, ref MouseAim.Enabled,
              "The mouse moves a reticle and you turn toward it at the game's own speed, as with " +
              "the D-pad, instead of the view turning as far as the hand moves. Small movements " +
              "near the centre do not turn you until the hand rests.");
        if (aim && !MouseAim.Enabled) MouseAim.Reset();

        ImGui.BeginDisabled(!MouseAim.Enabled);
        ImGui.Indent();
        int view = MouseAim.ViewMode;
        if (ImGui.Combo("View", ref view, MouseAim.ViewNames, MouseAim.ViewNames.Length))
        {
            MouseAim.HeadLook = view == 1;
            MouseAim.HoldView = view == 2;
            PatchSettings.Set(MouseAim.HeadKey, MouseAim.HeadLook);
            PatchSettings.Set(MouseAim.HoldKey, MouseAim.HoldView);
        }
        Tip("What the view does while you turn toward the reticle. Turns with you: the view is " +
            "your facing, and the reticle comes to the middle as you turn. Follows the mouse: the " +
            "view turns with the hand, as in mouse look, and your body follows at the game's own " +
            "speed. Holds still: the picture stays put while the dot, your facing, travels to the " +
            "circle; once you arrive, the view turns to bring both back to the middle.");
        Slider("Aim sensitivity", MouseAim.SensKey, ref MouseAim.Sens, 0.1f, 4f,
               "How far the reticle moves for a movement of the hand, on top of the turn " +
               "sensitivity. Lower it if the reticle reaches the edge of the picture too easily.");
        Check("Ease the turn", MouseAim.EaseKey, ref MouseAim.Ease,
              "On, a turn speeds up and slows down as the D-pad's does. Off, it starts at full " +
              "speed and stops dead on the reticle, at the same top speed.");
        int marks = MouseAim.Marks;
        if (ImGui.Combo("Aim marks", ref marks, MouseAim.MarksNames, MouseAim.MarksNames.Length))
        {
            MouseAim.Marks = marks;
            PatchSettings.Set(MouseAim.MarksKey, marks);
        }
        Tip("When the dot, line and circle are drawn. While aiming shows them only during a turn " +
            "or while the circle is off the middle, and lets them fade out after, so they do not " +
            "hold the eye. Off suits View follows the mouse, where the middle of the picture is the aim.");
        ImGui.BeginDisabled(MouseAim.Marks == MouseAim.MarksOff);
        Check("Connecting line", MouseAim.LineKey, ref MouseAim.Line,
              "The thin line from the dot at the middle to the circle. Without it the dot and the " +
              "circle are all that is drawn.");
        Check("Centre dot", MouseAim.DotKey, ref MouseAim.Dot,
              "The small dot where you face. Without it the circle, and the line if it is on, " +
              "are all that is drawn.");
        ImGui.EndDisabled();
        ImGui.Unindent();
        ImGui.EndDisabled();

        ImGui.Spacing();
        // This used to end "...exactly as for a controller", which was a denial
        // of the verb. The Gamepad tab now names the defaults in a column, so
        // this points at them instead of refusing to say.
        Note("Mouse buttons press pad buttons. What each one does by default is in the Gamepad " +
             "tab's table, and the game's own control configuration can reassign it.");
        Button("Left button", Mouse.LeftKey, ref Mouse.LeftButton);
        Button("Right button", Mouse.RightKey, ref Mouse.RightButton);
        Button("Middle button", Mouse.MiddleKey, ref Mouse.MiddleButton);

        ImGui.Spacing();
        CaptureKey();

        ImGui.EndDisabled();

        ImGui.Spacing();
        // Outside the disabled block on purpose: this one needs no capture and so
        // no mouse look. A player who leaves mouse look off still has a pointer
        // in the menus, and burying the switch under a feature it does not depend
        // on would hide that.
        Check("Menu pointer", MenuMouse.OnKey, ref MenuMouse.Enabled,
              "Point at an item in the in-game menu and the game's own cursor moves to it; " +
              "left click confirms, right click backs out. It takes the cursor only once the " +
              "mouse has moved, and hands it straight back when the pad or the keyboard moves " +
              "it. Needs no captured pointer.");

        if (!HostWindow.MouseAvailable)
            Note("No mouse is attached to the window, so nothing here will do anything.");
        else if (!Mouse.Enabled)
            Note("Mouse look is off; the pointer stays a pointer.");
        else
            Note(CaptureLine());
    }

    /// <summary>
    /// How the pointer is let go and taken back, as it stands: Escape opens the
    /// game's menu only with the port's layout in place and Escape not the capture
    /// key (<see cref="KeyLayout"/>); a capture key saved before the click took
    /// capture over is kept, and still toggles.
    /// </summary>
    static string CaptureLine()
    {
        var key = Mouse.CaptureKey;
        bool hasKey = key != Silk.NET.Input.Key.Unknown;
        bool escapeMenus = key != Silk.NET.Input.Key.Escape && KeyLayout.IsApplied();

        if (Mouse.Captured)
            return hasKey
                ? $"The pointer is captured — press {Mouse.KeyName(key)} to get it back, or switch to another window." +
                  (escapeMenus ? " Escape opens the game's menu, which gives it back too." : "")
                : escapeMenus
                    ? "The pointer is captured — Escape opens the game's menu, which gives it back, " +
                      "and so does switching to another window."
                    : "The pointer is captured — the game's menu gives it back, and so does switching " +
                      "to another window.";

        return "The pointer is free — click the game to capture it" +
               (hasKey ? $", or press {Mouse.KeyName(key)}" : "") +
               ". The game's menu, these settings and switching to another window give it back; " +
               "closing the game's menu captures it again.";
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
    /// The capture key, None by default since a click captures, as a short list
    /// rather than as a binding widget: the
    /// runtime's own "press a key" capture belongs to its pad-binding table and is
    /// internal to it, and a page that offers every key on the keyboard would let
    /// someone bind capture to a key the game already uses and lock themselves in.
    /// </summary>
    static void CaptureKey()
    {
        var keys = Mouse.CaptureKeys;
        var names = keys.Select(Mouse.KeyName).ToArray();
        int index = Array.IndexOf(keys, Mouse.CaptureKey);
        if (index < 0) index = 0;

        if (ImGui.Combo("Capture key", ref index, names, names.Length))
        {
            Mouse.CaptureKey = keys[index];
            PatchSettings.Set(Mouse.CaptureKeyKey, (int)Mouse.CaptureKey);
        }
        Tip("A key that locks the pointer and gives it back, besides the click. None by default: " +
            "Escape opens the game's menu, which already gives it back. The list is deliberately " +
            "short — a key the game's controls or the port's shortcuts already use would let " +
            "someone lock themselves in. Escape here takes it from the menu.");
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
