using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Keep what the area loader read into its buffer until the loader has copied it out,
/// even when another disc read lands there first.
///
///     KF2_STREAMGUARD=0     the recompiled loader alone: a walked crossing can load
///                           an area's objects from a texture's sectors -- comparison only
///
/// A walked crossing streams the next area in across frames (<c>func_8001689C</c>,
/// stage 7). Each step's read lands in the buffer at <c>0x801B6FA8</c>, its callback
/// sets the next step, and that step copies the buffer out on the loader's next
/// call: the map at step 1, the descriptors, creatures and objects at step 3. The
/// texture loader (callback <c>0x80014410</c>) reads the second part of a picture into
/// the same buffer. On the console a read takes tens of milliseconds, so the loader
/// always gets to its copy first. Here every read completes at once, and the disc
/// queue (<c>func_8001794C</c>, run from each vblank) can retire the loader's read,
/// a model, a palette and the picture's second part between two calls of the
/// loader: a hitch that delivers several vblanks inside one frame is enough. The
/// picture's sectors are then copied in as the area's objects. Seen walking area 1
/// into area 2 (<c>fdat08</c>) on 2026-11-25 and in fuzz seed 1024: 18 sectors from
/// LBA 7928 over the object data, then a crash in whichever routine touched the
/// garbage first (<c>func_80032CD8</c>, <c>func_8003B9EC</c>). See "A walked
/// crossing's objects came from a texture" in docs/GAME_INTERNALS.md.
///
/// So when a loader read into the buffer completes, the buffer is kept, and if it
/// has changed by the time the loader runs the step that read set, it is put back
/// first. Nothing is held or delayed: holding the queue instead deadlocked, since a
/// crossing object waits in stage 2 for the queue to drain while the loader waits
/// for stage 7. Whatever read over the buffer has already been handled; a picture
/// whose second part had not yet been checked fails the queue's checksum
/// (<c>func_80017DB0</c>) and is read again, which is the game's own retry.
/// </summary>
public static class StreamGuard
{
    const uint Pump = 0x8001794C;
    const uint Loader = 0x8001689C;
    const uint QueueHead = 0x801B6F44;   // u32: the slot being served
    const uint LoaderStep = 0x8017E05E;  // u16: the next step, 0xF0 while a read is in flight
    const ushort StepWaiting = 0xF0;
    const uint Buffer = 0x801B6FA8;

    /// <summary>The largest loader read into the buffer: step 0's map, 0x11000
    /// bytes. It ends before the map block at 0x801C8484.</summary>
    const int Kept = 0x11000;

    /// <summary>The loader's read callbacks all lie in <c>func_8001689C</c>'s
    /// neighbourhood (0x80016838 .. 0x80016F50).</summary>
    const uint LoaderCodeStart = 0x80016800, LoaderCodeEnd = 0x80017000;

    static bool _on = true, _queued;
    static readonly byte[] _kept = new byte[Kept];
    static bool _holding;
    static ushort _forStep;
    static long _restored;
    static double _lastLog = double.NegativeInfinity;

    /// <summary>How many times the loader's buffer was put back.</summary>
    public static long Restored => _restored;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.streamguard",
        Name = "Stream guard",
        Version = "1.0",
        Description = "Puts back what the area loader read into its buffer when another disc read landed there before the loader copied it out.",
    };

    public static void Configure(string? mode) =>
        _on = !(mode?.Trim() is "0" or "off");

    public static void Install()
    {
        if (!_on)
        {
            Console.WriteLine("[KF2] stream guard: off (KF2_STREAMGUARD=0); a texture can overwrite a walked crossing's objects");
            return;
        }
        HookAttach.OnOverlayLoad("stream guard", Attach);
    }

    static bool Attach()
    {
        var pump = SymbolRegistry.Resolve("game", null, Pump);
        var loader = SymbolRegistry.Resolve("game", null, Loader);
        if (pump == null || loader == null) return false;

        if (!_queued)
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            _queued = HookManager.AddReplace(_self, pump, typeof(StreamGuard).GetMethod(nameof(OnPump), flags)!)
                   && HookManager.AddReplace(_self, loader, typeof(StreamGuard).GetMethod(nameof(OnLoader), flags)!);
            if (!_queued) return false;
        }

        HookManager.Commit();
        bool ok = HookAttach.Installed(pump) && HookAttach.Installed(loader);
        Console.WriteLine(ok
            ? $"[KF2] stream guard: on, the disc queue at 0x{Pump:X8} and the area loader at 0x{Loader:X8}"
            : "[KF2] stream guard: not installed; a texture can overwrite a walked crossing's objects.");
        return ok;
    }

    /// <summary>The queue serves one slot a call. When the head is a loader read
    /// into the buffer and the call is the one that ran its callback (the step goes
    /// from waiting to a step), the buffer is what that step will copy out.</summary>
    static void OnPump(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        bool loaderRead = IsLoaderRead(m, m.ReadU32(QueueHead));
        ushort before = m.ReadU16(LoaderStep);
        orig(c, m);
        if (!loaderRead || before != StepWaiting) return;

        ushort after = m.ReadU16(LoaderStep);
        if (after == StepWaiting) return;
        Keep(m, after);
    }

    static void OnLoader(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (_holding && m.ReadU16(LoaderStep) == _forStep) Restore(m);
        orig(c, m);
        if (_holding && m.ReadU16(LoaderStep) != _forStep) _holding = false;
    }

    static bool IsLoaderRead(IMemory m, uint slot)
    {
        if (slot - 0x80000000u >= RecompOne.Runtime.Runtime.RamSize) return false;
        if (m.ReadU8(slot) == 0) return false;
        if (m.ReadU32(slot + 0xCu) != Buffer) return false;
        uint callback = m.ReadU32(slot + 0x14u);
        return callback >= LoaderCodeStart && callback < LoaderCodeEnd;
    }

    static void Keep(IMemory m, ushort step)
    {
        for (int i = 0; i < Kept; i += 4)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_kept.AsSpan(i), m.ReadU32(Buffer + (uint)i));
        _holding = true;
        _forStep = step;
    }

    static void Restore(IMemory m)
    {
        int changed = 0;
        for (int i = 0; i < Kept; i += 4)
        {
            uint kept = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_kept.AsSpan(i));
            if (m.ReadU32(Buffer + (uint)i) == kept) continue;
            m.WriteU32(Buffer + (uint)i, kept);
            changed++;
        }
        if (changed == 0) return;
        _restored++;
        Log(changed);
    }

    static void Log(int words)
    {
        double now = Environment.TickCount64;
        if (now - _lastLog < 1000) return;
        _lastLog = now;
        Console.WriteLine($"[KF2] stream guard: another read had overwritten {words * 4} bytes of the area loader's buffer " +
                          $"before step {_forStep}; put back ({_restored} so far)");
    }
}
