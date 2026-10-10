using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
// Upstream 0409bc2 emits one class per overlay, because CoreCLR caps a class
// at 65535 methods. Every func_ named here is GAME.EXE's, so the alias names the
// overlay once and the call sites below are unchanged.
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// The port's settings as a page of the game's own menu: PORT SETTINGS, in place
/// of RETURN, the eighth item of the in-game menu, opens it, drawn with the menu's
/// own boxes, font and sounds. A page of <see cref="PortSettings"/>' rows, Up/Down
/// for the row, Left/Right (or Cross) for the value, L1/R1 for the page, Circle to
/// leave; leaving with changes asks SAVE CHANGES, DISCARD CHANGES or RETURN.
/// Everything goes through <see cref="SettingsSession"/>, so nothing is written
/// unless the player changed a value and chose to save.
///
///     KF2_SETTINGSPAGE_PROBE=1   a line for each open, page, step, save and discard
///
/// RETURN is group 0's record 8, written in the game's list table each time the
/// menu draws it (the table comes back with GAME.EXE). Confirming the last item of
/// the menu's chooser is what closes the menu, so a post on the chooser runs the
/// page when it was that item confirmed, and writes the cancel word back to "not
/// cancelled" so the menu stays open. The game's dispatch never sees the item: the
/// chooser leaves the selection at none. The page's own records are built on the
/// guest stack, and the menu's cursor is left on the item.
/// </summary>
public static class SettingsPage
{
    // ---- The in-game menu func_80018E80 ----
    const uint Chooser = 0x8001EA14;              // func_8001EA14(cursor, last, &sel, &confirmed), &cancel at sp+0x10
    const uint MenuChooserReturn = 0x80018FFC;    // its call in the menu; the chooser has other callers
    const uint MenuSel = 0x18, MenuConfirmed = 0x1C, MenuCancel = 0x20;   // the menu's locals at its sp
    const uint NotCancelled = 0xFFFFFF9D;         // -99: the menu's "no cancel yet"
    const uint NoChoice = 0xFFFFFFFF;             // -1: the chooser's "nothing chosen"
    const uint ReturnItem = 7;                    // RETURN, which the page takes over

    // ---- The menu's list, group 0 ----
    const uint DrawList = 0x800208D8;             // func_800208D8(group, count, cursor, confirmed)
    const uint MenuListReturn1 = 0x80018EE4;      // the menu's two draws of the list
    const uint MenuListReturn2 = 0x8001906C;
    const uint MenuItems = 8;                     // USE ITEM .. RETURN
    const uint OurRecord = 0x80064DB4;            // group 0's record 8: X 31, Y 201, the string after
    const int OurX = 31, OurY = 201;
    const string OurLabel = "PORT SETTINGS";

    // ---- The action masks: the pad word ANDed with each, as the menu tests them ----
    const uint ConfirmMask = 0x8006E568;          // Cross
    const uint CancelMask = 0x8006E56C;           // Circle
    const uint StartMask = 0x8006E57C;            // Start, which also cancels in the chooser
    const uint UpMask = 0x8006E590, DownMask = 0x8006E594;
    const uint RightMask = 0x8006E598, LeftMask = 0x8006E59C;
    const uint R1Mask = 0x8006E580, L1Mask = 0x8006E588;
    const uint BlinkRestart = 0x8006E5D0;         // 0 restarts the cursor blink at the next draw

    // ---- Templates in GAME.EXE's data ----
    const uint BoxTemplate = 0x80064C20;          // a list item's box, 124 x 24, at the record's X - 6, Y - 6
    const int Inset = 6;                          // func_800218B4 draws a box that far up and left of its record
    const int GlyphW = 7;                         // a label cell, for the value column's extent
    const uint CursorSprite = 0x80064BFC;         // the blinking cursor of the selected item
    const uint LabelTemplate = 0x80064BF0;        // a label, drawn from its record's string

    // ---- Sounds (func_80022DC4) ----
    const uint SoundMove = 0x10, SoundChange = 0x11, SoundBack = 0x12;

    // ---- Layout: the menu's own places ----
    const int HeaderX = 17, HeaderY = 19;         // a list's header
    const int RowX = 31, FirstRowY = 45;          // the menu's items, under a header
    const int RowStep = 26;
    const int ValueX = 180;                       // OPTION's value column
    const int AskX = 98, AskY = 94, AskStep = 26; // the start menu's places (group 6)

    /// <summary>What fits the screen: seven rows under a header at the menu's
    /// spacing, from Y 45 to Y 201, the last box ending at Y 219, above the bottom
    /// of the screen.</summary>
    public const int MaxRows = 7;

    /// <summary>A label fits the 124-wide box: 16 cells of 7 pixels from X 31,
    /// ending at 143, inside the box's right edge at 149.</summary>
    public const int LabelChars = 16;

    /// <summary>A value from X 180 to the frame's right edge, less a margin.</summary>
    public const int ValueChars = 18;

    // A row that cannot change is drawn with its quads' grey darker
    // (func_800229D8 writes the grey into bytes +4..+6 of each quad).
    const uint DimGrey = 0x38;                    // the game's grey is 0x68
    const uint QuadSize = 0x28;

    // A frame of guest stack for the records and the chooser's words.
    const uint Frame = 0x100;
    const uint Record = 0x40;                     // one 0x1C-byte record, rebuilt before each draw
    const uint AskWords = 0x80;                   // sel, confirmed, cancel

    static readonly ModInfo _self = new()
    {
        Id = "kf2.settingspage",
        Name = "Settings page",
        Version = "1.0",
        Description = "The port's settings in the game's own menu, in place of RETURN.",
    };

    static bool _probe;
    static bool _open;
    static int _page;                             // kept between opens
    static readonly Dictionary<string, byte[]> _encoded = [];

    public static long Opened { get; private set; }

    public static void Configure(string? probe) => _probe = probe?.Trim() is "1" or "on" or "true";

    public static void Install() =>
        HookAttach.OnOverlayLoad("settings page", Attach,
            "PORT SETTINGS is missing from the in-game menu.");

    static MethodInfo M(string name) => typeof(SettingsPage).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    static bool Attach()
    {
        SymbolRegistry.Build();
        var chooser = SymbolRegistry.Resolve("game", null, Chooser);
        var list = SymbolRegistry.Resolve("game", null, DrawList);
        if (chooser == null || list == null) return false;
        // Both or none: an item drawn that cannot be chosen, or chosen and not drawn, is worse than no item.
        // The chooser's post runs after MenuMouse's (order 0), so the pointer's own work on the choice is done first.
        if (!HookManager.AddPre(_self, list, M(nameof(BeforeList)))
            || !HookManager.AddPost(_self, chooser, M(nameof(AfterChooser)), order: 100))
        {
            HookManager.RemoveMod(_self);
            return false;
        }
        HookManager.Commit();
        bool ok = HookAttach.Installed(chooser) && HookAttach.Installed(list);
        Console.WriteLine(ok ? "[KF2] settings page: PORT SETTINGS in place of RETURN"
                             : "[KF2] settings page: the menu hooks did not install");
        return ok;
    }

    /// <summary>The menu's list: RETURN's record is written as PORT SETTINGS each time
    /// the menu draws it, so the table itself is changed only for as long as the
    /// menu shows the item.</summary>
    public static bool BeforeList(CpuContext c, IMemory m)
    {
        if (c.A0 != 0u || c.A1 != MenuItems || (c.RA != MenuListReturn1 && c.RA != MenuListReturn2)) return true;
        Put(m, OurRecord, OurX, OurY, OurLabel);
        return true;
    }

    /// <summary>The menu's chooser has returned: no page is open. PORT SETTINGS
    /// confirmed (the chooser's last item, which sets the cancel word to -1) opens
    /// the page, and the cancel word goes back to "not cancelled" so the menu does
    /// not close. Posts run in ascending order, so this runs after MenuMouse's.</summary>
    public static void AfterChooser(CpuContext c, IMemory m)
    {
        if (_open || c.RA != MenuChooserReturn) return;
        if (c.V0 != ReturnItem || m.ReadU32(c.SP + MenuSel) != NoChoice
            || m.ReadU32(c.SP + MenuConfirmed) != 1u || m.ReadU32(c.SP + MenuCancel) != NoChoice) return;
        m.WriteU32(c.SP + MenuCancel, NotCancelled);
        m.WriteU32(c.SP + MenuConfirmed, 0u);   // the item is not pressed when the menu draws again
        Run(c, m);
    }

    static void Run(CpuContext c, IMemory m)
    {
        var saved = c.Snapshot();
        _open = true;
        Opened++;
        var session = SettingsSession.Open(PortSettings.All);
        try
        {
            c.RA = 0u;                            // not the menu's call, for the hook above
            c.SP -= Frame;
            KingsField2.func_80022EFC(c, m);      // every button up, as the game waits before a page
            if (_probe) Console.WriteLine($"[KF2] settings page: open on {PortSettings.Pages[_page]}");
            Loop(c, m, session);
            KingsField2.func_80022EFC(c, m);      // so the Circle that left does not close the menu
        }
        finally
        {
            // Never leave the session open, whatever happened.
            if (SettingsSession.Current == session) session.Discard();
            MenuMouse.Forget();                   // nothing the page drew is on screen now
            c.Restore(saved);
            _open = false;
        }
    }

    static void Loop(CpuContext c, IMemory m, SettingsSession session)
    {
        int row = 0;
        while (true)
        {
            var rows = PortSettings.OnPage(PortSettings.Pages[_page]).ToArray();
            KingsField2.func_80022E90(c, m);      // the repeat gate
            KingsField2.func_80022E58(c, m);      // the pad, in V0
            uint pad = c.V0;

            uint up = m.ReadU32(UpMask), down = m.ReadU32(DownMask);
            uint left = m.ReadU32(LeftMask), right = m.ReadU32(RightMask);
            uint l1 = m.ReadU32(L1Mask), r1 = m.ReadU32(R1Mask);

            if ((pad & up) != 0) { row = row == 0 ? rows.Length - 1 : row - 1; Moved(c, m); }
            else if ((pad & down) != 0) { row = row == rows.Length - 1 ? 0 : row + 1; Moved(c, m); }
            else if ((pad & (l1 | r1)) != 0)
            {
                int n = PortSettings.Pages.Length;
                _page = (_page + ((pad & r1) != 0 ? 1 : n - 1)) % n;
                row = 0;
                Moved(c, m);
                if (_probe) Console.WriteLine($"[KF2] settings page: page {PortSettings.Pages[_page]}");
            }
            else if ((pad & (m.ReadU32(ConfirmMask) | right | left)) != 0)
            {
                var s = rows[row];
                int dir = (pad & left) != 0 ? -1 : 1;
                if (session.Step(s, dir) is { } why)
                    Console.WriteLine($"[KF2] settings page: {s.Key} not changed: {why}");
                else
                {
                    Sound(c, m, SoundChange);
                    if (_probe) Console.WriteLine($"[KF2] settings page: {s.Key} -> {s.MenuValue(session.Shown(s))}" +
                                                  (session.IsChanged(s) ? "" : " (as it was)"));
                }
            }
            else if ((pad & (m.ReadU32(CancelMask) | m.ReadU32(StartMask))) != 0)
            {
                Sound(c, m, SoundBack);
                if (!session.Dirty)
                {
                    session.Discard();
                    if (_probe) Console.WriteLine("[KF2] settings page: left, nothing changed");
                    return;
                }
                switch (Ask(c, m))
                {
                    case 0:
                        int count = session.Changed.Count();
                        session.Save();
                        if (_probe) Console.WriteLine($"[KF2] settings page: saved {count} change(s)");
                        return;
                    case 1:
                        session.Discard();
                        if (_probe) Console.WriteLine("[KF2] settings page: discarded");
                        return;
                }
                // RETURN or Circle at the question: back to the page, the held button let go first.
                KingsField2.func_80022EFC(c, m);
            }

            for (int f = 0; f < 2; f++) DrawPage(c, m, session, rows, row);
        }
    }

    static void Moved(CpuContext c, IMemory m)
    {
        m.WriteU32(BlinkRestart, 0u);
        Sound(c, m, SoundMove);
    }

    /// <summary>SAVE CHANGES (0), DISCARD CHANGES (1) or RETURN, with the menu's own
    /// chooser; -1 when RETURN or Circle backs out.</summary>
    static int Ask(CpuContext c, IMemory m)
    {
        uint sel = c.SP + AskWords, confirmed = sel + 4u, cancel = sel + 8u;
        m.WriteU32(cancel, NotCancelled);
        uint cursor = 0;
        KingsField2.func_80022EFC(c, m);
        while (true)
        {
            m.WriteU32(c.SP + 0x10u, cancel);
            c.A0 = cursor; c.A1 = 2u; c.A2 = sel; c.A3 = confirmed;
            c.RA = 0u;                            // not the menu's call, for the hook above
            KingsField2.func_8001EA14(c, m);
            cursor = c.V0;
            if (m.ReadU32(confirmed) != 0) return (int)m.ReadU32(sel);
            if (m.ReadU32(cancel) != NotCancelled)
            {
                KingsField2.func_80022EFC(c, m);
                return -1;
            }
            for (int f = 0; f < 2; f++)
            {
                KingsField2.func_80022530(c, m);  // the frame head
                Item(c, m, AskX, AskY, "SAVE CHANGES", cursor == 0, usable: true);
                Item(c, m, AskX, AskY + AskStep, "DISCARD CHANGES", cursor == 1, usable: true);
                Item(c, m, AskX, AskY + 2 * AskStep, "RETURN", cursor == 2, usable: true);
                // The chooser above takes these as its rows, as it takes the drawer's.
                Pointed(m, AskX, AskY, AskStep, 3, (int)cursor, values: false, "settings question");
                KingsField2.func_800226A8(c, m);  // the presenter
            }
        }
    }

    static void DrawPage(CpuContext c, IMemory m, SettingsSession session, PortSetting[] rows, int row)
    {
        KingsField2.func_80022530(c, m);          // the frame head

        string header = $"{PortSettings.Pages[_page]} {_page + 1}/{PortSettings.Pages.Length}";
        Box(c, m, Put(m, c.SP + Record, HeaderX, HeaderY, header));
        Label(c, m, c.SP + Record, dim: false);

        for (int i = 0; i < rows.Length; i++)
        {
            var s = rows[i];
            int y = FirstRowY + i * RowStep;
            bool usable = s.LockedBy is null && s.IsUsable;
            Item(c, m, RowX, y, s.MenuLabel!, i == row, usable);

            string value = s.MenuValue(session.Shown(s));
            if (MenuFont.Check(value) is not null || value.Length > ValueChars) value = "?";
            Put(m, c.SP + Record, ValueX, y, value);
            Label(c, m, c.SP + Record, dim: !usable);
        }

        // The rows to the menu pointer: the page's loop keeps its cursor in a
        // register, so hover reaches it as Up/Down through the pad read and a
        // click on the selected row as Right. The header and the value column
        // are the page's too, so a click on them is not a click off it.
        var (w, h) = MenuMouse.BoxSize(m, BoxTemplate);
        int bottom = FirstRowY + Math.Max(rows.Length - 1, 0) * RowStep - Inset + h;
        Pointed(m, RowX, FirstRowY, RowStep, rows.Length, row, values: true, $"settings page {PortSettings.Pages[_page]}",
                (HeaderX - Inset, HeaderY - Inset, ValueX + ValueChars * GlyphW - (HeaderX - Inset), bottom - (HeaderY - Inset)));
        KingsField2.func_800226A8(c, m);          // the presenter
    }

    /// <summary>The rows just drawn, for the menu pointer (Verdite3's): the boxes
    /// <see cref="Box"/> draws, at X - 6, Y - 6 by the template's size. A click on the
    /// selected row of a page of <paramref name="values"/> steps it, as Right does.</summary>
    static void Pointed(IMemory m, int x, int y, int step, int count, int cursor, bool values, string what,
                        (int X, int Y, int W, int H)? area = null)
    {
        var (w, h) = MenuMouse.BoxSize(m, BoxTemplate);
        Span<(int, int, int, int)> rows = stackalloc (int, int, int, int)[count];
        for (int i = 0; i < count; i++) rows[i] = (x - Inset, y + i * step - Inset, w, h);
        MenuMouse.Rows(rows, cursor, values, what, area);
    }

    /// <summary>One list item as <c>func_800208D8</c> draws it: the box, then the
    /// blinking cursor on the selected one, then the label; one that cannot change
    /// has its label darker.</summary>
    static void Item(CpuContext c, IMemory m, int x, int y, string text, bool selected, bool usable)
    {
        uint rec = Put(m, c.SP + Record, x, y, text);
        Box(c, m, rec);
        if (selected)
        {
            c.A0 = CursorSprite; c.A1 = rec;
            KingsField2.func_80021A84(c, m);
        }
        Label(c, m, rec, dim: !usable);
    }

    static void Box(CpuContext c, IMemory m, uint rec)
    {
        c.A0 = BoxTemplate; c.A1 = rec;
        KingsField2.func_800218B4(c, m);
    }

    /// <summary>The label at <paramref name="rec"/>. A dimmed one has its quads
    /// rewritten to the darker grey: every primitive the draw adds is a 0x28-byte
    /// quad from the cursor, so the ones from the cursor before to the cursor after
    /// are the label's.</summary>
    static void Label(CpuContext c, IMemory m, uint rec, bool dim)
    {
        uint from = m.ReadU32(MenuDraw.Cursor);
        c.A0 = LabelTemplate; c.A1 = rec;
        KingsField2.func_80021E10(c, m);
        if (!dim) return;
        uint to = m.ReadU32(MenuDraw.Cursor);
        for (uint p = from; p < to; p += QuadSize)
        {
            m.WriteU8(p + 4, (byte)DimGrey);
            m.WriteU8(p + 5, (byte)DimGrey);
            m.WriteU8(p + 6, (byte)DimGrey);
        }
    }

    /// <summary>A list record, <c>s16 X, s16 Y</c> and the string ending 0xFF.</summary>
    static uint Put(IMemory m, uint at, int x, int y, string text)
    {
        if (!_encoded.TryGetValue(text, out var bytes)) _encoded[text] = bytes = MenuFont.Encode(text);
        m.WriteU16(at, (ushort)x);
        m.WriteU16(at + 2u, (ushort)y);
        for (int i = 0; i < bytes.Length; i++) m.WriteU8(at + 4u + (uint)i, bytes[i]);
        return at;
    }

    static void Sound(CpuContext c, IMemory m, uint sound)
    {
        c.A0 = sound;
        KingsField2.func_80022DC4(c, m);
    }
}
