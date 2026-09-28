using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// Draws the map further than the game's 24x24 visibility window reaches.
///
///     KF2_RENDERDIST=13.5       the far edge in tiles (10.5, the game's, is off)
///     KF2_RENDERDIST_PROBE=1    a line every 2 s: cells added, halves drawn, models let through
///     KF2_RENDERDIST_PROBE=2    also the box round the camera: '#' the game's cells, '+' added, '.' in the cone and dark
///
/// The game's own grid is left exactly as it built it. Past its cone the port adds
/// cells of its own: inside the cone's side lines carried out to <see cref="Tiles"/>,
/// lit only where a neighbour nearer the camera is lit and the eye's level has a
/// half there, so the game's flood is continued outward rather than replaced. They
/// are drawn by <see cref="TileWalk"/> after the game's cells, through the far
/// assembler, and <see cref="ModelWalk"/>'s visibility queries answer for them.
///
/// The walk hands a tile's offset from the camera to the GTE as an s16, so no tile
/// more than <see cref="Reach"/> tiles out on either axis can be placed. The game's
/// fog is left alone: in a fogged area what is added is drawn black.
///
/// See "Render distance" in docs/WIDESCREEN.md.
/// </summary>
public static class RenderDistance
{
    public const string Key = "kf2.renderdistance";

    /// <summary>The game's cone reaches 2688/256 tiles, level.</summary>
    public const float Stock = 10.5f;

    /// <summary>Tiles either side of the camera a tile can be placed at.</summary>
    public const int Reach = 15;
    public const float Max = Reach;

    const int Side = 2 * Reach + 1;

    const uint Grid = 0x80192EAC, GridOriginX = 0x80192EA0, GridOriginZ = 0x80192EA4;
    const uint CamWorldX = 0x80192E78, CamWorldZ = 0x80192E80;
    const uint MapBase = 0x801C8484;

    /// <summary>The side lines' slope and apex behind the camera, from the table:
    /// (2304 - 256) / (2688 + 128) per tile of depth, meeting 1.875 tiles back.</summary>
    public const float Slope = 0.727f, Apex = 1.875f;

    static float? _forced;
    static bool _probe, _map;

    /// <summary>The far edge in tiles; <see cref="Stock"/> is the game's own.</summary>
    public static float Tiles { get; private set; } = Stock;

    public static bool On => Tiles > Stock + 0.01f;

    /// <summary>This frame added cells; the walks and the ordering-table clamp read it.</summary>
    public static bool Any => _cells.Count > 0;

    // Flags per map tile for the cells added this frame, and the list to clear them by.
    static readonly byte[] _far = new byte[80 * 80];
    static readonly List<int> _cells = new();
    // The camera-centred box the flood is continued in: every cell's draw bits.
    static readonly byte[] _box = new byte[Side * Side];
    static readonly (int dx, int dz)[] _order = BuildOrder();
    static readonly char[] _glyph = new char[Side * Side];

    static long _frames, _added, _drawn, _models, _clamped;
    static double _probeAt;

    public static void Configure(string? tiles, string? probe)
    {
        if (float.TryParse(tiles, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float t))
            _forced = Math.Clamp(t, Stock, Max);
        _probe = probe is not (null or "" or "0");
        _map = probe == "2";
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Tiles = _forced ?? Math.Clamp(RecompOne.Runtime.Runtime.View.GetFloat(Key, Stock), Stock, Max);
            Console.WriteLine($"[KF2] render distance: {(On ? $"{Tiles:0.#} tiles" : "the game's")}");
        });
    }

    public static void Set(float tiles) => Tiles = Math.Clamp(tiles, Stock, Max);

    static (int, int)[] BuildOrder()
    {
        var list = new List<(int, int)>();
        for (int dz = -Reach; dz <= Reach; dz++)
            for (int dx = -Reach; dx <= Reach; dx++)
                list.Add((dx, dz));
        // A parent is one step nearer on an axis, so it comes first.
        list.Sort((a, b) => (Math.Abs(a.Item1) + Math.Abs(a.Item2)).CompareTo(Math.Abs(b.Item1) + Math.Abs(b.Item2)));
        return list.ToArray();
    }

    /// <summary>From the start of the frame's own tile walk, after the game built its grid.</summary>
    public static void Build(PSMemory mem)
    {
        foreach (int i in _cells) _far[i] = 0;
        _cells.Clear();
        if (!On || !ViewCone(mem, out float cx, out float cz, out float fx, out float fz, out float stockFar)) return;

        int ctx = (int)(mem.ReadU32(CamWorldX) >> 11), ctz = (int)(mem.ReadU32(CamWorldZ) >> 11);
        float slope = Slope * CullCone.Factor;

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

        foreach (var (dx, dz) in _order)
        {
            int bi = (dz + Reach) * Side + dx + Reach;
            int tx = ctx + dx, tz = ctz + dz;
            if ((uint)tx >= 80u || (uint)tz >= 80u) { _box[bi] = 0; continue; }

            uint col = ((uint)tx - originX) & 0xFFu;
            int row = tz - originZ;
            bool inWindow = col < 24u && (uint)row < 24u;
            byte stock = inWindow ? (byte)(mem.ReadU8(Grid + (uint)row * 24u + col) & 3u) : (byte)0;

            float px = tx + 0.5f - cx, pz = tz + 0.5f - cz;
            float d = px * fx + pz * fz;
            float l = MathF.Abs(px * fz - pz * fx);
            bool cone = d > 0f && d <= Tiles + 0.5f && l <= slope * (d + Apex) + 1f;
            bool added = cone && (!inWindow || d > stockFar);
            if (_map) _glyph[bi] = stock != 0 ? '#' : cone && added ? '.' : ' ';
            if (!added) { _box[bi] = stock; continue; }

            int sx = -Math.Sign(dx), sz = -Math.Sign(dz);
            byte parents = 0;
            if (sx != 0) parents |= _box[bi + sx];
            if (sz != 0) parents |= _box[bi + sz * Side];
            if (sx != 0 && sz != 0) parents |= _box[bi + sz * Side + sx];

            byte v = (parents & marker) != 0
                     && mem.ReadU8(MapBase + (uint)tz * 800u + (uint)tx * 10u + halfOff) != 0xFF ? marker : (byte)0;
            _box[bi] = (byte)(stock | v);
            if (v != 0 && stock == 0)
            {
                if (_map) _glyph[bi] = '+';
                int ti = tz * 80 + tx;
                _far[ti] = v;
                _cells.Add(ti);
            }
        }

        _frames++;
        _added += _cells.Count;
    }

    /// <summary>The camera in tiles, the unit heading of the game's last cone and how
    /// far along it the cone reached. A cell at offset (px, pz) from the camera is in
    /// the cone at depth d = p.f while |p x f| &lt;= Slope * Factor * (d + Apex) + 1.</summary>
    public static bool ViewCone(PSMemory mem, out float cx, out float cz, out float fx, out float fz, out float far)
    {
        cx = mem.ReadU32(CamWorldX) / 2048f;
        cz = mem.ReadU32(CamWorldZ) / 2048f;
        fx = fz = far = 0f;
        if (CullCone.StockBuilds == 0) return false;
        var sc = CullCone.StockCorners;
        float flx = sc[0] / 4096f, flz = sc[1] / 4096f, frx = sc[2] / 4096f, frz = sc[3] / 4096f;
        float nrx = sc[4] / 4096f, nrz = sc[5] / 4096f, nlx = sc[6] / 4096f, nlz = sc[7] / 4096f;
        fx = (flx + frx - nrx - nlx) * 0.5f;
        fz = (flz + frz - nrz - nlz) * 0.5f;
        float len = MathF.Sqrt(fx * fx + fz * fz);
        if (!(len > 0.01f)) return false;
        fx /= len; fz /= len;
        far = ((flx + frx) * 0.5f - cx) * fx + ((flz + frz) * 0.5f - cz) * fz;
        return true;
    }

    /// <summary>After the game's cells: the added ones, through func_80031B1C, with
    /// no assembler bits, so each half goes to the far assembler.</summary>
    public static void Walk(CpuContext c, PSMemory mem)
    {
        foreach (int ti in _cells)
        {
            Interrupts.Poll(c, mem);
            c.A0 = (uint)(ti % 80);
            c.A1 = (uint)(ti / 80);
            c.A2 = _far[ti];
            c.RA = 0x80031D14u;
            KingsField2.func_80031B1C(c, mem);
            _drawn++;
        }
    }

    /// <summary>The added cell's bits at a world position (x at +0, z at +8), for a
    /// visibility query the game's grid answered 0.</summary>
    public static uint Point(PSMemory mem, uint pos)
    {
        uint tx = mem.ReadU32(pos) >> 11, tz = mem.ReadU32(pos + 8u) >> 11;
        if (tx >= 80u || tz >= 80u) return 0;
        uint v = _far[tz * 80u + tx];
        if (v != 0) _models++;
        return v;
    }

    /// <summary>The same over the box func_80032DE8 asks about.</summary>
    public static uint Box(PSMemory mem, uint pos, uint radius)
    {
        int tx = (int)(mem.ReadU32(pos) >> 11), tz = (int)(mem.ReadU32(pos + 8u) >> 11), r = (int)radius;
        uint acc = 0;
        for (int z = Math.Max(tz - r, 0); z < Math.Min(tz + r, 80); z++)
            for (int x = Math.Max(tx - r, 0); x < Math.Min(tx + r, 80); x++)
                acc |= _far[z * 80 + x];
        if (acc != 0) _models++;
        return acc;
    }

    /// <summary>A far packet past the ordering table's end, placed at its last slot
    /// but one (the last is the sky's) instead of dropped.</summary>
    public static void Clamped() => _clamped++;

    public static void Report()
    {
        if (!_probe) return;
        double now = Environment.TickCount64 / 1000.0;
        if (_probeAt == 0) { _probeAt = now; return; }
        double span = now - _probeAt;
        if (span < 2.0) return;
        double f = Math.Max(_frames, 1);
        Console.WriteLine($"[renderdist] {Tiles:0.#} tiles: {_added / f:F1} cells added a frame, " +
                          $"{_drawn / f:F1} walked, {_models / f:F1} model queries let through, " +
                          $"{_clamped / f:F1} packets clamped to the table's end");
        if (_map)
        {
            // Row per z, north up; the camera is the centre.
            var sb = new System.Text.StringBuilder();
            for (int z = Side - 1; z >= 0; z--)
            {
                for (int x = 0; x < Side; x++)
                    sb.Append(z == Reach && x == Reach ? '@' : _glyph[z * Side + x]);
                sb.Append('\n');
            }
            Console.Write(sb.ToString());
        }
        _probeAt = now;
        _frames = _added = _drawn = _models = _clamped = 0;
    }
}
