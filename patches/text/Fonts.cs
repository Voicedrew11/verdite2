using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Kf2.Text;

/// <summary>A letter: for the picture fonts its ink (0 clear .. 1 full) a pixel,
/// for the menu font its palette index a pixel.</summary>
sealed class Glyph
{
    public int W, H;
    public float[] Ink = [];
    public byte[] Index = [];
}

sealed class Font
{
    public readonly string Name;
    public readonly int CellW, CellH, Cap, Baseline;
    public readonly bool Menu;
    public readonly Dictionary<char, Glyph> Glyphs = new();
    public string Source = "";
    public Spacing Sp = null!;

    public Font(string name, int cellW, int cellH, int cap, int baseline, bool menu)
    {
        Name = name; CellW = cellW; CellH = cellH; Cap = cap; Baseline = baseline; Menu = menu;
    }

    public string Charset => Menu ? FontData.Charset + FontData.MenuExtra : FontData.Charset;
}

/// <summary>
/// The three fonts, and their character maps.
///
/// The letters are read from the character maps in the data directory's fonts/
/// folder: NAME.png (the character map), NAME.json (where each letter is in it) and
/// NAME_template.png (the same grid, empty, for a new font). A font with no map
/// there gets the port's own, written from <see cref="FontMaps"/>, so editing one
/// changes the game. A map that cannot be read is built from the player's disc
/// (Disc.cs), the hand-drawn letters and the hand edits (FontData.cs) instead;
/// "Rebuild" in the settings does that for all three and writes the maps,
/// replacing what was there, and "Restore" puts the port's own back.
///
/// In a map, picture-font ink is grey (232 = full ink, darker = fainter, black
/// = clear); menu-font pixels are the menu palette's own colours, read back to
/// the nearest one. The coloured frames and ticks around the cells are guides
/// and are never read, except that each letter is wherever its frame is: a cell
/// can be widened or lengthened in the image, frame and all, and the letter is
/// read at its new size. NAME_spacing.json beside them holds the font's spacing
/// and kerning (Spacing.cs).
///
/// Loaded once, on the game thread, the first time a picture or the menu font
/// needs it. Rebuild and Restore run there too (<see cref="Lettering"/> queues
/// them from the settings window).
/// </summary>
static class Fonts
{
    /// <summary>The fonts folder: relative paths are the data directory's (the
    /// launcher makes it the working directory).</summary>
    public static readonly string Folder = Path.GetFullPath("fonts");

    public static readonly Font Thin = new("dialogue", 8, 14, 0, 11, false);
    public static readonly Font Bold = new("sign", 9, 18, 2, 16, false);
    public static readonly Font Menu = new("menu", 8, 15, 1, 11, true);
    public static ushort[] MenuClut = new ushort[16];

    /// <summary>Proportional spacing (Spacing.cs) on picture text and on menu text;
    /// off, letters sit in fixed cells as on the disc.</summary>
    public static bool PropPictures = true, PropMenus = true;

    static Fonts()
    {
        Thin.Sp = new Spacing(Thin, gap: 1, space: 4, maxKern: 1) { LineGap = 1 };
        Bold.Sp = new Spacing(Bold, gap: 2, space: 6, maxKern: 1) { LineGap = 2 };
        Menu.Sp = new Spacing(Menu, gap: 1, space: 5, maxKern: 1);
    }
    public static string Status { get; private set; } = "not loaded yet";
    static bool _tried, _ok;

    public static bool Ready()
    {
        if (_tried) return _ok;
        _tried = true;
        if (!Disc.Load()) { Status = Disc.Error; return false; }
        if (!Disc.MenuPage(out _, out MenuClut)) { Status = "the menu font was not found on the disc"; return false; }
        foreach (var f in new[] { Thin, Bold, Menu })
        {
            try
            {
                if (!LoadMap(f) && !(WriteShipped(f) && LoadMap(f))) { Build(f); SaveMap(f); }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[KF2] text: {f.Name}: {e.Message}; built from the disc instead");
                Build(f);
            }
        }
        foreach (var f in new[] { Thin, Bold, Menu }) f.Sp.Load();
        _ok = true;
        Status = $"dialogue {Thin.Glyphs.Count} letters ({Thin.Source}), sign {Bold.Glyphs.Count} ({Bold.Source}), menu {Menu.Glyphs.Count} ({Menu.Source})";
        Console.WriteLine($"[KF2] text: fonts: {Status}");
        return true;
    }

    /// <summary>Write every character map afresh from the disc, and the templates.</summary>
    public static void Rebuild()
    {
        if (!Disc.Load()) { Status = Disc.Error; return; }
        foreach (var f in new[] { Thin, Bold, Menu }) { Build(f); SaveMap(f); f.Sp.Load(); }
        _tried = _ok = true;
        Status = "rebuilt from the disc";
        MenuCells.Invalidate();
        HudMessages.Invalidate();
        Console.WriteLine("[KF2] text: character maps rebuilt from the disc");
    }

    /// <summary>Put the port's own character maps back, replacing what is in the
    /// folder, and read them.</summary>
    public static void Restore()
    {
        if (!Disc.Load()) { Status = Disc.Error; return; }
        foreach (var f in new[] { Thin, Bold, Menu })
        {
            if (!WriteShipped(f) || !LoadMap(f)) { Build(f); SaveMap(f); }
            f.Sp.Load();
        }
        _tried = _ok = true;
        Status = "the port's own, restored";
        MenuCells.Invalidate();
        HudMessages.Invalidate();
        Console.WriteLine("[KF2] text: character maps restored to the port's own");
    }

    /// <summary>The port's own files for a font into the folder. False if it has
    /// none or the folder cannot be written.</summary>
    static bool WriteShipped(Font f)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            bool any = false;
            foreach (var suffix in new[] { ".png", ".json", "_template.png", "_spacing.json" })
            {
                if (!FontMaps.Files.TryGetValue(f.Name + suffix, out var b64)) continue;
                File.WriteAllBytes(Path.Combine(Folder, f.Name + suffix), Convert.FromBase64String(b64));
                any = true;
            }
            return any;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[KF2] text: writing the {f.Name} font: {e.Message}");
            return false;
        }
    }

    // ---- building from the disc ----

    static float[] Cut(int archive, int entry, int x, int y, int w, int h)
    {
        var g = new float[w * h];
        var t = Disc.ReadTim(archive, entry);
        if (t == null) return g;
        int bg = Disc.Background(t.Px);
        double mx = 0;
        foreach (var p in t.Px) if (p != bg) mx = Math.Max(mx, Disc.Lum(t.Clut[p]));
        if (mx <= 0) return g;
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int yy = y + r, xx = x + c;
                if (yy < 0 || xx < 0 || yy >= t.H || xx >= t.W) continue;
                byte p = t.Px[yy * t.W + xx];
                if (p != bg) g[r * w + c] = (float)(Disc.Lum(t.Clut[p]) / mx);
            }
        return g;
    }

    static Glyph Ink(int w, int h, float[] ink) => new() { W = w, H = h, Ink = ink };

    static Glyph FromGrey(string[] rows, int h)
    {
        int w = rows.Length > 0 ? rows[0].Length / 2 : 0;
        var ink = new float[w * h];
        for (int r = 0; r < rows.Length && r < h; r++)
            for (int c = 0; c < w; c++)
            {
                string s = rows[r].Substring(2 * c, 2);
                if (s != "..") ink[r * w + c] = Convert.ToInt32(s, 16) / 232f;
            }
        return Ink(w, h, ink);
    }

    static float Quantize(float f) => f < 0.05f ? 0 : f >= 0.8f ? 1f : f >= 0.55f ? 168 / 232f : 104 / 232f;

    static void Build(Font f)
    {
        f.Glyphs.Clear();
        if (f == Thin)
        {
            foreach (var (c, s) in FontData.ThinDisc) f.Glyphs[c] = Ink(8, 14, Cut(s.Archive, s.Entry, s.X, s.Y, 8, 14));
            if (f.Glyphs.TryGetValue(':', out var colon) && f.Glyphs.TryGetValue(',', out var comma))
            {
                var semi = (float[])comma.Ink.Clone();
                for (int k = 0; k < 7 * 8; k++) semi[k] = Math.Max(semi[k], colon.Ink[k]);
                f.Glyphs[';'] = Ink(8, 14, semi);
            }
            foreach (var (c, edits) in FontData.ThinEdits)
            {
                if (!f.Glyphs.TryGetValue(c, out var g)) continue;
                for (int k = 0; k < g.Ink.Length; k++) g.Ink[k] = Quantize(g.Ink[k]);
                foreach (var (r, q, v) in edits) g.Ink[r * g.W + q] = v / 232f;
            }
            foreach (var (c, rows) in FontData.ThinDrawn) f.Glyphs[c] = FromGrey(rows, 14);
        }
        else if (f == Bold)
        {
            foreach (var (c, s) in FontData.BoldDisc) f.Glyphs[c] = Ink(9, 18, Cut(s.Archive, s.Entry, s.X, s.Y, 9, 18));
            f.Glyphs['→'] = Ink(18, 18, Cut(6, 186, 118, 101, 18, 18));   // the signpost arrows
            f.Glyphs['←'] = Ink(18, 18, Cut(6, 190, 118, 101, 18, 18));
            foreach (var (c, rows) in FontData.BoldDrawn) f.Glyphs[c] = FromGrey(rows, 18);
        }
        else
        {
            Disc.MenuPage(out var page, out MenuClut);
            foreach (var (c, index) in MenuCells.DiscCells)
            {
                var g = new Glyph { W = 8, H = 15, Index = new byte[8 * 15] };
                int u = (index & 15) * 8, v = (index >> 4) * 15;
                for (int r = 0; r < 15; r++)
                    for (int q = 0; q < 8; q++) g.Index[r * 8 + q] = page[(v + r) * 256 + u + q];
                f.Glyphs[c] = g;
            }
            // Lower case: the thin font's own, recoloured, then the hand edits.
            for (char c = 'a'; c <= 'z'; c++)
            {
                if (!Thin.Glyphs.TryGetValue(c, out var t)) continue;
                var g = new Glyph { W = 8, H = 15, Index = new byte[8 * 15] };
                for (int r = 0; r < 14 && r < t.H; r++)
                    for (int q = 0; q < 8; q++)
                    {
                        float ink = t.Ink[r * t.W + q];
                        g.Index[r * 8 + q] = (byte)(ink < 0.05f ? 0 : ink >= 0.85f ? 0xF : ink >= 0.45f ? 0xC : 0x2);
                    }
                if (FontData.MenuEdits.TryGetValue(c, out var edits))
                    foreach (var (r, q, v) in edits) g.Index[r * 8 + q] = (byte)v;
                f.Glyphs[c] = g;
            }
            foreach (var (c, rows) in FontData.MenuDrawn)
            {
                var g = new Glyph { W = 8, H = 15, Index = new byte[8 * 15] };
                for (int r = 0; r < rows.Length && r < 15; r++)
                    for (int q = 0; q < 8 && q < rows[r].Length; q++)
                        g.Index[r * 8 + q] = rows[r][q] == '.' ? (byte)0 : Convert.ToByte(rows[r][q].ToString(), 16);
                f.Glyphs[c] = g;
            }
        }
        f.Source = "built from the disc";
    }

    // ---- character maps ----

    const int PerRow = 16, Gap = 3;
    static readonly (byte, byte, byte) GapColour = (24, 24, 48), Frame = (0, 170, 60), Tick = (0, 150, 200);

    sealed record Slot(char C, int X, int Y, int W);

    static int SlotWidth(Font f, char c) => !f.Menu && (c == '→' || c == '←') ? 2 * f.CellW : f.CellW;

    static List<Slot> Layout(Font f, out int width, out int height)
    {
        var slots = new List<Slot>();
        int x = Gap, y = Gap, n = 0;
        width = 0;
        foreach (char c in f.Charset)
        {
            int w = SlotWidth(f, c);
            slots.Add(new Slot(c, x + 1, y + 1, w));
            x += w + 2 + Gap;
            width = Math.Max(width, x);
            if (++n % PerRow == 0) { x = Gap; y += f.CellH + 2 + Gap; }
        }
        height = (n % PerRow == 0 ? y : y + f.CellH + 2 + Gap);
        return slots;
    }

    static string MapPath(Font f, string suffix) => Path.Combine(Folder, f.Name + suffix);

    static void SaveMap(Font f)
    {
        Directory.CreateDirectory(Folder);
        var slots = Layout(f, out int w, out int h);
        Png.Write(MapPath(f, ".png"), w, h, Paint(f, slots, w, h, true));
        Png.Write(MapPath(f, "_template.png"), w, h, Paint(f, slots, w, h, false));
        var json = new StringBuilder();
        json.Append($"{{\n  \"font\": \"{f.Name}\", \"cellWidth\": {f.CellW}, \"cellHeight\": {f.CellH}, ");
        json.Append($"\"capHeightRow\": {f.Cap}, \"baselineRow\": {f.Baseline},\n");
        json.Append(f.Menu ? "  \"colours\": \"the menu palette's own; any other colour is read as the nearest\",\n"
                           : "  \"colours\": \"grey: 232 = full ink, darker = fainter, black = clear\",\n");
        json.Append("  \"letters\": [\n");
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            string c = s.C switch { '"' => "\\\"", '\\' => "\\\\", _ => s.C > 126 ? $"\\u{(int)s.C:x4}" : s.C.ToString() };
            json.Append($"    {{ \"char\": \"{c}\", \"x\": {s.X}, \"y\": {s.Y}, \"w\": {s.W}, \"h\": {f.CellH} }}{(i + 1 < slots.Count ? "," : "")}\n");
        }
        json.Append("  ]\n}\n");
        File.WriteAllText(MapPath(f, ".json"), json.ToString());
        Console.WriteLine($"[KF2] text: wrote {Path.GetFullPath(MapPath(f, ".png"))}");
    }

    static byte[] Paint(Font f, List<Slot> slots, int w, int h, bool letters)
    {
        var rgb = new byte[w * h * 3];
        void Put(int x, int y, (byte R, byte G, byte B) c)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int i = (y * w + x) * 3;
            rgb[i] = c.R; rgb[i + 1] = c.G; rgb[i + 2] = c.B;
        }
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) Put(x, y, GapColour);
        foreach (var s in slots)
        {
            for (int x = s.X - 1; x <= s.X + s.W; x++) { Put(x, s.Y - 1, Frame); Put(x, s.Y + f.CellH, Frame); }
            for (int y = s.Y - 1; y <= s.Y + f.CellH; y++) { Put(s.X - 1, y, Frame); Put(s.X + s.W, y, Frame); }
            foreach (int row in new[] { f.Cap, f.Baseline })
            {
                Put(s.X - 1, s.Y + row, Tick);
                Put(s.X + s.W, s.Y + row, Tick);
            }
            f.Glyphs.TryGetValue(s.C, out var g);
            for (int r = 0; r < f.CellH; r++)
                for (int q = 0; q < s.W; q++)
                {
                    (byte, byte, byte) c = (0, 0, 0);
                    if (letters && g != null && r < g.H && q < g.W)
                    {
                        if (f.Menu)
                        {
                            byte idx = g.Index[r * g.W + q];
                            if (idx != 0) c = Disc.Rgb(MenuClut[idx]);
                        }
                        else
                        {
                            int v = (int)Math.Round(Math.Clamp(g.Ink[r * g.W + q], 0f, 1.1f) * 232);
                            byte b = (byte)Math.Min(255, v);
                            c = (b, b, b);
                        }
                    }
                    Put(s.X + q, s.Y + r, c);
                }
        }
        return rgb;
    }

    /// <summary>Read a font's letters from its character map. False if it has none.</summary>
    static bool LoadMap(Font f)
    {
        string png = MapPath(f, ".png"), json = MapPath(f, ".json");
        if (!File.Exists(png) || !File.Exists(json)) return false;
        var rgb = Png.Read(png, out int w, out int h);
        using var doc = JsonDocument.Parse(File.ReadAllText(json), new JsonDocumentOptions { AllowTrailingCommas = true });
        f.Glyphs.Clear();
        // Cells as the map's own frames draw them, so a cell can be made wider or
        // taller in the image; the json's positions when the frames do not add up.
        var letters = new List<(char C, int X, int Y, int W, int H)>();
        var framed = Frames(rgb, w, h);
        if (framed.Count == f.Charset.Length)
            for (int i = 0; i < framed.Count; i++) letters.Add((f.Charset[i], framed[i].X, framed[i].Y, framed[i].W, framed[i].H));
        else
        {
            if (framed.Count > 0) Console.WriteLine($"[KF2] text: {f.Name}.png: {framed.Count} framed cells for {f.Charset.Length} letters; using {f.Name}.json's positions");
            foreach (var e in doc.RootElement.GetProperty("letters").EnumerateArray())
            {
                string cs = e.GetProperty("char").GetString() ?? "";
                if (cs.Length != 1) continue;
                letters.Add((cs[0], e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(),
                             e.GetProperty("w").GetInt32(), e.GetProperty("h").GetInt32()));
            }
        }
        foreach (var (ch, x, y, gw, gh) in letters)
        {
            string cs = ch.ToString();
            var g = new Glyph { W = gw, H = gh };
            if (f.Menu) g.Index = new byte[gw * gh]; else g.Ink = new float[gw * gh];
            bool any = false;
            for (int r = 0; r < gh; r++)
                for (int q = 0; q < gw; q++)
                {
                    int px = x + q, py = y + r;
                    if (px >= w || py >= h) continue;
                    int i = (py * w + px) * 3;
                    byte R = rgb[i], G = rgb[i + 1], B = rgb[i + 2];
                    if (f.Menu)
                    {
                        g.Index[r * gw + q] = Nearest(R, G, B);
                        any |= g.Index[r * gw + q] != 0;
                    }
                    else
                    {
                        float v = Math.Max(R, Math.Max(G, B)) / 232f;
                        g.Ink[r * gw + q] = v < 0.05f ? 0 : v;
                        any |= v >= 0.05f;
                    }
                }
            if (any) f.Glyphs[cs[0]] = g;   // an empty cell is a letter the font does not have
        }
        f.Source = "from its character map";
        return true;
    }

    /// <summary>The insides of the green frames, in reading order (rows top to
    /// bottom, then left to right). A frame's sides may carry the blue ticks.</summary>
    static List<(int X, int Y, int W, int H)> Frames(byte[] rgb, int w, int h)
    {
        bool F(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return false;
            int i = (y * w + x) * 3;
            return rgb[i] < 40 && rgb[i + 1] > 120 && rgb[i + 2] >= 30 && rgb[i + 2] <= 230;
        }
        var found = new List<(int X, int Y, int W, int H)>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!F(x, y) || F(x - 1, y) || F(x, y - 1) || !F(x + 1, y) || !F(x, y + 1)) continue;
                int x1 = x; while (F(x1 + 1, y)) x1++;
                int y1 = y; while (F(x, y1 + 1)) y1++;
                if (x1 - x < 3 || y1 - y < 3 || !F(x1, y1)) continue;     // a whole frame, not a stray mark
                found.Add((x + 1, y + 1, x1 - x - 1, y1 - y - 1));
            }
        found.Sort((a, b) => Math.Abs(a.Y - b.Y) < 3 ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        return found;
    }

    /// <summary>The tallest letter, for line spacing.</summary>
    public static int Tallest(Font f)
    {
        int t = f.CellH;
        foreach (var g in f.Glyphs.Values) t = Math.Max(t, g.H);
        return t;
    }

    static byte Nearest(byte r, byte g, byte b)
    {
        if (r < 8 && g < 8 && b < 8) return 0;
        int best = 1, dist = int.MaxValue;
        for (int i = 1; i < 16; i++)
        {
            var (R, G, B) = Disc.Rgb(MenuClut[i]);
            int d = (R - r) * (R - r) + (G - g) * (G - g) + (B - b) * (B - b);
            if (d < dist) { dist = d; best = i; }
        }
        return (byte)best;
    }
}
