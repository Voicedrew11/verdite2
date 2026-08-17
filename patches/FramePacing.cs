using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// What frame rate the port runs at.
///
///     KF2_FPS=30      (default) floor every rendered frame at two vblanks
///     KF2_FPS=60      render at one vblank, gate stages to every other one
///     KF2_FPS=off     no floor: the raw port, which bursts past 60
///     KF2_FPS_GATE=80040348+8002A550   stages to skip on odd frames at 60
///
/// It is also a setting, under Video -- see Kf2.Settings.FramePacingPage.
/// The saved choice is read on RuntimeReadyEvent rather than in Configure, since
/// ConfigManager only loads inside HostWindow.Initialize, after Program.cs; the
/// environment variable still wins over it.
///
/// King's Field's speed *is* its frame rate -- everything advances a fixed amount
/// per loop iteration, and the loop waits a whole number of vblanks, so rates
/// quantise to 60/n. On hardware a frame costs 2, 3 or 4 vblanks, which is the
/// 30/20/15 fps banding the game is known for; 30 is its ceiling.
///
/// This port never bands down, because DrawOTag returns as soon as the ordering
/// table is walked on an HLE GPU and the MIPS is native code. Sampling the game
/// thread puts about 90% of a frame in the pacing sleep and roughly a millisecond
/// in actual game code, so nothing here is ever load-limited the way hardware was.
///
/// `30` is therefore a *floor*, not a rescale, and it is the whole fix for running
/// too fast: hold each rendered frame to two vblanks and the port sits on NTSC's
/// fastest band exactly. Verified over 3,200 frames -- no report window above
/// 30.0 fps, eight consecutive in-area windows at 30.0 with a 2:100% histogram.
///
/// This lives in `patches/` and not in `mods/` on purpose. It is a correctness
/// fix that has to be on: shipping it as a runtime-loaded package would let it be
/// absent, disabled or fail to compile, and the failure would look like the game
/// simply running too fast. The measurement tools, which are genuinely optional,
/// are real mods -- see mods/framestats and mods/loopprobe.
/// </summary>
public static class FramePacing
{
    const double VBlankMs = 1000.0 / 60.0;
    const double SpinMs = 1.5;   // spin the last stretch; Thread.Sleep granularity is a few ms

    // libgpu DrawOTag, per overlay. The frame boundary: past it the frame's
    // drawing is done and the loop is about to VSync and show it.
    static readonly (string Overlay, uint Addr)[] DrawOTag =
    [
        ("open", 0x80016078), ("game", 0x80060818), ("end", 0x80013D80),
    ];

    /// <summary>Minimum vblanks a rendered frame may occupy. 2 = 30 fps, 1 = 60 fps.</summary>
    public static int MinVBlanks { get; private set; } = 2;

    /// <summary>False disables the floor entirely -- the raw port.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>Where the chosen rate is kept between runs.</summary>
    public const string VBlankKey = "kf2.framepacing.vblanks";

    /// <summary>Main-loop stages skipped on odd frames when rendering at 60.</summary>
    static readonly List<uint> _gated = [];

    /// <summary>
    /// The stages to gate at 60 when <c>KF2_FPS_GATE</c> named none. These two are
    /// the pair NOTES.md records as the working 60 fps configuration; hooking them
    /// unconditionally is what makes 60 a setting rather than a launch argument,
    /// and costs nothing at 30, where <see cref="BeforeStage"/> always runs the
    /// original.
    /// </summary>
    static readonly uint[] DefaultGate = [0x80040348, 0x8002A550];

    /// <summary>True once KF2_FPS has spoken, so the saved rate does not overrule it.</summary>
    static bool _fromEnv;

    static readonly Stopwatch _clock = Stopwatch.StartNew();

    // When the current frame is allowed to end. Absolute rather than now+min, so a
    // frame that overruns is paid for out of the next one and the rate averages to
    // exactly 60/MinVBlanks instead of drifting down by the accumulated jitter.
    static double _due;
    static long _frames;
    static int _vblanks;

    /// <summary>Frames a second over the last window, so the settings can show whether
    /// the chosen rate is the one being achieved. Zero until the first window ends.</summary>
    public static double Measured { get; private set; }

    static double _windowStart;
    static long _windowFrames;

    // HookManager attributes hooks to a mod so they can be removed again. This is
    // in-project rather than a loaded package, so it declares its own identity.
    static readonly ModInfo _self = new()
    {
        Id = "kf2.framepacing",
        Name = "Frame pacing",
        Version = "1.0",
        Description = "Holds the port to NTSC's fastest band.",
    };

    public static void Configure(string? fps, string? gate)
    {
        if (!string.IsNullOrWhiteSpace(fps))
        {
            if (string.Equals(fps, "off", StringComparison.OrdinalIgnoreCase)) Enabled = false;
            else if (int.TryParse(fps, out int rate))
                MinVBlanks = rate switch { >= 60 => 1, >= 30 => 2, >= 20 => 3, _ => 4 };
            else throw new ArgumentException($"KF2_FPS: cannot read '{fps}'");

            _fromEnv = true;
        }

        if (!string.IsNullOrWhiteSpace(gate))
            foreach (var a in gate.Split('+', StringSplitOptions.RemoveEmptyEntries))
                _gated.Add(Convert.ToUInt32(a.Trim(), 16));
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
        Event.AddListener<VSyncEvent>(_ => _vblanks++);

        // The saved rate can only be read once ConfigManager has loaded, which
        // happens inside HostWindow.Initialize -- after Program.cs called Configure.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            if (_fromEnv) return;
            SetCap(RecompOne.Runtime.Runtime.View.GetInt(VBlankKey, MinVBlanks));
        });

        // Attached whether or not the floor is on: "uncapped" is a choice that can
        // be taken back, and hooks cannot be added once the game is running past the
        // overlay loads.
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    /// <summary>
    /// Change the rate at run time. 0 is uncapped, otherwise the minimum vblanks a
    /// frame may occupy: 1 is 60 fps, 2 is 30, 4 is 15. Safe at any moment -- both
    /// readers of this state are per frame.
    /// </summary>
    public static void SetCap(int vblanks)
    {
        if (vblanks <= 0)
        {
            Enabled = false;
            return;
        }

        Enabled = true;
        MinVBlanks = Math.Clamp(vblanks, 1, 4);
    }

    /// <summary>0 when uncapped, else the vblank floor. The form <see cref="SetCap"/> takes.</summary>
    public static int Cap => Enabled ? MinVBlanks : 0;

    static void Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(FramePacing);
        var frameDrawn = self.GetMethod(nameof(AfterDrawOTag), BindingFlags.Public | BindingFlags.Static)!;
        var stageGate = self.GetMethod(nameof(BeforeStage), BindingFlags.Public | BindingFlags.Static)!;

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

        // The gate is hooked even at 30, where BeforeStage always lets the original
        // run, so that 60 can be chosen later from the settings. KF2_FPS_GATE replaces
        // the default pair rather than adding to it, and can name any address in
        // `game`, not just the thirteen main-loop stages -- experimenting is the
        // point.
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

        HookManager.Commit();
        Console.WriteLine($"[KF2] pacing: {(Enabled ? (MinVBlanks <= 1 ? "60" : (60 / MinVBlanks).ToString()) : "uncapped")} fps, " +
                          $"{n} hook(s), gating {string.Join(" ", gate.Select(g => g.ToString("X8")))}");

        if (gated == 0)
            Console.WriteLine("[KF2] pacing: no stage could be gated -- selecting 60 fps would run " +
                              "the whole game at DOUBLE SPEED. See \"60 fps\" in NOTES.md.");
    }

    /// <summary>
    /// The frame boundary. A second ordering table with no vblank between it and
    /// the first belongs to the frame already in flight -- King's Field draws one
    /// OT per frame, but defining the boundary by the vblank rather than by the
    /// call keeps anything that draws two from being charged twice.
    /// </summary>
    public static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        if (_vblanks == 0) return;
        _vblanks = 0;
        _frames++;

        _windowFrames++;
        double elapsed = _clock.Elapsed.TotalMilliseconds - _windowStart;
        if (elapsed >= 1000.0)
        {
            Measured = _windowFrames * 1000.0 / elapsed;
            _windowFrames = 0;
            _windowStart = _clock.Elapsed.TotalMilliseconds;
        }

        // At one vblank there is nothing to enforce: the runtime's FrameClock
        // already holds a single VSync to a vblank, which is why the unfloored
        // port tops out around 50 fps rather than running away entirely.
        if (Enabled && MinVBlanks > 1) Floor();
    }

    /// <summary>
    /// Skips a gated stage on odd frames, but only at 60 -- at any lower rate the
    /// frame already spans the two vblanks the stage would have run over, so it
    /// runs every time. Returning false makes the recompiled body not run;
    /// HookManager's Invoke honours it.
    /// </summary>
    public static bool BeforeStage(CpuContext c, IMemory m)
        => !Enabled || MinVBlanks > 1 || (_frames & 1) == 0;

    static void Floor()
    {
        double min = MinVBlanks * VBlankMs;
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
