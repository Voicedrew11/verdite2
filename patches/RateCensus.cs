using System.Diagnostics;
using System.Globalization;
using System.Text;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// Which words of the game's memory change at the render rate instead of at the
/// tick rate. The instrument that turns "something looks too fast" into an
/// address.
///
///     KF2_RATECENSUS=1                       on (off by default)
///     KF2_RATECENSUS_RANGE=80060000:801C0000 the window to watch (this is the default)
///     KF2_RATECENSUS_OUT=path                where to write (default ratecensus.txt)
///     KF2_RATECENSUS_PERIOD=5                seconds between dumps
///
/// ## Why this exists
///
/// Every rate defect in this port is the same defect: King's Field has no clock,
/// so it counts time in loop iterations and in <c>VSync</c> calls, and the port
/// has broken the identity between those and wall time. <see cref="FramePacing"/>
/// holds the world's counters to <see cref="FramePacing.LogicHz"/>, but only for
/// what a whole-function hook can reach -- and stage 2, the 396-arm object
/// dispatch, is deliberately outside it because it presents. So the remaining
/// sites are found by noticing them in play, one at a time, which is both slow
/// and biased toward whatever is annoying rather than whatever is wrong.
///
/// This finds them without anyone noticing anything: run the same scene at two
/// render rates and rank every word by how much more often it changed at the
/// higher one. A word on the tick clock changes at the same rate in both runs. A
/// word on the render clock does not.
///
/// ## The sampling clock is the vblank, and that is the whole trick
///
/// Sampling per rendered frame would be useless -- the sample rate would move
/// with the thing being measured. <c>VSyncEvent</c> fires once per *emulated
/// vblank*, and since patches/recompone/0021 that is a wall-clock 60 Hz grid, so
/// the sample rate is 60/s at 20 fps and 60/s at 144 fps and the two runs are
/// directly comparable.
///
/// Two consequences worth stating rather than discovering:
///
/// * **The ceiling is 60 changes a second.** Above that the sampling aliases, so
///   a word stepping at 144 Hz reports 60. That is fine for classification -- the
///   question is "does this track the render rate", not "what exactly is its
///   rate" -- but it means the ratio between a 20 fps and a 144 fps run tops out
///   near **3.0**, not 7.2. Read a ratio above ~1.5 as render-clocked and a ratio
///   near 1.0 as tick-clocked.
/// * **At a render rate below 60 the vblanks arrive in bursts**, several per
///   <c>VSync</c> call, and nothing runs between them -- so a burst reads as one
///   change, not several, which is what keeps a 20 Hz stepper reporting 20 rather
///   than 60.
///
/// ## What a ratio near 1.0 does *not* mean
///
/// It means the word tracks the tick, which is usually right and sometimes
/// exactly wrong: <see cref="FrameSmoothing"/> and <see cref="ObjectSmoothing"/>
/// write interpolated values every rendered frame **on purpose**, and will show
/// up here as render-clocked. So will anything genuinely per-frame, like a
/// primitive-buffer cursor. The output is a candidate list, not a defect list;
/// deciding is still a person's job, and turning both smoothing patches off for
/// a census run removes the loudest false positives.
///
/// Addresses come out of here; `scripts/find_writers.py` maps them to the
/// functions that write them. See "Finding the rate defects" in
/// docs/DEVELOPMENT.md.
/// </summary>
public static class RateCensus
{
    /// <summary>The game's data region: GAME.EXE's own globals from 0x80060000
    /// up through the object table, the entity table, the cull grid, the player
    /// block and the effect slots. Low RAM (code, the boot stub) and the stack
    /// are outside it on purpose.</summary>
    const uint DefaultLow = 0x80060000;
    const uint DefaultHigh = 0x801C0000;

    public static bool Enabled { get; private set; }

    static uint _low = DefaultLow, _high = DefaultHigh;
    static string _out = "ratecensus.txt";
    static double _periodMs = 5000.0;

    static uint[] _prev = [];
    static uint[] _changes = [];
    static long _samples;
    static bool _primed;

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _startMs = -1.0;
    static double _nextDumpMs;

    public static void Configure(string? enabled, string? range, string? outPath, string? period)
    {
        if (string.IsNullOrWhiteSpace(enabled) || enabled == "0") return;
        Enabled = true;

        if (!string.IsNullOrWhiteSpace(range))
        {
            var parts = range.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                throw new ArgumentException($"KF2_RATECENSUS_RANGE: want lo:hi in hex, got '{range}'");
            _low = Convert.ToUInt32(parts[0].Trim(), 16) & ~3u;
            _high = (Convert.ToUInt32(parts[1].Trim(), 16) + 3u) & ~3u;
            if (_high <= _low)
                throw new ArgumentException($"KF2_RATECENSUS_RANGE: empty range '{range}'");
        }

        if (!string.IsNullOrWhiteSpace(outPath)) _out = outPath;

        if (!string.IsNullOrWhiteSpace(period))
        {
            if (!double.TryParse(period, NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                throw new ArgumentException($"KF2_RATECENSUS_PERIOD: cannot read '{period}'");
            _periodMs = Math.Max(1.0, s) * 1000.0;
        }
    }

    public static void Install()
    {
        if (!Enabled) return;

        int words = (int)((_high - _low) / 4);
        _prev = new uint[words];
        _changes = new uint[words];

        Console.WriteLine($"[KF2] rate census: 0x{_low:X8}..0x{_high:X8} ({words} words), " +
                          $"sampling on the vblank grid, dumping to {_out} every {_periodMs / 1000.0:0.#} s");

        Event.AddListener<VSyncEvent>(_ => Sample());

        // A census run is normally ended by killing the process, so the periodic
        // dump is the one that matters; this only catches a clean exit.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dump();
    }

    static void Sample()
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return;

        double now = _clock.Elapsed.TotalMilliseconds;
        if (_startMs < 0.0) { _startMs = now; _nextDumpMs = now + _periodMs; }

        var prev = _prev;
        var changes = _changes;
        uint addr = _low;

        if (!_primed)
        {
            // The first sample has nothing to compare against, so it seeds the
            // snapshot and is not counted -- otherwise every non-zero word in the
            // game would read as one change.
            for (int i = 0; i < prev.Length; i++, addr += 4) prev[i] = m.ReadU32(addr);
            _primed = true;
            return;
        }

        for (int i = 0; i < prev.Length; i++, addr += 4)
        {
            uint v = m.ReadU32(addr);
            if (v != prev[i]) { prev[i] = v; changes[i]++; }
        }
        _samples++;

        if (now >= _nextDumpMs) { _nextDumpMs = now + _periodMs; Dump(); }
    }

    /// <summary>
    /// Every word that moved, with its change count, plus the sample rate so a
    /// comparison can check the two runs really were sampled on the same grid.
    /// Rewritten in full each time rather than appended, so killing the process
    /// mid-write is the worst case and the file is otherwise always complete.
    /// </summary>
    static void Dump()
    {
        if (!_primed || _samples == 0) return;

        double seconds = (_clock.Elapsed.TotalMilliseconds - _startMs) / 1000.0;
        if (seconds <= 0.0) return;

        var sb = new StringBuilder(1 << 20);
        sb.Append("# kf2 rate census\n");
        sb.Append($"# range {_low:X8}:{_high:X8}\n");
        sb.Append($"# samples {_samples}\n");
        sb.Append($"# seconds {seconds:0.###}\n");
        sb.Append($"# sampleHz {_samples / seconds:0.##}\n");
        sb.Append("# addr changes\n");

        var changes = _changes;
        uint addr = _low;
        for (int i = 0; i < changes.Length; i++, addr += 4)
            if (changes[i] != 0) sb.Append($"{addr:X8} {changes[i]}\n");

        try
        {
            File.WriteAllText(_out, sb.ToString());
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] rate census: cannot write {_out}: {e.Message}");
        }
    }
}
