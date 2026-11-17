using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Shortcuts;

/// <summary>
/// "Which slot?": a list of the game's own kind (stepped by func_8001EB70, drawn by
/// func_800209E0, as the Encyclopedia's pages are), run as a modal loop inside one of the
/// game's setters, on a scratch frame below the caller's stack: which hotkey an item or
/// spell goes on, or which of a hand's two slots a weapon or attack spell goes in. Every
/// word is drawn with the game's label routine func_80021E10. Game thread.
/// </summary>
static class Picker
{
    /// <summary>What a list asks: its title, its rows' labels and what each row holds now.</summary>
    internal sealed class Question
    {
        public required Func<byte[]> Title;
        public required int Rows;
        public required Func<int, byte[]> Label;
        public required Func<IMemory, int, byte[]> Holds;
        /// <summary>The row the cursor opens on, remembered between asks.</summary>
        public int LastPick;
    }

    const uint Quantity = 0x8006E5C8;                        // u32 Left/Right counter func_8001EB70 steps
    const uint TextFont = 0x80064BF0;                        // func_80021E10's template
    const uint HeaderBox = 0x80064C20;
    const uint FakeRa = 0x8001FFF4;
    const uint Backed = 0xFFFFFFFF, Unset = 0xFFFFFF9D;
    const int ConfirmSound = 0x11;

    // The scratch frame.
    const uint Scratch = 0x800;
    const uint OffDesc = 0x100, OffRows = 0x200, OffCounts = 0x300, OffIds = 0x400;
    const uint OffWords = 0x500, OffFlags = 0x700, OffBox = 0x740, OffRec = 0x760;

    // The list, where the game's pages put theirs.
    const int HeaderX = 17, HeaderY = 19, ListLeft = 42, ListTop = 39, RowInset = 5, RowHeight = 14;
    const int NameColumn = 63;                               // nine cells of the game's 7
    const int BoxX = 147, BoxY = 19, BoxWidth = 124, BoxInset = 6;

    static uint _base;
    static Question _q = null!;

    /// <summary>-1 back, else the row picked.</summary>
    public static int Run(CpuContext c, IMemory m, Question q, byte spell, byte item)
    {
        _q = q;
        var snap = c.Snapshot();
        uint quantity = m.ReadU32(Quantity);
        try
        {
            c.SP -= Scratch;
            _base = (c.SP + 0xF) & ~0xFu;
            for (uint i = 0; i < 0x100; i++)
            {
                m.WriteU8(_base + OffRows + i, 0xFF);
                m.WriteU8(_base + OffCounts + i, 0xFF);
                m.WriteU8(_base + OffIds + i, 0xFF);   // func_80022CAC(0xFF): no preview, as on a spell row
            }
            for (uint i = 0; i < 0x200; i += 4) m.WriteU32(_base + OffWords + i, 0xFFFFFFFF);

            byte[] what = Labels.Name(m, spell, item) ?? Labels.Cells(m, Labels.RemoveLabel);
            uint d = _base + OffDesc, confirmed = _base + OffFlags, result = confirmed + 4;
            InitDesc(m, d, Math.Clamp(q.LastPick, 0, q.Rows - 1));
            m.WriteU32(confirmed, 0);
            m.WriteU32(result, Unset);

            Frames(c, m, d, what);
            Call(c, m, KingsField2.func_80022EFC);            // let go of the Yes

            while (true)
            {
                Call(c, m, KingsField2.func_8001EB70, d, _base + OffIds, confirmed, result);
                if (m.ReadU32(result) == Backed)               // the step played its sound
                {
                    Call(c, m, KingsField2.func_80022EFC);
                    return -1;
                }
                if (m.ReadU32(confirmed) != 0)
                {
                    int sel = m.ReadU8(d + 0x21);
                    q.LastPick = sel;
                    Call(c, m, KingsField2.func_80022DC4, ConfirmSound);
                    Call(c, m, KingsField2.func_80022EFC);
                    return sel;
                }
                Frames(c, m, d, what);
            }
        }
        finally
        {
            m.WriteU32(Quantity, quantity);
            c.Restore(snap);
        }
    }

    static void InitDesc(IMemory m, uint d, int sel)
    {
        for (uint i = 0; i < 0x40; i++) m.WriteU8(d + i, 0);
        m.WriteU16(d + 0x00, HeaderX);
        m.WriteU16(d + 0x02, HeaderY);
        Labels.WriteCells(m, d + 4, _q.Title());
        m.WriteU8(d + 0x1C, ListLeft);
        m.WriteU8(d + 0x1D, ListTop);
        m.WriteU8(d + 0x1E, (byte)_q.Rows);
        m.WriteU8(d + 0x1F, (byte)_q.Rows);
        m.WriteU8(d + 0x20, 0);
        m.WriteU8(d + 0x21, (byte)sel);
        m.WriteU8(d + 0x22, (byte)sel);
        m.WriteU8(d + 0x23, 1);                            // one 0xFF byte a row: the game draws no text
        m.WriteU32(d + 0x24, _base + OffRows);
        m.WriteU32(d + 0x28, _base + OffRows);
        m.WriteU32(d + 0x2C, _base + OffCounts);
        m.WriteU32(d + 0x30, _base + OffWords);
    }

    /// <summary>Two frames, as the game's pages draw (the two buffers).</summary>
    static void Frames(CpuContext c, IMemory m, uint d, byte[] what)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            Call(c, m, KingsField2.func_80022530);         // frame head
            Call(c, m, KingsField2.func_800209E0, d, 1);   // header, panel, highlight, cursor
            DrawBox(c, m, what);
            DrawRows(c, m, d);
            Call(c, m, KingsField2.func_800226A8);         // present
        }
    }

    static void DrawRows(CpuContext c, IMemory m, uint d)
    {
        int x = m.ReadU8(d + 0x1C) + RowInset, y = m.ReadU8(d + 0x1D) + RowInset;
        for (int r = 0; r < _q.Rows; r++)
        {
            Draw(c, m, x, y + RowHeight * r, _q.Label(r));
            Draw(c, m, x + NameColumn, y + RowHeight * r, _q.Holds(m, r));
        }
    }

    /// <summary>A second header box right of the title, with the pick's name centred in it.</summary>
    static void DrawBox(CpuContext c, IMemory m, byte[] cells)
    {
        uint rec = _base + OffBox;
        m.WriteU16(rec, BoxX);
        m.WriteU16(rec + 2, BoxY);
        m.WriteU8(rec + 4, Labels.End);
        Call(c, m, KingsField2.func_800218B4, HeaderBox, rec);
        int x = BoxX - BoxInset + Math.Max(0, (BoxWidth - Labels.Width(cells)) / 2);
        Draw(c, m, x, BoxY, cells);
    }

    /// <summary>One label through the game's own routine, so the lettering, a
    /// translation and the Japanese menus apply.</summary>
    static void Draw(CpuContext c, IMemory m, int x, int y, byte[] cells)
    {
        uint rec = _base + OffRec;
        m.WriteU16(rec, (ushort)x);
        m.WriteU16(rec + 2, (ushort)y);
        Labels.WriteCells(m, rec + 4, cells);
        Call(c, m, KingsField2.func_80021E10, TextFont, rec);
    }

    static void Call(CpuContext c, IMemory m, Action<CpuContext, IMemory> f,
                     uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        c.A0 = a0; c.A1 = a1; c.A2 = a2; c.A3 = a3;
        c.RA = FakeRa;
        f(c, m);
    }
}
