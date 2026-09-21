using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Compile the recompiled code before the game runs it, on a background thread,
/// so walking into an area does not stop to JIT it.
///
///     KF2_PREJIT=0        leave every method to be compiled on first call
///     KF2_PREJIT_PROBE=1  a line per overlay as it is warmed
///
/// ## The defect
///
/// `TieredCompilationQuickJit` is off and must stay off -- tier-up recompiles a
/// hooked method and MonoMod's detour does not follow -- so **every** recompiled
/// function is compiled by the full optimizing JIT the first time it is called,
/// on the thread that called it. An area change is the largest batch of first
/// calls the game makes, and it makes them inside one frame. Measured at boot
/// with `KF2_PROFILE_SPIKE=30`, first entry to `fdat02`:
///
/// | frame | work | of which JIT | section |
/// |---|---|---|---|
/// | 358 | 297.87 ms | 292.37 ms, 234 methods | the assemblers, the model walk, stage 13 |
/// | 580 | 179.95 ms | 174.88 ms, 66 methods | stages 4, 3 and 2 |
/// | 1362 | 102.28 ms | 100.71 ms, 12 methods | stage 5, the first projectile |
///
/// The control is the same transition into a module the session has already run:
/// area 1 to area 0 with `fdat02` warm cost **15 ms**, a max frame of 25.93 ms
/// and `JIT 6.06 ms/s`. So it is the compile and not the disc -- an emulated CD
/// read out of the host's page cache is microseconds.
///
/// **It is worst when walking**, which is why it reads as a hitch rather than a
/// load: the full-load path (`func_80024154`, used by a save load and by
/// <see cref="AreaWarp"/>) runs the loading screen that <see cref="LoadPacing"/>
/// paces, and the JIT lands inside something that already looks like a load. A
/// boundary crossing is stage 2's own object state machine -- an object with
/// state `0xE0`, handler `0x80038DC8`, which calls `0x800162DC` directly -- and
/// that path enters neither wait `LoadPacing` hooks, so the loading figure never
/// steps and the freeze is naked, mid-stride.
///
/// ## Preparing rather than tiering
///
/// <c>RuntimeHelpers.PrepareMethod</c> compiles a body without calling it, and
/// the set to compile is already enumerable: every overlay is registered before
/// the first `Dispatcher.Load`, so `Functions` holds a delegate per recompiled
/// function and <c>Delegate.Method</c> is the method the JIT would compile
/// later. The port's own assembly and the runtime go in the same pass -- a
/// replace hook is an ordinary managed method, and so is the GTE fast path it
/// spends its time in, so both are compiled on first call like everything else.
/// That is why `PolyAssembler.Replace` and `ModelWalk.ReplaceWalk` are named in
/// the frame above, and the area modules are *not* the bulk: `fdat02` declares
/// ten functions. The cost is `game` (1035), the patches (1933) and the runtime
/// (3111).
///
/// **The thread starts at the first overlay load, not at the area's.** The load
/// returns straight into the first frame of the new area, so preparing the
/// incoming module from its own <c>OverlayLoadedEvent</c> would race the frames
/// it is meant to protect. It starts during `main`, which leaves the intro and
/// the title -- seconds of idle CPU -- to cover the whole assembly, and the area
/// modules are warmed first because they are what hitches.
///
/// Lowest priority and a yield every 32 methods, so a machine with few cores
/// spends its cores on the picture; background, so it cannot hold up an exit.
/// A method <see cref="HookManager"/> has already committed is left alone --
/// MonoMod prepares its target when it installs the detour, so there is nothing
/// to gain and one less thing to do concurrently with a detour being written.
///
/// See "The first frame of an area was the JIT" in docs/DEVELOPMENT.md.
/// </summary>
public static class Prejit
{
    /// <summary>Methods between two yields. Lowest priority is what actually
    /// keeps this off the game thread's back; the yield is for a host whose
    /// scheduler will not preempt on priority alone.</summary>
    const int YieldEvery = 32;

    public static bool Enabled { get; private set; } = true;

    static bool _probe;
    static Thread? _thread;

    /// <summary>Methods compiled by the pass, and whether it has finished. Read
    /// by the probe line; nothing gates on them.</summary>
    public static int Prepared { get; private set; }

    public static bool Done { get; private set; }

    public static void Configure(string? enabled, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
        if (!string.IsNullOrWhiteSpace(probe)) _probe = probe != "0";
    }

    /// <summary>
    /// Arm the pass. Deferred to the first <see cref="OverlayLoadedEvent"/>
    /// because that is the first moment the dispatcher's registry is populated,
    /// and installed last in Program.cs so the patches' own attach listeners have
    /// run -- and their hooks committed -- before the first method is prepared.
    /// </summary>
    public static void Install()
    {
        if (!Enabled)
        {
            Console.WriteLine("[KF2] prejit: off, every method compiles on its first call");
            return;
        }

        Event.AddListener<OverlayLoadedEvent>(_ => Start());
    }

    static void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Warm)
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "kf2-prejit",
        };
        _thread.Start();
        Console.WriteLine("[KF2] prejit: warming the recompiled code on a background thread");
    }

    static void Warm()
    {
        var clock = Stopwatch.StartNew();
        var seen = new HashSet<RuntimeMethodHandle>();
        int prepared = 0, left = 0, refused = 0, step = 0;

        foreach (var (label, methods) in Batches())
        {
            double t0 = clock.Elapsed.TotalMilliseconds;
            int n = 0;

            foreach (var mi in methods)
            {
                if (mi == null || !seen.Add(mi.MethodHandle)) continue;

                switch (Prepare(mi))
                {
                    case Verdict.Prepared: prepared++; n++; break;
                    case Verdict.LeftAlone: left++; break;
                    default: refused++; break;
                }

                if (++step % YieldEvery == 0) Thread.Yield();
            }

            if (_probe)
                Console.WriteLine($"[KF2] prejit: {label} {n} method(s) in " +
                                  $"{clock.Elapsed.TotalMilliseconds - t0:0} ms");
        }

        Prepared = prepared;
        Done = true;
        Console.WriteLine($"[KF2] prejit: {prepared} method(s) compiled in " +
                          $"{clock.Elapsed.TotalMilliseconds:0} ms, {left} left to their hook" +
                          (refused > 0 ? $", {refused} refused" : ""));
    }

    enum Verdict { Prepared, LeftAlone, Refused }

    static Verdict Prepare(MethodInfo mi)
    {
        // A generic definition has no code until it is instantiated, and an
        // abstract or extern method has no body at all; PrepareMethod throws on
        // both rather than reporting it.
        if (mi.IsAbstract || mi.IsGenericMethodDefinition || mi.ContainsGenericParameters)
            return Verdict.LeftAlone;

        if (HookManager.IsCommitted(mi)) return Verdict.LeftAlone;

        try
        {
            RuntimeHelpers.PrepareMethod(mi.MethodHandle);
            return Verdict.Prepared;
        }
        catch
        {
            // Nothing here is load-bearing: a method that will not prepare is a
            // method that compiles on its first call, which is where it was.
            return Verdict.Refused;
        }
    }

    /// <summary>
    /// Ordered by what a walk into an area needs, because the pass is racing the
    /// player: the nine area modules (only 10-19 functions each, so 0.3 s for the
    /// lot), then the port's own replace hooks and the runtime they spend their
    /// time in -- which is what the first frame of an area is -- then `game`,
    /// which holds the stages and the renderer and is the bulk at 1035. `open`
    /// and `end` are last: the title has already run by the time the pass reaches
    /// it, and the ending is an hour away.
    /// </summary>
    static IEnumerable<(string Label, IEnumerable<MethodInfo?> Methods)> Batches()
    {
        var overlays = Dispatcher.Overlays;
        var names = overlays.Keys.ToList();
        names.Sort(static (a, b) => Rank(a) != Rank(b)
            ? Rank(a) - Rank(b)
            : string.CompareOrdinal(a, b));

        foreach (var name in names)
        {
            if (Rank(name) > 0) break;
            yield return (name, Functions(overlays, name));
        }

        yield return ("patches", PortMethods());
        yield return ("runtime", AssemblyMethods(typeof(HookManager).Assembly, null));

        foreach (var name in names)
        {
            if (Rank(name) == 0) continue;
            yield return (name, Functions(overlays, name));
        }
    }

    static IEnumerable<MethodInfo?> Functions(IReadOnlyDictionary<string, IOverlay> overlays, string name)
    {
        IReadOnlyDictionary<uint, Action<CpuContext, IMemory>>? fns = null;
        try { fns = overlays[name].Functions; }
        catch { }
        return fns == null ? [] : fns.Values.Select(static f => f.Method);
    }

    static int Rank(string name) =>
        name.StartsWith("fdat", StringComparison.OrdinalIgnoreCase) ? 0
        : name.Equals("game", StringComparison.OrdinalIgnoreCase) ? 1
        : name.Equals("main", StringComparison.OrdinalIgnoreCase) ? 2
        : 3;

    /// <summary>`generated/` and `patches/` compile into one assembly, so the
    /// recompiled functions above and these come out of the same place; the
    /// namespace is what separates them.</summary>
    static IEnumerable<MethodInfo?> PortMethods()
        => AssemblyMethods(typeof(Prejit).Assembly, "Kf2");

    /// <summary>Every method an assembly declares, optionally under one namespace
    /// root. The runtime goes in whole: a replace hook's own time is spent in the
    /// GTE fast path, `GteVertexMap` and `GlCore`, and those are compiled on
    /// first call like everything else.</summary>
    static IEnumerable<MethodInfo?> AssemblyMethods(Assembly asm, string? ns)
    {
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }

        foreach (var t in types)
        {
            if (t == null || t.ContainsGenericParameters) continue;
            if (ns != null && (t.Namespace == null || !t.Namespace.StartsWith(ns, StringComparison.Ordinal)))
                continue;

            MethodInfo[] methods;
            try
            {
                methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Static | BindingFlags.Instance |
                                       BindingFlags.DeclaredOnly);
            }
            catch { continue; }

            foreach (var mi in methods) yield return mi;
        }
    }
}
