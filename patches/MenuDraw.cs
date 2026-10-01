using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// C# rewrites of the game's menu drawing routines (func_80021E10,
/// func_80022B20, func_80021FCC, func_800222B8), writing the same POLY_FT4
/// packets byte for byte. Takes IMemory only: no CpuContext, no stack frame,
/// nothing outside the primitive buffer, the cursor words and the two
/// ordering-table slots. See "The menu's primitives are POLY_FT4s out of a
/// cursor, and the cursor is mirrored" in docs/GAME_INTERNALS.md.
/// </summary>
public static class MenuDraw
{
    public const uint Cursor = 0x8006E914;
    public const uint ActiveDescriptor = 0x8017E0A4;
    public const uint OrderingTable = 0x8018E0A8;
    public const uint TextTemplate = 0x80064BF0;
    public const uint NumberTemplate = 0x80064BE4;
    public const uint WindowTemplates = 0x80064C68;

    const int QuadSize = 0x28;

    // func_800229D8: length 9 and code 0x2C, colour grey. Writes the length
    // and code bytes separately, as the game does.
    static uint NewQuad(IMemory m)
    {
        uint p = m.ReadU32(Cursor);
        m.WriteU8(p + 3, 9);
        m.WriteU8(p + 7, 0x2C);
        m.WriteU8(p + 4, 0x68);
        m.WriteU8(p + 5, 0x68);
        m.WriteU8(p + 6, 0x68);
        return p;
    }

    // func_80022A28: AddPrim onto the slot, then bump the cursor twice.
    static void Link(IMemory m, uint p, int slot)
    {
        uint entry = m.ReadU32(OrderingTable) + (uint)(slot * 4);
        uint tag = m.ReadU32(p);
        uint head = m.ReadU32(entry);
        m.WriteU32(p, (tag & 0xFF000000) | (head & 0x00FFFFFF));
        m.WriteU32(entry, (head & 0xFF000000) | (p & 0x00FFFFFF));

        uint next = m.ReadU32(Cursor) + QuadSize;
        m.WriteU32(Cursor, next);
        m.WriteU32(m.ReadU32(ActiveDescriptor) + 8, next);
    }

    static int GlyphOf(char ch)
    {
        if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 'a' + 'A');
        if (ch >= 'A' && ch <= 'Z') return ch - 'A';
        if (ch == ' ') return 0x7F;
        throw new ArgumentException($"menu draw: no glyph for '{ch}'");
    }

    // func_80021E10. A space draws the cell at 0x7F; an empty string draws
    // nothing; at most 24 quads (the x step stops at 168).
    public static void DrawText(IMemory m, int x, int y, string s)
    {
        int step = 0;
        foreach (char ch in s)
        {
            if (step >= 168) break;
            int g = GlyphOf(ch) & 0x7F;
            uint p = NewQuad(m);
            int w = (short)m.ReadU16(TextTemplate + 8);
            int h = (short)m.ReadU16(TextTemplate + 0xA);
            int X = x + step;

            m.WriteU16(p + 0x08, (ushort)X);
            m.WriteU16(p + 0x0A, (ushort)y);
            m.WriteU16(p + 0x10, (ushort)(X + w));
            m.WriteU16(p + 0x12, (ushort)y);
            m.WriteU16(p + 0x18, (ushort)X);
            m.WriteU16(p + 0x1A, (ushort)(y + h));
            m.WriteU16(p + 0x20, (ushort)(X + w));
            m.WriteU16(p + 0x22, (ushort)(y + h));

            m.WriteU16(p + 0x16, m.ReadU16(TextTemplate));
            m.WriteU16(p + 0x0E, m.ReadU16(TextTemplate + 2));

            int u = (g & 15) * 8;
            int v = (g >> 4) * 15;
            int uw = m.ReadU8(TextTemplate + 8);
            int vh = m.ReadU8(TextTemplate + 0xA);
            m.WriteU8(p + 0x0C, (byte)u);
            m.WriteU8(p + 0x0D, (byte)v);
            m.WriteU8(p + 0x14, (byte)(u + uw));
            m.WriteU8(p + 0x15, (byte)v);
            m.WriteU8(p + 0x1C, (byte)u);
            m.WriteU8(p + 0x1D, (byte)(v + vh));
            m.WriteU8(p + 0x24, (byte)(u + uw));
            m.WriteU8(p + 0x25, (byte)(v + vh));

            Link(m, p, 10);
            step += 7;
        }
    }

    // func_80022B20. Returns the widened digit buffer (no 0xFF terminator).
    public static byte[] FormatNumber(int value, int width, bool zeroPad = false, int mode = 0)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        int w = width;
        switch (mode)
        {
            case 1: case 2: case 6: w += 1; break;
            case 3: case 5: w += 2; break;
            case 4: w += 3; break;
        }
        byte fill = zeroPad ? (byte)0 : (byte)10;
        var buf = new byte[w];
        for (int i = 0; i < w; i++) buf[i] = fill;

        switch (mode)
        {
            case 1: buf[0] = 0x13; break;
            case 2: buf[w - 1] = 0x0D; w--; break;
            case 3: buf[0] = 0x0F; buf[1] = 0x10; break;
            case 4: buf[0] = 0x0C; buf[1] = 0x12; buf[2] = 0x10; break;
            case 5: buf[0] = 0x0E; buf[1] = 0x11; break;
            case 6: buf[w - 1] = 0x0B; w--; break;
        }

        if (w > 0)
        {
            int v = value, i = w - 1;
            while (true)
            {
                buf[i] = (byte)(v % 10);
                v /= 10;
                if (v == 0) break;
                if (--i < 0) break;
            }
        }
        return buf;
    }

    // func_80021FCC. Same 7 px loop as DrawText; d < 11 reads the template's
    // u, d >= 11 reads it plus 7. 0xFF ends the buffer and draws nothing.
    public static void DrawDigits(IMemory m, int x, int y, byte[] digits)
    {
        int step = 0;
        foreach (byte d in digits)
        {
            if (d == 0xFF) break;
            if (step >= 168) break;
            uint p = NewQuad(m);
            int w = (short)m.ReadU16(NumberTemplate + 8);
            int h = (short)m.ReadU16(NumberTemplate + 0xA);
            int X = x + step;

            m.WriteU16(p + 0x08, (ushort)X);
            m.WriteU16(p + 0x0A, (ushort)y);
            m.WriteU16(p + 0x10, (ushort)(X + w));
            m.WriteU16(p + 0x12, (ushort)y);
            m.WriteU16(p + 0x18, (ushort)X);
            m.WriteU16(p + 0x1A, (ushort)(y + h));
            m.WriteU16(p + 0x20, (ushort)(X + w));
            m.WriteU16(p + 0x22, (ushort)(y + h));

            m.WriteU16(p + 0x16, m.ReadU16(NumberTemplate));
            m.WriteU16(p + 0x0E, m.ReadU16(NumberTemplate + 2));

            int u = m.ReadU8(NumberTemplate + 4);
            int v;
            if (d < 11) { v = d * 15; }
            else { u += 7; v = (d - 11) * 15; }
            int uw = m.ReadU8(NumberTemplate + 8);
            int vh = m.ReadU8(NumberTemplate + 0xA);
            m.WriteU8(p + 0x0C, (byte)u);
            m.WriteU8(p + 0x0D, (byte)v);
            m.WriteU8(p + 0x14, (byte)(u + uw));
            m.WriteU8(p + 0x15, (byte)v);
            m.WriteU8(p + 0x1C, (byte)u);
            m.WriteU8(p + 0x1D, (byte)(v + vh));
            m.WriteU8(p + 0x24, (byte)(u + uw));
            m.WriteU8(p + 0x25, (byte)(v + vh));

            Link(m, p, 10);
            step += 7;
        }
    }

    public static void DrawNumber(IMemory m, int x, int y, int value) =>
        DrawDigits(m, x, y, FormatNumber(value, 3));

    // func_800222B8. 3x3 nine-slice from the templates at WindowTemplates,
    // row-major. The middle row/column stretch by dw/dh plus the pad args.
    public static void DrawWindow(IMemory m, int x, int y, int w, int h, int padW = 1, int padH = 2)
    {
        int dw = w - 94, dh = h - 94;
        for (int r = 0; r < 3; r++)
        {
            uint row = WindowTemplates + (uint)(r * 3 * 12);
            int Y, H;
            if (r == 0) { Y = y; H = (short)m.ReadU16(row + 0xA); }
            else if (r == 1) { Y = y + 33; H = (short)m.ReadU16(row + 0xA) + dh + padH; }
            else { Y = y + 33 + 28 + dh; H = (short)m.ReadU16(row + 0xA); }
            for (int c = 0; c < 3; c++)
            {
                uint t = row + (uint)(c * 12);
                int X, W;
                if (c == 0) { X = x; W = (short)m.ReadU16(t + 8); }
                else if (c == 1) { X = x + 33; W = (short)m.ReadU16(t + 8) + dw + padW; }
                else { X = x + 33 + 28 + dw; W = (short)m.ReadU16(t + 8); }

                uint p = NewQuad(m);
                m.WriteU8(p + 4, 0xFF);
                m.WriteU8(p + 5, 0xFF);
                m.WriteU8(p + 6, 0xFF);
                m.WriteU8(p + 7, (byte)(m.ReadU8(p + 7) | 0x02));

                m.WriteU16(p + 0x08, (ushort)X);
                m.WriteU16(p + 0x0A, (ushort)Y);
                m.WriteU16(p + 0x10, (ushort)(X + W));
                m.WriteU16(p + 0x12, (ushort)Y);
                m.WriteU16(p + 0x18, (ushort)X);
                m.WriteU16(p + 0x1A, (ushort)(Y + H));
                m.WriteU16(p + 0x20, (ushort)(X + W));
                m.WriteU16(p + 0x22, (ushort)(Y + H));

                m.WriteU16(p + 0x16, m.ReadU16(t));
                m.WriteU16(p + 0x0E, m.ReadU16(t + 2));

                int u = m.ReadU8(t + 4), v = m.ReadU8(t + 6);
                int uw = m.ReadU8(t + 8), vh = m.ReadU8(t + 0xA);
                m.WriteU8(p + 0x0C, (byte)u);
                m.WriteU8(p + 0x0D, (byte)v);
                m.WriteU8(p + 0x14, (byte)(u + uw));
                m.WriteU8(p + 0x15, (byte)v);
                m.WriteU8(p + 0x1C, (byte)u);
                m.WriteU8(p + 0x1D, (byte)(v + vh));
                m.WriteU8(p + 0x24, (byte)(u + uw));
                m.WriteU8(p + 0x25, (byte)(v + vh));

                Link(m, p, 20);
            }
        }
    }

    /// <summary>
    /// The same three draws through the recompiled routines, through a faked
    /// stack frame below the caller's SP: the comparison for
    /// <c>KF2_GEARCOMPARE=verify</c>. The caller snapshots and restores the
    /// context around a pass.
    /// </summary>
    public static class Reference
    {
        // A record at sp+0x20: s16 x, s16 y, text. func_80022B20's digits go at
        // sp+0x24, which is that record's text, exactly as the status screen does.
        const uint Rec = 0x20;

        public static void Text(CpuContext c, IMemory m, int x, int y, string s)
        {
            uint rec = c.SP + Rec;
            m.WriteU16(rec, (ushort)x);
            m.WriteU16(rec + 2, (ushort)y);
            uint p = rec + 4;
            foreach (char ch in s)
                m.WriteU8(p++, ch == ' ' ? (byte)0x7F : (byte)(char.ToUpperInvariant(ch) - 'A'));
            m.WriteU8(p, 0xFF);
            c.A0 = TextTemplate;
            c.A1 = rec;
            KingsField2.func_80021E10(c, m);
        }

        public static void Number(CpuContext c, IMemory m, int x, int y, int value)
        {
            uint rec = c.SP + Rec;
            m.WriteU16(rec, (ushort)x);
            m.WriteU16(rec + 2, (ushort)y);
            m.WriteU32(c.SP + 0x10u, rec + 4);
            c.A0 = (uint)value;
            c.A1 = 3;
            c.A2 = 0;
            c.A3 = 0;
            KingsField2.func_80022B20(c, m);
            c.A0 = NumberTemplate;
            c.A1 = rec;
            KingsField2.func_80021FCC(c, m);
        }

        public static void Box(CpuContext c, IMemory m, int x, int y, int w, int h)
        {
            m.WriteU32(c.SP + 0x10u, 1);
            m.WriteU32(c.SP + 0x14u, 2);
            c.A0 = (uint)x;
            c.A1 = (uint)y;
            c.A2 = (uint)w;
            c.A3 = (uint)h;
            KingsField2.func_800222B8(c, m);
        }
    }
}
