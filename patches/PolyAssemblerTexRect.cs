using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// A clipped fan's texture rectangle is its face's, not the part of the face's UVs
/// that survived the clip (<c>GteTexRect</c>). The texture filters stay inside it,
/// and the mip atlas keys on it, so a fan filters as its unclipped neighbours do.
/// See "Anisotropic filtering" in docs/RENDERING.md.
/// </summary>
public static partial class PolyAssembler
{
    /// <summary>After func_800302E8: every packet of the fan gets the face's UV
    /// bounds, read from the face record's corners at +0, +4, +8 (+0xC).</summary>
    static void TexRectClipped(PSMemory mem, uint before, uint f, int corners)
    {
        uint count = ClippedCount(mem, before);
        if (count == 0) return;

        int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
        for (uint i = 0; i < (uint)corners; i++)
        {
            uint uv = Peek32(mem, f + 4u * i);
            int u = (int)(uv & 0xFF), v = (int)((uv >> 8) & 0xFF);
            u0 = Math.Min(u0, u); u1 = Math.Max(u1, u);
            v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
        }
        uint rect = (uint)u0 | (uint)v0 << 8 | (uint)u1 << 16 | (uint)v1 << 24;

        for (uint k = 0; k < count; k++)
        {
            uint pkt = before + k * 0x28u;
            ref var r = ref GteTexRect.Slot(pkt);
            r.Cmd = Peek32(mem, pkt + 4u);
            r.Xy0 = Peek32(mem, pkt + 8u);
            r.XyLast = Peek32(mem, pkt + 0x20u);
            r.Rect = rect;
            GteTexRect.Recorded++;
        }
    }
}
