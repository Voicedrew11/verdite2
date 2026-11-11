using System;
using System.Collections.Generic;
using RecompOne.Runtime.Memory;

namespace Kf2.Text;

/// <summary>
/// Signs, dialogue, descriptions and place cards: 4-bit 256x256 TIMs from
/// COM\TALK.T and COM\ITEM.T, uploaded by func_80035684. Mods hand in text two
/// ways:
///  - Set(owner, archive, entry, style, text): that disc picture, wherever the
///    game shows it from, is redrawn with the text. It is recognised by its
///    fingerprint (palette + pixels), so a picture another mod already changed is
///    left alone.
///  - Next(style, text): the very next picture uploaded is redrawn with the text,
///    whatever it was (a mod showing a card the disc does not have points the game
///    at any card of the same kind and calls this first).
/// The picture keeps its TIM header and palette; the text is laid out the way the
/// US pictures are. Styles: "dialogue" (dialogue font, left, 31 a line) for dialogue
/// and descriptions, "card" (sign font, centred, at most 27 a line, its lines
/// balanced to even lengths) for signs and cards,
/// "auto" (by entry: TALK.T and ITEM.T 240-478 dialogue, the rest card).
///
/// Under what mods set is the base layer (SetBase): the game's own US text, put
/// there by GameText, so every picture is drawn in these fonts even with no other
/// mod. A mod's text for a picture always comes first.
/// </summary>
static class Pictures
{
    public const int Size = 256, PixelBytes = Size * Size / 2;

    sealed record Entry(string Owner, int Archive, int Number, string Style, string Text);

    static readonly Dictionary<(int, int), Entry> _set = new();
    static readonly Dictionary<(int, int), Entry> _base = new();
    static readonly object _sync = new();   // the settings change these while the game draws
    static readonly Dictionary<ulong, (int, int)> _byPrint = new();
    static bool _indexed;
    static (string Style, string Text)? _next;
    public static int Shown { get; private set; }
    static readonly HashSet<(string, char)> _missing = new();

    public static void Set(string owner, int archive, int entry, string style, string text)
    {
        lock (_sync)
        {
            _set[(archive, entry)] = new Entry(owner, archive, entry, style, text);
            if (_indexed) Index(archive, entry);
        }
    }

    public static void Clear(string owner)
    {
        lock (_sync)
        {
            var gone = new List<(int, int)>();
            foreach (var (k, e) in _set) if (e.Owner == owner) gone.Add(k);
            foreach (var k in gone) _set.Remove(k);
        }
    }

    public static int Count => _set.Count;

    /// <summary>The text a picture has when no mod gives it any.</summary>
    public static void SetBase(int archive, int entry, string style, string text)
    {
        lock (_sync)
        {
            _base[(archive, entry)] = new Entry("", archive, entry, style, text);
            if (_indexed) Index(archive, entry);
        }
    }

    public static void ClearBase() { lock (_sync) _base.Clear(); }

    public static int BaseCount => _base.Count;

    public static void Next(string style, string text) => _next = (style, text);

    static void Index(int archive, int entry)
    {
        var arc = Disc.Of(archive);
        if (arc == null || arc.Length(entry) < 64) return;
        var d = arc.Data;
        int s = arc.Offset(entry);
        if (BitConverter.ToUInt32(d, s) != 0x10 || BitConverter.ToUInt32(d, s + 4) != 8) return;
        var clut = new ushort[16];
        for (int i = 0; i < 16; i++) clut[i] = BitConverter.ToUInt16(d, s + 20 + 2 * i);
        int p = s + 8 + (int)BitConverter.ToUInt32(d, s + 8);
        if (BitConverter.ToUInt16(d, p + 8) * 4 != Size || BitConverter.ToUInt16(d, p + 10) != Size) return;
        int px = p + 12;
        _byPrint[Print(clut, d, px)] = (archive, entry);
    }

    /// <summary>A picture's fingerprint: its palette and its packed pixels from
    /// <paramref name="at"/> (every disc picture is indexed this way once).</summary>
    static ulong Print(ushort[] clut, byte[] data, int at)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (var c in clut)
        {
            h = (h ^ (byte)c) * 0x100000001B3UL;
            h = (h ^ (byte)(c >> 8)) * 0x100000001B3UL;
        }
        for (int k = 0; k < PixelBytes; k++) h = (h ^ data[at + k]) * 0x100000001B3UL;
        return h;
    }

    /// <summary>Pre on func_80035684, with the TIM at <paramref name="buf"/>.</summary>
    public static void BeforeUpload(IMemory m, uint buf)
    {
        if (_next == null && _set.Count == 0 && _base.Count == 0) return;
        if (m.ReadU32(buf) != 0x10 || m.ReadU32(buf + 4) != 8) return;
        uint clutLen = m.ReadU32(buf + 8);
        if (clutLen != 12 + 32) return;
        uint img = buf + 8 + clutLen;
        if (m.ReadU16(img + 8) * 4 != Size || m.ReadU16(img + 10) != Size) return;
        if (!Fonts.Ready()) return;
        lock (_sync)
        {
            if (!_indexed)
            {
                _indexed = true;
                foreach (var (archive, entry) in _set.Keys) Index(archive, entry);
                foreach (var (archive, entry) in _base.Keys) Index(archive, entry);
            }
        }
        uint px = img + 12;

        var clut = new ushort[16];
        for (uint i = 0; i < 16; i++) clut[i] = m.ReadU16(buf + 20 + 2 * i);
        var packed = new byte[PixelBytes];
        for (int k = 0; k < PixelBytes; k++) packed[k] = m.ReadU8(px + (uint)k);

        string? style = null, text = null;
        int archiveOf = 6, entryOf = 510;
        if (_next is { } next)
        {
            _next = null;
            (style, text) = next;
        }
        else
        {
            ulong print = Print(clut, packed, 0);
            lock (_sync)
                if (_byPrint.TryGetValue(print, out var key)
                    && (_set.TryGetValue(key, out var e) || _base.TryGetValue(key, out e)))
                    (style, text, archiveOf, entryOf) = (e.Style, e.Text, e.Archive, e.Number);
        }
        if (text == null) return;

        var pixels = new byte[Size * Size];
        for (int k = 0; k < PixelBytes; k++)
        {
            pixels[2 * k] = (byte)(packed[k] & 15);
            pixels[2 * k + 1] = (byte)(packed[k] >> 4);
        }
        if (style == "auto") style = archiveOf == 3 || (entryOf >= 240 && entryOf <= 478) ? "dialogue" : "card";
        pixels = Draw(style!, text, clut, pixels);
        Shown++;
        if (Lettering.Probe) Console.WriteLine($"[KF2] text: picture ({archiveOf}, {entryOf}) redrawn as {style}, {Layout(style!, text).Count} line(s)");
        for (int k = 0; k < PixelBytes; k++)
            m.WriteU8(px + (uint)k, (byte)(pixels[2 * k] | (pixels[2 * k + 1] << 4)));
    }

    // ---- layout ----

    static bool Wide(Font f, char c) => !f.Menu && (c == '→' || c == '←');

    // The room a line has: 31 cells of 8 from x 4 for dialogue, 27 of 9 centred for cards.
    const int DialogueWidth = 31 * 8, CardWidth = 27 * 9;

    /// <summary>A line's inked width in pixels: proportional (Spacing.cs), or in
    /// whole cells when proportional spacing is off.</summary>
    public static int Measure(Font f, string s)
    {
        if (Fonts.PropPictures) { f.Sp.Place(s, out int w); return w; }
        int n = 0;
        foreach (var c in s) n += Wide(f, c) ? 2 * f.CellW : f.CellW;
        return n;
    }

    /// <summary>Greedy word wrap to a width in pixels; a line break in the text
    /// always starts a new line.</summary>
    public static List<string> Wrap(string text, Font f, int px)
    {
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            var line = "";
            foreach (var raw in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw;
                while (word.Length > 1 && Measure(f, word) > px)
                {
                    int k = word.Length - 1;
                    while (k > 1 && Measure(f, word.Substring(0, k)) > px) k--;
                    if (line.Length > 0) { lines.Add(line); line = ""; }
                    lines.Add(word.Substring(0, k));
                    word = word.Substring(k);
                }
                if (line.Length == 0) line = word;
                else if (Measure(f, line + " " + word) <= px) line += " " + word;
                else { lines.Add(line); line = word; }
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>Each paragraph wrapped to the narrowest width that still takes as
    /// few lines as the full width, so a centred sign's lines come out even
    /// instead of one long line and a short one.</summary>
    public static List<string> Balanced(string text, Font f, int px)
    {
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            int n = Wrap(para, f, px).Count, lo = 1, hi = px;
            while (lo < hi)                      // the narrowest width that keeps n lines
            {
                int mid = (lo + hi) / 2;
                if (Wrap(para, f, mid).Count == n) hi = mid; else lo = mid + 1;
            }
            lines.AddRange(Wrap(para, f, hi));
        }
        return lines;
    }

    static List<string> Layout(string style, string text) =>
        style == "card" ? Balanced(text, Fonts.Bold, CardWidth) : Wrap(text, Fonts.Thin, DialogueWidth);

    public static int Lines(string style, string text) => Layout(style, text).Count;

    /// <summary>Text wrapped to a width and drawn as ink coverage (0 to 1, a byte a
    /// pixel scaled to 255), in a picture style's font, spacing and line pitch
    /// ("dialogue" the dialogue font, "card" the sign font), the first letter's ink
    /// at the left: for a mod that draws the lettering itself, as the Japanese Disc
    /// mod letters the movies.</summary>
    public static (byte[] Ink, int W, int H) Coverage(string style, string text, int width)
    {
        bool card = style == "card";
        var font = card ? Fonts.Bold : Fonts.Thin;
        var lines = Wrap(text, font, Math.Max(16, width));
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        int cw = font.CellW, pitch = (card ? 17 : 14) + Math.Max(0, Fonts.Tallest(font) - font.CellH) + font.Sp.LineGap;
        int w = 1;
        foreach (var l in lines) w = Math.Max(w, Measure(font, l) + 2);
        int h = Math.Max(1, (lines.Count - 1) * pitch + Math.Max(font.CellH, Fonts.Tallest(font)) + 2);
        var ink = new byte[w * h];
        bool prop = Fonts.PropPictures;
        for (int li = 0; li < lines.Count; li++)
        {
            string line = lines[li];
            int[] at = prop ? font.Sp.Place(line, out _) : new int[line.Length];
            if (!prop) for (int i = 0, pen = 0; i < line.Length; i++) { at[i] = pen; pen += Wide(font, line[i]) ? 2 * cw : cw; }
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == ' ' || !font.Glyphs.TryGetValue(c, out var g)) continue;
                int x = at[i] - (prop ? font.Sp.Left(c) : 0), y = li * pitch;
                for (int r = 0; r < g.H; r++)
                    for (int q = 0; q < g.W; q++)
                    {
                        int yy = y + r, xx = x + q;
                        if (yy < 0 || xx < 0 || yy >= h || xx >= w) continue;
                        byte v = (byte)Math.Clamp((int)(g.Ink[r * g.W + q] * 255 + 0.5f), 0, 255);
                        if (v > ink[yy * w + xx]) ink[yy * w + xx] = v;
                    }
            }
        }
        return (ink, w, h);
    }

    /// <summary>The new picture, in the old one's palette.</summary>
    static byte[] Draw(string style, string text, ushort[] clut, byte[] old)
    {
        bool card = style == "card";
        var font = card ? Fonts.Bold : Fonts.Thin;
        int cw = font.CellW, pitch = (card ? 17 : 14) + Math.Max(0, Fonts.Tallest(font) - font.CellH) + font.Sp.LineGap;   // letters made taller push the lines apart
        var lines = Layout(style, text);

        int bg = Disc.Background(old);
        var count = new int[16];
        foreach (var p in old) count[p]++;
        var ink = new List<int>();
        int main = -1;
        for (int i = 0; i < 16; i++)
            if (i != bg && count[i] > 0) { ink.Add(i); if (main < 0 || count[i] > count[main]) main = i; }
        if (ink.Count == 0)
        {
            for (int i = 0; i < 16; i++) if (i != bg && (main < 0 || Disc.Lum(clut[i]) > Disc.Lum(clut[main]))) main = i;
            ink.Add(main);
        }
        var cand = new List<int>();
        foreach (var i in ink) if (count[i] >= 0.02 * count[main]) cand.Add(i);
        if (cand.Count == 0) cand.Add(main);
        double mx = 0;
        foreach (var i in ink) mx = Math.Max(mx, Disc.Lum(clut[i]));
        if (mx <= 0) mx = 1;
        var shade = new byte[256];
        for (int f = 0; f < 256; f++)
        {
            int best = cand[0];
            foreach (var i in cand)
                if (Math.Abs(Disc.Lum(clut[i]) / mx - f / 255.0) < Math.Abs(Disc.Lum(clut[best]) / mx - f / 255.0)) best = i;
            shade[f] = (byte)best;
        }

        var outPx = new byte[Size * Size];
        Array.Fill(outPx, (byte)bg);
        int top = (Size - pitch * lines.Count) / 2;
        bool prop = Fonts.PropPictures;
        for (int li = 0; li < lines.Count; li++)
        {
            string line = lines[li];
            int width = Measure(font, line);
            int x0 = card ? (Size - width) / 2 : prop ? 5 : 4;   // proportional letters start at their ink
            int y = top + li * pitch;
            int[] at = prop ? font.Sp.Place(line, out _) : new int[line.Length];
            if (!prop) for (int i = 0, pen = 0; i < line.Length; i++) { at[i] = pen; pen += Wide(font, line[i]) ? 2 * cw : cw; }
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == ' ') continue;
                if (!font.Glyphs.TryGetValue(c, out var g))
                {
                    if (_missing.Add((font.Name, c))) Console.WriteLine($"[KF2] text: no letter '{c}' in the {font.Name} font");
                    continue;
                }
                int x = x0 + at[i] - (prop ? font.Sp.Left(c) : 0);
                for (int r = 0; r < g.H; r++)
                    for (int q = 0; q < g.W; q++)
                    {
                        float f = g.Ink[r * g.W + q];
                        int yy = y + r, xx = x + q;
                        if (f > 0.05f && yy >= 0 && xx >= 0 && yy < Size && xx < Size)
                            outPx[yy * Size + xx] = shade[Math.Min(255, (int)(f * 255 + 0.5f))];
                    }
            }
        }
        return outPx;
    }
}
