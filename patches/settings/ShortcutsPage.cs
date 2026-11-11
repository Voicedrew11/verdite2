using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host;
using Silk.NET.Input;
using Kf2.Shortcuts;
using Sc = Kf2.Shortcuts.Shortcuts;

namespace Kf2.Settings;

/// <summary>
/// <see cref="Sc"/>'s settings, under Gameplay: the switch the mod had in the Mods panel,
/// and the mod's own page (labels, a key and a pad button per shortcut, the d-pad).
/// Host thread. See "Shortcuts" in docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class ShortcutsPage : IPatchPage
{
    public string Id => "shortcuts";

    public string Title => "";

    public int Order => 28;

    public IReadOnlyList<string> Patches => ["kf2.shortcuts.port"];

    const float KeyWidth = 170f, PadWidth = 170f, NameColumn = 150f;

    public void Draw()
    {
        bool stoodDown = Sc.StoodDown;
        ImGui.BeginDisabled(stoodDown);

        bool on = Sc.Enabled;
        if (ImGui.Checkbox("Shortcuts", ref on))
        {
            Sc.Enabled = on;
            PatchSettings.Set(Sc.OnKey, on);
        }
        Tip(stoodDown
            ? "The Shortcuts mod is enabled in the Mods panel and does this itself."
            : "The Select shortcut, five times over: any of them holds an item or a spell, used with one key or button.");

        if (on)
        {
            ImGui.Indent();
            DrawParts();
            ImGui.Unindent();
        }

        ImGui.EndDisabled();
    }

    static void DrawParts()
    {
        PatchSettings.Note("Shortcut 1 is the game's own Select shortcut; its key here presses Select. To assign one, " +
                           "open Equipment, choose the Select button row (Hotkeys with keyboard labels), pick an item " +
                           "or spell and answer Yes, then choose which shortcut it goes on.");

        int labels = Sc.KeyboardLabels ? 0 : 1;
        if (ImGui.Combo("In-game labels", ref labels, "Keyboard keys\0Controller buttons\0"))
        {
            Sc.KeyboardLabels = labels == 0;
            PatchSettings.Set(Sc.LabelsKey, Sc.KeyboardLabels ? "keyboard" : "controller");
        }
        Tip("Keyboard keys also renames the Equipment page's Select button row to Hotkeys.");

        TakeCapturedKey();
        for (int i = 0; i < Sc.Count; i++) DrawShortcut(i);
        PatchSettings.Note("The pad's own Select button always works for shortcut 1. With no key for shortcut 1, " +
                           "the in-game list shows the game's own Select key.");

        bool dpad = Sc.DpadTakeover;
        if (ImGui.Checkbox("D-pad directions used by a shortcut don't walk or turn", ref dpad))
        {
            Sc.DpadTakeover = dpad;
            PatchSettings.Set(Sc.DpadKey, dpad);
        }
        Tip("Only in the world: menus still scroll with the d-pad, and the sticks are untouched.");

        string from = CardFiles.LastFile;
        PatchSettings.Note($"Shortcuts 2-5 are kept with each memory-card save. Loaded from: {(from.Length > 0 ? from : "nothing yet")}.");
    }

    /// <summary>A shortcut waiting for its key takes the first key down; Escape cancels,
    /// and a key another shortcut had moves over.</summary>
    static void TakeCapturedKey()
    {
        int capture = Sc.Capture;
        if (capture < 0) return;
        if (HostWindow.IsKeyDown(Key.Escape))
        {
            Sc.Capture = -1;
            return;
        }
        foreach (Key k in Enum.GetValues<Key>())
        {
            if (k == Key.Unknown || k == Key.Escape || !HostWindow.IsKeyDown(k)) continue;
            for (int j = 0; j < Sc.Count; j++)
                if (j != capture && Sc.Keys[j] == k) SetKey(j, Key.Unknown);
            SetKey(capture, k);
            Sc.Capture = -1;
            return;
        }
    }

    static void SetKey(int i, Key k)
    {
        Sc.Keys[i] = k;
        PatchSettings.Set(Sc.KeyKey(i), k.ToString());
    }

    static void DrawShortcut(int i)
    {
        ImGui.PushID(i);
        ImGui.Text(i == 0 ? "Shortcut 1 (Select):" : $"Shortcut {i + 1}:");
        ImGui.SameLine(NameColumn);

        string keyText = Sc.Capture == i ? "Press a key (Esc cancels)" : Labels.KeyLabel(Sc.Keys[i]);
        if (ImGui.Button(keyText + "##key", new Vector2(KeyWidth, 0f))) Sc.Capture = Sc.Capture == i ? -1 : i;
        ImGui.SameLine();
        if (ImGui.Button("No key")) SetKey(i, Key.Unknown);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(PadWidth);
        if (ImGui.BeginCombo("##pad", PadName(i, Sc.Pads[i])))
        {
            for (int p = 0; p < Sc.PadButtons.Length; p++)
                if (ImGui.Selectable(PadName(i, p), p == Sc.Pads[i]))
                {
                    Sc.Pads[i] = p;
                    PatchSettings.Set(Sc.PadKey(i), (float)Sc.PadButtons[p].Button);
                }
            ImGui.EndCombo();
        }

        if (Sc.Contents(i) is { Length: > 0 } holds)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(holds);
            if (i > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear")) Sc.Clear(i);
            }
        }
        ImGui.PopID();
    }

    static string PadName(int shortcut, int pad) =>
        shortcut == 0 && pad == 0 ? "Select only" : Sc.PadButtons[pad].Name;

    static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }
}
