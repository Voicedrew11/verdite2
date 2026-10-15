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
/// ## One back-out, and the pad read spends it
///
/// Backing out is **not** one of the three mechanisms, and it used to be: each
/// widget's cancel out-parameter was written where that widget was hooked. Three
/// of the game's six menu-side pad readers were covered that way and three were
/// not -- `func_8001BB7C` and `func_8001BE60` draw a fixed list and then read the
/// pad themselves rather than calling `func_8001EA14`, and `func_8001B0D0` reads
/// it too -- so right click worked on some screens and did nothing on others,
/// which is what "sometimes you have to press the menu button to get out" was.
///
/// All six call `func_80022E58`, the menu's `PadRead(1)`. So the gesture asks for
/// one thing, a flag, and a post-hook on that read ORs the game's own cancel
/// mask into the word it returns. Whichever widget is reading gets it; its own
/// cancel arm blips and writes its own out-parameter; a screen with no cancel arm
/// ignores it, which is exactly what the pad's cancel button does there. The
/// gesture is the cancel button, on every screen, including the ones this patch
/// has never heard of.
///
/// It cannot run away the way a held synthetic Cross would (see
/// <see cref="MenuPacing"/> on the missing edge detection): the flag is spent on
/// the read that delivers it, and one menu-loop iteration is one pad read, so a
/// click backs out exactly one level.
///
/// **Two gestures ask for it.** Right click anywhere over the picture, and left
/// click clear of the widget's own boxes -- "off to the side of the menu". Clear
/// of them by <see cref="EdgeSlack"/> on every side rather than merely off a row,
/// so the gutter between two boxes stays dead and a sweep down a list cannot
/// close the menu on the way past. The bounds are the union of the boxes the
/// widget drew and nothing more: the frame around them, the item picture beside a
/// list and the description under it are drawn elsewhere, so a click on those is
/// a click off the menu as far as this can tell. Never judged by eye.
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
/// `S1` for the length of its own modal loop -- so its *move* also goes through
/// the pad, and it can only do that safely because the loop *tells* this patch
/// its state every iteration: `func_80021478` is handed the flag as `a2`. The
/// same post-hook on `func_80022E58` ORs in one synthetic Up while the hovered
/// row disagrees with the drawn flag, and a Cross once they agree, so the toggle
/// that answers the request also stops it being made. That part is scoped to
/// `func_800206E0` being on the stack; the back-out above is not, being every
/// screen's.
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
/// (`HostWindow.TakeMouseWheel`, tools/RecompOne/patches/0038), which is the shape
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
/// a fourth shape -- hover and confirm still do nothing on them. Backing out
/// does work there now, being the pad read's rather than a widget's, except on
/// `func_8001BE60`, which has no cancel arm for the pad either. See "The menu
/// pointer" in docs/INPUT.md.
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
    /// loop and by every one of the six routines that read the pad inside one --
    /// `func_8001B0D0`, `func_8001BB7C`, `func_8001BE60`, both steppers and the
    /// prompt's loop. It is therefore the definition of "a menu is reading the
    /// pad", which is what makes it the one place a back-out is spent.</summary>
    const uint MenuPadRead = 0x80022E58;

    /// <summary>The pad masks, live out of the game's own control config: Up,
    /// Down, Cross and the first of the two cancel buttons the loops test. Every
    /// routine above that has a cancel arm at all tests this one first.</summary>
    const uint MaskUp = 0x8006E590, MaskCross = 0x8006E568, MaskCancel = 0x8006E56C;

    /// <summary>Down and Right, for a page with a loop of its own: a push the
    /// other way, and a click on a page of values (the settings page steps a
    /// value on Right, as on Cross).</summary>
    const uint MaskDown = 0x8006E594, MaskRight = 0x8006E598;

    /// <summary>The menu's own blips: move and confirm. The arguments
    /// `func_80022DC4` takes. There is no cancel blip here any more -- backing
    /// out goes through the game's own cancel arm, which blips itself.</summary>
    const uint BlipMove = 0x10, BlipConfirm = 0x11;

    /// <summary>A gap in <see cref="MenuPadRead"/> calls this long means the menu
    /// that was reading the pad is not the one reading it now. A menu-loop
    /// iteration is 105-166 ms here -- `func_80022E90`'s six held vblanks are
    /// 100 ms of that -- so this is three of the longest of them, and it is the
    /// scope for the button edges and for a pending back-out.</summary>
    const long SessionGapMs = 500;

    /// <summary>How far outside a widget's own boxes a left click has to land
    /// before it reads as a click *off* the menu rather than one in the gutter
    /// between two rows. The fixed list's boxes are 26 apart and 24 tall, so
    /// anything under the 2px gutter would turn the gutter into a back-out.
    /// Never judged by eye.</summary>
    const int EdgeSlack = 8;

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

    /// <summary>Always on, and not a setting: the pointer takes the cursor only once
    /// the mouse moves, so a player without one never meets it (the user's call,
    /// 2026-10-13). <c>KF2_MENUMOUSE=0</c> is the comparison; the saved key is no
    /// longer read.</summary>
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

    // --- the fixed list's geometry -------------------------------------------

    /// <summary>The boxes last drawn, one rectangle each in game pixels: the
    /// drawer's, or a page's own through <see cref="Rows"/>.</summary>
    static readonly (int X, int Y, int W, int H)[] _fix = new (int, int, int, int)[MaxFixedRows];
    static int _fixCount, _fixGroup = -1;
    static long _fixDrawnAt;

    // --- a page with a loop of its own (Rows) --------------------------------

    /// <summary>Menu pad reads so far, and the read the page's rows were drawn
    /// after: a page's rows count only at the first read after they were drawn,
    /// so a page's leftovers never steer the next loop to read the pad.</summary>
    static long _reads, _pageDrawnAfter = -1;
    static int _pageCursor = -1;
    static bool _pageValues;
    static string _pageWhat = "none";

    /// <summary>The page's whole drawn area, when it is wider than its boxes:
    /// a click inside it that is on no box is a click on nothing rather than a
    /// back-out.</summary>
    static (int X, int Y, int W, int H)? _pageArea;

    /// <summary>Pushes through the pad that did not move the drawn cursor,
    /// and the cursor they pushed from; <see cref="MaxPushes"/> of them stop the
    /// pushing until the pointer moves.</summary>
    static int _pushes, _pushedFrom = -1;

    /// <summary>Verdite3's number: a page whose loop does not take a push is
    /// given up on after three, rather than pushed for ever.</summary>
    const int MaxPushes = 3;

    /// <summary>The shell's pointer, in game pixels, in place of the host's
    /// (<see cref="Shell"/>), and its pending clicks.</summary>
    static Vector2? _fake;
    static bool _fakeLeft, _fakeRight;

    // --- the pointer ---------------------------------------------------------

    static Vector2 _lastPos = new(float.NaN, float.NaN);
    static long _movedAt;

    /// <summary>Set when the game moved the cursor itself; cleared when the
    /// pointer next moves. This is the "whichever moved last owns it" latch.</summary>
    static bool _padOwns;

    static bool _leftWas, _rightWas;
    static bool _clickLeft, _clickRight;
    static bool _inPicture;

    /// <summary>A back-out the mouse has asked for and the next menu pad read
    /// has not spent yet. One flag rather than three cancel writes: see "One
    /// back-out, and the pad read spends it" in the class summary.</summary>
    static bool _backOut;

    /// <summary>Set for the rest of the iteration once a cancel has been ORed
    /// into a pad read, so the widget's own post-hook -- which runs after the
    /// read, inside the same stepper -- leaves the cursor where the game's
    /// cancel arm left it.</summary>
    static bool _cancelled;

    /// <summary>When a menu last read the pad, for <see cref="SessionGapMs"/>.
    /// </summary>
    static long _padReadAt;

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
    static string _lastRowsWhat = "";
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
        HookAttach.OnOverlayLoad("menu pointer", Attach,
                                 "The in-game menus stay pad and keyboard only.");
    }

    static bool _loopHooked, _drawHooked, _cursorHooked, _scrollHooked, _promptHooked, _padHooked;

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
            loop = At(MenuLoop, "a captured pointer is not released for the menu, and a click there captures it.");
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

        // The pad read stands on its own rather than riding the prompt's block,
        // because it is no longer the prompt's alone: it is where every screen's
        // back-out is spent, including the ones no widget hook covers.
        if (!_padHooked)
        {
            padRead = At(MenuPadRead, "nothing can back out of a menu with the mouse.");
            if (padRead != null) HookManager.AddPost(_self, padRead, Own(nameof(AfterPadRead)));
        }

        if (!_promptHooked)
        {
            promptLoop = At(PromptLoop, "the yes/no prompt stays pad only.");
            promptDraw = At(PromptDraw, "the yes/no prompt's state cannot be read.");
            if (promptLoop != null && promptDraw != null)
            {
                HookManager.AddPre(_self, promptLoop, Own(nameof(BeforePrompt)));
                HookManager.AddPost(_self, promptLoop, Own(nameof(AfterPrompt)));
                HookManager.AddPre(_self, promptDraw, Own(nameof(BeforePromptDraw)));
            }
        }

        HookManager.Commit();

        // Read the detours back rather than the Add* returns, which only say the
        // delegate was queued. See patches/HookAttach.cs.
        _loopHooked |= HookAttach.Installed(loop);
        _drawHooked |= HookAttach.Installed(draw);
        _cursorHooked |= HookAttach.Installed(cursor);
        _scrollHooked |= HookAttach.Installed(scroll);
        _padHooked |= HookAttach.Installed(padRead);
        _promptHooked |= HookAttach.Installed(promptLoop) && HookAttach.Installed(promptDraw);

        Console.WriteLine($"[KF2] menu pointer: {(Enabled ? "on" : "off")}, " +
                          $"session {(_loopHooked ? "scoped" : "NOT scoped")}, " +
                          $"tab menu {(_drawHooked && _cursorHooked ? "driven" : "NOT driven")}, " +
                          $"lists {(_scrollHooked ? "driven" : "NOT driven")}, " +
                          $"prompt {(_promptHooked ? "driven" : "NOT driven")}, " +
                          $"back out {(_padHooked ? "driven" : "NOT driven")}");

        return _loopHooked && _drawHooked && _cursorHooked && _scrollHooked && _promptHooked &&
               _padHooked;
    }

    // ------------------------------------------------------------------------
    // The session
    // ------------------------------------------------------------------------

    /// <summary>
    /// The menu is opening. Hold the pointer free for it with
    /// <see cref="Mouse.Suspend"/>: under `CursorMode.Raw` GLFW reports an
    /// unbounded virtual position, so there is no "over the picture" while it is
    /// locked, and there is no visible cursor to point with either. The suspension
    /// also keeps a click on the menu from capturing the pointer (a click on the
    /// picture captures everywhere else), and it is taken whether or not the menu
    /// pointer is on, since that click capture is Verdite Core's either way.
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
        _backOut = _cancelled = false;
        _lastPos = new Vector2(float.NaN, float.NaN);
        _lastReported = -2;
        _lastRowsWhat = "";

        // Read the buttons once rather than leaving the previous session's state
        // to fire an edge on the first sample: opening the menu with Cross under
        // a held mouse button is not a click on anything.
        _leftWas = Down(HostMouseButton.Left);
        _rightWas = Down(HostMouseButton.Right);

        Mouse.Suspend(true);

        return true;
    }

    /// <summary>The menu closed. Give the pointer back to mouse look if the
    /// menu took it (Verdite Core's <see cref="Mouse.Suspend"/> remembers).</summary>
    public static void AfterMenu(CpuContext c, IMemory m)
    {
        if (--_depth > 0) return;
        _depth = 0;

        _fixCount = 0;
        _hover = -1;
        _live = "none";

        Mouse.Suspend(false);
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
        var (w, h) = BoxSize(m, ItemTemplate);

        for (int i = 0; i < count; i++)
        {
            uint rec = bas + RecordStride * (uint)(i + 1);
            // The record's position is the sprite's, and func_800218B4 draws the
            // box six pixels up and left of it.
            _fix[i] = ((short)m.ReadU16(rec) - TemplateInset, (short)m.ReadU16(rec + 2) - TemplateInset, w, h);
        }

        _fixCount = count;
        _fixGroup = group;
        _fixDrawnAt = Environment.TickCount64;
        // The tab menu's list is the chooser's, not a page that reads the pad.
        _pageDrawnAfter = -1;
        _pageArea = null;

        if (_probe && _lastReported != group)
        {
            _lastReported = group;
            var ys = string.Join(",", _fix.Take(count).Select(r => r.Y));
            Console.WriteLine($"[KF2] menu pointer: fixed group {group} at 0x{bas:X8}, {count} rows, " +
                              $"x {_fix[0].X}, y {ys}, box {w}x{h}");
        }

        return true;
    }

    /// <summary>A template's drawn size: <c>func_800218B4</c> reads its
    /// <c>+0x8</c>/<c>+0xA</c> as the box's width and height, and the box sits
    /// <see cref="TemplateInset"/> up and left of the record.</summary>
    public static (int W, int H) BoxSize(IMemory m, uint template) =>
        (Math.Max(1, (int)m.ReadU16(template + 0x8)), Math.Max(1, (int)m.ReadU16(template + 0xA)));

    /// <summary>
    /// A page that draws its own boxes hands them over: the boxes in game pixels,
    /// the row drawn selected, and whether a click on the selected row steps a
    /// value (Right) rather than confirming it (Cross). <paramref name="area"/> is
    /// what the page draws beyond its boxes -- a value column, a header -- where a
    /// click is on nothing rather than off the page.
    ///
    /// The rows serve two drivers. A chooser (`func_8001EA14`) called after the
    /// draw takes them as the fixed list's, exactly as it takes the drawer's: that
    /// is the settings page's question. A loop of the page's own, which keeps its
    /// cursor in a register and reads the pad through `func_80022E58`, is driven
    /// at that read (<see cref="AfterPadRead"/>): one Up or Down a read toward the
    /// pointer, then Right or Cross for a click. Verdite3's <c>MenuMouse.Rows</c>.
    /// </summary>
    public static void Rows(ReadOnlySpan<(int X, int Y, int W, int H)> rows, int cursor, bool values, string what,
                            (int X, int Y, int W, int H)? area = null)
    {
        if (!Enabled) return;
        int n = Math.Min(rows.Length, MaxFixedRows);
        rows[..n].CopyTo(_fix);
        _fixCount = n;
        _fixGroup = -1;
        _fixDrawnAt = Environment.TickCount64;
        _pageDrawnAfter = _reads;
        _pageCursor = cursor;
        _pageValues = values;
        _pageWhat = what;
        _pageArea = area;

        if (_probe && _lastRowsWhat != what)
        {
            _lastRowsWhat = what;
            var ys = string.Join(",", _fix.Take(n).Select(r => r.Y));
            Console.WriteLine($"[KF2] menu pointer: {what}, {n} rows, x {(n > 0 ? _fix[0].X : 0)}, y {ys}, " +
                              $"box {(n > 0 ? _fix[0].W : 0)}x{(n > 0 ? _fix[0].H : 0)}, cursor {cursor}" +
                              (values ? ", values" : "") +
                              (area is { } a ? $", area ({a.X},{a.Y}) {a.W}x{a.H}" : ""));
        }
    }

    /// <summary>The page that handed its rows over has closed: nothing it drew is
    /// on screen, so nothing of it may be hit before the next widget draws.</summary>
    public static void Forget()
    {
        _fixCount = 0;
        _fixGroup = -1;
        _pageDrawnAfter = -1;
        _pageArea = null;
        _pushes = 0;
        _pushedFrom = -1;
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

        // The pad read inside this very stepper carried an injected cancel, so
        // the game's own cancel arm has already run: leave its answer alone.
        if (gameMoved || gameConfirmed || _cancelled) { Report(); return; }

        // Everything that blips has to happen before V0 is written: the blip is a
        // real call into the recompiled routine and it clobbers V0 along with the
        // argument registers.
        int target = hover >= 0 && hover <= _fixMaxIn ? hover : (int)cursor;

        bool moved = target != (int)cursor;
        if (moved) Blip(c, m, BlipMove);

        if (_clickLeft)
        {
            _clickLeft = false;

            if (hover >= 0)
            {
                // The stepper's Cross arm, reproduced exactly -- including that
                // confirming the *last* entry is a cancel rather than a select.
                Blip(c, m, BlipConfirm);
                m.WriteU32(_fixConfirmPtr, 1u);
                if (target < _fixMaxIn) m.WriteU32(_fixSelPtr, (uint)target);
                else if (_fixCancelPtr != 0) m.WriteU32(_fixCancelPtr, 0xFFFFFFFFu);
                _confirms++; _confirmsTotal++;
            }
            else if (Off(FixedBounds()))
            {
                // Clear of the whole list rather than merely off a row: the 2px
                // gutter between two boxes stays dead, so a sweep down the list
                // cannot close the menu on the way past.
                _backOut = true;
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
            if (In(_fix[i].X, _fix[i].Y, _fix[i].W, _fix[i].H)) { _hovers++; return i; }
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

        if (gameMoved || gameConfirmed || _cancelled) { Report(); return; }

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
                _confirms++; _confirmsTotal++;
            }
            else if (Off(ScrollBounds(m, rows))) _backOut = true;
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
            // the click opened, and neither does a back-out asked of it.
            _clickLeft = _clickRight = _backOut = false;
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
    /// patch that goes through the pad, and it now does two jobs.
    ///
    /// **It is where every back-out is spent.** `func_80022E58` is called by all
    /// six routines that read the pad inside a menu, so a cancel ORed into its
    /// return is delivered to whichever widget is reading -- the tab menu, either
    /// list, the prompt, and the two fixed lists that read the pad themselves
    /// rather than calling `func_8001EA14`. That is the whole reason it replaced
    /// three per-widget cancel writes: those covered three of the six, so the
    /// gesture worked on some screens and not others.
    ///
    /// **It is also the prompt's mechanism**, unchanged: an Up is asked for only
    /// while the drawn flag disagrees with the hovered row, so the toggle that
    /// answers it also stops the asking, and a Cross once they agree. Neither
    /// stepper edge-detects -- that is the finding <see cref="MenuPacing"/>
    /// exists for -- so an injection has to be closed over the state it changes;
    /// a back-out is, differently, because the latch is spent on the read that
    /// delivers it and one iteration is one pad read.
    ///
    /// The latch at `0x8006E5C4` is `func_80022E58`'s own, set from the *real*
    /// pad word before this runs, so an injected button costs no repeat delay --
    /// exactly as a hover elsewhere costs none.
    ///
    /// **The button edges are sampled here rather than only in the widget
    /// hooks**, which is what makes a click on a screen this patch has never
    /// heard of arrive at all: a press and a release that both happen while no
    /// hooked stepper runs used to be seen by nothing.
    /// </summary>
    public static void AfterPadRead(CpuContext c, IMemory m)
    {
        if (!Enabled) return;

        // A gap this long means the menu that was reading the pad is not the one
        // reading it now -- a submenu opened, or a whole session did. Read the
        // buttons once rather than leaving the last one's state to fire an edge,
        // and drop a back-out asked of a screen that has gone. This is the
        // session scope for every menu, including the ones outside
        // `func_80018E80`: the save-slot list off the object-use handler, a shop
        // off an NPC.
        long now = Environment.TickCount64;
        bool opening = now - _padReadAt > SessionGapMs;
        _padReadAt = now;
        _cancelled = false;

        // A page's rows count at the first read after they were drawn and at no
        // other: the read of the loop that drew them. Taken before the count
        // moves on, and whatever else this read turns out to be.
        bool pageFresh = _fixCount > 0 && _pageDrawnAfter == _reads;
        _reads++;

        if (opening)
        {
            _leftWas = Down(HostMouseButton.Left);
            _rightWas = Down(HostMouseButton.Right);
            _clickLeft = _clickRight = _backOut = false;
        }

        Sample();

        // Right click over the picture is the back-out gesture on every screen,
        // whether or not this patch knows what is drawn on it.
        if (_clickRight)
        {
            _clickRight = false;
            if (_inPicture) _backOut = true;
        }

        if (_backOut)
        {
            // The game's own cancel arm does the rest -- the blip, the out
            // parameter, and on a screen with no cancel arm nothing at all,
            // which is exactly what the pad's cancel button does there.
            _backOut = false;
            _cancelled = true;
            c.V0 |= m.ReadU32(MaskCancel);
            _cancels++; _cancelsTotal++;
            _injects++; _injectsTotal++;
            Report();
            return;
        }

        // A read inside a stepper or the prompt belongs to that widget's own
        // mechanism; a page's rows drawn just before this read are the page's.
        if (pageFresh && !_haveFixed && !_haveScroll && _promptDepth <= 0)
        {
            PageRead(c, m);
            Report();
            return;
        }

        if (_promptDepth <= 0 || !_promptSeen) { Report(); return; }

        TakeWheel();          // nothing to scroll here either
        _live = "prompt";
        int hover = _hover = HoverLive() ? HitPrompt(m) : -1;

        uint add = 0;
        if (hover < 0)
        {
            // Off both boxes: clear of them is a back-out on the next read, and
            // in the gap between them is a click on nothing. Either way it is
            // spent rather than left pending for wherever the pointer goes next.
            if (_clickLeft && Off(PromptBounds(m))) _backOut = true;
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
            _confirms++; _confirmsTotal++;
        }

        if (add != 0)
        {
            c.V0 |= add;
            _injects++; _injectsTotal++;
        }

        Report();
    }

    /// <summary>
    /// A page with a loop of its own has read the pad: the rows it handed over
    /// through <see cref="Rows"/> were drawn just before, with its cursor, so
    /// this is the prompt's mechanism on any number of rows. One Up or Down a
    /// read while the hovered row and the drawn cursor disagree -- closed over
    /// the state it changes, since the next draw reports the cursor the push
    /// produced -- then, for a click once they agree, Right on a page of values
    /// or Cross on any other. A click on a row the cursor is not on is left
    /// pending until the cursor arrives, so pointing and clicking is one
    /// gesture. <see cref="MaxPushes"/> pushes that do not move the drawn cursor
    /// stop the pushing until the pointer moves.
    ///
    /// The pad wins a read it is pressing anything on: a real Up or Down hands it
    /// the cursor, and nothing is ORed over a button the player is holding.
    /// </summary>
    static void PageRead(CpuContext c, IMemory m)
    {
        uint up = m.ReadU32(MaskUp), down = m.ReadU32(MaskDown);
        uint real = c.V0;
        if ((real & (up | down)) != 0) _padOwns = true;

        TakeWheel();          // a page draws every row it has: nothing to scroll
        _live = $"page {_pageWhat}";
        if (_pageCursor != _pushedFrom) _pushes = 0;   // the last push moved it
        int hover = _hover = HoverLive() ? HitFixed() : -1;
        if (real != 0) return;

        uint add = 0;
        if (hover < 0)
        {
            // As in the prompt: clear of the page is a back-out on the next read,
            // in a gutter or on the page's value column a click on nothing.
            if (_clickLeft && Off(FixedBounds())) _backOut = true;
            _clickLeft = false;
        }
        else if (hover != _pageCursor)
        {
            if (_pushes < MaxPushes)
            {
                add = hover > _pageCursor ? down : up;
                _pushedFrom = _pageCursor;
                _pushes++;
                _moves++;
            }
            else _clickLeft = false;
        }
        else if (_clickLeft)
        {
            _clickLeft = false;
            add = _pageValues ? m.ReadU32(MaskRight) : m.ReadU32(MaskCross);
            _confirms++; _confirmsTotal++;
        }

        if (add != 0)
        {
            c.V0 |= add;
            _injects++; _injectsTotal++;
        }
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
    /// read so it costs nothing per call, and mods/map/MapPanel.cs already reads
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

        // The shell's pointer, already in game pixels: the conversion is skipped,
        // and with it the panels' cover and the host's buttons.
        if (_fake is { } fake)
        {
            if (fake != _lastPos) { _movedAt = Environment.TickCount64; _padOwns = false; _lastPos = fake; _pushes = 0; }
            if (_fakeLeft) { _clickLeft = true; _fakeLeft = false; _movedAt = Environment.TickCount64; _padOwns = false; }
            if (_fakeRight) { _clickRight = true; _fakeRight = false; }
            _inPicture = true;
            (_gameX, _gameY) = (fake.X, fake.Y);
            return;
        }

        // Before the first frame there is no context to read an IO out of.
        if (ImGui.GetCurrentContext() == IntPtr.Zero) return;

        // A popup is drawn over the game and wants the pointer for itself.
        if (PopupManager.AnyOpen) return;

        var pos = ImGui.GetIO().MousePos;
        if (pos != _lastPos && !float.IsNaN(pos.X))
        {
            if (!float.IsNaN(_lastPos.X)) { _movedAt = Environment.TickCount64; _padOwns = false; _pushes = 0; }
            _lastPos = pos;
        }

        bool left = Down(HostMouseButton.Left), right = Down(HostMouseButton.Right);
        bool press = left && !_leftWas;
        if (right && !_rightWas) { _clickRight = true; _movedAt = Environment.TickCount64; _padOwns = false; }
        _leftWas = left;
        _rightWas = right;

        Point(pos);

        // A press on the item preview is a drag of it (ItemTurn), not a click: off
        // the list's rows it would otherwise be a back-out.
        if (press && !ItemTurn.Grab(_gameX, _gameY))
        {
            _clickLeft = true;
            _movedAt = Environment.TickCount64;
            _padOwns = false;
        }
    }

    /// <summary>
    /// The pointer in the game's own pixels, or nowhere.
    ///
    /// **OutputView directly, and deliberately not GamePicture.Get**: that
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
        // A panel floating in front of the picture has the pointer: its clicks
        // are not the menu's, which would confirm a row or back out under it.
        if (OutputView.Covered) return;

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
    /// Is the pointer clear of a widget by <see cref="EdgeSlack"/> on every side?
    ///
    /// This is deliberately not `!In(...)`. A widget's bounds are the union of
    /// the boxes it drew and nothing else -- the frame around them, the item
    /// picture beside a list and the description under it are drawn elsewhere and
    /// this patch does not know where -- so the test has to answer three ways
    /// rather than two: on a row (confirm), near the rows (nothing, which keeps
    /// the gutters dead), clear of them (back out). A `null` rect is a widget
    /// with no geometry, and nothing is clear of a widget that was never drawn.
    /// </summary>
    static bool Off((int X, int Y, int W, int H)? r) =>
        _inPicture && r is { } b &&
        (_gameX < b.X - EdgeSlack || _gameX >= b.X + b.W + EdgeSlack ||
         _gameY < b.Y - EdgeSlack || _gameY >= b.Y + b.H + EdgeSlack);

    /// <summary>The fixed list's boxes, as one rectangle. The rows are not laid
    /// out on a grid -- the layout table gives each its own X and Y -- so this is
    /// their union rather than the first one's column.</summary>
    static (int, int, int, int)? FixedBounds()
    {
        if (_fixCount == 0 || Environment.TickCount64 - _fixDrawnAt > GeomStaleMs) return null;

        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (int i = 0; i < _fixCount; i++)
        {
            var r = _fix[i];
            x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
            x1 = Math.Max(x1, r.X + r.W); y1 = Math.Max(y1, r.Y + r.H);
        }
        // A page's value column and header are its own: on no box, but not off it.
        if (_pageArea is { } a)
        {
            x0 = Math.Min(x0, a.X); y0 = Math.Min(y0, a.Y);
            x1 = Math.Max(x1, a.X + a.W); y1 = Math.Max(y1, a.Y + a.H);
        }
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>The scrolling list's page, as one rectangle: the rows are packed,
    /// so this is <see cref="HitScroll"/>'s arithmetic with the loop taken
    /// out.</summary>
    static (int, int, int, int)? ScrollBounds(IMemory m, int rows)
    {
        if (rows <= 0) return null;
        int w = m.ReadU16(RowSprite + 0x8);
        if (w <= 0) return null;
        return (m.ReadU8(_scDesc + DescX), m.ReadU8(_scDesc + DescY) + RowInset,
                w, RowPitch * rows);
    }

    /// <summary>The prompt's two boxes, as one rectangle.</summary>
    static (int, int, int, int)? PromptBounds(IMemory m)
    {
        int w = (int)m.ReadU16(PromptTemplate + 0x8);
        int h = (int)m.ReadU16(PromptTemplate + 0xA);
        if (w <= 0 || h <= 0 || (_promptRec0 == 0 && _promptRec1 == 0)) return null;

        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (int i = 0; i < 2; i++)
        {
            uint rec = i == 0 ? _promptRec0 : _promptRec1;
            if (rec == 0) continue;
            int x = (short)m.ReadU16(rec) - TemplateInset;
            int y = (short)m.ReadU16(rec + 2) - TemplateInset;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x + w); y1 = Math.Max(y1, y + h);
        }
        return (x0, y0, x1 - x0, y1 - y0);
    }

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
    // Not while the item preview is dragged, nor after until the pointer moves
    // again: a drag across the list would pick each row it passed.
    static bool HoverLive() => !_padOwns && !ItemTurn.Dragging && _movedAt > ItemTurn.ReleasedAt
                               && Environment.TickCount64 - _movedAt < IdleMs;

    static bool Down(HostMouseButton b) => HostWindow.MouseAvailable && HostWindow.IsMouseButtonDown(b);

    /// <summary>
    /// The shell's <c>point</c> (Verdite3's): a pointer in game pixels in place
    /// of the host's, so a script can measure hover, a click and a back-out
    /// without a person. <c>point &lt;x&gt; &lt;y&gt;</c>, <c>point left</c>,
    /// <c>point right</c>, <c>point off</c> (the host's pointer again); alone,
    /// what the last sample saw. It skips the window-to-game conversion, so it
    /// says nothing about where a real pointer lands.
    /// </summary>
    public static string Shell(string a1, string a2)
    {
        static string Ok(string what) =>
            "{\"ok\":true,\"cmd\":\"point\",\"pointer\":" +
            (_fake is { } f ? $"[{f.X.ToString("0.#", Inv)},{f.Y.ToString("0.#", Inv)}]" : "\"host\"") +
            ",\"what\":\"" + what + "\",\"live\":\"" + _live.Replace("\"", "'") + "\",\"row\":" + _hover +
            ",\"pageCursor\":" + _pageCursor + ",\"padOwns\":" + (_padOwns ? "true" : "false") +
            ",\"confirmsTotal\":" + _confirmsTotal + ",\"injectsTotal\":" + _injectsTotal +
            ",\"cancelsTotal\":" + _cancelsTotal + "}";

        switch (a1)
        {
            case "": return Ok("status");
            case "off": _fake = null; _lastPos = new(float.NaN, float.NaN); return Ok("off");
            case "left": _fakeLeft = true; return Ok("left");
            case "right": _fakeRight = true; return Ok("right");
        }
        if (float.TryParse(a1, System.Globalization.NumberStyles.Float, Inv, out float x)
            && float.TryParse(a2, System.Globalization.NumberStyles.Float, Inv, out float y))
        {
            _fake = new Vector2(x, y);
            return Ok("moved");
        }
        return "{\"ok\":false,\"cmd\":\"point\",\"error\":\"point [<x> <y>|left|right|off]\"}";
    }

    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Running totals for the shell, which the probe's per-second
    /// counters are not: those are zeroed each time they are printed.</summary>
    static long _confirmsTotal, _injectsTotal, _cancelsTotal;

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
