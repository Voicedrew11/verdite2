using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Analog camera and movement, bound as a modern twin-stick scheme.
///
///     KF2_ANALOG=1              on (the default); 0 hands the sticks back
///     KF2_ANALOG_LOOK=1         right stick turns and looks
///     KF2_ANALOG_MOVE_ENABLE=1  left stick walks and strafes
///     KF2_TANK=1                3D (one-stick) controls: the left stick walks and turns,
///                               the right stick is not read
///     KF2_ANALOG_TURN/PITCH/MOVE=1.0            sensitivities
///     KF2_ANALOG_DEADZONE/MOVEDEADZONE=0.15     deadzones
///     KF2_ANALOG_CURVE/MOVECURVE=1.35/1.0       response curves
///     KF2_ANALOG_ACCEL=1 ACCELMAX=2.2 ACCELTIME=0.5   the look ramp
///     KF2_ANALOG_INSTANTSTOP=1                  camera stops on release
///     KF2_ANALOG_EASE=0                         the right stick neither eases in nor out
///     KF2_ANALOG_INVERTY/INVERTTURN/INVERTSTRAFE/INVERTFWD=1
///     KF2_ANALOG_PROBE=1        the control-state report; see AnalogProbe
///
/// The knobs are settings, under Input — see Kf2.Settings.AnalogPage. The saved
/// choices are read on RuntimeReadyEvent rather than in <see cref="Configure"/>,
/// since ConfigManager only loads inside HostWindow.Initialize, after Program.cs;
/// an environment variable still wins over the saved value for the key it names.
///
/// King's Field is a digital-pad game: one 16-bit button word a frame and a
/// fixed step for everything. The port has had real sticks all along --
/// InputManager fills Controller.LeftX/LeftY/RightX/RightY -- but the game asks
/// the BIOS for PAD_dr and sees a digital pad, so nothing consumed them; the
/// runtime's default binding just wires the left stick to the D-pad, which means
/// the stick *turns* at the game's fixed rate.
///
/// What makes real analog cheap here is the shape of the game's own control
/// code. Turning, looking and walking are all velocity based, and every one of
/// them has the same three-branch form (found in the emitted C#, see NOTES):
///
///     if      (pad &amp; maskInc)  vel += rate >> 2, clamp to +rate
///     else if (pad &amp; maskDec)  vel -= rate >> 2, clamp to -rate
///     else                     vel decays toward 0
///     angle_or_position += vel
///
/// So the patch does not replace anything and does not touch the position. It
/// pre-loads the velocity word with `target - accel` and asserts the matching
/// button in the game's own pad global; the game's next instruction adds `accel`
/// and lands exactly on `target`, then applies it through its own path --
/// collision, the ±0x2BC pitch limit, the walk-speed normalisation, footsteps
/// and animation all run as they always did, on an amount we chose.
///
/// The buttons are read out of the game's action-mask table at 0x8006E568..5D0
/// rather than hardcoded, so it follows whatever the player set in the game's own
/// control-config screen.
///
/// The look hook is shared with <see cref="Mouse"/>, which is the same idea
/// applied to a device that gives displacement rather than a rate: it hands
/// <see cref="BeforeLook"/> a step and this class writes the word, so the two
/// devices cannot fight over it. That is also why the look hook runs with
/// <see cref="Enabled"/> off — keyboard and mouse is a scheme of its own.
///
/// Sticks idle == patch idle: the hooks return before touching anything, so the
/// D-pad and keyboard behave exactly as they do with <see cref="Enabled"/> off.
/// That is also why this can default to *on*: a keyboard player never reaches the
/// code at all, and a pad player without it has a left stick wired to the D-pad,
/// which in this game turns rather than walks. It lives in `patches/` rather than
/// `mods/` for the reason under "What belongs in a mod, and what does not":
/// working sticks on a modern pad are not a taste a player should have to find a
/// package to fix. See "Analog twin-stick control" in NOTES.md.
/// </summary>
public static class Analog
{
    // ---- the game's per-frame control state (GAME.EXE, all off 0x801A0000) ----
    const uint Pad        = 0x80199554;   // u16, the word PadRead(1) stored this frame; active high
    const uint StrafeVel  = 0x8019953E;   // s16, + moves along yaw-0x400
    const uint FwdVel     = 0x80199540;   // s16, + moves along yaw
    const uint TurnVel    = 0x80199544;   // s16, added to yaw each frame
    const uint PitchVel   = 0x80199546;   // s16, added to pitch each frame
    const uint MoveSpeed  = 0x80199558;   // u32, this frame's walk speed  (0xC8 = 200)
    const uint TurnRate   = 0x8019955C;   // u32, this frame's turn rate   (0x1C walking, 0x23 standing)

    // The view angles, for the probe. base pitch/yaw/roll; the composed triple
    // the renderer reads is at 0x80199504/06/08.
    internal const uint Pitch = 0x8019950C;
    internal const uint Yaw   = 0x8019950E;

    // ---- the action -> button mask table (GAME.EXE .data) ----
    // Named for what the branch does to the velocity, not for a direction: which
    // way "+" points on screen is a convention, and the invert toggles settle it.
    internal const uint MaskTurnInc   = 0x8006E59C;
    internal const uint MaskTurnDec   = 0x8006E598;
    internal const uint MaskPitchInc  = 0x8006E584;
    internal const uint MaskPitchDec  = 0x8006E58C;
    internal const uint MaskFwdInc    = 0x8006E590;
    internal const uint MaskFwdDec    = 0x8006E594;
    internal const uint MaskStrafeInc = 0x8006E580;
    internal const uint MaskStrafeDec = 0x8006E588;

    // Turning and looking, and walking and strafing: the two routines out of
    // stage 3 whose velocities this drives.
    const uint LookRoutine = 0x80028DB8;
    const uint MoveRoutine = 0x800290D4;

    // The accelerations the game applies once we have asserted the button.
    // Turn and move are a quarter of that frame's rate; pitch is a flat 3.
    const int PitchAccel = 3;
    const int PitchVelMax = 32;

    // How far past the game's own per-frame limit the camera may be driven.
    // The limit only exists in the two branches that read a button; the branch
    // that runs with neither button down applies the velocity unclamped, having
    // first decayed it by the same step. So writing target+accel with no button
    // asserted lands on target however large it is -- see Drive. Four times is a
    // ceiling to keep a bad sensitivity from spinning the view.
    const int OverspeedCap = 4;

    // ---- the settings, and where they are kept between runs -------------------
    //
    // Plain public fields rather than the properties the other patches expose,
    // because every one of these is a slider or a checkbox and ImGui takes its
    // value by `ref`: eighteen Set… methods would exist only to be passed a value
    // the widget had already clamped. AnalogPage clamps what the widget does not
    // (ctrl+click typing) with ImGuiSliderFlags.AlwaysClamp, and nothing here can
    // be driven out of range by a bad number anyway -- Step's own ceiling and the
    // deadzone test see to that.

    public const string OnKey          = "kf2.analog.on";
    public const string LookKey        = "kf2.analog.look";
    public const string MoveKey        = "kf2.analog.move";
    public const string StopKey        = "kf2.analog.instantstop";
    public const string AccelKey       = "kf2.analog.accel";
    public const string EaseKey        = "kf2.analog.ease";
    public const string AccelMaxKey    = "kf2.analog.accelmax";
    public const string AccelTimeKey   = "kf2.analog.acceltime";
    public const string TurnSensKey    = "kf2.analog.turn";
    public const string PitchSensKey   = "kf2.analog.pitch";
    public const string MoveSensKey    = "kf2.analog.movesens";
    public const string LookDeadKey    = "kf2.analog.lookdeadzone";
    public const string MoveDeadKey    = "kf2.analog.movedeadzone";
    public const string LookCurveKey   = "kf2.analog.lookcurve";
    public const string MoveCurveKey   = "kf2.analog.movecurve";
    public const string InvertPitchKey = "kf2.analog.invertpitch";
    public const string InvertTurnKey  = "kf2.analog.invertturn";
    public const string InvertStrafeKey = "kf2.analog.invertstrafe";
    public const string InvertFwdKey   = "kf2.analog.invertforward";
    public const string ControlsKey    = "kf2.controls";

    /// <summary>Live rather than fixed at startup: the hooks stay attached and
    /// return immediately when this is off, so it can be taken back mid-session.</summary>
    public static bool Enabled = true;
    public static bool AnalogLook = true;
    public static bool AnalogMove = true;

    /// <summary>3D controls: one stick, as tank controls are. The left stick walks and
    /// turns as the D-pad does, L1 and R1 strafe, L2 and R2 look, and the right stick
    /// is not read. Off is 2D, the twin sticks. Kept as a bool ("True"/"False") under
    /// <see cref="ControlsKey"/>, as every switch of this port is.</summary>
    public static bool Tank;

    // Look acceleration: hold the stick out and the camera keeps speeding up for
    // the first half second, instead of sitting at one rate the moment you touch
    // it. This is what a modern shooter's stick does and what the game, built for
    // a d-pad that is either down or not, has no notion of. Fine aim near centre
    // stays fine -- the ramp only starts past LookAccelThreshold -- and it falls
    // away three times faster than it builds, so easing off is immediate.
    //
    // Off by default: it turns the view past the D-pad's rate, up to 2.2 times it,
    // and the game's combat is built on how fast the player can turn. The default
    // stick is the original's speed; faster is something a player asks for.
    public static bool LookAccel = false;
    public static float LookAccelMax = 2.2f;
    public static float LookAccelTime = 0.5f;
    const float LookAccelThreshold = 0.8f;
    static float _accelT;
    static long _accelTick;

    public static float LookDeadzone = 0.15f;
    public static float MoveDeadzone = 0.15f;
    // Nearly linear. A steeper curve plus a hard speed cap is what "stiff" is:
    // slow to get going and never fast.
    public static float LookCurve = 1.35f;
    public static float MoveCurve = 1.0f;
    // 3D controls read each axis of the left stick on its own deadzone (a cross,
    // not a circle), so a thumb a little off forward walks straight and a sideways
    // push turns in place: past a radial deadzone the off axis counts in full, and
    // there it is a turn or a walk nobody asked for. The turn is squared, so a small
    // push corrects finely and a full one still turns at the full rate. 0.3 of the
    // byte's range is well inside the runtime's own stick-to-D-pad threshold
    // (InputManager.StickThreshold, about 0.64 of the byte), so a stick inside this
    // deadzone never reaches the game as a D-pad turn either.
    const float TankDeadzone = 0.3f;
    const float TankTurnCurve = 2f;
    public static float TurnSens = 1.0f;
    public static float PitchSens = 1.0f;
    public static float MoveSens = 1.0f;

    public static bool InvertTurn;
    public static bool InvertPitch;
    public static bool InvertStrafe;
    public static bool InvertForward;

    // The camera stops when the stick is released rather than coasting.
    //
    // The game ramps a released velocity down instead of dropping it -- pitch by
    // 3 a frame from a limit of 32, so a released stick keeps looking for about
    // eleven frames, a third of a second and some 16 degrees. That is fine for a
    // held button, which cannot be released halfway, and wrong for a stick: it
    // reads as inertia on the look axis and nowhere else, because the turn axis
    // is already being zeroed by the left-stick leak fix below.
    //
    // Movement is deliberately *not* included. Its ramp-down is the walking
    // momentum the game has always had, and stopping the player dead would be a
    // change to how the game plays rather than to how the stick reads.
    //
    // Off by default, for the same reason the stick ramps up (see RampTurn): a stick
    // that starts and stops the view dead turns it faster than the game ever did,
    // and the original's ease in and out is how its turning feels.
    public static bool CameraInstantStop = false;

    // The right stick eased toward what it asks, by the game's own step (see Ramp).
    // Off, it is direct: the turn and look are the stick's position at once, still
    // capped at the D-pad's rate and L2/R2's, and a released stick stops dead, so
    // CameraInstantStop says nothing. The speed is the original's either way; only
    // the drag a held button has, four or five ticks in and out, is gone. 2D only:
    // 3D's left-stick turn is the D-pad's, ease and all.
    public static bool StickEase = true;

    // Which camera axes the sticks were driving last frame, so the release can be
    // handed back to the D-pad and L2/R2 after exactly one zeroing frame, and the
    // same for the mouse -- kept apart because the mouse's release does not go
    // through CameraInstantStop.
    static bool _ownedTurn, _ownedPitch;
    static bool _mouseTurn, _mousePitch;

    // Fractional remainders. Not optional: at a 15 Hz tick a small stick deflection
    // rounds to a zero step every tick, and the player would simply not move.
    static float _turnCarry, _pitchCarry, _fwdCarry, _strafeCarry;

    // Whether the turn and look carries are the mouse's alone: set by a step the
    // mouse asked for with no stick, gyro or tank turn in it, cleared by any step
    // one of those shares, and left as it was by a step neither asks for.
    static bool _turnCarryMouse, _pitchCarryMouse;

    /// <summary>The fraction of the mouse's turn and look the truncated steps have
    /// not spent yet, which the next step adds to what it is asked; zero while a
    /// stick, the gyro or the tank turn shares the carry. The mouse's lead shows it,
    /// so the picture and the tick agree to the fraction (FrameSmoothing.MouseLead).</summary>
    internal static (float Turn, float Pitch) MouseCarry =>
        (_turnCarryMouse ? _turnCarry : 0f, _pitchCarryMouse ? _pitchCarry : 0f);

    // The sticks' turn and look as they ramp toward what the stick asks, and whether
    // the stick drove each last tick: the game's own ease in and out, as a held D-pad
    // and L2/R2 get it (see Ramp). One pair for both control schemes, since 3D's left
    // stick and 2D's right stick never turn at once.
    static float _rampTurn, _rampPitch;
    static bool _rampTurnLive, _rampPitchLive;

    /// <summary>Raised whenever the shipped defaults change in a way a saved value
    /// should not survive. 2: the stick turns at the D-pad's rate, so look
    /// acceleration and the instant stop went off and sensitivities above 1.0 were
    /// put back to 1.0, once.</summary>
    const int DefaultsVersion = 2;
    const string DefaultsKey = "kf2.analog.defaults";

    /// <summary>Keys a KF2_ANALOG* variable set, which the saved settings must
    /// not overwrite — the same precedence the other patches keep.</summary>
    static readonly HashSet<string> _fromEnv = new(StringComparer.Ordinal);

    // HookManager attributes hooks to a mod so they can be removed again. This is
    // in-project rather than a loaded package, so it declares its own identity.
    static readonly ModInfo _self = new()
    {
        Id = "kf2.analog",
        Name = "Analog twin-stick control",
        Version = "1.0",
        Description = "Drives the game's own control velocities from the sticks.",
    };

    /// <summary>
    /// This game's values for Verdite Core's mouse look
    /// (<see cref="Verdite.Core.Mouse"/>); core owns the mechanism, this is the
    /// data. The angles are <see cref="Yaw"/>/<see cref="Pitch"/>, and the frame
    /// clock is the one core's stale-motion test keys off.
    /// </summary>
    public static readonly MouseGame MouseValues = new(
        UnitsPerDegree: 4096f / 360f,   // 12 bits to the circle (`yaw & 0xFFF`)
        DegreesPerPixel: 0.15f,         // a 90-degree turn is 600 pixels of desk
        StepCap: 1024,                  // the most one frame may turn: a quarter turn
        PitchLimit: 0x2BC,              // about 62 degrees either side of level
        YawAddress: Yaw,
        PitchAddress: Pitch,
        DefaultLeftButton: 3,           // Square swings
        DefaultRightButton: 4,          // Triangle casts
        DefaultMiddleButton: 1,         // Cross is the action button
        TextEditing: () => HotkeyGate.Typing || Remaster.Editor.Open,
        Frames: () => FramePacing.Frames,
        LogicHz: () => FramePacing.LogicHz,
        Paused: () => FramePacing.Paused,
        // A click on the picture captures; Escape opens the menu (KeyLayout), which
        // gives the pointer back. A key saved before this keeps working.
        DefaultCaptureKey: Silk.NET.Input.Key.Unknown,
        // Mouse look and instant mouse look are always on: a mouse is used or it
        // is not, and a player without one never meets either (the user's call,
        // 2026-10-13). KF2_MOUSE=0 and KF2_MOUSE_LEAD=0 are the comparisons.
        OnIsSetting: false,
        LeadIsSetting: false);

    /// <summary>
    /// The environment variables, read before anything else. This one reads them
    /// itself rather than being handed their values by Program.cs the way
    /// <see cref="NoDither"/> and <see cref="AutoReload"/> are: there are eighteen
    /// of them, and eighteen string parameters would say less about which variable
    /// sets what than the table above does.
    /// </summary>
    public static void Configure()
    {
        Env("KF2_ANALOG", OnKey, ref Enabled);
        Env("KF2_ANALOG_LOOK", LookKey, ref AnalogLook);
        Env("KF2_ANALOG_MOVE_ENABLE", MoveKey, ref AnalogMove);
        Env("KF2_TANK", ControlsKey, ref Tank);
        Env("KF2_ANALOG_INVERTY", InvertPitchKey, ref InvertPitch);
        Env("KF2_ANALOG_INVERTTURN", InvertTurnKey, ref InvertTurn);
        Env("KF2_ANALOG_INVERTSTRAFE", InvertStrafeKey, ref InvertStrafe);
        Env("KF2_ANALOG_INVERTFWD", InvertFwdKey, ref InvertForward);

        // One deadzone variable for both sticks, since wanting different ones is
        // the rarer case; MOVEDEADZONE below is how you say so.
        Env("KF2_ANALOG_DEADZONE", LookDeadKey, ref LookDeadzone);
        if (_fromEnv.Contains(LookDeadKey))
        {
            MoveDeadzone = LookDeadzone;
            _fromEnv.Add(MoveDeadKey);
        }
        Env("KF2_ANALOG_MOVEDEADZONE", MoveDeadKey, ref MoveDeadzone);

        Env("KF2_ANALOG_CURVE", LookCurveKey, ref LookCurve);
        Env("KF2_ANALOG_MOVECURVE", MoveCurveKey, ref MoveCurve);
        Env("KF2_ANALOG_TURN", TurnSensKey, ref TurnSens);
        Env("KF2_ANALOG_PITCH", PitchSensKey, ref PitchSens);
        Env("KF2_ANALOG_MOVE", MoveSensKey, ref MoveSens);
        Env("KF2_ANALOG_INSTANTSTOP", StopKey, ref CameraInstantStop);
        Env("KF2_ANALOG_ACCEL", AccelKey, ref LookAccel);
        Env("KF2_ANALOG_EASE", EaseKey, ref StickEase);
        Env("KF2_ANALOG_ACCELMAX", AccelMaxKey, ref LookAccelMax);
        Env("KF2_ANALOG_ACCELTIME", AccelTimeKey, ref LookAccelTime);

        AnalogProbe.Configure();
    }

    /// <summary>
    /// Attach the hooks. Deferred to the first overlay load because
    /// <see cref="SymbolRegistry"/> reads the dispatcher's overlay tables, and
    /// those are registered inside Entry.Run — after Program.cs has run, but before
    /// anything is loaded, so the first load event is the earliest moment every
    /// overlay is resolvable.
    /// </summary>
    public static void Install()
    {
        // The saved choices can only be read once ConfigManager has loaded, which
        // happens inside HostWindow.Initialize -- after Program.cs called Configure.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            MigrateDefaults();
            Saved(OnKey, ref Enabled);
            Saved(LookKey, ref AnalogLook);
            Saved(MoveKey, ref AnalogMove);
            Saved(ControlsKey, ref Tank);
            Saved(StopKey, ref CameraInstantStop);
            Saved(AccelKey, ref LookAccel);
            Saved(EaseKey, ref StickEase);
            Saved(AccelMaxKey, ref LookAccelMax);
            Saved(AccelTimeKey, ref LookAccelTime);
            Saved(TurnSensKey, ref TurnSens);
            Saved(PitchSensKey, ref PitchSens);
            Saved(MoveSensKey, ref MoveSens);
            Saved(LookDeadKey, ref LookDeadzone);
            Saved(MoveDeadKey, ref MoveDeadzone);
            Saved(LookCurveKey, ref LookCurve);
            Saved(MoveCurveKey, ref MoveCurve);
            Saved(InvertPitchKey, ref InvertPitch);
            Saved(InvertTurnKey, ref InvertTurn);
            Saved(InvertStrafeKey, ref InvertStrafe);
            Saved(InvertFwdKey, ref InvertForward);
            AnalogProbe.LoadSaved();
        });

        // Attached whether or not the setting is on: analog control is a choice
        // that can be taken back, and hooks cannot be added once the game is
        // running past the overlay loads.
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    /// <summary>
    /// Bring a saved config to the current defaults once (<see cref="DefaultsVersion"/>):
    /// a value saved under the old ones was chosen against a stick that already
    /// turned faster than the D-pad, so it is reset, not kept. Run before the saved
    /// values are read; a variable set in the environment still wins, as it never
    /// touched the store.
    /// </summary>
    static void MigrateDefaults()
    {
        var view = RecompOne.Runtime.Runtime.View;
        if (view.GetInt(DefaultsKey, 0) >= DefaultsVersion) return;

        int reset = 0;
        if (view.Has(AccelKey) && view.GetBool(AccelKey, false)) { view.SetBool(AccelKey, false); reset++; }
        if (view.Has(StopKey) && view.GetBool(StopKey, false)) { view.SetBool(StopKey, false); reset++; }
        if (view.Has(TurnSensKey) && view.GetFloat(TurnSensKey, 1f) > 1f) { view.SetFloat(TurnSensKey, 1f); reset++; }
        if (view.Has(PitchSensKey) && view.GetFloat(PitchSensKey, 1f) > 1f) { view.SetFloat(PitchSensKey, 1f); reset++; }
        view.SetInt(DefaultsKey, DefaultsVersion);
        RecompOne.Runtime.Runtime.SaveView();
        if (reset > 0)
            Console.WriteLine($"[KF2] analog: the stick now turns at the D-pad's rate; {reset} saved " +
                              "setting(s) that turned it faster were reset (Input ▸ Gamepad to change them back)");
    }

    static void Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(Analog);
        int n = 0;
        n += Hook(LookRoutine, self.GetMethod(nameof(BeforeLook), BindingFlags.Public | BindingFlags.Static)!);
        n += Hook(MoveRoutine, self.GetMethod(nameof(BeforeMove), BindingFlags.Public | BindingFlags.Static)!);
        n += AnalogProbe.Attach(_self);

        if (n == 0)
        {
            Console.Error.WriteLine("[KF2] analog: nothing hooked — the sticks will do whatever the " +
                                    "pad bindings say, whatever the settings say. " +
                                    "See \"Analog twin-stick control\" in NOTES.md.");
            return;
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] analog: {(Enabled ? "on" : "off")}, {n} hook(s) " +
                          $"(deadzone {LookDeadzone:0.##}, turn x{TurnSens:0.##}, move x{MoveSens:0.##}); " +
                          (Tank ? "3D, one stick: the left stick walks and turns, the right stick is not read, "
                                : "2D: the left stick walks and strafes, the right stick turns and looks, ") +
                          "and the D-pad is untouched while the sticks are centred");
    }

    static int Hook(uint addr, MethodInfo pre)
    {
        var target = SymbolRegistry.Resolve("game", null, addr);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] analog: no function at game/0x{addr:X8}");
            return 0;
        }
        return HookManager.AddPre(_self, target, pre) ? 1 : 0;
    }

    // Turning and looking. The function reads the pad, accumulates turn and
    // pitch velocity, then does `yaw = (yaw + turnVel) & 0xFFF` and the same for
    // pitch with a ±0x2BC limit -- so setting the velocity here is the whole job.
    //
    // It is also where the mouse's motion is spent (see <see cref="Mouse"/>): a
    // mouse and a stick are two ways of asking for the same per-frame step, and
    // one routine deciding it is what keeps them from writing the same word twice
    // in a frame. That is why this runs with the sticks switched off -- keyboard
    // and mouse is a scheme of its own, not a variant of the pad one.
    public static void BeforeLook(CpuContext c, IMemory m)
    {
        // Taken unconditionally: the accumulator has to be emptied whether or not
        // anything here is going to spend it.
        var (mTurn, mPitch) = Mouse.TakeLook();
        // The gyro (GyroAim, a drawn bow) asks as the mouse does, with an amount,
        // and is spent beside the stick's share; emptied here whatever happens.
        var (gTurn, gPitch) = GyroAim.Take();
        bool gyro = gTurn != 0f || gPitch != 0f;
        // 3D is one stick, as tank controls are: the right stick is not read at all,
        // L2 and R2 look as the game has them, and the left stick turns. So the left
        // stick needs only "walks" there, not "the right stick turns and looks".
        bool right = Enabled && AnalogLook && !Tank;
        bool left = Enabled && AnalogMove && (Tank || AnalogLook);
        var (x, y) = right ? Shape(Controller.RightX, Controller.RightY, LookDeadzone, LookCurve)
                           : (0f, 0f);
        // Read once: the left stick's turn for 3D, which is also the stick's share below.
        (float X, float Y) tankLeft = Tank ? TankLeft : (0f, 0f);

        // Mouse aim (MouseAim): the motion moves a reticle, and what is spent is the
        // step toward it at the D-pad's rate, as the mouse's amount. The pointer
        // let go takes the reticle with it, so nothing steers that cannot be seen.
        // Each device that turns an axis this tick is told to MouseAim, which yields
        // that axis, so the player never turns at the sum of two rates.
        if (MouseAim.Active)
        {
            if (Mouse.Captured)
            {
                ushort padNow = m.ReadU16(Pad);
                bool dpadTurn = (padNow & ((ushort)m.ReadU32(MaskTurnInc) | (ushort)m.ReadU32(MaskTurnDec))) != 0;
                bool dpadPitch = (padNow & ((ushort)m.ReadU32(MaskPitchInc) | (ushort)m.ReadU32(MaskPitchDec))) != 0;
                bool yieldYaw = (right && x != 0f) || (Tank && left && tankLeft.X != 0f) || gTurn != 0f
                                || _rampTurnLive || dpadTurn;
                bool yieldPitch = (right && y != 0f) || gPitch != 0f || _rampPitchLive || dpadPitch;
                (mTurn, mPitch) = MouseAim.Steer(m, mTurn, mPitch, (int)m.ReadU32(TurnRate), yieldYaw, yieldPitch);
                // A held button takes the axis from the mouse as it is: no release
                // tick, which would zero the velocity and clear the button for a tick
                // before the game's own branch turned again.
                if (dpadTurn) _mouseTurn = false;
                if (dpadPitch) _mousePitch = false;
            }
            else MouseAim.Reset();
        }
        if (!right && !left && !gyro && mTurn == 0f && mPitch == 0f && !_mouseTurn && !_mousePitch)
        {
            _rampTurnLive = _rampPitchLive = false;
            return;
        }

        // The runtime binds the left stick to the D-pad by default, and the game's
        // turn actions *are* D-pad left/right -- so a left stick pushed sideways
        // turns as well as strafes unless the turn bits are taken away from it.
        // Owning them with a zero step is exactly that: buttons cleared, velocity
        // zeroed, and the D-pad still turns when neither stick is deflected. In 3D
        // the same ownership is what makes the sideways push the stick's turn.
        var (lx, ly) = !left ? (0f, 0f)
                     : Tank ? tankLeft
                     : Shape(Controller.LeftX, Controller.LeftY, MoveDeadzone, MoveCurve);
        bool leftActive = left && (lx != 0f || ly != 0f);

        // A released axis still needs one frame to stop the velocity the game
        // would otherwise ramp down; _ownedTurn/_ownedPitch are what keep us in
        // the hook for that frame and out of it afterwards.
        //
        // For the mouse that frame is not optional. A stick can be *held* still at
        // the centre, so leaving the ramp-down alone is a defensible feel and an
        // option; a mouse that has stopped moving has said nothing at all, and a
        // camera that carries on for a third of a second afterwards is simply
        // wrong. Hence the separate flags, which ignore CameraInstantStop.
        // A direct stick (StickEase off) stops dead as well, having no ease to stop by.
        bool direct = !StickEase && !Tank;
        bool stopDead = CameraInstantStop || direct;
        bool releaseTurn  = (stopDead && _ownedTurn)  || _mouseTurn;
        bool releasePitch = (stopDead && _ownedPitch) || _mousePitch;

        if (x == 0f && y == 0f && !leftActive && mTurn == 0f && mPitch == 0f && !gyro &&
            !releaseTurn && !releasePitch)
        {
            _accelT = 0f;
            _accelTick = 0;
            // The game decays a released velocity itself while nothing is in the
            // hook, so the next push starts from wherever that decay has got to.
            _rampTurnLive = _rampPitchLive = false;
            return;
        }

        // The ramp is a held stick's, so it is neither fed nor applied by a mouse.
        float mult = right ? Accelerate(RawMag(Controller.RightX, Controller.RightY, LookDeadzone)) : 1f;

        ushort pad = m.ReadU16(Pad);
        int rate = (int)m.ReadU32(TurnRate);
        if (rate <= 0) { _rampTurnLive = _rampPitchLive = false; return; }

        // What the stick asks for: the game's own rate at full deflection times the
        // sensitivity, as the D-pad turns, and 32 a tick for the look, L2/R2's limit.
        // 3D turns with the left stick's sideways deflection, without the look ramp,
        // which is the right stick's, for aiming. Stick right turns right, the
        // decreasing branch, in both.
        float turnWant = -x * rate * TurnSens * mult * (InvertTurn ? -1f : 1f);
        float pitchWant = y * PitchVelMax * PitchSens * mult * (InvertPitch ? -1f : 1f);
        bool tankTurn = Tank && leftActive;
        if (tankTurn) turnWant = -lx * rate * TurnSens * (InvertTurn ? -1f : 1f);

        // The stick's share of each step, so FrameSmoothing can tell how much of
        // what the game turns by this tick was the mouse's (Mouse.NoteSpent): the
        // wanted speed eased toward by the game's own acceleration, as a held D-pad
        // and L2/R2 are, so the stick neither starts nor stops faster than they do.
        // A direct stick skips the ease and is the speed it asks for at once.
        float stickTurn = direct ? (x != 0f ? turnWant : 0f)
            : x != 0f || tankTurn || _rampTurnLive
            ? Ramp(turnWant, ref _rampTurn, ref _rampTurnLive, m, TurnVel, rate >> 2, _mouseTurn)
            : 0f;
        float stickPitch = direct ? (y != 0f ? pitchWant : 0f)
            : y != 0f || _rampPitchLive
            ? Ramp(pitchWant, ref _rampPitch, ref _rampPitchLive, m, PitchVel, PitchAccel, _mousePitch)
            : 0f;
        if (direct) _rampTurnLive = _rampPitchLive = false;
        // The instant stop asks for the opposite, and is the player's to choose.
        if (CameraInstantStop && x == 0f && !Tank) { stickTurn = 0f; _rampTurnLive = false; }
        if (CameraInstantStop && y == 0f) { stickPitch = 0f; _rampPitchLive = false; }
        float tankWant = tankTurn ? turnWant : 0f;
        stickTurn += gTurn;
        stickPitch += gPitch;
        Mouse.NoteSpent(m, mTurn, stickTurn, mPitch, stickPitch);

        if (x != 0f || leftActive || mTurn != 0f || gTurn != 0f || releaseTurn || _rampTurnLive)
        {
            // Stick right turns right, and turning right is the *decreasing*
            // branch: the mask that increases yaw is the game's Left, which the
            // probe's table dump is the evidence for. Hence the negation. The
            // mouse arrives already in that convention, from Mouse.TakeLook.
            int step = Step(stickTurn + mTurn,
                            ref _turnCarry, Ceiling(rate * OverspeedCap, MathF.Abs(mTurn) + MathF.Abs(gTurn)));
            if (stickTurn != 0f) _turnCarryMouse = false;
            else if (mTurn != 0f) _turnCarryMouse = true;
            pad = Drive(m, pad, TurnVel, step, rate >> 2, rate, MaskTurnInc, MaskTurnDec);
            // 3D's release is the D-pad's: the stick back inside its deadzone leaves
            // the velocity to the game's decay, the ramp-down a released Left/Right
            // gets, rather than CameraInstantStop's zero, which is the right stick's.
            _ownedTurn = step != 0 && !Tank;
            _mouseTurn = mTurn != 0f || gTurn != 0f;
        }

        if (y != 0f || mPitch != 0f || gPitch != 0f || releasePitch || _rampPitchLive)
        {
            // Screen up is a negative stick Y, and looking up is the pitch
            // velocity going *down*: the increasing branch is the game's R2,
            // which looks down. That is the one sign in this patch no static
            // evidence settled -- the mask table gives the button but not which
            // way the view tips -- so it was fixed by playing it, and the sticks
            // agree with the D-pad's own L2/R2 now.
            int step = Step(stickPitch + mPitch,
                            ref _pitchCarry, Ceiling(PitchVelMax * OverspeedCap, MathF.Abs(mPitch) + MathF.Abs(gPitch)));
            if (stickPitch != 0f) _pitchCarryMouse = false;
            else if (mPitch != 0f) _pitchCarryMouse = true;
            pad = Drive(m, pad, PitchVel, step, PitchAccel, PitchVelMax, MaskPitchInc, MaskPitchDec);
            _ownedPitch = step != 0;
            _mousePitch = mPitch != 0f || gPitch != 0f;
        }

        m.WriteU16(Pad, pad);
        if (x != 0f || y != 0f) AnalogProbe.NoteLook(x, y, rate);
        if (tankTurn) AnalogProbe.NoteTank(lx, tankWant, _rampTurn, rate);
        if (mTurn != 0f || mPitch != 0f) AnalogProbe.NoteMouse(mTurn, mPitch);
    }

    /// <summary>
    /// The per-frame ceiling for an axis: the stick's, or the mouse's larger one
    /// while the mouse is driving.
    ///
    /// A stick asks for a *rate*, and four times the game's own is already faster
    /// than any button can turn. A mouse asks for an *amount*, and a flick that
    /// takes a tenth of a second arrives here as three or four very large frames,
    /// so the stick's ceiling would turn every fast turn into a slow one.
    /// </summary>
    static int Ceiling(int stick, float mouse) => mouse == 0f ? stick : Math.Max(stick, Mouse.StepCap);

    // Walking and strafing. Same three-branch shape, twice, off this frame's
    // walk speed; the two velocities are then turned into a heading off the yaw
    // and applied through the game's own collision path.
    public static void BeforeMove(CpuContext c, IMemory m)
    {
        if (!Enabled || !AnalogMove) return;

        // 3D: the stick's sideways deflection turns (BeforeLook), not strafes, and the
        // walk is read on its own deadzone, so a turn does not creep forward or back.
        // L1 and R1 still strafe: the strafe bits are left alone when x is zero.
        var (x, y) = Tank ? (0f, TankLeft.Y)
                          : Shape(Controller.LeftX, Controller.LeftY, MoveDeadzone, MoveCurve);
        if (x == 0f && y == 0f) return;

        ushort pad = m.ReadU16(Pad);
        int speed = (int)m.ReadU32(MoveSpeed);
        if (speed <= 0) return;
        int accel = speed >> 2;

        if (y != 0f)
        {
            int step = Step(-y * speed * MoveSens * (InvertForward ? -1f : 1f), ref _fwdCarry, speed);
            pad = Drive(m, pad, FwdVel, step, accel, speed, MaskFwdInc, MaskFwdDec);
        }

        if (x != 0f)
        {
            int step = Step(x * speed * MoveSens * (InvertStrafe ? -1f : 1f), ref _strafeCarry, speed);
            pad = Drive(m, pad, StrafeVel, step, accel, speed, MaskStrafeInc, MaskStrafeDec);
        }

        m.WriteU16(Pad, pad);
        AnalogProbe.NoteMove(x, y, speed);
    }

    /// <summary>
    /// One tick of the game's own ease toward a velocity: the step moves toward
    /// <paramref name="want"/> by at most <paramref name="accel"/> (the turn's
    /// rate&gt;&gt;2, four or five ticks to full; the look's 3, eleven), up and down
    /// alike, as the button branches and the released branch of func_80028DB8 move
    /// it. It carries on from the game's velocity when the stick takes over from a
    /// D-pad turn or its decay, but not from a step the mouse or gyro drove, which
    /// is an amount and not a speed. Live until it has come back to rest.
    /// </summary>
    static float Ramp(float want, ref float state, ref bool live, IMemory m, uint velAddr, int accel, bool wasMouse)
    {
        float from = live ? state : wasMouse ? 0f : (short)m.ReadU16(velAddr);
        state = Math.Clamp(want, from - accel, from + accel);
        live = state != 0f || want != 0f;
        return state;
    }

    /// <summary>
    /// Pre-load a velocity word so the game's own accumulate lands on `step`,
    /// and assert the button that makes it take that branch. A zero step clears
    /// both buttons and zeroes the velocity, which is the game's idle state --
    /// not its decay, because the stick is the authority while it is deflected.
    /// </summary>
    static ushort Drive(IMemory m, ushort pad, uint velAddr, int step, int accel, int clamp,
                        uint maskInc, uint maskDec)
    {
        ushort inc = (ushort)m.ReadU32(maskInc);
        ushort dec = (ushort)m.ReadU32(maskDec);
        pad = (ushort)(pad & ~(inc | dec));

        if (step == 0) { m.WriteU16(velAddr, 0); return pad; }

        if (Math.Abs(step) <= clamp)
        {
            // Inside the game's own limit: assert the button and let its
            // accumulate carry the pre-load up to the target.
            m.WriteU16(velAddr, (ushort)(short)(step > 0 ? step - accel : step + accel));
            pad |= step > 0 ? inc : dec;
        }
        else
        {
            // Past it. The limit lives only in the two button branches -- the
            // branch that runs with neither button down decays the velocity by
            // the same step and applies whatever is left, unclamped. So pre-load
            // the other way and assert nothing: the decay lands on the target and
            // the camera goes faster than any button could drive it.
            m.WriteU16(velAddr, (ushort)(short)(step > 0 ? step + accel : step - accel));
        }

        return pad;
    }

    /// <summary>
    /// The look-speed multiplier for this frame: 1 at rest, rising to
    /// <see cref="LookAccelMax"/> over <see cref="LookAccelTime"/> while the stick
    /// is held past the threshold, and falling back three times faster than it
    /// built.
    /// </summary>
    static float Accelerate(float mag)
    {
        if (!LookAccel) { _accelT = 0f; return 1f; }

        long now = Environment.TickCount64;
        float dt = _accelTick == 0 ? 0f : Math.Clamp((now - _accelTick) / 1000f, 0f, 0.1f);
        _accelTick = now;

        _accelT = Math.Clamp(mag >= LookAccelThreshold ? _accelT + dt : _accelT - dt * 3f,
                             0f, LookAccelTime);

        float t = LookAccelTime <= 0f ? 1f : _accelT / LookAccelTime;
        return 1f + (LookAccelMax - 1f) * t;
    }

    /// <summary>
    /// A stick's deflection as 0..1 past the deadzone, before any curve. The
    /// acceleration ramp keys off this rather than the shaped value, so the curve
    /// and the ramp stay independent settings.
    /// </summary>
    static float RawMag(byte bx, byte by, float deadzone)
    {
        float x = (bx - 128) / 127f;
        float y = (by - 128) / 127f;
        float mag = MathF.Sqrt(x * x + y * y);
        return mag <= deadzone ? 0f : Math.Clamp((mag - deadzone) / (1f - deadzone), 0f, 1f);
    }

    /// <summary>
    /// Integer part of the wanted step, with the fraction carried to next frame
    /// and the result held inside the given ceiling.
    /// </summary>
    static int Step(float want, ref float carry, int limit)
    {
        float total = want + carry;
        int step = (int)MathF.Truncate(total);
        carry = total - step;
        return Math.Clamp(step, -limit, limit);
    }

    /// <summary>
    /// One stick, as a radial-deadzoned and curved vector. The bytes are the
    /// runtime's own 0..255 with 0x80 centre (InputManager.AxisToByte, which
    /// already applies a 1.3x gain, so the byte saturates a little before the
    /// stick does).
    /// </summary>
    static (float X, float Y) Shape(byte bx, byte by, float deadzone, float curve)
    {
        float x = (bx - 128) / 127f;
        float y = (by - 128) / 127f;
        float mag = MathF.Sqrt(x * x + y * y);
        if (mag <= deadzone || mag <= 0f) return (0f, 0f);

        float unit = Math.Clamp((mag - deadzone) / (1f - deadzone), 0f, 1f);
        float scaled = MathF.Pow(unit, curve) / mag;
        return (x * scaled, y * scaled);
    }

    /// <summary>The left stick for 3D controls, as (turn, walk): each axis past its
    /// own deadzone, so neither leaks into the other (see TankDeadzone).</summary>
    static (float X, float Y) TankLeft => (
        Axis((Controller.LeftX - 128) / 127f, TankDeadzone, TankTurnCurve),
        Axis((Controller.LeftY - 128) / 127f, TankDeadzone, MoveCurve));

    /// <summary>One axis past an axial deadzone, rescaled to 0..1 and curved, signed.</summary>
    static float Axis(float v, float deadzone, float curve)
    {
        float a = MathF.Abs(v);
        if (a <= deadzone) return 0f;
        return MathF.Sign(v) * MathF.Pow(Math.Clamp((a - deadzone) / (1f - deadzone), 0f, 1f), curve);
    }

    /// <summary>The right stick shaped as the look shapes it, for a turn that is not
    /// the view's (ItemTurn).</summary>
    internal static (float X, float Y) RightStick =>
        Shape(Controller.RightX, Controller.RightY, LookDeadzone, LookCurve);

    /// <summary>
    /// The two sticks as the hooks see them, raw and before any deadzone: the
    /// settings page draws this so a deadzone can be set against the pad in hand
    /// rather than by guessing at where its centre rests.
    /// </summary>
    public static (float Lx, float Ly, float Rx, float Ry) Sticks => (
        (Controller.LeftX - 128) / 127f, (Controller.LeftY - 128) / 127f,
        (Controller.RightX - 128) / 127f, (Controller.RightY - 128) / 127f);

    // ---- environment, then interface.ini ------------------------------------

    // The set is a parameter rather than always this class's own, because
    // <see cref="Mouse"/> keeps the same precedence over the same store and
    // there is no reason for two copies of the rule -- only for two records of
    // which variables were actually set.

    internal static void Env(string name, string key, ref bool value) => Env(name, key, ref value, _fromEnv);

    internal static void Env(string name, string key, ref float value) => Env(name, key, ref value, _fromEnv);

    internal static void Env(string name, string key, ref bool value, HashSet<string> from)
        => Kept.Env(name, key, ref value, from);

    internal static void Env(string name, string key, ref float value, HashSet<string> from)
        => Kept.Env(name, key, ref value, from);

    /// <summary>The saved value, unless the environment already set this key.</summary>
    internal static void Saved(string key, ref bool value) => Saved(key, ref value, _fromEnv);

    internal static void Saved(string key, ref float value) => Saved(key, ref value, _fromEnv);

    internal static void Saved(string key, ref bool value, HashSet<string> from)
        => Kept.Saved(key, ref value, from);

    internal static void Saved(string key, ref float value, HashSet<string> from)
        => Kept.Saved(key, ref value, from);

    internal static void Saved(string key, ref int value, HashSet<string> from)
        => Kept.Saved(key, ref value, from);
}
