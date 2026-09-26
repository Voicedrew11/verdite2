using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using GpuJobs = RecompOne.Runtime.Host.GpuJobs;
using GteDepth = RecompOne.Runtime.GteDepth;
using GteVertexMap = RecompOne.Runtime.GteVertexMap;
using PsxGpu = RecompOne.Runtime.Gpu;

namespace Kf2;

/// <summary>
/// One run of stage 13, captured whole: every GP0 command it sent, every call to
/// the routines it is made of, and every batch the GL backend submitted -- then
/// replayed on a detached software GPU so the frame can be scrubbed command by
/// command. <see cref="FrameViewerPanel"/> is the window (Shift+F).
///
///     KF2_FRAMEVIEW=panel         open the panel at boot
///     KF2_FRAMEVIEW_CAPTURE=20,40 capture at these seconds after boot, summary on the console
///     KF2_FRAMEVIEW_OUT=dir       also write each capture's commands as CSV there
///
/// See "Watching a frame being built" in docs/DEVELOPMENT.md.
/// </summary>
public static class FrameCapture
{
    // ---- the routines --------------------------------------------------------------

    public const int MaxSlots = 32;
    public static readonly string[] SlotName = new string[MaxSlots];
    public static readonly uint[] SlotAddr = new uint[MaxSlots];
    public static int SlotCount { get; private set; }

    static readonly MethodInfo?[] _target = new MethodInfo?[MaxSlots];
    static readonly bool[] _queued = new bool[MaxSlots];
    static readonly bool[] _hooked = new bool[MaxSlots];

    static readonly ModInfo _self = new()
    {
        Id = "kf2.framecapture",
        Name = "Frame capture",
        Version = "1.0",
        Description = "Captures one run of stage 13 and replays it command by command.",
    };

    static FrameCapture()
    {
        foreach (var (addr, what) in DrawCensus.Routines) AddSlot(addr, what.Trim());
        SlotName[0] = "stage 13: renderer";
        // FramePacing's frame cap sleeps in a post on DrawOTag, so this call's time is mostly waiting.
        AddSlot(0x80060818, "DrawOTag (+ frame cap wait)");
        AddSlot(0x8005FCC8, "VSync thunk (present, frame cap)");
    }

    static int AddSlot(uint addr, string name)
    {
        for (var i = 0; i < SlotCount; i++)
            if (SlotAddr[i] == addr) return i;
        if (SlotCount >= MaxSlots) return -1;
        SlotAddr[SlotCount] = addr;
        SlotName[SlotCount] = name;
        return SlotCount++;
    }

    public static string SlotLabel(int slot) => slot < 0 ? "(no routine)" : $"{SlotName[slot]} (func_{SlotAddr[slot]:X8})";

    /// <summary>Add a GAME.EXE function as a routine, from the panel or the environment.</summary>
    public static string AddFunction(string spec)
    {
        var hex = spec.Trim().Replace("game:", "").Replace("0x", "").Replace("func_", "");
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr))
            return $"'{spec}': not an address";
        if (SymbolRegistry.Resolve("game", null, addr) == null) return $"0x{addr:X8}: no GAME.EXE function starts there";
        var slot = AddSlot(addr, $"func_{addr:X8}");
        if (slot < 0) return $"all {MaxSlots} routine slots are taken";
        return EnsureHooks();
    }

    /// <summary>Hook every routine not yet hooked. A detour each, for the rest of the session.</summary>
    static string EnsureHooks()
    {
        var added = false;
        for (var i = 0; i < SlotCount; i++)
        {
            if (_queued[i]) continue;
            var target = SymbolRegistry.Resolve("game", null, SlotAddr[i]);
            if (target == null) continue;
            var pre = typeof(FrameCapture).GetMethod($"Pre{i:00}", BindingFlags.Public | BindingFlags.Static)!;
            var post = typeof(FrameCapture).GetMethod($"Post{i:00}", BindingFlags.Public | BindingFlags.Static)!;
            // Post first, as in DrawCensus: an orphan post is harmless, an orphan pre is not.
            if (!HookManager.AddPost(_self, target, post) || !HookManager.AddPre(_self, target, pre)) continue;
            _target[i] = target;
            _queued[i] = true;
            added = true;
        }

        if (added) HookManager.Commit();
        var n = 0;
        for (var i = 0; i < SlotCount; i++)
        {
            _hooked[i] = HookAttach.Installed(_target[i]);
            if (_hooked[i]) n++;
        }
        return $"{n} of {SlotCount} routines hooked";
    }

    // ---- configuration -------------------------------------------------------------------

    static bool _openPanel;
    static double[] _autoAt = [];
    static int _autoNext;
    static string? _outDir;
    static long _installedAt;

    public static void Configure(string? view, string? capture, string? outDir)
    {
        _openPanel = view is "panel" or "1";
        if (!string.IsNullOrWhiteSpace(capture))
            _autoAt = capture.Split([',', '+', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1)
                .Where(v => v >= 0).OrderBy(v => v).ToArray();
        _outDir = string.IsNullOrWhiteSpace(outDir) ? null : outDir;
    }

    public static void Install()
    {
        _installedAt = Environment.TickCount64;

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            RecompOne.Runtime.Host.Window.Localization.Merge("""
            {
              "strings": {
                "kf2.frameviewer": { "en": "Frame viewer", "pt-BR": "Visualizador de quadros",
                                     "es-419": "Visor de cuadros" }
              }
            }
            """);
            RecompOne.Runtime.Host.Window.PanelManager.Register(FrameViewerPanel.Instance);
            RecompOne.Runtime.Config.ConfigManager.ApplyViewToPanels([FrameViewerPanel.Instance]);
            if (_openPanel) FrameViewerPanel.Instance.IsOpen = true;
        });

        // Shift+F, in the shape of the profiler's Shift+P.
        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!e.Pressed || e.Repeat || e.Key != (int)Silk.NET.Input.Key.F) return;
            if (RecompOne.Runtime.Host.Window.PopupManager.AnyOpen || HotkeyGate.Typing) return;
            if (!RecompOne.Runtime.Host.HostWindow.IsKeyDown(Silk.NET.Input.Key.ShiftLeft) &&
                !RecompOne.Runtime.Host.HostWindow.IsKeyDown(Silk.NET.Input.Key.ShiftRight)) return;
            FrameViewerPanel.Instance.IsOpen = !FrameViewerPanel.Instance.IsOpen;
        });

        if (_autoAt.Length > 0)
            Event.AddListener<VSyncEvent>(_ =>
            {
                if (_autoNext >= _autoAt.Length || _armed || _capturing) return;
                if ((Environment.TickCount64 - _installedAt) / 1000.0 < _autoAt[_autoNext]) return;
                _autoNext++;
                Console.WriteLine($"[KF2] frame capture: {Arm()}");
            });
    }

    // ---- capture --------------------------------------------------------------------------

    static bool _armed, _capturing;
    static int _stageDepth;

    public static bool Armed => _armed;

    /// <summary>Switch the GTE vertex map off for the captured frame, so its cost is the
    /// difference from a capture with it on. That one frame draws affine, with no
    /// recovered depth.</summary>
    public static bool HoldMapOff;
    public static Capture? Latest { get; private set; }
    public static int Count { get; private set; }

    /// <summary>Capture the next run of stage 13.</summary>
    public static string Arm()
    {
        var before = _hooked.Count(h => h);
        var hooks = EnsureHooks();
        if (!_hooked[0]) return $"stage 13 is not hooked ({hooks}); it exists only once GAME.EXE is loaded";
        Warm();
        // A hook just installed compiles on its first call, inside the frame it would measure.
        _skip = _hooked.Count(h => h) != before ? 1 : 0;
        _armed = true;
        return $"armed; {hooks}" + (_skip > 0 ? "; skipping one frame for the new hooks to compile" : "");
    }

    static int _skip;
    static bool _warm;

    /// <summary>Compile the per-word path before it runs inside a capture.</summary>
    static void Warm()
    {
        if (_warm) return;
        _warm = true;
        foreach (var t in new[] { typeof(Sink), typeof(TailSink), typeof(ProfileSink) })
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            RuntimeHelpers.PrepareMethod(m.MethodHandle);
        RuntimeHelpers.PrepareMethod(typeof(VtxCounts).GetMethod(nameof(VtxCounts.Now))!.MethodHandle);
        foreach (var name in new[] { nameof(Enter), nameof(Leave), nameof(Open), nameof(Close), nameof(Append), nameof(Arena) })
            RuntimeHelpers.PrepareMethod(typeof(FrameCapture).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MethodHandle);
    }

    const uint ActiveDescriptor = 0x8017E0A4;

    struct Ev
    {
        public long T;
        public int Slot;
        public bool Enter;
        public uint Desc, Start, Cur;
        public VtxCounts Vtx;
    }

    struct SecEv
    {
        public long T;
        public int Id;
        public bool Enter;
    }

    static readonly List<Ev> _events = new(512);
    static readonly List<CapturedCmd> _cmds = new(8192);
    static readonly List<CapturedFlush> _flushes = new(256);
    static readonly List<SecEv> _secs = new(8192);
    static readonly List<CapturedWork> _works = new(16);
    const int MaxSectionEvents = 1 << 18;
    static uint[] _words = [];
    static uint[] _srcs = [];
    static bool[] _gp1 = [];
    static int _n;
    static bool _open;
    static int _truncatedAt = -1;
    static long _t0;
    static PsxGpu? _start;
    static readonly Sink _sink = new();
    static readonly TailSink _tail = new();
    static readonly ProfileSink _profileSink = new();
    static string _state = "";
    static bool _mapHeld;

    static (uint Desc, uint Start, uint Cur) Arena(IMemory m)
    {
        var desc = m.ReadU32(ActiveDescriptor);
        if (desc == 0) return (0u, 0u, 0u);
        uint start = m.ReadU32(desc), end = m.ReadU32(desc + 4u);
        return start == 0 || end <= start ? (0u, 0u, 0u) : (desc, start, m.ReadU32(desc + 8u));
    }

    static void Enter(int slot, IMemory m)
    {
        if (slot == 0)
        {
            if (_resolving != null && !_capturing) TryResolve();
            if (!_capturing)
            {
                if (!_armed) return;
                if (_skip > 0)
                {
                    _skip--;
                    return;
                }
                Begin();
                if (!_capturing) return;
            }
            _stageDepth++;
        }
        else if (!_capturing) return;

        var (desc, start, cur) = Arena(m);
        _events.Add(new Ev { T = Stopwatch.GetTimestamp(), Slot = slot, Enter = true, Desc = desc, Start = start, Cur = cur, Vtx = VtxCounts.Now() });
    }

    static void Leave(int slot, IMemory m)
    {
        if (!_capturing) return;
        var (desc, start, cur) = Arena(m);
        _events.Add(new Ev { T = Stopwatch.GetTimestamp(), Slot = slot, Enter = false, Desc = desc, Start = start, Cur = cur, Vtx = VtxCounts.Now() });
        if (slot == 0 && --_stageDepth <= 0) End();
    }

    static void Begin()
    {
        _armed = false;
        var gpu = RecompOne.Runtime.Runtime.Gpu;
        if (gpu == null) return;
        if (_resolving != null) FinishResolve(true);

        // The frame's starting state. The software VRAM holds every texture but not the
        // picture when GL is drawing, so the whole of VRAM is read back from the backend
        // first -- before the clock starts, and without offering it to 0039's restore copies.
        _start = new PsxGpu { Detached = true };
        _start.CopyStateFrom(gpu);
        if (GpuHle.Active && GpuHle.Backend is GlCore { Ready: true } gl)
        {
            var buf = _start.Vram;
            if (GpuJobs.Claimed && !GpuJobs.IsOwner)
                GpuJobs.Run(() => gl.ReadVram(0, 0, PsxGpu.VramWidth, PsxGpu.VramHeight, buf, false));
            else
                gl.ReadVram(0, 0, PsxGpu.VramWidth, PsxGpu.VramHeight, buf, false);
        }

        if (_words.Length == 0)
        {
            _words = new uint[1 << 20];
            _srcs = new uint[1 << 20];
            _gp1 = new bool[1 << 20];
        }
        _events.Clear();
        _cmds.Clear();
        _flushes.Clear();
        _secs.Clear();
        _works.Clear();
        _n = 0;
        _open = false;
        _truncatedAt = -1;
        _stageDepth = 0;
        _state = PortState();
        _mapHeld = HoldMapOff && GteVertexMap.Active;
        if (_mapHeld) GteVertexMap.SetActive(false);
        _capturing = true;
        GpuTrace.Sink = _sink;
        // Every profiler section inside the frame -- hooks, the present, the AO pass,
        // the vertex lookups -- whether or not the profiler itself is recording.
        Profiler.AdoptThread();
        Profiler.Trace = _profileSink;
        _t0 = Stopwatch.GetTimestamp();
    }

    static string PortState()
    {
        static string On(bool b) => b ? "on" : "off";
        var map = GteVertexMap.Active ? (HoldMapOff ? "held off for this capture" : "on") : "off";
        return $"perspective {On(GteDepth.Enabled)}, sub-pixel {On(GteDepth.Subpixel)}, Z-buffer {On(GteDepth.ZBuffer)}, " +
               $"AO {(GteDepth.AmbientOcclusion ? $"on ({GteDepth.AoSamples} samples)" : "off")}, " +
               $"anisotropic {(GteDepth.Anisotropy > 1 ? $"{GteDepth.Anisotropy}x" : "off")}, " +
               $"PGXP {On(RecompOne.Runtime.Pgxp.Pgxp.Enabled)}, true colour {On(GteDepth.TrueColor)}, " +
               $"render scale {GlVram.Scale}x, vertex map {map}";
    }

    static void End()
    {
        GpuTrace.Sink = null;
        Profiler.Trace = null;
        _capturing = false;
        if (_open) Close();
        var t1 = Stopwatch.GetTimestamp();
        var endVtx = VtxCounts.Now();
        var mapHeld = _mapHeld;
        if (_mapHeld)
        {
            GteVertexMap.SetActive(true);
            _mapHeld = false;
        }

        Capture cap;
        var sw = Stopwatch.StartNew();
        try
        {
            cap = Capture.Build(_start!, _t0, t1, _words, _srcs, _gp1,
                _truncatedAt >= 0 ? CollectionsMarshal.AsSpan(_cmds)[.._truncatedAt] : CollectionsMarshal.AsSpan(_cmds),
                CollectionsMarshal.AsSpan(_events).ToArray().Select(e => (e.T, e.Slot, e.Enter, e.Desc, e.Start, e.Cur, e.Vtx)).ToArray(),
                CollectionsMarshal.AsSpan(_flushes), _truncatedAt >= 0,
                CollectionsMarshal.AsSpan(_secs).ToArray().Select(e => (e.T, e.Id, e.Enter)).ToArray(),
                CollectionsMarshal.AsSpan(_works), endVtx);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] frame capture: analysis failed -- {e}");
            return;
        }

        cap.AnalyseMs = sw.Elapsed.TotalMilliseconds;
        cap.Number = ++Count;
        cap.State = _state;
        cap.MapHeldOff = mapHeld;
        cap.TimerQueries = GpuHle.Backend is GlCore { TimerQueries: true };
        Latest = cap;
        foreach (var line in cap.Report()) Console.WriteLine($"[KF2] {line}");

        // The frame's last batch is submitted, and its picture presented, at the next
        // present; the GPU answers some frames after that.
        _tail.Reset(cap);
        GpuTrace.Sink = _tail;
        _resolving = cap;
        _resolveTries = 0;
    }

    // ---- GPU times, after the frame ----------------------------------------------------

    static Capture? _resolving;
    static int _resolveTries;

    static void TryResolve()
    {
        var cap = _resolving!;
        _resolveTries++;
        if (!cap.TailDone && _resolveTries < 30) return;
        var done = false;
        OnGl(() => done = cap.ResolveGpu());
        if (!done && _resolveTries < 240) return;
        FinishResolve(false);
    }

    static void FinishResolve(bool superseded)
    {
        var cap = _resolving!;
        _resolving = null;
        if (GpuTrace.Sink == _tail) GpuTrace.Sink = null;
        OnGl(() =>
        {
            cap.ResolveGpu();
            cap.AbandonGpu();
        });
        cap.FinishGpu();
        if (superseded) return;
        foreach (var line in cap.GpuReport()) Console.WriteLine($"[KF2] {line}");
        if (_outDir == null) return;
        try
        {
            Directory.CreateDirectory(_outDir);
            var path = Path.Combine(_outDir, $"framecapture-{cap.Number}.csv");
            cap.WriteAllCsv(path);
            Console.WriteLine($"[KF2] frame capture: wrote {path}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] frame capture: could not write CSV -- {e.Message}");
        }
    }

    static void OnGl(Action work)
    {
        if (GpuJobs.Claimed && !GpuJobs.IsOwner) GpuJobs.Run(work);
        else work();
    }

    internal static void DeleteQuery(uint query)
    {
        if (query != 0 && GpuHle.Backend is GlCore gl) gl.DeleteGpuTimer(query);
    }

    static void Open(uint src)
    {
        _cmds.Add(new CapturedCmd
        {
            First = _n,
            Start = Stopwatch.GetTimestamp(),
            Src = src,
            OtEntry = GteDepth.OtEntry,
            OtSlot = GteDepth.OtSlot,
            Owner = -1,
            RunIn = -1,
        });
        _open = true;
    }

    static void Close()
    {
        ref var c = ref CollectionsMarshal.AsSpan(_cmds)[^1];
        c.End = Stopwatch.GetTimestamp();
        c.Count = _n - c.First;
        _open = false;
    }

    static void Append(uint word, uint src, bool gp1)
    {
        if (_n >= _words.Length)
        {
            if (_truncatedAt < 0) _truncatedAt = _cmds.Count - 1;
            return;
        }
        _words[_n] = word;
        _srcs[_n] = src;
        _gp1[_n] = gp1;
        _n++;
    }

    sealed class Sink : IGpuTrace
    {
        public void Word(uint word, uint src)
        {
            if (!_open) Open(src);
            Append(word, src, false);
        }

        public void Gp1(uint word)
        {
            if (_open)
            {
                Append(word, 0, true);
                return;
            }
            Open(0);
            Append(word, 0, true);
            Close();
        }

        public void Executed()
        {
            if (_open) Close();
        }

        public void Flush(FlushReason why, int verts)
        {
            _flushes.Add(new CapturedFlush
            {
                Start = Stopwatch.GetTimestamp(),
                Why = why,
                Verts = verts,
                At = _open ? _cmds.Count - 1 : _cmds.Count,
                InCmd = _open,
            });
        }

        public void Flushed()
        {
            if (_flushes.Count > 0) CollectionsMarshal.AsSpan(_flushes)[^1].End = Stopwatch.GetTimestamp();
        }

        public void Vertices(int asked, int hits)
        {
            if (!_open) return;
            ref var c = ref CollectionsMarshal.AsSpan(_cmds)[^1];
            c.VtxAsked += asked;
            c.VtxHits += hits;
        }

        public void Work(GpuWork what, long start, long end, uint query)
        {
            if (what == GpuWork.Batch)
            {
                if (_flushes.Count > 0) CollectionsMarshal.AsSpan(_flushes)[^1].Query = query;
                else DeleteQuery(query);
                return;
            }
            _works.Add(new CapturedWork { What = what, Start = start, End = end, Query = query });
        }
    }

    /// <summary>After the frame, until the next present: the frame's last batch (the
    /// first submit to come) and the present of its picture, AO pass included.</summary>
    sealed class TailSink : IGpuTrace
    {
        Capture? _cap;
        bool _inFlush, _tookFlush;

        public void Reset(Capture cap) => (_cap, _inFlush, _tookFlush) = (cap, false, false);

        public void Word(uint word, uint src) { }
        public void Gp1(uint word) { }
        public void Executed() { }
        public void Vertices(int asked, int hits) { }

        public void Flush(FlushReason why, int verts)
        {
            if (_tookFlush) return;
            _tookFlush = _inFlush = true;
            _cap!.TailFlush = new CapturedFlush { Start = Stopwatch.GetTimestamp(), Why = why, Verts = verts, At = _cap.Cmds.Length };
            _cap.HasTailFlush = true;
        }

        public void Flushed()
        {
            if (!_inFlush) return;
            _inFlush = false;
            _cap!.TailFlush.End = Stopwatch.GetTimestamp();
        }

        public void Work(GpuWork what, long start, long end, uint query)
        {
            if (what == GpuWork.Batch)
            {
                if (_inFlush) _cap!.TailFlush.Query = query;
                else DeleteQuery(query);
                return;
            }
            _cap!.TailWorks.Add(new CapturedWork { What = what, Start = start, End = end, Query = query });
            if (what != GpuWork.Present) return;
            _cap.TailDone = true;
            if (GpuTrace.Sink == this) GpuTrace.Sink = null;
        }
    }

    sealed class ProfileSink : IProfileTrace
    {
        public void Enter(int id, long t)
        {
            if (_secs.Count < MaxSectionEvents) _secs.Add(new SecEv { T = t, Id = id, Enter = true });
        }

        public void Leave(int id, long t)
        {
            if (_secs.Count < MaxSectionEvents) _secs.Add(new SecEv { T = t, Id = id, Enter = false });
        }
    }

    // One pair per slot. Boilerplate on purpose, as in DrawCensus: a pre-hook is
    // handed no hint of which function it is on.
    public static void Pre00(CpuContext c, IMemory m) => Enter(0, m);
    public static void Post00(CpuContext c, IMemory m) => Leave(0, m);
    public static void Pre01(CpuContext c, IMemory m) => Enter(1, m);
    public static void Post01(CpuContext c, IMemory m) => Leave(1, m);
    public static void Pre02(CpuContext c, IMemory m) => Enter(2, m);
    public static void Post02(CpuContext c, IMemory m) => Leave(2, m);
    public static void Pre03(CpuContext c, IMemory m) => Enter(3, m);
    public static void Post03(CpuContext c, IMemory m) => Leave(3, m);
    public static void Pre04(CpuContext c, IMemory m) => Enter(4, m);
    public static void Post04(CpuContext c, IMemory m) => Leave(4, m);
    public static void Pre05(CpuContext c, IMemory m) => Enter(5, m);
    public static void Post05(CpuContext c, IMemory m) => Leave(5, m);
    public static void Pre06(CpuContext c, IMemory m) => Enter(6, m);
    public static void Post06(CpuContext c, IMemory m) => Leave(6, m);
    public static void Pre07(CpuContext c, IMemory m) => Enter(7, m);
    public static void Post07(CpuContext c, IMemory m) => Leave(7, m);
    public static void Pre08(CpuContext c, IMemory m) => Enter(8, m);
    public static void Post08(CpuContext c, IMemory m) => Leave(8, m);
    public static void Pre09(CpuContext c, IMemory m) => Enter(9, m);
    public static void Post09(CpuContext c, IMemory m) => Leave(9, m);
    public static void Pre10(CpuContext c, IMemory m) => Enter(10, m);
    public static void Post10(CpuContext c, IMemory m) => Leave(10, m);
    public static void Pre11(CpuContext c, IMemory m) => Enter(11, m);
    public static void Post11(CpuContext c, IMemory m) => Leave(11, m);
    public static void Pre12(CpuContext c, IMemory m) => Enter(12, m);
    public static void Post12(CpuContext c, IMemory m) => Leave(12, m);
    public static void Pre13(CpuContext c, IMemory m) => Enter(13, m);
    public static void Post13(CpuContext c, IMemory m) => Leave(13, m);
    public static void Pre14(CpuContext c, IMemory m) => Enter(14, m);
    public static void Post14(CpuContext c, IMemory m) => Leave(14, m);
    public static void Pre15(CpuContext c, IMemory m) => Enter(15, m);
    public static void Post15(CpuContext c, IMemory m) => Leave(15, m);
    public static void Pre16(CpuContext c, IMemory m) => Enter(16, m);
    public static void Post16(CpuContext c, IMemory m) => Leave(16, m);
    public static void Pre17(CpuContext c, IMemory m) => Enter(17, m);
    public static void Post17(CpuContext c, IMemory m) => Leave(17, m);
    public static void Pre18(CpuContext c, IMemory m) => Enter(18, m);
    public static void Post18(CpuContext c, IMemory m) => Leave(18, m);
    public static void Pre19(CpuContext c, IMemory m) => Enter(19, m);
    public static void Post19(CpuContext c, IMemory m) => Leave(19, m);
    public static void Pre20(CpuContext c, IMemory m) => Enter(20, m);
    public static void Post20(CpuContext c, IMemory m) => Leave(20, m);
    public static void Pre21(CpuContext c, IMemory m) => Enter(21, m);
    public static void Post21(CpuContext c, IMemory m) => Leave(21, m);
    public static void Pre22(CpuContext c, IMemory m) => Enter(22, m);
    public static void Post22(CpuContext c, IMemory m) => Leave(22, m);
    public static void Pre23(CpuContext c, IMemory m) => Enter(23, m);
    public static void Post23(CpuContext c, IMemory m) => Leave(23, m);
    public static void Pre24(CpuContext c, IMemory m) => Enter(24, m);
    public static void Post24(CpuContext c, IMemory m) => Leave(24, m);
    public static void Pre25(CpuContext c, IMemory m) => Enter(25, m);
    public static void Post25(CpuContext c, IMemory m) => Leave(25, m);
    public static void Pre26(CpuContext c, IMemory m) => Enter(26, m);
    public static void Post26(CpuContext c, IMemory m) => Leave(26, m);
    public static void Pre27(CpuContext c, IMemory m) => Enter(27, m);
    public static void Post27(CpuContext c, IMemory m) => Leave(27, m);
    public static void Pre28(CpuContext c, IMemory m) => Enter(28, m);
    public static void Post28(CpuContext c, IMemory m) => Leave(28, m);
    public static void Pre29(CpuContext c, IMemory m) => Enter(29, m);
    public static void Post29(CpuContext c, IMemory m) => Leave(29, m);
    public static void Pre30(CpuContext c, IMemory m) => Enter(30, m);
    public static void Post30(CpuContext c, IMemory m) => Leave(30, m);
    public static void Pre31(CpuContext c, IMemory m) => Enter(31, m);
    public static void Post31(CpuContext c, IMemory m) => Leave(31, m);
}

public enum CmdKind : byte { Misc, Env, Fill, Poly, Line, Sprite, Copy, Upload, Readback, Gp1 }

public struct CapturedCmd
{
    public int First, Count;
    public long Start, End;
    /// <summary>Guest address of the first word, physical; 0 if written to GP0 directly.</summary>
    public uint Src;
    public int OtEntry, OtSlot;
    /// <summary>The call whose arena bump holds the packet, or by time when none does.</summary>
    public int Owner;
    public bool OwnerByTime;
    /// <summary>The innermost call running when it was sent.</summary>
    public int RunIn;
    public int Batch, Flushes;
    public FlushReason Reason;
    public long Frags;
    /// <summary>Vertices looked up for perspective, sub-pixel or depth, and those recovered.</summary>
    public int VtxAsked, VtxHits;
    /// <summary>Time in that lookup, inside <see cref="Ticks"/>.</summary>
    public long LookupTicks;
    /// <summary>A share of its batch's GPU time, by fragments.</summary>
    public long GpuNs;
    public bool HasGpu;
    public int OffX, OffY, ClipL, ClipT, ClipR, ClipB;
    public CmdKind Kind;
    public byte Op;
    public bool Tex, Semi, Gouraud, Quad;

    public readonly long Ticks => End - Start;
}

public struct CapturedCall
{
    public int Slot, Parent, Depth;
    public long Enter, Leave, Self;
    public uint RangeLo, RangeHi;
    public int Packets, Flushes;
    public long WalkTicks, Frags;
    /// <summary>The vertex map's work inside the call, inclusive, and without its children.</summary>
    public VtxCounts Vtx, SelfVtx;
    public int VtxAsked, VtxHits;
    public long GpuNs;
    public readonly long Incl => Leave - Enter;
}

/// <summary>A profiler section that ran inside the capture: a hook, the present, a GL
/// submit, the AO pass, a vertex lookup.</summary>
public struct CapturedSection
{
    public int Id, Parent, Depth;
    public long Enter, Leave, Self;
    /// <summary>The flush or backend work it timed, -1 for none.</summary>
    public int Flush, Work;
    public readonly long Incl => Leave - Enter;
}

public struct CapturedWork
{
    public GpuWork What;
    public long Start, End;
    public uint Query;
    public long GpuNs;
    public bool HasGpu;
    public readonly long Ticks => End - Start;
}

public struct SectionStat
{
    public int Id, Calls;
    public long Incl, Self, GpuNs;
    public bool HasGpu;
}

/// <summary>GteVertexMap's trace counters, and the lookups the GPU end made.</summary>
public struct VtxCounts
{
    public long Stores, Scans, Bound, Published, Republished;

    public static VtxCounts Now() => new()
    {
        Stores = GteVertexMap.Stores, Scans = GteVertexMap.TraceScans, Bound = GteVertexMap.TraceBound,
        Published = GteVertexMap.TracePublished, Republished = GteVertexMap.TraceRepublished,
    };

    /// <summary>Between two snapshots; the store count is a uint that wraps.</summary>
    public static VtxCounts Between(in VtxCounts from, in VtxCounts to) => new()
    {
        Stores = (uint)(to.Stores - from.Stores), Scans = to.Scans - from.Scans, Bound = to.Bound - from.Bound,
        Published = to.Published - from.Published, Republished = to.Republished - from.Republished,
    };

    public static VtxCounts operator +(VtxCounts a, VtxCounts b) => new()
    {
        Stores = a.Stores + b.Stores, Scans = a.Scans + b.Scans, Bound = a.Bound + b.Bound,
        Published = a.Published + b.Published, Republished = a.Republished + b.Republished,
    };

    public static VtxCounts operator -(VtxCounts a, VtxCounts b) => new()
    {
        Stores = a.Stores - b.Stores, Scans = a.Scans - b.Scans, Bound = a.Bound - b.Bound,
        Published = a.Published - b.Published, Republished = a.Republished - b.Republished,
    };
}

public struct CapturedFlush
{
    public long Start, End;
    public FlushReason Why;
    public int Verts;
    /// <summary>The command it happened inside (InCmd), or the next one to run.</summary>
    public int At;
    public bool InCmd;
    public uint Query;
    public long GpuNs;
    public bool HasGpu;
}

public struct SlotStat
{
    public int Calls, Packets, Polys, Flushes;
    public long Incl, Self, WalkTicks, Frags;
    public VtxCounts Vtx;
    public int VtxAsked, VtxHits;
    public long LookupTicks, GpuNs;
}

/// <summary>A finished capture, and a replay of it.</summary>
public sealed class Capture
{
    public static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    public int Number;
    /// <summary>What the capture cost after the frame: the analysis, which is one full replay.</summary>
    public double AnalyseMs;
    public DateTime When = DateTime.Now;
    public long T0, T1;
    public uint[] Words = [];
    public bool[] Gp1 = [];
    public CapturedCmd[] Cmds = [];
    public CapturedCall[] Calls = [];
    public CapturedFlush[] Flushes = [];
    public PsxGpu Start = null!;
    public bool Truncated;

    public string State = "";
    public bool MapHeldOff, TimerQueries;
    public CapturedSection[] Sections = [];
    public SectionStat[] SectionStats = [];
    /// <summary>Backend work inside the frame -- the present there is the previous frame's.</summary>
    public CapturedWork[] Works = [];
    /// <summary>After the frame: its last batch, and the present of its own picture.</summary>
    public CapturedFlush TailFlush;
    public bool HasTailFlush, TailDone;
    public readonly List<CapturedWork> TailWorks = new();
    public bool GpuFinal;
    public VtxCounts StageVtx;
    public int VtxAsked, VtxHits;
    public long LookupTicks, BatchGpuNs;

    public int ViewL, ViewT, ViewW = 320, ViewH = 240;
    public long ViewFrags;
    public int Covered, MaxOverdraw;
    public readonly int[] OverdrawHistogram = new int[9];
    public int Polys, Tris, Sprites, Lines, Fills, EnvCmds, Uploads, Copies, Owned, Attributable;
    public long WalkTicks;
    public readonly long[] ByReason = new long[Enum.GetValues<FlushReason>().Length];
    /// <summary>Indexed by slot; the last entry is commands no routine owns.</summary>
    public SlotStat[] Slots = [];

    public double Ms => (T1 - T0) * TicksToMs;
    public double RelMs(long t) => (t - T0) * TicksToMs;

    internal static Capture Build(PsxGpu start, long t0, long t1, uint[] words, uint[] srcs, bool[] gp1,
                                  ReadOnlySpan<CapturedCmd> cmds,
                                  (long T, int Slot, bool Enter, uint Desc, uint Start, uint Cur, VtxCounts Vtx)[] events,
                                  ReadOnlySpan<CapturedFlush> flushes, bool truncated,
                                  (long T, int Id, bool Enter)[] sections, ReadOnlySpan<CapturedWork> works, VtxCounts endVtx)
    {
        var cap = new Capture { Start = start, T0 = t0, T1 = t1, Truncated = truncated };
        var used = cmds.Length == 0 ? 0 : cmds[^1].First + cmds[^1].Count;
        cap.Words = words.AsSpan(0, used).ToArray();
        cap.Gp1 = gp1.AsSpan(0, used).ToArray();
        cap.Cmds = cmds.ToArray();
        cap.Flushes = flushes.ToArray();
        cap.Works = works.ToArray();

        // Calls, from the enter/leave events. A leave pops back to its own enter; an
        // enter never left is closed at the end.
        var calls = new List<CapturedCall>();
        var stack = new List<(int Call, uint Desc, uint Cur, VtxCounts Vtx)>();
        foreach (var e in events)
        {
            if (e.Enter)
            {
                calls.Add(new CapturedCall
                {
                    Slot = e.Slot, Parent = stack.Count > 0 ? stack[^1].Call : -1, Depth = stack.Count,
                    Enter = e.T, Leave = t1, Vtx = VtxCounts.Between(e.Vtx, endVtx),
                });
                stack.Add((calls.Count - 1, e.Desc, e.Cur, e.Vtx));
                continue;
            }

            var at = stack.FindLastIndex(s => calls[s.Call].Slot == e.Slot);
            if (at < 0) continue;
            var (idx, desc, cur, vtx) = stack[at];
            stack.RemoveRange(at, stack.Count - at);
            var call = calls[idx];
            call.Leave = e.T;
            call.Vtx = VtxCounts.Between(vtx, e.Vtx);
            // Stage 13's head swaps the arena, so a call the swap happened inside drew
            // from the start of the buffer it ended in.
            if (e.Desc != 0)
            {
                var lo = desc != e.Desc || e.Cur < cur ? e.Start : cur;
                (call.RangeLo, call.RangeHi) = (lo & (RecompOne.Runtime.Runtime.RamSize - 1u), e.Cur & (RecompOne.Runtime.Runtime.RamSize - 1u));
            }
            calls[idx] = call;
        }
        cap.Calls = calls.ToArray();
        for (var i = 0; i < cap.Calls.Length; i++) (cap.Calls[i].Self, cap.Calls[i].SelfVtx) = (cap.Calls[i].Incl, cap.Calls[i].Vtx);
        for (var i = 0; i < cap.Calls.Length; i++)
            if (cap.Calls[i].Parent is var p and >= 0)
            {
                cap.Calls[p].Self -= cap.Calls[i].Incl;
                cap.Calls[p].SelfVtx -= cap.Calls[i].Vtx;
            }
        for (var i = 0; i < cap.Calls.Length; i++)
            if (cap.Calls[i].Slot == 0)
            {
                cap.StageVtx = cap.Calls[i].Vtx;
                break;
            }

        cap.BuildSections(sections, t1);

        for (var i = 0; i < cap.Cmds.Length; i++)
        {
            ref var c = ref cap.Cmds[i];
            Decode(ref c, cap.Words[c.First], cap.Gp1[c.First]);
            c.Src &= (RecompOne.Runtime.Runtime.RamSize - 1u);

            // By time: the deepest call open when the command was sent.
            for (var k = 0; k < cap.Calls.Length; k++)
                if (cap.Calls[k].Enter <= c.Start && c.Start < cap.Calls[k].Leave &&
                    (c.RunIn < 0 || cap.Calls[k].Depth > cap.Calls[c.RunIn].Depth))
                    c.RunIn = k;

            // By place: the smallest arena bump the packet header sits in.
            if (c.Src != 0)
            {
                var header = c.Src - 4;
                var best = -1;
                for (var k = 0; k < cap.Calls.Length; k++)
                {
                    ref var call = ref cap.Calls[k];
                    if (call.RangeHi <= call.RangeLo || header < call.RangeLo || header >= call.RangeHi) continue;
                    // Smallest wins; a callee that drew everything its caller did ties, and the deeper one did the drawing.
                    var size = call.RangeHi - call.RangeLo;
                    var bestSize = best < 0 ? uint.MaxValue : cap.Calls[best].RangeHi - cap.Calls[best].RangeLo;
                    if (size < bestSize || (size == bestSize && call.Depth > cap.Calls[best].Depth))
                        best = k;
                }
                c.Owner = best;
                if (c.Kind is CmdKind.Poly or CmdKind.Sprite or CmdKind.Line) cap.Attributable++;
                if (best >= 0 && c.Kind is CmdKind.Poly or CmdKind.Sprite or CmdKind.Line) cap.Owned++;
            }
            if (c.Owner < 0)
            {
                c.Owner = c.RunIn;
                c.OwnerByTime = true;
            }
        }

        // Batches: a submit inside command i happened before i's vertices joined, so i
        // starts the next batch; one between commands delimits at the next command.
        var f = 0;
        var batch = 0;
        for (var i = 0; i < cap.Cmds.Length; i++)
        {
            ref var c = ref cap.Cmds[i];
            while (f < cap.Flushes.Length && cap.Flushes[f].At <= i)
            {
                if (cap.Flushes[f].InCmd && cap.Flushes[f].At == i)
                {
                    if (c.Flushes++ == 0) c.Reason = cap.Flushes[f].Why;
                }
                batch++;
                f++;
            }
            c.Batch = batch;
        }
        foreach (var fl in cap.Flushes) cap.ByReason[(int)fl.Why]++;

        cap.Analyse();
        return cap;
    }

    /// <summary>Sections from the profiler's enter and leave, each tied to the flush or
    /// backend work it timed, and each vertex lookup charged to its command.</summary>
    void BuildSections((long T, int Id, bool Enter)[] events, long t1)
    {
        var secs = new List<CapturedSection>();
        var stack = new List<int>();
        foreach (var e in events)
        {
            if (e.Enter)
            {
                secs.Add(new CapturedSection
                {
                    Id = e.Id, Parent = stack.Count > 0 ? stack[^1] : -1, Depth = stack.Count,
                    Enter = e.T, Leave = t1, Flush = -1, Work = -1,
                });
                stack.Add(secs.Count - 1);
                continue;
            }
            var at = stack.FindLastIndex(k => secs[k].Id == e.Id);
            if (at < 0) continue;
            for (var k = at; k < stack.Count; k++)
            {
                var sec = secs[stack[k]];
                sec.Leave = e.T;
                secs[stack[k]] = sec;
            }
            stack.RemoveRange(at, stack.Count - at);
        }
        Sections = secs.ToArray();
        for (var i = 0; i < Sections.Length; i++) Sections[i].Self = Sections[i].Incl;
        for (var i = 0; i < Sections.Length; i++)
            if (Sections[i].Parent >= 0) Sections[Sections[i].Parent].Self -= Sections[i].Incl;

        var f = 0;
        for (var i = 0; i < Sections.Length; i++)
        {
            ref var sec = ref Sections[i];
            if (sec.Id == Profiler.GlFlush)
            {
                while (f < Flushes.Length && Flushes[f].End != 0 && Flushes[f].End < sec.Enter) f++;
                if (f < Flushes.Length && Flushes[f].Start <= sec.Enter) sec.Flush = f++;
            }
            var what = sec.Id == Profiler.Ao ? GpuWork.AmbientOcclusion
                : sec.Id == Profiler.Ssr ? GpuWork.Reflections
                : sec.Id == Profiler.Composite ? GpuWork.Composite
                : sec.Id == Profiler.Display ? GpuWork.Present : (GpuWork)255;
            if ((int)what == 255) continue;
            for (var w = 0; w < Works.Length; w++)
                if (Works[w].What == what && Works[w].Start >= sec.Enter && Works[w].Start <= sec.Leave)
                {
                    sec.Work = w;
                    break;
                }
        }

        var byId = new Dictionary<int, SectionStat>();
        foreach (var sec in Sections)
        {
            var st = byId.GetValueOrDefault(sec.Id);
            st.Id = sec.Id;
            st.Calls++;
            st.Self += sec.Self;
            var outer = true;
            for (var p = sec.Parent; p >= 0; p = Sections[p].Parent)
                if (Sections[p].Id == sec.Id) { outer = false; break; }
            if (outer) st.Incl += sec.Incl;
            byId[sec.Id] = st;
        }
        SectionStats = byId.Values.OrderByDescending(x => x.Self).ToArray();
    }

    // ---- GPU times ----------------------------------------------------------------------

    /// <summary>Read back every timer query the GPU has answered; true once none is left.
    /// On the GL thread.</summary>
    public bool ResolveGpu()
    {
        if (GpuHle.Backend is not GlCore gl) return true;
        var left = false;
        for (var i = 0; i < Flushes.Length; i++) Take(gl, ref Flushes[i].Query, ref Flushes[i].GpuNs, ref Flushes[i].HasGpu, ref left);
        if (HasTailFlush) Take(gl, ref TailFlush.Query, ref TailFlush.GpuNs, ref TailFlush.HasGpu, ref left);
        for (var i = 0; i < Works.Length; i++) Take(gl, ref Works[i].Query, ref Works[i].GpuNs, ref Works[i].HasGpu, ref left);
        var tail = CollectionsMarshal.AsSpan(TailWorks);
        for (var i = 0; i < tail.Length; i++) Take(gl, ref tail[i].Query, ref tail[i].GpuNs, ref tail[i].HasGpu, ref left);
        return !left;

        static void Take(GlCore gl, ref uint query, ref long ns, ref bool has, ref bool left)
        {
            if (query == 0) return;
            var v = gl.GpuTimeNs(query);
            if (v < 0)
            {
                left = true;
                return;
            }
            (ns, has, query) = (v, true, 0);
        }
    }

    /// <summary>Give up on the queries still unanswered.</summary>
    public void AbandonGpu()
    {
        for (var i = 0; i < Flushes.Length; i++) Drop(ref Flushes[i].Query);
        Drop(ref TailFlush.Query);
        for (var i = 0; i < Works.Length; i++) Drop(ref Works[i].Query);
        var tail = CollectionsMarshal.AsSpan(TailWorks);
        for (var i = 0; i < tail.Length; i++) Drop(ref tail[i].Query);

        static void Drop(ref uint query)
        {
            FrameCapture.DeleteQuery(query);
            query = 0;
        }
    }

    /// <summary>Spread each batch's GPU time over its primitives by fragments -- flush k
    /// drew the commands in batch k, and the last batch is the tail's -- and total it
    /// by routine and section.</summary>
    public void FinishGpu()
    {
        var nb = Flushes.Length + 1;
        var ns = new long[nb];
        var has = new bool[nb];
        for (var k = 0; k < Flushes.Length; k++) (ns[k], has[k]) = (Flushes[k].GpuNs, Flushes[k].HasGpu);
        if (HasTailFlush) (ns[^1], has[^1]) = (TailFlush.GpuNs, TailFlush.HasGpu);

        var frags = new long[nb];
        var prims = new int[nb];
        foreach (var c in Cmds)
            if (c.Batch < nb && IsPrim(c))
            {
                frags[c.Batch] += c.Frags;
                prims[c.Batch]++;
            }

        BatchGpuNs = 0;
        for (var k = 0; k < nb; k++)
            if (has[k] && prims[k] > 0) BatchGpuNs += ns[k];
        for (var i = 0; i < Slots.Length; i++) Slots[i].GpuNs = 0;
        for (var i = 0; i < Calls.Length; i++) Calls[i].GpuNs = 0;
        for (var i = 0; i < Cmds.Length; i++)
        {
            ref var c = ref Cmds[i];
            var b = c.Batch;
            if (b >= nb || !has[b] || !IsPrim(c)) continue;
            c.GpuNs = frags[b] > 0 ? ns[b] * c.Frags / frags[b] : ns[b] / prims[b];
            c.HasGpu = true;
            Slots[c.Owner >= 0 ? Calls[c.Owner].Slot : Slots.Length - 1].GpuNs += c.GpuNs;
            if (c.Owner >= 0) Calls[c.Owner].GpuNs += c.GpuNs;
        }

        for (var i = 0; i < SectionStats.Length; i++) (SectionStats[i].GpuNs, SectionStats[i].HasGpu) = (0, false);
        foreach (var sec in Sections)
        {
            long v;
            if (sec.Flush >= 0 && Flushes[sec.Flush].HasGpu) v = Flushes[sec.Flush].GpuNs;
            else if (sec.Work >= 0 && Works[sec.Work].HasGpu) v = Works[sec.Work].GpuNs;
            else continue;
            for (var i = 0; i < SectionStats.Length; i++)
                if (SectionStats[i].Id == sec.Id)
                {
                    SectionStats[i].GpuNs += v;
                    SectionStats[i].HasGpu = true;
                }
        }
        GpuFinal = true;
    }

    public long SectionGpuNs(in CapturedSection sec) =>
        sec.Flush >= 0 && Flushes[sec.Flush].HasGpu ? Flushes[sec.Flush].GpuNs
        : sec.Work >= 0 && Works[sec.Work].HasGpu ? Works[sec.Work].GpuNs : -1;

    public static bool IsPrim(in CapturedCmd c) => c.Kind is CmdKind.Poly or CmdKind.Sprite or CmdKind.Line;

    public static string SectionName(int id) => id >= 0 && id < Profiler.SectionCount ? Profiler.DisplayName(id) : $"section {id}";

    public static string WorkName(GpuWork w) => w switch
    {
        GpuWork.AmbientOcclusion => "AO pass",
        GpuWork.Reflections => "reflection pass",
        GpuWork.Composite => "composite (present blit + post-fx)",
        GpuWork.Present => "whole present",
        _ => "batch",
    };

    static void Decode(ref CapturedCmd c, uint w0, bool gp1)
    {
        var op = w0 >> 24;
        c.Op = (byte)op;
        if (gp1)
        {
            c.Kind = CmdKind.Gp1;
            return;
        }
        c.Kind = op switch
        {
            0x02 => CmdKind.Fill,
            >= 0x20 and <= 0x3F => CmdKind.Poly,
            >= 0x40 and <= 0x5F => CmdKind.Line,
            >= 0x60 and <= 0x7F => CmdKind.Sprite,
            >= 0x80 and <= 0x9F => CmdKind.Copy,
            >= 0xA0 and <= 0xBF => CmdKind.Upload,
            >= 0xC0 and <= 0xDF => CmdKind.Readback,
            >= 0xE1 and <= 0xE6 => CmdKind.Env,
            _ => CmdKind.Misc,
        };
        var prim = c.Kind is CmdKind.Poly or CmdKind.Sprite or CmdKind.Line;
        c.Tex = prim && c.Kind != CmdKind.Line && (w0 & (1u << 26)) != 0;
        c.Semi = prim && (w0 & (1u << 25)) != 0;
        c.Gouraud = c.Kind is CmdKind.Poly or CmdKind.Line && (w0 & (1u << 28)) != 0;
        c.Quad = c.Kind == CmdKind.Poly && (w0 & (1u << 27)) != 0;
    }

    /// <summary>One full replay: fragments per command, the draw state it ran under,
    /// the view, overdraw, and the per-routine totals.</summary>
    void Analyse()
    {
        var r = new Replayer();
        r.Reset(this, false);
        for (var i = 0; i < Cmds.Length; i++)
        {
            ref var c = ref Cmds[i];
            var g = r.Gpu;
            (c.OffX, c.OffY) = (g.DrawOffsetX, g.DrawOffsetY);
            (c.ClipL, c.ClipT, c.ClipR, c.ClipB) = (g.DrawAreaLeft, g.DrawAreaTop, g.DrawAreaRight, g.DrawAreaBottom);
            var before = g.Fragments;
            r.Step();
            c.Frags = g.Fragments - before;

            switch (c.Kind)
            {
                case CmdKind.Poly: Polys++; Tris += c.Quad ? 2 : 1; break;
                case CmdKind.Sprite: Sprites++; break;
                case CmdKind.Line: Lines++; break;
                case CmdKind.Fill: Fills++; break;
                case CmdKind.Env: EnvCmds++; break;
                case CmdKind.Upload: Uploads++; break;
                case CmdKind.Copy: Copies++; break;
            }
        }

        // The view is the clip rectangle that took the most fragments.
        var byClip = new Dictionary<(int, int, int, int), long>();
        foreach (var c in Cmds)
            if (c.Frags > 0)
                byClip[(c.ClipL, c.ClipT, c.ClipR, c.ClipB)] = byClip.GetValueOrDefault((c.ClipL, c.ClipT, c.ClipR, c.ClipB)) + c.Frags;
        if (byClip.Count > 0)
        {
            var (l, t, rr, b) = byClip.MaxBy(kv => kv.Value).Key;
            (ViewL, ViewT) = (l, t);
            ViewW = Math.Clamp(rr - l + 1, 1, PsxGpu.VramWidth - l);
            ViewH = Math.Clamp(b - t + 1, 1, PsxGpu.VramHeight - t);
        }

        for (var y = ViewT; y < ViewT + ViewH; y++)
        for (var x = ViewL; x < ViewL + ViewW; x++)
        {
            var n = r.Coverage[y * PsxGpu.VramWidth + x];
            ViewFrags += n;
            if (n > 0) Covered++;
            if (n > MaxOverdraw) MaxOverdraw = n;
            OverdrawHistogram[Math.Min(n, OverdrawHistogram.Length - 1)]++;
        }

        Slots = new SlotStat[FrameCapture.SlotCount + 1];
        foreach (var call in Calls)
        {
            ref var s = ref Slots[call.Slot];
            s.Calls++;
            s.Self += call.Self;
            // Inclusive once per nest of the same routine.
            var outer = true;
            for (var p = call.Parent; p >= 0; p = Calls[p].Parent)
                if (Calls[p].Slot == call.Slot) { outer = false; break; }
            if (outer) s.Incl += call.Incl;
        }
        // Each vertex lookup ran inside the command whose send window holds it.
        foreach (var sec in Sections)
        {
            if (sec.Id != Profiler.VertexLookup) continue;
            int lo = 0, hi = Cmds.Length - 1, at = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (Cmds[mid].Start <= sec.Enter) { at = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (at >= 0 && sec.Enter <= Cmds[at].End) Cmds[at].LookupTicks += sec.Incl;
        }
        foreach (var call in Calls) Slots[call.Slot].Vtx += call.SelfVtx;

        foreach (var c in Cmds)
        {
            WalkTicks += c.Ticks;
            VtxAsked += c.VtxAsked;
            VtxHits += c.VtxHits;
            LookupTicks += c.LookupTicks;
            ref var s = ref Slots[c.Owner >= 0 ? Calls[c.Owner].Slot : Slots.Length - 1];
            if (c.Kind is CmdKind.Poly or CmdKind.Sprite or CmdKind.Line) s.Packets++;
            if (c.Kind == CmdKind.Poly) s.Polys++;
            s.WalkTicks += c.Ticks;
            s.Frags += c.Frags;
            s.Flushes += c.Flushes;
            s.VtxAsked += c.VtxAsked;
            s.VtxHits += c.VtxHits;
            s.LookupTicks += c.LookupTicks;
            if (c.Owner >= 0)
            {
                ref var call = ref Calls[c.Owner];
                call.VtxAsked += c.VtxAsked;
                call.VtxHits += c.VtxHits;
                if (c.Kind is CmdKind.Poly or CmdKind.Sprite or CmdKind.Line) call.Packets++;
                call.WalkTicks += c.Ticks;
                call.Frags += c.Frags;
                call.Flushes += c.Flushes;
            }
        }
    }

    public string KindLabel(in CapturedCmd c) => c.Kind switch
    {
        CmdKind.Poly => $"{(c.Quad ? "quad" : "tri")}{(c.Tex ? " tex" : "")}{(c.Gouraud ? " gouraud" : " flat")}{(c.Semi ? " semi" : "")}",
        CmdKind.Sprite => $"sprite{(c.Tex ? " tex" : "")}{(c.Semi ? " semi" : "")}",
        CmdKind.Line => $"line{(c.Gouraud ? " gouraud" : "")}{(c.Semi ? " semi" : "")}",
        CmdKind.Env => c.Op switch
        {
            0xE1 => "env: draw mode", 0xE2 => "env: texture window", 0xE3 => "env: clip top-left",
            0xE4 => "env: clip bottom-right", 0xE5 => "env: draw offset", _ => "env: mask bits",
        },
        CmdKind.Gp1 => $"GP1({c.Op:X2})",
        CmdKind.Misc => $"GP0({c.Op:X2})",
        _ => c.Kind.ToString().ToLowerInvariant(),
    };

    public static string ReasonLabel(FlushReason r) => r switch
    {
        FlushReason.Target => "render target changed",
        FlushReason.Full => "vertex buffer full",
        FlushReason.TextureFeedback => "texture feedback (writeback)",
        FlushReason.Fill => "fill",
        FlushReason.Copy => "VRAM copy",
        FlushReason.Upload => "VRAM upload",
        FlushReason.Readback => "VRAM readback",
        FlushReason.Present => "present",
        FlushReason.StateReplacement => "state: replacement texture",
        FlushReason.StateSemi => "state: semi-transparency",
        FlushReason.StateBlend => "state: blend mode",
        FlushReason.StateImage => "state: image",
        FlushReason.StateDepthMode => "state: depth mode",
        FlushReason.StateMask => "state: mask bits",
        FlushReason.StateTexWindow => "state: texture window",
        FlushReason.StateClip => "state: clip rectangle",
        _ => "other",
    };

    public string OwnerLabel(in CapturedCmd c)
    {
        if (c.Owner < 0) return "(no routine)";
        var name = FrameCapture.SlotName[Calls[c.Owner].Slot];
        return c.OwnerByTime ? $"{name} (by time)" : name;
    }

    /// <summary>Screen vertices of a primitive, draw offset applied; tris and quads in
    /// packet order, sprites and fills as four corners clockwise.</summary>
    public int Vertices(int i, Span<(int X, int Y)> v)
    {
        ref var c = ref Cmds[i];
        var w = Words.AsSpan(c.First, c.Count);
        switch (c.Kind)
        {
            case CmdKind.Poly:
            {
                int n = c.Quad ? 4 : 3, idx = 1;
                for (var k = 0; k < n; k++)
                {
                    if (c.Gouraud && k > 0) idx++;
                    if (idx >= w.Length) return k;
                    v[k] = (c.OffX + X11(w[idx]), c.OffY + Y11(w[idx]));
                    idx++;
                    if (c.Tex) idx++;
                }
                return n;
            }
            case CmdKind.Sprite:
            {
                if (w.Length < 2) return 0;
                int x = c.OffX + X11(w[1]), y = c.OffY + Y11(w[1]), idx = 2;
                if (c.Tex) idx++;
                var sz = (w[0] >> 27) & 3;
                int sw, sh;
                if (sz == 0)
                {
                    if (idx >= w.Length) return 0;
                    (sw, sh) = ((int)(w[idx] & 0xFFFF), (int)((w[idx] >> 16) & 0xFFFF));
                }
                else sw = sh = sz == 1 ? 1 : sz == 2 ? 8 : 16;
                return Corners(v, x, y, sw, sh);
            }
            case CmdKind.Line:
            {
                int n = 0, idx = 1;
                for (var k = 0; idx < w.Length && n < v.Length; k++)
                {
                    if (c.Gouraud && k > 0) idx++;
                    if (idx >= w.Length || (k >= 2 && (w[idx] & 0xF000F000u) == 0x50005000u)) break;
                    v[n++] = (c.OffX + X11(w[idx]), c.OffY + Y11(w[idx]));
                    idx++;
                    if ((w[0] & (1u << 27)) == 0 && n == 2) break;
                }
                return n;
            }
            case CmdKind.Fill:
                if (w.Length < 3) return 0;
                return Corners(v, (int)(w[1] & 0x3F0), (int)((w[1] >> 16) & 0x1FF),
                    (int)(((w[2] & 0x3FF) + 0xF) & ~0xF), (int)((w[2] >> 16) & 0x1FF));
            default:
                return 0;
        }

        static int Corners(Span<(int X, int Y)> v, int x, int y, int w, int h)
        {
            v[0] = (x, y);
            v[1] = (x + w, y);
            v[2] = (x + w, y + h);
            v[3] = (x, y + h);
            return 4;
        }
    }

    static int X11(uint w)
    {
        var x = (int)(w & 0x7FF);
        return (x & 0x400) != 0 ? x - 0x800 : x;
    }

    static int Y11(uint w)
    {
        var y = (int)((w >> 16) & 0x7FF);
        return (y & 0x400) != 0 ? y - 0x800 : y;
    }

    // ---- reporting -----------------------------------------------------------------------

    /// <summary>The lines above the tables: what was drawn, what the port did to it.</summary>
    public IEnumerable<string> Summary()
    {
        var drawOTag = SlotTotal("DrawOTag (+ frame cap wait)");
        var cmdMs = WalkTicks * TicksToMs;
        yield return $"frame capture {Number}: stage 13 {Ms:0.00} ms; {Cmds.Length} commands took {cmdMs:0.00} ms " +
                     $"(DrawOTag {drawOTag:0.00} ms with the frame cap): {Polys} polygons ({Tris} triangles), {Sprites} sprites, " +
                     $"{Lines} lines, {Fills} fills, {EnvCmds} env, {Uploads} uploads, {Copies} copies" +
                     (Truncated ? "; TRUNCATED" : "") + $"; analysed in {AnalyseMs:0.0} ms";
        yield return $"  port: {State}";
        yield return $"  arena attribution: {Owned} of {Attributable} addressed primitives " +
                     $"({(Attributable > 0 ? 100.0 * Owned / Attributable : 0):0.0}%)";
        var avg = Covered > 0 ? (double)ViewFrags / Covered : 0;
        yield return $"  fragments (1x): {ViewFrags} in the {ViewW}x{ViewH} view at ({ViewL},{ViewT}), " +
                     $"{Covered} pixels covered ({100.0 * Covered / Math.Max(1, ViewW * ViewH):0.0}%), " +
                     $"overdraw {avg:0.00} average, {MaxOverdraw} max; histogram " +
                     string.Join(" ", OverdrawHistogram.Select((n, i) => $"{(i == OverdrawHistogram.Length - 1 ? $"{i}+" : $"{i}")}:{n}"));
        yield return $"  GL batch submits: {Flushes.Length}" + (Flushes.Length == 0 ? "" : ": " + string.Join(", ",
            Enumerable.Range(0, ByReason.Length).Where(i => ByReason[i] > 0).OrderByDescending(i => ByReason[i])
                .Select(i => $"{ReasonLabel((FlushReason)i)} {ByReason[i]}")));
        var v = StageVtx;
        yield return $"  vertex map in stage 13: {v.Stores} stores watched, {v.Scans} ring scans, {v.Bound} bound, " +
                     $"{v.Published} coordinates from the GTE, {v.Republished} loads carried on; lookups: {VtxHits} of " +
                     $"{VtxAsked} vertices recovered ({(VtxAsked > 0 ? 100.0 * VtxHits / VtxAsked : 0):0.0}%) in " +
                     $"{LookupTicks * TicksToMs:0.000} ms" + (MapHeldOff ? " -- the map was HELD OFF for this capture" : "");
        yield return "  " + GpuLine();
    }

    public string GpuLine()
    {
        if (!TimerQueries) return "GPU: no timer queries on this GL context";
        if (!GpuFinal) return "GPU: waiting for the GPU to answer";
        var ao = TailWorks.FirstOrDefault(w => w.What == GpuWork.AmbientOcclusion);
        var ssr = TailWorks.FirstOrDefault(w => w.What == GpuWork.Reflections);
        var comp = TailWorks.FirstOrDefault(w => w.What == GpuWork.Composite);
        var pres = TailWorks.FirstOrDefault(w => w.What == GpuWork.Present);
        return $"GPU: the frame's batches {BatchGpuNs / 1e6:0.000} ms" +
               (HasTailFlush && TailFlush.HasGpu ? $" (last batch {TailFlush.GpuNs / 1e6:0.000}, submitted at the next present)" : "") +
               (TailDone
                   ? $"; its present: whole {pres.Ticks * TicksToMs:0.000} ms CPU" +
                     (ao.What == GpuWork.AmbientOcclusion ? $", AO pass {ao.Ticks * TicksToMs:0.000} CPU / {Gpu(ao)} GPU" : ", no AO pass") +
                     (ssr.What == GpuWork.Reflections ? $", reflection pass {ssr.Ticks * TicksToMs:0.000} CPU / {Gpu(ssr)} GPU" : "") +
                     $", composite {comp.Ticks * TicksToMs:0.000} CPU / {Gpu(comp)} GPU"
                   : "; its present was not seen");

        static string Gpu(in CapturedWork w) => w.HasGpu ? $"{w.GpuNs / 1e6:0.000}" : "?";
    }

    public IEnumerable<string> Report()
    {
        foreach (var line in Summary()) yield return line;
        yield return $"  {"routine",-36} {"calls",5} {"incl ms",8} {"self ms",8} {"prims",6} {"send ms",8} {"fragments",9} {"submits",7} {"stores",7} {"bound",6} {"verts",9}";
        for (var i = 0; i < Slots.Length; i++)
        {
            var s = Slots[i];
            if (s.Calls == 0 && s.Packets == 0 && s.Frags == 0) continue;
            var name = i < Slots.Length - 1 ? FrameCapture.SlotName[i] : "(no routine)";
            yield return $"  {Truncate(name, 36),-36} {s.Calls,5} {s.Incl * TicksToMs,8:0.000} {s.Self * TicksToMs,8:0.000} " +
                         $"{s.Packets,6} {s.WalkTicks * TicksToMs,8:0.000} {s.Frags,9} {s.Flushes,7} {s.Vtx.Stores,7} {s.Vtx.Bound,6} " +
                         $"{$"{s.VtxHits}/{s.VtxAsked}",9}";
        }
        yield return $"  {"runtime section (top 16 by self)",-60} {"calls",5} {"incl ms",8} {"self ms",8}";
        foreach (var st in SectionStats.Take(16))
            yield return $"  {Truncate(SectionName(st.Id), 60),-60} {st.Calls,5} {st.Incl * TicksToMs,8:0.000} {st.Self * TicksToMs,8:0.000}";
    }

    /// <summary>Once the GPU has answered: the line, and each routine's share.</summary>
    public IEnumerable<string> GpuReport()
    {
        yield return $"frame capture {Number}: " + GpuLine();
        if (!TimerQueries || !GpuFinal) yield break;
        var parts = Enumerable.Range(0, Slots.Length).Where(i => Slots[i].GpuNs > 0).OrderByDescending(i => Slots[i].GpuNs)
            .Select(i => $"{(i < Slots.Length - 1 ? FrameCapture.SlotName[i].Trim() : "(no routine)")} {Slots[i].GpuNs / 1e6:0.000}");
        yield return "  GPU by routine (each batch split by fragments): " + string.Join(", ", parts);
    }

    static string Truncate(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    double SlotTotal(string name)
    {
        for (var i = 0; i < FrameCapture.SlotCount && i < Slots.Length - 1; i++)
            if (FrameCapture.SlotName[i] == name) return Slots[i].Incl * TicksToMs;
        return 0;
    }

    static string F(double v, string fmt = "0.0000") => v.ToString(fmt, CultureInfo.InvariantCulture);

    static string Quote(string s) => s.IndexOfAny([',', '"']) < 0 ? s : $"\"{s.Replace("\"", "\"\"")}\"";

    /// <summary>Commands, calls and sections, as three files beside each other.</summary>
    public void WriteAllCsv(string path)
    {
        WriteCsv(path);
        WriteCallsCsv(Path.ChangeExtension(path, ".calls.csv"));
        WriteSectionsCsv(Path.ChangeExtension(path, ".sections.csv"));
    }

    public void WriteCsv(string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("index,t_ms,cost_us,kind,op,owner,by_time,ran_in,ot_entry,ot_slot,src,frags,batch,submits,reason,clip," +
                    "verts_asked,verts_hit,lookup_us,gpu_us,verts");
        Span<(int X, int Y)> v = stackalloc (int, int)[16];
        for (var i = 0; i < Cmds.Length; i++)
        {
            var c = Cmds[i];
            var ranIn = c.RunIn >= 0 ? FrameCapture.SlotName[Calls[c.RunIn].Slot] : "";
            var owner = c.Owner >= 0 ? FrameCapture.SlotName[Calls[c.Owner].Slot] : "";
            w.WriteLine(string.Join(',', i, F(RelMs(c.Start)), F(c.Ticks * TicksToMs * 1000, "0.0"),
                Quote(KindLabel(c)), $"{c.Op:X2}", Quote(owner), c.OwnerByTime ? 1 : 0, Quote(ranIn),
                c.OtEntry, c.OtSlot, $"{c.Src:X6}", c.Frags, c.Batch, c.Flushes,
                c.Flushes > 0 ? Quote(ReasonLabel(c.Reason)) : "",
                $"{c.ClipL}:{c.ClipT}:{c.ClipR}:{c.ClipB}",
                c.VtxAsked, c.VtxHits, F(c.LookupTicks * TicksToMs * 1000, "0.0"), c.HasGpu ? F(c.GpuNs / 1e3, "0.0") : "",
                string.Join(' ', v[..Vertices(i, v)].ToArray().Select(p => $"{p.X}:{p.Y}"))));
        }
    }

    public void WriteCallsCsv(string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("index,routine,address,depth,parent,enter_ms,incl_ms,self_ms,arena_lo,arena_hi,prims,send_us,frags,submits," +
                    "self_stores,self_scans,self_bound,self_published,self_republished,verts_asked,verts_hit,gpu_us");
        for (var i = 0; i < Calls.Length; i++)
        {
            var c = Calls[i];
            var v = c.SelfVtx;
            w.WriteLine(string.Join(',', i, FrameCapture.SlotName[c.Slot].Replace(",", ";"), $"{FrameCapture.SlotAddr[c.Slot]:X8}",
                c.Depth, c.Parent, F(RelMs(c.Enter)), F(c.Incl * TicksToMs), F(c.Self * TicksToMs),
                $"{c.RangeLo:X6}", $"{c.RangeHi:X6}", c.Packets, F(c.WalkTicks * TicksToMs * 1000, "0.0"), c.Frags, c.Flushes,
                v.Stores, v.Scans, v.Bound, v.Published, v.Republished, c.VtxAsked, c.VtxHits,
                GpuFinal ? F(c.GpuNs / 1e3, "0.0") : ""));
        }
    }

    public void WriteSectionsCsv(string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(false));
        w.WriteLine("index,section,group,depth,parent,enter_ms,incl_ms,self_ms,gpu_us");
        for (var i = 0; i < Sections.Length; i++)
        {
            var sec = Sections[i];
            var gpu = SectionGpuNs(sec);
            w.WriteLine(string.Join(',', i, Quote(SectionName(sec.Id)), Profiler.Group(sec.Id), sec.Depth, sec.Parent,
                F(RelMs(sec.Enter)), F(sec.Incl * TicksToMs), F(sec.Self * TicksToMs), gpu >= 0 ? F(gpu / 1e3, "0.0") : ""));
        }
        foreach (var t in TailWorks)
            w.WriteLine(string.Join(',', "tail", Quote($"next present: {WorkName(t.What)}"), "Runtime", 0, -1,
                F(RelMs(t.Start)), F(t.Ticks * TicksToMs), F(t.Ticks * TicksToMs), t.HasGpu ? F(t.GpuNs / 1e3, "0.0") : ""));
    }
}

/// <summary>Replays a capture on a detached software GPU, up to any command.</summary>
public sealed class Replayer
{
    public readonly PsxGpu Gpu = new() { Detached = true };
    public readonly int[] Coverage = new int[PsxGpu.VramWidth * PsxGpu.VramHeight];
    public readonly int[] Owner = new int[PsxGpu.VramWidth * PsxGpu.VramHeight];

    Capture? _cap;
    bool _checker;

    /// <summary>The last command applied, -1 for none.</summary>
    public int Pos { get; private set; } = -1;
    public Capture? Source => _cap;

    public void Seek(Capture cap, int k, bool checker)
    {
        k = Math.Clamp(k, -1, cap.Cmds.Length - 1);
        if (cap != _cap || checker != _checker || k < Pos) Reset(cap, checker);
        while (Pos < k) Step();
    }

    internal void Reset(Capture cap, bool checker)
    {
        _cap = cap;
        _checker = checker;
        Gpu.CopyStateFrom(cap.Start);
        Gpu.Coverage = Coverage;
        Gpu.Owner = Owner;
        Gpu.Fragments = 0;
        Array.Clear(Coverage);
        Array.Fill(Owner, -1);
        Pos = -1;

        // A dark checker where the frame is drawn, so what this frame never covers shows.
        if (!checker) return;
        for (var y = cap.ViewT; y < cap.ViewT + cap.ViewH; y++)
        for (var x = cap.ViewL; x < cap.ViewL + cap.ViewW; x++)
            Gpu.Vram[y * PsxGpu.VramWidth + x] = (((x >> 3) + (y >> 3)) & 1) == 0 ? (ushort)0x0842 : (ushort)0x14A5;
    }

    internal void Step()
    {
        var i = ++Pos;
        ref var c = ref _cap!.Cmds[i];
        Gpu.CoverTag = i;
        for (var j = c.First; j < c.First + c.Count; j++)
            if (_cap.Gp1[j]) Gpu.WriteGp1(_cap.Words[j]);
            else Gpu.WriteGp0(_cap.Words[j], 0u);
    }
}
