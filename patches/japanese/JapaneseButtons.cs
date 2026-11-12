using System;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;

namespace Kf2.Japanese;

/// <summary>
/// A Japanese run's pad buttons, the Japanese release's way round: ○ confirms, uses and
/// opens, × cancels and opens the in-game menu. The US King's Field has them the other
/// way (Cross confirms, Circle the menu: the action-mask table at 0x8006E568). The
/// Japanese GAME.EXE has no such table and tests the bits in place: 0x40 (Cross) opens
/// the menu at 0x800290A4, and its option list at 0x8001E3F8 confirms on 0x20 (Circle)
/// and cancels on 0x40. docs/JAPANESE.md, "The pad's ○ and ×".
///
///     KF2_JP_BUTTONS=0    keep the US buttons in a Japanese run -- comparison only
///
/// The physical pad's two bits trade places as the runtime polls it (0123's
/// <see cref="HostWindow.PadButtons"/>), not the game's table and not the bindings: the
/// keyboard (F still uses, Tab and Escape still open the menu), the mouse, and every
/// button the port presses itself (auto start, shortcuts, the menu pointer) keep their
/// meaning, and nothing is written to settings. Only while <see cref="JapaneseDisc.Active"/>,
/// which is fixed for the run.
/// </summary>
public static class JapaneseButtons
{
    const ushort Faces = Controller.Cross | Controller.Circle;

    /// <summary>True while the pad's ○ and × are swapped.</summary>
    public static bool On => _allowed && JapaneseDisc.Active;

    static bool _allowed = true;

    public static void Install()
    {
        _allowed = Verdite.Core.Game.Env("JP_BUTTONS") != "0";
        if (!_allowed) return;
        HostWindow.PadButtons = Swap;
    }

    /// <summary>Host thread, in the pad poll. Active low.</summary>
    static ushort Swap(int port, ushort buttons)
    {
        if (!JapaneseDisc.Active) return buttons;
        bool cross = (buttons & Controller.Cross) == 0;
        bool circle = (buttons & Controller.Circle) == 0;
        buttons |= Faces;
        if (cross) buttons &= unchecked((ushort)~Controller.Circle);
        if (circle) buttons &= unchecked((ushort)~Controller.Cross);
        return buttons;
    }
}
