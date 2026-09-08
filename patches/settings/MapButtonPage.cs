using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The pad button that opens the full-screen map — **under Input, with the other
/// bindings**, rather than on the Map page with the map's own switches.
///
/// It sat there because it arrived with the map, and that is the wrong reason: a
/// player looking for what a button does looks under Input, beside the keyboard
/// layout and the sticks, and nothing else in the port asks them to look
/// somewhere else for one binding. What the map *is* stays under Gameplay. It is
/// drawn in the Gamepad tab, under the stick block — see <see cref="InputSection"/>.
///
/// The value is a raw <c>SDL_GameControllerButton</c> index, not a PSX pad bit,
/// and that is the only reason the touchpad can be offered at all: the PS1 had no
/// such button, so it is read off <c>ControllerEvent</c> before <c>PadState</c>
/// maps anything onto the pad and cannot leak into the game. See Map.PadButton.
/// </summary>
public sealed class MapButtonPage : IPatchPage
{
    public string Id => "mapbutton";
    public string Title => "Map";

    // The pad button that opens the full-screen map, and the SDL index each entry
    // stores. Not every pad has every one of them -- a controller with no touchpad
    // never sends button 20 -- and nothing here can ask the pad what it has, so
    // they are offered as a list rather than probed.
    static readonly string[] PadButtons =
        ["Touchpad (DualSense / DualShock 4)", "L3 (left stick click)", "R3 (right stick click)",
         "Select / Back", "None"];
    static readonly int[] PadValues =
        [Map.PadTouchpad, Map.PadL3, Map.PadR3, Map.PadSelect, Map.PadNone];

    public void Draw()
    {
        ImGui.BeginDisabled(!Map.Enabled);

        int padIdx = System.Array.IndexOf(PadValues, Map.PadButton);
        if (padIdx < 0) padIdx = PadValues.Length - 1;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Open the full-screen map with", ref padIdx, PadButtons, PadButtons.Length))
            Map.SetPadButton(PadValues[padIdx]);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The pad button that opens the map. M on the keyboard always does too.");

        ImGui.EndDisabled();

        // Wrapped rather than TextDisabled: inside a tab the pane is some 40px
        // narrower than the one this was written against, and TextDisabled does
        // not wrap -- it would run out of the tab. Disabled and explained rather
        // than hidden, which is this page's own rule and AutoReloadPage's.
        if (!Map.Enabled)
            PatchSettings.Note("The map is switched off under Gameplay.");
    }
}
