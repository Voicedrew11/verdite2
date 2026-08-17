using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// How close the game's per-frame primitive buffer comes to running out, and what
/// it costs when it does.
///
///     KF2_PRIMBUF_PROBE=1     peak usage, capacity and overflows, on the console
///
/// `func_8002DF80` hands out two buffers of `0x19000` bytes, one per frame, from
/// `0x800FC99C` and `0x8011599C`, and keeps each one's `{start, end, current}` in a
/// 12-byte descriptor at `0x8017E08C` / `0x8017E098`, with `0x8017E0A4` pointing at
/// whichever is this frame's. A `POLY_GT4` is `0x34` bytes, so the frame's budget is
/// **1969 quads** — and the two buffers run back to back into `0x8012E99C`, which is
/// the view clipper's near-plane word, so there is no slack after them to grow into.
///
/// `func_80030540` allocates out of that buffer per polygon and, when the bump
/// passes `end`, **abandons the rest of the call**:
///
/// <code>
/// cur = *(desc + 8); *(desc + 8) = cur + 0x34;
/// if (*(desc + 4) &lt; *(desc + 8)) return;     // the rest of this object is dropped
/// </code>
///
/// Which matters here because widening the cull cone spends exactly this budget.
/// NOTES.md measures King's Field at about a thousand primitives a frame at 4:3
/// against a capacity of 1969, and opening the cone to 16:9 lit 31% more tiles. A
/// frame that runs out loses whatever it had not drawn yet — occasionally, in the
/// busiest scenes, which is the shape of "the floor and ceiling cull at the corners
/// sometimes" far better than any frustum does.
///
/// This is a measurement, not a fix: it reads the descriptor after every
/// `func_80030540` and reports the high-water mark. See "The cull the margin runs
/// into" in NOTES.md.
/// </summary>
public static class PrimBuffer
{
    /// <summary>Points at this frame's `{start, end, current}` descriptor.</summary>
    const uint ActiveDescriptor = 0x8017E0A4;

    /// <summary>func_80030540, the polygon assembler that allocates out of it.</summary>
    const uint Assembler = 0x80030540;

    /// <summary>A POLY_GT4, which is what the world is made of.</summary>
    const int PacketBytes = 0x34;

    static bool _measure;

    static long _calls, _overflows, _peak, _capacity;
    static double _windowStart;

    static double Now => Environment.TickCount64 / 1000.0;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.primbuffer",
        Name = "Primitive buffer probe",
        Version = "1.0",
        Description = "Reports how close the frame's primitive buffer comes to running out.",
    };

    public static void Configure(string? probe)
    {
        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
            _measure = true;
    }

    public static void Install()
    {
        if (!_measure) return;

        _windowStart = Now;
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;

            SymbolRegistry.Build();
            var target = SymbolRegistry.Resolve("game", null, Assembler);
            var impl = typeof(PrimBuffer)
                .GetMethod(nameof(AfterAssemble), BindingFlags.Public | BindingFlags.Static)!;

            if (target == null || !HookManager.AddPost(_self, target, impl))
            {
                Console.Error.WriteLine($"[KF2] primbuf: nothing hooked at game/0x{Assembler:X8}");
                return;
            }

            HookManager.Commit();
            Console.WriteLine("[KF2] primbuf: probing");
        });
    }

    public static void AfterAssemble(CpuContext c, IMemory m)
    {
        uint desc = m.ReadU32(ActiveDescriptor);
        if (desc == 0) return;

        uint start = m.ReadU32(desc), end = m.ReadU32(desc + 4u), cur = m.ReadU32(desc + 8u);
        if (start == 0 || end <= start) return;

        _calls++;
        _capacity = end - start;
        long used = (long)cur - start;
        if (used > _peak) _peak = used;
        if (cur > end) _overflows++;

        double now = Now;
        if (now - _windowStart < 2.0) return;

        Console.WriteLine($"[primbuf] peak {_peak}/{_capacity} bytes " +
                          $"({(_capacity > 0 ? 100.0 * _peak / _capacity : 0):0.0}%, " +
                          $"{_peak / PacketBytes} of {_capacity / PacketBytes} packets), " +
                          $"{_overflows} overflow(s) in {_calls} calls");

        _windowStart = now;
        _calls = _overflows = _peak = 0;
    }
}
