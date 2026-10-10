using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// The bow, read off the game once a tick: the port's reader for the core's
/// <c>Rumble</c> and <c>GyroAim</c>, handed in as <see cref="Reads"/>.
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

    internal static bool Trace = Environment.GetEnvironmentVariable("KF2_RUMBLE_PROBE")?.Trim() == "2";

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

    /// <summary>The bow as the core's rumble and gyro aim read it (Verdite Core's
    /// <c>BowReads</c>): the weapon routine's tick and memory, the three bow bytes, the
    /// two bow ids (16 and 17), and the attack button the gyro probe prints.</summary>
    internal static readonly BowReads Reads = new()
    {
        Memory = () => Memory,
        TickMs = () => TickMs,
        WeaponSlot = WeaponSlot,
        SwingClock = SwingClock,
        ClipByte = ClipByte,
        Weapons = (BowId, ArbalestId),
        AttackHeld = m => (m.ReadU16(0x80199554) & m.ReadU32(0x8006E570)) != 0,
        Attach = Install,
    };
}
