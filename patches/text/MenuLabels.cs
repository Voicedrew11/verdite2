using System;
using System.Collections.Generic;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2.Text;

/// <summary>
/// Menu text with proportional spacing. The game's label routine,
/// func_80021E10(template, rec), draws a record's letters (s16 x, s16 y, then
/// font cells to 0xFF, at most 24) 7 pixels apart. This replaces it: the letters
/// are placed by the menu font's spacing (Spacing.cs), and each one is drawn by
/// the game's own routine as a one-letter record, so every packet is still the
/// game's. A label may run at most a few pixels (Slack) wider than the game's 7
/// a letter; one that would run wider still is drawn the game's way instead.
///
/// Leading spaces mean one of two things. In a record from GAME.EXE's label
/// table they right-align it, so it ends where its last cell's digit would. In a
/// record the drawing routine built on its own stack (" SLASH" and the rest of
/// the Attack and Defense lists) they are an indent, so it keeps its left edge
/// like any other label. The status page's Condition value ("     CURSE") is
/// recognised by its text instead (IsCondition), wherever the page keeps the
/// record (measured from play, its spaces were being taken as an indent): it ends flush with the numbers above it, Good or Paralysis alike,
/// translated or not. The record is put
/// back as it came before returning, so the hooks after this one (the
/// translation's) see the game's own bytes.
/// </summary>
static class MenuLabels
{
    public const uint DrawLabel = 0x80021E10;
    const int GameAdvance = 7, MaxCells = 24;
    const int Slack = 3;      // pixels a label may run past the game's 7 a letter, for a wide font
    const byte SpaceCell = 0x7F, End = 0xFF;
    const uint StackReach = 0x4000;   // how far above the stack pointer a caller's own record can be
    const uint ConditionSlots = 0x8006580C;   // GAME.EXE's PARALYZE .. GOOD, 24 bytes apart
    const uint SlotSize = 0x18, ConditionCount = 6;
    const int ConditionCells = 10;    // the US pads each to ten cells: "      GOOD"

    static char CharOf(byte cell)
    {
        if (cell == SpaceCell) return ' ';
        char c = MenuCells.Char(cell & 0x7F);
        return c == '\0' ? '?' : c;
    }

    /// <summary>The cell offset of each letter from the text's origin (where its
    /// first cell would be drawn), and the inked width from that origin.</summary>
    public static int[] Place(IList<byte> cells, out int width)
    {
        var sp = Fonts.Menu.Sp;
        var x = new int[cells.Count];
        if (!Fonts.PropMenus || cells.Count == 0)
        {
            for (int i = 0; i < cells.Count; i++) x[i] = i * GameAdvance;
            width = cells.Count * GameAdvance;
            return x;
        }
        var s = new char[cells.Count];
        for (int i = 0; i < cells.Count; i++) s[i] = CharOf(cells[i]);
        var text = new string(s);
        var ink = sp.Place(text, out int inkWidth);
        int first = 0;
        while (first < s.Length && s[first] == ' ') first++;
        int origin = first < s.Length ? sp.Left(s[first]) : 0;   // the first letter's ink stays where the game put it
        for (int i = 0; i < s.Length; i++)
            x[i] = ink[i] - (s[i] == ' ' ? 0 : sp.Left(s[i])) + origin;
        width = inkWidth + origin;
        return x;
    }

    /// <summary>Where a right-aligned label's ink ends: where a number's would in its
    /// last cell. The numbers (func_80021FCC) step 7 pixels from the record's x like
    /// the labels, so a right-aligned label and the numbers above it share their
    /// last cell; ending the label where the digits' ink ends lines the two up,
    /// whatever its last letter.</summary>
    static int ColumnRight(int x0, int cells) => x0 + (cells - 1) * GameAdvance + DigitRight();

    /// <summary>The digits' ink right edge in their 7-pixel strip cell.</summary>
    static int DigitRight()
    {
        var sp = Fonts.Menu.Sp;
        int r = 0;
        for (char d = '0'; d <= '9'; d++) r = Math.Max(r, Math.Min(sp.Left(d) + sp.Width(d), GameAdvance));
        return r > 0 ? r : GameAdvance;
    }

    /// <summary>The status page's Condition value: its text is one of GAME.EXE's six
    /// condition slots (PARALYZE, CURSE, SLOW, POISON, DARK, GOOD), as the game or a
    /// translation left them. The US text pads each to ten cells to right-align it
    /// in the game's fixed spacing; matching the slot rather than the padding puts
    /// a translation's word, padded or not, in the same column.</summary>
    static bool IsCondition(IMemory m, List<byte> cells)
    {
        for (uint k = 0; k < ConditionCount; k++)
        {
            uint slot = ConditionSlots + k * SlotSize;
            int i = 0;
            while (i < cells.Count && m.ReadU8(slot + (uint)i) == cells[i]) i++;
            if (i == cells.Count && m.ReadU8(slot + (uint)i) == End) return true;
        }
        return false;
    }

    /// <summary>For other mods: a string's cells and their offsets, and its width.</summary>
    public static (int[] Cells, int[] X, int Width) PlaceText(string text)
    {
        var enc = MenuCells.Encode(text, 64);
        var cells = new List<byte>(enc.Length);
        foreach (var b in enc) if (b != End) cells.Add(b);
        var x = Place(cells, out int w);
        var c = new int[cells.Count];
        for (int i = 0; i < c.Length; i++) c[i] = cells[i];
        return (c, x, w);
    }

    /// <summary>Replace on func_80021E10. A label still in the disc's capitals is
    /// drawn in its normal case (GameText) and put back after.</summary>
    public static void Draw(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        uint rec = c.A1;
        var cells = new List<byte>(MaxCells);
        for (uint i = 0; i < MaxCells; i++)
        {
            byte b = m.ReadU8(rec + 4 + i);
            if (b == End) break;
            cells.Add(b);
        }
        var cased = GameText.Cased(cells);
        if (cased == null) { DrawCells(orig, c, m, cells, cells); return; }
        for (int k = 0; k < cased.Count; k++) m.WriteU8(rec + 4 + (uint)k, cased[k]);
        try { DrawCells(orig, c, m, cased, cells); }
        finally { for (int k = 0; k < cells.Count; k++) m.WriteU8(rec + 4 + (uint)k, cells[k]); }
    }

    /// <summary>The label as <paramref name="cells"/> (what the record now holds),
    /// spaced proportionally; <paramref name="game"/> is the game's own text, which
    /// says whether it is the Condition value.</summary>
    static void DrawCells(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m, List<byte> cells, List<byte> game)
    {
        uint template = c.A0, rec = c.A1;
        int x0 = (short)m.ReadU16(rec);
        bool onStack = rec >= c.SP && rec - c.SP < StackReach;   // built by the caller: the spaces are an indent
        var letters = Arrange(m, cells, game, x0, onStack);
        if (letters == null) { orig(c, m); return; }

        int saved = 4 + cells.Count + 1;
        var keep = new byte[saved];
        for (int i = 0; i < saved; i++) keep[i] = m.ReadU8(rec + (uint)i);
        try
        {
            foreach (var (cell, x) in letters)
            {
                m.WriteU16(rec, (ushort)x);
                m.WriteU8(rec + 4, cell);
                m.WriteU8(rec + 5, End);
                c.A0 = template; c.A1 = rec;
                orig(c, m);
            }
        }
        finally
        {
            for (int i = 0; i < saved; i++) m.WriteU8(rec + (uint)i, keep[i]);
            c.A0 = template; c.A1 = rec;
        }
    }

    /// <summary>Where each visible letter of a label at <paramref name="x0"/> goes,
    /// spaced proportionally; null to draw it the game's way (a cell every 7 pixels,
    /// spaces included). <paramref name="onStack"/>: leading spaces are an indent
    /// rather than a right alignment.</summary>
    static List<(byte Cell, int X)>? Arrange(IMemory m, List<byte> cells, List<byte> game, int x0, bool onStack)
    {
        if (!Fonts.PropMenus || !MenuCells.Ready || !Fonts.Ready()) return null;
        if (cells.Count < 2) return null;

        int lead = 0;
        while (lead < cells.Count && cells[lead] == SpaceCell) lead++;
        var letters = new List<(byte, int)>(cells.Count);
        if (lead == cells.Count) return letters;                   // only spaces: nothing visible
        var body = cells.GetRange(lead, cells.Count - lead);
        var x = Place(body, out int width);
        if (width > body.Count * GameAdvance + Slack) return null; // at most a few pixels wider than the game's own
        int start = x0 + lead * GameAdvance;
        if (IsCondition(m, game))
            start = ColumnRight(x0, ConditionCells) - width;        // the Condition value: flush with the numbers above it
        else if (lead > 0 && !onStack)
            start = ColumnRight(x0, cells.Count) - width;           // right-aligned by its spaces: flush with its last cell
        for (int k = 0; k < body.Count; k++)
            if (body[k] != SpaceCell) letters.Add((body[k], start + x[k]));
        return letters;
    }

    /// <summary>
    /// For the port's own C# menu drawing (<see cref="Kf2.MenuDraw"/>, the gear
    /// comparison): each quad a label of the game's capitals at <paramref name="x0"/>
    /// draws, its cell and x, as this feature draws a label built on the caller's
    /// stack -- cased, spaced, or the game's way, a cell every 7 pixels -- so the two
    /// paths make the same packets. With the feature off, the game's way in capitals.
    /// </summary>
    public static List<(byte Cell, int X)> Lay(IMemory m, List<byte> gameCells, int x0)
    {
        var cells = gameCells;
        if (Lettering.Active && GameText.Cased(gameCells) is { } cased) cells = cased;
        if (Lettering.Active && Arrange(m, cells, gameCells, x0, onStack: true) is { } letters) return letters;
        var game = new List<(byte, int)>(cells.Count);
        for (int i = 0; i < cells.Count && i < MaxCells; i++) game.Add((cells[i], x0 + i * GameAdvance));
        return game;
    }
}
