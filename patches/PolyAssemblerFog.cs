using System.Runtime.CompilerServices;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// <see cref="EvenFog"/>'s two parts. Nothing here writes a GTE register.
/// </summary>
public static partial class PolyAssembler
{
    const uint TileBase = 0x801C8484;       // 80 x 80 tiles of two 5-byte halves
    const uint LightRecords = 0x801930F0;   // 0x68 a record; +0x66 the fog word

    // The tile half func_80031950 is drawing while its fog differs from a neighbour's,
    // else 0; its quarter turns, fog word and the eight around it, (dz+1)*3 + (dx+1),
    // Empty where that half has no model or is off the map.
    static uint _tile;
    static int _tileRot, _tileWord;
    static readonly int[] _nearWords = new int[9];
    const int Empty = int.MinValue;

    internal static void BeginTile(uint half, IMemory m)
    {
        _tile = 0;
        if (!EvenFog.Enabled || !EvenFog.Blend || _mode == Mode.Verify || m is not PSMemory mem) return;
        uint off = half - TileBase;
        if (off >= 80u * 800u) return;
        int tx = (int)(off / 10u % 80u), tz = (int)(off / 800u);
        uint h = off % 10u;
        _tileRot = (int)Peek32(mem, half + 2u) & 3;
        _tileWord = FogWord(mem, half);
        bool mixed = false;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int x = tx + dx, z = tz + dz, w = _tileWord;
            if ((dx | dz) != 0)
            {
                uint nb = TileBase + 800u * (uint)z + 10u * (uint)x + h;
                w = (uint)x < 80u && (uint)z < 80u && (byte)Peek32(mem, nb) < 240 ? FogWord(mem, nb) : Empty;
            }
            _nearWords[(dz + 1) * 3 + dx + 1] = w;
            mixed |= w != _tileWord && w != Empty;
        }
        if (mixed) _tile = half;
    }

    internal static void EndTile() => _tile = 0;

    static int FogWord(PSMemory mem, uint half) =>
        Peek16(mem, LightRecords + ((uint)Peek32(mem, half + 4u) & 0x3Fu) * 0x68u + 0x66u);

    /// <summary>The near transform's fog weight for a depth cue (NearTransformBody).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int NearFog(int p, bool far) => far ? 0 : p < 2800 ? p : ((p - 0xAF0) << 1) + p;

    /// <summary>A fog word's weight at a depth cue, on the near or the far transform's curve.</summary>
    static int CurveFog(int word, int ir0, bool near) =>
        word >= 32000 ? 0 : near || (word & 0x8000) == 0 ? NearFog(ir0, false) : Math.Max(ir0 - 0x320, 0) << 1;

    /// <summary>IR0 under a fog word: func_8002DDDC's SetFogNear(word/2, 200), then Rtp's depth cue.</summary>
    static int WordIr0(int word, uint quotient, int dqb)
    {
        short dqa = (short)(-(((word & 0x7FFF) >> 1) * 320) / 200);
        return Math.Clamp((int)(((long)quotient * dqa + dqb) >> 12), 0, 0x1000);
    }

    /// <summary>A tile vertex's offset from the tile's centre on the map's axes: the
    /// mesh is turned by quarter turns (func_80014B88).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void TileOffset(int vx, int vz, out int wx, out int wz)
    {
        (wx, wz) = _tileRot switch { 0 => (vx, vz), 1 => (vz, -vx), 2 => (-vx, -vz), _ => (-vz, vx) };
    }

    /// <summary>
    /// The fog weight at a vertex of the current tile, blended bilinearly between the
    /// centres of the four tiles around it: its own at the centre, half and half at an
    /// edge, a quarter each at a corner. An empty half has no record, so it drops out
    /// and the rest share its weight; every tile meeting at a point then blends the
    /// same set. False, and <paramref name="ownFog"/>, where every tile that weighs in
    /// has the tile's own word.
    /// </summary>
    static bool BlendFog(int ownFog, uint sz3, short vx, short vz, bool near, out int fog)
    {
        fog = ownFog;
        TileOffset(vx, vz, out int wx, out int wz);
        int sx = wx < 0 ? -1 : 1, sz = wz < 0 ? -1 : 1;
        // Mesh edges sit at +-1034, ten units past the tile's; both sides clamp to half.
        long ax = Math.Min(Math.Abs(wx), 1024) * 2, az = Math.Min(Math.Abs(wz), 1024) * 2;
        int wX = _nearWords[4 + sx], wZ = _nearWords[4 + 3 * sz], wD = _nearWords[4 + 3 * sz + sx];
        long kOwn = (4096 - ax) * (4096 - az);
        long kX = wX == Empty ? 0 : ax * (4096 - az);
        long kZ = wZ == Empty ? 0 : (4096 - ax) * az;
        long kD = wD == Empty ? 0 : ax * az;
        if ((kX == 0 || wX == _tileWord) && (kZ == 0 || wZ == _tileWord) && (kD == 0 || wD == _tileWord))
            return false;

        uint q = Gte.DepthQuotient(sz3);
        int dqb = (int)Gte.ReadControl(28);
        long total = kOwn + kX + kZ + kD;
        long sum = kOwn * ownFog + kX * Weight(wX) + kZ * Weight(wZ) + kD * Weight(wD);
        fog = (int)((sum + total / 2) / total);
        return true;

        int Weight(int w) => w == _tileWord || w == Empty ? ownFog : CurveFog(w, WordIr0(w, q, dqb), near);
    }

    /// <summary>A transform's vertex: its fog weight, blended when the tile is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TileVertexFog(PSMemory mem, uint src, int ownFog, bool near, out int fog)
    {
        fog = ownFog;
        return _tile != 0 && BlendFog(ownFog, Gte.Read(19), Peek16(mem, src), Peek16(mem, src + 4u), near, out fog);
    }

    /// <summary>A clipper record's fog weight on the tiles' curve, blended when the
    /// tile is. The record holds the interpolated local vertex at +0/+4 and its view
    /// Z at +0x10.</summary>
    static int RecordFogWeight(PSMemory mem, uint rec, bool far, out bool blended)
    {
        int fog = NearFog((int)Peek32(mem, rec + 0x14u), far);
        blended = _tile != 0 && BlendFog(fog, (uint)Math.Clamp((int)Peek32(mem, rec + 0x10u), 0, 0xFFFF),
                                         Peek16(mem, rec), Peek16(mem, rec + 4u), near: true, out fog);
        return fog;
    }

    /// <summary>DPCS at sf=12 lm=0 on the colour's low 24 bits, with no register
    /// touched; the high byte is left as it was.</summary>
    static uint DepthCue(uint word, uint colour, int ir0)
    {
        short w = (short)ir0;
        uint rgb = 0;
        for (int ch = 0; ch < 3; ch++)
        {
            long c = (long)((colour >> (8 * ch)) & 0xFF) << 16;
            int d = Math.Clamp((int)((((long)(int)Gte.ReadControl(21 + ch) << 12) - c) >> 12), -0x8000, 0x7FFF);
            int mac = (int)((d * (long)w + c) >> 12);
            rgb |= (uint)Math.Clamp(mac >> 4, 0, 0xFF) << (8 * ch);
        }
        return (word & 0xFF000000u) | rgb;
    }

    /// <summary>The lit colour func_800302E8 fogs, as NormalColorCol makes it.</summary>
    static uint ClippedColour(PSMemory mem, uint normal)
    {
        Gte.LightProducts(Peek16(mem, normal), Peek16(mem, normal + 2u), Peek16(mem, normal + 4u),
                          out int i1, out int i2, out int i3);
        uint light = Peek32(mem, LightColour);
        return (uint)Nccs(light & 0xFF, i1) | ((uint)Nccs((light >> 8) & 0xFF, i2) << 8)
             | ((uint)Nccs((light >> 16) & 0xFF, i3) << 16);
    }

    /// <summary>The packets func_800302E8 filled since <paramref name="before"/>: one
    /// past the buffer's end was allocated and never filled.</summary>
    static uint ClippedCount(PSMemory mem, uint before)
    {
        uint desc = Peek32(mem, PrimDescriptor);
        uint after = Peek32(mem, desc + 8u);
        if (after <= before) return 0;
        uint count = (after - before) / 0x28u;
        if (after > Peek32(mem, desc + 4u)) count--;
        return count;
    }

    /// <summary>Packet k's corners are records 0, k+1 and k+2 of ClipOut.</summary>
    static void RefogClipped(PSMemory mem, uint before, uint normal)
    {
        uint count = ClippedCount(mem, before);
        if (count == 0) return;
        bool far = (int)Peek32(mem, FogMode) >= 32000;
        uint colour = ClippedColour(mem, normal);
        uint r0 = Peek32(mem, ClipOut);
        uint c0 = DepthCue(0, colour, RecordFogWeight(mem, r0, far, out _));
        for (uint k = 0; k < count; k++)
        {
            uint pkt = before + k * 0x28u;
            uint ra = Peek32(mem, ClipOut + 4u * (k + 1)), rb = Peek32(mem, ClipOut + 4u * (k + 2));
            mem.WriteU32(pkt + 0x04u, (Peek32(mem, pkt + 0x04u) & 0xFF000000u) | c0);
            mem.WriteU32(pkt + 0x10u, DepthCue(Peek32(mem, pkt + 0x10u), colour, RecordFogWeight(mem, ra, far, out _)));
            mem.WriteU32(pkt + 0x1Cu, DepthCue(Peek32(mem, pkt + 0x1Cu), colour, RecordFogWeight(mem, rb, far, out _)));
        }
    }
}
