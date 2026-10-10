using System.Diagnostics;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;

namespace Kf2;

/// <summary>
/// Aim a drawn bow with the pad's gyroscope: while the bow or the arbalest is in
/// hand and being drawn, turning and tipping the pad turns and tips the view, one
/// to one.
///
///     KF2_GYROAIM=0           off (on by default, as Verdite3's since 2026-10-08)
///     KF2_GYROAIM_PROBE=1     a line a second: the weapon, the clock, the clip, the
///                             attack button, vblanks gated in, the units handed to
///                             the look
///
/// **Drawn** is <see cref="Bow.Drawn"/>: weapon 16 or 17 in <c>0x801994AF</c>, the
/// clip byte <c>0x801994AE</c> at 0 and the swing clock <c>0x801994A4</c> running,
/// from the press that nocks the arrow, through the draw, to the release. The game
/// holds full draw while Square is held, so the aim lasts as long as the hold.
///
/// The rate is integrated every vblank while the gate holds, and the look routine's
/// tick spends the sum through <see cref="Analog.BeforeLook"/> beside the right
/// stick, as an amount the way the mouse's is. A sum the look routine has not spent
/// in <see cref="StaleMs"/> (a menu, a cutscene) is dropped. See "Gyro aim with a
/// drawn bow" in docs/INPUT.md.
/// </summary>
public static class GyroAim
{
    const uint AttackMask = 0x8006E570;     // u32, entry 2 of the action mask table: Square
    const uint Pad = 0x80199554;            // u16, this tick's pad word, active high

    /// <summary>A gyro rate below this, in radians a second, is a hand at rest: it
    /// keeps a pad's drift from creeping the aim.</summary>
    const float GyroRest = 0.05f;

    const float Units = 4096f / (2f * MathF.PI);     // angle units a radian

    /// <summary>A sum older than this is not the hand's any more.</summary>
    const double StaleMs = 250;

    /// <summary>The world has stopped when the weapon routine has not run for this
    /// long (<see cref="Rumble"/>'s rule).</summary>
    const long StillMs = 150;

    public const string OnKey = "kf2.gyroaim.on";

    public static bool Enabled = true;

    static readonly HashSet<string> _fromEnv = [];
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _lastMs, _takenMs;
    static float _turn, _pitch;

    static bool _probe;
    static double _probeAt;
    static long _gated, _ticks;
    static float _spentTurn, _spentPitch;

    public static void Configure()
    {
        Kept.Env("KF2_GYROAIM", OnKey, ref Enabled, _fromEnv);
        _probe = Environment.GetEnvironmentVariable("KF2_GYROAIM_PROBE")?.Trim() is "1" or "on" or "true";
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Kept.Saved(OnKey, ref Enabled, _fromEnv);
            SetEnabled(Enabled);
        });
        Event.AddListener<VSyncEvent>(_ => Integrate());
        Bow.Install();
    }

    /// <summary>The pad streams its gyroscope (a larger report, runtime 0102) only
    /// while this is on.</summary>
    public static void SetEnabled(bool on)
    {
        Enabled = on;
        Controller.WantGyro = on;
        if (!on) _turn = _pitch = 0f;
    }

    static void Integrate()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        float dt = (float)Math.Clamp((now - _lastMs) / 1000.0, 0.0, 0.1);
        _lastMs = now;
        if (_probe) Probe(now);

        var m = Bow.Memory;
        if (!Enabled || !Controller.Gyro || m == null ||
            Environment.TickCount64 - Bow.TickMs > StillMs || !Bow.Drawn(m, out _))
        {
            _turn = _pitch = 0f;
            return;
        }
        if (now - _takenMs > StaleMs) _turn = _pitch = 0f;
        if (_probe) _gated++;

        // SDL's axes for a pad held in front of you: x across it, y up, z toward you,
        // anticlockwise positive. Turning the pad left (+y) turns the view left, which
        // is yaw increasing (the game's Left increases it). Tipping the far edge up
        // (+x) looks up, which is pitch decreasing (R2, which increases it, looks
        // down) and the way the mouse's pitch goes when it is pushed away, so it
        // takes the mouse's inversion with it.
        float gx = Rest(Controller.GyroX), gy = Rest(Controller.GyroY);
        _turn += gy * Units * dt;
        _pitch -= gx * Units * dt * (Mouse.InvertY ? -1f : 1f);
    }

    static float Rest(float rate) => MathF.Abs(rate) < GyroRest ? 0f : rate;

    /// <summary>What the gyro asks the look routine for this tick, in angle units,
    /// emptied as it is taken. Called once a tick from <see cref="Analog.BeforeLook"/>.</summary>
    internal static (float Turn, float Pitch) Take()
    {
        _takenMs = _clock.Elapsed.TotalMilliseconds;
        var taken = (_turn, _pitch);
        _turn = _pitch = 0f;
        if (_probe && (taken._turn != 0f || taken._pitch != 0f))
        {
            _ticks++;
            _spentTurn += taken._turn;
            _spentPitch += taken._pitch;
        }
        return taken;
    }

    static void Probe(double now)
    {
        if (now - _probeAt < 1000) return;
        _probeAt = now;
        var m = Bow.Memory;
        if (m == null) return;
        Console.WriteLine($"[KF2] gyro aim: {(Enabled ? "on" : "off")}, pad gyro {(Controller.Gyro ? "on" : "off")}, " +
                          $"weapon {m.ReadU8(Bow.WeaponSlot)}, clock {(short)m.ReadU16(Bow.SwingClock)}, clip {m.ReadU8(Bow.ClipByte)}, " +
                          $"attack {((m.ReadU16(Pad) & m.ReadU32(AttackMask)) != 0 ? "held" : "up")}, " +
                          $"vblanks drawn {_gated}, ticks spent {_ticks}, turn {_spentTurn:0} pitch {_spentPitch:0}");
        _gated = _ticks = 0;
        _spentTurn = _spentPitch = 0f;
    }
}
