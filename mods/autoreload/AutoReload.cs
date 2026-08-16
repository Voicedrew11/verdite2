// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;

namespace Kf2.Mods.AutoReload;

/// <summary>
/// Reload the last save on death.
///
/// King's Field has no retry. Dying leaves you in the game's death sequence and
/// the way back is the menu, then the load screen, then the slot -- four screens
/// of the game's own UI between a death and being where you were.
///
/// The mod adds no loading path of its own. The game can already load a save
/// without leaving the area it is running: `func_80029CBC` handles the in-game
/// menu's result, and its -3 arm -- "the menu loaded a save" -- is twelve
/// instructions at 0x80029E0C:
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
/// never-saved case, and the mod stays out of the way for it.
/// </summary>
public sealed class AutoReloadMod : IMod
{
    // ---- GAME.EXE player state ----
    //
    // The stat block is buf2, the 0x58-byte per-area buffer at 0x80199414. It was
    // found through the memory card: func_80023CC0 stamps the decimal EXP and LV
    // into the save's 64-byte Shift-JIS title, reading them from the first two
    // fields here.
    const uint Exp        = 0x80199414;   // u32
    const uint Level      = 0x8019941C;   // u8
    const uint MaxHp      = 0x80199426;   // u16
    const uint Hp         = 0x80199428;   // u16, current
    const uint MaxMp      = 0x8019942A;   // u16
    const uint Mp         = 0x8019942C;   // u16, current

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
    // 65 frames is 2.17 s at 30 fps and 1.08 s at 60, so any delay long enough
    // to read as deliberate loses a race with it. Holding the counter here is
    // what makes the delay ours: the animation finishes, the fade never starts,
    // and the game's own respawn never comes due.
    const uint DeathFrames = 0x8019951A;  // u16
    const ushort HoldAt    = 31;

    // The slot last saved to or loaded from, written by both func_80023638 and
    // func_80023764. Zero in the executable image, so zero means "neither has
    // run this session".
    const uint CurrentSlot = 0x8006E5D4;  // u8

    const string EnabledKey = "kf2.autoreload.enabled";
    const string DelayKey   = "kf2.autoreload.delay";
    const string SlotKey    = "kf2.autoreload.slot";

    static bool _enabled = true;

    // Long enough that the death registers as a death. The game's own sequence
    // -- the camera tipping over, the sound -- runs underneath it, so this is
    // how much of that sequence the player sees before the area comes back.
    static float _delay = 2.0f;

    // 0 = whatever the game says the current slot is; 1..3 pins one.
    static int _slotOverride;

    static long _deadSince;      // TickCount64 at the death edge, 0 when alive
    static bool _fired;          // one reload per death
    static bool _warnedNoSave;

    // A death is a transition *from* alive, and at boot there is nothing to
    // transition from: buf2 is clear, the state byte is whatever was in RAM, and
    // the attract demo runs GAME.EXE's stage 3 like anything else. Without this
    // the mod reloads during the demo, which is what it did the first time it
    // was run. Cleared whenever GAME.EXE is (re)loaded, since that is a new
    // character or none.
    static bool _sawAlive;

    static long _deaths, _reloads;
    static string _status = "no death yet";

    public void OnLoad()
    {
        _enabled = RecompOne.Runtime.Runtime.View.GetBool(EnabledKey, true);
        _delay = RecompOne.Runtime.Runtime.View.GetFloat(DelayKey, 2.0f);
        _slotOverride = RecompOne.Runtime.Runtime.View.GetInt(SlotKey, 0);

        ReadEnv("KF2_AUTORELOAD", ref _enabled);
        ReadEnv("KF2_AUTORELOAD_DELAY", ref _delay);
        ReadEnv("KF2_AUTORELOAD_SLOT", ref _slotOverride);

        _delay = Math.Clamp(_delay, 0f, 10f);
        _slotOverride = Math.Clamp(_slotOverride, 0, 3);

        Event.AddListener<OverlayLoadedEvent>(OnOverlay);

        Console.WriteLine($"[autoreload] {(_enabled ? "on" : "off")}, {_delay:0.#}s after death, " +
                          $"slot {(_slotOverride == 0 ? "last used" : _slotOverride.ToString())}");
    }

    public void OnUnload()
    {
        Event.RemoveListener<OverlayLoadedEvent>(OnOverlay);

        // Nothing to undo in game memory -- the mod only ever calls the game's
        // own routines. Just drop the arming so a reload cannot fire from a
        // death that happened while the mod was loaded.
        _deadSince = 0;
        _fired = false;
        _sawAlive = false;
    }

    // GAME.EXE arriving means a new character or none at all, so whatever we
    // watched before it does not carry over.
    static void OnOverlay(OverlayLoadedEvent e)
    {
        if (!e.Name.Equals("game", StringComparison.Ordinal)) return;
        _deadSince = 0;
        _fired = false;
        _sawAlive = false;
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped("Watches for the player's death and reloads the save last saved to or " +
                          "loaded from, by calling the game's own loader and its own post-load " +
                          "area re-entry. Nothing is restored by hand.");
        ImGui.Separator();

        bool on = _enabled;
        if (ImGui.Checkbox("Enabled", ref on))
        {
            _enabled = on;
            RecompOne.Runtime.Runtime.View.SetBool(EnabledKey, _enabled);
            RecompOne.Runtime.Runtime.SaveView();
        }

        float delay = _delay;
        if (ImGui.SliderFloat("Delay after death (s)", ref delay, 0f, 10f))
        {
            _delay = delay;
            RecompOne.Runtime.Runtime.View.SetFloat(DelayKey, _delay);
            RecompOne.Runtime.Runtime.SaveView();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How much of the game's own death sequence plays before the reload. " +
                             "Zero reloads the frame you die, which reads as a glitch rather than " +
                             "as a death.");

        int slot = _slotOverride;
        if (ImGui.Combo("Save slot", ref slot, "Last used\0Slot 1\0Slot 2\0Slot 3\0"))
        {
            _slotOverride = slot;
            RecompOne.Runtime.Runtime.View.SetInt(SlotKey, _slotOverride);
            RecompOne.Runtime.Runtime.SaveView();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("\"Last used\" is the game's own record at 0x8006E5D4, which both " +
                             "saving and loading write. Pinning a slot ignores where you actually " +
                             "saved.");

        ImGui.Separator();
        ImGui.TextWrapped(_status);
        ImGui.Text($"deaths seen {_deaths}, reloads {_reloads}");

        ImGui.Separator();
        ImGui.TextWrapped("Dying on purpose to test this is slow and unreliable, so:");
        if (ImGui.Button("Simulate death"))
            Simulate();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Zeroes HP and calls the game's own death latch, exactly as the " +
                             "damage path does. Does nothing unless an area is running.");
    }

    /// <summary>
    /// End of main-loop stage 3, which runs every frame in GAME.EXE -- including
    /// while dead, since the death sequence is one arm of stage 3's own state
    /// machine.
    ///
    /// Not func_80029CBC, which is where the game does this from: that is
    /// dispatched only from the arms for states 1 and 2, so it stops being called
    /// the moment the state byte latches to 0x11.
    /// </summary>
    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (!_enabled) return;

        if (m.ReadU8(State) != StateDead)
        {
            // The common path, and it has to stay free: a live player pays one
            // byte read and one word read a frame for the whole mod.
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
            _deaths++;
            Console.WriteLine($"[autoreload] death (LV {m.ReadU8(Level)}, max HP {m.ReadU16(MaxHp)}); " +
                              $"reloading in {_delay:0.#}s");
            return;
        }

        if (_fired) return;

        // Hold the game's post-death clock at the end of the animation. The
        // handler has already run for this frame, so it will increment to 32
        // next frame and take the first step of the fade -- whose amount at 32
        // is (32 - 32) << 7, exactly zero -- before this clamps it back.
        if (m.ReadU16(DeathFrames) > HoldAt) m.WriteU16(DeathFrames, HoldAt);

        if (Environment.TickCount64 - _deadSince < (long)(_delay * 1000f)) return;

        // Released either way: a reload replaces the timeline, and a failed one
        // hands it back so the game does what it always did.
        _fired = true;
        Reload(c, m);
    }

    /// <summary>
    /// The game's own loader, then the game's own post-load arm from
    /// func_80029CBC at 0x80029E0C, transcribed. The stack window is for
    /// func_80024154's fifth and sixth arguments, which MIPS passes at sp+0x10
    /// and sp+0x14 of the caller's frame.
    /// </summary>
    static void Reload(CpuContext c, IMemory m)
    {
        byte slot = _slotOverride != 0 ? (byte)_slotOverride : m.ReadU8(CurrentSlot);
        if (slot == 0)
        {
            _status = "no save to reload -- nothing has been saved or loaded this session";
            if (!_warnedNoSave)
            {
                _warnedNoSave = true;
                Console.WriteLine("[autoreload] died with no save on record; leaving the game alone");
            }
            return;
        }

        ushort held = m.ReadU16(DeathFrames);

        var saved = c.Snapshot();

        c.A0 = slot;
        KingsField2.func_80023638(c, m);
        uint result = c.V0;

        if (result == 0)
        {
            KingsField2.func_800240B8(c, m);

            uint area = m.ReadU8(Area);
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
            // the game's own reset for the state byte and its timers.
            if (m.ReadU8(State) == StateDead)
                KingsField2.func_80029E5C(c, m);

            _reloads++;
            // The counter is the proof the hold worked: it must still be at the
            // animation's end. Anything near 65 means the game's own respawn had
            // already run and this reload is fighting it.
            _status = $"reloaded slot {slot} into area {area} " +
                      $"(HP {m.ReadU16(Hp)}/{m.ReadU16(MaxHp)}, LV {m.ReadU8(Level)}, " +
                      $"held at frame {held})";
            Console.WriteLine($"[autoreload] {_status}");
        }
        else
        {
            // func_8004A040, the unpack, never ran -- game state is untouched, so
            // the death sequence just carries on.
            string why = result == 1 ? "no such save file" : "checksum failed";
            _status = $"slot {slot} would not load: {why} ({result})";
            Console.WriteLine($"[autoreload] {_status}");
        }

        c.Restore(saved);
    }

    /// <summary>
    /// Kill the player the way the game does: zero HP, then the same latch
    /// func_80024F90 calls when a hit takes HP to zero.
    /// </summary>
    static void Simulate()
    {
        var cpu = RecompOne.Runtime.Runtime.Cpu;
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (cpu == null || mem == null) { _status = "not running"; return; }

        if (mem.ReadU16(MaxHp) == 0)
        {
            // buf2 is cleared until an area is up, so a zero max HP means there
            // is no character to kill yet.
            _status = "no area running -- load a save first";
            return;
        }

        var saved = cpu.Snapshot();
        mem.WriteU16(Hp, 0);
        cpu.A0 = 0;
        KingsField2.func_8002A264(cpu, mem);
        cpu.Restore(saved);

        _status = "simulated death";
        Console.WriteLine("[autoreload] simulated death");
    }

    static void ReadEnv(string name, ref bool value)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return;
        v = v.Trim().ToLowerInvariant();
        value = v is "1" or "on" or "true" or "yes";
    }

    static void ReadEnv(string name, ref float value)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (float.TryParse(v, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float f))
            value = f;
    }

    static void ReadEnv(string name, ref int value)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(v, System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out int i))
            value = i;
    }
}
