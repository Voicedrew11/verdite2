using System.Reflection;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;

namespace Kf2.Shortcuts;

/// <summary>
/// Souls-style hands and hotkeys. The right hand holds two weapons and the left two
/// attack spells (<see cref="Hands"/>); five hotkeys hold an item or a spell each, the
/// game's Select shortcut five times over, from the Shortcuts mod (kf2.shortcuts) of
/// PerdueEgg's verdite2-mods. On the pad (<see cref="PadLayout"/>): R1 swings, L1 casts,
/// Square uses the current hotkey, d-pad Right and Left swap the right and left hands,
/// Down and Up cycle the hotkeys, and the sticks move. On the keyboard: keys 1-5 use a
/// hotkey, X and Z swap the hands. Drawn bottom left (<see cref="HandsHud"/>). Stands
/// down while the mod is enabled. See "Shortcuts" in docs/PATCHES_AND_MODS.md.
///
///     KF2_SHORTCUTS=0|1       off or on for the run (Gameplay ▸ Shortcuts)
///     KF2_SHORTCUTS_PROBE=1   a line for each assignment, swap, hotkey used and card load
///
/// ---- the game's own shortcut ----
///
/// The Equipment page (func_8001A2DC) opens the shortcut list from its row 9:
/// func_8001AD84. That page lists the inventory and then every learned spell, then a
/// "remove" row, with two parallel id arrays on its frame: a spell id per row at
/// sp+0x908 and an item id per row at sp+0x8B8, 0xFF where a row is not one. After the
/// Yes prompt the chosen row is u32 sp+0x95C, and the page's last act is two setters:
///
///     func_80025F80(spellIds[row])   // u8 0x801994AC = spell; clears the item if not 0xFF
///     func_80025FA8(itemIds[row])    // u8 0x801994AD = item;  clears the spell if not 0xFF
///
/// called with return addresses 0x8001B088 and 0x8001B0A0. Select is read in player
/// control, func_8002957C, as a just-pressed test of the action-mask word 0x8006E578
/// against the pad words 0x80199554 (this frame) and 0x80199556 (last frame); the cast
/// (func_80027DC0) and the item use carry every rule (MP, the gauge, counts).
///
/// ---- what this adds ----
///
/// Assigning: the spell setter is hooked when it comes from the shortcut page. Before it
/// runs, a second list of the game's own kind asks which shortcut the pick is for
/// (<see cref="Picker"/>). Shortcut 1 lets both setters run, so the game's Select slot is
/// set exactly as before; one of the other four keeps the pick here and skips both;
/// backing out changes nothing.
///
/// The hands are assigned the same way from the weapon page (the game's equip
/// func_80026210 from 0x8001AA4C) and the attack magic page (its setter 0x80025F70, which
/// the signature merge misnamed SsSetAutoKeyOffMode, from 0x8001AD68): which of the
/// hand's two slots. The slot that is out lets the game's setter run.
///
/// Using: the pad's Square or a hotkey's key latches a press. At the next func_8002957C
/// Select is pressed on the game's behalf for that one call; for hotkeys 2-5 their
/// spell or item is first put in the Select slot, and afterwards the slot and the pad
/// words are put back. Hotkey 1 is the game's Select.
///
/// The Equipment page copies its ten row labels into a buffer on its frame and draws
/// them from there (func_800209E0 from 0x8001A4B0); with keyboard labels, row 9 is
/// written as HOTKEYS there before each draw.
///
/// Saving: hotkeys 2-5 are kept beside the card saves as shortcuts_slotN.txt
/// (<see cref="CardFiles"/>), the mod's own files, so a player's mod shortcuts carry over;
/// the hands and the current hotkey in hands_slotN.txt beside them.
///
/// Threads: the hooks and the VSync latch run on the game thread; the settings page on
/// the host thread reads <see cref="Contents"/> and clears a shortcut through
/// <see cref="Clear"/>, both under <see cref="Sync"/>.
/// </summary>
public static class Shortcuts
{
    // ---- game addresses ----
    const uint PadNow = 0x80199554, PadLast = 0x80199556;
    const uint SelectMask = 0x8006E578;                      // u32 action-mask entry 4
    internal const uint SlotSpell = 0x801994AC, SlotItem = 0x801994AD;
    const uint MaxHp = 0x80199426;                           // u16, 0 until a game is loaded

    const uint MenuLoop = 0x80018E80;
    const uint PlayerControl = 0x8002957C;
    const uint ListDraw = 0x800209E0;
    const uint SetSpell = 0x80025F80, SetItem = 0x80025FA8;
    const uint EquipWeapon = 0x80026210, SetAttackSpell = 0x80025F70;
    const uint HudBuilder = 0x80031D5C;
    const uint CardLoad = 0x80023638, CardSave = 0x80023764;
    const uint GameStart = 0x8004A628, CharacterReset = 0x80025B4C;

    const uint SpellSetterRet = 0x8001B088;                  // func_8001AD84 -> func_80025F80
    const uint ItemSetterRet = 0x8001B0A0;                   // func_8001AD84 -> func_80025FA8
    const uint WeaponPageRet = 0x8001AA4C;                   // func_8001A6E8 -> func_80026210
    const uint AttackPageRet = 0x8001AD68;                   // func_8001AB48 -> 0x80025F70
    const uint FrameRow = 0x95C, FrameSpells = 0x908, FrameItems = 0x8B8;
    const uint EquipListDraw = 0x8001A4B0;                   // func_8001A2DC -> func_800209E0
    const uint DescCount = 0x1E, DescStride = 0x23, DescRows = 0x24;
    const int EquipRows = 10, SelectRow = 9;
    const uint DeathReset = 0x8002B064;                      // func_8002A550 -> func_80025B4C

    /// <summary>Shortcut 1 (the game's Select) and 2..5.</summary>
    public const int Count = 5;
    internal const byte None = 0xFF;

    // ---- settings ----
    const string Owner = "kf2.shortcuts";
    public const string OnKey = "kf2.shortcuts.on";
    public const string LabelsKey = "kf2.shortcuts.labels";
    public const string WeaponKeyKey = "kf2.shortcuts.keyweapon";
    public const string SpellKeyKey = "kf2.shortcuts.keyspell";
    public static string KeyKey(int i) => $"kf2.shortcuts.key{i + 1}";

    static readonly Key[] DefaultKeys = [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5];

    /// <summary>Off by default: not yet played in the port.</summary>
    public static bool Enabled { get; set; }

    /// <summary>The in-game list names keyboard keys (and the Select row says HOTKEYS),
    /// or controller buttons.</summary>
    public static bool KeyboardLabels { get; set; } = true;

    /// <summary>Each hotkey's key; Key.Unknown for none.</summary>
    public static readonly Key[] Keys = new Key[Count];

    /// <summary>The keys that swap the right hand (weapons) and the left (spells).</summary>
    public static Key WeaponKey { get; set; } = Key.X;
    public static Key SpellKey { get; set; } = Key.Z;

    /// <summary>The settings page's key rows: the five hotkeys, then the two hands.</summary>
    public const int KeyRows = Count + 2, WeaponRow = Count, SpellRow = Count + 1;

    /// <summary>A key row (<see cref="KeyRows"/>) whose key the settings page is waiting
    /// for, or -1.</summary>
    public static int Capture { get; set; } = -1;

    /// <summary>The Shortcuts mod is enabled in the Mods panel and does this itself.</summary>
    public static bool StoodDown
    {
        get
        {
            foreach (var e in ModLoader.Mods)
                if (e.Enabled && string.Equals(e.Info.Id, Owner, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    internal static bool Live => Enabled && !StoodDown;

    // ---- the shortcuts (index 0, Select, is the game's own and unused here) ----
    internal struct Slot { public byte Spell, Item; }

    internal static readonly object Sync = new();
    static readonly Slot[] _slots = new Slot[Count];
    static readonly string[] _contents = ["", "", "", "", ""];

    // ---- input; game thread ----
    static readonly bool[] _held = new bool[KeyRows];
    static readonly int[] _latch = new int[KeyRows];
    const int LatchFrames = 8;
    static bool _inMenu;
    static long _lastControlMs;

    /// <summary>A gap longer than this between control ticks means a menu, a message or
    /// a scene was between them, and the pad's counted presses belong to it.</summary>
    const int ControlGapMs = 250;

    /// <summary>The hotkey Square uses, 0..4 (0 the game's Select); game thread.</summary>
    static int _hotkey;
    internal static int Hotkey => _hotkey;

    // ---- the press made on the game's behalf; game thread ----
    static bool _injected, _swapped;
    static ushort _savedNow, _savedLast;
    static byte _savedSpell, _savedItem;

    // ---- assigning; game thread ----
    const int NoPicker = -2, Back = -1;
    static int _decision = NoPicker;

    static bool _fromEnv, _probe;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.shortcuts.port",
        Name = "Shortcuts",
        Version = "1.0",
        Description = "Five Select shortcuts, each holding an item or a spell.",
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
        else Console.WriteLine($"[KF2] shortcuts: KF2_SHORTCUTS is 0 or 1, not '{env}'; ignored");
    }

    public static void Install()
    {
        ClearSlots();
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            if (!_fromEnv) Enabled = view.GetBool(OnKey, Enabled);
            KeyboardLabels = view.GetString(LabelsKey, "keyboard") != "controller";
            for (int i = 0; i < Count; i++) Keys[i] = ReadKey(view.GetString(KeyKey(i), DefaultKeys[i].ToString()));
            WeaponKey = ReadKey(view.GetString(WeaponKeyKey, WeaponKey.ToString()));
            SpellKey = ReadKey(view.GetString(SpellKeyKey, SpellKey.ToString()));
        });
        Event.AddListener<VSyncEvent>(_ => Latch());
        PadLayout.Install();
        AgentServer.FastVerbs["hands"] = Shell;
        HookAttach.OnOverlayLoad("shortcuts", Attach);
    }

    static Key ReadKey(string name) => Enum.TryParse<Key>(name, out var key) ? key : Key.Unknown;

    /// <summary>Key row <paramref name="row"/>'s key (<see cref="KeyRows"/>).</summary>
    public static Key KeyOf(int row) => row switch
    {
        WeaponRow => WeaponKey,
        SpellRow => SpellKey,
        _ => Keys[row],
    };

    /// <summary>Sets key row <paramref name="row"/>; host thread (the settings page).</summary>
    public static void SetKey(int row, Key key)
    {
        switch (row)
        {
            case WeaponRow: WeaponKey = key; break;
            case SpellRow: SpellKey = key; break;
            default: Keys[row] = key; break;
        }
    }

    public static string KeyKeyOf(int row) => row switch
    {
        WeaponRow => WeaponKeyKey,
        SpellRow => SpellKeyKey,
        _ => KeyKey(row),
    };

    static readonly HashSet<uint> _hooked = [];

    static readonly (uint Addr, string Pre, string Post)[] Targets =
    [
        (MenuLoop, nameof(BeforeMenu), nameof(AfterMenu)),
        (PlayerControl, nameof(BeforeControl), nameof(AfterControl)),
        (ListDraw, nameof(BeforeListDraw), ""),
        (SetSpell, nameof(BeforeSetSpell), ""),
        (SetItem, nameof(BeforeSetItem), ""),
        (CardLoad, nameof(BeforeCardLoad), nameof(AfterCardLoad)),
        (CardSave, "", nameof(AfterCardSave)),
        (GameStart, nameof(OnGameStart), ""),
        (CharacterReset, nameof(BeforeCharacterReset), ""),
        (EquipWeapon, nameof(BeforeEquipWeapon), ""),
        (SetAttackSpell, nameof(BeforeSetAttackSpell), ""),
        (HudBuilder, "", nameof(AfterHud)),
    ];

    static bool Attach()
    {
        SymbolRegistry.Build();
        var queued = new List<(uint Addr, MethodInfo Fn)>();
        foreach (var (addr, pre, post) in Targets)
        {
            if (_hooked.Contains(addr)) continue;
            var fn = SymbolRegistry.Resolve("game", null, addr);
            if (fn == null)
            {
                Console.Error.WriteLine($"[KF2] shortcuts: no game function at 0x{addr:X8}");
                continue;
            }
            if (pre != "") HookManager.AddPre(_self, fn, Own(pre));
            if (post != "") HookManager.AddPost(_self, fn, Own(post));
            queued.Add((addr, fn));
        }

        HookManager.Commit();
        foreach (var (addr, fn) in queued)
            if (HookAttach.Installed(fn)) _hooked.Add(addr);

        Console.WriteLine($"[KF2] shortcuts: {(Enabled ? "on" : "off")}, {_hooked.Count}/{Targets.Length} hooked");
        return _hooked.Count == Targets.Length;
    }

    static MethodInfo Own(string name) =>
        typeof(Shortcuts).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    // ---- the shortcuts' contents ----

    static void ClearSlots()
    {
        lock (Sync)
            for (int i = 0; i < Count; i++) _slots[i] = new Slot { Spell = None, Item = None };
    }

    internal static Slot Get(int i)
    {
        lock (Sync) return _slots[i];
    }

    internal static void Set(int i, Slot slot)
    {
        lock (Sync) _slots[i] = slot;
    }

    internal static bool AnyAssigned()
    {
        lock (Sync)
            for (int i = 1; i < Count; i++)
                if (_slots[i].Spell != None || _slots[i].Item != None) return true;
        return false;
    }

    /// <summary>Empties shortcut 2..5; host thread (the settings page).</summary>
    public static void Clear(int i)
    {
        if (i >= 1 && i < Count) Set(i, new Slot { Spell = None, Item = None });
    }

    /// <summary>What each shortcut holds, as the last control tick read it; empty before
    /// a game is loaded. Host thread.</summary>
    public static string Contents(int i)
    {
        lock (Sync) return _contents[i];
    }

    static void ReadContents(IMemory m)
    {
        var names = new string[Count];
        for (int i = 0; i < Count; i++) names[i] = Labels.Text(Labels.Contents(m, i));
        lock (Sync) Array.Copy(names, _contents, Count);
    }

    // ---- input: latch each shortcut's press, once a frame; game thread ----

    /// <summary>A text field has the keyboard, so a key is not a shortcut.</summary>
    static bool Typing => ImGui.GetCurrentContext() != nint.Zero && ImGui.GetIO().WantTextInput;

    static void Latch()
    {
        bool typing = Typing;
        for (int i = 0; i < KeyRows; i++)
        {
            var key = KeyOf(i);
            bool down = !typing && key != Key.Unknown && HostWindow.IsKeyDown(key);
            if (_inMenu || Capture >= 0 || !Live) _latch[i] = 0;
            else if (down && !_held[i]) _latch[i] = LatchFrames;
            else if (_latch[i] > 0) _latch[i]--;
            _held[i] = down;
        }
    }

    public static void BeforeMenu(CpuContext c, IMemory m)
    {
        _inMenu = true;
        PadLayout.Forget();
    }

    public static void AfterMenu(CpuContext c, IMemory m)
    {
        _inMenu = false;
        Array.Clear(_latch);
        PadLayout.Forget();
    }

    // ---- using a shortcut: Select, pressed on the game's behalf ----

    public static void BeforeControl(CpuContext c, IMemory m)
    {
        if (!Live || m.ReadU16(MaxHp) == 0)
        {
            PadLayout.Forget();
            return;
        }
        long now = Environment.TickCount64;
        if (now - _lastControlMs > ControlGapMs) PadLayout.Forget();
        _lastControlMs = now;

        ReadContents(m);
        PadLayout.Rewrite(m);
        Hands.Tick(c, m, _probe);

        int asked = Interlocked.Exchange(ref _shellPress, -1);
        if (asked >= 0) TakeShellAway(m);
        bool Asked(PadLayout.Press p) => asked == (int)p;

        if (PadLayout.Take(PadLayout.Press.SwapWeapon) > 0 | Fired(WeaponRow) | Asked(PadLayout.Press.SwapWeapon)) Hands.SwapWeapon(c, m, _probe);
        if (PadLayout.Take(PadLayout.Press.SwapSpell) > 0 | Fired(SpellRow) | Asked(PadLayout.Press.SwapSpell)) Hands.SwapSpell(m, _probe);
        Cycle(m, PadLayout.Take(PadLayout.Press.NextHotkey) - PadLayout.Take(PadLayout.Press.PreviousHotkey)
                 + (Asked(PadLayout.Press.NextHotkey) ? 1 : 0) - (Asked(PadLayout.Press.PreviousHotkey) ? 1 : 0));
        HandsHud.Prepare(c, m, _probe);

        int fire = Array.FindIndex(_latch, 0, Count, l => l > 0);
        if (PadLayout.Take(PadLayout.Press.UseHotkey) > 0 | Asked(PadLayout.Press.UseHotkey)) fire = _hotkey;
        Array.Clear(_latch);
        if (fire < 0) return;
        if (_probe) Console.WriteLine($"[KF2] shortcuts: hotkey {fire + 1} used");

        // Hotkey 1 is the game's Select as it stands; 2-5 lend it their contents.
        _swapped = false;
        if (fire > 0)
        {
            var s = Get(fire);
            if (s.Spell == None && s.Item == None) return;
            _savedSpell = m.ReadU8(SlotSpell);
            _savedItem = m.ReadU8(SlotItem);
            m.WriteU8(SlotSpell, s.Spell);
            m.WriteU8(SlotItem, s.Spell != None ? None : s.Item);
            _swapped = true;
        }

        ushort sel = (ushort)m.ReadU32(SelectMask);
        _savedNow = m.ReadU16(PadNow);
        _savedLast = m.ReadU16(PadLast);
        m.WriteU16(PadNow, (ushort)(_savedNow | sel));
        m.WriteU16(PadLast, (ushort)(_savedLast & ~sel));
        _injected = true;
    }

    static bool Fired(int row) => _latch[row] > 0;

    /// <summary>Steps the current hotkey <paramref name="steps"/> along the ones that
    /// hold something (back for a negative count); stays put when none does.</summary>
    static void Cycle(IMemory m, int steps)
    {
        if (steps == 0) return;
        int dir = Math.Sign(steps), at = _hotkey;
        for (int n = Math.Abs(steps); n > 0; n--)
            for (int k = 1; k <= Count; k++)
            {
                int next = ((at + dir * k) % Count + Count) % Count;
                var (spell, item) = Labels.Held(m, next);
                if (spell == None && item == None) continue;
                at = next;
                break;
            }
        if (at == _hotkey) return;
        _hotkey = at;
        if (_probe) Console.WriteLine($"[KF2] shortcuts: hotkey {at + 1} current");
    }

    public static void AfterControl(CpuContext c, IMemory m)
    {
        if (!_injected) return;
        _injected = false;
        ushort sel = (ushort)m.ReadU32(SelectMask);
        m.WriteU16(PadNow, (ushort)((m.ReadU16(PadNow) & ~sel) | (_savedNow & sel)));
        m.WriteU16(PadLast, (ushort)((m.ReadU16(PadLast) & ~sel) | (_savedLast & sel)));
        if (!_swapped) return;
        m.WriteU8(SlotSpell, _savedSpell);
        m.WriteU8(SlotItem, _savedItem);
        _swapped = false;
    }

    // ---- the Equipment page's Select row says HOTKEYS with keyboard labels ----

    public static void BeforeListDraw(CpuContext c, IMemory m)
    {
        if (!Live || c.RA != EquipListDraw) return;
        uint d = c.A0;
        if (m.ReadU8(d + DescCount) != EquipRows || m.ReadU8(d + DescStride) != Labels.NameStride) return;
        uint buf = m.ReadU32(d + DescRows);
        if (buf == 0) return;
        byte[] cells = KeyboardLabels ? Labels.Word("hotkeys", "Hotkeys") : Labels.Cells(m, Labels.SelectLabel);
        Labels.WriteCells(m, buf + SelectRow * Labels.NameStride, cells);
    }

    // ---- assigning: which hotkey or which slot of a hand, asked before the game's setters run ----

    static readonly Picker.Question HotkeyQuestion = new()
    {
        Title = () => KeyboardLabels ? Labels.Word("hotkeys", "Hotkeys") : Labels.Word("shortcuts", "Shortcuts"),
        Rows = Count,
        Label = r => Labels.Word(null, Labels.Label(r)),
        Holds = Labels.Contents,
    };

    static readonly Picker.Question WeaponQuestion = new()
    {
        Title = () => Labels.Word("right", "Right hand"),
        Rows = 2,
        Label = r => Labels.Word(null, $"Weapon {r + 1}"),
        Holds = (m, r) => Labels.Name(m, None, Hands.InSlot(Hands.Right, m.ReadU8(Hands.Weapon), r)) ?? Labels.Word("empty", "Empty"),
    };

    static readonly Picker.Question SpellQuestion = new()
    {
        Title = () => Labels.Word("left", "Left hand"),
        Rows = 2,
        Label = r => Labels.Word(null, $"Magic {r + 1}"),
        Holds = (m, r) => Labels.Name(m, Hands.InSlot(Hands.Left, m.ReadU8(Hands.AttackSpell), r), None) ?? Labels.Word("empty", "Empty"),
    };

    /// <summary>Which slot of the right hand a weapon from the weapon page goes in;
    /// false skips the game's equip (the slot is not the one out, or backed out).</summary>
    public static bool BeforeEquipWeapon(CpuContext c, IMemory m)
    {
        if (!Live || c.RA != WeaponPageRet) return true;
        byte id = (byte)c.A0;
        int slot = Ask(c, m, WeaponQuestion, None, id, "weapon");
        return slot >= 0 && Hands.AssignWeapon(m, slot, id);
    }

    /// <summary>Which slot of the left hand an attack spell goes in; as <see cref="BeforeEquipWeapon"/>.</summary>
    public static bool BeforeSetAttackSpell(CpuContext c, IMemory m)
    {
        if (!Live || c.RA != AttackPageRet) return true;
        byte id = (byte)c.A0;
        int slot = Ask(c, m, SpellQuestion, id, None, "spell");
        return slot >= 0 && Hands.AssignSpell(m, slot, id);
    }

    /// <summary>The picker, run; a failure picks the slot that is out, so the game's own
    /// setter runs as it would have.</summary>
    static int Ask(CpuContext c, IMemory m, Picker.Question q, byte spell, byte item, string what)
    {
        int choice;
        try { choice = Picker.Run(c, m, q, spell, item); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] shortcuts: the picker failed, the game's own setter runs -- {e}");
            return q == WeaponQuestion ? Hands.Right.Out : q == SpellQuestion ? Hands.Left.Out : 0;
        }
        if (_probe)
            Console.WriteLine($"[KF2] shortcuts: {what} spell 0x{spell:X2} item 0x{item:X2} -> " +
                              (choice < 0 ? "backed out" : $"row {choice + 1}"));
        return choice;
    }

    /// <summary>False skips the game's spell setter.</summary>
    public static bool BeforeSetSpell(CpuContext c, IMemory m)
    {
        if (!Live || c.RA != SpellSetterRet) return true;
        uint sp = c.SP;
        uint row = m.ReadU32(sp + FrameRow);
        byte spell = (byte)c.A0;
        byte item = m.ReadU8(sp + FrameItems + row);
        if (m.ReadU8(sp + FrameSpells + row) != spell) item = None;   // not the frame expected: be safe

        int choice = Ask(c, m, HotkeyQuestion, spell, item, "hotkey");
        _decision = choice;
        if (choice >= 1 && choice < Count)
            Set(choice, spell != None ? new Slot { Spell = spell, Item = None } : new Slot { Spell = None, Item = item });
        return choice == 0;
    }

    /// <summary>False skips the game's item setter.</summary>
    public static bool BeforeSetItem(CpuContext c, IMemory m)
    {
        if (c.RA != ItemSetterRet) return true;
        bool run = _decision is 0 or NoPicker;
        _decision = NoPicker;
        return run;
    }

    // ---- kept with each card save ----

    static int _loadSlot;

    public static void OnGameStart(CpuContext c, IMemory m)
    {
        if (StoodDown) return;
        ClearSlots();
        Hands.Reset();
        _hotkey = 0;
        CardFiles.LastFile = "new game";
    }

    public static void BeforeCharacterReset(CpuContext c, IMemory m)
    {
        if (StoodDown || c.RA != DeathReset) return;
        ClearSlots();
        Hands.Reset();
        _hotkey = 0;
        CardFiles.LastFile = "new game (died)";
    }

    public static void BeforeCardLoad(CpuContext c, IMemory m) => _loadSlot = (int)c.A0;

    public static void AfterCardLoad(CpuContext c, IMemory m)
    {
        if (StoodDown || c.V0 != 0) return;   // no file or a bad checksum: nothing was loaded
        var loaded = CardFiles.Read(_loadSlot);
        lock (Sync)
            for (int i = 0; i < Count; i++) _slots[i] = loaded[i];
        var (right, left, hotkey) = CardFiles.ReadHands(_loadSlot);
        Hands.Restore(right, left);
        _hotkey = hotkey;
        if (_probe)
            Console.WriteLine($"[KF2] shortcuts: card slot {_loadSlot} loaded, shortcuts 2-5 " +
                              string.Join(" ", loaded[1..].Select(x => $"{x.Spell:X2}/{x.Item:X2}")) +
                              $"; {Hands.Describe(right, "weapon")}; {Hands.Describe(left, "spell")}; hotkey {hotkey + 1}");
    }

    public static void AfterCardSave(CpuContext c, IMemory m)
    {
        // Written while on, or while anything is assigned, so turning it off keeps a
        // player's shortcuts and a player who never used it gets no files.
        if (StoodDown || !(Live || AnyAssigned() || Hands.AnyAway())) return;
        Slot[] slots;
        lock (Sync) slots = (Slot[])_slots.Clone();
        CardFiles.Write(m, slots);
        CardFiles.WriteHands(m, Hands.Right, Hands.Left, _hotkey);
    }

    // ---- the shell's "hands" verb: what the pad would do, without a pad ----

    static int _shellPress = -1;
    static volatile int _shellWeaponAway = -1, _shellSpellAway = -1;

    /// <summary>Puts a weapon or spell in a hand's other slot for the shell, as the
    /// weapon or magic page would.</summary>
    static void TakeShellAway(IMemory m)
    {
        int w = Interlocked.Exchange(ref _shellWeaponAway, -1), s = Interlocked.Exchange(ref _shellSpellAway, -1);
        var right = Hands.Right;
        var left = Hands.Left;
        if (w >= 0) Hands.AssignWeapon(m, 1 - right.Out, (byte)w);
        if (s >= 0) Hands.AssignSpell(m, 1 - left.Out, (byte)s);
    }

    /// <summary><c>hands</c> the state; <c>hands weapon|spell|next|prev|use</c> a press at
    /// the next control tick; <c>hands weapon:ID</c> / <c>spell:ID</c> fill the hand's
    /// other slot (decimal). Fast queue.</summary>
    static string Shell(string arg)
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return "{\"ok\":false,\"error\":\"not running\"}";
        arg = arg.Trim().ToLowerInvariant();
        if (arg == "records")
        {
            var rows = new List<string>();
            for (uint i = 0; i < 20; i++)
            {
                var b = new byte[26];
                for (uint k = 0; k < 26; k++) b[k] = m.ReadU8(0x8019C5EC + i * 26 + k);
                rows.Add("\"" + Convert.ToHexString(b) + "\"");
            }
            return "{\"ok\":true,\"records\":[" + string.Join(",", rows) + "]}";
        }
        string? error = null;
        if (arg.StartsWith("weapon:") || arg.StartsWith("spell:"))
        {
            bool weapon = arg[0] == 'w';
            if (!int.TryParse(arg[(arg.IndexOf(':') + 1)..], out int id) || id < 0 || id > 0xFF) error = "an id 0-255";
            else if (weapon) _shellWeaponAway = id;
            else _shellSpellAway = id;
            if (error == null) Interlocked.CompareExchange(ref _shellPress, 99, -1);
        }
        else
        {
            int press = arg switch
            {
                "" or "state" => -1,
                "weapon" => (int)PadLayout.Press.SwapWeapon,
                "spell" => (int)PadLayout.Press.SwapSpell,
                "next" => (int)PadLayout.Press.NextHotkey,
                "prev" => (int)PadLayout.Press.PreviousHotkey,
                "use" => (int)PadLayout.Press.UseHotkey,
                _ => -2,
            };
            if (press == -2) error = "hands [state|weapon|spell|next|prev|use|weapon:ID|spell:ID]";
            else if (press >= 0) _shellPress = press;
        }
        if (error != null) return "{\"ok\":false,\"error\":\"" + error + "\"}";

        var r = Hands.Right;
        var l = Hands.Left;
        return "{\"ok\":true,\"live\":" + (Live ? "true" : "false") +
               ",\"weapon\":" + m.ReadU8(Hands.Weapon) + ",\"weaponOut\":" + (r.Out + 1) + ",\"weaponAway\":" + r.Away +
               ",\"spell\":" + m.ReadU8(Hands.AttackSpell) + ",\"spellOut\":" + (l.Out + 1) + ",\"spellAway\":" + l.Away +
               ",\"hotkey\":" + (_hotkey + 1) + ",\"holds\":" + HeldJson(m, _hotkey) +
               ",\"select\":[" + m.ReadU8(SlotSpell) + "," + m.ReadU8(SlotItem) + "]" +
               ",\"queued\":" + _shellPress + "}";
    }

    static string HeldJson(IMemory m, int row)
    {
        var (spell, item) = Labels.Held(m, row);
        return "{\"spell\":" + spell + ",\"item\":" + item +
               ",\"count\":" + (item == None ? 0 : m.ReadU8(Hands.Inventory + item)) + "}";
    }

    // ---- the HUD ----

    public static void AfterHud(CpuContext c, IMemory m)
    {
        if (!Live || m.ReadU16(MaxHp) == 0) return;
        HandsHud.Draw(c, m);
    }
}
