using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Widens the game's own view cone so a wide picture has something to put in the
/// margin.
///
///     KF2_WIDESCREEN_CULL=0       leave the cone at its 4:3 shape
///     KF2_WIDESCREEN_CULL=1.5     force a widening factor instead of the aspect's
///     KF2_WIDESCREEN_CULL_PROBE=1 the cone's shape and what the grid clipped
///
/// ## What the cull actually is
///
/// King's Field does not cull per polygon and it does not cull per object against
/// a frustum. It keeps a **24×24 byte grid of tiles** at <c>0x80192EAC</c> — one
/// byte per 2048-unit map tile — rebuilt every frame at the top of the renderer
/// by <c>func_8002D3A8</c>, and *everything* the frame draws is gated on it. Both
/// of the renderer's object walks ask the grid whether the tile an object stands
/// on is lit (<c>func_80032D78</c> for a point, <c>func_80032DE8</c> for a box),
/// and the world's own 200 geometry blocks are gated the same way. A tile that is
/// not in the grid is not submitted at all, which is why no amount of margin can
/// show what is out there: the GPU never sees it.
///
/// The grid is built as a **top-down trapezoid** — the view frustum flattened onto
/// the map. Four calls to <c>func_8002CD0C</c> (a Bresenham line into the grid)
/// draw its outline, <c>func_8002CF0C</c> scanline-fills between them, and an
/// occlusion pass then radiates out from the middle and knocks out what walls
/// hide. The trapezoid's corners come from seven <c>s16</c> pairs in GAME.EXE's
/// data at <see cref="Table"/>, in units of 1/256 of a tile, each pair lerped by
/// <c>0x1000 - rcos(pitch)</c> so the shape opens out as you look up or down:
///
///     index 0        how far ahead of the player the 24×24 window is centred
///     indices 1, 2   the far edge, left and right      (-2272, +2336 = ±9 tiles)
///     index 3        the far edge's depth              (+2688  = 10.5 tiles)
///     indices 4, 5   the near edge, left and right     (-224, +288 = ±1 tile)
///     index 6        the near edge's depth             (-128 = half a tile back)
///
/// So the cone is 9 tiles wide at 10.5 tiles out: a half-angle of 36°, which is
/// <c>160/H</c> for <c>H ≈ 220</c> — the screen's own half-width, plus a tile of
/// slack at the near end. It is the 4:3 frustum, drawn on the floor.
///
/// Widening it is therefore four numbers: scale indices 1, 2, 4 and 5 by the same
/// ratio the margin widens the picture by (428/320 at 16:9), and the trapezoid
/// opens to the new screen edges. Nothing else moves — the depth stays, the
/// forward push stays, the projection was never involved.
///
/// ## Why that needs the fill fixed as well
///
/// The 24×24 window is exactly big enough for the cone the game ships and not one
/// tile bigger. Its centre sits 5 tiles ahead of the player, so the far corners
/// are <c>sqrt(5.5² + 9²) = 10.55</c> tiles from it and the window reaches 11 —
/// which is not slack, it is a fit. Any widening at all pushes a corner off the
/// grid at some yaw.
///
/// That matters because of *how* the game fills the trapezoid.
/// <c>func_8002CF0C</c> takes each of the 24 rows, finds the first run of the
/// marker scanning in from the left and the first scanning in from the right, and
/// fills between them. <c>func_8002CD0C</c> drops the cells that fall outside the
/// grid. So a row whose left edge left the grid has one boundary instead of two,
/// the two scans meet, and **the row is not filled at all** — a whole rank of
/// tiles vanishes rather than merely being clipped. Widening the table on its own
/// trades pop-in at the edges for chunks of the world blinking out.
///
/// So this also carries <see cref="AfterFill"/>: a post-hook on the fill that
/// re-rasterises the four recorded edges with the game's own Bresenham, takes each
/// row's true span, clamps it to the grid and fills that. It runs only when a
/// corner actually fell outside — at 4:3 the cone fits, nothing is recorded as
/// clipped, and the hook returns having read four integers.
///
/// ## What it cannot buy
///
/// The window is 24 tiles and stays 24 tiles: its stride and its bounds are
/// hard-coded in a dozen recompiled routines and in an occlusion pass that floods
/// 14 rings out from the centre, so growing it is a rewrite of the visibility
/// system rather than a patch. Laterally that caps the cone at about 9.5 tiles
/// from the centre in the worst yaw and 12 in the best, against the 9 it ships
/// with. 16:9 wants 12; 21:9 wants 15. So the near and middle distance — where
/// edge pop-in is actually visible — widens fully, and the far corners of a very
/// wide aspect stay clipped. <c>KF2_WIDESCREEN_CULL_PROBE=1</c> reports how many
/// rows that cost in the window it just measured.
///
/// See "The view cone is a 24×24 tile grid" in NOTES.md.
/// </summary>
public static class CullCone
{
    /// <summary>GAME.EXE data: seven (level, pitched) pairs of s16, in 1/256 of a
    /// tile, lerped by <c>0x1000 - rcos(pitch)</c> and then rotated by the yaw
    /// into the four corners of the view trapezoid.</summary>
    public const uint Table = 0x80068760;

    /// <summary>What the game ships. Read back before anything is written, so a
    /// mismatch means the address is wrong and this patch stays out of the way
    /// rather than corrupting the renderer's idea of what is visible.</summary>
    static readonly short[] Stock =
    [
         1280,     0,   // 0  forward push of the 24x24 window
        -2272, -1248,   // 1  far edge, left
         2336,  1312,   // 2  far edge, right
         2688,  1152,   // 3  far edge, depth
         -224, -1248,   // 4  near edge, left
          288,  1312,   // 5  near edge, right
         -128, -1152,   // 6  near edge, depth
    ];

    /// <summary>The four the widening scales: the left and right of each end.
    /// The two depths and the forward push are left alone — the cone gets wider,
    /// not longer, and the push is where the occlusion flood starts.</summary>
    static readonly int[] LateralPairs = [1, 2, 4, 5];

    /// <summary>func_8002CD0C, one Bresenham edge of the trapezoid into the grid.
    /// Called four times a frame, from func_8002D3A8 and nowhere else.</summary>
    const uint LineRoutine = 0x8002CD0C;

    /// <summary>func_8002CF0C, the scanline fill between those edges.</summary>
    const uint FillRoutine = 0x8002CF0C;

    /// <summary>The visibility grid itself: 24×24 bytes, row-major on Z.</summary>
    const uint Grid = 0x80192EAC;

    /// <summary>Grid index = (world >> 11) + this, per axis. Written by
    /// func_8002D3A8 as a 32-bit word and read by the grid routines as a u16, so
    /// it is read here the same way and sign-extended from 16 bits.</summary>
    const uint OffsetX = 0x80192E98;

    /// <summary>See <see cref="OffsetX"/>.</summary>
    const uint OffsetZ = 0x80192E9C;

    /// <summary>How wide the grid is, in tiles, on both axes.</summary>
    const int Span = 24;

    /// <summary>Where the choice is kept between runs.</summary>
    public const string Key = "kf2.widescreen.widencull";

    /// <summary>Widen the cone with the aspect. On by default: a wide picture
    /// whose cull is still 4:3 shows the margin filling in and emptying as you
    /// turn, which is a worse picture than no widescreen at all.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>The factor in force, 1 when the cone is the game's own.</summary>
    public static float Factor { get; private set; } = 1f;

    // KF2_WIDESCREEN_CULL: "0" to switch off, a number to pin the factor.
    static bool? _forced;
    static float? _forcedFactor;

    // KF2_WIDESCREEN_CULL_PROBE.
    static bool _measure;

    // KF2_WIDESCREEN_CULL_PROBE=2: the post-occlusion census, by ring.
    static bool _rings;

    /// <summary>func_8002D3A8, the whole grid build. Hooked only for the ring
    /// census, which has to be taken after the occlusion flood has run.</summary>
    const uint BuildRoutine = 0x8002D3A8;

    // Lit cells per Chebyshev ring from the window's middle, summed over the
    // report window. Ring 11 is the outermost the 24x24 window has.
    static readonly long[] _ring = new long[12];
    static long _ringFrames;

    // Set once the table has been read back and matched against Stock. Nothing is
    // written before that, and nothing is written at all if it did not match.
    static bool _verified;
    static bool _refused;

    // Whether GAME.EXE is the overlay currently mapped over 0x80011000. The table
    // is in its data, and OPEN.EXE and END.EXE link at the same base, so writing
    // while either of those is up would be writing into some unrelated function.
    static bool _resident;

    // The four edges of the trapezoid as the game handed them to func_8002CD0C,
    // in grid coordinates, newest frame only.
    static readonly int[] _ex0 = new int[4], _ez0 = new int[4], _ex1 = new int[4], _ez1 = new int[4];
    static int _edges;
    static byte _mark;

    // Reused by the fill so it allocates nothing per frame.
    static readonly int[] _rowLo = new int[Span], _rowHi = new int[Span];

    // Per report window. _lit is the census that actually answers "did the cull
    // open": how many of the 576 tiles the cone marked, which depends on the cone
    // and not on where in the area the camera happens to be standing.
    static long _frames, _clippedFrames, _clippedRows, _recovered, _lit;
    static double _windowStart;

    static double Now => Environment.TickCount64 / 1000.0;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.cullcone",
        Name = "Cull cone",
        Version = "1.0",
        Description = "Opens the game's own tile-visibility cone to the widescreen aspect.",
    };

    public static void Configure(string? widen, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(widen))
        {
            if (widen.Equals("0", StringComparison.Ordinal) ||
                widen.Equals("off", StringComparison.OrdinalIgnoreCase))
                _forced = false;
            else if (widen.Equals("1", StringComparison.Ordinal) ||
                     widen.Equals("on", StringComparison.OrdinalIgnoreCase))
                _forced = true;
            else if (float.TryParse(widen, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float f) && f > 0f)
            {
                _forced = true;
                _forcedFactor = Math.Clamp(f, 1f, 4f);
            }
        }

        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
        {
            _measure = true;
            // =2 also censuses the grid *after* the occlusion flood, by ring, which
            // is the question "is the 24-tile window actually binding" — a cone the
            // walls close off before it reaches the edge does not care how big the
            // window is.
            _rings = probe.Equals("2", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Attach on the first overlay load, the way every other patch here does, and
    /// read the saved setting on <c>RuntimeReadyEvent</c> — ConfigManager only
    /// loads inside HostWindow.Initialize, which is after Program.cs.
    /// </summary>
    public static void Install()
    {
        _windowStart = Now;

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(Key, true);
            Apply();
        });

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            // GAME.EXE is where the table lives, and a load restores it from the
            // disc image, so the widening is written again every time it arrives.
            // The area overlays sit elsewhere in RAM and leave it alone.
            if (e.Name is "game" or "open" or "end")
            {
                _resident = e.Name == "game";
                _verified = false;
            }

            if (!attached) { attached = true; Attach(); }
            Apply();
        });
    }

    /// <summary>Turn the widening on or off at run time.</summary>
    public static void SetEnabled(bool on)
    {
        Enabled = on;
        Apply();
    }

    /// <summary>Called by <see cref="Widescreen"/> whenever the aspect moves, so
    /// the cone follows the picture without the settings page having to know the
    /// two are connected.</summary>
    public static void Apply()
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null || _refused || !_resident) return;

        float want = 1f;
        if (Enabled && Widescreen.On)
        {
            // The same ratio the picture widens by: the runtime's own margin rule,
            // asked about this game's 320-pixel screen, so the cone and the render
            // target cannot disagree about how wide "wide" is.
            int margin = Display.WideMargin(320);
            want = _forcedFactor ?? (320f + 2f * margin) / 320f;
        }
        else if (_forcedFactor is { } pinned && Enabled)
        {
            want = pinned;
        }

        if (!_verified && !Verify(m)) return;

        Factor = want;
        for (int i = 0; i < Stock.Length; i += 2)
        {
            bool lateral = Array.IndexOf(LateralPairs, i / 2) >= 0;
            Write(m, i, lateral ? Scale(Stock[i]) : Stock[i]);
            Write(m, i + 1, lateral ? Scale(Stock[i + 1]) : Stock[i + 1]);
        }

        if (_measure)
            Console.WriteLine($"[KF2] cull cone: x{Factor:0.###}, " +
                              $"far edge ±{Scale(Stock[4]) / 256f:0.#} tiles at " +
                              $"{Stock[6] / 256f:0.#}, window ±12");
    }

    static short Scale(short v)
    {
        int scaled = (int)MathF.Round(v * Factor);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    static void Write(IMemory m, int index, short value) =>
        m.WriteU16(Table + (uint)index * 2u, (ushort)value);

    // Read the table back before touching it. It is a static const in GAME.EXE's
    // data, so it is the shipped values or the address is wrong; there is no third
    // case worth guessing at.
    static bool Verify(IMemory m)
    {
        for (int i = 0; i < Stock.Length; i++)
        {
            short got = (short)m.ReadU16(Table + (uint)i * 2u);
            if (got == Stock[i]) continue;

            // Already widened by a previous Apply for this overlay load: the words
            // we wrote are the ones we would write again, so that is not a
            // mismatch, it is the second call.
            if (Factor != 1f && got == Scale(Stock[i])) continue;

            _refused = true;
            Console.Error.WriteLine(
                $"[KF2] cull cone: 0x{Table:X8}+{i * 2} is {got}, expected {Stock[i]} — " +
                "not the view-cone table. Leaving the cull alone; the margin will show " +
                "whatever the game's own 4:3 cone submitted. " +
                "See \"The view cone is a 24×24 tile grid\" in NOTES.md.");
            return false;
        }

        _verified = true;
        return true;
    }

    static void Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(CullCone);

        int n = 0;
        n += Hook(LineRoutine, self.GetMethod(nameof(BeforeLine), BindingFlags.Public | BindingFlags.Static)!, pre: true);
        n += Hook(FillRoutine, self.GetMethod(nameof(AfterFill), BindingFlags.Public | BindingFlags.Static)!, pre: false);

        if (_rings)
            Hook(BuildRoutine, self.GetMethod(nameof(AfterBuild), BindingFlags.Public | BindingFlags.Static)!, pre: false);

        if (n < 2)
        {
            Console.Error.WriteLine("[KF2] cull cone: the fill fix is not hooked — the cone will not " +
                                    "be widened, since a widened cone the game cannot fill loses whole " +
                                    "ranks of tiles. See \"The view cone is a 24×24 tile grid\" in NOTES.md.");
            _refused = true;
            return;
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] cull cone: {(Enabled ? "follows the aspect" : "off")}");
    }

    static int Hook(uint addr, MethodInfo impl, bool pre)
    {
        var target = SymbolRegistry.Resolve("game", null, addr);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] cull cone: no function at game/0x{addr:X8}");
            return 0;
        }
        return (pre ? HookManager.AddPre(_self, target, impl)
                    : HookManager.AddPost(_self, target, impl)) ? 1 : 0;
    }

    /// <summary>
    /// Record one edge of the trapezoid as the game is about to draw it. Four of
    /// these run per frame and each reads four words; the recording is
    /// unconditional so that the fill can tell a clipped frame from a fitting one
    /// without the two hooks having to agree about the aspect.
    /// </summary>
    public static void BeforeLine(CpuContext c, IMemory m)
    {
        int i = _edges & 3;
        _ex0[i] = GridX(m, m.ReadU32(c.A0));
        _ez0[i] = GridZ(m, m.ReadU32(c.A0 + 4u));
        _ex1[i] = GridX(m, m.ReadU32(c.A1));
        _ez1[i] = GridZ(m, m.ReadU32(c.A1 + 4u));
        _mark = (byte)c.A2;
        _edges++;
    }

    // func_8002CD0C's own arithmetic: a logical shift of the world coordinate,
    // plus the offset read as a u16, and the sum taken 16 bits wide. Sign-extending
    // that is the signed grid index, off the grid included.
    static int GridX(IMemory m, uint world) => (short)((world >> 12) + m.ReadU16(OffsetX));

    static int GridZ(IMemory m, uint world) => (short)((world >> 12) + m.ReadU16(OffsetZ));

    /// <summary>
    /// Fill the rows the game's own fill could not. It scans each row in from both
    /// sides looking for the outline and fills between the two runs it finds, so a
    /// row whose outline left the grid on one side gets no fill at all. This
    /// re-walks the four recorded edges with the same Bresenham the game uses,
    /// takes each row's real span, clamps it and writes the marker over it.
    ///
    /// Runs after the fill and before the occlusion pass, which is where the game's
    /// own fill sits, so the recovered tiles are shadowed by walls like every other
    /// tile. Returns immediately unless a corner actually fell off the grid.
    /// </summary>
    public static void AfterFill(CpuContext c, IMemory m)
    {
        int edges = _edges;
        _edges = 0;
        _frames++;

        if (edges != 4) { Report(m); return; }

        bool clipped = false;
        for (int e = 0; e < 4 && !clipped; e++)
            clipped = Outside(_ex0[e], _ez0[e]) || Outside(_ex1[e], _ez1[e]);

        if (!clipped) { Report(m); return; }
        _clippedFrames++;

        for (int y = 0; y < Span; y++) { _rowLo[y] = int.MaxValue; _rowHi[y] = int.MinValue; }
        for (int e = 0; e < 4; e++) Trace(_ex0[e], _ez0[e], _ex1[e], _ez1[e]);

        for (int y = 0; y < Span; y++)
        {
            if (_rowLo[y] > _rowHi[y]) continue;
            if (_rowLo[y] >= 0 && _rowHi[y] < Span) continue;   // the game filled this one

            int lo = Math.Max(_rowLo[y], 0), hi = Math.Min(_rowHi[y], Span - 1);
            if (lo > hi) continue;

            uint row = Grid + (uint)(y * Span);
            bool touched = false;
            for (int x = lo; x <= hi; x++)
                if (m.ReadU8(row + (uint)x) != _mark)
                {
                    m.WriteU8(row + (uint)x, _mark);
                    _recovered++;
                    touched = true;
                }
            if (touched) _clippedRows++;
        }

        Report(m);
    }

    static bool Outside(int x, int z) => (uint)x >= Span || (uint)z >= Span;

    /// <summary>
    /// The census that says whether the 24-tile window is a limit worth lifting:
    /// what survives the occlusion flood, by how far out it sits. A cone the walls
    /// close off two tiles from the camera does not care how big the window is, and
    /// King's Field is corridors. Post-hook on the whole build, so this is the grid
    /// exactly as func_80032D78 will read it. KF2_WIDESCREEN_CULL_PROBE=2.
    /// </summary>
    public static void AfterBuild(CpuContext c, IMemory m)
    {
        _ringFrames++;
        for (int z = 0; z < Span; z++)
            for (int x = 0; x < Span; x++)
            {
                if (m.ReadU8(Grid + (uint)(z * Span + x)) == 0) continue;
                int r = Math.Max(Math.Abs(x - Span / 2), Math.Abs(z - Span / 2));
                _ring[Math.Min(r, _ring.Length - 1)]++;
            }
    }

    // func_8002CD0C's Bresenham, without its per-cell bounds check: the same
    // major-axis choice, the same halved error term and the same "step the minor
    // axis when the error stops being positive", so the span this produces is the
    // one the game would have drawn had the grid been big enough.
    static void Trace(int x, int y, int x1, int y1)
    {
        int dx = x1 - x, dy = y1 - y;
        int sx = dx >= 0 ? 1 : -1; if (dx < 0) dx = -dx;
        int sy = dy >= 0 ? 1 : -1; if (dy < 0) dy = -dy;

        if (dx >= dy)
        {
            int err = dx >> 1;
            for (int i = 0; i <= dx; i++)
            {
                Extend(x, y);
                err -= dy;
                if (err <= 0) { y += sy; err += dx; }
                x += sx;
            }
        }
        else
        {
            int err = dy >> 1;
            for (int i = 0; i <= dy; i++)
            {
                Extend(x, y);
                err -= dx;
                if (err <= 0) { x += sx; err += dy; }
                y += sy;
            }
        }
    }

    static void Extend(int x, int y)
    {
        if ((uint)y >= Span) return;
        if (x < _rowLo[y]) _rowLo[y] = x;
        if (x > _rowHi[y]) _rowHi[y] = x;
    }

    static void Report(IMemory m)
    {
        if (!_measure) return;

        // The direct measurement of the cull, taken where the game itself would
        // read it: how much of the 24x24 window this frame's cone lit. A primitive
        // count answers the same question through the scene the camera happens to
        // be pointed at; this one does not.
        for (uint i = 0; i < Span * Span; i++)
            if (m.ReadU8(Grid + i) != 0) _lit++;

        double now = Now;
        if (now - _windowStart < 2.0) return;

        Console.WriteLine($"[cullcone] x{Factor:0.###}: {(_frames > 0 ? _lit / (double)_frames : 0):0.0} " +
                          $"of {Span * Span} tiles lit, {_clippedFrames}/{_frames} frames reached the " +
                          $"grid edge, {_recovered} tiles recovered over {_clippedRows} rows the " +
                          "game's own fill dropped");

        if (_rings && _ringFrames > 0)
        {
            var by = string.Join(" ", _ring.Select((v, r) => $"{r}:{v / (double)_ringFrames:0.0}"));
            Console.WriteLine($"[cullcone] after occlusion, lit per ring from the middle: {by}");
            Array.Clear(_ring);
            _ringFrames = 0;
        }

        _windowStart = now;
        _frames = _clippedFrames = _clippedRows = _recovered = _lit = 0;
    }
}
