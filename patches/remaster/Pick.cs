using System.Numerics;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>
/// The game's own projection, both ways: a world point to a game pixel, and a game
/// pixel to the floor it lands on. The view is what <c>func_80031950</c> loads for
/// every tile -- the matrix at 0x80192E18 applied to the world position less the
/// camera's -- and the screen is the GTE's <c>H</c> and centre as <c>Gte.Rtp</c>
/// last saw them (<see cref="GteDepth.ProjH"/>). Nothing is keyed on a screen
/// position; this only turns a click into a tile key.
/// </summary>
public static class Pick
{
    const uint ViewMatrix = 0x80192E18;
    const uint CamWorldX = 0x80192E78, CamWorldY = 0x80192E7C, CamWorldZ = 0x80192E80;
    const uint PosXAddr = 0x801994EC, PosZAddr = 0x801994F4;

    /// <summary>How many cells a ray is followed through.</summary>
    const int MaxCells = 64;

    public struct View
    {
        public Matrix4x4 R;     // rows are the view axes, unit length
        public Vector3 T, Cam;  // the matrix's translation; the camera in world units
        public float H, Cx, Cy;
    }

    public static View Read(IMemory m)
    {
        var v = new View();
        float E(uint off) => (short)m.ReadU16(ViewMatrix + off) / 4096f;
        v.R = new Matrix4x4(E(0), E(2), E(4), 0, E(6), E(8), E(10), 0, E(12), E(14), E(16), 0, 0, 0, 0, 1);
        v.T = new Vector3((int)m.ReadU32(ViewMatrix + 0x14), (int)m.ReadU32(ViewMatrix + 0x18), (int)m.ReadU32(ViewMatrix + 0x1C));
        // The camera is kept in sixteen bits, which a map 163,840 units wide
        // overflows; the player is never 32,768 units from it.
        int px = (int)m.ReadU32(PosXAddr), pz = (int)m.ReadU32(PosZAddr);
        v.Cam = new Vector3(px + (short)(m.ReadU16(CamWorldX) - px),
                            (short)m.ReadU16(CamWorldY),
                            pz + (short)(m.ReadU16(CamWorldZ) - pz));
        v.H = Math.Max(1f, GteDepth.ProjH);
        v.Cx = GteDepth.ProjCx;
        v.Cy = GteDepth.ProjCy;
        return v;
    }

    static Vector3 Mul(in Matrix4x4 r, Vector3 p) => new(
        r.M11 * p.X + r.M12 * p.Y + r.M13 * p.Z,
        r.M21 * p.X + r.M22 * p.Y + r.M23 * p.Z,
        r.M31 * p.X + r.M32 * p.Y + r.M33 * p.Z);

    static Vector3 MulT(in Matrix4x4 r, Vector3 p) => new(
        r.M11 * p.X + r.M21 * p.Y + r.M31 * p.Z,
        r.M12 * p.X + r.M22 * p.Y + r.M32 * p.Z,
        r.M13 * p.X + r.M23 * p.Y + r.M33 * p.Z);

    /// <summary>A world point to game pixels; false behind the camera.</summary>
    public static bool Project(in View v, Vector3 world, out Vector2 screen)
    {
        var p = Mul(v.R, world - v.Cam) + v.T;
        screen = default;
        if (p.Z < 16f) return false;
        screen = new Vector2(v.Cx + v.H * p.X / p.Z, v.Cy + v.H * p.Y / p.Z);
        return true;
    }

    /// <summary>The ray through a game pixel, in world space.</summary>
    public static (Vector3 Origin, Vector3 Dir) Ray(in View v, Vector2 screen)
    {
        var d = new Vector3((screen.X - v.Cx) / v.H, (screen.Y - v.Cy) / v.H, 1f);
        var origin = v.Cam + MulT(v.R, -v.T);
        return (origin, Vector3.Normalize(MulT(v.R, d)));
    }

    /// <summary>
    /// The first drawn floor a ray comes down on, walking the grid cell by cell from
    /// the eye, or what stopped it. Walls are not in the grid as geometry, so a tile
    /// with no drawn floor is taken as rock and a ray entering a tile below its lowest
    /// floor as having met the step. `+4` bit 0x80 is not a wall: every tile of
    /// fdat02's shore carries it.
    /// </summary>
    public static TileKey? Floor(IMemory m, in View v, Vector2 screen, out Vector3 hit)
        => Floor(m, v, screen, out hit, out _);

    public static TileKey? Floor(IMemory m, in View v, Vector2 screen, out Vector3 hit, out string? stop)
    {
        hit = default;
        stop = null;
        if (Identity.Area < 0) { stop = "no area"; return null; }
        var (o, d) = Ray(v, screen);

        float unit = Identity.TileUnits;
        int cx = (int)MathF.Floor(o.X / unit), cz = (int)MathF.Floor(o.Z / unit);
        int sx = d.X > 0 ? 1 : -1, sz = d.Z > 0 ? 1 : -1;
        float tdx = MathF.Abs(d.X) < 1e-6f ? float.MaxValue : unit / MathF.Abs(d.X);
        float tdz = MathF.Abs(d.Z) < 1e-6f ? float.MaxValue : unit / MathF.Abs(d.Z);
        float nx = sx > 0 ? (cx + 1) * unit : cx * unit, nz = sz > 0 ? (cz + 1) * unit : cz * unit;
        float tx = MathF.Abs(d.X) < 1e-6f ? float.MaxValue : (nx - o.X) / d.X;
        float tz = MathF.Abs(d.Z) < 1e-6f ? float.MaxValue : (nz - o.Z) / d.Z;
        float t0 = 0f;

        for (int i = 0; i < MaxCells; i++)
        {
            float t1 = MathF.Min(tx, tz);
            if ((uint)cx >= Identity.Span || (uint)cz >= Identity.Span) { stop = "off the map"; return null; }

            bool drawn = false;
            float lowest = float.MinValue;   // up is -Y, so the lowest floor has the largest Y
            TileKey? best = null;
            float bt = float.MaxValue;
            for (int half = 0; half < 2; half++)
            {
                uint rec = Identity.HalfRecord(cx, cz, half);
                if (m.ReadU8(rec) >= 240) continue;
                drawn = true;
                float y = -(m.ReadU8(rec + 1u) << 7);
                lowest = MathF.Max(lowest, y);
                if (d.Y <= 1e-4f) continue;   // a floor is only seen from above
                float t = (y - o.Y) / d.Y;
                if (t < t0 - 1f || t > t1 + 1f || t <= 0f || t >= bt) continue;
                bt = t;
                best = new TileKey(Identity.Area, cx, cz, half);
            }
            // The eye's own tile is never a wall to the eye.
            if (i > 0)
            {
                if (!drawn) { stop = $"rock at {cx},{cz}"; hit = o + d * t0; return null; }
                if (o.Y + d.Y * t0 > lowest + 1f) { stop = $"a step up at {cx},{cz}"; hit = o + d * t0; return null; }
            }
            if (best != null)
            {
                hit = o + d * bt;
                return best;
            }
            t0 = t1;
            if (tx < tz) { cx += sx; tx += tdx; }
            else { cz += sz; tz += tdz; }
        }
        stop = "too far";
        return null;
    }

    /// <summary>The four corners of a half's floor, in world units.</summary>
    public static Vector3[] Corners(IMemory m, TileKey k)
    {
        uint rec = Identity.HalfRecord(k.X, k.Z, k.Half);
        float y = -(m.ReadU8(rec + 1u) << 7);
        float x0 = k.X * Identity.TileUnits, z0 = k.Z * Identity.TileUnits, s = Identity.TileUnits;
        return [new(x0, y, z0), new(x0 + s, y, z0), new(x0 + s, y, z0 + s), new(x0, y, z0 + s)];
    }
}
