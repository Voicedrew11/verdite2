using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Shortcuts;

/// <summary>
/// The HP panel's frame, drawn at another size: HUD record 13's model (a 2000 x 1000
/// frame, a 128-unit bevel sunk 512 deep round two translucent fill quads), its light
/// record, scale and depth, through the same calls the HUD builder func_80031D5C makes
/// for it. The model's own vertices are moved, not scaled: anything within the bevel of
/// an edge keeps its distance from that edge, the rest is spread across, so a card has
/// the panel's bevel at any size, or one narrower by whole pixels, every vertex on a
/// whole pixel so the bevel is the same on both sides.
///
/// The moved vertices go on the stack, and the vertex base func_8002E910 reads is
/// pointed at them after func_8002E1F0 sets it; the model in the bank is never written.
///
/// Game thread, inside the HUD builder's post-hook.
/// </summary>
static class CardFrame
{
    /// <summary>The HUD record of the HP panel's frame.</summary>
    internal const int PanelRecord = 13;

    const uint LightRecords = 0x801930F0, LightBytes = 0x68;
    const uint CurrentModel = 0x8018E19C, VertexBase = 0x8018EAA0;
    const uint FakeRa = 0x8001FFF4;
    const int MaxVertices = 24;

    static uint Record => Stage13.HudRecords + PanelRecord * Stage13.HudStride;

    /// <summary>The bevel last drawn in whole pixels, across and down; (0, 0) until a
    /// frame has been drawn.</summary>
    internal static (int X, int Y) Bevel { get; private set; }

    /// <summary>The frame filling <paramref name="w"/> x <paramref name="h"/> pixels with
    /// its top left at (<paramref name="x"/>, <paramref name="y"/>), in the coordinates the
    /// HUD's packets are drawn in, its bevel <paramref name="bevelLess"/> pixels a side
    /// narrower than the panel's as drawn (5 across, 6 down).</summary>
    internal static void Draw(CpuContext c, IMemory m, int x, int y, int w, int h, int bevelLess)
    {
        uint rec = Record;
        int sx = (short)m.ReadU16(rec + 0x8), sy = (short)m.ReadU16(rec + 0xA), sz = (short)m.ReadU16(rec + 0xC);
        if (sx <= 0 || sy <= 0) return;
        uint light = LightRecords + m.ReadU8(rec + 0x2) * LightBytes;

        uint savedModel = m.ReadU32(CurrentModel), savedVerts = m.ReadU32(VertexBase);
        var snap = c.Snapshot();
        try
        {
            c.SP = (c.SP - 0x200) & ~0xFu;
            uint matrix = c.SP + 0x40, verts = c.SP + 0x80;

            Call(c, m, KingsField2.func_80034834, m.ReadU16(rec + 0x4));
            uint obj = Call(c, m, KingsField2.func_8002E1BC, 0);
            uint count = m.ReadU32(obj + 4);
            if (count == 0 || count > MaxVertices) return;
            if (!Place(m, obj + m.ReadU32(obj), count, verts, w, h, bevelLess, sx, sy)) return;

            WriteMatrix(m, matrix, sx, sy, sz, x, y, (short)m.ReadU16(rec + 0x14));
            Call(c, m, KingsField2.func_8002DDDC, (uint)(short)m.ReadU16(light + 0x66));
            Call(c, m, KingsField2.SetBackColor_game, m.ReadU8(light + 0x62), m.ReadU8(light + 0x63), m.ReadU8(light + 0x64));
            Call(c, m, KingsField2.SetColorMatrix, light + 0x50);
            Call(c, m, KingsField2.SetLightMatrix, light);   // the builder's MulMatrix0 with an unturned record
            Call(c, m, KingsField2.SetRotMatrix, matrix);
            Call(c, m, KingsField2.SetTransMatrix, matrix);

            Call(c, m, KingsField2.func_8002E1F0, 0);
            m.WriteU32(VertexBase, verts);
            Call(c, m, KingsField2.func_8002E910, count);
            Call(c, m, KingsField2.func_8002F214, 0, 0);
        }
        finally
        {
            c.Restore(snap);
            m.WriteU32(CurrentModel, savedModel);
            m.WriteU32(VertexBase, savedVerts);
        }
    }

    /// <summary>Writes the model's vertices moved to a frame <paramref name="w"/> x
    /// <paramref name="h"/> pixels into <paramref name="dst"/>, its bevel the panel's as
    /// drawn less <paramref name="less"/> pixels. Every vertex lands on a whole pixel:
    /// the transform drops a fraction (the HUD publishes none for an unturned piece), so
    /// a bevel of 3.56 pixels was 3 on the left and 4 on the right. False if the model
    /// is not the frame shape (corners at 0, a bevel inside).</summary>
    static bool Place(IMemory m, uint src, uint count, uint dst, int w, int h, int less, int sx, int sy)
    {
        int fullX = 0, fullY = 0, bevelX = int.MaxValue, bevelY = int.MaxValue;
        for (uint i = 0; i < count; i++)
        {
            int vx = (short)m.ReadU16(src + i * 8), vy = (short)m.ReadU16(src + i * 8 + 2);
            if (vx < 0 || vy < 0) return false;
            fullX = Math.Max(fullX, vx);
            fullY = Math.Max(fullY, vy);
            if (vx > 0) bevelX = Math.Min(bevelX, vx);
            if (vy > 0) bevelY = Math.Min(bevelY, vy);
        }
        if (fullX == 0 || fullY == 0 || 2 * bevelX >= fullX || 2 * bevelY >= fullY) return false;
        int toX = Math.Max(1, bevelX * sx / 4096 - less), toY = Math.Max(1, bevelY * sy / 4096 - less);
        if (w <= 2 * toX || h <= 2 * toY) return false;
        Bevel = (toX, toY);

        for (uint i = 0; i < count; i++)
        {
            uint s = src + i * 8, d = dst + i * 8;
            m.WriteU16(d, (ushort)Units(Move((short)m.ReadU16(s), fullX, bevelX, toX, w), sx));
            m.WriteU16(d + 2, (ushort)Units(Move((short)m.ReadU16(s + 2), fullY, bevelY, toY, h), sy));
            m.WriteU16(d + 4, m.ReadU16(s + 4));
            m.WriteU16(d + 6, m.ReadU16(s + 6));
        }
        return true;
    }

    /// <summary>Model units that come to exactly <paramref name="pixels"/> at
    /// <paramref name="scale"/> once the transform drops the fraction: the fewest that
    /// reach it, never a whole pixel past (a scale below 4096 adds less than one).</summary>
    static int Units(int pixels, int scale) => (pixels * 4096 + scale - 1) / scale;

    /// <summary>One coordinate of a frame <paramref name="full"/> units long with a bevel
    /// <paramref name="bevel"/> deep, as a whole pixel of one <paramref name="target"/>
    /// pixels long with a bevel <paramref name="to"/> deep.</summary>
    static int Move(int v, int full, int bevel, int to, int target)
    {
        if (v <= bevel) return (int)Math.Round((double)v * to / bevel);
        if (v >= full - bevel) return target - (int)Math.Round((double)(full - v) * to / bevel);
        return (int)Math.Round((double)v * target / full);
    }

    /// <summary>A PSY-Q MATRIX scaled by (sx, sy, sz) and unturned, as RotMatrix of the
    /// record's zero angles and ScaleMatrix make it, translated to (tx, ty, tz).</summary>
    static void WriteMatrix(IMemory m, uint at, int sx, int sy, int sz, int tx, int ty, int tz)
    {
        int[] r = [sx, 0, 0, 0, sy, 0, 0, 0, sz];
        for (uint i = 0; i < 9; i++) m.WriteU16(at + i * 2, (ushort)(short)r[i]);
        m.WriteU16(at + 18, 0);
        m.WriteU32(at + 0x14, (uint)tx);
        m.WriteU32(at + 0x18, (uint)ty);
        m.WriteU32(at + 0x1C, (uint)tz);
    }

    static uint Call(CpuContext c, IMemory m, Action<CpuContext, IMemory> f, uint a0 = 0, uint a1 = 0, uint a2 = 0)
    {
        c.A0 = a0; c.A1 = a1; c.A2 = a2;
        c.RA = FakeRa;
        f(c, m);
        return c.V0;
    }
}
