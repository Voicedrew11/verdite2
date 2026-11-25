using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Report a disc read that lands on RAM another requester read into and has not yet
/// copied out: an order the console's drive could not produce.
///
///     KF2_DISCRACE=0     no report
///
/// The port's drive completes a read at once (<c>LibCd.CdRead</c>), and a host hitch
/// can deliver several vblanks, so several turns of the game's disc queue
/// (<c>func_8001794C</c>, a vblank callback), inside one frame. On the console a read
/// takes tens of milliseconds, so a requester always gets to copy out what it read
/// before anything else can read over it. Code that relies on that breaks only when
/// the port is fast: that is the class of fault this watches for.
///
/// Most requesters use their data in their read's callback, which the queue runs
/// before it starts the next read, so nothing can race them. A requester whose
/// callback only marks the data ready, and copies it out later from code of its own,
/// is a <em>deferred consumer</em>, listed in <see cref="DeferredConsumers"/>. Its
/// read stays live until that code has run; a read by anyone else over a live one is
/// a race. One a guard repairs (the area loader's, with <see cref="StreamGuard"/>) is
/// counted as handled; any other is <see cref="Unhandled"/>, logged, and a fuzz
/// violation (<c>disc-race</c>). A new deferred consumer is a new row. See "A walked
/// crossing's objects came from a texture" in docs/GAME_INTERNALS.md.
/// </summary>
public static class DiscRace
{
    const uint CdRead = 0x8004B000;        // the game's thunk to LibCd.CdRead(sectors, buf, mode)
    const uint QueueHead = 0x801B6F44;     // u32: the queue slot being served
    const uint SectorBytes = 0x800;        // the game reads with mode 0x80: 2048-byte sectors

    /// <summary>Requesters, by their read callbacks, that copy their reads out after
    /// the callback. Their reads stay live until the consumer has run: for the area
    /// loader, its next call (<see cref="StreamGuard.LoaderRan"/>), or the next frame
    /// when the guard's hooks are off. Not the loader's type 0x40 stream (callback
    /// 0x80016F50): it marks a chunk ready, and the queue uses it before serving the
    /// next slot.</summary>
    static readonly (uint[] Callbacks, string Name)[] DeferredConsumers =
    {
        // func_8001689C: each callback sets the next step, which copies the read out.
        (new uint[] { 0x80016838, 0x8001684C, 0x80016860, 0x80016874, 0x80016888 }, "area loader"),
    };

    static bool _on = true, _queued;
    static readonly List<(uint Lo, uint Hi, int Consumer)> _live = new();
    static long _unhandled, _handled;
    static double _lastLog = double.NegativeInfinity;

    /// <summary>Races no guard repairs: each is a fault waiting for a hitch.</summary>
    public static long Unhandled => _unhandled;

    /// <summary>Races a guard repairs.</summary>
    public static long Handled => _handled;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.discrace",
        Name = "Disc race",
        Version = "1.0",
        Description = "Reports a disc read over RAM another requester has read into and not yet copied out.",
    };

    public static void Configure(string? mode) =>
        _on = !(mode?.Trim() is "0" or "off");

    public static void Install()
    {
        if (!_on)
        {
            Console.WriteLine("[KF2] disc race: off (KF2_DISCRACE=0)");
            return;
        }
        StreamGuard.LoaderRan += () => Consumed(LoaderRow);
        FramePacing.Boundary += () => { if (!StreamGuard.Hooked) Consumed(LoaderRow); };
        HookAttach.OnOverlayLoad("disc race", Attach);
    }

    const int LoaderRow = 0;

    static bool Attach()
    {
        var target = SymbolRegistry.Resolve("game", null, CdRead);
        if (target == null) return false;

        if (!_queued)
        {
            var impl = typeof(DiscRace).GetMethod(nameof(Replace),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            _queued = HookManager.AddReplace(_self, target, impl);
            if (!_queued) return false;
        }

        HookManager.Commit();
        bool ok = HookAttach.Installed(target);
        Console.WriteLine(ok
            ? $"[KF2] disc race: watching the reads at 0x{CdRead:X8}, {DeferredConsumers.Length} deferred consumer(s)"
            : "[KF2] disc race: not installed; reads that race each other go unreported.");
        return ok;
    }

    static void Replace(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        uint lo = c.A1, hi = c.A1 + c.A0 * SectorBytes;
        uint owner = Owner(m);
        int consumer = ConsumerOf(owner);
        orig(c, m);
        if (hi <= lo) return;

        lock (_live)
        {
            foreach (var (pLo, pHi, pConsumer) in _live)
            {
                if (pConsumer == consumer || hi <= pLo || pHi <= lo) continue;
                Report(pConsumer, owner, Math.Max(lo, pLo), Math.Min(hi, pHi));
                break;
            }
            if (consumer >= 0) _live.Add((lo, hi, consumer));
        }
    }

    static void Consumed(int consumer)
    {
        lock (_live) _live.RemoveAll(e => e.Consumer == consumer);
    }

    /// <summary>The head slot's callback names the requester; 0 when the read did
    /// not come through the queue.</summary>
    static uint Owner(IMemory m)
    {
        uint slot = m.ReadU32(QueueHead);
        if (slot - 0x80000000u >= RecompOne.Runtime.Runtime.RamSize) return 0;
        return m.ReadU32(slot + 0x14u);
    }

    /// <summary>The row of <see cref="DeferredConsumers"/> a callback belongs to, or
    /// -1 for a requester that uses its read in the callback.</summary>
    static int ConsumerOf(uint callback)
    {
        for (int i = 0; i < DeferredConsumers.Length; i++)
            if (Array.IndexOf(DeferredConsumers[i].Callbacks, callback) >= 0) return i;
        return -1;
    }

    static void Report(int consumer, uint second, uint lo, uint hi)
    {
        bool handled = consumer == LoaderRow && StreamGuard.Hooked;
        if (handled) _handled++; else _unhandled++;

        double now = Environment.TickCount64;
        if (!handled || now - _lastLog >= 1000)
        {
            _lastLog = now;
            Console.WriteLine($"[KF2] disc race{(handled ? " (the stream guard repairs it)" : "")}: " +
                              $"0x{lo:X8}..0x{hi:X8} read for the {DeferredConsumers[consumer].Name}, then over it " +
                              $"for callback 0x{second:X8} before the {DeferredConsumers[consumer].Name} copied it out " +
                              $"({_unhandled} unhandled, {_handled} handled)");
        }
    }
}
