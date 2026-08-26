using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Hold the menus' cursor auto-repeat to the rate the console had, at any frame
/// rate.
///
///     KF2_MENUREPEAT=0        leave it on the frame clock -- comparison only
///     KF2_MENUREPEAT_PROBE=1  what each repeat actually cost, in ms
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
/// On by default and with no settings page: this is a correctness fix like frame
/// pacing rather than a taste like dithering, and the console fixed the number at
/// one value. See "The menu's cursor repeat" in docs/PATCHES_AND_MODS.md.
/// </summary>
public static class MenuRepeat
{
    /// <summary>The auto-repeat delay every list in the game calls first: spin on
    /// up to six `VSync(0)` calls while a button is still held, then let the
    /// caller step the cursor once.</summary>
    const uint RepeatGate = 0x80022E90;

    /// <summary>libetc `VSync`, GAME.EXE's copy -- the same address
    /// <see cref="FramePacing"/> counts frames on. `HookManager` runs every pre
    /// before the config's `replace`, so both coexist.</summary>
    const uint VSyncThunk = 0x8005FCC8;

    /// <summary>The vblank the six calls were each worth on hardware. Not read
    /// from `LibEtc`: its `_vcount` only advances *from* a VSync call, so waiting
    /// on it here would deadlock.</summary>
    const double VBlankMs = 1000.0 / 60.0;

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
        var self = typeof(MenuRepeat);
        var before = self.GetMethod(nameof(BeforeRepeat), BindingFlags.Public | BindingFlags.Static)!;
        var after = self.GetMethod(nameof(AfterRepeat), BindingFlags.Public | BindingFlags.Static)!;
        var vsync = self.GetMethod(nameof(BeforeVSync), BindingFlags.Public | BindingFlags.Static)!;

        var gate = SymbolRegistry.Resolve("game", null, RepeatGate);
        if (gate == null)
        {
            Console.Error.WriteLine($"[KF2] menu repeat: no game function at 0x{RepeatGate:X8} -- " +
                                    "a held menu direction will keep repeating at the frame rate.");
            return;
        }

        var thunk = SymbolRegistry.Resolve("game", null, VSyncThunk);
        if (thunk == null)
        {
            Console.Error.WriteLine($"[KF2] menu repeat: no VSync at game/0x{VSyncThunk:X8} -- " +
                                    "nothing can be paced, so the repeat is left on the frame rate.");
            return;
        }

        int n = 0;
        if (HookManager.AddPre(_self, gate, before)) n++;
        if (HookManager.AddPost(_self, gate, after)) n++;
        if (HookManager.AddPre(_self, thunk, vsync)) n++;
        HookManager.Commit();

        Console.WriteLine($"[KF2] menu repeat: {(Enabled ? "on" : "off")}, {n} hook(s), " +
                          $"a held direction every {6.0 * VBlankMs:0.#} ms");
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
            Console.WriteLine($"[KF2] menu repeat: {_clock.Elapsed.TotalMilliseconds - _enteredMs:0.#} ms " +
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
}
