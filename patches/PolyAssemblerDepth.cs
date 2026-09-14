using System.Runtime.CompilerServices;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// Each packet's corner depths, recorded for the depth buffer (<see cref="ZBuffer"/>,
/// <c>GtePacketDepth</c>). Reads only, like the lighting records. The HUD's icons and
/// the first-person arm are not recorded, so they keep painter's order.
/// See "The assemblers write the depth" in docs/RENDERING.md.
/// </summary>
public static partial class PolyAssembler
{
    // SZ3 per vertex-cache slot, with the two cache words it was written beside.
    struct CacheDepth
    {
        public uint W0, W1;
        public float Z;
    }

    static readonly CacheDepth[] _cacheDepth = new CacheDepth[CacheSlots];

    /// <summary>Set while the first-person arm draws.</summary>
    public static bool InArm;

    /// <summary>Clipped fans whose packet did not carry its records' screen words.</summary>
    public static long DepthClipMismatches;

    static bool DepthOn() => GtePacketDepth.Active && !InHud && !InArm;

    /// <summary>The transforms, after both cache words are written.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void NoteDepth(PSMemory mem, uint dst, uint sxy)
    {
        uint i = (dst - VertexCache) >> 3;
        if (i >= CacheSlots) return;
        ref var e = ref _cacheDepth[i];
        e.W0 = sxy;
        e.W1 = Peek32(mem, dst + 4u);
        e.Z = (int)Gte.Read(19);
    }

    /// <summary>A cached vertex's depth, if the cache still holds what our transform wrote.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float CacheZ(PSMemory mem, uint p)
    {
        uint i = (p - VertexCache) >> 3;
        if (i >= CacheSlots) return 0f;
        ref var e = ref _cacheDepth[i];
        return e.W0 == Peek32(mem, p) && e.W1 == Peek32(mem, p + 4u) ? e.Z : 0f;
    }

    /// <summary>A packet built from cached vertices; <paramref name="last"/> is the
    /// offset of its last vertex word.</summary>
    static void DepthFace(PSMemory mem, uint pkt, uint last, int n, uint p0, uint p1, uint p2, uint p3)
    {
        ref var r = ref GtePacketDepth.Slot(pkt);
        r.Z0 = CacheZ(mem, p0);
        r.Z1 = CacheZ(mem, p1);
        r.Z2 = CacheZ(mem, p2);
        r.Z3 = n == 4 ? CacheZ(mem, p3) : 0f;
        if (r.Z0 <= 0f || r.Z1 <= 0f || r.Z2 <= 0f || (n == 4 && r.Z3 <= 0f)) { r.Cmd = 0; return; }
        SealDepth(mem, ref r, pkt, last);
    }

    /// <summary>A finished packet: recorded, or its address's old record dropped.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void RecordDepth(ref Frame fr, uint pkt, uint last, int n, uint p0, uint p1, uint p2, uint p3)
    {
        if (fr.Depth) DepthFace(fr.Mem, pkt, last, n, p0, p1, p2, p3);
        else if (fr.DepthTable) NoDepth(pkt);
    }

    /// <summary>Drop whatever record a packet at this address had.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void NoDepth(uint pkt) => GtePacketDepth.Slot(pkt).Cmd = 0;

    static void SealDepth(PSMemory mem, ref GtePacketDepth.Rec r, uint pkt, uint last)
    {
        r.Cmd = Peek32(mem, pkt + 4u);
        r.Xy0 = Peek32(mem, pkt + 8u);
        r.XyLast = Peek32(mem, pkt + last);
        GtePacketDepth.Recorded++;
    }

    /// <summary>After func_800302E8 and anything that rewrote its colours: packet k is
    /// records 0, k+1 and k+2, each at its view-space Z (+0x10), which is the SZ3 the
    /// clipper projected it with.</summary>
    static void DepthClipped(PSMemory mem, uint before, bool record)
    {
        uint count = ClippedCount(mem, before);
        if (count == 0) return;

        uint r0 = Peek32(mem, ClipOut);
        for (uint k = 0; k < count; k++)
        {
            uint pkt = before + k * 0x28u;
            if (!record) { NoDepth(pkt); continue; }
            uint ra = Peek32(mem, ClipOut + 4u * (k + 1)), rb = Peek32(mem, ClipOut + 4u * (k + 2));
            ref var r = ref GtePacketDepth.Slot(pkt);
            if (Peek32(mem, pkt + 0x08u) != Peek32(mem, r0 + 0x18u) || Peek32(mem, pkt + 0x14u) != Peek32(mem, ra + 0x18u)
                || Peek32(mem, pkt + 0x20u) != Peek32(mem, rb + 0x18u))
            {
                DepthClipMismatches++;
                r.Cmd = 0;
                continue;
            }
            r.Z0 = (int)Peek32(mem, r0 + 0x10u);
            r.Z1 = (int)Peek32(mem, ra + 0x10u);
            r.Z2 = (int)Peek32(mem, rb + 0x10u);
            if (r.Z0 <= 0f || r.Z1 <= 0f || r.Z2 <= 0f) { r.Cmd = 0; continue; }
            SealDepth(mem, ref r, pkt, 0x20u);
        }
    }
}
