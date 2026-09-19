using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// `Clip4FTP` and `Clip3FTP`, the view-space clipper `func_80030540` sends an oversized
/// polygon to: copy the vertices into 0x2C-byte records, `RotTrans` and bound each,
/// run `ClipFT`'s six Sutherland-Hodgman passes (`ZClipFT` makes each new vertex),
/// then `RotTransPers` whatever survived. Every load and store of RAM is the
/// recompiled routine's, in its order; the saved registers and the words each
/// routine keeps on its own stack are not. See "The clipper in C#" in
/// docs/PATCHES_AND_MODS.md.
/// </summary>
public static partial class PolyAssembler
{
    const uint Clip4 = 0x8005CAC8;
    const uint Clip3 = 0x8005CA48;

    // What ClipFT scales Y and X by when it finds where an edge crosses a side plane.
    const uint ClipYEdge = 0x800FC994;
    const uint ClipXEdge = 0x800FC984;

    static bool _queuedClip4, _queuedClip3;

    public static bool ClipperEnabled { get; set; } = true;

    static readonly Check _clip4Check = new("Clip4FTP", () => "");
    static readonly Check _clip3Check = new("Clip3FTP", () => "");

    static void ReplaceClip4(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (Recompiled(Enabled && ClipperEnabled) || m is not PSMemory mem) { orig(c, m); return; }
        if (_mode == Mode.Verify) Verify(_clip4Check, orig, c, mem, RunClip4);
        else RunClip4(c, mem);
    }

    static void ReplaceClip3(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (Recompiled(Enabled && ClipperEnabled) || m is not PSMemory mem) { orig(c, m); return; }
        if (_mode == Mode.Verify) Verify(_clip3Check, orig, c, mem, RunClip3);
        else RunClip3(c, mem);
    }

    /// <summary>a0-a3 the vertices, the stack the four UV pointers and the list.</summary>
    static void RunClip4(CpuContext c, PSMemory mem)
    {
        var saved = new Saved(c);
        uint sp = saved.SP;
        uint uv0 = mem.ReadU32(sp + 0x10u), uv1 = mem.ReadU32(sp + 0x14u);
        uint uv2 = mem.ReadU32(sp + 0x18u), uv3 = mem.ReadU32(sp + 0x1Cu);
        uint list = mem.ReadU32(sp + 0x20u);
        uint v0 = c.A0, v1 = c.A1, v2 = c.A2, v3 = c.A3;
        try
        {
            c.SP = sp - 0x48u;
            uint n = ClipRecords4(c, mem, v0, v1, v3, v2, uv0, uv1, uv3, uv2, list);
            Survivors(c, mem, n, list);
        }
        finally { saved.Restore(c); }
    }

    /// <summary>a0-a2 the vertices, a3 and the stack the three UV pointers, then the list.</summary>
    static void RunClip3(CpuContext c, PSMemory mem)
    {
        var saved = new Saved(c);
        uint sp = saved.SP;
        uint uv1 = mem.ReadU32(sp + 0x10u), uv2 = mem.ReadU32(sp + 0x14u);
        uint list = mem.ReadU32(sp + 0x18u);
        uint v0 = c.A0, v1 = c.A1, v2 = c.A2, uv0 = c.A3;
        try
        {
            c.SP = sp - 0x40u;
            uint n = ClipRecords3(c, mem, v0, v1, v2, uv0, uv1, uv2, list);
            Survivors(c, mem, n, list);
        }
        finally { saved.Restore(c); }
    }

    /// <summary>Clip4FT: the records in the order given (the caller passes a0, a1, a3, a2).</summary>
    static uint ClipRecords4(CpuContext c, PSMemory mem, uint a, uint b, uint d, uint e,
                             uint ua, uint ub, uint ud, uint ue, uint list)
    {
        Copy8(mem, a, mem.ReadU32(ClipRecords));
        Copy8(mem, b, mem.ReadU32(ClipRecords) + 0x2Cu);
        Copy8(mem, d, mem.ReadU32(ClipRecords) + 0x58u);
        Copy8(mem, e, mem.ReadU32(ClipRecords) + 0x84u);
        uint recs = mem.ReadU32(ClipRecords);
        mem.WriteU16(recs + 0x20u, mem.ReadU16(ua));
        mem.WriteU16(recs + 0x4Cu, mem.ReadU16(ub));
        mem.WriteU16(recs + 0x78u, mem.ReadU16(ud));
        mem.WriteU16(recs + 0xA4u, mem.ReadU16(ue));
        Bound(c, mem, 4, list);
        return ClipPasses(c, mem, 4u, list);
    }

    /// <summary>Clip3FT.</summary>
    static uint ClipRecords3(CpuContext c, PSMemory mem, uint a, uint b, uint d,
                             uint ua, uint ub, uint ud, uint list)
    {
        Copy8(mem, a, mem.ReadU32(ClipRecords));
        Copy8(mem, b, mem.ReadU32(ClipRecords) + 0x2Cu);
        Copy8(mem, d, mem.ReadU32(ClipRecords) + 0x58u);
        uint recs = mem.ReadU32(ClipRecords);
        mem.WriteU16(recs + 0x20u, mem.ReadU16(ua));
        mem.WriteU16(recs + 0x4Cu, mem.ReadU16(ub));
        mem.WriteU16(recs + 0x78u, mem.ReadU16(ud));
        Bound(c, mem, 3, list);
        return ClipPasses(c, mem, 3u, list);
    }

    /// <summary>RotTrans each record, its two side-plane bounds, and the first list.</summary>
    static void Bound(CpuContext c, PSMemory mem, int n, uint list)
    {
        for (int i = 0; i < n; i++)
        {
            Interrupts.Poll(c, mem);
            uint rec = mem.ReadU32(ClipRecords) + 0x2Cu * (uint)i;
            RotTrans(mem, rec);
            uint r = mem.ReadU32(ClipRecords) + 0x2Cu * (uint)i;
            uint kx = mem.ReadU32(ClipXScale);
            uint zx = mem.ReadU32(r + 0x10u);
            uint zy = mem.ReadU32(r + 0x10u);
            uint ky = mem.ReadU32(ClipYScale);
            mem.WriteU32(r + 0x24u, (uint)(Lo(zx, kx) >> 12));
            mem.WriteU32(r + 0x28u, (uint)(Lo(zy, ky) >> 12));
            mem.WriteU32(list + 4u * (uint)i, r);
        }
    }

    /// <summary>RotTrans into the record's +0x8; its flag goes to the caller's stack.</summary>
    static void RotTrans(PSMemory mem, uint rec)
    {
        Gte.Write(0, mem.ReadU32(rec));
        Gte.Write(1, mem.ReadU32(rec + 4u));
        Gte.MvmvaOp(12, false, 0, 0, 0);
        mem.WriteU32(rec + 0x08u, Gte.Read(25));
        mem.WriteU32(rec + 0x0Cu, Gte.Read(26));
        mem.WriteU32(rec + 0x10u, Gte.Read(27));
    }

    /// <summary>The low word of a signed multiply, as an int.</summary>
    static int Lo(uint a, uint b) => (int)(uint)((long)(int)a * (int)b);

    /// <summary>Clip4FTP/Clip3FTP after the clipper: RotTransPers each surviving record
    /// into its +0x18 (screen word) and +0x14 (depth cue), and return the count.</summary>
    static void Survivors(CpuContext c, PSMemory mem, uint n, uint list)
    {
        bool lighting = GteLightMap.Active;
        for (int i = 0; i < (int)n; i++)
        {
            Interrupts.Poll(c, mem);
            uint rec = mem.ReadU32(list + 4u * (uint)i);
            Gte.Write(0, mem.ReadU32(rec));
            Gte.Write(1, mem.ReadU32(rec + 4u));
            Gte.Rtps(12, false);
            uint sxy = Gte.Read(14);
            mem.WriteU32(rec + 0x18u, sxy);
            mem.WriteU32(rec + 0x14u, Gte.Read(8));
            if (lighting) NoteRecord(mem, rec, sxy, Gte.Read(8));
        }
        c.V0 = n;
    }

    // ---- ClipFT -------------------------------------------------------------

    /// <summary>Six passes between the list and the one 0x28 past it: far, near, then
    /// the Y and X side planes. New vertices are records appended after the n given.</summary>
    static uint ClipPasses(CpuContext c, PSMemory mem, uint n, uint list)
    {
        uint other = list + 0x28u;
        uint made = n;
        n = DepthPass(c, mem, list, other, n, ref made, ClipFar, far: true);
        n = DepthPass(c, mem, other, list, n, ref made, ClipNear, far: false);
        n = LowPass(c, mem, list, other, n, ref made, 0x0Cu, 0x28u, ClipYEdge);
        n = HighPass(c, mem, other, list, n, ref made, 0x0Cu, 0x28u, ClipYEdge);
        n = LowPass(c, mem, list, other, n, ref made, 0x08u, 0x24u, ClipXEdge);
        n = HighPass(c, mem, other, list, n, ref made, 0x08u, 0x24u, ClipXEdge);
        return n;
    }

    /// <summary>Z against the far plane (inside below it) or the near one (inside at or
    /// above it). The edge crossing is at (plane - z_out) / (z_in - z_out).</summary>
    static uint DepthPass(CpuContext c, PSMemory mem, uint src, uint dst, uint n, ref uint made, uint plane, bool far)
    {
        if ((int)n <= 0) return 0;
        uint k = 0;
        uint rec = made * 0x2Cu;
        for (uint i = 0; (int)i < (int)n; i++)
        {
            Interrupts.Poll(c, mem);
            uint t0 = mem.ReadU32(src + 4u * i);
            uint t1 = i + 1 == n ? mem.ReadU32(src) : mem.ReadU32(src + 4u * i + 4u);
            uint z0 = mem.ReadU32(t0 + 0x10u);
            uint lim = mem.ReadU32(plane);
            uint vout, vin, num, den, recs;
            if (((int)z0 < (int)lim) == far)
            {
                mem.WriteU32(dst + 4u * k, t0);
                uint z1 = mem.ReadU32(t1 + 0x10u);
                k++;
                if (((int)z1 < (int)lim) == far) continue;
                uint zin = mem.ReadU32(t0 + 0x10u);
                recs = mem.ReadU32(ClipRecords);
                num = lim - z1;
                den = zin - z1;
                vout = t1;
                vin = t0;
            }
            else
            {
                uint z1 = mem.ReadU32(t1 + 0x10u);
                if (((int)z1 < (int)lim) != far) continue;
                den = z1 - z0;
                recs = mem.ReadU32(ClipRecords);
                num = lim - z0;
                vout = t0;
                vin = t1;
            }
            Crossing(c, mem, vout, vin, rec + recs, num, den);
            mem.WriteU32(dst + 4u * k, rec + mem.ReadU32(ClipRecords));
            made++;
            k++;
            rec += 0x2Cu;
        }
        return k;
    }

    /// <summary>A side plane at -bound: inside where coord >= -bound. The crossing is
    /// found on the plane's own line, (s*coord >> 12) + z.</summary>
    static uint LowPass(CpuContext c, PSMemory mem, uint src, uint dst, uint n, ref uint made,
                        uint co, uint bo, uint scale)
    {
        if ((int)n <= 0) return 0;
        uint k = 0;
        uint rec = made * 0x2Cu;
        for (uint i = 0; (int)i < (int)n; i++)
        {
            Interrupts.Poll(c, mem);
            uint t0 = mem.ReadU32(src + 4u * i);
            uint t1 = i + 1 == n ? mem.ReadU32(src) : mem.ReadU32(src + 4u * i + 4u);
            uint b0 = mem.ReadU32(t0 + bo);
            uint c0 = mem.ReadU32(t0 + co);
            uint vout, vin, num, recs;
            int loIn;
            if ((int)c0 >= (int)(0u - b0))
            {
                mem.WriteU32(dst + 4u * k, t0);
                k++;
                uint b1 = mem.ReadU32(t1 + bo);
                uint c1 = mem.ReadU32(t1 + co);
                if ((int)c1 >= (int)(0u - b1)) continue;
                uint s = mem.ReadU32(scale);
                int loOut = Lo(s, c1);
                loIn = Lo(s, mem.ReadU32(t0 + co));
                recs = mem.ReadU32(ClipRecords);
                num = (uint)(loOut >> 12) + mem.ReadU32(t1 + 0x10u);
                vout = t1;
                vin = t0;
            }
            else
            {
                uint b1 = mem.ReadU32(t1 + bo);
                uint c1 = mem.ReadU32(t1 + co);
                if ((int)c1 < (int)(0u - b1)) continue;
                uint s = mem.ReadU32(scale);
                int loOut = Lo(s, c0);
                loIn = Lo(s, c1);
                uint zout = mem.ReadU32(t0 + 0x10u);
                recs = mem.ReadU32(ClipRecords);
                num = (uint)(loOut >> 12) + zout;
                vout = t0;
                vin = t1;
            }
            uint den = num - ((uint)(loIn >> 12) + mem.ReadU32(vin + 0x10u));
            Crossing(c, mem, vout, vin, rec + recs, num, den);
            mem.WriteU32(dst + 4u * k, rec + mem.ReadU32(ClipRecords));
            made++;
            k++;
            rec += 0x2Cu;
        }
        return k;
    }

    /// <summary>A side plane at +bound: inside where bound >= coord. The crossing is
    /// found on z - (s*coord >> 12).</summary>
    static uint HighPass(CpuContext c, PSMemory mem, uint src, uint dst, uint n, ref uint made,
                         uint co, uint bo, uint scale)
    {
        if ((int)n <= 0) return 0;
        uint k = 0;
        uint rec = made * 0x2Cu;
        for (uint i = 0; (int)i < (int)n; i++)
        {
            Interrupts.Poll(c, mem);
            uint t0 = mem.ReadU32(src + 4u * i);
            uint t1 = i + 1 == n ? mem.ReadU32(src) : mem.ReadU32(src + 4u * i + 4u);
            uint c0 = mem.ReadU32(t0 + co);
            uint b0 = mem.ReadU32(t0 + bo);
            uint vout, vin, num, recs;
            int loIn;
            if ((int)b0 >= (int)c0)
            {
                mem.WriteU32(dst + 4u * k, t0);
                uint c1 = mem.ReadU32(t1 + co);
                uint b1 = mem.ReadU32(t1 + bo);
                k++;
                if ((int)b1 >= (int)c1) continue;
                uint s = mem.ReadU32(scale);
                int loOut = Lo(s, c1);
                loIn = Lo(s, mem.ReadU32(t0 + co));
                recs = mem.ReadU32(ClipRecords);
                num = mem.ReadU32(t1 + 0x10u) - (uint)(loOut >> 12);
                vout = t1;
                vin = t0;
            }
            else
            {
                uint c1 = mem.ReadU32(t1 + co);
                uint b1 = mem.ReadU32(t1 + bo);
                if ((int)b1 < (int)c1) continue;
                uint s = mem.ReadU32(scale);
                int loOut = Lo(s, c0);
                loIn = Lo(s, c1);
                uint zout = mem.ReadU32(t0 + 0x10u);
                recs = mem.ReadU32(ClipRecords);
                num = zout - (uint)(loOut >> 12);
                vout = t0;
                vin = t1;
            }
            uint den = num - (mem.ReadU32(vin + 0x10u) - (uint)(loIn >> 12));
            Crossing(c, mem, vout, vin, rec + recs, num, den);
            mem.WriteU32(dst + 4u * k, rec + mem.ReadU32(ClipRecords));
            made++;
            k++;
            rec += 0x2Cu;
        }
        return k;
    }

    /// <summary>ZClipFT: the new record at t = (num << 12) / den along out -> in. The
    /// SVECTOR and the UV are blended by the GTE (LoadAverageShort12, LoadAverageByte),
    /// then transformed and bounded as the originals were.</summary>
    static void Crossing(CpuContext c, PSMemory mem, uint vout, uint vin, uint rec, uint num, uint den)
    {
        uint t = 0;
        if (den != 0)
        {
            int s = (int)(num << 12), d = (int)den;
            t = s == int.MinValue && d == -1 ? 0x80000000u : (uint)(s / d);
        }
        uint w = 0x1000u - t;

        uint a0 = mem.ReadU32(vout), a2 = mem.ReadU32(vout + 4u);
        Gte.Write(8, w);
        Gte.Write(9, a0 & 0xFFFFu);
        Gte.Write(10, (uint)((int)a0 >> 16));
        Gte.Write(11, a2 & 0xFFFFu);
        Gte.Gpf(12, false);
        uint b0 = mem.ReadU32(vin), b2 = mem.ReadU32(vin + 4u);
        Gte.Write(8, t);
        Gte.Write(9, b0 & 0xFFFFu);
        Gte.Write(10, (uint)((int)b0 >> 16));
        Gte.Write(11, b2 & 0xFFFFu);
        Gte.Gpl(12, false);
        uint o0 = (Gte.Read(9) & 0xFFFFu) | (Gte.Read(10) << 16);
        uint o2 = Gte.Read(11);
        mem.WriteU32(rec, o0);
        mem.WriteU32(rec + 4u, o2);

        uint ua = mem.ReadU8(vout + 0x20u), va = mem.ReadU8(vout + 0x21u);
        Gte.Write(8, w);
        Gte.Write(9, ua);
        Gte.Write(10, va);
        Gte.Gpf(12, false);
        uint ub = mem.ReadU8(vin + 0x20u), vb = mem.ReadU8(vin + 0x21u);
        Gte.Write(8, t);
        Gte.Write(9, ub);
        Gte.Write(10, vb);
        Gte.Gpl(12, false);
        uint ou = Gte.Read(9), ov = Gte.Read(10);
        mem.WriteU8(rec + 0x20u, (byte)ou);
        mem.WriteU8(rec + 0x21u, (byte)ov);

        RotTrans(mem, rec);
        uint zx = mem.ReadU32(rec + 0x10u);
        uint kx = mem.ReadU32(ClipXScale);
        uint zy = mem.ReadU32(rec + 0x10u);
        uint ky = mem.ReadU32(ClipYScale);
        mem.WriteU32(rec + 0x24u, (uint)(Lo(zx, kx) >> 12));
        mem.WriteU32(rec + 0x28u, (uint)(Lo(zy, ky) >> 12));
    }
}

// ---- Culling on the whole polygon ---------------------------------------------

public static partial class PolyAssembler
{
    /// <summary>KF2_POLYASM_FACING=0 culls on the first three corners, as the game does.</summary>
    public static bool WholeFacing { get; set; } = true;

    /// <summary>Clipped polygons and quads the first three corners would have culled
    /// while the whole polygon faces the camera; kept unless KF2_POLYASM_FACING=0.
    /// Never reset.</summary>
    public static long FansTurned, QuadsKept;

    /// <summary>func_800302E8 culls a clipped polygon on NormalClip of its first three
    /// corners, and both assemblers cull a quad on its first triangle. A subdivided
    /// quad of a parent with one short edge starts on an almost straight corner, which
    /// reads as facing away while the polygon faces the camera: a gap in the floor.
    /// When the whole polygon faces, start the list at the corner that faces most; the
    /// fan still covers the same convex polygon. See "A floor quarter missing at a
    /// short edge" in docs/RENDERING.md.</summary>
    static void FaceFan(PSMemory mem, uint n)
    {
        if (n > 10u) return;
        Span<uint> rec = stackalloc uint[10];
        Span<int> x = stackalloc int[10], y = stackalloc int[10];
        int count = (int)n;
        for (int i = 0; i < count; i++)
        {
            rec[i] = Peek32(mem, ClipOut + 4u * (uint)i);
            uint w = Peek32(mem, rec[i] + 0x18u);
            x[i] = (short)w;
            y[i] = (short)(w >> 16);
        }
        if (Corner(x, y, count, 0) > 0) return;

        long area = 0;
        for (int i = 0; i < count; i++)
        {
            int j = i + 1 == count ? 0 : i + 1;
            area += (long)x[i] * y[j] - (long)x[j] * y[i];
        }
        if (area <= 0) return;

        int best = 0;
        long most = 0;
        for (int k = 1; k < count; k++)
        {
            long v = Corner(x, y, count, k);
            if (v > most) { most = v; best = k; }
        }
        if (best == 0) return;

        FansTurned++;
        if (!WholeFacing) return;
        for (int i = 0; i < count; i++)
            mem.WriteU32(ClipOut + 4u * (uint)i, rec[(i + best) % count]);
    }

    /// <summary>NormalClip of corners k, k+1, k+2.</summary>
    static long Corner(Span<int> x, Span<int> y, int n, int k)
    {
        int a = k, b = (k + 1) % n, d = (k + 2) % n;
        return (long)(x[b] - x[a]) * (y[d] - y[a]) - (long)(x[d] - x[a]) * (y[b] - y[a]);
    }

    /// <summary>A quad the first triangle culled: keep it when its second triangle
    /// (1, 3, 2) outweighs the first, which is the whole quad facing.</summary>
    static bool QuadFaces(PSMemory mem, uint p0, uint p1, uint p2, uint p3)
    {
        if (_mode == Mode.Verify) return false;
        uint w0 = Peek32(mem, p0), w1 = Peek32(mem, p1), w2 = Peek32(mem, p2), w3 = Peek32(mem, p3);
        if (Nc(w0, w1, w2) + Nc(w1, w3, w2) <= 0) return false;
        QuadsKept++;
        return WholeFacing;
    }

    static long Nc(uint a, uint b, uint d)
        => (long)((short)b - (short)a) * ((short)(d >> 16) - (short)(a >> 16))
         - (long)((short)d - (short)a) * ((short)(b >> 16) - (short)(a >> 16));
}
