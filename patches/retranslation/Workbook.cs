// Reading the translation straight from the workbook (KF2_retranslation.ods, from
// verdite2-mods' mods/retranslation) in the data directory's retranslation/ folder:
// the "Mod EN" column of each sheet replaces the built-in text in Script.cs, by the
// same rules as verdite2-mods' tools/build_text_cs.py. The Movies sheet is not read.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Kf2.Retranslation;

/// <summary>
/// The translation tables (Script.cs), as a copy that can be filled from the
/// workbook and then put in place in one go.
/// </summary>
sealed class Tables
{
    public readonly Dictionary<string, string> Messages = new(), Coded = new(), Encyclopedia = new(),
                                               Cards = new();
    public (uint Address, string Text)[] Strings = [];
    public string[] Hud = [];

    /// <summary>What Script.cs holds now.</summary>
    public static Tables Current()
    {
        var t = new Tables();
        lock (Script.Sync)
        {
            Copy(Script.Messages, t.Messages);
            Copy(Script.Coded, t.Coded);
            Copy(Script.Encyclopedia, t.Encyclopedia);
            Copy(Script.EncyclopediaCards, t.Cards);
            t.Strings = (ValueTuple<uint, string>[])Script.Strings.Clone();
            t.Hud = (string[])Script.Hud.Clone();
        }
        return t;
    }

    public Tables Clone()
    {
        var t = new Tables();
        Copy(Messages, t.Messages);
        Copy(Coded, t.Coded);
        Copy(Encyclopedia, t.Encyclopedia);
        Copy(Cards, t.Cards);
        t.Strings = (ValueTuple<uint, string>[])Strings.Clone();
        t.Hud = (string[])Hud.Clone();
        return t;
    }

    /// <summary>Put these tables in Script.cs's place. The slot list keeps its
    /// addresses and order; only the text changes.</summary>
    public void Install()
    {
        lock (Script.Sync)
        {
            Copy(Messages, Script.Messages);
            Copy(Coded, Script.Coded);
            Copy(Encyclopedia, Script.Encyclopedia);
            Copy(Cards, Script.EncyclopediaCards);
            for (int i = 0; i < Script.Strings.Length && i < Strings.Length; i++) Script.Strings[i] = Strings[i];
            for (int i = 0; i < Script.Hud.Length && i < Hud.Length; i++) Script.Hud[i] = Hud[i];
        }
    }

    /// <summary>How many entries differ from another copy.</summary>
    public int Differences(Tables other)
    {
        int n = Diff(Messages, other.Messages) + Diff(Coded, other.Coded) + Diff(Encyclopedia, other.Encyclopedia)
              + Diff(Cards, other.Cards);
        for (int i = 0; i < Strings.Length && i < other.Strings.Length; i++) if (Strings[i].Text != other.Strings[i].Text) n++;
        for (int i = 0; i < Hud.Length && i < other.Hud.Length; i++) if (Hud[i] != other.Hud[i]) n++;
        return n;
    }

    static void Copy(Dictionary<string, string> from, Dictionary<string, string> to)
    {
        to.Clear();
        foreach (var (k, v) in from) to[k] = v;
    }

    static int Diff(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        int n = 0;
        foreach (var (k, v) in a) if (!b.TryGetValue(k, out var w) || w != v) n++;
        foreach (var k in b.Keys) if (!a.ContainsKey(k)) n++;
        return n;
    }
}

static class Workbook
{
    const int MenuMax = 23;          // a 24-byte slot, with its end mark
    const int CodedMax = 11;         // Strings.CodedMax

    // ACCESS MEMORYCARD: the Japanese カードのNEW is unclear here, so the US text stays.
    static readonly HashSet<uint> SkipSlots = [0x80065764];

    // The Encyclopedia's own place cards: (its key, where the text comes from).
    static readonly (string Key, bool Place, string From)[] Cards =
    [
        ("Kraken's Nest", true, "Kraken's Nest"),
        ("Royal Treasure House", true, "Royal Treasure House"),
        ("The Space For Completion", false, "I143"),      // the sign's text, both lines
        ("Guyra's Dimension", true, "Guyra's Dimension"),
    ];

    // The picture fonts' letters (Kf2.Text.FontData.Charset), plus space and line break.
    static readonly string Charset = Kf2.Text.FontData.Charset + " \n";

    static Tables? _builtIn;

    /// <summary>The workbook last read, and when; null while the built-in text is in use.</summary>
    public static string? File { get; private set; }
    public static DateTime ReadAt { get; private set; }
    /// <summary>Entries that differ from the built-in text.</summary>
    public static int Changed { get; private set; }
    /// <summary>One line about the last read, for the settings page.</summary>
    public static string Status { get; private set; } = "Using the built-in text.";
    public static readonly List<string> Notes = new();

    /// <summary>Keep Script.cs's own tables, to go back to. Call once, before any import.</summary>
    public static void KeepBuiltIn() => _builtIn ??= Tables.Current();

    /// <summary>The data directory's retranslation/ folder (the repository root's for a
    /// dev run), beside fonts/.</summary>
    public static readonly string Folder = Path.GetFullPath("retranslation");

    /// <summary>The workbook in <see cref="Folder"/>: an .ods, the one named like
    /// "retranslation" if there are several. Null when there is none.</summary>
    public static string? Find()
    {
        try
        {
            string folder = Folder;
            if (!Directory.Exists(folder)) return null;
            string? any = null;
            foreach (var f in Directory.EnumerateFiles(folder, "*.ods"))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith(".~lock", StringComparison.Ordinal) || name.StartsWith('~')) continue;
                if (name.Contains("retranslation", StringComparison.OrdinalIgnoreCase)) return f;
                any ??= f;
            }
            return any;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KF2] retranslation: looking for the workbook: {ex.Message}");
            return null;
        }
    }

    /// <summary>True when the workbook read last has been saved again since.</summary>
    public static bool Stale()
    {
        try { return File != null && System.IO.File.Exists(File) && System.IO.File.GetLastWriteTimeUtc(File) > ReadAt; }
        catch { return false; }
    }

    /// <summary>The built-in text, as tables.</summary>
    public static Tables BuiltIn()
    {
        KeepBuiltIn();
        File = null;
        Changed = 0;
        Notes.Clear();
        Status = "Using the built-in text.";
        return _builtIn!.Clone();
    }

    /// <summary>Read a workbook into new tables (the built-in text where a cell is
    /// blank or doesn't fit). Null, with Status saying why, when it can't be read.</summary>
    public static Tables? Read(string path)
    {
        KeepBuiltIn();
        var notes = new List<string>();
        Tables t;
        DateTime saved;
        try
        {
            saved = System.IO.File.GetLastWriteTimeUtc(path);
            var sheets = Ods.Read(path);
            t = Build(sheets, notes);
        }
        catch (Exception ex)
        {
            Status = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}";
            Console.WriteLine($"[KF2] retranslation: {Status}");
            return null;
        }
        File = path;
        ReadAt = saved;
        Changed = t.Differences(_builtIn!);
        Notes.Clear();
        Notes.AddRange(notes);
        Status = $"Read {Path.GetFileName(path)}: {Changed} entries differ from the built-in text"
               + (notes.Count > 0 ? $", {notes.Count} notes." : ".");
        Console.WriteLine($"[KF2] retranslation: {Status}");
        foreach (var n in notes) Console.WriteLine($"[KF2] retranslation:   {n}");
        return t;
    }

    // ---- the sheets to the tables -------------------------------------------------

    static Tables Build(Dictionary<string, List<List<string>>> sheets, List<string> notes)
    {
        var t = _builtIn!.Clone();

        // Messages: Key (T0, I143, ...) -> Mod EN.
        foreach (var r in Sheet(sheets, "Messages", notes))
        {
            string k = Cell(r, "Key").Trim(), v = Clean(Cell(r, "Mod EN"));
            if (k.Length == 0) continue;
            if (!t.Messages.ContainsKey(k)) { notes.Add($"Messages {k}: not a disc entry the mod knows; skipped"); continue; }
            if (v.Length == 0) { notes.Add($"Messages {k}: Mod EN is blank; kept the built-in text"); continue; }
            Glyphs($"Messages {k}", v, notes);
            t.Messages[k] = v;
        }

        // Menus & Names: by US address, the slots the mod already knows; Code rows by their US text.
        var slots = new Dictionary<uint, int>();
        for (int i = 0; i < t.Strings.Length; i++) slots[t.Strings[i].Address] = i;
        foreach (var r in Sheet(sheets, "Menus & Names", notes))
        {
            string kind = Cell(r, "Kind").Trim(), v = Clean(Cell(r, "Mod EN"));
            if (kind == "Code")
            {
                string us = Cell(r, "Current US").Trim();
                if (us.Length == 0 || v.Length == 0 || v == us) continue;
                if (v.Length > CodedMax) { notes.Add($"Code label {us}: \"{v}\" is over {CodedMax} letters; skipped"); continue; }
                t.Coded[us] = v;
                continue;
            }
            if (!TryHex(Cell(r, "US address"), out uint a) || SkipSlots.Contains(a)) continue;
            if (!slots.TryGetValue(a, out int at)) { notes.Add($"Menus & Names 0x{a:x8}: not a slot the mod knows; skipped"); continue; }
            if (v.Length == 0) { notes.Add($"Menus & Names 0x{a:x8}: Mod EN is blank; kept the built-in text"); continue; }
            if (v.Length > MenuMax) { notes.Add($"Menus & Names 0x{a:x8}: \"{v}\" is over {MenuMax} characters; kept the built-in text"); continue; }
            t.Strings[at] = (a, v);
        }

        // Messages in play: H0-H35.
        foreach (var r in Sheet(sheets, "Messages in play", notes))
        {
            string k = Cell(r, "Key").Trim(), v = Clean(Cell(r, "Mod EN"));
            if (k.Length < 2 || k[0] != 'H' || !int.TryParse(k.AsSpan(1), out int i)) continue;
            if (i < 0 || i >= t.Hud.Length) { notes.Add($"Messages in play {k}: the page has {t.Hud.Length} messages; skipped"); continue; }
            if (v.Length == 0) { notes.Add($"Messages in play {k}: Mod EN is blank; kept the built-in text"); continue; }
            t.Hud[i] = v;
        }

        // Encyclopedia: its own English -> Mod EN; the first row wins for a shared name.
        var names = new Dictionary<string, string>();
        t.Encyclopedia.Clear();
        foreach (var r in Sheet(sheets, "Encyclopedia", notes))
        {
            string name = Cell(r, "Mod name (current)").Trim(), v = Clean(Cell(r, "Mod EN"));
            if (name.Length == 0 || v.Length == 0) continue;
            if (names.TryGetValue(name, out var first))
            {
                if (first != v)
                    notes.Add($"Encyclopedia \"{name}\": two rows give \"{first}\" and \"{v}\", but the Encyclopedia "
                            + $"uses one name for both; using \"{first}\"");
                continue;
            }
            names[name] = v;
            if (v != name) t.Encyclopedia[name] = v;
        }
        if (names.Count == 0) foreach (var (k, v) in _builtIn!.Encyclopedia) t.Encyclopedia[k] = v;   // no sheet: keep them
        foreach (var (key, place, source) in Cards)
        {
            string? v = place ? (names.TryGetValue(source, out var p) ? p : null)
                              : (t.Messages.TryGetValue(source, out var m) ? m : null);
            if (!string.IsNullOrEmpty(v)) t.Cards[key] = v;
        }

        return t;
    }

    /// <summary>A sheet's rows below its header, each as header -> cell. A sheet
    /// that is missing is noted and gives no rows (that part keeps the built-in text).</summary>
    static List<Dictionary<string, string>> Sheet(Dictionary<string, List<List<string>>> sheets, string name, List<string> notes)
    {
        var list = new List<Dictionary<string, string>>();
        if (!sheets.TryGetValue(name, out var rows) || rows.Count == 0)
        {
            notes.Add($"No \"{name}\" sheet: kept the built-in text for it");
            return list;
        }
        var head = rows[0];
        for (int r = 1; r < rows.Count; r++)
        {
            var d = new Dictionary<string, string>();
            for (int c = 0; c < head.Count; c++)
            {
                string h = head[c].Trim();
                if (h.Length > 0 && !d.ContainsKey(h)) d[h] = c < rows[r].Count ? rows[r][c] : "";
            }
            list.Add(d);
        }
        return list;
    }

    static string Cell(Dictionary<string, string> row, string column) => row.TryGetValue(column, out var v) ? v : "";

    /// <summary>The cell's text for the game: straight quotes (the fonts have no
    /// curly ones), no carriage returns or spaces at line ends.</summary>
    static string Clean(string s)
    {
        s = s.Replace("\r", "").Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"').Trim();
        var lines = s.Split('\n');
        for (int i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
        return string.Join('\n', lines);
    }

    static void Glyphs(string where, string text, List<string> notes)
    {
        var bad = new SortedSet<char>();
        foreach (char c in text) if (Charset.IndexOf(c) < 0) bad.Add(c);
        if (bad.Count > 0) notes.Add($"{where}: no letter in the font for {string.Join(' ', bad)}");
    }

    static bool TryHex(string s, out uint value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out value) && s.Length > 0;
    }
}

/// <summary>
/// Just enough of an OpenDocument spreadsheet reader: content.xml from the zip,
/// each sheet as rows of cell text, as LibreOffice shows it (paragraphs joined
/// by line breaks, repeated spaces and columns expanded, comments left out).
/// </summary>
static class Ods
{
    const int MaxColumns = 64;

    public static Dictionary<string, List<List<string>>> Read(string path)
    {
        string xml;
        // Shared, so it reads while LibreOffice has the file open.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = zip.GetEntry("content.xml") ?? throw new InvalidDataException("no content.xml in it (not an .ods file?)");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            xml = reader.ReadToEnd();
        }
        return Parse(xml);
    }

    public static Dictionary<string, List<List<string>>> Parse(string xml)
    {
        var sheets = new Dictionary<string, List<List<string>>>();
        List<List<string>>? rows = null;
        List<string>? row = null;
        StringBuilder? cell = null;
        int repeat = 1, paragraphs = 0, skip = 0;
        bool inP = false;

        int i = 0;
        while (i < xml.Length)
        {
            int lt = xml.IndexOf('<', i);
            if (lt < 0) break;
            if (lt > i && inP && skip == 0 && cell != null) Decode(xml, i, lt, cell);

            if (string.CompareOrdinal(xml, lt, "<!--", 0, 4) == 0)
            {
                int close = xml.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = close < 0 ? xml.Length : close + 3;
                continue;
            }
            int gt = TagEnd(xml, lt);
            if (gt < 0) break;
            i = gt + 1;
            char first = xml[lt + 1];
            if (first == '?' || first == '!') continue;

            bool end = first == '/';
            bool self = !end && xml[gt - 1] == '/';
            int ns = lt + (end ? 2 : 1), ne = ns;
            while (ne < gt && !char.IsWhiteSpace(xml[ne]) && xml[ne] != '/') ne++;
            string name = xml.Substring(ns, ne - ns);
            string attrs = end ? "" : xml.Substring(ne, (self ? gt - 1 : gt) - ne);

            // Comments on cells hold paragraphs of their own: not the cell's text.
            if (name == "office:annotation")
            {
                if (end) skip--;
                else if (!self) skip++;
                continue;
            }
            if (skip > 0) continue;

            switch (name)
            {
                case "table:table":
                    if (end) rows = null;
                    else
                    {
                        rows = new List<List<string>>();
                        sheets[Attr(attrs, "table:name") ?? $"Sheet{sheets.Count + 1}"] = rows;
                    }
                    break;

                case "table:table-row":
                    if (!end) row = new List<string>();
                    if (end || self)
                    {
                        if (row != null && rows != null)
                        {
                            while (row.Count > 0 && row[^1].Length == 0) row.RemoveAt(row.Count - 1);
                            if (row.Count > 0) rows.Add(row);
                        }
                        row = null;
                    }
                    break;

                case "table:table-cell":
                case "table:covered-table-cell":
                    if (!end)
                    {
                        cell = new StringBuilder();
                        paragraphs = 0;
                        repeat = int.TryParse(Attr(attrs, "table:number-columns-repeated"), out int cols) && cols > 1 ? cols : 1;
                    }
                    if (end || self)
                    {
                        if (row != null && cell != null)
                        {
                            string v = cell.ToString();
                            for (int k = 0; k < repeat && row.Count < MaxColumns; k++) row.Add(v);
                        }
                        cell = null;
                        inP = false;
                    }
                    break;

                case "text:p":
                case "text:h":
                    if (cell == null) break;
                    if (!end)
                    {
                        if (paragraphs++ > 0) cell.Append('\n');
                        inP = !self;
                    }
                    else inP = false;
                    break;

                case "text:s":
                    if (inP && cell != null && !end)
                        cell.Append(' ', int.TryParse(Attr(attrs, "text:c"), out int spaces) && spaces > 0 ? spaces : 1);
                    break;

                case "text:tab":
                    if (inP && cell != null && !end) cell.Append('\t');
                    break;

                case "text:line-break":
                    if (inP && cell != null && !end) cell.Append('\n');
                    break;
            }
        }
        return sheets;
    }

    /// <summary>The '>' that ends the tag at lt, outside quoted attribute values.</summary>
    static int TagEnd(string xml, int lt)
    {
        char quote = '\0';
        for (int i = lt + 1; i < xml.Length; i++)
        {
            char c = xml[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c == '"' || c == '\'') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }

    static string? Attr(string attrs, string name)
    {
        int at = 0;
        while ((at = attrs.IndexOf(name, at, StringComparison.Ordinal)) >= 0)
        {
            int eq = at + name.Length;
            bool starts = at == 0 || char.IsWhiteSpace(attrs[at - 1]);
            while (eq < attrs.Length && char.IsWhiteSpace(attrs[eq])) eq++;
            if (starts && eq < attrs.Length && attrs[eq] == '=')
            {
                int q = eq + 1;
                while (q < attrs.Length && char.IsWhiteSpace(attrs[q])) q++;
                if (q >= attrs.Length) return null;
                char quote = attrs[q];
                int close = attrs.IndexOf(quote, q + 1);
                if (close < 0) return null;
                var sb = new StringBuilder();
                Decode(attrs, q + 1, close, sb);
                return sb.ToString();
            }
            at += name.Length;
        }
        return null;
    }

    /// <summary>Text from start to end with its entities (&amp;amp; &amp;#x2019; ...) decoded.</summary>
    static void Decode(string s, int start, int end, StringBuilder into)
    {
        for (int i = start; i < end; i++)
        {
            char c = s[i];
            if (c != '&') { into.Append(c); continue; }
            int semi = s.IndexOf(';', i + 1, Math.Min(12, end - i - 1));
            if (semi < 0) { into.Append(c); continue; }
            string e = s.Substring(i + 1, semi - i - 1);
            string? r = e switch
            {
                "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'",
                _ when e.StartsWith("#x", StringComparison.OrdinalIgnoreCase)
                       && int.TryParse(e.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int h) => char.ConvertFromUtf32(h),
                _ when e.StartsWith('#') && int.TryParse(e.AsSpan(1), out int d) => char.ConvertFromUtf32(d),
                _ => null,
            };
            if (r == null) { into.Append(c); continue; }
            into.Append(r);
            i = semi;
        }
    }
}
