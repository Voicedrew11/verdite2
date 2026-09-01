using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// What the weapon's hit resolution was handed, one call before it crashes.
///
///     KF2_HITPROBE=1     report every malformed entity the hit check resolves,
///                        and a census of what it saw
///     KF2_HITPROBE=2     also report every call, which is very loud
///
/// ## The crash this exists for
///
/// `docs/TODO.md` #14 — the game dies when the final boss takes its last hit,
/// above the tick rate and not at it. It had never been reproduced, and the
/// standing guess was that it lived in `fdat23`'s two post-boss modal loops and
/// so belonged to <see cref="LoopPacing"/>. **The first stack trace of it says
/// otherwise**, and there is no `fdat23` frame anywhere on it:
///
///     func_8001369C   the main loop
///      +- func_8002A550    stage 3, the player and weapon stage
///         +- func_800271D0    steps the swing, walks the object table
///            +- func_8003A9CC    resolve a hit against one entity
///               +- func_8003A490
///                  +- func_8003A448   ReadU8(0x0FFF0000) -> unmapped
///
/// So it fires on the killing blow itself, from the main loop, before the ending
/// sequence is entered at all — which is why the `ending boss` rig kept passing
/// at 144 fps. That rig runs the two modal loops from stage 3's post with a boss
/// that was never fought, so it never reaches this call at all.
///
/// ## What goes wrong, and the three ways it could
///
/// `func_800271D0` takes `u16[obj+0x2A]` as an **entity index**;
/// `func_8003A9CC` turns it into `0x8016C544 + idx*0x7C`, redirects through
/// `s16[rec+0x22]` when the record's kind byte is 3, and then does
///
///     desc = 0x80172624 + u8[rec+0x2] * 120
///
/// `u8[rec+0x2]` is the creature *type*, indexing a per-area descriptor table
/// that `func_80017244` copies to `0x80172624` — `0xCB0` **words**, so the block
/// ends at `0x801758E4` and holds at most 108 records, likely fewer since other
/// tables share it. `func_8003A490` then walks fifteen pointers at `desc+0x38`
/// and does `ReadU8` on each. `0x0FFF0000` is not a pointer; it is whatever bytes
/// sit at `desc+0x38` once `desc` has walked off the end of the real table.
///
/// **Neither routine validates anything** — no bound on the type byte, and no
/// check that the slot is occupied before reading its type. A free slot holds
/// `0xFF` at `+0x0`, and the code tests that byte for `3` (the redirect) and
/// never for `0xFF`. So there are three distinguishable causes and this probe
/// exists to say which:
///
/// * the **index** is out of range (`>= 200`, or a negative redirect), so the
///   record is not in the table at all;
/// * the index is fine but the **slot is free**, so the type byte is the last
///   tenant's — the shape you would expect one frame after a boss dies;
/// * both are fine and the **type byte** is out of range, which would mean the
///   descriptor table is shorter than the block, or is not loaded yet.
///
/// Each points somewhere different, so the census reports them separately rather
/// than as one "bad record" count.
///
/// It also walks the fifteen pointers itself and names the first one that is
/// neither zero nor a RAM address — that is the read that throws, so the line is
/// printed on the call *before* the crash rather than lost in the unwind.
///
/// Everything here is a guarded read: <see cref="Ram"/> fences every load, so the
/// probe cannot itself fault on the state it is reporting. Nothing is written to
/// game memory and no call is skipped — this diagnoses, it does not rescue. Off
/// by default and hooked only when on, since a swing calls into the hit check
/// once per candidate object per tick.
/// </summary>
internal static class HitProbe
{
    /// <summary>`func_8003A9CC(a0 = entity index, ...)`, the hit resolution.
    /// Reached from `func_800271D0` in stage 3; `c.RA` at the pre names the call
    /// site, which is worth having because it is not the only caller.</summary>
    const uint HitResolve = 0x8003A9CC;

    // The entity table, and the per-area creature-type descriptors immediately
    // after it. Both confirmed against the recompiled func_8003A9CC prologue:
    // `0x80170000 - 0x3ABC` is the table, `+ 0x60E0` the descriptors.
    const uint EntityBase = 0x8016C544;
    const int EntityStride = 0x7C;
    const int EntityCount = 0xC8;          // 200
    const int KindOff = 0x0;               // 0xFF while the slot is free
    const int RedirectKind = 0x3;          // follow s16[rec+0x22] instead
    const int RedirectOff = 0x22;
    const int TypeOff = 0x2;               // index into the descriptors below

    const uint DescBase = 0x80172624;      // EntityBase + 0x60E0
    const int DescStride = 120;            // (t << 4) - t, then << 3
    const int DescPtrOff = 0x38;           // fifteen pointers, tagged by u8[ptr]
    const int DescPtrCount = 15;

    /// <summary>The end of the block `func_80017244(0x80172624, src, 0xCB0)` copies
    /// in — `0xCB0` *words*. A descriptor whose pointer table reaches past this was
    /// never loaded, whatever the type byte says.</summary>
    const uint DescBlockEnd = DescBase + 0xCB0u * 4u;

    public static bool On;
    public static bool Verbose;

    /// <summary>Distinct anomalies printed in full before the class falls back to
    /// counting. A swing resolves a hit per candidate object per tick, so a
    /// genuinely broken record would otherwise fill the terminal and push the
    /// first — the only one before the crash — out of the scrollback.</summary>
    const int MaxReports = 24;

    static int _calls, _reports;
    static int _badIndex, _badRedirect, _freeSlot, _badType, _badPointer;
    static int _maxIdx = -1, _maxType = -1;
    static readonly HashSet<long> _seen = [];

    static double _windowStart;
    const double WindowSeconds = 10.0;
    static double Now => Environment.TickCount64 / 1000.0;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.hitprobe",
        Name = "Hit probe",
        Version = "1.0",
        Description = "Reports what the weapon's hit resolution was handed, for the crash on the final boss's death.",
    };

    public static void Configure(string? probe)
    {
        if (string.IsNullOrWhiteSpace(probe)) return;
        On = probe != "0";
        Verbose = probe == "2";
    }

    public static void Install()
    {
        if (!On) return;
        HookAttach.OnOverlayLoad("hit probe", Attach, "docs/TODO.md #14");
    }

    static bool _hooked;

    static bool Attach()
    {
        SymbolRegistry.Build();

        var target = SymbolRegistry.Resolve("game", null, HitResolve);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] hit probe: no game function at 0x{HitResolve:X8}");
            return false;
        }

        var impl = typeof(HitProbe).GetMethod(nameof(BeforeHit), BindingFlags.Public | BindingFlags.Static)!;
        HookManager.AddPre(_self, target, impl);
        HookManager.Commit();

        _hooked = HookAttach.Installed(target);
        _windowStart = Now;

        Console.WriteLine($"[KF2] hit probe: {(_hooked ? "on" : "off")}, watching func_{HitResolve:X8} " +
                          $"(entities {EntityBase:X8}+{EntityCount}, descriptors {DescBase:X8}..{DescBlockEnd:X8})" +
                          (Verbose ? ", reporting every call" : ""));

        return _hooked;
    }

    /// <summary>Main RAM. Every read below is fenced with this, so the probe cannot
    /// throw on the very state it is describing.</summary>
    static bool Ram(uint addr) => addr >= 0x80010000u && addr < 0x80200000u;

    /// <summary>
    /// Replay what `func_8003A9CC` is about to do to its argument, and say so if
    /// any step of it is malformed. Reads only.
    /// </summary>
    public static void BeforeHit(CpuContext c, IMemory m)
    {
        if (!_hooked) return;

        _calls++;
        int idx = (int)(c.A0 & 0xFFFFu);
        uint ra = c.RA;
        if (idx > _maxIdx) _maxIdx = idx;

        string? fault = null;
        int redirect = -1;

        if (idx >= EntityCount)
        {
            _badIndex++;
            fault = $"index {idx} past the {EntityCount}-slot table";
        }

        uint rec = EntityBase + (uint)(idx * EntityStride);
        int kind = -1, type = -1;

        if (fault == null && Ram(rec))
        {
            kind = m.ReadU8(rec + KindOff);

            // The game's own redirect: kind 3 means "resolve against the record
            // this one names" and the type byte is read from *that* one.
            if (kind == RedirectKind && Ram(rec + RedirectOff))
            {
                redirect = (short)m.ReadU16(rec + RedirectOff);
                if (redirect < 0 || redirect >= EntityCount)
                {
                    _badRedirect++;
                    fault = $"kind 3 redirect to {redirect}, outside the {EntityCount}-slot table";
                }
                else
                {
                    rec = EntityBase + (uint)(redirect * EntityStride);
                    kind = Ram(rec) ? m.ReadU8(rec + KindOff) : -1;
                }
            }

            // A free slot still has last tenant's type byte in it, which is the
            // shape to expect one frame after something dies.
            if (fault == null && kind == 0xFF)
            {
                _freeSlot++;
                fault = "the slot is free (kind 0xFF); the type byte is the last tenant's";
            }
        }

        if (fault == null && Ram(rec + TypeOff))
        {
            type = m.ReadU8(rec + TypeOff);
            if (type > _maxType) _maxType = type;

            uint desc = DescBase + (uint)(type * DescStride);
            uint tableEnd = desc + DescPtrOff + (uint)(DescPtrCount * 4);

            if (tableEnd > DescBlockEnd)
            {
                _badType++;
                fault = $"type {type} -> descriptor 0x{desc:X8}, past the loaded block 0x{DescBlockEnd:X8}";
            }
            else
            {
                // The read that actually throws. Naming it here puts the line out
                // on the call before the crash instead of losing it in the unwind.
                for (int k = 0; k < DescPtrCount; k++)
                {
                    uint slot = desc + (uint)DescPtrOff + (uint)(k * 4);
                    if (!Ram(slot)) break;
                    uint ptr = m.ReadU32(slot);
                    if (ptr == 0u || Ram(ptr)) continue;
                    _badPointer++;
                    fault = $"type {type} -> descriptor 0x{desc:X8}, pointer {k} at " +
                            $"0x{slot:X8} reads 0x{ptr:X8}, which is not RAM";
                    break;
                }
            }
        }

        if (fault != null) Report(idx, redirect, kind, type, ra, fault);
        else if (Verbose)
            Console.WriteLine($"[KF2] hit probe: idx {idx} kind {kind} type {type} from 0x{ra:X8}");

        Census();
    }

    /// <summary>
    /// One line per distinct anomaly, on stderr so it is not sitting in a pipe's
    /// buffer when the process dies a call later. Deduplicated on what makes an
    /// anomaly the same one, so a swing that resolves the same broken record every
    /// tick reports once and is counted thereafter.
    /// </summary>
    static void Report(int idx, int redirect, int kind, int type, uint ra, string fault)
    {
        long key = ((long)idx << 32) ^ ((long)(kind & 0xFF) << 24) ^ ((long)(type & 0xFF) << 16) ^ redirect;
        if (!_seen.Add(key)) return;
        if (_reports++ >= MaxReports) return;

        Console.Error.WriteLine($"[KF2] hit probe: {fault} " +
                                $"-- a0={idx}{(redirect >= 0 ? $" ->{redirect}" : "")} " +
                                $"kind=0x{(kind < 0 ? 0 : kind):X2} type={type} " +
                                $"rec=0x{EntityBase + (uint)((redirect >= 0 ? redirect : idx) * EntityStride):X8} " +
                                $"called from 0x{ra:X8}" +
                                (_reports == MaxReports ? " [further distinct anomalies counted only]" : ""));
    }

    /// <summary>
    /// What the hit check saw over the window. The two maxima are the point of it
    /// even when nothing goes wrong: they are the only measurement of what the
    /// real bounds on the index and the type byte are, which is what says whether
    /// a value seen at the crash was merely large or genuinely impossible.
    /// </summary>
    static void Census()
    {
        double now = Now;
        if (_windowStart <= 0.0) { _windowStart = now; return; }
        if (now - _windowStart < WindowSeconds) return;
        _windowStart = now;

        int bad = _badIndex + _badRedirect + _freeSlot + _badType + _badPointer;
        if (_calls == 0) return;

        Console.WriteLine($"[KF2] hit probe: {_calls} resolve(s), widest index {_maxIdx}, " +
                          $"widest type {_maxType}, {bad} malformed " +
                          $"({_badIndex} index, {_badRedirect} redirect, {_freeSlot} free slot, " +
                          $"{_badType} type, {_badPointer} pointer)");

        _calls = 0;
        _badIndex = _badRedirect = _freeSlot = _badType = _badPointer = 0;
    }
}
