using System;
using System.Collections.Generic;
using System.IO;

namespace Kf2.Japanese;

/// <summary>
/// The Japanese label glyphs, copied into the free part of the US label font's page, so
/// the US font and the Japanese one share one texture page and one palette, and the
/// English left on screen (the port's own pages, a label with no Japanese) still draws.
///
/// The page is the label font's: tpage 29, 4-bit, at VRAM words (832, 256), 64 words
/// (256 px) wide, palette (576, 509). The Japanese font fills the same page on its own
/// disc, in 15 x 15 cells, 16 across: glyph <c>g</c> at u = 15 * (g &amp; 15),
/// v = 15 * (g &gt;&gt; 4). Its palette differs from the US one, so each glyph's
/// pixels are mapped to the nearest US colour.
///
/// The menus use more Japanese glyphs than the free cells hold, but no one screen comes
/// near that, so the cells are a cache: a glyph is written to a cell the first time it is
/// drawn, and a cell is reused when its glyph has not been drawn for a few vblanks. The
/// primitives already queued for a frame read VRAM when they are drawn, which is why a
/// cell used that recently is not taken. The game uploads the page again with entry 47
/// (an area load), which zeroes the cells; each use checks a cell's pixels against VRAM
/// and writes them back when they differ.
///
/// A cell is free when no US pixel is in it, it is past the rows the port's lettering
/// adds lower case to (rows 0-6, u 0-127), and no US blank character reads from it (the
/// label space, code 0x7F, at u 120, v 105).
/// </summary>
sealed class JapaneseFontAtlas
{
    public const int PageX = 832, PageY = 256, PageWords = 64;
    const int PagePixels = 256;
    const int VramWidth = 1024;
    const int Cell = 15;
    const int GlyphColumns = 16;

    /// <summary>The atlas grid stops short of the number strip at u 240.</summary>
    const int Columns = 16;

    /// <summary>The port's added letters: u 0-127, v 0-104 (MenuCells' Letters area).</summary>
    const int LetteringRight = 128, LetteringBottom = 105;

    static readonly (int U, int V, int Width, int Height)[] UsBlanks =
    [
        (120, 105, 8, 15),
    ];

    /// <summary>A cell drawn within this many vblanks may still be waiting in the frame's primitives.</summary>
    const long ProtectedVblanks = 3;

    readonly byte[] _glyphs;                                  // the Japanese page, one nibble a byte, US colours
    readonly (int X, int Y)[] _free;
    readonly int[] _glyphAt;
    readonly long[] _usedAt;
    readonly Dictionary<int, int> _cellOf = new();

    JapaneseFontAtlas(byte[] glyphs, List<(int X, int Y)> free)
    {
        _glyphs = glyphs;
        _free = free.ToArray();
        _glyphAt = new int[_free.Length];
        Array.Fill(_glyphAt, -1);
        _usedAt = new long[_free.Length];
        Array.Fill(_usedAt, long.MinValue);
    }

    public int FreeCount => _free.Length;

    public int Writes { get; private set; }

    public int ForcedEvictions { get; private set; }

    /// <summary>The page u, v of a glyph at this vblank, written into VRAM if it is not there.</summary>
    public (byte U, byte V) CellOf(int glyph, long vblank, ushort[] vram, Action<uint> writeGp0)
    {
        if (!_cellOf.TryGetValue(glyph, out int cell))
        {
            cell = TakeCell(vblank);
            if (_glyphAt[cell] >= 0) _cellOf.Remove(_glyphAt[cell]);
            _glyphAt[cell] = glyph;
            _cellOf[glyph] = cell;
        }

        _usedAt[cell] = vblank;
        var (x, y) = _free[cell];
        if (!Holds(vram, glyph, x, y)) Write(vram, writeGp0, glyph, x, y);
        return ((byte)x, (byte)y);
    }

    int TakeCell(long vblank)
    {
        int oldest = 0;
        for (int i = 0; i < _free.Length; i++)
        {
            if (_glyphAt[i] < 0) return i;
            if (_usedAt[i] < _usedAt[oldest]) oldest = i;
        }
        if (vblank - _usedAt[oldest] <= ProtectedVblanks) ForcedEvictions++;
        return oldest;
    }

    bool Holds(ushort[] vram, int glyph, int cellX, int cellY)
    {
        for (int dy = 0; dy < Cell; dy++)
            for (int dx = 0; dx < Cell; dx++)
                if (VramNibble(vram, cellX + dx, cellY + dy) != GlyphNibble(glyph, dx, dy))
                    return false;
        return true;
    }

    /// <summary>Writes one cell with the GPU's LoadImage words (GP0 0xA0), keeping the
    /// neighbouring pixels of the words it shares.</summary>
    void Write(ushort[] vram, Action<uint> writeGp0, int glyph, int cellX, int cellY)
    {
        int firstWord = cellX / 4, lastWord = (cellX + Cell - 1) / 4;
        int words = lastWord - firstWord + 1;
        var region = new ushort[words * Cell];
        for (int dy = 0; dy < Cell; dy++)
            for (int w = 0; w < words; w++)
                region[dy * words + w] = ComposeWord(vram, glyph, cellX, cellY, firstWord + w, dy);

        writeGp0(0x01000000u);
        writeGp0(0xA0000000u);
        writeGp0(Pack(PageX + firstWord, PageY + cellY));
        writeGp0(Pack(words, Cell));
        for (int i = 0; i < region.Length; i += 2)
            writeGp0(region[i] | (i + 1 < region.Length ? (uint)region[i + 1] << 16 : 0));
        Writes++;
    }

    ushort ComposeWord(ushort[] vram, int glyph, int cellX, int cellY, int pageWord, int dy)
    {
        ushort word = vram[(PageY + cellY + dy) * VramWidth + PageX + pageWord];
        for (int nibble = 0; nibble < 4; nibble++)
        {
            int dx = pageWord * 4 + nibble - cellX;
            if (dx < 0 || dx >= Cell) continue;
            int shift = nibble * 4;
            word = (ushort)(word & ~(0xF << shift) | GlyphNibble(glyph, dx, dy) << shift);
        }
        return word;
    }

    static int VramNibble(ushort[] vram, int pageX, int pageY)
    {
        ushort word = vram[(PageY + pageY) * VramWidth + PageX + pageX / 4];
        return word >> (pageX % 4 * 4) & 0xF;
    }

    int GlyphNibble(int glyph, int dx, int dy)
    {
        int u = Cell * (glyph % GlyphColumns) + dx, v = Cell * (glyph / GlyphColumns) + dy;
        if (u >= PagePixels || v >= PagePixels) return 0;
        return _glyphs[v * PagePixels + u];
    }

    static uint Pack(int low, int high) => (uint)(ushort)low | (uint)(ushort)high << 16;

    /// <summary>The highest glyph the Japanese page holds: 17 rows of 16.</summary>
    public const int MaxGlyph = (PagePixels / Cell) * GlyphColumns - 1;

    /// <summary>
    /// The atlas, from FDAT.T entry 47 of both discs: the US page says which cells are
    /// free, the Japanese page and palette give the glyphs, mapped to the US palette.
    /// Throws InvalidDataException when the blocks are not as measured.
    /// </summary>
    public static JapaneseFontAtlas Plan(byte[] usFdat, byte[] jpFdat)
    {
        var usEntry = Entry(usFdat, JapaneseMix.PageEntry);
        var jpEntry = Entry(jpFdat, JapaneseMix.PageEntry);
        var (usPage, usClut) = Page(usEntry, "US");
        var (jpPage, jpClut) = Page(jpEntry, "Japanese");
        var toUs = ColourMap(jpClut, usClut);
        var glyphs = new byte[jpPage.Length];
        for (int i = 0; i < glyphs.Length; i++) glyphs[i] = toUs[jpPage[i]];
        return new JapaneseFontAtlas(glyphs, FreeCells(usPage));
    }

    /// <summary>For each Japanese palette index, the US index of the nearest colour; 0 (clear) stays 0.</summary>
    static byte[] ColourMap(ushort[] from, ushort[] to)
    {
        var map = new byte[16];
        for (int i = 1; i < 16; i++)
        {
            int best = 1, bestDistance = int.MaxValue;
            for (int j = 1; j < 16; j++)
            {
                int d = Distance(from[i], to[j]);
                if (d < bestDistance) { bestDistance = d; best = j; }
            }
            map[i] = (byte)best;
        }
        return map;
    }

    static int Distance(ushort a, ushort b)
    {
        int dr = (a & 31) - (b & 31), dg = (a >> 5 & 31) - (b >> 5 & 31), db = (a >> 10 & 31) - (b >> 10 & 31);
        return dr * dr + dg * dg + db * db;
    }

    static List<(int X, int Y)> FreeCells(byte[] usPage)
    {
        var free = new List<(int X, int Y)>();
        for (int y = 0; y + Cell <= PagePixels; y += Cell)
            for (int column = 0; column < Columns; column++)
            {
                int x = column * Cell;
                if (x < LetteringRight && y < LetteringBottom) continue;
                if (HoldsUsBlank(x, y) || !Empty(usPage, x, y)) continue;
                free.Add((x, y));
            }
        return free;
    }

    static bool HoldsUsBlank(int x, int y)
    {
        foreach (var blank in UsBlanks)
            if (x < blank.U + blank.Width && x + Cell > blank.U && y < blank.V + blank.Height && y + Cell > blank.V)
                return true;
        return false;
    }

    static bool Empty(byte[] page, int x, int y)
    {
        for (int dy = 0; dy < Cell; dy++)
            for (int dx = 0; dx < Cell; dx++)
                if (page[(y + dy) * PagePixels + x + dx] != 0) return false;
        return true;
    }

    /// <summary>The label page (one nibble a byte) and its palette, from an entry of VRAM blocks.</summary>
    static (byte[] Page, ushort[] Clut) Page(byte[] entry, string disc)
    {
        byte[]? page = null;
        ushort[]? clut = null;
        int at = 0;
        while (at + 16 <= entry.Length)
        {
            int x = U16(entry, at), y = U16(entry, at + 2), w = U16(entry, at + 4), h = U16(entry, at + 6);
            if (w == 0 || h == 0 || w == 0xFFFF) break;
            if (U16(entry, at + 8) != x || U16(entry, at + 10) != y || U16(entry, at + 12) != w || U16(entry, at + 14) != h) break;
            int data = at + 16, bytes = w * h * 2;
            if (data + bytes > entry.Length) break;
            if (x == 576 && y == 509 && w == 16 && h == 1)
            {
                clut = new ushort[16];
                for (int i = 0; i < 16; i++) clut[i] = (ushort)U16(entry, data + 2 * i);
            }
            else if (x == PageX && y == PageY && w == PageWords && h == PagePixels)
            {
                page = new byte[PagePixels * PagePixels];
                for (int k = 0; k < page.Length; k += 2)
                {
                    byte b = entry[data + (k >> 1)];
                    page[k] = (byte)(b & 15);
                    page[k + 1] = (byte)(b >> 4);
                }
            }
            at = data + bytes;
        }
        if (page is null || clut is null)
            throw new InvalidDataException($"the {disc} FDAT.T entry {JapaneseMix.PageEntry} has no label font page at ({PageX}, {PageY}) with its palette");
        return (page, clut);
    }

    static byte[] Entry(byte[] archive, int index)
    {
        var parsed = DiscArchive.Read("FDAT.T", archive);
        if (index >= parsed.Count) throw new InvalidDataException($"FDAT.T has {parsed.Count} entries, not {index + 1}");
        return parsed.Entry(index).ToArray();
    }

    static int U16(byte[] bytes, int at)
    {
        if (at < 0 || at + 2 > bytes.Length) throw new InvalidDataException($"a read at 0x{at:X} is outside the entry");
        return bytes[at] | bytes[at + 1] << 8;
    }
}
