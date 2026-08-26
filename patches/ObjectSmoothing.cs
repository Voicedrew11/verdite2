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
/// ## What moves, and where -- two tables, because the renderer walks two
///
/// The model submitter `func_800331B4` has **two loops**, and they feed
/// `func_80032588` (under stage 13's world walk) from two different tables:
///
/// * **The object table -- `0x80177714`, 396 slots of `0x44`**, position a
///   `VECTOR` at `+0x14`, free when the byte at `+0x4` is `0xFF`. Static props,
///   doors and sprites. This is what a `KF2_DRAWCENSUS=2` reading of
///   `func_80032588`'s `a2` caught (`0x80177714 + slot*0x44 + 0x14`) -- but only
///   because the scene it measured had props and no creatures near.
/// * **The entity table -- `0x8016C544`, 200 slots of `0x7C`**, free when the byte
///   at `+0x0` is `0xFF`, position a `VECTOR` at `+0x2C` and a three-`s16`
///   rotation at `+0x40`. **Creatures/enemies.** The object table has no rotation
///   at all, so this loop is the only place an enemy's facing lives.
///
/// Both are the constants `patches/AgentServer.cs` reports `nearby` from
/// (`objects` and `entities`). The earlier belief that "the renderer reads the
/// object table and not the entity record" was right for props and wrong for
/// creatures: stage 4 (`func_80040348`) copies the object position into
/// `rec+0x2C` of the entity record, but the entity record is *also* what the first
/// loop draws creatures from, rotation included. Smoothing the object table alone
/// therefore left every enemy stepping in both position and facing -- the reported
/// jitter -- which is why this carries **both** tables, and the entity table's
/// rotation on top.
///
/// ## Interpolate, and so does the camera now
///
/// **This interpolated, then extrapolated, and now interpolates again -- and the
/// round trip is the point.** Interpolating was right on its own terms: nothing in
/// this table is steered by the player, so a tick of latency is free, and walking
/// between two positions the game actually produced cannot overshoot the way
/// extrapolation can. It was abandoned only because <see cref="FrameSmoothing"/>
/// *extrapolated* the view, to `t + frac`, while interpolating draws an object at
/// `t - 1 + frac`. The two were then a whole tick apart -- 50 ms at the default
/// rate -- and a constant offset between the world and the things standing in it
/// read not as latency but as **the objects moving more slowly than everything
/// else** ("the enemies still move visibly slower than the compass").
///
/// The camera now interpolates too, so the two are back on the same clock and at
/// the same instant (`t - 1 + frac`). Interpolation is the better of the two
/// wherever it can be afforded: it never predicts past a position the game
/// produced, so a creature that stops or turns simply stops -- no bounce-back on
/// the next tick, which is what forward extrapolation gave. The one thing it costs,
/// a tick of latency, is exactly what an unsteered object can spend for free.
///
/// The guard against a *placement* below is kept regardless: prev and cur can
/// straddle a spawn or a script-move even when interpolating, and lerping across
/// that would sweep an object a whole area's width over one tick.
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

    // The entity table -- creatures/enemies -- as patches/AgentServer.cs reads it
    // for `nearby` "entities". The renderer draws these in a *separate* loop from
    // the object table (func_800331B4's first loop over 0x8016C544, stride 0x7C),
    // reading the position from +0x2C and a rotation the object table has no
    // equivalent of. Neither steps through the object table, so smoothing that one
    // alone leaves every enemy jittering in place and turn.
    const uint EntityTable = 0x8016C544;
    const int EntityStride = 0x7C;
    const int EntityCount = 0xC8;
    const int EntityEmptyOff = 0x0;   // byte == 0xFF when the slot is free
    const int EntityPosOff = 0x2C;    // VECTOR: three s32
    const int EntityRotOff = 0x40;    // three s16, XYZ; the yaw lane is biased by
                                      // 0x800 downstream, half of AngleMod below

    /// <summary>One whole turn, in the entity rotation lanes' units. The 0x800 yaw
    /// bias the renderer applies is exactly half of this, which is the evidence a
    /// turn is 4096. The raw lanes are *not* confined to [0, AngleMod): the probe
    /// measures values above 0xFFF and just under 0x10000 -- small signed or
    /// accumulated angles -- so the interpolation works modulo AngleMod (the only
    /// part the GTE sees) and preserves the bits above it untouched on write.</summary>
    const int AngleMod = 0x1000;

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

    // The entity table's own last/this-tick samples: position (three s32) and
    // rotation (three s16, kept as raw int). Same priming and OverlayLoadedEvent
    // invalidation as the object arrays above.
    static readonly int[] _ePrev = new int[EntityCount * 3];
    static readonly int[] _eCur = new int[EntityCount * 3];
    static readonly int[] _ePrevRot = new int[EntityCount * 3];
    static readonly int[] _eCurRot = new int[EntityCount * 3];
    static readonly bool[] _eLive = new bool[EntityCount];

    static readonly int[] _eSaved = new int[EntityCount * 3];     // position, to restore
    static readonly int[] _eSavedRot = new int[EntityCount * 3];  // rotation, to restore
    static readonly bool[] _eTouched = new bool[EntityCount];
    static readonly int[] _eWrote = new int[EntityCount];         // wrote X, for the leak check

    // ---- the probe ------------------------------------------------------------

    static bool _probe;
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _reportedAt;
    static long _frames, _carriedFrames, _carriedSlots, _mismatches;
    static double _moveSum, _fracSum;
    static int _biggestStep;
    static long _teleports;

    // The entity pass's own counters.
    static long _eCarriedFrames, _eCarriedSlots, _eMismatches, _eTeleports;
    static double _eRotSum;
    static int _eBiggestAngleStep, _eMaxRawAngle;

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

        // Not gated on a small phase: interpolation must overwrite the table even at
        // frac ~= 0, because on a tick frame the table holds `_cur` (the new tick) and
        // the frame is meant to draw `_prev`. The per-slot dx==dy==dz==0 skip below
        // covers "nothing to do".
        double frac = FramePacing.LogicPhase;

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

            // Interpolated between the previous tick and this one -- _prev + delta *
            // frac, i.e. lerp(_prev, _cur, frac). Never past a position the game
            // actually produced, so it cannot overshoot on a stop or a turn. Same
            // clock as the camera, which now interpolates too -- see the class
            // comment.
            int x = _prev[b] + (int)Math.Round(dx * frac);
            int y = _prev[b + 1] + (int)Math.Round(dy * frac);
            int z = _prev[b + 2] + (int)Math.Round(dz * frac);

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

        // The entity table: creatures, carried in position *and* facing. Rotation
        // is the half the object table never had, and is why an enemy turning to
        // face you snapped a whole tick at a time even with objects smoothed.
        int eCarried = 0;
        int eMaxRaw = 0;
        double eRotMoved = 0.0;
        int mask = AngleMod - 1;

        for (int i = 0; i < EntityCount; i++)
        {
            if (!_eLive[i]) continue;

            int b = i * 3;
            int dx = _eCur[b] - _ePrev[b], dy = _eCur[b + 1] - _ePrev[b + 1], dz = _eCur[b + 2] - _ePrev[b + 2];
            int rdx = DeltaAngle(_ePrevRot[b], _eCurRot[b]);
            int rdy = DeltaAngle(_ePrevRot[b + 1], _eCurRot[b + 1]);
            int rdz = DeltaAngle(_ePrevRot[b + 2], _eCurRot[b + 2]);

            if (dx == 0 && dy == 0 && dz == 0 && rdx == 0 && rdy == 0 && rdz == 0) continue;

            // A step over the placement threshold is a spawn or a script move; leave
            // the whole slot -- position and facing both -- where the game put it.
            if (Math.Abs(dx) > TeleportUnits || Math.Abs(dy) > TeleportUnits ||
                Math.Abs(dz) > TeleportUnits)
            {
                if (_probe) _eTeleports++;
                continue;
            }

            int x = _ePrev[b] + (int)Math.Round(dx * frac);
            int y = _ePrev[b + 1] + (int)Math.Round(dy * frac);
            int z = _ePrev[b + 2] + (int)Math.Round(dz * frac);

            // Interpolate the low AngleMod bits along the shortest way round; keep
            // whatever sits above them, so a lane that turns out to be wider than
            // 12 bits is preserved rather than truncated.
            int rx = (_ePrevRot[b] + (int)Math.Round(rdx * frac)) & mask;
            int ry = (_ePrevRot[b + 1] + (int)Math.Round(rdy * frac)) & mask;
            int rz = (_ePrevRot[b + 2] + (int)Math.Round(rdz * frac)) & mask;

            uint pos = (uint)(EntityTable + i * EntityStride + EntityPosOff);
            uint rot = (uint)(EntityTable + i * EntityStride + EntityRotOff);
            _eSaved[b] = (int)m.ReadU32(pos);
            _eSaved[b + 1] = (int)m.ReadU32(pos + 4u);
            _eSaved[b + 2] = (int)m.ReadU32(pos + 8u);
            _eSavedRot[b] = m.ReadU16(rot);
            _eSavedRot[b + 1] = m.ReadU16(rot + 2u);
            _eSavedRot[b + 2] = m.ReadU16(rot + 4u);
            _eTouched[i] = true;
            _applied = true;

            _eWrote[i] = x;
            m.WriteU32(pos, (uint)x);
            m.WriteU32(pos + 4u, (uint)y);
            m.WriteU32(pos + 8u, (uint)z);
            m.WriteU16(rot, (ushort)((_eSavedRot[b] & ~mask) | rx));
            m.WriteU16(rot + 2u, (ushort)((_eSavedRot[b + 1] & ~mask) | ry));
            m.WriteU16(rot + 4u, (ushort)((_eSavedRot[b + 2] & ~mask) | rz));

            eCarried++;
            if (_probe)
            {
                int astep = Math.Abs(rdx) + Math.Abs(rdy) + Math.Abs(rdz);
                if (astep > _eBiggestAngleStep) _eBiggestAngleStep = astep;
                eRotMoved += astep * frac;
                int raw = Math.Max(_eCurRot[b], Math.Max(_eCurRot[b + 1], _eCurRot[b + 2]));
                if (raw > eMaxRaw) eMaxRaw = raw;
            }
        }

        if (_probe && eCarried > 0)
        {
            _eCarriedFrames++;
            _eCarriedSlots += eCarried;
            _eRotSum += eRotMoved / eCarried;
            if (eMaxRaw > _eMaxRawAngle) _eMaxRawAngle = eMaxRaw;
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

            for (int i = 0; i < EntityCount; i++)
            {
                if (!_eTouched[i]) continue;
                _eTouched[i] = false;

                int b = i * 3;
                uint pos = (uint)(EntityTable + i * EntityStride + EntityPosOff);
                uint rot = (uint)(EntityTable + i * EntityStride + EntityRotOff);

                if (_probe && (int)m.ReadU32(pos) != _eWrote[i]) _eMismatches++;

                m.WriteU32(pos, (uint)_eSaved[b]);
                m.WriteU32(pos + 4u, (uint)_eSaved[b + 1]);
                m.WriteU32(pos + 8u, (uint)_eSaved[b + 2]);
                m.WriteU16(rot, (ushort)_eSavedRot[b]);
                m.WriteU16(rot + 2u, (ushort)_eSavedRot[b + 1]);
                m.WriteU16(rot + 4u, (ushort)_eSavedRot[b + 2]);
            }

            _applied = false;
        }

        if (_probe) Report();
    }

    /// <summary>Shortest signed step from one angle to another, in
    /// [-AngleMod/2, AngleMod/2), so a turn through the wrap takes the short way
    /// round -- the same idea as FrameSmoothing.Delta12, generalised to AngleMod.
    /// AngleMod is a power of two, so `&amp; mask` is the modulus.</summary>
    static int DeltaAngle(int from, int to)
    {
        int mask = AngleMod - 1;
        int d = ((to & mask) - (from & mask)) & mask;   // 0 .. AngleMod-1
        if (d > AngleMod / 2) d -= AngleMod;            // to the shorter side
        return d;
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

        // The entity table, the same way, plus its rotation lanes.
        for (int i = 0; i < EntityCount; i++)
        {
            int b = i * 3;
            bool wasFree = m.ReadU8((uint)(EntityTable + i * EntityStride + EntityEmptyOff)) == 0xFF;

            _ePrev[b] = _eCur[b];
            _ePrev[b + 1] = _eCur[b + 1];
            _ePrev[b + 2] = _eCur[b + 2];
            _ePrevRot[b] = _eCurRot[b];
            _ePrevRot[b + 1] = _eCurRot[b + 1];
            _ePrevRot[b + 2] = _eCurRot[b + 2];

            if (wasFree)
            {
                _eLive[i] = false;
                continue;
            }

            uint pos = (uint)(EntityTable + i * EntityStride + EntityPosOff);
            _eCur[b] = (int)m.ReadU32(pos);
            _eCur[b + 1] = (int)m.ReadU32(pos + 4u);
            _eCur[b + 2] = (int)m.ReadU32(pos + 8u);

            uint rot = (uint)(EntityTable + i * EntityStride + EntityRotOff);
            _eCurRot[b] = m.ReadU16(rot);
            _eCurRot[b + 1] = m.ReadU16(rot + 2u);
            _eCurRot[b + 2] = m.ReadU16(rot + 4u);

            _eLive[i] = _primed;
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

        if (_eCarriedFrames > 0)
            Console.WriteLine($"[KF2] entities: {_eCarriedFrames}/{_frames} frames carried, " +
                              $"{(double)_eCarriedSlots / _eCarriedFrames:0.0} creature(s) each, " +
                              $"mean rot step {_eRotSum / _eCarriedFrames:0.0} u, " +
                              $"biggest {_eBiggestAngleStep} u, max raw angle 0x{_eMaxRawAngle:X}" +
                              (_eTeleports > 0 ? $", {_eTeleports} placement(s) left alone" : "") +
                              (_eMismatches > 0 ? $", {_eMismatches} LEAKED" : ""));

        _frames = _carriedFrames = _carriedSlots = _mismatches = 0;
        _moveSum = _fracSum = 0.0;
        _biggestStep = 0;
        _teleports = 0;

        _eCarriedFrames = _eCarriedSlots = _eMismatches = _eTeleports = 0;
        _eRotSum = 0.0;
        _eBiggestAngleStep = _eMaxRawAngle = 0;
    }
}
