using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// Mouse aim: the mouse moves a reticle, and the player turns toward it at the
/// game's own speed -- War Thunder's mouse aim, where the hand points and the
/// plane follows. Ordinary mouse look turns the view by however far the hand moved,
/// which out-turns anything the D-pad can do; the game's fights are built on how
/// fast the player turns (see "The stick turns at the original's speed" in
/// docs/INPUT.md), and this keeps that.
///
/// **The reticle is an offset from the facing, in the game's own angle units**,
/// yaw in <see cref="Mouse.TakeLook"/>'s sign convention (positive is the game's
/// Left) and pitch the same. The mouse adds to it, and each tick the look hook
/// asks for a step toward it and takes that step off it, so a reached reticle sits
/// at the centre. Relative rather than a world direction, so a warp, a load or a
/// death leaves it where it was on the screen rather than pulling the player round
/// to an old heading.
///
/// **The step is the D-pad's**: at most the game's turn rate (<c>0x1C</c> walking,
/// <c>0x23</c> standing) and changing by at most its acceleration (rate&gt;&gt;2) a
/// tick, the look's 32 and 3 for pitch -- the same ease the button branches of
/// <c>func_80028DB8</c> give a held D-pad and L2/R2. It brakes on the curve that
/// stops it on the reticle (v = sqrt(2a|d|)), so it does not overshoot, and never
/// asks for more than is left. The step is handed to <see cref="Analog.BeforeLook"/>
/// as the mouse's amount, so the pad word, the release and the carry are the ones
/// mouse look already uses.
///
/// The reticle is held on the picture: inside 90% of the half-width (widescreen
/// margin included) and half-height, as angles off the GTE's own projection
/// distance, and inside the game's pitch limit. Pushed against the edge it keeps
/// the turn at full speed, so a turn round is a hand held at the edge.
///
/// Instant mouse look stands down while this is on (<see cref="FrameSmoothing"/>):
/// the view is the player's facing, so there is nothing to lead. The reticle
/// itself moves every drawn frame -- <see cref="Frame"/> drains the host's motion
/// at stage 8, where the lead used to -- and is drawn by
/// <see cref="MouseAimOverlay"/>.
/// See "Mouse aim" in docs/INPUT.md.
/// </summary>
public static class MouseAim
{
    public const string OnKey = "kf2.mouse.aim";

    /// <summary>The setting. Only does anything while mouse look is on.</summary>
    public static bool Enabled;

    /// <summary>On, and mouse look on: the mouse steers rather than turns.</summary>
    public static bool Active => Enabled && Mouse.Enabled;

    static readonly HashSet<string> _fromEnv = new(StringComparer.Ordinal);

    // The reticle off the facing, in 12-bit angle units: what the ticks have left.
    static float _offYaw, _offPitch;

    // The steering velocities, in units a tick, eased as the D-pad's are.
    static float _velYaw, _velPitch;

    // The step the last tick took off the offset. The drawn view reaches it only
    // at phase 1, so the part it has not reached yet is still owed to the reticle.
    static float _lastStepYaw, _lastStepPitch;

    // What the overlay draws: the offset plus the step the view has not reached
    // yet and the motion the next tick has not taken, published at each tick and
    // at stage 8. Read on the host thread.
    static volatile float _shownYaw, _shownPitch;

    // When the look routine last steered; the overlay is drawn only while it is
    // live, so a menu, a load or the paused map hides it.
    static long _steered;

    // The ring the reticle may wander in without turning the player, in angle
    // units (KF2_MOUSE_AIM_RING; 0 turns it off). Not a setting.
    public static int Ring = 32;

    // Whether a turn is under way. Set once the reticle leaves the ring, cleared
    // once it lands on the centre.
    static bool _steering;

    const float TwoPi = MathF.PI * 2f;
    const float UnitsPerRadian = 4096f / TwoPi;

    // The look's acceleration and limit (Analog's PitchAccel, PitchVelMax).
    const int PitchAccel = 3;
    const int PitchVelMax = 32;

    /// <summary>How much of the picture's half-width and half-height the reticle
    /// may reach.</summary>
    const float Reach = 0.9f;

    public static void Configure()
    {
        Kept.Env("KF2_MOUSE_AIM", OnKey, ref Enabled, _fromEnv);

        string ring = Environment.GetEnvironmentVariable("KF2_MOUSE_AIM_RING");
        if (ring != null && int.TryParse(ring, out int r) && r >= 0) Ring = r;
    }

    public static void Install()
    {
        // Registered here rather than in Install's body for the reason Map gives:
        // PanelManager and the ImGui context exist only from RuntimeReadyEvent, so
        // a panel registered earlier would not be drawn at all.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Kept.Saved(OnKey, ref Enabled, _fromEnv);
            PanelManager.Register(MouseAimOverlay.Instance);
        });
    }

    /// <summary>
    /// Called from the look hook once a tick with the motion since the last one.
    /// Returns the step to turn and look by this tick, which the hook spends as the
    /// mouse's amount.
    /// </summary>
    internal static (float Turn, float Pitch) Steer(IMemory m, float turn, float pitch, int rate)
    {
        _steered = Environment.TickCount64;
        if (rate <= 0)
        {
            _lastStepYaw = _lastStepPitch = 0f;
            return (0f, 0f);
        }

        _offYaw += turn;
        _offPitch += pitch;
        Clamp(m);

        // Hand tremor is smaller than a turn, so the reticle may wander inside the
        // ring without turning the player. Past its edge the turn is a decision:
        // it is committed, and it lands on the reticle even if the hand comes back.
        if (Ring > 0 && !_steering)
        {
            float dist = MathF.Sqrt(_offYaw * _offYaw + _offPitch * _offPitch);
            if (dist <= Ring)
            {
                // No step this tick, so the view owes the reticle nothing either.
                _velYaw = _velPitch = 0f;
                _lastStepYaw = _lastStepPitch = 0f;
                Publish(0f, 0f);
                return (0f, 0f);
            }
            _steering = true;
        }

        float stepYaw = Toward(_offYaw, ref _velYaw, rate, rate >> 2);
        float stepPitch = Toward(_offPitch, ref _velPitch, PitchVelMax, PitchAccel);

        // Whole units: the hook truncates and carries the fraction, and what the
        // offset gives up has to be what the game turns by, or the reticle drifts
        // off the place the player is turning to.
        stepYaw = MathF.Truncate(stepYaw);
        stepPitch = MathF.Truncate(stepPitch);
        _offYaw -= stepYaw;
        _offPitch -= stepPitch;
        if (stepYaw == 0f) _velYaw = 0f;
        if (stepPitch == 0f) _velPitch = 0f;

        // Landed: the turn is over, and the next tremor is a new decision.
        if (_steering && stepYaw == 0f && stepPitch == 0f
            && MathF.Abs(_offYaw) < 1f && MathF.Abs(_offPitch) < 1f)
            _steering = false;

        // Kept for Frame: the view is still behind this step at the tick, so the
        // reticle is drawn with the step's unreached part added back on.
        _lastStepYaw = stepYaw;
        _lastStepPitch = stepPitch;
        Publish(stepYaw, stepPitch);
        return (stepYaw, stepPitch);
    }

    /// <summary>
    /// Every drawn frame, from stage 8: the host's motion so far, added to the
    /// reticle the overlay draws, and the part of the last tick's step the
    /// interpolated view has not reached yet (<paramref name="frac"/> is its phase).
    /// That part is what keeps the reticle still on the picture: without it the
    /// reticle drifts with the view and snaps back at each tick. Not spent: the
    /// next tick takes the motion.
    /// </summary>
    internal static void Frame(double frac)
    {
        Mouse.Poll();
        var (turn, look) = Mouse.Pending;
        float keep = (float)(1.0 - frac);
        Publish(_lastStepYaw * keep + turn, _lastStepPitch * keep + look);
    }

    /// <summary>Forget the reticle, as on turning this off or losing the pointer.</summary>
    public static void Reset()
    {
        _offYaw = _offPitch = 0f;
        _velYaw = _velPitch = 0f;
        _lastStepYaw = _lastStepPitch = 0f;
        _steering = false;
        Publish(0f, 0f);
    }

    /// <summary>
    /// The reticle's offset from the facing to draw, in radians (yaw positive to the
    /// game's Left, pitch positive down), or null while there is none to draw: off,
    /// the pointer free, or the look routine not running.
    /// </summary>
    public static (float Yaw, float Pitch)? Shown
    {
        get
        {
            if (!Active || !Mouse.Captured || Mouse.Suspended) return null;
            long now = Environment.TickCount64;
            double hz = Math.Max(1.0, FramePacing.LogicHz);
            if (FramePacing.Paused || now - _steered > (long)(3000.0 / hz)) return null;
            return (_shownYaw / UnitsPerRadian, _shownPitch / UnitsPerRadian);
        }
    }

    /// <summary>The offset plus what the view still owes the reticle (the unreached
    /// step and the pending motion), held on the picture.</summary>
    static void Publish(float addYaw, float addPitch)
    {
        var (maxYaw, maxPitch) = Limits();
        _shownYaw = Math.Clamp(_offYaw + addYaw, -maxYaw, maxYaw);
        _shownPitch = Math.Clamp(_offPitch + addPitch, -maxPitch, maxPitch);
    }

    /// <summary>The reticle held on the picture, and its pitch inside the game's
    /// limit off the base angle the step adds to.</summary>
    static void Clamp(IMemory m)
    {
        var (maxYaw, maxPitch) = Limits();
        _offYaw = Math.Clamp(_offYaw, -maxYaw, maxYaw);

        // The base pitch is a 12-bit angle: just above level it reads 0x0FFx.
        int basePitch = ((m.ReadU16(Analog.Pitch) & 0xFFF) ^ 0x800) - 0x800;
        int limit = Mouse.PitchLimit;
        float lo = Math.Max(-maxPitch, -limit - basePitch);
        float hi = Math.Min(maxPitch, limit - basePitch);
        _offPitch = lo <= hi ? Math.Clamp(_offPitch, lo, hi) : 0f;
    }

    /// <summary>The largest offsets that keep the reticle on the picture, in
    /// angle units, off the GTE's projection distance.</summary>
    static (float Yaw, float Pitch) Limits()
    {
        float h = Math.Max(1f, GteDepth.ProjH);
        int w = OutputView.GameW > 0 ? OutputView.GameW : 320;
        int hgt = OutputView.GameH > 0 ? OutputView.GameH : 240;
        float halfW = w / 2f + Display.WideMargin(w);
        return (MathF.Atan(Reach * halfW / h) * UnitsPerRadian,
                MathF.Atan(Reach * hgt / 2f / h) * UnitsPerRadian);
    }

    /// <summary>
    /// One axis's step toward an offset <paramref name="d"/>, in whole units: the
    /// velocity eased by <paramref name="accel"/> a tick toward the fastest whole
    /// speed that can still stop on the offset when it brakes by <paramref name="accel"/>
    /// a tick, capped at <paramref name="max"/> and at what is left. The stopping
    /// speed is the discrete one (<see cref="StopDistance"/>), not the continuous
    /// sqrt(2*a*d): that brakes a tick too late, so the last steps drop faster than
    /// the ease allows.
    /// </summary>
    static float Toward(float d, ref float vel, int max, int accel)
    {
        float dist = MathF.Abs(d);
        float sign = MathF.Sign(d);

        // The closed form v = (-a + sqrt(a*a + 8*a*dist)) / 2 solves v(v+a)/2a = dist,
        // and the exact sum is never less than that, so it bounds the answer from
        // above; step down from it to the largest speed whose exact run fits.
        int fast = (int)MathF.Floor((-accel + MathF.Sqrt(accel * accel + 8f * accel * dist)) / 2f);
        fast = Math.Min(fast, max);
        while (fast > 0 && StopDistance(fast, accel) > dist) fast--;

        // Whole units throughout (vel starts at 0 and moves by whole accel steps), so
        // the caller's truncation takes nothing and the offset is spent exactly.
        float want = sign * fast;
        vel = Math.Clamp(want, vel - accel, vel + accel);
        // Never past the reticle: a reversal still brakes at the game's rate, but
        // a step that would carry beyond it in the same direction, or past a
        // reticle already reached, is cut to it (truncated, so still whole units
        // and never beyond |d|).
        if (MathF.Sign(vel) * sign >= 0 && MathF.Abs(vel) > dist) vel = MathF.Truncate(d);
        return vel;
    }

    /// <summary>
    /// The distance a run covers braking by <paramref name="accel"/> a tick from the
    /// whole speed <paramref name="v"/> to rest: v + (v-a) + ... over its positive steps.
    /// </summary>
    static int StopDistance(int v, int accel)
    {
        int sum = 0;
        for (int s = v; s > 0; s -= accel) sum += s;
        return sum;
    }
}
