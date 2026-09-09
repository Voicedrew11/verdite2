using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
// Three namespaces in scope define a MouseButton or a Mouse; this alias picks
// the one meant here -- Silk's, which HostWindow.IsMouseButtonDown takes.
using HostMouseButton = Silk.NET.Input.MouseButton;
// Upstream 0409bc2 emits one class per overlay. Every func_ named here is
// GAME.EXE's, so the alias names the overlay once.
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// Hover the in-game menus with the mouse: point at an item and the game's own
/// cursor moves to it, left click confirms, right click backs out.
///
///     KF2_MENUMOUSE=0        off -- the menus are pad and keyboard only again
///     KF2_MENUMOUSE_PROBE=1  which list is live, the pointer's row, and what it did
///
/// The switch is a setting, on the Mouse tab of the port's Input pane, and it is
/// **independent of mouse look**: a player who never captures the pointer still
/// has a pointer, and pointing it at a menu is the one thing a mouse can do in
/// this game without being locked to the window first.
///
/// ## There are three menus, not one
///
/// The first version of this patch knew only the first of them, which is why it
/// worked on the tab menu and did nothing in any submenu. The game builds a menu
/// out of three unrelated widgets, each with its own cursor, its own geometry and
/// its own idea of where that cursor lives:
///
/// | widget | drawn by | stepped by | the cursor is |
/// |---|---|---|---|
/// | fixed option list | `func_800208D8` | `func_8001EA14` | the caller's register, returned in `V0` |
/// | scrolling list | `func_800209E0` | `func_8001EB70` | `u8[desc+0x21]` in memory |
/// | two-line prompt | `func_80021478` | inline in `func_800206E0` | that function's `S1`, and nowhere else |
///
/// So there are three mechanisms below rather than one generalisation, because
/// the three cursors are not the same kind of thing. What they share is the
/// pointer, the hit test and the "whichever device moved last owns the cursor"
/// rule.
///
/// ## The hit test is the rectangle the game drew
///
/// Not a band, and not one axis. Every one of the three widgets draws its rows as
/// quads whose corners this patch can compute from the same numbers the drawing
/// routine uses, so the test is `pointer in rect` on the actual item:
///
///  * **fixed list** -- `func_800218B4(template, record)` emits the quad
///    `(X-6, Y-6)` to `(X-6 + w-6, Y-6 + h-6)`, with `X`/`Y` out of the layout
///    table and `w`/`h` out of the *template* at `+0x8`/`+0xA` (124x24, so a
///    118x18 box six pixels up and left of the record's own position).
///  * **scrolling list** -- `func_800209E0` lays its rows out itself: row `r` is
///    `(u8[desc+0x1C], u8[desc+0x1D] + 5 + 14*r)`, 236x14, packed with no gap.
///  * **prompt** -- the same `func_800218B4`, with the 54x24 template at
///    `0x80064C08` and the two records `func_800206E0` builds.
///
/// The version this replaces synthesised a band per row instead: the smallest gap
/// between two rows, floored at the box height, tested against Y alone. That was
/// wrong in three ways at once. It was six pixels low, because the record's `Y`
/// is not the box's top edge. It ran a row's band into the 8px gutter the game
/// leaves between boxes, so a click in the gap selected the row above. And
/// testing Y alone made the whole width of the screen live, so a click far off to
/// the right of a 118-pixel box confirmed it.
///
/// **X is exact rather than avoided.** The old note reasoned that widescreen
/// moves X and Y is safe, and that is true of the *conversion* and not of the
/// test: the presented picture is `GameW + 2*margin` game pixels wide with the
/// game's own column 0 at `margin`, and <c>Display.WideMargin</c> is that number.
/// Subtract it and a game X is a game X at every aspect. See <see cref="Point"/>.
///
/// ## What each mechanism writes
///
/// **The fixed list** is driven by its stepper's **return value**: a post-hook on
/// `func_8001EA14` writes `V0`, and a click writes the stepper's own
/// out-parameters exactly as its Cross arm does -- including the arm's quirk that
/// confirming the last entry is a cancel. Nothing else, plus `0x8006E5D0`, the
/// blink direction, zeroed where the Up/Down arms zero it.
///
/// **The scrolling list** keeps its cursor in the descriptor its caller passes,
/// so there is a cursor in memory to write: `u8[desc+0x21]` (the absolute index)
/// and `u8[desc+0x22]` (its row on the page). Hover never scrolls -- it can only
/// reach a row that is on screen -- so `u8[desc+0x20]` is left alone and the two
/// stay consistent by construction. A move also replays the stepper's own move
/// arm: the blip, and `func_80022CAC(items[cursor])`, which is what loads the
/// item's preview model. Without that call the list moves and the picture beside
/// it does not.
///
/// **The prompt** has no cursor to write at all -- `func_800206E0` keeps it in
/// `S1` for the length of its own modal loop -- so it is the one place that goes
/// through the pad, and it can only do that safely because the loop *tells* this
/// patch its state every iteration: `func_80021478` is handed the flag as `a2`.
/// A post-hook on `func_80022E58` (the loop's `PadRead`) ORs in one synthetic Up
/// while the hovered row disagrees with the drawn flag, and a Cross once they
/// agree. Neither stepper edge-detects -- that is the finding
/// <see cref="MenuPacing"/> exists for -- so an injection that was not closed
/// over the state it changes would run away; this one cannot, because the next
/// iteration reads the flag it just produced and stops asking. It is scoped to
/// `func_800206E0` being on the stack, so no other menu sees an injected button.
///
/// ## Whichever device moved last owns the cursor
///
/// Hover only takes over once the pointer has **moved**, and hands back the
/// moment the pad or the keyboard moves the cursor itself. Without that a mouse
/// resting over the picture would pin the selection to whatever row it happens to
/// sit on, and the D-pad would look broken. <see cref="IdleMs"/> is how long a
/// move stays live.
///
/// ## The wheel owns the page, not the cursor
///
/// A list longer than its window could only be paged with the D-pad, so half an
/// inventory was unreachable with the mouse. The wheel is the gesture for that,
/// and the first version of this patch had one and took it out for a good
/// reason: it stepped the **cursor**, relative to where the cursor was, while
/// hover puts the cursor where the pointer is. Two rules for one byte, and they
/// contradict each other on the very next iteration of the menu loop.
///
/// The scrolling list has a second axis, and it is the way out. `+0x20` is the
/// entry drawn on row 0 -- the page -- and `+0x21` is the selection; hover has
/// never written the first of them, precisely because a pointer can only reach a
/// row that is on screen. So the wheel takes the page and hover keeps the
/// cursor. Scrolling under a still pointer changes which entries the rows show,
/// hover reads the row the pointer is on, and the answer is the same one either
/// order would have given. Nothing has to tie-break, because nothing is
/// contested.
///
/// It is the scrolling list's alone. A fixed list and a prompt draw every row
/// they have, so there is no page to move and a notch over one is spent rather
/// than saved -- see <see cref="TakeWheel"/>. The asymmetry is the finding
/// above, not a gap: on those two the cursor *is* the only axis, and the wheel
/// would be back to fighting the pointer for it.
///
/// The notch is **taken from the host, not listened for**
/// (`HostWindow.TakeMouseWheel`, patches/recompone/0038), which is the shape
/// `TakeMouseMotion` already had and which matters twice here. A drained
/// accumulator cannot miss a notch that arrived between two iterations of a
/// menu loop, where ImGui's per-frame `MouseWheel` would lose one whenever two
/// frames passed between steps and repeat one whenever none did. And it needs
/// no scope: **not every scrolling list is inside `func_80018E80`** -- the
/// save-slot menu is opened from the object-use handler and a shop from an NPC
/// -- so a listener hung on this class's own session would have covered the
/// inventory and quietly missed both.
///
/// **Never judged by eye**: whether the cursor lands on the item the pointer is
/// actually over, whether a desktop pointer over a 1996 menu reads acceptably,
/// whether the gutters between the fixed list's boxes -- 8 pixels in 26, and
/// now dead rather than assigned to a neighbour -- are felt when sweeping down a
/// list, and whether one notch a row is the right speed. No list measured so far
/// has been longer than its window, so the scroll itself has been exercised only
/// against the arithmetic; and a trackpad's fractional notch is read off Silk's
/// contract rather than measured, no such device having been used here.
///
/// **Not covered**: `func_8001BB7C` and `func_8001BE60` draw a fixed list and
/// then read the pad themselves rather than calling `func_8001EA14`, so they are
/// a fourth shape. See "The menu pointer" in docs/INPUT.md.
/// </summary>
public static class MenuMouse
{
    /// <summary>The in-game menu's modal loop. It blocks for the whole session,
    /// so a pre and a post on it are "opened" and "closed".</summary>
    const uint MenuLoop = 0x80018E80;

    // --- the fixed option list ----------------------------------------------

    /// <summary>The fixed option list's drawer: `(group, count, cursor,
    /// confirmed)`. Ten call sites, each beside the stepper in the same
    /// function.</summary>
    const uint OptionDraw = 0x800208D8;

    /// <summary>The fixed option list's cursor stepper:
    /// `(cursor, maxIndex, *selected, *confirmed, *cancelled) -> cursor`.</summary>
    const uint FixedCursor = 0x8001EA14;

    /// <summary>The layout table the drawer indexes, its per-group stride, and
    /// the per-record one. Record 0 of a group is the header; item `i` is record
    /// `i + 1`.</summary>
    const uint LayoutBase = 0x80064CD4;
    const uint GroupStride = 0x134;
    const uint RecordStride = 0x1C;

    /// <summary>`0x134 / 0x1C` is 11 records, one of which is the header.</summary>
    const int MaxFixedRows = 10;

    /// <summary>The sprite template an item box is drawn with. `func_800218B4`
    /// reads its `+0x8`/`+0xA` as the box's size and puts the box's top-left six
    /// pixels up and left of the record's own position, so the drawn rect is
    /// `(X - 6, Y - 6)` by `(w, h)` -- 124x24 here, which leaves a 2px gutter in
    /// a list whose records are 26 apart.</summary>
    const uint ItemTemplate = 0x80064C20;
    const int TemplateInset = 6;

    /// <summary>The blink's direction word. Zeroed on an accepted move in the
    /// fixed list, which is what restarts the wink; see <see cref="MenuPacing"/>,
    /// which holds the counter beside it. The scrolling list's stepper does not
    /// touch it, so neither does this.</summary>
    const uint BlinkDir = 0x8006E5D0;

    // --- the scrolling list --------------------------------------------------

    /// <summary>The scrolling list's cursor stepper:
    /// `(desc, items, *confirmed, *cancelled) -> padWord`. Sixteen call sites --
    /// inventory, magic, equipment, the shops, the save slots.</summary>
    const uint ScrollCursor = 0x8001EB70;

    /// <summary>What loads the preview model for the item under the cursor, and
    /// resets its rotation and zoom. The stepper calls it on every accepted move,
    /// so a hover move must too.</summary>
    const uint LoadPreview = 0x80022CAC;

    // The descriptor `func_8001EB70` and `func_800209E0` share. Both of them read
    // every one of these; none of it is inferred.
    const uint DescX = 0x1C;        // u8, the list's left edge
    const uint DescY = 0x1D;        // u8, the top of row 0, before the +5 inset
    const uint DescCount = 0x1E;    // u8, entries in the whole list
    const uint DescVisible = 0x1F;  // u8, rows drawn on one page
    const uint DescScroll = 0x20;   // u8, the entry drawn on row 0
    const uint DescCursor = 0x21;   // u8, the selected entry, absolute
    const uint DescRow = 0x22;      // u8, its row on the page: cursor - scroll

    /// <summary>Row 0's top is `DescY + 5` and each row is 14 tall, packed. Both
    /// are `func_800209E0`'s own literals -- the `0xE` its highlight loop adds per
    /// row, and the `+5` every one of its quad corners carries.</summary>
    const int RowInset = 5, RowPitch = 14;

    /// <summary>The row quad's width, out of the highlight sprite at
    /// `0x80064C44 + 0x8`. Unlike the fixed list's box this is not drawn through
    /// `func_800218B4`, so there is no six-pixel inset on it.</summary>
    const uint RowSprite = 0x80064C44;

    /// <summary>A page can show more rows than the fixed table has records, and
    /// this is the bound on a `u8` read out of a descriptor that could be
    /// anything if the hook ever fired somewhere unexpected.</summary>
    const int MaxScrollRows = 32;

    // --- the two-line prompt -------------------------------------------------

    /// <summary>The prompt's modal loop: `(desc, ?, listFlag, page) -> choice`.
    /// It keeps its cursor in a register, so this is a scope rather than a
    /// hook target.</summary>
    const uint PromptLoop = 0x800206E0;

    /// <summary>The prompt's drawer: `(rec0, rec1, flag, confirmed)`. Its third
    /// argument is the state the loop's register holds, which is the only way to
    /// read it.</summary>
    const uint PromptDraw = 0x80021478;

    /// <summary>The prompt's box template, 54x24 through `func_800218B4`.</summary>
    const uint PromptTemplate = 0x80064C08;

    /// <summary>The menu's `PadRead(1)`, called once per iteration of every menu
    /// loop. The prompt's injected button is ORed into its return value.</summary>
    const uint MenuPadRead = 0x80022E58;

    /// <summary>The pad masks, live out of the game's own control config: Up,
    /// Down, Cross and the first of the two cancel buttons the loops test.</summary>
    const uint MaskUp = 0x8006E590, MaskCross = 0x8006E568, MaskCancel = 0x8006E56C;

    /// <summary>The menu's own blips: move, confirm, cancel. The arguments
    /// `func_80022DC4` takes.</summary>
    const uint BlipMove = 0x10, BlipConfirm = 0x11, BlipCancel = 0x12;

    /// <summary>How long a pointer movement keeps the cursor. Long enough that
    /// reading an item and then clicking it is one gesture, short enough that a
    /// mouse left alone gives the pad the cursor back before the next menu.</summary>
    const long IdleMs = 2000;

    /// <summary>How many entries one notch of the wheel moves the page by. One,
    /// because a notch is the gesture's own unit and the pad's Down at the
    /// window's edge scrolls by exactly one: a wheel that moved the page faster
    /// than the D-pad can would be a different control rather than the same one
    /// on a different device.</summary>
    const int WheelRows = 1;

    /// <summary>How stale a fixed-list layout read may be and still be used. Only
    /// that list needs it: its geometry comes from a drawer call rather than from
    /// the stepper's own arguments, so a page change has to be able to invalidate
    /// it. The other two read their geometry live.</summary>
    const long GeomStaleMs = 500;

    /// <summary>A field rather than a property because the settings page and
    /// <c>Analog.Saved</c> both take it by reference, the way every other switch
    /// in this directory does.</summary>
    public static bool Enabled = true;

    public const string OnKey = "kf2.menumouse.on";

    static bool _probe;

    static readonly HashSet<string> _fromEnv = new(StringComparer.Ordinal);

    static readonly ModInfo _self = new()
    {
        Id = "kf2.menumouse",
        Name = "Menu pointer",
        Version = "2.0",
        Description = "Hover and click the in-game menus with the mouse.",
    };

    // --- the menu session ----------------------------------------------------

    /// <summary>Depth rather than a flag: nothing nests `func_80018E80` today,
    /// but a counter cannot latch the session open if one ever does.</summary>
    static int _depth;

    /// <summary>Whether this class took the pointer back on the way in, and so
    /// owes it on the way out. A pointer captured for mouse look is an unbounded
    /// virtual position -- there is no "where is it over the menu" while it is
    /// locked -- so the session releases it and the desktop cursor is what the
    /// player points with.</summary>
    static bool _tookCapture;

    // --- the fixed list's geometry -------------------------------------------

    static readonly int[] _fixX = new int[MaxFixedRows];
    static readonly int[] _fixY = new int[MaxFixedRows];
    static int _fixCount, _fixW, _fixH, _fixGroup = -1;
    static long _fixDrawnAt;

    // --- the pointer ---------------------------------------------------------

    static Vector2 _lastPos = new(float.NaN, float.NaN);
    static long _movedAt;

    /// <summary>Set when the game moved the cursor itself; cleared when the
    /// pointer next moves. This is the "whichever moved last owns it" latch.</summary>
    static bool _padOwns;

    static bool _leftWas, _rightWas;
    static bool _clickLeft, _clickRight;
    static bool _inPicture;

    /// <summary>How long a gap in stepper calls means the widget being stepped
    /// has only just opened, and any wheel held over from before it is not a
    /// request made of it. A live widget is stepped once per iteration of its
    /// own loop, measured at 30 a second, so this is seven of those.</summary>
    const long WidgetGapMs = 250;

    /// <summary>When a widget was last stepped, for that test.</summary>
    static long _steppedAt;

    /// <summary>Part of a notch left over from the last drain. The host is asked
    /// for the wheel rather than listened to, so nothing accumulates here except
    /// the fraction: <c>HostWindow.TakeMouseWheel</c> clears its own accumulator
    /// on every read, which is what makes a notch impossible to miss or to spend
    /// twice, and a trackpad's sub-notch scroll is carried here until enough of
    /// them add up to a row.</summary>
    static float _wheel;

    /// <summary>The pointer in the game's own pixels, valid while
    /// <see cref="_inPicture"/>.</summary>
    static float _gameX = float.NaN, _gameY = float.NaN;

    /// <summary>The row the pointer is over and the widget it belongs to, for the
    /// probe. Recomputed once per stepper call.</summary>
    static int _hover = -1;
    static string _live = "none";

    // The fixed stepper's arguments, stashed by the pre because the body
    // clobbers them.
    static bool _haveFixed;
    static int _fixCursorIn, _fixMaxIn;
    static uint _fixSelPtr, _fixConfirmPtr, _fixCancelPtr;

    // The scrolling stepper's.
    static bool _haveScroll;
    static uint _scDesc, _scItems, _scConfirmPtr, _scCancelPtr;
    static int _scCursorIn;

    // The prompt's.
    static int _promptDepth;
    static bool _promptSeen;
    static uint _promptRec0, _promptRec1;
    static int _promptFlag = -1, _promptAsked = -1;

    // --- the probe -----------------------------------------------------------

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _windowMs = -1.0;
    static int _samples, _hovers, _moves, _scrolls, _confirms, _cancels, _injects;
    static int _lastReported = -2;
    static uint _lastScDesc;
    static int _lastScCount = -1;

    public static void Configure(string? enabled, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(enabled))
        {
            Enabled = enabled != "0";
            _fromEnv.Add(OnKey);
        }
        if (!string.IsNullOrWhiteSpace(probe)) _probe = probe != "0";
    }

    /// <summary>
    /// Attach the hooks, deferred to the first overlay load for the reason
    /// <see cref="MenuPacing.Install"/> gives: <c>SymbolRegistry</c> reads the
    /// dispatcher's overlay tables, which are registered inside Entry.Run.
    ///
    /// Attached whether or not it is enabled, since the switch is a setting and
    /// hooks cannot be added once the game is past its overlay loads.
    /// </summary>
    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Analog.Saved(OnKey, ref Enabled, _fromEnv);
        });

        HookAttach.OnOverlayLoad("menu pointer", Attach,
                                 "The in-game menus stay pad and keyboard only.");
    }

    static bool _loopHooked, _drawHooked, _cursorHooked, _scrollHooked, _promptHooked;

    static bool Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(MenuMouse);
        MethodInfo Own(string name) => self.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

        MethodInfo? At(uint addr, string lost)
        {
            var mi = SymbolRegistry.Resolve("game", null, addr);
            if (mi == null)
                Console.Error.WriteLine($"[KF2] menu pointer: no game function at 0x{addr:X8} -- {lost}");
            return mi;
        }

        MethodInfo? loop = null, draw = null, cursor = null, scroll = null,
                    promptLoop = null, promptDraw = null, padRead = null;

        if (!_loopHooked)
        {
            loop = At(MenuLoop, "a captured pointer will not be given back inside the menu.");
            if (loop != null)
            {
                HookManager.AddPre(_self, loop, Own(nameof(BeforeMenu)));
                HookManager.AddPost(_self, loop, Own(nameof(AfterMenu)));
            }
        }

        if (!_drawHooked)
        {
            draw = At(OptionDraw, "no item positions, so the tab menu has nothing to point at.");
            if (draw != null) HookManager.AddPre(_self, draw, Own(nameof(BeforeOptionDraw)));
        }

        if (!_cursorHooked)
        {
            cursor = At(FixedCursor, "nothing but the pad can move the tab menu's cursor.");
            if (cursor != null)
            {
                HookManager.AddPre(_self, cursor, Own(nameof(BeforeCursor)));
                HookManager.AddPost(_self, cursor, Own(nameof(AfterCursor)));
            }
        }

        if (!_scrollHooked)
        {
            scroll = At(ScrollCursor, "the inventory, magic and equipment lists stay pad only.");
            if (scroll != null)
            {
                HookManager.AddPre(_self, scroll, Own(nameof(BeforeScroll)));
                HookManager.AddPost(_self, scroll, Own(nameof(AfterScroll)));
            }
        }

        if (!_promptHooked)
        {
            promptLoop = At(PromptLoop, "the yes/no prompt stays pad only.");
            promptDraw = At(PromptDraw, "the yes/no prompt's state cannot be read.");
            padRead = At(MenuPadRead, "the yes/no prompt has no way in.");
            if (promptLoop != null && promptDraw != null && padRead != null)
            {
                HookManager.AddPre(_self, promptLoop, Own(nameof(BeforePrompt)));
                HookManager.AddPost(_self, promptLoop, Own(nameof(AfterPrompt)));
                HookManager.AddPre(_self, promptDraw, Own(nameof(BeforePromptDraw)));
                HookManager.AddPost(_self, padRead, Own(nameof(AfterPadRead)));
            }
        }

        HookManager.Commit();

        // Read the detours back rather than the Add* returns, which only say the
        // delegate was queued. See patches/HookAttach.cs.
        _loopHooked |= HookAttach.Installed(loop);
        _drawHooked |= HookAttach.Installed(draw);
        _cursorHooked |= HookAttach.Installed(cursor);
        _scrollHooked |= HookAttach.Installed(scroll);
        _promptHooked |= HookAttach.Installed(promptLoop) && HookAttach.Installed(promptDraw) &&
                         HookAttach.Installed(padRead);

        Console.WriteLine($"[KF2] menu pointer: {(Enabled ? "on" : "off")}, " +
                          $"session {(_loopHooked ? "scoped" : "NOT scoped")}, " +
                          $"tab menu {(_drawHooked && _cursorHooked ? "driven" : "NOT driven")}, " +
                          $"lists {(_scrollHooked ? "driven" : "NOT driven")}, " +
                          $"prompt {(_promptHooked ? "driven" : "NOT driven")}");

        return _loopHooked && _drawHooked && _cursorHooked && _scrollHooked && _promptHooked;
    }

    // ------------------------------------------------------------------------
    // The session
    // ------------------------------------------------------------------------

    /// <summary>
    /// The menu is opening. Give the pointer back if mouse look had it: under
    /// `CursorMode.Raw` GLFW reports an unbounded virtual position, so there is
    /// no "over the picture" while it is locked, and there is no visible cursor
    /// to point with either. <see cref="Mouse.SetCaptured"/> announces both ends
    /// of that through the glyph in patches/MouseIndicator.cs.
    /// </summary>
    public static bool BeforeMenu(CpuContext c, IMemory m)
    {
        if (_depth++ > 0) return true;

        _fixCount = 0;
        _fixGroup = -1;
        _hover = -1;
        _live = "none";
        _padOwns = true;          // the pad opened the menu; it owns the cursor
        _clickLeft = _clickRight = false;
        _lastPos = new Vector2(float.NaN, float.NaN);
        _lastReported = -2;

        // Read the buttons once rather than leaving the previous session's state
        // to fire an edge on the first sample: opening the menu with Cross under
        // a held mouse button is not a click on anything.
        _leftWas = Down(HostMouseButton.Left);
        _rightWas = Down(HostMouseButton.Right);

        if (Enabled)
        {
            _tookCapture = Mouse.Captured;
            if (_tookCapture) Mouse.SetCaptured(false);
        }

        return true;
    }

    /// <summary>The menu closed. Give the pointer back to mouse look if it had
    /// it.</summary>
    public static void AfterMenu(CpuContext c, IMemory m)
    {
        if (--_depth > 0) return;
        _depth = 0;

        _fixCount = 0;
        _hover = -1;
        _live = "none";

        if (_tookCapture)
        {
            _tookCapture = false;
            if (Mouse.Enabled) Mouse.SetCaptured(true);
        }
    }

    // ------------------------------------------------------------------------
    // The fixed option list
    // ------------------------------------------------------------------------

    /// <summary>
    /// The option list is about to draw. Read the rows it is about to draw from
    /// the same table it reads them from, off the arguments it was called with --
    /// so a page this class has never heard of is measured correctly the first
    /// time it is drawn.
    /// </summary>
    public static bool BeforeOptionDraw(CpuContext c, IMemory m)
    {
        if (!Enabled) return true;

        int group = (int)c.A0;
        int count = (int)c.A1;

        // The group indexes a fixed-stride table with no bound of its own, so
        // this is the bound: a wild argument would otherwise read the rows out of
        // whatever follows the table.
        if (count <= 0 || count > MaxFixedRows || group < 0 || group > 31) return true;

        uint bas = LayoutBase + (uint)group * GroupStride;

        for (int i = 0; i < count; i++)
        {
            uint rec = bas + RecordStride * (uint)(i + 1);
            // The record's position is the sprite's, and func_800218B4 draws the
            // box six pixels up and left of it.
            _fixX[i] = (short)m.ReadU16(rec) - TemplateInset;
            _fixY[i] = (short)m.ReadU16(rec + 2) - TemplateInset;
        }

        _fixW = Math.Max(1, (int)m.ReadU16(ItemTemplate + 0x8));
        _fixH = Math.Max(1, (int)m.ReadU16(ItemTemplate + 0xA));
        _fixCount = count;
        _fixGroup = group;
        _fixDrawnAt = Environment.TickCount64;

        if (_probe && _lastReported != group)
        {
            _lastReported = group;
            var ys = string.Join(",", _fixY.Take(count));
            Console.WriteLine($"[KF2] menu pointer: fixed group {group} at 0x{bas:X8}, {count} rows, " +
                              $"x {_fixX[0]}, y {ys}, box {_fixW}x{_fixH}");
        }

        return true;
    }

    /// <summary>
    /// Stash the stepper's arguments. The body clobbers `A0`-`A3` and unwinds the
    /// stack, so the post cannot read them -- and the fifth argument is on the
    /// *caller's* stack at `SP+0x10`, which is only what `SP` points at before the
    /// prologue runs. Same o32 window patches/AreaWarp.cs writes through.
    /// </summary>
    public static bool BeforeCursor(CpuContext c, IMemory m)
    {
        if (!Enabled) return true;

        _fixCursorIn = (int)c.A0;
        _fixMaxIn = (int)c.A1;
        _fixSelPtr = c.A2;
        _fixConfirmPtr = c.A3;
        _fixCancelPtr = m.ReadU32(c.SP + 0x10u);
        _haveFixed = true;
        return true;
    }

    /// <summary>
    /// The stepper has run. Sample the pointer *here* rather than in the pre,
    /// because the body can spin for up to 100 ms inside `func_80022E90` and
    /// presents a frame on every one of those -- so the freshest pointer position
    /// is the one after it, not before.
    /// </summary>
    public static void AfterCursor(CpuContext c, IMemory m)
    {
        if (!_haveFixed) return;
        _haveFixed = false;
        if (!Enabled) return;

        uint cursor = c.V0;
        bool gameMoved = (int)cursor != _fixCursorIn;
        bool gameConfirmed = m.ReadU32(_fixConfirmPtr) != 0;

        // The pad or the keyboard just moved it, so it owns the cursor until the
        // pointer moves again. Read before Sample, which is what clears the latch.
        if (gameMoved) _padOwns = true;

        Sample();
        TakeWheel();          // nothing to scroll here; see TakeWheel
        _live = "fixed";
        int hover = _hover = HoverLive() ? HitFixed() : -1;

        if (gameMoved || gameConfirmed) { Report(); return; }

        // Everything that blips has to happen before V0 is written: the blip is a
        // real call into the recompiled routine and it clobbers V0 along with the
        // argument registers.
        int target = hover >= 0 && hover <= _fixMaxIn ? hover : (int)cursor;

        bool moved = target != (int)cursor;
        if (moved) Blip(c, m, BlipMove);

        if (_clickLeft)
        {
            _clickLeft = false;

            // Only a click *on a row*. A click over the picture but off the list
            // is a click on the menu's background, and the game has no action for
            // that -- confirming whatever happened to be selected would make a
            // misdirected click do something irreversible.
            if (hover >= 0)
            {
                // The stepper's Cross arm, reproduced exactly -- including that
                // confirming the *last* entry is a cancel rather than a select.
                Blip(c, m, BlipConfirm);
                m.WriteU32(_fixConfirmPtr, 1u);
                if (target < _fixMaxIn) m.WriteU32(_fixSelPtr, (uint)target);
                else if (_fixCancelPtr != 0) m.WriteU32(_fixCancelPtr, 0xFFFFFFFFu);
                _confirms++;
            }
        }
        else if (_clickRight)
        {
            _clickRight = false;
            if (_inPicture)
            {
                Blip(c, m, BlipCancel);
                if (_fixCancelPtr != 0) m.WriteU32(_fixCancelPtr, 0xFFFFFFFFu);
                _cancels++;
            }
        }

        if (moved)
        {
            // Where the Up/Down arms zero it, and for the same reason: the wink
            // is one ramp per accepted move, so a move that does not restart it
            // is a move with no cursor animation at all.
            m.WriteU32(BlinkDir, 0u);
            _moves++;
        }

        c.V0 = (uint)target;
        Report();
    }

    /// <summary>Which fixed-list box the pointer is inside, or -1. The rows are
    /// the boxes `func_800218B4` drew, with the 2px gutter between them left
    /// dead: a click in the gap is a click on nothing, not on the row above.</summary>
    static int HitFixed()
    {
        if (_fixCount == 0 || Environment.TickCount64 - _fixDrawnAt > GeomStaleMs) return -1;
        for (int i = 0; i < _fixCount; i++)
            if (In(_fixX[i], _fixY[i], _fixW, _fixH)) { _hovers++; return i; }
        return -1;
    }

    // ------------------------------------------------------------------------
    // The scrolling list
    // ------------------------------------------------------------------------

    /// <summary>
    /// `func_8001EB70(desc, items, *confirmed, *cancelled)`. Unlike the fixed
    /// list's stepper this one returns the pad word rather than the cursor: the
    /// cursor is three bytes in the descriptor, and that is what both this and
    /// the drawer read.
    /// </summary>
    public static bool BeforeScroll(CpuContext c, IMemory m)
    {
        if (!Enabled) return true;

        _scDesc = c.A0;
        _scItems = c.A1;
        _scConfirmPtr = c.A2;
        _scCancelPtr = c.A3;
        _scCursorIn = m.ReadU8(_scDesc + DescCursor);
        _haveScroll = true;
        return true;
    }

    public static void AfterScroll(CpuContext c, IMemory m)
    {
        if (!_haveScroll) return;
        _haveScroll = false;
        if (!Enabled) return;

        int cursor = m.ReadU8(_scDesc + DescCursor);
        int scroll = m.ReadU8(_scDesc + DescScroll);
        int count = m.ReadU8(_scDesc + DescCount);
        int visible = m.ReadU8(_scDesc + DescVisible);

        bool gameMoved = cursor != _scCursorIn;
        bool gameConfirmed = _scConfirmPtr != 0 && m.ReadU32(_scConfirmPtr) != 0;
        if (gameMoved) _padOwns = true;

        Sample();
        _live = "list";

        // The wheel moves the page, and the pointer still owns the cursor: the
        // two touch different bytes of the descriptor, which is the whole reason
        // this can exist where a wheel that stepped the cursor could not. Left
        // alone while the game moved the cursor itself this iteration -- the
        // notch is not discarded, so it is spent on the next one instead.
        int delta = gameMoved || gameConfirmed ? 0 : Page(m, TakeWheel(), ref scroll, count, visible);

        if (_probe && (_scDesc != _lastScDesc || count != _lastScCount))
        {
            _lastScDesc = _scDesc; _lastScCount = count;
            Console.WriteLine($"[KF2] menu pointer: list at 0x{_scDesc:X8}, {count} entries, " +
                              $"{visible} visible from {scroll}, cursor {cursor}/row {m.ReadU8(_scDesc + DescRow)}, " +
                              $"rows at x {m.ReadU8(_scDesc + DescX)} y {m.ReadU8(_scDesc + DescY) + RowInset} " +
                              $"{m.ReadU16(RowSprite + 0x8)}x{RowPitch}");
        }

        // Rows on screen, off the page the wheel may just have moved: hover can
        // only ever reach a row that is drawn, which is what keeps the cursor
        // inside the window without a rule of its own.
        int rows = Math.Min(Math.Min(visible, MaxScrollRows), count - scroll);
        int hover = _hover = HoverLive() ? HitScroll(m, rows) : -1;

        // The page is something this class writes now, so the per-second line
        // has to show it: a list whose window is shorter than its contents is
        // the whole reason the wheel exists, and it is invisible in a row index.
        if (_probe) _live = $"list, rows {scroll}-{scroll + Math.Max(rows, 1) - 1} of {count}";

        if (gameMoved || gameConfirmed) { Report(); return; }

        // Under the pointer if it is on a row; otherwise the entry the page
        // carried with it, which is the same row of the window it already was --
        // so the cursor never leaves the page, whichever of the two moved.
        int target = hover >= 0 ? scroll + hover : cursor + delta;
        if (target < 0 || target >= count) target = cursor;
        bool moved = target != cursor;

        // The stepper's own move arm, in its three parts: the blip, the two
        // cursor bytes, the preview load. All of it before the click below for
        // the reason the fixed list writes V0 last -- a call into the recompiled
        // routine clobbers the argument registers.
        if (moved) Blip(c, m, BlipMove);

        // Both bytes, for a page that moved under a cursor that did not, as well
        // as for a cursor that moved: `+0x22` is `+0x21` minus `+0x20`, so a
        // scroll alone changes it and leaving it stale draws the highlight on
        // the wrong row.
        if (moved || delta != 0)
        {
            m.WriteU8(_scDesc + DescCursor, (byte)target);
            m.WriteU8(_scDesc + DescRow, (byte)(target - scroll));
        }

        if (moved)
        {
            if (_scItems != 0) Preview(c, m, m.ReadU8(_scItems + (uint)target));
            _moves++;
        }

        if (_clickLeft)
        {
            _clickLeft = false;
            if (hover >= 0 && _scConfirmPtr != 0)
            {
                Blip(c, m, BlipConfirm);
                m.WriteU32(_scConfirmPtr, 1u);
                _confirms++;
            }
        }
        else if (_clickRight)
        {
            _clickRight = false;
            if (_inPicture && _scCancelPtr != 0)
            {
                Blip(c, m, BlipCancel);
                m.WriteU32(_scCancelPtr, 0xFFFFFFFFu);
                _cancels++;
            }
        }

        Report();
    }

    /// <summary>
    /// Spend whole notches on the page, and say how far it moved.
    ///
    /// **`+0x20` is the one byte of the descriptor hover never writes**, and
    /// that is what makes a wheel possible here at all. The note above records
    /// a wheel that was written and taken out, and it was right to take out:
    /// that one stepped the *cursor*, relative to where it was, while hover puts
    /// the cursor where the pointer is -- two rules for one byte, contradicting
    /// each other on the next iteration of the menu loop. The page is a second
    /// axis. Scrolling it under a still pointer changes which entries the rows
    /// show, hover then reads off the row the pointer is on, and the two agree
    /// by construction rather than by a tie-break.
    ///
    /// The clamp is the game's own window: `+0x20` may reach `count - visible`
    /// and no further, which is exactly where `func_8001EB70`'s own wrap-to-the-
    /// bottom arm puts it. It deliberately does **not** wrap, unlike that arm --
    /// a wheel is a continuous gesture and a list that jumped to the far end
    /// when it ran out would be unusable; the pad's Down still wraps, since the
    /// game's stepper is untouched.
    /// </summary>
    static int Page(IMemory m, int notches, ref int scroll, int count, int visible)
    {
        if (notches == 0 || count <= 0 || visible <= 0) return 0;

        int max = count - visible;
        if (max <= 0) return 0;   // the whole list is on screen; pointing reaches it all

        // Positive is away from the user, which is toward the top of the list.
        int want = Math.Clamp(scroll - notches * WheelRows, 0, max);
        int delta = want - scroll;
        if (delta == 0) return 0;

        m.WriteU8(_scDesc + DescScroll, (byte)want);
        scroll = want;
        _scrolls++;
        return delta;
    }

    /// <summary>Which visible row the pointer is over, or -1. `func_800209E0`
    /// lays the page out itself -- row 0 at `DescY + 5`, 14 apart, 236 wide from
    /// `DescX` -- so this is that arithmetic and not a measurement.</summary>
    static int HitScroll(IMemory m, int rows)
    {
        if (rows <= 0) return -1;
        int x = m.ReadU8(_scDesc + DescX);
        int y = m.ReadU8(_scDesc + DescY) + RowInset;
        int w = m.ReadU16(RowSprite + 0x8);
        if (w <= 0) return -1;

        for (int r = 0; r < rows; r++)
            if (In(x, y + RowPitch * r, w, RowPitch)) { _hovers++; return r; }
        return -1;
    }

    // ------------------------------------------------------------------------
    // The two-line prompt
    // ------------------------------------------------------------------------

    public static bool BeforePrompt(CpuContext c, IMemory m)
    {
        if (_promptDepth++ == 0)
        {
            _promptSeen = false;
            _promptFlag = _promptAsked = -1;
            // A click made on the list underneath does not carry into the prompt
            // the click opened.
            _clickLeft = _clickRight = false;
        }
        return true;
    }

    public static void AfterPrompt(CpuContext c, IMemory m)
    {
        if (--_promptDepth > 0) return;
        _promptDepth = 0;
        _promptSeen = false;
        _promptFlag = _promptAsked = -1;
        _live = "none";
    }

    /// <summary>
    /// `func_80021478(rec0, rec1, flag, confirmed)` -- the only reader of the
    /// prompt's cursor that is outside the register it lives in. Both records are
    /// on the loop's stack, so their addresses are taken here rather than assumed.
    /// </summary>
    public static bool BeforePromptDraw(CpuContext c, IMemory m)
    {
        if (_promptDepth <= 0) return true;

        int flag = (int)c.A2 != 0 ? 1 : 0;

        // The flag changed and it was not the injection asking: the pad moved it,
        // so the pad owns the cursor again.
        if (_promptSeen && flag != _promptFlag && flag != _promptAsked) _padOwns = true;

        if (_probe && !_promptSeen)
        {
            int pw = (int)m.ReadU16(PromptTemplate + 0x8);
            int ph = (int)m.ReadU16(PromptTemplate + 0xA);
            Console.WriteLine($"[KF2] menu pointer: prompt boxes " +
                              $"({(short)m.ReadU16(c.A0) - TemplateInset},{(short)m.ReadU16(c.A0 + 2) - TemplateInset}) and " +
                              $"({(short)m.ReadU16(c.A1) - TemplateInset},{(short)m.ReadU16(c.A1 + 2) - TemplateInset}), " +
                              $"{pw}x{ph}, flag {flag}");
        }

        _promptRec0 = c.A0;
        _promptRec1 = c.A1;
        _promptFlag = flag;
        _promptAsked = -1;
        _promptSeen = true;
        return true;
    }

    /// <summary>
    /// The menu's `PadRead(1)`, after it has run. This is the one place in the
    /// patch that goes through the pad, and it is scoped to the prompt's loop
    /// being on the stack -- every other menu is driven by writing its cursor
    /// directly, and would run away on a held synthetic button.
    ///
    /// It cannot run away here either, because the loop reports the state back
    /// through <see cref="BeforePromptDraw"/> on the same iteration: an Up is
    /// asked for only while the drawn flag disagrees with the hovered row, so the
    /// toggle that answers it also stops the asking.
    ///
    /// The latch at `0x8006E5C4` is `func_80022E58`'s own, set from the *real*
    /// pad word before this runs, so an injected button costs no repeat delay --
    /// exactly as a hover elsewhere costs none.
    /// </summary>
    public static void AfterPadRead(CpuContext c, IMemory m)
    {
        if (!Enabled || _promptDepth <= 0 || !_promptSeen) return;

        Sample();
        TakeWheel();          // nothing to scroll here either
        _live = "prompt";
        int hover = _hover = HoverLive() ? HitPrompt(m) : -1;

        uint add = 0;
        if (_clickRight)
        {
            _clickRight = false;
            if (_inPicture) add = m.ReadU32(MaskCancel);
        }
        else if (hover < 0)
        {
            // A click off both boxes is a click on nothing, and is spent rather
            // than left pending for wherever the pointer goes next.
            _clickLeft = false;
        }
        else if (hover != _promptFlag)
        {
            // One toggle. A click is deliberately left pending: the next
            // iteration draws the row the pointer is on and confirms it there.
            add = m.ReadU32(MaskUp);
            _promptAsked = hover;
        }
        else if (_clickLeft)
        {
            _clickLeft = false;
            add = m.ReadU32(MaskCross);
            _confirms++;
        }

        if (add != 0)
        {
            c.V0 |= add;
            _injects++;
        }

        Report();
    }

    /// <summary>Which of the prompt's two boxes the pointer is inside, or -1.
    /// Both are drawn through `func_800218B4` with the 54x24 template at
    /// `0x80064C08`, so they carry the same six-pixel origin inset the fixed
    /// list's boxes do. The records are on `func_800206E0`'s stack, so their
    /// addresses are taken from the drawer's arguments rather than assumed --
    /// which is also what makes this correct for a prompt drawn anywhere
    /// else.</summary>
    static int HitPrompt(IMemory m)
    {
        int w = (int)m.ReadU16(PromptTemplate + 0x8);
        int h = (int)m.ReadU16(PromptTemplate + 0xA);
        if (w <= 0 || h <= 0) return -1;

        for (int i = 0; i < 2; i++)
        {
            uint rec = i == 0 ? _promptRec0 : _promptRec1;
            if (rec == 0) continue;
            int x = (short)m.ReadU16(rec) - TemplateInset;
            int y = (short)m.ReadU16(rec + 2) - TemplateInset;
            if (In(x, y, w, h)) { _hovers++; return i; }
        }
        return -1;
    }

    // ------------------------------------------------------------------------
    // Calling back into the game
    // ------------------------------------------------------------------------

    /// <summary>
    /// The menu's own blip. A real call into the recompiled routine rather than a
    /// reimplementation, so the sound is the game's; it clobbers `V0`, `A0`-`A3`
    /// and `RA`, which is why every caller here writes `V0` afterwards and why the
    /// four are saved and put back.
    /// </summary>
    static void Blip(CpuContext c, IMemory m, uint which)
    {
        uint v0 = c.V0, a0 = c.A0, a1 = c.A1, a2 = c.A2, a3 = c.A3, ra = c.RA;
        c.A0 = which;
        KingsField2.func_80022DC4(c, m);
        (c.V0, c.A0, c.A1, c.A2, c.A3, c.RA) = (v0, a0, a1, a2, a3, ra);
    }

    /// <summary>
    /// `func_80022CAC(item)` -- what the scrolling stepper calls on every accepted
    /// move. It loads the item's preview model and resets that model's rotation
    /// and zoom, so a hover move that skipped it would leave the picture beside
    /// the list showing the item the cursor was on before. Same register
    /// discipline as <see cref="Blip"/>.
    /// </summary>
    static void Preview(CpuContext c, IMemory m, uint item)
    {
        uint v0 = c.V0, a0 = c.A0, a1 = c.A1, a2 = c.A2, a3 = c.A3, ra = c.RA;
        c.A0 = item;
        KingsField2.func_80022CAC(c, m);
        (c.V0, c.A0, c.A1, c.A2, c.A3, c.RA) = (v0, a0, a1, a2, a3, ra);
    }

    // ------------------------------------------------------------------------
    // The pointer
    // ------------------------------------------------------------------------

    /// <summary>
    /// Where the pointer is, whether it moved, and what is pressed.
    ///
    /// The position comes from ImGui's own IO rather than from a new host API:
    /// it is in the same screen space as <c>OutputView</c>, it is a plain field
    /// read so it costs nothing per call, and patches/MapPanel.cs already reads
    /// it. It carries the last presented frame's value, which is the right one --
    /// every menu loop presents through `func_800226A8`, and the panels draw
    /// inside that `VSync`.
    /// </summary>
    static void Sample()
    {
        _samples++;
        _hover = -1;
        _inPicture = false;
        _gameX = _gameY = float.NaN;

        // Before the first frame there is no context to read an IO out of.
        if (ImGui.GetCurrentContext() == IntPtr.Zero) return;

        // A popup is drawn over the game and wants the pointer for itself.
        if (PopupManager.AnyOpen) return;

        var pos = ImGui.GetIO().MousePos;
        if (pos != _lastPos && !float.IsNaN(pos.X))
        {
            if (!float.IsNaN(_lastPos.X)) { _movedAt = Environment.TickCount64; _padOwns = false; }
            _lastPos = pos;
        }

        bool left = Down(HostMouseButton.Left), right = Down(HostMouseButton.Right);
        if (left && !_leftWas) { _clickLeft = true; _movedAt = Environment.TickCount64; _padOwns = false; }
        if (right && !_rightWas) { _clickRight = true; _movedAt = Environment.TickCount64; _padOwns = false; }
        _leftWas = left;
        _rightWas = right;

        Point(pos);
    }

    /// <summary>
    /// The pointer in the game's own pixels, or nowhere.
    ///
    /// **OutputView directly, and deliberately not MapRender.Picture**: that
    /// helper falls back to the whole viewport when the panel drew no picture,
    /// which is right for something that has to be drawn somewhere and wrong for
    /// a coordinate conversion. A hit test would rather answer "nowhere" than
    /// answer confidently against a rectangle the game is not in.
    ///
    /// **X carries the widescreen margin.** The presented picture is
    /// `GameW + 2*margin` game pixels wide with the game's own column 0 sitting at
    /// `margin` -- that is what `GlCore.PresentDisplay` builds and what
    /// <c>Display.WideMargin</c> computes -- so the margin comes off after the
    /// scale and a game X is a game X at every aspect. `GameW` is the game's own
    /// width rather than the presented one (0029 says so explicitly), which is
    /// why the margin has to be added back here rather than read off the picture.
    /// </summary>
    static void Point(Vector2 pos)
    {
        if (!OutputView.Valid) return;
        var g0 = OutputView.Min;
        var size = OutputView.Size;
        int gameW = OutputView.GameW, gameH = OutputView.GameH;
        if (size.X < 32f || size.Y < 32f || gameW <= 0 || gameH <= 0) return;

        if (pos.X < g0.X || pos.X > OutputView.Max.X ||
            pos.Y < g0.Y || pos.Y > OutputView.Max.Y) return;

        int margin = Display.WideMargin(gameW);
        float picW = gameW + 2 * margin;

        // Set before the geometry is consulted: whether the pointer is over the
        // picture is a fact about the pointer, and a right click means "back" on
        // a screen with no list on it just as much as on one with.
        _inPicture = true;
        _gameX = (pos.X - g0.X) / size.X * picW - margin;
        _gameY = (pos.Y - g0.Y) / size.Y * gameH;

        // The rectangle and the scale come from two different places, so this is
        // the one test that catches them disagreeing rather than trusting the
        // pair. It caught exactly that: the size was first published off the
        // GL backend's *render target*, which the render-scale setting makes
        // three times the picture, and the probe read a pointer "in the picture
        // at game y 582" on a 240-line screen.
        if (_gameY < 0f || _gameY > gameH) { _inPicture = false; _gameX = _gameY = float.NaN; }
    }

    /// <summary>Is the pointer inside a rectangle in the game's own pixels? The
    /// rect is half-open, so two rows that touch share no pixel.</summary>
    static bool In(int x, int y, int w, int h) =>
        _inPicture && _gameX >= x && _gameX < x + w && _gameY >= y && _gameY < y + h;

    /// <summary>
    /// The whole notches scrolled since the last call, the remainder carried.
    ///
    /// **Every live widget takes them, and only the scrolling list can use
    /// them.** A notch spent over a fixed list or a prompt has nothing to move
    /// -- both draw every one of their rows -- and leaving it in the
    /// accumulator would fire it the moment a scrolling list opened, which is a
    /// list that jumps on the frame it appears.
    /// </summary>
    static int TakeWheel()
    {
        if (!HostWindow.MouseAvailable) return 0;

        long now = Environment.TickCount64;
        bool opening = now - _steppedAt > WidgetGapMs;
        _steppedAt = now;

        _wheel += HostWindow.TakeMouseWheel();

        // Nothing drains the host's accumulator while the game is being played,
        // where the wheel is bound to nothing, so the first step of a widget
        // throws away whatever was scrolled before it existed -- a list that
        // pages itself on the frame it opens is worse than one that ignores a
        // gesture made at the world.
        if (opening) { _wheel = 0f; return 0; }

        int notches = (int)_wheel;   // toward zero, so a part-notch is kept
        _wheel -= notches;

        // A notch is the pointer moving, so it takes the cursor the way a move
        // or a click does. Otherwise scrolling a list the pad owns would page
        // the view and leave the selection behind on a row it no longer names.
        if (notches != 0) { _movedAt = Environment.TickCount64; _padOwns = false; }
        return notches;
    }

    /// <summary>Whether hover is allowed to move anything: the pointer has moved
    /// recently and the pad has not moved the cursor since.</summary>
    static bool HoverLive() => !_padOwns && Environment.TickCount64 - _movedAt < IdleMs;

    static bool Down(HostMouseButton b) => HostWindow.MouseAvailable && HostWindow.IsMouseButtonDown(b);

    static void Report()
    {
        if (!_probe) return;

        double now = _clock.Elapsed.TotalMilliseconds;
        if (_windowMs < 0.0) { _windowMs = now; return; }

        double elapsed = now - _windowMs;
        if (elapsed < 1000.0) return;

        Console.WriteLine($"[KF2] menu pointer: {_samples * 1000.0 / elapsed:0.#} samples/s, " +
                          $"pointer ({_lastPos.X:0.},{_lastPos.Y:0.}) " +
                          $"{(_inPicture ? $"in the picture at game ({_gameX:0.#},{_gameY:0.#})" : "outside the picture")} " +
                          $"of {OutputView.GameW}x{OutputView.GameH} +{Display.WideMargin(OutputView.GameW)} " +
                          $"-> {_live} row {_hover}, " +
                          $"{(_padOwns ? "pad owns" : "pointer owns")}, " +
                          $"hovered {_hovers}, moved {_moves}, scrolled {_scrolls}, " +
                          $"confirmed {_confirms}, cancelled {_cancels}, injected {_injects}");

        _windowMs = now;
        _samples = _hovers = _moves = _scrolls = _confirms = _cancels = _injects = 0;
    }
}
