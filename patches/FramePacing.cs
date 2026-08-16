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

    /// <summary>Main-loop stages skipped on odd frames when rendering at 60.</summary>
    static readonly List<uint> _gated = [];

    static readonly Stopwatch _clock = Stopwatch.StartNew();

    // When the current frame is allowed to end. Absolute rather than now+min, so a
    // frame that overruns is paid for out of the next one and the rate averages to
    // exactly 60/MinVBlanks instead of drifting down by the accumulated jitter.
    static double _due;
    static long _frames;
    static int _vblanks;

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

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached || !Enabled) return;
            attached = true;
            Attach();
        });
    }

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

        // Gating costs nothing when it is not asked for: with no gate list, no
        // stage is hooked at all. Any address in `game` can be named, not just the
        // thirteen main-loop stages -- experimenting is the point.
        foreach (uint addr in _gated)
        {
            var target = SymbolRegistry.Resolve("game", null, addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] pacing: no game function at 0x{addr:X8}, not gated");
                continue;
            }
            if (HookManager.AddPre(_self, target, stageGate)) n++;
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] pacing: {(MinVBlanks <= 1 ? "60" : (60 / MinVBlanks).ToString())} fps, " +
                          $"{n} hook(s)" + (_gated.Count > 0
                              ? $", gating {string.Join(" ", _gated.Select(g => g.ToString("X8")))}"
                              : ""));

        if (MinVBlanks <= 1 && _gated.Count == 0)
            Console.WriteLine("[KF2] pacing: 60 fps with no gated stages -- the whole game runs at " +
                              "DOUBLE SPEED. Rendering-only 60 fps; see \"60 fps\" in NOTES.md.");
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

        // At one vblank there is nothing to enforce: the runtime's FrameClock
        // already holds a single VSync to a vblank, which is why the unfloored
        // port tops out around 50 fps rather than running away entirely.
        if (MinVBlanks > 1) Floor();
    }

    /// <summary>Skips a gated stage on odd frames. Returning false makes the
    /// recompiled body not run -- HookManager's Invoke honours it.</summary>
    public static bool BeforeStage(CpuContext c, IMemory m) => (_frames & 1) == 0;

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
