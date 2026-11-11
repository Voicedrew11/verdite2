using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Kf2.Text;

/// <summary>
/// Proportional spacing and kerning for one font. Every letter is still drawn
/// from its cell, but placed by its ink: a letter starts where its first inked
/// column is, and the next one follows its last inked column plus Gap pixels.
/// A pair whose shapes leave room (To, LT, Av, r.) is then pulled closer by up
/// to MaxKern pixels, never so far that they touch, row for row or diagonally.
///
/// The numbers live in NAME_spacing.json in the fonts folder, written with
/// the defaults when it is missing and read on every launch:
///
///   gap        pixels between one letter's ink and the next
///   space      the width of a space
///   maxKern    how far automatic kerning may pull a pair together (0 = off)
///   lineGap    (pictures) extra pixels between lines, so tails clear the capitals below
///   advance    { "i": 3 }   a letter's whole advance, overriding its ink width
///   left       { "j": 1 }   where a letter's ink starts, overriding the cell
///   kern       { "Te": -1, "rn": 0 }   a pair's kerning, overriding the automatic one
///   clear      { "'": 1 }   at least this many clear pixels between a letter and
///              the letters either side of it: automatic kerning never pulls it
///              closer, and a bigger number widens the gap (2 = a pixel more than
///              the usual gap of 1). For marks like ' and . that kerning would
///              otherwise tuck right against their neighbours ("who're")
///
/// Measured from the letters themselves, so a font edited or replaced in its
/// character map is spaced by its own shapes.
/// </summary>
sealed class Spacing
{
    public readonly Font Font;
    public int Gap = 1, Space = 4, MaxKern = 1, LineGap;
    readonly Dictionary<char, int> _advance = new(), _left = new(), _clear = new();
    readonly Dictionary<string, int> _kern = new();
    readonly Dictionary<char, Ink> _ink = new();
    readonly Dictionary<(char, char), int> _auto = new();

    sealed class Ink
    {
        public int Left, Width;                 // the inked columns, in the cell
        public int[] First = [], Last = [];     // per row: first and last inked column, -1 none
    }

    public Spacing(Font font, int gap, int space, int maxKern)
    {
        Font = font; Gap = gap; Space = space; MaxKern = maxKern;
    }

    public void Clear() { _ink.Clear(); _auto.Clear(); }

    Ink Of(char c)
    {
        if (_ink.TryGetValue(c, out var k)) return k;
        k = new Ink();
        if (Font.Glyphs.TryGetValue(c, out var g))
        {
            k.First = new int[g.H]; k.Last = new int[g.H];
            int lo = int.MaxValue, hi = -1;
            for (int r = 0; r < g.H; r++)
            {
                k.First[r] = -1; k.Last[r] = -1;
                for (int q = 0; q < g.W; q++)
                {
                    bool on = Font.Menu ? g.Index[r * g.W + q] != 0 : g.Ink[r * g.W + q] > 0.05f;
                    if (!on) continue;
                    if (k.First[r] < 0) k.First[r] = q;
                    k.Last[r] = q;
                }
                if (k.First[r] >= 0) { lo = Math.Min(lo, k.First[r]); hi = Math.Max(hi, k.Last[r]); }
            }
            if (hi >= 0) { k.Left = lo; k.Width = hi - lo + 1; }
        }
        else { k.Left = 0; k.Width = Font.CellW - 1; }
        _ink[c] = k;
        return k;
    }

    /// <summary>Where the letter's ink starts in its cell.</summary>
    public int Left(char c) => _left.TryGetValue(c, out int l) ? l : Of(c).Left;

    /// <summary>The ink's width.</summary>
    public int Width(char c) => Of(c).Width;

    /// <summary>From this letter's ink start to the next one's, before kerning.</summary>
    public int Advance(char c)
    {
        if (c == ' ') return Space;
        return _advance.TryGetValue(c, out int v) ? v : Of(c).Width + Gap;
    }

    /// <summary>The pair's kerning: 0 or less, or more than 0 to keep a "clear"
    /// letter's pixels clear.</summary>
    public int Kern(char a, char b)
    {
        if (a == ' ' || b == ' ') return 0;
        if (_kern.TryGetValue(new string(new[] { a, b }), out int k)) return k;
        bool ca = _clear.TryGetValue(a, out int clearA), cb = _clear.TryGetValue(b, out int clearB);
        if (ca || cb)
        {
            int gap = Advance(a) - Width(a);                 // the pixels between their ink, unkerned
            return Math.Max(0, Math.Max(clearA, clearB) - gap);
        }
        if (MaxKern <= 0) return 0;
        if (_auto.TryGetValue((a, b), out k)) return k;
        var A = Of(a); var B = Of(b);
        int room = int.MaxValue;
        int rows = Math.Min(A.First.Length, B.First.Length);
        for (int r = 0; r < rows; r++)
        {
            if (A.Last[r] < 0) continue;
            int right = A.Left + A.Width - 1 - A.Last[r];          // A's empty columns right of its ink, this row
            for (int dr = -1; dr <= 1; dr++)                        // and the rows either side, so nothing touches diagonally
            {
                int rr = r + dr;
                if (rr < 0 || rr >= rows || B.First[rr] < 0) continue;
                room = Math.Min(room, right + B.First[rr] - B.Left);
            }
        }
        k = room == int.MaxValue ? -MaxKern : -Math.Min(room, MaxKern);
        _auto[(a, b)] = k;
        return k;
    }

    /// <summary>Ink-start positions of each character of a line from 0, and the
    /// line's inked width.</summary>
    public int[] Place(string s, out int width)
    {
        var x = new int[s.Length];
        int pen = 0, end = 0;
        for (int i = 0; i < s.Length; i++)
        {
            x[i] = pen;
            if (s[i] != ' ') end = pen + Width(s[i]);
            pen += Advance(s[i]) + (i + 1 < s.Length ? Kern(s[i], s[i + 1]) : 0);
        }
        width = end;
        return x;
    }

    // ---- the settings file ----

    static string PathOf(Font f) => System.IO.Path.Combine(Fonts.Folder, f.Name + "_spacing.json");

    public void Load()
    {
        Clear();
        string p = PathOf(Font);
        if (!File.Exists(p)) { Save(); return; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p), new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.TryGetProperty("gap", out var e)) Gap = e.GetInt32();
            if (root.TryGetProperty("space", out e)) Space = e.GetInt32();
            if (root.TryGetProperty("maxKern", out e)) MaxKern = e.GetInt32();
            if (root.TryGetProperty("lineGap", out e)) LineGap = e.GetInt32();
            _advance.Clear(); _left.Clear(); _kern.Clear(); _clear.Clear();
            if (root.TryGetProperty("advance", out e)) foreach (var kv in e.EnumerateObject()) if (kv.Name.Length == 1) _advance[kv.Name[0]] = kv.Value.GetInt32();
            if (root.TryGetProperty("left", out e)) foreach (var kv in e.EnumerateObject()) if (kv.Name.Length == 1) _left[kv.Name[0]] = kv.Value.GetInt32();
            if (root.TryGetProperty("kern", out e)) foreach (var kv in e.EnumerateObject()) if (kv.Name.Length == 2) _kern[kv.Name] = kv.Value.GetInt32();
            if (root.TryGetProperty("clear", out e)) foreach (var kv in e.EnumerateObject()) if (kv.Name.Length == 1) _clear[kv.Name[0]] = kv.Value.GetInt32();
        }
        catch (Exception ex) { Console.WriteLine($"[KF2] text: {Font.Name} spacing: {ex.Message}; using the defaults"); }
        Clear();
    }

    void Save()
    {
        Directory.CreateDirectory(Fonts.Folder);
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"about\": \"Proportional spacing for the " + Font.Name + " font. gap: pixels between letters. space: a space's width. ");
        sb.Append("maxKern: how far automatic kerning may pull a pair together (0 turns it off). ");
        if (!Font.Menu) sb.Append("lineGap: extra pixels between lines. ");
        sb.Append("advance / left / kern: per-letter and per-pair overrides, for example \\\"advance\\\": { \\\"i\\\": 3 }, \\\"kern\\\": { \\\"Te\\\": -1 }. ");
        sb.Append("clear: the fewest pixels kept clear between a letter and the letters either side, never kerned closer, for example \\\"clear\\\": { \\\"'\\\": 1, \\\".\\\": 1 }.\",\n");
        sb.Append($"  \"gap\": {Gap},\n  \"space\": {Space},\n  \"maxKern\": {MaxKern},\n");
        if (!Font.Menu) sb.Append($"  \"lineGap\": {LineGap},\n");
        sb.Append("  \"advance\": {},\n  \"left\": {},\n  \"kern\": {},\n  \"clear\": {}\n}\n");
        File.WriteAllText(PathOf(Font), sb.ToString());
    }
}
