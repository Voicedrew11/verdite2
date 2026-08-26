using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Carries everything in the world that moves between logic ticks, the way
/// <see cref="FrameSmoothing"/> carries the view.
///
///     KF2_SMOOTH_OBJECTS=1       on; off by default
///     KF2_SMOOTH_OBJECTS_PROBE=1 how much is being carried, per second
///
/// It is a setting under Video, beside the two frame-smoothing checkboxes.
///
/// ## Why the camera alone was not enough
///
/// With the world on <see cref="FramePacing.LogicHz"/> and the picture drawn
/// faster, <see cref="FrameSmoothing"/> makes the *camera* move every frame. That
/// is most of the picture, because most of the picture is architecture that never
/// moves -- reproject it through a camera that did and it is smooth. What it does
/// not cover is anything whose own position advances on the tick: an enemy walking
/// towards you keeps the right speed but arrives in 50 ms steps, and against a
/// world sliding smoothly past it the step is *more* obvious than it would be if
/// nothing were smoothed at all. Reported from play at 60 fps against a 20 Hz
/// world: "the enemies move at the correct speed, but they are animated at a
/// visibly lower framerate."
///
/// ## What moves, and where
///
/// One table: **`0x80177714`, 396 slots of `0x44` bytes**, with the position a
/// `VECTOR` at `+0x14` and the slot free when the byte at `+0x4` is `0xFF`. Those
/// are the same constants `patches/AgentServer.cs` reports `nearby` from.
///
/// It is the *object* table rather than the 200-record entity table at
/// `0x8016C544`, and that distinction is load-bearing: the entity record is AI
/// state, and stage 4 (`func_80040348`) *copies* the object's position into it at
/// `rec+0x2C` each tick rather than the other way round. The renderer never reads
/// the entity record -- measured, by handing `func_80032588` (the model submitter,
/// under stage 13's world walk) its arguments: it is called with `a2` pointing at
/// `0x80177714 + slot*0x44 + 0x14`, and the slot numbers match what `nearby`
/// reports. So the object table is where a drawn position lives, and carrying it
/// is what moves the picture.
///
/// ## Extrapolate, because the camera does
///
/// **This interpolated at first, and that was wrong -- not in itself, but next to
/// the camera.** The reasoning for interpolating was sound on its own terms:
/// nothing in this table is steered by the player, so a tick of latency is free,
/// and walking between two positions the game actually produced cannot overshoot
/// the way <c>KF2_SMOOTH_POS</c> can. What it missed is that
/// <see cref="FrameSmoothing"/> carries the view *forward*, to `t + frac`, while
/// interpolating draws an object at `t - 1 + frac`. The two are then a whole tick
/// apart -- 50 ms at the default rate -- and a constant offset between the world
/// and the things standing in it does not read as latency, it reads as **the
/// objects moving more slowly than everything else**. Which is what it was
/// reported as: "the enemies still move visibly slower than the compass."
///
/// So it extrapolates, on the same clock and by the same fraction as the view.
/// The overshoot that argument was avoiding is real but bounded and brief: an
/// object that stops or turns is carried at most one tick past where it went and
/// is corrected on the next one. Being on a different clock from the camera is
/// neither bounded nor brief.
///
/// The guard against a *placement* below is what makes this safe to do forward at
/// all -- an extrapolated teleport would fling an object a whole area's width.
///
/// ## Why it cannot leak
///
/// The same shape as <see cref="FrameSmoothing"/>: a pre-hook writes, a post-hook
/// puts back exactly what was there, and the pair brackets **stage 13**
/// (`func_800342D8`) -- the renderer, and the only filler of the display list. The
/// loop-state census in docs/GAME_INTERNALS.md has stage 13 writing nothing but
/// the display list, so the interpolated positions exist for the length of one
/// function call and are gone before the next tick's AI, a save, or a proximity
/// trigger can see them. The hook is on stage 13 rather than on stage 8 because it
/// is the renderer that reads these, not the camera builder.
/// </summary>
public static class ObjectSmoothing
{
    /// <summary>Stage 13, the renderer. The bracket.</summary>
    const uint Renderer = 0x800342D8;

    // The object table, as patches/AgentServer.cs reads it for `nearby`.
    const uint Table = 0x80177714;
    const int Stride = 0x44;
    const int Count = 0x18C;
    const int EmptyOff = 0x4;     // byte == 0xFF when the slot is free
    const int PosOff = 0x14;      // VECTOR: three s32

    /// <summary>
    /// Units on one axis in one tick past which a slot is treated as having been
    /// *placed* rather than having moved, and is left where the game put it.
    ///
    /// Without it an object that is teleported -- spawned, respawned, moved by a
    /// script, or simply re-placed when the area finished loading -- gets swept
    /// smoothly across the map over the next tick instead of appearing. Measured
    /// on the way in: real motion in a quiet area was **0 units of XZ a tick**
    /// (the four things moving were bobbing in Y), while one window caught a
    /// **233,472-unit** step, which is most of an area. The player, the fastest
    /// thing in the game, covers 1817 units in 2 s at 20 Hz -- about **45 units a
    /// tick** -- so 1024 sits some twenty times above anything that walks and two
    /// hundred times below the placement it has to catch.
    /// </summary>
    const int TeleportUnits = 1024;

    public const string OnKey = "kf2.smoothing.objects";

    /// <summary>Carry object positions between ticks. **Off by default**, the house
    /// rule for a mechanism that has been measured and whose picture has not been
    /// looked at.</summary>
    public static bool Enabled { get; private set; }

    static bool _onFromEnv;

    // Last tick's and this tick's positions, three s32 a slot. Sampled at the head
    // of a frame the world advanced on, so `_cur` is always what the game most
    // recently wrote and `_prev` is what it wrote the tick before.
    static readonly int[] _prev = new int[Count * 3];
    static readonly int[] _cur = new int[Count * 3];

    /// <summary>Whether a slot had a position in both samples, so there is
    /// something to walk between. A slot that has just appeared interpolates from
    /// nothing and must be left where it is.</summary>
    static readonly bool[] _live = new bool[Count];

    /// <summary>True once both samples exist. Cleared when an area loads, because
    /// the table is rebuilt and last area's positions are meaningless.</summary>
    static bool _primed;

    // What the pre-hook overwrote, so the post-hook can put it back. One call in
    // flight at a time, on one thread.
    static readonly int[] _saved = new int[Count * 3];
    static readonly bool[] _touched = new bool[Count];

    /// <summary>The X the pre-hook wrote, kept only so the probe can check that
    /// what the renderer left behind is still it -- a difference means something
    /// downstream wrote a position, which is the one failure this must not have.</summary>
    static readonly int[] _wrote = new int[Count];
    static bool _applied;

    // ---- the probe ------------------------------------------------------------

    static bool _probe;
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _reportedAt;
    static long _frames, _carriedFrames, _carriedSlots, _mismatches;
    static double _moveSum, _fracSum;
    static int _biggestStep;
    static long _teleports;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.objectsmoothing",
        Name = "Object smoothing",
        Version = "1.0",
        Description = "Carries object positions between the game's logic ticks.",
    };

    public static void Configure(string? on, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on)) { Enabled = on != "0"; _onFromEnv = true; }
        _probe = probe == "1";
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            if (!_onFromEnv)
                Enabled = RecompOne.Runtime.Runtime.View.GetBool(OnKey, Enabled);
        });

        // An area swap rebuilds the table, so the previous sample describes objects
        // that no longer exist. Overlay loads cover both the executable swaps and
        // the fdat area modules, which is exactly the set that invalidates it.
        Event.AddListener<OverlayLoadedEvent>(_ => _primed = false);

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    public static void SetEnabled(bool on) => Enabled = on;

    static void Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("game", null, Renderer);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] objects: no game function at 0x{Renderer:X8}; " +
                                    "objects will step at the logic rate above it.");
            return;
        }

        var self = typeof(ObjectSmoothing);
        int n = 0;
        if (HookManager.AddPost(_self, target,
                self.GetMethod(nameof(After), BindingFlags.Public | BindingFlags.Static)!)) n++;
        if (HookManager.AddPre(_self, target,
                self.GetMethod(nameof(Before), BindingFlags.Public | BindingFlags.Static)!)) n++;

        HookManager.Commit();

        // Half a pair would leave interpolated positions in the table for the AI to
        // find, which is the one outcome this must never have.
        if (n < 2)
        {
            Enabled = false;
            Console.Error.WriteLine("[KF2] objects: only half the pair attached; " +
                                    "the interpolation is disabled rather than left applied.");
        }
        else
        {
            Console.WriteLine($"[KF2] objects: {(Enabled ? "on" : "off")}, " +
                              $"hooked stage 13 at 0x{Renderer:X8}");
        }
    }

    /// <summary>
    /// Sample on a frame the world advanced on, then walk every object the fraction
    /// of a tick this frame stands at.
    /// </summary>
    public static void Before(CpuContext c, IMemory m)
    {
        _applied = false;
        if (!Enabled || !FramePacing.Gating) return;

        if (_probe) _frames++;

        if (FramePacing.TickedThisFrame) Sample(m);
        if (!_primed) return;

        double frac = FramePacing.LogicPhase;
        if (frac <= 0.0005) return;

        int carried = 0;
        double moved = 0.0;

        for (int i = 0; i < Count; i++)
        {
            if (!_live[i]) continue;

            int b = i * 3;
            int dx = _cur[b] - _prev[b], dy = _cur[b + 1] - _prev[b + 1], dz = _cur[b + 2] - _prev[b + 2];
            if (dx == 0 && dy == 0 && dz == 0) continue;

            if (Math.Abs(dx) > TeleportUnits || Math.Abs(dy) > TeleportUnits ||
                Math.Abs(dz) > TeleportUnits)
            {
                if (_probe) _teleports++;
                continue;
            }

            // Carried *forward* from where the game has put it, by the part of the
            // tick this frame stands at, on the assumption the object keeps doing
            // what it did last tick. Same clock as the camera -- see the class
            // comment for why that matters more than the overshoot does.
            int x = _cur[b] + (int)Math.Round(dx * frac);
            int y = _cur[b + 1] + (int)Math.Round(dy * frac);
            int z = _cur[b + 2] + (int)Math.Round(dz * frac);

            uint pos = (uint)(Table + i * Stride + PosOff);
            _saved[b] = (int)m.ReadU32(pos);
            _saved[b + 1] = (int)m.ReadU32(pos + 4u);
            _saved[b + 2] = (int)m.ReadU32(pos + 8u);
            _touched[i] = true;
            _applied = true;

            _wrote[i] = x;
            m.WriteU32(pos, (uint)x);
            m.WriteU32(pos + 4u, (uint)y);
            m.WriteU32(pos + 8u, (uint)z);

            carried++;
            if (_probe)
            {
                moved += Math.Abs(dx * frac) + Math.Abs(dy * frac) + Math.Abs(dz * frac);
                int step = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                if (step > _biggestStep) _biggestStep = step;
            }
        }

        if (_probe && carried > 0)
        {
            _carriedFrames++;
            _carriedSlots += carried;
            _moveSum += moved / carried;
            _fracSum += frac;
        }
    }

    /// <summary>
    /// Put the table back the moment the renderer has read it, so that the next
    /// tick's AI, a proximity trigger and a save all see exactly what the game
    /// wrote.
    /// </summary>
    public static void After(CpuContext c, IMemory m)
    {
        if (_applied)
        {
            for (int i = 0; i < Count; i++)
            {
                if (!_touched[i]) continue;
                _touched[i] = false;

                int b = i * 3;
                uint pos = (uint)(Table + i * Stride + PosOff);

                // The probe's own check that nothing leaks: what the renderer left
                // behind must be what the pre-hook wrote, or something downstream
                // has been writing to a position that was not the game's.
                if (_probe && (int)m.ReadU32(pos) != _wrote[i]) _mismatches++;

                m.WriteU32(pos, (uint)_saved[b]);
                m.WriteU32(pos + 4u, (uint)_saved[b + 1]);
                m.WriteU32(pos + 8u, (uint)_saved[b + 2]);
            }
            _applied = false;
        }

        if (_probe) Report();
    }

    /// <summary>
    /// Roll this tick's positions into last tick's and re-read the table. A slot
    /// that was free in either sample is not live, so an object that has just been
    /// spawned is drawn where the game put it rather than swept in from wherever
    /// the slot's previous tenant died.
    /// </summary>
    static void Sample(IMemory m)
    {
        for (int i = 0; i < Count; i++)
        {
            int b = i * 3;
            bool wasFree = m.ReadU8((uint)(Table + i * Stride + EmptyOff)) == 0xFF;

            _prev[b] = _cur[b];
            _prev[b + 1] = _cur[b + 1];
            _prev[b + 2] = _cur[b + 2];

            if (wasFree)
            {
                _live[i] = false;
                continue;
            }

            uint pos = (uint)(Table + i * Stride + PosOff);
            _cur[b] = (int)m.ReadU32(pos);
            _cur[b + 1] = (int)m.ReadU32(pos + 4u);
            _cur[b + 2] = (int)m.ReadU32(pos + 8u);

            // Live only once the slot has been occupied for two samples running,
            // which is also what makes the first sample after an area load safe.
            _live[i] = _primed;
        }

        _primed = true;
    }

    static void Report()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _reportedAt < 2000.0) return;
        _reportedAt = now;

        if (_frames == 0) return;

        if (_carriedFrames == 0)
            Console.WriteLine($"[KF2] objects: 0 of {_frames} frames carried -- " +
                              $"{(FramePacing.Extrapolating ? "nothing moving" : "not extrapolating")}");
        else
            Console.WriteLine($"[KF2] objects: {_carriedFrames}/{_frames} frames carried, " +
                              $"{(double)_carriedSlots / _carriedFrames:0.0} object(s) each, " +
                              $"mean phase {_fracSum / _carriedFrames:0.00} tick, " +
                              $"offset {_moveSum / _carriedFrames:0.0} u, " +
                              $"biggest tick step {_biggestStep} u" +
                              (_teleports > 0 ? $", {_teleports} placement(s) left alone" : "") +
                              (_mismatches > 0 ? $", {_mismatches} LEAKED" : ""));

        _frames = _carriedFrames = _carriedSlots = _mismatches = 0;
        _moveSum = _fracSum = 0.0;
        _biggestStep = 0;
        _teleports = 0;
    }
}
