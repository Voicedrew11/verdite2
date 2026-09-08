using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The two keyboard layouts, as two buttons, at the head of the Keyboard tab.
///
/// The tab ends in a "Reset to defaults" button, and that one means *RecompOne's*
/// defaults — the console layout, face buttons on Z X A S. This page is the other
/// default, the one the port ships, and it is a page rather than a line in the
/// binding table because the choice is between two whole schemes and not between
/// two keys.
///
/// **It sits directly above the table it writes**, which is the point of
/// <see cref="InputSection"/>: pressing either button repaints the sixteen rows
/// below on the same frame, since <c>KeyLayout.IsApplied</c> is a per-frame
/// comparison rather than a latch. The trap runs the other way — editing a single
/// row by hand un-applies the layout, so the left button un-greys the instant you
/// rebind one key, which reads as the port undoing the edit unless something says
/// the table is the authority. The note below says it.
///
/// See "The keyboard layout" in docs/INPUT.md.
/// </summary>
public sealed class KeyLayoutPage : IPatchPage
{
    public string Id => "keylayout";
    /// <summary>No heading of its own: the tab is already called Keyboard and
    /// the two buttons name themselves. See <see cref="IPatchPage.Title"/>.</summary>
    public string Title => "";

    public void Draw()
    {
        bool applied = KeyLayout.IsApplied();

        ImGui.BeginDisabled(applied);
        if (ImGui.Button("King's Field layout")) KeyLayout.Apply();
        ImGui.EndDisabled();
        // This tooltip described the *superseded* v1 layout until it was caught:
        // it said "R and F look up and down" and "E uses", while Layout() has
        // L2 and R2 empty (pitch is the mouse's alone), Space on Square and F on
        // Cross. The boot line KeyLayout.Install prints was right the whole time,
        // which is what it was checked against.
        Tip("W and S walk, A and D strafe, the arrows walk and turn, Space attacks, F uses, " +
            "Q casts and Tab opens the menu. Looking up and down is the mouse's alone — no keys " +
            "are bound to it. Writes the sixteen bindings in the table below; the arrows keep " +
            "walking through a second binding the table cannot show, so the in-game menu still " +
            "moves on them.");

        ImGui.SameLine();

        ImGui.BeginDisabled(!applied);
        if (ImGui.Button("RecompOne layout")) KeyLayout.ApplyStock();
        ImGui.EndDisabled();
        Tip("The console layout: face buttons on Z X A S, shoulders on Q W E R, the D-pad on " +
            "the arrows. The same thing the Reset button at the top of this section does.");

        Note(applied
            ? "The port's layout is in place. What each button then does is the game's own " +
              "control configuration, so remapping in-game moves the keys with it. Editing a row " +
              "below by hand stands, and un-applies the layout."
            : "The bindings above are not the port's layout. King's Field walks and turns on " +
              "the D-pad and strafes on the shoulder buttons, so the arrows alone are a tank " +
              "control — the layout on the left puts walking and strafing on W A S D and leaves " +
              "the arrows doing what they did.");
    }

    static void Tip(string tip)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
