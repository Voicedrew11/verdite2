// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;
// Upstream 0409bc2 emits one class per overlay, because CoreCLR caps a class at
// 65535 methods. func_80048178 is GAME.EXE's, so the alias names the overlay
// once and the call site below is unchanged.
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Mods.Debug;

/// <summary>
/// The inventory: one byte per item id, and the item id is an index into the
/// font-index name table.
///
/// ---- what the inventory is ----
///
/// `func_80019444` is the routine every scrolling item list is built by, and it
/// is the whole map in one function:
///
/// <code>
/// int func_80019444(u8 *counts, u8 *rows, u8 *outCounts, u8 *outIds, int first, int last)
/// {
///     n = 0;
///     for (i = first; i &lt;= last; i++)
///         if (counts[i] != 0) {
///             memcpy(rows + n*0x18, (u8*)0x80065B24 + i*0x18, 0x18);  /* the name */
///             outCounts[n] = counts[i];
///             outIds[n]    = i;
///             n++;
///         }
///     return n;
/// }
/// </code>
///
/// So the inventory is **not** a list of slots: it is a flat array of counts
/// indexed by item id, and "holding" an item is a non-zero byte. Seven callers
/// pass `counts` as `0x8009B52C` (the player's) and a `first`/`last` pair that
/// says which page is being drawn; the shop pages pass a static stock array in
/// `GAME.EXE`'s own data instead (`0x80066844`, `0x80066A24`, `0x80066A9C`).
/// The widest range any caller asks for is `0 .. 0x77`, which is 120 ids, and
/// `0x8009B5A4` -- one past id 119 -- is the next global anything touches, so
/// the array is exactly 120 bytes.
///
/// Two ids are addressed individually elsewhere and they are the check on the
/// whole alignment: `0x8009B5A1` and `0x8009B5A2` are ids 117 and 118, which
/// decode out of the name table as ARROW FOR THE BOW and ELF'S BOLT -- the two
/// ammunition types, which are exactly the two an archery routine would want to
/// reach by address rather than by index.
///
/// ---- the names ----
///
/// `GAME.EXE` holds no English UI strings; its text is indices into its own
/// font, one byte a character, in 24-byte records -- `0x00` = `A`, `0x7F` =
/// space, `0xFF` = terminator, plus four punctuation codes that are the only
/// non-letters in the whole table. See "The status screen names the rest of
/// buf2" in docs/GAME_INTERNALS.md. Item id `i`'s name is the record at
/// `0x80065B24 + i * 0x18`, and it is decoded here **live out of memory** rather
/// than baked into this file, so the panel is reading the running game's own
/// table and not a copy that could drift from it.
///
/// An id the game does not use holds a placeholder record -- `00 FF`, the single
/// letter `A` -- and those placeholders are what separate the categories. The
/// groups below are therefore read off the table at run time rather than
/// guessed: every run of named ids between two placeholders is one category. Two
/// of those runs are confirmed by the game itself, since a caller of
/// `func_80019444` asks for exactly `0x35..0x3B` (the accessories) and another
/// for exactly `0x43..0x77` (the items and keys).
///
/// ---- adding one ----
///
/// `func_80048178(id)` is the game's own "give item", called by the area modules
/// when a chest or an NPC hands something over:
///
/// <code>
/// int func_80048178(int id)
/// {
///     if (inv[id] >= 99) { func_80033F08(0x12); return 1; }   /* the full chime */
///     inv[id]++;
///     (*(void(**)())(*(u32*)0x8017E068 + 0x18))();            /* the area module's slot 6 */
///     return 0;
/// }
/// </code>
///
/// That is what the "+1" button runs -- the same rule <see cref="Attributes"/>
/// follows for the level-up, and for the same reason: the game's own routine
/// carries the cap, the full chime and whatever the area module's hook does,
/// where imitating it would carry only the increment. It needs a CpuContext, so
/// it is queued and run from the stage-3 post hook.
///
/// Setting a count directly is offered too, because it is the only way to
/// *remove* something: `func_80048124` is the consume and it takes one at a
/// time. A direct write is also what a count above what the game would ever give
/// you needs.
/// </summary>
internal static class Items
{
    /// <summary>The count array. One byte per id; zero means you do not hold it.</summary>
    internal const uint InvBase = 0x8009B52C;

    /// <summary>
    /// Ids 0..119. The bound is the widest range a `func_80019444` caller asks
    /// for, and `0x8009B5A4` -- `InvBase + 120` -- is the next global in the
    /// image, so nothing here writes past the array.
    /// </summary>
    internal const int Count = 120;

    /// <summary>
    /// How many of those bytes a save carries. `func_80049A88` packs the
    /// inventory with a single `0x70`-byte copy from <see cref="InvBase"/>, and
    /// `func_8004A040` unpacks the same 0x70 -- so ids 112..119 are live state
    /// the save does not round-trip. Read statically off both routines; what
    /// happens to an id in that range across a save and a load has not been
    /// watched in play.
    /// </summary>
    internal const int SavedCount = 0x70;

    /// <summary>Item id 0's name record. Stride 0x18, same as every other string here.</summary>
    internal const uint NameTable = 0x80065B24;
    internal const int NameStride = 0x18;

    /// <summary>func_80048178's own ceiling: it refuses to add at 99.</summary>
    internal const int MaxHeld = 99;

    /// <summary>GAME.EXE's pointer to the loaded area module, whose slot 6 the give routine calls.</summary>
    const uint ModulePtr = 0x8017E068;

    // ---- reading the array ----

    internal static int Held(IMemory m, int id) =>
        (uint)id < Count ? m.ReadU8(InvBase + (uint)id) : 0;

    internal static void SetHeld(IMemory m, int id, int count)
    {
        if ((uint)id >= Count) return;
        m.WriteU8(InvBase + (uint)id, (byte)Math.Clamp(count, 0, 255));
    }

    /// <summary>How many distinct ids are held, which is the length of the in-game list.</summary>
    internal static int DistinctHeld(IMemory m)
    {
        int n = 0;
        for (int i = 0; i < Count; i++) if (Held(m, i) != 0) n++;
        return n;
    }

    // ---- the names ----

    // The only four non-letter codes in the whole table, found by counting every
    // byte of all 133 records: nothing else outside 0x00..0x19 and 0x7F appears.
    // An unexpected code is rendered as its hex rather than guessed at, so a
    // wrong reading shows as a wrong reading.
    static string Punctuation(byte c) => c switch
    {
        0x31 => ",",
        0x32 => "'",
        0x38 => "!",
        0x3A => "?",
        0x7F => " ",
        _ => $"{{{c:X2}}}",
    };

    /// <summary>
    /// Decode item id's name out of the running image. Twenty-four bytes is the
    /// record, and a record with no terminator in it is not a string -- so the
    /// loop stops at the stride as well as at 0xFF.
    /// </summary>
    internal static string Name(IMemory m, int id)
    {
        if ((uint)id >= Count) return "";

        uint rec = NameTable + (uint)id * NameStride;
        var sb = new System.Text.StringBuilder(NameStride);

        for (int i = 0; i < NameStride; i++)
        {
            byte c = m.ReadU8(rec + (uint)i);
            if (c == 0xFF) break;
            sb.Append(c < 26 ? (char)('A' + c) : Punctuation(c));
        }

        return sb.ToString();
    }

    /// <summary>
    /// An id the game does not use. Its record is `00 FF` -- the letter A and a
    /// terminator -- which is the separator the categories are read off.
    /// </summary>
    internal static bool IsUnused(IMemory m, int id)
    {
        uint rec = NameTable + (uint)id * NameStride;
        return m.ReadU8(rec) == 0x00 && m.ReadU8(rec + 1) == 0xFF;
    }

    // ---- the categories ----

    internal readonly struct Group
    {
        public readonly string Name;
        public readonly int First;
        public readonly int Last;

        public Group(string name, int first, int last)
        {
            Name = name; First = first; Last = last;
        }
    }

    // Our labels, not the game's -- GAME.EXE has no category strings, only the
    // separators. The ranges are read from the table; these name the runs in the
    // order they come out of it, and a table that ever yields a different number
    // of runs falls back to naming each by its ids rather than mislabelling one.
    static readonly string[] RunLabels =
    [
        "Swords and axes",            //   0 -   6
        "Enchanted blades and bows",  //   9 -  17
        "Helms",                      //  21 -  26
        "Armour",                     //  28 -  32
        "Shields",                    //  34 -  39
        "Gauntlets",                  //  41 -  45
        "Boots",                      //  47 -  51
        "Accessories",                //  53 -  59  (the game asks for 0x35..0x3B)
        "Maps, stones and potions",   //  67 -  80
        "Quest items and crystals",   //  82 -  97
        "Keys",                       //  99 - 109
        "Gates, keys and ammunition", // 111 - 118
    ];

    static Group[]? _groups;

    /// <summary>
    /// The categories, read off the placeholder records once and cached. The
    /// table lives in GAME.EXE, which is resident for the whole session, so one
    /// read is enough -- but the cache is dropped by <see cref="Reset"/> so an
    /// unload and reload re-reads it rather than trusting a stale scan.
    /// </summary>
    internal static Group[] Groups(IMemory m)
    {
        if (_groups != null) return _groups;

        var runs = new List<Group>();
        int start = -1;

        for (int id = 0; id <= Count; id++)
        {
            bool named = id < Count && !IsUnused(m, id);

            if (named && start < 0) start = id;
            else if (!named && start >= 0)
            {
                runs.Add(new Group("", start, id - 1));
                start = -1;
            }
        }

        var groups = new Group[runs.Count];
        for (int i = 0; i < runs.Count; i++)
        {
            string label = runs.Count == RunLabels.Length
                ? RunLabels[i]
                : $"Ids {runs[i].First}-{runs[i].Last}";
            groups[i] = new Group(label, runs[i].First, runs[i].Last);
        }

        _groups = groups;
        return groups;
    }

    // ---- giving, through the game's own routine ----
    //
    // Queued, not called: the panel draws inside Present, which is inside VSync,
    // and a recompiled routine wants the game thread at a point where it is safe
    // to run. Stage 3 is that point, and it is where every other feature in this
    // mod does its work.

    static readonly List<int> _pending = [];

    internal static string Status = "";

    internal static void QueueGive(int id, int times = 1)
    {
        if ((uint)id >= Count) return;
        for (int i = 0; i < times; i++) _pending.Add(id);
        Status = $"queued {_pending.Count} item(s)";
    }

    /// <summary>Queue one of every named id. The count the game grants is its own business.</summary>
    internal static void QueueGiveAll(IMemory m)
    {
        int n = 0;
        for (int id = 0; id < Count; id++)
        {
            if (IsUnused(m, id) || Held(m, id) != 0) continue;
            _pending.Add(id);
            n++;
        }
        Status = n == 0 ? "you already hold one of everything" : $"queued {n} item(s)";
    }

    /// <summary>
    /// Zero every count. A direct write rather than the game's own consume,
    /// which takes one at a time and would be 120 * 99 calls.
    /// </summary>
    internal static void ClearAll(IMemory m)
    {
        for (int id = 0; id < Count; id++) m.WriteU8(InvBase + (uint)id, 0);
        Status = "inventory cleared";
    }

    /// <summary>
    /// End of main-loop stage 3 -- the same site noclip, the cheats, the area
    /// warp and the attribute editor use.
    /// </summary>
    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (!GameState.IsInGame(m)) return;

        RunProbe(m);

        if (_pending.Count == 0) return;
        RunGives(c, m);
    }

    static void RunGives(CpuContext c, IMemory m)
    {
        // func_80048178 ends in a call through the area module's dispatch slot 6
        // (u32[u32[0x8017E068] + 0x18]). In an area that slot is filled -- every
        // fdat module calls this routine itself -- but a null there would be a
        // jump to zero, so it is checked rather than assumed, and the direct
        // write is what a missing hook falls back to.
        bool useGameRoutine = HasModuleHook(m);

        int given = 0, refused = 0;
        var saved = c.Snapshot();

        foreach (int id in _pending)
        {
            if (Held(m, id) >= MaxHeld) { refused++; continue; }

            if (useGameRoutine)
            {
                c.A0 = (uint)id;
                KingsField2.func_80048178(c, m);
            }
            else
            {
                SetHeld(m, id, Held(m, id) + 1);
            }

            given++;
        }

        c.Restore(saved);
        _pending.Clear();

        Status = refused == 0
            ? $"gave {given} item(s)"
            : $"gave {given} item(s); {refused} already at {MaxHeld}";
        Console.WriteLine($"[kf2debug] {Status}"
                        + (useGameRoutine ? "" : " (direct write: the area module has no give hook)"));

        if (ProbeLevel >= 2) ProbeAfterGive(m);
    }

    static bool HasModuleHook(IMemory m)
    {
        uint module = m.ReadU32(ModulePtr);
        if (module < 0x80010000u || module >= 0x80200000u) return false;
        uint slot = m.ReadU32(module + 0x18);
        return slot >= 0x80010000u && slot < 0x80200000u;
    }

    // ---- the probe ----
    //
    // `KF2_DEBUG_ITEMS_PROBE=1` prints the table once, the first time an area is
    // up. It is what says the addresses above are right without anyone looking at
    // a screen: a run of names that decode as English, in groups whose boundaries
    // fall where the game's own page filters say they do, is the reading being
    // correct; a wrong base would print hex escapes and nonsense.

    static bool _probeDone;

    /// <summary>
    /// 0 off, 1 print the table, 2 also queue one of everything -- which is the
    /// acceptance test for the give path, since the count beside every name on
    /// the next area's print is what `func_80048178` actually did.
    /// </summary>
    internal static readonly int ProbeLevel =
        int.TryParse(Environment.GetEnvironmentVariable("KF2_DEBUG_ITEMS_PROBE"), out int lv) ? lv : 0;

    internal static void RunProbe(IMemory m)
    {
        if (_probeDone || ProbeLevel <= 0) return;
        _probeDone = true;

        Console.WriteLine($"[kf2debug] inventory at 0x{InvBase:X8}, {Count} ids, "
                        + $"names at 0x{NameTable:X8} stride 0x{NameStride:X}");

        foreach (var g in Groups(m))
        {
            Console.WriteLine($"[kf2debug]   {g.First,3}-{g.Last,3}  {g.Name}");
            for (int id = g.First; id <= g.Last; id++)
                Console.WriteLine($"[kf2debug]     {id,3}  x{Held(m, id),-3} {Name(m, id)}");
        }

        Console.WriteLine($"[kf2debug] {DistinctHeld(m)} of {Count} ids held");

        if (ProbeLevel >= 2) QueueGiveAll(m);
    }

    /// <summary>
    /// Re-print the counts after the queue has run. Only at probe level 2, and
    /// only once -- this is the "did the give path work" half of the probe.
    /// </summary>
    static void ProbeAfterGive(IMemory m)
    {
        Console.WriteLine($"[kf2debug] after the give: {DistinctHeld(m)} of {Count} ids held");
        for (int id = 0; id < Count; id++)
            if (Held(m, id) != 0)
                Console.WriteLine($"[kf2debug]     {id,3}  x{Held(m, id),-3} {Name(m, id)}");
    }

    internal static void Reset()
    {
        _pending.Clear();
        _groups = null;
        _probeDone = false;
        Status = "";
    }
}
