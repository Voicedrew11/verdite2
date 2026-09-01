using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// What the game's state was at the moment an unhandled exception left the
/// recompiled code — read from RAM *after* the fault, from
/// <c>Program.cs</c>'s catch around <c>Entry.Run</c>.
///
///     KF2_CRASHDUMP=0    say nothing; let the exception print on its own
///
/// ## Why this exists rather than another hook
///
/// `docs/TODO.md` #14 is reproducible — 100% at 165 fps — but only with **no hook
/// on the faulting path**. With <see cref="HitGuard"/>'s pre-hook installed it did
/// not fire. That may be the hook perturbing the timing of a rate-sensitive bug,
/// or it may be that the two runs differed in frame rate as well; either way, a
/// diagnostic that has to be *present* to observe the crash is no use if being
/// present is what stops it.
///
/// So this observes nothing while the game runs. It adds no hook, no per-call
/// work and no allocation on any path the game takes; it is a `catch` at the top
/// of the process that reads memory once, after the fault, when game RAM is still
/// intact and nothing is going to run again. It cannot perturb what it measures,
/// which is the one property the probe could not offer.
///
/// ## What it prints, and why each piece
///
/// Everything TODO #14 needs to tell its four candidate causes apart:
///
/// * **The equipped weapon.** `func_800271D0` reads its record from
///   `0x80199494`, which `func_80026210` fills as `0x801C7FBC + itemId*0x44` —
///   static item data, so the record is the disc's and not a derived block. The
///   dump gives the id, the pointer, and **both parameter sets**, because the
///   routine picks between them on `u8[0x801994AE]` and only the second one reads
///   `+0x2A` — the field that becomes an entity index. Which set was live at the
///   fault is the single fact that would say whether the Moonlight Sword's
///   alternate attack is the path.
/// * **The entity table.** Every occupied slot with its kind and type byte, and
///   whether that type's descriptor lands inside the loaded block. One line of
///   this says whether the index was out of range, the slot was free, or the type
///   byte was junk.
/// * **The descriptor block**, and the first descriptor whose pointer table holds
///   something that is neither zero nor RAM — which is the read that throws.
///
/// Every read is fenced, and the dump is wrapped in its own try/catch: a
/// diagnostic that throws while reporting a crash would replace the stack trace
/// with its own, which is worse than saying nothing.
/// </summary>
internal static class CrashDump
{
    const uint EntityBase = 0x8016C544;
    const int EntityStride = 0x7C;
    const int EntityCount = 0xC8;

    const uint DescBase = 0x80172624;
    const int DescStride = 120;
    const int DescPtrOff = 0x38;
    const int DescPtrCount = 15;
    const uint DescBlockEnd = DescBase + 0xCB0u * 4u;

    const uint WeaponPtr = 0x80199494;   // u32 -> 0x801C7FBC + id*0x44
    const uint AltMode = 0x801994AE;     // u8, picks which parameter set is read
    const uint EquipId = 0x801994AF;     // u8, the item id func_80026210 was given
    const uint SwingClock = 0x801994A4;  // s16, -1 while no swing is running

    public static bool On = true;

    public static void Configure(string? v)
    {
        if (!string.IsNullOrWhiteSpace(v)) On = v != "0";
    }

    static bool Ram(uint a) => a >= 0x80010000u && a < 0x80200000u;

    /// <summary>
    /// Print the state behind an unhandled exception. Never throws: a report that
    /// faults would bury the stack trace it exists to explain.
    /// </summary>
    public static void Dump(Exception e, IMemory? m)
    {
        if (!On) return;

        try
        {
            var w = Console.Error;
            w.WriteLine();
            w.WriteLine("[KF2] ---- crash dump ----------------------------------------");
            w.WriteLine($"[KF2] {e.GetType().Name}: {e.Message}");

            if (m == null) { w.WriteLine("[KF2] no memory bound; nothing more to say."); return; }

            DumpWeapon(w, m);
            DumpDescriptors(w, m);
            DumpBossRecord(w, m);
            DumpEntities(w, m);

            w.WriteLine("[KF2] see docs/TODO.md #14; KF2_CRASHDUMP=0 turns this off.");
            w.WriteLine("[KF2] ---------------------------------------------------------");
        }
        catch (Exception inner)
        {
            Console.Error.WriteLine($"[KF2] crash dump failed: {inner.GetType().Name}: {inner.Message}");
        }
    }

    static void DumpWeapon(TextWriter w, IMemory m)
    {
        int id = Ram(EquipId) ? m.ReadU8(EquipId) : -1;
        int alt = Ram(AltMode) ? m.ReadU8(AltMode) : -1;
        int clock = Ram(SwingClock) ? (short)m.ReadU16(SwingClock) : 0;
        uint rec = Ram(WeaponPtr) ? m.ReadU32(WeaponPtr) : 0u;

        w.WriteLine($"[KF2] weapon: id 0x{id:X2}, record 0x{rec:X8}, " +
                    $"swing clock {clock} ({(clock < 0 ? "idle" : "mid-swing")}), " +
                    $"alt-attack flag 0x{alt:X2} -> {(alt != 0 ? "SECOND" : "first")} parameter set");

        if (!Ram(rec)) { w.WriteLine("[KF2] weapon: record is not RAM; nothing to read."); return; }

        // The two sets func_800271D0 chooses between. Only the second reads +0x2A,
        // and +0x2A is what func_8003A9CC is handed as an entity index.
        w.WriteLine($"[KF2] weapon: first  set +1C={m.ReadU16(rec + 0x1Cu)} " +
                    $"+1E={m.ReadU16(rec + 0x1Eu)} +2C={m.ReadU16(rec + 0x2Cu)}");
        w.WriteLine($"[KF2] weapon: second set +24={m.ReadU16(rec + 0x24u)} " +
                    $"+28={m.ReadU16(rec + 0x28u)} " +
                    $"+2A={m.ReadU16(rec + 0x2Au)} <- the entity index " +
                    $"+30={m.ReadU16(rec + 0x30u)} +32={m.ReadU16(rec + 0x32u)}");

        int idx = m.ReadU16(rec + 0x2Au);
        if (idx >= EntityCount)
            w.WriteLine($"[KF2] weapon: +2A is {idx}, past the {EntityCount}-slot entity table " +
                        "-- that alone explains the fault.");
    }

    /// <summary>
    /// How many descriptors this area actually loaded, which is **not** how many
    /// fit. The block has room for 108, and the area fills only the types it uses
    /// -- measured, 30 in one area and 14 in another -- leaving the rest as
    /// `0xFFFFFFFF` filler. That is not a terminator: `func_8003A448` skips a
    /// *zero* pointer and dereferences everything else, so the first filler
    /// descriptor a creature's type byte reaches faults on its first read. The real
    /// bound is therefore the filler boundary, and the block's end is only a
    /// backstop.
    /// </summary>
    static int LoadedCount(IMemory m)
    {
        int capacity = (int)((DescBlockEnd - DescBase) / DescStride);
        for (int t = 0; t < capacity; t++)
        {
            uint slot = DescBase + (uint)(t * DescStride) + DescPtrOff;
            if (!Ram(slot)) return t;
            uint ptr = m.ReadU32(slot);
            if (ptr != 0u && !Ram(ptr)) return t;
        }
        return capacity;
    }

    /// <summary>The first pointer `func_8003A448` would dereference for this type,
    /// which is the read that throws.</summary>
    static uint FirstPtr(IMemory m, int type)
    {
        uint slot = DescBase + (uint)(type * DescStride) + DescPtrOff;
        return Ram(slot) ? m.ReadU32(slot) : 0u;
    }

    static void DumpDescriptors(TextWriter w, IMemory m)
    {
        int capacity = (int)((DescBlockEnd - DescBase) / DescStride);
        int loaded = LoadedCount(m);
        w.WriteLine($"[KF2] descriptors: 0x{DescBase:X8}..0x{DescBlockEnd:X8}, " +
                    $"{DescStride} bytes each, {capacity} fit, " +
                    $"**{loaded} actually loaded** (type {loaded} upward is filler)");
    }

    /// <summary>
    /// The record `fdat23` builds through `0x801A0598` — the pointer CLAUDE.md
    /// already names as the one `func_8019F474` fills and `func_8019F688` writes
    /// its camera through.
    ///
    /// Two things about that build are worth printing. It writes the **drawn flag
    /// `+0x9 = 1` four instructions before the type byte `+0x2`**, so anything
    /// observing the record in between sees a drawn record with the previous
    /// tenant's type — and `func_8003B72C`, the query that picks a hit candidate,
    /// selects on exactly that flag. And the type it finally writes is **`0x11`,
    /// 17**, against a boss area that loads only 14 descriptors, so type 17 is
    /// filler and a hit resolved against this record faults on its first pointer.
    ///
    /// Whether the pointer lands in the entity table at all is the thing to read
    /// off this line; the offsets match the layout but that is inference.
    /// </summary>
    static void DumpBossRecord(TextWriter w, IMemory m)
    {
        const uint BossRecPtr = 0x801A0598;
        if (!Ram(BossRecPtr)) return;

        uint rec = m.ReadU32(BossRecPtr);
        if (rec == 0u) return;

        bool inTable = rec >= EntityBase &&
                       rec < EntityBase + (uint)(EntityCount * EntityStride) &&
                       (rec - EntityBase) % EntityStride == 0;
        string where = inTable
            ? $"entity slot {(rec - EntityBase) / EntityStride}"
            : "NOT an aligned entity slot";

        w.Write($"[KF2] fdat23 record: 0x801A0598 -> 0x{rec:X8} ({where})");
        if (Ram(rec + 0x9u))
            w.Write($", type {m.ReadU8(rec + 0x2u)} drawn {m.ReadU8(rec + 0x9u)} " +
                    $"(the build writes +9 before +2, and writes type 0x11)");
        w.WriteLine();
    }

    static void DumpEntities(TextWriter w, IMemory m)
    {
        int loaded = LoadedCount(m);
        int live = 0, suspect = 0;
        var lines = new List<string>();

        for (int i = 0; i < EntityCount; i++)
        {
            uint rec = EntityBase + (uint)(i * EntityStride);
            if (!Ram(rec + 0x9u)) break;

            int kind = m.ReadU8(rec);
            int drawn = m.ReadU8(rec + 0x9u);
            if (kind == 0xFF && drawn != 1) continue;   // free and not drawn: skip
            live++;

            int type = m.ReadU8(rec + 0x2u);
            if (kind != 0xFF && type < loaded) continue;

            suspect++;
            if (lines.Count >= 12) continue;

            uint desc = DescBase + (uint)(type * DescStride);
            uint ptr = type < (int)((DescBlockEnd - DescBase) / DescStride) ? FirstPtr(m, type) : 0u;

            // The record's own position, because that is what the reach scan in
            // func_800271D0 tests before it resolves a hit -- and it is what
            // ObjectSmoothing carries between ticks on any row reading drawn == 1.
            string pos = Ram(rec + 0x2Cu)
                ? $" pos {(int)m.ReadU32(rec + 0x2Cu)},{(int)m.ReadU32(rec + 0x30u)},{(int)m.ReadU32(rec + 0x34u)}"
                : "";

            lines.Add($"[KF2] entity {i}: kind 0x{kind:X2} type {type} drawn {drawn}{pos}" +
                      (kind == 0xFF ? " -- FREE but still reachable" : "") +
                      (type >= loaded ? $" -- type past the {loaded} loaded; " +
                                        $"descriptor 0x{desc:X8}, first pointer reads 0x{ptr:X8}" : ""));
        }

        w.WriteLine($"[KF2] entities: {live} occupied or drawn of {EntityCount}, {suspect} suspect " +
                    $"(a type >= {loaded} has no descriptor)");
        foreach (var l in lines) w.WriteLine(l);
    }
}
