using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using RecompOne.Runtime;
using RecompOne.Runtime.Host;

namespace Kf2;

/// <summary>
/// What the audio path is doing, since none of it can be judged from a frame counter.
///
///     KF2_AUDIO_PROBE=1        a line a second: voices, clamps, mixer cost, underruns, levels, reverb preset
///     KF2_AUDIO_DUMP=dir       the final mix and the reverb return, as 44.1 kHz WAVs
///
/// scripts/audio_spectrum.py reads the dumps. See docs/AUDIO.md.
/// </summary>
public static class AudioProbe
{
    const int FramesPerBuffer = 256;

    static bool _probe;
    static string? _dumpDir;

    static readonly object _levelGate = new();
    static double _mixSq, _wetSq;
    static long _levelFrames;

    public static void Configure(string? probe, string? dump)
    {
        _probe = !string.IsNullOrWhiteSpace(probe) && probe != "0";
        _dumpDir = string.IsNullOrWhiteSpace(dump) ? null : dump;
    }

    public static void Install()
    {
        if (_probe)
        {
            Spu.Mixed += MeasureLevels;
            new Thread(Report) { IsBackground = true, Name = "kf2-audio-probe" }.Start();
        }

        if (_dumpDir != null) Dump.Start(_dumpDir);
    }

    static void MeasureLevels(short[] mix, short[] wet, int frames)
    {
        double m = 0, w = 0;
        for (var i = 0; i < frames * 2; i++)
        {
            m += (double)mix[i] * mix[i];
            w += (double)wet[i] * wet[i];
        }

        lock (_levelGate)
        {
            _mixSq += m;
            _wetSq += w;
            _levelFrames += frames;
        }
    }

    static void Report()
    {
        long frames = 0, ticks = 0, voiceClamps = 0, mixClamps = 0, underruns = 0, stalls = 0;
        while (true)
        {
            Thread.Sleep(1000);
            var spu = RecompOne.Runtime.Runtime.Spu;
            if (spu == null) continue;

            var s = spu.Stats;
            double mixSq, wetSq;
            long levelFrames;
            lock (_levelGate)
            {
                (mixSq, wetSq, levelFrames) = (_mixSq, _wetSq, _levelFrames);
                (_mixSq, _wetSq, _levelFrames) = (0, 0, 0);
            }

            long f = s.Frames, t = s.Ticks, vc = s.VoiceClamps, mc = s.MixClamps;
            long u = AudioStats.Underruns, st = AudioStats.Stalls;
            var buffers = (f - frames) / (double)FramesPerBuffer;
            var ms = buffers > 0 ? (t - ticks) * 1000.0 / Stopwatch.Frequency / buffers : 0;

            Console.WriteLine(
                $"[audio] voices {Interlocked.Exchange(ref s.ActiveVoicesPeak, 0)}, " +
                $"clamps {vc - voiceClamps} voice / {mc - mixClamps} mix, " +
                $"mix {ms:0.000} ms per {FramesPerBuffer} frames, underruns {u - underruns}, stalls {st - stalls}, " +
                $"level {Db(mixSq, levelFrames)} mix / {Db(wetSq, levelFrames)} wet, " +
                $"reverb {spu.ReverbPreset} via {spu.ReverbPath}, interp {Spu.Interpolation}, " +
                $"xa {(XaAudio.Playing ? $"playing at {XaAudio.SourceRate} Hz" : "idle")}, " +
                $"device {AudioStats.DeviceRate} Hz, resampler {AudioStats.Resampler}");

            (frames, ticks, voiceClamps, mixClamps, underruns, stalls) = (f, t, vc, mc, u, st);
        }
    }

    static string Db(double sumSq, long frames) =>
        frames == 0 || sumSq == 0 ? "-inf dBFS" : $"{10 * Math.Log10(sumSq / (frames * 2) / (32768.0 * 32768.0)):0.0} dBFS";

    static class Dump
    {
        static readonly Channel<(short[] Mix, short[] Wet)> Queue =
            Channel.CreateUnbounded<(short[], short[])>(new UnboundedChannelOptions { SingleReader = true });

        public static void Start(string dir)
        {
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var mix = new WavWriter(Path.Combine(dir, $"kf2-mix-{stamp}.wav"));
            var wet = new WavWriter(Path.Combine(dir, $"kf2-wet-{stamp}.wav"));

            Spu.Mixed += (m, w, n) => Queue.Writer.TryWrite((m[..(n * 2)], w[..(n * 2)]));
            new Thread(() => Drain(mix, wet)) { IsBackground = true, Name = "kf2-audio-dump" }.Start();
            Console.WriteLine($"[KF2] audio dump: {mix.FilePath}, {wet.FilePath}");
        }

        static void Drain(WavWriter mix, WavWriter wet)
        {
            var reader = Queue.Reader;
            var flushed = Environment.TickCount64;
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var block))
                {
                    mix.Write(block.Mix);
                    wet.Write(block.Wet);
                }

                if (Environment.TickCount64 - flushed < 1000) continue;
                mix.Flush();
                wet.Flush();
                flushed = Environment.TickCount64;
            }
        }
    }

    sealed class WavWriter
    {
        public readonly string FilePath;
        readonly FileStream _file;
        long _dataBytes;

        public WavWriter(string path)
        {
            FilePath = path;
            _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _file.Write(new byte[44]);
            Flush();
        }

        public void Write(short[] samples)
        {
            var bytes = MemoryMarshal.AsBytes(samples.AsSpan());
            _file.Write(bytes);
            _dataBytes += bytes.Length;
        }

        // The sizes are rewritten on every flush, so a killed process still leaves a readable file.
        public void Flush()
        {
            Span<byte> h = stackalloc byte[44];
            "RIFF"u8.CopyTo(h);
            BitConverter.TryWriteBytes(h[4..], (int)(36 + _dataBytes));
            "WAVEfmt "u8.CopyTo(h[8..]);
            BitConverter.TryWriteBytes(h[16..], 16);
            BitConverter.TryWriteBytes(h[20..], (short)1);
            BitConverter.TryWriteBytes(h[22..], (short)2);
            BitConverter.TryWriteBytes(h[24..], 44100);
            BitConverter.TryWriteBytes(h[28..], 44100 * 4);
            BitConverter.TryWriteBytes(h[32..], (short)4);
            BitConverter.TryWriteBytes(h[34..], (short)16);
            "data"u8.CopyTo(h[36..]);
            BitConverter.TryWriteBytes(h[40..], (int)_dataBytes);

            var end = _file.Position;
            _file.Position = 0;
            _file.Write(h);
            _file.Position = end;
            _file.Flush();
        }
    }
}
