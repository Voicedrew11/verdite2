using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2.Shortcuts;

/// <summary>
/// The hands and the current hotkey, bottom left, in the game's own pieces: three boxes
/// in a Souls cross, each a small copy of the menu's window border (its nine templates
/// at 0x80064C68 drawn at a third of their size), the left the magic hand, the right the
/// weapon hand, the one below them the hotkey Square uses. The weapon and an item hotkey
/// show their own model turning (<see cref="HandsIcons"/>); a spell, which has no model,
/// shows its name in small letters. Each hand's box has the number of the slot that is
/// out in its corner. Under the cross, the hotkey's name and count, or for two seconds
/// after a swap the name of what the hand took out.
///
/// Drawn after the HUD builder (func_80031D5C, which stage 13 and MenuWorld's passes both
/// call), only while the game draws its HUD (u8 0x801994DD), with the menu's text and
/// window templates, on ordering-table slot 1 where the game's own 2D HUD sprites go.
/// Follows the HP panel to the screen's edge when the HUD is anchored there. Kept left of
/// x 96 and above y 224, clear of the message strip at (96, 203).
///
/// Game thread.
/// </summary>
static class HandsHud
{
    const uint HudShown = 0x801994DD;                        // u8 1 while the HP panel draws
    const uint Descriptor = MenuDraw.ActiveDescriptor;
    const uint TextTemplate = MenuDraw.TextTemplate, NumberTemplate = MenuDraw.NumberTemplate;
    const uint WindowTemplates = MenuDraw.WindowTemplates;
    const int Slot = 1;
    const byte White = 0xFF, TextGrey = 0x80;
    const int MaxQuads = 160;
    const int ShowSwapMs = 2000;

    // The cross, in the game's 320 x 240.
    const int Box = 30, Gap = 2;
    const int LeftX = 6, Top = 156;
    const int RightX = LeftX + 2 * (Box + Gap), BottomX = LeftX + Box + Gap, BottomY = Top + Box / 2 + Gap + 4;
    const int NameY = BottomY + Box + 2, NameRight = 94;
    const double BorderScale = 0.3;

    /// <summary>The icon slots of <see cref="HandsIcons"/>.</summary>
    const int WeaponIcon = 0, HotkeyIcon = 1;

    /// <summary>Called from player control's pre-hook: reads the models the boxes will
    /// want, so the draw never touches the disc.</summary>
    internal static void Prepare(CpuContext c, IMemory m, bool probe)
    {
        HandsIcons.Want(c, m, WeaponIcon, m.ReadU8(Hands.Weapon), probe);
        var (spell, item) = Labels.Held(m, Shortcuts.Hotkey);
        HandsIcons.Want(c, m, HotkeyIcon, spell == Shortcuts.None ? item : Shortcuts.None, probe);
    }

    internal static void Draw(CpuContext c, IMemory m)
    {
        if (m.ReadU8(HudShown) != 1) return;
        uint d = m.ReadU32(Descriptor);
        uint at = m.ReadU32(d + 8), end = m.ReadU32(d + 4);
        if (at == 0 || end <= at || end - at < MaxQuads * MenuDraw.QuadBytes * 2) return;   // room for ours and the models'
        m.WriteU32(MenuDraw.Cursor, at);

        int x0 = Widescreen.AnchorHud && Widescreen.On ? -Widescreen.Margin : 0;
        int light = m.ReadU8(Stage13.HudRecords + Stage13.HudStride + 2);           // the HP panel's light
        int z = (short)m.ReadU16(Stage13.HudRecords + Stage13.HudStride + 0x14);

        var right = Hands.Right;
        var left = Hands.Left;
        byte weapon = m.ReadU8(Hands.Weapon), spell = m.ReadU8(Hands.AttackSpell);
        int hotkey = Shortcuts.Hotkey;
        var (hSpell, hItem) = Labels.Held(m, hotkey);

        // Text first: within a slot the last linked is drawn first, so the boxes go under.
        if (right.Away != Shortcuts.None || weapon != Shortcuts.None) SlotNumber(m, x0 + RightX, Top, right.Out);
        if (left.Away != Shortcuts.None || spell != Shortcuts.None) SlotNumber(m, x0 + LeftX, Top, left.Out);
        if (spell != Shortcuts.None) SmallName(m, x0 + LeftX, Top, Labels.Name(m, spell, Shortcuts.None));
        if (hSpell != Shortcuts.None) SmallName(m, x0 + BottomX, BottomY, Labels.Name(m, hSpell, Shortcuts.None));
        NameLine(m, x0, weapon, spell, hSpell, hItem);

        if (weapon != Shortcuts.None) Icon(c, m, WeaponIcon, weapon, x0 + RightX, Top, light, z);
        if (hSpell == Shortcuts.None && hItem != Shortcuts.None) Icon(c, m, HotkeyIcon, hItem, x0 + BottomX, BottomY, light, z);
        // The model assembler moves the frame's cursor and not the menu's mirror of it.
        m.WriteU32(MenuDraw.Cursor, m.ReadU32(d + 8));
        if (m.ReadU32(d + 4) - m.ReadU32(d + 8) < 9 * 3 * MenuDraw.QuadBytes) return;

        Border(m, x0 + LeftX, Top, Box, Box);
        Border(m, x0 + RightX, Top, Box, Box);
        Border(m, x0 + BottomX, BottomY, Box, Box);
    }

    static void Icon(CpuContext c, IMemory m, int slot, byte item, int x, int y, int light, int z)
    {
        if (HandsIcons.Ready(slot, item))
        {
            uint d = m.ReadU32(Descriptor);
            HandsIcons.Draw(c, m, slot, x + Box / 2, y + Box / 2, Box / 2 - 4, light, z);
            m.WriteU32(MenuDraw.Cursor, m.ReadU32(d + 8));
        }
        else SmallName(m, x, y, Labels.Name(m, Shortcuts.None, item));
    }

    /// <summary>The line under the cross: what a hand just took out, else the hotkey.</summary>
    static void NameLine(IMemory m, int x0, byte weapon, byte spell, byte hSpell, byte hItem)
    {
        byte[]? name;
        int count = -1;
        if (Hands.SwappedAt >= 0 && Environment.TickCount64 - Hands.SwappedAt < ShowSwapMs)
            name = Hands.SwappedWeapon ? Labels.Name(m, Shortcuts.None, weapon) : Labels.Name(m, spell, Shortcuts.None);
        else
        {
            name = Labels.Name(m, hSpell, hItem);
            if (hSpell == Shortcuts.None && hItem != Shortcuts.None) count = m.ReadU8(Hands.Inventory + hItem);
        }
        if (name == null) return;

        int limit = x0 + NameRight;
        if (count > 1)
        {
            var digits = MenuDraw.FormatNumber(count, 2);
            int dx = limit - digits.Length * 7;
            for (int i = 0; i < digits.Length; i++) Digit(m, dx + i * 7, NameY, digits[i], 1.0);
            limit = dx - 4;
        }
        Letters(m, x0 + LeftX, NameY, name, 1.0, limit, TextGrey);
    }

    /// <summary>The number of the slot that is out, small, in the box's top corner.</summary>
    static void SlotNumber(IMemory m, int x, int y, int outSlot) =>
        Digit(m, x + Box - 7, y + 2, (byte)(outSlot + 1), 0.5);

    /// <summary>A name in half-size letters inside a box, a word a line, three lines at most.</summary>
    static void SmallName(IMemory m, int x, int y, byte[]? cells)
    {
        if (cells == null) return;
        var words = new List<List<byte>> { new() };
        foreach (byte g in cells)
        {
            if (g == Labels.End) break;
            if (g == 0x7F) { if (words[^1].Count > 0) words.Add([]); continue; }
            words[^1].Add(g);
        }
        int lines = Math.Min(3, words.Count);
        int top = y + (Box - lines * 8) / 2;
        for (int i = 0; i < lines; i++)
            Letters(m, x + 3, top + i * 8, [.. words[i], Labels.End], 0.5, x + Box - 2, TextGrey);
    }

    /// <summary>Cells in the menu font at <paramref name="scale"/>, placed as the label
    /// routine places them (the lettering's spacing and case when it draws), stopping
    /// before <paramref name="limit"/>.</summary>
    static void Letters(IMemory m, int x, int y, byte[] cells, double scale, int limit, byte grey)
    {
        var list = new List<byte>(cells.Length);
        foreach (byte g in cells)
        {
            if (g == Labels.End) break;
            list.Add(g);
        }
        int w = (short)m.ReadU16(TextTemplate + 8), h = (short)m.ReadU16(TextTemplate + 0xA);
        int uw = m.ReadU8(TextTemplate + 8), vh = m.ReadU8(TextTemplate + 0xA);
        int sw = Math.Max(1, (int)Math.Round(w * scale)), sh = Math.Max(1, (int)Math.Round(h * scale));
        foreach (var (cell, X) in Text.MenuLabels.Lay(m, list, x))
        {
            int sx = x + (int)Math.Round((X - x) * scale);
            if (sx + sw > limit) break;
            int g = cell & 0x7F;
            MenuDraw.Quad(m, TextTemplate, sx, y, sw, sh, (g & 15) * 8, (g >> 4) * 15, uw, vh, grey, false, Slot);
        }
    }

    /// <summary>One digit of the menu's number font (its template's strip).</summary>
    static void Digit(IMemory m, int x, int y, byte d, double scale)
    {
        int w = (short)m.ReadU16(NumberTemplate + 8), h = (short)m.ReadU16(NumberTemplate + 0xA);
        int u = m.ReadU8(NumberTemplate + 4), uw = m.ReadU8(NumberTemplate + 8), vh = m.ReadU8(NumberTemplate + 0xA);
        int v;
        if (d < 11) v = d * 15;
        else { u += 7; v = (d - 11) * 15; }
        MenuDraw.Quad(m, NumberTemplate, x, y, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)),
                      u, v, uw, vh, TextGrey, false, Slot);
    }

    /// <summary>The menu's window border, its nine pieces drawn at <see cref="BorderScale"/>
    /// round a w x h box: the corners scaled, the edges and middle stretched between them.</summary>
    static void Border(IMemory m, int x, int y, int w, int h)
    {
        Span<int> cw = stackalloc int[3], ch = stackalloc int[3];
        for (int i = 0; i < 3; i += 2)
        {
            cw[i] = (int)Math.Round((short)m.ReadU16(Piece(0, i) + 8) * BorderScale);
            ch[i] = (int)Math.Round((short)m.ReadU16(Piece(i, 0) + 0xA) * BorderScale);
        }
        cw[1] = Math.Max(0, w - cw[0] - cw[2]);
        ch[1] = Math.Max(0, h - ch[0] - ch[2]);
        int Y = y;
        for (int r = 0; r < 3; r++)
        {
            int X = x;
            for (int col = 0; col < 3; col++)
            {
                uint t = Piece(r, col);
                if (cw[col] > 0 && ch[r] > 0)
                    MenuDraw.Quad(m, t, X, Y, cw[col], ch[r], m.ReadU8(t + 4), m.ReadU8(t + 6),
                                  m.ReadU8(t + 8), m.ReadU8(t + 0xA), White, true, Slot);
                X += cw[col];
            }
            Y += ch[r];
        }
    }

    static uint Piece(int row, int col) => WindowTemplates + (uint)((row * 3 + col) * 12);
}
