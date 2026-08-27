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
/// cannot reach, so restore it: **a modal loop's body runs once per world tick,
/// not once per rendered frame.** Nothing is enumerated, nothing is snapshotted
/// and no counter has to be found -- the loop iterates as often as the world
/// ticks, so every number inside it is right by construction.
///
/// **Holding the loop is only half of it, and the first version shipped only that
/// half.** Pacing the loop's frames to the tick rate makes the speed right and
/// makes the *picture* 20 fps, which is what came back from play: "it runs at the
/// correct speed, BUT the animation is at its original framerate, and so is the
/// camera". Of course it was -- the frame the loop drew *was* the tick, so
/// <see cref="FramePacing.LogicPhase"/> was 0 on every one of them and the three
/// smoothing patches had nothing to carry, exactly as they had nothing to carry
/// before the frame boundary was fixed.
///
/// So the loop is not paced. It is **gated**, and the gap between its iterations
/// is filled with extra renders -- the main loop's own drawing tail, stage 8 then
/// stage 13, run again at the phase the frame now stands at. That is not a new
/// mechanism: `func_80037B5C` already renders extra frames inside a stage by
/// calling stage 13, and that is why stages 2 and 3 are recorded gate exceptions.
/// What it buys is that a modal frame becomes **exactly a main-loop frame**: the
/// world advances at <see cref="FramePacing.LogicHz"/>, the picture is drawn at
/// the render rate, and <see cref="FrameSmoothing"/>, <see cref="ObjectSmoothing"/>
/// and <see cref="AnimSmoothing"/> -- which hook stage 8 and stage 13, both of
/// which a modal loop reaches -- carry the view, the objects, the creatures and
/// the poses between ticks the way they already do everywhere else.
///
/// It is the slowdown fix run backwards, and then the slowdown fix again on top:
/// withhold the iterations the game should not have computed, then draw the
/// in-between frames it never could.
///
/// **What an extra render does not reach** is a counter the modal loop steps in
/// its own body -- a picked-up item's spin, if its transform does not come from
/// one of the tables <see cref="ObjectSmoothing"/> carries. That steps once a
/// tick, which is the console's own rate for it; smoothing it would need the model
/// submit's *arguments* interpolated rather than a table. Stated here rather than
/// discovered later.
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
/// ## The interface is the other case, and it is paced rather than filled
///
/// A modal loop that draws **no world** -- the menu -- has nothing for the
/// smoothing patches to carry and never calls stage 13, so there is no gap worth
/// filling. It is paced instead, at <see cref="InterfaceHz"/>: **60, not the tick
/// rate**, for the reason <see cref="MenuPacing.BlinkMs"/> already records -- a
/// menu frame is one vblank, and `KF2_TICKRATE` is a setting about game *speed*
/// with no business retuning a cursor. The menu also presents twice per iteration
/// of `func_80018E80`, so binding it to a 20 Hz tick would make it respond at
/// 10 Hz.
///
/// Pacing is also the **fallback** for a world-drawing loop when stage 8 or stage
/// 13 cannot be resolved: the speed stays right and the picture goes back to
/// stepping, which is the failure worth having.
///
/// ## What it costs, and when it does nothing
///
/// <see cref="FrameMinMs"/> returns the frame length FramePacing would have used
/// anyway, and <see cref="AfterRenderer"/> renders nothing extra, whenever the
/// render rate is at or below the tick rate -- so **at the 20 fps default this
/// class does nothing at all**, which is where the defect does not exist either.
/// It also stands down under `KF2_FPS_LOGIC=full`, which is deliberately
/// "everything at the render rate". Nothing here reads or writes game memory: it
/// lengthens a frame, or it asks the renderer to draw one more.
///
/// **The one thing an extra render costs** is that whatever stage 13 steps in its
/// *own* body now steps once per rendered frame inside a modal loop, exactly as it
/// already does in the main loop -- the jitter accumulator at `0x8006E608` and
/// `func_800331B4`'s ambient-sound retrigger. Both are already on that list in
/// docs/TODO.md; this makes a modal loop no worse than an ordinary frame rather
/// than better.
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

    /// <summary>Stage 13, the renderer. Both the post that fills a modal loop's gap
    /// and the extra render itself; `ObjectSmoothing` and `AnimSmoothing` hook it
    /// too, which is what makes an extra render an interpolated one.</summary>
    const uint Renderer = 0x800342D8;

    /// <summary>Stage 8, the only copy of the camera between the player state and
    /// the renderer, and where <see cref="FrameSmoothing"/> carries the view. Run
    /// before each extra render for the same reason the main loop runs it before
    /// each of its own: without it the view is whatever the last copy left.</summary>
    const uint CameraCopy = 0x80025A1C;

    /// <summary>The rate an interface-only modal frame is held to. One vblank, the
    /// same grid and the same reasoning as <see cref="MenuPacing.BlinkMs"/>.</summary>
    const double InterfaceHz = 60.0;

    /// <summary>
    /// Extra renders one modal iteration may be followed by. The loop below ends
    /// on the logic clock, which is wall-clock driven and therefore always ticks,
    /// so this is a backstop against a configuration nobody has thought of rather
    /// than the mechanism -- 64 is over three ticks' worth at 1000 fps.
    /// </summary>
    const int MaxExtraRenders = 64;

    public static bool Enabled { get; private set; } = true;

    static bool _probe;

    /// <summary>Set by <see cref="MainLoopStage"/>, consumed at the frame boundary.
    /// Absent there, the frame was produced by a loop of the game's own.</summary>
    static bool _mainLoopSeen;

    /// <summary>Set by <see cref="WorldDrawn"/> -- stage 13 reached its own frame
    /// gate, so this frame is a picture of the world rather than of the
    /// interface.</summary>
    static bool _worldDrawn;

    /// <summary>How the frame that has just ended classified. Written at the frame
    /// boundary, read by <see cref="AfterRenderer"/> once stage 13 has returned --
    /// which is the first moment outside the renderer, and so the first moment
    /// another render can be asked for.</summary>
    static bool _lastModal, _lastWorld;

    /// <summary>True while <see cref="AfterRenderer"/> is driving a render of its
    /// own. Every hook in the port fires on those too, this one included, so
    /// without it the first extra render would recurse.</summary>
    static bool _inExtra;

    /// <summary>Stage 8 and stage 13 as callables. Bound on first use rather than
    /// at attach time, so they are resolved after every patch has committed its
    /// hooks and an extra render goes through the same detours an ordinary call
    /// does.</summary>
    static Action<CpuContext, IMemory>? _stage8, _stage13;
    static bool _bound;

    /// <summary>False when stage 8 or stage 13 could not be resolved: a
    /// world-drawing modal loop is then paced like the interface instead, which
    /// keeps the speed right and gives up the picture.</summary>
    static bool _canFill = true;

    // For the probe: modal world iterations the loop body actually ran, and extra
    // renders issued to fill the gaps between them. The first is the number that
    // has to equal the tick rate; the second is what turns it back into a picture.
    static int _iters, _extra;

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

        var self = typeof(LoopPacing);
        var hook = self.GetMethod(nameof(MainLoopStage), BindingFlags.Public | BindingFlags.Static)!;
        if (!HookManager.AddPre(_self, marker, hook)) return;

        // The gap filler. This post must run *after* the smoothing patches' own
        // posts on the same function, or the extra render would be asked for while
        // their interpolated values were still in the tables -- hence Program.cs
        // installing this class after all three of them. HookManager runs posts in
        // the order they were added.
        var renderer = SymbolRegistry.Resolve("game", null, Renderer);
        var camera = SymbolRegistry.Resolve("game", null, CameraCopy);
        if (renderer == null || camera == null)
        {
            _canFill = false;
            Console.Error.WriteLine($"[KF2] loop pacing: no game function at " +
                                    $"0x{(renderer == null ? Renderer : CameraCopy):X8} -- " +
                                    "a modal loop cannot be handed an extra render, so its speed " +
                                    "will be right and its picture will step at the tick rate.");
        }
        else
        {
            var fill = self.GetMethod(nameof(AfterRenderer), BindingFlags.Public | BindingFlags.Static)!;
            if (!HookManager.AddPost(_self, renderer, fill)) _canFill = false;
        }

        HookManager.Commit();

        Console.WriteLine($"[KF2] loop pacing: {(Enabled ? "on" : "off")}, " +
                          $"a self-rendered world frame {(_canFill ? "filled to the render rate" : $"paced at {FramePacing.LogicHz:0.#} Hz")}, " +
                          $"an interface frame at {InterfaceHz:0.#} Hz");
    }

    /// <summary>
    /// Bind stage 8 and stage 13 as callables, once, on the first frame that wants
    /// them. Late on purpose: a delegate made here goes through whatever detours
    /// every patch has committed by now, which is the whole point -- an extra
    /// render has to run the smoothing hooks or it is just the same picture again.
    /// </summary>
    static void Bind()
    {
        _bound = true;
        var renderer = SymbolRegistry.Resolve("game", null, Renderer);
        var camera = SymbolRegistry.Resolve("game", null, CameraCopy);
        if (renderer == null || camera == null) { _canFill = false; return; }

        _stage13 = renderer.CreateDelegate<Action<CpuContext, IMemory>>();
        _stage8 = camera.CreateDelegate<Action<CpuContext, IMemory>>();
    }

    /// <summary>
    /// Stage 13 has returned, so the modal loop that called it is about to step its
    /// state again. Hold it there until the world is due to advance, and fill the
    /// wait with the main loop's own drawing tail -- stage 8, then stage 13 -- so
    /// the picture is drawn at the render rate and the smoothing patches carry it.
    ///
    /// Each extra render passes the frame boundary itself, so it is paced by
    /// <c>FramePacing.Floor</c> and advances the logic clock exactly as an ordinary
    /// frame does; the loop below therefore ends on the tick rather than on a
    /// counter, and the phase every render is drawn at is the real one.
    /// </summary>
    public static void AfterRenderer(CpuContext c, IMemory m)
    {
        // Every hook fires on an extra render too, this one included.
        if (_inExtra) return;

        if (!FramePacing.Gating) return;

        // The main loop paces itself; only a loop of the game's own is held here.
        if (!_lastModal || !_lastWorld) return;

        // Counted before the switch and the rate are consulted, so the probe reports
        // the loop body's rate in every configuration -- including KF2_LOOPPACING=0,
        // where the whole claim is that it tracks the render rate.
        if (_probe) _iters++;

        if (!Enabled || !_canFill) return;

        // Below or at the tick rate there is no gap: the loop's own frame is
        // already at least a tick long.
        if (!FramePacing.Extrapolating) return;

        if (!_bound) Bind();
        if (_stage13 == null || _stage8 == null) return;

        int n = 0;
        _inExtra = true;
        try
        {
            while (!FramePacing.TickedThisFrame && n < MaxExtraRenders)
            {
                n++;
                _stage8(c, m);
                _stage13(c, m);
            }
        }
        finally
        {
            _inExtra = false;
        }

        if (_probe) _extra += n;
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
    /// The answer is never *shorter* than what FramePacing asked for: a paced modal
    /// frame takes the longer of the two deadlines, so a render rate below the tick
    /// rate still paces at the render rate and nothing here can make the port run
    /// fast.
    ///
    /// **A world-drawing modal frame is not lengthened when the gap can be filled**
    /// -- <see cref="AfterRenderer"/> holds the loop's *body* to the tick instead,
    /// and lengthening the frame as well would leave the extra renders nowhere to
    /// go and put the picture back at 20 fps.
    /// </summary>
    public static double FrameMinMs(bool enabled, double targetFps)
    {
        bool mainLoop = _mainLoopSeen;
        bool world = _worldDrawn;
        _mainLoopSeen = false;
        _worldDrawn = false;

        // Stashed rather than returned: the first moment another render can be
        // asked for is stage 13's post, which runs after this.
        _lastModal = !mainLoop;
        _lastWorld = world;

        double min = enabled && targetFps > 0.0 ? 1000.0 / targetFps : 0.0;

        if (_probe) Count(mainLoop, world);

        // Not a modal frame, switched off, or the comparison mode that deliberately
        // runs everything at the render rate.
        if (mainLoop || !Enabled || !FramePacing.Gating) return min;

        // The world case is gated and filled rather than paced, unless the filling
        // could not be wired up -- then pacing is the fallback and the picture
        // steps.
        if (world && _canFill) return min;

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
                              $"{_uiFrames * 1000.0 / elapsed:0.#} modal interface frames a second" +
                              (_iters > 0
                                  ? $"; {_iters * 1000.0 / elapsed:0.#} world iteration(s) a second, " +
                                    $"{(double)_extra / _iters:0.0} extra render(s) each"
                                  : ""));

        _windowMs = now;
        _mainFrames = 0;
        _worldFrames = 0;
        _uiFrames = 0;
        _extra = 0;
        _iters = 0;
    }
}
