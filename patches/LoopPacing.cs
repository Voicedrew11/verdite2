using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Hold every loop that renders its own frames to the rate the console ran it at,
/// whatever the port draws at, and fill the gap it leaves with redraws.
///
///     KF2_LOOPPACING=0        leave them on the render rate -- comparison only
///     KF2_LOOPPACING=pace     hold the loop and do *not* redraw -- comparison only:
///                             the speed is right and the picture steps at the tick
///     KF2_LOOPPACING=nocarry  redraw, but do not carry the view a modal loop pans
///                             itself -- comparison only
///     KF2_LOOPPACING_PROBE=1  modal frames a second, world and interface, against
///                             the main loop's own
///     KF2_LOOPPACING_PROBE=2  also how far the drawn view moves between one
///                             iteration of a modal loop's body and the next
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
/// **Holding the loop is only half of it.** Pacing the loop's frames to the tick
/// rate makes the speed right and makes the *picture* step at the tick rate, which
/// is what came back from play: "it runs at the correct speed, BUT the animation is
/// at its original framerate, and so is the camera". Of course it was -- the frame
/// the loop drew *was* the tick, so <see cref="FramePacing.LogicPhase"/> was 0 on
/// every one of them and the three smoothing patches had nothing to carry, exactly
/// as they had nothing to carry before the frame boundary was fixed.
///
/// So the loop is not paced. It is **gated**, and the gap between its iterations is
/// filled with **redraws**: stage 13 called again at the phase the frame now stands
/// at. That is not a new mechanism -- `func_80037B5C` already renders extra frames
/// inside a stage by calling stage 13, and that is why stages 2 and 3 are recorded
/// gate exceptions. What it buys is that the world advances at
/// <see cref="FramePacing.LogicHz"/> while the picture is drawn at the render rate,
/// and <see cref="ObjectSmoothing"/> and <see cref="AnimSmoothing"/> -- which
/// bracket stage 13 -- carry the objects, the creatures and the poses between ticks
/// the way they already do everywhere else.
///
/// ## Stage 13 takes two pointers, and the first version of this did not pass them
///
/// **This is the defect that shipped in the redraw's first version and it is worth
/// naming.** Stage 13 is `func_800342D8(VECTOR *pos, SVECTOR *rot)`: it opens with
/// `func_8002E22C`, which copies 16 bytes from `a0` and 8 from `a1` into
/// `0x80192E78`/`0x80192E88` and builds the frame's whole view matrix out of them
/// -- unless both are zero, in which case it reuses what is already stored. Stage 8
/// `func_80025A1C(pos, rot)` is the routine that *fills* those two blocks, and the
/// main loop `func_8001369C` hands both stages the same pair of stack scratch
/// blocks.
///
/// A redraw that calls either without setting `a0`/`a1` gets whatever the register
/// file happens to hold -- after stage 13 that is the tail of `func_8003549C`, so
/// `a0` is a pointer into the sound-slot table near `0x8018EAA4`. Stage 8 then
/// *writes the camera into live game data*, and stage 13 builds its view matrix out
/// of that. A garbage view draws next to nothing, and this game's `PutDrawEnv` has
/// `isbg=0` -- there is no background clear -- so a mostly-empty frame leaves the
/// previous contents of that buffer on screen. Double-buffered at seven redraws a
/// tick that is a black flicker and a display alternating between the correct frame
/// and one two frames old, which is exactly what came back from play.
///
/// So a redraw **replays stage 13 with the arguments the modal loop itself passed**,
/// recorded by <see cref="BeforeRenderer"/>. That is literally "draw this frame
/// again at a later phase", and it is right for all three call shapes the game
/// uses: the fade's own stack blocks (still live, and unchanged because the world
/// is frozen), the `0, 0` of the item-use and `fdat05`/`fdat14` loops (reuse the
/// stored view, as intended), and `fdat23`'s **scripted** cutscene camera.
///
/// **Stage 8 is deliberately not part of a redraw.** Re-running it would overwrite
/// a cutscene's scripted camera with the player's, and it buys nothing: no gated
/// stage runs inside a modal loop, so the *player* camera cannot move there --
/// measured, `KF2_LOOPPACING_PROBE=2` reads `drawn view moved 0.0 u per iteration`
/// through a transition fade, a warp and the menu.
///
/// The register file is snapshotted and restored around the redraws, so a modal
/// loop resumes with exactly the registers stage 13 left it.
///
/// ## A modal loop that pans a camera of its own, which is most of them that move
///
/// The player camera is frozen in a modal loop; a camera the **loop itself** builds
/// is not. `func_8004831C`, the cutscene and message-box loop, is the case play
/// reported -- *"the camera is visibly moving at a lower framerate in the modals"*.
/// It has two of them: one ramps a heading `0 -> 0x1000` by `0x200` an iteration and
/// hands stage 13 the `u16` it just wrote (`a1 = s2`), which is a full turn in 32
/// steps; the other steps `rec+0x26` by `0x40` an iteration and passes
/// `a1 = 0x80199504`, the player's own composed view. Held to the tick that is
/// 11.25 degrees a step against a 165 fps picture, which is exactly as steppy as it
/// sounds -- and it is not a regression, it is the *speed* fix arriving without the
/// smoothing half, the same shape as everything else in this port.
///
/// So a redraw carries it, in the shape <see cref="FrameSmoothing"/> and
/// <see cref="ObjectSmoothing"/> already use: keep the block the loop passed at the
/// previous iteration and at this one, and draw `lerp(prev, cur, phase)` --
/// **interpolated**, at `t - 1 + frac`, so it agrees with those two and can never
/// reach a heading the loop did not produce. It is applied in the pre and taken
/// back in the post, so the interpolated values exist for the length of one stage 13
/// call and the loop's own state is never touched. Three `u16` angles at `a1` and
/// three words of position at `a0` -- exactly what `func_8002E22C` consumes -- and
/// a step past <see cref="CutUnits"/> is a cut rather than a pan and is left alone,
/// the way both other patches guard a placement. Re-primed whenever the pointer pair
/// changes or the main loop takes over, so one loop never lerps from another's view.
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
/// Pacing is also the **fallback** for a world-drawing loop when stage 13 cannot be
/// resolved or has not yet been seen with its arguments, and it is what
/// `KF2_LOOPPACING=pace` selects on purpose: the speed stays right and the picture
/// goes back to stepping, which is the failure worth having.
///
/// ## What it costs, and when it does nothing
///
/// <see cref="FrameMinMs"/> returns the frame length FramePacing would have used
/// anyway, and <see cref="AfterRenderer"/> redraws nothing, whenever the render
/// rate is at or below the tick rate -- so **at the 20 fps default this class does
/// nothing at all**, which is where the defect does not exist either. It also
/// stands down under `KF2_FPS_LOGIC=full`, which is deliberately "everything at the
/// render rate". Nothing here reads or writes game memory: it lengthens a frame, or
/// it asks the renderer to draw the same one again.
///
/// **The one thing a redraw costs** is that whatever stage 13 steps in its *own*
/// body now steps once per rendered frame inside a modal loop, exactly as it
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

    /// <summary>Stage 13, the renderer -- `func_800342D8(VECTOR *pos, SVECTOR *rot)`.
    /// The pre that records those two pointers, the post that fills a modal loop's
    /// gap, and the redraw itself; `ObjectSmoothing` and `AnimSmoothing` bracket it
    /// too, which is what makes a redraw an interpolated one.</summary>
    const uint Renderer = 0x800342D8;

    /// <summary>The rate an interface-only modal frame is held to. One vblank, the
    /// same grid and the same reasoning as <see cref="MenuPacing.BlinkMs"/>.</summary>
    const double InterfaceHz = 60.0;

    /// <summary>
    /// The view stage 13 actually draws with, which is where `KF2_LOOPPACING_PROBE=2`
    /// looks. `func_8002E22C` copies the caller's two blocks here and then builds the
    /// frame's matrices out of *these* on every call -- so when a modal loop passes
    /// `0, 0` it is these that stand still. The three angles are `u16`s at
    /// <see cref="ViewRot"/>+0/+2/+4 and the position the words at
    /// <see cref="ViewPos"/>+0/+4/+8, the same layout stage 8 fills.
    /// </summary>
    const uint ViewPos = 0x80192E78;
    const uint ViewRot = 0x80192E88;

    /// <summary>Units on one axis, or twelve-bit angle units on one axis, between two
    /// iterations past which the loop **cut** to a new view rather than panning to
    /// it, and it is left where the loop put it. 1024 is a quarter turn, and eight
    /// times the largest pan step measured (`0x200`, a full turn in 32). Matches the
    /// placement guard <see cref="FrameSmoothing"/> and <see cref="ObjectSmoothing"/>
    /// already use.</summary>
    const int CutUnits = 1024;

    /// <summary>
    /// The floor under <see cref="MaxRedraws"/>. The loop below ends on the logic
    /// clock, which is wall-clock driven and therefore always ticks, so the cap is
    /// a backstop against a configuration nobody has thought of rather than the
    /// mechanism -- but it must not be a *constant*, because the redraws one tick
    /// needs are `TargetFps / LogicHz` and both ends of that are settings.
    /// </summary>
    const int MinRedrawCap = 64;

    /// <summary>
    /// How many redraws one modal iteration may be followed by, three ticks' worth
    /// of them at the rate actually in play.
    ///
    /// The old fixed 64 was justified against the 20 Hz default -- "over three
    /// ticks' worth at 1000 fps" -- but <c>KF2_TICKRATE</c> is clamped to [5, 60],
    /// so the ratio runs to `1000 / 5 = 200` and 64 is a *third* of one tick.
    /// `KF2_TICKRATE=5` with an uncapped `KF2_FPS` is the reachable case: the cap
    /// is hit before the tick arrives, the loop body runs anyway, and it iterates
    /// several times faster than the world -- the defect this class exists to
    /// remove, silently back.
    ///
    /// Uncapped there is no target to divide, so the rate actually being achieved
    /// stands in for it: <see cref="FramePacing.Measured"/> is a real number
    /// computed once a second at the boundary.
    /// </summary>
    static int MaxRedraws()
    {
        double fps = Math.Max(FramePacing.TargetFps, FramePacing.Measured);
        double hz = Math.Max(FramePacing.LogicHz, 1.0);
        if (!(fps > 0.0)) return MinRedrawCap;
        return Math.Max(MinRedrawCap, (int)Math.Ceiling(fps / hz * 3.0));
    }

    public static bool Enabled { get; private set; } = true;

    /// <summary>`KF2_LOOPPACING=pace`: hold the loop's body to the tick and do not
    /// redraw, which is the behaviour the fallback path gives when stage 13 cannot
    /// be reached. The speed is right and the picture steps at the tick rate;
    /// kept as the A/B against the redraws.</summary>
    static bool _paceOnly;

    static bool _probe;

    /// <summary>`KF2_LOOPPACING_PROBE=2`: also report how far the view stage 13 draws
    /// with moves between one iteration of a modal loop's body and the next. Zero
    /// means the camera is frozen inside the loop; anything else is a loop panning a
    /// camera of its own, which is what <see cref="Carry"/> carries.</summary>
    static bool _viewProbe;

    /// <summary>`KF2_LOOPPACING=nocarry`: redraw, but leave a loop's own pan stepping
    /// at the tick rate. The A/B for the paragraph above.</summary>
    static bool _noCarry;

    /// <summary>Set by <see cref="MainLoopStage"/>, consumed at the frame boundary.
    /// Absent there, the frame was produced by a loop of the game's own.</summary>
    static bool _mainLoopSeen;

    /// <summary>
    /// Whether the executable now loaded is GAME.EXE, which is the only one this
    /// class has any addresses in.
    ///
    /// **Both markers are GAME.EXE's and `DrawOTag` is not.** `MainLoopStage` is
    /// hooked on GAME.EXE's `0x800140AC` and <see cref="WorldDrawn"/> comes from
    /// `FramePacing.BeforeFrameGate` on GAME.EXE's `0x80017880`; neither routine
    /// exists in OPEN.EXE or END.EXE. But `FramePacing.AfterDrawOTag` -- the one
    /// caller of <see cref="FrameMinMs"/> -- is hooked in all three, so every
    /// title, save-select, intro and ending frame arrived here with *both* flags
    /// clear and fell through to the interface branch: the whole of OPEN.EXE and
    /// END.EXE capped at <see cref="InterfaceHz"/> whatever `KF2_FPS` asked for,
    /// and `if (mainLoop) Reprime()` never firing there either.
    ///
    /// The absence of the marker was being read as positive evidence of a modal
    /// loop when in those two overlays it only means "GAME.EXE is not running".
    /// `OverlayLoadedEvent` names the executable, and the nine fdat area modules
    /// are GAME.EXE still running, so they are left alone -- the same distinction
    /// `patches/Widescreen.cs` draws for the margin latch.
    /// </summary>
    static bool _gameExe;

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
    /// without it the first redraw would recurse -- and the recorded arguments
    /// would be overwritten with the ones this class had just set.</summary>
    static bool _inExtra;

    /// <summary>The two pointers the caller handed stage 13, recorded by
    /// <see cref="BeforeRenderer"/> on the loop's own frame and replayed on every
    /// redraw. See the class comment: getting these wrong is not a subtle
    /// difference, it is a garbage view matrix and a corrupted sound table.</summary>
    static uint _argPos, _argRot;
    static bool _argsSeen;

    /// <summary>Stage 13 as a callable. Bound on first use rather than at attach
    /// time, so it is resolved after every patch has committed its hooks and a
    /// redraw goes through the same detours an ordinary call does.</summary>
    static Action<CpuContext, IMemory>? _stage13;
    static bool _bound;

    /// <summary>False when stage 13 could not be resolved: a world-drawing modal
    /// loop is then paced like the interface instead, which keeps the speed right
    /// and gives up the picture.</summary>
    static bool _canFill = true;

    /// <summary>Whether a modal world frame is filled with redraws or simply paced.</summary>
    static bool Filling => _canFill && !_paceOnly;

    /// <summary>The cap in <see cref="AfterRenderer"/> has been hit and said so.
    /// Once a session: it is a standing configuration problem, not an event, so a
    /// line a frame would bury the one that matters.</summary>
    static bool _capWarned;

    // For the probe: modal world iterations the loop body actually ran, and redraws
    // issued to fill the gaps between them. The first is the number that has to
    // equal the tick rate; the second is what turns it back into a picture.
    static int _iters, _extra;

    // The view the modal loop passed stage 13 at the previous iteration of its body
    // and at this one. The frame is drawn at lerp(prev, cur, phase), which is the
    // same instant -- t - 1 + frac -- FrameSmoothing and ObjectSmoothing draw at.
    static bool _primed, _carriable;
    static uint _primedPos, _primedRot;
    static readonly int[] _prevRot = new int[3], _curRot = new int[3];
    static readonly int[] _prevPos = new int[3], _curPos = new int[3];

    // What Carry overwrote, and whether it overwrote anything. Put back by
    // Restore(); there is one stage 13 call in flight at a time, on one thread.
    static bool _applied;
    static readonly int[] _heldRot = new int[3], _heldPos = new int[3];
    static bool _heldRotLive, _heldPosLive;

    // For the view probe: how far the loop's own view moved, summed over the window.
    static long _viewSteps, _viewAngleSum, _viewPosSum, _viewAngleMax;

    // For the probe: frames of each kind since the window opened.
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _windowMs = -1.0;
    static int _mainFrames, _worldFrames, _uiFrames;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.looppacing",
        Name = "Loop pacing",
        Version = "1.1",
        Description = "Runs a loop that renders its own frames at the world's rate, not the render rate.",
    };

    public static void Configure(string? enabled, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(enabled))
        {
            if (string.Equals(enabled, "pace", StringComparison.OrdinalIgnoreCase))
            {
                Enabled = true;
                _paceOnly = true;
            }
            else if (string.Equals(enabled, "nocarry", StringComparison.OrdinalIgnoreCase))
            {
                Enabled = true;
                _noCarry = true;
            }
            else Enabled = enabled != "0";
        }
        if (!string.IsNullOrWhiteSpace(probe))
        {
            _probe = probe != "0";
            _viewProbe = probe == "2";
        }
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
        // An area or executable swap replaces whatever a loop was drawing, so the
        // previous sample describes a view that no longer means anything.
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            Reprime();

            // Only the three executables move this. An fdat is GAME.EXE still
            // running; see _gameExe for what reading one as a hand-over costs.
            if (e.Name is "game") _gameExe = true;
            else if (e.Name is "open" or "end") _gameExe = false;
        });

        HookAttach.OnOverlayLoad("loop pacing", Attach);
    }

    // What is hooked already, so a retry adds only what is missing.
    static bool _markerHooked, _rendererHooked;

    static bool Attach()
    {
        SymbolRegistry.Build();

        var marker = _markerHooked ? null : SymbolRegistry.Resolve("game", null, MainLoopMarker);
        if (marker == null && !_markerHooked)
        {
            // Without the marker, _mainLoopSeen is permanently false, so every
            // frame would read as *modal* -- the opposite of what this used to
            // say. That is not "no fix": it caps the whole game at InterfaceHz,
            // and `if (mainLoop) Reprime()` never fires, so Carry starts lerping
            // stage 13's view in the main loop. FrameMinMs therefore reads
            // _markerHooked and treats an unhooked marker as "all main loop",
            // which is the degradation that was claimed here.
            Console.Error.WriteLine($"[KF2] loop pacing: no game function at 0x{MainLoopMarker:X8} -- " +
                                    "a modal loop's frame cannot be told from the main loop's, so " +
                                    "the class stands down: item, cutscene and fade animations stay " +
                                    "on the render rate.");
            _canFill = false;
            return false;
        }

        var self = typeof(LoopPacing);
        if (marker != null)
        {
            var hook = self.GetMethod(nameof(MainLoopStage), BindingFlags.Public | BindingFlags.Static)!;
            HookManager.AddPre(_self, marker, hook);
        }

        // The argument recorder and the gap filler. The post must run *after* the
        // smoothing patches' own posts on the same function, or the redraw would be
        // asked for while their interpolated values were still in the tables --
        // hence Program.cs installing this class after all three of them.
        // HookManager runs posts in the order they were added.
        var renderer = _rendererHooked ? null : SymbolRegistry.Resolve("game", null, Renderer);
        if (renderer == null && !_rendererHooked)
            Console.Error.WriteLine($"[KF2] loop pacing: no game function at 0x{Renderer:X8} -- " +
                                    "a modal loop cannot be handed a redraw, so its speed will be " +
                                    "right and its picture will step at the tick rate.");
        else if (renderer != null)
        {
            var args = self.GetMethod(nameof(BeforeRenderer), BindingFlags.Public | BindingFlags.Static)!;
            var fill = self.GetMethod(nameof(AfterRenderer), BindingFlags.Public | BindingFlags.Static)!;
            HookManager.AddPre(_self, renderer, args);
            HookManager.AddPost(_self, renderer, fill);
        }

        HookManager.Commit();

        // The detours, not the registrations. Without the marker every frame reads
        // as the main loop's -- today's behaviour -- so that half returns false and
        // is retried; without the renderer pair the loop is paced but not redrawn,
        // which is a picture at the tick rate rather than a wrong one.
        _markerHooked |= HookAttach.Installed(marker);
        _rendererHooked |= HookAttach.Installed(renderer);

        // Assigned, not latched off: a retry that lands the renderer pair on the
        // second pass has to be able to turn filling back on. Bind's own refusal
        // still wins, since it means the delegate could not be made -- though it
        // resolves the address this just resolved, so the two cannot disagree.
        _canFill = _rendererHooked && (!_bound || _stage13 != null);

        Console.WriteLine($"[KF2] loop pacing: {(Enabled && _markerHooked ? "on" : "off")}, " +
                          $"a self-rendered world frame {(Filling ? "redrawn to the render rate" : $"paced at {FramePacing.LogicHz:0.#} Hz")}, " +
                          $"an interface frame at {InterfaceHz:0.#} Hz");

        return _markerHooked && _rendererHooked;
    }

    /// <summary>
    /// Bind stage 13 as a callable, once, on the first frame that wants it. Late on
    /// purpose: a delegate made here goes through whatever detours every patch has
    /// committed by now, which is the whole point -- a redraw has to run the
    /// smoothing hooks or it is just the same picture again.
    /// </summary>
    static void Bind()
    {
        _bound = true;
        var renderer = SymbolRegistry.Resolve("game", null, Renderer);
        if (renderer == null) { _canFill = false; return; }

        _stage13 = renderer.CreateDelegate<Action<CpuContext, IMemory>>();
    }

    /// <summary>
    /// Record the view stage 13 was handed -- `a0` a VECTOR position, `a1` an
    /// SVECTOR of angles, or both zero for "reuse the stored view". A redraw
    /// replays exactly these; see the class comment for what happens when it does
    /// not. Skipped while a redraw is in flight, which is this class's own values
    /// coming back round.
    /// </summary>
    public static void BeforeRenderer(CpuContext c, IMemory m)
    {
        if (!_inExtra)
        {
            // A different pair is a different loop, or the same loop switched
            // cameras: the previous sample describes a view that no longer means
            // anything, so start priming again rather than lerp across the cut.
            if (c.A0 != _argPos || c.A1 != _argRot) Reprime();

            _argPos = c.A0;
            _argRot = c.A1;
            _argsSeen = true;

            // The loop has just written this iteration's view, so this is the moment
            // `cur` is real. A redraw must not re-sample: the block then holds what
            // Restore put back, which is the same `cur` again.
            Sample(m);
        }

        Carry(m);
    }

    /// <summary>Roll the previous iteration's view forward and read this one's.</summary>
    static void Sample(IMemory m)
    {
        for (int i = 0; i < 3; i++)
        {
            _prevRot[i] = _curRot[i];
            _prevPos[i] = _curPos[i];
        }

        if (_argRot != 0u)
            for (int i = 0; i < 3; i++) _curRot[i] = m.ReadU16(_argRot + (uint)(i * 2));
        if (_argPos != 0u)
            for (int i = 0; i < 3; i++) _curPos[i] = (int)m.ReadU32(_argPos + (uint)(i * 4));

        _carriable = _primed;   // both prev and cur are real only after two samples
        _primed = true;
        _primedPos = _argPos;
        _primedRot = _argRot;

        if (!_viewProbe || !_carriable) return;

        int da = 0;
        for (int i = 0; i < 3; i++) da += Math.Abs(Wrap12(_curRot[i] - _prevRot[i]));
        long dp = 0;
        for (int i = 0; i < 3; i++) dp += Math.Abs((long)_curPos[i] - _prevPos[i]);

        _viewSteps++;
        _viewAngleSum += da;
        _viewPosSum += dp;
        if (da > _viewAngleMax) _viewAngleMax = da;
    }

    /// <summary>
    /// Draw this frame at `lerp(prev, cur, phase)` -- the view the loop had an
    /// iteration ago, carried towards the one it has now by however far into the tick
    /// the frame stands. Applied on the loop's own frame as well as on a redraw: at
    /// phase ~0 that draws `prev`, which is what makes it an interpolation rather
    /// than a snap forward on every tick.
    /// </summary>
    static void Carry(IMemory m)
    {
        _applied = false;
        _heldRotLive = false;
        _heldPosLive = false;

        if (_noCarry || !Enabled || !_carriable || !FramePacing.Extrapolating) return;
        if (_argPos != _primedPos || _argRot != _primedRot) return;

        // The reprime lives in MainLoopStage, so an unhooked marker means it never
        // runs -- and every frame then reads as the main loop's, which stops the
        // redraws but not this. Carrying there is the very defect the move was
        // made to prevent: stage 13's view lerped in the main loop, on top of
        // FrameSmoothing's own. Without the marker the class has already stood
        // itself down; this is the half that is not reached through _lastModal.
        if (!_markerHooked) return;

        double frac = FramePacing.LogicPhase;

        if (_argRot != 0u)
        {
            var d = new int[3];
            bool live = false, cut = false;
            for (int i = 0; i < 3; i++)
            {
                d[i] = Wrap12(_curRot[i] - _prevRot[i]);
                if (d[i] != 0) live = true;
                if (Math.Abs(d[i]) > CutUnits) cut = true;
            }

            if (live && !cut)
            {
                for (int i = 0; i < 3; i++)
                {
                    _heldRot[i] = (int)m.ReadU16(_argRot + (uint)(i * 2));
                    int v = (_prevRot[i] + (int)Math.Round(d[i] * frac)) & 0xFFF;
                    m.WriteU16(_argRot + (uint)(i * 2), (ushort)v);
                }
                _heldRotLive = true;
                _applied = true;
            }
        }

        if (_argPos != 0u)
        {
            var d = new int[3];
            bool live = false, cut = false;
            for (int i = 0; i < 3; i++)
            {
                d[i] = _curPos[i] - _prevPos[i];
                if (d[i] != 0) live = true;
                if (Math.Abs(d[i]) > CutUnits) cut = true;
            }

            if (live && !cut)
            {
                for (int i = 0; i < 3; i++)
                {
                    _heldPos[i] = (int)m.ReadU32(_argPos + (uint)(i * 4));
                    m.WriteU32(_argPos + (uint)(i * 4),
                               (uint)(_prevPos[i] + (int)Math.Round(d[i] * frac)));
                }
                _heldPosLive = true;
                _applied = true;
            }
        }
    }

    /// <summary>Put the loop's own view back the moment stage 13 has read it, so the
    /// interpolated values exist for exactly one call and the loop's state is
    /// untouched.</summary>
    static void Restore(IMemory m)
    {
        if (!_applied) return;

        if (_heldRotLive)
            for (int i = 0; i < 3; i++) m.WriteU16(_argRot + (uint)(i * 2), (ushort)_heldRot[i]);
        if (_heldPosLive)
            for (int i = 0; i < 3; i++) m.WriteU32(_argPos + (uint)(i * 4), (uint)_heldPos[i]);

        _applied = false;
    }

    /// <summary>Forget the two samples: a different loop, a different camera, or the
    /// main loop taking over again.</summary>
    static void Reprime()
    {
        _primed = false;
        _carriable = false;
    }

    static int Wrap12(int d)
    {
        d &= 0xFFF;
        return d > 2048 ? d - 4096 : d;
    }

    /// <summary>
    /// Stage 13 has returned, so the modal loop that called it is about to step its
    /// state again. Hold it there until the world is due to advance, and fill the
    /// wait by drawing the same frame again -- at the phase the frame now stands
    /// at, so <see cref="ObjectSmoothing"/> and <see cref="AnimSmoothing"/> carry
    /// it.
    ///
    /// Each redraw passes the frame boundary itself, so it is paced by
    /// <c>FramePacing.Floor</c> and advances the logic clock exactly as an ordinary
    /// frame does; the loop below therefore ends on the tick rather than on a
    /// counter, and the phase every redraw is drawn at is the real one.
    /// </summary>
    public static void AfterRenderer(CpuContext c, IMemory m)
    {
        // Paired with Carry in the pre, and taken back before anything else can see
        // it -- including the redraws below, each of which applies its own.
        Restore(m);

        // Every hook fires on a redraw too, this one included.
        if (_inExtra) return;

        if (!FramePacing.Gating) return;

        // The main loop paces itself; only a loop of the game's own is held here.
        if (!_lastModal || !_lastWorld) return;

        // Counted before the switch and the rate are consulted, so the probe reports
        // the loop body's rate in every configuration -- including KF2_LOOPPACING=0,
        // where the whole claim is that it tracks the render rate.
        if (_probe) _iters++;

        if (!Enabled || !Filling || !_argsSeen) return;

        // Below or at the tick rate there is no gap: the loop's own frame is
        // already at least a tick long.
        if (!FramePacing.Extrapolating) return;

        if (!_bound) Bind();
        if (_stage13 == null) return;

        int cap = MaxRedraws();
        int n = 0;
        var saved = c.Snapshot();
        _inExtra = true;
        try
        {
            while (!FramePacing.TickedThisFrame && n < cap)
            {
                n++;
                c.A0 = _argPos;
                c.A1 = _argRot;
                _stage13(c, m);
            }
        }
        finally
        {
            _inExtra = false;
            // The modal loop resumes with exactly the registers stage 13 left it,
            // whatever the redraws did to them.
            c.Restore(saved);
        }

        // Hitting the cap means the loop body is about to run without the world
        // having ticked -- the defect this class removes, coming back. Every other
        // backstop in this port announces itself (FramePacing's boundary watchdog,
        // SpriteAnim's); this one recorded it in _extra and only when the probe
        // happened to be on.
        if (n >= cap && !FramePacing.TickedThisFrame && !_capWarned)
        {
            _capWarned = true;
            Console.Error.WriteLine($"[KF2] loop pacing: {cap} redraws did not reach a tick at " +
                                    $"{FramePacing.TargetFps:0.#} fps against {FramePacing.LogicHz:0.#} Hz -- " +
                                    "the modal loop's body is running faster than the world. " +
                                    "Reported once.");
        }

        if (_probe) _extra += n;
    }

    /// <summary>
    /// The main loop reached stage 9, so the frame it is building is its own -- and
    /// any modal loop that was running has ended, so the next one must not lerp
    /// from the view this one left behind.
    ///
    /// **The reprime is here rather than at the frame boundary**, which is where it
    /// used to be. It is the single line keeping <see cref="_carriable"/> false
    /// during ordinary play -- <see cref="Sample"/> sets `_primed` on every stage 13
    /// call and this clears it again -- and routing it through
    /// <see cref="FrameMinMs"/> made that rest on the boundary being reached.
    /// `FrameMinMs` has one call site, inside `FramePacing.AfterDrawOTag`, and a
    /// lost frame boundary is a failure this repo has hit twice (patch `0027`, and
    /// the watchdog `FramePacing.BeforeStage` now carries). It would have latched
    /// `_carriable` true and started interpolating stage 13's view *in the main
    /// loop*, at a frozen phase -- `_logicCredit` is advanced on the same dead path
    /// -- which is a constant-lag camera on top of `FrameSmoothing`'s own carry.
    ///
    /// Stage 9 runs after every stage a modal loop is entered from and before stage
    /// 13, so the main loop's own frame still finds `_primed` clear, and the signal
    /// is the main loop actually running rather than a frame having been counted.
    /// </summary>
    public static void MainLoopStage(CpuContext c, IMemory m)
    {
        _mainLoopSeen = true;
        Reprime();
    }

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
    /// and lengthening the frame as well would leave the redraws nowhere to go and
    /// put the picture back at the tick rate.
    /// </summary>
    public static double FrameMinMs(bool enabled, double targetFps)
    {
        // An unhooked marker means _mainLoopSeen can never be set, and reading that
        // as "modal" would pace the entire game rather than stand the class down.
        // Neither marker exists outside GAME.EXE, so OPEN.EXE and END.EXE are the
        // same trap one overlay wider -- they pace themselves, as FramePacing
        // already says of them.
        bool mainLoop = _mainLoopSeen || !_markerHooked || !_gameExe;
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
        // could not be wired up or was switched off -- then pacing is the fallback
        // and the picture steps.
        if (world && Filling) return min;

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
                                    $"{(double)_extra / _iters:0.0} redraw(s) each, " +
                                    $"view a0=0x{_argPos:X8} a1=0x{_argRot:X8}"
                                  : "") +
                              (_viewProbe && _viewSteps > 0
                                  ? $"; the loop's own view moved {(double)_viewAngleSum / _viewSteps:0.0} u " +
                                    $"(peak {_viewAngleMax}) and {(double)_viewPosSum / _viewSteps:0.0} units " +
                                    $"per iteration over {_viewSteps}"
                                  : ""));

        _windowMs = now;
        _mainFrames = 0;
        _worldFrames = 0;
        _uiFrames = 0;
        _extra = 0;
        _iters = 0;
        _viewSteps = 0;
        _viewAngleSum = 0;
        _viewPosSum = 0;
        _viewAngleMax = 0;
    }
}
