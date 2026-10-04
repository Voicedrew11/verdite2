using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// What the reflections may show, grown past what the camera sees and held over time.
///
///     KF2_REFLECT_REACH=2         cells to grow the set by (0 is the frame's own halves, as before)
///     KF2_REFLECT_REACH_PROBE=1   a line every 2 s: halves drawn, grown, held, fading, models let through, entered in view
///
/// A reflection looks from under the water, so it sees what the game's visibility
/// flood culled for the eye: the inside of a cavern just round a cliff, a creature
/// whose cell is dark. It used to show only what the frame drew, so those popped into
/// the water as the eye turned. Each frame the halves the tile walk drew are grown by
/// <see cref="Reach"/> cells on their own level, through halves that are drawn at all,
/// and a half stays in the set for <see cref="Hold"/> seconds after it leaves,
/// fading in and out over <see cref="FadeSeconds"/> (a dither in the world program).
///
/// The retained scene takes the weights as its half gate. Creatures, objects, effects
/// and sprites standing in the set are submitted by the object walk, which the
/// retained scene captures; they are drawn in the picture too, where the walls in
/// front of them hide them. The planar walk no longer reads it: it has a cull of its
/// own (<see cref="PlanarCull"/>). See "The reflections see past the camera's cull"
/// in docs/RENDERING.md.
/// </summary>
public static class ReflectionReach
{
    public const string Key = "kf2.reflectreach";
    public const int Max = 4;
    public const int DefaultReach = 0;

    /// <summary>How long a half stays after the set loses it, and how long a fade takes.</summary>
    public const double Hold = 0.75, FadeSeconds = 0.3;

    const uint MapBase = 0x801C8484;

    const int Halves = 80 * 80 * 2;

    static int? _forced;
    static bool _probe;

    public static int Reach { get; private set; }

    /// <summary>This frame's set holds halves beyond the frame's own.</summary>
    public static bool Any { get; private set; }

    static readonly int[] _drawn = new int[Halves];     // frame stamp a half was drawn in
    static readonly int[] _seen = new int[Halves];      // frame stamp a half was reached in
    static readonly byte[] _dist = new byte[Halves];
    static readonly double[] _last = new double[Halves];  // when a half was last in the set
    static readonly float[] _weight = new float[Halves];
    static readonly bool[] _listed = new bool[Halves];
    static readonly List<int> _drawnList = new(), _active = new(), _extraTiles = new();
    static readonly Queue<int> _queue = new();
    static readonly byte[] _tileBits = new byte[80 * 80];
    static int _stamp;
    static double _lastBuild;

    static long _frames, _drawnSum, _grownSum, _heldSum, _fadingSum, _models;
    static double _probeAt;

    public static void Configure(string? reach, string? probe)
    {
        if (int.TryParse(reach, out int r)) _forced = Math.Clamp(r, 0, Max);
        _probe = probe is not (null or "" or "0");
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            // No longer a setting: the planar walk has a cull of its own (PlanarCull),
            // and KF2_REFLECT_REACH is the retained scene's comparison.
            Reach = _forced ?? DefaultReach;
            Console.WriteLine($"[KF2] reflection reach: {(Reach > 0 ? $"{Reach} cell(s), held {Hold} s" : "the frame's own")}");
        });
        Event.AddListener<OverlayLoadedEvent>(_ => Forget());
    }

    public static void Set(int reach)
    {
        Reach = Math.Clamp(reach, 0, Max);
        if (Reach == 0) Forget();
    }

    static bool Wanted => Reach > 0 && RetainedMap.ReflectionsReady;

    /// <summary>A new area: nothing held from the last one.</summary>
    static void Forget()
    {
        foreach (int h in _active) { _weight[h] = 0; _listed[h] = false; _tileBits[h >> 1] = 0; }
        _active.Clear();
        _extraTiles.Clear();
        Any = false;
    }

    /// <summary>From <see cref="TileWalk"/>'s frame walk: a half it drew.</summary>
    public static void NoteDrawn(int tx, int tz, int upper)
    {
        if (!Wanted || (uint)tx >= 80u || (uint)tz >= 80u) return;
        int h = (tz * 80 + tx) * 2 + upper;
        if (_drawn[h] == _stamp + 1) return;
        _drawn[h] = _stamp + 1;
        _drawnList.Add(h);
    }

    static bool Drawable(PSMemory mem, int h) =>
        mem.ReadU8(MapBase + (uint)(h >> 1) / 80u * 800u + (uint)(h >> 1) % 80u * 10u + (uint)(h & 1) * 5u) < 240;

    /// <summary>After the frame's own walk: grow, hold, fade, and publish.</summary>
    public static void Build(PSMemory mem)
    {
        _stamp++;
        if (!Wanted)
        {
            if (_active.Count > 0) Forget();
            _drawnList.Clear();
            return;
        }

        double now = Environment.TickCount64 / 1000.0;
        float step = (float)(Math.Clamp(now - _lastBuild, 0.0, 0.1) / FadeSeconds);
        _lastBuild = now;

        // Grow: breadth first from every half drawn, on its own level, never past
        // what a tile can be placed at (RenderDistance.Reach).
        _viewOk = RenderDistance.ViewCone(mem, out float cx, out float cz, out _vfx, out _vfz, out float stockFar);
        _vcx = cx; _vcz = cz;
        _vfar = Math.Max(RenderDistance.Tiles, stockFar) + 0.5f;
        _vslope = RenderDistance.Slope * CullCone.Factor;
        int ctx = (int)MathF.Floor(cx), ctz = (int)MathF.Floor(cz);
        int grown = 0, drawn = _drawnList.Count;
        foreach (int h in _drawnList)
        {
            _seen[h] = _stamp;
            _dist[h] = 0;
            _queue.Enqueue(h);
        }
        while (_queue.Count > 0)
        {
            int h = _queue.Dequeue();
            Reached(h, now);
            int d = _dist[h];
            if (d >= Reach) continue;
            int tile = h >> 1, tx = tile % 80, tz = tile / 80, up = h & 1;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = tx + dx, z = tz + dz;
                    if ((dx | dz) == 0 || (uint)x >= 80u || (uint)z >= 80u) continue;
                    if (Math.Abs(x - ctx) > RenderDistance.Reach || Math.Abs(z - ctz) > RenderDistance.Reach) continue;
                    int n = (z * 80 + x) * 2 + up;
                    if (_seen[n] == _stamp || !Drawable(mem, n)) continue;
                    _seen[n] = _stamp;
                    _dist[n] = (byte)(d + 1);
                    _queue.Enqueue(n);
                    grown++;
                }
        }
        _drawnList.Clear();

        // Hold and fade. A half the frame drew is at full weight at once: the picture
        // shows it, so the reflection does.
        int held = 0, fading = 0;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            int h = _active[i];
            bool inSet = now - _last[h] < Hold;
            float w = _drawn[h] == _stamp ? 1f
                    : Math.Clamp(_weight[h] + (inSet ? step : -step), 0f, 1f);
            _weight[h] = w;
            if (_seen[h] != _stamp && inSet) held++;
            if (w > 0f && w < 1f) fading++;
            if (w <= 0f && !inSet)
            {
                _listed[h] = false;
                _active[i] = _active[^1];
                _active.RemoveAt(_active.Count - 1);
            }
        }

        // Publish: the retained scene's gate and the model queries.
        var gate = RetainedMap.ReflectionsReady ? RetainedScene.CurrentHalves : Span<byte>.Empty;
        foreach (int t in _extraTiles) _tileBits[t] = 0;
        _extraTiles.Clear();
        foreach (int h in _active)
        {
            float w = _weight[h];
            if (w <= 0f) continue;
            if (!gate.IsEmpty) gate[h] = (byte)Math.Clamp((int)(w * 255f + 0.5f), 1, 255);
            if (_drawn[h] == _stamp) continue;
            int t = h >> 1;
            if (_tileBits[t] == 0) _extraTiles.Add(t);
            _tileBits[t] |= (byte)(1 << (h & 1));
        }
        Any = _extraTiles.Count > 0;

        _frames++;
        _drawnSum += drawn;
        _grownSum += grown;
        _heldSum += held;
        _fadingSum += fading;
        Report();
    }

    static void Reached(int h, double now)
    {
        if (!_listed[h])
        {
            _listed[h] = true;
            _active.Add(h);
            // A half entering the set inside the eye's own cone is one the mirror
            // may already be showing: it pops in.
            if (_probe && _viewOk && _drawn[h] != _stamp)
            {
                float px = (h >> 1) % 80 + 0.5f - _vcx, pz = (h >> 1) / 80 + 0.5f - _vcz;
                float d = px * _vfx + pz * _vfz;
                if (d > 0f && d <= _vfar && MathF.Abs(px * _vfz - pz * _vfx) <= _vslope * (d + RenderDistance.Apex))
                {
                    _enteredInView++;
                    _nearestEntry = Math.Min(_nearestEntry, d);
                }
            }
        }
        _last[h] = now;
    }

    static bool _viewOk;
    static float _vcx, _vcz, _vfx, _vfz, _vfar, _vslope;
    static long _enteredInView;
    static float _nearestEntry = float.MaxValue;

    /// <summary>The set's level bits at a world position (x at +0, z at +8), for a
    /// visibility query the game's grid answered 0.</summary>
    public static uint Point(PSMemory mem, uint pos)
    {
        if (!Any) return 0;
        uint tx = mem.ReadU32(pos) >> 11, tz = mem.ReadU32(pos + 8u) >> 11;
        if (tx >= 80u || tz >= 80u) return 0;
        uint v = _tileBits[tz * 80u + tx];
        if (v != 0) _models++;
        return v;
    }

    /// <summary>The same over the box func_80032DE8 asks about.</summary>
    public static uint Box(PSMemory mem, uint pos, uint radius)
    {
        if (!Any) return 0;
        int tx = (int)(mem.ReadU32(pos) >> 11), tz = (int)(mem.ReadU32(pos + 8u) >> 11), r = (int)radius;
        uint acc = 0;
        for (int z = Math.Max(tz - r, 0); z < Math.Min(tz + r, 80); z++)
            for (int x = Math.Max(tx - r, 0); x < Math.Min(tx + r, 80); x++)
                acc |= _tileBits[z * 80 + x];
        if (acc != 0) _models++;
        return acc;
    }

    static void Report()
    {
        if (!_probe) return;
        double now = Environment.TickCount64 / 1000.0;
        if (_probeAt == 0) { _probeAt = now; return; }
        if (now - _probeAt < 2.0) return;
        double f = Math.Max(_frames, 1);
        Console.WriteLine($"[reflectreach] {Reach} cell(s): {_drawnSum / f:F0} halves reflected the frame drew, " +
                          $"{_grownSum / f:F0} grown, {_heldSum / f:F0} held after leaving, {_fadingSum / f:F0} fading, " +
                          $"{_extraTiles.Count} extra tiles now, {_models / f:F1} model queries let through, " +
                          $"{_enteredInView / (now - _probeAt):F1}/s entered inside the view" +
                          (_enteredInView > 0 ? $", the nearest {_nearestEntry:F1} tiles deep" : ""));
        _probeAt = now;
        _frames = _drawnSum = _grownSum = _heldSum = _fadingSum = _models = _enteredInView = 0;
        _nearestEntry = float.MaxValue;
    }
}
