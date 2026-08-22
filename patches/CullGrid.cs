using System.Reflection;
using System.Runtime.InteropServices;
using Recompiled;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// A 32×32 rebuild of the tile-visibility pipeline the game runs at 24×24, so
/// the widened cone stops being truncated by a window that fits the shipped
/// 4:3 cone exactly.
///
///     KF2_CULLGRID=shadow        run our build after the stock one, change nothing
///     KF2_CULLGRID=on            replace the build and both queries with ours
///     KF2_CULLGRID_COMPARE=1     in shadow, diff our grid against the stock one
///
/// ## Why the 24×24 window has to go
///
/// <see cref="CullCone"/> widens the cone, and its <c>AfterFill</c> clamps every
/// recovered row to the 24-column grid, so the lit set is cone ∩ grid. The grid
/// spans ±12 tiles from its centre — the camera plus the cone's five-tile push —
/// and the widened cone's sides need 0.727·f·(z+1.875) tiles of lateral room,
/// which reaches the boundary at z≈10.5 at 16:9 and z≈7.4 at 21:9, depending on
/// yaw. Worst-yaw the 16:9 far corner is 13.2 tiles out: past the window at any
/// factor. The cut is a straight line in world space, which is what the screen
/// edges showed. Widening the cone further (the doc's old elimination test) only
/// steepens the sides and hits the same boundary closer in — the test never
/// exercised an untruncated cone.
///
/// ## What is replaced
///
/// The build (<c>func_8002D3A8</c>) and the two queries (<c>func_80032D78</c>,
/// <c>func_80032DE8</c>) are the whole pipeline: the build is their only caller's
/// only writer, and nothing else reads the array raw except
/// <c>func_80031C94</c>'s world walk, which keeps reading the legacy 24×24 array
/// — we crop our central 24×24 into it every frame. The stride-24 immediates in
/// the nine routines we do not touch are never reached.
///
/// The port is a faithful transcription of the generated build, cell for cell:
/// the same seven-pair lerp of the LIVE cone table (which <see cref="CullCone"/>
/// has already widened), the same rotation by the game's own fixed-point trig
/// (invoked, not reimplemented), the same four Bresenham edges, the same
/// two-sided scanline fill, the same ring-marching occlusion flood out of the
/// window middle, the same force-lit 3×3 at the middle — at stride 32, window
/// centre 16 instead of 12, so stock cell (i,j) corresponds to ours (i+4,j+4).
/// The near-camera rescue is shared with <see cref="CullCone"/>.
///
/// ## The differential oracle
///
/// Shadow mode runs our build after the stock one each frame and, with
/// KF2_CULLGRID_COMPARE=1, diffs all 576 central pairs. At a widening factor of
/// exactly 1 the two pipelines see the same cone, so the shipped implementation
/// is the reference and the acceptance gate is zero mismatches across a whole
/// attract session — the oracle the docs said did not exist. Rescue-class cells
/// (inside the force-lit discs, which only exist while the picture is wide) are
/// counted separately so the expected deltas do not mask real ones. At the
/// aspect's own factor the grids legitimately differ — stock truncates, ours
/// does not — so there the compare is informational and the mismatch direction
/// must be one-sided: ours lit where stock is dark, never the reverse inside the
/// stock window.
/// </summary>
public static class CullGrid
{
    /// <summary>func_8002D3A8, the stock build.</summary>
    const uint BuildRoutine = 0x8002D3A8;

    /// <summary>func_80032D78, the point query.</summary>
    const uint QueryPointRoutine = 0x80032D78;

    /// <summary>func_80032DE8, the box query.</summary>
    const uint QueryBoxRoutine = 0x80032DE8;

    /// <summary>func_8002D3A8's own inputs: pitch and yaw, and the camera's
    /// world position the stock build feeds func_8002B6B4.</summary>
    const uint PitchAddr = 0x80192E88;
    const uint YawAddr = 0x80192E8A;
    const uint CamWorldX = 0x80192E78;
    const uint CamWorldY = 0x80192E7C;
    const uint CamWorldZ = 0x80192E80;

    /// <summary>The grid index offsets, written as words and read as words by
    /// the queries; and the negated mirrors func_80031C94 walks the world
    /// from.</summary>
    const uint OffsetX = 0x80192E98;
    const uint OffsetZ = 0x80192E9C;
    const uint MirrorX = 0x80192EA0;
    const uint MirrorZ = 0x80192EA4;

    /// <summary>The legacy 24×24 array, cropped into every frame in on mode so
    /// func_80031C94 and any future raw reader stay correct.</summary>
    const uint Legacy = 0x80192EAC;

    /// <summary>The eye's world tile, the flood's origin, left by the build's
    /// caller; and the cone table, read live (CullCone widens it in place).</summary>
    const uint EyeTileX = 0x80192E90;
    const uint EyeTileZ = 0x80192E94;

    /// <summary>The map-record table and the u16 func_8002B6B4 leaves at
    /// 0x801D9C8E naming the sub-map, which selects the record bytes the flood
    /// tests.</summary>
    const uint MapBase = 0x801C8484;
    const uint AreaNum = 0x801D9C8E;

    /// <summary>Window size and centre. Stock: 24 and 12 — hence the +4 mapping
    /// between the two grids.</summary>
    const int Span = 32;
    const int Centre = Span / 2;
    const int LegacySpan = 24;

    enum Mode { Off, Shadow, On }
    static Mode _mode;
    static bool _compare;

    static readonly byte[] _grid = new byte[Span * Span];
    static GCHandle _pin;

    // The seven lerped table pairs, and the rotation terms.
    static readonly short[] _lerp = new short[7];

    // Flood state, the mirror of 0x801B69C0..D0 while the flood runs.
    static int _absX, _absZ;     // world tile of the cell under test (map record)
    static int _curX, _curZ;     // grid cell under test
    static int _cell;            // index into _grid
    static byte _marker;         // the fill's own bit in a cell byte (1 or 2)
    static byte _alive;          // the ray-alive bit — the OTHER one
    static uint _area, _adj;     // sub-map number, and 5 − area

    static IMemory _m = null!;

    // Per report window, shadow compare only.
    static long _frames, _mismatch, _rescued;
    static readonly List<string> _samples = new();
    static double _windowStart;

    static double Now => Environment.TickCount64 / 1000.0;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.cullgrid",
        Name = "Cull grid 32",
        Version = "1.0",
        Description = "Rebuilds the tile-visibility pipeline at 32×32 so the widened cone is not truncated.",
    };

    public static void Configure(string? mode, string? compare)
    {
        _mode = mode?.ToLowerInvariant() switch
        {
            "shadow" => Mode.Shadow,
            "on" => Mode.On,
            _ => Mode.Off,
        };
        _compare = _mode == Mode.Shadow && compare == "1";
    }

    public static void Install()
    {
        if (_mode == Mode.Off) return;

        // The grid is handed to nothing but our own replaced routines, all C# —
        // the pin is belt and braces against a future reader taking its address.
        _pin = GCHandle.Alloc(_grid, GCHandleType.Pinned);
        _windowStart = Now;

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    static void Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(CullGrid);
        int n = 0;

        if (_mode == Mode.On)
        {
            n += Replace(BuildRoutine, self.GetMethod(nameof(BuildReplace), BindingFlags.NonPublic | BindingFlags.Static)!);
            n += Replace(QueryPointRoutine, self.GetMethod(nameof(QueryPoint32), BindingFlags.NonPublic | BindingFlags.Static)!);
            n += Replace(QueryBoxRoutine, self.GetMethod(nameof(QueryBox32), BindingFlags.NonPublic | BindingFlags.Static)!);
        }
        else
        {
            var target = SymbolRegistry.Resolve("game", null, BuildRoutine);
            if (target != null && HookManager.AddPost(_self, target,
                    self.GetMethod(nameof(BuildShadow), BindingFlags.NonPublic | BindingFlags.Static)!))
                n++;
        }

        if (n == 0)
        {
            Console.Error.WriteLine("[KF2] cull grid: nothing hooked — staying out of the way");
            return;
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] cull grid: {_mode}{(_compare ? " + compare" : "")}, {n} hook(s)");
    }

    static int Replace(uint addr, MethodInfo impl)
    {
        var target = SymbolRegistry.Resolve("game", null, addr);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] cull grid: no function at game/0x{addr:X8}");
            return 0;
        }
        return HookManager.AddReplace(_self, target, impl) ? 1 : 0;
    }

    // ------------------------------------------------------------------
    // Entry points. The replacement stands in for a MIPS function, so the
    // callee-saved registers its callers hold live across the call have to
    // survive it; the generated helpers we invoke save their own.
    // ------------------------------------------------------------------

    static void BuildReplace(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        uint ra = c.RA, sp = c.SP;
        uint s0 = c.S0, s1 = c.S1, s2 = c.S2, s3 = c.S3, s4 = c.S4, s5 = c.S5, s6 = c.S6, s7 = c.S7;
        try
        {
            c.SP -= 0x70;
            Build(m, shadow: false);
        }
        finally
        {
            c.RA = ra; c.SP = sp;
            c.S0 = s0; c.S1 = s1; c.S2 = s2; c.S3 = s3; c.S4 = s4; c.S5 = s5; c.S6 = s6; c.S7 = s7;
        }
    }

    static void BuildShadow(CpuContext c, IMemory m) => Build(m, shadow: true);

    // ------------------------------------------------------------------
    // The build. A cell-for-cell port of func_8002D3A8 at stride 32; the
    // generated helpers it calls are invoked, not reimplemented.
    // ------------------------------------------------------------------

    static void Build(IMemory m, bool shadow)
    {
        _m = m;

        // Shadow compare needs the stock grid as it stood when we were called —
        // CullCone's own post-hook (rescue, census) has already run by then.
        byte[]? stock = null;
        if (shadow && _compare)
        {
            stock = new byte[LegacySpan * LegacySpan];
            for (uint i = 0; i < stock.Length; i++)
                stock[i] = m.ReadU8(Legacy + i);
        }

        // The seven (level, pitched) pairs, lerped by 0x1000 - rcos(pitch) — the
        // live table, which CullCone has already widened.
        c_.A0 = (uint)(short)m.ReadU16(PitchAddr);
        KingsField2.func_8005EC10(c_, m);
        int t = 0x1000 - (int)c_.V0;
        for (int i = 0; i < 7; i++)
        {
            short lo = (short)m.ReadU16(CullCone.Table + (uint)i * 4u);
            short hi = (short)m.ReadU16(CullCone.Table + (uint)i * 4u + 2u);
            _lerp[i] = (short)(((hi - lo) * t >> 12) + lo);
        }

        // Rotation by yaw, the game's own fixed-point trig.
        int yaw = (short)m.ReadU16(YawAddr);
        c_.A0 = (uint)yaw;
        KingsField2.func_8005EB08(c_, m);
        int sinv = (int)c_.V0;
        c_.A0 = (uint)yaw;
        KingsField2.func_8005EC10(c_, m);
        int cosv = (int)c_.V0;

        // func_8002B6B4's side effect picks the sub-map number the flood tests
        // records against; the stock build ignores its return value too.
        c_.A0 = m.ReadU32(CamWorldX);
        c_.A1 = m.ReadU32(CamWorldY);
        c_.A2 = m.ReadU32(CamWorldZ);
        KingsField2.func_8002B6B4(c_, m);
        _area = m.ReadU16(AreaNum);
        _adj = 5u - _area;
        _marker = _area != 0 ? (byte)2 : (byte)1;
        _alive = _area != 0 ? (byte)1 : (byte)2;

        // Window centre: the push rotated onto the facing direction, +16, &0xFF.
        uint eyeX = m.ReadU32(EyeTileX), eyeZ = m.ReadU32(EyeTileZ);
        uint midX = (uint)((((int)sinv * (int)_lerp[0]) >> 20) + Centre) & 0xFFu;
        uint midZ = (uint)((((int)-cosv * (int)_lerp[0]) >> 20) + Centre) & 0xFFu;

        // The words the queries map world positions by: ours name the 32-window.
        // In shadow the stock build's own stay in RAM — its queries are still
        // live and would misread ours — so everything below uses these locals.
        uint offXv = midX - eyeX, offZv = midZ - eyeZ;
        if (!shadow)
        {
            _m.WriteU32(OffsetX, offXv);
            _m.WriteU32(OffsetZ, offZv);
            _m.WriteU32(MirrorX, 0u - offXv);
            _m.WriteU32(MirrorZ, 0u - offZv);
        }

        // The trapezoid corners: each lerped (lateral, depth) pair rotated by
        // yaw and translated by the camera, in 1/4096-tile units.
        uint tx = m.ReadU32(CamWorldX) << 1;
        uint tz = m.ReadU32(CamWorldZ) << 1;
        uint c0x = Corner(_lerp[1], _lerp[3], sinv, cosv, tx);
        uint c0y = Corner(_lerp[1], _lerp[3], cosv, sinv, tz);
        uint c1x = Corner(_lerp[2], _lerp[3], sinv, cosv, tx);
        uint c1y = Corner(_lerp[2], _lerp[3], cosv, sinv, tz);
        uint c2x = Corner(_lerp[4], _lerp[6], sinv, cosv, tx);
        uint c2y = Corner(_lerp[4], _lerp[6], cosv, sinv, tz);
        uint c3x = Corner(_lerp[5], _lerp[6], sinv, cosv, tx);
        uint c3y = Corner(_lerp[5], _lerp[6], cosv, sinv, tz);
        byte mark = (byte)(_marker | 0x20);
        ushort ox = (ushort)offXv, oz = (ushort)offZv;
        Edge(c0x, c0y, c1x, c1y, mark, ox, oz);   // far
        Edge(c1x, c1y, c3x, c3y, mark, ox, oz);   // right
        Edge(c3x, c3y, c2x, c2y, mark, ox, oz);   // near
        Edge(c2x, c2y, c0x, c0y, mark, ox, oz);   // left

        Fill(mark);

        // The flood's one seed, tested against the eye's own map record.
        _absX = (int)eyeX; _absZ = (int)eyeZ;
        _curX = (int)midX; _curZ = (int)midZ;
        _cell = (int)(midZ * Span + midX);
        uint seedRec = MapBase + 4u + 800u * eyeZ + 10u * eyeX;
        Set(_cell, (m.ReadU8(seedRec) & 0x80) != 0 ? (byte)3 : _marker);

        Flood();

        // The stock epilogue: force-light the 3×3 around the middle. The game
        // band-aids the flood's eye-blindness here and nowhere else.
        Force3x3((int)midX, (int)midZ);

        // The near-camera rescue, on our grid, same discs as CullCone's.
        if (CullCone.RescueActive)
        {
            int r = CullCone.RescueRadius;
            int cx = (int)((m.ReadU32(CamWorldX) >> 11) + offXv);
            int cz = (int)((m.ReadU32(CamWorldZ) >> 11) + offZv);
            if ((uint)cx < Span && (uint)cz < Span) Disc(cx, cz, r);
            int ex = (int)(eyeX + offXv);
            int ez = (int)(eyeZ + offZv);
            if ((uint)ex < Span && (uint)ez < Span) Disc(ex, ez, r);
        }

        if (shadow)
        {
            if (stock != null) Compare(stock, offXv, offZv);
            return;
        }

        // The legacy consumer (func_80031C94) walks the raw 24×24 array: hand it
        // our central crop so what it renders stays the truth.
        for (int z = 0; z < LegacySpan; z++)
            for (int x = 0; x < LegacySpan; x++)
                m.WriteU8(Legacy + (uint)(z * LegacySpan + x), _grid[(z + 4) * Span + (x + 4)]);
    }

    // A scratch context for the pure helpers. They read A0 and write V0 and
    // touch no game state; the build's own context is not interrupted.
    static readonly CpuContext c_ = new();

    static uint Corner(short lateral, short depth, int a, int b, uint translate) =>
        (uint)((lateral * a - depth * b >> 8) + (int)translate);

    // func_8002CD0C: one Bresenham edge into the grid, bounds-checked per cell.
    static void Edge(uint x0w, uint z0w, uint x1w, uint z1w, byte mark, ushort offX, ushort offZ)
    {
        int x = (int)(((x0w >> 12) + offX) & 0xFFFF);
        int y = (int)(((z0w >> 12) + offZ) & 0xFFFF);
        int xe = (int)(((x1w >> 12) + offX) & 0xFFFF);
        int ye = (int)(((z1w >> 12) + offZ) & 0xFFFF);

        int dx = xe - x, dy = ye - y;
        int adx = dx >= 0 ? dx : -dx, ady = dy >= 0 ? dy : -dy;
        int sx = dx >= 0 ? 1 : -1, sy = dy >= 0 ? 1 : -1;

        if (adx >= ady)
        {
            int err = adx >> 1;
            for (int i = 0; i <= adx; i++)
            {
                Plot(x, y, mark);
                err -= ady;
                if (err <= 0) { y += sy; err += adx; }
                x += sx;
            }
        }
        else
        {
            int err = ady >> 1;
            for (int i = 0; i <= ady; i++)
            {
                Plot(x, y, mark);
                err -= adx;
                if (err <= 0) { x += sx; err += ady; }
                y += sy;
            }
        }
    }

    static void Plot(int x, int y, byte mark)
    {
        if ((uint)x >= Span || (uint)y >= Span) return;
        _grid[y * Span + x] = mark;
    }

    // func_8002CEA8 + func_8002CF0C: scan each row in from both sides for the
    // first run of the marker, and fill between the runs' inner ends. A row
    // whose outline left the grid has one run, the scans meet, and — exactly as
    // in the stock build — the row is not filled at all.
    static void Fill(byte mark)
    {
        for (int y = 0; y < Span; y++)
        {
            int l = Scan(y, 0, 1, mark);
            int r = Scan(y, Span - 1, -1, mark);
            if (r < l) continue;
            for (int x = l; x <= r; x++)
                _grid[y * Span + x] = mark;
        }
    }

    static int Scan(int y, int start, int step, byte mark)
    {
        int col = start;
        bool inRun = false;
        while ((uint)col < Span)
        {
            byte b = _grid[y * Span + col];
            if (!inRun)
            {
                if (b == mark) inRun = true;
            }
            else if (b != mark)
            {
                return col;
            }
            col += step;
        }
        return col;
    }

    // func_8002CFC8 + func_8002D15C: fourteen rings marching out of the middle
    // through lit cells only, eight directions a ring, each cell decided by its
    // two parents on the ring inside it and the map record at its world tile.
    static readonly (int pA, int pB, int xs, int zs, int ps, int first)[] Dir =
    {
        (-32, -31, -1,  0,  -1, -32),
        (  1, -31,  0, -1, -32, -31),
        (  1,  33,  0, -1, -32,   1),
        ( 32,  33,  1,  0,   1,  33),
        ( 32,  31,  1,  0,   1,  32),
        ( -1,  31,  0,  1,  32,  31),
        ( -1, -33,  0,  1,  32,  -1),
        (-32, -33, -1,  0,  -1, -33),
    };

    static void Flood()
    {
        for (int ring = 0; ring < 14; ring++)
        {
            _absZ++; _curZ++; _cell += Span;
            foreach (var d in Dir)
            {
                Cell(d.first, null);
                Step(d);
                for (int i = 0; i < ring; i++)
                {
                    Cell(d.pA, d.pB);
                    Step(d);
                }
            }
        }
    }

    static void Step((int pA, int pB, int xs, int zs, int ps, int first) d)
    {
        _absX += d.xs; _curX += d.xs;
        _absZ += d.zs; _curZ += d.zs;
        _cell += d.ps;
    }

    /// <summary>The one rule both flood workers apply per cell: parents pB null
    /// is func_8002CFC8's single-parent form, two parents func_8002D15C's.</summary>
    static void Cell(int pA, int? pB)
    {
        if ((uint)_curX >= Span || (uint)_curZ >= Span) return;
        if (At(_cell) == 0) return;

        if ((uint)_absX >= 80 || (uint)_absZ >= 80) { Set(_cell, 0); return; }

        bool lit = (At(_cell + pA) & _marker) != 0;
        if (!lit && pB is { } pb) lit = (At(_cell + pb) & _marker) != 0;
        if (!lit) { And(_cell, (byte)~_marker); return; }

        uint off = 800u * (uint)_absZ + 10u * (uint)_absX;
        if (_m.ReadU8(MapBase + _area + off) == 0xFF) { And(_cell, (byte)~_marker); return; }
        if ((_m.ReadU8(MapBase + _area + off + 4u) & 0x80) != 0) { Or(_cell, _alive); return; }
        if (_m.ReadU8(MapBase + _adj + off) == 0xFF) return;

        bool wall = (At(_cell + pA) & _alive) != 0;
        if (!wall && pB is { } pb2) wall = (At(_cell + pb2) & _alive) != 0;
        if (wall) Or(_cell, _alive);
    }

    // The stock epilogue's nine ORs around the middle cell: diagonals 0x80,
    // orthogonal neighbours and the middle itself 0xC0. Offsets are relative to
    // the middle and scale with the stride.
    static readonly (int off, byte val)[] Force =
    {
        (-Span - 1, 0x80), (-Span + 1, 0x80),
        (-Span, 0xC0), (-1, 0xC0), (0, 0xC0), (1, 0xC0),
        (Span - 1, 0x80), (Span + 1, 0x80), (Span, 0xC0),
    };

    static void Force3x3(int mx, int mz)
    {
        foreach (var (off, val) in Force)
            Set(mx + mz * Span + off, val);
    }

    static void Disc(int cx, int cz, int radius)
    {
        int lo = Math.Max(cx - radius, 0), hi = Math.Min(cx + radius, Span - 1);
        int zlo = Math.Max(cz - radius, 0), zhi = Math.Min(cz + radius, Span - 1);
        for (int z = zlo; z <= zhi; z++)
            for (int x = lo; x <= hi; x++)
                _grid[z * Span + x] |= 0xC0;
    }

    static byte At(int i) => (uint)i < Span * Span ? _grid[i] : (byte)0;
    static void Set(int i, byte v) { if ((uint)i < Span * Span) _grid[i] = v; }
    static void Or(int i, byte v) { if ((uint)i < Span * Span) _grid[i] |= v; }
    static void And(int i, byte v) { if ((uint)i < Span * Span) _grid[i] &= v; }

    // ------------------------------------------------------------------
    // The queries, replaced in on mode. Same arithmetic as func_80032D78 and
    // func_80032DE8, at span 32.
    // ------------------------------------------------------------------

    static void QueryPoint32(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        int z = (int)((m.ReadU32(c.A0 + 8u) >> 11) + m.ReadU32(OffsetZ));
        if ((uint)z >= Span) { c.V0 = 0; return; }
        int x = (int)((m.ReadU32(c.A0) >> 11) + m.ReadU32(OffsetX));
        if ((uint)x >= Span) { c.V0 = 0; return; }
        c.V0 = _grid[z * Span + x];
    }

    static void QueryBox32(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        uint width = c.A1 << 1;
        uint acc = 0;
        uint rowOff = ((m.ReadU32(c.A0 + 8u) >> 11) + m.ReadU32(OffsetZ) - c.A1) * Span;
        uint x = (m.ReadU32(c.A0) >> 11) + m.ReadU32(OffsetX) - c.A1;

        for (uint i = 0; i < width; i++)
        {
            if ((int)rowOff >= 0 && rowOff < Span * Span)
            {
                uint col = x;
                for (uint j = 0; j < width; j++)
                {
                    if ((int)col >= 0 && col < Span) acc |= _grid[rowOff + col];
                    col++;
                }
            }
            rowOff += Span;
        }
        c.V0 = acc & 0xFF;
    }

    // ------------------------------------------------------------------
    // The differential oracle (shadow + compare).
    // ------------------------------------------------------------------

    static void Compare(byte[] stock, uint offX, uint offZ)
    {
        _frames++;

        // Cells the rescue legitimately overrides — only ever a delta while the
        // picture is wide, and counted separately so it cannot mask real ones.
        bool rescue = CullCone.RescueActive;
        bool[] mask = null;
        if (rescue)
        {
            Array.Clear(_discMask);
            int r = CullCone.RescueRadius;
            MarkDisc(_discMask, (int)((_m.ReadU32(CamWorldX) >> 11) + offX),
                                (int)((_m.ReadU32(CamWorldZ) >> 11) + offZ), r);
            MarkDisc(_discMask, (int)(_m.ReadU32(EyeTileX) + offX),
                                (int)(_m.ReadU32(EyeTileZ) + offZ), r);
            mask = _discMask;
        }

        for (int z = 0; z < LegacySpan; z++)
            for (int x = 0; x < LegacySpan; x++)
            {
                bool ours = _grid[(z + 4) * Span + (x + 4)] != 0;
                bool theirs = stock[z * LegacySpan + x] != 0;
                if (ours == theirs) continue;
                if (mask != null && mask[(z + 4) * Span + (x + 4)]) { _rescued++; continue; }
                _mismatch++;
                if (_samples.Count < 8)
                    _samples.Add($"({x},{z}{(ours ? '+' : '-')})");
            }

        double now = Now;
        if (now - _windowStart < 2.0) return;

        Console.WriteLine($"[cullgrid] shadow x{CullCone.Factor:0.###}: {_mismatch} mismatches over " +
                          $"{_frames} frames ({_rescued} rescue-class" +
                          (_samples.Count > 0 ? $"; samples: {string.Join(" ", _samples)}" : "") + ")");
        _windowStart = now;
        _frames = _mismatch = _rescued = 0;
        _samples.Clear();
    }

    static void MarkDisc(bool[] mask, int cx, int cz, int radius)
    {
        if ((uint)cx >= Span || (uint)cz >= Span) return;
        int lo = Math.Max(cx - radius, 0), hi = Math.Min(cx + radius, Span - 1);
        int zlo = Math.Max(cz - radius, 0), zhi = Math.Min(cz + radius, Span - 1);
        for (int z = zlo; z <= zhi; z++)
            for (int x = lo; x <= hi; x++)
                mask[z * Span + x] = true;
    }

    // Reused by Compare so the per-frame oracle allocates nothing.
    static readonly bool[] _discMask = new bool[Span * Span];
}
