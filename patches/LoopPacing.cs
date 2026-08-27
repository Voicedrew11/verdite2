using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Hold every loop that renders its own frames to the rate the console ran it at,
/// whatever the port draws at.
///
///     KF2_LOOPPACING=0        leave them on the render rate -- comparison only
///     KF2_LOOPPACING_PROBE=1  modal frames a second, world and interface, against
///                             the main loop's own
///
/// ## The hole this fills
///
/// <see cref="FramePacing"/> holds the world to <see cref="FramePacing.LogicHz"/>
/// by *skipping* five main-loop stages on a frame the logic clock did not tick.
/// A **modal loop** -- a function that takes the main loop over and presents its
/// own frames by calling stage 13 -- is entered *from* one of those stages, so the
/// gate decides only whether it is entered and, in FramePacing's own words,
/// "cannot cut one in half". Once inside, no gated stage is being called: the loop
/// iterates once per *rendered* frame and everything it steps runs at the render
/// rate.
///
/// That is not a short list of oversights, it is a structural class, and the
/// repo's documents name it three times over: the transition fade
/// `func_80037B5C`, the menu box open/close `func_800356F4`, and the spell-cast
/// and item-use animations `func_800474D0`, `func_80047000`, `func_8004831C`.
/// <see cref="MenuPacing"/> fixed two counters inside one such loop by hand. The
/// complaint that prompted this one was a picked-up item spinning too fast and an
/// NPC's interact animation playing too fast -- two more entries on a list nobody
/// should be enumerating by playing the game.
///
/// ## What it does
///
/// On the console a modal loop's iteration *was* a rendered frame and a rendered
/// frame *was* a tick. The port broke that identity everywhere the stage gate
/// cannot reach, so restore it: **a frame the main loop did not produce is paced
/// by the world's clock rather than by the render rate.** Nothing is enumerated,
/// nothing is snapshotted and no counter has to be found -- the loop iterates as
/// often as the world ticks, so every number inside it is right by construction.
///
/// It is the slowdown fix run backwards. Smoothing takes the phase between two
/// ticks and *adds* frames the game did not compute; this takes the same phase and
/// *withholds* iterations the game should not have computed.
///
/// ## Classifying a frame costs one hook
///
/// `FramePacing.AfterDrawOTag` -- a `DrawOTag` after a `VSync` call -- **already
/// fires on modal frames**, because a modal loop presents through `func_8002E0FC`
/// (stage 13) or `func_800226A8` (the menu) and both VSync and then draw the
/// table. So the whole defect is that `Floor` paces such a frame to
/// `TargetFps`. Two questions decide what it should pace to instead:
///
/// * **Is this the main loop's frame?** <see cref="MainLoopStage"/> is a pre-hook
///   on stage 9, `func_800140AC`, whose *only* caller is `func_8001369C`, the main
///   loop (stages 3, 4 and 6 are the only others with a single caller; stage 1 is
///   called from two modal loops and three area modules and would have been the
///   wrong choice). It sits after every stage a modal loop is entered from, so the
///   loop's own first frame classifies correctly, and it is not in the gate set,
///   so `KF2_FPS_GATE` cannot disturb it.
/// * **Did this modal frame draw the world?** The game's own frame gate
///   `func_80017880` is called by stage 13 and by nothing else, and
///   <see cref="FramePacing.BeforeFrameGate"/> already hooks it -- so
///   <see cref="WorldDrawn"/> costs no hook at all.
///
/// ## Two rates, and the second one is a choice
///
/// A modal loop that draws the world is showing the world, so it gets
/// <see cref="FramePacing.LogicHz"/>. A modal loop that draws no world is the
/// interface, and it gets <see cref="InterfaceHz"/> -- **60, not the tick rate**,
/// for the reason <see cref="MenuPacing.BlinkMs"/> already records: a menu frame is
/// one vblank, and `KF2_TICKRATE` is a setting about game *speed* with no business
/// retuning a cursor. The menu also presents twice per iteration of
/// `func_80018E80`, so binding it to a 20 Hz tick would make it respond at 10 Hz.
///
/// ## What it costs, and when it does nothing
///
/// <see cref="FrameMinMs"/> returns the frame length FramePacing would have used
/// anyway whenever the render rate is at or below the tick rate, so **at the 20 fps
/// default this class does nothing at all** -- the defect only exists above the
/// tick rate and neither does the fix. It also stands down under
/// `KF2_FPS_LOGIC=full`, which is deliberately "everything at the render rate".
/// Nothing here reads or writes game memory; it only ever lengthens a frame.
///
/// On by default and with no settings page: a correctness fix in the class of
/// frame pacing and the menu repeat, not a taste like dithering. See "Loops that
/// render their own frames" in docs/PATCHES_AND_MODS.md.
/// </summary>
public static class LoopPacing
{
    /// <summary>
    /// Main-loop stage 9, the 3D sound listener. Chosen as the marker for "the
    /// main loop produced this frame" on three counts: `func_8001369C` is its only
    /// caller, it is called unconditionally in the loop's flat list of thirteen,
    /// and it is not in <c>FramePacing.DefaultGate</c>, so gating experiments
    /// cannot silently change what this measures.
    /// </summary>
    const uint MainLoopMarker = 0x800140AC;

    /// <summary>The rate an interface-only modal frame is held to. One vblank, the
    /// same grid and the same reasoning as <see cref="MenuPacing.BlinkMs"/>.</summary>
    const double InterfaceHz = 60.0;

    public static bool Enabled { get; private set; } = true;

    static bool _probe;

    /// <summary>Set by <see cref="MainLoopStage"/>, consumed at the frame boundary.
    /// Absent there, the frame was produced by a loop of the game's own.</summary>
    static bool _mainLoopSeen;

    /// <summary>Set by <see cref="WorldDrawn"/> -- stage 13 reached its own frame
    /// gate, so this frame is a picture of the world rather than of the
    /// interface.</summary>
    static bool _worldDrawn;

    // For the probe: frames of each kind since the window opened.
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _windowMs = -1.0;
    static int _mainFrames, _worldFrames, _uiFrames;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.looppacing",
        Name = "Loop pacing",
        Version = "1.0",
        Description = "Runs a loop that renders its own frames at the world's rate, not the render rate.",
    };

    public static void Configure(string? enabled, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
        if (!string.IsNullOrWhiteSpace(probe)) _probe = probe != "0";
    }

    /// <summary>
    /// Attach the marker hook. Deferred to the first overlay load for the reason
    /// <see cref="FramePacing.Install"/> gives: <see cref="SymbolRegistry"/> reads
    /// the dispatcher's overlay tables, which are registered inside Entry.Run,
    /// after Program.cs has run.
    ///
    /// Attached whether or not it is enabled, since the switch can be taken back
    /// and hooks cannot be added once the game is past its overlay loads.
    /// </summary>
    public static void Install()
    {
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    static void Attach()
    {
        SymbolRegistry.Build();

        var marker = SymbolRegistry.Resolve("game", null, MainLoopMarker);
        if (marker == null)
        {
            // Without the marker every frame reads as the main loop's, which is
            // exactly today's behaviour -- so this degrades to "no fix" rather
            // than to "everything paced at 20", but it degrades silently unless
            // it says so here.
            Console.Error.WriteLine($"[KF2] loop pacing: no game function at 0x{MainLoopMarker:X8} -- " +
                                    "a frame produced by a modal loop cannot be told from the main " +
                                    "loop's, so item, cutscene and fade animations stay on the " +
                                    "render rate.");
            return;
        }

        var hook = typeof(LoopPacing).GetMethod(nameof(MainLoopStage),
                                                BindingFlags.Public | BindingFlags.Static)!;
        if (!HookManager.AddPre(_self, marker, hook)) return;
        HookManager.Commit();

        Console.WriteLine($"[KF2] loop pacing: {(Enabled ? "on" : "off")}, " +
                          $"a self-rendered world frame at {FramePacing.LogicHz:0.#} Hz, " +
                          $"an interface frame at {InterfaceHz:0.#} Hz");
    }

    /// <summary>The main loop reached stage 9, so the frame it is building is its
    /// own.</summary>
    public static void MainLoopStage(CpuContext c, IMemory m) => _mainLoopSeen = true;

    /// <summary>Stage 13 reached the game's own frame gate, so this frame drew the
    /// world. Called from <see cref="FramePacing.BeforeFrameGate"/>, which is
    /// already hooked there.</summary>
    public static void WorldDrawn() => _worldDrawn = true;

    /// <summary>
    /// How long the frame that has just ended is allowed to be, in ms; 0 means do
    /// not wait at all. Called once at the frame boundary, and it consumes both
    /// flags, so exactly one caller may ask per frame.
    ///
    /// The answer is never *shorter* than what FramePacing asked for: a modal frame
    /// takes the longer of the two deadlines, so a render rate below the tick rate
    /// still paces at the render rate and nothing here can make the port run fast.
    /// </summary>
    public static double FrameMinMs(bool enabled, double targetFps)
    {
        bool mainLoop = _mainLoopSeen;
        bool world = _worldDrawn;
        _mainLoopSeen = false;
        _worldDrawn = false;

        double min = enabled && targetFps > 0.0 ? 1000.0 / targetFps : 0.0;

        if (_probe) Count(mainLoop, world);

        // Not a modal frame, switched off, or the comparison mode that deliberately
        // runs everything at the render rate.
        if (mainLoop || !Enabled || !FramePacing.Gating) return min;

        double hz = world ? FramePacing.LogicHz : InterfaceHz;
        return Math.Max(min, 1000.0 / hz);
    }

    /// <summary>
    /// Frames of each kind a second. The comparison the fix rests on is that the
    /// two modal rows stop tracking the render rate while the main-loop row keeps
    /// following it -- run it once with <c>KF2_LOOPPACING=0</c> for the before.
    /// </summary>
    static void Count(bool mainLoop, bool world)
    {
        if (mainLoop) _mainFrames++;
        else if (world) _worldFrames++;
        else _uiFrames++;

        double now = _clock.Elapsed.TotalMilliseconds;
        if (_windowMs < 0.0) { _windowMs = now; return; }

        double elapsed = now - _windowMs;
        if (elapsed < 1000.0) return;

        // Silent unless a modal loop actually ran, so leaving the probe on during
        // ordinary play does not bury the line that matters.
        if (_worldFrames > 0 || _uiFrames > 0)
            Console.WriteLine($"[KF2] loop pacing: {_mainFrames * 1000.0 / elapsed:0.#} main-loop, " +
                              $"{_worldFrames * 1000.0 / elapsed:0.#} modal world, " +
                              $"{_uiFrames * 1000.0 / elapsed:0.#} modal interface frames a second");

        _windowMs = now;
        _mainFrames = 0;
        _worldFrames = 0;
        _uiFrames = 0;
    }
}
