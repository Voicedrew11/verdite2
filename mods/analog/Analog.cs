// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.Analog;

/// <summary>
/// Analog camera and movement, bound as a modern twin-stick scheme.
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
///     if      (pad & maskInc)  vel += rate >> 2, clamp to +rate
///     else if (pad & maskDec)  vel -= rate >> 2, clamp to -rate
///     else                     vel decays toward 0
///     angle_or_position += vel
///
/// So the mod does not replace anything and does not touch the position. It
/// pre-loads the velocity word with `target - accel` and asserts the matching
/// button in the game's own pad global; the game's next instruction adds `accel`
/// and lands exactly on `target`, then applies it through its own path --
/// collision, the ±0x2BC pitch limit, the walk-speed normalisation, footsteps
/// and animation all run as they always did, on an amount we chose.
///
/// The buttons are read out of the game's action-mask table at 0x8006E568..5D0
/// rather than hardcoded, so the mod follows whatever the player set in the
/// game's own control-config screen.
///
/// Sticks idle == mod idle: the hooks return before touching anything, so the
/// D-pad and keyboard behave exactly as they do with the mod unloaded.
/// </summary>
public sealed class AnalogMod : IMod
{
    // ---- the game's per-frame control state (GAME.EXE, all off 0x801A0000) ----
    const uint Pad        = 0x80199554;   // u16, the word PadRead(1) stored this frame; active high
    const uint StrafeVel  = 0x8019953E;   // s16, + moves along yaw-0x400
    const uint FwdVel     = 0x80199540;   // s16, + moves along yaw
    const uint TurnVel    = 0x80199544;   // s16, added to yaw each frame
    const uint PitchVel   = 0x80199546;   // s16, added to pitch each frame
    const uint MoveSpeed  = 0x80199558;   // u32, this frame's walk speed  (0xC8 = 200)
    const uint TurnRate   = 0x8019955C;   // u32, this frame's turn rate   (0x1C, 0x23 running)

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

    // The accelerations the game applies once we have asserted the button.
    // Turn and move are a quarter of that frame's rate; pitch is a flat 3.
    const int PitchAccel = 3;
    const int PitchVelMax = 32;

    static bool _enabled = true;
    static bool _analogLook = true;
    static bool _analogMove = true;

    static float _lookDeadzone = 0.15f;
    static float _moveDeadzone = 0.15f;
    static float _lookCurve = 1.6f;
    static float _moveCurve = 1.0f;
    static float _turnSens = 1.0f;
    static float _pitchSens = 1.0f;
    static float _moveSens = 1.0f;

    static bool _invertTurn;
    static bool _invertPitch;
    static bool _invertStrafe;
    static bool _invertForward;

    // Fractional remainders. Not optional: at 30 fps a small stick deflection
    // rounds to a zero step every frame, and the player would simply not move.
    static float _turnCarry, _pitchCarry, _fwdCarry, _strafeCarry;

    public void OnLoad()
    {
        ReadEnv("KF2_ANALOG", ref _enabled);
        ReadEnv("KF2_ANALOG_LOOK", ref _analogLook);
        ReadEnv("KF2_ANALOG_MOVE_ENABLE", ref _analogMove);
        ReadEnv("KF2_ANALOG_INVERTY", ref _invertPitch);
        ReadEnv("KF2_ANALOG_INVERTTURN", ref _invertTurn);
        ReadEnv("KF2_ANALOG_INVERTSTRAFE", ref _invertStrafe);
        ReadEnv("KF2_ANALOG_INVERTFWD", ref _invertForward);
        ReadEnv("KF2_ANALOG_DEADZONE", ref _lookDeadzone);
        _moveDeadzone = _lookDeadzone;
        ReadEnv("KF2_ANALOG_MOVEDEADZONE", ref _moveDeadzone);
        ReadEnv("KF2_ANALOG_CURVE", ref _lookCurve);
        ReadEnv("KF2_ANALOG_MOVECURVE", ref _moveCurve);
        ReadEnv("KF2_ANALOG_TURN", ref _turnSens);
        ReadEnv("KF2_ANALOG_PITCH", ref _pitchSens);
        ReadEnv("KF2_ANALOG_MOVE", ref _moveSens);

        AnalogProbe.Configure();

        Console.WriteLine($"[analog] twin-stick control {(_enabled ? "on" : "off")} " +
                          $"(deadzone {_lookDeadzone:0.##}, turn x{_turnSens:0.##}, move x{_moveSens:0.##})");
        Console.WriteLine("[analog] left stick walks and strafes, right stick turns and looks; " +
                          "the D-pad is untouched while the sticks are centred");
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped("Writes the turn, pitch, forward and strafe velocities the game would " +
                          "have accumulated from held buttons, scaled by stick deflection. The " +
                          "game's own movement, collision and limits still run.");
        ImGui.Checkbox("Enabled", ref _enabled);
        ImGui.Checkbox("Analog look (right stick)", ref _analogLook);
        ImGui.Checkbox("Analog move (left stick)", ref _analogMove);

        ImGui.Separator();
        ImGui.SliderFloat("Turn sensitivity", ref _turnSens, 0.1f, 3f);
        ImGui.SliderFloat("Look sensitivity", ref _pitchSens, 0.1f, 3f);
        ImGui.SliderFloat("Move sensitivity", ref _moveSens, 0.1f, 1.5f);
        ImGui.SliderFloat("Look deadzone", ref _lookDeadzone, 0f, 0.5f);
        ImGui.SliderFloat("Move deadzone", ref _moveDeadzone, 0f, 0.5f);
        ImGui.SliderFloat("Look curve", ref _lookCurve, 1f, 3f);
        ImGui.SliderFloat("Move curve", ref _moveCurve, 1f, 3f);

        ImGui.Separator();
        ImGui.TextWrapped("Which way \"+\" points is the game's convention, not ours -- flip an " +
                          "axis here if it runs backwards.");
        ImGui.Checkbox("Invert look Y", ref _invertPitch);
        ImGui.SameLine(); ImGui.Checkbox("Invert turn", ref _invertTurn);
        ImGui.Checkbox("Invert strafe", ref _invertStrafe);
        ImGui.SameLine(); ImGui.Checkbox("Invert forward", ref _invertForward);

        ImGui.Separator();
        AnalogProbe.DrawSettings();
    }

    // Turning and looking. The function reads the pad, accumulates turn and
    // pitch velocity, then does `yaw = (yaw + turnVel) & 0xFFF` and the same for
    // pitch with a ±0x2BC limit -- so setting the velocity here is the whole job.
    [PreHook("game", Address = 0x80028DB8)]
    static void BeforeLook(CpuContext c, IMemory m)
    {
        if (!_enabled || !_analogLook) return;

        var (x, y) = Shape(Controller.RightX, Controller.RightY, _lookDeadzone, _lookCurve);

        // The runtime binds the left stick to the D-pad by default, and the game's
        // turn actions *are* D-pad left/right -- so a left stick pushed sideways
        // turns as well as strafes unless the turn bits are taken away from it.
        // Owning them with a zero step is exactly that: buttons cleared, velocity
        // zeroed, and the D-pad still turns when neither stick is deflected.
        var (lx, ly) = Shape(Controller.LeftX, Controller.LeftY, _moveDeadzone, _moveCurve);
        bool leftActive = _analogMove && (lx != 0f || ly != 0f);

        if (x == 0f && y == 0f && !leftActive) return;

        ushort pad = m.ReadU16(Pad);
        int rate = (int)m.ReadU32(TurnRate);
        if (rate <= 0) return;

        if (x != 0f || leftActive)
        {
            // Stick right turns right, and turning right is the *decreasing*
            // branch: the mask that increases yaw is the game's Left, which the
            // probe's table dump is the evidence for. Hence the negation.
            int step = Step(-x * rate * _turnSens * (_invertTurn ? -1f : 1f), ref _turnCarry, rate);
            pad = Drive(m, pad, TurnVel, step, rate >> 2, MaskTurnInc, MaskTurnDec);
        }

        if (y != 0f)
        {
            // Screen up is a negative stick Y and the increasing branch is the
            // game's R2. Which of L2/R2 looks up is the one direction here that
            // no static evidence settles -- flip "Invert look Y" if it is wrong.
            int step = Step(-y * PitchVelMax * _pitchSens * (_invertPitch ? -1f : 1f),
                            ref _pitchCarry, PitchVelMax);
            pad = Drive(m, pad, PitchVel, step, PitchAccel, MaskPitchInc, MaskPitchDec);
        }

        m.WriteU16(Pad, pad);
        if (x != 0f || y != 0f) AnalogProbe.NoteLook(x, y, rate);
    }

    // Walking and strafing. Same three-branch shape, twice, off this frame's
    // walk speed; the two velocities are then turned into a heading off the yaw
    // and applied through the game's own collision path.
    [PreHook("game", Address = 0x800290D4)]
    static void BeforeMove(CpuContext c, IMemory m)
    {
        if (!_enabled || !_analogMove) return;

        var (x, y) = Shape(Controller.LeftX, Controller.LeftY, _moveDeadzone, _moveCurve);
        if (x == 0f && y == 0f) return;

        ushort pad = m.ReadU16(Pad);
        int speed = (int)m.ReadU32(MoveSpeed);
        if (speed <= 0) return;
        int accel = speed >> 2;

        if (y != 0f)
        {
            int step = Step(-y * speed * _moveSens * (_invertForward ? -1f : 1f), ref _fwdCarry, speed);
            pad = Drive(m, pad, FwdVel, step, accel, MaskFwdInc, MaskFwdDec);
        }

        if (x != 0f)
        {
            int step = Step(x * speed * _moveSens * (_invertStrafe ? -1f : 1f), ref _strafeCarry, speed);
            pad = Drive(m, pad, StrafeVel, step, accel, MaskStrafeInc, MaskStrafeDec);
        }

        m.WriteU16(Pad, pad);
        AnalogProbe.NoteMove(x, y, speed);
    }

    /// <summary>
    /// Pre-load a velocity word so the game's own accumulate lands on `step`,
    /// and assert the button that makes it take that branch. A zero step clears
    /// both buttons and zeroes the velocity, which is the game's idle state --
    /// not its decay, because the stick is the authority while it is deflected.
    /// </summary>
    static ushort Drive(IMemory m, ushort pad, uint velAddr, int step, int accel, uint maskInc, uint maskDec)
    {
        ushort inc = (ushort)m.ReadU32(maskInc);
        ushort dec = (ushort)m.ReadU32(maskDec);
        pad = (ushort)(pad & ~(inc | dec));

        if (step > 0) { m.WriteU16(velAddr, (ushort)(short)(step - accel)); pad |= inc; }
        else if (step < 0) { m.WriteU16(velAddr, (ushort)(short)(step + accel)); pad |= dec; }
        else m.WriteU16(velAddr, 0);

        return pad;
    }

    /// <summary>
    /// Integer part of the wanted step, with the fraction carried to next frame
    /// and the result held inside the game's own clamp.
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

    static void ReadEnv(string name, ref bool value)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return;
        v = v.Trim().ToLowerInvariant();
        value = v is "1" or "on" or "true" or "yes";
    }

    static void ReadEnv(string name, ref float value)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (float.TryParse(v, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float f))
            value = f;
    }
}
