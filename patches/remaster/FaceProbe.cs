using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>
/// A measurement, not a feature (KF2_FACE_PROBE=1, with KF2_SSR=1 KF2_SSR_PROBE=1):
/// whether a tile face can be named by (record, face index) on every assembler path,
/// whether the subdivider's output maps back to its source face, and whether a pick
/// taken from the frame's own recorded triangles agrees with what the GPU drew.
/// Every tile face is given one of the four authored ids by a hash of its key; the
/// reflection probe's readback holds the id per pixel.
/// </summary>
public static class FaceProbe
{
    public static readonly bool On = Environment.GetEnvironmentVariable("KF2_FACE_PROBE") is "1";

    const uint VertexBase = 0x8018EAA0;

    // Subdivided output face -> source face, while the subdivided call runs.
    static int[] _map = new int[256];
    static int _mapN = -1;

    static uint _salt = 1;

    // Faces entered per path.
    static long _plainFaces, _subFaces, _farFaces, _unmapped;
    // Subdivider census.
    static long _groups, _split, _copied, _countBad, _cmdBad, _texBad, _outside, _cornerMissing, _checkedVerts;
    static readonly HashSet<uint> _meshes = new();

    struct Tri
    {
        public float X0, Y0, Z0, X1, Y1, Z1, X2, Y2, Z2;
        public int Label;   // 4..7, or 0 for a blocker (a model)
        public uint Rec;
        public int Face;
    }

    static List<Tri> _cur = new(), _prev = new();
    static long _seenSerial;

    static int Label(uint rec, int face)
    {
        uint h = rec * 0x9E3779B1u ^ (uint)face * 0x85EBCA6Bu ^ _salt * 0xC2B2AE35u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
        return 4 + (int)(h & 3u);
    }

    /// <summary>PolyAssembler's face loops, per face, before it is assembled.</summary>
    public static void Enter(bool subdivided, bool far, int index)
    {
        uint rec = TileWalk.CurrentRecord;
        if (rec == 0 || PolyAssembler.InModel) return;
        int face = index;
        if (subdivided)
        {
            if (_mapN < 0 || index >= _mapN) { _unmapped++; return; }
            face = _map[index];
            _subFaces++;
        }
        else if (far) _farFaces++;
        else _plainFaces++;
        PolyAssembler.TileMaterial = (byte)Label(rec, face);
        _face = face;
    }

    static int _face = -1;

    /// <summary>SealDepth: every recorded packet, for the pick.</summary>
    public static void Seal(PSMemory mem, uint pkt, uint last, in GtePacketDepth.Rec r)
    {
        int n = last == 0x2Cu || (last == 0x20u && r.Z3 > 0f) ? 4 : 3;
        uint stride = (last - 8u) / (uint)(n - 1);
        Span<float> x = stackalloc float[4], y = stackalloc float[4], z = stackalloc float[4];
        for (int i = 0; i < n; i++)
        {
            uint w = mem.ReadU32(pkt + 8u + stride * (uint)i);
            x[i] = (short)w; y[i] = (short)(w >> 16);
        }
        z[0] = r.Z0; z[1] = r.Z1; z[2] = r.Z2; z[3] = r.Z3;
        bool tile = TileWalk.CurrentRecord != 0 && !PolyAssembler.InModel;
        int label = tile ? PolyAssembler.TileMaterial : 0;
        Add(x[0], y[0], z[0], x[1], y[1], z[1], x[2], y[2], z[2], label, tile ? _face : -1);
        if (n == 4) Add(x[1], y[1], z[1], x[3], y[3], z[3], x[2], y[2], z[2], label, tile ? _face : -1);
    }

    static void Add(float x0, float y0, float z0, float x1, float y1, float z1, float x2, float y2, float z2, int label, int face)
        => _cur.Add(new Tri { X0 = x0, Y0 = y0, Z0 = z0, X1 = x1, Y1 = y1, Z1 = z1, X2 = x2, Y2 = y2, Z2 = z2,
                              Label = label, Rec = TileWalk.CurrentRecord, Face = face });

    public static void LeaveHalf() => _face = -1;

    // ---- the subdivider ------------------------------------------------------

    static uint Next(PSMemory mem, uint f) => f + 4u + ((mem.ReadU32(f) >> 6) & 0x3FCu);

    static bool Splits(uint cmd) => cmd is 0x2C or 0x2E or 0x24 or 0x26;

    static int Corners(uint cmd) => (cmd & 0xFDu) == 0x2Cu ? 4 : (cmd & 0xFDu) == 0x24u ? 3 : 0;

    static uint VertexOffset(PSMemory mem, uint f, uint cmd, int i)
        => mem.ReadU16(f + 4u + ((cmd & 0xFDu) == 0x2Cu ? 0x12u : 0x0Eu) + 2u * (uint)i);

    /// <summary>After func_80030C94 built <paramref name="mesh"/> from model
    /// <paramref name="model"/>; <paramref name="srcVerts"/> is VertexBase before it.</summary>
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

        if (_map.Length < on) _map = new int[on * 2];
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
                _map[o] = s;
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
        _mapN = Math.Min(o, on);
    }

    static bool Inside(short v, Span<short> s, int n)
    {
        short lo = short.MaxValue, hi = short.MinValue;
        for (int i = 0; i < n; i++) { if (s[i] < lo) lo = s[i]; if (s[i] > hi) hi = s[i]; }
        return v >= lo - 1 && v <= hi + 1;
    }

    public static void SubdividedDone() => _mapN = -1;

    // ---- the pick against the readback -----------------------------------------

    /// <summary>The start of each tile walk: the last frame's list is complete.</summary>
    public static void FrameStart()
    {
        (_prev, _cur) = (_cur, _prev);
        _cur.Clear();
        if (ScreenReflections.MapSerial != _seenSerial && ScreenReflections.LastInfo is { } info)
        {
            _seenSerial = ScreenReflections.MapSerial;
            Analyze(info, ScreenReflections.LastW, ScreenReflections.LastH);
            _salt++;
        }
    }

    /// <summary>The nearest recorded triangle at a point: its index, or -1.</summary>
    static int Pick(float px, float py)
    {
        int best = -1;
        float bestZ = float.MaxValue;
        var l = _prev;
        for (int i = 0; i < l.Count; i++)
        {
            var t = l[i];
            float d = (t.X1 - t.X0) * (t.Y2 - t.Y0) - (t.X2 - t.X0) * (t.Y1 - t.Y0);
            if (MathF.Abs(d) < 1e-3f) continue;
            float a = ((t.X1 - px) * (t.Y2 - py) - (t.X2 - px) * (t.Y1 - py)) / d;
            float b = ((t.X2 - px) * (t.Y0 - py) - (t.X0 - px) * (t.Y2 - py)) / d;
            float c = 1f - a - b;
            if (a < 0f || b < 0f || c < 0f) continue;
            float iz = a / t.Z0 + b / t.Z1 + c / t.Z2;
            float z = 1f / iz;
            if (z < bestZ) { bestZ = z; best = i; }
        }
        return best;
    }

    static int _misses;

    static string Covering(float px, float py)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _prev.Count; i++)
        {
            var t = _prev[i];
            float d = (t.X1 - t.X0) * (t.Y2 - t.Y0) - (t.X2 - t.X0) * (t.Y1 - t.Y0);
            if (MathF.Abs(d) < 1e-3f) continue;
            float a = ((t.X1 - px) * (t.Y2 - py) - (t.X2 - px) * (t.Y1 - py)) / d;
            float b = ((t.X2 - px) * (t.Y0 - py) - (t.X0 - px) * (t.Y2 - py)) / d;
            if (a < 0f || b < 0f || 1f - a - b < 0f) continue;
            sb.Append($"[#{i} L{t.Label} {t.Rec:X8}/{t.Face} z{Z(t, px, py):F0} ({t.X0},{t.Y0} {t.X1},{t.Y1} {t.X2},{t.Y2}) z{t.Z0:F0},{t.Z1:F0},{t.Z2:F0}] ");
        }
        return sb.ToString();
    }

    static float Z(Tri t, float px, float py)
    {
        float d = (t.X1 - t.X0) * (t.Y2 - t.Y0) - (t.X2 - t.X0) * (t.Y1 - t.Y0);
        float a = ((t.X1 - px) * (t.Y2 - py) - (t.X2 - px) * (t.Y1 - py)) / d;
        float b = ((t.X2 - px) * (t.Y0 - py) - (t.X0 - px) * (t.Y2 - py)) / d;
        return 1f / (a / t.Z0 + b / t.Z1 + (1f - a - b) / t.Z2);
    }

    static float Seg(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay, l = dx * dx + dy * dy;
        float u = l < 1e-6f ? 0f : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l, 0f, 1f);
        float ex = ax + u * dx - px, ey = ay + u * dy - py;
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    static float EdgeDistance(Tri t, float x, float y)
        => MathF.Min(Seg(x, y, t.X0, t.Y0, t.X1, t.Y1), MathF.Min(Seg(x, y, t.X1, t.Y1, t.X2, t.Y2), Seg(x, y, t.X2, t.Y2, t.X0, t.Y0)));

    /// <summary>Whether a recorded face carrying <paramref name="label"/> covers a point
    /// within <paramref name="r"/> game pixels.</summary>
    static bool Nearby(float x, float y, int label, float r)
    {
        for (float dy = -r; dy <= r; dy += 0.5f)
            for (float dx = -r; dx <= r; dx += 0.5f)
            {
                int p = Pick(x + dx, y + dy);
                if (p >= 0 && _prev[p].Label == label) return true;
            }
        return false;
    }

    static void Analyze(byte[] info, int w, int h)
    {
        _misses = 0;
        float gameW = w * 240f / h, margin = (gameW - 320f) / 2f;
        int Gpu(float x, float y)
        {
            int px = (int)((x + margin) * w / gameW), py = (int)(y * h / 240f);
            if ((uint)px >= (uint)w || (uint)py >= (uint)h) return -1;
            return info[((long)py * w + px) * 4];
        }

        // Centroids of every tile triangle of at least 30 square game pixels, and a
        // grid every 6 game pixels over the whole picture.
        long cAgree = 0, cDis = 0, cGpuOther = 0, cModel = 0, cOwn = 0, cN = 0;
        var l = _prev;
        for (int i = 0; i < l.Count; i++)
        {
            var t = l[i];
            if (t.Label == 0) continue;
            float area = MathF.Abs((t.X1 - t.X0) * (t.Y2 - t.Y0) - (t.X2 - t.X0) * (t.Y1 - t.Y0)) / 2f;
            if (area < 30f) continue;
            float cx = (t.X0 + t.X1 + t.X2) / 3f, cy = (t.Y0 + t.Y1 + t.Y2) / 3f;
            if (cx < -margin || cx >= 320f + margin || cy < 0f || cy >= 240f) continue;
            cN++;
            int p = Pick(cx, cy);
            if (p < 0) continue;
            if (p == i || (l[p].Rec == t.Rec && l[p].Face == t.Face)) cOwn++;
            if (l[p].Label == 0) { cModel++; continue; }
            int g = Gpu(cx, cy);
            if (g < 4) { cGpuOther++; continue; }
            if (g == l[p].Label) cAgree++; else cDis++;
        }

        long gDisInterior = 0, gDisEdge1 = 0, gDisEdge2 = 0, gDisNear = 0;
        long gAgree = 0, gDis = 0, gNone = 0, gModel = 0, gGpuOther = 0, gGpuTileCpuNone = 0;
        var otherIds = new long[8];
        for (float y = 3f; y < 240f; y += 6f)
            for (float x = -margin + 3f; x < 320f + margin; x += 6f)
            {
                int p = Pick(x, y);
                int g = Gpu(x, y);
                if (p < 0) { gNone++; if (g >= 4) gGpuTileCpuNone++; continue; }
                if (l[p].Label == 0) { gModel++; continue; }
                if (g < 4) { gGpuOther++; if (g >= 0) otherIds[g]++; continue; }
                if (g == l[p].Label) { gAgree++; continue; }
                gDis++;
                float e = EdgeDistance(l[p], x, y);
                if (e < 1f) gDisEdge1++; else if (e < 2f) gDisEdge2++;
                else if (Nearby(x, y, g, 1.5f)) gDisNear++;
                else if (++gDisInterior > 0 && _misses < 4) { _misses++; Console.WriteLine($"[KF2] faceprobe:   miss at {x},{y}: cpu {l[p].Label} rec {l[p].Rec:X8} face {l[p].Face} z {Z(l[p], x, y):F0}, gpu {g}, edge {e:F1}; covering: {Covering(x, y)}"); }
            }

        Console.WriteLine($"[KF2] faceprobe: {l.Count} recorded tris, readback {w}x{h}, margin {margin:F1}, salt {_salt}");
        Console.WriteLine($"[KF2] faceprobe:   centroids {cN}: pick agrees {cAgree}, disagrees {cDis}, gpu not a tile id {cGpuOther}, " +
                          $"pick is a model {cModel}; pick is the centroid's own face {cOwn}");
        Console.WriteLine($"[KF2] faceprobe:   grid: agrees {gAgree}, disagrees {gDis}, gpu not a tile id {gGpuOther} " +
                          $"(ids 0-3: {otherIds[0]},{otherIds[1]},{otherIds[2]},{otherIds[3]}), pick is a model {gModel}, " +
                          $"nothing recorded {gNone} ({gGpuTileCpuNone} of them a tile id on the gpu); " +
                          $"disagreements within 1 px of the pick's edge {gDisEdge1}, 1-2 px {gDisEdge2}, gpu id on a face within 1.5 px {gDisNear}, interior {gDisInterior}");
        Console.WriteLine($"[KF2] faceprobe:   faces entered plain {_plainFaces}, far {_farFaces}, subdivided {_subFaces}, unmapped {_unmapped}");
        Console.WriteLine($"[KF2] faceprobe:   subdivider: {_meshes.Count} meshes, {_groups} source faces ({_split} split, {_copied} copied), " +
                          $"count mismatches {_countBad}, cmd {_cmdBad}, texture {_texBad}, " +
                          $"{_checkedVerts} vertices checked, {_outside} outside their source face, {_cornerMissing} source corners missing");
    }
}
