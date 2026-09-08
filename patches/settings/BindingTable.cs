using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using Silk.NET.Input;

namespace Kf2.Settings;

/// <summary>
/// The sixteen pad buttons, what each one does in King's Field, and what
/// presses it — the middle column being the whole reason this exists.
///
/// **It is a copy of the runtime's table rather than a call into it**, because
/// <c>InputSettingsSection</c> is <c>internal</c>: the port replaces that section
/// by id (see <see cref="InputSection"/>), so the runtime's own body is
/// unreachable and would not run anyway. Copied verbatim from
/// <c>tools/RecompOne/RecompOne.Runtime/Host/Window/Settings/Sections/InputSettingsSection.cs</c>
/// at the pin in <c>scripts/setup_tools.sh</c>: the row tuples, the per-row
/// capture, and <see cref="PadLabel"/>'s SDL indices. **That last one is the only
/// number the port duplicates rather than derives** — it is <c>InputManager</c>'s
/// encoding, and a change to it there is silent here and shows as a mislabelled
/// binding. A pin bump should diff that file.
///
/// Two things are deliberately *not* copied:
///
/// **Pad 1 / Pad 2.** The runtime offers a second controller and this game has
/// none: <c>BiosB.PadRead</c> packs pad 2 into the high half of the pad word and
/// the game keeps only the low sixteen bits, at <c>0x80199554</c>. So the whole
/// tab was a set of bindings that could not reach the game. Note this stops the
/// port *offering* pad 2 and does not stop the runtime *having* one —
/// <c>InputManager.Poll</c> still fills <c>Controller.State2</c> from
/// <c>Keys2</c>/<c>Pad2</c>, and those two are never read or written here, so a
/// <c>settings.json</c> that already carries them keeps them byte for byte.
///
/// **The action column.** <c>docs/INPUT.md</c> records a rule against naming the
/// verb behind a button, on the grounds that the port presses a button and the
/// game's own control-config screen decides what it means. That rule is kept and
/// the column is not a breach of it: the strings are the game's *measured
/// defaults* — the action-mask table at <c>0x8006E568</c>-<c>0x8006E5D0</c>, and
/// <c>func_8002957C</c>'s four branches — the header says so, and
/// <see cref="InputSection"/> draws one note under the table saying the game can
/// reassign them. Sixteen rows of <c>Cross</c> and <c>L1</c> told a King's Field
/// player nothing, which is the cost the rule was quietly charging.
/// </summary>
static class BindingTable
{
    // Label, the two binding accessors for each device, and what the button does
    // in King's Field by default. Order is the PSX pad's own, matching the
    // runtime's, so a player comparing this pane with any other is not reading a
    // reshuffled list.
    //
    // Provenance for the last column, all measured, none guessed:
    //   the action-mask table 0x8006E568-0x8006E5D0 -- Left/Right turn,
    //   Up/Down walk, R1/L1 strafe right/left, R2/L2 pitch (R2 looks down),
    //   entry 1 = Circle opens the menu, entry 0 = Cross confirms;
    //   func_8002957C -- Square attack, Cross the action button, Triangle cast,
    //   Select the second slot.
    // See "The action-mask table" in docs/GAME_INTERNALS.md and "Which pad
    // buttons the defaults are, and why" in docs/INPUT.md.
    //
    // Start is blank because its branch has never been identified, and a guess
    // in a column read as measurement is worse than a gap. L3 and R3 say the
    // game does not read them, which is a fact a blank would not carry: a player
    // would otherwise learn it by binding one and finding nothing happens.
    static readonly (string Label,
                     Func<KeyBindings, string> GetKey, Action<KeyBindings, string> SetKey,
                     Func<GamepadBindings, int[]> GetPad, Action<GamepadBindings, int[]> SetPad,
                     string Action)[] _rows =
    [
        ("Cross",    b => b.Cross,    (b,v) => b.Cross = v,    p => p.Cross,    (p,v) => p.Cross = v,    "use, open; confirm"),
        ("Circle",   b => b.Circle,   (b,v) => b.Circle = v,   p => p.Circle,   (p,v) => p.Circle = v,   "the in-game menu"),
        ("Square",   b => b.Square,   (b,v) => b.Square = v,   p => p.Square,   (p,v) => p.Square = v,   "attack"),
        ("Triangle", b => b.Triangle, (b,v) => b.Triangle = v, p => p.Triangle, (p,v) => p.Triangle = v, "cast"),
        ("L1",       b => b.L1,       (b,v) => b.L1 = v,       p => p.L1,       (p,v) => p.L1 = v,       "strafe left"),
        ("R1",       b => b.R1,       (b,v) => b.R1 = v,       p => p.R1,       (p,v) => p.R1 = v,       "strafe right"),
        ("L2",       b => b.L2,       (b,v) => b.L2 = v,       p => p.L2,       (p,v) => p.L2 = v,       "look up"),
        ("R2",       b => b.R2,       (b,v) => b.R2 = v,       p => p.R2,       (p,v) => p.R2 = v,       "look down"),
        ("L3",       b => b.L3,       (b,v) => b.L3 = v,       p => p.L3,       (p,v) => p.L3 = v,       "(the game does not read it)"),
        ("R3",       b => b.R3,       (b,v) => b.R3 = v,       p => p.R3,       (p,v) => p.R3 = v,       "(the game does not read it)"),
        ("Start",    b => b.Start,    (b,v) => b.Start = v,    p => p.Start,    (p,v) => p.Start = v,    ""),
        ("Select",   b => b.Select,   (b,v) => b.Select = v,   p => p.Select,   (p,v) => p.Select = v,   "second spell or item"),
        ("Up",       b => b.Up,       (b,v) => b.Up = v,       p => p.Up,       (p,v) => p.Up = v,       "walk forward"),
        ("Down",     b => b.Down,     (b,v) => b.Down = v,     p => p.Down,     (p,v) => p.Down = v,     "walk back"),
        ("Left",     b => b.Left,     (b,v) => b.Left = v,     p => p.Left,     (p,v) => p.Left = v,     "turn left"),
        ("Right",    b => b.Right,    (b,v) => b.Right = v,    p => p.Right,    (p,v) => p.Right = v,    "turn right"),
    ];

    /// <summary>Every key the capture scans. Cached because the runtime's version
    /// calls <c>Enum.GetValues</c> inside the per-frame poll, which allocates a
    /// fresh array on every frame a row is waiting for a key.</summary>
    static readonly Key[] _keys = [.. Enum.GetValues<Key>()];

    /// <summary>
    /// Draw the table for one device.
    ///
    /// The capture row is the caller's, not a field here, because
    /// <see cref="InputSection"/> keeps one per tab: the runtime had a single
    /// index shared by two devices and two pad slots and had to clear it in four
    /// places, and two fields make switching tab mid-capture a non-event by
    /// construction rather than by remembering.
    /// </summary>
    public static void Draw(bool gamepad, ref int remapRow, ref bool remapAdd)
    {
        // Three columns rather than two and a SameLine: the binding cell is a
        // full-width button, so dimmed text after it cannot align, and sixteen
        // rows of unaligned text is a list rather than a column. A column also
        // earns a header, which is where "by default" gets said once instead of
        // sixteen times.
        if (!ImGui.BeginTable("##kf2-bindings", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn(Localization.T("settings.input.button"), ImGuiTableColumnFlags.WidthFixed, 78f);
        // English, and so is the column under it, while its two siblings come
        // from the runtime's own three-language table. That is not an oversight:
        // every string this port writes is English -- every tooltip, every note,
        // every combo entry under Video and Gameplay -- and a verb mistranslated
        // in a column a player reads as measurement is worse than one they can
        // see is the port's. It also keeps this off Localization.Merge, which
        // would otherwise want sixteen keys in three languages.
        ImGui.TableSetupColumn("In King's Field", ImGuiTableColumnFlags.WidthStretch, 0.85f);
        ImGui.TableSetupColumn(Localization.T(gamepad ? "settings.input.gamepad" : "settings.input.keyboard"),
            ImGuiTableColumnFlags.WidthStretch, 1.00f);
        ImGui.TableHeadersRow();

        var keys = ConfigManager.Game.Keys;
        var pad = ConfigManager.Game.Pad;

        for (int i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(row.Label);

            // Single-line and clipped by the column, not TextWrapped: sixteen
            // rows of uneven height would cost more of the pane than the whole
            // column is worth, and every string here is short enough to fit.
            ImGui.TableSetColumnIndex(1);
            Dim(row.Action);

            ImGui.TableSetColumnIndex(2);
            if (gamepad) PadCell(i, row, pad, ref remapRow, ref remapAdd);
            else KeyCell(i, row, keys, ref remapRow);
        }

        ImGui.EndTable();
    }

    static void KeyCell(int i,
        (string Label, Func<KeyBindings, string> GetKey, Action<KeyBindings, string> SetKey,
         Func<GamepadBindings, int[]> GetPad, Action<GamepadBindings, int[]> SetPad, string Action) row,
        KeyBindings keys, ref int remapRow)
    {
        bool awaiting = remapRow == i;
        var key = row.GetKey(keys);
        var text = awaiting
            ? Localization.T("settings.input.press_key")
            : $"{(key.Length == 0 ? Localization.T("settings.input.unbound") : key)}##k{i}";

        if (ImGui.Button(text, new System.Numerics.Vector2(-1, 0))) remapRow = i;
        if (!awaiting) return;

        if (GetPressedKey() is { } pressed)
        {
            row.SetKey(keys, pressed);
            remapRow = -1;
            ConfigManager.SaveGame();
        }
    }

    static void PadCell(int i,
        (string Label, Func<KeyBindings, string> GetKey, Action<KeyBindings, string> SetKey,
         Func<GamepadBindings, int[]> GetPad, Action<GamepadBindings, int[]> SetPad, string Action) row,
        GamepadBindings pad, ref int remapRow, ref bool remapAdd)
    {
        bool awaiting = remapRow == i;
        var bindings = row.GetPad(pad);
        string text = awaiting
            ? Localization.T(remapAdd ? "settings.input.press_button_add" : "settings.input.press_button")
            : bindings.Length == 0 ? Localization.T("settings.input.unbound")
                                   : string.Join(" | ", bindings.Select(PadLabel));

        float plusW = ImGui.GetFrameHeight();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.Button($"{text}##p{i}", new System.Numerics.Vector2(-plusW - spacing, 0)))
        {
            remapRow = i;
            remapAdd = false;
        }
        ImGui.SameLine();
        if (ImGui.Button($"+##add{i}", new System.Numerics.Vector2(plusW, 0)))
        {
            remapRow = i;
            remapAdd = true;
        }

        if (!awaiting) return;

        // 0032: InputManager is internal, so this and IsPadConnected are reached
        // through HostWindow, the same way the mouse is.
        if (HostWindow.GetFirstPressedPadButton(0) is { } p)
        {
            if (remapAdd)
            {
                if (!bindings.Contains(p)) row.SetPad(pad, [.. bindings, p]);
            }
            else row.SetPad(pad, [p]);

            remapRow = -1;
            ConfigManager.SaveGame();
        }
    }

    static string? GetPressedKey()
    {
        foreach (var k in _keys)
        {
            if (k is Key.Unknown or Key.Menu) continue;
            if (HostWindow.IsKeyDown(k)) return k.ToString();
        }
        return null;
    }

    /// <summary>InputManager's own encoding: face and shoulder buttons are their
    /// SDL index, the triggers are 100/101, the stick directions 102-109.</summary>
    static string PadLabel(int b) => b switch
    {
        0 => "Cross (A)",
        1 => "Circle (B)",
        2 => "Square (X)",
        3 => "Triangle (Y)",
        4 => "Select (Back)",
        5 => "Guide",
        6 => "Start",
        7 => "L3 (LStick)",
        8 => "R3 (RStick)",
        9 => "L1 (LBumper)",
        10 => "R1 (RBumper)",
        11 => "D-Up",
        12 => "D-Down",
        13 => "D-Left",
        14 => "D-Right",
        100 => "L2 (LTrigger)",
        101 => "R2 (RTrigger)",
        102 => "LStick Left",
        103 => "LStick Right",
        104 => "LStick Up",
        105 => "LStick Down",
        106 => "RStick Left",
        107 => "RStick Right",
        108 => "RStick Up",
        109 => "RStick Down",
        _ => $"Btn {b}",
    };

    /// <summary>ImGuiEx.TextDisabled is internal to the runtime, so the port
    /// spells it out — the same three lines every page here already carries.</summary>
    static void Dim(string text)
    {
        if (text.Length == 0) return;
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }
}
