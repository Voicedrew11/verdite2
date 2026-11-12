using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2.Shortcuts;

/// <summary>
/// The hands and the current hotkey, bottom left, as three cards in a Souls cross: the
/// left the magic hand, the right the weapon hand, the one between them and lower the
/// hotkey Square uses. Each card is the HP panel's own frame (<see cref="CardFrame"/>)
/// round the item's model (<see cref="HandsIcons"/>; a spell shows its element's
/// crystal and its level as a Roman numeral). A hand's card has the number of the slot
/// that is out in its top right corner, an item hotkey its count in the bottom right.
///
/// Laid out in the game's pixels to work at 240p: the cards are 28 x 42, a pixel apart,
/// as wide together as the HP panel, their bevel a pixel a side narrower than the
/// panel's for room inside; their digits and numerals are the menu's fonts at
/// their own size. The margins are the panel's: its left edge, and its distance from the
/// top as the distance from the bottom. They follow the panel to the screen's edge when
/// the HUD is anchored (<see cref="Widescreen.AnchoredHudX"/>).
///
/// Drawn after the HUD builder (func_80031D5C, which stage 13 and MenuWorld's passes both
/// call), only when the builder drew the HP panel: record 1's drawn byte, not the options
/// screen's toggle at 0x801994DD it is copied from. Before a menu, a shop or a talk the
/// game draws one frame with the toggle zeroed (func_80029C50), which leaves the records
/// hidden, and MenuWorld's passes draw from the records as they stand. The frames and
/// models at the panel's and its contents' depths, the digits on ordering-table slot 1
/// where the game's own 2D HUD sprites go.
///
/// Game thread.
/// </summary>
static class HandsHud
{
    const uint PanelDrawn = Stage13.HudRecords + Stage13.HudStride;   // record 1's +0: u8 1 while the HP panel draws
    const uint ContentDepth = PanelDrawn + 0x14;                      // record 1's Z: the panel's letters and digits
    const uint Descriptor = MenuDraw.ActiveDescriptor;
    const uint TextTemplate = MenuDraw.TextTemplate, NumberTemplate = MenuDraw.NumberTemplate;
    const int Slot = 1;
    const byte White = 0xFF, TextGrey = 0x80;
    const int Room = 64 * MenuDraw.QuadBytes;                         // ours, the models' and the frames'
    const int ScreenHeight = 240;

    // The cross, in the game's pixels.
    const int CardWidth = 28, CardHeight = 42, Gap = 1;
    const int BevelLess = 1;                                          // the panel's bevel, this much narrower a side
    const int Drop = 14;                                              // the hotkey's card, below the hands'

    /// <summary>The icon slots of <see cref="HandsIcons"/>.</summary>
    const int WeaponIcon = 0, HotkeyIcon = 1, MagicIcon = 2;

    /// <summary>Called from player control's pre-hook: reads the models the cards will
    /// want, so the draw never touches the disc. A spell's model is its crystal.</summary>
    internal static void Prepare(CpuContext c, IMemory m, bool probe)
    {
        HandsIcons.Want(c, m, WeaponIcon, m.ReadU8(Hands.Weapon), probe);
        HandsIcons.Want(c, m, MagicIcon, SpellCrystals.Crystal(m.ReadU8(Hands.AttackSpell)), probe);
        var (spell, item) = Labels.Held(m, Shortcuts.Hotkey);
        HandsIcons.Want(c, m, HotkeyIcon, spell != Shortcuts.None ? SpellCrystals.Crystal(spell) : item, probe);
    }

    /// <summary>The cross, in the HUD builder's post-hook. Drawn as the HUD is drawn:
    /// with <see cref="PolyAssembler.InHud"/> held, which per-pixel lighting and the
    /// packet depth raise only inside the builder, so the lit assembler leaves the
    /// cards and models their own colours and no depth, whichever order the posts run in.</summary>
    internal static void Draw(CpuContext c, IMemory m)
    {
        if (m.ReadU8(PanelDrawn) != 1) return;
        bool wasInHud = PolyAssembler.InHud;
        PolyAssembler.InHud = true;
        try { DrawCross(c, m); }
        finally { PolyAssembler.InHud = wasInHud; }
    }

    static void DrawCross(CpuContext c, IMemory m)
    {
        uint d = m.ReadU32(Descriptor);
        if (!HasRoom(m, d)) return;

        uint panel = Stage13.HudRecords + CardFrame.PanelRecord * Stage13.HudStride;
        int left = Widescreen.AnchoredHudX(Widescreen.NativeHudX(m, CardFrame.PanelRecord));
        int bottom = ScreenHeight - (short)m.ReadU16(panel + 0x12);
        int handsY = bottom - Drop - CardHeight, hotkeyY = bottom - CardHeight;
        int magicX = left, hotkeyX = left + CardWidth + Gap, weaponX = left + 2 * (CardWidth + Gap);

        var right = Hands.Right;
        var hand = Hands.Left;
        byte weapon = m.ReadU8(Hands.Weapon), spell = m.ReadU8(Hands.AttackSpell);
        var (hSpell, hItem) = Labels.Held(m, Shortcuts.Hotkey);

        // The frames first: the lit assembler links them at the panel's depth, behind
        // the models at its contents' depth.
        Frame(c, m, d, magicX, handsY);
        Frame(c, m, d, hotkeyX, hotkeyY);
        Frame(c, m, d, weaponX, handsY);
        if (!HasRoom(m, d)) return;

        int z = (short)m.ReadU16(ContentDepth);
        if (spell != Shortcuts.None) Icon(c, m, d, MagicIcon, SpellCrystals.Crystal(spell), magicX, handsY, z);
        if (weapon != Shortcuts.None) Icon(c, m, d, WeaponIcon, weapon, weaponX, handsY, z);
        if (hSpell != Shortcuts.None) Icon(c, m, d, HotkeyIcon, SpellCrystals.Crystal(hSpell), hotkeyX, hotkeyY, z);
        else if (hItem != Shortcuts.None) Icon(c, m, d, HotkeyIcon, hItem, hotkeyX, hotkeyY, z);
        if (!HasRoom(m, d)) return;

        if (hand.Away != Shortcuts.None || spell != Shortcuts.None) SlotNumber(m, magicX, handsY, hand.Out);
        if (right.Away != Shortcuts.None || weapon != Shortcuts.None) SlotNumber(m, weaponX, handsY, right.Out);
        if (spell != Shortcuts.None) Numeral(m, magicX, handsY, SpellCrystals.Level(m, spell));
        if (hSpell != Shortcuts.None) Numeral(m, hotkeyX, hotkeyY, SpellCrystals.Level(m, hSpell));
        else if (hItem != Shortcuts.None) Count(m, hotkeyX, hotkeyY, m.ReadU8(Hands.Inventory + hItem));
    }

    /// <summary>Room left in the frame's primitive buffer for everything the cross adds;
    /// also brings the menu's mirror of the cursor up to the frame's, which the model
    /// assembler moves alone.</summary>
    static bool HasRoom(IMemory m, uint d)
    {
        uint at = m.ReadU32(d + 8), end = m.ReadU32(d + 4);
        if (at == 0 || end <= at || end - at < Room) return false;
        m.WriteU32(MenuDraw.Cursor, at);
        return true;
    }

    static void Frame(CpuContext c, IMemory m, uint d, int x, int y)
    {
        if (!HasRoom(m, d)) return;
        CardFrame.Draw(c, m, x, y, CardWidth, CardHeight, BevelLess);
    }

    /// <summary>The inside of a card at (x, y): the frame less its bevel.</summary>
    static (int X, int Y, int W, int H) Inside(int x, int y)
    {
        var (bx, by) = CardFrame.Bevel;
        int ix = (int)Math.Ceiling(bx), iy = (int)Math.Ceiling(by);
        return (x + ix, y + iy, CardWidth - 2 * ix, CardHeight - 2 * iy);
    }

    /// <summary>The model of <paramref name="item"/> as large as fits across the inside,
    /// centred; nothing until the model has been read.</summary>
    static void Icon(CpuContext c, IMemory m, uint d, int slot, byte item, int x, int y, int z)
    {
        if (!HandsIcons.Ready(slot, item) || !HasRoom(m, d)) return;
        var (ix, iy, iw, ih) = Inside(x, y);
        HandsIcons.Draw(c, m, slot, ix + iw / 2, iy + ih / 2, Math.Min(iw, ih), z);
    }

    /// <summary>The number of the slot that is out, in the inside's top right corner.</summary>
    static void SlotNumber(IMemory m, int x, int y, int outSlot)
    {
        var (ix, iy, iw, _) = Inside(x, y);
        Digit(m, ix + iw - DigitWidth(m), iy, (byte)(outSlot + 1));
    }

    /// <summary>A count, right-aligned in the inside's bottom right corner.</summary>
    static void Count(IMemory m, int x, int y, int count)
    {
        var (ix, iy, iw, ih) = Inside(x, y);
        var digits = MenuDraw.FormatNumber(Math.Min(count, 99), count > 9 ? 2 : 1);
        int w = DigitWidth(m), h = (short)m.ReadU16(NumberTemplate + 0xA);
        int at = ix + iw - digits.Length * w;
        for (int i = 0; i < digits.Length; i++) Digit(m, at + i * w, iy + ih - h, digits[i]);
    }

    /// <summary>A spell's level as a Roman numeral in the inside's bottom left corner, in
    /// the menu's capitals at their own size, an I narrower than its cell.</summary>
    static void Numeral(IMemory m, int x, int y, int level)
    {
        const int NarrowAdvance = 4, WideAdvance = 8;
        var (ix, iy, _, ih) = Inside(x, y);
        int w = (short)m.ReadU16(TextTemplate + 8), h = (short)m.ReadU16(TextTemplate + 0xA);
        int uw = m.ReadU8(TextTemplate + 8), vh = m.ReadU8(TextTemplate + 0xA);
        int cx = ix, cy = iy + ih - h;
        foreach (char ch in SpellCrystals.Roman(level))
        {
            int g = ch - 'A';                                // the disc's capitals, cells 0-25
            MenuDraw.Quad(m, TextTemplate, cx, cy, w, h, (g & 15) * 8, (g >> 4) * 15, uw, vh, White, false, Slot);
            cx += ch == 'I' ? NarrowAdvance : WideAdvance;
        }
    }

    static int DigitWidth(IMemory m) => (short)m.ReadU16(NumberTemplate + 8);

    /// <summary>One digit of the menu's number font (its template's strip), at its own size.</summary>
    static void Digit(IMemory m, int x, int y, byte d)
    {
        int w = DigitWidth(m), h = (short)m.ReadU16(NumberTemplate + 0xA);
        int u = m.ReadU8(NumberTemplate + 4), uw = m.ReadU8(NumberTemplate + 8), vh = m.ReadU8(NumberTemplate + 0xA);
        int v;
        if (d < 11) v = d * 15;
        else { u += 7; v = (d - 11) * 15; }
        MenuDraw.Quad(m, NumberTemplate, x, y, w, h, u, v, uw, vh, TextGrey, false, Slot);
    }
}
