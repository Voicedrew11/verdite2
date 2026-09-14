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

    // The tile half func_80031950 is drawing while a neighbour's record differs from
    // its own in something being blended, else 0; its quarter turns, and the records
    // of it and the eight around it, (dz+1)*3 + (dx+1), 0 where that half has no model
    // or is off the map. Fog words the same, Empty for no record.
    static uint _tile;
    static bool _tileFog, _tileLight;
    static int _tileRot, _tileWord;
    static readonly uint[] _nearRecs = new uint[9];
    static readonly int[] _nearWords = new int[9];
    const int Empty = int.MinValue;

    internal static void BeginTile(uint half, IMemory m)
    {
        _tile = 0;
        _tileFog = _tileLight = false;
        bool fog = EvenFog.Enabled && EvenFog.Blend, light = EvenFog.Light;
        if (!(fog || light) || _mode == Mode.Verify || m is not PSMemory mem) return;
        uint off = half - TileBase;
        if (off >= 80u * 800u) return;
        int tx = (int)(off / 10u % 80u), tz = (int)(off / 800u);
        uint h = off % 10u;
        _tileRot = (int)Peek32(mem, half + 2u) & 3;
        uint own = Record(mem, half);
        _tileWord = Peek16(mem, own + 0x66u);
        bool fogMixed = false, lightMixed = false;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int x = tx + dx, z = tz + dz, i = (dz + 1) * 3 + dx + 1;
            uint rec = own;
            if ((dx | dz) != 0)
            {
                uint nb = TileBase + 800u * (uint)z + 10u * (uint)x + h;
                rec = (uint)x < 80u && (uint)z < 80u && (byte)Peek32(mem, nb) < 240 ? Record(mem, nb) : 0u;
            }
            _nearRecs[i] = rec;
            _nearWords[i] = rec == 0 ? Empty : Peek16(mem, rec + 0x66u);
            if (rec == 0 || rec == own) continue;
            fogMixed |= _nearWords[i] != _tileWord;
            lightMixed |= !SameLight(mem, rec, own);
        }
        _tileFog = fog && fogMixed;
        _tileLight = light && lightMixed;
        if (_tileFog || _tileLight) _tile = half;
    }

    internal static void EndTile()
    {
        _tile = 0;
        _tileFog = _tileLight = false;
    }

    static uint Record(PSMemory mem, uint half) => LightRecords + ((uint)Peek32(mem, half + 4u) & 0x3Fu) * 0x68u;

    /// <summary>The colour matrix (+0x50, nine shorts) and back colour (+0x62, three bytes).</summary>
    static bool SameLight(PSMemory mem, uint a, uint b)
    {
        for (uint o = 0x50u; o < 0x64u; o += 4u)
            if (Peek32(mem, a + o) != Peek32(mem, b + o)) return false;
        return (byte)Peek32(mem, a + 0x64u) == (byte)Peek32(mem, b + 0x64u);
    }

    /// <summary>
    /// Bilinear weights between the centres of the four tiles around a vertex of the
    /// current tile: its own at the centre, half and half at an edge, a quarter each at
    /// a corner. <paramref name="iX"/>, <paramref name="iZ"/> and <paramref name="iD"/>
    /// index the neighbours; an empty one has weight 0, so every tile meeting at a
    /// point shares the same set.
    /// </summary>
    static void TileWeights(short vx, short vz, out long kOwn, out long kX, out long kZ, out long kD,
                            out int iX, out int iZ, out int iD)
    {
        TileOffset(vx, vz, out int wx, out int wz);
        int sx = wx < 0 ? -1 : 1, sz = wz < 0 ? -1 : 1;
        // Mesh edges sit at +-1034, ten units past the tile's; both sides clamp to half.
        long ax = Math.Min(Math.Abs(wx), 1024) * 2, az = Math.Min(Math.Abs(wz), 1024) * 2;
        iX = 4 + sx; iZ = 4 + 3 * sz; iD = 4 + 3 * sz + sx;
        kOwn = (4096 - ax) * (4096 - az);
        kX = _nearRecs[iX] == 0 ? 0 : ax * (4096 - az);
        kZ = _nearRecs[iZ] == 0 ? 0 : (4096 - ax) * az;
        kD = _nearRecs[iD] == 0 ? 0 : ax * az;
    }

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
    /// The fog weight at a vertex of the current tile, blended between the tiles around
    /// it (<see cref="TileWeights"/>). A neighbour's weight is its own word's curve at
    /// its own IR0. False, and <paramref name="ownFog"/>, where every tile that weighs
    /// in has the tile's own word.
    /// </summary>
    static bool BlendFog(int ownFog, uint sz3, short vx, short vz, bool near, out int fog)
    {
        fog = ownFog;
        TileWeights(vx, vz, out long kOwn, out long kX, out long kZ, out long kD, out int iX, out int iZ, out int iD);
        int wX = _nearWords[iX], wZ = _nearWords[iZ], wD = _nearWords[iD];
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

    /// <summary>
    /// The lit colour at a vertex of the current tile. The colour matrix and back colour
    /// are blended between the records of the tiles around it, as func_80032588 blends
    /// two records for an object (func_80015930, func_800158C8); the light matrix stays
    /// the tile's own, as there. Then NormalColorCol's arithmetic, with no register
    /// touched. <paramref name="own"/> where every tile that weighs in lights alike.
    /// </summary>
    static uint BlendLight(PSMemory mem, uint normal, uint own, short vx, short vz)
    {
        TileWeights(vx, vz, out long kOwn, out long kX, out long kZ, out long kD, out int iX, out int iZ, out int iD);
        uint r0 = _nearRecs[4], rX = _nearRecs[iX], rZ = _nearRecs[iZ], rD = _nearRecs[iD];
        if ((kX == 0 || SameLight(mem, rX, r0)) && (kZ == 0 || SameLight(mem, rZ, r0)) && (kD == 0 || SameLight(mem, rD, r0)))
            return own;
        return LitColour(mem, normal, own, r0, kX == 0 ? r0 : rX, kZ == 0 ? r0 : rZ, kD == 0 ? r0 : rD, kOwn, kX, kZ, kD);
    }

    /// <summary>NormalColorCol under the tile's light matrix and the weighted mix of four
    /// records' colour matrix and back colour.</summary>
    static uint LitColour(PSMemory mem, uint normal, uint own, uint r0, uint rX, uint rZ, uint rD,
                    long kOwn, long kX, long kZ, long kD)
    {
        long total = kOwn + kX + kZ + kD;
        long nx = Peek16(mem, normal), ny = Peek16(mem, normal + 2u), nz = Peek16(mem, normal + 4u);
        uint llm = r0 + (uint)_tileRot * 20u;
        Span<long> a = stackalloc long[3];
        for (int j = 0; j < 3; j++)
        {
            long v = Peek16(mem, llm + 6u * (uint)j) * nx + Peek16(mem, llm + 6u * (uint)j + 2u) * ny
                   + Peek16(mem, llm + 6u * (uint)j + 4u) * nz;
            a[j] = Math.Clamp((int)(v >> 12), 0, 0x7FFF);
        }

        uint light = Peek32(mem, LightColour);
        uint rgb = 0;
        for (int c = 0; c < 3; c++)
        {
            long v = (long)Mix(0x62u + (uint)c, true) << 12;
            for (int j = 0; j < 3; j++) v += Mix(0x50u + 6u * (uint)c + 2u * (uint)j, false) * a[j];
            int ir = Math.Clamp((int)(v >> 12), 0, 0x7FFF);
            int mac = (int)((((long)((light >> (8 * c)) & 0xFF) * ir) << 4) >> 12);
            rgb |= (uint)Math.Clamp(mac >> 4, 0, 0xFF) << (8 * c);
        }
        return (own & 0xFF000000u) | rgb;

        int Mix(uint o, bool back)
        {
            long Value(uint rec) => back ? (byte)Peek32(mem, rec + o) << 4 : Peek16(mem, rec + o);
            long sum = kOwn * Value(r0) + kX * Value(rX) + kZ * Value(rZ) + kD * Value(rD);
            return (int)Math.Round((double)sum / total, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>A tile face's corner colours: blended per vertex when the tile's light is.</summary>
    static void TileColours(PSMemory mem, uint normal, uint own, int n, uint p0, uint p1, uint p2, uint p3,
                            out uint c0, out uint c1, out uint c2, out uint c3)
    {
        uint verts = Peek32(mem, VertexBase);
        c0 = Corner(p0); c1 = Corner(p1); c2 = Corner(p2);
        c3 = n == 4 ? Corner(p3) : own;

        uint Corner(uint p)
        {
            uint src = verts + (p - VertexCache);
            return BlendLight(mem, normal, own, Peek16(mem, src), Peek16(mem, src + 4u));
        }
    }

    /// <summary>A clipper record's lit colour: blended when the tile's light is.</summary>
    static uint RecordColour(PSMemory mem, uint rec, uint normal, uint own) =>
        _tileLight ? BlendLight(mem, normal, own, Peek16(mem, rec), Peek16(mem, rec + 4u)) : own;

    /// <summary>A transform's vertex: its fog weight, blended when the tile is.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool TileVertexFog(PSMemory mem, uint src, int ownFog, bool near, out int fog)
    {
        fog = ownFog;
        return _tileFog && BlendFog(ownFog, Gte.Read(19), Peek16(mem, src), Peek16(mem, src + 4u), near, out fog);
    }

    /// <summary>A clipper record's fog weight on the tiles' curve, blended when the
    /// tile is. The record holds the interpolated local vertex at +0/+4 and its view
    /// Z at +0x10.</summary>
    static int RecordFogWeight(PSMemory mem, uint rec, bool far, out bool blended)
    {
        int fog = NearFog((int)Peek32(mem, rec + 0x14u), far);
        blended = _tileFog && BlendFog(fog, (uint)Math.Clamp((int)Peek32(mem, rec + 0x10u), 0, 0xFFFF),
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

    /// <summary>
    /// Packet k's corners are records 0, k+1 and k+2 of ClipOut. Fogged on the tiles'
    /// curve when <paramref name="refog"/>, else at the emitter's own IR0 / 2; lit per
    /// record when the tile's light is blended.
    /// </summary>
    static void RewriteClipped(PSMemory mem, uint before, uint normal, bool refog)
    {
        uint count = ClippedCount(mem, before);
        if (count == 0) return;
        bool far = (int)Peek32(mem, FogMode) >= 32000;
        uint colour = ClippedColour(mem, normal);
        uint r0 = Peek32(mem, ClipOut);
        uint c0 = DepthCue(0, RecordColour(mem, r0, normal, colour), Fog(r0));
        for (uint k = 0; k < count; k++)
        {
            uint pkt = before + k * 0x28u;
            uint ra = Peek32(mem, ClipOut + 4u * (k + 1)), rb = Peek32(mem, ClipOut + 4u * (k + 2));
            mem.WriteU32(pkt + 0x04u, (Peek32(mem, pkt + 0x04u) & 0xFF000000u) | (c0 & 0xFFFFFFu));
            mem.WriteU32(pkt + 0x10u, DepthCue(Peek32(mem, pkt + 0x10u), RecordColour(mem, ra, normal, colour), Fog(ra)));
            mem.WriteU32(pkt + 0x1Cu, DepthCue(Peek32(mem, pkt + 0x1Cu), RecordColour(mem, rb, normal, colour), Fog(rb)));
        }

        int Fog(uint rec) => refog ? RecordFogWeight(mem, rec, far, out _) : (int)Peek32(mem, rec + 0x14u) >> 1;
    }
}
