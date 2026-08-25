// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.FrameStats;

/// <summary>
/// Reports the frame rate, the vblank-per-frame histogram and the number of
/// VSync calls a frame costs.
///
/// This is the measurement the pacing work is decided from. **The two counts are
/// not the same number and used to be conflated.** Since
/// patches/recompone/0021 the emulated vblank advances on a wall-clock 60 Hz grid
/// rather than once per VSync call, so:
///
/// * **vblanks/frame** is how long the frame took -- 2 means 33 ms, i.e. 30 fps,
///   and **0 means the frame was shorter than a vblank**, which is ordinary above
///   60. It says nothing about what the game asked for, which made the original
///   write-up of this measurement circular.
/// * **calls/frame** is how many times the game asked, counted by hooking the
///   `VSync` thunk itself. That is the number that decides whether RecompOne's
///   per-call FrameClock throttle can even express the rate being asked for.
///
/// In an area the game's own frame gate (`func_80017880`) spins on its vblank
/// credit until it reaches two, so an unmodified port reads `2` on both counts at
/// 30 fps. Above 30 the gate is skipped and calls/frame should fall to 1.
///
/// The same numbers used to cost a `KF2_LOG=sdk` trace, which is gigabytes a
/// minute during play because the game also polls the pad ~200k times a second.
///
/// Bands are per report window rather than cumulative, so consecutive lines
/// separate the title screen from an area, and a burst shows up as a burst
/// instead of being averaged into the session.
/// </summary>
public sealed class FrameStatsMod : IMod
{
    static float _seconds = 15f;

    // Index is the vblank count charged to a frame; [8] catches everything slower.
    static readonly long[] _bands = new long[9];
    // Index is the VSync calls charged to a frame; [8] catches everything above.
    static readonly long[] _calls = new long[9];
    static long _total;
    static double _windowStart;
    static int _vblanks;
    static int _vsyncCalls;

    static double Now => Environment.TickCount64 / 1000.0;

    public void OnLoad()
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("KF2_FRAMESTATS"), out double s) && s > 0)
            _seconds = (float)s;

        _windowStart = Now;
        Event.AddListener<VSyncEvent>(OnVSync);
        Console.WriteLine($"[framestats] reporting every {_seconds:0.#}s");
    }

    public void OnUnload() => Event.RemoveListener<VSyncEvent>(OnVSync);

    public void DrawSettings()
    {
        ImGui.TextWrapped("Reports two counts per rendered frame: vblanks, which is how long " +
                          "the frame took (2 = 33 ms = 30 fps), and VSync calls, which is how " +
                          "many times the game asked. They are different numbers -- the vblank " +
                          "runs on a wall clock here -- and conflating them is what made the " +
                          "first pass at frame pacing hard to read.");
        ImGui.SliderFloat("Report interval (s)", ref _seconds, 1f, 120f);
    }

    static void OnVSync(VSyncEvent e) => _vblanks++;

    /// <summary>
    /// The game's own `VSync` thunk -- `func_8005FCC8` in GAME.EXE, one line that
    /// forwards to the runtime's HLE. Counting calls here rather than counting
    /// VSyncEvents is the whole point of this mod's second histogram: an event is a
    /// vblank on the wall clock, a call is the game asking.
    /// </summary>
    [PreHook("game", Address = 0x8005FCC8)]
    static void BeforeVSync(CpuContext c, IMemory m) => _vsyncCalls++;

    // A second ordering table with no VSync call between it and the first belongs
    // to the frame already in flight, so the boundary is the call. It used to be
    // the vblank, which stops being once-a-frame the moment the port draws faster
    // than 60 -- the same defect patches/FramePacing.cs carried, where it cost the
    // pacing rather than just the report. A `0:` band is now the correct and
    // expected answer for a frame shorter than a vblank.
    [PostHook("open", Address = 0x80016078)]
    [PostHook("game", Address = 0x80060818)]
    [PostHook("end", Address = 0x80013D80)]
    static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        if (_vsyncCalls == 0) return;
        _bands[Math.Min(_vblanks, 8)]++;
        _calls[Math.Min(_vsyncCalls, 8)]++;
        _vblanks = 0;
        _vsyncCalls = 0;
        _total++;

        double window = Now - _windowStart;
        if (window < _seconds) return;

        long shown = 0;
        for (int i = 0; i < _bands.Length; i++) shown += _bands[i];
        if (shown == 0) { _windowStart = Now; return; }

        Console.WriteLine($"[framestats] {shown / window:F1} fps over {shown} frames " +
                          $"({_total} total) | vblanks/frame {Histogram(_bands, shown)}" +
                          $" | calls/frame {Histogram(_calls, shown)}");

        Array.Clear(_bands);
        Array.Clear(_calls);
        _windowStart = Now;
    }

    static string Histogram(long[] counts, double shown)
    {
        var parts = new List<string>();
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] > 0)
                parts.Add($"{(i == 8 ? "8+" : i.ToString())}:{100.0 * counts[i] / shown:F1}%");
        return parts.Count == 0 ? "-" : string.Join(" ", parts);
    }
}
