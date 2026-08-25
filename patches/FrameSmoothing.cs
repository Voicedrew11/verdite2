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
///     KF2_SMOOTH_POS=1    extrapolate the position too, not just the angles
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
/// A pre-hook that nudges those globals and a post-hook that puts them back means
/// the extrapolation exists for exactly one function call: it cannot accumulate,
/// it cannot reach the collision code, and it cannot reach a save. That isolation
/// is the whole reason this is safe, and it is why the hook is not on stage 2 or
/// stage 13 -- both of which do other things, repeatedly.
///
/// ## What is carried, and what that costs
///
/// * **Yaw and pitch**, from the velocity words stage 3 wrote this tick
///   (`0x80199544` and `0x80199546` -- the same two <c>patches/Analog.cs</c>
///   drives). The game adds the whole velocity once per tick; this adds the
///   fraction of it the frame is worth. Turning is where the judder is most
///   visible in a first-person game and where extrapolation cannot be wrong:
///   an angle has no collision.
/// * **Position**, optionally, from `0x801994FC`/`FE`/`0x80199500` -- the s16
///   triple `func_80028080` writes with the movement it *actually applied* this
///   tick, wall slide and all, and which `func_800290D4` zeroes when the player is
///   not moving. So this is the game's own answer to "how far did you move", not
///   a re-derivation of it from the yaw, and it needs no trigonometry and no guess
///   about which way strafe points.
///
///   It is **off by default** all the same. The delta is last tick's, so it is an
///   extrapolation rather than an interpolation -- no added latency, but a player
///   who walks into a wall keeps being carried into it for the rest of the tick
///   before snapping back. Whether that reads as smoothness or as jitter is a
///   question for eyes, not for a counter.
///
/// <see cref="FramePacing.LogicPhase"/> is the fraction used, and it is continuous
/// across a tick boundary -- it does not reset to zero on the frames where the
/// world did advance -- so the camera does not jump when the world catches up.
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

    // This tick's view velocities, added to the angles once by func_80028DB8.
    const uint TurnVel  = 0x80199544;        // s16
    const uint PitchVel = 0x80199546;        // s16

    // This tick's applied position delta, written by func_80028080 after the
    // collision test and zeroed by func_800290D4 when standing still.
    const uint MoveDX = 0x801994FC;          // s16
    const uint MoveDY = 0x801994FE;
    const uint MoveDZ = 0x80199500;

    /// <summary>The game's own pitch limit, held by func_80015364.</summary>
    const int PitchLimit = 0x2BC;

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

    // Why a frame was skipped. "0 of N carried (phase idle)" used to be printed
    // whichever it was, which reads as "the logic clock is broken" when in fact
    // the player was standing still and there was nothing to carry.
    static long _skipPhase, _skipStill;
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
                                    "the extrapolation is disabled rather than left applied.");
        else
            Console.WriteLine($"[KF2] smoothing: {(Enabled ? "on" : "off")}" +
                              $"{(Position ? ", carrying position" : "")}, hooked stage 8 at 0x{CameraCopy:X8}");

        if (n < 2) Enabled = false;
    }

    /// <summary>
    /// Nudge the view forward by the fraction of a logic tick this frame stands at,
    /// just before the one function that reads it.
    /// </summary>
    public static void Before(CpuContext c, IMemory m)
    {
        _applied = false;
        if (!Enabled || !FramePacing.Gating) return;

        double frac = FramePacing.LogicPhase;
        if (frac <= 0.0005) { if (_probe) { _skipped++; _skipPhase++; } return; }

        int turn = (short)m.ReadU16(TurnVel);
        int pitchVel = (short)m.ReadU16(PitchVel);

        int dx = 0, dy = 0, dz = 0;
        if (Position)
        {
            dx = (short)m.ReadU16(MoveDX);
            dy = (short)m.ReadU16(MoveDY);
            dz = (short)m.ReadU16(MoveDZ);
        }

        if (turn == 0 && pitchVel == 0 && dx == 0 && dy == 0 && dz == 0)
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

        int yawStep = Step(turn * frac);
        int pitchStep = Step(pitchVel * frac);

        if (yawStep != 0) m.WriteU16(ComposedYaw, (ushort)((_yaw + yawStep) & 0xFFF));
        if (pitchStep != 0) m.WriteU16(ComposedPitch, Pitch(_pitch, pitchStep));

        if (Position)
        {
            m.WriteU32(PosX, (uint)((int)_x + Step(dx * frac)));
            m.WriteU32(PosY, (uint)((int)_y + Step(dy * frac)));
            m.WriteU32(PosZ, (uint)((int)_z + Step(dz * frac)));
        }

        if (_probe)
        {
            _carried++;
            _yawSum += Math.Abs(yawStep);
            _pitchSum += Math.Abs(pitchStep);
            _posSum += Math.Abs(Step(dx * frac)) + Math.Abs(Step(dz * frac));
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

    /// <summary>
    /// Round away from zero, so that a step smaller than half a unit still moves
    /// the view. At 120 fps a quarter of a slow turn is well under one unit, and
    /// rounding it to nothing would put the judder straight back.
    /// </summary>
    static int Step(double v)
    {
        if (v == 0.0) return 0;
        int n = (int)Math.Round(Math.Abs(v), MidpointRounding.AwayFromZero);
        if (n == 0) n = 1;
        return v < 0.0 ? -n : n;
    }

    /// <summary>
    /// Add to a 12-bit wrapped angle, holding the game's own ±0x2BC pitch limit --
    /// but only when the angle is inside it already, so that a cutscene or the
    /// death camera driving the view further is not clamped by the smoothing.
    /// </summary>
    static ushort Pitch(ushort raw, int step)
    {
        int v = raw & 0xFFF;
        int signed = v >= 0x800 ? v - 0x1000 : v;
        int next = signed + step;
        if (signed >= -PitchLimit && signed <= PitchLimit)
            next = Math.Clamp(next, -PitchLimit, PitchLimit);
        return (ushort)(next & 0xFFF);
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
                              $"{_skipPhase} on the tick, {_skipStill} with nothing moving" +
                              $"{(FramePacing.Gating ? "" : ", not gating")}");
        else
            Console.WriteLine($"[KF2] smoothing: {_carried}/{total} frames carried, " +
                              $"mean phase {_fracSum / _carried:0.00} tick, " +
                              $"yaw {_yawSum / _carried:0.0} u, pitch {_pitchSum / _carried:0.0} u, " +
                              $"pos {_posSum / _carried:0.0} u");

        _carried = _skipped = _skipPhase = _skipStill = 0;
        _yawSum = _pitchSum = _posSum = _fracSum = 0.0;
    }
}
