using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host;
using Silk.NET.Input;
using Kf2.Shortcuts;
using Sc = Kf2.Shortcuts.Shortcuts;

namespace Kf2.Settings;

/// <summary>
/// <see cref="Sc"/>'s settings, under Gameplay: the switch the mod had in the Mods panel,
/// the in-game labels, and a key for each hotkey and each hand. The pad's layout is
/// fixed (R1, L1, Square, the d-pad), so it has no rows.
/// Host thread. See "Shortcuts" in docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class ShortcutsPage : IPatchPage
{
    public string Id => "shortcuts";

    public string Title => "";

    public int Order => 28;

    public IReadOnlyList<string> Patches => ["kf2.shortcuts.port"];

    const float KeyWidth = 170f, NameColumn = 150f;

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
            : "Souls-style hands and hotkeys: two weapons in the right hand, two attack spells in the left, five hotkey items, and the pad laid out for them.");

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
        PatchSettings.Note("On the pad, in the world: R1 swings the weapon, L1 casts the attack spell, Square uses the " +
                           "current hotkey, d-pad Right and Left swap the weapon and the spell, Down and Up step through " +
                           "the hotkeys. The sticks walk, strafe, turn and look (3D controls are set aside while this is on). " +
                           "Menus keep the pad as it was.");
        PatchSettings.Note("To assign: in Equipment, a weapon or an attack spell asks which of its hand's two slots it goes in " +
                           "after Yes; the Select button row (Hotkeys with keyboard labels) asks which hotkey. Hotkey 1 is the " +
                           "game's own Select shortcut.");

        int labels = Sc.KeyboardLabels ? 0 : 1;
        if (ImGui.Combo("In-game labels", ref labels, "Keyboard keys\0Controller buttons\0"))
        {
            Sc.KeyboardLabels = labels == 0;
            PatchSettings.Set(Sc.LabelsKey, Sc.KeyboardLabels ? "keyboard" : "controller");
        }
        Tip("Keyboard keys also renames the Equipment page's Select button row to Hotkeys.");

        TakeCapturedKey();
        for (int i = 0; i < Sc.KeyRows; i++) DrawKeyRow(i);
        PatchSettings.Note("The pad's own Select button always uses hotkey 1. With no key for hotkey 1, " +
                           "the in-game list shows the game's own Select key.");

        string from = CardFiles.LastFile;
        PatchSettings.Note($"Hotkeys and hands are kept with each memory-card save. Loaded from: {(from.Length > 0 ? from : "nothing yet")}.");
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
            for (int j = 0; j < Sc.KeyRows; j++)
                if (j != capture && Sc.KeyOf(j) == k) SetKey(j, Key.Unknown);
            SetKey(capture, k);
            Sc.Capture = -1;
            return;
        }
    }

    static void SetKey(int row, Key k)
    {
        Sc.SetKey(row, k);
        PatchSettings.Set(Sc.KeyKeyOf(row), k.ToString());
    }

    static string RowName(int row) => row switch
    {
        Sc.WeaponRow => "Swap weapon:",
        Sc.SpellRow => "Swap spell:",
        0 => "Hotkey 1 (Select):",
        _ => $"Hotkey {row + 1}:",
    };

    static void DrawKeyRow(int i)
    {
        ImGui.PushID(i);
        ImGui.Text(RowName(i));
        ImGui.SameLine(NameColumn);

        string keyText = Sc.Capture == i ? "Press a key (Esc cancels)" : Labels.KeyLabel(Sc.KeyOf(i));
        if (ImGui.Button(keyText + "##key", new Vector2(KeyWidth, 0f))) Sc.Capture = Sc.Capture == i ? -1 : i;
        ImGui.SameLine();
        if (ImGui.Button("No key")) SetKey(i, Key.Unknown);

        if (i < Sc.Count && Sc.Contents(i) is { Length: > 0 } holds)
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

    static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }
}
