using System.Runtime.InteropServices;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// A recompiled routine and its C# transcription, run from one state and compared:
/// RAM outside the stack window below the call, the callee-saved registers with SP
/// and RA, and the GTE. **The recompiled result stands**, so a mismatch cannot reach
/// the picture. This is the shape <see cref="TileWalk"/>, <see cref="ModelWalk"/>
/// and <see cref="PolyAssembler"/> each carry a copy of; the RAM helpers are shared
/// with <see cref="Stage13"/>'s replay, which compares a different way.
/// </summary>
sealed class Differential(string tag, string name, uint stackWindow)
{
    byte[] _before = [], _theirs = [];
    readonly Gte.State _gteEntry = new(), _gteTheirs = new(), _gteOurs = new();
    long _calls, _badRam, _badReg, _badGte;
    readonly List<string> _samples = [];
    double _reportAt;

    public void Run(Action<CpuContext, IMemory> orig, CpuContext c, PSMemory mem, Action<CpuContext, PSMemory> ours)
    {
        var ram = Ram(mem);
        if (_before.Length != ram.Length)
        {
            _before = new byte[ram.Length];
            _theirs = new byte[ram.Length];
        }

        uint a0 = c.A0, a1 = c.A1;
        ram.CopyTo(_before);
        var entry = c.Snapshot();
        Gte.Save(_gteEntry);

        orig(c, mem);
        ram.CopyTo(_theirs);
        var theirs = c.Snapshot();
        Gte.Save(_gteTheirs);

        _before.CopyTo(ram);
        c.Restore(entry);
        Gte.Load(_gteEntry);
        ours(c, mem);
        var mine = c.Snapshot();
        Gte.Save(_gteOurs);

        _calls++;
        int hi = (int)(entry.SP & (uint)(ram.Length - 1));
        int lo = Math.Max(0, hi - (int)stackWindow);
        string? diff = Describe(ram, _theirs, 0, lo) ?? Describe(ram, _theirs, hi, ram.Length);
        if (diff != null)
        {
            _badRam++;
            Sample($"a0={a0:X} a1={a1:X}: {diff}");
        }

        if (!CalleeSavedEqual(mine, theirs))
        {
            _badReg++;
            Sample($"a0={a0:X} a1={a1:X}: registers sp {theirs.SP:X}/{mine.SP:X} ra {theirs.RA:X}/{mine.RA:X} " +
                   $"s0 {theirs.S0:X}/{mine.S0:X}");
        }

        if (Gte.Diff(_gteTheirs, _gteOurs) is { } gte)
        {
            _badGte++;
            Sample($"a0={a0:X} a1={a1:X}: GTE {gte}");
        }

        _theirs.CopyTo(ram);
        c.Restore(theirs);
        Gte.Load(_gteTheirs);

        double now = Environment.TickCount64 / 1000.0;
        if (now < _reportAt) return;
        _reportAt = now + 2.0;
        Console.WriteLine($"[{tag}] verify {name}: {_calls} call(s), {_badRam} RAM mismatch(es), " +
                          $"{_badReg} register mismatch(es), {_badGte} GTE mismatch(es)");
        foreach (var s in _samples) Console.WriteLine($"[{tag}]   {s}");
        _samples.Clear();
        _calls = _badRam = _badReg = _badGte = 0;
    }

    void Sample(string s)
    {
        if (_samples.Count < 8) _samples.Add(s);
    }

    /// <summary>Guest RAM as a writable span, for copying whole states in and out.</summary>
    public static Span<byte> Ram(PSMemory mem)
    {
        var ro = mem.Ram;
        return MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(ro), ro.Length);
    }

    /// <summary>What a caller may rely on surviving a call.</summary>
    public static bool CalleeSavedEqual(in CpuSnapshot a, in CpuSnapshot b) =>
        a.S0 == b.S0 && a.S1 == b.S1 && a.S2 == b.S2 && a.S3 == b.S3 && a.S4 == b.S4 && a.S5 == b.S5 &&
        a.S6 == b.S6 && a.S7 == b.S7 && a.FP == b.FP && a.SP == b.SP && a.RA == b.RA;

    /// <summary>How <paramref name="ours"/> differs from <paramref name="theirs"/> over
    /// <c>[from, to)</c>, or null when it does not.</summary>
    public static string? Describe(ReadOnlySpan<byte> ours, ReadOnlySpan<byte> theirs, int from, int to)
    {
        int first = ours[from..to].CommonPrefixLength(theirs[from..to]);
        if (first == to - from) return null;
        int i = from + first, n = 0;
        for (int k = i; k < to; k++) if (ours[k] != theirs[k]) n++;
        return $"{n} byte(s), first 0x{0x80000000u + (uint)i:X8} recompiled {theirs[i]:X2} ours {ours[i]:X2}";
    }
}
