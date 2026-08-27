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
///   `VECTOR` at `+0x14` and a **three-`s16` rotation at `+0x24`**, free when the
///   byte at `+0x4` is `0xFF`. Static props, doors and sprites. This is what a
///   `KF2_DRAWCENSUS=2` reading of `func_80032588`'s `a2` caught
///   (`0x80177714 + slot*0x44 + 0x14`) -- but only because the scene it measured
///   had props and no creatures near.
/// * **The entity table -- `0x8016C544`, 200 slots of `0x7C`**, free when the byte
///   at `+0x0` is `0xFF`, position a `VECTOR` at `+0x2C` and a three-`s16`
///   rotation at `+0x40`. **Creatures/enemies.**
///
/// **The object table's rotation was missed at first, and a door closing is what
/// found it.** This comment used to say the object table had no rotation at all,
/// on the strength of the draw census reading only `a2`. It has one, in the same
/// shape and with the same `0x800` yaw bias as the entity table's: the object loop
/// of `func_800331B4` builds the triple it passes as `a3` from `rec+0x24`,
/// `rec+0x26 + 0x800` and `rec+0x28`. Carrying position alone therefore left
/// anything that *turns* -- a swinging door, a spinning crystal -- stepping at the
/// tick rate against a position that glided, which reads as the animation running
/// at a low frame rate while its speed is right.
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
    // ---- what the renderer actually draws --------------------------------------

    /// <summary>
    /// One table stage 13 walks and draws from. There are four, and treating them
    /// as a list rather than as hand-copied code is deliberate: the first two were
    /// carried and the other two were not, and *both* omissions reached a player as
    /// "the animation runs at a low frame rate" before anyone went and read
    /// `func_800331B4` to the end. A new table is a row here now.
    ///
    /// `FreeOff`/`FreeWidth`/`FreeValue` are **the renderer's own emptiness test**,
    /// not the owning stage's. The object table has two different ones -- stage 2
    /// steps a slot when the byte at `+0x4` is not `0xFF`, the renderer draws it
    /// when the `u16` at `+0x6` is not `0xFF` -- and using stage 2's here silently
    /// dropped every slot that is drawn but not stepped by it. What is being
    /// interpolated is what is *drawn*, so the drawing test is the right one; a
    /// slot carried but not drawn costs a write and a restore and nothing else.
    ///
    /// `RotOff` is -1 for a table the renderer hands a zeroed rotation triple.
    /// </summary>
    sealed record TableSpec(
        string Label, string Noun, uint Base, int Stride, int Count,
        int FreeOff, int FreeWidth, int FreeValue, int PosOff, int RotOff);

    static readonly TableSpec[] Tables =
    [
        // Creatures/enemies. Stage 4 (gated) copies the object position into +0x2C,
        // but the renderer draws from this copy plus a rotation of its own, so this
        // is the only place an enemy's facing lives.
        new("entities", "creature", 0x8016C544, 0x7C, 0xC8, 0x0, 1, 0xFF, 0x2C, 0x40),

        // Static props, doors and sprites -- the table stage 2 steps. Note the free
        // test is +0x6 and not the +0x4 that stage 2 and AgentServer use; see the
        // TableSpec comment.
        new("objects", "object", 0x80177714, 0x44, 0x18C, 0x6, 2, 0xFF, 0x14, 0x24),

        // Stage 5's table (128 lifetimes at rec+0x0E). Named for the projectiles and
        // effects in it, but the renderer draws it with a full position *and*
        // rotation, in the same +0x14/+0x24 layout as the object table -- and stage 5
        // is gated, so anything in here stepped at the tick rate with nothing
        // carrying it.
        new("effects", "effect", 0x8019CC6C, 0x48, 0x80, 0x0, 1, 0xFF, 0x14, 0x24),

        // Billboard sprites. The renderer zeroes the rotation triple for these, so
        // there is no facing to carry -- position at +0x8 only. Its positions are
        // written under stage 13 itself (func_8002DF80) and so already move at the
        // render rate; carrying is a no-op there, and harmless.
        new("sprites", "sprite", 0x80195174, 0x18, 0x80, 0x0, 2, 0xFFFF, 0x8, -1),
    ];

    /// <summary>Per-table sample, carry and restore state. One array set per row of
    /// <see cref="Tables"/>, so adding a row needs no new fields.</summary>
    sealed class TableState
    {
        public required TableSpec Spec;
        public int[] Prev = [], Cur = [], PrevRot = [], CurRot = [];
        public int[] Saved = [], SavedRot = [], Wrote = [];
        public bool[] Live = [], Touched = [];

        /// <summary>Slots carried on the last tick, which get
        /// <see cref="GlidingFactor"/> times the placement threshold on this
        /// one. Motion is assumed to continue; a cliff is not.</summary>
        public bool[] Gliding = [];

        // The probe's per-table window.
        public long CarriedFrames, CarriedSlots, RotSlots, Mismatches, Teleports;
        public double MoveSum;
        public int BiggestStep, BiggestAngleStep, MaxRawAngle;

        public static TableState For(TableSpec s) => new()
        {
            Spec = s,
            Prev = new int[s.Count * 3], Cur = new int[s.Count * 3],
            PrevRot = new int[s.Count * 3], CurRot = new int[s.Count * 3],
            Saved = new int[s.Count * 3], SavedRot = new int[s.Count * 3],
            Wrote = new int[s.Count],
            Live = new bool[s.Count], Touched = new bool[s.Count],
            Gliding = new bool[s.Count],
        };

        public void ResetWindow()
        {
            CarriedFrames = CarriedSlots = RotSlots = Mismatches = Teleports = 0;
            MoveSum = 0.0;
            BiggestStep = BiggestAngleStep = MaxRawAngle = 0;
        }
    }

    static readonly TableState[] _state = [.. Tables.Select(TableState.For)];

    /// <summary>One whole turn, in the rotation lanes' units. The 0x800 yaw bias the
    /// renderer applies is exactly half of this, which is the evidence a turn is
    /// 4096. The raw lanes are *not* confined to [0, AngleMod): the probe measures
    /// values above 0xFFF and just under 0x10000 -- small signed or accumulated
    /// angles -- so the interpolation works modulo AngleMod (the only part the GTE
    /// sees) and preserves the bits above it untouched on write.</summary>
    const int AngleMod = 0x1000;

    /// <summary>
    /// Units on one axis in one tick past which a slot is treated as having been
    /// *placed* rather than having moved, and is left where the game put it.
    ///
    /// Without it an object that is teleported -- spawned, respawned, moved by a
    /// script, or simply re-placed when the area finished loading -- gets swept
    /// smoothly across the map over the next tick instead of appearing. Measured on
    /// the way in: real motion in a quiet area was **0 units of XZ a tick** (the four
    /// things moving were bobbing in Y and spinning 0x80 a tick), while one window
    /// caught a **233,472-unit** step, which is most of an area. The player, the
    /// fastest thing in the game, covers 1817 units in 2 s at 20 Hz -- about **45
    /// units a tick** -- so 1024 sat some twenty times above anything that walks and
    /// two hundred times below the placement it has to catch.
    ///
    /// **1024 was measured against things that walk, and a boss does not walk.**
    /// Play reported the final boss and the piranhas "freaking out" during an
    /// attack -- and an attack is exactly when a big creature's parts cover the
    /// most ground in a tick. A part whose step sits near the threshold is
    /// carried on the tick it comes in under and left where the game put it on
    /// the tick it goes over, so it glides across a tick and then holds still
    /// for one, twenty times a second, while the parts either side of it keep
    /// gliding. That reads as the creature tearing itself apart, and it cannot
    /// show at 20 fps because the phase is 0 and nothing is carried at all.
    ///
    /// The walking measurements never bounded fast motion; they only bounded
    /// walking. The gap between them and the one placement ever measured is a
    /// factor of two hundred, so the threshold can be raised a long way and
    /// still catch what it exists to catch: 8192 is 180 times a walk and 28
    /// times below the 233,472-unit placement.
    /// </summary>
    const int TeleportUnits = 8192;

    /// <summary>
    /// How much further a slot **already being carried** may step before it is
    /// called a placement.
    ///
    /// A bare threshold is a cliff, and the shake is the sound of a slot falling
    /// off it and climbing back on alternate ticks. Raising it moves the cliff
    /// without removing it, so the decision is made sticky instead: something
    /// that was moving is assumed to still be moving, and it takes four times
    /// the threshold to decide otherwise. A real placement clears that by seven
    /// times over.
    /// </summary>
    const int GlidingFactor = 4;

    public const string OnKey = "kf2.smoothing.objects";

    /// <summary>Carry positions and facings between ticks. **Off by default**, the
    /// house rule for a mechanism that has been measured and whose picture has
    /// not.</summary>
    public static bool Enabled { get; private set; }

    static bool _onFromEnv;

    /// <summary>True once both samples exist. Cleared when an area loads, because the
    /// tables are rebuilt and the last area's positions are meaningless.</summary>
    static bool _primed;

    /// <summary>True when the pre-hook wrote anything this frame, so the post-hook
    /// knows whether there is a restore to do.</summary>
    static bool _applied;

    // ---- the probe ------------------------------------------------------------

    static bool _probe;
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _reportedAt;
    static long _frames;
    static double _fracSum;

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
    /// Sample on a frame the world advanced on, then walk every drawn thing the
    /// fraction of a tick this frame stands at.
    /// </summary>
    public static void Before(CpuContext c, IMemory m)
    {
        _applied = false;
        if (!Enabled || !FramePacing.Gating) return;

        if (_probe) _frames++;

        if (FramePacing.TickedThisFrame) Sample(m);
        if (!_primed) return;

        // Not gated on a small phase: interpolation must overwrite the tables even at
        // frac ~= 0, because on a tick frame they hold `Cur` (the new tick) and the
        // frame is meant to draw `Prev`. The per-slot "nothing moved" skip below
        // covers the do-nothing case.
        double frac = FramePacing.LogicPhase;
        int mask = AngleMod - 1;

        foreach (var t in _state)
        {
            var s = t.Spec;
            int carried = 0;

            for (int i = 0; i < s.Count; i++)
            {
                if (!t.Live[i]) continue;

                int b = i * 3;
                int dx = t.Cur[b] - t.Prev[b];
                int dy = t.Cur[b + 1] - t.Prev[b + 1];
                int dz = t.Cur[b + 2] - t.Prev[b + 2];

                // Rotation is carried on its own terms, not as a rider on position: a
                // door swings without its origin moving, and skipping the slot when the
                // position held still is exactly what left it stepping at the tick rate.
                int rdx = 0, rdy = 0, rdz = 0;
                if (s.RotOff >= 0)
                {
                    rdx = DeltaAngle(t.PrevRot[b], t.CurRot[b]);
                    rdy = DeltaAngle(t.PrevRot[b + 1], t.CurRot[b + 1]);
                    rdz = DeltaAngle(t.PrevRot[b + 2], t.CurRot[b + 2]);
                }

                bool posMoved = dx != 0 || dy != 0 || dz != 0;
                bool rotMoved = rdx != 0 || rdy != 0 || rdz != 0;
                if (!posMoved && !rotMoved) continue;

                // The placement guard is the position's alone. An angle cannot be
                // "placed too far" -- DeltaAngle already takes the short way round, so
                // the worst a re-placed facing costs is half a turn of sweep -- and
                // letting it veto the whole slot would put the position test in charge
                // of whether a door animates.
                // Sticky: a slot that was gliding last tick keeps gliding
                // unless the step is far past the threshold, so a part whose
                // motion sits near it cannot alternate between carried and held
                // and tear itself off the rest of the creature.
                int cap = t.Gliding[i] ? TeleportUnits * GlidingFactor : TeleportUnits;
                bool posLive = posMoved &&
                               Math.Abs(dx) <= cap &&
                               Math.Abs(dy) <= cap &&
                               Math.Abs(dz) <= cap;
                t.Gliding[i] = posLive;
                if (posMoved && !posLive && _probe) t.Teleports++;
                if (!posLive && !rotMoved) continue;

                uint pos = (uint)(s.Base + i * s.Stride + s.PosOff);
                t.Saved[b] = (int)m.ReadU32(pos);
                t.Saved[b + 1] = (int)m.ReadU32(pos + 4u);
                t.Saved[b + 2] = (int)m.ReadU32(pos + 8u);
                t.Touched[i] = true;
                _applied = true;

                if (posLive)
                {
                    // lerp(Prev, Cur, frac): never past a position the game actually
                    // produced, so it cannot overshoot on a stop or into a wall. Same
                    // clock as the camera, which interpolates too.
                    int x = t.Prev[b] + (int)Math.Round(dx * frac);
                    int y = t.Prev[b + 1] + (int)Math.Round(dy * frac);
                    int z = t.Prev[b + 2] + (int)Math.Round(dz * frac);

                    t.Wrote[i] = x;
                    m.WriteU32(pos, (uint)x);
                    m.WriteU32(pos + 4u, (uint)y);
                    m.WriteU32(pos + 8u, (uint)z);
                }
                else
                {
                    t.Wrote[i] = t.Saved[b];   // untouched, so the leak check still holds
                }

                if (s.RotOff >= 0)
                {
                    uint rot = (uint)(s.Base + i * s.Stride + s.RotOff);
                    t.SavedRot[b] = m.ReadU16(rot);
                    t.SavedRot[b + 1] = m.ReadU16(rot + 2u);
                    t.SavedRot[b + 2] = m.ReadU16(rot + 4u);

                    if (rotMoved)
                    {
                        // Interpolate the low AngleMod bits along the shortest way
                        // round; keep whatever sits above them, so a lane wider than
                        // 12 bits is preserved rather than truncated.
                        int rx = (t.PrevRot[b] + (int)Math.Round(rdx * frac)) & mask;
                        int ry = (t.PrevRot[b + 1] + (int)Math.Round(rdy * frac)) & mask;
                        int rz = (t.PrevRot[b + 2] + (int)Math.Round(rdz * frac)) & mask;

                        m.WriteU16(rot, (ushort)((t.SavedRot[b] & ~mask) | rx));
                        m.WriteU16(rot + 2u, (ushort)((t.SavedRot[b + 1] & ~mask) | ry));
                        m.WriteU16(rot + 4u, (ushort)((t.SavedRot[b + 2] & ~mask) | rz));
                    }
                }

                carried++;
                if (_probe)
                {
                    t.MoveSum += Math.Abs(dx * frac) + Math.Abs(dy * frac) + Math.Abs(dz * frac);
                    int step = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                    if (step > t.BiggestStep) t.BiggestStep = step;

                    if (rotMoved)
                    {
                        t.RotSlots++;
                        int astep = Math.Abs(rdx) + Math.Abs(rdy) + Math.Abs(rdz);
                        if (astep > t.BiggestAngleStep) t.BiggestAngleStep = astep;
                        int raw = Math.Max(t.CurRot[b], Math.Max(t.CurRot[b + 1], t.CurRot[b + 2]));
                        if (raw > t.MaxRawAngle) t.MaxRawAngle = raw;
                    }
                }
            }

            if (_probe && carried > 0) { t.CarriedFrames++; t.CarriedSlots += carried; }
        }

        if (_probe && _applied) _fracSum += frac;
        if (_probe) Report();
    }

    /// <summary>
    /// Put every table back the moment the renderer has read it. Everything
    /// downstream -- the next tick's AI, a collision test, a save -- then sees
    /// exactly what the game wrote.
    /// </summary>
    public static void After(CpuContext c, IMemory m)
    {
        if (!_applied) return;

        foreach (var t in _state)
        {
            var s = t.Spec;
            for (int i = 0; i < s.Count; i++)
            {
                if (!t.Touched[i]) continue;
                t.Touched[i] = false;

                int b = i * 3;
                uint pos = (uint)(s.Base + i * s.Stride + s.PosOff);

                // The probe's own check that nothing leaks: what the renderer left
                // behind must be what the pre-hook wrote, or something downstream has
                // been writing a position that was not the game's.
                if (_probe && (int)m.ReadU32(pos) != t.Wrote[i]) t.Mismatches++;

                m.WriteU32(pos, (uint)t.Saved[b]);
                m.WriteU32(pos + 4u, (uint)t.Saved[b + 1]);
                m.WriteU32(pos + 8u, (uint)t.Saved[b + 2]);

                if (s.RotOff >= 0)
                {
                    uint rot = (uint)(s.Base + i * s.Stride + s.RotOff);
                    m.WriteU16(rot, (ushort)t.SavedRot[b]);
                    m.WriteU16(rot + 2u, (ushort)t.SavedRot[b + 1]);
                    m.WriteU16(rot + 4u, (ushort)t.SavedRot[b + 2]);
                }
            }
        }

        _applied = false;
    }

    /// <summary>
    /// Roll this tick's samples into last tick's and re-read every table. A slot free
    /// in either sample is not live, so something just spawned is drawn where the game
    /// put it rather than swept in from wherever the slot's previous tenant died.
    /// </summary>
    static void Sample(IMemory m)
    {
        foreach (var t in _state)
        {
            var s = t.Spec;
            for (int i = 0; i < s.Count; i++)
            {
                int b = i * 3;
                uint slot = (uint)(s.Base + i * s.Stride);
                uint free = (uint)(slot + s.FreeOff);
                bool wasFree = s.FreeWidth == 1
                    ? m.ReadU8(free) == (byte)s.FreeValue
                    : m.ReadU16(free) == (ushort)s.FreeValue;

                t.Prev[b] = t.Cur[b];
                t.Prev[b + 1] = t.Cur[b + 1];
                t.Prev[b + 2] = t.Cur[b + 2];
                t.PrevRot[b] = t.CurRot[b];
                t.PrevRot[b + 1] = t.CurRot[b + 1];
                t.PrevRot[b + 2] = t.CurRot[b + 2];

                if (wasFree)
                {
                    t.Live[i] = false;
                    t.Gliding[i] = false;
                    continue;
                }

                uint pos = slot + (uint)s.PosOff;
                t.Cur[b] = (int)m.ReadU32(pos);
                t.Cur[b + 1] = (int)m.ReadU32(pos + 4u);
                t.Cur[b + 2] = (int)m.ReadU32(pos + 8u);

                if (s.RotOff >= 0)
                {
                    uint rot = slot + (uint)s.RotOff;
                    t.CurRot[b] = m.ReadU16(rot);
                    t.CurRot[b + 1] = m.ReadU16(rot + 2u);
                    t.CurRot[b + 2] = m.ReadU16(rot + 4u);
                }

                // Live only once the slot has been occupied for two samples running,
                // which is also what makes the first sample after an area load safe.
                t.Live[i] = _primed;
            }
        }

        _primed = true;
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

    static void Report()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _reportedAt < 2000.0) return;
        _reportedAt = now;

        if (_frames == 0) return;

        long anyCarried = 0;
        foreach (var t in _state) anyCarried += t.CarriedFrames;

        if (anyCarried == 0)
        {
            Console.WriteLine($"[KF2] objects: 0 of {_frames} frames carried -- " +
                              $"{(FramePacing.Extrapolating ? "nothing moving" : "not extrapolating")}");
        }
        else
        {
            foreach (var t in _state)
            {
                if (t.CarriedFrames == 0) continue;
                var s = t.Spec;
                Console.WriteLine($"[KF2] {s.Label}: {t.CarriedFrames}/{_frames} frames carried, " +
                                  $"{(double)t.CarriedSlots / t.CarriedFrames:0.0} {s.Noun}(s) each, " +
                                  $"offset {t.MoveSum / t.CarriedFrames:0.0} u, " +
                                  $"biggest tick step {t.BiggestStep} u" +
                                  (s.RotOff >= 0
                                      ? $", {(double)t.RotSlots / t.CarriedFrames:0.0} turning, " +
                                        $"biggest angle step {t.BiggestAngleStep} u, " +
                                        $"max raw angle 0x{t.MaxRawAngle:X}"
                                      : "") +
                                  (t.Teleports > 0 ? $", {t.Teleports} placement(s) left alone" : "") +
                                  (t.Mismatches > 0 ? $", {t.Mismatches} LEAKED" : ""));
            }
            Console.WriteLine($"[KF2] smoothing: mean phase {_fracSum / anyCarried:0.00} tick " +
                              $"over {_frames} frame(s)");
        }

        _frames = 0;
        _fracSum = 0.0;
        foreach (var t in _state) t.ResetWindow();
    }
}
