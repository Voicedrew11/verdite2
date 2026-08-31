using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Make a `VSync(0)` inside the disc wait cost a vblank again, so the loading
/// screen's walking figure steps at the rate the console stepped it at whatever
/// the port renders at.
///
///     KF2_LOADPACING=0        leave it on the host ceiling -- comparison only
///     KF2_LOADPACING_PROBE=1  the figure's steps a second, and what a load cost
///
/// ## The defect
///
/// A disc read is a **blocking wait** here, not a frame. `func_80017CA8` spins
/// `func_80017818(); if (flag) func_8001883C() x4;` until the CD job's queue
/// drains, and `func_800181B0` spins `func_8001883C()` while `CdReadSync` reports
/// sectors outstanding. `func_8001883C` is the loading screen's animator -- the
/// little figure that walks across the screen while an area loads. It draws
/// straight into VRAM, `ClearImage` (`0x800605D4`) over where the sprite was and
/// `MoveImage` (`0x8006069C`) where it now is, and it writes exactly three
/// globals, which is the whole animation:
///
/// | address | what |
/// |---|---|
/// | `0x8006E5A4` | the frame counter, `++` as the last act of every call; the `&amp; 3` and `&amp; 7` gates that pace the sequence's sub-steps read it |
/// | `0x8006E5A8` | the sequence's state |
/// | `0x8006E5AC` | the figure's **x**, `+= 3` a call, or 5 once the state is past the middle band, while it is under 288 |
///
/// It ends in `DrawSync(0); VSync(0)`, so on hardware **one call was one vblank**
/// and the figure walked three to five pixels every 1/60 s. Here `VSync` returns
/// as soon as `FrameClock`'s deliberately permissive host ceiling --
/// `max(60, TargetFps * 2)`, set by `FramePacing.ApplyHostCeiling` -- allows,
/// which above 60 fps is barely at all. Measured over the area load that follows
/// the title menu: **84 steps in 352 ms at `KF2_FPS=144` (238.6 a second) against
/// 84 in 1717 ms at the 20 fps default (48.9 a second)**, so the figure walks
/// nearly five times too fast at a high render rate, and the whole loading screen
/// flicks past in a third of a second.
///
/// This is <see cref="MenuPacing"/>'s bug in a third place and the shared cause is
/// worth stating plainly: **the game counts time in `VSync(0)` calls, and a
/// `VSync(0)` call is no longer a vblank.** Neither <see cref="FramePacing"/> nor
/// <see cref="LoopPacing"/> can see this one at all -- both key on the frame
/// boundary, a `DrawOTag` after a `VSync`, and this loop draws no ordering table
/// (measured: **zero** `DrawOTag` calls between entering the loader and the
/// transition fade at the end of it), so it never produces one.
///
/// ## Paced, not capped, and the choice is measured rather than assumed
///
/// The window between <see cref="BeforeWait"/> and <see cref="AfterWait"/> holds
/// every *blocking* `VSync` to the 60 Hz grid, exactly as
/// <see cref="MenuPacing.BeforeVSync"/> does for the cursor repeat's six calls.
/// That restores the identity the whole sequence is written against -- one call,
/// one vblank -- so the animator's three counters and the `&amp; 3` / `&amp; 7` gates
/// keyed on them are all right by construction, with nothing enumerated.
///
/// **Holding the three counters instead was written and measured first, and it is
/// the worse fix.** Capping them on a 60 Hz grid leaves the disc wait spinning at
/// the render rate, which keeps loads short -- but it cannot reproduce the rate
/// the port already has at its own default: the calls arrive in bursts of four,
/// so a cap refuses steps that were never too early on average, and 48.9 steps a
/// second at 20 fps came out **36-40** with the cap on. A fix for "too fast at 144"
/// that also slows the configuration the report says looks right is the wrong
/// trade.
///
/// **What pacing costs is stated rather than discovered later:** above 60 fps a
/// load takes as long as it already does at the 20 fps default -- 1.7 s rather
/// than 0.35 s for the load measured above. That is the console's own duration and
/// the port's own default behaviour, and it is the direct consequence of the
/// figure walking at the right speed; the alternative is a loading screen whose
/// length depends on the render rate, which is the thing this port exists to stop.
/// At or below 60 fps the ceiling is already 60 and **this class does nothing** --
/// which is where the defect does not exist either.
///
/// The window contains nothing of the *game's* that could be slowed: no gated
/// stage, no stage 13, and no rendered frame. It does slow something of the
/// **port's**, and that is worth saying rather than leaving to be chased: the
/// same "no `DrawOTag` in here" that makes this loop invisible to
/// <see cref="FramePacing"/> also means stretching the load stretches the
/// boundary gap with it. Measured at 144 fps, the widest gap at a wait's close is
/// **449.6 ms with this off and 1827.9 ms with it on**, against a
/// `FramePacing.BoundaryDeadMs` of 500 -- so pacing is what carries a load past
/// the watchdog for a *broken* boundary, and the unpaced case sat just under it.
/// The alarm does not currently print, because the fade that ends the load
/// redraws before the main loop reaches a gated stage, but that is this path's
/// ordering rather than a guarantee. <see cref="AfterWait"/> therefore excuses
/// the gap explicitly.
///
/// 60 Hz and not <see cref="FramePacing.LogicHz"/>
/// for the reason <see cref="MenuPacing.BlinkMs"/> gives -- this is one vblank of
/// an interface animation, and `KF2_TICKRATE` is a setting about the speed of the
/// *world*.
///
/// On by default and with no settings page, in the class of frame pacing and the
/// menu repeat rather than of dithering. See "The loading screen's walking figure"
/// in docs/PATCHES_AND_MODS.md.
/// </summary>
public static class LoadPacing
{
    /// <summary>The CD job's drain loop: `func_80017818()` then four calls to the
    /// animator, until the queue at `0x801B6F44` empties. Reached from the area
    /// loader `func_80024154`, the door warp `func_800474D0` and four others.</summary>
    const uint DrainWait = 0x80017CA8;

    /// <summary>The sector wait: `CdRead` then a spin on `CdReadSync` calling the
    /// animator once an iteration.</summary>
    const uint SectorWait = 0x800181B0;

    /// <summary>The loading screen's animator, hooked only so the probe can count
    /// the figure's steps; nothing here changes what it does.</summary>
    const uint Animator = 0x8001883C;

    /// <summary>libetc `VSync`, GAME.EXE's copy -- the same address
    /// <see cref="FramePacing"/> counts frames on and <see cref="MenuPacing"/>
    /// paces the cursor repeat on. `HookManager` runs every pre before the
    /// config's `replace`, so all three coexist.</summary>
    const uint VSyncThunk = 0x8005FCC8;

    /// <summary>The vblank one call was worth on hardware. Not read from
    /// `LibEtc`: its `_vcount` only advances *from* a VSync call, so waiting on it
    /// here would deadlock.</summary>
    const double VBlankMs = 1000.0 / 60.0;

    /// <summary>Spin the last stretch; `Thread.Sleep` granularity is a few ms.
    /// The same shape as <c>FramePacing.Floor</c> and <c>MenuPacing</c>.</summary>
    const double SpinMs = 1.5;

    /// <summary>Silence longer than this ends a load, for the probe's report.</summary>
    const double LoadGapMs = 250.0;

    /// <summary>A disc wait open longer than this is a leaked window rather than a
    /// slow read. Not the siblings' 500 ms: a *paced* load legitimately runs 1.7 s
    /// and a large area longer, so this is generous by an order of magnitude and
    /// still finite. See <see cref="DropLeakedWindow"/>.</summary>
    const double WaitDeadMs = 30000.0;

    public static bool Enabled { get; private set; } = true;

    static bool _probe;

    static readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>How deep we are inside a disc wait. A count rather than a flag
    /// because `func_800181B0` calls `func_80017CA8`, so the two windows nest.</summary>
    static int _depth;

    /// <summary>When the outermost wait opened, on <see cref="_clock"/>. Negative
    /// when no window is open; <see cref="WaitDeadMs"/> past it the window is
    /// treated as leaked.</summary>
    static double _windowOpenMs = -1.0;

    static bool _leakSaid;

    /// <summary>When the next paced VSync call may return, in ms. Negative means
    /// the grid has not been seeded for this wait yet.</summary>
    static double _due = -1.0;

    // For the probe: the figure's steps over one load, and when they happened.
    static double _windowMs = -1.0, _lastStepMs = -1.0;
    static int _steps, _held;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.loadpacing",
        Name = "Loading screen pacing",
        Version = "1.0",
        Description = "Walks the loading screen's figure at the console's rate, not the frame rate.",
    };

    public static void Configure(string? enabled, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
        if (!string.IsNullOrWhiteSpace(probe)) _probe = probe != "0";
    }

    /// <summary>
    /// Attach the hooks. Deferred to the first overlay load for the reason
    /// <see cref="FramePacing.Install"/> gives, and attached whether or not it is
    /// enabled, since the switch can be taken back and hooks cannot be added once
    /// the game is past its overlay loads.
    /// </summary>
    public static void Install()
    {
        HookAttach.OnOverlayLoad("load pacing", Attach);

        // The probe reports a load once the load has ended, and a load ends by the
        // calls stopping rather than by anything announcing it -- so the report is
        // driven off the vblank, which keeps running afterwards.
        Event.AddListener<VSyncEvent>(_ =>
        {
            if (!_probe || _steps == 0) return;
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now - _lastStepMs > LoadGapMs) ReportLoad(now);
        });
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(LoadPacing);
        var before = self.GetMethod(nameof(BeforeWait), BindingFlags.Public | BindingFlags.Static)!;
        var after = self.GetMethod(nameof(AfterWait), BindingFlags.Public | BindingFlags.Static)!;

        List<(uint Addr, MethodInfo Fn)> waits = [];
        foreach (uint addr in new[] { DrainWait, SectorWait })
        {
            if (_waitsHooked.Contains(addr)) continue;
            var fn = SymbolRegistry.Resolve("game", null, addr);
            if (fn == null)
            {
                Console.Error.WriteLine($"[KF2] load pacing: no game function at 0x{addr:X8} -- " +
                                        "that disc wait stays on the host ceiling.");
                continue;
            }
            HookManager.AddPre(_self, fn, before);
            HookManager.AddPost(_self, fn, after);
            waits.Add((addr, fn));
        }

        // Not a `return`: by this point up to four hooks are already registered on
        // the two disc waits, and bailing out left them sitting uncommitted in
        // HookManager's dictionary -- installed only if some other patch happened
        // to call Commit later in the same event -- and skipped the one line this
        // class prints, in the one case where it most needs to say what it did.
        MethodInfo? thunk = null;
        if (!_vsyncHooked)
        {
            thunk = SymbolRegistry.Resolve("game", null, VSyncThunk);
            if (thunk == null)
                Console.Error.WriteLine($"[KF2] load pacing: no VSync at game/0x{VSyncThunk:X8} -- " +
                                        "nothing can be paced, so the figure stays on the render rate.");
            else
                HookManager.AddPre(_self, thunk,
                    self.GetMethod(nameof(BeforeVSync), BindingFlags.Public | BindingFlags.Static)!);
        }

        MethodInfo? animator = null;
        if (!_animatorHooked)
        {
            animator = SymbolRegistry.Resolve("game", null, Animator);
            if (animator != null)
                HookManager.AddPre(_self, animator,
                    self.GetMethod(nameof(CountStep), BindingFlags.Public | BindingFlags.Static)!);
        }

        HookManager.Commit();

        foreach (var (addr, fn) in waits)
            if (HookAttach.Installed(fn)) _waitsHooked.Add(addr);
            else Console.Error.WriteLine($"[KF2] load pacing: the disc wait at 0x{addr:X8} " +
                                         "queued but did not install.");
        _vsyncHooked |= HookAttach.Installed(thunk);
        _animatorHooked |= HookAttach.Installed(animator);

        // The window is the two waits; the pacing inside it is the thunk. Either
        // one missing means a load is not paced, so both are named.
        Console.WriteLine($"[KF2] load pacing: {(Enabled ? "on" : "off")}, " +
                          $"{_waitsHooked.Count}/2 disc wait(s), " +
                          $"VSync {(_vsyncHooked ? $"held to {1000.0 / VBlankMs:0.#} Hz" : "NOT held")}" +
                          $"{(_animatorHooked ? ", counting steps" : "")}");

        // The animator hook is KF2_LOADPACING_PROBE's step counter and nothing
        // else -- CountStep returns immediately unless the probe is on -- so it is
        // reported but deliberately not part of the verdict. Letting it decide
        // would spend all three attach passes and print the give-up line over a
        // hook that paces nothing.
        return _waitsHooked.Count == 2 && _vsyncHooked;
    }

    // What is hooked already, so a retry adds only what is missing.
    static readonly HashSet<uint> _waitsHooked = [];
    static bool _vsyncHooked, _animatorHooked;

    /// <summary>
    /// Open the window. The grid is seeded on the first paced call rather than
    /// here, so a wait that returns without spinning costs an increment.
    /// </summary>
    public static void BeforeWait(CpuContext c, IMemory m)
    {
        if (_depth++ == 0)
        {
            _due = -1.0;
            _windowOpenMs = _clock.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>Close it. Runs even if some other pre on the same function ever
    /// skipped the body -- HookManager invokes every post regardless.</summary>
    public static void AfterWait(CpuContext c, IMemory m)
    {
        if (_depth > 0) _depth--;
        if (_depth != 0) return;

        _windowOpenMs = -1.0;

        // The window this class just closed drew nothing -- that is the measured
        // fact the whole patch rests on -- and pacing stretches the boundary gap
        // with it, from a widest 449.6 ms to a widest 1827.9 ms at 144 fps, past
        // `FramePacing.BoundaryDeadMs`. A gated stage reached in that state takes
        // the fallback path and prints the alarm for a *broken* boundary. The
        // boundary hook is fine here; it had nothing to do. Excusing it costs one
        // watchdog period, so a boundary that really is gone still trips on the
        // frames that follow.
        FramePacing.ExcuseBoundaryGap();
    }

    /// <summary>
    /// Stand the window down after <see cref="WaitDeadMs"/>. A load that long is
    /// an unbalanced <see cref="BeforeWait"/> rather than a slow disc, and leaving
    /// `_depth` up paces the rest of the session to 60 Hz with nothing said.
    /// </summary>
    static void DropLeakedWindow()
    {
        _depth = 0;
        _windowOpenMs = -1.0;
        _due = -1.0;

        if (_leakSaid) return;
        _leakSaid = true;
        Console.Error.WriteLine(
            $"[KF2] load pacing: a disc wait has been open for over {WaitDeadMs / 1000.0:0.#} s, so " +
            "its post never ran -- treating the window as closed rather than pacing every VSync in " +
            "the game to 60 Hz. See \"The loading screen's walking figure\" in " +
            "docs/PATCHES_AND_MODS.md.");
    }

    /// <summary>
    /// A `VSync(0)` inside a disc wait, held to the next vblank the way the
    /// hardware call was. Outside one this returns immediately, so the render rate
    /// everywhere else is untouched.
    /// </summary>
    public static void BeforeVSync(CpuContext c, IMemory m)
    {
        // **Only a mode that blocked on hardware is charged a vblank.** The window
        // here is a whole disc wait rather than a known handful of calls, so a
        // libcd poll or a timeout check inside it can reach this with the standard
        // `VSync(-1)` counter-read idiom -- and `LibEtc.VSync` returns from that
        // and from mode 1 without presenting or advancing anything, exactly as the
        // hardware call did. Charging them a vblank each would serialise N queries
        // into N vblanks against an unbounded retry loop (`func_800181B0`'s spin on
        // `CdReadSync`). The animator only ever passes 0, so this is a guard rather
        // than an observed fault.
        int mode = (int)c.A0;
        if (mode < 0 || mode == 1) return;

        if (_depth == 0) return;

        double now = _clock.Elapsed.TotalMilliseconds;

        // The window's own watchdog. `HookManager` runs a post only after the
        // recompiled body returns, so any escape that is not a normal return -- an
        // `unmapped call`, a throw from a nested hook -- skips AfterWait and leaves
        // `_depth` above zero for the session, which paces *every* VSync in the
        // game to 60 Hz and quietly turns KF2_FPS=144 into KF2_FPS=60. A hold fails
        // closed, so it needs the watchdog the stage gate and the sprite counter
        // both carry; the threshold is a load's own duration with room to spare
        // rather than their 500 ms, since a paced load legitimately runs seconds.
        if (_windowOpenMs >= 0.0 && now - _windowOpenMs > WaitDeadMs) { DropLeakedWindow(); return; }

        // Counted before the switch is consulted, so the probe measures the
        // unpaced spin too -- that comparison is the whole claim, and this is
        // `MenuPacing.BeforeVSync`'s order for the same reason. It counts the
        // blocking calls in the window, not the ones that waited: a call arriving
        // after its slot has already passed is not held and is still one of the
        // calls the load is made of.
        _held++;
        if (!Enabled) return;

        // Seed on the first call of this wait, and re-seed if the host fell more
        // than a vblank behind -- a stall means the game stopped drawing rather
        // than ran late, and paying the debt back would spend the rest of the load
        // spinning flat out.
        if (_due < 0.0 || _due < now - VBlankMs) _due = now;
        _due += VBlankMs;

        if (now >= _due) return;

        double sleepUntil = _due - SpinMs;
        if (now < sleepUntil)
        {
            int ms = (int)(sleepUntil - now);
            if (ms > 0) Thread.Sleep(ms);
        }
        while (_clock.Elapsed.TotalMilliseconds < _due) Thread.SpinWait(48);
    }

    /// <summary>Probe only: one step of the figure. Counted here rather than off
    /// the grid, so the switch-off comparison measures the game and not this
    /// class.</summary>
    public static void CountStep(CpuContext c, IMemory m)
    {
        if (!_probe) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_lastStepMs >= 0.0 && now - _lastStepMs > LoadGapMs) ReportLoad(now);
        if (_windowMs < 0.0) _windowMs = now;
        _lastStepMs = now;
        _steps++;
    }

    /// <summary>One load's worth of animation, printed once the load has ended.</summary>
    static void ReportLoad(double now)
    {
        double elapsed = _lastStepMs - _windowMs;
        if (_steps > 0 && elapsed > 0.0)
            Console.WriteLine($"[KF2] load pacing: the figure took {_steps} step(s) in {elapsed:0.#} ms " +
                              $"-- {_steps * 1000.0 / elapsed:0.#} a second, over {_held} blocking " +
                              "VSync call(s) in the wait");
        _windowMs = now;
        _steps = 0;
        _held = 0;
    }
}
