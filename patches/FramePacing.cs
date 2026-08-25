using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// What frame rate the port runs at, and how the game's own clock is kept at
/// 30 Hz while it does.
///
///     KF2_FPS=30      (default) the game paces itself, as it does on hardware
///     KF2_FPS=60      render at 60, tick the world at 30
///     KF2_FPS=144     any number is allowed; the world still ticks at 30
///     KF2_FPS=off     no pacing at all: the raw port, which runs far too fast
///     KF2_FPS_LOGIC=full   do not gate anything; scale the movement deltas instead
///     KF2_FPS_GATE=8002A550+80040348+80046A60   the stages to tick at 30
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
/// ## What actually holds the port at 30
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
/// 2. **RecompOne's FrameClock**, which used to be a hard-coded 60 Hz applied per
///    *VSync call*. patches/recompone/0025 makes it settable; this class sets it
///    as a permissive ceiling rather than as the pacer, because a frame can carry
///    more than one VSync call and a per-call throttle therefore cannot express a
///    frame rate.
/// 3. **<see cref="Floor"/>**, below -- the port's own deadline at the frame
///    boundary, which is the only one of the three that knows where a frame ends.
///
/// At the default 30 fps *none* of this moves: the game's gate is left in place,
/// FrameClock keeps its 60, and the floor has nothing to enforce. Everything here
/// only starts doing something once a rate above 30 is asked for.
///
/// ## The logic clock
///
/// Above 30 the game's gate is removed and the loop runs at whatever the floor
/// allows -- so the world would advance a fixed amount that many times a second.
/// The fix is a fixed timestep: a wall-clock accumulator ticks at
/// <see cref="LogicHz"/>, and the main-loop stages that hold per-tick state run
/// only on a frame where it ticked. Everything the game counts in frames -- the
/// death sequence, spell lifetimes, buff timers, the poison tick, entity AI --
/// then keeps hardware timing exactly, at any rate.
///
/// What is gated is everything found to hold per-tick state that cannot draw:
///
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
///
/// **Can it draw** is the test each of those had to pass, because a stage that
/// submits primitives cannot be skipped -- at 120 fps three frames in four would
/// have nothing from it. That is checked against the emitted C#: the subtree of a
/// gated function must contain no `DrawOTag`, `VSync`, `PutDispEnv` or
/// `PutDrawEnv`. Stage 3 is the one exception and it is a different shape: it
/// reaches them only as `func_8002A550 -> func_80037B5C -> func_800342D8`, calling
/// stage 13 from inside a modal sub-loop (the in-game menu) that takes the main
/// loop over and renders its own frames. Skipping stage 3 decides whether such a
/// loop is entered; it cannot cut one in half.
///
/// **What this still does not cover, stated rather than discovered later.**
/// **Stage 2** (`func_80037C0C`, the 396-arm object dispatch) holds per-frame
/// state too and is deliberately *not* gated: all four of those SDK entry points
/// are in its 268-function subtree, so it presents. Its per-tick counters would
/// have to be found and gated one at a time. And stage 13's **jitter
/// accumulator at 0x8006E608** is in stage 13's own body rather than in a callee,
/// so no hook can reach it -- it is a damped accumulator (decayed by an eighth a
/// call) driving the screen shake, so above 30 it settles faster and smaller.
///
/// The camera would then move 30 times a second while the picture updated more
/// often, which looks worse than 30 fps did. <see cref="FrameSmoothing"/> is the
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
    /// <summary>The rate the game's own logic is held to. It is what the game was
    /// built around: NTSC's fastest band, and King's Field's ceiling on hardware.</summary>
    public const double LogicHz = 30.0;

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
    /// all, which runs the game far too fast and is a diagnostic setting.</summary>
    public static double TargetFps { get; private set; } = LogicHz;

    /// <summary>False only when uncapped.</summary>
    public static bool Enabled => TargetFps > 0.0;

    /// <summary>Where the chosen rate is kept between runs.</summary>
    public const string FpsKey = "kf2.framepacing.fps";

    /// <summary>The key this used to be kept under, as a vblank divisor. Read once
    /// and converted, so an existing config keeps the rate it had.</summary>
    public const string VBlankKey = "kf2.framepacing.vblanks";

    /// <summary>What to do about the game advancing once per loop iteration.</summary>
    public enum Logic
    {
        /// <summary>Tick the world at <see cref="LogicHz"/> however often the port
        /// draws. Every counter in the game keeps hardware timing.</summary>
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
    /// What ticks at 30 when <c>KF2_FPS_GATE</c> names nothing: main-loop stages
    /// 3, 4, 5 and 6, plus stage 13's fade state machine. Hooking them
    /// unconditionally is what makes the rate a setting rather than a launch
    /// argument, and costs nothing at 30, where <see cref="BeforeStage"/> always
    /// runs the original.
    ///
    /// Every one of them was checked for whether it can *draw* before being put
    /// here, because a stage that submits primitives cannot be skipped -- the
    /// picture would flicker at three frames in four. Stage 2 is the reason that
    /// matters: it holds per-frame state too, but `DrawOTag`, `VSync`,
    /// `PutDispEnv` and `PutDrawEnv` are all in its 268-function subtree, so it
    /// is deliberately **not** here. See "Any frame rate" in
    /// docs/PATCHES_AND_MODS.md.
    /// </summary>
    static readonly uint[] DefaultGate =
    [
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
    ];

    /// <summary>True once KF2_FPS has spoken, so the saved rate does not overrule it.</summary>
    static bool _fromEnv;

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
    // target still runs the world at 30 Hz instead of at whatever it achieved.
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

    /// <summary>True when the world is being ticked at <see cref="LogicHz"/> rather
    /// than once per rendered frame -- which is only above 30, and only in
    /// <see cref="Logic.Fixed"/>.</summary>
    public static bool Gating => LogicMode == Logic.Fixed && TargetFps > LogicHz + 0.001;

    /// <summary>True when the game's own two-vblank wait is being skipped.</summary>
    static bool SkipFrameGate => !Enabled || TargetFps > LogicHz + 0.001;

    // HookManager attributes hooks to a mod so they can be removed again. This is
    // in-project rather than a loaded package, so it declares its own identity.
    static readonly ModInfo _self = new()
    {
        Id = "kf2.framepacing",
        Name = "Frame pacing",
        Version = "2.0",
        Description = "Draws at the chosen rate and ticks the world at 30.",
    };

    public static void Configure(string? fps, string? gate, string? logic = null)
    {
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

        // The game's own frame gate. Hooked at every rate, because BeforeFrameGate
        // lets it run at 30 and the rate can change later; only GAME.EXE's copy is
        // hooked, so the title and the ending pace themselves as they always did.
        bool gateHooked = false;
        {
            var target = SymbolRegistry.Resolve("game", null, FrameGate);
            if (target == null)
                Console.Error.WriteLine($"[KF2] pacing: no game function at 0x{FrameGate:X8} -- " +
                                        "the game's own two-vblank wait cannot be removed, so no " +
                                        "rate above 30 will be reached.");
            else if (HookManager.AddPre(_self, target, frameGate)) { n++; gateHooked = true; }
        }

        // The stage gate is hooked even at 30, where BeforeStage always lets the
        // original run, so that a higher rate can be chosen later. KF2_FPS_GATE
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
                              "not held to 30 Hz. See \"Any frame rate\" in docs/PATCHES_AND_MODS.md.");

        if (gated == 0 && LogicMode == Logic.Fixed)
            Console.WriteLine("[KF2] pacing: no stage could be gated -- any rate above 30 would run " +
                              "the whole game too fast. See \"Frame pacing\" in docs/PATCHES_AND_MODS.md.");

        if (!gateHooked && TargetFps > LogicHz)
            Console.WriteLine("[KF2] pacing: the game's own frame gate is still in place, so the " +
                              "rate will sit at 30 whatever was asked for.");
    }

    static string Describe() => Enabled ? $"{TargetFps:0.#} fps" : "uncapped";

    /// <summary>
    /// The game's own frame gate. Left alone at 30 and below -- there the game
    /// paces itself exactly as it does on hardware and the port adds nothing --
    /// and skipped above it, which is the whole of "render faster". The credit is
    /// zeroed here because the function that would have zeroed it did not run.
    /// </summary>
    public static bool BeforeFrameGate(CpuContext c, IMemory m)
    {
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

        if (Enabled) Floor();
    }

    /// <summary>
    /// Decide whether the next frame's gated stages run. Advanced by wall-clock
    /// time rather than by the nominal rate, so a host that cannot hit the target
    /// still runs the world at <see cref="LogicHz"/> instead of at whatever it
    /// managed -- which is the difference between "the picture stutters" and "the
    /// game plays in slow motion".
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
    /// At 30 and below, and in <see cref="Logic.Full"/>, this always runs the
    /// original.
    /// </summary>
    public static bool BeforeStage(CpuContext c, IMemory m) => !Gating || _tickThisFrame;

    static void Floor()
    {
        double min = 1000.0 / TargetFps;
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
