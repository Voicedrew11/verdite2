using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The two keyboard layouts, as two buttons, under Input.
///
/// The runtime's own section already ends in a "Reset to defaults" button, and
/// that one means *RecompOne's* defaults — the console layout, face buttons on
/// Z X A S. This page is the other default, the one the port ships, and it is a
/// page rather than a line in the binding table because the choice is between two
/// whole schemes and not between two keys.
///
/// The table above it is still the authority: pressing either button writes
/// sixteen bindings into it, and anything edited by hand afterwards stands. See
/// "The keyboard layout" in NOTES.md.
/// </summary>
public sealed class KeyLayoutPage : IPatchPage
{
    public string Id => "keylayout";
    public string Title => "Keyboard layout";

    public void Draw()
    {
        bool applied = KeyLayout.IsApplied();

        ImGui.BeginDisabled(applied);
        if (ImGui.Button("King's Field layout")) KeyLayout.Apply();
        ImGui.EndDisabled();
        Tip("W and S walk, A and D strafe, the arrows walk and turn, R and F look up and down, " +
            "Space attacks, E uses, Q casts and Tab opens the menu. Writes the sixteen bindings " +
            "in the table above; the arrows keep walking through a second binding the table " +
            "cannot show, so the in-game menu still moves on them.");

        ImGui.SameLine();

        ImGui.BeginDisabled(!applied);
        if (ImGui.Button("RecompOne layout")) KeyLayout.ApplyStock();
        ImGui.EndDisabled();
        Tip("The console layout: face buttons on Z X A S, shoulders on Q W E R, the D-pad on " +
            "the arrows. The same thing the Reset button at the top of this section does.");

        Note(applied
            ? "The port's layout is in place. What each button then does is the game's own " +
              "control configuration, so remapping in-game moves the keys with it."
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
