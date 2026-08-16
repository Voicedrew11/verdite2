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
/// Reports the frame rate and the vblank-per-frame histogram.
///
/// This is the measurement the pacing work was decided from -- counting the
/// VSync(0) calls the game charges between consecutive DrawOTags. King's Field's
/// speed *is* its frame rate, and the loop waits a whole number of vblanks, so
/// which band each frame lands in is the whole story: 2 vblanks is 30 fps, which
/// is the game's ceiling on NTSC hardware, and 1 is 60, which is faster than it
/// can run anywhere.
///
/// The same number used to cost a `KF2_LOG=sdk` trace, which is gigabytes a
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
    static long _total;
    static double _windowStart;
    static int _vblanks;

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
        ImGui.TextWrapped("Counts the VSync(0) calls the game charges between " +
                          "consecutive DrawOTags. 2 vblanks is 30 fps, the game's " +
                          "ceiling on hardware; 1 is 60, which is faster than it can run.");
        ImGui.SliderFloat("Report interval (s)", ref _seconds, 1f, 120f);
    }

    static void OnVSync(VSyncEvent e) => _vblanks++;

    // A second ordering table with no vblank between it and the first belongs to
    // the frame already in flight, so the boundary is the vblank, not the call.
    [PostHook("open", Address = 0x80016078)]
    [PostHook("game", Address = 0x80060818)]
    [PostHook("end", Address = 0x80013D80)]
    static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        if (_vblanks == 0) return;
        _bands[Math.Min(_vblanks, 8)]++;
        _vblanks = 0;
        _total++;

        double window = Now - _windowStart;
        if (window < _seconds) return;

        long shown = 0;
        for (int i = 1; i < _bands.Length; i++) shown += _bands[i];
        if (shown == 0) { _windowStart = Now; return; }

        var bands = new List<string>();
        for (int i = 1; i < _bands.Length; i++)
            if (_bands[i] > 0)
                bands.Add($"{(i == 8 ? "8+" : i.ToString())}:{100.0 * _bands[i] / shown:F1}%");

        Console.WriteLine($"[framestats] {shown / window:F1} fps over {shown} frames " +
                          $"({_total} total) | vblanks/frame {string.Join(" ", bands)}");

        Array.Clear(_bands);
        _windowStart = Now;
    }
}
