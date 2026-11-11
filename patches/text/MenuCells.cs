using System;
using System.Collections.Generic;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Text;

/// <summary>
/// The menu font: a 4-bit page at VRAM (832, 256), 8x15 cells, index g drawn
/// from u = (g &amp; 15) * 8, v = (g &gt;&gt; 4) * 15 (func_80021E10), 0x7F a space and 0xFF
/// the end of a string. The disc fills rows 0-3; this adds
///   0x40 (   0x41-0x5A a-z   0x5B )   0x5C :   0x5D ;   0x5E &amp;   0x5F "
///   0x60 +   0x61 %   0x62 left arrow   0x63 right arrow
/// and writes every letter of the menu font's character map (Fonts.Menu) into
/// rows 0-6 -- the disc's own capitals by default, or whatever the map holds.
///
/// The numbers the game draws itself (func_80021FCC: levels, HP, gold, counts)
/// come from a second set of glyphs, two 7-wide columns at u 240: digits 0-9 at
/// v = d * 15 in the first, and in the second the letters and signs it puts
/// beside numbers (C E G L M P V X, a small x, and the / between HP and its
/// maximum), sign s at v = (s - 11) * 15. Both are written from the map too.
///
/// Two ways in, so the cells are there whenever the font is: a pre on LoadImage
/// (func_80060624) writes them into any upload that covers them, and once, from
/// the main loop, a direct upload of each area from scratch memory far below
/// the stack pointer. Until then, text is encoded in capitals only (Ready).
///
/// With FONT on CLASSIC (<see cref="Lettering.ModernApplied"/>) both areas are the disc's own
/// pixels, put up once from the main loop to undo the port's letters, and Ready is false.
/// </summary>
static class MenuCells
{
    public const uint LoadImage = 0x80060624;
    const int PageX = 832, PageY = 256, Rows = 7, CellW = 8, CellH = 15;
    const uint FakeRa = 0x8001FFE0;
    const int StripU = 240, StripW = 7, StripH = 14;   // func_80021FCC's glyphs, two columns 7 wide
    /// <summary>The second column, from the top: signs 0x0B-0x14.</summary>
    const string StripSigns = "CEGLMPVXx/";

    /// <summary>A block of the page this writes: from u (a multiple of 4), w by h pixels.</summary>
    sealed class Area
    {
        public readonly int U, W, H;
        public Area(int u, int w, int h) { U = u; W = w; H = h; }
        public byte[,]? Px;
        public bool Uploaded;
        public int X => PageX + U / 4;     // VRAM halfwords
        public int HW => W / 4;
    }
    static readonly Area Letters = new(0, 128, Rows * CellH);
    static readonly Area Digits = new(StripU, 16, 10 * CellH);
    static readonly Area[] Areas = [Letters, Digits];

    /// <summary>The disc's own cells.</summary>
    public static readonly Dictionary<char, int> DiscCells = MakeDisc();
    static readonly Dictionary<char, int> _cells = MakeAll();
    static readonly Dictionary<int, char> _chars = Reverse();

    /// <summary>The port's letters, lower case included, are in VRAM.</summary>
    public static bool Ready => _ready && Lettering.Drawing;

    static bool _ready;

    static Dictionary<char, int> MakeDisc()
    {
        var d = new Dictionary<char, int>();
        for (char c = 'A'; c <= 'Z'; c++) d[c] = c - 'A';
        for (char c = '0'; c <= '9'; c++) d[c] = 0x20 + (c - '0');
        string p = ".,'-=/*#!■?";
        for (int i = 0; i < p.Length; i++) d[p[i]] = 0x30 + i;
        return d;
    }

    static Dictionary<char, int> MakeAll()
    {
        var d = new Dictionary<char, int>(MakeDisc());
        for (char c = 'a'; c <= 'z'; c++) d[c] = 0x41 + (c - 'a');
        d['('] = 0x40; d[')'] = 0x5B; d[':'] = 0x5C; d[';'] = 0x5D; d['&'] = 0x5E; d['"'] = 0x5F;
        d['+'] = 0x60; d['%'] = 0x61; d['←'] = 0x62; d['→'] = 0x63;
        return d;
    }

    static Dictionary<int, char> Reverse()
    {
        var r = new Dictionary<int, char> { [0x7F] = ' ' };
        foreach (var (c, i) in MakeAll()) r[i] = c;
        r[0x32] = '\'';
        return r;
    }

    /// <summary>The cell for a character, or -1. Until the added cells are in
    /// VRAM, lower case falls back to capitals and the added punctuation to none.</summary>
    public static int Index(char c)
    {
        if (c == ' ') return 0x7F;
        if (c == '’') c = '\'';
        if (!_cells.TryGetValue(c, out int i)) return -1;
        if (i < 0x40 || Ready) return i;
        return c >= 'a' && c <= 'z' ? c - 'a' : -1;
    }

    /// <summary>The character a cell shows, or '\0'.</summary>
    public static char Char(int index) => _chars.TryGetValue(index, out char c) ? c : '\0';

    /// <summary>Text as cells, at most <paramref name="max"/> of them, then 0xFF.
    /// A character the font lacks is left out.</summary>
    public static byte[] Encode(string text, int max)
    {
        var b = new List<byte>(text.Length + 1);
        foreach (char c in text)
        {
            if (b.Count >= max) break;
            int i = Index(c);
            if (i >= 0) b.Add((byte)i);
        }
        b.Add(0xFF);
        return b.ToArray();
    }

    public static void Invalidate()
    {
        foreach (var a in Areas) { a.Px = null; a.Uploaded = false; }   // the main loop puts the new letters up
        _ready = false;
    }

    /// <summary>An area's pixels: rows 0-6 from the character map; the digit
    /// strip from the disc's page with the map's 0-9 over its first column. On
    /// CLASSIC, both the disc's page as it is.</summary>
    static byte[,]? Pixels(Area a)
    {
        if (a.Px != null) return a.Px;
        if (!Lettering.ModernApplied) return a.Px = DiscPixels(a);
        if (!Fonts.Ready()) return null;
        var px = new byte[a.H, a.W];
        if (a == Letters)
        {
            foreach (var (c, index) in _cells)
            {
                if (!Fonts.Menu.Glyphs.TryGetValue(c, out var g)) continue;
                int row = index >> 4, col = index & 15;
                if (row >= Rows) continue;
                for (int r = 0; r < CellH && r < g.H; r++)
                    for (int q = 0; q < CellW && q < g.W; q++)
                        px[row * CellH + r, col * CellW + q] = g.Index[r * g.W + q];
            }
        }
        else
        {
            if (!Disc.MenuPage(out var page, out _)) return null;
            for (int v = 0; v < a.H; v++)
                for (int u = 0; u < a.W; u++) px[v, u] = page[v * 256 + a.U + u];
            void Put(char ch, int slot, int column)
            {
                if (!Fonts.Menu.Glyphs.TryGetValue(ch, out var g)) return;
                for (int r = 0; r < StripH && r < g.H; r++)
                    for (int q = 0; q < StripW && q < g.W; q++)
                        px[slot * CellH + r, column * StripW + q] = g.Index[r * g.W + q];
            }
            for (int d = 0; d < 10; d++) Put((char)('0' + d), d, 0);
            for (int k = 0; k < StripSigns.Length; k++) Put(StripSigns[k], k, 1);
        }
        a.Px = px;
        return px;
    }

    static byte[,]? DiscPixels(Area a)
    {
        if (!Disc.MenuPage(out var page, out _)) return null;
        var px = new byte[a.H, a.W];
        for (int v = 0; v < a.H; v++)
            for (int u = 0; u < a.W; u++) px[v, u] = page[v * 256 + a.U + u];
        return px;
    }

    /// <summary>Pre on LoadImage(rect, data): the cells into any upload over them.</summary>
    public static void BeforeLoadImage(CpuContext c, IMemory m)
    {
        uint rect = c.A0, data = c.A1;
        int rx = (short)m.ReadU16(rect), ry = (short)m.ReadU16(rect + 2);
        int rw = (short)m.ReadU16(rect + 4), rh = (short)m.ReadU16(rect + 6);
        if (rw <= 0 || rh <= 0) return;
        foreach (var a in Areas)
        {
            if (rx >= a.X + a.HW || rx + rw <= a.X || ry >= PageY + a.H || ry + rh <= PageY) continue;
            var px = Pixels(a);
            if (px == null) continue;
            for (int v = 0; v < a.H; v++)
            {
                int vy = PageY + v;
                if (vy < ry || vy >= ry + rh) continue;
                for (int u = 0; u < a.W; u++)
                {
                    int vx = a.X + u / 4;
                    if (vx < rx || vx >= rx + rw) continue;
                    uint at = data + (uint)(((vy - ry) * rw + (vx - rx)) * 2 + (u & 3) / 2);
                    byte b = m.ReadU8(at);
                    int value = px[v, u];
                    b = (u & 1) == 0 ? (byte)((b & 0xF0) | value) : (byte)((b & 0x0F) | (value << 4));
                    m.WriteU8(at, b);
                }
            }
            if (a == Letters && rx <= a.X && rx + rw >= a.X + a.HW && ry <= PageY && ry + rh >= PageY + a.H) _ready = Lettering.ModernApplied;
        }
    }

    /// <summary>From the main loop: put each area up directly, once (and after a rebuild).</summary>
    public static void UploadOnce(CpuContext c, IMemory m)
    {
        foreach (var a in Areas)
        {
            if (a.Uploaded) continue;
            var px = Pixels(a);
            if (px == null) return;
            a.Uploaded = true;
            uint scratch = (c.SP - 0x3000) & ~0xFu, data = scratch + 8;
            m.WriteU16(scratch, (ushort)a.X);
            m.WriteU16(scratch + 2, PageY);
            m.WriteU16(scratch + 4, (ushort)a.HW);
            m.WriteU16(scratch + 6, (ushort)a.H);
            for (int v = 0; v < a.H; v++)
                for (int u = 0; u < a.W; u += 2)
                    m.WriteU8(data + (uint)((v * a.W + u) / 2), (byte)(px[v, u] | (px[v, u + 1] << 4)));
            var snap = c.Snapshot();
            try
            {
                c.A0 = scratch; c.A1 = data; c.A2 = 0; c.A3 = 0;
                c.RA = FakeRa;
                KingsField2.func_80060624(c, m);
                string font = Lettering.ModernApplied ? "" : " (classic: the disc's own)";
                if (a == Letters) { _ready = Lettering.ModernApplied; Console.WriteLine($"[KF2] text: menu font: letters uploaded{font}"); }
                else Console.WriteLine($"[KF2] text: menu font: number digits and signs uploaded{font}");
            }
            catch (Exception e) { Console.WriteLine($"[KF2] text: menu font upload: {e.Message}"); }
            finally { c.Restore(snap); }
        }
    }
}
