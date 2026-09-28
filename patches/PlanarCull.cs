using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// The planar walk's own cull: every half on the eye's level inside the view cone,
/// with no occlusion flood.
///
///     KF2_PLANAR_CULL=0        the mirrored walk draws only the eye's cells, as before
///
/// The game culls its map in two steps: a level cone in plan along the yaw, then a
/// flood from the camera that darkens what walls hide from the eye. The mirror
/// looks from under the water, so the flood's answer is the wrong one for it: the
/// inside of a cavern round a cliff is hidden from the eye and plain in the water,
/// and it popped in as the eye's flood reached it. The mirrored walk therefore
/// walks the cone without the flood, out to the render distance, and the depth test
/// hides what the mirror cannot see. A cell enters the cone at its side, which the
/// frustum does not reach, or at its far edge, which the fog has already blacked out.
///
/// Models standing in cells only this cull lights are admitted by the object walk
/// as mirror-only: <see cref="ModelWalk"/> hands their submit to
/// <see cref="PlanarWalk.Record"/> instead of drawing it, so the picture is
/// unchanged and the mirror replays them with the rest.
///
/// See "The mirror's own cull" in docs/RENDERING.md.
/// </summary>
public static class PlanarCull
{
    const uint Grid = 0x80192EAC, GridOriginX = 0x80192EA0, GridOriginZ = 0x80192EA4;
    const uint CamWorldX = 0x80192E78, CamWorldZ = 0x80192E80;
    const uint MapBase = 0x801C8484;
    const uint ViewMatrix = CameraBlock.ViewMatrix;

    /// <summary>Tiles from the camera a half is drawn with the clipper, as the game
    /// draws its near tiles.</summary>
    const int NearTiles = 3;

    const int Reach = RenderDistance.Reach;

    public static bool On = true;

    /// <summary>This frame's cull added cells to the mirror.</summary>
    public static bool Any => _cells.Count > 0;

    static readonly byte[] _bits = new byte[80 * 80];
    static readonly List<int> _cells = new();

    public static long Frames, Added, Models;

    /// <summary>How much wider than the game's the last cone was for the pitch;
    /// infinity when it was open all round.</summary>
    public static float Pitched = 1f;

    public static void Configure(string? on) => On = on?.Trim() != "0";

    /// <summary>From the end of the frame's own tile walk, before the object walk
    /// asks what is visible.</summary>
    public static void Build(PSMemory mem)
    {
        foreach (int t in _cells) _bits[t] = 0;
        _cells.Clear();
        if (!On || !PlanarWalk.Enabled) return;
        if (!RenderDistance.ViewCone(mem, out float cx, out float cz, out float fx, out float fz, out float stockFar)) return;

        int ctx = (int)(mem.ReadU32(CamWorldX) >> 11), ctz = (int)(mem.ReadU32(CamWorldZ) >> 11);
        float far = Math.Max(stockFar, RenderDistance.Tiles) + 0.5f;
        float slope = RenderDistance.Slope * CullCone.Factor;
        // The game's cone is drawn for a level camera. Pitched, the frustum's corner
        // rays run wider in plan: a ray (tanH, tanV, 1) turned by the pitch has a
        // forward run of cos - tanV * |sin| for a sideways run of tanH. The mirror
        // is pitched as far as the eye, the other way.
        float h = GteDepth.ProjH > 1f ? GteDepth.ProjH : 200f;
        float tanH = 160f * CullCone.Factor / h, tanV = 1.1f * 120f / h;
        float fwdX = (short)mem.ReadU16(ViewMatrix + 12u), fwdY = (short)mem.ReadU16(ViewMatrix + 14u), fwdZ = (short)mem.ReadU16(ViewMatrix + 16u);
        float cos = MathF.Sqrt(fwdX * fwdX + fwdZ * fwdZ) / 4096f, sin = MathF.Abs(fwdY) / 4096f;
        float run = cos - tanV * sin;
        // Steep enough, the frustum reaches round behind the camera: every cell in reach.
        bool open = run < 0.2f;
        if (!open) slope = Math.Max(slope, tanH / run);
        Pitched = open ? float.PositiveInfinity : slope / (RenderDistance.Slope * CullCone.Factor);
        uint originX = mem.ReadU32(GridOriginX);
        int originZ = (int)mem.ReadU32(GridOriginZ);

        // The eye's level is the bit the game's flood lit most of.
        int lower = 0, upper = 0;
        for (uint i = 0; i < 24 * 24; i++)
        {
            uint b = mem.ReadU8(Grid + i);
            lower += (int)(b & 1u);
            upper += (int)((b >> 1) & 1u);
        }
        byte marker = upper > lower ? (byte)2 : (byte)1;
        uint halfOff = marker == 2 ? 5u : 0u;

        for (int dz = -Reach; dz <= Reach; dz++)
            for (int dx = -Reach; dx <= Reach; dx++)
            {
                int tx = ctx + dx, tz = ctz + dz;
                if ((uint)tx >= 80u || (uint)tz >= 80u) continue;
                float px = tx + 0.5f - cx, pz = tz + 0.5f - cz;
                float d = px * fx + pz * fz;
                // A cell straddling the camera is always in; the cone's apex is behind it.
                bool here = Math.Abs(dx) <= 1 && Math.Abs(dz) <= 1;
                if (open) { if (!here && px * px + pz * pz > far * far) continue; }
                else if (!here && (d < -RenderDistance.Apex || d > far || MathF.Abs(px * fz - pz * fx) > slope * (d + RenderDistance.Apex) + 1.5f))
                    continue;
                if (mem.ReadU8(MapBase + (uint)tz * 800u + (uint)tx * 10u + halfOff) == 0xFF) continue;

                // Already the grid's: the mirrored walk draws it with the game's own cells.
                uint col = ((uint)tx - originX) & 0xFFu;
                int row = tz - originZ;
                if (col < 24u && (uint)row < 24u && (mem.ReadU8(Grid + (uint)row * 24u + col) & marker) != 0) continue;

                int ti = tz * 80 + tx;
                _bits[ti] = marker;
                _cells.Add(ti);
            }

        Frames++;
        Added += _cells.Count;
    }

    /// <summary>In the mirrored walk, after the grid's cells: the ones this cull added.</summary>
    public static void Walk(CpuContext c, PSMemory mem)
    {
        int cx = (int)(mem.ReadU32(CamWorldX) >> 11), cz = (int)(mem.ReadU32(CamWorldZ) >> 11);
        foreach (int t in _cells)
        {
            Interrupts.Poll(c, mem);
            int tx = t % 80, tz = t / 80;
            bool near = Math.Max(Math.Abs(tx - cx), Math.Abs(tz - cz)) <= NearTiles;
            c.A0 = (uint)tx;
            c.A1 = (uint)tz;
            c.A2 = _bits[t] | (near ? 0x80u : 0u);
            c.RA = 0x80031D14u;
            KingsField2.func_80031B1C(c, mem);
        }
    }

    /// <summary>The cull's level bits at a world position (x at +0, z at +8), for a
    /// visibility query the game's grid answered 0.</summary>
    public static uint Point(PSMemory mem, uint pos)
    {
        uint tx = mem.ReadU32(pos) >> 11, tz = mem.ReadU32(pos + 8u) >> 11;
        if (tx >= 80u || tz >= 80u) return 0;
        return _bits[tz * 80u + tx];
    }

    /// <summary>The same over the box func_80032DE8 asks about.</summary>
    public static uint Box(PSMemory mem, uint pos, uint radius)
    {
        int tx = (int)(mem.ReadU32(pos) >> 11), tz = (int)(mem.ReadU32(pos + 8u) >> 11), r = (int)radius;
        uint acc = 0;
        for (int z = Math.Max(tz - r, 0); z < Math.Min(tz + r, 80); z++)
            for (int x = Math.Max(tx - r, 0); x < Math.Min(tx + r, 80); x++)
                acc |= _bits[z * 80 + x];
        return acc;
    }
}
