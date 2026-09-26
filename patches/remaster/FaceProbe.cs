using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>
/// A measurement, not a feature (KF2_FACE_PROBE=1, with KF2_SSR=1 KF2_SSR_PROBE=1):
/// whether the subdivider's output maps back to its source face, and whether a pick
/// taken from the frame's own recorded triangles (<see cref="Faces"/>) agrees with
/// what the GPU drew. Every tile face is given one of the four authored ids by a hash
/// of its key, over any authored material; the reflection probe's readback holds the
/// id per pixel. See "A tile half is a whole mesh, and a face is the key under it" in
/// docs/REMASTER.md.
/// </summary>
public static class FaceProbe
{
    public static readonly bool On = Environment.GetEnvironmentVariable("KF2_FACE_PROBE") is "1";

    static uint _salt = 1;
    static long _seenSerial;

    // Faces entered, and the subdivider census.
    static long _faces, _unmapped;
    static long _groups, _split, _copied, _countBad, _cmdBad, _texBad, _outside, _cornerMissing, _checkedVerts;
    static readonly HashSet<uint> _meshes = new();

    static byte Label(uint rec, int face)
    {
        uint h = rec * 0x9E3779B1u ^ (uint)face * 0x85EBCA6Bu ^ _salt * 0xC2B2AE35u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
        return (byte)(4 + (h & 3u));
    }

    /// <summary><see cref="Faces.Enter"/>, per tile face.</summary>
    public static void Enter(int face)
    {
        if (face < 0) { _unmapped++; return; }
        _faces++;
        PolyAssembler.TileMaterial = Label(TileWalk.CurrentRecord, face);
    }

    // ---- the subdivider ------------------------------------------------------

    static uint Next(PSMemory mem, uint f) => f + 4u + ((mem.ReadU32(f) >> 6) & 0x3FCu);

    static bool Splits(uint cmd) => cmd is 0x2C or 0x2E or 0x24 or 0x26;

    static int Corners(uint cmd) => (cmd & 0xFDu) == 0x2Cu ? 4 : (cmd & 0xFDu) == 0x24u ? 3 : 0;

    static uint VertexOffset(PSMemory mem, uint f, uint cmd, int i)
        => mem.ReadU16(f + 4u + ((cmd & 0xFDu) == 0x2Cu ? 0x12u : 0x0Eu) + 2u * (uint)i);

    /// <summary>After func_80030C94 built <paramref name="mesh"/> from model
    /// <paramref name="model"/>; <paramref name="srcVerts"/> is VertexBase before it.
    /// Every piece is checked against the source face it counts back to.</summary>
    public static void Subdivided(PSMemory mem, uint model, uint mesh, uint srcVerts)
    {
        uint table = mem.ReadU32(0x8018E19Cu);
        uint sh = table + 0xCu + model * 28u;
        uint sf = table + 0xCu + mem.ReadU32(sh + 0x10u);
        int sn = (int)mem.ReadU32(sh + 0x14u);
        uint oh = mesh + 0xCu;
        uint of = oh + mem.ReadU32(oh + 0x10u);
        int on = (int)mem.ReadU32(oh + 0x14u);
        uint ov = mesh + mem.ReadU32(mesh + 0xCu) + 0xCu;
        _meshes.Add(model);

        int o = 0;
        for (int s = 0; s < sn; s++, sf = Next(mem, sf))
        {
            _groups++;
            uint cmd = mem.ReadU32(sf) >> 24;
            int k = Splits(cmd) ? 4 : 1;
            if (k == 4) _split++; else _copied++;

            // The source face's box, in model space.
            int corners = Corners(cmd);
            Span<short> sx = stackalloc short[4], sy = stackalloc short[4], sz = stackalloc short[4];
            for (int i = 0; i < corners; i++)
            {
                uint v = srcVerts + VertexOffset(mem, sf, cmd, i);
                sx[i] = (short)mem.ReadU16(v); sy[i] = (short)mem.ReadU16(v + 2u); sz[i] = (short)mem.ReadU16(v + 4u);
            }
            Span<bool> found = stackalloc bool[4];
            found.Clear();

            for (int q = 0; q < k; q++, o++)
            {
                if (o >= on) { _countBad++; break; }
                uint ocmd = mem.ReadU32(of) >> 24;
                if (ocmd != cmd) _cmdBad++;
                else if (corners > 0)
                {
                    if (mem.ReadU16(of + 6u) != mem.ReadU16(sf + 6u) || mem.ReadU16(of + 10u) != mem.ReadU16(sf + 10u)) _texBad++;
                    for (int i = 0; i < corners; i++)
                    {
                        uint v = ov + VertexOffset(mem, of, ocmd, i);
                        short x = (short)mem.ReadU16(v), y = (short)mem.ReadU16(v + 2u), z = (short)mem.ReadU16(v + 4u);
                        _checkedVerts++;
                        if (!Inside(x, sx, corners) || !Inside(y, sy, corners) || !Inside(z, sz, corners)) _outside++;
                        for (int j = 0; j < corners; j++)
                            if (sx[j] == x && sy[j] == y && sz[j] == z) found[j] = true;
                    }
                }
                of = Next(mem, of);
            }
            if (k == 4) for (int j = 0; j < corners; j++) if (!found[j]) _cornerMissing++;
        }
        if (o != on) _countBad++;
    }

    static bool Inside(short v, Span<short> s, int n)
    {
        short lo = short.MaxValue, hi = short.MinValue;
        for (int i = 0; i < n; i++) { if (s[i] < lo) lo = s[i]; if (s[i] > hi) hi = s[i]; }
        return v >= lo - 1 && v <= hi + 1;
    }

    // ---- the pick against the readback -----------------------------------------

    /// <summary>A frame's triangles are complete: compare them with a new readback.</summary>
    public static void FrameDone()
    {
        if (ScreenReflections.MapSerial == _seenSerial || ScreenReflections.LastInfo is not { } info) return;
        _seenSerial = ScreenReflections.MapSerial;
        Analyze(info, ScreenReflections.LastW, ScreenReflections.LastH);
        _salt++;
    }

    static int _misses;

    static float EdgeDistance(in Faces.Tri t, float x, float y)
    {
        static float Seg(float px, float py, float ax, float ay, float bx, float by)
        {
            float dx = bx - ax, dy = by - ay, l = dx * dx + dy * dy;
            float u = l < 1e-6f ? 0f : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l, 0f, 1f);
            float ex = ax + u * dx - px, ey = ay + u * dy - py;
            return MathF.Sqrt(ex * ex + ey * ey);
        }
        return MathF.Min(Seg(x, y, t.X0, t.Y0, t.X1, t.Y1), MathF.Min(Seg(x, y, t.X1, t.Y1, t.X2, t.Y2), Seg(x, y, t.X2, t.Y2, t.X0, t.Y0)));
    }

    /// <summary>Whether any face the pick would select at a point carries <paramref name="label"/>.</summary>
    static bool Picked(float x, float y, int label)
    {
        var l = Faces.Last;
        int n = Faces.Nearest(new(x, y), out float z);
        if (n < 0) return false;
        float limit = z + Faces.Coplanar(z);
        for (int i = 0; i < l.Count; i++)
            if (l[i].Rec != 0 && l[i].Label == label && Faces.DepthAt(l[i], x, y) <= limit) return true;
        return false;
    }

    static void Analyze(byte[] info, int w, int h)
    {
        _misses = 0;
        var l = Faces.Last;
        float gameW = w * 240f / h, margin = (gameW - 320f) / 2f;
        int Gpu(float x, float y)
        {
            int px = (int)((x + margin) * w / gameW), py = (int)(y * h / 240f);
            if ((uint)px >= (uint)w || (uint)py >= (uint)h) return -1;
            return info[((long)py * w + px) * 4];
        }

        // A grid every 6 game pixels over the whole picture. A disagreement is what the
        // pick would not have selected: the GPU's id on none of the faces it returns.
        long agree = 0, dis = 0, coplanar = 0, none = 0, model = 0, gpuOther = 0, edge1 = 0, edge2 = 0, interior = 0;
        for (float y = 3f; y < 240f; y += 6f)
            for (float x = -margin + 3f; x < 320f + margin; x += 6f)
            {
                int p = Faces.Nearest(new(x, y), out _);
                int g = Gpu(x, y);
                if (p < 0) { none++; continue; }
                if (l[p].Rec == 0) { model++; continue; }
                if (g < 4) { gpuOther++; continue; }
                if (g == l[p].Label) { agree++; continue; }
                if (Picked(x, y, g)) { coplanar++; continue; }
                dis++;
                float e = EdgeDistance(l[p], x, y);
                if (e < 1f) edge1++;
                else if (e < 2f) edge2++;
                else
                {
                    interior++;
                    if (_misses++ < 4)
                        Console.WriteLine($"[KF2] faceprobe:   miss at {x},{y}: nearest {l[p].Rec:X8}/{l[p].Face} id {l[p].Label}, gpu {g}, edge {e:F1}");
                }
            }

        Console.WriteLine($"[KF2] faceprobe: {l.Count} recorded tris, readback {w}x{h}, salt {_salt}; grid: nearest agrees {agree}, " +
                          $"another face the pick also selects {coplanar}, disagrees {dis} (within 1 px of an edge {edge1}, 1-2 px {edge2}, " +
                          $"interior {interior}); gpu not a tile id {gpuOther}, a model in front {model}, nothing recorded {none}");
        Console.WriteLine($"[KF2] faceprobe:   faces entered {_faces}, unmapped {_unmapped}, subdivided calls refused {Faces.MapRefused}; " +
                          $"subdivider: {_meshes.Count} meshes, {_groups} source faces ({_split} split, {_copied} copied), " +
                          $"count mismatches {_countBad}, cmd {_cmdBad}, texture {_texBad}, " +
                          $"{_checkedVerts} vertices checked, {_outside} outside their source face, {_cornerMissing} source corners missing");
    }
}
