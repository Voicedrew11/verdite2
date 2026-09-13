using System.Globalization;
using System.Reflection;
using System.Text;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;
using HostWindow = RecompOne.Runtime.Host.HostWindow;

namespace Kf2;

/// <summary>
/// Where a frame's time goes: the port's half of the frame profiler
/// (<c>RecompOne.Runtime.Diagnostics.Profiler</c>, patches/recompone/0045).
///
///     KF2_PROFILE=1            record from boot; a summary on the console every 5 s
///     KF2_PROFILE=panel        record from boot and open the panel (Shift+P toggles it)
///     KF2_PROFILE_OUT=path     every frame's sections as CSV rows, for
///                              scripts/profile_report.py
///     KF2_PROFILE_SPIKE=12     a console line for every frame whose *work* passes 12 ms
///     KF2_PROFILE_FUNCS=stages time the thirteen main-loop stages; or a list,
///                              game:80040348+800342D8 (overlay defaults to game)
///
/// **What it can see without being asked.** Every hooked function is already a
/// section, because the runtime times inside <c>HookManager.Invoke</c>: the
/// function's own body (recompiled MIPS) and every pre, post and replace delegate
/// separately, named after the method that implements it. With the port's
/// patches that covers the gated stages, stage 13, DrawOTag and VSync. The runtime
/// adds the present path -- window events, picture compose, the interface, the
/// swap -- and the sleeps are sections of their own (<see cref="ProfileGroup.Wait"/>),
/// so a frame capped at 144 fps reads as work plus waiting rather than as 6.9 ms of
/// something. Whatever no section claims is "game code (no section)".
///
/// **To look inside the game's own time**, time more functions: an empty pre-hook
/// makes any recompiled function a section, which is what
/// <see cref="AddProbe"/> does. That is a detour, so it is opt-in.
///
/// Recording costs nothing when off -- one bool at every site -- and turns itself
/// on while the panel is open, so it is not a setting and saves nothing.
/// </summary>
public static class FrameProfiler
{
    // The port's own waits, registered here so the three pacers can name them.
    public static readonly int FloorWait = Profiler.Register("FramePacing.Floor (frame cap)", ProfileGroup.Wait);
    public static readonly int MenuWait = Profiler.Register("MenuPacing (menu repeat grid)", ProfileGroup.Wait);
    public static readonly int LoadWait = Profiler.Register("LoadPacing (loading figure grid)", ProfileGroup.Wait);

    static readonly ModInfo _self = new()
    {
        Id = "kf2.profiler",
        Name = "Frame profiler",
        Version = "1.0",
        Description = "Times the frame by section; empty pre-hooks make game functions sections.",
    };

    static bool _envOn;
    static bool _openPanel;
    static bool _console;
    static double _spikeMs;
    static string? _csvPath;
    static string? _funcs;

    /// <summary>Record while the panel is closed.</summary>
    public static bool KeepRecording;
    /// <summary>Hold the history still, for reading.</summary>
    public static bool Paused;

    public static void Configure(string? on, string? csv, string? spike, string? funcs)
    {
        _envOn = on is "1" or "panel" or "on";
        _openPanel = on == "panel";
        _console = _envOn;
        _csvPath = string.IsNullOrWhiteSpace(csv) ? null : csv;
        if (double.TryParse(spike, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0) _spikeMs = s;
        _funcs = string.IsNullOrWhiteSpace(funcs) ? null : funcs;
    }

    /// <summary>Recording follows whoever wants it: the environment, the CSV, the panel
    /// being open, or the panel's keep-recording box -- and a pause beats all of them.</summary>
    public static void UpdateEnabled()
    {
        var want = _envOn || _csv != null || _spikeMs > 0 || KeepRecording || ProfilerPanel.Instance.IsOpen;
        Profiler.Enabled = want && !Paused;
    }

    public static void Install()
    {
        Profiler.FrameCompleted += OnFrame;

        if (_csvPath != null)
        {
            try
            {
                _csv = new StreamWriter(_csvPath, false, new UTF8Encoding(false), 1 << 16);
                _csv.WriteLine(CsvHeader);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => _csv?.Flush();
                Console.WriteLine($"[KF2] profile: writing every frame to {_csvPath}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[KF2] profile: cannot write {_csvPath}: {e.Message}");
                _csv = null;
            }
        }

        UpdateEnabled();
        if (Profiler.Enabled)
            Console.WriteLine("[KF2] profile: recording" +
                              (_spikeMs > 0 ? $", spikes over {_spikeMs:0.#} ms of work" : ""));

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Localization.Merge("""
            {
              "strings": {
                "kf2.profiler": { "en": "Frame profiler", "pt-BR": "Perfilador de quadros",
                                  "es-419": "Perfilador de cuadros" }
              }
            }
            """);
            PanelManager.Register(ProfilerPanel.Instance);
            ConfigManager.ApplyViewToPanels([ProfilerPanel.Instance]);
            if (_openPanel) ProfilerPanel.Instance.IsOpen = true;
            UpdateEnabled();
        });

        // Off the event bus for the reason patches/Map.cs gives: a polled key would be
        // dead in the menus and through a load, which are two of the places worth
        // profiling. Shift+P, in the shape of the map's Shift+M: every function key is
        // taken -- F1 and F11 by the runtime, F2-F8 by mods/kf2debug, F9, F10 and F12
        // offered as the mouse-capture key.
        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!e.Pressed || e.Repeat || e.Key != (int)Key.P || PopupManager.AnyOpen) return;
            if (!HostWindow.IsKeyDown(Key.ShiftLeft) && !HostWindow.IsKeyDown(Key.ShiftRight)) return;
            ProfilerPanel.Instance.IsOpen = !ProfilerPanel.Instance.IsOpen;
        });

        HookAttach.OnOverlayLoad("profile", () =>
        {
            ApplyLabels();
            if (_funcs != null)
                foreach (var line in AddProbes(_funcs))
                    Console.WriteLine($"[KF2] profile: {line}");
            return true;
        });
    }

    // ---- names for the addresses the port knows --------------------------------

    static readonly (string Overlay, uint Addr, string Label)[] Known =
    [
        ("game", 0x8002C944, "stage 1"),
        ("game", 0x80037C0C, "stage 2: object state machine"),
        ("game", 0x8002A550, "stage 3: player, pad, menu"),
        ("game", 0x80040348, "stage 4: entity table"),
        ("game", 0x80046A60, "stage 5: effects and projectiles"),
        ("game", 0x8004910C, "stage 6: area module per-frame"),
        ("game", 0x8001689C, "stage 7: area loader"),
        ("game", 0x80025A1C, "stage 8"),
        ("game", 0x800140AC, "stage 9"),
        ("game", 0x8002CA74, "stage 10"),
        ("game", 0x80016FC8, "stage 11"),
        ("game", 0x80014534, "stage 12"),
        ("game", 0x800342D8, "stage 13: renderer"),
        ("game", 0x80033FBC, "fade stepper"),
        ("game", 0x8002DC78, "animated textures"),
        ("game", 0x80017880, "frame gate"),
        ("game", 0x80018E80, "in-game menu loop"),
        ("game", 0x80037B5C, "transition fade loop"),
        ("game", 0x80060818, "DrawOTag"),
        ("game", 0x8005FCC8, "VSync thunk"),
        ("open", 0x80016078, "DrawOTag"),
        ("open", 0x8001EB88, "VSync thunk"),
        ("end", 0x80013D80, "DrawOTag"),
        ("end", 0x8001B154, "VSync thunk"),
    ];

    static bool _labelled;

    static void ApplyLabels()
    {
        if (_labelled) return;
        _labelled = true;
        foreach (var (overlay, addr, label) in Known)
            if (SymbolRegistry.Resolve(overlay, null, addr) is { } target)
                Profiler.SetLabel(Profiler.FunctionName(target), label);
        Profiler.SetLabel($"pre {nameof(FrameProfiler)}.{nameof(Probe)}", "profiler probes (empty)", ProfileGroup.Hook);
    }

    // ---- probes ------------------------------------------------------------------

    /// <summary>The empty pre-hook a probe installs. Its only job is to exist:
    /// HookManager times every hooked function.</summary>
    public static void Probe(CpuContext c, IMemory m)
    {
    }

    /// <summary>Make a recompiled function a section. Must run on the game thread,
    /// which is where the panel draws.</summary>
    public static string AddProbe(string overlay, uint addr)
    {
        var target = SymbolRegistry.Resolve(overlay, null, addr);
        if (target == null) return $"{overlay} 0x{addr:X8}: no function starts there";
        var name = Profiler.FunctionName(target);
        if (HookManager.IsRegistered(target)) return $"{name}: already hooked, so already timed";

        var probe = typeof(FrameProfiler).GetMethod(nameof(Probe), BindingFlags.Public | BindingFlags.Static)!;
        if (!HookManager.AddPre(_self, target, probe)) return $"{name}: HookManager refused the probe";
        HookManager.Commit();
        return HookAttach.Installed(target) ? $"{name}: timing" : $"{name}: queued but not installed";
    }

    /// <summary><c>stages</c>, or <c>[overlay:]hex</c> items separated by + or ,.</summary>
    public static IEnumerable<string> AddProbes(string spec)
    {
        foreach (var raw in spec.Split(['+', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Equals("stages", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (overlay, addr, label) in Known)
                    if (label.StartsWith("stage ", StringComparison.Ordinal))
                        yield return AddProbe(overlay, addr);
                continue;
            }

            var colon = raw.IndexOf(':');
            var overlayName = colon >= 0 ? raw[..colon] : "game";
            var hex = (colon >= 0 ? raw[(colon + 1)..] : raw).Replace("0x", "").Replace("func_", "");
            yield return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a)
                ? AddProbe(overlayName.ToLowerInvariant(), a)
                : $"'{raw}': not an address";
        }
    }

    // ---- console and CSV ---------------------------------------------------------

    static StreamWriter? _csv;
    static long _csvFlushAt;

    static readonly long[] _sumSelf = new long[Profiler.MaxSections];
    static readonly List<double> _frameMs = new(4096);
    static long _winStart;
    static double _winWork, _winWait, _winGpu, _winGc, _winJit;
    static long _winAlloc;

    static void OnFrame(Profiler.Frame f)
    {
        if (_csv != null)
        {
            WriteCsv(_csv, f);
            if (f.Start >= _csvFlushAt)
            {
                _csv.Flush();
                _csvFlushAt = f.Start + (long)(1000.0 / Profiler.TicksToMs);
            }
        }
        if (_spikeMs > 0 && f.WorkMs > _spikeMs)
            Console.WriteLine($"[KF2] profile spike: frame {f.Index}, {f.WorkMs:0.00} ms of work in {f.Ms:0.00} ms" +
                              (f.GcPauseMs > 0 ? $", GC {f.GcPauseMs:0.00} ms" : "") +
                              (f.JitMs > 0.05 ? $", JIT {f.JitMs:0.00} ms ({f.JitMethods} methods)" : "") +
                              $": {Top(f.Span, 6)}");
        if (_console) Accumulate(f);
    }

    public const string CsvHeader = "frame,time_ms,section,group,self_ms,incl_ms,calls";

    /// <summary>One frame as CSV rows: a row per section, then the frame-level
    /// measurements as pseudo-sections in the same columns.</summary>
    public static void WriteCsv(StreamWriter w, Profiler.Frame f)
    {
        var t = (f.Start * Profiler.TicksToMs).ToString("0.000", CultureInfo.InvariantCulture);
        foreach (var s in f.Span)
        {
            w.Write(f.Index);
            w.Write(',');
            w.Write(t);
            w.Write(',');
            WriteQuoted(w, Profiler.DisplayName(s.Id));
            w.Write(',');
            w.Write(Profiler.Group(s.Id).ToString());
            w.Write(',');
            w.Write(s.SelfMs.ToString("0.0000", CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(s.InclMs.ToString("0.0000", CultureInfo.InvariantCulture));
            w.Write(',');
            w.WriteLine(s.Calls);
        }

        // Frame-level measurements as pseudo-sections in the same columns, so one
        // file carries everything and a reader needs no second format.
        Row(w, f, t, "frame.total", f.Ms);
        if (f.GcPauseMs > 0) Row(w, f, t, "frame.gc_pause", f.GcPauseMs);
        if (f.AllocBytes > 0) Row(w, f, t, "frame.alloc_kb", f.AllocBytes / 1024.0);
        if (f.JitMs > 0) Row(w, f, t, "frame.jit", f.JitMs);

        static void Row(StreamWriter w, Profiler.Frame f, string t, string name, double v)
            => w.WriteLine($"{f.Index},{t},{name},Frame,{v.ToString("0.0000", CultureInfo.InvariantCulture)},,");

        static void WriteQuoted(StreamWriter w, string s)
        {
            if (s.IndexOfAny([',', '"']) < 0)
            {
                w.Write(s);
                return;
            }

            w.Write('"');
            w.Write(s.Replace("\"", "\"\""));
            w.Write('"');
        }
    }

    const double ReportSeconds = 5.0;

    static void Accumulate(Profiler.Frame f)
    {
        if (_frameMs.Count == 0) _winStart = f.Start;
        _frameMs.Add(f.Ms);
        foreach (var s in f.Span) _sumSelf[s.Id] += s.Self;
        _winWork += f.WorkMs;
        _winWait += f.WaitMs;
        _winGpu += f.GpuMs;
        _winGc += f.GcPauseMs;
        _winJit += f.JitMs;
        _winAlloc += f.AllocBytes;

        var seconds = (f.Start + f.Ticks - _winStart) * Profiler.TicksToMs / 1000.0;
        if (seconds < ReportSeconds) return;

        var n = _frameMs.Count;
        var avg = _frameMs.Sum() / n;
        _frameMs.Sort();
        var p99 = _frameMs[Math.Min(n - 1, (int)(n * 0.99))];
        var max = _frameMs[n - 1];

        var ids = new List<int>();
        for (var id = 0; id < Profiler.SectionCount; id++)
            if (_sumSelf[id] > 0) ids.Add(id);
        ids.Sort((a, b) => _sumSelf[b].CompareTo(_sumSelf[a]));

        var top = string.Join(", ", ids.Where(id => Profiler.Group(id) != ProfileGroup.Wait).Take(10)
            .Select(id => $"{Profiler.DisplayName(id)} {_sumSelf[id] * Profiler.TicksToMs / n:0.000}"));

        Console.WriteLine($"[KF2] profile: {n / seconds:0.0} fps, frame {avg:0.00} ms avg / {p99:0.00} p99 / " +
                          $"{max:0.00} max; work {_winWork / n:0.00}, wait {_winWait / n:0.00}, " +
                          $"swap {_winGpu / n:0.00} ms; GC {_winGc / seconds:0.00} ms/s, JIT {_winJit / seconds:0.00} ms/s, " +
                          $"{_winAlloc / 1024.0 / n:0.0} KB/frame allocated; top self ms/frame: {top}");

        _frameMs.Clear();
        Array.Clear(_sumSelf);
        _winWork = _winWait = _winGpu = _winGc = _winJit = 0;
        _winAlloc = 0;
    }

    /// <summary>The <paramref name="n"/> biggest self times in a frame, work first.</summary>
    public static string Top(ReadOnlySpan<Profiler.Sample> samples, int n)
    {
        var list = samples.ToArray()
            .Where(s => Profiler.Group(s.Id) != ProfileGroup.Wait && s.Self > 0)
            .OrderByDescending(s => s.Self)
            .Take(n)
            .Select(s => $"{Profiler.DisplayName(s.Id)} {s.SelfMs:0.00}");
        return string.Join(", ", list);
    }
}
