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
/// The game's Select shortcut, five times over, from the Shortcuts mod (kf2.shortcuts) of
/// PerdueEgg's verdite2-mods. Any of the five holds an item, an attack spell or a support
/// spell, assigned from the Equipment page's Select row and used with one press through
/// the game's own Select code. Stands down while the mod is enabled. See "Shortcuts" in
/// docs/PATCHES_AND_MODS.md.
///
///     KF2_SHORTCUTS=0|1       off or on for the run (Gameplay ▸ Shortcuts)
///     KF2_SHORTCUTS_PROBE=1   a line for each assignment, shortcut fired and card load
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
/// Using: a shortcut's key or pad button latches a press. At the next func_8002957C
/// Select is pressed on the game's behalf for that one call; for shortcuts 2-5 their
/// spell or item is first put in the Select slot, and afterwards the slot and the pad
/// words are put back. Shortcut 1 is the game's Select: its key only presses Select.
///
/// The d-pad: a d-pad direction given to a shortcut is taken out of the pad word in the
/// world, before player control reads it; menus still scroll with it, the sticks are
/// untouched.
///
/// The Equipment page copies its ten row labels into a buffer on its frame and draws
/// them from there (func_800209E0 from 0x8001A4B0); with keyboard labels, row 9 is
/// written as HOTKEYS there before each draw.
///
/// Saving: shortcuts 2-5 are kept beside the card saves as shortcuts_slotN.txt
/// (<see cref="CardFiles"/>), the mod's own files, so a player's mod shortcuts carry over.
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
    const uint MaskUp = 0x8006E590, MaskDown = 0x8006E594, MaskLeft = 0x8006E59C, MaskRight = 0x8006E598;
    internal const uint SlotSpell = 0x801994AC, SlotItem = 0x801994AD;
    const uint MaxHp = 0x80199426;                           // u16, 0 until a game is loaded

    const uint MenuLoop = 0x80018E80;
    const uint PlayerControl = 0x8002957C;
    const uint ListDraw = 0x800209E0;
    const uint SetSpell = 0x80025F80, SetItem = 0x80025FA8;
    const uint CardLoad = 0x80023638, CardSave = 0x80023764;
    const uint GameStart = 0x8004A628, CharacterReset = 0x80025B4C;

    const uint SpellSetterRet = 0x8001B088;                  // func_8001AD84 -> func_80025F80
    const uint ItemSetterRet = 0x8001B0A0;                   // func_8001AD84 -> func_80025FA8
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
    public const string DpadKey = "kf2.shortcuts.dpad";
    public static string KeyKey(int i) => $"kf2.shortcuts.key{i + 1}";
    public static string PadKey(int i) => $"kf2.shortcuts.pad{i + 1}";

    /// <summary>A pad button a shortcut can take: SDL GameControllerButton indices, the
    /// encoding HostWindow.IsPadButtonDown takes.</summary>
    public static readonly (string Name, string Label, int Button)[] PadButtons =
    [
        ("None", "-", -1),
        ("D-pad Up", "Up", 11),
        ("D-pad Down", "Down", 12),
        ("D-pad Left", "Left", 13),
        ("D-pad Right", "Right", 14),
        ("L3 (left stick click)", "L3", 7),
        ("R3 (right stick click)", "R3", 8),
        ("Misc (share / mute)", "Misc", 15),
    ];

    static readonly Key[] DefaultKeys = [Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5];
    static readonly int[] DefaultPads = [-1, 11, 12, 13, 14];   // shortcut 1 is on the pad's own Select already

    /// <summary>Off by default: not yet played in the port.</summary>
    public static bool Enabled { get; set; }

    /// <summary>The in-game list names keyboard keys (and the Select row says HOTKEYS),
    /// or controller buttons.</summary>
    public static bool KeyboardLabels { get; set; } = true;

    /// <summary>A d-pad direction used by a shortcut does not walk or turn.</summary>
    public static bool DpadTakeover { get; set; } = true;

    /// <summary>Each shortcut's key; Key.Unknown for none.</summary>
    public static readonly Key[] Keys = new Key[Count];

    /// <summary>Each shortcut's pad button, an index into <see cref="PadButtons"/>.</summary>
    public static readonly int[] Pads = new int[Count];

    /// <summary>A shortcut whose key the settings page is waiting for, or -1.</summary>
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

    static bool Live => Enabled && !StoodDown;

    // ---- the shortcuts (index 0, Select, is the game's own and unused here) ----
    internal struct Slot { public byte Spell, Item; }

    internal static readonly object Sync = new();
    static readonly Slot[] _slots = new Slot[Count];
    static readonly string[] _contents = ["", "", "", "", ""];

    // ---- input; game thread ----
    static readonly bool[] _held = new bool[Count];
    static readonly int[] _latch = new int[Count];
    const int LatchFrames = 8;
    static bool _inMenu;

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
            DpadTakeover = view.GetBool(DpadKey, DpadTakeover);
            for (int i = 0; i < Count; i++)
            {
                string k = view.GetString(KeyKey(i), DefaultKeys[i].ToString());
                Keys[i] = Enum.TryParse<Key>(k, out var key) ? key : Key.Unknown;
                Pads[i] = PadIndex((int)view.GetFloat(PadKey(i), DefaultPads[i]));
            }
        });
        Event.AddListener<VSyncEvent>(_ => Latch());
        HookAttach.OnOverlayLoad("shortcuts", Attach);
    }

    static int PadIndex(int button)
    {
        for (int p = 0; p < PadButtons.Length; p++)
            if (PadButtons[p].Button == button) return p;
        return 0;
    }

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
        for (int i = 0; i < Count; i++)
        {
            var key = Keys[i];
            int pad = PadButtons[Pads[i]].Button;
            bool down = (!typing && key != Key.Unknown && HostWindow.IsKeyDown(key))
                     || (pad >= 0 && HostWindow.IsPadButtonDown(pad));
            if (_inMenu || Capture >= 0 || !Live) _latch[i] = 0;
            else if (down && !_held[i]) _latch[i] = LatchFrames;
            else if (_latch[i] > 0) _latch[i]--;
            _held[i] = down;
        }
    }

    public static void BeforeMenu(CpuContext c, IMemory m) => _inMenu = true;

    public static void AfterMenu(CpuContext c, IMemory m)
    {
        _inMenu = false;
        Array.Clear(_latch);
    }

    // ---- using a shortcut: Select, pressed on the game's behalf ----

    public static void BeforeControl(CpuContext c, IMemory m)
    {
        if (!Live || m.ReadU16(MaxHp) == 0) return;
        ReadContents(m);

        if (DpadTakeover) TakeDpad(m);

        int fire = Array.FindIndex(_latch, l => l > 0);
        if (fire < 0) return;
        Array.Clear(_latch);
        if (_probe) Console.WriteLine($"[KF2] shortcuts: shortcut {fire + 1} fired");

        // Shortcut 1 is the game's Select as it stands; 2-5 lend it their contents.
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

    /// <summary>A d-pad direction that is a shortcut is not also a step or a turn.</summary>
    static void TakeDpad(IMemory m)
    {
        ushort now = m.ReadU16(PadNow), was = now;
        for (int i = 0; i < Count; i++)
        {
            int b = PadButtons[Pads[i]].Button;
            if (b < 11 || b > 14 || !HostWindow.IsPadButtonDown(b)) continue;
            uint mask = b switch { 11 => MaskUp, 12 => MaskDown, 13 => MaskLeft, _ => MaskRight };
            now &= (ushort)~m.ReadU32(mask);
        }
        if (now != was) m.WriteU16(PadNow, now);
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

    // ---- assigning: which shortcut, asked before the game's setters run ----

    /// <summary>False skips the game's spell setter.</summary>
    public static bool BeforeSetSpell(CpuContext c, IMemory m)
    {
        if (!Live || c.RA != SpellSetterRet) return true;
        uint sp = c.SP;
        uint row = m.ReadU32(sp + FrameRow);
        byte spell = (byte)c.A0;
        byte item = m.ReadU8(sp + FrameItems + row);
        if (m.ReadU8(sp + FrameSpells + row) != spell) item = None;   // not the frame expected: be safe

        int choice;
        try { choice = Picker.Run(c, m, spell, item); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] shortcuts: the picker failed, the game's own Select is set -- {e}");
            choice = 0;
        }
        _decision = choice;
        if (_probe)
            Console.WriteLine($"[KF2] shortcuts: spell 0x{spell:X2} item 0x{item:X2} -> " +
                              (choice < 0 ? "backed out" : choice == 0 ? "the game's Select" : $"shortcut {choice + 1}"));
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
        CardFiles.LastFile = "new game";
    }

    public static void BeforeCharacterReset(CpuContext c, IMemory m)
    {
        if (StoodDown || c.RA != DeathReset) return;
        ClearSlots();
        CardFiles.LastFile = "new game (died)";
    }

    public static void BeforeCardLoad(CpuContext c, IMemory m) => _loadSlot = (int)c.A0;

    public static void AfterCardLoad(CpuContext c, IMemory m)
    {
        if (StoodDown || c.V0 != 0) return;   // no file or a bad checksum: nothing was loaded
        var loaded = CardFiles.Read(_loadSlot);
        lock (Sync)
            for (int i = 0; i < Count; i++) _slots[i] = loaded[i];
        if (_probe)
            Console.WriteLine($"[KF2] shortcuts: card slot {_loadSlot} loaded, shortcuts 2-5 " +
                              string.Join(" ", loaded[1..].Select(x => $"{x.Spell:X2}/{x.Item:X2}")));
    }

    public static void AfterCardSave(CpuContext c, IMemory m)
    {
        // Written while on, or while anything is assigned, so turning it off keeps a
        // player's shortcuts and a player who never used it gets no files.
        if (StoodDown || !(Live || AnyAssigned())) return;
        Slot[] slots;
        lock (Sync) slots = (Slot[])_slots.Clone();
        CardFiles.Write(m, slots);
    }
}
