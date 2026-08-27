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
///     KF2_SMOOTH_ANIM=1        on (weight only, bounded); off by default
///     KF2_SMOOTH_ANIM=time     also drive the integer clip time (unbounded)
///     KF2_SMOOTH_ANIM_PROBE=1  morph vs rigid submits, and the time step
///
/// It is a setting under Video, beside the three other smoothing checkboxes.
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
/// feeds this function from four different tables with four different strides
/// (`func_800331B4`'s object loop walks 0x48 and takes the pair from `S0-0x5`
/// and `S0+0x9`), and the argument list is the one description true for all of
/// them.
///
/// `func_8003486C(bank, clip, time, &amp;segment, &amp;weight)` turns that integer time
/// into a segment index and a 12.12 weight: it accumulates the per-segment
/// durations at `segment+0x2` until the time falls inside one, then publishes
/// `((time - segmentStart) &lt;&lt; 12) / duration`, and returns the segment record
/// itself in `v0`.
///
/// **Why driving that clock reaches the mesh at all** is `L80034FCC` in
/// `func_80034DA8`, and it is the load-bearing observation: when the clip and the
/// segment index are both unchanged the blender skips rebuilding its keyframe
/// *cache*, but it still copies the base mesh into `0x80190AD8` and still calls
/// the decoder with the weight `func_8003486C` just wrote. Stage 13 therefore
/// re-morphs every rendered frame; only the time it morphs *to* is stuck on the
/// tick. Move the weight and the mesh moves.
///
/// ## Two modes, and why the bounded one is the default
///
/// Play reported poses **spazzing out with interpolation on and never at 20
/// fps**, which is the reading that matters: at 20 fps the phase is 0 on every
/// frame, so the carry is zero and this patch does nothing. The game's own data
/// is therefore fine and the in-between pose is what is wrong.
///
/// Everything static analysis can settle here says the mechanism is sound --
/// the blender's keyframe rebuild is **absolute rather than incremental** (a
/// fixed keyframe list per segment, `func_80034934` then `func_800349F8`), so
/// asking it for a segment out of order cannot desynchronise a stream; the
/// weight slot is genuinely the caller's (`func_8003486C` has no prologue, so
/// `SP+0x10` is the blender's frame); the reversed-segment sign is right. What
/// *cannot* be settled from here is what the whole pipeline does when the
/// **segment index moves at the frame rate** rather than at the tick rate, and
/// that is the one thing driving the integer time does that nothing else in this
/// port does.
///
/// So the default stopped doing it. <see cref="Mode.Weight"/> leaves the integer
/// time exactly as the game wrote it and spends the phase on the **12.12 blend
/// weight alone**, clamped to the segment. The pose is then always between that
/// segment's start and the pose the game asked for -- it cannot reach anywhere
/// the game would not have drawn this tick, whatever the clip time is doing --
/// and the segment index, the cache and every decode are bit-for-bit what they
/// are with the patch absent. That bound holds under every explanation of the
/// spazzing that could not be eliminated, which is why it is the default rather
/// than one more guess at the cause.
///
/// It costs motion *across* a segment boundary, which clamps at the boundary
/// instead of continuing through. A tick advances roughly one segment (mean step
/// 511 against `u16` durations), so most of the in-between motion is inside a
/// segment and survives. <see cref="Mode.Time"/> (`KF2_SMOOTH_ANIM=time`) is the
/// old behaviour, kept for comparison by eye.
///
/// ## What this does
///
/// One pre on **stage 13** (`func_800342D8`) to open the frame -- it is where the
/// tick is noticed and the phase read, the same bracket
/// <see cref="ObjectSmoothing"/> uses -- then a pre/post pair on the model submit
/// and a pre/post pair on the clip clock. On a morph submit it interpolates last
/// tick's time with this tick's, hands `floor(t)` to `func_8003486C` so the
/// *segment* pick matches the in-between instant, and adds the leftover fraction
/// onto the 12.12 weight the decoder then consumes.
///
/// **It interpolates (`t-1+frac`) rather than extrapolating**, which is what
/// <see cref="FrameSmoothing"/> and <see cref="ObjectSmoothing"/> both do now: a
/// pose is not steered by the player, so the tick of latency is free, and the
/// three have to agree about what time it is or the parts of a creature read as
/// running at different speeds.
///
/// **The reversed segment is a real case and the sign is not free.** When the
/// flag `u16` at `segment+0x0` is set, `func_8003486C` publishes `0x1000 - raw`,
/// so the weight *decreases* as time advances. Adding the carry there would run
/// those segments backwards between ticks, so it is subtracted instead.
///
/// Nothing here writes game state. `c.A2` is a register on one call and the
/// weight lives in the *caller's* own stack temp, both consumed before
/// `func_80034DA8` returns -- so unlike the table smoothers there is nothing to
/// put back and nothing that can leak into the next tick's AI or a save.
///
/// A slot whose clip time jumps more than <see cref="MaxTimeStep"/> in a tick is
/// left alone, the pose equivalent of the placement guard in
/// <see cref="ObjectSmoothing"/>. **The guard is on the size and not on the
/// sign**, and that distinction cost one bug each way: setting the size near a
/// plausible-looking number (32, against a real 511) made the first version carry
/// nothing at all, and treating a *negative* step as a discontinuity left every
/// clip played in reverse stepping at the tick rate -- the drawbridge lever went
/// up in 50 ms jumps while the same lever came down smoothly. A clip swap is
/// caught by the clip byte; only the size separates playback from a re-seek.
///
/// ## The end of a cycle is not a re-seek
///
/// A looping clip runs its time up and then resets it, keeping the same clip
/// byte -- so the wrap arrives here as one ordinary tick whose step is a whole
/// cycle *backwards*. Interpolating that plays the animation in reverse, at
/// cycle-per-tick speed, over every frame of that tick: the rewind reported as
/// "it snaps back to the first position at the end of the cycle". The size guard
/// does not catch it and should not -- a cycle shorter than
/// <see cref="MaxTimeStep"/> slips under it, and one longer only turns the rewind
/// into a hard cut.
///
/// <see cref="Classify"/> separates the wrap from a re-seek on **where the time
/// landed** rather than on how far it moved: a loop turning over lands within one
/// tick's advance of the cycle's first frame, because the overshoot past the end
/// is what the new time is made of. <see cref="WrapTime"/> then runs the tick
/// *forwards* through the turnover -- finishing the old cycle for the first
/// `1 - CurTime/LastStep` of it and running the new one for the rest -- so the
/// loop is continuous instead of rewound. It needs no clip length, which is as
/// well: the only place the total duration exists is the segment table
/// `func_8003486C` walks, and asking that clock for a time past the end is safe
/// anyway (it answers with the last segment at a full `0x1000` weight, the pose
/// the clip ends on).
///
/// The turnover is **synthesised** out of `LastStep` rather than measured, so it
/// is only believed off a settled run of playback (<see cref="WrapRun"/>).
/// Without that, a clip whose time is merely *jittering* near its own head reads
/// as wrapping every other tick, and the pose invented for it sweeps a fraction
/// of the clip and then cuts to the head -- far more violent than the hard cut
/// it replaced.
///
/// ## A clip being fought over is not animating
///
/// The last case is a clip the game cannot settle: an attack whose animation the
/// AI restarts every tick because the conditions to finish it are never met --
/// a piranha, or the final boss with the player under its head. Its time steps
/// one way and back the next tick, never landing at a cycle boundary, and
/// interpolating that sweeps the pose *continuously* between the two poses
/// instead of alternating between them, which reads as a violent shake rather
/// than as the 20 Hz flicker the console showed. <see cref="ThrashFlips"/>
/// reversals, net of steady playback, and the slot is held at the game's own
/// time until it resolves. Holding is not a repair of the game's own indecision;
/// it is a refusal to draw it more often than the game makes it.
///
/// `KF2_SMOOTH_ANIM_PROBE=1` counts the wraps, how many were carried through,
/// and how many slots are being held stuck.
///
/// **The picture has been looked at and it works** -- which is worth saying
/// plainly, because no counter in this repo could have said it: every scene an
/// agent could drive itself to had morph submits with a clip time of 0, and the
/// probe's "with a running clip" column exists to keep that reading as "no
/// subject" rather than "no effect". It stays off by default anyway; whether
/// smooth poses are the authentic picture is the same kind of judgement
/// <see cref="FramePacing.LogicHz"/> is.
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

    /// <summary>The MO clip clock: integer time in, segment index and 12.12
    /// weight out.</summary>
    const uint ClipClock = 0x8003486C;

    /// <summary>
    /// A backstop on the clip-time step, not a filter. **32 was a guess and it
    /// suppressed everything**: the one scene measured with a clip actually
    /// running stepped a mean of 511 units a tick, so 24 of its 30 morph submits
    /// were skipped and nothing was ever carried.
    ///
    /// A clip being **swapped** is caught by `slot.Clip != clip` and needs no
    /// magnitude. What this catches is a **re-seek** — a restart, or a jump to
    /// somewhere else in the same clip — and it has to do it **on both signs**,
    /// because the sign alone is a direction: reverse playback and a restart both
    /// run the time down, and only the size tells them apart. Set well clear of
    /// any step observed playing rather than close to it: 12.12's whole unit,
    /// eight times the only measurement.
    /// </summary>
    const int MaxTimeStep = 4096;

    /// <summary>
    /// How many consecutive same-direction ticks a slot must have behind it
    /// before a turnover is *synthesised* rather than held.
    ///
    /// <see cref="WrapTime"/> makes a wrap tick up out of `LastStep`, so a
    /// `LastStep` that is not a real playback rate makes one up wrongly — and
    /// the pose it invents sweeps a fraction of the clip and then cuts to the
    /// head, which is far more violent than the hard cut it replaces. Two is
    /// enough to exclude a slot whose time is oscillating (which never gets a
    /// run at all) while still catching the first wrap of any clip three ticks
    /// long or more.
    /// </summary>
    const int WrapRun = 2;

    /// <summary>
    /// Reversals, net of playback, at which a slot is declared stuck and left at
    /// the game's own time.
    ///
    /// **A clip whose time is being fought over is not animating**, and
    /// carrying it renders the fight at the frame rate instead of at the tick
    /// rate: an attack the AI cannot resolve — a piranha or the final boss with
    /// the player under its head — steps its time one way and back the next
    /// tick, and interpolating that sweeps the pose continuously between the two
    /// instead of alternating between them, which reads as a violent shake. The
    /// console alternated, so a stuck slot is held. Three ticks of it is 150 ms
    /// at the default rate; a ping-pong clip flips once a half-cycle and decays
    /// back long before it gets here.
    /// </summary>
    const int ThrashFlips = 3;

    /// <summary>How much of the clip clock this is allowed to drive.</summary>
    public enum Mode
    {
        /// <summary>
        /// **Carry the weight only, inside the segment the game itself chose.**
        /// The integer time handed to `func_8003486C` is left exactly as the
        /// game wrote it, so the segment index, the blender's keyframe cache and
        /// every decode it does are bit-for-bit what they are with smoothing
        /// off; the only thing that moves is the 12.12 blend weight, and it is
        /// clamped to its own segment. The pose is therefore always **between
        /// the segment's start and the pose the game asked for** -- it cannot
        /// reach anywhere the game would not have drawn this tick, whatever the
        /// clip time does. That bound is the point: it holds under every
        /// explanation of a spazzing pose, including the ones no counter here
        /// can rule out.
        ///
        /// What it gives up is motion *across* a segment boundary, which clamps
        /// to the boundary instead of continuing through it. Since a tick
        /// advances roughly one segment (mean step 511 against `u16` durations),
        /// most of the in-between motion is inside a segment and survives.
        /// </summary>
        Weight,

        /// <summary>
        /// Drive the integer time as well, so the in-between instant picks its
        /// own segment. Strictly more faithful when it works, and strictly less
        /// bounded: the pose can land on a segment the game did not ask for, and
        /// what the blender does with a segment index moving at the frame rate
        /// is not something this repo can check by counter. `KF2_SMOOTH_ANIM=time`.
        /// </summary>
        Time,
    }

    /// <summary>What a tick's clip-time step was, decided once in
    /// <see cref="Classify"/>.</summary>
    enum Verdict
    {
        /// <summary>Nothing moved.</summary>
        Still,
        /// <summary>Ordinary playback: interpolate it.</summary>
        Play,
        /// <summary>A cycle turned over: run the tick forwards through it.</summary>
        Wrap,
        /// <summary>A re-seek, a clip being fought over, or a turnover with no
        /// settled rate behind it. Leave the game's own time alone.</summary>
        Hold,
    }

    sealed class Slot
    {
        public int Clip = -1;
        public int PrevTime, CurTime;
        public bool HasCur, HasPrev;

        /// <summary>This tick's step and what <see cref="Classify"/> made of
        /// it. Decided once per tick, not once per frame, so the verdict cannot
        /// disagree with itself across the frames of one tick.</summary>
        public int Step;
        public Verdict Say;

        /// <summary>Consecutive ticks stepping the same way. A wrap is
        /// *synthesised* from <see cref="LastStep"/> rather than measured, so it
        /// is only believed off a settled run of playback.</summary>
        public int Run;

        /// <summary>Direction reversals that were not cycle turnovers, decayed
        /// one a tick by steady playback. A clip whose time is being fought over
        /// -- an attack the AI cannot resolve -- flips every tick and climbs;
        /// a ping-pong clip flips once a half-cycle and never does.</summary>
        public int Flips;

        /// <summary>The last step that was *playback* -- a wrap's step is a
        /// cycle length and is deliberately not recorded here. It is the
        /// estimate of how far the clip advances in a tick, which is what says
        /// where in the wrap tick the cycle turned over.</summary>
        public int LastStep;

        /// <summary>The highest clip time seen on this clip, which is the best
        /// estimate of where its last segment ends. Only a reverse-played loop
        /// needs it, to know where its cycle starts.</summary>
        public int MaxTime;
        public bool HasMax;

        /// <summary>The logic tick this slot last sampled on. One sample per
        /// tick, whichever frame of that tick the slot happens to be drawn on.</summary>
        public long Tick = -1;
    }

    static readonly Dictionary<uint, Slot> _slots = [];

    /// <summary>Bumped once per logic tick, at the frame bracket. A counter
    /// rather than a per-slot flag cleared on non-tick frames: the flag version
    /// never cleared at all when every frame ticked.</summary>
    static long _tick;

    /// <summary>This frame's <see cref="FramePacing.LogicPhase"/>, read once at
    /// the bracket. It is stable for the whole frame by construction.</summary>
    static double _phase;

    static int _depth;
    static bool _pending;
    static double _carry;
    static uint _weightPtr;
    static int _tFloor;

    /// <summary>How far *back* in clip time from the game's own current time
    /// this frame stands, in <see cref="Mode.Weight"/>. Signed, so a clip played
    /// in reverse needs no special case.</summary>
    static double _back;

    public const string OnKey = "kf2.smoothing.anim";

    /// <summary>Drive the MO clip clock between ticks. **Off by default.**</summary>
    public static bool Enabled { get; private set; }

    /// <summary>How much of the clock to drive. <see cref="Mode.Weight"/> is the
    /// default because it is the bounded one.</summary>
    public static Mode Carry { get; private set; } = Mode.Weight;

    static bool _onFromEnv;
    static bool _probe;

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _reportedAt;
    static long _submits, _morph, _rigid, _clipTicks, _carried, _skipped, _reversed, _live,
                _backward, _wraps, _wrapCarried, _stuck;
    static int _maxTimeSeen;
    static double _timeStepSum, _fracSum;
    static int _maxStep, _minStep = int.MaxValue;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.animsmoothing",
        Name = "Animation smoothing",
        Version = "2.0",
        Description = "Carries MO clip time between the game's logic ticks.",
    };

    public static void Configure(string? on, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on))
        {
            Enabled = on != "0";
            Carry = on is "time" or "full" ? Mode.Time : Mode.Weight;
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
        });

        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            _slots.Clear();
            _depth = 0;
            _pending = false;
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
    }

    public static void BeforeSubmit(CpuContext c, IMemory m)
    {
        _depth++;
        if (_depth != 1) return;

        _pending = false;
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
        if (slot.Clip != clip)
        {
            slot.Clip = clip;
            slot.HasCur = slot.HasPrev = slot.HasMax = false;
            slot.LastStep = slot.Step = slot.MaxTime = slot.Run = slot.Flips = 0;
            slot.Say = Verdict.Still;
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
            slot.Say = Verdict.Still;
            if (slot.HasPrev) Classify(slot);

            if (_probe && slot.HasPrev)
            {
                int d = Math.Abs(slot.Step);
                if (slot.Say == Verdict.Wrap) _wraps++;
                else if (slot.Say == Verdict.Hold)
                {
                    if (slot.Flips >= ThrashFlips) _stuck++; else _skipped++;
                }
                else if (slot.Say == Verdict.Play)
                {
                    _clipTicks++;
                    _timeStepSum += d;
                    if (d > _maxStep) _maxStep = d;
                    if (d < _minStep) _minStep = d;
                }
                if (slot.MaxTime > _maxTimeSeen) _maxTimeSeen = slot.MaxTime;
            }
        }

        if (!Enabled || !FramePacing.Gating || !slot.HasPrev) return;

        // A wrap's own step is a cycle length; what the tick actually advanced
        // by is the playback rate behind it.
        int advance = slot.Say == Verdict.Wrap ? slot.LastStep : slot.Step;

        if (Carry == Mode.Weight)
        {
            switch (slot.Say)
            {
                case Verdict.Wrap:
                    if (_probe) _wrapCarried++;
                    break;
                case Verdict.Play:
                    if (_probe && slot.Step < 0) _backward++;
                    break;
                default:
                    return;   // Still, or a hold: what the game asked for stands
            }

            // The frame stands `(1 - phase)` of a tick behind the pose the game
            // asked for. Say that in clip time and let AfterClock spend it on
            // the weight, inside the game's own segment -- the integer time is
            // not touched at all, so the segment index and the blender's cache
            // are exactly what they would be with this patch absent.
            _back = (1.0 - _phase) * advance;
            _pending = true;
            return;
        }

        double t;
        switch (slot.Say)
        {
            case Verdict.Wrap:
                // The clip looped. Lerping prev -> cur across it plays the whole
                // cycle *backwards* over one tick, which is the rewind this used
                // to show; run it forwards through the turnover instead.
                if (_probe) _wrapCarried++;
                t = WrapTime(slot, _phase);
                break;

            case Verdict.Play:
                if (_probe && slot.Step < 0) _backward++;
                t = slot.PrevTime + slot.Step * _phase;
                break;

            default:
                return;   // Still, or a hold: whatever the game asked for stands
        }

        _tFloor = (int)Math.Floor(t);
        _carry = t - _tFloor;
        _pending = true;
    }

    public static void AfterSubmit(CpuContext c, IMemory m)
    {
        if (_depth == 0) return;
        _depth--;
        if (_depth != 0) return;
        _pending = false;
        if (_probe) Report();
    }

    /// <summary>
    /// <c>func_8003486C</c>: A2 is the integer clip time. Hand it floor(lerp) so
    /// the segment pick matches the in-between instant; <see cref="AfterClock"/>
    /// adds the fraction onto the 12.12 weight.
    /// </summary>
    public static void BeforeClock(CpuContext c, IMemory m)
    {
        if (!_pending) return;

        // func_8003486C has no prologue, so SP is still the blender's and
        // SP+0x10 is the weight slot it stored for the clock to fill in.
        _weightPtr = m.ReadU32(c.SP + 0x10u);

        // In Weight mode the time is deliberately left alone: the segment the
        // game picked is the segment that gets drawn.
        if (Carry == Mode.Time) c.A2 = (uint)_tFloor;
    }

    public static void AfterClock(CpuContext c, IMemory m)
    {
        if (!_pending) return;
        _pending = false;
        double spend = Carry == Mode.Weight ? _back : _carry;
        if (_weightPtr == 0 || spend == 0.0) return;
        if (Carry == Mode.Time && spend < 0.0) return;

        // v0 is the segment record: +0x0 the direction flag, +0x2 the duration
        // the weight was divided by.
        uint segment = c.V0;
        if (segment == 0) return;
        int duration = m.ReadU16(segment + 2u);
        if (duration <= 0) return;

        // Weight mode spends a *backward* offset from the game's own pose, so
        // the sign flips; the clamp below is what keeps it inside this segment.
        int add = (int)Math.Round(spend * 4096.0 / duration);
        if (Carry == Mode.Weight) add = -add;
        if (add == 0) return;

        // A flagged segment is published as 0x1000 - raw, so its weight runs down
        // as the clip runs forward and the carry has to go the other way.
        bool reversed = m.ReadU16(segment) != 0;
        if (reversed) add = -add;

        int weight = (int)m.ReadU32(_weightPtr);
        int next = Math.Clamp(weight + add, 0, 0x1000);
        m.WriteU32(_weightPtr, (uint)next);

        if (_probe)
        {
            _carried++;
            if (reversed) _reversed++;
            _fracSum += Math.Abs(spend);
        }
    }

    /// <summary>
    /// Decide what this tick's step *was*: playback, a cycle wrap, a re-seek, or
    /// a clip nothing can currently agree on.
    ///
    /// A wrap and a re-seek both run the time the wrong way; what tells them
    /// apart is **where the time landed**. A loop turning over lands within one
    /// tick's advance of the cycle's first frame -- it cannot land further,
    /// since the overshoot past the end is what the new time is made of -- while
    /// a ping-pong clip easing back through its own last frames lands where it
    /// already was, and a re-seek lands anywhere. Magnitude alone cannot do it:
    /// a short cycle wraps by less than a long clip's ordinary step. It does
    /// have to move *further than one tick's advance*, though, or a clip
    /// jittering near its own head reads as wrapping every other tick.
    ///
    /// The remaining case is a clip being **fought over** rather than played --
    /// an attack whose animation the AI restarts every tick because the
    /// conditions to finish it are never met. Its step reverses every tick
    /// without ever landing at a cycle boundary, and carrying it draws the fight
    /// at the frame rate. <see cref="ThrashFlips"/> such reversals, net of
    /// steady playback, and the slot is held at the game's own time until it
    /// resolves.
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
                s.Say = s.Run >= WrapRun ? Verdict.Wrap : Verdict.Hold;
                s.Run = 0;
                // A wrap's step is a cycle length, not a rate: recording it
                // would make the *next* wrap unrecognisable.
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

        if (Math.Abs(step) > MaxTimeStep)
        {
            // A re-seek: a restart, or a jump to somewhere else in the same
            // clip. Not a rate either, so LastStep stands.
            s.Say = Verdict.Hold;
            s.Run = 0;
            return;
        }

        // The sign is a direction, not a discontinuity. A clip played in reverse
        // runs its time down by the same small amount a forward one runs it up
        // -- the drawbridge lever going back up against the same lever coming
        // down -- so bailing on a negative step left exactly those animations
        // stepping at the tick rate.
        s.LastStep = step;
        s.Say = s.Flips >= ThrashFlips ? Verdict.Hold : Verdict.Play;
    }

    /// <summary>
    /// The in-between time on the tick a cycle turned over, without knowing how
    /// long the cycle is.
    ///
    /// The clip advances about <c>LastStep</c> per tick, and the part of that
    /// advance already spent in the *new* cycle is the new time itself (measured
    /// from the cycle's first frame). So the turnover happened at
    /// <c>1 - CurTime/LastStep</c> of the way through the tick: before that the
    /// old cycle is still finishing, after it the new one is running. Nothing
    /// here needs the clip's total duration -- which is as well, since the only
    /// place it exists is the segment table <c>func_8003486C</c> walks.
    ///
    /// Overshooting the real end by up to a frame is harmless: past its last
    /// segment the clock returns that segment at a full <c>0x1000</c> weight,
    /// which is the pose the clip ends on anyway.
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

        // A negative time would land in the first segment with a negative
        // weight, which the clock writes out as a huge unsigned one.
        return t < 0.0 ? 0.0 : t;
    }

    static Slot Get(uint id)
    {
        if (!_slots.TryGetValue(id, out var s))
            _slots[id] = s = new Slot();
        return s;
    }

    static void Report()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _reportedAt < 1000.0) return;
        _reportedAt = now;

        string morphPct = _submits == 0 ? "n/a" : $"{100.0 * _morph / _submits:0.0}%";
        string step = _clipTicks == 0 ? "" :
            $", time step {_minStep}..{_maxStep} mean {_timeStepSum / _clipTicks:0.0} over {_clipTicks}";
        string live = _morph == 0 ? "" : $", {_live} with a running clip";
        string back = _backward == 0 ? "" : $", {_backward} playing backwards";
        string wrap = _wraps == 0 ? "" :
            $", {_wraps} cycle wrap(s) (longest clip seen {_maxTimeSeen})" +
            (Enabled ? $", {_wrapCarried} carried through" : "");
        string stuck = _stuck == 0 ? "" : $", {_stuck} stuck";
        string carry = !Enabled ? ""
            : _carried == 0 ? ", 0 weights carried"
            : $", {_carried} weight(s) carried ({_reversed} reversed), " +
              $"mean frac {_fracSum / _carried:0.00}";
        Console.WriteLine($"[KF2] anim: {_submits} submit(s), " +
                          $"morph {_morph} ({morphPct}), rigid {_rigid}{live}" +
                          $"{step}{back}{wrap}; {_skipped} re-seek(s){stuck}" +
                          carry);

        _submits = _morph = _rigid = _clipTicks = _carried = _skipped = _reversed = _live = 0;
        _backward = _wraps = _wrapCarried = _stuck = 0;
        _timeStepSum = _fracSum = 0;
        _maxStep = 0; _minStep = int.MaxValue;
    }
}
