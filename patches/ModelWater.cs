using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// Whether a model's faces in the water's texture are water. The water's rects
/// (<see cref="Reflections"/>) are the fluid slots, and the slots also hold the
/// creatures' skins: a slime is drawn in the water's texture, and so are the spinning
/// crystals of areas 4 and 7. Most water is the map's, but area 0's flooded cave draws
/// its near water as two objects (kind <c>5F</c>, model 204; kind <c>A0</c>, model 185).
///
/// A sheet of water is flat, a crystal and a slime are not: a model is water when it is
/// a rigid object and every averaging face of it on the water's rects lies at one height
/// in model space. A creature, an effect, a billboard, the arm and an MO-animated model
/// never are. Decided per submit by <see cref="ModelWalk"/>, sealed into each packet as
/// <c>GtePacketDepth.Rec.NotRect</c> and into the GPU-drawn mesh's water flags, so the
/// murk and the reflections both stop at it. See "Only water is murked" in
/// docs/RENDERING.md.
/// </summary>
public static class ModelWater
{
    const uint PolyModelTable = 0x8018E19C;
    const uint VertexBase = 0x8018EAA0;

    /// <summary>How far apart in model space a water model's faces may lie, in units.</summary>
    const int Flat = 8;

    static readonly Dictionary<(uint Face, uint Count, uint Verts), (int Faces, int Spread)> _known = new();
    static ulong _rectKey;
    static bool _listening;
    static readonly bool _probe = Environment.GetEnvironmentVariable("KF2_MODELWATER_PROBE") is { } p && p != "" && p != "0";

    /// <summary>Decisions taken, and the models found to be water; never reset.</summary>
    public static long Decided, Water;

    public static bool Is(PSMemory mem, ModelKind kind, int model, uint sub, uint vertices, bool rigid)
    {
        if (SurfaceMaterial.RectN == 0 || !_probe && (kind != ModelKind.Object || !rigid)) return false;
        if (!_listening)
        {
            Event.AddListener<OverlayLoadedEvent>(_ => _known.Clear());
            _listening = true;
        }
        ulong rk = RectKey();
        if (rk != _rectKey) { _known.Clear(); _rectKey = rk; }

        uint table = mem.ReadU32(PolyModelTable);
        uint header = (sub & 0xFFFFu) * 28u + 0xCu + table;
        uint count = mem.ReadU32(header + 0x14u);
        uint face = mem.ReadU32(header + 0x10u) + 0xCu + table;
        uint verts = mem.ReadU32(VertexBase);
        var key = (face, count, verts);
        bool fresh = !_known.TryGetValue(key, out var m);
        if (fresh) _known[key] = m = Measure(mem, face, count, verts, vertices);
        bool water = kind == ModelKind.Object && rigid && m.Faces > 0 && m.Spread <= Flat;
        if (fresh)
        {
            Decided++;
            if (water) Water++;
            if (_probe && m.Faces != 0)
                Console.WriteLine($"[KF2] model water: {kind} model {model} sub {sub}{(rigid ? "" : " (animated)")}: " +
                                  $"{(m.Faces < 0 ? "unreadable" : $"{m.Faces} face(s) in the water's texture, {m.Spread} units high")}, " +
                                  $"{(water ? "water" : "not water")}");
        }
        return water;
    }

    /// <summary>The averaging faces on the water's rects, and how far apart in height
    /// their corners lie; -1 faces if the model cannot be read.</summary>
    static (int Faces, int Spread) Measure(PSMemory mem, uint face, uint count, uint verts, uint vertices)
    {
        if (count == 0 || count > 4096 || vertices == 0 || vertices > 8192
            || !InRam(face, 4) || !InRam(verts, vertices * 8u)) return (-1, 0);
        int faces = 0;
        Span<uint> idx = stackalloc uint[4];
        int lo = int.MaxValue, hi = int.MinValue;
        for (uint i = 0; i < count; i++)
        {
            if (!InRam(face, 4)) return (-1, 0);
            uint word = mem.ReadU32(face);
            uint f = face + 4u;
            uint cmd = word >> 24;
            face = f + ((word >> 6) & 0x3FCu);
            int corners;
            switch (cmd & 0xFDu)
            {
                case 0x24u: corners = 3; idx[0] = 0x0E; idx[1] = 0x10; idx[2] = 0x12; break;
                case 0x2Cu: corners = 4; idx[0] = 0x12; idx[1] = 0x14; idx[2] = 0x16; idx[3] = 0x18; break;
                case 0x34u: corners = 3; idx[0] = 0x0E; idx[1] = 0x12; idx[2] = 0x16; break;
                case 0x3Cu: corners = 4; idx[0] = 0x12; idx[1] = 0x16; idx[2] = 0x1A; idx[3] = 0x1E; break;
                default: continue;
            }
            if ((cmd & 2u) == 0) continue;
            uint tpage = mem.ReadU16(f + 6u) & 0x1FFu;
            uint blend = (tpage >> 5) & 3u;
            if (blend is not (0u or 3u)) continue;
            int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
            for (int k = 0; k < corners; k++)
            {
                uint uv = mem.ReadU16(f + (uint)(k == 3 ? 0xC : k * 4));
                int u = (int)(uv & 0xFF), v = (int)(uv >> 8);
                u0 = Math.Min(u0, u); v0 = Math.Min(v0, v); u1 = Math.Max(u1, u); v1 = Math.Max(v1, v);
            }
            if (!OnWaterRect(tpage, u0, v0, u1, v1)) continue;
            faces++;
            for (int k = 0; k < corners; k++)
            {
                uint off = mem.ReadU16(f + idx[k]);
                if ((off & 7u) != 0 || (off >> 3) >= vertices) return (-1, 0);
                int y = (short)mem.ReadU16(verts + off + 2u);
                lo = Math.Min(lo, y); hi = Math.Max(hi, y);
            }
        }
        return (faces, faces > 0 ? hi - lo : 0);
    }

    static bool OnWaterRect(uint tpage, int u0, int v0, int u1, int v1)
    {
        int mode = (int)(tpage >> 7) & 3;
        int div = mode == 0 ? 4 : mode == 1 ? 2 : 1;
        int x0 = (int)(tpage & 0xF) * 64 + u0 / div, x1 = (int)(tpage & 0xF) * 64 + u1 / div + 1;
        int y0 = (int)((tpage >> 4) & 1) * 256 + v0, y1 = (int)((tpage >> 4) & 1) * 256 + v1 + 1;
        for (int i = 0; i < SurfaceMaterial.RectN; i++)
        {
            ref var r = ref SurfaceMaterial.Rects[i];
            if (x0 >= r.X + r.W || x1 <= r.X || y0 >= r.Y + r.H || y1 <= r.Y) continue;
            if (r.Material == SurfaceMaterial.Water) return true;
        }
        return false;
    }

    static ulong RectKey()
    {
        ulong h = (ulong)SurfaceMaterial.RectN * 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < SurfaceMaterial.RectN; i++)
        {
            ref var r = ref SurfaceMaterial.Rects[i];
            h = (h ^ (uint)(r.X | r.Y << 16)) * 0x100000001B3UL;
            h = (h ^ (uint)(r.W | r.H << 16 | r.Material << 28)) * 0x100000001B3UL;
        }
        return h;
    }

    static bool InRam(uint a, uint bytes)
    {
        uint lo = a - 0x80000000u;
        return a >= 0x80000000u && lo + bytes <= Runtime.RamSize && (a & 3u) == 0;
    }
}
