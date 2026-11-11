using System;
using System.IO;
using RecompOne.Runtime.Cdrom;

namespace Kf2.Text;

/// <summary>
/// The three archives the fonts come from, read once off the player's disc:
/// COM\TALK.T (archive 3) and COM\ITEM.T (archive 6), whose message pictures
/// carry the dialogue and sign letters, and COM\FDAT.T, whose entry 47 holds the menu
/// font page (VRAM 832,256) and its palette (576,509). Each archive is a u16
/// count, then u16 start sectors (count + 1 of them).
/// </summary>
static class Disc
{
    public sealed class Archive
    {
        public readonly byte[] Data;
        readonly int[] _starts;
        public Archive(byte[] data)
        {
            Data = data;
            int n = BitConverter.ToUInt16(data, 0);
            _starts = new int[n + 1];
            for (int i = 0; i <= n; i++) _starts[i] = BitConverter.ToUInt16(data, 2 + 2 * i) * 2048;
        }
        public int Count => _starts.Length - 1;
        public int Offset(int i) => _starts[i];
        public int Length(int i) => i < 0 || i >= Count ? 0 : Math.Max(Math.Min(_starts[i + 1], Data.Length) - _starts[i], 0);
    }

    /// <summary>A 4-bit TIM with a CLUT: 16 colours, one byte a pixel.</summary>
    public sealed class Tim
    {
        public ushort[] Clut = new ushort[16];
        public byte[] Px = [];
        public int W, H;
        public int PixelOffset;            // where its packed pixels start in the archive
    }

    public static Archive? Talk, Item, Fdat;
    public static string Error { get; private set; } = "";
    static bool _tried;

    public static bool Load()
    {
        if (_tried) return Talk != null;
        _tried = true;
        try
        {
            string? path = DiscPath();
            if (path == null) { Error = "the disc image was not found"; return false; }
            using (var disc = DiscFs.Open(path))
            {
                Talk = new Archive(disc.ReadFile("CD/COM/TALK.T"));
                Item = new Archive(disc.ReadFile("CD/COM/ITEM.T"));
                Fdat = new Archive(disc.ReadFile("CD/COM/FDAT.T"));
            }
            return true;
        }
        catch (Exception e)
        {
            Talk = null;
            Error = "could not read the disc: " + e.Message;
            Console.WriteLine($"[KF2] text: reading the disc: {e}");
            return false;
        }
    }

    /// <summary>The disc the game runs: the one on the command line, as Entry.Run
    /// takes it, else the saved one. (The mod read the saved one first, which is the
    /// wrong disc when the two differ.)</summary>
    static string? DiscPath()
    {
        foreach (var a in Environment.GetCommandLineArgs().Skip(1))
        {
            string ext = Path.GetExtension(a).ToLowerInvariant();
            if ((ext is ".cue" or ".bin" or ".chd" or ".iso") && File.Exists(a)) return a;
        }
        string p = RecompOne.Runtime.Runtime.CdPath;
        return !string.IsNullOrWhiteSpace(p) && File.Exists(p) ? p : null;
    }

    public static Archive? Of(int archive) => archive == 3 ? Talk : archive == 6 ? Item : null;

    public static Tim? ReadTim(int archive, int entry)
    {
        var arc = Of(archive);
        if (arc == null || arc.Length(entry) < 64) return null;
        var d = arc.Data;
        int s = arc.Offset(entry);
        if (BitConverter.ToUInt32(d, s) != 0x10 || BitConverter.ToUInt32(d, s + 4) != 8) return null;
        var t = new Tim();
        for (int i = 0; i < 16; i++) t.Clut[i] = BitConverter.ToUInt16(d, s + 20 + 2 * i);
        int p = s + 8 + (int)BitConverter.ToUInt32(d, s + 8);
        t.W = BitConverter.ToUInt16(d, p + 8) * 4;
        t.H = BitConverter.ToUInt16(d, p + 10);
        t.PixelOffset = p + 12;
        t.Px = new byte[t.W * t.H];
        for (int k = 0; k < t.Px.Length; k += 2)
        {
            byte b = d[t.PixelOffset + (k >> 1)];
            t.Px[k] = (byte)(b & 15);
            t.Px[k + 1] = (byte)(b >> 4);
        }
        return t;
    }

    /// <summary>The menu font page (256x256, one byte a pixel) and its palette,
    /// from FDAT.T entry 47's VRAM blocks: (x, y, w, h) twice, then the pixels.</summary>
    public static bool MenuPage(out byte[] page, out ushort[] clut)
    {
        page = new byte[256 * 256];
        clut = new ushort[16];
        if (Fdat == null) return false;
        var d = Fdat.Data;
        int p = Fdat.Offset(47), end = p + Fdat.Length(47);
        bool gotPage = false, gotClut = false;
        while (p + 16 <= end)
        {
            int x = BitConverter.ToUInt16(d, p), y = BitConverter.ToUInt16(d, p + 2);
            int w = BitConverter.ToUInt16(d, p + 4), h = BitConverter.ToUInt16(d, p + 6);
            if (w == 0 || h == 0 || w == 0xFFFF || BitConverter.ToUInt32(d, p) != BitConverter.ToUInt32(d, p + 8)) break;
            int data = p + 16, bytes = w * h * 2;
            if (data + bytes > end) break;
            if (x == 576 && y == 509 && w == 16 && h == 1)
            {
                for (int i = 0; i < 16; i++) clut[i] = BitConverter.ToUInt16(d, data + 2 * i);
                gotClut = true;
            }
            else if (x == 832 && y == 256 && w == 64 && h == 256)
            {
                for (int k = 0; k < 256 * 256; k += 2)
                {
                    byte b = d[data + (k >> 1)];
                    page[k] = (byte)(b & 15);
                    page[k + 1] = (byte)(b >> 4);
                }
                gotPage = true;
            }
            p = data + bytes;
        }
        return gotPage && gotClut;
    }

    /// <summary>A 4-bit block of FDAT.T entry 47 by its VRAM position, one byte a
    /// pixel, with the palette uploaded just before it (each block follows its own).</summary>
    public static bool Block(int vx, int vy, out byte[] px, out int w, out int h, out ushort[] clut)
    {
        px = []; w = h = 0;
        clut = new ushort[16];
        if (Fdat == null) return false;
        var d = Fdat.Data;
        int p = Fdat.Offset(47), end = p + Fdat.Length(47);
        var last = new ushort[16];
        while (p + 16 <= end)
        {
            int x = BitConverter.ToUInt16(d, p), y = BitConverter.ToUInt16(d, p + 2);
            int bw = BitConverter.ToUInt16(d, p + 4), bh = BitConverter.ToUInt16(d, p + 6);
            if (bw == 0 || bh == 0 || bw == 0xFFFF || BitConverter.ToUInt32(d, p) != BitConverter.ToUInt32(d, p + 8)) break;
            int data = p + 16, bytes = bw * bh * 2;
            if (data + bytes > end) break;
            if (bw == 16 && bh == 1)
                for (int i = 0; i < 16; i++) last[i] = BitConverter.ToUInt16(d, data + 2 * i);
            else if (x == vx && y == vy)
            {
                w = bw * 4; h = bh;
                px = new byte[w * h];
                for (int k = 0; k < px.Length; k += 2)
                {
                    byte b = d[data + (k >> 1)];
                    px[k] = (byte)(b & 15);
                    px[k + 1] = (byte)(b >> 4);
                }
                Array.Copy(last, clut, 16);
                return true;
            }
            p = data + bytes;
        }
        return false;
    }

    public static double Lum(ushort c) => ((c & 31) * 2 + ((c >> 5) & 31) * 5 + ((c >> 10) & 31)) / 8.0;

    public static (byte R, byte G, byte B) Rgb(ushort c) =>
        ((byte)((c & 31) * 255 / 31), (byte)(((c >> 5) & 31) * 255 / 31), (byte)(((c >> 10) & 31) * 255 / 31));

    public static int Background(byte[] px)
    {
        var n = new int[16];
        foreach (var p in px) n[p]++;
        int bg = 0;
        for (int i = 1; i < 16; i++) if (n[i] > n[bg]) bg = i;
        return bg;
    }
}
