// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using System.Text;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.Analog;

/// <summary>
/// The measurement that justifies the mod, and the thing to turn on when an axis
/// runs the wrong way.
///
/// It reports what the game's control state actually did this window -- the
/// velocities, the yaw and pitch steps, the walk speed and turn rate the game
/// picked -- next to the stick deflection the mod fed in. Proportionality
/// between the two is the whole claim: a stick at 30% must produce about 30% of
/// the yaw step that full deflection does, and must not produce *zero* steps,
/// which is what the fractional carry exists to prevent.
///
/// It also dumps the action-mask table once. Those 24 words are the game's own
/// control-config mapping, so the dump is the ground truth for which pad bit
/// means "turn left" in this save -- the mod reads the same words rather than
/// hardcoding bits, and the dump is how you check it agreed with reality.
///
/// A plain static class, not a second IMod: ModLoader instantiates every IMod in
/// the assembly, but it scans every type for hooks.
/// </summary>
internal static class AnalogProbe
{
    static bool _on;
    static float _seconds = 10f;

    static bool _dumped;
    static double _windowStart;

    static int _lookFrames, _moveFrames, _frames;
    static float _lastRx, _lastRy, _lastLx, _lastLy;
    static long _yawSteps, _yawStepSum, _pitchStepSum;
    static int _lastYaw = -1, _lastPitch;
    static int _lastRate, _lastSpeed;

    static double Now => Environment.TickCount64 / 1000.0;

    internal static void Configure()
    {
        string? v = Environment.GetEnvironmentVariable("KF2_ANALOG_PROBE");
        if (!string.IsNullOrWhiteSpace(v))
        {
            v = v.Trim().ToLowerInvariant();
            _on = v is "1" or "on" or "true" or "yes";
        }
        _windowStart = Now;
        if (_on) Console.WriteLine($"[analog] probe on, reporting every {_seconds:0.#}s");
    }

    internal static void DrawSettings()
    {
        ImGui.Checkbox("Probe (report control state to the console)", ref _on);
        ImGui.SliderFloat("Probe interval (s)", ref _seconds, 2f, 60f);
    }

    internal static void NoteLook(float x, float y, int rate)
    {
        if (!_on) return;
        _lookFrames++;
        _lastRx = x; _lastRy = y; _lastRate = rate;
    }

    internal static void NoteMove(float x, float y, int speed)
    {
        if (!_on) return;
        _moveFrames++;
        _lastLx = x; _lastLy = y; _lastSpeed = speed;
    }

    // Once per frame, after the whole input stage has run: this is where the
    // angles have their final value for the frame.
    [PostHook("game", Address = 0x8002A550)]
    static void AfterInputStage(CpuContext c, IMemory m)
    {
        if (!_on) return;

        if (!_dumped) { DumpMasks(m); _dumped = true; }

        _frames++;
        int yaw = m.ReadU16(AnalogMod.Yaw);
        int pitch = (short)m.ReadU16(AnalogMod.Pitch);
        if (_lastYaw >= 0)
        {
            int dYaw = ((yaw - _lastYaw + 0x800) & 0xFFF) - 0x800;   // shortest way round
            if (dYaw != 0) { _yawSteps++; _yawStepSum += Math.Abs(dYaw); }
            // Pitch lives in the same 12-bit space and is stored either small and
            // positive or as 0xFD44-ish for negative, so it needs the same
            // shortest-way-round or a zero crossing reads as a 4096 jump.
            _pitchStepSum += Math.Abs((((pitch - _lastPitch) & 0xFFF) + 0x800 & 0xFFF) - 0x800);
        }
        _lastYaw = yaw;
        _lastPitch = pitch;

        if (Now - _windowStart < _seconds) return;
        Report(m);
    }

    static void Report(IMemory m)
    {
        double window = Now - _windowStart;
        _windowStart = Now;

        Console.WriteLine(
            $"[analog] {_frames} frames in {window:0.#}s | " +
            $"look {_lookFrames} move {_moveFrames} | " +
            $"stick R({_lastRx:+0.00;-0.00;0.00},{_lastRy:+0.00;-0.00;0.00}) " +
            $"L({_lastLx:+0.00;-0.00;0.00},{_lastLy:+0.00;-0.00;0.00})");

        Console.WriteLine(
            $"           yaw {m.ReadU16(AnalogMod.Yaw),5}  pitch {(short)m.ReadU16(AnalogMod.Pitch),5}  " +
            $"turnVel {(short)m.ReadU16(0x80199544),4}  pitchVel {(short)m.ReadU16(0x80199546),4}  " +
            $"fwdVel {(short)m.ReadU16(0x80199540),5}  strafeVel {(short)m.ReadU16(0x8019953E),5}");

        Console.WriteLine(
            $"           rate {_lastRate} speed {_lastSpeed}  walkMag {(short)m.ReadU16(0x80199542),5}  " +
            $"pos ({(int)m.ReadU32(0x801994EC)}, {(int)m.ReadU32(0x801994F0)}, {(int)m.ReadU32(0x801994F4)})  " +
            $"| yaw stepped {_yawSteps}/{_frames} frames, mean |step| " +
            $"{(_yawSteps == 0 ? 0.0 : (double)_yawStepSum / _yawSteps):0.00}, " +
            $"pitch total {_pitchStepSum}");

        _frames = _lookFrames = _moveFrames = 0;
        _yawSteps = _yawStepSum = _pitchStepSum = 0;
    }

    // The game's action -> button mask table. Word-spaced, and the eight the mod
    // drives are named; the rest are printed so the whole table is on the record.
    static void DumpMasks(IMemory m)
    {
        var named = new Dictionary<uint, string>
        {
            [AnalogMod.MaskTurnInc]   = "turn +   (yaw increases)",
            [AnalogMod.MaskTurnDec]   = "turn -   (yaw decreases)",
            [AnalogMod.MaskPitchInc]  = "pitch +  (look up)",
            [AnalogMod.MaskPitchDec]  = "pitch -  (look down)",
            [AnalogMod.MaskFwdInc]    = "walk +   (forward)",
            [AnalogMod.MaskFwdDec]    = "walk -   (back)",
            [AnalogMod.MaskStrafeInc] = "strafe + (yaw-0x400)",
            [AnalogMod.MaskStrafeDec] = "strafe - (yaw+0x400)",
        };

        Console.WriteLine("[analog] action mask table 0x8006E568..0x8006E5D0 " +
                          "(the game's own control config; the mod reads these, it does not assume bits)");
        for (uint a = 0x8006E568; a <= 0x8006E5D0; a += 4)
        {
            uint v = m.ReadU32(a);
            if (v == 0) continue;
            named.TryGetValue(a, out string? role);
            Console.WriteLine($"    0x{a:X8}  0x{v:X8}  {Buttons(v)}{(role == null ? "" : "   <- " + role)}");
        }
    }

    static readonly string[] BitNames =
    [
        "Select", "L3", "R3", "Start", "Up", "Right", "Down", "Left",
        "L2", "R2", "L1", "R1", "Triangle", "Circle", "Cross", "Square",
    ];

    /// <summary>
    /// Name the buttons in a mask.
    ///
    /// The game's pad word carries the two button bytes in the opposite order to
    /// Controller's bit layout -- libetc's PadRead hands back the BIOS buffer's
    /// halves as they sit in memory -- so bit i of a mask is Controller bit
    /// (i + 8) &amp; 15. Decoding it straight makes the movement keys come out as
    /// Triangle/Cross/Square/Circle, which is how this was noticed: swapped, they
    /// read Up/Down/Left/Right with L1/R1 strafing and L2/R2 looking, which is
    /// King's Field's actual control scheme.
    ///
    /// The mod itself never needs this: it ORs the game's own mask words back
    /// into the game's own pad word, so it is layout-agnostic. Only the label is.
    /// </summary>
    static string Buttons(uint mask)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < BitNames.Length; i++)
            if ((mask & (1u << i)) != 0)
            {
                if (sb.Length > 0) sb.Append('+');
                sb.Append(BitNames[(i + 8) & 15]);
            }
        return sb.Length == 0 ? "-" : sb.ToString();
    }
}
