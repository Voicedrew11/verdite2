using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Carries creature (and morphing-object) pose between logic ticks by driving
/// the MO clip clock, not by rewriting vertices after the fact.
///
///     KF2_SMOOTH_ANIM=1         on, in the default mode; off by default
///     KF2_SMOOTH_ANIM=timeline  interpolate on the clip's own timeline (default)
///     KF2_SMOOTH_ANIM=weight    the old bounded comparison mode
///     KF2_SMOOTH_ANIM=time      the old unbounded comparison mode
///     KF2_SMOOTH_ANIM_PROBE=1   morph vs rigid submits, and the verdict census
///
/// It is a setting under Video, beside the three other smoothing checkboxes,
/// with the mode as a combo underneath it so the three can be compared while a
/// creature is on screen.
///
/// ## Where the pose actually lives
///
/// <see cref="ObjectSmoothing"/> carries origin and Euler out of the four tables
/// the renderer walks, which makes an enemy *travel and turn* smoothly and leaves
/// its shape stepping at the tick rate. The shape is a **mesh morph**: MO is a
/// base TMD plus packed vertex deltas, `func_80034DA8` applies it into the
/// scratch at `0x80190AD8`, and `func_80034A74` is the decoder.
///
/// `func_80032588` chooses between that and rigid architecture on its **eighth
/// stack word** -- the clip byte, `caller SP+0x1C`, tested `&lt; 0x80` at
/// `0x80032998`. The ninth, `caller SP+0x20`, is the integer clip time. Reading
/// them off the stack rather than out of a table is deliberate: the renderer
/// feeds this function from four different tables with four different strides,
/// and the argument list is the one description true for all of them.
///
/// `func_8003486C(bank, clip, time, &amp;segment, &amp;weight)` turns that integer
/// time into a segment index and a 12.12 weight, and returns the segment record
/// in `v0`. **Why driving that clock reaches the mesh at all** is `L80034FCC` in
/// `func_80034DA8`: when the clip and the segment index are both unchanged the
/// blender skips rebuilding its keyframe *cache*, but it still copies the base
/// mesh into `0x80190AD8` and still calls the decoder with the weight
/// `func_8003486C` just wrote. Stage 13 therefore re-morphs every rendered
/// frame; only the time it morphs *to* is stuck on the tick.
///
/// ## The clip is a timeline, and the old modes did not know how long it was
///
/// Every version of this patch before <see cref="Mode.Timeline"/> lerped the
/// integer clip time as if it were a Euclidean scalar on an unbounded line, and
/// then bolted a classifier onto each way that fails. The end of a looping cycle
/// was told from a re-seek by *where the time landed*; the turnover was
/// synthesised out of the last playback step and only believed off a settled
/// run; a step larger than a magic 4096 was a re-seek; a clip whose time the AI
/// was fighting over was counted in direction reversals and held; and the
/// default mode gave up driving the time at all, spending the phase on the blend
/// weight inside the game's own segment and refusing the whole tick whenever
/// that ran out of the segment. Each of those is a repair of the same missing
/// fact.
///
/// **The missing fact is the clip's length**, and it is not missing -- it is the
/// sum of the per-segment durations in the very table `func_8003486C` walks.
/// <see cref="Duration"/> reads it once per `(bank, clip)`:
///
/// * `clipTable = bank + u32[bank + 0x10]`
/// * `clipRec   = bank + u32[clipTable + clip*4]`
/// * `u16[clipRec]` is the segment count, and `u32[clipRec + 4 + 4*i]` are
///   `bank`-relative offsets to the segment records
/// * a segment record is `u16 reversedFlag` at `+0x0`, `u16 duration` at `+0x2`
///
/// so `D = sum of the durations`, which is exactly the number the clock's own
/// accumulator compares the time against.
///
/// With `D` in hand a clip time is a point on a **circle of circumference D**,
/// and there is one predicate instead of five. Playback is constant velocity
/// along that circle, including through 0. So each tick:
///
/// 1. the clip byte changed -- a hard cut, show the game's pose, start again;
/// 2. else unwrap this tick's step against the settled rate: the candidates are
///    `cur + kD - prev` for the few `k` that can reach it, plus the two
///    **ping-pong** turns `(-cur) - prev` and `(2D - cur) - prev` for a clip that
///    reflects at an endpoint rather than wrapping. The nearest candidate to the
///    settled rate wins if it is within <see cref="RateTolRel"/> of it. That one
///    test is the cycle wrap, the reverse clip, the ping-pong turn and ordinary
///    playback;
/// 3. no candidate matched -- a re-seek, or a clip the AI is fighting over --
///    so hold at the game's own time. Not a repair of the game's indecision; a
///    refusal to draw it more often than the game makes it.
///
/// A carried tick then interpolates along the path it just recognised,
/// `raw = prev + delta * phase`, folds it back onto the circle (modulo for a
/// wrap, a triangle fold for a ping-pong turn), hands `floor(t)` to
/// `func_8003486C` so the segment pick matches the in-between instant, and adds
/// the leftover fraction onto the 12.12 weight the decoder consumes. The
/// fraction is under one clip unit, so it can never leave the segment `floor(t)`
/// landed in -- the whole-tick overrun refusal the bounded mode needed has
/// nothing left to refuse.
///
/// **The reversed segment is a real case and the sign is not free.** When the
/// flag `u16` at `segment+0x0` is set, `func_8003486C` publishes `0x1000 - raw`,
/// so the weight *decreases* as time advances and the fraction is subtracted.
///
/// **It interpolates (`t-1+frac`) rather than extrapolating**, on the same clock
/// and by the same fraction as <see cref="FrameSmoothing"/> and
/// <see cref="ObjectSmoothing"/>: a pose is not steered by the player, so the
/// tick of latency is free, and the three have to agree about what time it is or
/// the parts of a creature read as running at different speeds.
///
/// **A held root holds the pose.** If <see cref="ObjectSmoothing"/> judged this
/// slot's step a placement and left the creature where the game put it, morphing
/// its vertices at the frame rate on top of a root that jumps is worse than not
/// smoothing at all -- the two disagree about what time it is, reported on a fast
/// boss as its head snapping into the next frame of the animation. That coupling
/// is a coupling and not a motion model: `ObjectSmoothing` publishes the position
/// addresses it refused and this asks by the same address, since
/// `func_80032588`'s `a2` *is* `base + slot*stride + PosOff`.
///
/// ## What it costs, and what has to be looked at
///
/// A settled rate takes one tick to establish, so a slot carries from its
/// **third** sample of a clip rather than its second -- except at a genuine clip
/// change, where the first step *is* the definition of the rate and there is
/// nothing to have re-seeked from, so that one is taken on trust
/// (<see cref="Slot.FirstStep"/>). A clip whose length cannot be read (a bad
/// segment count, a zero total) never carries at all.
///
/// Nothing here writes game state. `c.A2` is a register on one call and the
/// weight lives in the *caller's* own stack temp, both consumed before
/// `func_80034DA8` returns -- so unlike the table smoothers there is nothing to
/// put back and nothing that can leak into the next tick's AI or a save.
///
/// The two old modes are kept as <see cref="Mode.Weight"/> and
/// <see cref="Mode.Time"/> so the picture can be compared by eye, which is the
/// only way this gets settled; see "The bounded mode" in
/// `docs/PATCHES_AND_MODS.md` for what each of them does and why. They are
/// comparison modes, not fallbacks -- when the picture has been judged, the
/// loser goes.
///
/// Measured not to move the world clock: 65 death frames in 3.25 s with it off
/// and 3.25-3.28 s with it on, at 20, 60 and 144 fps against a 20 Hz world.
/// </summary>
public static class AnimSmoothing
{
    /// <summary>Stage 13, the renderer. The frame bracket -- the same one
    /// <see cref="ObjectSmoothing"/> hangs off.</summary>
    const uint Renderer = 0x800342D8;

    /// <summary>The model submitter. Its `a2` is the position pointer this keys
    /// slots on, and its eighth and ninth stack words are the clip and the
    /// clip time.</summary>
    const uint ModelSubmit = 0x80032588;

    /// <summary>The MO clip clock: `a0` the bank, `a1` the clip, `a2` the
    /// integer time; segment index and 12.12 weight out.</summary>
    const uint ClipClock = 0x8003486C;

    /// <summary>
    /// How far a tick's step may differ from the settled playback rate and still
    /// count as playback, as a fraction of that rate.
    ///
    /// This is the *only* tolerance left, and unlike the magnitude cutoff it
    /// replaces it is relative to something the slot measured rather than to a
    /// number chosen here. A clip whose speed the game genuinely changes fails
    /// one tick, re-seeds the rate from what it saw, and carries again on the
    /// next -- so the cost of it being too tight is one held tick, not a slot
    /// that never carries.
    /// </summary>
    const double RateTolRel = 0.5;

    /// <summary>
    /// The floor under <see cref="RateTolRel"/>, in clip units. A clip creeping
    /// at one unit a tick would otherwise have a tolerance of half a unit and
    /// could not survive the integer time being rounded.
    /// </summary>
    const double RateTolAbs = 2.0;

    /// <summary>
    /// A ceiling on the acceptance window, as a fraction of the clip's length.
    ///
    /// The window is <see cref="RateTolRel"/> *of the settled rate*, so it grows
    /// with whatever was last recorded — and the hold path records what it saw,
    /// including a seek. Without a ceiling a rate of half the clip carries a
    /// window of an eighth of it and lets the next tick accept a pose from
    /// somewhere else entirely. Measured playback is 64-290 units against a clip
    /// length of 4096, so a window of a twentieth of the clip is about one
    /// tick's motion — the loosest a "did it keep playing" test should ever be —
    /// and it only binds above a rate of ~410, which is well past anything
    /// observed. This is what lets <see cref="Seed"/> record a rate of any size
    /// without that size becoming its own excuse.
    /// </summary>
    const double RateTolCap = 0.05;

    /// <summary>
    /// The largest opening step, as a fraction of the clip's own length, that is
    /// carried on trust as the start of playback **without a confirming tick**.
    ///
    /// The first moving tick of a clip has no settled rate to be checked
    /// against, and a seek into the middle of a clip is indistinguishable from
    /// the start of a fast one -- except by size. Trusting an unbounded first
    /// step let a slot carry a delta of up to half the clip, which sweeps most
    /// of the animation inside a 50 ms tick and then becomes the rate, with a
    /// tolerance scaled to match. Measured playback is 64-290 units against a
    /// clip length of 4096, so a quarter of the clip is an order of magnitude
    /// clear of anything real and still refuses a seek. Expressed in the clip's
    /// own length rather than as a magnitude, which is the whole point of
    /// knowing it.
    ///
    /// **It is a shortcut and not a gate**, and getting that wrong cost a bug: a
    /// step past it used to be refused *as a rate* as well, so a clip whose
    /// genuine playback rate exceeded it could never settle one — the slot held,
    /// declined to record what it saw, held again on the identical reasoning,
    /// and drew at the tick rate for the whole animation. Play found it on a
    /// gecko's backflip, which is fast enough to clear a quarter of its clip in
    /// a tick. Size buys a tick of latency; **repetition** is what buys
    /// correctness, and two consecutive ticks agreeing is settled playback
    /// whatever the size.
    /// </summary>
    const double FirstStepFrac = 0.25;

    /// <summary>The widest segment count treated as a real clip record. A wild
    /// pointer reads as tens of thousands of segments; every clip in the game is
    /// far under this.</summary>
    const int MaxSegments = 4096;

    /// <summary>How much of the clip clock this is allowed to drive.</summary>
    public enum Mode
    {
        /// <summary>
        /// **Interpolate on the clip's own timeline.** The clip's length is read
        /// out of the segment table, the tick's step is unwrapped on the circle
        /// of that length against the slot's settled playback rate, and the
        /// in-between instant is handed to `func_8003486C` as an integer time
        /// plus a sub-unit fraction on the weight. One predicate covers ordinary
        /// playback, a cycle wrap, a reverse clip and a ping-pong turn; anything
        /// it does not recognise is held at the game's own time.
        /// </summary>
        Timeline,

        /// <summary>
        /// **Carry the weight only, inside the segment the game itself chose.**
        /// The integer time handed to `func_8003486C` is left exactly as the game
        /// wrote it, so the segment index, the blender's keyframe cache and every
        /// decode it does are bit-for-bit what they are with smoothing off; only
        /// the 12.12 blend weight moves, and a carry that would leave the segment
        /// is refused for the whole tick. Bounded, and it gives up all motion
        /// across a segment boundary. Kept for comparison by eye.
        /// </summary>
        Weight,

        /// <summary>
        /// **The original unbounded mode.** Lerps the integer clip time as a
        /// scalar on an unbounded line, with the landing-site wrap classifier,
        /// the thrash hold and the 4096 re-seek cutoff. Kept for comparison by
        /// eye. `KF2_SMOOTH_ANIM=time`.
        /// </summary>
        Time,
    }

    /// <summary>What a tick's clip-time step was, decided once per tick.</summary>
    enum Verdict
    {
        /// <summary>Nothing moved.</summary>
        Still,
        /// <summary>Recognised playback: interpolate it.</summary>
        Play,
        /// <summary>A cycle turned over (<see cref="Mode.Time"/> only, where the
        /// turnover has to be synthesised).</summary>
        Wrap,
        /// <summary>Not recognised as playback -- a re-seek, a clip being fought
        /// over, or a rate not yet settled. Leave the game's own time alone.</summary>
        Hold,
    }

    /// <summary>How a carried tick's unwrapped path folds back onto the clip.</summary>
    enum Fold
    {
        /// <summary>Straight, possibly through 0 or the end: take it modulo D.</summary>
        Circle,
        /// <summary>The clip reflected at an endpoint: fold it as a triangle wave.</summary>
        Mirror,
    }

    sealed class Slot
    {
        public int Clip = -1;
        public int PrevTime, CurTime;
        public bool HasCur, HasPrev;

        // ---- Mode.Timeline --------------------------------------------------

        /// <summary>The clip's total length in clip units, summed off the segment
        /// table. 0 while it is unknown, which is a reason to hold.</summary>
        public int Length;

        /// <summary>The bank the length was read against. The same clip byte
        /// under a different bank is a different clip.</summary>
        public uint Bank;

        /// <summary>The settled playback velocity in clip units per tick, signed.
        /// A reverse clip is a negative rate and needs no special case.</summary>
        public double Rate;
        public bool HasRate;

        /// <summary>True until this clip's first moving tick has been seen. That
        /// step *is* the definition of the rate -- there is nothing to have
        /// re-seeked away from yet -- so it is taken on trust rather than held
        /// for a tick.</summary>
        public bool FirstStep = true;

        /// <summary>This tick's accepted step along the unwrapped timeline, and
        /// how the path folds back onto the clip. Valid only when
        /// <see cref="Say"/> is <see cref="Verdict.Play"/>.</summary>
        public double Delta;
        public Fold Path;

        /// <summary>Set when the accepted candidate needed a wrap or a turn,
        /// which is only ever reported.</summary>
        public bool Wrapped, Mirrored;

        // ---- Mode.Weight and Mode.Time --------------------------------------

        /// <summary>This tick's step, in the legacy modes.</summary>
        public int Step;

        /// <summary>Consecutive ticks stepping the same way.</summary>
        public int Run;

        /// <summary>Direction reversals that were not cycle turnovers, decayed
        /// one a tick by steady playback.</summary>
        public int Flips;

        /// <summary>The last step that was playback -- a wrap's step is a cycle
        /// length and is deliberately not recorded here.</summary>
        public int LastStep;

        /// <summary>The highest clip time seen on this clip. Only a
        /// reverse-played loop needs it, to know where its cycle starts.</summary>
        public int MaxTime;
        public bool HasMax;

        /// <summary>The tick a <see cref="Mode.Weight"/> carry last ran out of
        /// its segment on.</summary>
        public long OverrunTick = -1;

        // ---- shared ---------------------------------------------------------

        /// <summary>What this tick's step was. Decided once per tick, not once
        /// per frame, so the verdict cannot disagree with itself across the
        /// frames of one tick.</summary>
        public Verdict Say;

        /// <summary>The logic tick this slot last sampled on. One sample per
        /// tick, whichever frame of that tick the slot happens to be drawn on.</summary>
        public long Tick = -1;
    }

    static readonly Dictionary<uint, Slot> _slots = [];

    /// <summary>Clip lengths, keyed on the bank and the clip index the clock was
    /// actually called with. Walking the segment table is a handful of loads, but
    /// it is per morph submit per frame otherwise.</summary>
    static readonly Dictionary<(uint Bank, uint Clip), int> _lengths = [];

    /// <summary>Bumped once per logic tick, at the frame bracket.</summary>
    static long _tick;

    /// <summary>This frame's <see cref="FramePacing.LogicPhase"/>, read once at
    /// the bracket. It is stable for the whole frame by construction.</summary>
    static double _phase;

    static int _depth;
    static bool _pending;

    /// <summary>The slot this submit is for. Null on a rigid submit.</summary>
    static Slot? _slot;

    /// <summary>Set by the first clip-clock call inside a submit, so a second one
    /// -- `func_8003507C` reaches the same clock from elsewhere in the subtree --
    /// neither takes the carry nor re-reads the length.</summary>
    static bool _clockSeen;

    static double _carry;
    static uint _weightPtr;
    static int _tFloor;

    /// <summary>How far *back* in clip time from the game's own current time
    /// this frame stands, in <see cref="Mode.Weight"/>.</summary>
    static double _back;

    public const string OnKey = "kf2.smoothing.anim";
    public const string ModeKey = "kf2.smoothing.anim.mode";

    /// <summary>Drive the MO clip clock between ticks. **Off by default.**</summary>
    public static bool Enabled { get; private set; }

    /// <summary>
    /// How much of the clock to drive.
    ///
    /// **<see cref="Mode.Timeline"/> is the default, and it took two readings by
    /// eye to get there.** It shipped as the default, play reported the teleport
    /// crystals shaking rapidly up and down, and the default moved to
    /// <see cref="Mode.Time"/> — the only mode with a positive report at the
    /// time — while the cause was found: a spurious endpoint turn running the
    /// pose to the end of the clip and back inside one tick. With that fixed,
    /// play reports it looking very good, so it is the default again. The
    /// argument was always the stronger one; what it lacked was the eye, and the
    /// eye is the only thing that can settle this.
    /// </summary>
    public static Mode Carry { get; private set; } = Mode.Timeline;

    static bool _onFromEnv, _modeFromEnv;
    static bool _probe;

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _reportedAt;
    static long _submits, _morph, _rigid, _live, _carried, _reversed;
    static long _play, _wrapped, _mirrored, _backward, _held, _unsettled, _noLength, _rootHeld;
    static long _clipTicks, _wraps, _wrapCarried, _stuck, _skipped, _overran;
    static int _maxTimeSeen;
    static double _timeStepSum, _fracSum;
    static int _maxStep, _minStep = int.MaxValue;

    /// <summary>The widest step a hold turned away, which is the counter that
    /// makes a clip too fast to be carried visible. A slot stuck at the tick
    /// rate shows up here as a number far above the carried range beside it;
    /// without it, a stranded fast clip is indistinguishable from a scene with
    /// nothing animating in it.</summary>
    static int _maxRefused;

    static void Refused(double delta)
    {
        int a = (int)Math.Round(Math.Abs(delta));
        if (a > _maxRefused) _maxRefused = a;
    }

    static readonly ModInfo _self = new()
    {
        Id = "kf2.animsmoothing",
        Name = "Animation smoothing",
        Version = "3.0",
        Description = "Interpolates MO pose on the clip's own timeline between logic ticks.",
    };

    public static void Configure(string? on, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on))
        {
            Enabled = on != "0";
            switch (on)
            {
                case "time" or "full": Carry = Mode.Time; _modeFromEnv = true; break;
                case "weight" or "bounded": Carry = Mode.Weight; _modeFromEnv = true; break;
                case "timeline" or "clip": Carry = Mode.Timeline; _modeFromEnv = true; break;
            }
            _onFromEnv = true;
        }
        _probe = !string.IsNullOrWhiteSpace(probe) && probe != "0";
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            if (!_onFromEnv)
                Enabled = RecompOne.Runtime.Runtime.View.GetBool(OnKey, Enabled);
            if (!_modeFromEnv)
            {
                int mode = RecompOne.Runtime.Runtime.View.GetInt(ModeKey, (int)Carry);
                if (Enum.IsDefined(typeof(Mode), mode)) Carry = (Mode)mode;
            }
        });

        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            // The banks are re-linked and the clip records move with them, so a
            // cached length is not merely stale, it is a read of someone else's
            // memory.
            _slots.Clear();
            _lengths.Clear();
            _depth = 0;
            _pending = false;
            _clockSeen = false;
        });

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    public static void SetEnabled(bool on) => Enabled = on;

    /// <summary>Switch modes at run time, for comparison by eye. The per-slot
    /// state means different things in each mode, so it is thrown away rather
    /// than reinterpreted -- the cost is one tick of stepping at the switch.</summary>
    public static void SetCarry(Mode mode)
    {
        if (Carry == mode) return;
        Carry = mode;
        _slots.Clear();
    }

    static void Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(AnimSmoothing);
        int n = 0;
        n += Pre(self, Renderer, nameof(BeforeRenderer)) ? 1 : 0;
        n += Pair(self, ModelSubmit, nameof(BeforeSubmit), nameof(AfterSubmit)) ? 1 : 0;
        n += Pair(self, ClipClock, nameof(BeforeClock), nameof(AfterClock)) ? 1 : 0;
        HookManager.Commit();

        Console.WriteLine($"[KF2] anim: {(Enabled ? Carry.ToString().ToLowerInvariant() : "off")}" +
                          (_probe ? ", probe" : "") +
                          $", hooked {n} of 3 site(s) (clip clock)");
    }

    static MethodInfo? Target(uint addr)
    {
        var target = SymbolRegistry.Resolve("game", null, addr);
        if (target == null)
            Console.Error.WriteLine($"[KF2] anim: no game function at 0x{addr:X8}");
        return target;
    }

    static bool Pre(Type self, uint addr, string pre)
    {
        var target = Target(addr);
        if (target == null) return false;
        var before = self.GetMethod(pre, BindingFlags.Public | BindingFlags.Static)!;
        return HookManager.AddPre(_self, target, before);
    }

    static bool Pair(Type self, uint addr, string pre, string post)
    {
        var target = Target(addr);
        if (target == null) return false;
        var before = self.GetMethod(pre, BindingFlags.Public | BindingFlags.Static)!;
        var after = self.GetMethod(post, BindingFlags.Public | BindingFlags.Static)!;

        // Both, then the verdict: `AddPost(..) && AddPre(..)` would leave the pre
        // unattached and unreported if the post ever failed.
        bool posted = HookManager.AddPost(_self, target, after);
        bool pred = HookManager.AddPre(_self, target, before);
        return posted && pred;
    }

    /// <summary>
    /// The frame bracket. The tick is noticed here rather than inside the submit
    /// so that a slot samples once per *tick* however many frames that tick is
    /// drawn over -- and, at the tick rate, so that it samples at all.
    /// </summary>
    public static void BeforeRenderer(CpuContext c, IMemory m)
    {
        if (FramePacing.TickedThisFrame) _tick++;
        _phase = FramePacing.LogicPhase;
        _depth = 0;
        _pending = false;
        _clockSeen = false;
    }

    public static void BeforeSubmit(CpuContext c, IMemory m)
    {
        _depth++;
        if (_depth != 1) return;

        _pending = false;
        _clockSeen = false;
        _slot = null;
        if (_probe) _submits++;

        // The incoming stack, before the prologue: +0x1C is the clip byte,
        // +0x20 the integer clip time.
        int clip = (int)(m.ReadU32(c.SP + 0x1Cu) & 0xFFu);
        int time = (int)(m.ReadU32(c.SP + 0x20u) & 0xFFFFu);
        uint id = c.A2;

        if (clip >= 0x80)
        {
            if (_probe) _rigid++;
            return;
        }

        if (_probe)
        {
            _morph++;
            // A morph submit whose clip time is 0 is a prop posed through the MO
            // path rather than a clip being played -- one of the five call sites
            // in func_800331B4 passes a literal 0. Counting them apart is what
            // separates "nothing to carry" from "nothing animating on screen".
            if (time != 0) _live++;
        }
        if (id == 0) return;

        var slot = Get(id);
        _slot = slot;
        if (slot.Clip != clip)
        {
            Reset(slot);
            slot.Clip = clip;
        }

        if (slot.Tick != _tick)
        {
            slot.Tick = _tick;
            if (slot.HasCur)
            {
                slot.PrevTime = slot.CurTime;
                slot.HasPrev = true;
            }
            slot.CurTime = time;
            slot.HasCur = true;
            if (!slot.HasMax || time > slot.MaxTime) { slot.MaxTime = time; slot.HasMax = true; }

            slot.Step = 0;
            slot.Wrapped = slot.Mirrored = false;
            slot.Say = Verdict.Still;
            if (slot.HasPrev)
            {
                if (Carry == Mode.Timeline) Predict(slot);
                else Classify(slot);
            }
            if (_probe) Census(slot);
        }

        if (!Enabled || !FramePacing.Gating || !slot.HasPrev) return;

        // The root this pose hangs off is stepping at the tick rate, because
        // ObjectSmoothing judged its step a placement and left it where the game
        // put it. Morphing the vertices at the frame rate on top of a root that
        // jumps is worse than not smoothing the pose: the two disagree about what
        // time it is, and the pose reads as sliding or snapping ahead of the body
        // -- reported, on a boss moving fast, as its head snapping into the next
        // frame of the animation. Hold the pose with the root instead, so the
        // creature degrades to a coherent tick-rate creature rather than an
        // incoherent smooth one.
        if (ObjectSmoothing.PositionHeld(id))
        {
            if (_probe) _rootHeld++;
            return;
        }

        if (Carry == Mode.Timeline) { PrepareTimeline(slot); return; }
        if (Carry == Mode.Weight) { PrepareWeight(slot); return; }
        PrepareTime(slot);
    }

    // ---- Mode.Timeline --------------------------------------------------------

    /// <summary>
    /// Decide whether this tick's step is playback on the clip's own circle, and
    /// if so along which path.
    ///
    /// The clip time lives on a circle of circumference `D`, so the observed
    /// `cur` is consistent with any step `cur + kD - prev`. Two more candidates
    /// cover a clip that **reflects** at an endpoint instead of wrapping: a turn
    /// at 0 lands at `-raw`, a turn at the end at `2D - raw`. Playback is
    /// constant velocity, so the candidate nearest the settled rate wins if it is
    /// within <see cref="RateTolRel"/> of it -- and that single test is the cycle
    /// wrap, the reverse clip, the ping-pong turn and ordinary playback at once.
    ///
    /// Nothing matched means the game moved the clip somewhere playback could not
    /// have taken it: a re-seek, or an attack the AI is restarting every tick.
    /// Either way the tick is held and the rate is re-seeded from what was
    /// actually seen, so a clip that genuinely changed speed costs one tick.
    /// </summary>
    static void Predict(Slot s)
    {
        int step = s.CurTime - s.PrevTime;
        s.Step = step;

        if (step == 0)
        {
            // A clip that has not moved. Nothing to carry, and deliberately not
            // a rate either: the settled rate and `FirstStep` are both left
            // alone, so a clip that is posed for a tick before it starts still
            // gets its opening step taken on trust, and one paused mid-playback
            // does not have to re-settle when it resumes.
            s.Say = Verdict.Still;
            return;
        }

        int d = s.Length;
        if (d <= 0)
        {
            // The length is read at the clock, which runs after this. Until the
            // slot has been through one, there is no circle to unwrap on.
            s.Say = Verdict.Hold;
            return;
        }

        double best;
        var path = Fold.Circle;
        bool wrapped = false, mirrored = false;

        if (!s.HasRate)
        {
            // Nothing to check against yet. The first moving tick of a clip *is*
            // the definition of its rate -- the game just started playing it and
            // there is nothing to have re-seeked away from -- so a step within
            // reach of a rate is carried straight away. A bigger one is not
            // refused, it is **made to wait one tick** and confirmed by
            // repetition instead; refusing it outright stranded any clip whose
            // real rate was that big, which is what a gecko's backflip is.
            best = Nearest(step, d, 0.0);
            if (!s.FirstStep || !Trusted(best, d))
            {
                Seed(s, best);
                s.FirstStep = false;
                if (_probe) Refused(best);
                s.Say = Verdict.Hold;
                return;
            }
        }
        else
        {
            double rate = s.Rate;
            double tol = Window(rate, d);

            best = Nearest(step, d, rate);
            double err = Math.Abs(best - rate);

            if (err > tol)
            {
                // Constant velocity along the circle cannot explain this tick.
                // **Only now** is a turn at an endpoint worth considering, and
                // only if the clip would genuinely have run off that end: the
                // reflection is a free extra parameter, so offering it whenever
                // it merely scores better than straight playback lets it win on
                // noise. It did -- a clip that simply slowed near the end of its
                // cycle read as a turn, and a spurious turn runs the pose to the
                // endpoint and back inside one tick, which is a shake at the
                // frame rate. The two columns that caught it are in the probe:
                // turns were being counted while `in reverse` stayed at 0, and a
                // real turn is always followed by reverse playback.
                double overshoot = s.PrevTime + rate;
                double turn =
                    rate > 0 && overshoot > d ? 2.0 * d - s.CurTime - s.PrevTime :
                    rate < 0 && overshoot < 0 ? -s.CurTime - s.PrevTime :
                    double.NaN;

                if (double.IsNaN(turn) || Math.Abs(turn - rate) > tol)
                {
                    // Not playback at all. Re-seed from what was actually seen
                    // so the next tick has something to confirm against, and
                    // hold this one.
                    double seen = Nearest(step, d, 0.0);
                    Seed(s, seen);
                    if (_probe) Refused(seen);
                    s.Say = Verdict.Hold;
                    return;
                }

                best = turn;
                path = Fold.Mirror;
            }

            mirrored = path == Fold.Mirror;
            wrapped = !mirrored && (s.PrevTime + best < 0 || s.PrevTime + best >= d);
        }

        s.FirstStep = false;
        s.Delta = best;
        s.Path = path;
        s.Wrapped = wrapped;
        s.Mirrored = mirrored;

        // **After a turn the clip is running the other way.** `best` is the
        // length of the path it took this tick, which is the speed; the
        // direction is reversed, so the rate the *next* tick is predicted
        // against is its negation. Storing the unwrapped path length instead
        // kept the pre-turn sign, so every turn mispredicted the tick after it,
        // re-seeded, and turned again -- an oscillation this patch generated by
        // itself, and the reason no turn was ever followed by reverse playback.
        s.Rate = mirrored ? -best : best;
        s.HasRate = true;
        s.Say = best == 0.0 ? Verdict.Still : Verdict.Play;
    }

    /// <summary>
    /// Whether an opening step is small enough to be carried **without** the
    /// confirming tick every other step needs. See <see cref="FirstStepFrac"/>.
    /// A step past it is not refused, only made to wait a tick.
    /// </summary>
    static bool Trusted(double delta, int d) => Math.Abs(delta) <= d * FirstStepFrac;

    /// <summary>The acceptance window around the settled rate, floored by
    /// <see cref="RateTolAbs"/> and capped by <see cref="RateTolCap"/>.</summary>
    static double Window(double rate, int d) =>
        Math.Min(Math.Max(RateTolAbs, Math.Abs(rate) * RateTolRel), d * RateTolCap);

    /// <summary>
    /// Record what a held tick saw, as the rate the next one is checked against.
    ///
    /// **Whatever its size.** A step too big to be carried on trust is still the
    /// best available guess at what the clip is doing, and refusing to record it
    /// is what stranded a fast clip: with nothing recorded the next tick reasons
    /// identically, holds identically, and the slot never settles a rate at all.
    /// The danger a large rate poses is not that it is large, it is that the
    /// acceptance window scales with it — so that is capped
    /// (<see cref="RateTolCap"/>) and the size itself is allowed. A tick that
    /// cannot be explained then costs one held tick and a fresh guess, which is
    /// the bounded fallback the mode needs: **being wrong costs a stepped tick
    /// rather than a pose from elsewhere in the clip.**
    /// </summary>
    static void Seed(Slot s, double delta)
    {
        s.Rate = delta;
        s.HasRate = true;
    }

    /// <summary>
    /// The unwrapped step nearest <paramref name="rate"/> that lands on
    /// <c>step (mod d)</c>. `k` is chosen directly rather than searched: the
    /// wanted step is `rate`, so the wrap count is the one that puts
    /// `step + k*d` closest to it.
    /// </summary>
    static double Nearest(int step, int d, double rate)
    {
        double k = Math.Round((rate - step) / d);
        return step + k * d;
    }

    static void PrepareTimeline(Slot s)
    {
        if (s.Say != Verdict.Play) return;

        double raw = s.PrevTime + s.Delta * _phase;
        double t = s.Path == Fold.Mirror ? Mirror(raw, s.Length) : Circle(raw, s.Length);

        _tFloor = (int)Math.Floor(t);
        _carry = t - _tFloor;
        if (_tFloor < 0) { _tFloor = 0; _carry = 0.0; }
        _pending = true;
    }

    /// <summary>A time folded back onto the clip's circle, for a path that ran
    /// off either end and continued.</summary>
    static double Circle(double t, int d)
    {
        double r = t % d;
        return r < 0 ? r + d : r;
    }

    /// <summary>A time folded back onto the clip as a triangle wave, for a path
    /// that turned at an endpoint instead of wrapping.</summary>
    static double Mirror(double t, int d)
    {
        double r = Circle(t, 2 * d);
        return r <= d ? r : 2.0 * d - r;
    }

    /// <summary>
    /// The clip's total length, summed off the same segment table
    /// `func_8003486C` walks -- which is the only place it exists.
    ///
    /// `clipTable = bank + u32[bank+0x10]`, `clipRec = bank + u32[clipTable +
    /// clip*4]`, `u16[clipRec]` segments, `u32[clipRec + 4 + 4i]` bank-relative
    /// offsets to records whose `u16` at `+0x2` is the duration the clock
    /// accumulates. 0 for anything that does not read as a clip record: the
    /// length is the whole basis of <see cref="Mode.Timeline"/>, so a slot
    /// without one holds rather than guessing.
    /// </summary>
    static int Duration(IMemory m, uint bank, uint clip)
    {
        if (_lengths.TryGetValue((bank, clip), out int cached)) return cached;

        int total = 0;
        if (Ram(bank))
        {
            uint table = bank + m.ReadU32(bank + 0x10u);
            if (Ram(table) && Ram(table + clip * 4u))
            {
                uint rec = bank + m.ReadU32(table + clip * 4u);
                if (Ram(rec))
                {
                    int count = (int)m.ReadU16(rec);
                    if (count > 0 && count <= MaxSegments && Ram(rec + 4u + (uint)count * 4u))
                    {
                        for (int i = 0; i < count; i++)
                        {
                            uint seg = bank + m.ReadU32(rec + 4u + (uint)i * 4u);
                            if (!Ram(seg)) { total = 0; break; }
                            total += m.ReadU16(seg + 2u);
                        }
                    }
                }
            }
        }

        _lengths[(bank, clip)] = total;
        return total;
    }

    /// <summary>Main RAM, so a wild pointer is refused before it is read
    /// through. The clip records live in the loaded banks, all of which are
    /// here.</summary>
    static bool Ram(uint addr) => addr >= 0x80010000u && addr < 0x80200000u;

    // ---- Mode.Weight ----------------------------------------------------------

    static void PrepareWeight(Slot s)
    {
        // A wrap's own step is a cycle length; what the tick actually advanced by
        // is the playback rate behind it.
        int advance = s.Say == Verdict.Wrap ? s.LastStep : s.Step;

        switch (s.Say)
        {
            case Verdict.Wrap:
                if (_probe) _wrapCarried++;
                break;
            case Verdict.Play:
                if (_probe && s.Step < 0) _backward++;
                break;
            default:
                return;   // Still, or a hold: what the game asked for stands
        }

        // This tick already asked for more than its segment could give. The
        // remaining frames of it stand *closer* to the game's pose and would
        // carry, so letting them run would step the pose backwards at the
        // boundary between the frame that refused and the frame that did not.
        if (s.OverrunTick == _tick) return;

        // The frame stands `(1 - phase)` of a tick behind the pose the game asked
        // for. Say that in clip time and let AfterClock spend it on the weight,
        // inside the game's own segment -- the integer time is not touched at
        // all, so the segment index and the blender's cache are exactly what they
        // would be with this patch absent.
        _back = (1.0 - _phase) * advance;
        _pending = true;
    }

    // ---- Mode.Time ------------------------------------------------------------

    static void PrepareTime(Slot s)
    {
        double t;
        switch (s.Say)
        {
            case Verdict.Wrap:
                // The clip looped. Lerping prev -> cur across it plays the whole
                // cycle *backwards* over one tick, so run it forwards through the
                // turnover instead.
                if (_probe) _wrapCarried++;
                t = WrapTime(s, _phase);
                break;

            case Verdict.Play:
                if (_probe && s.Step < 0) _backward++;
                t = s.PrevTime + s.Step * _phase;
                break;

            default:
                return;   // Still, or a hold: whatever the game asked for stands
        }

        _tFloor = (int)Math.Floor(t);
        _carry = t - _tFloor;
        _pending = true;
    }

    // ---- the hooks ------------------------------------------------------------

    public static void AfterSubmit(CpuContext c, IMemory m)
    {
        if (_depth == 0) return;
        _depth--;
        if (_depth != 0) return;
        _pending = false;
        _clockSeen = false;
        if (_probe) Report();
    }

    /// <summary>
    /// <c>func_8003486C</c>: `a0` is the bank, `a1` the clip, `a2` the integer
    /// clip time. The length is read here because this is the only site that sees
    /// which table the clock is about to walk; the carry hands it `floor(t)` so
    /// the segment pick matches the in-between instant, and
    /// <see cref="AfterClock"/> adds the fraction onto the 12.12 weight.
    /// </summary>
    public static void BeforeClock(CpuContext c, IMemory m)
    {
        if (_depth == 0 || _clockSeen) return;
        _clockSeen = true;

        // Learn the clip's length whether or not this tick is being carried: the
        // length is what the *next* tick's predicate needs, and a slot that only
        // ever holds would otherwise never acquire one.
        if (_slot != null && Carry == Mode.Timeline)
        {
            uint bank = c.A0, clip = c.A1;
            if (_slot.Length <= 0 || _slot.Bank != bank)
            {
                _slot.Bank = bank;
                _slot.Length = Duration(m, bank, clip);
            }
        }

        if (!_pending) return;

        // func_8003486C has no prologue, so SP is still the blender's and
        // SP+0x10 is the weight slot it stored for the clock to fill in.
        _weightPtr = m.ReadU32(c.SP + 0x10u);

        // In Weight mode the time is deliberately left alone: the segment the
        // game picked is the segment that gets drawn.
        if (Carry != Mode.Weight) c.A2 = (uint)_tFloor;
    }

    public static void AfterClock(CpuContext c, IMemory m)
    {
        if (!_pending) return;
        _pending = false;
        double spend = Carry == Mode.Weight ? _back : _carry;
        if (_weightPtr == 0 || spend == 0.0) return;
        if (Carry != Mode.Weight && spend < 0.0) return;

        // v0 is the segment record: +0x0 the direction flag, +0x2 the duration
        // the weight was divided by.
        uint segment = c.V0;
        if (segment == 0) return;
        int duration = m.ReadU16(segment + 2u);
        if (duration <= 0) return;

        // Weight mode spends a *backward* offset from the game's own pose, so the
        // sign flips; the clamp below is what keeps it inside this segment.
        int add = (int)Math.Round(spend * 4096.0 / duration);
        if (Carry == Mode.Weight) add = -add;
        if (add == 0) return;

        // A flagged segment is published as 0x1000 - raw, so its weight runs down
        // as the clip runs forward and the carry has to go the other way.
        bool reversed = m.ReadU16(segment) != 0;
        if (reversed) add = -add;

        int weight = (int)m.ReadU32(_weightPtr);
        int want = weight + add;
        int next = Math.Clamp(want, 0, 0x1000);

        // In Weight mode a clamped weight is not a pose between two ticks, it is
        // the segment's own end, and writing it made a fast clip jitter rather
        // than smooth: the carry is refused for the whole tick instead. Timeline
        // mode cannot get here for that reason -- its fraction is under one clip
        // unit, so it cannot leave the segment `floor(t)` landed in -- and the
        // clamp is only rounding at the very top of a segment.
        if (Carry == Mode.Weight && next != want)
        {
            if (_slot != null) _slot.OverrunTick = _tick;
            if (_probe) _overran++;
            return;
        }

        m.WriteU32(_weightPtr, (uint)next);

        if (_probe)
        {
            _carried++;
            if (reversed) _reversed++;
            _fracSum += Math.Abs(spend);
        }
    }

    // ---- the legacy classifier (Mode.Weight and Mode.Time) --------------------

    /// <summary>
    /// The pre-timeline predicate, kept with the two modes that need it. It
    /// separates playback from a cycle wrap, a re-seek and a clip being fought
    /// over using the landing site, a synthesised turnover and a magnitude
    /// cutoff, because it has no clip length to work on. See "The bounded mode"
    /// in `docs/PATCHES_AND_MODS.md`.
    /// </summary>
    static void Classify(Slot s)
    {
        int step = s.CurTime - s.PrevTime;
        s.Step = step;
        if (step == 0)
        {
            // A frozen clip is not a fight: nothing to carry, nothing to hold
            // against, and a run of one pose is not a run of playback.
            s.Say = Verdict.Still;
            s.Run = 0;
            return;
        }

        int adv = Math.Abs(s.LastStep);
        if (adv > 0 && Math.Sign(step) != Math.Sign(s.LastStep))
        {
            bool atStart = s.LastStep > 0
                ? s.CurTime <= adv                    // forward, back to the head
                : s.CurTime >= s.MaxTime - adv;       // reverse, back to the tail

            if (atStart && Math.Abs(step) > adv)
            {
                // A cycle turned over. Believe it only off a settled run, since
                // the turnover is synthesised out of LastStep rather than
                // measured; otherwise hold, which is the console's hard cut.
                s.Say = s.Run >= LegacyWrapRun ? Verdict.Wrap : Verdict.Hold;
                s.Run = 0;
                // A wrap's step is a cycle length, not a rate: recording it would
                // make the *next* wrap unrecognisable.
                return;
            }

            s.Flips++;
            s.Run = 0;
        }
        else
        {
            s.Run++;
            if (s.Flips > 0) s.Flips--;
        }

        if (Math.Abs(step) > LegacyMaxTimeStep)
        {
            // A re-seek: a restart, or a jump to somewhere else in the same clip.
            // Not a rate either, so LastStep stands.
            s.Say = Verdict.Hold;
            s.Run = 0;
            return;
        }

        // The sign is a direction, not a discontinuity: a clip played in reverse
        // runs its time down by the same small amount a forward one runs it up.
        s.LastStep = step;
        s.Say = s.Flips >= LegacyThrashFlips ? Verdict.Hold : Verdict.Play;
    }

    /// <summary>The old magnitude cutoff on a clip-time step, in
    /// <see cref="Mode.Weight"/> and <see cref="Mode.Time"/> only.</summary>
    const int LegacyMaxTimeStep = 4096;

    /// <summary>Same-direction ticks required before a turnover is synthesised
    /// rather than held, in the legacy modes.</summary>
    const int LegacyWrapRun = 2;

    /// <summary>Reversals, net of playback, at which a legacy slot is declared
    /// stuck and left at the game's own time.</summary>
    const int LegacyThrashFlips = 3;

    /// <summary>
    /// The in-between time on the tick a cycle turned over, without knowing how
    /// long the cycle is -- the legacy modes' substitute for the clip length.
    /// The clip advances about <c>LastStep</c> per tick, and the part of that
    /// already spent in the *new* cycle is the new time itself, so the turnover
    /// happened at <c>1 - CurTime/LastStep</c> of the way through the tick.
    /// </summary>
    static double WrapTime(Slot s, double phase)
    {
        int dir = Math.Sign(s.LastStep);
        double adv = Math.Abs(s.LastStep);
        double intoNew = dir > 0 ? s.CurTime : Math.Max(0, s.MaxTime - s.CurTime);
        double tailFrac = 1.0 - Math.Min(1.0, intoNew / adv);

        double t = phase < tailFrac
            ? s.PrevTime + dir * (phase * adv)                               // finishing
            : (dir > 0 ? 0.0 : s.MaxTime) + dir * ((phase - tailFrac) * adv); // restarted

        // A negative time would land in the first segment with a negative weight,
        // which the clock writes out as a huge unsigned one.
        return t < 0.0 ? 0.0 : t;
    }

    // ---- bookkeeping ----------------------------------------------------------

    static void Reset(Slot s)
    {
        s.HasCur = s.HasPrev = s.HasMax = false;
        s.Length = 0;
        s.Bank = 0;
        s.Rate = 0;
        s.HasRate = false;
        s.FirstStep = true;
        s.Delta = 0;
        s.Path = Fold.Circle;
        s.Wrapped = s.Mirrored = false;
        s.LastStep = s.Step = s.MaxTime = s.Run = s.Flips = 0;
        s.OverrunTick = -1;
        s.Say = Verdict.Still;
    }

    static Slot Get(uint id)
    {
        if (!_slots.TryGetValue(id, out var s))
            _slots[id] = s = new Slot();
        return s;
    }

    static void Census(Slot s)
    {
        if (!s.HasPrev) return;
        int d = Math.Abs(s.Step);

        if (Carry == Mode.Timeline)
        {
            switch (s.Say)
            {
                case Verdict.Play:
                    _play++;
                    if (s.Wrapped) _wrapped++;
                    if (s.Mirrored) _mirrored++;
                    if (s.Delta < 0) _backward++;
                    _clipTicks++;
                    // The *unwrapped* step, which is the number the mode acts
                    // on. The raw one is a cycle length on a wrap tick and would
                    // read as a huge outlier next to the rate beside it.
                    int u = (int)Math.Round(Math.Abs(s.Delta));
                    _timeStepSum += u;
                    if (u > _maxStep) _maxStep = u;
                    if (u < _minStep) _minStep = u;
                    break;
                case Verdict.Hold:
                    if (s.Length <= 0) _noLength++;
                    else if (!s.HasRate || s.FirstStep) _unsettled++;
                    else _held++;
                    break;
            }
            if (s.Length > _maxTimeSeen) _maxTimeSeen = s.Length;
            return;
        }

        if (s.Say == Verdict.Wrap) _wraps++;
        else if (s.Say == Verdict.Hold)
        {
            if (s.Flips >= LegacyThrashFlips) _stuck++; else _skipped++;
        }
        else if (s.Say == Verdict.Play)
        {
            _clipTicks++;
            _timeStepSum += d;
            if (d > _maxStep) _maxStep = d;
            if (d < _minStep) _minStep = d;
        }
        if (s.MaxTime > _maxTimeSeen) _maxTimeSeen = s.MaxTime;
    }

    static void Report()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _reportedAt < 1000.0) return;
        _reportedAt = now;

        string morphPct = _submits == 0 ? "n/a" : $"{100.0 * _morph / _submits:0.0}%";
        string live = _morph == 0 ? "" : $", {_live} with a running clip";
        string step = _clipTicks == 0 ? "" :
            $", step {_minStep}..{_maxStep} mean {_timeStepSum / _clipTicks:0.0}";
        string carry = !Enabled ? ""
            : _carried == 0 ? "; 0 weights carried"
            : $"; {_carried} weight(s) carried ({_reversed} reversed), " +
              $"mean frac {_fracSum / _carried:0.00}";
        string root = _rootHeld == 0 ? "" : $", {_rootHeld} refused (root held)";

        string verdicts = Carry == Mode.Timeline
            ? $"; {_play} playback ({_wrapped} on the wrap, {_mirrored} turned, " +
              $"{_backward} in reverse), {_held} held (no match), " +
              $"{_unsettled} settling, {_noLength} with no clip length" +
              (_maxRefused > 0 ? $", widest refused {_maxRefused}" : "") +
              (_maxTimeSeen > 0 ? $", longest clip {_maxTimeSeen}" : "")
            : $"; {_wraps} cycle wrap(s) (longest time seen {_maxTimeSeen}), " +
              $"{_wrapCarried} carried through, {_skipped} re-seek(s), {_stuck} stuck, " +
              $"{_backward} playing backwards" +
              (_overran == 0 ? "" : $", {_overran} refused (left the segment)");

        Console.WriteLine($"[KF2] anim({Carry.ToString().ToLowerInvariant()}): " +
                          $"{_submits} submit(s), morph {_morph} ({morphPct}), " +
                          $"rigid {_rigid}{live}{step}{verdicts}{carry}{root}");

        _submits = _morph = _rigid = _live = _carried = _reversed = 0;
        _play = _wrapped = _mirrored = _backward = _held = _unsettled = _noLength = _rootHeld = 0;
        _clipTicks = _wraps = _wrapCarried = _stuck = _skipped = _overran = 0;
        _timeStepSum = _fracSum = 0;
        _maxTimeSeen = _maxRefused = 0;
        _maxStep = 0; _minStep = int.MaxValue;
    }
}
