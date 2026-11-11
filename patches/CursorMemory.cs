using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// King's Field III style menu cursors, from the Cursor Memory mod (kf2.cursormemory) of
/// PerdueEgg's verdite2-mods: the Items page and the Magic page open on what was last
/// highlighted, and every Equipment sub menu opens on what is equipped. Stands down while
/// the mod is enabled. See "Cursor memory" in docs/PATCHES_AND_MODS.md.
///
///     KF2_CURSORMEMORY=0|1        off or on for the run (Gameplay ▸ Cursor memory)
///     KF2_CURSORMEMORY_PROBE=1    a line for every cursor recorded or placed
///
/// ---- how the pages keep their cursor ----
///
/// Every scrolling list in the in-game menu has a descriptor that <c>func_80022A78</c>
/// fills in, then zeroes the cursor in:
///
///     +0x1E  rows in the list          +0x21  the highlighted entry
///     +0x1F  rows shown at once        +0x22  its row in the window
///     +0x20  the entry at the window's top      (+0x21 - +0x20)
///
/// and <c>func_8001EB70(desc, ids, &amp;state, &amp;choice)</c> moves it, once a frame,
/// wrapping at both ends. Each page builds its rows first and then runs that loop:
///
///     Items         func_80019204   rows by func_80019444, ids at S4;
///                                   step called from 0x80019348
///     Magic         func_8001A04C   the support spells, spell numbers 14..19, built
///                                   by func_8001A1E8 (return 0x8001A09C);
///                                   step from 0x8001A170
///     Attack magic  func_8001AB48   spells 0..13 and a "none" row (0xFF), built by
///                                   func_8001A1E8 (return 0x8001AB9C);
///                                   step from 0x8001AD08
///     Gear          func_8001A6E8   weapon and armour, ids at S3, preview call
///                                   0x8001A8FC, step from 0x8001A994
///     Slot item     func_8001AD84   the second slot: item rows (ids at S7) then spell
///                                   rows (spell ids at frame + 0x908); preview call
///                                   0x8001AF68, step from 0x8001B004
///
/// So a page always opens on entry 0. The cursor is remembered by id, the item or spell
/// number, so it stays on the same thing when the list changes; if the id is gone it
/// stays on the same row number, which is the next entry.
///
/// ---- when ----
///
/// * Recorded after every step of the Items and Magic pages (a post on the step, after
///   MenuMouse's, which may have moved the cursor).
/// * Restored before the page's first step (a pre, before MenuMouse's, so MenuMouse
///   reads the placed cursor as the one it came in with). The Items page loads the
///   highlighted item's preview once while it sets up, with
///   <c>func_80022CAC(ids[cursor])</c> (return 0x800192A8): the cursor is set there and
///   the call pointed at the remembered item, so the preview matches.
///
/// ---- the Equipment sub menus ----
///
/// The Equipment page (<c>func_8001A2DC</c>) opens the gear pages by kind (the slot each
/// writes: 0 weapon 0x801994AF; 3, 4, 2, 5, 6, 7, 8 the armour and accessory bytes
/// 0x801994D4..DA), the attack magic page (the Cast button's spell, 0x801994AB) and the
/// second slot's page (an item in 0x801994AD or a spell in 0x801994AC, 0xFF for none).
/// Each opens with the highlight on the row whose id is equipped; an empty slot leaves
/// it on the first row, as the game does.
///
/// Threads: every hook runs on the game thread; the settings only set the switches.
/// </summary>
public static class CursorMemory
{
    // The hooked routines.
    const uint MagicPage = 0x8001A04C, AttackPage = 0x8001AB48, SpellRows = 0x8001A1E8;
    const uint LoadPreview = 0x80022CAC, Step = 0x8001EB70;

    // Return addresses that say which page called.
    const uint MagicRowsSite = 0x8001A09C, AttackRowsSite = 0x8001AB9C;
    const uint ItemsPreviewSite = 0x800192A8, GearPreviewSite = 0x8001A8FC, SlotPreviewSite = 0x8001AF68;
    const uint ItemsStepSite = 0x80019348, MagicStepSite = 0x8001A170, AttackStepSite = 0x8001AD08;

    // The list descriptor.
    const uint DescCount = 0x1E, DescVisible = 0x1F, DescTop = 0x20, DescCursor = 0x21, DescRow = 0x22;

    /// <summary>The Items page's descriptor, on its own frame.</summary>
    const uint ItemsDescOnFrame = 0x18;

    /// <summary>The second slot page's spell ids, on its own frame.</summary>
    const uint SlotSpellIdsOnFrame = 0x908;

    const uint AttackSpellByte = 0x801994AB, SlotSpellByte = 0x801994AC, SlotItemByte = 0x801994AD;
    const int Nothing = 0xFF;

    /// <summary>The equipped byte of each gear kind the Equipment page opens.</summary>
    static readonly Dictionary<int, uint> GearSlots = new()
    {
        [0] = 0x801994AF, [3] = 0x801994D4, [4] = 0x801994D5, [2] = 0x801994D6,
        [5] = 0x801994D7, [6] = 0x801994D8, [7] = 0x801994D9, [8] = 0x801994DA,
    };

    /// <summary>Before MenuMouse's pre on the step (order 0), after its post.</summary>
    const int BeforeOthers = -10, AfterOthers = 10;

    const string Owner = "kf2.cursormemory";
    public const string OnKey = "kf2.cursormemory.on";
    public const string ItemsKey = "kf2.cursormemory.items";
    public const string MagicKey = "kf2.cursormemory.magic";
    public const string EquippedKey = "kf2.cursormemory.equipped";

    /// <summary>The whole of it. Off by default: not yet played in the port.</summary>
    public static bool Enabled { get; set; }

    /// <summary>The Items page opens on what was last highlighted.</summary>
    public static bool Items { get; set; } = true;

    /// <summary>The Magic page opens on what was last highlighted.</summary>
    public static bool Magic { get; set; } = true;

    /// <summary>The Equipment sub menus open on what is equipped.</summary>
    public static bool Equipped { get; set; } = true;

    /// <summary>The Cursor Memory mod is enabled in the Mods panel and does this itself.</summary>
    public static bool StoodDown
    {
        get
        {
            foreach (var e in ModLoader.Mods)
                if (e.Enabled && string.Equals(e.Info.Id, Owner, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    static bool Live => Enabled && !StoodDown;

    /// <summary>A remembered cursor, by the item or spell it was on.</summary>
    sealed class Memory(string name)
    {
        public readonly string Name = name;
        public bool Has;
        public int Id, Entry, Row;
    }

    static readonly Memory ItemsMemory = new("items"), MagicMemory = new("magic");

    static bool _fromEnv, _probe;
    static bool _magicFresh, _attackFresh;     // the page opened and has not stepped yet
    static uint _magicIds, _attackIds;         // the row builder's id arrays
    static uint _stepDesc, _stepIds;           // the arguments of the step running now

    static readonly ModInfo _self = new()
    {
        Id = "kf2.cursormemory.port",
        Name = "Cursor memory",
        Version = "1.0",
        Description = "Menu pages open on the last entry highlighted, or on what is equipped.",
    };

    public static void Configure(string? env, string? probe)
    {
        _probe = probe == "1";
        if (string.IsNullOrWhiteSpace(env)) return;
        if (env is "0" or "1")
        {
            Enabled = env == "1";
            _fromEnv = true;
        }
        else Console.WriteLine($"[KF2] cursor memory: KF2_CURSORMEMORY is 0 or 1, not '{env}'; ignored");
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            if (!_fromEnv) Enabled = view.GetBool(OnKey, Enabled);
            Items = view.GetBool(ItemsKey, Items);
            Magic = view.GetBool(MagicKey, Magic);
            Equipped = view.GetBool(EquippedKey, Equipped);
        });
        HookAttach.OnOverlayLoad("cursor memory", Attach);
    }

    static readonly HashSet<uint> _hooked = [];

    static bool Attach()
    {
        SymbolRegistry.Build();
        var targets = new (uint Addr, string Pre, string Post)[]
        {
            (MagicPage, nameof(MagicOpens), ""),
            (AttackPage, nameof(AttackOpens), ""),
            (SpellRows, nameof(BeforeSpellRows), ""),
            (LoadPreview, nameof(BeforePreview), ""),
            (Step, nameof(BeforeStep), nameof(AfterStep)),
        };

        var queued = new List<(uint Addr, MethodInfo Fn)>();
        foreach (var (addr, pre, post) in targets)
        {
            if (_hooked.Contains(addr)) continue;
            var fn = SymbolRegistry.Resolve("game", null, addr);
            if (fn == null)
            {
                Console.Error.WriteLine($"[KF2] cursor memory: no game function at 0x{addr:X8}");
                continue;
            }
            if (pre != "") HookManager.AddPre(_self, fn, Own(pre), BeforeOthers);
            if (post != "") HookManager.AddPost(_self, fn, Own(post), AfterOthers);
            queued.Add((addr, fn));
        }

        HookManager.Commit();
        foreach (var (addr, fn) in queued)
            if (HookAttach.Installed(fn)) _hooked.Add(addr);

        Console.WriteLine($"[KF2] cursor memory: {(Enabled ? "on" : "off")}, {_hooked.Count}/{targets.Length} hooked");
        return _hooked.Count == targets.Length;
    }

    static MethodInfo Own(string name) =>
        typeof(CursorMemory).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    // ---- a page opening ----

    public static void MagicOpens(CpuContext c, IMemory m) => _magicFresh = true;

    public static void AttackOpens(CpuContext c, IMemory m) => _attackFresh = true;

    public static void BeforeSpellRows(CpuContext c, IMemory m)
    {
        if (c.RA == MagicRowsSite) _magicIds = c.A3;
        else if (c.RA == AttackRowsSite) _attackIds = c.A3;
    }

    // ---- pages that show a preview: set the cursor where it is loaded ----

    public static void BeforePreview(CpuContext c, IMemory m)
    {
        if (!Live) return;
        uint ids;
        int at;
        switch (c.RA)
        {
            case ItemsPreviewSite:
                ids = c.S4;
                at = Items ? Restore(ItemsMemory, m, c.SP + ItemsDescOnFrame, ids) : -1;
                break;
            case GearPreviewSite:
                ids = c.S3;
                at = Equipped ? Focus(m, c.SP + ItemsDescOnFrame, ids, GearSlot(m, (int)c.S6), "gear") : -1;
                break;
            case SlotPreviewSite:
                ids = c.S7;
                at = Equipped ? FocusSlot(m, c.SP + ItemsDescOnFrame, ids, c.SP + SlotSpellIdsOnFrame) : -1;
                break;
            default:
                return;
        }
        if (at >= 0) c.A0 = m.ReadU8(ids + (uint)at);
    }

    // ---- the cursor step, every frame of every list ----

    public static void BeforeStep(CpuContext c, IMemory m)
    {
        // The arguments, for the post: a called function is free to change A0..A3.
        _stepDesc = c.A0;
        _stepIds = c.A1;
        if (c.RA == MagicStepSite && _magicFresh)
        {
            _magicFresh = false;
            if (Live && Magic) Restore(MagicMemory, m, c.A0, _magicIds);
        }
        else if (c.RA == AttackStepSite && _attackFresh)
        {
            _attackFresh = false;
            if (Live && Equipped) Focus(m, c.A0, _attackIds, m.ReadU8(AttackSpellByte), "attack magic");
        }
    }

    public static void AfterStep(CpuContext c, IMemory m)
    {
        if (!Live) return;
        if (c.RA == ItemsStepSite && Items) Record(ItemsMemory, m, _stepDesc, _stepIds);
        else if (c.RA == MagicStepSite && Magic) Record(MagicMemory, m, _stepDesc, _magicIds);
    }

    static void Record(Memory memory, IMemory m, uint desc, uint ids)
    {
        if (ids == 0) return;
        int n = m.ReadU8(desc + DescCount);
        int cur = m.ReadU8(desc + DescCursor);
        if (n == 0 || cur >= n) return;
        int id = m.ReadU8(ids + (uint)cur);
        if (_probe && (!memory.Has || memory.Id != id || memory.Entry != cur))
            Console.WriteLine($"[KF2] cursor memory: {memory.Name} recorded id 0x{id:X2} at entry {cur} of {n}");
        memory.Has = true;
        memory.Id = id;
        memory.Entry = cur;
        memory.Row = m.ReadU8(desc + DescRow);
    }

    static int GearSlot(IMemory m, int kind) =>
        GearSlots.TryGetValue(kind, out uint slot) ? m.ReadU8(slot) : Nothing;

    /// <summary>The second slot: the equipped item's row, else the equipped spell's.</summary>
    static int FocusSlot(IMemory m, uint desc, uint itemIds, uint spellIds)
    {
        int at = Focus(m, desc, itemIds, m.ReadU8(SlotItemByte), "second slot item");
        if (at >= 0) return at;
        return Focus(m, desc, spellIds, m.ReadU8(SlotSpellByte), "second slot spell");
    }

    /// <summary>Puts the cursor on the row whose id is <paramref name="id"/>, in the
    /// window's middle. Returns the row, or -1 when nothing is equipped or the id is not
    /// in the list.</summary>
    static int Focus(IMemory m, uint desc, uint ids, int id, string what)
    {
        if (id == Nothing || ids == 0) return -1;
        int at = Find(m, desc, ids, id);
        if (at < 0) return -1;
        int n = m.ReadU8(desc + DescCount);
        Place(m, desc, n, at, (Math.Max(1, (int)m.ReadU8(desc + DescVisible)) - 1) / 2);
        if (_probe) Console.WriteLine($"[KF2] cursor memory: {what} opened on id 0x{id:X2}, entry {at} of {n}");
        return at;
    }

    /// <summary>Puts the list's cursor on the remembered entry. Returns the entry, or -1.</summary>
    static int Restore(Memory memory, IMemory m, uint desc, uint ids)
    {
        if (!memory.Has || ids == 0) return -1;
        int n = m.ReadU8(desc + DescCount);
        if (n == 0) return -1;

        int at = Find(m, desc, ids, memory.Id);
        if (at < 0) at = Math.Min(memory.Entry, n - 1);

        Place(m, desc, n, at, memory.Row);
        if (_probe) Console.WriteLine($"[KF2] cursor memory: {memory.Name} opened on entry {at} of {n} (remembered id 0x{memory.Id:X2})");
        return at;
    }

    static int Find(IMemory m, uint desc, uint ids, int id)
    {
        int n = m.ReadU8(desc + DescCount);
        for (int i = 0; i < n; i++)
            if (m.ReadU8(ids + (uint)i) == id) return i;
        return -1;
    }

    /// <summary>Writes the cursor: the entry, the window's top and the entry's row in it.</summary>
    static void Place(IMemory m, uint desc, int n, int at, int row)
    {
        int visible = Math.Max(1, (int)m.ReadU8(desc + DescVisible));
        int top = at - Math.Min(row, visible - 1);
        top = Math.Clamp(top, 0, Math.Max(0, n - visible));
        if (at >= top + visible) top = at - visible + 1;
        m.WriteU8(desc + DescCursor, (byte)at);
        m.WriteU8(desc + DescTop, (byte)top);
        m.WriteU8(desc + DescRow, (byte)(at - top));
    }
}
