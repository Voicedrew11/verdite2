using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Hold the two things in the menus that are counted in vblanks -- the cursor's
/// auto-repeat and its blink -- to the rate the console had, at any frame rate.
///
///     KF2_MENUPACING=0        leave both on the frame clock -- comparison only
///     KF2_MENUPACING_PROBE=1  what each repeat cost, and the blink's step rate
///
/// ## The menu is outside the stage gate by construction
///
/// <see cref="FramePacing"/> holds the world to <see cref="FramePacing.LogicHz"/>
/// with a pre-hook on six main-loop stage entry points. The in-game menu is not
/// one: `func_80029CBC`, inside stage 3, `jal`s **`func_80018E80`** on a
/// just-pressed Circle and that call **blocks for the whole menu session**,
/// running its own loop and presenting its own frames through `func_800226A8`
/// (`VSync` then `DrawOTag`). While it runs, no gated function is being called,
/// so nothing inside it is on the tick clock -- FramePacing's own class comment
/// says as much: *skipping stage 3 decides whether such a loop is entered; it
/// cannot cut one in half*.
///
/// ## What made the cursor race
///
/// The two cursor steppers -- `func_8001EA14` (a fixed option list) and
/// `func_8001EB70` (a scrolling one: inventory, equipment, magic) -- open the
/// same way:
///
///     func_80022E90();   // the auto-repeat delay
///     func_80022E58();   // PadRead(1), and latch 0x8006E5C4 if anything is down
///     ... test Up (0x8006E590) / Down (0x8006E594) against that word, and step
///
/// There is **no edge detection**. Holding Up steps the cursor on every iteration
/// of the menu loop; the only throttle is <see cref="RepeatGate"/>:
///
///     if (*0x8006E5C4 != 1) return;          // nothing was down last read
///     *0x8006E5C4 = 0;
///     for (s0 = 0; ; ) {
///         if (PadRead(1) == 0) return;       // released
///         if (s0 &lt; 6) { s0++; VSync(0); continue; }
///         *0x8006E5CC = 0; return;           // the repeat fires
///     }
///
/// On hardware `VSync(0)` waits for the next vblank, so that spin costs **six
/// vblanks -- 100 ms** whatever frame rate the game itself was achieving. Here
/// `VSync(0)` presents and returns (see `Sdk.LibEtc.VSync`, and
/// patches/recompone/0021, which puts the vblank on a wall-clock grid instead of
/// on the call). The only thing pacing a VSync *call* is RecompOne's
/// `FrameClock`, which <c>FramePacing.ApplyHostCeiling</c> deliberately sets
/// permissive at `max(60, TargetFps * 2)`. Raising the render rate therefore
/// shortens the delay, and above 60 it stops holding the spin at all -- measured,
/// holding Down in the menu for two seconds:
///
///     KF2_FPS   spin, unpaced   steps/s      spin, paced   steps/s
///     20            66 ms         7.5          100.8 ms      6.0
///     60            41 ms        15.0          100.7 ms      8.5
///     144          1.2 ms        37.5          100.2 ms      9.5
///
/// patches/recompone/0025 names the trap in its own comment: the ceiling is
/// permissive *because* it paces per call, and a caller that needs a rate should
/// keep its own deadline. This delay is expressed in calls, so it inherited the
/// ceiling -- and at 144 fps inherited nothing at all.
///
/// The residual spread once paced -- 6.0 to 9.5 steps a second -- is the menu's
/// own frame, which still lands at the render rate after the spin returns. It is
/// the difference between 50 ms and 7 ms on top of a constant 100, against the
/// five-fold spread it replaces.
///
/// ## What this does
///
/// Makes those six `VSync(0)` calls cost a vblank each again, and only those.
/// One pre/post pair around <see cref="RepeatGate"/> marks the window, and a pre
/// on the VSync thunk holds to the next 1/60 s boundary while it is open. The
/// six frames are still presented, which is the point of pacing the calls rather
/// than sleeping the shortfall afterwards: at 144 fps that would be ~79 ms of
/// frozen picture every repeat.
///
/// **Why not the obvious alternatives.** Adding `func_80018E80` to FramePacing's
/// gate would skip the entire menu session rather than one iteration of its loop,
/// because `HookManager` detours whole functions. Gating the steppers themselves
/// is worse: both return the new cursor index in `V0`, so a bare `return false`
/// hands the caller garbage.
///
/// **It costs nothing when idle.** <see cref="RepeatGate"/> returns before its
/// loop whenever `0x8006E5C4` is not 1, so nothing is paced unless a direction is
/// actually being held in a menu -- and one hook covers every list in the game,
/// since `func_8001EA14`, `func_8001EB70`, `func_8001B0D0`, `func_8001BB7C`,
/// `func_8001BE60` and `func_800206E0` all call it.
///
/// ## The blink is the same bug one layer up
///
/// The cursor's highlight is an eight-step ramp up and back down:
/// <see cref="BlinkStepper"/>
/// -- `func_80022530`, the menu's frame head (buffer swap, OT pointer,
/// `ClearOTag`) -- steps `0x8006E5CC` by one in the direction at `0x8006E5D0`,
/// clamping to 7 at the top and latching off at the bottom, and `func_80021A84`
/// reads the counter as `(v + 0x1F4) &lt;&lt; 6` into a sprite's `+0xE` to pick one
/// of eight cursor frames. `func_8001EA14` zeroes the direction on every accepted
/// move, restarting the ramp; `func_80022E90` zeroes the counter when a repeat
/// fires.
///
/// It is not a continuous pulse: the down ramp latches the direction to
/// `0xFFFFFFFF` and freezes, so it is **one wink per accepted move**, sixteen
/// steps long. Sitting still in a menu steps it zero times a second, measured.
///
/// It steps **once per menu frame**, so its rate is the render rate. Measured
/// steps a second while holding Down, `KF2_MENUPACING=0` against on:
///
///     KF2_FPS    off        on
///     20         15-19      8-13
///     60         30-35      ~21
///     144        73-77      9-20
///
/// The frame head cannot be skipped -- it swaps the buffer -- so this is a
/// pre/post pair that saves the two words and puts them back on a frame the grid
/// did not advance on, the same shape `ObjectSmoothing` uses. **Nothing sleeps
/// here**: the menu still renders at the render rate and only the counter is
/// held, so the blink is *capped* rather than paced.
///
/// **60 Hz and not <see cref="FramePacing.LogicHz"/>, deliberately.** A menu
/// frame is one `VSync(0)`, which is one vblank; the tick rate is a judgement
/// about what the *world* achieved under load and has no business changing how
/// fast a cursor winks. Binding it there would also make `KF2_TICKRATE` -- a
/// setting about game speed -- retune the interface.
///
/// **It does bite a little at the 20 fps default**, which is worth stating
/// because the first guess was that it would not: the menu renders its frames in
/// pairs (two passes of an inner loop per iteration of `func_80018E80`, each
/// presenting), and the second of a pair lands inside the same vblank slot as the
/// first whatever the render rate. So the default's wink gets somewhat longer
/// rather than staying exactly as it was. Whether that reads better or worse is a
/// by-eye question, and <see cref="BlinkMs"/> is where the answer goes.
///
/// On by default and with no settings page: this is a correctness fix like frame
/// pacing rather than a taste like dithering, and the console fixed the numbers
/// at one value each. See "The menu's cursor repeat" in
/// docs/PATCHES_AND_MODS.md.
/// </summary>
public static class MenuPacing
{
    /// <summary>The auto-repeat delay every list in the game calls first: spin on
    /// up to six `VSync(0)` calls while a button is still held, then let the
    /// caller step the cursor once.</summary>
    const uint RepeatGate = 0x80022E90;

    /// <summary>The menu's frame head -- buffer swap, OT pointer, `ClearOTag`,
    /// and the cursor blink's ping-pong counter. Called once per menu frame from
    /// every screen in `func_80018E80`'s jump table.</summary>
    const uint BlinkStepper = 0x80022530;

    /// <summary>The blink's ping-pong counter (u32, 0..7) and its direction
    /// (u32: 0 counts up, 1 counts down, anything else is frozen).</summary>
    const uint BlinkCount = 0x8006E5CC;
    const uint BlinkDir   = 0x8006E5D0;

    /// <summary>libetc `VSync`, GAME.EXE's copy -- the same address
    /// <see cref="FramePacing"/> counts frames on. `HookManager` runs every pre
    /// before the config's `replace`, so both coexist.</summary>
    const uint VSyncThunk = 0x8005FCC8;

    /// <summary>The vblank the six calls were each worth on hardware. Not read
    /// from `LibEtc`: its `_vcount` only advances *from* a VSync call, so waiting
    /// on it here would deadlock.</summary>
    const double VBlankMs = 1000.0 / 60.0;

    /// <summary>
    /// The fastest the blink's counter is allowed to advance, in ms per step.
    /// One vblank.
    ///
    /// **This one is a choice, and the constant is here so it is an easy one to
    /// change.** The frame head runs **twice** per menu-loop iteration (measured:
    /// 144 calls a second against a 144 fps render, so 72 iterations), and on
    /// hardware an iteration cost at least one `VSync`. If the console's menu held
    /// 60 fps it therefore stepped the blink twice a vblank, and the honest cap
    /// would be 8.3 ms rather than 16.7 -- a 0.13 s wink instead of 0.27 s. That
    /// rests on an assumption about a frame rate this port cannot observe, and the
    /// complaint being fixed is "too fast", so the slower of the two is the
    /// default. By eye is the only way to settle it.
    /// </summary>
    const double BlinkMs = VBlankMs;

    /// <summary>Spin the last stretch; `Thread.Sleep` granularity is a few ms.
    /// The same shape as <c>FramePacing.Floor</c>.</summary>
    const double SpinMs = 1.5;

    public static bool Enabled { get; private set; } = true;

    static bool _probe;

    static readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>True between <see cref="BeforeRepeat"/> and
    /// <see cref="AfterRepeat"/> -- the only window in which a VSync call is
    /// paced.</summary>
    static bool _inRepeat;

    /// <summary>When the next paced VSync call may return, in ms. Negative means
    /// the grid has not been seeded for this repeat yet.</summary>
    static double _due = -1.0;

    // For the probe: what the window actually cost, and how many calls it held.
    static double _enteredMs;
    static int _held;

    // The blink's own grid: when the counter may next advance, and the two words
    // as they stood before the frame head ran.
    static double _blinkDue = -1.0;
    static uint _blinkCount, _blinkDir;

    // For the probe: blink steps allowed against menu frames drawn, per second.
    static double _blinkWindowMs = -1.0;
    static int _blinkFrames, _blinkSteps;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.menurepeat",
        Name = "Menu repeat",
        Version = "1.0",
        Description = "Repeats a held menu direction at the console's rate, not the frame rate.",
    };

    public static void Configure(string? enabled, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
        if (!string.IsNullOrWhiteSpace(probe)) _probe = probe != "0";
    }

    /// <summary>
    /// Attach the hooks. Deferred to the first overlay load for the reason
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
        var self = typeof(MenuPacing);
        var before = self.GetMethod(nameof(BeforeRepeat), BindingFlags.Public | BindingFlags.Static)!;
        var after = self.GetMethod(nameof(AfterRepeat), BindingFlags.Public | BindingFlags.Static)!;
        var vsync = self.GetMethod(nameof(BeforeVSync), BindingFlags.Public | BindingFlags.Static)!;

        var gate = SymbolRegistry.Resolve("game", null, RepeatGate);
        if (gate == null)
        {
            Console.Error.WriteLine($"[KF2] menu pacing: no game function at 0x{RepeatGate:X8} -- " +
                                    "a held menu direction will keep repeating at the frame rate.");
            return;
        }

        var thunk = SymbolRegistry.Resolve("game", null, VSyncThunk);
        if (thunk == null)
        {
            Console.Error.WriteLine($"[KF2] menu pacing: no VSync at game/0x{VSyncThunk:X8} -- " +
                                    "nothing can be paced, so the repeat is left on the frame rate.");
            return;
        }

        int n = 0;
        if (HookManager.AddPre(_self, gate, before)) n++;
        if (HookManager.AddPost(_self, gate, after)) n++;
        if (HookManager.AddPre(_self, thunk, vsync)) n++;

        // The blink is a separate finding on a separate function, so a missing
        // frame head costs the blink and leaves the repeat working.
        var head = SymbolRegistry.Resolve("game", null, BlinkStepper);
        if (head == null)
            Console.Error.WriteLine($"[KF2] menu pacing: no game function at 0x{BlinkStepper:X8} -- " +
                                    "the cursor's blink will keep running at the render rate.");
        else
        {
            var blinkPre = self.GetMethod(nameof(BeforeFrameHead), BindingFlags.Public | BindingFlags.Static)!;
            var blinkPost = self.GetMethod(nameof(AfterFrameHead), BindingFlags.Public | BindingFlags.Static)!;
            if (HookManager.AddPre(_self, head, blinkPre)) n++;
            if (HookManager.AddPost(_self, head, blinkPost)) n++;
        }

        HookManager.Commit();

        Console.WriteLine($"[KF2] menu pacing: {(Enabled ? "on" : "off")}, {n} hook(s), " +
                          $"a held direction every {6.0 * VBlankMs:0.#} ms, " +
                          $"the blink capped at {1000.0 / BlinkMs:0.#} Hz");
    }

    /// <summary>
    /// Open the window. The grid is seeded on the first paced call rather than
    /// here, so a repeat that never spins -- the common case, nothing held --
    /// costs one bool.
    /// </summary>
    public static void BeforeRepeat(CpuContext c, IMemory m)
    {
        _inRepeat = true;
        _due = -1.0;
        _held = 0;
        _enteredMs = _clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>Close it. Runs even if some other pre on the same function ever
    /// skipped the body -- HookManager invokes every post regardless.</summary>
    public static void AfterRepeat(CpuContext c, IMemory m)
    {
        _inRepeat = false;

        if (_probe && _held > 0)
            Console.WriteLine($"[KF2] menu pacing: {_clock.Elapsed.TotalMilliseconds - _enteredMs:0.#} ms " +
                              $"over {_held} VSync call(s)");
    }

    /// <summary>
    /// A `VSync(0)` inside the repeat spin, held to the next vblank the way the
    /// hardware call was. Outside the spin this returns immediately, so the
    /// render rate everywhere else is untouched.
    /// </summary>
    public static void BeforeVSync(CpuContext c, IMemory m)
    {
        if (!_inRepeat) return;

        // Counted before the switch is consulted, so the probe measures the
        // unpaced spin too -- that comparison is the whole claim.
        _held++;
        if (!Enabled) return;

        double now = _clock.Elapsed.TotalMilliseconds;

        // Seed on the first call of this repeat, and re-seed if the host fell
        // more than a vblank behind -- a stall means the game stopped drawing
        // rather than ran late, and paying the debt back would spend the whole
        // repeat spinning.
        if (_due < 0.0 || _due < now - VBlankMs) _due = now;
        _due += VBlankMs;

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
    }

    /// <summary>
    /// The menu's frame head, about to run. Remember the blink's two words so the
    /// post can put them back; everything else the frame head does -- the buffer
    /// swap, the OT pointer, the `ClearOTag` -- has to happen on every frame and
    /// is untouched.
    /// </summary>
    public static void BeforeFrameHead(CpuContext c, IMemory m)
    {
        if (!Enabled && !_probe) return;
        _blinkCount = m.ReadU32(BlinkCount);
        _blinkDir = m.ReadU32(BlinkDir);
    }

    /// <summary>
    /// Undo the blink's step unless a 60 Hz slot has come round. Nothing sleeps:
    /// the frame was drawn at the render rate either way, and only the counter is
    /// held back, so this caps the blink rather than pacing the menu.
    /// </summary>
    public static void AfterFrameHead(CpuContext c, IMemory m)
    {
        if (!Enabled && !_probe) return;

        double now = _clock.Elapsed.TotalMilliseconds;
        bool step = now >= _blinkDue;

        if (step)
        {
            // A stale deadline means the menu was closed for a while rather than
            // that the host ran late, so restart the grid instead of letting the
            // backlog wave the cursor through several frames in a row.
            _blinkDue = (_blinkDue < 0.0 || now - _blinkDue > BlinkMs)
                ? now + BlinkMs : _blinkDue + BlinkMs;
        }
        else if (Enabled)
        {
            m.WriteU32(BlinkCount, _blinkCount);
            m.WriteU32(BlinkDir, _blinkDir);
        }

        if (!_probe) return;

        // Counted off the words themselves rather than off the grid, so the
        // switch-off comparison measures the game and not this class. Either word
        // moving is a step: at the top of the ramp the count clamps and only the
        // direction changes.
        _blinkFrames++;
        if (m.ReadU32(BlinkCount) != _blinkCount || m.ReadU32(BlinkDir) != _blinkDir) _blinkSteps++;
        if (_blinkWindowMs < 0.0) { _blinkWindowMs = now; return; }

        double elapsed = now - _blinkWindowMs;
        if (elapsed < 1000.0) return;

        Console.WriteLine($"[KF2] menu pacing: blink stepped {_blinkSteps * 1000.0 / elapsed:0.#} " +
                          $"times a second over {_blinkFrames * 1000.0 / elapsed:0.#} menu frames");
        _blinkWindowMs = now;
        _blinkFrames = 0;
        _blinkSteps = 0;
    }
}
