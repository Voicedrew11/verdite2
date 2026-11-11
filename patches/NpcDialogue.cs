using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// King's Field III style conversations, from the NPC Dialogue mod (kf2.npcdialogue) of
/// PerdueEgg's verdite2-mods: while an NPC's line is up, Interact goes straight to their
/// next line, and Cancel ends the conversation. At the end of their dialogue Interact
/// repeats the last line until Cancel. NPCs that open a menu after talking (shops, the
/// fortune teller) are left as they are. Stands down while the mod is enabled. See
/// "NPC dialogue" in docs/PATCHES_AND_MODS.md.
///
///     KF2_NPCDIALOGUE=0|1     off or on for the run (Gameplay ▸ NPC dialogue)
///
/// ---- how a conversation runs ----
///
/// Talking to an NPC runs <c>func_80047000(npc)</c>, called from the use handler
/// <c>func_800489FC</c> and from a few area modules. It is a small script interpreter:
///
///     rec = *(0x8017265C + npc[2] * 0x78)    // the NPC's script record, 0x70 first
///     rec[0x10]                              // the script cursor, KEPT between talks
///     rec[0x12] >> 4                         // what follows the talk: 0..3 a menu
///                                            // (shop, fortuneteller...), else nothing
///     rec[0x1] / rec[0x11]                   // the talk and the return animation
///
/// A byte below 0xF0 is a line: <c>func_80035B48(3, rec[0xC] + byte)</c> shows it, from
/// the call site whose return address is 0x800472C0. The 0xF? bytes are commands. The
/// talk animation plays once before the first line (0x80047290 / 0x800472AC, both
/// <c>func_80046DE4</c>), and the return animation once after the last (0x80047408).
/// A talk shows one line and leaves the cursor after it, which is why talking again
/// says the next one, and why the last line repeats: the script ends by jumping back
/// to it.
///
/// ---- what this does ----
///
/// Talking again IS the next line, so this talks again, right away:
///
/// 1. When a line closes (<c>func_80035B48</c> returns to 0x800472C0), it checks which
///    button closed it, sampled where the closing fade starts (<c>func_800356F4</c>,
///    return address 0x80035C6C; MenuWorld replaces that routine, and a pre runs before
///    a replace). Interact (the Cross mask at 0x8006E568), and not Cancel (0x8006E56C),
///    means "next line". Only NPCs with nothing after the talk are chained.
/// 2. When that talk ends (a post on <c>func_80047000</c>), <c>func_80047000</c> is
///    called again for the same NPC, for as long as Interact closes the line.
/// 3. No animations in between (<see cref="SkipAnimations"/>): the return animation of
///    a talk about to be followed by another is skipped (a pre on <c>func_80046DE4</c>),
///    and so are the follow-up's talk animations.
///
/// Two loose ends of the game's own flow:
///
/// * The talk ends with <c>S2[0xC] = S5</c>, putting the NPC's animation id back to what
///   it read as the talk began; after a chained talk that is the talking id, so the idle
///   id is remembered from the first talk and written back at the end.
/// * Cancel is also the menu button, and <c>func_80025D38</c> strips Circle from this
///   tick's pad word, so the next tick would see a new press and open the menu. If
///   Cancel is still down when the conversation ends it is put back in both pad words
///   (0x80199554 now, 0x80199556 last tick) (<see cref="GuardMenu"/>).
///
/// Threads: every hook runs on the game thread; the settings only set the switches.
/// </summary>
public static class NpcDialogue
{
    const uint Talk = 0x80047000;
    const uint ShowMessage = 0x80035B48;
    const uint MessageFade = 0x800356F4;
    const uint Animate = 0x80046DE4;

    const uint LineCallSite = 0x800472C0;       // func_80047000's call to show a line
    const uint ClosingFadeSite = 0x80035C6C;    // func_80035B48's closing fade
    const uint TalkAnimSites0 = 0x80047290, TalkAnimSites1 = 0x800472AC;
    const uint ReturnAnimSite = 0x80047408;

    const uint PadNow = 0x80199554, PadLast = 0x80199556;
    const uint CrossMask = 0x8006E568, CancelMask = 0x8006E56C;

    /// <summary>A guard, not a limit anyone reaches: every chained talk needs a press.</summary>
    const int MaxChain = 1000;

    const string Owner = "kf2.npcdialogue";
    public const string OnKey = "kf2.npcdialogue.on";
    public const string SkipKey = "kf2.npcdialogue.skipanims";
    public const string GuardKey = "kf2.npcdialogue.guardmenu";

    /// <summary>Interact goes to the next line. Off by default: not yet played in the port.</summary>
    public static bool Enabled { get; set; }

    /// <summary>The NPC keeps its talking pose until the conversation ends.</summary>
    public static bool SkipAnimations { get; set; } = true;

    /// <summary>Ending a conversation with Cancel does not open the menu.</summary>
    public static bool GuardMenu { get; set; } = true;

    /// <summary>The NPC Dialogue mod is enabled in the Mods panel and does this itself.</summary>
    public static bool StoodDown
    {
        get
        {
            foreach (var e in ModLoader.Mods)
                if (e.Enabled && string.Equals(e.Info.Id, Owner, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    static bool _fromEnv;

    // One conversation's state; game thread only.
    static bool _in;          // inside a conversation (the outermost call to func_80047000)
    static bool _looping;     // the chained calls are running
    static bool _cont;        // the call running now is a chained one
    static bool _chain;       // the last line was closed by Interact: talk again
    static bool _lineOpen;    // one of the NPC's lines is up
    static ushort _closeHeld; // the pad, as the line began to close
    static uint _npc;         // the NPC entity
    static bool _haveIdle;
    static byte _idle, _talk; // the animation ids before the talk and during it
    static bool _returned;    // the last talk played its return animation
    static bool _didChain;
    static bool _menuless;    // the conversation has no menu after it

    static readonly ModInfo _self = new()
    {
        Id = "kf2.npcdialogue.port",
        Name = "NPC dialogue",
        Version = "1.0",
        Description = "Interact goes to an NPC's next line; Cancel ends the conversation.",
    };

    public static void Configure(string? env)
    {
        if (string.IsNullOrWhiteSpace(env)) return;
        if (env is "0" or "1")
        {
            Enabled = env == "1";
            _fromEnv = true;
        }
        else Console.WriteLine($"[KF2] npc dialogue: KF2_NPCDIALOGUE is 0 or 1, not '{env}'; ignored");
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            if (!_fromEnv) Enabled = view.GetBool(OnKey, Enabled);
            SkipAnimations = view.GetBool(SkipKey, SkipAnimations);
            GuardMenu = view.GetBool(GuardKey, GuardMenu);
        });
        HookAttach.OnOverlayLoad("npc dialogue", Attach);
    }

    static readonly HashSet<uint> _hooked = [];

    static bool Attach()
    {
        SymbolRegistry.Build();
        var targets = new (uint Addr, string Pre, string Post)[]
        {
            (Talk, nameof(TalkBegins), nameof(TalkEnds)),
            (ShowMessage, nameof(LineOpens), nameof(LineCloses)),
            (MessageFade, nameof(FadeBegins), ""),
            (Animate, nameof(BeforeAnimate), ""),
        };

        var queued = new List<(uint Addr, MethodInfo Fn)>();
        foreach (var (addr, pre, post) in targets)
        {
            if (_hooked.Contains(addr)) continue;
            var fn = SymbolRegistry.Resolve("game", null, addr);
            if (fn == null)
            {
                Console.Error.WriteLine($"[KF2] npc dialogue: no game function at 0x{addr:X8}");
                continue;
            }
            if (pre != "") HookManager.AddPre(_self, fn, Own(pre));
            if (post != "") HookManager.AddPost(_self, fn, Own(post));
            queued.Add((addr, fn));
        }

        HookManager.Commit();
        foreach (var (addr, fn) in queued)
            if (HookAttach.Installed(fn)) _hooked.Add(addr);

        Console.WriteLine($"[KF2] npc dialogue: {(Enabled ? "on" : "off")}, {_hooked.Count}/{targets.Length} hooked");
        return _hooked.Count == targets.Length;
    }

    static MethodInfo Own(string name) =>
        typeof(NpcDialogue).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    // ---- the conversation ----

    public static void TalkBegins(CpuContext c, IMemory m)
    {
        if (_looping) return;                // one of the chained calls
        _in = Enabled && !StoodDown && _hooked.Count == 4;
        _cont = false;
        _chain = false;
        _lineOpen = false;
        _haveIdle = false;
        _returned = false;
        _didChain = false;
        _menuless = false;
        _npc = c.A0;
    }

    public static void TalkEnds(CpuContext c, IMemory m)
    {
        if (_looping || !_in) return;
        _looping = true;
        var snap = c.Snapshot();
        try
        {
            for (int guard = 0; _chain && guard < MaxChain; guard++)
            {
                _chain = false;
                _cont = true;
                _didChain = true;
                c.A0 = _npc;
                KingsField2.func_80047000(c, m);
            }

            if (_didChain && _returned && _haveIdle && m.ReadU8(_npc + 0xC) == _talk)
                m.WriteU8(_npc + 0xC, _idle);

            if (GuardMenu && _menuless) KeepCancelHeld(c, m);
        }
        finally
        {
            c.Restore(snap);
            _looping = false;
            _in = false;
            _cont = false;
            _chain = false;
            _lineOpen = false;
        }
    }

    // ---- a line closing ----

    public static void LineOpens(CpuContext c, IMemory m)
    {
        // Only func_80047000's line; the fortuneteller's (0x800473AC) and every other
        // message are not touched.
        if (!_in || c.RA != LineCallSite) return;
        _lineOpen = true;
        _closeHeld = 0;
    }

    public static void FadeBegins(CpuContext c, IMemory m)
    {
        // The closing fade (the opening one is 0x80035C24). The button that closed the
        // line is still down here; a few frames later it may not be.
        if (_lineOpen && c.RA == ClosingFadeSite) _closeHeld = Held(c, m);
    }

    public static void LineCloses(CpuContext c, IMemory m)
    {
        if (!_lineOpen || c.RA != LineCallSite) return;
        _lineOpen = false;

        ushort cross = (ushort)m.ReadU32(CrossMask);
        ushort cancel = (ushort)m.ReadU32(CancelMask);

        // S1 is the NPC's script record here. Its +0x12 high nibble 0..3 opens a menu
        // once the talk is over; a line in front of one must not be chained over.
        bool menuless = (m.ReadU8(c.S1 + 0x12) & 0xF0) >= 0x40;
        if (menuless) _menuless = true;

        _chain = menuless
              && cross != 0
              && (_closeHeld & cross) != 0
              && (_closeHeld & cancel) == 0;
    }

    // ---- animations ----

    /// <summary>False skips the animation.</summary>
    public static bool BeforeAnimate(CpuContext c, IMemory m)
    {
        if (!_in) return true;
        uint ra = c.RA;

        // The talk animation, and finishing the one in progress before it. S5 holds the
        // NPC's animation id as it was before.
        if (ra == TalkAnimSites0 || ra == TalkAnimSites1)
        {
            if (!_haveIdle && !_cont)
            {
                _haveIdle = true;
                _idle = (byte)c.S5;
                _talk = m.ReadU8(c.S1 + 1);
            }
            return !(_cont && SkipAnimations);
        }

        // The return animation. Before another talk it is skipped; the line after it
        // writes S5 back to the animation id, so S5 is set to the id the NPC has now.
        if (ra == ReturnAnimSite)
        {
            if (_chain && SkipAnimations)
            {
                c.S5 = m.ReadU8(_npc + 0xC);
                return false;
            }
            _returned = true;
        }
        return true;
    }

    // ---- the pad ----

    /// <summary>The buttons down now, as the game's own PadRead(1) says (active high).</summary>
    static ushort Held(CpuContext c, IMemory m)
    {
        var snap = c.Snapshot();
        try
        {
            c.A0 = 1;
            KingsField2.PadRead_game(c, m);
            return (ushort)c.V0;
        }
        finally { c.Restore(snap); }
    }

    /// <summary>If Cancel is still down, make it "not new" in both pad words, so the
    /// tick that follows does not read it as a press and open the menu.</summary>
    static void KeepCancelHeld(CpuContext c, IMemory m)
    {
        ushort cancel = (ushort)m.ReadU32(CancelMask);
        ushort cross = (ushort)m.ReadU32(CrossMask);
        if (cancel == 0 || cancel == cross) return;
        if ((Held(c, m) & cancel) == 0) return;
        m.WriteU16(PadNow, (ushort)(m.ReadU16(PadNow) | cancel));
        m.WriteU16(PadLast, (ushort)(m.ReadU16(PadLast) | cancel));
    }
}
