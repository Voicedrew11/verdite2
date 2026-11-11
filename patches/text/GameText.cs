using System;
using System.Collections.Generic;
using System.Text;

namespace Kf2.Text;

/// <summary>
/// The unmodded game's own text in this mod's fonts (UsText.cs), as the layer
/// underneath whatever other mods set. Two parts, each with its own switch:
///
///  - Pictures: every sign, dialogue line, description and place card is redrawn
///    from its US text (Pictures.SetBase). A picture another mod gives text to
///    takes that mod's text instead; when the mod takes it back, this shows again.
///  - Normal case: menu-font text the game draws in capitals is drawn in its normal
///    capitalisation (Use Item, Short Sword, Checking the RAM card) for as long as
///    the draw lasts (MenuLabels.Draw); RAM keeps the game's own bytes. Only a label
///    that is still the disc's capitals is changed, so a translation that has
///    rewritten one keeps its own. The short messages in play (HudMessages) take
///    their sentence-case text from here too, and the port's own settings page and
///    the map mod's are drawn in title case to match.
/// </summary>
static class GameText
{
    public static bool ShowPictures = true;
    public static bool NormalCase = true;

    const byte SpaceCell = 0x7F;

    /// <summary>Put the US pictures in place, or take them away.</summary>
    public static void SetPictures(bool on)
    {
        ShowPictures = on;
        Pictures.ClearBase();
        if (!on) return;
        foreach (var (key, text) in UsText.Pictures)
        {
            int archive = key[0] == 'T' ? 3 : 6;
            if (int.TryParse(key.AsSpan(1), out int entry)) Pictures.SetBase(archive, entry, "auto", text);
        }
    }

    // Each label's cells in normal case, by its capitals; null where it has none.
    static readonly Dictionary<string, byte[]?> _cased = new();

    /// <summary>A label's cells with its letters in normal case, the same number of
    /// cells and its padding as it was; or null to draw it as it is: switched off,
    /// lower case not in the menu font yet, a cell that is not the disc's (another
    /// mod's text), or text this does not know.</summary>
    public static List<byte>? Cased(List<byte> cells)
    {
        if (!NormalCase || !MenuCells.Ready || cells.Count == 0) return null;
        int lead = 0, end = cells.Count;
        while (lead < end && cells[lead] == SpaceCell) lead++;
        while (end > lead && cells[end - 1] == SpaceCell) end--;
        if (end == lead) return null;

        var sb = new StringBuilder(end - lead);
        for (int i = lead; i < end; i++)
        {
            byte b = cells[i];
            if (b == SpaceCell) { sb.Append(' '); continue; }
            if (b >= 0x40) return null;                 // an added cell: already someone's own text
            char ch = MenuCells.Char(b);
            if (ch == '\0') return null;
            sb.Append(ch);
        }
        string core = sb.ToString();

        if (!_cased.TryGetValue(core, out var enc))
        {
            enc = null;
            string? want = UsText.Menu.TryGetValue(core, out var w) ? w : TitleCase(core);
            if (want != null && want != core && want.Length == core.Length)
            {
                var e = MenuCells.Encode(want, core.Length);
                if (e.Length == core.Length + 1) enc = e[..^1];   // every letter has a cell
            }
            _cased[core] = enc;
            if (Lettering.Probe) Console.WriteLine($"[KF2] text: label \"{core}\" " + (enc != null ? $"drawn \"{want}\"" : "kept"));
        }
        if (enc == null) return null;
        var outCells = new List<byte>(cells);
        for (int k = 0; k < enc.Length; k++) outCells[lead + k] = enc[k];
        return outCells;
    }

    /// <summary>For other mods: a label in the game's capitals as it will be drawn
    /// (SHORT SWORD as Short Sword), so a mod that lays text out can measure it;
    /// anything else, or with the normal case off, as it is.</summary>
    public static string Shown(string text)
    {
        if (!NormalCase || !MenuCells.Ready) return text;
        string core = text.Trim(' ');
        if (core.Length == 0) return text;
        string? want = UsText.Menu.TryGetValue(core, out var w) ? w : TitleCase(core);
        if (want == null || want.Length != core.Length) return text;
        int lead = text.Length - text.TrimStart(' ').Length;
        return text.Substring(0, lead) + want + text.Substring(lead + core.Length);
    }

    // ---- the port's settings page, the map's, and the Encyclopedia mod's titles ----

    // Their words, all in capitals on the page. A label made only of these is drawn
    // in title case. A word holding a digit (1440P, 16/9, X1.25, 4X) is kept, with
    // its X and P in lower case.
    static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "PORT", "SETTINGS", "DISPLAY", "GRAPHICS", "WORLD", "GAMEPLAY", "CONTROLS", "SOUND",
        "WINDOWED", "FULLSCREEN", "BORDERLESS", "RESOLUTION", "ASPECT", "HUD", "AT", "EDGES",
        "FRAME", "RATE", "UNCAPPED", "ORIGINAL", "NO", "SMOOTH", "TEXTURE", "FILTER", "TRILINEAR",
        "MIPMAPS", "PER-PIXEL", "LIGHT", "AMB.", "OCCLUSION", "LOW", "MEDIUM", "HIGH", "SHADING",
        "DITHER", "NONE", "RENDER", "DISTANCE", "ENHANCED", "FOG", "CORNERS", "PIXEL", "DEPTH",
        "WATER", "FULL", "CUSTOM", "RELOAD", "ON", "OFF", "DEATH", "COMPARE", "GEAR", "ENEMY", "TURN",
        "ACCEL", "INVERTED", "CAMERA", "STICK", "MOUSE", "SENS", "GYRO", "RUMBLE", "HD", "AUDIO",
        "SPEAKERS", "HEADPHONES", "RUN", "SETUP", "SAVE", "DISCARD", "CHANGES", "MAP", "WHOLE",
        "AREA", "AS", "YOU", "GO",
        // The gear comparison's (GearCompare), drawn through MenuDraw.
        "OFFENSE", "DEFENSE", "NEW", "CHANGE", "TOTAL", "STR", "MAG", "POWER",
        // The Encyclopedia's page titles, so they match the game's own around them.
        "ENCYCLOPEDIA", "ENEMIES", "PEOPLE", "PLACES", "ITEMS", "ACCESSORY",
    };
    static readonly HashSet<string> Kept = new(StringComparer.Ordinal) { "HUD", "HD" };
    static bool _harvested;

    /// <summary>The PORT SETTINGS page's own words, read off its declaration the
    /// first time a label is cased, so a row added there is cased without a change
    /// here: its pages, every row's label and every value a row can show.</summary>
    static void HarvestPortSettings()
    {
        if (_harvested) return;
        _harvested = true;
        var labels = new List<string>(Kf2.PortSettings.Pages)
            { "PORT SETTINGS", "RUN SETUP", "SAVE CHANGES", "DISCARD CHANGES", "RETURN" };
        foreach (var row in Kf2.PortSettings.Menu)
        {
            if (row.MenuLabel is { } label) labels.Add(label);
            if (row.MenuValue is not { } value) continue;
            foreach (double v in row.Steps)
            {
                try { labels.Add(value(v)); }
                catch (Exception) { }               // a value that needs live state; its words are elsewhere
            }
        }
        foreach (var label in labels)
            foreach (var w in label.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                bool letters = true;
                foreach (char ch in w) letters &= ch is (>= 'A' and <= 'Z') or '-' or '.';
                if (letters && Words.Add(w) && Lettering.Probe) Console.WriteLine($"[KF2] text: PORT SETTINGS word {w}");
            }
    }
    static readonly HashSet<string> Small = new(StringComparer.Ordinal) { "AT", "ON", "AS", "OF" };

    /// <summary>A settings-page label in title case, or null when it holds a word
    /// that is not the page's.</summary>
    static string? TitleCase(string label)
    {
        HarvestPortSettings();
        var words = label.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i];
            if (w.Length == 0) continue;
            bool digit = false;
            foreach (char ch in w) if (ch >= '0' && ch <= '9') digit = true;
            if (digit) { words[i] = w.Replace('X', 'x').Replace('P', 'p'); continue; }
            if (!Words.Contains(w)) return null;
            if (Kept.Contains(w)) continue;
            if (i > 0 && Small.Contains(w)) { words[i] = w.ToLowerInvariant(); continue; }
            var parts = w.Split('-');
            for (int k = 0; k < parts.Length; k++)
                if (parts[k].Length > 0) parts[k] = parts[k].Substring(0, 1) + parts[k].Substring(1).ToLowerInvariant();
            words[i] = string.Join("-", parts);
        }
        return string.Join(" ", words);
    }
}
