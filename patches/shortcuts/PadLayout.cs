using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace Kf2.Shortcuts;

/// <summary>
/// The pad's Souls layout, while Shortcuts is on: in the world, R1 swings (the game's
/// Square, held as R1 is held, so a bow still draws), L1 casts (the game's Triangle),
/// and Square, the d-pad, L2 and R2 do nothing the game sees. Square uses the current
/// hotkey and the d-pad swaps hands and cycles hotkeys (<see cref="Shortcuts"/> and
/// <see cref="Hands"/> act on the presses counted here). Movement is the sticks':
/// <see cref="Analog"/> runs twin-stick while this is on.
///
/// Only the physical pad: its buttons are watched, unchanged, where the runtime polls
/// them (<see cref="HostWindow.PadButtons"/>, chained after whatever was there), and
/// the game's pad word is rewritten in player control's pre-hook, which runs only in
/// the world. So menus, the keyboard (Space still attacks, A and D still strafe), the
/// mouse and every button the port presses itself keep their meaning.
///
/// Threads: <see cref="Observe"/> runs wherever the runtime polls (the game thread's
/// pad read and the host's present); <see cref="Rewrite"/> and <see cref="Take"/> on
/// the game thread.
/// </summary>
static class PadLayout
{
    // The game's action masks, in the game's byte order.
    const uint MaskSquare = 0x8006E570, MaskTriangle = 0x8006E574;
    const uint MaskR1 = 0x8006E580, MaskR2 = 0x8006E584, MaskL1 = 0x8006E588, MaskL2 = 0x8006E58C;
    const uint MaskUp = 0x8006E590, MaskDown = 0x8006E594, MaskRight = 0x8006E598, MaskLeft = 0x8006E59C;
    const uint PadNow = 0x80199554;

    /// <summary>A press the layout gives a meaning of its own.</summary>
    public enum Press { UseHotkey, SwapWeapon, SwapSpell, NextHotkey, PreviousHotkey }

    static readonly (ushort Bit, Press Press)[] Presses =
    [
        (Controller.Square, Press.UseHotkey),
        (Controller.Right, Press.SwapWeapon),
        (Controller.Left, Press.SwapSpell),
        (Controller.Down, Press.NextHotkey),
        (Controller.Up, Press.PreviousHotkey),
    ];

    /// <summary>Pad buttons the game never sees in the world, with their mask words.</summary>
    static readonly (ushort Bit, uint Mask)[] Taken =
    [
        (Controller.Square, MaskSquare), (Controller.L1, MaskL1), (Controller.R1, MaskR1),
        (Controller.L2, MaskL2), (Controller.R2, MaskR2),
        (Controller.Up, MaskUp), (Controller.Down, MaskDown), (Controller.Left, MaskLeft), (Controller.Right, MaskRight),
    ];

    static Func<int, ushort, ushort>? _next;
    static volatile int _held;                       // port 0's buttons held, active high, Controller's layout
    static readonly int[] _pressed = new int[Presses.Length];

    /// <summary>The layout applies: Shortcuts is on and not stood down.</summary>
    public static bool On => Shortcuts.Live;

    public static void Install()
    {
        _next = HostWindow.PadButtons;
        HostWindow.PadButtons = Observe;
    }

    /// <summary>Counts each new press of a layout button; returns the buttons unchanged.</summary>
    static ushort Observe(int port, ushort buttons)
    {
        if (_next != null) buttons = _next(port, buttons);
        if (port != 0) return buttons;
        int held = ~buttons & 0xFFFF;
        int was = _held;
        _held = held;
        for (int i = 0; i < Presses.Length; i++)
        {
            int bit = Presses[i].Bit;
            if ((held & bit) != 0 && (was & bit) == 0) Interlocked.Increment(ref _pressed[i]);
        }
        return buttons;
    }

    /// <summary>The presses of <paramref name="press"/> since the last take; game thread.</summary>
    public static int Take(Press press) => Interlocked.Exchange(ref _pressed[(int)press], 0);

    /// <summary>Drops every counted press: a menu or a scene was in between.</summary>
    public static void Forget()
    {
        for (int i = 0; i < _pressed.Length; i++) Interlocked.Exchange(ref _pressed[i], 0);
    }

    /// <summary>The pad's buttons, in the game's pad word for this tick: taken ones out,
    /// R1 as Square and L1 as Triangle. Player control's pre-hook; game thread.</summary>
    public static void Rewrite(IMemory m)
    {
        int held = _held;
        if (held == 0) return;
        ushort now = m.ReadU16(PadNow), was = now;
        foreach (var (bit, mask) in Taken)
            if ((held & bit) != 0) now &= (ushort)~m.ReadU32(mask);
        if ((held & Controller.R1) != 0) now |= (ushort)m.ReadU32(MaskSquare);
        if ((held & Controller.L1) != 0) now |= (ushort)m.ReadU32(MaskTriangle);
        if (now != was) m.WriteU16(PadNow, now);
    }
}
