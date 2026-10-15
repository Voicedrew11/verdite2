using System.Globalization;
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
/// <c>func_80028DB8</c> give a held D-pad and L2/R2. It brakes on the discrete
/// stopping distance (<see cref="StopDistance"/>), so it does not overshoot, and
/// never asks for more than is left. The step is handed to <see cref="Analog.BeforeLook"/>
/// as the mouse's amount, so the pad word, the release and the carry are the ones
/// mouse look already uses.
///
/// **One device turns at a time.** A stick, the D-pad or the gyro turning an axis
/// owns that axis for the tick: the mouse still moves the reticle, so the hand's
/// motion is not lost, but takes no step there. The player never turns at the sum
/// of two rates; whichever device moved last owns the turn.
///
/// **A small aim is carried out.** Inside the ring the reticle is only a tremor, so
/// nothing turns it; but a hand that holds a small aim still for
/// <see cref="Settle"/> ticks has decided it, and the reticle is brought to the
/// facing rather than left a few units off it for good.
///
/// **The turn is measured, not assumed.** The step can be cut by the step carry,
/// the overspeed ceiling or the pitch limit. Once a tick the game's measured turn
/// for the mouse is read back (<see cref="Mouse.SpentThisFrame"/>) and the reticle
/// takes back what was not turned, so it stays where the player is turning to.
///
/// <see cref="Sens"/> scales the hand's motion before it reaches the reticle.
///
/// **Head look** (<see cref="HeadLook"/>): the view is drawn at the facing plus the
/// reticle, so the screen centre follows the hand one to one while the body, which
/// moves the player and aims the attacks, chases it at the D-pad's rate. Off, the
/// view is the facing and the reticle is only drawn.
///
/// The reticle is held on the picture: inside 90% of the half-width (widescreen
/// margin included) and half-height, as angles off the GTE's own projection
/// distance, and inside the game's pitch limit. Pushed against the edge it keeps
/// the turn at full speed, so a turn round is a hand held at the edge.
///
/// Instant mouse look stands down while this is on (<see cref="FrameSmoothing"/>),
/// except that head look leads the view by the reticle. The reticle itself moves
/// every drawn frame -- <see cref="Frame"/> drains the host's motion at stage 8,
/// where the lead used to -- and is drawn by <see cref="MouseAimOverlay"/>.
/// **Not a setting.** It is off, and it is no longer in the settings window, PORT
/// SETTINGS or the first-run setup; its saved keys (<c>kf2.mouse.aim*</c>) are no
/// longer read. The <c>KF2_MOUSE_AIM*</c> variables are the comparison and the only
/// way to turn it on.
/// See "Mouse aim" in docs/INPUT.md.
/// </summary>
public static class MouseAim
{
    public const string OnKey = "kf2.mouse.aim";
    public const string HeadKey = "kf2.mouse.aimhead";
    public const string HoldKey = "kf2.mouse.aimhold";
    public const string SensKey = "kf2.mouse.aimsens";
    public const string MarksKey = "kf2.mouse.aimmarks";
    public const string LineKey = "kf2.mouse.aimline";
    public const string EaseKey = "kf2.mouse.aimease";
    public const string DotKey = "kf2.mouse.aimdot";

    /// <summary>The setting. Only does anything while mouse look is on.</summary>
    public static bool Enabled;

    /// <summary>Head look: the view follows the reticle, and the body follows it at
    /// the D-pad's rate. Off, the view is the facing (KF2_MOUSE_AIM_HEAD).</summary>
    public static bool HeadLook;

    /// <summary>The view holds still while the body turns: the dot, the facing,
    /// travels across a still picture to the circle, and the view catches up after
    /// (KF2_MOUSE_AIM_HOLD). Head look wins if both are set.</summary>
    public static bool HoldView;

    /// <summary>The three things the view can do while the body turns, as the
    /// settings page offers them: index 0 turns with the body, 1 is
    /// <see cref="HeadLook"/>, 2 is <see cref="HoldView"/>.</summary>
    public static readonly string[] ViewNames = ["Turns with you", "Follows the mouse", "Holds still"];
    public static int ViewMode => HeadLook ? 1 : HoldView ? 2 : 0;

    /// <summary>The hand's motion, scaled before it reaches the reticle
    /// (KF2_MOUSE_AIM_SENS).</summary>
    public static float Sens = 1f;

    /// <summary>When <see cref="MouseAimOverlay"/> draws its marks: never, only while
    /// a turn is under way or the circle is off the middle (the default), or always
    /// (KF2_MOUSE_AIM_MARKS: off, aiming, always).</summary>
    public static int Marks = MarksAiming;
    public const int MarksOff = 0, MarksAiming = 1, MarksAlways = 2;
    public static readonly string[] MarksNames = ["Off", "While aiming", "Always"];

    /// <summary>Whether the marks include the line from the middle to the circle
    /// (KF2_MOUSE_AIM_LINE).</summary>
    public static bool Line = true;

    /// <summary>Whether the marks include the dot, the facing (KF2_MOUSE_AIM_DOT).</summary>
    public static bool Dot = true;

    /// <summary>Whether a turn eases in and out as the D-pad's does. Off, it starts at
    /// the full rate and stops dead on the reticle: the speed is still the D-pad's,
    /// but there is no ramp at either end (KF2_MOUSE_AIM_EASE).</summary>
    public static bool Ease = true;

    /// <summary>Ticks a small aim must stay still inside the ring before it is carried
    /// out (KF2_MOUSE_AIM_SETTLE; 0 is off). Not a setting.</summary>
    public static int Settle = 3;

    /// <summary>On, and mouse look on: the mouse steers rather than turns.</summary>
    public static bool Active => Enabled && Mouse.Enabled;

    /// <summary>Active, with head look on: the view is drawn at the reticle as well.</summary>
    public static bool HeadActive => Active && HeadLook;

    /// <summary>Active, with the view held still while the body turns.</summary>
    public static bool HoldActive => Active && HoldView && !HeadLook;

    /// <summary>The view is drawn off the facing, by <see cref="ViewLead"/>.</summary>
    public static bool ViewLeads => HeadActive || HoldActive;

    /// <summary>Whether a turn is under way (outside the ring, or settling).</summary>
    public static bool Steering => _steering;

    /// <summary>The ring's radius, in radians.</summary>
    public static float RingRadians => Ring / UnitsPerRadian;

    /// <summary>
    /// Seconds since the reticle last moved, or a nonzero step was taken; zero while
    /// either is happening.
    /// </summary>
    public static double IdleSeconds
    {
        get
        {
            long now = Environment.TickCount64;
            return Math.Max(0.0, (now - Volatile.Read(ref _movedAt)) / 1000.0);
        }
    }

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

    // The held view (HoldView): the camera's angle off the facing, in angle units,
    // its recentring velocity, and what it turned by on the last tick (the drag at
    // the picture's edge and the recentre), which the drawn view has not reached yet.
    static float _camYaw, _camPitch, _camVelYaw, _camVelPitch, _camStepYaw, _camStepPitch;

    // What the view is drawn off the facing by in the held view, published at each
    // tick and at stage 8 as the shown values are.
    static volatile float _leadYaw, _leadPitch;

    // Diagnostics (KF2_MOUSE_AIM_PROBE=1): one line a tick while the reticle moves.
    static bool _probe;

    // Ticks in a row the hand has moved less than half a unit: the settle count.
    static int _stillTicks;

    // The game's measured turn for the mouse on the last measured tick, in angle
    // units: what the reticle was told the view turned by.
    static int _measYaw, _measPitch;
    static long _measFrame = -1;

    // When the reticle last moved or stepped, for IdleSeconds. Read off the host thread.
    static long _movedAt = Environment.TickCount64;

    // Synthetic motion from Inject, which the shell calls from another thread: the
    // total, when it started, how long it takes, and what the ticks have taken so far.
    static readonly object _injectLock = new();
    static float _injYaw, _injPitch, _injTakenYaw, _injTakenPitch;
    static long _injStart;
    static int _injMs;

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
        Kept.Env("KF2_MOUSE_AIM_HEAD", HeadKey, ref HeadLook, _fromEnv);
        Kept.Env("KF2_MOUSE_AIM_HOLD", HoldKey, ref HoldView, _fromEnv);
        Kept.Env("KF2_MOUSE_AIM_SENS", SensKey, ref Sens, _fromEnv);
        Kept.Env("KF2_MOUSE_AIM_LINE", LineKey, ref Line, _fromEnv);
        Kept.Env("KF2_MOUSE_AIM_DOT", DotKey, ref Dot, _fromEnv);
        Kept.Env("KF2_MOUSE_AIM_EASE", EaseKey, ref Ease, _fromEnv);

        string ring = Environment.GetEnvironmentVariable("KF2_MOUSE_AIM_RING");
        if (ring != null && int.TryParse(ring, out int r) && r >= 0) Ring = r;

        string marks = Environment.GetEnvironmentVariable("KF2_MOUSE_AIM_MARKS");
        int m = marks?.Trim().ToLowerInvariant() switch
        {
            "off" or "0" => MarksOff,
            "aiming" or "1" => MarksAiming,
            "always" or "2" => MarksAlways,
            _ => -1,
        };
        if (m >= 0) { Marks = m; _fromEnv.Add(MarksKey); }

        string settle = Environment.GetEnvironmentVariable("KF2_MOUSE_AIM_SETTLE");
        if (settle != null && int.TryParse(settle, out int s) && s >= 0) Settle = s;

        _probe = Environment.GetEnvironmentVariable("KF2_MOUSE_AIM_PROBE") == "1";
    }

    public static void Install()
    {
        // Registered here rather than in Install's body for the reason Map gives:
        // PanelManager and the ImGui context exist only from RuntimeReadyEvent, so
        // a panel registered earlier would not be drawn at all.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Marks = Math.Clamp(Marks, MarksOff, MarksAlways);
            PanelManager.Register(MouseAimOverlay.Instance);
        });
    }

    /// <summary>
    /// Called from the look hook once a tick with the motion since the last one.
    /// Returns the step to turn and look by this tick, which the hook spends as the
    /// mouse's amount. <paramref name="yieldYaw"/> and <paramref name="yieldPitch"/>
    /// say that a stick, the D-pad or the gyro is turning that axis this tick: the
    /// motion still moves the reticle, but no step is taken there.
    /// </summary>
    internal static (float Turn, float Pitch) Steer(IMemory m, float turn, float pitch, int rate,
                                                    bool yieldYaw, bool yieldPitch)
    {
        var step = SteerBody(m, turn, pitch, rate, yieldYaw, yieldPitch);
        HoldCamera(step.Turn, step.Pitch, rate);
        PublishLead(1f);
        return step;
    }

    /// <summary>
    /// The held view's camera for this tick (<see cref="HoldView"/>): held still in the
    /// world while the body turns under it, so its angle off the facing loses the
    /// body's step; dragged along when the facing would leave the picture; and, once
    /// the turn has landed, brought back onto the facing at the D-pad's rate and ease,
    /// as a second turn the eye can follow. Off, it is kept on the facing.
    /// </summary>
    static void HoldCamera(float stepYaw, float stepPitch, int rate)
    {
        if (!HoldActive || rate <= 0)
        {
            _camYaw = _camPitch = _camVelYaw = _camVelPitch = _camStepYaw = _camStepPitch = 0f;
            return;
        }

        _camYaw -= stepYaw;
        _camPitch -= stepPitch;

        float turnYaw = 0f, turnPitch = 0f;
        if (!_steering)
        {
            turnYaw = MathF.Truncate(Toward(-_camYaw, ref _camVelYaw, rate, rate >> 2));
            turnPitch = MathF.Truncate(Toward(-_camPitch, ref _camVelPitch, PitchVelMax, PitchAccel));
            if (turnYaw == 0f) _camVelYaw = 0f;
            if (turnPitch == 0f) _camVelPitch = 0f;
        }
        else _camVelYaw = _camVelPitch = 0f;
        _camYaw += turnYaw;
        _camPitch += turnPitch;

        // The facing kept on the picture: past the reticle's reach the camera is
        // dragged, and the drag is a turn of the view like the recentre.
        var (maxYaw, maxPitch) = Limits();
        float y0 = _camYaw, p0 = _camPitch;
        _camYaw = Math.Clamp(_camYaw, -maxYaw, maxYaw);
        _camPitch = Math.Clamp(_camPitch, -maxPitch, maxPitch);
        _camStepYaw = turnYaw + (_camYaw - y0);
        _camStepPitch = turnPitch + (_camPitch - p0);
    }

    /// <summary>The held view's lead at phase <paramref name="keep"/> = 1 - frac: the
    /// camera's angle off the facing, less the part of the body's step the drawn view
    /// has not reached, plus the part of the camera's own turn it has not.</summary>
    static void PublishLead(float keep)
    {
        if (!HoldActive) { _leadYaw = _leadPitch = 0f; return; }
        _leadYaw = _camYaw + (_lastStepYaw - _camStepYaw) * keep;
        _leadPitch = _camPitch + (_lastStepPitch - _camStepPitch) * keep;
    }

    static (float Turn, float Pitch) SteerBody(IMemory m, float turn, float pitch, int rate,
                                               bool yieldYaw, bool yieldPitch)
    {
        long now = Environment.TickCount64;
        _steered = now;
        turn *= Sens;
        pitch *= Sens;
        var (dueYaw, duePitch) = TakeDue(now);
        turn += dueYaw;
        pitch += duePitch;
        if (turn != 0f || pitch != 0f) Volatile.Write(ref _movedAt, now);

        if (rate <= 0)
        {
            _lastStepYaw = _lastStepPitch = 0f;
            return (0f, 0f);
        }

        // Read before the motion is added: a hand that has barely moved is a hand
        // that has settled on its aim, whichever way it is pointing.
        _stillTicks = MathF.Abs(turn) + MathF.Abs(pitch) < 0.5f ? _stillTicks + 1 : 0;

        _offYaw += turn;
        _offPitch += pitch;
        Clamp(m);

        // Both axes are another device's: the reticle keeps following the hand, and
        // the turn stays with whoever owns it. Steering, if it was under way, is
        // still under way when they let go.
        if (yieldYaw && yieldPitch)
        {
            _velYaw = _velPitch = 0f;
            _lastStepYaw = _lastStepPitch = 0f;
            Publish(0f, 0f);
            Probe(0f, 0f, yieldYaw, yieldPitch);
            return (0f, 0f);
        }

        // Hand tremor is smaller than a turn, so the reticle may wander inside the
        // ring without turning the player. Past its edge the turn is a decision:
        // it is committed, and it lands on the reticle even if the hand comes back.
        // A small aim the hand has held still for Settle ticks is a decision too.
        if (Ring > 0 && !_steering)
        {
            float dist = MathF.Sqrt(_offYaw * _offYaw + _offPitch * _offPitch);
            bool settled = Settle > 0 && _stillTicks >= Settle && dist >= 1f;
            if (dist <= Ring && !settled)
            {
                // No step this tick, so the view owes the reticle nothing either.
                _velYaw = _velPitch = 0f;
                _lastStepYaw = _lastStepPitch = 0f;
                Publish(0f, 0f);
                Probe(0f, 0f, yieldYaw, yieldPitch);
                return (0f, 0f);
            }
            _steering = true;
            _stillTicks = 0;
        }

        // A yielding axis takes no step and loses its velocity (Toward is not asked),
        // so when the device that owns it lets go the ease starts again from rest.
        float stepYaw = yieldYaw ? 0f : Toward(_offYaw, ref _velYaw, rate, rate >> 2);
        float stepPitch = yieldPitch ? 0f : Toward(_offPitch, ref _velPitch, PitchVelMax, PitchAccel);

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
        if (stepYaw != 0f || stepPitch != 0f) Volatile.Write(ref _movedAt, now);
        Publish(stepYaw, stepPitch);
        Probe(stepYaw, stepPitch, yieldYaw, yieldPitch);
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
    internal static void Frame(IMemory m, double frac)
    {
        Mouse.Poll();
        if (FramePacing.FirstWalkOfTick(ref _measFrame)) Measure(m);

        var (turn, look) = Mouse.Pending;
        turn *= Sens;
        look *= Sens;
        var (dueYaw, duePitch) = PeekDue(Environment.TickCount64);
        turn += dueYaw;
        look += duePitch;
        if (turn != 0f || look != 0f) Volatile.Write(ref _movedAt, Environment.TickCount64);

        float keep = (float)(1.0 - frac);
        Clamp(m);
        PublishLead(keep);
        Publish(_lastStepYaw * keep + turn, _lastStepPitch * keep + look);
    }

    /// <summary>
    /// Once a tick, after the look routine has spent the mouse: what the game
    /// actually turned by for it, against what was asked. The step can be cut by the
    /// step carry, the overspeed ceiling or the pitch limit, so the reticle takes
    /// back the part that was not turned, and the unreached part of the step is the
    /// part that was. A difference this large is a warp or a load, not a cut.
    /// </summary>
    static void Measure(IMemory m)
    {
        if (Mouse.SpentThisFrame(m) is not { } s) return;
        _measYaw = s.Yaw;
        _measPitch = s.Pitch;

        float dy = s.AskedYaw - s.Yaw;
        float dp = s.AskedPitch - s.Pitch;
        // The held camera lost the asked step too, so it gets the same back.
        if (MathF.Abs(dy) <= 64f) { _offYaw += dy; _lastStepYaw = s.Yaw; if (HoldActive) _camYaw += dy; }
        if (MathF.Abs(dp) <= 64f) { _offPitch += dp; _lastStepPitch = s.Pitch; if (HoldActive) _camPitch += dp; }
    }

    /// <summary>Forget the reticle, as on turning this off or losing the pointer.</summary>
    public static void Reset()
    {
        _offYaw = _offPitch = 0f;
        _velYaw = _velPitch = 0f;
        _lastStepYaw = _lastStepPitch = 0f;
        _steering = false;
        _stillTicks = 0;
        _measYaw = _measPitch = 0;
        _camYaw = _camPitch = _camVelYaw = _camVelPitch = _camStepYaw = _camStepPitch = 0f;
        _leadYaw = _leadPitch = 0f;
        lock (_injectLock)
        {
            _injYaw = _injPitch = _injTakenYaw = _injTakenPitch = 0f;
            _injMs = 0;
        }
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

    /// <summary>
    /// The lead the view adds off the facing, in angle units. In head look it is the
    /// published reticle, the offset plus what the view still owes it; in the held
    /// view it is the camera's angle off the facing. Zero otherwise, and while the
    /// reticle is not drawn (a menu, a load, the paused map), so the view does not
    /// stay turned behind them.
    /// </summary>
    internal static (double Yaw, double Pitch) ViewLead
    {
        get
        {
            if (Shown is null) return (0.0, 0.0);
            if (HeadActive) return (_shownYaw, _shownPitch);
            if (HoldActive) return (_leadYaw, _leadPitch);
            return (0.0, 0.0);
        }
    }

    /// <summary>The held view's lead in radians, for the overlay: where the facing is
    /// on the picture is its negation. Zero when the view is not held.</summary>
    public static (float Yaw, float Pitch) HeldLead =>
        HoldActive ? (_leadYaw / UnitsPerRadian, _leadPitch / UnitsPerRadian) : (0f, 0f);

    /// <summary>
    /// Synthetic reticle motion for the shell's <c>aim</c> verb: <paramref name="yaw"/>
    /// and <paramref name="pitch"/> in angle units, spread evenly over
    /// <paramref name="ms"/> (zero is at once). Replaces an unfinished one, whose
    /// undelivered remainder is dropped. Called from another thread.
    /// </summary>
    public static void Inject(float yaw, float pitch, int ms)
    {
        lock (_injectLock)
        {
            _injYaw = yaw;
            _injPitch = pitch;
            _injMs = ms;
            _injStart = Environment.TickCount64;
            _injTakenYaw = _injTakenPitch = 0f;
        }
    }

    /// <summary>
    /// The state the shell's <c>aim</c> verb reports, as one line of JSON. Angles are
    /// in angle units, one decimal.
    /// </summary>
    public static string StateJson()
    {
        return "{" +
            $"\"active\":{B(Active)},\"head\":{B(HeadActive)},\"hold\":{B(HoldActive)},\"cam\":{Pair(_camYaw, _camPitch)},\"captured\":{B(Mouse.Captured)}," +
            $"\"off\":{Pair(_offYaw, _offPitch)},\"shown\":{Pair(_shownYaw, _shownPitch)}," +
            $"\"vel\":{Pair(_velYaw, _velPitch)},\"lastStep\":{Pair(_lastStepYaw, _lastStepPitch)}," +
            $"\"measured\":{Pair(_measYaw, _measPitch)},\"steering\":{B(_steering)}," +
            $"\"ring\":{Ring.ToString(CultureInfo.InvariantCulture)}," +
            $"\"settle\":{Settle.ToString(CultureInfo.InvariantCulture)}," +
            $"\"sens\":{F(Sens)},\"ease\":{B(Ease)}" + "}";
    }

    /// <summary>Publishes the offset plus what the view still owes the reticle (the
    /// unreached step and the pending motion), held on the picture.</summary>
    static void Publish(float addYaw, float addPitch)
    {
        // On the picture: round the middle, or in the held view round where the view
        // points off the facing.
        var (maxYaw, maxPitch) = Limits();
        float cy = HoldActive ? _leadYaw : 0f, cp = HoldActive ? _leadPitch : 0f;
        _shownYaw = Math.Clamp(_offYaw + addYaw, cy - maxYaw, cy + maxYaw);
        _shownPitch = Math.Clamp(_offPitch + addPitch, cp - maxPitch, cp + maxPitch);
    }

    /// <summary>The reticle held on the picture, and its pitch inside the game's
    /// limit off the base angle the step adds to.</summary>
    static void Clamp(IMemory m)
    {
        // Round the middle of the picture, which in the held view is the camera's
        // angle off the facing rather than the facing.
        var (maxYaw, maxPitch) = Limits();
        float cy = HoldActive ? _camYaw : 0f, cp = HoldActive ? _camPitch : 0f;
        _offYaw = Math.Clamp(_offYaw, cy - maxYaw, cy + maxYaw);

        // The base pitch is a 12-bit angle: just above level it reads 0x0FFx.
        int basePitch = ((m.ReadU16(Analog.Pitch) & 0xFFF) ^ 0x800) - 0x800;
        int limit = Mouse.PitchLimit;
        float lo = Math.Max(cp - maxPitch, -limit - basePitch);
        float hi = Math.Min(cp + maxPitch, limit - basePitch);
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
    /// The synthetic motion still due at <paramref name="now"/>: the total, spread
    /// evenly over its time, less what the ticks have taken.
    /// </summary>
    static (float Yaw, float Pitch) Due(long now)
    {
        float f = _injMs <= 0 ? 1f : Math.Min(1f, (now - _injStart) / (float)_injMs);
        return (_injYaw * f - _injTakenYaw, _injPitch * f - _injTakenPitch);
    }

    /// <summary>Takes the synthetic motion due, for a tick's step.</summary>
    static (float Yaw, float Pitch) TakeDue(long now)
    {
        lock (_injectLock)
        {
            var due = Due(now);
            _injTakenYaw += due.Yaw;
            _injTakenPitch += due.Pitch;
            return due;
        }
    }

    /// <summary>The synthetic motion due, for the reticle drawn this frame; not taken.</summary>
    static (float Yaw, float Pitch) PeekDue(long now)
    {
        lock (_injectLock) return Due(now);
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
        // Without the ease the step is the whole way at the full rate: on at once,
        // and the last step whatever is left, so it stops dead on the reticle.
        if (!Ease)
        {
            vel = Math.Clamp(MathF.Truncate(d), -max, max);
            return vel;
        }

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

    /// <summary>The probe line (KF2_MOUSE_AIM_PROBE=1), when the reticle or a step is
    /// not at rest.</summary>
    static void Probe(float stepYaw, float stepPitch, bool yieldYaw, bool yieldPitch)
    {
        if (!_probe) return;
        // A remainder under a unit is a landed reticle (the clamp to the picture's
        // edge leaves one), not motion worth a line a tick.
        if (MathF.Abs(_offYaw) < 1f && MathF.Abs(_offPitch) < 1f && stepYaw == 0f && stepPitch == 0f && !_steering) return;
        Console.WriteLine($"[KF2] aim: off=({F(_offYaw)},{F(_offPitch)}) step=({F(stepYaw)},{F(stepPitch)}) " +
                          $"vel=({F(_velYaw)},{F(_velPitch)}) steering={B(_steering)} " +
                          $"yield=({B(yieldYaw)},{B(yieldPitch)}) measured=({_measYaw},{_measPitch}) " +
                          $"still={_stillTicks}");
    }

    static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    static string B(bool v) => v ? "true" : "false";
    static string Pair(float a, float b) => $"[{F(a)},{F(b)}]";
}
