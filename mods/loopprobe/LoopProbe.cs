// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.LoopProbe;

/// <summary>
/// Attributes per-frame memory writes to the main-loop stage that made them.
///
/// This exists to answer the question 60 fps is blocked on: *which* of the
/// thirteen stages advances the world, and which only reads it. GAME.EXE's loop
/// is the tail of func_8001369C -- a flat list of thirteen calls with a backward
/// branch at 0x80013918 and the renderer last -- so gating is easy. The hard part
/// is knowing what to gate, and a stage's write set is the most direct evidence
/// available without a decompilation.
///
/// The watched region is the nine buffers func_8001369C clears on entry to an
/// area, which is the game's own declaration of where its per-area state lives:
/// the addresses come straight out of the memset calls at the top of that
/// function. Roughly 66 KB, snapshotted once per stage -- about 6% of a frame,
/// which is affordable because the game thread is asleep for 90% of it anyway,
/// but enough to shift the band histogram, so run it deliberately.
///
/// Attribution is by difference between consecutive stage entries, so a stage is
/// credited with everything that changed since the previous stage began. That
/// includes anything an interrupt did in between; a word that shows up under
/// every stage is noise, not a finding.
///
/// Two traps, both learned the hard way:
///   * Take the numbers from a *steady* window. In the window where an area
///     loads, stages 7 and 10 look like the busiest state writers in the loop and
///     write nothing at all once it has finished.
///   * There is no clock in these buffers. Every busy word has a mean signed
///     change of about zero, so "gate a stage and watch a counter halve" needs a
///     counter found somewhere else first.
/// </summary>
public sealed class LoopProbeMod : IMod
{
    // The per-area state buffers, from the memsets at the top of func_8001369C.
    static readonly (uint Addr, uint Len, string Name)[] Buffers =
    [
        (0x8017E05C, 0x0007, "buf0"), (0x8017E084, 0x5F3C, "buf1"), (0x80199414, 0x0058, "buf2"),
        (0x801B3084, 0x0E46, "buf3"), (0x801C8484, 0x4611, "buf4"), (0x80175914, 0x21D1, "buf5"),
        (0x8016C544, 0x24F3, "buf6"), (0x8019C5EC, 0x0AA3, "buf7"), (0x80198574, 0x03A7, "buf8"),
    ];

    // The thirteen calls the main loop makes each iteration, in loop order.
    static readonly uint[] Stages =
    [
        0x8002C944, 0x80037C0C, 0x8002A550, 0x80040348, 0x80046A60, 0x8004910C,
        0x8001689C, 0x80025A1C, 0x800140AC, 0x8002CA74, 0x80016FC8, 0x80014534, 0x800342D8,
    ];
    const uint RenderStage = 0x800342D8;

    static float _seconds = 20f;

    static readonly uint[] WordAddr = BuildWordAddresses();
    static readonly uint[] _prev = new uint[WordAddr.Length];
    static readonly uint[] _cur = new uint[WordAddr.Length];
    static bool _primed;

    static uint _lastStage;
    static readonly Dictionary<uint, long> _writes = [];
    static readonly Dictionary<uint, long> _entries = [];
    static readonly Dictionary<uint, HashSet<int>> _distinct = [];
    static double _windowStart;

    static readonly int[] _churn = new int[WordAddr.Length];
    static readonly uint[] _churnBy = new uint[WordAddr.Length];

    // Signed change summed over the window. Net/changes separates the two things a
    // busy word can be: a clock steps by the same amount every time, so its mean is
    // exactly that step; a coordinate wanders, so its mean sits near zero however
    // far it travels. That one number is what would make a tick counter findable
    // without knowing anything about the game's data layout.
    static readonly long[] _delta = new long[WordAddr.Length];

    static double Now => Environment.TickCount64 / 1000.0;

    static uint[] BuildWordAddresses()
    {
        var list = new List<uint>();
        foreach (var (addr, len, _) in Buffers)
            for (uint a = addr & ~3u; a < addr + len; a += 4) list.Add(a);
        return [.. list];
    }

    static string BufferOf(uint address)
    {
        foreach (var (addr, len, name) in Buffers)
            if (address >= addr && address < addr + len) return name;
        return "?";
    }

    public void OnLoad()
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("KF2_LOOPPROBE"), out double s) && s > 0)
            _seconds = (float)s;
        _windowStart = Now;
        Console.WriteLine($"[loopprobe] watching {WordAddr.Length} words across " +
                          $"{Buffers.Length} buffers, reporting every {_seconds:0.#}s");
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped($"Snapshots {WordAddr.Length} words at every main-loop stage " +
                          "boundary and attributes each change to the stage that ran before " +
                          "it. Costs about 6% of a frame and shifts the band histogram.");
        ImGui.SliderFloat("Report interval (s)", ref _seconds, 5f, 120f);
    }

    // One thin entry point per stage: the pre-hook signature carries no address.
    [PreHook("game", Address = 0x8002C944)] static void S00(CpuContext c, IMemory m) => Stage(0x8002C944, m);
    [PreHook("game", Address = 0x80037C0C)] static void S01(CpuContext c, IMemory m) => Stage(0x80037C0C, m);
    [PreHook("game", Address = 0x8002A550)] static void S02(CpuContext c, IMemory m) => Stage(0x8002A550, m);
    [PreHook("game", Address = 0x80040348)] static void S03(CpuContext c, IMemory m) => Stage(0x80040348, m);
    [PreHook("game", Address = 0x80046A60)] static void S04(CpuContext c, IMemory m) => Stage(0x80046A60, m);
    [PreHook("game", Address = 0x8004910C)] static void S05(CpuContext c, IMemory m) => Stage(0x8004910C, m);
    [PreHook("game", Address = 0x8001689C)] static void S06(CpuContext c, IMemory m) => Stage(0x8001689C, m);
    [PreHook("game", Address = 0x80025A1C)] static void S07(CpuContext c, IMemory m) => Stage(0x80025A1C, m);
    [PreHook("game", Address = 0x800140AC)] static void S08(CpuContext c, IMemory m) => Stage(0x800140AC, m);
    [PreHook("game", Address = 0x8002CA74)] static void S09(CpuContext c, IMemory m) => Stage(0x8002CA74, m);
    [PreHook("game", Address = 0x80016FC8)] static void S10(CpuContext c, IMemory m) => Stage(0x80016FC8, m);
    [PreHook("game", Address = 0x80014534)] static void S11(CpuContext c, IMemory m) => Stage(0x80014534, m);
    [PreHook("game", Address = 0x800342D8)] static void S12(CpuContext c, IMemory m) => Stage(0x800342D8, m);

    static void Stage(uint stage, IMemory m)
    {
        for (int i = 0; i < WordAddr.Length; i++) _cur[i] = m.ReadU32(WordAddr[i]);

        if (_primed)
        {
            long changed = 0;
            var distinct = _distinct.TryGetValue(_lastStage, out var set) ? set : _distinct[_lastStage] = [];
            for (int i = 0; i < _cur.Length; i++)
                if (_cur[i] != _prev[i])
                {
                    changed++;
                    distinct.Add(i);
                    _churn[i]++;
                    _churnBy[i] = _lastStage;
                    _delta[i] += (long)(int)_cur[i] - (int)_prev[i];
                }

            _writes[_lastStage] = _writes.GetValueOrDefault(_lastStage) + changed;
            _entries[_lastStage] = _entries.GetValueOrDefault(_lastStage) + 1;
        }

        Array.Copy(_cur, _prev, _cur.Length);
        _lastStage = stage;
        _primed = true;

        if (Now - _windowStart >= _seconds) Report();
    }

    static void Report()
    {
        double window = Now - _windowStart;
        _windowStart = Now;

        Console.WriteLine($"[loopprobe] words changed per stage entry, over {window:0.#}s");
        foreach (uint stage in Stages)
        {
            long entries = _entries.GetValueOrDefault(stage);
            long words = _writes.GetValueOrDefault(stage);
            _distinct.TryGetValue(stage, out var set);
            if (entries == 0) { Console.WriteLine($"    {stage:X8}  (never entered)"); continue; }

            var byBuffer = new Dictionary<string, int>();
            if (set != null)
                foreach (int i in set)
                {
                    string b = BufferOf(WordAddr[i]);
                    byBuffer[b] = byBuffer.GetValueOrDefault(b) + 1;
                }
            string where = string.Join(" ", byBuffer.OrderByDescending(kv => kv.Value)
                                                    .Take(4).Select(kv => $"{kv.Key}:{kv.Value}"));

            Console.WriteLine($"    {stage:X8}  {(double)words / entries,8:F1} words/entry  " +
                              $"{set?.Count ?? 0,6} distinct  {where}" +
                              (stage == RenderStage ? "   <- renderer" : ""));
        }

        var top = Enumerable.Range(0, _churn.Length)
                            .Where(i => _churn[i] > 0 && BufferOf(WordAddr[i]) != "buf1")
                            .OrderByDescending(i => _churn[i])
                            .Take(10).ToList();
        if (top.Count > 0)
        {
            Console.WriteLine("    busiest words outside the display list " +
                              "(mean == step => clock, mean ~ 0 => wanders):");
            foreach (int i in top)
                Console.WriteLine($"      {WordAddr[i]:X8} ({BufferOf(WordAddr[i])})  " +
                                  $"changed {_churn[i]}x  mean {(double)_delta[i] / _churn[i],12:+#0.###;-#0.###;0}  " +
                                  $"now {(int)_prev[i],12}  by {_churnBy[i]:X8}");
        }

        _writes.Clear();
        _entries.Clear();
        _distinct.Clear();
        Array.Clear(_churn);
        Array.Clear(_delta);
    }
}
