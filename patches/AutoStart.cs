using System.Reflection;
using System.Threading;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Boot straight into a save, so an automated agent — or an impatient human —
/// lands in an area without a person driving the title and start menus:
///
///     KF2_AUTOSTART=2    load slot 1..3 at boot (off unless set)
///
/// It exists because those menus are the wall every agent hits: with no input the
/// port never leaves OPEN.EXE's title, and scripted input could not get past it
/// either (KF2_AUTOPAD's clock does not start until the first area module loads,
/// the very thing that has not happened yet). So this drives the pad itself, three
/// steps:
///
///   1. Pulse Start through OPEN.EXE's intro and title, which advances it to
///      GAME.EXE. The pad is injected through PAD_dr (a <c>PadReadEvent</c>
///      listener), the path that reaches the game wherever it is — including its
///      menus, which writing Controller.State does not.
///   2. Pulse Cross at GAME.EXE's start menu, whose default is New Game, to drop
///      into the opening area fdat02. That is a real area with the main loop
///      turning — which the start menu, a blocking poll loop, never reaches.
///   3. From the stage-3 hook that only runs once an area is live, load the chosen
///      slot over the New Game through <see cref="AutoReload.LoadSlot"/> — the same
///      menu-free loader path AutoReload uses on death. The New Game is a scratch
///      vehicle: nothing is saved, and the load replaces it with the slot's own
///      area and character.
///
/// End to end the beacon (KF2_AGENT) shows main -> open -> game -> fdat02 ->
/// (slot's area), with the character's HP/level/area switching to the save's. A
/// patch rather than a mod for the KF2_AUTOPAD reason: it must work from an
/// environment variable with no package to enable. Off unless KF2_AUTOSTART is set.
/// </summary>
public static class AutoStart
{
    // A live character in an area -> nonzero max HP. buf2 is clear until an area is
    // up. See mods/kf2debug/GameState.cs for the map.
    const uint MaxHp = 0x80199426;   // u16
    const uint Hp    = 0x80199428;   // u16
    const uint Level = 0x8019941C;   // u8

    // End of main-loop stage 3, GAME.EXE's per-frame hook site -- the same one
    // AutoReload watches. It runs only once an area is live, never at the start
    // menu (a blocking poll loop), which is why the load happens after a New Game
    // rather than at the menu.
    const uint PlayerStage = 0x8002A550;

    // Frames to let the New Game settle before loading the slot over it.
    const int SettleFrames = 90;

    // Start-menu Cross cadence: a short press once a second, repeated until we
    // leave the menu, so the exact moment the menu goes live does not matter.
    const double CrossPeriod = 1.0;
    const double CrossHold   = 0.15;

    /// <summary>1..3 to load that slot at boot; 0 (the default) is off.</summary>
    public static int Slot { get; private set; }

    /// <summary>What the boot sequence did, for the log.</summary>
    public static string Status { get; private set; } = "off";

    // One shot per GAME.EXE, reset when GAME.EXE (re)loads since that is a fresh
    // title.
    static bool _fired;
    static int _ticks;

    // The overlay up now, tracked for the input driver and the load gate.
    static volatile string _overlay = "boot";

    // The Controller bits pressed this instant, set by the driver thread and read
    // by the PAD_dr listener. 0 = nothing held.
    static volatile ushort _inject;

    // HookManager attributes hooks to a mod so they can be removed again; this is
    // in-project rather than a package, so it declares its own identity.
    static readonly ModInfo _self = new()
    {
        Id = "kf2.autostart",
        Name = "Auto start",
        Version = "1.0",
        Description = "Boots straight into a save, skipping the title menus.",
    };

    public static void Configure(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot)) return;
        if (!int.TryParse(slot.Trim(), System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out int which))
            throw new ArgumentException($"KF2_AUTOSTART: cannot read '{slot}'");
        Slot = Math.Clamp(which, 0, 3);
    }

    public static void Install()
    {
        if (Slot == 0) return;

        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            _overlay = e.Name;
            // GAME.EXE arriving means a fresh title, so arm again.
            if (!e.Name.Equals("game", StringComparison.Ordinal)) return;
            _fired = false;
            _ticks = 0;
        });

        // Attach the stage-3 hook once GAME.EXE itself is loaded, so its symbols
        // are the live ones.
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            if (attached || !e.Name.Equals("game", StringComparison.Ordinal)) return;
            attached = true;
            Attach();
        });

        // Inject through PAD_dr, the path that reaches the game wherever it is --
        // including its boot menus, which reading Controller.State does not. The
        // pad buffer is active-low and its two button bytes are swapped against
        // Controller's layout (see patches/Mouse.cs), so a pressed Controller bit
        // becomes a cleared swapped bit here.
        Event.AddListener<PadReadEvent>(e =>
        {
            if (e.Port != 0) return;
            ushort press = _inject;
            if (press != 0)
                e.Buttons &= (ushort)~(ushort)((press >> 8) | (press << 8));
        });

        new Thread(Drive) { IsBackground = true, Name = "kf2-autostart" }.Start();
        Console.WriteLine($"[KF2] autostart: booting into slot {Slot}");
    }

    /// <summary>
    /// The input driver: pulse Start through OPEN.EXE, then Cross at GAME.EXE's
    /// start menu, until an area is live and the driver falls silent.
    /// </summary>
    static void Drive()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ushort press = 0;
            double t = sw.Elapsed.TotalSeconds;

            if (_overlay == "open")
            {
                // ~14-of-30-frame press cadence at 60 Hz: clean Start edges.
                if ((long)(t * 60) % 30 < 14) press = Controller.Start;
            }
            else if (_overlay == "game" && !_fired)
            {
                if (t % CrossPeriod < CrossHold) press = Controller.Cross;
            }

            _inject = press;
            Thread.Sleep(1);
        }
    }

    static void Attach()
    {
        SymbolRegistry.Build();
        var impl = typeof(AutoStart)
            .GetMethod(nameof(AfterPlayerStage), BindingFlags.Public | BindingFlags.Static)!;

        var target = SymbolRegistry.Resolve("game", null, PlayerStage);
        if (target == null || !HookManager.AddPost(_self, target, impl))
        {
            Console.Error.WriteLine($"[KF2] autostart: nothing hooked at game/0x{PlayerStage:X8} — " +
                                    "cannot load a save at boot.");
            return;
        }

        HookManager.Commit();
    }

    /// <summary>
    /// End of main-loop stage 3. It only runs once GAME.EXE is in a real area (the
    /// start menu never reaches here), so by the time this fires the New Game is
    /// live; after a short settle, load the chosen slot over it.
    /// </summary>
    public static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (_fired) return;

        if (m.ReadU16(MaxHp) == 0) return;
        if (!_overlay.StartsWith("fdat", StringComparison.Ordinal)) return;
        if (++_ticks < SettleFrames) return;

        _fired = true;
        uint result = AutoReload.LoadSlot(c, m, (byte)Slot, out uint area);

        if (result == 0)
        {
            Status = $"loaded slot {Slot} into area {area} " +
                     $"(HP {m.ReadU16(Hp)}/{m.ReadU16(MaxHp)}, LV {m.ReadU8(Level)})";
            Console.WriteLine($"[KF2] autostart: {Status}");
        }
        else
        {
            string why = result == 1 ? "no such save file" : "checksum failed";
            Status = $"slot {Slot} would not load: {why} ({result})";
            Console.WriteLine($"[KF2] autostart: {Status}");
        }
    }
}
