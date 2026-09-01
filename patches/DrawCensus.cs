using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Which routine in the renderer drew how much of the frame.
///
///     KF2_DRAWCENSUS=1    the per-routine primitive census, every two seconds
///     KF2_DRAWCENSUS=2    also name what the model submitter was asked to draw
///
/// The docs record what stage 13 (`func_800342D8`) *is* -- the renderer, the only
/// filler of the display list -- but not which of its callees draws what, and that
/// question has to be answered before anything can be said about a particular
/// thing on screen updating at the wrong rate. The first-person weapon is the case
/// this was written for: "the arm steps at the tick rate" has two completely
/// different fixes depending on whether the arm is placed in world space from the
/// player transform or anchored to the view, and guessing is not allowed.
///
/// ## How it measures
///
/// The game hands out one primitive arena a frame (`func_8002DF80`, two buffers of
/// `0x19000` bytes) and `func_80030540` bumps a `{start, end, current}` descriptor
/// through it per polygon, with `0x8017E0A4` pointing at this frame's -- exactly
/// what <see cref="PrimBuffer"/> already reads. So the bytes a routine drew are
/// the bump in `current` across it: **two hooks per routine and nothing per
/// polygon**, rather than a hook on the assembler that would have to work out who
/// called it.
///
/// Nesting is handled with a stack, so each routine is charged its **exclusive**
/// bytes as well as its total: `func_80032588` inside `func_800331B4` inside stage
/// 13 shows up three times, and only once in each column. That also makes the
/// stage-3 modal sub-loop (the in-game menu, which calls stage 13 from inside
/// itself) harmless -- it simply nests.
///
/// ## Reading it
///
/// The routines are the ones stage 13 calls, plus the drawing half of the world
/// walk. Three of them reach the polygon assembler at all -- `func_80031C94`,
/// which walks the 24x24 tile visibility grid at `0x80192EAC`; `func_800331B4`,
/// the world and object walks; and `func_80032400`, the first-person arm, which
/// draws only while a swing is running. So anything three-dimensional that is
/// neither the map nor the player's own weapon is under `func_800331B4`.
///
/// To pick one *object* out of that, difference two windows rather than reading one:
/// `KF2_SHELL`'s `press Square` swings the weapon, and equipping or unequipping
/// changes what is drawn, so the routine whose byte count moves with the weapon is
/// the routine that draws it. That needs no screenshot, which is the point.
///
/// **It answered the question it was built for and got the arm wrong**, which is
/// worth keeping here because the failure is a property of the method. The arm is
/// **not** 2D and is not drawn by the HUD builder: it is a 3D MO mesh drawn by
/// `func_80032400`, the row below. That routine returns before drawing anything
/// while the swing clock at `0x801994A4` reads `-1`, so standing in an area it
/// costs nothing, and a thirteen-tick swing barely moves it inside a two-second
/// window. The row that *did* move on pressing attack was the HUD builder's, and
/// it moved **downward** (56.9 -> 54.0 -> 52.7) -- the wrong direction for an arm
/// appearing, because what changed was the HP/MP gauges collapsing. **A
/// difference is only evidence if its sign is checked**, and a routine that draws
/// conditionally needs a window around the condition rather than a two-second
/// average. Its other answer stands: `func_80032588` is handed the *object* table
/// at `0x80177714`, not the entity records. See "What in the renderer draws what"
/// in docs/GAME_INTERNALS.md.
///
/// This is a measurement and nothing else -- it never writes to game memory. It
/// is off unless asked for, and it costs two memory reads per routine per frame
/// when it is on.
/// </summary>
public static class DrawCensus
{
    /// <summary>Points at this frame's `{start, end, current}` descriptor. The same
    /// address <see cref="PrimBuffer"/> reads; the arena is rewound once a frame by
    /// `func_8002E064`, at the head of stage 13.</summary>
    const uint ActiveDescriptor = 0x8017E0A4;

    /// <summary>A POLY_GT4, which is what the world is made of. Only used to turn
    /// bytes back into a packet count for the report.</summary>
    const int PacketBytes = 0x34;

    /// <summary>
    /// What to attribute. Stage 13's own callees in the order it calls them, then
    /// the drawing callees of the world walk, so one run gives both levels.
    /// </summary>
    static readonly (uint Addr, string What)[] Routines =
    [
        (0x800342D8, "13  renderer"),
        (0x8002E22C, "    head a"),
        (0x8002DC78, "    head b"),
        (0x8002D3A8, "    cull grid build"),
        (0x8002E064, "    OT swap + clear"),
        (0x800353AC, "    head c"),
        (0x80032400, "    first-person arm"),
        (0x80015374, "    head d"),
        (0x80031D5C, "    HUD"),
        (0x80033E78, "    overlay a"),
        (0x80031C94, "    map tiles (24x24 grid)"),
        (0x800331B4, "    world + object walks"),
        (0x80032588, "      geometry submit"),
        (0x80032AC4, "      object submit"),
        (0x8003309C, "      walk callee 3"),
        (0x80032FAC, "      walk callee 4"),
        (0x8003202C, "    overlay b"),
        (0x800320BC, "    overlay c"),
        (0x8003214C, "    screen tint"),
        (0x80032234, "    overlay d"),
        (0x8002E0FC, "    present"),
        (0x8003549C, "    tail"),
    ];

    static bool _measure;

    /// <summary>KF2_DRAWCENSUS=2: also name what `func_80032588` was asked to draw.
    /// It is the model submitter -- GTE transform, matrix, polygon assembler -- and
    /// it is handed the model at `a0` and a world position at `a2`, which it
    /// immediately subtracts the eye at `0x80192E78/7C/80` from. A call whose
    /// position is the player's own is the first-person weapon; anything else is an
    /// object in the area. That is the one question the census was written to
    /// answer, and it needs no screenshot.</summary>
    static bool _models;

    /// <summary>func_80032588's model argument, its position argument, and the
    /// three u16 the position holds. Collected for one frame a second.</summary>
    const uint EyeX = 0x80192E78, EyeY = 0x80192E7C, EyeZ = 0x80192E80;
    const uint PlayerX = 0x801994EC, PlayerY = 0x801994F0, PlayerZ = 0x801994F4;

    static double _modelsAt;
    static bool _collecting;
    static readonly List<string> _models1 = [];

    /// <summary>Bytes charged to a routine, exclusive of anything it called, and
    /// including it. Indexed the same as <see cref="Routines"/>.</summary>
    static long[] _exclusive = [], _inclusive = [], _calls = [];


    // The call stack of censused routines: which routine, what `current` was on
    // entry, and how much its children have already been charged. One thread, and
    // a routine that never returns simply leaves its frame behind -- see Reset.
    static readonly (int Slot, uint Desc, uint Entry, long Children)[] _stack =
        new (int, uint, uint, long)[32];
    static int _depth;

    static double _windowStart;
    static long _frames;

    static double Now => Environment.TickCount64 / 1000.0;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.drawcensus",
        Name = "Draw census",
        Version = "1.0",
        Description = "Attributes the frame's primitives to the routine that drew them.",
    };

    public static void Configure(string? probe)
    {
        if (string.IsNullOrWhiteSpace(probe) || probe.Equals("0", StringComparison.Ordinal)) return;
        _measure = true;
        _models = probe.Equals("2", StringComparison.Ordinal);
    }

    public static void Install()
    {
        if (!_measure) return;

        _exclusive = new long[Routines.Length];
        _inclusive = new long[Routines.Length];
        _calls = new long[Routines.Length];
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
        var self = typeof(DrawCensus);

        int n = 0;
        for (int i = 0; i < Routines.Length; i++)
        {
            var target = SymbolRegistry.Resolve("game", null, Routines[i].Addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] census: no game function at 0x{Routines[i].Addr:X8}");
                continue;
            }

            // A pre-hook is handed a CpuContext and nothing else -- HookManager
            // takes a `void|bool (CpuContext, IMemory)` MethodInfo and has no way
            // to carry state alongside it -- so which routine a hook belongs to has
            // to be baked into the hook. Hence one named pair per slot below,
            // resolved by name from the index rather than hand-wired.
            var before = self.GetMethod($"Pre{i:00}", BindingFlags.Public | BindingFlags.Static);
            var after = self.GetMethod($"Post{i:00}", BindingFlags.Public | BindingFlags.Static);
            if (before == null || after == null)
            {
                Console.Error.WriteLine($"[KF2] census: no Pre{i:00}/Post{i:00} pair; " +
                                        "the table and the hook pairs have drifted apart");
                continue;
            }

            // Post first: an orphan post is harmless (Leave returns on an empty
            // stack), while an orphan pre would push a frame nothing ever pops.
            if (HookManager.AddPost(_self, target, after) &&
                HookManager.AddPre(_self, target, before)) n++;
        }

        if (_models)
        {
            var target = SymbolRegistry.Resolve("game", null, ModelSubmit);
            var impl = self.GetMethod(nameof(BeforeModel), BindingFlags.Public | BindingFlags.Static)!;
            if (target == null || !HookManager.AddPre(_self, target, impl))
                Console.Error.WriteLine($"[KF2] census: no model submitter at 0x{ModelSubmit:X8}");
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] census: {n} of {Routines.Length} routine(s) attributed" +
                          (_models ? ", naming models" : ""));
    }

    /// <summary>This frame's arena descriptor, its base, and its bump pointer, or
    /// all zero if there is no arena yet.</summary>
    static (uint Desc, uint Start, uint Cur) Arena(IMemory m)
    {
        uint desc = m.ReadU32(ActiveDescriptor);
        if (desc == 0) return (0u, 0u, 0u);
        uint start = m.ReadU32(desc), end = m.ReadU32(desc + 4u);
        return start == 0 || end <= start ? (0u, 0u, 0u) : (desc, start, m.ReadU32(desc + 8u));
    }

    static void Enter(int slot, IMemory m)
    {
        if (_depth >= _stack.Length) { _depth++; return; }
        var a = Arena(m);
        _stack[_depth++] = (slot, a.Desc, a.Cur, 0L);
    }

    static void Leave(IMemory m)
    {
        if (_depth <= 0) return;
        if (_depth > _stack.Length) { _depth--; return; }

        var frame = _stack[--_depth];

        // Stage 13's head (func_8002E064) *swaps* the arena -- there are two of
        // them, 0x800FC99C and 0x8011599C, one per frame -- and then rewinds the
        // new one. So across stage 13 the entry and exit bump pointers are in
        // different buffers and subtracting them is meaningless: on the frames that
        // swap upwards it reads as 0x19000 too much, and on the frames that swap
        // downwards as a negative. Comparing the *descriptor* is what catches it,
        // and when it moved the honest answer is the whole frame -- `cur - start`
        // of the buffer actually drawn into. Every censused callee runs after the
        // swap, so none of them ever takes this branch.
        var (desc, start, now) = Arena(m);
        long total = now == 0 ? 0L
                   : desc != frame.Desc || now < frame.Entry ? now - start
                   : now - frame.Entry;

        _inclusive[frame.Slot] += total;
        _exclusive[frame.Slot] += Math.Max(0L, total - frame.Children);
        _calls[frame.Slot]++;


        if (_depth > 0 && _depth <= _stack.Length)
            _stack[_depth - 1].Children += total;

        // Report on the way out of the renderer, so a window never cuts a frame in
        // half. Keyed on the slot rather than on `_depth == 0`, because a censused
        // helper called from outside stage 13 also unwinds to depth 0 and would
        // otherwise be counted as a frame -- `func_80015374` does, and it inflated
        // the frame count by 2.5x.
        if (frame.Slot == 0) { _frames++; if (_models) ReportModels(m); Report(); }
    }

    /// <summary>func_80032588, the model submitter.</summary>
    const uint ModelSubmit = 0x80032588;

    public static void BeforeModel(CpuContext c, IMemory m)
    {
        if (!_collecting) return;

        uint pos = c.A2;
        int px = (short)m.ReadU16(pos), py = (short)m.ReadU16(pos + 4u), pz = (short)m.ReadU16(pos + 8u);
        _models1.Add($"model 0x{c.A0:X8} at pos 0x{pos:X8} ({px}, {py}, {pz}) rot {(short)(ushort)c.A1}");
    }

    static void ReportModels(IMemory m)
    {
        double now = Now;
        if (_collecting)
        {
            _collecting = false;
            Console.WriteLine($"[census] {_models1.Count} model(s) this frame; " +
                              $"eye ({(short)m.ReadU16(EyeX)}, {(short)m.ReadU16(EyeY)}, " +
                              $"{(short)m.ReadU16(EyeZ)}), player " +
                              $"({(int)m.ReadU32(PlayerX)}, {(int)m.ReadU32(PlayerY)}, " +
                              $"{(int)m.ReadU32(PlayerZ)})");
            foreach (var line in _models1) Console.WriteLine($"[census]   {line}");
            _models1.Clear();
            return;
        }

        if (now - _modelsAt < 1.0) return;
        _modelsAt = now;
        _collecting = true;
    }

    static void Report()
    {
        double now = Now;
        if (now - _windowStart < 2.0) return;

        double secs = now - _windowStart;
        Console.WriteLine($"[census] {_frames} frames in {secs:0.0}s " +
                          $"({_frames / secs:0.0}/s), bytes per frame:");

        for (int i = 0; i < Routines.Length; i++)
        {
            if (_calls[i] == 0) continue;
            double excl = (double)_exclusive[i] / _frames;
            double incl = (double)_inclusive[i] / _frames;
            Console.WriteLine($"[census]   {Routines[i].What,-30} " +
                              $"{excl,8:0} excl  {incl,8:0} incl  " +
                              $"({excl / PacketBytes:0.0} packets, " +
                              $"{(double)_calls[i] / _frames:0.0} call/frame)");
        }

        Array.Clear(_exclusive);
        Array.Clear(_inclusive);
        Array.Clear(_calls);
        _frames = 0;
        _windowStart = now;
    }

    // One pair per row of Routines, in the same order. Boilerplate on purpose:
    // the alternative is a DynamicMethod per slot, which buys nothing a
    // diagnostic needs and hides the mapping.
    public static void Pre00(CpuContext c, IMemory m) => Enter(0, m);
    public static void Post00(CpuContext c, IMemory m) => Leave(m);
    public static void Pre01(CpuContext c, IMemory m) => Enter(1, m);
    public static void Post01(CpuContext c, IMemory m) => Leave(m);
    public static void Pre02(CpuContext c, IMemory m) => Enter(2, m);
    public static void Post02(CpuContext c, IMemory m) => Leave(m);
    public static void Pre03(CpuContext c, IMemory m) => Enter(3, m);
    public static void Post03(CpuContext c, IMemory m) => Leave(m);
    public static void Pre04(CpuContext c, IMemory m) => Enter(4, m);
    public static void Post04(CpuContext c, IMemory m) => Leave(m);
    public static void Pre05(CpuContext c, IMemory m) => Enter(5, m);
    public static void Post05(CpuContext c, IMemory m) => Leave(m);
    public static void Pre06(CpuContext c, IMemory m) => Enter(6, m);
    public static void Post06(CpuContext c, IMemory m) => Leave(m);
    public static void Pre07(CpuContext c, IMemory m) => Enter(7, m);
    public static void Post07(CpuContext c, IMemory m) => Leave(m);
    public static void Pre08(CpuContext c, IMemory m) => Enter(8, m);
    public static void Post08(CpuContext c, IMemory m) => Leave(m);
    public static void Pre09(CpuContext c, IMemory m) => Enter(9, m);
    public static void Post09(CpuContext c, IMemory m) => Leave(m);
    public static void Pre10(CpuContext c, IMemory m) => Enter(10, m);
    public static void Post10(CpuContext c, IMemory m) => Leave(m);
    public static void Pre11(CpuContext c, IMemory m) => Enter(11, m);
    public static void Post11(CpuContext c, IMemory m) => Leave(m);
    public static void Pre12(CpuContext c, IMemory m) => Enter(12, m);
    public static void Post12(CpuContext c, IMemory m) => Leave(m);
    public static void Pre13(CpuContext c, IMemory m) => Enter(13, m);
    public static void Post13(CpuContext c, IMemory m) => Leave(m);
    public static void Pre14(CpuContext c, IMemory m) => Enter(14, m);
    public static void Post14(CpuContext c, IMemory m) => Leave(m);
    public static void Pre15(CpuContext c, IMemory m) => Enter(15, m);
    public static void Post15(CpuContext c, IMemory m) => Leave(m);
    public static void Pre16(CpuContext c, IMemory m) => Enter(16, m);
    public static void Post16(CpuContext c, IMemory m) => Leave(m);
    public static void Pre17(CpuContext c, IMemory m) => Enter(17, m);
    public static void Post17(CpuContext c, IMemory m) => Leave(m);
    public static void Pre18(CpuContext c, IMemory m) => Enter(18, m);
    public static void Post18(CpuContext c, IMemory m) => Leave(m);
    public static void Pre19(CpuContext c, IMemory m) => Enter(19, m);
    public static void Post19(CpuContext c, IMemory m) => Leave(m);
    public static void Pre20(CpuContext c, IMemory m) => Enter(20, m);
    public static void Post20(CpuContext c, IMemory m) => Leave(m);
    public static void Pre21(CpuContext c, IMemory m) => Enter(21, m);
    public static void Post21(CpuContext c, IMemory m) => Leave(m);
}
