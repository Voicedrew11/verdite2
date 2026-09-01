using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Refuse a hit resolution that would read a non-pointer as a pointer, and say
/// which of the four impossible things it found.
///
///     KF2_HITGUARD=0     let the read fault, which is a hard crash -- comparison
///     KF2_HITPROBE=1     also report a census of what the hit check saw
///     KF2_HITPROBE=2     also report every call, which is very loud
///
/// ## The crash this exists for
///
/// `docs/TODO.md` #14. Reported from play: the final boss dies, a cutscene walks
/// the player to the Moonlight Sword and takes it, and **after that** the game
/// dies -- above the tick rate, not at it. The standing guess was `fdat23`'s two
/// post-boss modal loops and therefore <see cref="LoopPacing"/>'s redraws. The
/// first stack trace of it has no `fdat23` frame anywhere:
///
///     func_8001369C   the main loop
///      +- func_8002A550    stage 3, the player and weapon stage
///         +- func_800271D0    the equipped weapon's per-tick reach scan
///            +- func_8003A9CC    resolve a hit against one entity
///               +- func_8003A490
///                  +- func_8003A448   ReadU8(0x0FFF0000) -> unmapped address
///
/// ## What the routine does, and the four ways it has no defence
///
/// `func_800271D0` reads the equipped weapon's record from `0x80199494` and scans
/// the entity table for creatures inside the weapon's reach. For each candidate
/// it takes `u8[entity+0x2]` -- the creature **type** -- and indexes a per-area
/// descriptor table to find that creature's size:
///
///     desc = 0x80172624 + u8[entity+0x2] * 120
///
/// That table is copied in per area by `func_80017244(0x80172624, src, 0xCB0)` --
/// `0xCB0` **words**, so the block ends at `0x801758E4`. When a hit lands it calls
/// `func_8003A9CC` with the entity's index, which re-derives the same record
/// (redirecting through `s16[rec+0x22]` when the kind byte is 3), re-derives the
/// same descriptor, and hands it to `func_8003A490` -- which walks fifteen
/// pointers at `desc+0x38` and does `ReadU8` on each.
///
/// **Nothing on that path validates anything.** No bound on the index, none on
/// the type byte, and no check that the slot is occupied -- a free slot holds
/// `0xFF` at `+0x0`, and the code tests that byte for `3` (the redirect) and never
/// for `0xFF`. So `0x0FFF0000` is not a pointer that went bad; it is whatever
/// bytes sit at `desc+0x38` once `desc` has walked out of the loaded block.
///
/// ## Why refusing the call is the right shape, and where the argument is thin
///
/// **The console did not stop here.** The PS1 has no MMU, `0x0FFF0000` is
/// unmapped KUSEG, and a load there returns open-bus rather than trapping -- the
/// game shipped, and people finished it. `PSMemory` throwing is a *port*
/// behaviour, deliberately, because it is what makes a genuine recompilation bug
/// visible instead of silent. Here it converts a garbage read the hardware
/// absorbed into a crash that costs the player the ending.
///
/// **That is the load-bearing assumption and it is not verified.** It is a claim
/// about the hardware, reasoned from the PS1 having no fault path a game would
/// survive, not a reading of one. If the console *did* trap here, this guard is
/// papering over a state the original never reached, and the root cause matters
/// more than the symptom.
///
/// So the intervention is kept as narrow as it can be. It does not weaken
/// `PSMemory`; it is one function, and it only refuses the call in the four
/// states where the read is **certain** to fault or the index is arithmetically
/// impossible:
///
/// * the index is `>= 200`, past the entity table;
/// * the kind-3 redirect points outside the table;
/// * the descriptor's pointer block reaches past `0x801758E4`, so it was never
///   loaded;
/// * a pointer in that block is neither zero nor a RAM address -- which *is* the
///   read that throws.
///
/// **A free slot is reported and not refused.** That the kind byte reads `0xFF`
/// is the shape you would expect a frame after something dies, and it is the
/// best current guess at the cause -- but it is an inference, not a guaranteed
/// fault, and refusing on it would silently drop hits the game meant to land.
/// Skipping a swing's damage is a worse failure than a log line.
///
/// ## The guard is also the diagnostic
///
/// This is the reason it is on by default rather than a probe you remember to
/// turn on. `func_8003A9CC` is void to all four of its callers -- none reads `V0`
/// -- so a refused call costs one hit against a record that is not a creature,
/// which is nothing. What it buys is that the next playthrough **reports which of
/// the four it was, and keeps running**, instead of dying with a stack trace that
/// cannot say. Measured cost: the hit check resolves about 1.4 times a second in
/// combat, so the checks are free, and over a boss fight and 40 swings it found
/// nothing to refuse -- widest index 57 of 200, widest type 9 against a block
/// that holds ~108, both far from their bounds.
///
/// Every read below is fenced by <see cref="Ram"/>, so the guard cannot fault on
/// the state it is describing, and it writes nothing to game memory.
/// </summary>
internal static class HitGuard
{
    /// <summary>`func_8003A9CC(a0 = entity index, ...)`, the hit resolution. `c.RA`
    /// at the pre names the call site, which is worth having because
    /// `func_800271D0` is one of four callers.</summary>
    const uint HitResolve = 0x8003A9CC;

    // The entity table, and the per-area creature-type descriptors immediately
    // after it. Both read off the recompiled func_8003A9CC prologue:
    // `0x80170000 - 0x3ABC` is the table, `+ 0x60E0` the descriptors.
    const uint EntityBase = 0x8016C544;
    const int EntityStride = 0x7C;
    const int EntityCount = 0xC8;          // 200
    const int KindOff = 0x0;               // 0xFF while the slot is free
    const int RedirectKind = 0x3;          // resolve against s16[rec+0x22] instead
    const int RedirectOff = 0x22;
    const int TypeOff = 0x2;               // index into the descriptors below

    const uint DescBase = 0x80172624;      // EntityBase + 0x60E0
    const int DescStride = 120;            // (t << 4) - t, then << 3
    const int DescPtrOff = 0x38;           // fifteen pointers, tagged by u8[ptr]
    const int DescPtrCount = 15;

    /// <summary>The end of the block `func_80017244(0x80172624, src, 0xCB0)` copies
    /// in -- `0xCB0` *words*. A descriptor reaching past this was never loaded,
    /// whatever the type byte says.</summary>
    const uint DescBlockEnd = DescBase + 0xCB0u * 4u;

    /// <summary>Refuse a call that would fault. On by default; `KF2_HITGUARD=0` is
    /// the comparison, and the comparison is a hard crash.</summary>
    public static bool Guard = true;

    public static bool Probe;
    public static bool Verbose;

    /// <summary>Distinct findings printed in full before the class falls back to
    /// counting, so the first one -- the only one that matters -- is not pushed out
    /// of the scrollback by a swing repeating it every tick.</summary>
    const int MaxReports = 24;

    static int _calls, _reports;
    static int _badIndex, _badRedirect, _freeSlot, _badType, _badPointer, _refused;
    static int _maxIdx = -1, _maxType = -1;
    static readonly HashSet<long> _seen = [];

    /// <summary>Main-loop stage 3, `func_8002A550` -- the same function
    /// `func_800271D0` is called from. Polled after it, behind `KF2_HITPROBE`, to
    /// catch the *first tick* a malformed record exists rather than the first tick
    /// something walks into one.</summary>
    const uint PlayerStage = 0x8002A550;

    static double _windowStart;
    const double WindowSeconds = 10.0;
    static double Now => Environment.TickCount64 / 1000.0;

    static bool _watchFired;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.hitguard",
        Name = "Hit guard",
        Version = "1.0",
        Description = "Refuses a hit resolution that would read a non-pointer as a pointer, and names what it found.",
    };

    public static void Configure(string? guard, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(guard)) Guard = guard != "0";
        if (!string.IsNullOrWhiteSpace(probe))
        {
            Probe = probe != "0";
            Verbose = probe == "2";
        }
    }

    public static void Install()
    {
        if (!Guard && !Probe) return;

        // The descriptor table is per area, so the loaded count it caches is too.
        Event.AddListener<OverlayLoadedEvent>(_ => ForgetArea());

        HookAttach.OnOverlayLoad("hit guard", Attach, "docs/TODO.md #14");
    }

    static bool _hooked;

    static bool Attach()
    {
        SymbolRegistry.Build();

        var target = SymbolRegistry.Resolve("game", null, HitResolve);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] hit guard: no game function at 0x{HitResolve:X8} -- " +
                                    "a malformed hit record will fault as it does today.");
            return false;
        }

        var impl = typeof(HitGuard).GetMethod(nameof(BeforeHit), BindingFlags.Public | BindingFlags.Static)!;
        HookManager.AddPre(_self, target, impl);

        // The appearance watch is a separate question from the guard: the guard
        // fires when something *walks into* a malformed record, which is far later
        // than when the record went bad. Only under the probe, since it reads two
        // bytes per slot per tick.
        MethodInfo? stage = Probe ? SymbolRegistry.Resolve("game", null, PlayerStage) : null;
        if (stage != null)
            HookManager.AddPost(_self, stage,
                typeof(HitGuard).GetMethod(nameof(AfterPlayerStage), BindingFlags.Public | BindingFlags.Static)!);

        HookManager.Commit();

        _hooked = HookAttach.Installed(target);
        _windowStart = Now;

        Console.WriteLine($"[KF2] hit guard: {(_hooked && Guard ? "on" : "off")}, watching func_{HitResolve:X8} " +
                          $"(entities 0x{EntityBase:X8}+{EntityCount}, " +
                          $"descriptors 0x{DescBase:X8}..0x{DescBlockEnd:X8})" +
                          (Probe ? ", census on" : "") + (Verbose ? ", reporting every call" : ""));

        return _hooked;
    }

    /// <summary>Main RAM. Fences every read below, so the guard cannot itself fault
    /// on the state it is reporting.</summary>
    static bool Ram(uint addr) => addr >= 0x80010000u && addr < 0x80200000u;

    /// <summary>
    /// Replay what `func_8003A9CC` is about to do to its argument. Returns false --
    /// which <c>HookManager.Invoke</c> takes as "skip the call" -- only in the four
    /// states where the read is certain to fault. Reads only.
    /// </summary>
    public static bool BeforeHit(CpuContext c, IMemory m)
    {
        if (!_hooked) return true;

        _calls++;
        int idx = (int)(c.A0 & 0xFFFFu);
        uint ra = c.RA;
        if (idx > _maxIdx) _maxIdx = idx;

        string? fatal = null;   // would fault: refuse the call
        string? note = null;    // suspicious but survivable: report only
        int redirect = -1;

        if (idx >= EntityCount)
        {
            _badIndex++;
            fatal = $"index {idx} past the {EntityCount}-slot entity table";
        }

        uint rec = EntityBase + (uint)(idx * EntityStride);
        int kind = -1, type = -1;

        if (fatal == null && Ram(rec))
        {
            kind = m.ReadU8(rec + KindOff);

            // The game's own redirect: kind 3 resolves against the record this one
            // names, and the type byte is read from *that* one.
            if (kind == RedirectKind && Ram(rec + RedirectOff))
            {
                redirect = (short)m.ReadU16(rec + RedirectOff);
                if (redirect < 0 || redirect >= EntityCount)
                {
                    _badRedirect++;
                    fatal = $"kind 3 redirect to {redirect}, outside the {EntityCount}-slot table";
                }
                else
                {
                    rec = EntityBase + (uint)(redirect * EntityStride);
                    kind = Ram(rec) ? m.ReadU8(rec + KindOff) : -1;
                }
            }

            // Reported, never refused. This is the best guess at the cause -- the
            // shape to expect a frame after something dies -- but it is an
            // inference, and refusing on it would drop hits the game meant to land.
            if (fatal == null && kind == 0xFF)
            {
                _freeSlot++;
                note = "the slot is free (kind 0xFF), so the type byte is the last tenant's";
            }
        }

        if (fatal == null && Ram(rec + TypeOff))
        {
            type = m.ReadU8(rec + TypeOff);
            if (type > _maxType) _maxType = type;

            uint desc = DescBase + (uint)(type * DescStride);
            uint tableEnd = desc + DescPtrOff + (uint)(DescPtrCount * 4);

            if (tableEnd > DescBlockEnd)
            {
                _badType++;
                fatal = $"type {type} -> descriptor 0x{desc:X8}, past the loaded block 0x{DescBlockEnd:X8}";
            }
            else
            {
                // The read that actually throws, found before it is taken.
                //
                // `func_8003A448` walks these in order, *skips* a zero and
                // dereferences everything else, stopping at the first whose first
                // byte matches. So the first non-zero pointer is read
                // unconditionally and every later one only if the walk gets that
                // far -- and refusing on a later one would be more aggressive than
                // the game, dropping a hit it would have landed. Only the first is
                // fatal; the rest are counted, because a descriptor with a junk
                // tail is worth knowing about and is not worth a refusal.
                bool first = true;
                for (int k = 0; k < DescPtrCount; k++)
                {
                    uint slot = desc + (uint)DescPtrOff + (uint)(k * 4);
                    if (!Ram(slot)) break;
                    uint ptr = m.ReadU32(slot);
                    if (ptr == 0u) continue;
                    if (!Ram(ptr))
                    {
                        _badPointer++;
                        string where = $"type {type} -> descriptor 0x{desc:X8}, pointer {k} at " +
                                       $"0x{slot:X8} reads 0x{ptr:X8}, which is not RAM";
                        if (first) fatal = where;
                        else note ??= where + " (past the first, so only read if the walk reaches it)";
                        break;
                    }
                    first = false;
                }
            }
        }

        if (fatal != null)
        {
            bool refuse = Guard;
            if (refuse) _refused++;
            Report(idx, redirect, kind, type, ra,
                   fatal + (refuse ? " -- hit refused" : " -- letting it fault (KF2_HITGUARD=0)"));
            return !refuse;
        }

        if (note != null) Report(idx, redirect, kind, type, ra, note);
        else if (Verbose)
            Console.WriteLine($"[KF2] hit guard: idx {idx} kind {kind} type {type} from 0x{ra:X8}");

        Census();
        return true;
    }

    /// <summary>
    /// One line per distinct finding, on stderr -- so with the guard off it is out
    /// of the buffer before the process dies a call later, and with the guard on it
    /// is the report that replaces the stack trace. Deduplicated on what makes a
    /// finding the same one, so a swing repeating it every tick reports once.
    /// </summary>
    static void Report(int idx, int redirect, int kind, int type, uint ra, string what)
    {
        long key = ((long)idx << 32) ^ ((long)(kind & 0xFF) << 24) ^ ((long)(type & 0xFF) << 16) ^ redirect;
        if (!_seen.Add(key)) return;
        if (_reports++ >= MaxReports) return;

        Console.Error.WriteLine($"[KF2] hit guard: {what} " +
                                $"-- a0={idx}{(redirect >= 0 ? $" ->{redirect}" : "")} " +
                                $"kind=0x{(kind < 0 ? 0 : kind):X2} type={type} " +
                                $"rec=0x{EntityBase + (uint)((redirect >= 0 ? redirect : idx) * EntityStride):X8} " +
                                $"called from 0x{ra:X8}" +
                                (_reports == MaxReports ? " [further distinct findings counted only]" : ""));
    }

    /// <summary>
    /// How many descriptors this area actually loaded, which is not how many fit:
    /// an area fills only the types it uses and leaves the rest as `0xFFFFFFFF`
    /// filler -- measured 30 in one area and 14 in the final boss's. Recomputed per
    /// call is too expensive, so it is cached and cleared when an overlay loads.
    /// </summary>
    static int _loaded = -1;

    static int LoadedCount(IMemory m)
    {
        if (_loaded >= 0) return _loaded;
        int capacity = (int)((DescBlockEnd - DescBase) / DescStride);
        _loaded = capacity;
        for (int t = 0; t < capacity; t++)
        {
            uint slot = DescBase + (uint)(t * DescStride) + DescPtrOff;
            if (!Ram(slot)) { _loaded = t; break; }
            uint ptr = m.ReadU32(slot);
            if (ptr != 0u && !Ram(ptr)) { _loaded = t; break; }
        }
        return _loaded;
    }

    public static void ForgetArea() => _loaded = -1;

    /// <summary>
    /// The first tick on which a record exists that the game's own spatial query
    /// would select and the hit check cannot survive.
    ///
    /// `func_8003B72C` -- the query `func_800271D0` uses to pick a candidate --
    /// selects on `u8[rec+0x9] == 1`, the renderer's drawn flag, and nothing on the
    /// path then bounds `u8[rec+0x2]`. So a record that is drawn but typeless is
    /// picked by the game and faults, and the interesting moment is when that
    /// record came to exist, not when something walked into it. Fires once, prints
    /// the whole record, and says nothing again.
    /// </summary>
    public static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (_watchFired) return;

        int loaded = LoadedCount(m);
        for (int i = 0; i < EntityCount; i++)
        {
            uint rec = EntityBase + (uint)(i * EntityStride);
            if (!Ram(rec + 0x9u)) return;
            if (m.ReadU8(rec + 0x9u) != 1) continue;
            int type = m.ReadU8(rec + (uint)TypeOff);
            if (type < loaded) continue;

            _watchFired = true;
            var bytes = new System.Text.StringBuilder();
            for (int b = 0; b < EntityStride; b++)
            {
                if (b % 16 == 0) bytes.Append($"\n[KF2] hit guard:   +{b:X2} ");
                bytes.Append($"{m.ReadU8(rec + (uint)b):X2} ");
            }

            Console.Error.WriteLine(
                $"[KF2] hit guard: FIRST malformed record at frame {FramePacing.Frames} -- " +
                $"entity {i} is drawn (u8[+9]==1, which is what func_8003B72C selects on) " +
                $"but its type byte is {type} and this area loaded only {loaded} descriptors. " +
                $"The record:{bytes}");
            return;
        }
    }

    /// <summary>
    /// What the hit check saw over the window, behind `KF2_HITPROBE`. The two
    /// maxima are the point of it even when nothing is wrong: they are the only
    /// measurement of what the real bounds on the index and the type byte are,
    /// which is what says whether a value seen at a crash was merely large or
    /// arithmetically impossible.
    /// </summary>
    static void Census()
    {
        if (!Probe) return;

        double now = Now;
        if (_windowStart <= 0.0) { _windowStart = now; return; }
        if (now - _windowStart < WindowSeconds) return;
        _windowStart = now;

        if (_calls == 0) return;

        Console.WriteLine($"[KF2] hit guard: {_calls} resolve(s), widest index {_maxIdx}, " +
                          $"widest type {_maxType}, {_refused} refused " +
                          $"({_badIndex} index, {_badRedirect} redirect, {_freeSlot} free slot, " +
                          $"{_badType} type, {_badPointer} pointer)");

        _calls = 0;
        _badIndex = _badRedirect = _freeSlot = _badType = _badPointer = _refused = 0;
    }
}
