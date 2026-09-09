using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
// Three namespaces in scope define a MouseButton or a Mouse; these two aliases
// pick the ones meant here. Silk's is what HostWindow.IsMouseButtonDown takes,
// and RecompOne.Runtime.Events' is what the wheel event carries.
using HostMouseButton = Silk.NET.Input.MouseButton;
// Upstream 0409bc2 emits one class per overlay. Every func_ named here is
// GAME.EXE's, so the alias names the overlay once.
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// Hover the in-game menu with the mouse: point at an item and the game's own
/// cursor moves to it, left click confirms, right click backs out.
///
///     KF2_MENUMOUSE=0        off -- the menu is pad and keyboard only again
///     KF2_MENUMOUSE_PROBE=1  the layout table, the pointer's row, and what it did
///
/// The switch is a setting, on the Mouse tab of the port's Input pane, and it is
/// **independent of mouse look**: a player who never captures the pointer still
/// has a pointer, and pointing it at a menu is the one thing a mouse can do in
/// this game without being locked to the window first.
///
/// ## Two things rule out every obvious approach
///
/// **The cursor index is a stack local.** `func_80018E80` keeps it in `S2` and
/// the open page at `SP+0x18`; the scrolling lists keep theirs in a
/// caller-supplied descriptor. There is no global to write. The only ways in are
/// the stepper's return value in `V0` and its out-pointers.
///
/// **Injecting Up/Down does not work, and is already recorded as not working.**
/// See "The wall is the title, not the Continue menu" in docs/PATCHES_AND_MODS.md
/// -- driving `PAD_dr` at the start menu moved nothing, while Cross registered.
/// The other injection route is worse: the menu does not read stage 3's pad word
/// at `0x80199554` at all, it calls `PadRead(1)` itself through `func_80022E58`,
/// so <see cref="Analog"/>'s mechanism cannot reach it either.
///
/// So this drives the stepper's **return value**, not the pad. Which also
/// answers the question a synthetic Cross would have raised: neither stepper
/// edge-detects (that is the whole finding behind <see cref="MenuPacing"/>), so a
/// held synthetic confirm would fire on every iteration of the menu loop and run
/// away through the submenus. A click is edge-detected here and spent once.
///
/// ## Where the items are
///
/// The fixed option lists are drawn by `func_800208D8(group, count, cursor,
/// confirmed)`, and their positions are a **static layout table in GAME.EXE
/// data** -- so hover is exact rather than calibrated:
///
///     base   = 0x80064CD4 + 0x134 * group      /* the emitted chain computes 308*a0 */
///     header = base                            /* drawn only when its X is non-zero */
///     item i = base + 0x1C * (i + 1)           /* 0x134 is 11 records of 0x1C */
///     record +0x00 = u16 X, +0x02 = u16 Y      /* screen coordinates */
///
/// and `func_800218B4(template, record)` draws the box `(X, Y)` to
/// `(X + w - 6, Y + h - 6)` with `w`/`h` out of the *template* at `+0x8`/`+0xA`,
/// not out of the record. See "The menu's item positions are a table" in
/// docs/GAME_INTERNALS.md.
///
/// The table is read live out of RAM every time the menu draws, and it is read
/// off the arguments `func_800208D8` was actually called with, so nothing here
/// knows how many pages the menu has or how many items each holds.
///
/// ## The hit test is on Y alone
///
/// Two reasons, and the second is the load-bearing one. The lists are vertical,
/// so a row band spanning the picture is what a menu wants anyway; and **X is the
/// axis widescreen moves**. The picture is the widened display buffer, so a game
/// X of 160 is not the middle of it, while a game Y of 120 is always the middle
/// of the picture whatever the aspect. Testing Y alone means the aspect cannot
/// make the pointer miss. The rect's X is read and reported by the probe anyway,
/// because a row whose X moves is the first sign the table was misread.
///
/// A row owns the half-open band `[y, y + pitch)`, pitch being the smallest
/// positive gap between two rows -- which for an evenly spaced list *is* the
/// spacing -- floored at the box's own height. So there are no dead gaps between
/// items, and the band stops one row past the last one rather than running to the
/// bottom of the screen.
///
/// ## Whichever device moved last owns the cursor
///
/// Hover only takes over once the pointer has **moved**, and hands back the
/// moment the pad or the keyboard moves the cursor itself. Without that a mouse
/// resting anywhere over the picture would pin the selection to whatever row it
/// happens to sit on, and the D-pad would appear broken. <see cref="IdleMs"/> is
/// how long a move stays live.
///
/// ## What it writes
///
/// `V0`, the stepper's own out-parameters, and `0x8006E5D0` -- the blink
/// direction, zeroed to restart the wink exactly where the Up/Down arms zero it.
/// Nothing else in game memory. `0x8006E5C4`, the repeat gate's latch, is
/// deliberately untouched, so <see cref="MenuPacing"/> is unaffected: a hover
/// costs no repeat delay because it never goes through the pad.
///
/// **The wheel is deliberately not here.** It was written and taken out: a wheel
/// steps the cursor relative to where it is, hover puts it where the pointer is,
/// and the two contradict each other on the very next iteration of the menu loop
/// -- scroll two rows and hover snaps it straight back to whatever the pointer is
/// over. One of them has to own the cursor, and pointing at a thing is the
/// gesture that was asked for.
///
/// **Never judged by eye**: whether the cursor lands on the item the pointer is
/// actually over (the measurements can only say it lands on the row the *table*
/// says is there), whether the desktop pointer over a 1996 menu reads
/// acceptably, and whether a move blip on every row crossed is pleasant or noisy.
///
/// Scrolling lists -- inventory, magic, equipment, `func_8001EB70` -- are **not**
/// covered yet: their rows are drawn by per-page loops rather than by
/// `func_800208D8`, so the geometry has to be found once per page. See "The menu
/// pointer" in docs/INPUT.md.
/// </summary>
public static class MenuMouse
{
    /// <summary>The in-game menu's modal loop. It blocks for the whole session,
    /// so a pre and a post on it are "opened" and "closed".</summary>
    const uint MenuLoop = 0x80018E80;

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
    const int MaxRows = 10;

    /// <summary>The sprite template an unselected item is drawn with; its `+0x8`
    /// and `+0xA` are the box's width and height, and the drawn box is six pixels
    /// short of each.</summary>
    const uint ItemTemplate = 0x80064C20;
    const int TemplateInset = 6;

    /// <summary>The blink's direction word. Zeroed on an accepted move, which is
    /// what restarts the wink; see <see cref="MenuPacing"/>, which holds the
    /// counter beside it.</summary>
    const uint BlinkDir = 0x8006E5D0;

    /// <summary>The menu's own blips: move, confirm, cancel. The arguments
    /// `func_80022DC4` takes.</summary>
    const uint BlipMove = 0x10, BlipConfirm = 0x11, BlipCancel = 0x12;

    /// <summary>How long a pointer movement keeps the cursor. Long enough that
    /// reading an item and then clicking it is one gesture, short enough that a
    /// mouse left alone gives the pad the cursor back before the next menu.</summary>
    const long IdleMs = 2000;

    /// <summary>How stale a layout read may be and still be used. A page change
    /// redraws immediately, so anything older than this means the menu is drawing
    /// something else and the rows on file are not on screen.</summary>
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
        Version = "1.0",
        Description = "Hover and click the in-game menu with the mouse.",
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

    // --- the geometry --------------------------------------------------------

    static readonly int[] _rowX = new int[MaxRows];
    static readonly int[] _rowY = new int[MaxRows];
    static int _rowCount;
    static int _rowH, _rowPitch;
    static int _group = -1;
    static long _drawnAt;

    // --- the pointer ---------------------------------------------------------

    static Vector2 _lastPos = new(float.NaN, float.NaN);
    static long _movedAt;

    /// <summary>Set when the game moved the cursor itself; cleared when the
    /// pointer next moves. This is the "whichever moved last owns it" latch.</summary>
    static bool _padOwns;

    static bool _leftWas, _rightWas;
    static bool _clickLeft, _clickRight;
    static bool _inPicture;

    /// <summary>The pointer's Y in the game's own pixels, valid while
    /// <see cref="_inPicture"/>. Kept for the probe as much as for the hit
    /// test.</summary>
    static float _gameY = float.NaN;

    /// <summary>The row the pointer is over, or -1. Recomputed once per stepper
    /// call, which is once per iteration of the menu loop.</summary>
    static int _hover = -1;

    // The stepper's arguments, stashed by the pre because the body clobbers them.
    static bool _haveArgs;
    static int _cursorIn, _maxIn;
    static uint _selPtr, _confirmPtr, _cancelPtr;

    // --- the probe -----------------------------------------------------------

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _windowMs = -1.0;
    static int _samples, _hovers, _moves, _confirms, _cancels;
    static int _lastReported = -2;

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
    /// Attach the three hooks, deferred to the first overlay load for the reason
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
                                 "The in-game menu stays pad and keyboard only.");
    }

    static bool _loopHooked, _drawHooked, _cursorHooked;

    static bool Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(MenuMouse);
        MethodInfo Own(string name) => self.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

        MethodInfo? loop = null, draw = null, cursor = null;

        if (!_loopHooked)
        {
            loop = SymbolRegistry.Resolve("game", null, MenuLoop);
            if (loop == null)
                Console.Error.WriteLine($"[KF2] menu pointer: no game function at 0x{MenuLoop:X8} -- " +
                                        "a captured pointer will not be given back inside the menu.");
            else
            {
                HookManager.AddPre(_self, loop, Own(nameof(BeforeMenu)));
                HookManager.AddPost(_self, loop, Own(nameof(AfterMenu)));
            }
        }

        if (!_drawHooked)
        {
            draw = SymbolRegistry.Resolve("game", null, OptionDraw);
            if (draw == null)
                Console.Error.WriteLine($"[KF2] menu pointer: no game function at 0x{OptionDraw:X8} -- " +
                                        "no item positions, so hover has nothing to point at.");
            else HookManager.AddPre(_self, draw, Own(nameof(BeforeOptionDraw)));
        }

        if (!_cursorHooked)
        {
            cursor = SymbolRegistry.Resolve("game", null, FixedCursor);
            if (cursor == null)
                Console.Error.WriteLine($"[KF2] menu pointer: no game function at 0x{FixedCursor:X8} -- " +
                                        "nothing can move the cursor but the pad.");
            else
            {
                HookManager.AddPre(_self, cursor, Own(nameof(BeforeCursor)));
                HookManager.AddPost(_self, cursor, Own(nameof(AfterCursor)));
            }
        }

        HookManager.Commit();

        // Read the detours back rather than the Add* returns, which only say the
        // delegate was queued. See patches/HookAttach.cs.
        _loopHooked |= HookAttach.Installed(loop);
        _drawHooked |= HookAttach.Installed(draw);
        _cursorHooked |= HookAttach.Installed(cursor);

        Console.WriteLine($"[KF2] menu pointer: {(Enabled ? "on" : "off")}, " +
                          $"session {(_loopHooked ? "scoped" : "NOT scoped")}, " +
                          $"items {(_drawHooked ? "read" : "NOT read")}, " +
                          $"cursor {(_cursorHooked ? "driven" : "NOT driven")}");

        return _loopHooked && _drawHooked && _cursorHooked;
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

        _rowCount = 0;
        _group = -1;
        _hover = -1;
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

        _rowCount = 0;
        _hover = -1;

        if (_tookCapture)
        {
            _tookCapture = false;
            if (Mouse.Enabled) Mouse.SetCaptured(true);
        }
    }

    // ------------------------------------------------------------------------
    // The geometry
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
        if (count <= 0 || count > MaxRows || group < 0 || group > 31) return true;

        uint bas = LayoutBase + (uint)group * GroupStride;

        int h = (int)m.ReadU16(ItemTemplate + 0xA) - TemplateInset;
        for (int i = 0; i < count; i++)
        {
            uint rec = bas + RecordStride * (uint)(i + 1);
            _rowX[i] = (short)m.ReadU16(rec);
            _rowY[i] = (short)m.ReadU16(rec + 2);
        }

        _rowCount = count;
        _rowH = h > 0 ? h : 1;
        _group = group;
        _drawnAt = Environment.TickCount64;

        // The pitch is the smallest positive gap between two rows, which for an
        // evenly spaced list is the spacing. Floored at the box height so a list
        // drawn with its boxes touching still has a band each, and defaulted to
        // the box height for a list of one.
        int pitch = int.MaxValue;
        for (int i = 0; i < count; i++)
        for (int j = i + 1; j < count; j++)
        {
            int d = Math.Abs(_rowY[i] - _rowY[j]);
            if (d > 0 && d < pitch) pitch = d;
        }
        _rowPitch = pitch == int.MaxValue ? _rowH : Math.Max(pitch, _rowH);

        if (_probe && _lastReported != group)
        {
            _lastReported = group;
            var ys = string.Join(",", _rowY.Take(count));
            Console.WriteLine($"[KF2] menu pointer: group {group} at 0x{bas:X8}, {count} rows, " +
                              $"x {_rowX[0]}, y {ys}, box {_rowH}, pitch {_rowPitch}");
        }

        return true;
    }

    // ------------------------------------------------------------------------
    // The cursor
    // ------------------------------------------------------------------------

    /// <summary>
    /// Stash the stepper's arguments. The body clobbers `A0`-`A3` and unwinds the
    /// stack, so the post cannot read them -- and the fifth argument is on the
    /// *caller's* stack at `SP+0x10`, which is only what `SP` points at before the
    /// prologue runs. Same o32 window patches/AreaWarp.cs writes through.
    /// </summary>
    public static bool BeforeCursor(CpuContext c, IMemory m)
    {
        if (!Enabled) return true;

        _cursorIn = (int)c.A0;
        _maxIn = (int)c.A1;
        _selPtr = c.A2;
        _confirmPtr = c.A3;
        _cancelPtr = m.ReadU32(c.SP + 0x10u);
        _haveArgs = true;
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
        if (!_haveArgs) return;
        _haveArgs = false;
        if (!Enabled) return;

        uint cursor = c.V0;
        bool gameMoved = (int)cursor != _cursorIn;
        bool gameConfirmed = m.ReadU32(_confirmPtr) != 0;

        // The pad or the keyboard just moved it, so it owns the cursor until the
        // pointer moves again. Read before Sample, which is what clears the latch.
        if (gameMoved) _padOwns = true;

        Sample();

        if (gameMoved || gameConfirmed) { Report(); return; }

        bool live = !_padOwns && Environment.TickCount64 - _movedAt < IdleMs;
        int hover = live ? _hover : -1;

        // Everything that blips has to happen before V0 is written: the blip is a
        // real call into the recompiled routine and it clobbers V0 along with the
        // argument registers.
        int target = hover >= 0 && hover <= _maxIn ? hover : (int)cursor;

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
                m.WriteU32(_confirmPtr, 1u);
                if (target < _maxIn) m.WriteU32(_selPtr, (uint)target);
                else if (_cancelPtr != 0) m.WriteU32(_cancelPtr, 0xFFFFFFFFu);
                _confirms++;
            }
        }
        else if (_clickRight)
        {
            _clickRight = false;
            if (_inPicture)
            {
                Blip(c, m, BlipCancel);
                if (_cancelPtr != 0) m.WriteU32(_cancelPtr, 0xFFFFFFFFu);
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

    // ------------------------------------------------------------------------
    // The pointer
    // ------------------------------------------------------------------------

    /// <summary>
    /// Where the pointer is, whether it moved, and what is pressed -- and from
    /// that, which row it is over.
    ///
    /// The position comes from ImGui's own IO rather than from a new host API:
    /// it is in the same screen space as <c>OutputView</c>, it is a plain field
    /// read so it costs nothing per call, and patches/MapPanel.cs already reads
    /// it. It carries the last presented frame's value, which is the right one --
    /// the menu presents through `func_800226A8`, and the panels draw inside that
    /// `VSync`.
    /// </summary>
    static void Sample()
    {
        _samples++;
        _hover = -1;
        _inPicture = false;
        _gameY = float.NaN;

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

        // OutputView directly, and deliberately *not* MapRender.Picture: that
        // helper falls back to the whole viewport when the panel drew no picture,
        // which is right for something that has to be drawn somewhere and wrong
        // for a coordinate conversion. A hit test would rather answer "nowhere"
        // than answer confidently against a rectangle the game is not in.
        if (!OutputView.Valid) return;
        var g0 = OutputView.Min;
        var size = OutputView.Size;
        int gameH = OutputView.GameH;
        if (size.X < 32f || size.Y < 32f || gameH <= 0) return;

        if (pos.X < g0.X || pos.X > OutputView.Max.X ||
            pos.Y < g0.Y || pos.Y > OutputView.Max.Y) return;

        // Set before the geometry is consulted: whether the pointer is over the
        // picture is a fact about the pointer, and a right click means "back" on
        // a screen with no list on it just as much as on one with.
        _inPicture = true;
        _gameY = (pos.Y - g0.Y) / size.Y * gameH;

        // The rectangle and the scale come from two different places, so this is
        // the one test that catches them disagreeing rather than trusting the
        // pair. It caught exactly that: the size was first published off the
        // GL backend's *render target*, which the render-scale setting makes
        // three times the picture, and the probe read a pointer "in the picture
        // at game y 582" on a 240-line screen.
        if (_gameY < 0f || _gameY > gameH) { _inPicture = false; _gameY = float.NaN; return; }

        if (_rowCount == 0 || Environment.TickCount64 - _drawnAt > GeomStaleMs) return;

        for (int i = 0; i < _rowCount; i++)
        {
            if (_gameY < _rowY[i] || _gameY >= _rowY[i] + _rowPitch) continue;
            _hover = i;
            _hovers++;
            break;
        }
    }

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
                          $"{(_inPicture ? $"in the picture at game y {_gameY:0.#}" : "outside the picture")} " +
                          $"of {OutputView.GameW}x{OutputView.GameH} " +
                          $"-> row {_hover} of {_rowCount} (group {_group}), " +
                          $"{(_padOwns ? "pad owns" : "pointer owns")}, " +
                          $"hovered {_hovers}, moved {_moves}, " +
                          $"confirmed {_confirms}, cancelled {_cancels}");

        _windowMs = now;
        _samples = _hovers = _moves = _confirms = _cancels = 0;
    }
}
