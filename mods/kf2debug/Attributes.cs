// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;

namespace Kf2.Mods.Debug;

/// <summary>
/// Editing the character: experience, level, vitals, gold, the two attributes
/// and the seventeen combat ratings the status screen's second page shows.
///
/// ---- the one thing that makes this more than a memory editor ----
///
/// buf2 is not a flat sheet of stats. Nineteen of its words are a **cache**:
/// `func_800244CC` opens by zeroing the eight OFFENSE and nine DEFENSE words,
/// copies `BaseStr`/`BaseMag` into `StrPower`/`MagPower`, and then walks the
/// equipped items adding each one's contribution (and subtracting 20 from STR
/// while `CondCurse` is set). Twelve routines call it -- equip, unequip, use an
/// item, level up, load, and four sites inside stage 3 -- so a number typed into
/// one of those nineteen words survives only until the next of those happens,
/// which in play is seconds.
///
/// So this file does two different things with two different mechanisms:
///
///   * The **character** -- EXP, level, HP/MP and their maxima, gold, the two
///     base attributes and the five condition timers -- is plain memory. Nothing
///     recomputes it; a write sticks, and the save carries it (`func_80049A88`
///     packs every one of these addresses).
///
///   * The **ratings** -- STR/MAG POWER, OFFENSE, DEFENSE -- are held by a post
///     hook on `func_800244CC` itself, which is the only writer. Writing them
///     from the panel is offered too, and says plainly that it will not last.
///
/// Editing `BaseStr` is therefore the honest way to be stronger: it is a real
/// attribute, it persists, and the recompute picks it up. Locking `StrPower` is
/// the blunt way, and the panel says which is which.
///
/// ---- calling the game's own routines ----
///
/// Two buttons run recompiled MIPS rather than writing memory: "Level up" calls
/// `func_80024CAC`, the game's own level-up, so the level, both maxima, both
/// base attributes and the next-level threshold all move by the game's own
/// table instead of by a guess; "Recalculate" calls `func_800244CC` so an edited
/// base attribute shows on the status screen at once. Both need a CpuContext, so
/// both are queued and run from the stage-3 post hook -- the same rule
/// <see cref="Warp"/> follows, and for the same reason.
/// </summary>
internal static class Attributes
{
    /// <summary>The game's own EXP ceiling, from func_80024CAC's clamp.</summary>
    internal const int ExpMax = 999_999;

    /// <summary>
    /// One editable word. Width is 1, 2 or 4 bytes; <see cref="Signed"/> is set
    /// for the condition timers, which the status screen loads with lh.
    /// </summary>
    internal readonly struct Field
    {
        public readonly string Name;
        public readonly uint Address;
        public readonly int Width;
        public readonly bool Signed;
        public readonly string Tip;

        public Field(string name, uint address, int width, string tip = "", bool signed = false)
        {
            Name = name; Address = address; Width = width; Tip = tip; Signed = signed;
        }
    }

    // ---- the character: plain memory, nothing recomputes it ----

    internal static readonly Field[] Progress =
    [
        new("Experience", GameState.Exp, 4, "Capped at 999999 by the game's own level-up routine."),
        new("Next level at", GameState.ExpNext, 4,
            "The EXP the next level needs. func_80024CAC compares against this word and "
          + "rewrites it from the game's table each time you level."),
        new("Level", GameState.Level, 1,
            "The byte alone. Typing a number here does NOT grant the HP, MP and attribute "
          + "increases that levelling gives -- use the Level up button for that."),
    ];

    internal static readonly Field[] Vitals =
    [
        new("HP", GameState.Hp, 2),
        new("Max HP", GameState.MaxHp, 2, "Zero here is how everything in this mod tells "
                                        + "\"no character\" from \"a character at 0 HP\"."),
        new("MP", GameState.Mp, 2),
        new("Max MP", GameState.MaxMp, 2),
        new("Gold", GameState.Gold, 4),
    ];

    internal static readonly Field[] BaseAttributes =
    [
        new("Base strength", GameState.BaseStr, 2,
            "The real attribute. A level-up raises this, the save carries it, and "
          + "func_800244CC copies it into STR POWER before adding your equipment."),
        new("Base magic", GameState.BaseMag, 2,
            "The same, for MAG POWER."),
    ];

    internal static readonly Field[] Conditions =
    [
        new("Poison",   GameState.CondPoison,   2, signed: true),
        new("Curse",    GameState.CondCurse,    2, "Also costs 20 STR POWER while it is set.", true),
        new("Dark",     GameState.CondDark,     2, signed: true),
        new("Slow",     GameState.CondSlow,     2, signed: true),
        new("Paralyze", GameState.CondParalyze, 2, signed: true),
    ];

    // ---- the ratings: func_800244CC owns these ----

    internal static readonly Field[] Powers =
    [
        new("STR POWER", GameState.StrPower, 2),
        new("MAG POWER", GameState.MagPower, 2),
    ];

    internal static readonly Field[] Offense =
    [
        new("Slash", GameState.OffSlash, 2),
        new("Chop",  GameState.OffChop,  2),
        new("Stab",  GameState.OffStab,  2),
        new("Holy",  GameState.OffHoly,  2),
        new("Fire",  GameState.OffFire,  2),
        new("Earth", GameState.OffEarth, 2),
        new("Wind",  GameState.OffWind,  2),
        new("Water", GameState.OffWater, 2),
    ];

    internal static readonly Field[] Defense =
    [
        new("Slash",  GameState.DefSlash,  2),
        new("Chop",   GameState.DefChop,   2),
        new("Stab",   GameState.DefStab,   2),
        new("Poison", GameState.DefPoison, 2),
        new("Dark",   GameState.DefDark,   2),
        new("Fire",   GameState.DefFire,   2),
        new("Earth",  GameState.DefEarth,  2),
        new("Wind",   GameState.DefWind,   2),
        new("Water",  GameState.DefWater,  2),
    ];

    /// <summary>
    /// Every word func_800244CC rebuilds, in one array so the lock is one loop.
    /// </summary>
    internal static readonly Field[] Derived =
        [.. Powers, .. Offense, .. Defense];

    /// <summary>
    /// Hold the derived ratings at <see cref="Held"/> against the recompute.
    /// Off by default: the values the game computes are the honest ones, and the
    /// base attributes above are the place to change them from.
    /// </summary>
    internal static bool LockDerived;

    internal static readonly int[] Held = new int[Derived.Length];

    // ---- typed access ----

    internal static int Read(IMemory m, in Field f) => f.Width switch
    {
        1 => m.ReadU8(f.Address),
        2 => f.Signed ? (short)m.ReadU16(f.Address) : m.ReadU16(f.Address),
        _ => (int)m.ReadU32(f.Address),
    };

    internal static void Write(IMemory m, in Field f, int value)
    {
        switch (f.Width)
        {
            case 1: m.WriteU8(f.Address, (byte)Math.Clamp(value, 0, 255)); break;
            case 2:
                m.WriteU16(f.Address, f.Signed
                    ? (ushort)(short)Math.Clamp(value, short.MinValue, short.MaxValue)
                    : (ushort)Math.Clamp(value, 0, ushort.MaxValue));
                break;
            default: m.WriteU32(f.Address, (uint)Math.Max(value, 0)); break;
        }
    }

    /// <summary>
    /// The index into <see cref="Held"/> for an address, or -1. Keyed on the
    /// address rather than on the struct, so it needs no equality on Field.
    /// </summary>
    internal static int HeldIndex(uint address)
    {
        for (int i = 0; i < Derived.Length; i++)
            if (Derived[i].Address == address) return i;
        return -1;
    }

    /// <summary>Copy the live ratings into the hold, so switching the lock on changes nothing.</summary>
    internal static void PrimeHold(IMemory m)
    {
        for (int i = 0; i < Derived.Length; i++) Held[i] = Read(m, Derived[i]);
    }

    // ---- quick actions that are only memory ----

    internal static void FullHeal(IMemory m)
    {
        m.WriteU16(GameState.Hp, m.ReadU16(GameState.MaxHp));
        m.WriteU16(GameState.Mp, m.ReadU16(GameState.MaxMp));
    }

    internal static void CureConditions(IMemory m)
    {
        foreach (var f in Conditions) m.WriteU16(f.Address, 0);
    }

    // ---- quick actions that run the game's own code ----
    //
    // Queued, not called: the panel draws inside Present, which is inside VSync,
    // and a recompiled routine wants the game thread at a point where it is safe
    // to run. Stage 3 is that point, and it is where every other feature here
    // does its work.

    static int _pendingLevelUps;
    static bool _pendingRecalc;

    internal static string Status = "";

    /// <summary>
    /// Queue one run of the game's own level-up. EXP is topped up to the
    /// threshold first, since func_80024CAC returns early below it -- so the
    /// button means "level up now" rather than "grant some experience".
    /// </summary>
    internal static void QueueLevelUp(int times = 1)
    {
        _pendingLevelUps += Math.Max(times, 0);
        Status = $"queued {_pendingLevelUps} level-up(s)";
    }

    /// <summary>Queue func_800244CC so an edited base attribute shows at once.</summary>
    internal static void QueueRecalculate()
    {
        _pendingRecalc = true;
        Status = "queued a recalculation";
    }

    /// <summary>
    /// End of stage 3 -- the same site noclip, cheats and the area warp use.
    ///
    /// Order matters: the queued work runs first, then the lock, so a lock that
    /// is on still wins over a recompute this call triggered.
    /// </summary>
    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (!GameState.IsInGame(m)) return;

        if (_pendingLevelUps > 0) RunLevelUps(c, m);
        if (_pendingRecalc) RunRecalculate(c, m);

        if (LockDerived) ApplyHold(m);
    }

    /// <summary>
    /// The recompute itself. A post rather than a pre, because the point is to
    /// overwrite what it just wrote; every other writer of these words goes
    /// through here, so this one hook covers equip, unequip, load and level-up
    /// alike.
    /// </summary>
    [PostHook("game", Address = 0x800244CC)]
    static void AfterRecalculate(CpuContext c, IMemory m)
    {
        if (LockDerived && GameState.IsInGame(m)) ApplyHold(m);
    }

    static void ApplyHold(IMemory m)
    {
        for (int i = 0; i < Derived.Length; i++) Write(m, Derived[i], Held[i]);
    }

    static void RunLevelUps(CpuContext c, IMemory m)
    {
        int want = _pendingLevelUps;
        _pendingLevelUps = 0;

        var saved = c.Snapshot();
        int before = m.ReadU8(GameState.Level);

        for (int i = 0; i < want; i++)
        {
            if (m.ReadU8(GameState.Level) >= 255) break;

            // func_80024CAC(s16 gain): EXP += gain, clamped at 999999, and it
            // returns without doing anything while EXP is below the threshold
            // word. Topping EXP up to the threshold first and passing zero makes
            // it level exactly once, by its own table.
            uint need = m.ReadU32(GameState.ExpNext);
            if (m.ReadU32(GameState.Exp) < need)
                m.WriteU32(GameState.Exp, Math.Min(need, (uint)ExpMax));

            c.A0 = 0u;
            KingsField2.func_80024CAC(c, m);
        }

        c.Restore(saved);

        int after = m.ReadU8(GameState.Level);
        Status = after == before
            ? $"no level gained (level {after}; EXP is capped at {ExpMax})"
            : $"level {before} -> {after}";
        Console.WriteLine($"[kf2debug] {Status}");
    }

    static void RunRecalculate(CpuContext c, IMemory m)
    {
        _pendingRecalc = false;

        var saved = c.Snapshot();
        KingsField2.func_800244CC(c, m);
        c.Restore(saved);

        Status = $"recalculated: STR POWER {m.ReadU16(GameState.StrPower)}, "
               + $"MAG POWER {m.ReadU16(GameState.MagPower)}";
    }

    internal static void Reset()
    {
        LockDerived = false;
        _pendingLevelUps = 0;
        _pendingRecalc = false;
        Status = "";
    }
}
