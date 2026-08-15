using RecompOne.Runtime.Memory;

namespace Kf2.Mods;

/// <summary>
/// Attributes per-frame memory writes to the main-loop stage that made them.
///
///     KF2_MODS=loopprobe=20        report every 20 seconds
///
/// This exists to answer the question 60 fps is blocked on: *which* of the
/// thirteen stages advances the world, and which only reads it. The loop is a
/// flat list with the renderer last, so gating is easy -- the hard part is
/// knowing what to gate, and a stage's write set is the most direct evidence
/// available without a decompilation.
///
/// The watched region is the nine buffers `func_8001369C` clears on entry to an
/// area, which is the game's own declaration of where its per-area state lives:
/// the addresses come straight out of the memset calls at the top of that
/// function. Roughly 66 KB, snapshotted once per stage -- about 6% of a frame,
/// which is affordable because the game thread is asleep for 90% of it anyway.
///
/// Attribution is by difference between consecutive stage entries, so a stage is
/// credited with everything that changed since the previous stage began. That
/// includes anything an interrupt did in between; a word that shows up under
/// every stage is noise, not a finding.
/// </summary>
public sealed class LoopProbeMod : Mod
{
    public override string Name => "loopprobe";
    public override string Summary => "attribute per-frame memory writes to main-loop stages";
    public override string State => $"{_seconds:0.#}s";

    // The per-area state buffers, from the memsets at the top of func_8001369C.
    static readonly (uint Addr, uint Len, string Name)[] Buffers =
    [
        (0x8017E05C, 0x0007, "buf0"),
        (0x8017E084, 0x5F3C, "buf1"),
        (0x80199414, 0x0058, "buf2"),
        (0x801B3084, 0x0E46, "buf3"),
        (0x801C8484, 0x4611, "buf4"),
        (0x80175914, 0x21D1, "buf5"),
        (0x8016C544, 0x24F3, "buf6"),
        (0x8019C5EC, 0x0AA3, "buf7"),
        (0x80198574, 0x03A7, "buf8"),
    ];

    double _seconds = 20;

    // Flattened word view of every watched buffer.
    static readonly uint[] WordAddr = BuildWordAddresses();
    readonly uint[] _prev = new uint[WordAddr.Length];
    readonly uint[] _cur = new uint[WordAddr.Length];
    bool _primed;

    uint _lastStage;
    readonly Dictionary<uint, long> _writes = [];      // stage -> words changed, summed
    readonly Dictionary<uint, long> _entries = [];     // stage -> times entered
    readonly Dictionary<uint, HashSet<int>> _distinct = [];  // stage -> which words, ever
    double _windowStart;

    // How often each word changed, and who last changed it. The words that move
    // every single frame are the per-frame counters -- a tick counter among them
    // is what makes a 60 fps gate measurable instead of a judgement call.
    readonly int[] _churn = new int[WordAddr.Length];
    readonly uint[] _churnBy = new uint[WordAddr.Length];

    // Signed change summed over the window. Net/changes separates the two things
    // a busy word can be: a clock steps by the same amount every time, so its mean
    // is exactly that step; a coordinate wanders, so its mean sits near zero however
    // far it travels. That one number is what makes a tick counter identifiable
    // without knowing anything about the game's data layout.
    readonly long[] _delta = new long[WordAddr.Length];

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

    protected internal override void Configure(string value)
    {
        if (value.Length > 0 && double.TryParse(value, out double s) && s > 0) _seconds = s;
    }

    protected internal override void OnEnabled()
    {
        _windowStart = Now;
        Hooks.StageEntered += OnStage;
        Console.WriteLine($"[KF2] loopprobe: watching {WordAddr.Length} words across " +
                          $"{Buffers.Length} buffers, reporting every {_seconds:0.#}s");
    }

    protected internal override void OnDisabled() => Hooks.StageEntered -= OnStage;

    void OnStage(uint stage, IMemory m)
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

    void Report()
    {
        double window = Now - _windowStart;
        _windowStart = Now;

        Console.WriteLine($"[KF2] loopprobe: words changed per stage entry, over {window:0.#}s");
        foreach (uint stage in Hooks.Stages)
        {
            long entries = _entries.GetValueOrDefault(stage);
            long words = _writes.GetValueOrDefault(stage);
            int distinct = _distinct.TryGetValue(stage, out var set) ? set.Count : 0;
            if (entries == 0) { Console.WriteLine($"    {stage:X8}  (never entered)"); continue; }

            // Which buffers this stage touches, busiest first.
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
                              $"{distinct,6} distinct  {where}" +
                              (stage == Hooks.RenderStage ? "   <- renderer" : ""));
        }

        // The words that changed most often, with the value they hold now. A word
        // moving once per frame outside the display list is a counter; one moving
        // by a constant step is a clock, which is the thing worth watching to
        // check that a gated world really does advance at half the render rate.
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
