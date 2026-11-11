using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Kf2.Text;

/// <summary>
/// Just enough PNG for the character maps: writes 8-bit RGB, and reads what
/// pixel editors save -- 8-bit greyscale, RGB, indexed, grey+alpha or RGBA,
/// not interlaced -- as RGB.
/// </summary>
static class Png
{
    static readonly uint[] _crc = MakeCrc();

    static uint[] MakeCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in type) c = _crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = _crc[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    static void BE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        BE(s, (uint)data.Length);
        s.Write(t, 0, 4);
        s.Write(data, 0, data.Length);
        BE(s, Crc(t, data));
    }

    /// <summary>Write rgb (3 bytes a pixel, row-major) as a PNG.</summary>
    public static void Write(string path, int w, int h, byte[] rgb)
    {
        var raw = new byte[h * (1 + w * 3)];
        for (int y = 0; y < h; y++)
        {
            raw[y * (1 + w * 3)] = 0;   // filter: none
            Buffer.BlockCopy(rgb, y * w * 3, raw, y * (1 + w * 3) + 1, w * 3);
        }
        byte[] packed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            packed = ms.ToArray();
        }
        var head = new byte[13];
        head[0] = (byte)(w >> 24); head[1] = (byte)(w >> 16); head[2] = (byte)(w >> 8); head[3] = (byte)w;
        head[4] = (byte)(h >> 24); head[5] = (byte)(h >> 16); head[6] = (byte)(h >> 8); head[7] = (byte)h;
        head[8] = 8; head[9] = 2;      // 8-bit RGB
        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(f, "IHDR", head);
        Chunk(f, "IDAT", packed);
        Chunk(f, "IEND", []);
    }

    static uint U32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    /// <summary>Read a PNG as RGB (3 bytes a pixel). Throws with a plain message
    /// for a kind it does not read.</summary>
    public static byte[] Read(string path, out int w, out int h)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 8 || b[0] != 0x89 || b[1] != 0x50) throw new InvalidDataException("not a PNG");
        int p = 8, depth = 0, type = 0, interlace = 0;
        w = h = 0;
        byte[] palette = [];
        using var idat = new MemoryStream();
        while (p + 8 <= b.Length)
        {
            int len = (int)U32(b, p);
            string t = Encoding.ASCII.GetString(b, p + 4, 4);
            int d = p + 8;
            if (t == "IHDR")
            {
                w = (int)U32(b, d); h = (int)U32(b, d + 4);
                depth = b[d + 8]; type = b[d + 9]; interlace = b[d + 12];
            }
            else if (t == "PLTE") { palette = new byte[len]; Buffer.BlockCopy(b, d, palette, 0, len); }
            else if (t == "IDAT") idat.Write(b, d, len);
            else if (t == "IEND") break;
            p = d + len + 4;
        }
        if (depth != 8) throw new InvalidDataException($"{depth}-bit PNG: save it as 8 bits a channel");
        if (interlace != 0) throw new InvalidDataException("interlaced PNG: save it without interlacing");
        int bpp = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (bpp == 0) throw new InvalidDataException($"PNG colour type {type}");

        idat.Position = 0;
        byte[] raw;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        using (var outp = new MemoryStream())
        {
            z.CopyTo(outp);
            raw = outp.ToArray();
        }
        int stride = w * bpp;
        var px = new byte[h * stride];
        var prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)], src = y * (stride + 1) + 1;
            var line = new byte[stride];
            for (int i = 0; i < stride; i++)
            {
                int x = raw[src + i];
                int a = i >= bpp ? line[i - bpp] : 0, up = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int v = f switch
                {
                    1 => x + a,
                    2 => x + up,
                    3 => x + ((a + up) >> 1),
                    4 => x + Paeth(a, up, c),
                    _ => x,
                };
                line[i] = (byte)v;
            }
            Buffer.BlockCopy(line, 0, px, y * stride, stride);
            prev = line;
        }
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            byte r, g, bl;
            switch (type)
            {
                case 0: r = g = bl = px[i]; break;
                case 4: r = g = bl = px[i * 2]; break;
                case 3:
                    int k = px[i] * 3;
                    r = k + 2 < palette.Length ? palette[k] : (byte)0;
                    g = k + 2 < palette.Length ? palette[k + 1] : (byte)0;
                    bl = k + 2 < palette.Length ? palette[k + 2] : (byte)0;
                    break;
                default: r = px[i * bpp]; g = px[i * bpp + 1]; bl = px[i * bpp + 2]; break;
            }
            rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = bl;
        }
        return rgb;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
