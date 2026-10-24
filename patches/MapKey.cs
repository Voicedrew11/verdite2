using System.Reflection;
using Recompiled;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using RecompOne.Runtime.Host.Window;
using Silk.NET.Input;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// M, or the touchpad of a DualShock 4 or DualSense, opens the game's own map item
/// as using it from the in-game ITEM page would:
///
///     KF2_MAPKEY=1        on (the default); 0 leaves M and the touchpad alone
///     KF2_MAPKEY_TEST=10  open the map ten seconds into the first area, as a press would
///
/// The three maps are items 0x43 PIRATE'S MAP, 0x44 MINER'S MAP and 0x45 NECRON'S
/// MAP. The ITEM page (<c>func_80019204</c>) does not use them the way it uses a
/// potion: it runs their picture viewer <c>func_80019B54(id)</c> directly, which
/// draws the picture until any pad button is pressed. The first map held, in id
/// order, is the one opened; with none held nothing happens.
///
/// The viewer is a menu page's routine, so the wrapping the in-game menu gives it
/// is reproduced: the menu's enter <c>func_80022754</c>; the page's own call before
/// the viewer, <c>func_80022D80</c>, which frees the preview model; the viewer; the
/// page's call after it, <c>func_80022DC4(0x12)</c>; the menu's leave
/// <c>func_800228C8</c>. The press is taken in a post hook on main-loop stage 3
/// (<c>func_8002A550</c>, the player's stage) on a world tick, where the in-game
/// menu opens from too, so nothing modal is open there. While the picture is up,
/// M or the touchpad again holds Circle through <see cref="PadReadEvent"/>, which
/// closes it. See "The maps" in docs/GAME_INTERNALS.md.
/// </summary>
public static class MapKey
{
    const uint PlayerStage = 0x8002A550;
    const uint Viewer = 0x80019B54;

    const uint Inventory = 0x8009B52C;      // u8 per item id, 0 = not held
    const uint FirstMap = 0x43, LastMap = 0x45;

    const uint MaxHp = 0x80199426;          // u16, 0 with no character
    const uint State = 0x801994E1;          // u8, the action byte
    const byte StateDead = 0x11;

    const long RequestMaxAgeMs = 500;       // a press older than this is dropped
    const long OverlayQuietMs = 1000;       // no press is taken this soon after an overlay loads

    const int PadTouchpad = 20;             // SDL_CONTROLLER_BUTTON_TOUCHPAD

    public static bool Enabled { get; private set; } = true;

    static volatile bool _request, _open, _closeHeld;
    static bool _queued, _viewerQueued;
    static long _requestedAt, _overlayAt = -1_000_000;
    static long _lastFrame = -1;
    static double _testAt;
    static uint _give;
    static double _closeAfter;
    static long _openedAt;
    static bool _given;
    static readonly System.Diagnostics.Stopwatch _testClock = new();

    static readonly ModInfo _self = new()
    {
        Id = "kf2.mapkey",
        Name = "Map key",
        Version = "1.0",
        Description = "M or the touchpad opens the game's map item, if one is held.",
    };

    public static void Configure(string? enabled, string? test)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled.Trim() is not ("0" or "off");
        if (!string.IsNullOrWhiteSpace(test) &&
            double.TryParse(test, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            _testAt = Math.Max(seconds, 0.1);
    }

    /// <summary>KF2_MAPKEY_GIVE=0x43 (or 67): give that item once, through the game's
    /// own give, in the first live tick. A test switch for a save with no map in it.</summary>
    public static void Configure(string? enabled, string? test, string? give, string? close = null)
    {
        Configure(enabled, test);
        if (!string.IsNullOrWhiteSpace(close) &&
            double.TryParse(close, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            _closeAfter = Math.Max(seconds, 0.1);
        if (string.IsNullOrWhiteSpace(give)) return;
        string g = give.Trim();
        bool hex = g.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? g[2..] : g,
                           hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out uint id) || id > 0x77)
            throw new ArgumentException($"KF2_MAPKEY_GIVE: cannot read '{give}'");
        _give = id;
    }

    public static void Install()
    {
        HookAttach.OnOverlayLoad("map viewer", AttachViewer);
        if (!Enabled) return;

        Event.AddListener<KeyboardEvent>(e =>
        {
            if (e.Key != (int)Key.M || e.Repeat) return;
            if (e.Pressed && (PopupManager.AnyOpen || HotkeyGate.Typing)) return;
            Press(e.Pressed);
        });
        Event.AddListener<ControllerEvent>(e =>
        {
            if (e.Button != PadTouchpad) return;
            if (e.Pressed && PopupManager.AnyOpen) return;
            Press(e.Pressed);
        });
        Event.AddListener<PadReadEvent>(e =>
        {
            if (e.Port != 0 || !_open) return;
            // KF2_MAPKEY_CLOSE: a test close, Circle held this many seconds after it opens.
            if (_closeAfter > 0)
            {
                long into = Environment.TickCount64 - _openedAt;
                if (into >= _closeAfter * 1000 && into < _closeAfter * 1000 + 300)
                {
                    ushort circle = Controller.Circle;
                    e.Buttons &= (ushort)~(ushort)((circle >> 8) | (circle << 8));
                    return;
                }
            }
            if (!_closeHeld) return;
            // Active low, the two button bytes swapped from Controller's layout.
            ushort bit = Controller.Circle;
            e.Buttons &= (ushort)~(ushort)((bit >> 8) | (bit << 8));
        });
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            _overlayAt = Environment.TickCount64;
            if (_testAt > 0 && !_testClock.IsRunning && e.Name.StartsWith("fdat", StringComparison.Ordinal))
                _testClock.Start();
        });

        HookAttach.OnOverlayLoad("map key", Attach);
    }

    /// <summary>A press opens the map, or closes the one open; a release ends the
    /// Circle it held down.</summary>
    static void Press(bool pressed)
    {
        if (!pressed) { _closeHeld = false; return; }
        if (_open) { _closeHeld = true; return; }
        _requestedAt = Environment.TickCount64;
        _request = true;
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("game", null, PlayerStage);
        if (target == null) return false;
        if (!_queued)
        {
            var impl = typeof(MapKey).GetMethod(nameof(AfterPlayerStage), BindingFlags.Public | BindingFlags.Static)!;
            _queued = HookManager.AddPost(_self, target, impl);
            if (!_queued) return false;
        }
        HookManager.Commit();

        bool ok = HookAttach.Installed(target);
        Console.WriteLine(ok ? "[KF2] map key: on (M, touchpad)" : "[KF2] map key: the stage 3 hook did not install");
        return ok;
    }

    /// <summary>The viewer's watchdog hold, attached with the key on or off.</summary>
    static bool AttachViewer()
    {
        SymbolRegistry.Build();
        var viewer = SymbolRegistry.Resolve("game", null, Viewer);
        if (viewer == null) return false;
        if (!_viewerQueued)
        {
            var hold = typeof(MapKey).GetMethod(nameof(ViewerUnwatched), BindingFlags.NonPublic | BindingFlags.Static)!;
            _viewerQueued = HookManager.AddReplace(_self, viewer, hold);
            if (!_viewerQueued) return false;
        }
        HookManager.Commit();
        bool ok = HookAttach.Installed(viewer);
        if (!ok) Console.WriteLine("[KF2] map key: the viewer's watchdog hold did not install");
        return ok;
    }

    /// <summary>The viewer, from the ITEM page or the key, with the hang watchdog
    /// paused: it draws the picture twice and then polls the pad, presenting nothing,
    /// until a button, so a map looked at for 15 s wrote a hang report. See "The maps"
    /// in docs/GAME_INTERNALS.md.</summary>
    static void ViewerUnwatched(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        bool was = RecompOne.Runtime.Diagnostics.Watchdog.Paused;
        RecompOne.Runtime.Diagnostics.Watchdog.Paused = true;
        try { orig(c, m); }
        finally { RecompOne.Runtime.Diagnostics.Watchdog.Paused = was; }
    }

    /// <summary>End of main-loop stage 3, on a world tick. A press older than half a
    /// second (made in a menu, or while loading) is dropped.</summary>
    public static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (!FramePacing.FirstWalkOfTick(ref _lastFrame) || m is not PSMemory mem) return;
        if (_give != 0 && !_given && m.ReadU16(MaxHp) != 0)
        {
            _given = true;
            c.A0 = _give;
            KingsField2.func_80048178(c, mem);
            Console.WriteLine($"[KF2] map key: gave item 0x{_give:X2} (KF2_MAPKEY_GIVE), held={m.ReadU8(Inventory + _give)}");
        }
        if (_testAt > 0 && _testClock.IsRunning && _testClock.Elapsed.TotalSeconds >= _testAt)
        {
            _testAt = 0;
            _testClock.Stop();
            _requestedAt = Environment.TickCount64;
            _request = true;
        }
        if (!_request) return;
        _request = false;
        if (Environment.TickCount64 - _requestedAt > RequestMaxAgeMs) return;

        if (m.ReadU16(MaxHp) == 0 || m.ReadU8(State) == StateDead)
        {
            Console.WriteLine("[KF2] map key: refused, no character or dead");
            return;
        }
        if (Environment.TickCount64 - _overlayAt < OverlayQuietMs)
        {
            Console.WriteLine("[KF2] map key: refused, an area is loading");
            return;
        }
        if (MenuWorld.MessageLive)
        {
            Console.WriteLine("[KF2] map key: refused, a message box is up");
            return;
        }

        uint? map = null;
        for (uint id = FirstMap; id <= LastMap && map == null; id++)
            if (m.ReadU8(Inventory + id) != 0) map = id;
        if (map == null)
        {
            Console.WriteLine("[KF2] map key: no map held");
            return;
        }

        Console.WriteLine($"[KF2] map key: map item 0x{map:X2}, area {m.ReadU8(0x8017E060)}");
        var saved = c.Snapshot();
        _open = true;
        _openedAt = Environment.TickCount64;
        try
        {
            KingsField2.func_80022754(c, mem);     // the menu's enter
            KingsField2.func_80022D80(c, mem);     // the ITEM page, before its viewer
            c.A0 = map.Value;
            KingsField2.func_80019B54(c, mem);     // the map viewer, to any pad button
            c.A0 = 0x12;
            KingsField2.func_80022DC4(c, mem);     // the ITEM page, after its viewer
            KingsField2.func_800228C8(c, mem);     // the menu's leave
        }
        finally
        {
            _open = false;
            _closeHeld = false;
            c.Restore(saved);
        }
        Console.WriteLine("[KF2] map key: closed");
    }
}
