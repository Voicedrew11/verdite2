namespace Kf2.Mods;

/// <summary>
/// Reports the frame rate and the vblank-per-frame histogram.
///
///     KF2_MODS=framestats=15      every 15 seconds
///
/// This is the measurement the pacing work was decided from -- counting the
/// VSync(0) calls the game charges between consecutive DrawOTags -- made cheap.
/// The same number used to cost a `KF2_LOG=sdk` trace, which is gigabytes a
/// minute during play because the game also polls the pad ~200k times a second.
///
/// Bands are per report window rather than cumulative, so consecutive lines
/// separate the title screen from an area, and a burst shows up as a burst
/// instead of being averaged into the session.
/// </summary>
public sealed class FrameStatsMod : Mod
{
    public override string Name => "framestats";
    public override string Summary => "report fps and the vblank-per-frame histogram";
    public override string State => $"{_seconds:0.#}s";

    double _seconds = 15;

    // Index is the vblank count charged to a frame; [8] catches everything slower.
    readonly long[] _bands = new long[9];
    long _total;
    double _windowStart;

    static double Now => Environment.TickCount64 / 1000.0;

    protected internal override void Configure(string value)
    {
        if (value.Length > 0 && double.TryParse(value, out double s) && s > 0) _seconds = s;
    }

    protected internal override void OnEnabled()
    {
        _windowStart = Now;
        Hooks.FrameDrawn += OnFrameDrawn;
    }

    protected internal override void OnDisabled() => Hooks.FrameDrawn -= OnFrameDrawn;

    void OnFrameDrawn()
    {
        _bands[Math.Min(Hooks.VBlanksThisFrame, 8)]++;
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

        Console.WriteLine($"[KF2] pacing: {shown / window:F1} fps over {shown} frames " +
                          $"({_total} total) | vblanks/frame {string.Join(" ", bands)}");

        Array.Clear(_bands);
        _windowStart = Now;
    }
}
