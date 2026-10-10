using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// The bow, read off the game once a tick: what <see cref="Rumble"/> and
/// <see cref="GyroAim"/> both gate on.
///
/// **The bow is <c>func_800271D0</c>'s branch for weapons 16 (BOW) and 17
/// (ARBALEST)**, the equipped-weapon byte <c>u8 0x801994AF</c> (<c>0xFF</c> none).
/// The press (<c>func_800262C8(0)</c>, from stage 3's just-pressed Square) sets the
/// clip byte <c>u8 0x801994AE</c> to 0 and the swing clock <c>s16 0x801994A4</c> to
/// 0. While the clip byte is 0 the bow is **drawn**: on the clock's first tick an
/// arrow is nocked (one ARROW FOR THE BOW, item 117, for the bow; one ELF'S BOLT,
/// 118, for the arbalest, if any is held), and each tick adds the weapon record's
/// <c>+0x1C</c> until the clock holds at <c>0x0FFF</c>, full draw, for as long as
/// Square is held. Letting go **looses** it: clip 1 and the clock back at 0, then
/// stepped <c>0x190</c> a tick to -1. So the tension is the clock over
/// <c>0x0FFF</c>, read off the game rather than timed here. See "The bow and the
/// arbalest" in docs/GAME_INTERNALS.md.
///
/// A post on <c>func_800271D0</c> is the tick: stage 3 runs it once a tick
/// whatever the weapon, and not at all under a menu, so the time it last ran is
/// how a reader tells a drawn bow from a world that has stopped with one drawn.
///
///     KF2_RUMBLE_PROBE=2      also a line each tick the bow's state changes
/// </summary>
internal static class Bow
{
    public const uint WeaponSlot = 0x801994AF;  // u8, the equipped weapon id, 0xFF none
    public const uint SwingClock = 0x801994A4;  // s16, -1 idle; the draw, then the loose
    public const uint ClipByte = 0x801994AE;    // u8, for a bow: 0 drawn, 1 loosed
    public const uint DrawPower = 0x8019942E;   // u16, 0..5000 past the record's +0x1E
    public const uint Nocked = 0x801994A0;      // u32, the arrow's object, 0 none
    public const uint Arrows = 0x8009B5A1;      // u8, ARROW FOR THE BOW held
    public const uint Bolts = 0x8009B5A2;       // u8, ELF'S BOLT held
    public const int BowId = 16, ArbalestId = 17;
    public const int FullDraw = 0x0FFF;

    const uint WeaponTick = 0x800271D0;

    /// <summary>The guest memory, from the last tick.</summary>
    internal static IMemory? Memory { get; private set; }

    /// <summary>When the weapon routine last ran (Environment.TickCount64).</summary>
    internal static long TickMs { get; private set; }

    internal static bool Trace;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.bow",
        Name = "Bow",
        Version = "1.0",
        Description = "Reads the bow's draw and loose off the weapon routine for rumble and gyro aim.",
    };

    static bool _installed, _hooked;
    static string _last = "";

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        HookAttach.OnOverlayLoad("bow", Attach, "rumble and gyro aim will not see a bow");
    }

    static bool Attach()
    {
        if (_hooked) return true;
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("game", null, WeaponTick);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] bow: no game function at 0x{WeaponTick:X8}");
            return false;
        }
        HookManager.AddPost(_self, target,
            typeof(Bow).GetMethod(nameof(AfterWeaponTick), BindingFlags.Public | BindingFlags.Static)!);
        HookManager.Commit();
        _hooked = HookAttach.Installed(target);
        Console.WriteLine(_hooked
            ? $"[KF2] bow: reading the draw after func_{WeaponTick:X8}"
            : $"[KF2] bow: func_{WeaponTick:X8} did not commit");
        return _hooked;
    }

    public static void AfterWeaponTick(CpuContext c, IMemory m)
    {
        Memory = m;
        TickMs = Environment.TickCount64;
        if (!Trace) return;

        int weapon = m.ReadU8(WeaponSlot);
        if (weapon is not (BowId or ArbalestId)) return;
        string now = $"clock {(short)m.ReadU16(SwingClock)}, clip {m.ReadU8(ClipByte)}, " +
                     $"power {m.ReadU16(DrawPower)}, nocked 0x{m.ReadU32(Nocked):X8}, " +
                     $"arrows {m.ReadU8(Arrows)}, bolts {m.ReadU8(Bolts)}";
        if (now == _last) return;
        _last = now;
        Console.WriteLine($"[KF2] bow: weapon {weapon}, {now}");
    }

    /// <summary>A bow in hand and being drawn: from the nock, through the draw, to
    /// the release.</summary>
    internal static bool Drawn(IMemory m, out int clock)
    {
        clock = (short)m.ReadU16(SwingClock);
        int weapon = m.ReadU8(WeaponSlot);
        return weapon is BowId or ArbalestId && m.ReadU8(ClipByte) == 0 && clock >= 0;
    }

    internal static bool InHand(IMemory m) => m.ReadU8(WeaponSlot) is BowId or ArbalestId;
}

/// <summary>
/// Rumble the pad as a bow is drawn and loosed: two-motor rumble on any pad that
/// has it, HD rumble on a Switch Pro Controller or a single Joy-Con (runtime 0104),
/// the actuators of a DualSense over USB (runtime 0105).
///
///     KF2_RUMBLE=0            no rumble (on by default)
///     KF2_RUMBLE_HD=0         two-motor rumble on a Switch pad and a DualSense too
///                             (HD on by default)
///     KF2_RUMBLE_PROBE=1      a line a second: the phase, the tension, waves asked, HD;
///                             2 also a line each tick the bow's state changes
///     KF2_RUMBLE_TEST=1       from 3 s after start-up, the bow three times over without
///                             one: nock, a 1.5 s draw, 1 s at full draw, the loose
///
/// The bow's state is <see cref="Bow"/>'s. The waves: a click as the arrow is
/// nocked; a creak while it is drawn, low and grained, rising in pitch and
/// strength with the tension; at full draw a steady strain with a slow tremble;
/// and on the loose a twang that dies away in a fifth of a second, as strong as
/// the draw was. A two-motor pad takes the amplitudes, the low band on the large
/// motor. Verdite3's, unchanged but for the addresses. See "Rumble" in
/// docs/INPUT.md.
/// </summary>
public static class Rumble
{
    const double NockMs = 45, TwangMs = 200, TwangDecayMs = 50;

    /// <summary>The world has stopped (a menu, a cutscene) when the weapon routine has
    /// not run for this long, three ticks; a held draw's rumble stops with it.</summary>
    const long StillMs = 150;

    /// <summary>How long one grain of the creak lasts.</summary>
    const double GrainMs = 28;

    public const string OnKey = "kf2.rumble.on";
    public const string HdKey = "kf2.rumble.hd";

    public static bool Enabled = true;
    public static bool Hd = true;

    static readonly HashSet<string> _fromEnv = [];
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static readonly Random _grain = new(0x271D0);

    static bool _drawing;
    static float _tension, _power;
    static double _drawnMs, _loosedMs = double.NegativeInfinity, _grainMs;
    static float _grainLevel = 1f;

    static bool _test;
    static double _testAt = -1;
    const double TestCycleMs = 3500, TestDrawnMs = 1500, TestLooseMs = 2500;

    static bool _probe;
    static double _probeAt;
    static long _waves;
    static string _phase = "idle";

    public static void Configure()
    {
        Kept.Env("KF2_RUMBLE", OnKey, ref Enabled, _fromEnv);
        Kept.Env("KF2_RUMBLE_HD", HdKey, ref Hd, _fromEnv);
        string? probe = Environment.GetEnvironmentVariable("KF2_RUMBLE_PROBE")?.Trim();
        _probe = probe is "1" or "2" or "on" or "true";
        Bow.Trace = probe == "2";
        _test = Environment.GetEnvironmentVariable("KF2_RUMBLE_TEST")?.Trim() is "1" or "on" or "true";
        _probe |= _test;
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Kept.Saved(OnKey, ref Enabled, _fromEnv);
            Kept.Saved(HdKey, ref Hd, _fromEnv);
            SetEnabled(Enabled);
        });
        Event.AddListener<VSyncEvent>(_ => Step());
        Bow.Install();
    }

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        if (!on) Controller.Rumble = null;
        SetHd(Hd);
    }

    public static void SetHd(bool on)
    {
        Hd = on;
        Controller.WantHdRumble = Enabled && on;
    }

    static void Step()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_probe) Probe(now);

        if (Enabled && _test && Test(now)) return;

        var m = Bow.Memory;
        if (!Enabled || m == null || Environment.TickCount64 - Bow.TickMs > StillMs)
        {
            _drawing = false;
            Ask(null, "idle");
            return;
        }

        bool bow = Bow.InHand(m);
        bool drawing = Bow.Drawn(m, out int clock);
        int clip = m.ReadU8(Bow.ClipByte);
        if (drawing && !_drawing) _drawnMs = now;
        if (!drawing && _drawing && bow && clip == 1 && clock >= 0)
        {
            _loosedMs = now;
            _power = 0.35f + 0.65f * _tension;
        }
        _drawing = drawing;

        if (drawing)
        {
            _tension = Math.Clamp(clock / (float)Bow.FullDraw, 0f, 1f);
            if (now - _drawnMs < NockMs) Ask(Nock, "nock");
            else if (clock >= Bow.FullDraw) Ask(Strain(now), "full draw");
            else Ask(Creak(now, _tension), "drawing");
            return;
        }

        double since = now - _loosedMs;
        if (since < TwangMs)
        {
            Ask(Twang(since, _power), "loosed");
            return;
        }

        Ask(null, "idle");
    }

    static readonly Controller.RumbleWave Nock = new(120f, 0.2f, 400f, 0.55f);

    /// <summary>KF2_RUMBLE_TEST: the bow's waves on a clock of their own, three times,
    /// for a pad on the desk. False before it starts, so the game's own run goes on.</summary>
    static bool Test(double now)
    {
        if (_testAt < 0) _testAt = now + 3000;
        double t = now - _testAt;
        if (t < 0) return false;
        if (t >= 3 * TestCycleMs)
        {
            _test = false;
            Ask(null, "idle");
            return true;
        }

        double u = t % TestCycleMs;
        if (u < NockMs) Ask(Nock, "test: nock");
        else if (u < TestDrawnMs) Ask(Creak(now, _tension = (float)((u - NockMs) / (TestDrawnMs - NockMs))), "test: drawing");
        else if (u < TestLooseMs) Ask(Strain(now), "test: full draw");
        else if (u < TestLooseMs + TwangMs) Ask(Twang(u - TestLooseMs, 1f), "test: loosed");
        else Ask(null, "test: rest");
        return true;
    }

    /// <summary>The loose: a twang dying away, as strong as the draw was.</summary>
    static Controller.RumbleWave Twang(double since, float power)
    {
        float e = (float)Math.Exp(-since / TwangDecayMs) * power;
        return new(90f, 0.75f * e, 520f, 0.95f * e);
    }

    /// <summary>The string drawn back: a low creak broken into grains, its pitch and
    /// strength rising with the tension.</summary>
    static Controller.RumbleWave Creak(double now, float t)
    {
        if (now - _grainMs >= GrainMs)
        {
            _grainMs = now;
            _grainLevel = 0.7f + 0.6f * (float)_grain.NextDouble();
        }
        return new(70f + 80f * t, (0.12f + 0.3f * t) * _grainLevel, 170f + 180f * t, 0.04f + 0.14f * t);
    }

    /// <summary>Held at full draw: a steady strain with a slow tremble in it.</summary>
    static Controller.RumbleWave Strain(double now)
    {
        float tremble = MathF.Sin((float)(now / 1000.0 * 2.0 * Math.PI * 7.0));
        return new(150f, 0.3f + 0.07f * tremble, 320f, 0.1f);
    }

    static void Ask(Controller.RumbleWave? wave, string phase)
    {
        _phase = phase;
        // An unchanged wave keeps its record, so the host resends it on its own clock
        // instead of taking it as a new moment.
        if (wave == Controller.Rumble) return;
        Controller.Rumble = wave;
        if (wave != null) _waves++;
    }

    static void Probe(double now)
    {
        if (now - _probeAt < 1000) return;
        _probeAt = now;
        Console.WriteLine($"[KF2] rumble: {(Enabled ? "on" : "off")}, HD {(Controller.HdRumble ? "on" : Hd ? "asked, pad cannot" : "off")}, " +
                          $"{_phase}, tension {_tension:0.00}, waves asked {_waves}");
        _waves = 0;
    }
}
