using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// A floor on how long a rendered frame may take, and the measurement that
/// justifies it.
///
/// King's Field's game speed *is* its frame rate: everything advances a fixed
/// amount per loop iteration, and the loop waits a whole number of vblanks, so
/// the achievable rates are quantised to 60/n. On hardware a frame costs 2, 3 or
/// 4 vblanks depending on scene load -- 30, 20 or 15 fps -- which is the
/// "banding" the game is known for, and 30 fps is its ceiling.
///
/// This port never bands down: `DrawOTag` returns as soon as the ordering table
/// is walked because the GPU is HLE, and the MIPS runs as native code, so no
/// frame is ever expensive enough to miss a vblank. Measured over a 30-minute
/// session (49,570 frames) it sat at two vblanks 85.7% of the time and *one*
/// vblank 14.0% of the time -- and that second group runs at 60 fps, twice the
/// rate the game can reach on any console.
///
/// So the fix is a floor, not a rescale: hold every rendered frame to at least
/// two vblanks and the port is a constant 30 fps -- exactly NTSC's fastest band,
/// never above it, and without the wander that made the original's speed depend
/// on what was on screen. No game constant is touched and no knowledge of which
/// variable holds what is needed.
///
/// Installed as a `post` hook on `DrawOTag` in config/kf2.json (all three
/// overlays) rather than as a change to the runtime's own FrameClock, which
/// paces per *VSync call* and so cannot tell a one-vblank frame from the first
/// half of a two-vblank one. The two clocks do not fight: FrameClock's deadline
/// simply falls behind real time and its wait collapses to nothing, leaving the
/// floor here as the sole pacer for the frames that need it.
/// </summary>
public static class FramePacing
{
    const double VBlankMs = 1000.0 / 60.0;

    // Spin out the last stretch rather than sleeping it. Thread.Sleep granularity
    // is a few ms, which is a quarter of a vblank.
    const double SpinMs = 1.5;

    /// <summary>
    /// Minimum vblanks a rendered frame may occupy. 2 is NTSC's top band, 30 fps.
    /// 1 or less disables the floor: the runtime's FrameClock already caps a
    /// single VSync at one vblank, so a one-vblank floor would do nothing.
    /// Set from Program.cs (KF2_MINVBLANKS).
    /// </summary>
    public static int MinVBlanks { get; set; } = 2;

    /// <summary>
    /// Seconds between pacing reports; 0 (the default) prints nothing.
    /// Set from Program.cs (KF2_FRAMESTATS).
    /// </summary>
    public static double StatsSeconds { get; set; }

    static readonly Stopwatch _clock = Stopwatch.StartNew();

    // When the current frame is allowed to end. Absolute, not now+min: a frame
    // that overruns is paid for out of the next one, so the rate averages to
    // exactly 60/MinVBlanks instead of drifting down by the accumulated jitter.
    static double _due;

    // VSync(0) calls charged to the frame being drawn.
    static int _vblanks;

    // Vblanks-per-frame histogram for the window since the last report; index is
    // the vblank count, [8] catches everything slower.
    static readonly long[] _bands = new long[9];
    static long _frames;
    static double _windowStart;

    /// <summary>
    /// Subscribe to the vblank. Cheap -- the runtime only builds and dispatches a
    /// VSyncEvent when something is listening, and this fires 60 times a second.
    /// </summary>
    public static void Install()
    {
        _windowStart = _clock.Elapsed.TotalMilliseconds;
        Event.AddListener<VSyncEvent>(_ => _vblanks++);
    }

    /// <summary>
    /// Runs after libgpu's DrawOTag, on the game thread. This is the frame
    /// boundary: the ordering table has been walked, so the frame's drawing is
    /// done and the loop is about to VSync and show it.
    /// </summary>
    public static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        // A second ordering table with no vblank between it and the first belongs
        // to the frame already in flight. King's Field draws one OT per frame --
        // no zero-vblank gap appeared in the 49,570-frame measurement -- but
        // charging the floor per DrawOTag would halve the rate of any screen that
        // does otherwise, so the boundary is defined by the vblank, not the call.
        if (_vblanks == 0) return;

        _bands[Math.Min(_vblanks, 8)]++;
        _frames++;
        _vblanks = 0;

        Floor();
        if (StatsSeconds > 0) Report();
    }

    static void Floor()
    {
        if (MinVBlanks <= 1) return;
        double min = MinVBlanks * VBlankMs;
        double now = _clock.Elapsed.TotalMilliseconds;

        // More than a frame of debt means the game stopped drawing rather than
        // ran late -- a disc read, a module swap, the first frame of all. Restart
        // the cadence instead of running flat out to pay it off.
        if (_due < now - min) _due = now;

        if (now < _due)
        {
            double sleepUntil = _due - SpinMs;
            if (now < sleepUntil)
            {
                int ms = (int)(sleepUntil - now);
                if (ms > 0) Thread.Sleep(ms);
            }
            while (_clock.Elapsed.TotalMilliseconds < _due) Thread.SpinWait(48);
        }

        _due += min;
    }

    // The same measurement the floor was decided from: how many vblanks the game
    // charged each rendered frame, and what that came out to in real time. Bands
    // are per window, so consecutive lines separate the title screen from an area.
    static void Report()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double window = now - _windowStart;
        if (window < StatsSeconds * 1000.0) return;

        long shown = 0;
        for (int i = 1; i < _bands.Length; i++) shown += _bands[i];
        if (shown == 0) { _windowStart = now; return; }

        var bands = new List<string>();
        for (int i = 1; i < _bands.Length; i++)
            if (_bands[i] > 0)
                bands.Add($"{(i == 8 ? "8+" : i.ToString())}:{100.0 * _bands[i] / shown:F1}%");

        Console.WriteLine($"[KF2] pacing: {shown * 1000.0 / window:F1} fps over {shown} frames " +
                          $"({_frames} total) | vblanks/frame {string.Join(" ", bands)}");

        Array.Clear(_bands);
        _windowStart = now;
    }
}
