using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// What frame rate the port runs at, and how the game's own clock is held to
/// <see cref="LogicHz"/> while it does.
///
///     KF2_FPS=20      (default) draw at 20, the rate the console achieved
///     KF2_FPS=60      render at 60, tick the world at 20
///     KF2_FPS=144     any number is allowed; the world still ticks at 20
///     KF2_FPS=off     no pacing at all -- the picture is uncapped, the world is not
///     KF2_TICKRATE=30 tick the world at what the game's code asks for instead
///     KF2_FPS_LOGIC=full   do not gate anything; scale the movement deltas instead
///     KF2_FPS_GATE=80037C0C+8002A550+80040348+80046A60+8004910C+80033FBC+8002DC78
///                     the stages to tick at LogicHz -- naming any replaces the set
///
/// It is also a setting, under Video -- see Kf2.Settings.FramePacingPage. The
/// saved choice is read on RuntimeReadyEvent rather than in Configure, since
/// ConfigManager only loads inside HostWindow.Initialize, after Program.cs; the
/// environment variable still wins over it.
///
/// King's Field's speed *is* its frame rate -- everything advances a fixed amount
/// per loop iteration -- so a port that draws more often has to decide what to do
/// about that, and this is where that decision lives.
///
/// ## What used to hold the port at 30, and why none of it does now
///
/// Three separate gates, and they are worth keeping straight because only the
/// first is the game's:
///
/// 1. **The game's own frame gate, `func_80017880`.** Stage 13 (the renderer)
///    ends by calling it. It spins on a vblank credit at 0x801B6CA8 -- bumped by
///    the game's own vblank callback, `func_80017850` -- until the credit reaches
///    **2**, then zeroes it. That literal 2 is the game's frame rate, in software.
///    Since patches/recompone/0021 ticks the emulated vblank on a wall-clock 60 Hz
///    grid, this alone paces the port to exactly 30 fps whatever the host does.
///    **<see cref="BeforeFrameGate"/> now skips it at every rate, not only above
///    30**, because the rate the world is held to is no longer the rate that
///    literal asks for -- see below.
/// 2. **RecompOne's FrameClock**, which used to be a hard-coded 60 Hz applied per
///    *VSync call*. patches/recompone/0025 makes it settable; this class sets it
///    as a permissive ceiling rather than as the pacer, because a frame can carry
///    more than one VSync call and a per-call throttle therefore cannot express a
///    frame rate.
/// 3. **<see cref="Floor"/>**, below -- the port's own deadline at the frame
///    boundary, which is the only one of the three that knows where a frame ends,
///    and now the only thing pacing the picture at any rate.
///
/// With the game's gate gone a rendered frame carries **exactly one VSync call**
/// at every rate -- the presenter's, `func_8002E0FC`, which does VSync(0)
/// immediately before its DrawOTag. The gate's spin calls were the second one.
/// That makes the frame boundary in <see cref="AfterDrawOTag"/> the same shape at
/// 20 fps as at 144.
///
/// ## The logic clock
///
/// The game's gate is removed and the loop runs at whatever the floor allows -- so
/// the world would advance a fixed amount that many times a second.
/// The fix is a fixed timestep: a wall-clock accumulator ticks at
/// <see cref="LogicHz"/>, and the main-loop stages that hold per-tick state run
/// only on a frame where it ticked. **The accumulator is the only thing holding
/// the world to a rate now**, which is why it runs in every configuration,
/// including uncapped and including a render rate below the tick rate. Everything the game counts in frames -- the
/// death sequence, spell lifetimes, buff timers, the poison tick, entity AI --
/// then keeps hardware timing exactly, at any rate.
///
/// What is gated is everything found to hold per-tick state that cannot draw:
///
///     2  func_80037C0C   the object-table state machine: 396 slots of 0x44 at
///                        0x80177714, dispatched on the type byte at rec+0x4
///                        through a 224-entry jump table at 0x8001191C. Every
///                        world prop that moves is in here -- doors, the
///                        drawbridge, the minecart, the crystals -- and it writes
///                        their position VECTOR itself, so ungated they moved at
///                        the render rate
///     3  func_8002A550   pad read, turn, walk, angle fold, the death counter at
///                        0x8019951A, the poison tick, the buff timers at
///                        0x80199472..0x80199482, and 0x80199488, the global frame
///                        counter it bumps last
///     4  func_80040348   the 200-record entity table at 0x8016C544. Its AI runs
///                        one entity in four off 0x80175908 &amp; 3, which stays
///                        right for free once the stage itself is gated
///     5  func_80046A60   128 effect/projectile slots at 0x8019CC6C, each with a
///                        lifetime at rec+0x0E decremented once per call. Ungated,
///                        every spell and effect expires at the render rate
///     6  func_8004910C   the area module's per-frame entry, slot 1 of the module
///                        header, reached as *(u32*)(*(u32*)0x8017E068 + 4). Six
///                        of the nine modules leave it an empty `jr $ra`; fdat11,
///                        fdat14 and fdat20 run proximity and trigger logic there
///     13 func_80033FBC   the fade state machine stage 13 calls -- state byte
///                        0x80192D42, brightness 0x80192D44 stepping +0x14 in and
///                        -0x14 out, hold counter 0x80192D43. Its own subtree is
///                        three functions and none can draw, so the fade is gated
///                        even though its caller is the renderer
///     13 func_8002DC78   the animated-texture updater stage 13 calls -- eight
///                        slots at 0x80192D58 (stride 0x18), each a scroll phase at
///                        rec+0x4 re-uploaded to VRAM through func_80060624. The
///                        water, the fire and the creatures' skins. Its subtree is
///                        the VRAM upload alone, so like the fade it is gated though
///                        its caller draws; ungated it scrolled at the render rate
///
/// **Can it draw** is the test each of those had to pass, because a stage that
/// submits primitives cannot be skipped -- at 120 fps three frames in four would
/// have nothing from it. That is checked against the emitted C#: the subtree of a
/// gated function must contain no `DrawOTag`, `VSync`, `PutDispEnv` or
/// `PutDrawEnv`. **Stages 2 and 3 are the two exceptions and they are the same
/// shape**: what they reach is **stage 13 itself**, not a drawing primitive of
/// their own. Stage 3 gets there through `func_80029CBC -> func_80018E80` (the
/// in-game menu) and `func_80037B5C` (the transition fade), both of which take
/// the main loop over and render their own frames, and through `func_80022DC4`
/// (the menu blip) which VSyncs without drawing. Stage 2's case is strictly
/// narrower: enumerating every function in its subtree that calls a submitting or
/// presenting entry point directly leaves exactly one, reached by exactly one
/// edge -- `func_80037C0C -> func_80037B5C -> func_800342D8 -> func_8002E0FC` --
/// plus the area modules' own message-box and cutscene loops (`func_80047000`,
/// `func_80048208`, `func_8004831C`) off its indirect arm, which reach stage 13
/// the same way. Those are *extra* renders inside the stage rather than the
/// frame's own: the main loop still runs stage 13 afterwards, so skipping either
/// stage costs a redundant draw and not a frame's picture. Skipping decides
/// whether such a loop is entered; it cannot cut one in half, since once entered
/// the stage is on the stack and drives its own frames. The cost is that entering
/// a fade or a cutscene can be deferred by up to one tick. (`scripts/check_gate.py`
/// re-derives this; both are recorded exceptions there, and the single-path
/// version of the stage 3 sentence was wrong.)
///
/// **What this still does not cover, stated rather than discovered later.**
/// Stage 13's **jitter accumulator at 0x8006E608** is in stage 13's own body
/// rather than in a callee, so no hook can reach it -- it is a damped accumulator
/// (decayed by an eighth a call) driving the screen shake, so above the tick rate
/// it settles faster and smaller. **The modal loops are covered, and not by this
/// class**: `func_80037B5C` (the transition fade), the item-use and spell-cast
/// animations and the menu all step once per *rendered* frame inside a loop of
/// their own, which a gate cannot reach because it decides only whether such a
/// loop is entered. <see cref="LoopPacing"/> paces the frames those loops produce
/// instead, so an iteration of one costs a tick again. And **the object
/// record's rec+0x40 on two slots survived gating stage 2 at ratio 3.69**: that
/// one is stepped by stage 13's own object pass `func_800331B4` (proved by gating
/// it as a probe -- 53.4/s fell to 17.6/s), where it is an ambient-sound retrigger
/// deadline in vblank units against 0x801B6CAC. `func_800331B4` steps the timer
/// and draws the models in one loop, so no whole-function hook separates them.
///
/// The camera would then move <see cref="LogicHz"/> times a second while the
/// picture updated more often, which looks worse than not drawing faster at all. <see cref="FrameSmoothing"/> is the
/// other half of this and carries the view between ticks.
///
/// This lives in `patches/` and not in `mods/` on purpose. It is a correctness
/// fix that has to be on: shipping it as a runtime-loaded package would let it be
/// absent, disabled or fail to compile, and the failure would look like the game
/// simply running too fast. The genuinely optional extras are real mods -- see
/// mods/kf2debug.
/// </summary>
public static class FramePacing
{
    /// <summary>
    /// Ticks a second the game's own world is held to, whatever the port draws at.
    ///
    /// **20, not 30, and that is a judgement rather than a reading.** The game's
    /// own frame gate asks for two vblanks -- 30 fps -- and for a long time this
    /// was 30 for that reason. But the literal is what the code *asks* for, not
    /// what the console *achieved*: King's Field is heavy enough that the loop
    /// lands in the three-vblank band under load, and since the game's speed is
    /// its frame rate, that band is the speed the game was played at. This port
    /// has an HLE GPU and native MIPS, so it makes the two-vblank deadline every
    /// single frame and never bands down -- which is why it needs to be told.
    ///
    /// No counter here can settle it, so it is a setting: 30 is the other answer
    /// and is one combo entry away. See "Frame pacing" in
    /// docs/PATCHES_AND_MODS.md.
    /// </summary>
    public static double LogicHz { get; private set; } = 20.0;

    const double SpinMs = 1.5;   // spin the last stretch; Thread.Sleep granularity is a few ms

    // libgpu DrawOTag, per overlay. The frame boundary: past it the frame's
    // drawing is done and the loop is about to VSync and show it.
    static readonly (string Overlay, uint Addr)[] DrawOTag =
    [
        ("open", 0x80016078), ("game", 0x80060818), ("end", 0x80013D80),
    ];

    // libetc VSync, per overlay -- the same three addresses config/kf2.json binds
    // to LibEtc.VSync. A `replace` in the config and a `pre` here coexist:
    // HookManager runs every pre before the replacement, which is what
    // mods/framestats already relies on. Counting *calls* here is what makes the
    // frame boundary below rate-independent; VSyncEvent is a vblank, not a call.
    static readonly (string Overlay, uint Addr)[] VSyncThunk =
    [
        ("open", 0x8001EB88), ("game", 0x8005FCC8), ("end", 0x8001B154),
    ];

    /// <summary>
    /// The game's own frame gate: spin until the vblank callback has counted two,
    /// then zero the count. Called from stage 13. Skipping it is what lets the
    /// loop run faster than 30.
    /// </summary>
    const uint FrameGate = 0x80017880;

    /// <summary>The u32 the game's vblank callback (`func_80017850`) bumps and the
    /// frame gate consumes. Zeroed here when the gate is skipped, so that going
    /// back to 30 mid-session finds it where the game left it.</summary>
    const uint VBlankCredit = 0x801B6CA8;

    /// <summary>Frames a second the port aims for. 0 is uncapped -- no pacing at
    /// all, which draws as fast as the host can. It no longer runs the *game* fast:
    /// the logic clock holds the world to <see cref="LogicHz"/> in every
    /// configuration, uncapped included.</summary>
    public static double TargetFps { get; private set; } = LogicHz;

    /// <summary>False only when uncapped.</summary>
    public static bool Enabled => TargetFps > 0.0;

    /// <summary>Where the chosen rate is kept between runs.</summary>
    public const string FpsKey = "kf2.framepacing.fps";

    /// <summary>The key this used to be kept under, as a vblank divisor. Read once
    /// and converted, so an existing config keeps the rate it had.</summary>
    public const string VBlankKey = "kf2.framepacing.vblanks";

    /// <summary>Where the chosen tick rate is kept between runs.</summary>
    public const string LogicHzKey = "kf2.framepacing.logichz";

    /// <summary>What to do about the game advancing once per loop iteration.</summary>
    public enum Logic
    {
        /// <summary>Tick the world at <see cref="LogicHz"/> however often the port
        /// draws. Every counter in the game keeps a consistent rate, and the game's
        /// own frame gate is never allowed to set one.</summary>
        Fixed,

        /// <summary>Run every stage every frame and scale the movement deltas
        /// instead -- true input and collision at the render rate, but every
        /// per-tick *counter* in the game then runs at that rate too. A comparison
        /// mode; see <see cref="FullRateLogic"/>.</summary>
        Full,
    }

    public static Logic LogicMode { get; private set; } = Logic.Fixed;

    /// <summary>Main-loop stages run at <see cref="LogicHz"/> rather than at the
    /// render rate.</summary>
    static readonly List<uint> _gated = [];

    /// <summary>
    /// What ticks at <see cref="LogicHz"/> when <c>KF2_FPS_GATE</c> names nothing:
    /// main-loop stages 2, 3, 4, 5 and 6, plus stage 13's fade state machine and
    /// its animated-texture updater. Hooking
    /// them unconditionally is what makes the rate a setting rather than a launch
    /// argument. It costs nothing when the render rate equals the tick rate, where
    /// the accumulator ticks on every frame and <see cref="BeforeStage"/> always
    /// runs the original.
    ///
    /// Every one of them was checked for whether it can *draw* before being put
    /// here, because a stage that submits primitives cannot be skipped -- the
    /// picture would flicker at three frames in four. Stages 2 and 3 are the two
    /// recorded exceptions, and they are the same shape: what they reach is stage
    /// 13 itself, called as an extra render from inside a modal loop of their own,
    /// never a primitive the frame's picture depends on. `scripts/check_gate.py`
    /// re-derives that and holds both reasons. See "Any frame rate" in
    /// docs/PATCHES_AND_MODS.md.
    /// </summary>
    static readonly uint[] DefaultGate =
    [
        0x80037C0C,   // 2  the object-table state machine. It walks the 396 slots
                      //    of 0x44 at 0x80177714, skipping a slot whose type byte
                      //    at rec+0x4 is 0xFF, publishes the record to 0x8017E04C
                      //    and its 0x18-stride definition (indexed by the u16 at
                      //    rec+0x6) to 0x8017E048, then dispatches on that type
                      //    byte through a 224-entry jump table at 0x8001191C --
                      //    thirty distinct arms, plus an indirect arm into the
                      //    area module's own handler. This is every world prop
                      //    that moves: doors, the drawbridge, the minecart, the
                      //    crystals. It writes the position VECTOR directly
                      //    (rec+0x14/+0x18/+0x1C), the state word at rec+0x08 (43
                      //    sites, the busiest field in the function) and the
                      //    timers at rec+0x24 and rec+0x40. Ungated, all of it ran
                      //    once per rendered frame -- the census measured rec+0x18,
                      //    rec+0x24 and rec+0x40 at ratio 3.8-4.05, 13/s against
                      //    50/s, standing still in area 1 at 144 fps.
                      //
                      //    It reaches DrawOTag, and is gated anyway: the only edge
                      //    out is func_80037B5C, the transition fade, which renders
                      //    its own frames by calling stage 13. The main loop still
                      //    runs stage 13 afterwards, so this costs the *entry* to a
                      //    fade or a cutscene up to one tick of delay, never a
                      //    frame's picture. Recorded in check_gate.py's KNOWN.
        0x8002A550,   // 3  pad read, turn, walk, the death counter at 0x8019951A,
                      //    the poison tick, the buff timers, the frame counter
        0x80040348,   // 4  the 200-record entity table at 0x8016C544
        0x80046A60,   // 5  128 effect/projectile lifetimes at 0x8019CC6C
        0x8004910C,   // 6  the area module's own per-frame entry, dispatched
                      //    through *(u32*)(*(u32*)0x8017E068 + 4) -- slot 1 of the
                      //    module header. Six of the nine modules leave it an
                      //    empty `jr $ra`; fdat11, fdat14 and fdat20 use it for
                      //    proximity and trigger logic that writes state bytes.
                      //    No SDK entry point in any of the nine subtrees.
        0x80033FBC,   // 13 the fade state machine, called from stage 13 -- state
                      //    byte 0x80192D42, brightness 0x80192D44 stepping +0x14
                      //    in and -0x14 out, hold counter 0x80192D43 counting
                      //    down. Three functions in its subtree, none of which
                      //    can draw, so it is safe to skip even though its caller
                      //    is the renderer. Ungated, an area fade is four times
                      //    quicker at 120 fps than on hardware.
        0x8002DC78,   // 13 the animated-texture updater, also called from stage 13
                      //    (at 0x800346..). It walks 8 slots at 0x80192D58 (stride
                      //    0x18): each advances a scroll phase at rec+0x4 by the
                      //    per-slot rate at rec+0x3, wrapping at rec+0xC, then
                      //    re-uploads the scrolled texture region to VRAM through
                      //    func_80060624 (a LoadImage-style GPU transfer). This is
                      //    the animated water, the main-hall fire and the creatures'
                      //    scrolling skins. Ungated it advanced once per rendered
                      //    frame, so the scroll ran at the render rate -- measured
                      //    rec+0x4 changing on 100% of frames at 120 fps, i.e. six
                      //    times too fast against the 20 Hz world. Its subtree is
                      //    the VRAM upload alone -- no DrawOTag/VSync/PutDispEnv/
                      //    PutDrawEnv -- so skipping it on a non-tick frame is safe:
                      //    the phase holds and VRAM keeps the last tick's frame, so
                      //    the texture animates at the tick rate.
    ];

    /// <summary>True once KF2_FPS has spoken, so the saved rate does not overrule it.</summary>
    static bool _fromEnv;

    /// <summary>The same, for KF2_TICKRATE and the saved tick rate.</summary>
    static bool _logicFromEnv;

    static readonly Stopwatch _clock = Stopwatch.StartNew();

    // When the current frame is allowed to end. Absolute rather than now+min, so a
    // frame that overruns is paid for out of the next one and the rate averages to
    // exactly the target instead of drifting down by the accumulated jitter.
    static double _due;
    static long _frames;

    /// <summary>VSync calls since the last frame boundary. Reset there, and the
    /// only thing that decides where a frame ends -- see <see cref="AfterDrawOTag"/>.</summary>
    static int _vsyncCalls;

    /// <summary>Frames a second over the last window, so the settings can show whether
    /// the chosen rate is the one being achieved. Zero until the first window ends.</summary>
    public static double Measured { get; private set; }

    static double _windowStart;
    static long _windowFrames;

    // ---- the logic clock ------------------------------------------------------

    // Unspent logic ticks, in ticks. Advanced by wall-clock time at the frame
    // boundary rather than by 1/TargetFps per frame, so a host that misses the
    // target still runs the world at LogicHz instead of at whatever it achieved.
    static double _logicCredit;
    static double _logicClockMs = -1.0;

    /// <summary>Whether the gated stages run on the frame now being built. Decided
    /// at the previous frame's boundary, because that is the last moment before
    /// the stages run.</summary>
    static bool _tickThisFrame = true;

    /// <summary>
    /// How far the frame being drawn is past the last logic tick, in ticks, in
    /// [0,1). This is what <see cref="FrameSmoothing"/> extrapolates the view by;
    /// it is continuous across a tick boundary, so the camera does not jump on the
    /// frames where the world did advance.
    /// </summary>
    public static double LogicPhase => Gating ? Math.Clamp(_logicCredit, 0.0, 1.0) : 0.0;

    /// <summary>
    /// Whether the gated stages ran on the frame now being drawn -- that is,
    /// whether the world advanced since the last one. Decided at the previous
    /// frame's boundary like the phase, so it is stable for the whole frame and
    /// can be read from anywhere in it.
    ///
    /// <see cref="ObjectSmoothing"/> is what wants it: to carry a moving object
    /// between ticks you need last tick's position as well as this one's, and the
    /// only moment worth re-sampling is a frame the world actually moved on.
    /// </summary>
    public static bool TickedThisFrame => !Gating || _tickThisFrame;

    /// <summary>
    /// True when the world is on the logic clock rather than on the loop.
    ///
    /// **This used to require a rate above 30**, because below that the game's own
    /// frame gate did the job. The gate is now skipped at every rate, so nothing
    /// else is left to hold the world down and the accumulator has to run in every
    /// configuration -- including at the tick rate, where it simply ticks on every
    /// frame, and uncapped, where it is the only thing keeping the game playable.
    /// </summary>
    public static bool Gating => LogicMode == Logic.Fixed;

    /// <summary>
    /// True when a frame can land part-way between two logic ticks, which is the
    /// only condition under which <see cref="FrameSmoothing"/> has anything to
    /// carry. <see cref="Gating"/> is no longer that test -- it is true at the tick
    /// rate too, where <see cref="LogicPhase"/> is always ~0.
    /// </summary>
    public static bool Extrapolating => Gating && (!Enabled || TargetFps > LogicHz + 0.001);

    /// <summary>
    /// True when the game's own two-vblank wait is being skipped, which is always.
    /// It is kept as a name rather than inlined because it is the single decision
    /// the whole rate scheme rests on: the game's gate asks for 30 fps and 30 ticks
    /// together, and the port wants those to be two different numbers.
    /// </summary>
    static bool SkipFrameGate => true;

    // HookManager attributes hooks to a mod so they can be removed again. This is
    // in-project rather than a loaded package, so it declares its own identity.
    static readonly ModInfo _self = new()
    {
        Id = "kf2.framepacing",
        Name = "Frame pacing",
        Version = "2.0",
        Description = "Draws at the chosen rate and ticks the world at its own.",
    };

    public static void Configure(string? fps, string? gate, string? logic = null,
                                 string? tickRate = null)
    {
        if (!string.IsNullOrWhiteSpace(tickRate))
        {
            if (!double.TryParse(tickRate, NumberStyles.Float, CultureInfo.InvariantCulture,
                                 out double hz))
                throw new ArgumentException($"KF2_TICKRATE: cannot read '{tickRate}'");

            LogicHz = ClampLogic(hz);
            _logicFromEnv = true;
        }

        if (!string.IsNullOrWhiteSpace(fps))
        {
            if (string.Equals(fps, "off", StringComparison.OrdinalIgnoreCase)) TargetFps = 0.0;
            else if (double.TryParse(fps, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
                TargetFps = Clamp(rate);
            else throw new ArgumentException($"KF2_FPS: cannot read '{fps}'");

            _fromEnv = true;
        }

        if (!string.IsNullOrWhiteSpace(gate))
            foreach (var a in gate.Split('+', StringSplitOptions.RemoveEmptyEntries))
                _gated.Add(Convert.ToUInt32(a.Trim(), 16));

        if (!string.IsNullOrWhiteSpace(logic))
            LogicMode = string.Equals(logic, "full", StringComparison.OrdinalIgnoreCase)
                ? Logic.Full : Logic.Fixed;
    }

    /// <summary>
    /// Attach the hooks. Deferred to the first overlay load because
    /// <see cref="SymbolRegistry"/> reads the dispatcher's overlay tables, and
    /// those are registered inside Entry.Run -- after Program.cs has run, but
    /// before anything is loaded, so the first load event is the earliest moment
    /// every overlay is resolvable.
    /// </summary>
    public static void Install()
    {
        // The saved rate can only be read once ConfigManager has loaded, which
        // happens inside HostWindow.Initialize -- after Program.cs called Configure.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            // Order matters: the tick rate is read first, because SetTargetFps
            // resets the accumulator and the default render rate is the tick rate.
            if (!_logicFromEnv) SetLogicHz(SavedLogicHz(), save: false);
            if (!_fromEnv) SetTargetFps(SavedRate());
            else ApplyHostCeiling();
        });

        // Attached whether or not the rate is the default: a rate is a choice that
        // can be taken back, and hooks cannot be added once the game is running
        // past the overlay loads.
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    /// <summary>
    /// The rate a previous run left behind. Migrates the vblank divisor this used
    /// to be saved as, once, so an existing config keeps the rate it had: n
    /// vblanks was 60/n fps, and 0 was uncapped.
    /// </summary>
    /// <summary>The tick rate a previous run left behind. No migration: the key is
    /// new, and an older config simply has not chosen.</summary>
    static double SavedLogicHz()
    {
        var view = RecompOne.Runtime.Runtime.View;
        return view.Values.ContainsKey(LogicHzKey)
            ? ClampLogic(view.GetFloat(LogicHzKey, (float)LogicHz))
            : LogicHz;
    }

    static double SavedRate()
    {
        var view = RecompOne.Runtime.Runtime.View;
        if (view.Values.ContainsKey(FpsKey)) return view.GetFloat(FpsKey, (float)TargetFps);

        if (!view.Values.ContainsKey(VBlankKey)) return TargetFps;

        int vblanks = view.GetInt(VBlankKey, 2);
        double rate = vblanks <= 0 ? 0.0 : Clamp(60.0 / Math.Clamp(vblanks, 1, 4));
        view.SetFloat(FpsKey, (float)rate);
        return rate;
    }

    /// <summary>
    /// Change the rate at run time. 0 is uncapped, otherwise frames a second.
    /// Safe at any moment -- every reader of this state is per frame.
    /// </summary>
    public static void SetTargetFps(double fps)
    {
        TargetFps = fps > 0.0 ? Clamp(fps) : 0.0;
        _due = _clock.Elapsed.TotalMilliseconds;
        _logicClockMs = -1.0;
        _logicCredit = 0.0;
        _tickThisFrame = true;
        ApplyHostCeiling();
    }

    /// <summary>
    /// Change the tick rate at run time. Safe at any moment -- every reader of this
    /// state is per frame -- and it resets the accumulator rather than rescaling
    /// the credit, so the change lands on the next tick instead of part-way
    /// through one.
    /// </summary>
    public static void SetLogicHz(double hz, bool save = true)
    {
        LogicHz = ClampLogic(hz);
        _logicClockMs = -1.0;
        _logicCredit = 0.0;
        _tickThisFrame = true;

        if (save) RecompOne.Runtime.Runtime.View.SetFloat(LogicHzKey, (float)LogicHz);
    }

    /// <summary>The game's own achievable rates are 60/n, so 60, 30, 20, 15, 12.
    /// The range is that band set with room either side; a tick rate above the
    /// vblank grid is not a thing the game was ever asked to do.</summary>
    static double ClampLogic(double hz) => Math.Clamp(hz, 5.0, 60.0);

    /// <summary>Rates below this are the game's own slow bands and are allowed;
    /// above it is a host frame rate. The ceiling is arbitrary and only exists so
    /// that a typed-in number cannot ask for a zero-length frame.</summary>
    static double Clamp(double fps) => Math.Clamp(fps, 5.0, 1000.0);

    /// <summary>
    /// Hand RecompOne's own throttle a permissive ceiling rather than the target.
    /// It paces per <c>VSync</c> *call* and a frame can carry more than one, so it
    /// cannot express a frame rate; its job here is only to stop a loop that has
    /// stopped drawing -- a disc read, a menu -- from spinning. Never lowered below
    /// 60, which is what it was before patches/recompone/0025 made it settable.
    ///
    /// **Uncapped leaves it at 60 rather than turning it off.** "Uncapped" has
    /// always meant "the port's own floor is off", and what that produced was a
    /// port topping out around 50 fps because FrameClock held each VSync call to a
    /// vblank. Setting 0 here would mean something new -- present as fast as the
    /// host can, with nothing between the loop and the GPU -- which is not the
    /// diagnostic anyone asked for. The capability exists in 0025; nothing uses it.
    /// </summary>
    static void ApplyHostCeiling()
        => RecompOne.Runtime.Runtime.TargetFps = Enabled ? Math.Max(60.0, TargetFps * 2.0) : 60.0;

    /// <summary>The vblank floor this used to be expressed as, for the settings and
    /// for anything that still thinks in bands. 0 is uncapped.</summary>
    public static int Cap => Enabled ? Math.Max(1, (int)Math.Round(60.0 / TargetFps)) : 0;

    /// <summary>The form the old settings page took. Kept so a caller that thinks
    /// in vblanks still works: 1 is 60 fps, 2 is 30, 4 is 15, 0 is uncapped.</summary>
    public static void SetCap(int vblanks)
        => SetTargetFps(vblanks <= 0 ? 0.0 : 60.0 / Math.Clamp(vblanks, 1, 4));

    static void Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(FramePacing);
        var frameDrawn = self.GetMethod(nameof(AfterDrawOTag), BindingFlags.Public | BindingFlags.Static)!;
        var stageGate = self.GetMethod(nameof(BeforeStage), BindingFlags.Public | BindingFlags.Static)!;
        var frameGate = self.GetMethod(nameof(BeforeFrameGate), BindingFlags.Public | BindingFlags.Static)!;
        var vsyncCall = self.GetMethod(nameof(BeforeVSync), BindingFlags.Public | BindingFlags.Static)!;

        int n = 0;
        foreach (var (overlay, addr) in DrawOTag)
        {
            var target = SymbolRegistry.Resolve(overlay, null, addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] pacing: no function at {overlay}/0x{addr:X8}");
                continue;
            }
            if (HookManager.AddPost(_self, target, frameDrawn)) n++;
        }

        // Without this the frame boundary never fires and nothing here paces
        // anything, so say so rather than run silently at whatever the host does.
        int vsyncHooked = 0;
        foreach (var (overlay, addr) in VSyncThunk)
        {
            var target = SymbolRegistry.Resolve(overlay, null, addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] pacing: no VSync at {overlay}/0x{addr:X8}");
                continue;
            }
            if (HookManager.AddPre(_self, target, vsyncCall)) { n++; vsyncHooked++; }
        }

        // The game's own frame gate, skipped at every rate. Only GAME.EXE's copy is
        // hooked, so the title and the ending pace themselves as they always did --
        // they are CD-bound at 7-15 fps and have no world to tick.
        bool gateHooked = false;
        {
            var target = SymbolRegistry.Resolve("game", null, FrameGate);
            if (target == null)
                Console.Error.WriteLine($"[KF2] pacing: no game function at 0x{FrameGate:X8} -- " +
                                        "the game's own two-vblank wait cannot be removed, so the " +
                                        "port will draw and tick at 30 whatever was asked for.");
            else if (HookManager.AddPre(_self, target, frameGate)) { n++; gateHooked = true; }
        }

        // The stage gate is hooked even when the render rate equals the tick rate,
        // where BeforeStage always lets the original run, so that a higher rate can
        // be chosen later. KF2_FPS_GATE
        // replaces the default set rather than adding to it, and can name any
        // address in `game`, not just the thirteen main-loop stages -- experimenting
        // is the point.
        var gate = _gated.Count > 0 ? _gated : (IReadOnlyList<uint>)DefaultGate;
        int gated = 0;
        foreach (uint addr in gate)
        {
            var target = SymbolRegistry.Resolve("game", null, addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] pacing: no game function at 0x{addr:X8}, not gated");
                continue;
            }
            if (HookManager.AddPre(_self, target, stageGate)) { n++; gated++; }
        }

        n += FullRateLogic.Attach(_self);

        HookManager.Commit();
        Console.WriteLine($"[KF2] pacing: {Describe()}, {n} hook(s), " +
                          $"world {(LogicMode == Logic.Full ? "at the render rate" : $"at {LogicHz:0.#} Hz")} " +
                          $"gating {string.Join(" ", gate.Select(g => g.ToString("X8")))}");

        if (vsyncHooked == 0)
            Console.WriteLine("[KF2] pacing: no VSync thunk could be hooked, so no frame " +
                              "boundary is ever reached -- nothing is paced and the world is " +
                              $"not held to {LogicHz:0.#} Hz. See \"Any frame rate\" in " +
                              "docs/PATCHES_AND_MODS.md.");

        if (gated == 0 && LogicMode == Logic.Fixed)
            Console.WriteLine("[KF2] pacing: no stage could be gated -- the world will advance " +
                              "once per rendered frame instead of at the tick rate. See " +
                              "\"Frame pacing\" in docs/PATCHES_AND_MODS.md.");

        if (!gateHooked)
            Console.WriteLine("[KF2] pacing: the game's own frame gate is still in place, so both " +
                              "the picture and the world will sit at 30 whatever was asked for.");
    }

    static string Describe() => Enabled ? $"{TargetFps:0.#} fps" : "uncapped";

    /// <summary>
    /// The game's own frame gate, skipped at every rate. The credit is zeroed here
    /// because the function that would have zeroed it did not run.
    ///
    /// **It used to be left alone at 30 and below**, on the reasoning that there
    /// the game paced itself exactly as it does on hardware and the port added
    /// nothing. That stopped being true when the tick rate became a number of its
    /// own: the gate does not just pace, it *also* decides how often the world
    /// advances, and it only knows one answer for both -- 30. Letting it run would
    /// pin the world back to 30 Hz at any render rate at or below 30, silently
    /// making <see cref="LogicHz"/> a lie exactly at the default.
    ///
    /// What is given up with it is the port's most-tested configuration, where
    /// none of this class did anything. <see cref="Floor"/> is now the pacer at
    /// every rate, and 0x801B6CAC and the vblank callback func_80017850 are
    /// untouched -- only the spin is skipped, so the CD timeout riding on that
    /// counter is unaffected.
    /// </summary>
    public static bool BeforeFrameGate(CpuContext c, IMemory m)
    {
        // Stage 13 is the only caller, so reaching here is the one signal that the
        // frame now being finished is a picture of the world rather than of the
        // interface. LoopPacing wants that and would otherwise need a hook of its
        // own; taking it here costs nothing.
        LoopPacing.WorldDrawn();

        if (!SkipFrameGate) return true;
        m.WriteU32(VBlankCredit, 0u);
        return false;
    }

    /// <summary>
    /// Counts the game asking to present. The frame boundary is defined off this
    /// rather than off the vblank; see <see cref="AfterDrawOTag"/>.
    /// </summary>
    public static void BeforeVSync(CpuContext c, IMemory m) => _vsyncCalls++;

    /// <summary>
    /// The frame boundary: **the ordering table drawn after the game asked to
    /// present**. A second OT with no VSync call between it and the first belongs
    /// to the frame already in flight -- King's Field draws one OT per frame, but
    /// this keeps anything that draws two from being charged twice.
    ///
    /// **This used to be keyed on the vblank instead, and that was the bug that
    /// made every rate above 30 run the game fast.** Since
    /// patches/recompone/0021 the emulated vblank is a wall-clock 60 Hz grid, so
    /// once the port draws faster than 60 most frames reach here with no vblank
    /// elapsed. They were discarded: no <see cref="Floor"/>, no
    /// <see cref="AdvanceLogicClock"/> -- which left <c>_tickThisFrame</c> at the
    /// previous frame's value, so the gated stages ran again and the world ticked
    /// at 30 x (frames per vblank). Measured at KF2_FPS=60: 120 fps drawn, the
    /// 65-tick death clock at 0x8019951A finishing in 1.10 s instead of 2.17.
    /// A VSync call is once per rendered frame at any rate, which the vblank is
    /// not, so it is the boundary. At 30 nothing changes: the presenter's
    /// VSync(0) runs just before this, and the frame gate's spin calls land after
    /// it, so the count is non-zero exactly once per OT either way.
    /// </summary>
    public static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        if (_vsyncCalls == 0) return;
        _vsyncCalls = 0;
        _frames++;

        double now = _clock.Elapsed.TotalMilliseconds;

        _windowFrames++;
        double elapsed = now - _windowStart;
        if (elapsed >= 1000.0)
        {
            Measured = _windowFrames * 1000.0 / elapsed;
            _windowFrames = 0;
            _windowStart = now;
        }

        AdvanceLogicClock(now);

        // How long this frame is allowed to be is no longer the render rate alone:
        // a frame the main loop did not produce belongs to a loop of the game's own
        // that steps its state once per iteration, and LoopPacing holds those to the
        // world's clock. It returns 1000/TargetFps for an ordinary frame, and 0 when
        // nothing should wait, so Floor runs in the uncapped configuration too --
        // which is correct, since the world is held to LogicHz there as well.
        double min = LoopPacing.FrameMinMs(Enabled, TargetFps);
        if (min > 0.0) Floor(min);
    }

    /// <summary>
    /// Decide whether the next frame's gated stages run. Advanced by wall-clock
    /// time rather than by the nominal rate, so a host that cannot hit the target
    /// still runs the world at <see cref="LogicHz"/> instead of at whatever it
    /// managed -- which is the difference between "the picture stutters" and "the
    /// game plays in slow motion".
    ///
    /// At the render rate *equal* to the tick rate this ticks on every frame and
    /// nothing is skipped, because <see cref="Floor"/> guarantees a frame is at
    /// least 1000/LogicHz ms long and the credit therefore always reaches 1.
    /// </summary>
    static void AdvanceLogicClock(double nowMs)
    {
        if (!Gating)
        {
            _logicCredit = 0.0;
            _logicClockMs = nowMs;
            _tickThisFrame = true;
            return;
        }

        double dt = _logicClockMs < 0.0 ? 0.0 : nowMs - _logicClockMs;
        _logicClockMs = nowMs;

        // More than a quarter of a second means the game stopped drawing rather
        // than ran late -- a disc read, a module swap, the first frame of all.
        // Tick once and restart, rather than running the world forward through the
        // gap the way a naive accumulator would.
        if (dt <= 0.0 || dt > 250.0) _logicCredit = 1.0;
        else _logicCredit = Math.Min(_logicCredit + dt * LogicHz / 1000.0, 2.0);

        _tickThisFrame = _logicCredit >= 1.0;
        if (_tickThisFrame) _logicCredit -= 1.0;
    }

    /// <summary>
    /// Skips a gated stage on a frame the logic clock did not tick. Returning
    /// false makes the recompiled body not run; HookManager's Invoke honours it.
    /// In <see cref="Logic.Full"/> this always runs the original, and at the tick
    /// rate the accumulator ticks on every frame, so it does there too.
    /// </summary>
    public static bool BeforeStage(CpuContext c, IMemory m) => !Gating || _tickThisFrame;

    /// <summary>
    /// Hold the frame boundary until <paramref name="min"/> ms have passed since
    /// the last one. The minimum is passed in rather than derived from
    /// <see cref="TargetFps"/> because a modal frame's is longer -- see
    /// <see cref="LoopPacing.FrameMinMs"/>.
    /// </summary>
    static void Floor(double min)
    {
        double now = _clock.Elapsed.TotalMilliseconds;

        // More than a frame of debt means the game stopped drawing rather than ran
        // late -- a disc read, a module swap, the first frame of all. Restart the
        // cadence instead of running flat out to pay it off.
        if (_due < now - min) _due = now;

        if (now < _due)
        {
            double sleepUntil = _due - SpinMs;
            if (now < sleepUntil)
            {
                int ms = (int)(sleepUntil - now);
                if (ms > 0) Thread.Sleep(ms);
            }
            while (_clock.Elapsed.TotalMilliseconds < _due) Thread.SpinWait(48);
        }

        _due += min;
    }
}
