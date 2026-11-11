using System;
using System.Collections.Generic;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Text;

/// <summary>
/// The short messages at the bottom of the screen in play: LEVEL UP, LOCKED,
/// A GRAVE, "36 GOLD" and the rest. They are not text but one 4-bit page,
/// VRAM (704, 256), palette (576, 508), uploaded from FDAT.T entry 47 with the
/// menu font: two columns of 128 pixels, eighteen rows of 14, one message a cell,
/// message i at column i / 18, row i % 18.
///
/// func_80033F08(id, value) queues a message; func_80033FBC shows it by pointing
/// the first sprite record at 0x800676F4 (18 bytes: on, u, v, w, h, then s16 x,
/// y, w, h, clut, tpage) at the message's cell, 127 x 14 at (96, 203). GOLD
/// (message 21) is the exception: the second record, at x 160, shows it beside
/// four digit sprites from the menu font's number strip (u 240) at x 116-143, so
/// it is the one message whose letters start at the cell's left edge; the rest
/// are centred. func_80033E78 draws every record that is on through
/// func_80033D24(rec, blend, rgb), subtractive then additive, at a brightness
/// that fades in and out.
///
/// This redraws the page in the menu font's letters, with its spacing, whenever
/// the game uploads it (a pre on LoadImage that sees the whole page, with the
/// disc's own pixels) and once from the main loop. A mod sets a message's words
/// in the shared string table as "hud:" + its number; without one it is the US
/// text (UsText.Hud, in sentence case), in the redrawn letters.
/// </summary>
static class HudMessages
{
    const int PageX = 704, PageY = 256, PageHW = 64, PageW = 256, PageH = 256;
    const int ColW = 128, RowH = 14, Rows = 18, CellW = 127;
    const int Band = 16;                  // rows a direct upload sends at once
    public const int Count = 36;
    public const string Key = "hud:";

    /// <summary>The disc's messages, in page order.</summary>
    public static readonly string[] Us =
    [
        "LEVEL UP", "MAGIC LEARNED", "LOCKED", "SEALED", "WRONG KEY", "NOTHING THERE",
        "EMPTY", "DEAD", "A BROKEN SWORD", "BONES", "A WARRIOR'S GRAVE", "TEO'S BODY",
        "A DEAD CRYSTAL-MINER", "A SOLDIER OF VERDITE", "A TREE", "DRAGON GRASS", "A GUIDEPOST", "THE PILLAR OF WIND",
        "CAN'T CARRY ANY MORE", "NOT ACCEPTED", "NOTHING HAPPENS", "GOLD", "DREW SOME WATER", "A GRAVE",
        "A BROKEN SHOVEL", "THE ELF'S GRAVE", "THE MINER'S GRAVE", "THE SOLDIER'S GRAVE", "HARRIS'S GRAVE", "THE SAMURAI'S GRAVE",
        "THE MAGICIAN'S LAMP", "STRENGTH GAINED", "MAGIC GAINED", "A CULTURE MEDIUM", "LOST THE EFFECT", "A LAMP",
    ];

    /// <summary>Messages shown after a number, so they start at the cell's left edge.</summary>
    static readonly HashSet<int> LeftAligned = new() { 21 };

    static byte[]? _px;                   // the page, one palette index a pixel
    static byte[]? _disc;                 // the disc's page, packed as uploaded, to recognise it
    static ushort[]? _clut;
    static bool _uploaded;
    static readonly HashSet<int> _tooWide = new();
    public static int Shown { get; private set; }

    public static Func<string, string?> Lookup = _ => null;

    /// <summary>Build the page again (words, letters or spacing changed).</summary>
    public static void Invalidate() { _px = null; _uploaded = false; }

    /// <summary>A message's words: a mod's, else the US text, in sentence case unless
    /// that is switched off (GameText).</summary>
    public static string Text(int i) => Lookup(Key + i) ?? (GameText.NormalCase ? UsText.Hud[i] : Us[i]);

    static bool Load()
    {
        if (_clut != null) return true;
        if (!Fonts.Ready()) return false;
        if (!Disc.Block(PageX, PageY, out var px, out int w, out int h, out var clut) || w != PageW || h != PageH) return false;
        var packed = new byte[PageW * PageH / 2];
        for (int k = 0; k < packed.Length; k++) packed[k] = (byte)(px[2 * k] | (px[2 * k + 1] << 4));
        _disc = packed;
        _clut = clut;
        return true;
    }

    /// <summary>Each menu-font colour as the nearest one in the messages' palette.</summary>
    static byte[] ColourMap()
    {
        var map = new byte[16];
        for (int i = 1; i < 16; i++)
        {
            var (r, g, b) = Disc.Rgb(Fonts.MenuClut[i]);
            int best = 1, dist = int.MaxValue;
            for (int j = 1; j < 16; j++)
            {
                var (R, G, B) = Disc.Rgb(_clut![j]);
                int d = (R - r) * (R - r) + (G - g) * (G - g) + (B - b) * (B - b);
                if (d < dist) { dist = d; best = j; }
            }
            map[i] = (byte)best;
        }
        return map;
    }

    /// <summary>Each letter's cell offset, and the ink's extent [Lo, Hi). Spaced as
    /// the menus are; a message that would not fit its 127 pixels is tightened just
    /// enough: its spaces first (to 3 pixels), then one pixel out of as few of the
    /// gaps between letters as it takes, spread along the line, then the spaces
    /// again, down to a pixel.</summary>
    static (int[] X, int Lo, int Hi) Lay(string s)
    {
        var sp = Fonts.Menu.Sp;
        var x = new int[s.Length];
        if (Fonts.PropMenus)
        {
            bool Ink(char ch) => ch != ' ' && Fonts.Menu.Glyphs.ContainsKey(ch);
            var adv = new int[s.Length];
            int width = 0, pen = 0;
            for (int i = 0; i < s.Length; i++)
            {
                adv[i] = sp.Advance(s[i]) + (i + 1 < s.Length ? sp.Kern(s[i], s[i + 1]) : 0);
                if (Ink(s[i])) width = pen + sp.Width(s[i]);
                pen += adv[i];
            }
            int excess = width - CellW;
            for (int i = 0; i < s.Length && excess > 0; i++)
                if (s[i] == ' ') { int cut = Math.Min(excess, Math.Max(0, adv[i] - 3)); adv[i] -= cut; excess -= cut; }
            if (excess > 0)
            {
                var pairs = new List<int>();          // letters followed by a letter, with a pixel between their ink
                for (int i = 0; i + 1 < s.Length; i++)
                    if (Ink(s[i]) && Ink(s[i + 1]) && adv[i] - sp.Width(s[i]) >= 1) pairs.Add(i);
                int n = Math.Min(excess, pairs.Count);
                for (int k = 0; k < n; k++) adv[pairs[(int)((k + 0.5) * pairs.Count / n)]]--;
                excess -= n;
            }
            for (int i = 0; i < s.Length && excess > 0; i++)       // last: the spaces to a pixel
                if (s[i] == ' ' && adv[i] > 1) { int cut = Math.Min(excess, adv[i] - 1); adv[i] -= cut; excess -= cut; }
            pen = 0;
            for (int i = 0; i < s.Length; i++)
            {
                x[i] = s[i] == ' ' ? pen : pen - sp.Left(s[i]);
                pen += adv[i];
            }
        }
        else
            for (int i = 0; i < s.Length; i++) x[i] = i * 7;
        int lo = int.MaxValue, hi = int.MinValue;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == ' ' || !Fonts.Menu.Glyphs.ContainsKey(s[i])) continue;
            lo = Math.Min(lo, x[i] + sp.Left(s[i]));
            hi = Math.Max(hi, x[i] + sp.Left(s[i]) + sp.Width(s[i]));
        }
        return lo > hi ? (x, 0, 0) : (x, lo, hi);
    }

    /// <summary>The page to upload: the redrawn one, or on FONT CLASSIC the disc's own.</summary>
    static byte[]? Pixels()
    {
        if (_px != null) return _px;
        if (!Load()) return null;
        if (!Lettering.Modern)
        {
            _px = new byte[PageW * PageH];
            for (int k = 0; k < _disc!.Length; k++) { _px[2 * k] = (byte)(_disc[k] & 15); _px[2 * k + 1] = (byte)(_disc[k] >> 4); }
            return _px;
        }
        var map = ColourMap();
        var px = new byte[PageW * PageH];
        for (int id = 0; id < Count; id++)
        {
            string s = Text(id).Replace('’', '\'');
            int col = id / Rows, row = id % Rows;
            int left = col * ColW, top = row * RowH;
            var (x, lo, hi) = Lay(s);
            if (hi - lo > CellW && _tooWide.Add(id))
                Console.WriteLine($"[KF2] text: on-screen message {id} \"{s}\" is {hi - lo} pixels wide; {CellW} fit, the rest is cut off");
            int ox = LeftAligned.Contains(id) ? left - lo : left + (CellW - (hi - lo)) / 2 - lo;
            if (hi - lo > CellW) ox = left - lo;
            for (int k = 0; k < s.Length; k++)
            {
                if (s[k] == ' ' || !Fonts.Menu.Glyphs.TryGetValue(s[k], out var g)) continue;
                for (int r = 0; r < g.H && r < RowH; r++)
                    for (int q = 0; q < g.W; q++)
                    {
                        byte v = g.Index[r * g.W + q];
                        if (v == 0) continue;
                        int X = ox + x[k] + q;
                        if (X < left || X >= left + CellW) continue;
                        px[(top + r) * PageW + X] = map[v & 15];
                    }
            }
        }
        _px = px;
        return px;
    }

    /// <summary>Pre on LoadImage(rect, data): the game's own upload of the whole
    /// page, recognised by its pixels, gets the redrawn one instead.</summary>
    public static void BeforeLoadImage(CpuContext c, IMemory m)
    {
        uint rect = c.A0, data = c.A1;
        if ((short)m.ReadU16(rect) != PageX || (short)m.ReadU16(rect + 2) != PageY
            || (short)m.ReadU16(rect + 4) != PageHW || (short)m.ReadU16(rect + 6) != PageH) return;
        var px = Pixels();
        if (px == null || _disc == null) return;
        for (int k = 0; k < _disc.Length; k += 97)                 // the disc's page, not something else put there
            if (m.ReadU8(data + (uint)k) != _disc[k]) return;
        for (int k = 0; k < px.Length; k += 2)
            m.WriteU8(data + (uint)(k >> 1), (byte)(px[k] | (px[k + 1] << 4)));
        Shown++;
    }

    /// <summary>From the main loop: put the page up directly, once (and after a change).</summary>
    public static void UploadOnce(CpuContext c, IMemory m)
    {
        if (_uploaded) return;
        var px = Pixels();
        if (px == null) return;
        _uploaded = true;
        uint scratch = (c.SP - 0x3000) & ~0xFu, data = scratch + 8;
        var snap = c.Snapshot();
        try
        {
            for (int y = 0; y < PageH; y += Band)
            {
                m.WriteU16(scratch, PageX);
                m.WriteU16(scratch + 2, (ushort)(PageY + y));
                m.WriteU16(scratch + 4, PageHW);
                m.WriteU16(scratch + 6, Band);
                for (int v = 0; v < Band; v++)
                    for (int u = 0; u < PageW; u += 2)
                        m.WriteU8(data + (uint)((v * PageW + u) / 2), (byte)(px[(y + v) * PageW + u] | (px[(y + v) * PageW + u + 1] << 4)));
                c.Restore(snap);
                c.A0 = scratch; c.A1 = data; c.A2 = 0; c.A3 = 0;
                c.RA = 0x8001FFE0;
                KingsField2.func_80060624(c, m);
            }
            Console.WriteLine("[KF2] text: on-screen messages uploaded");
        }
        catch (Exception e) { Console.WriteLine($"[KF2] text: on-screen messages upload: {e.Message}"); }
        finally { c.Restore(snap); }
    }
}
