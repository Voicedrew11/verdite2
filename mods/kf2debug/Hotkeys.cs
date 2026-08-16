// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
// HostWindow is in RecompOne.Runtime.Host, not .Host.Window -- the file lives in
// the Window folder but declares the parent namespace. ToastNotifications,
// PanelManager and MenuRegistry really are in .Window.
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using Silk.NET.Input;

namespace Kf2.Mods.Debug;

/// <summary>
/// Keyboard hotkeys.
///
/// HostWindow.IsKeyDown is public and reads the live keyboard, so the toggles
/// are polled once a frame from the same stage-3 hook everything else here uses,
/// and edge-detected in this class. That is simpler than the KeyboardEvent bus
/// and it gives held keys -- which flight needs -- for free.
///
/// F1 and F11 are deliberately not used: the host claims both, F1 for the top
/// bar and F11 for fullscreen. The game's own default keymap is Z X A S Q W E R
/// F G, Enter, RShift and the arrows, so the function keys are otherwise clear.
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
    internal static Key FlyUp         = Key.Space;
    internal static Key FlyDown       = Key.ControlLeft;
    internal static Key FlyFastKey    = Key.ShiftLeft;

    static readonly HashSet<Key> _held = [];

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

    internal static bool FlyFast() => Enabled && Down(FlyFastKey);

    /// <summary>+1 for up, -1 for down, 0 for neither.</summary>
    internal static float FlyVertical()
    {
        if (!Enabled) return 0f;
        float v = 0f;
        if (Down(FlyUp)) v += 1f;
        if (Down(FlyDown)) v -= 1f;
        return v;
    }

    /// <summary>
    /// Run the toggles. Called once a frame from the mod's stage-3 hook.
    /// </summary>
    internal static void Poll()
    {
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
    }

    /// <summary>
    /// A toast, so a hotkey press is visible without opening the panel. There is
    /// no way to draw text into the emulated 320x240 picture short of building
    /// GPU primitives, so this sits on top of it instead.
    /// </summary>
    static void Notify(string what, bool on) =>
        ToastNotifications.ShowText("Debug Tools", $"{what} {(on ? "on" : "off")}");

    internal static void Reset() => _held.Clear();
}
