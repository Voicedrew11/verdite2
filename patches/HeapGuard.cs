using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Keep the model heap's compaction <c>func_80017580</c> from writing over the
/// heap's end.
///
///     KF2_HEAPGUARD=0     the recompiled routine alone: the overrun back -- comparison only
///
/// The game keeps its streamed models in a heap of blocks, each a header
/// <c>{u8 state, u32 size, u32 owner}</c> and its data, ended by a <c>0xFF</c>
/// state byte (<c>func_80017664</c>). When an allocation finds no free block,
/// <c>func_80017684</c> calls the compaction, which frees every evictable block
/// (state 1), slides the blocks in use down over the gaps, and writes one free
/// header over what is left: state 0 and size <c>end - dest - 0xC</c>. **With
/// nothing evictable the heap is already packed, <c>dest</c> is the end, and that
/// header lands on the terminator with a size of <c>-0xC</c>**: the end is gone,
/// and the size word is four bytes past the heap. The search that follows takes
/// the header as a free block of 4 GB, so the model is allocated at the heap's end
/// and loaded over whatever lies past it -- in GAME.EXE's heap at
/// <c>0x8009BFE4</c>, the HUD's static models from <c>0x800FAFEC</c>, so the HUD
/// builder faults on a garbage mesh pointer; and a walk that reaches the
/// <c>-0xC</c> block steps onto itself forever (<c>func_80017390</c>), which is the
/// hang. See "The model heap overran its end" in docs/GAME_INTERNALS.md.
///
/// So the terminator and the word after it are read before the compaction and put
/// back after it when they were overwritten. The heap is then full with nothing
/// free, which is what the allocation then answers (0); both callers skip their
/// load on a 0, and the walk asks again next frame, once something has gone
/// evictable.
/// </summary>
public static class HeapGuard
{
    const uint Compact = 0x80017580;
    const byte End = 0xFF;
    const uint Header = 0xC;

    /// <summary>A walk that has not met the terminator in this many blocks is
    /// walking a heap that is already broken, and is not followed.</summary>
    const int MaxBlocks = 4096;

    static bool _on = true, _queued;
    static long _overruns;
    static double _lastLog = double.NegativeInfinity;

    /// <summary>How many compactions would have written over a heap's end.</summary>
    public static long Overruns => _overruns;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.heapguard",
        Name = "Heap guard",
        Version = "1.0",
        Description = "Keeps func_80017580's heap compaction from overwriting the heap's end when nothing can be evicted.",
    };

    public static void Configure(string? mode) =>
        _on = !(mode?.Trim() is "0" or "off");

    public static void Install()
    {
        if (!_on)
        {
            Console.WriteLine("[KF2] heap guard: off (KF2_HEAPGUARD=0); a full model heap is overrun");
            return;
        }
        HookAttach.OnOverlayLoad("heap guard", Attach);
    }

    static bool Attach()
    {
        var target = SymbolRegistry.Resolve("game", null, Compact);
        if (target == null) return false;

        if (!_queued)
        {
            var impl = typeof(HeapGuard).GetMethod(nameof(Replace),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            _queued = HookManager.AddReplace(_self, target, impl);
            if (!_queued) return false;
        }

        HookManager.Commit();
        bool ok = HookAttach.Installed(target);
        Console.WriteLine(ok
            ? $"[KF2] heap guard: on, the compaction at 0x{Compact:X8}"
            : "[KF2] heap guard: not installed; a full model heap is overrun.");
        return ok;
    }

    static void Replace(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        uint heap = c.A0;
        if (Terminator(m, heap) is not uint end)
        {
            orig(c, m);
            return;
        }

        uint stateWord = m.ReadU32(end), sizeWord = m.ReadU32(end + 4u);
        orig(c, m);
        if (m.ReadU8(end) == End) return;

        m.WriteU32(end, stateWord);
        m.WriteU32(end + 4u, sizeWord);
        _overruns++;
        Log(heap);
    }

    /// <summary>The address of the heap's <c>0xFF</c>, or null when the walk leaves
    /// RAM or does not reach it.</summary>
    static uint? Terminator(IMemory m, uint heap)
    {
        uint at = heap;
        for (int i = 0; i < MaxBlocks; i++)
        {
            if (!InRam(at) || !InRam(at + Header)) return null;
            if (m.ReadU8(at) == End) return at;
            uint next = at + Header + m.ReadU32(at + 4u);
            if (next <= at) return null;
            at = next;
        }
        return null;
    }

    static bool InRam(uint address) => address - 0x80000000u < 0x00200000u;

    static void Log(uint heap)
    {
        double now = Environment.TickCount64;
        if (now - _lastLog < 1000) return;
        _lastLog = now;
        Console.WriteLine($"[KF2] heap guard: the heap at 0x{heap:X8} is full with nothing evictable; " +
                          $"its end kept, the allocation refused ({_overruns} so far)");
    }
}
