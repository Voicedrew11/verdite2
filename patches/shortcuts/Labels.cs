using System.Text;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Memory;
using Silk.NET.Input;

namespace Kf2.Shortcuts;

/// <summary>
/// The shortcuts' words in the menu font. Names are copied cell for cell out of the
/// game's name slots (items 0x80065B24, spells 0x80066664, 24 bytes each), never decoded
/// and re-encoded, so whatever the lettering or a translation does to those slots or to
/// the label routine applies here as in the game's own menus. Shortcuts' own words are
/// encoded through the "kf2.text" slot (<see cref="TextApi"/>) when its lowercase is in
/// the font, in the disc's capitals otherwise.
/// </summary>
static class Labels
{
    internal const uint NameStride = 0x18;
    const uint ItemNames = 0x80065B24, SpellNames = 0x80066664;
    internal const uint SelectLabel = 0x8006574C;            // the Equipment page's "■ BUTTON"
    internal const uint RemoveLabel = 0x8006577C;            // the shortcut list's empty row, "REMOVE"
    const int ItemCount = 120, SpellCount = 20;
    internal const byte End = 0xFF;
    const byte Space = 0x7F;
    internal const int MaxCells = 23;

    /// <summary>The name cells of a spell, else an item, else null.</summary>
    internal static byte[]? Name(IMemory m, byte spell, byte item)
    {
        if (spell != Shortcuts.None && spell < SpellCount) return Cells(m, SpellNames + spell * NameStride);
        if (item != Shortcuts.None && item < ItemCount) return Cells(m, ItemNames + item * NameStride);
        return null;
    }

    /// <summary>What shortcut <paramref name="row"/> holds: row 0 is the game's Select slot.</summary>
    internal static byte[] Contents(IMemory m, int row)
    {
        byte spell, item;
        if (row == 0)
        {
            spell = m.ReadU8(Shortcuts.SlotSpell);
            item = m.ReadU8(Shortcuts.SlotItem);
        }
        else
        {
            var s = Shortcuts.Get(row);
            spell = s.Spell;
            item = s.Item;
        }
        return Name(m, spell, item) ?? Word("empty", "Empty");
    }

    /// <summary>Row 0 is the game's Select; rows 1..4 are the port's. Normal case:
    /// <see cref="Word"/> puts it in capitals when the font has no lowercase.</summary>
    internal static string Label(int row)
    {
        if (!Shortcuts.KeyboardLabels)
            return row == 0 && Shortcuts.Pads[0] == 0 ? "Select" : Shortcuts.PadButtons[Shortcuts.Pads[row]].Label;
        if (row == 0 && Shortcuts.Keys[0] == Key.Unknown) return SelectKeyLabel();
        return KeyLabel(Shortcuts.Keys[row]);
    }

    static string SelectKeyLabel()
    {
        foreach (var name in new[] { ConfigManager.Game.Keys.Select, ConfigManager.Game.Keys2.Select })
            if (!string.IsNullOrEmpty(name) && Enum.TryParse<Key>(name, out var k)) return KeyLabel(k);
        return "Select";
    }

    static readonly Dictionary<string, string> ShortKeyNames = new()
    {
        ["ShiftLeft"] = "L Shift", ["ShiftRight"] = "R Shift",
        ["ControlLeft"] = "L Ctrl", ["ControlRight"] = "R Ctrl",
        ["AltLeft"] = "L Alt", ["AltRight"] = "R Alt",
        ["Backspace"] = "Bksp", ["CapsLock"] = "Caps", ["GraveAccent"] = "Tilde",
        ["Minus"] = "-", ["Equal"] = "=", ["Slash"] = "/", ["Period"] = ".", ["Comma"] = ",",
        ["Apostrophe"] = "'", ["Semicolon"] = "Semi",
        ["LeftBracket"] = "L Brkt", ["RightBracket"] = "R Brkt", ["BackSlash"] = "BSlash",
    };

    /// <summary>A key's name, short enough for the list.</summary>
    internal static string KeyLabel(Key k)
    {
        if (k == Key.Unknown) return "-";
        string n = k.ToString();
        if (n.StartsWith("Number") && n.Length == 7) return n[6..];
        if (n.StartsWith("Keypad") && n.Length == 7) return "Num " + n[6..];
        if (ShortKeyNames.TryGetValue(n, out var shortName)) return shortName;
        var s = new StringBuilder();
        foreach (char ch in n)
            if (char.IsLetterOrDigit(ch)) s.Append(ch);
        string t = s.ToString();
        return t.Length > 7 ? t[..7] : t;
    }

    /// <summary>A name slot's cells as they are in RAM, whoever wrote them, then 0xFF.</summary>
    internal static byte[] Cells(IMemory m, uint slot)
    {
        var b = new List<byte>((int)NameStride);
        for (uint i = 0; i < MaxCells; i++)
        {
            byte g = m.ReadU8(slot + i);
            if (g == End) break;
            b.Add(g);
        }
        b.Add(End);
        return [.. b];
    }

    internal static void WriteCells(IMemory m, uint at, byte[] cells)
    {
        int n = 0;
        for (; n < cells.Length && n < MaxCells && cells[n] != End; n++) m.WriteU8(at + (uint)n, cells[n]);
        m.WriteU8(at + (uint)n, End);
    }

    /// <summary>The port's own word in the menu font. With the lettering's lowercase
    /// loaded, as written (a translation can reword it through the string table, key
    /// "shortcuts:" + <paramref name="key"/>); otherwise in the disc's capitals.</summary>
    internal static byte[] Word(string? key, string text)
    {
        if (TextApi.Present)
        {
            if (key != null && TextApi.GetString("shortcuts:" + key) is { Length: > 0 } custom) text = custom;
            if (TextApi.MenuReady && TextApi.MenuEncode(text, MaxCells) is { Length: > 1 } encoded) return encoded;
        }
        return Capitals(text);
    }

    /// <summary>The disc's font: A-Z 0x00-0x19, 0-9 0x20-0x29,
    /// ". , ' - = / * # !" 0x30-0x38, "?" 0x3A, space 0x7F.</summary>
    const string Signs = ".,'-=/*#!";

    static byte[] Capitals(string s)
    {
        var b = new List<byte>(s.Length + 1);
        foreach (char raw in s.ToUpperInvariant())
        {
            if (raw >= 'A' && raw <= 'Z') b.Add((byte)(raw - 'A'));
            else if (raw >= '0' && raw <= '9') b.Add((byte)(0x20 + raw - '0'));
            else if (raw == ' ') b.Add(Space);
            else if (raw == '?') b.Add(0x3A);
            else if (Signs.IndexOf(raw) is int k && k >= 0) b.Add((byte)(0x30 + k));
            if (b.Count == MaxCells) break;
        }
        b.Add(End);
        return [.. b];
    }

    /// <summary>A cell's character: the lettering's map when it is published, the disc's
    /// otherwise; '\0' if unknown.</summary>
    static char CharOf(byte g)
    {
        if (g == Space) return ' ';
        if (TextApi.Present && TextApi.MenuChar(g) is var c && c != '\0') return c;
        if (g < 26) return (char)('A' + g);
        if (g >= 0x20 && g <= 0x29) return (char)('0' + g - 0x20);
        if (g >= 0x30 && g < 0x30 + Signs.Length) return Signs[g - 0x30];
        if (g == 0x3A) return '?';
        return '\0';
    }

    internal static string Text(byte[] cells)
    {
        var s = new StringBuilder();
        foreach (byte g in cells)
        {
            if (g == End) break;
            char c = CharOf(g);
            s.Append(c == '\0' ? ' ' : c);
        }
        return s.ToString().Trim();
    }

    /// <summary>How wide the cells draw: the lettering's menu spacing when it is
    /// published, the game's 7 a letter otherwise.</summary>
    internal static int Width(byte[] cells)
    {
        int n = 0;
        while (n < cells.Length && cells[n] != End) n++;
        if (TextApi.Present && TextApi.MenuReady && TextApi.MenuPlace(TextApi.MenuShown(Text(cells))) is { Width: > 0 } placed)
            return placed.Width;
        return n * 7;
    }
}

/// <summary>
/// The text functions published in the process data slot "kf2.text": by the port's
/// lettering (patches/text/Lettering.cs), or by the Text mod while it is enabled in its
/// place. Looked up at each call, so either serves and neither is required.
/// </summary>
static class TextApi
{
    static IDictionary<string, object>? Api => AppDomain.CurrentDomain.GetData("kf2.text") as IDictionary<string, object>;

    static T? Get<T>(string name) where T : class =>
        Api is { } api && api.TryGetValue(name, out var f) ? f as T : null;

    public static bool Present => Api != null;

    /// <summary>Lower case is in the menu font (until then text is encoded in capitals).</summary>
    public static bool MenuReady => Get<Func<bool>>("MenuReady")?.Invoke() ?? false;

    /// <summary>The character a menu font cell shows, '\0' if none.</summary>
    public static char MenuChar(int index) => Get<Func<int, char>>("MenuChar")?.Invoke(index) ?? '\0';

    /// <summary>Text as menu font cells, at most <paramref name="max"/>, then 0xFF.</summary>
    public static byte[] MenuEncode(string text, int max) =>
        Get<Func<string, int, byte[]>>("MenuEncode")?.Invoke(text, max) ?? [0xFF];

    /// <summary>The game's own capitals as the lettering draws them.</summary>
    public static string MenuShown(string text) => Get<Func<string, string>>("MenuShown")?.Invoke(text) ?? text;

    /// <summary>A string as menu cells, their offsets and the width; null when unpublished.</summary>
    public static (int[] Cells, int[] X, int Width)? MenuPlace(string text) =>
        Get<Func<string, (int[] Cells, int[] X, int Width)>>("MenuPlace")?.Invoke(text);

    /// <summary>The shared string table.</summary>
    public static string? GetString(string key) => Get<Func<string, string?>>("GetString")?.Invoke(key);
}
