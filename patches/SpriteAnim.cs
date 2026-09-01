using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Step the billboard sprites' animation once a world tick instead of once a
/// rendered frame, so a torch flame burns at the speed it burned on hardware
/// whatever the port draws at.
///
///     KF2_SPRITEANIM=0        leave the animation on the render rate -- comparison only
///     KF2_SPRITEANIM_PROBE=1  frame changes a second, live slots, and walks a second
///
/// ## The defect
///
/// Reported from play: *"These flames still run really fast when you play at a
/// high framerate."* They are not the animated **textures** -- those are the eight
/// scrolling slots at `0x80192D58` that `func_8002DC78` re-uploads, and
/// <see cref="FramePacing"/> has gated that since it was found. These are the
/// **billboard sprites**, table 4 of the four the renderer walks: `0x80195174`,
/// 128 records of `0x18`, free when the `u16` at `+0x0` is `0xFFFF`. A flame, a
/// spark, a glow -- anything drawn as a camera-facing quad out of a strip of
/// authored frames.
///
/// `func_80035550` fills the table from the area's own `0x10`-byte definitions
/// when the area loads, and the four fields that matter are all in the first six
/// bytes:
///
/// | offset | what |
/// |---|---|
/// | `+0x0` | `u16` definition id; `0xFFFF` is a free slot |
/// | `+0x2` | the visibility mask the renderer ANDs `func_80032D78`'s answer with |
/// | `+0x3` | **how many frames** the strip has |
/// | `+0x4` | **the interval** -- step the frame every this many counts |
/// | `+0x5` | **the current frame**, seeded at load to `(rand * frames) &gt;&gt; 15` so two torches do not flicker in step |
/// | `+0x8` | the position `VECTOR`, three words |
///
/// The second loop of `func_800331B4` draws slot `i` by handing
/// `func_80032588` the frame index `u8[rec+0x5] + 0x80` on the stack, and then
/// steps it:
///
///     if (u8[rec+0x4] != 0 &amp;&amp; u32[0x80195170] % u8[rec+0x4] == 0)
///         if (++u8[rec+0x5] &gt;= u8[rec+0x3]) u8[rec+0x5] = 0;
///
/// and the **last act of `func_800331B4` is `++u32[0x80195170]`**. That word is
/// the whole clock of the system -- nothing else in `GAME.EXE` reads or writes it
/// (three sites: the init zero in `func_8002DF80`, the modulus here, the increment
/// here) -- and `func_800331B4` is called once, from stage 13. So the count is a
/// count of **rendered frames**, and every sprite in the game animates at the
/// render rate: at the 20 fps default it is right by coincidence, and at 144 it is
/// 7.2x too fast.
///
/// ## Why the stage gate cannot reach it
///
/// This is the class `docs/TODO.md` records as *"a counter stepped inside a
/// drawing function's own body"*, alongside stage 13's shake accumulator at
/// `0x8006E608` and `func_800331B4`'s own ambient-sound retrigger at `rec+0x40`.
/// `HookManager` detours whole functions, `func_800331B4` **is** the renderer's
/// world and object walk, and skipping it would draw nothing -- so the gate the
/// six ticked stages use is not available. What is available here and is not
/// available for the other two is that the counter is a **single word read by
/// everything that steps**: hold it and the whole system holds, with no field
/// enumerated and no per-slot rule.
///
/// So this is a **hold/restore pair** around `func_800331B4`. On a frame the world
/// did not advance on, the pre records `0x80195170` and the 128 frame bytes and
/// the post puts them back -- the sprite is *drawn* with the frame the tick left
/// it at, because the submit happens before the step, and the step is then undone.
/// On a tick frame nothing is saved and nothing is restored, so the game runs its
/// own code. At the tick rate that is every frame, which is why the port's own 20
/// fps default is bit-identical with this on or off.
///
/// **The frame identity is the guard, not the tick flag alone.** A modal loop's
/// redraw (<see cref="LoopPacing"/>) calls stage 13 again, and the transition fade
/// renders its own frames from inside stage 2; each of those is its own frame
/// boundary, so one walk per boundary is what actually holds, and keying on
/// <see cref="FramePacing.Frames"/> as well as <see cref="FramePacing.TickedThisFrame"/>
/// makes a second walk inside one frame a held one rather than a second step.
///
/// **And it needs the same watchdog the stage gate has, because a hold fails
/// *closed*.** That is not hypothetical: the first measured run of this patch lost
/// the frame boundary -- the failure `patches/recompone/0027` and
/// <c>FramePacing.FallbackTick</c> exist for -- and with `Frames` frozen the
/// identity test can never pass again, so every flame in the game stood still for
/// the rest of the session while the world played on at the right speed. A frozen
/// picture is a worse outcome than a fast one. So when `Frames` has not moved for
/// <see cref="BoundaryDeadMs"/> the step is taken from a wall-clock grid at
/// <see cref="FramePacing.LogicHz"/> instead, the same shape and for the same
/// reason as the stage gate's fallback.
///
/// **Restoring is per slot and checks the id**, so a table rewritten under us --
/// `func_80035550` on an area load -- cannot have a stale frame index written back
/// into a slot that now holds a different sprite. Nothing else is touched: the
/// position lanes are <see cref="ObjectSmoothing"/>'s, and the strip's length and
/// interval are the area's data.
///
/// **This is a rate, not a picture.** There is nothing to interpolate -- the
/// frames are authored cels, and a half-way flame does not exist -- so unlike the
/// smoothing patches there is no in-between to draw and no reason for it to be a
/// choice. On by default and with no settings page, in the class of frame pacing
/// and the menu repeat. See "The flames run at the render rate" in
/// docs/PATCHES_AND_MODS.md.
/// </summary>
public static class SpriteAnim
{
    /// <summary>Stage 13's world and object walk. Its second loop draws the
    /// billboards and its last instruction steps <see cref="Counter"/>.</summary>
    const uint Walk = 0x800331B4;

    /// <summary>The `u32` every sprite's interval divides. Incremented once per
    /// call of <see cref="Walk"/>, zeroed by `func_8002DF80` at init, and read
    /// nowhere else in `GAME.EXE`.</summary>
    const uint Counter = 0x80195170;

    /// <summary>The billboard table: 128 records of `0x18`. The same base
    /// <c>ObjectSmoothing.Tables</c> carries positions from.</summary>
    const uint Table = 0x80195174;
    const int Stride = 0x18, Count = 0x80;

    /// <summary>`u16` definition id, `0xFFFF` when the slot is free.</summary>
    const int IdOff = 0x0;

    /// <summary>The current cel, which is what runs too fast.</summary>
    const int FrameOff = 0x5;

    public static bool Enabled { get; private set; } = true;

    static bool _probe;

    static readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Whether this walk is allowed to step the animation. Decided in the
    /// pre and read in the post, so a hook that skipped the body still restores.</summary>
    static bool _step = true;

    /// <summary>The frame boundary the last stepping walk belonged to, so a second
    /// walk inside one frame is held rather than counted.</summary>
    static long _lastFrame = -1;

    /// <summary>No frame boundary for this long means there is not one, and the
    /// identity guard would hold the animation for ever. The same number
    /// <c>FramePacing.BoundaryDeadMs</c> uses, for the same reason.</summary>
    const double BoundaryDeadMs = 500.0;

    // The boundary watchdog: the last value of FramePacing.Frames and when it was
    // first seen. A grid of our own, in ms, for when it stops moving.
    static long _framesSeen = -1;
    static double _framesAtMs = -1.0, _fallbackNextMs = -1.0;
    static bool _fallbackSaid, _onFallback;

    static uint _savedCounter;
    static readonly byte[] _savedFrame = new byte[Count];
    static readonly ushort[] _savedId = new ushort[Count];

    // The probe's window: what the frame bytes were at the end of the last walk,
    // how many changed since, how many walks there were, and how many stepped.
    static readonly byte[] _probeLast = new byte[Count];
    static bool _probePrimed;
    static long _changes, _walks, _stepped, _live;
    static double _windowMs;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.spriteanim",
        Name = "Sprite animation pacing",
        Version = "1.0",
        Description = "Animates billboard sprites at the world's rate, not the frame rate.",
    };

    public static void Configure(string? enabled, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
        if (!string.IsNullOrWhiteSpace(probe)) _probe = probe != "0";
    }

    /// <summary>
    /// Attach the pair. Deferred to the first overlay load for the reason
    /// <see cref="FramePacing.Install"/> gives, and attached whether or not it is
    /// enabled, since hooks cannot be added once the overlay loads are past.
    /// </summary>
    public static void Install()
    {
        HookAttach.OnOverlayLoad("sprite anim", Attach);
    }

    /// <summary>Whether the pair is installed, so the switch cannot undo
    /// <see cref="Attach"/>'s safety disable: a post with no pre writes whatever
    /// the saved array happens to hold into the game's own table.</summary>
    static bool _paired;

    public static void SetEnabled(bool on) => Enabled = on && _paired;

    static bool Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("game", null, Walk);
        if (target == null)
        {
            Enabled = false;
            Console.Error.WriteLine($"[KF2] sprite anim: no game function at 0x{Walk:X8} -- " +
                                    "billboard sprites stay on the render rate.");
            return false;
        }

        var self = typeof(SpriteAnim);
        int n = 0;
        if (HookManager.AddPre(_self, target,
                self.GetMethod(nameof(Before), BindingFlags.Public | BindingFlags.Static)!)) n++;
        if (HookManager.AddPost(_self, target,
                self.GetMethod(nameof(After), BindingFlags.Public | BindingFlags.Static)!)) n++;

        HookManager.Commit();

        // Half a pair is worse than neither: a pre that saves and no post to put
        // anything back is a pure cost, and a post with no pre would write whatever
        // the array happened to hold into the game's table. A pair that was only
        // queued is a hold that never happens, reported as one that does.
        _paired = n == 2 && HookAttach.Installed(target);
        if (!_paired)
        {
            Enabled = false;
            Console.Error.WriteLine("[KF2] sprite anim: the pair did not attach" +
                                    (n == 2 ? " (queued, but the detour did not install)" : " (only half of it)") +
                                    "; the hold is disabled rather than left applied.");
            return false;
        }

        Console.WriteLine($"[KF2] sprite anim: {(Enabled ? "on" : "off")}, " +
                          $"hooked the world walk at 0x{Walk:X8}");
        return true;
    }

    /// <summary>
    /// Decide whether this walk steps the animation, and record what to put back
    /// if it does not.
    /// </summary>
    public static void Before(CpuContext c, IMemory m)
    {
        long frame = FramePacing.Frames;
        double now = _clock.Elapsed.TotalMilliseconds;
        if (frame != _framesSeen) { _framesSeen = frame; _framesAtMs = now; }

        _onFallback = _framesAtMs >= 0.0 && now - _framesAtMs > BoundaryDeadMs;

        if (!Enabled)
            _step = true;
        else if (_onFallback)
            _step = FallbackStep(now);
        else
        {
            _step = FramePacing.TickedThisFrame && frame != _lastFrame;
            if (_step) _lastFrame = frame;
        }

        if (_probe) { _walks++; if (_step) _stepped++; }

        if (_step) return;

        _savedCounter = m.ReadU32(Counter);
        for (int i = 0; i < Count; i++)
        {
            uint rec = (uint)(Table + i * Stride);
            _savedId[i] = m.ReadU16(rec + IdOff);
            _savedFrame[i] = m.ReadU8(rec + FrameOff);
        }
    }

    /// <summary>
    /// Step on an absolute wall-clock grid, for when there is no frame boundary to
    /// hang the decision on. Absolute so the rate averages to
    /// <see cref="FramePacing.LogicHz"/> rather than drifting, and restarted past
    /// four periods of debt because that means the game stopped drawing rather
    /// than ran late.
    /// </summary>
    static bool FallbackStep(double now)
    {
        if (!_fallbackSaid)
        {
            _fallbackSaid = true;
            Console.Error.WriteLine(
                "[KF2] sprite anim: no frame boundary has been reached, so the sprite clock is " +
                $"being stepped from the wall clock at {FramePacing.LogicHz:0.#} Hz instead. " +
                "See \"The flames run at the render rate\" in docs/PATCHES_AND_MODS.md.");
        }

        double period = 1000.0 / Math.Max(1.0, FramePacing.LogicHz);
        if (_fallbackNextMs < 0.0 || now - _fallbackNextMs > 4.0 * period) _fallbackNextMs = now;
        if (now < _fallbackNextMs) return false;
        _fallbackNextMs += period;
        return true;
    }

    /// <summary>Put the clock and the cels back on a frame the world did not
    /// advance on. Runs whatever the pre decided, because `HookManager` invokes
    /// every post regardless.</summary>
    public static void After(CpuContext c, IMemory m)
    {
        if (!_step)
        {
            m.WriteU32(Counter, _savedCounter);
            for (int i = 0; i < Count; i++)
            {
                if (_savedId[i] == 0xFFFF) continue;
                uint rec = (uint)(Table + i * Stride);

                // The slot has to still be the sprite it was. An area load rewrites
                // the whole table, and a stale cel index in a slot that now holds a
                // different strip would be a frame out of another animation.
                if (m.ReadU16(rec + IdOff) != _savedId[i]) continue;
                m.WriteU8(rec + FrameOff, _savedFrame[i]);
            }
        }

        if (_probe) Sample(m);
    }

    /// <summary>How many cels actually moved since the last walk, and how many
    /// slots are live. Read after the restore, so it measures what the game will
    /// draw next rather than what it did inside the call.</summary>
    static void Sample(IMemory m)
    {
        int live = 0;
        for (int i = 0; i < Count; i++)
        {
            uint rec = (uint)(Table + i * Stride);
            if (m.ReadU16(rec + IdOff) == 0xFFFF) { _probeLast[i] = 0; continue; }
            live++;
            byte f = m.ReadU8(rec + FrameOff);
            if (_probePrimed && f != _probeLast[i]) _changes++;
            _probeLast[i] = f;
        }
        _probePrimed = true;
        _live = live;

        double now = _clock.Elapsed.TotalMilliseconds;
        if (_windowMs <= 0.0) { _windowMs = now; return; }
        double elapsed = now - _windowMs;
        if (elapsed < 1000.0) return;

        Console.WriteLine($"[KF2] sprite anim: {_changes * 1000.0 / elapsed:0.#} cel change(s) a second " +
                          $"over {_live} live slot(s), {_walks * 1000.0 / elapsed:0.#} walk(s) a second, " +
                          $"{_stepped * 1000.0 / elapsed:0.#} stepped{(_onFallback ? ", off the wall clock" : "")}");
        _changes = _walks = _stepped = 0;
        _windowMs = now;
    }
}
