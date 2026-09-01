using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;

namespace Kf2;

/// <summary>
/// Reload the last save on death.
///
///     KF2_AUTORELOAD=1        on (the default); 0 leaves the death alone
///     KF2_AUTORELOAD_DELAY=2  seconds of the game's death sequence first
///     KF2_AUTORELOAD_SLOT=0   0 = the game's own "last used" slot, 1..3 pins one
///
/// It is a setting, under Gameplay — see Kf2.Settings.AutoReloadPage. The saved
/// choices are read on RuntimeReadyEvent rather than in <see cref="Configure"/>,
/// since ConfigManager only loads inside HostWindow.Initialize, after Program.cs;
/// the environment variables still win over them.
///
/// King's Field has no retry. Dying leaves you in the game's death sequence and
/// the way back is the menu, then the load screen, then the slot — four screens
/// of the game's own UI between a death and being where you were.
///
/// This adds no loading path of its own. The game can already load a save without
/// leaving the area it is running: `func_80029CBC` handles the in-game menu's
/// result, and its -3 arm — "the menu loaded a save" — is twelve instructions at
/// 0x80029E0C:
///
///     func_800240B8();                     /* post-load fixup                */
///     area = *(u8*)0x8017E060;             /* the loaded save's area         */
///     func_80024154(area, area, area, area, area, 0xFF);
///     func_80025D38();
///
/// So a reload is the game's own loader, `func_80023638(slot)`, followed by that
/// arm transcribed verbatim. No overlay swap, no title screen, no menu driving
/// and no synthetic pad input; the area re-entry, the music, the equipment and
/// the character state are all the game's own code running on its own data.
///
/// Which slot is "the last save" is the game's answer too. Both the load and the
/// save write the chosen slot to 0x8006E5D4, so that byte is whichever slot the
/// player last touched. It is zero until one of them runs, which is the
/// never-saved case, and this stays out of the way for it.
///
/// It lives in `patches/` rather than `mods/` because it is the port's answer to
/// a design of the original that a package should not have to be present to fix:
/// a death costing four screens of menu is the kind of thing a player expects the
/// port itself to have dealt with. See "Auto reload" in NOTES.md.
/// </summary>
public static class AutoReload
{
    // ---- GAME.EXE player state ----
    //
    // The stat block is buf2, the 0x58-byte per-area buffer at 0x80199414. It was
    // found through the memory card: func_80023CC0 stamps the decimal EXP and LV
    // into the save's 64-byte Shift-JIS title, reading them from the first two
    // fields here.
    const uint Level      = 0x8019941C;   // u8
    const uint MaxHp      = 0x80199426;   // u16
    const uint Hp         = 0x80199428;   // u16, current

    // The player's action state, dispatched through a jump table at
    // 0x80011300 + state*4 in main-loop stage 3. 0x11 is dead: func_8002A264
    // latches it, the take-damage routine func_80024FE0 returns early once it is
    // set, and state 0x11's own handler (0x8002ADAC) opens by forcing HP to 0.
    const uint State      = 0x801994E1;   // u8
    const byte StateDead  = 0x11;

    // The area the loaded save belongs to. In buf0, and what the game's own
    // post-load arm passes to the area-entry routine.
    const uint Area       = 0x8017E060;   // u8

    // Frames since death, and the game's own clock for what happens next.
    // func_8002A264 zeroes it, state 0x11's handler increments it, and those
    // three sites are its only uses in GAME.EXE:
    //
    //     1..31   the death animation
    //     32..64  fade to black, amount (n - 32) << 7
    //     65      func_80024154(0, 0, 0, 0, 0, 0xFF)  -- respawn at area 0,
    //             i.e. back to the beginning of the game
    //
    // These are logic ticks, not rendered frames, so 65 of them is 3.25 s at the
    // 20 Hz the world runs at and 2.17 s if the tick rate is set to 30 -- either
    // way, any delay long enough to read as deliberate loses a race with it. Holding the counter here is
    // what makes the delay ours: the animation finishes, the fade never starts,
    // and the game's own respawn never comes due.
    const uint DeathFrames = 0x8019951A;  // u16
    const ushort HoldAt    = 31;

    // The slot last saved to or loaded from, written by both func_80023638 and
    // func_80023764. Zero in the executable image, so zero means "neither has
    // run this session".
    const uint CurrentSlot = 0x8006E5D4;  // u8

    // End of main-loop stage 3, which runs every frame in GAME.EXE -- including
    // while dead, since the death sequence is one arm of stage 3's own state
    // machine. Not func_80029CBC, which is where the game does its own load
    // from: that is dispatched only from the arms for states 1 and 2, so it
    // stops being called the moment the state byte latches to 0x11.
    const uint PlayerStage = 0x8002A550;

    /// <summary>interface.ini keys, read and written by the settings page.</summary>
    public const string OnKey    = "kf2.autoreload.enabled";
    public const string DelayKey = "kf2.autoreload.delay";
    public const string SlotKey  = "kf2.autoreload.slot";

    /// <summary>Live rather than fixed at startup: the hook stays attached and
    /// does nothing when this is off, so it can be taken back mid-session.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>Seconds of the game's own death sequence before the reload. Long
    /// enough that the death registers as a death.</summary>
    public static float Delay { get; private set; } = 2.0f;

    /// <summary>0 = whatever the game says the current slot is; 1..3 pins one.</summary>
    public static int Slot { get; private set; }

    /// <summary>What the last reload attempt did, for the settings page.</summary>
    public static string Status { get; private set; } = "no death yet";

    public static long Deaths { get; private set; }
    public static long Reloads { get; private set; }

    // KF2_AUTORELOAD* was given, so the saved settings must not overwrite it.
    static bool _onFromEnv, _delayFromEnv, _slotFromEnv;

    static long _deadSince;      // TickCount64 at the death edge, 0 when alive
    static bool _fired;          // one reload per death
    static bool _warnedNoSave;

    // A death is a transition *from* alive, and at boot there is nothing to
    // transition from: buf2 is clear, the state byte is whatever was in RAM, and
    // the attract demo runs GAME.EXE's stage 3 like anything else. Without this
    // the reload fires during the demo, which is what it did the first time it
    // was run. Cleared whenever GAME.EXE is (re)loaded, since that is a new
    // character or none.
    static bool _sawAlive;

    // HookManager attributes hooks to a mod so they can be removed again. This is
    // in-project rather than a loaded package, so it declares its own identity.
    static readonly ModInfo _self = new()
    {
        Id = "kf2.autoreload",
        Name = "Auto reload",
        Version = "1.0",
        Description = "Reloads the last save on death.",
    };

    public static void Configure(string? enabled, string? delay, string? slot)
    {
        if (!string.IsNullOrWhiteSpace(enabled))
        {
            Enabled = enabled.Trim().ToLowerInvariant() is "1" or "on" or "true" or "yes";
            _onFromEnv = true;
        }

        if (!string.IsNullOrWhiteSpace(delay))
        {
            if (!float.TryParse(delay, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float seconds))
                throw new ArgumentException($"KF2_AUTORELOAD_DELAY: cannot read '{delay}'");

            SetDelay(seconds);
            _delayFromEnv = true;
        }

        if (!string.IsNullOrWhiteSpace(slot))
        {
            if (!int.TryParse(slot, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int which))
                throw new ArgumentException($"KF2_AUTORELOAD_SLOT: cannot read '{slot}'");

            SetSlot(which);
            _slotFromEnv = true;
        }
    }

    /// <summary>
    /// Attach the hook. Deferred to the first overlay load because
    /// <see cref="SymbolRegistry"/> reads the dispatcher's overlay tables, and
    /// those are registered inside Entry.Run — after Program.cs has run, but before
    /// anything is loaded, so the first load event is the earliest moment every
    /// overlay is resolvable.
    /// </summary>
    public static void Install()
    {
        // The saved choices can only be read once ConfigManager has loaded, which
        // happens inside HostWindow.Initialize -- after Program.cs called Configure.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            if (!_onFromEnv) Enabled = view.GetBool(OnKey, Enabled);
            if (!_delayFromEnv) SetDelay(view.GetFloat(DelayKey, Delay));
            if (!_slotFromEnv) SetSlot(view.GetInt(SlotKey, Slot));
        });

        // GAME.EXE arriving means a new character or none at all, so whatever was
        // watched before it does not carry over.
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            if (!e.Name.Equals("game", StringComparison.Ordinal)) return;
            _deadSince = 0;
            _fired = false;
            _sawAlive = false;
        });

        // Attached whether or not the setting is on: it is a choice that can be
        // taken back, and hooks cannot be added once the game is running past the
        // overlay loads.
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    /// <summary>Change the setting at run time. Safe at any moment — the only
    /// reader is the per-frame hook.</summary>
    public static void SetEnabled(bool on) => Enabled = on;

    /// <summary>Seconds before the reload, clamped to the slider's range.</summary>
    public static void SetDelay(float seconds) => Delay = Math.Clamp(seconds, 0f, 10f);

    /// <summary>0 for the game's own last-used slot, 1..3 to pin one.</summary>
    public static void SetSlot(int slot) => Slot = Math.Clamp(slot, 0, 3);

    static void Attach()
    {
        SymbolRegistry.Build();
        var impl = typeof(AutoReload)
            .GetMethod(nameof(AfterPlayerStage), BindingFlags.Public | BindingFlags.Static)!;

        var target = SymbolRegistry.Resolve("game", null, PlayerStage);
        if (target == null || !HookManager.AddPost(_self, target, impl))
        {
            Console.Error.WriteLine($"[KF2] autoreload: nothing hooked at game/0x{PlayerStage:X8} — " +
                                    "a death will cost the menu, whatever the setting says. " +
                                    "See \"Auto reload\" in NOTES.md.");
            return;
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] autoreload: {(Enabled ? "on" : "off")}, {Delay:0.#}s after death, " +
                          $"slot {(Slot == 0 ? "last used" : Slot.ToString())}");
    }

    /// <summary>
    /// Kill the player the way the game does: zero HP, then the same latch
    /// func_80024F90 calls when a hit takes HP to zero. Dying on purpose to test
    /// this is slow and unreliable, so the settings page offers it as a button.
    /// </summary>
    public static void Simulate()
    {
        var cpu = RecompOne.Runtime.Runtime.Cpu;
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (cpu == null || mem == null) { Status = "not running"; return; }

        if (mem.ReadU16(MaxHp) == 0)
        {
            // buf2 is cleared until an area is up, so a zero max HP means there
            // is no character to kill yet.
            Status = "no area running — load a save first";
            return;
        }

        var saved = cpu.Snapshot();
        mem.WriteU16(Hp, 0);
        cpu.A0 = 0;
        KingsField2.func_8002A264(cpu, mem);
        cpu.Restore(saved);

        Status = "simulated death";
        Console.WriteLine("[KF2] autoreload: simulated death");
    }

    /// <summary>
    /// End of main-loop stage 3. Runs every frame in GAME.EXE, dead or alive.
    /// </summary>
    public static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (!Enabled) return;

        if (m.ReadU8(State) != StateDead)
        {
            // The common path, and it has to stay free: a live player pays one
            // byte read and one word read a frame for the whole patch.
            _deadSince = 0;
            _fired = false;
            // A character with hit points to lose. Max HP is the test rather
            // than current HP because current HP is legitimately zero on the
            // frame you die; max HP is only zero before buf2 has been filled.
            if (m.ReadU16(MaxHp) != 0) _sawAlive = true;
            return;
        }

        if (_deadSince == 0)
        {
            // Never arm from a state we did not watch the player enter.
            if (!_sawAlive) return;
            // HP zero is what separates a death from any other reason the state
            // byte could be sitting at 0x11.
            if (m.ReadU16(Hp) != 0) return;
            _deadSince = Environment.TickCount64;
            Deaths++;
            Console.WriteLine($"[KF2] autoreload: death (LV {m.ReadU8(Level)}, max HP {m.ReadU16(MaxHp)}); " +
                              $"reloading in {Delay:0.#}s");
            return;
        }

        if (_fired) return;

        // Hold the game's post-death clock at the end of the animation. The
        // handler has already run for this frame, so it will increment to 32
        // next frame and take the first step of the fade -- whose amount at 32
        // is (32 - 32) << 7, exactly zero -- before this clamps it back.
        if (m.ReadU16(DeathFrames) > HoldAt) m.WriteU16(DeathFrames, HoldAt);

        if (Environment.TickCount64 - _deadSince < (long)(Delay * 1000f)) return;

        // Released either way: a reload replaces the timeline, and a failed one
        // hands it back so the game does what it always did.
        _fired = true;
        Reload(c, m);
    }

    /// <summary>
    /// The game's own loader, then the game's own post-load arm from
    /// func_80029CBC at 0x80029E0C, transcribed — the mechanism, with no
    /// death-specific messaging, so it serves both a reload from death and a cold
    /// start from the title (patches/AutoStart.cs). Returns func_80023638's result
    /// code: 0 loaded, 1 no such save file, 2 checksum failed; on 0, <paramref
    /// name="area"/> is the loaded save's area and the character is live in it.
    ///
    /// It runs the game's own code on the caller's CpuContext, so it brackets the
    /// whole sequence in Snapshot/Restore. The stack window is for func_80024154's
    /// fifth and sixth arguments, which MIPS passes at sp+0x10 and sp+0x14 of the
    /// caller's frame. Memory is not part of the snapshot, so a caller reads the
    /// loaded HP/level straight back afterwards.
    /// </summary>
    internal static uint LoadSlot(CpuContext c, IMemory m, byte slot, out uint area)
    {
        area = 0;

        var saved = c.Snapshot();

        c.A0 = slot;
        KingsField2.func_80023638(c, m);
        uint result = c.V0;

        if (result == 0)
        {
            KingsField2.func_800240B8(c, m);

            area = m.ReadU8(Area);
            c.SP -= 0x20u;
            m.WriteU32(c.SP + 0x14u, 0xFFu);
            m.WriteU32(c.SP + 0x10u, area);
            c.A0 = area;
            c.A1 = area;
            c.A2 = area;
            c.A3 = area;
            KingsField2.func_80024154(c, m);
            c.SP += 0x20u;

            KingsField2.func_80025D38(c, m);

            // The game reaches that arm from a live state and so never has to
            // clear the death latch. Coming from 0x11, we do -- func_80029E5C is
            // the game's own reset for the state byte and its timers. A cold start
            // is never dead, so this is a no-op for AutoStart.
            if (m.ReadU8(State) == StateDead)
                KingsField2.func_80029E5C(c, m);
        }

        c.Restore(saved);
        return result;
    }

    /// <summary>
    /// Load the last save on death, through <see cref="LoadSlot"/>.
    /// </summary>
    static void Reload(CpuContext c, IMemory m)
    {
        byte slot = Slot != 0 ? (byte)Slot : m.ReadU8(CurrentSlot);
        if (slot == 0)
        {
            Status = "no save to reload — nothing has been saved or loaded this session";
            if (!_warnedNoSave)
            {
                _warnedNoSave = true;
                Console.WriteLine("[KF2] autoreload: died with no save on record; leaving the game alone");
            }
            return;
        }

        ushort held = m.ReadU16(DeathFrames);

        uint result = LoadSlot(c, m, slot, out uint area);

        if (result == 0)
        {
            Reloads++;
            // The counter is the proof the hold worked: it must still be at the
            // animation's end. Anything near 65 means the game's own respawn had
            // already run and this reload is fighting it.
            Status = $"reloaded slot {slot} into area {area} " +
                     $"(HP {m.ReadU16(Hp)}/{m.ReadU16(MaxHp)}, LV {m.ReadU8(Level)}, " +
                     $"held at frame {held})";
            Console.WriteLine($"[KF2] autoreload: {Status}");
        }
        else
        {
            // func_8004A040, the unpack, never ran -- game state is untouched, so
            // the death sequence just carries on.
            string why = result == 1 ? "no such save file" : "checksum failed";
            Status = $"slot {slot} would not load: {why} ({result})";
            Console.WriteLine($"[KF2] autoreload: {Status}");
        }
    }
}
