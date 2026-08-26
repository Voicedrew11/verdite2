using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Carries the view between logic ticks, so that a picture drawn more often than
/// the world advances actually looks like it.
///
///     KF2_SMOOTH=1        on; without it the camera moves at the tick rate only
///     KF2_SMOOTH_POS=1    interpolate the position too, not just the angles
///     KF2_SMOOTH_PROBE=1  what is being carried, per second
///
/// Both are settings, under Video beside the frame rate. **Both default to off**:
/// until FramePacing's frame boundary was fixed this never ran at any rate --
/// LogicPhase was pinned to 0 and the probe read `0 of N frames carried (phase
/// idle)` -- so the picture it makes has never been seen.
///
/// This is the other half of <see cref="FramePacing"/> rather than an option
/// beside it. Above the tick rate the port ticks the game's own stages on their
/// own clock and draws in between, which keeps every counter in the game right --
/// and leaves the camera standing still on the frames that did not tick. At 120
/// fps against a 20 Hz world that is the same motion presented six times, which is
/// not merely no better than 20 fps, it is worse: the picture updates, the view
/// does not, and the eye reads the mismatch as stutter. The default tick rate
/// moving from 30 to 20 makes this **more** load-bearing, not less: a tick is now
/// 50 ms rather than 33, so the camera stands still for half again as long.
///
/// ## Why the hook point is exactly one function
///
/// Main-loop **stage 8, `func_80025A1C`**, is the whole of "build the render
/// camera from the player state", and it is nothing else. Twenty-eight
/// instructions, no branches:
///
///     a0[0] = 0x801994EC (X)        a1[0] = 0x80199504 (composed pitch)
///     a0[8] = 0x801994F4 (Z)        a1[2] = 0x80199506 (composed yaw)
///     a0[4] = 0x801994F0 (Y) + s16 0x80199548 + s16 0x8019954C - 0x640
///                                   a1[4] = 0x80199508 (composed roll)
///
/// It runs after stage 3 and before stage 13, and the two stack blocks it fills
/// are what the renderer -- and stage 9, the 3D sound listener -- are handed. So
/// it is the *only* thing between the authoritative player state and the picture.
/// A pre-hook that writes those globals and a post-hook that puts them back means
/// the carried view exists for exactly one function call: it cannot accumulate,
/// it cannot reach the collision code, and it cannot reach a save. That isolation
/// is the whole reason this is safe, and it is why the hook is not on stage 2 or
/// stage 13 -- both of which do other things, repeatedly.
///
/// ## Interpolate, not extrapolate
///
/// **This carried the view *forward* at first -- current angle + this tick's
/// velocity x frac -- and that is what made it bounce.** Forward extrapolation is
/// smooth only while the velocity holds. King's Field damps a turn and stops dead
/// at a wall, so the next tick's real angle is routinely *less* than the one that
/// was predicted, and the view snapped back to it -- "the camera bounces back to a
/// position it would have travelled in 20 Hz", every time a turn eased off.
///
/// So it interpolates instead: it keeps the view the game produced at the previous
/// tick and at the current one, and draws `lerp(prev, cur, frac)`. That can never
/// reach a position the game did not produce, so nothing overshoots and nothing
/// snaps. The cost is a tick of latency -- the picture trails input by up to 50 ms
/// at 20 Hz -- which for a game whose input is already sampled at the tick rate is
/// a delay of the *display*, not of the response, and is the price the smoothness
/// is worth.
///
/// What is carried:
/// * **Yaw and pitch**, the composed view angles at `0x80199504`/`0x80199506` --
///   the values stage 3 (`func_80028DB8`) has already folded this tick's turn into,
///   sampled fresh on every frame the world advanced. Yaw is 12-bit and wraps, so
///   the interpolation takes the shortest way round.
/// * **Position**, optionally, from `0x801994EC`/`F0`/`F4` -- the player position
///   `func_80028080` writes after the collision test, so walking a wall interpolates
///   between two positions that already slid along it, with no overshoot into it. A
///   step past <see cref="TeleportUnits"/> on an axis is a warp rather than a walk
///   and is left alone, the way <see cref="ObjectSmoothing"/> guards a placement.
///
/// Both agree on time with <see cref="ObjectSmoothing"/>, which interpolates too:
/// both draw the world at `t - 1 + frac`, so nothing slides against anything else.
///
/// <see cref="FramePacing.LogicPhase"/> is the fraction used, and it is continuous
/// across a tick boundary -- it does not reset to zero on the frames where the
/// world did advance -- so the view does not jump when the world catches up.
/// </summary>
public static class FrameSmoothing
{
    // Stage 8: the render camera copy. The one hook.
    const uint CameraCopy = 0x80025A1C;

    // The composed view the renderer is handed: base + the three delta vectors.
    const uint ComposedPitch = 0x80199504;   // u16, wrapped to 12 bits
    const uint ComposedYaw   = 0x80199506;

    // The position triple, read as signed; a normal Y is negative.
    const uint PosX = 0x801994EC;            // u32
    const uint PosY = 0x801994F0;
    const uint PosZ = 0x801994F4;

    /// <summary>Units on one axis between two ticks past which the position is a
    /// warp rather than a walk, and is left where the game put it -- lerping across
    /// it would sweep the camera the width of the map. The player, the fastest
    /// thing in the game, covers about 45 units a tick, so 1024 clears any walk by
    /// twenty times. Matches <see cref="ObjectSmoothing"/>'s placement guard.</summary>
    const int TeleportUnits = 1024;

    public const string OnKey  = "kf2.smoothing.on";
    public const string PosKey = "kf2.smoothing.pos";

    /// <summary>Carry the view angles between ticks. **Off by default**, though
    /// without it a rate above the tick rate buys a faster picture of a camera not
    /// moving. It was on until the frame boundary in <see cref="FramePacing"/> was
    /// fixed, and until then it never ran at all -- LogicPhase was pinned to 0 and
    /// the probe read `0 of 240 frames carried (phase idle)` at every rate. So its
    /// picture has still never been looked at, which is the sub-pixel reason for a
    /// default of off.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>Carry the position too. Off by default; the mechanism is measured
    /// and the picture is not.</summary>
    public static bool Position { get; private set; }

    static bool _onFromEnv, _posFromEnv;

    // Last tick's and this tick's composed view, sampled on a frame the world
    // advanced on. `_cur` is what the game most recently produced, `_prev` what it
    // produced the tick before; the frame is drawn at lerp(prev, cur, phase).
    static ushort _prevYaw, _curYaw, _prevPitch, _curPitch;
    static int _prevX, _curX, _prevY, _curY, _prevZ, _curZ;

    // False until a first sample exists, then until a second does. Carrying needs
    // both prev and cur to be real, exactly as ObjectSmoothing's `_live` does.
    static bool _primed, _carriable;

    // What the pre-hook overwrote, and whether it overwrote anything. Restored by
    // the post-hook; there is exactly one call in flight at a time, on one thread.
    static bool _applied;
    static ushort _pitch, _yaw;
    static uint _x, _y, _z;

    // ---- the probe ------------------------------------------------------------

    static bool _probe;
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _reportedAt;
    static long _carried, _skipped;

    // Why a frame was skipped. With interpolation the only reason left is that the
    // view did not change between the two ticks -- the player was standing still --
    // which reads very differently from "the logic clock is broken".
    static long _skipStill;
    static double _yawSum, _pitchSum, _posSum, _fracSum;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.framesmoothing",
        Name = "Frame smoothing",
        Version = "1.0",
        Description = "Carries the view between the game's logic ticks.",
    };

    public static void Configure(string? on, string? position, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on)) { Enabled = on != "0"; _onFromEnv = true; }
        if (!string.IsNullOrWhiteSpace(position)) { Position = position != "0"; _posFromEnv = true; }
        _probe = probe == "1";
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            if (!_onFromEnv) Enabled = view.GetBool(OnKey, Enabled);
            if (!_posFromEnv) Position = view.GetBool(PosKey, Position);
        });

        // An area or executable swap rebuilds the player state, so the previous
        // sample describes a position and heading that no longer mean anything;
        // start priming again rather than lerp across the discontinuity.
        Event.AddListener<OverlayLoadedEvent>(_ => { _primed = false; _carriable = false; });

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    public static void SetEnabled(bool on) => Enabled = on;
    public static void SetPosition(bool on) => Position = on;

    static void Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("game", null, CameraCopy);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] smoothing: no game function at 0x{CameraCopy:X8}; " +
                                    "the view will step at the logic rate above it.");
            return;
        }

        var self = typeof(FrameSmoothing);
        int n = 0;
        if (HookManager.AddPre(_self, target,
                self.GetMethod(nameof(Before), BindingFlags.Public | BindingFlags.Static)!)) n++;
        if (HookManager.AddPost(_self, target,
                self.GetMethod(nameof(After), BindingFlags.Public | BindingFlags.Static)!)) n++;

        // Committed here rather than left to FramePacing, which listens for the same
        // event and therefore attaches *first* -- Program.cs installs it first, and
        // listeners run in registration order, so its Commit has already happened by
        // the time this runs. Commit is idempotent; every other patch calls it too.
        HookManager.Commit();

        if (n < 2)
            Console.Error.WriteLine("[KF2] smoothing: only half the pair attached; " +
                                    "the interpolation is disabled rather than left applied.");
        else
            Console.WriteLine($"[KF2] smoothing: {(Enabled ? "on" : "off")}" +
                              $"{(Position ? ", carrying position" : "")}, hooked stage 8 at 0x{CameraCopy:X8}");

        if (n < 2) Enabled = false;
    }

    /// <summary>
    /// Draw the view at lerp(prev tick, this tick, phase), just before the one
    /// function that reads it. On a frame the world advanced on, this tick's value
    /// is re-sampled first; on every other frame the two samples stand and only the
    /// phase moves.
    /// </summary>
    public static void Before(CpuContext c, IMemory m)
    {
        _applied = false;
        if (!Enabled || !FramePacing.Gating) return;

        // Roll forward on a tick, then re-read the values the game just produced.
        // Stage 3 (func_80028DB8 for the angles, func_80028080 for the position)
        // runs before stage 8, so on a tick frame these already hold the new tick.
        if (FramePacing.TickedThisFrame)
        {
            _prevYaw = _curYaw; _prevPitch = _curPitch;
            _prevX = _curX; _prevY = _curY; _prevZ = _curZ;

            _curYaw = m.ReadU16(ComposedYaw);
            _curPitch = m.ReadU16(ComposedPitch);
            _curX = (int)m.ReadU32(PosX);
            _curY = (int)m.ReadU32(PosY);
            _curZ = (int)m.ReadU32(PosZ);

            _carriable = _primed;   // both prev and cur are real only after two samples
            _primed = true;
        }

        // Not gated on a small phase: interpolation must overwrite the live globals
        // even at frac ~= 0, because on a tick frame they hold `cur` (the new tick)
        // and the frame is meant to draw `prev`. Skipping there would leave the new
        // value on screen and put a snap back the other way.
        if (!_carriable) return;

        double frac = FramePacing.LogicPhase;

        int yawD = Delta12(_prevYaw, _curYaw);
        int pitchD = S12(_curPitch) - S12(_prevPitch);

        int dx = _curX - _prevX, dy = _curY - _prevY, dz = _curZ - _prevZ;
        bool posLive = Position &&
                       Math.Abs(dx) <= TeleportUnits &&
                       Math.Abs(dy) <= TeleportUnits &&
                       Math.Abs(dz) <= TeleportUnits &&
                       (dx != 0 || dy != 0 || dz != 0);

        if (yawD == 0 && pitchD == 0 && !posLive)
        {
            if (_probe) { _skipped++; _skipStill++; }
            return;
        }

        _pitch = m.ReadU16(ComposedPitch);
        _yaw   = m.ReadU16(ComposedYaw);
        _x = m.ReadU32(PosX);
        _y = m.ReadU32(PosY);
        _z = m.ReadU32(PosZ);
        _applied = true;

        int yawStep = (int)Math.Round(yawD * frac);
        int pitchStep = (int)Math.Round(pitchD * frac);

        m.WriteU16(ComposedYaw, (ushort)(((_prevYaw & 0xFFF) + yawStep) & 0xFFF));
        m.WriteU16(ComposedPitch, (ushort)((S12(_prevPitch) + pitchStep) & 0xFFF));

        if (posLive)
        {
            m.WriteU32(PosX, (uint)(_prevX + (int)Math.Round(dx * frac)));
            m.WriteU32(PosY, (uint)(_prevY + (int)Math.Round(dy * frac)));
            m.WriteU32(PosZ, (uint)(_prevZ + (int)Math.Round(dz * frac)));
        }

        if (_probe)
        {
            _carried++;
            _yawSum += Math.Abs(yawStep);
            _pitchSum += Math.Abs(pitchStep);
            if (posLive) _posSum += Math.Abs(dx * frac) + Math.Abs(dz * frac);
            _fracSum += frac;
        }
    }

    /// <summary>
    /// Put the state back the moment stage 8 has read it. Everything downstream --
    /// stage 13, the next tick's collision, a save -- then sees exactly what the
    /// game wrote.
    /// </summary>
    public static void After(CpuContext c, IMemory m)
    {
        if (_applied)
        {
            m.WriteU16(ComposedPitch, _pitch);
            m.WriteU16(ComposedYaw, _yaw);
            m.WriteU32(PosX, _x);
            m.WriteU32(PosY, _y);
            m.WriteU32(PosZ, _z);
            _applied = false;
        }

        if (_probe) Report();
    }

    /// <summary>Read a 12-bit angle as signed, in [-2048, 2047]. Pitch is a small
    /// signed angle (±0x2BC) stored this way; yaw uses the whole range.</summary>
    static int S12(int raw)
    {
        int v = raw & 0xFFF;
        return v >= 0x800 ? v - 0x1000 : v;
    }

    /// <summary>
    /// The shortest signed distance from one 12-bit angle to another, in
    /// [-2048, 2048]. Interpolating a yaw with this instead of the raw difference is
    /// what stops a turn through the 0/4095 wrap from spinning the long way round.
    /// </summary>
    static int Delta12(ushort from, ushort to)
    {
        int d = (to & 0xFFF) - (from & 0xFFF);
        if (d > 2048) d -= 4096;
        else if (d < -2048) d += 4096;
        return d;
    }

    static void Report()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _reportedAt < 2000.0) return;
        _reportedAt = now;

        long total = _carried + _skipped;
        if (total == 0) return;

        if (_carried == 0)
            Console.WriteLine($"[KF2] smoothing: 0 of {total} frames carried -- " +
                              $"{_skipStill} with nothing moving" +
                              $"{(FramePacing.Gating ? "" : ", not gating")}");
        else
            Console.WriteLine($"[KF2] smoothing: {_carried}/{total} frames carried, " +
                              $"mean phase {_fracSum / _carried:0.00} tick, " +
                              $"yaw {_yawSum / _carried:0.0} u, pitch {_pitchSum / _carried:0.0} u, " +
                              $"pos {_posSum / _carried:0.0} u");

        _carried = _skipped = _skipStill = 0;
        _yawSum = _pitchSum = _posSum = _fracSum = 0.0;
    }
}
