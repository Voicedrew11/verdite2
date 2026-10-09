using RecompOne.Runtime.Config;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host.Window;
using Silk.NET.Input;

namespace Kf2;

/// <summary>
/// The keyboard layout the port ships, which is not the one RecompOne ships.
///
///     KF2_KEYS=fps      force this layout on (the default for a fresh install)
///     KF2_KEYS=stock    leave RecompOne's Z X A S Q W E R F G alone
///
/// RecompOne's defaults are a *console's* defaults spelled on a keyboard — the
/// face buttons on Z X A S, the shoulders on Q W E R, the D-pad on the arrows.
/// That is the right generic answer for a machine that has to run any PS1 game,
/// and it is the wrong answer for this one, because the D-pad in King's Field
/// walks *and turns*: the arrows alone are a tank control, and a mouse in the
/// other hand has nothing sensible to do.
///
/// Two things about how this is applied are deliberate.
///
/// **A fresh install gets it as a default, not as an override.**
/// <see cref="Configure"/> runs from Program.cs, *before* ConfigManager.Load, and
/// Load writes the object it finds in memory when there is no settings.json to
/// read. So on a first run this is simply what the port's defaults are, and on
/// every run after it the player's own file wins.
///
/// **An existing install is migrated once, and only from stock.** Anyone who
/// already ran the port has a settings.json full of RecompOne's defaults, and a
/// default that only reaches new installs is not much of a default.
/// <see cref="Install"/> therefore rewrites those bindings once — but only if
/// they are *exactly* the stock ones, so a single key someone chose for
/// themselves stops it, and it records that it has run so that deliberately
/// going back to stock is not undone on the next launch.
///
/// The runtime's own "Reset to defaults" button under Input still resets to
/// RecompOne's scheme; <see cref="Kf2.Settings.KeyLayoutPage"/> is the button
/// that puts this one back.
///
/// The second-key handling and the migration live in Verdite Core's
/// <c>KeyLayoutApply</c>; this file keeps the table, the store, and Escape as a
/// second key on the menu button (<see cref="OnKey"/>).
///
/// See "The keyboard layout" in NOTES.md.
/// </summary>
public static class KeyLayout
{
    /// <summary>Which version of the layout the config has been migrated to. Kept
    /// in interface.ini rather than in settings.json, because settings.json is the
    /// thing being migrated and a marker inside it would need the runtime's
    /// schema to grow a field.</summary>
    public const string AppliedKey = "kf2.keys.layout";

    const int Version = 2;

    /// <summary>
    /// Layouts this port has shipped before and has since changed its mind about.
    ///
    /// The migration only rewrites bindings it recognises — stock, or one of
    /// these — because anything else is a choice someone made. That means a change
    /// to <see cref="Layout"/> after release reaches nobody unless the layout it
    /// replaces is recorded here and <see cref="Version"/> is bumped: without both,
    /// an existing config reads as customised and is left alone forever.
    ///
    /// Version 1 is here because it shipped with attack and use the wrong way
    /// round — Space on Cross, F on Square. The static read of `func_8002957C`
    /// named the buttons correctly and then guessed at what their branches did;
    /// playing it settled the opposite, which is the same lesson the analog patch
    /// learned on the pitch sign.
    /// </summary>
    static readonly KeyBindings[] Superseded =
    [
        new()
        {
            Up = "W", Down = "S", L1 = "A", R1 = "D",
            Left = "Left", Right = "Right", L2 = "R", R2 = "F",
            Cross = "Space", Square = "E", Triangle = "Q", Circle = "Tab",
            Select = "ShiftRight", Start = "Enter", L3 = "", R3 = "",
        },
    ];

    /// <summary>
    /// W A S D and the rest. Only the sixteen pad buttons exist, so this says
    /// which *key* presses each one; what the button then does is the game's own
    /// control configuration, exactly as it is for a pad.
    /// </summary>
    public static KeyBindings Layout() => new()
    {
        // Move. The strafes are on the shoulder buttons in this game, which is
        // what lets A and D strafe rather than turn.
        Up = "W",
        Down = "S",
        L1 = "A",
        R1 = "D",

        // Turn. Left and Right stay on the arrows, where they have always been,
        // and the arrows go on walking too (the second key is in Verdite Core).
        Left = "Left",
        Right = "Right",

        // Pitch is the mouse's, and only the mouse's. A keyboard pair for it
        // exists -- the game looks up and down on L2/R2 and a pad still does --
        // but two more keys to learn buy a worse version of something the mouse
        // does continuously, so the keyboard does not carry them.
        L2 = "",
        R2 = "",

        // Act. Square swings, so it gets the thumb; Cross is the action button --
        // doors, levers, the things in front of you -- so it gets F, where thirty
        // years of first-person games have put "use". Q casts.
        Square = "Space",
        Cross = "F",
        Triangle = "Q",
        Circle = "Tab",
        Select = "ShiftRight",
        Start = "Enter",

        // The game reads neither.
        L3 = "",
        R3 = "",
    };

    /// <summary>
    /// Install this as the port's default bindings. **Must be called before
    /// ConfigManager.Load**, i.e. from Program.cs: Load either overwrites this
    /// object from settings.json or, when there is no such file, saves it — which
    /// is precisely the behaviour a default wants.
    /// </summary>
    public static void Configure() =>
        KeyLayoutApply.Configure(Layout, Version, Superseded, Announce, GetApplied, SetApplied);

    const string Announce = "WASD layout applied (W/S walk, A/D strafe, arrows walk and turn, " +
                            "Space attack, F use, Q cast, Tab or Escape menu). Input settings has both layouts.";

    /// <summary>
    /// Migrate an existing settings.json, once, and only if nothing in it was
    /// chosen by hand.
    /// </summary>
    public static void Install()
    {
        KeyLayoutApply.Install();
        Event.AddListener<KeyboardEvent>(OnKey);
        Event.AddListener(_escape);
        Console.WriteLine("[KF2] keys: Escape opens the menu and backs out (Circle, with the port's layout" +
                          " in place and no capture key on Escape)");
    }

    // ---- Escape, the menu's second key ----------------------------------------
    //
    // A desktop game's Escape opens its menu and backs out of it, and Circle does
    // both here: the in-game menu's button (0x8006E56C, a just-pressed edge in
    // func_80029CBC) and the cancel every chooser tests through func_80022E58 --
    // the in-game menu, its pages, the shops, the start and load-slot menus. The
    // runtime's table holds one key a button and Tab has Circle, so Escape presses
    // it inside PAD_dr, as KeyLayoutApply's arrows press Up and Down. See "Escape,
    // the menu's second key" in docs/INPUT.md.
    //
    // Decided once, on the press, not on every PAD_dr: whether the layout is in
    // place is sixteen string compares and an allocation, and PAD_dr is called
    // hundreds of thousands of times a second. Refused for a press that began over
    // a popup (a popup closes on Escape, and the key still held after it closed is
    // not a menu), while text or the remaster editor has the keyboard, and while
    // Escape is the player's own capture key: one saved before the click took
    // capture over is still theirs, and core's Mouse toggles on it.

    static volatile bool _escHeld;
    static long _escReads;
    static long _escDown;

    static void OnKey(KeyboardEvent e)
    {
        if (e.Key != (int)Key.Escape) return;

        if (!e.Pressed)
        {
            if (!_escHeld) return;
            _escHeld = false;
            Console.WriteLine($"[KF2] keys: Escape up after {Environment.TickCount64 - _escDown} ms; " +
                              $"Circle pressed in {Interlocked.Exchange(ref _escReads, 0)} pad reads");
            return;
        }

        string? refused =
            Mouse.CaptureKey == Key.Escape ? "Escape is the capture key" :
            PopupManager.AnyOpen ? "a popup is open" :
            HotkeyGate.Editing ? "text or the editor has the keyboard" :
            !IsApplied() ? "the port's layout is not in place" :
            null;
        if (refused != null)
        {
            Console.WriteLine($"[KF2] keys: Escape not the menu: {refused}");
            return;
        }

        Interlocked.Exchange(ref _escReads, 0);
        _escDown = Environment.TickCount64;
        _escHeld = true;
    }

    static readonly Action<PadReadEvent> _escape = e =>
    {
        if (e.Port != 0 || !_escHeld) return;
        e.Buttons &= unchecked((ushort)~CircleSwapped);
        Interlocked.Increment(ref _escReads);
    };

    // Active low, the two button bytes swapped from Controller's layout.
    const ushort CircleSwapped = unchecked((ushort)((Controller.Circle >> 8) | (Controller.Circle << 8)));

    /// <summary>Write the layout and save it. What the settings button calls.</summary>
    public static void Apply() => KeyLayoutApply.Apply();

    /// <summary>Back to RecompOne's own scheme, and remember that it was asked
    /// for, so the migration above does not undo it on the next launch.</summary>
    public static void ApplyStock() => KeyLayoutApply.ApplyStock();

    public static bool IsApplied() => KeyLayoutApply.IsApplied();

    static int GetApplied() => Settings.PatchSettings.Get(AppliedKey, 0);

    static void SetApplied(int version) => Settings.PatchSettings.Set(AppliedKey, version);
}
