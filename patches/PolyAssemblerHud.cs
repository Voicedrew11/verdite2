using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// `func_8002E910`, the HUD's vertex transform, in C#: `RotTrans` per vertex into the
/// vertex cache, with the fraction the GTE's shift drops handed to the vertex map.
/// See "The HUD's transform in C#" in docs/PATCHES_AND_MODS.md.
/// </summary>
public static partial class PolyAssembler
{
    const uint HudTransform = 0x8002E910;

    static bool _queuedHudTransform;

    /// <summary>Screen-space vertices offered to the vertex map, and those with a
    /// fraction to offer; <c>KF2_SUBPIXEL_PROBE</c> reads and clears them.</summary>
    public static long ScreenVertices, ScreenFractional, ScreenAligned;

    static void ReplaceHudTransform(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (Recompiled(Enabled && TransformEnabled) || m is not PSMemory mem) { orig(c, m); return; }
        if (_mode == Mode.Verify) Verify(_hudTransformCheck, orig, c, mem, RunHudTransform);
        else RunHudTransform(c, mem);
    }

    static readonly Check _hudTransformCheck = new("func_8002E910", () => "");

    static void RunHudTransform(CpuContext c, PSMemory mem)
    {
        var saved = new Saved(c);
        TransformCalls++;
        try { HudTransformBody(c, mem, saved.SP - 0x48u); }
        finally { saved.Restore(c); }
    }

    /// <summary>
    /// Each of a0 vertices from *VertexBase, rotated and translated with no divide:
    /// the HUD is orthographic, and the result is its screen position. The cache entry
    /// is the X and Y halves of the result, <c>Z &gt;&gt; 2</c> and Z. The routine
    /// stores X and Y as two halfwords; here they are one word, the same bytes, so the
    /// vertex map can bind the fraction to it. RotTrans's VECTOR and flag go to the
    /// stack and are read back only for the cache, so they stay in locals.
    /// </summary>
    static void HudTransformBody(CpuContext c, PSMemory mem, uint sp)
    {
        c.SP = sp;
        uint src = mem.ReadU32(VertexBase);
        uint dst = VertexCache;
        bool publish = GteVertexMap.Active;
        bool turned = publish && Turned();

        var fr = new Frame(mem);
        for (uint n = c.A0; n != 0; n--)
        {
            Interrupts.Poll(c, mem);
            fr.Check();
            uint v01 = mem.ReadU32(src), v2 = mem.ReadU32(src + 4u);
            Gte.Write(0, v01);
            Gte.Write(1, v2);
            Gte.MvmvaOp(12, false, 0, 0, 0);
            int x = (int)Gte.Read(25), y = (int)Gte.Read(26), z = (int)Gte.Read(27);

            uint xy = (ushort)x | (uint)(ushort)y << 16;
            if (publish) PublishScreenVertex(xy, x, y, (short)v01, (short)(v01 >> 16), (short)v2, turned);
            mem.WriteU32(dst, xy);
            W16(ref fr, dst + 4u, (ushort)(z >> 2));
            W16(ref fr, dst + 6u, (ushort)z);
            src += 8u;
            dst += 8u;
        }
    }

    /// <summary>
    /// Offer a screen-space vertex's fraction for the store that follows: the part
    /// of <c>(TR &lt;&lt; 12) + R·V</c> below the GTE's shift of 12. Published with no
    /// depth, which is what says it was placed on the screen rather than projected: it
    /// takes no W and no depth, and the polygon stays 2D to the passes that ask
    /// (<c>HleVertex.Projected</c>). A product that disagrees with the GTE's integer
    /// (an overflow the GTE saturated) is offered with no fraction.
    ///
    /// A piece that is not <paramref name="turned"/> is offered no fraction either:
    /// its art was drawn for the whole-pixel snap. The gauges are lit tubes 2.5
    /// pixels tall whose dark edge faces are slivers of 0.1-0.3 of a pixel, and the
    /// snap is what gives each of them the full row the console shows.
    /// </summary>
    static void PublishScreenVertex(uint xy, int x, int y, short vx, short vy, short vz, bool turned)
    {
        if (!turned)
        {
            GteVertexMap.Publish(xy, 0f, 0f, 0f, false);
            ScreenVertices++;
            ScreenAligned++;
            return;
        }

        // Rows 1 and 2 of R: control registers 0-2, two s16 elements a word.
        uint r0 = Gte.ReadControl(0), r1 = Gte.ReadControl(1), r2 = Gte.ReadControl(2);
        long sx = ((long)(int)Gte.ReadControl(5) << 12) + (short)r0 * vx + (short)(r0 >> 16) * vy + (short)r1 * vz;
        long sy = ((long)(int)Gte.ReadControl(6) << 12) + (short)(r1 >> 16) * vx + (short)r2 * vy + (short)(r2 >> 16) * vz;
        float fx = (sx >> 12) == x ? (sx & 0xFFF) / 4096f : 0f;
        float fy = (sy >> 12) == y ? (sy & 0xFFF) / 4096f : 0f;
        GteVertexMap.Publish(xy, 0f, fx, fy, false);
        ScreenVertices++;
        if (fx != 0f || fy != 0f) ScreenFractional++;
    }

    /// <summary>Whether the rotation turns a piece in the screen plane: any element
    /// of R's first two rows off the diagonal. A scale alone does not.</summary>
    static bool Turned()
    {
        uint r0 = Gte.ReadControl(0), r1 = Gte.ReadControl(1), r2 = Gte.ReadControl(2);
        return (r0 >> 16) != 0 || r1 != 0 || (r2 >> 16) != 0;
    }
}
