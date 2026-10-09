// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using System.Globalization;
// HostWindow is in RecompOne.Runtime.Host, not .Host.Window -- the file lives in
// the Window folder but declares the parent namespace. ToastNotifications,
// PanelManager and MenuRegistry really are in .Window.
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using Silk.NET.Input;

namespace Kf2.Mods.Debug;

/// <summary>
/// Keyboard hotkeys, and the pad buttons the game itself does not use.
///
/// HostWindow.IsKeyDown is public and reads the live keyboard, so the toggles
/// are polled once a frame from the same stage-3 hook everything else here uses,
/// and edge-detected in this class. That is simpler than the KeyboardEvent bus
/// and it gives held keys -- which flight needs -- for free.
///
/// The pad half is the same poll through HostWindow.IsPadButtonDown, which
/// answers about one named button -- GetFirstPressedPadButton reports only the
/// lowest index held, so the mute key would be invisible behind anything else.
///
/// F1 and F11 are deliberately not used: the host claims both, F1 for the top
/// bar and F11 for fullscreen. The game's own default keymap is Z X A S Q W E R
/// F G, Enter, RShift and the arrows, so the function keys are otherwise clear.
///
/// V, which neither that keymap nor the port's WASD layout (patches/KeyLayout.cs)
/// binds, is film mode: the cinematic camera, enemies that ignore you and noclip,
/// together. The pad's mute key does the same.
/// </summary>
internal static class Hotkeys
{
    internal static bool Enabled = true;

    internal static Key TogglePanel   = Key.F2;
    internal static Key ToggleNoclip  = Key.F3;
    internal static Key ToggleGodMode = Key.F4;
    internal static Key SaveBookmark  = Key.F5;
    internal static Key LoadBookmark  = Key.F6;
    internal static Key SnapToFloor   = Key.F7;
    internal static Key ReturnToEntry = Key.F8;
    internal static Key FilmMode      = Key.V;
    internal static Key FlyUp         = Key.Space;
    internal static Key FlyDown       = Key.ControlLeft;
    internal static Key FlyFastKey    = Key.ShiftLeft;

    // SDL's GameControllerButton indices, which is the encoding
    // HostWindow.IsPadButtonDown takes. Misc1 is the extra button a pad has
    // beyond the PlayStation layout -- the DualSense's mute key, an Xbox Series
    // pad's share button -- and nothing in the game is bound to it.
    internal const int PadMute        = 15;   // Misc1
    internal const int PadLeftStick   = 7;    // L3, the fast modifier
    internal const int PadLShoulder   = 9;    // L1, fly down
    internal const int PadRShoulder   = 10;   // R1, fly up

    static readonly HashSet<Key> _held = [];
    static readonly HashSet<int> _padHeld = [];

    /// <summary>
    /// True on the frame the key goes down, false while it stays down. Polled,
    /// so a key tapped and released between two frames is missed -- which at 30
    /// fps needs a 33 ms tap and has not come up in practice.
    /// </summary>
    static bool Pressed(Key k)
    {
        bool down = HostWindow.IsKeyDown(k);
        if (!down)
        {
            _held.Remove(k);
            return false;
        }
        return _held.Add(k);
    }

    static bool Down(Key k) => HostWindow.IsKeyDown(k);

    /// <summary>The pad's own edge detect, the same shape as Pressed(Key).</summary>
    static bool PadPressed(int button)
    {
        if (!HostWindow.IsPadButtonDown(button))
        {
            _padHeld.Remove(button);
            return false;
        }
        return _padHeld.Add(button);
    }

    internal static bool PadDown(int button) => Enabled && HostWindow.IsPadButtonDown(button);

    internal static bool FlyFast() => Enabled && (Down(FlyFastKey) || PadDown(PadLeftStick));

    /// <summary>
    /// +1 for up, -1 for down, 0 for neither.
    ///
    /// The shoulders are read off the pad directly rather than from
    /// Controller.State, because that word carries the keyboard's bindings too
    /// and the shipped layout has A and D on L1/R1 -- strafing. Asking the pad
    /// is what keeps one button from meaning two things.
    /// </summary>
    internal static float FlyVertical()
    {
        if (!Enabled) return 0f;
        float v = 0f;
        if (Down(FlyUp) || PadDown(PadRShoulder)) v += 1f;
        if (Down(FlyDown) || PadDown(PadLShoulder)) v -= 1f;
        return v;
    }

    /// <summary>
    /// Run the toggles. Called once a frame from the mod's stage-3 hook.
    /// </summary>
    internal static void Poll()
    {
        FilmModeProbe();
        if (!Enabled) return;

        if (Pressed(TogglePanel))
            DebugPanel.Instance.IsOpen = !DebugPanel.Instance.IsOpen;

        if (Pressed(ToggleNoclip))
        {
            Noclip.Enabled = !Noclip.Enabled;
            Notify("Noclip", Noclip.Enabled);
        }

        if (Pressed(ToggleGodMode))
        {
            Cheats.Invincible = !Cheats.Invincible;
            Notify("Invincibility", Cheats.Invincible);
        }

        if (Pressed(SaveBookmark)) Warp.Save(0);
        if (Pressed(LoadBookmark)) Warp.Restore(0);
        if (Pressed(SnapToFloor)) Noclip.SnapToFloor();
        if (Pressed(ReturnToEntry)) Noclip.ReturnToEntry();

        // Film mode: all three on, or all three off when they already are. No
        // toast either way -- it is for filming, and one would land in the shot.
        // V, or the pad's spare button -- the DualSense's mute key -- does it.
        if (Pressed(FilmMode) || PadPressed(PadMute)) ToggleFilmMode();
    }

    /// <summary>The cinematic camera, enemies ignoring you and noclip: all on,
    /// or all off when they already are.</summary>
    internal static void ToggleFilmMode()
    {
        bool on = !FilmModeOn;
        Noclip.Cinematic = on;
        Cheats.Peaceful = on;
        Noclip.Enabled = on;
    }

    internal static bool FilmModeOn => Noclip.Cinematic && Cheats.Peaceful && Noclip.Enabled;

    // KF2_DEBUG_FILMMODE_PROBE=<seconds> toggles film mode that often, as V
    // would, and prints what each flag reads after -- the check that all three
    // flip together and that the port's mouse lead follows the cinematic camera.
    // There is no other way to press V without a hand on the keyboard.
    static readonly double ProbeSeconds =
        double.TryParse(Environment.GetEnvironmentVariable("KF2_DEBUG_FILMMODE_PROBE"),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 0;
    static long _probeAt;

    static void FilmModeProbe()
    {
        if (ProbeSeconds <= 0) return;
        long now = Environment.TickCount64;
        if (_probeAt == 0) { _probeAt = now; return; }
        if (now - _probeAt < ProbeSeconds * 1000) return;
        _probeAt = now;
        ToggleFilmMode();
        Console.WriteLine($"[kf2debug] film mode probe: on={FilmModeOn} cinematic={Noclip.Cinematic} "
                        + $"peaceful={Cheats.Peaceful} noclip={Noclip.Enabled} "
                        + $"leadSuppressed={Kf2.FrameSmoothing.LeadSuppressed} lead={Verdite.Core.Mouse.Lead}");
    }

    /// <summary>
    /// A toast, so a hotkey press is visible without opening the panel. There is
    /// no way to draw text into the emulated 320x240 picture short of building
    /// GPU primitives, so this sits on top of it instead.
    /// </summary>
    static void Notify(string what, bool on)
    {
        // The cinematic camera is for filming a flythrough: nothing may fade in
        // over the picture while it is on, and that includes these.
        if (Noclip.Cinematic) return;
        ToastNotifications.ShowText("Debug Tools", $"{what} {(on ? "on" : "off")}");
    }

    internal static void Reset()
    {
        _held.Clear();
        _padHeld.Clear();
        _probeAt = 0;
    }
}
