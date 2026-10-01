using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Step 4's census: what 3D the game's own code still builds while the GPU world
/// renderer is on, and where from.
///
///     KF2_GPUWORLD_CENSUS=1   a line every 2 s: calls into each transform and assembler,
///                             by context, the CPU's projections and the 3D packets drawn,
///                             in stage 13 and outside it; the first caller seen outside
///                             stage 13 for each routine, once
///
/// A context is where the call came from: the object or tile walk of the main view,
/// the mirror's walk or replay, the arm, the rest of stage 13, or outside stage 13
/// (the modal loops that draw without it). Frames are VSync calls. A measurement only:
/// it writes nothing. See "Step 4, the fallback census" in docs/GPU_RENDERER.md.
/// </summary>
public static class GpuWorldCensus
{
    static readonly (uint Addr, string What)[] Routines =
    [
        (0x8002E650, "transform"),
        (0x8002E7CC, "near transform"),
        (0x8002E9B8, "view-space transform"),
        (0x8002F214, "lit assembler"),
        (0x8002EAEC, "twin assembler"),
        (0x80030540, "clipped assembler"),
        (0x8002FECC, "far map assembler"),
        (0x8002F918, "sky assembler"),
        (0x80031950, "map half"),
    ];

    const uint Stage13Addr = 0x800342D8, TileSweep = 0x80031C94;

    static readonly string[] Contexts = ["walk", "mirror", "arm", "stage 13", "outside"];

    static bool _on, _inStage13, _inSweep;
    static readonly long[,] _calls = new long[9, 5], _bytes = new long[9, 5];
    static readonly uint[] _entry = new uint[9];
    static readonly int[] _entryCtx = new int[9];

    /// <summary>This frame's primitive arena descriptor, as <see cref="DrawCensus"/> reads it.</summary>
    const uint ActiveDescriptor = 0x8017E0A4;
    static readonly bool[,] _named = new bool[9, 5];
    static long _frames, _vsyncAt, _stage13Frames;
    static long _projIn, _projOut, _pktIn, _pktOut, _projMark, _pktMark;
    static double _reportAt;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.gpuworldcensus",
        Name = "GPU world census",
        Version = "1.0",
        Description = "Counts the 3D the game's code still builds under the GPU world renderer.",
    };

    public static void Configure(string? probe) => _on = probe?.Trim() is "1";

    public static void Install()
    {
        if (!_on) return;
        HookAttach.OnOverlayLoad("gpu world census", Attach);
        // Every loop flips the display, stage 13 or not, and OPEN.EXE and END.EXE too.
        RecompOne.Runtime.Events.Event.AddListener<RecompOne.Runtime.Events.DispEnvEvent>(_ => Report());
    }

    static bool _queued;
    static MethodInfo?[] _targets = [];

    static bool Attach()
    {
        SymbolRegistry.Build();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        var self = typeof(GpuWorldCensus);
        var stage = SymbolRegistry.Resolve("game", null, Stage13Addr);
        var sweep = SymbolRegistry.Resolve("game", null, TileSweep);
        if (stage == null || sweep == null) return false;
        if (!_queued)
        {
            _targets = new MethodInfo?[Routines.Length];
            HookManager.AddPre(_self, stage, self.GetMethod(nameof(BeforeStage13), flags)!);
            HookManager.AddPost(_self, stage, self.GetMethod(nameof(AfterStage13), flags)!);
            HookManager.AddPre(_self, sweep, self.GetMethod(nameof(BeforeSweep), flags)!);
            HookManager.AddPost(_self, sweep, self.GetMethod(nameof(AfterSweep), flags)!);
            for (int i = 0; i < Routines.Length; i++)
            {
                _targets[i] = SymbolRegistry.Resolve("game", null, Routines[i].Addr);
                if (_targets[i] == null) continue;
                HookManager.AddPre(_self, _targets[i]!, self.GetMethod($"Pre{i}", flags)!);
                HookManager.AddPost(_self, _targets[i]!, self.GetMethod($"Post{i}", flags)!);
            }
            _queued = true;
        }
        HookManager.Commit();
        int n = _targets.Count(HookAttach.Installed);
        Console.WriteLine($"[KF2] gpu world census: stage 13 {(HookAttach.Installed(stage) ? "hooked" : "not hooked")}, {n} of {Routines.Length} routine(s)");
        return HookAttach.Installed(stage) && n == Routines.Length;
    }

    static int Context() =>
        PlanarWalk.Mirroring || PlanarWalk.Replaying ? 1
        : ModelWalk.InArm ? 2
        : !_inStage13 ? 4
        : ModelWalk.InWalk || _inSweep ? 0
        : 3;

    static uint Cursor(IMemory m)
    {
        uint desc = m.ReadU32(ActiveDescriptor);
        return desc == 0 ? 0u : m.ReadU32(desc + 8u);
    }

    static void Done(int i, IMemory m)
    {
        uint now = Cursor(m);
        if (now > _entry[i] && _entry[i] != 0) _bytes[i, _entryCtx[i]] += now - _entry[i];
    }

    static void Note(int i, IMemory m)
    {
        int ctx = Context();
        _calls[i, ctx]++;
        _entry[i] = Cursor(m);
        _entryCtx[i] = ctx;
        if (ctx >= 3 && !_named[i, ctx])
        {
            _named[i, ctx] = true;
            var frames = new System.Diagnostics.StackTrace().GetFrames()
                .Select(f => f.GetMethod())
                .Where(mb => mb != null && (mb.Name.StartsWith("func_") || mb.DeclaringType?.Namespace == "Kf2")
                             && mb.DeclaringType != typeof(GpuWorldCensus))
                .Select(mb => mb!.Name.StartsWith("func_") ? mb.Name : $"{mb.DeclaringType!.Name}.{mb.Name}")
                .Take(10);
            Console.WriteLine($"[KF2] gpu world census: {Routines[i].What} called {Contexts[ctx]} from {string.Join(" < ", frames)}");
        }
        Report();
    }

    // The CPU's projections and the 3D packets drawn, charged to stage 13 or not.
    static void Mark(bool entering)
    {
        long proj = GteDepth.Recorded, pkt = GtePacketDepth.Hits;
        long dp = proj >= _projMark ? proj - _projMark : proj, dk = pkt >= _pktMark ? pkt - _pktMark : pkt;
        if (entering) { _projOut += dp; _pktOut += dk; }
        else { _projIn += dp; _pktIn += dk; }
        _projMark = proj; _pktMark = pkt;
    }

    public static void BeforeStage13(CpuContext c, IMemory m)
    {
        Mark(true);
        _inStage13 = true;
        Report();
    }

    public static void BeforeSweep(CpuContext c, IMemory m) => _inSweep = true;
    public static void AfterSweep(CpuContext c, IMemory m) => _inSweep = false;

    public static void AfterStage13(CpuContext c, IMemory m)
    {
        Mark(false);
        _inStage13 = false;
        _stage13Frames++;
    }

    static void Report()
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now < _reportAt) return;
        // Charge what has run since the last mark to where we are now: outside stage 13,
        // a modal loop may never enter it.
        Mark(!_inStage13);
        bool first = _reportAt == 0;
        _reportAt = now + 2.0;
        long v = RecompOne.Runtime.Sdk.LibEtc.VSyncCalls;
        _frames = v - _vsyncAt;
        _vsyncAt = v;
        if (first) { Array.Clear(_calls); _projIn = _projOut = _pktIn = _pktOut = _stage13Frames = 0; return; }
        double f = Math.Max(_frames, 1);
        Console.WriteLine($"[KF2] gpu world census: {(GpuWorld.Active ? "renderer active" : "renderer standing down")}" +
                          $", {_frames} VSync(s), {_stage13Frames} stage 13 frame(s); a VSync: projections " +
                          $"{_projIn / f:F1} in stage 13, {_projOut / f:F1} outside; 3D packets {_pktIn / f:F1} in, {_pktOut / f:F1} outside");
        for (int i = 0; i < Routines.Length; i++)
        {
            var parts = new List<string>();
            for (int k = 0; k < Contexts.Length; k++)
                if (_calls[i, k] != 0) parts.Add($"{Contexts[k]} {_calls[i, k] / f:F2} ({_bytes[i, k] / f:F0} B)");
            if (parts.Count != 0)
                Console.WriteLine($"[KF2] gpu world census:   {Routines[i].What,-22} {string.Join(", ", parts)}");
        }
        Array.Clear(_calls);
        Array.Clear(_bytes);
        _projIn = _projOut = _pktIn = _pktOut = _stage13Frames = 0;
    }

    public static void Pre0(CpuContext c, IMemory m) => Note(0, m);
    public static void Post0(CpuContext c, IMemory m) => Done(0, m);
    public static void Pre1(CpuContext c, IMemory m) => Note(1, m);
    public static void Post1(CpuContext c, IMemory m) => Done(1, m);
    public static void Pre2(CpuContext c, IMemory m) => Note(2, m);
    public static void Post2(CpuContext c, IMemory m) => Done(2, m);
    public static void Pre3(CpuContext c, IMemory m) => Note(3, m);
    public static void Post3(CpuContext c, IMemory m) => Done(3, m);
    public static void Pre4(CpuContext c, IMemory m) => Note(4, m);
    public static void Post4(CpuContext c, IMemory m) => Done(4, m);
    public static void Pre5(CpuContext c, IMemory m) => Note(5, m);
    public static void Post5(CpuContext c, IMemory m) => Done(5, m);
    public static void Pre6(CpuContext c, IMemory m) => Note(6, m);
    public static void Post6(CpuContext c, IMemory m) => Done(6, m);
    public static void Pre7(CpuContext c, IMemory m) => Note(7, m);
    public static void Post7(CpuContext c, IMemory m) => Done(7, m);
    public static void Pre8(CpuContext c, IMemory m) => Note(8, m);
    public static void Post8(CpuContext c, IMemory m) => Done(8, m);
}
