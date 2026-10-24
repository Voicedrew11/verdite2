using System.Globalization;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// A monkey at the pad: seeded random play, invariants checked every frame, and
/// a record of what was pressed, for <c>scripts/fuzz.py</c> to run by the hundred
/// headless (<c>KF2_HEADLESS=1 KF2_TURBO=1</c>).
///
///     KF2_FUZZ=&lt;seed&gt;          on, with this seed (off unless set)
///     KF2_FUZZ_FRAMES=20000     frames of play before a clean exit (0)
///     KF2_FUZZ_STOP=1           exit 3 on the first violation
///     KF2_FUZZ_GODMODE=1        HP topped up, so a run explores instead of reloading
///     KF2_FUZZ_MACROS=0         no warps, loads or teleports, only the pad
///     KF2_FUZZ_REPLAY=&lt;file&gt;    play a recording back instead of the PRNG
///     KF2_FUZZ_OUT=fuzz-input.log  where the recording goes
///
/// **The policy is driven by frames, not ticks**, because the in-game menu and the
/// game's modal loops stop the main loop's stages while frames still come; a
/// policy on stage 3 would hold its last button through a menu forever. Macros
/// (warp, load, goto) are only queued here and run from the post on stage 3, the
/// one place the game's own loader may run (see <see cref="AreaWarp.TryRun"/>).
///
/// **Determinism is partial.** The game's <c>rand</c> is seeded from the fuzz seed,
/// and the pad is a function of the frame count, but CD timing and the host's
/// threads are not, so a seed does not replay a run exactly. The recording is
/// what replays: each line is a frame and what was pressed or done there.
///
/// Every finding is one stdout line starting <c>[KF2-FUZZ]</c>, which is all the
/// orchestrator reads. See "Fuzzing headless playthroughs" in docs/DEVELOPMENT.md.
/// </summary>
public static class Fuzz
{
    const uint PlayerStage = 0x8002A550;

    const uint MaxHp = 0x80199426, Hp = 0x80199428;   // u16
    const uint MaxMp = 0x8019942A, Mp = 0x8019942C;   // u16
    const uint Level = 0x8019941C;                    // u8
    const uint State = 0x801994E1;                    // u8
    const byte StateDead = 0x11;
    const uint Area = 0x8017E060;                     // u8
    const uint PosX = 0x801994EC, PosY = 0x801994F0, PosZ = 0x801994F4;   // s32
    const int TileShift = 11, TileCount = 0x50;

    // The hit path's invariant (HitGuard): a live creature's type indexes a
    // 120-byte descriptor inside the block copied to 0x80172624..0x801758E4.
    const uint EntityTable = 0x8016C544;
    const int EntityStride = 0x7C, EntityCount = 0xC8;
    const uint DescBase = 0x80172624, DescEnd = 0x801758E4;
    const int DescStride = 120;

    /// <summary>Frames with no stage 3 before the pad starts trying to leave
    /// whatever holds it, and before that is reported as a stall.</summary>
    const int EscapeAfter = 600, StallAfter = 6000;

    /// <summary>Pad reads with no frame before the pad stops holding the policy's buttons.</summary>
    const long PadStall = 20000;
    static long _padReads;

    static long _heapOverruns;

    /// <summary>Emulated vblanks (a wall-clock 60 Hz) with no frame before the same.</summary>
    const long VbStall = 15;
    static long _vblanks;
    const long QuietFrames = 600;
    static long _quietUntil;
    static bool _queued;
    static readonly long StatusEvery = long.TryParse(Environment.GetEnvironmentVariable("KF2_FUZZ_STATUS"), out var n) && n > 0 ? n : 1000;

    public static bool On { get; private set; }
    static uint _seed;
    static long _frameLimit;
    static bool _stop, _godmode, _macros = true;
    static string _out = "fuzz-input.log";
    static string? _replayPath;

    static Rng _rng;
    static ushort _buttons;
    static int _hold;
    static int _menuFrames;

    static string _overlay = "boot";
    static bool _armed;
    static long _frame;            // frames of play since armed
    static long _lastStageFrame;   // _frame at the last stage-3 post
    static bool _stallSaid;
    static long _violations, _warps, _loads, _gotos, _deaths, _titles;
    static bool _wasDead;
    static readonly HashSet<string> _said = new(StringComparer.Ordinal);

    static volatile string? _macro;
    static StreamWriter? _rec;
    static ushort _recButtons = 0xFFFF;

    // Replay: frame -> buttons, and frame -> macro.
    static SortedList<long, ushort>? _replayPad;
    static SortedList<long, string>? _replayMacro;
    static int _replayPadAt, _replayMacroAt;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.fuzz",
        Name = "Fuzz",
        Version = "1.0",
        Description = "Seeded random play with invariants, for headless crash hunting.",
    };

    public static void Configure(string? seed, string? frames, string? stop, string? godmode,
                                 string? macros, string? replay, string? output)
    {
        if (string.IsNullOrWhiteSpace(seed) && string.IsNullOrWhiteSpace(replay)) return;
        On = true;
        _seed = string.IsNullOrWhiteSpace(seed) ? 1u
              : uint.TryParse(seed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s
              : (uint)seed.Trim().GetHashCode();
        _frameLimit = long.TryParse(frames, out var f) && f > 0 ? f : 20000;
        _stop = Yes(stop);
        _godmode = Yes(godmode);
        _macros = !(macros?.Trim() is "0" or "off");
        _replayPath = string.IsNullOrWhiteSpace(replay) ? null : replay.Trim();
        if (!string.IsNullOrWhiteSpace(output)) _out = output.Trim();
        _rng = new Rng(_seed);

        static bool Yes(string? v) => v?.Trim().ToLowerInvariant() is "1" or "on" or "true" or "yes";
    }

    public static void Install()
    {
        if (!On) return;

        SeedGameRand(_seed);
        if (_replayPath != null) LoadReplay(_replayPath);
        else
        {
            _rec = new StreamWriter(_out, false) { AutoFlush = true };
            _rec.WriteLine($"# seed {_seed} frames {_frameLimit} macros {(_macros ? 1 : 0)} godmode {(_godmode ? 1 : 0)}");
        }

        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            _overlay = e.Name;
            _quietUntil = _frame + QuietFrames;
            Say($"overlay {e.Name} frame={_frame}");
            // Back at the title (the menu's quit, the ending): let AutoStart boot
            // into the slot again, which it re-arms for on GAME.EXE, and arm again
            // after it -- its presses and this pad's would otherwise be ANDed.
            if (_armed && e.Name is "open" or "game" or "end")
            {
                _armed = false;
                _titles++;
                _buttons = 0;
                _rec?.WriteLine($"{_frame} m title");
            }
        });

        // A frame boundary, not VSyncEvent: that is the emulated vblank, 60 a
        // wall-clock second, however fast turbo draws.
        FramePacing.Boundary += OnFrame;
        Event.AddListener<VSyncEvent>(_ => { if (_armed) _vblanks++; });

        // The pad, as AutoStart and the shell inject it: active-low, bytes swapped.
        Event.AddListener<PadReadEvent>(e =>
        {
            if (e.Port != 0) return;
            if (!_armed)
            {
                // "The End" waits for any button (EndingHold); AutoStart takes it
                // from the title on.
                if (_overlay == "end" && Environment.TickCount64 / 250 % 2 == 0)
                    e.Buttons &= unchecked((ushort)~(Controller.Cross >> 8 | Controller.Cross << 8));
                return;
            }
            ushort press = _buttons;
            // A loop of the game's that polls the pad with no frame between --
            // waiting for a release (the menu's repeat gate, func_80022E90), or for
            // a press to dismiss something -- would see the held button forever,
            // since the policy steps on frames. Past PadStall reads, or VbStall
            // vblanks (the emulated vblank keeps coming when frames do not), release,
            // then Cross, then release, then Circle.
            if (++_padReads > PadStall)
                press = ((_padReads - PadStall) / 4000 % 4) switch { 1 => Controller.Cross, 3 => Controller.Circle, _ => (ushort)0 };
            else if (_vblanks > VbStall)
                press = ((_vblanks - VbStall) / 8 % 4) switch { 1 => Controller.Cross, 3 => Controller.Circle, _ => (ushort)0 };
            if (press != 0) e.Buttons &= (ushort)~(ushort)((press >> 8) | (press << 8));
        });

        HookAttach.OnOverlayLoad("fuzz", Attach);
        Console.WriteLine(_replayPath != null
            ? $"[KF2-FUZZ] start replay={_replayPath} frames={_frameLimit}"
            : $"[KF2-FUZZ] start seed={_seed} frames={_frameLimit} macros={(_macros ? 1 : 0)} godmode={(_godmode ? 1 : 0)}");
    }

    static bool Attach()
    {
        var target = SymbolRegistry.Resolve("game", null, PlayerStage);
        if (target == null) return false;
        // Not keyed on Installed: AutoStart, AutoReload and the shell post on this
        // same routine, so it can be committed before this hook is even queued.
        if (!_queued)
        {
            var impl = typeof(Fuzz).GetMethod(nameof(AfterPlayerStage), BindingFlags.Public | BindingFlags.Static)!;
            _queued = HookManager.AddPost(_self, target, impl);
            if (!_queued) return false;
        }
        HookManager.Commit();
        bool ok = HookAttach.Installed(target);
        if (ok) Console.WriteLine($"[KF2-FUZZ] hooked stage 3 at 0x{PlayerStage:X8}");
        return ok;
    }

    /// <summary>The game's rand is BIOS A(2Fh), whose seed the game never sets
    /// (no A(30h) call site), so it starts at 1 in every run unless set here.</summary>
    static void SeedGameRand(uint seed)
    {
        var f = typeof(RecompOne.Runtime.Runtime).Assembly.GetType("RecompOne.Runtime.Bios.BiosA")
            ?.GetField("_randSeed", BindingFlags.NonPublic | BindingFlags.Static);
        if (f == null) { Console.Error.WriteLine("[KF2-FUZZ] could not seed the game's rand"); return; }
        f.SetValue(null, seed);
    }

    static void OnFrame()
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return;

        if (!_armed)
        {
            bool autoDone = (AutoStart.Slot == 0 && !AutoStart.NewGame) || AutoStart.Fired;
            if (!autoDone || m.ReadU16(MaxHp) == 0 || !_overlay.StartsWith("fdat", StringComparison.Ordinal))
                return;
            _armed = true;
            _lastStageFrame = 0;
            Say($"armed area={m.ReadU8(Area)} hp={m.ReadU16(Hp)}/{m.ReadU16(MaxHp)}");
        }

        _frame++;
        _padReads = 0;
        _vblanks = 0;
        Check(m);
        if (_godmode && m.ReadU16(MaxHp) != 0 && m.ReadU16(Hp) < m.ReadU16(MaxHp))
            m.WriteU16(Hp, m.ReadU16(MaxHp));

        if (_replayPad != null) Replay();
        else Step();

        if (_rec != null && _buttons != _recButtons)
        {
            _recButtons = _buttons;
            _rec.WriteLine($"{_frame} b {_buttons:X4}");
        }

        if (_frame % StatusEvery == 0)
            Say($"status frame={_frame} area={m.ReadU8(Area)} hp={m.ReadU16(Hp)}/{m.ReadU16(MaxHp)} " +
                $"pos=({(int)m.ReadU32(PosX)},{(int)m.ReadU32(PosZ)}) yaw={m.ReadU16(0x80199506)} " +
                $"stage={_frame - _lastStageFrame} " +
                $"overlay={_overlay} warps={_warps} loads={_loads} gotos={_gotos} deaths={_deaths} titles={_titles} " +
                $"violations={_violations}");

        if (_frame >= _frameLimit) Finish(0);
    }

    /// <summary>Choose the pad's next state from the policy, once the current hold runs out.</summary>
    static void Step()
    {
        long sinceStage = _frame - _lastStageFrame;
        if (sinceStage > EscapeAfter)
        {
            // Something modal holds the main loop -- the in-game menu, almost
            // always, which runs inside stage 3. Back out the way a player would:
            // Circle is every chooser's cancel, and a Cross now and then dismisses
            // a message. Clean edges, since the menu's steppers do not edge-detect.
            int phase = (int)(_frame / 8 % 4);
            _buttons = (_frame % 8) < 2 ? (phase == 3 ? Controller.Cross : Controller.Circle) : (ushort)0;
            return;
        }

        if (--_hold > 0) return;

        if (_menuFrames > 0)
        {
            // Wander the in-game menu: up and down, in and out, then close it.
            _menuFrames -= 6;
            int r = _rng.Next(100);
            _buttons = _menuFrames <= 0 ? Controller.Circle
                     : r < 35 ? Controller.Down : r < 60 ? Controller.Up
                     : r < 72 ? Controller.Right : r < 80 ? Controller.Left
                     : r < 92 ? Controller.Cross : Controller.Triangle;
            _hold = 2;
            _menuGap = true;
            return;
        }

        if (_menuGap)
        {
            // A release between menu presses, so each is an edge.
            _menuGap = false;
            _buttons = 0;
            _hold = 4;
            return;
        }

        int p = _rng.Next(1000);
        ushort turn = _rng.Next(2) == 0 ? Controller.Left : Controller.Right;
        (_buttons, _hold) = p switch
        {
            < 300 => ((ushort)(Controller.Up | (_rng.Next(10) < 4 ? turn : 0)), _rng.Range(5, 60)),
            < 360 => (Controller.Down, _rng.Range(3, 20)),
            < 500 => (turn, _rng.Range(2, 20)),
            < 580 => (_rng.Next(2) == 0 ? Controller.L1 : Controller.R1, _rng.Range(3, 25)),
            < 620 => (_rng.Next(2) == 0 ? Controller.L2 : Controller.R2, _rng.Range(1, 6)),
            < 740 => (Controller.Square, _rng.Range(1, 3)),
            < 780 => (Controller.Triangle, _rng.Range(1, 3)),
            < 880 => (Controller.Cross, _rng.Range(1, 3)),
            < 900 => (Controller.Circle, 2),
            < 905 => (Controller.Start, 2),
            < 908 => (Controller.Select, 2),
            _ => ((ushort)0, _rng.Range(1, 15)),
        };
        if (_buttons == Controller.Circle) { _menuFrames = _rng.Range(30, 400); _menuGap = true; }

        if (_macros && _macro == null)
        {
            int q = _rng.Next(10000);
            if (q < 4) _macro = $"warp {_rng.Next(8)}";
            else if (q < 6) _macro = $"load {_rng.Range(1, 4)}";
            else if (q < 9) _macro = "goto";
        }
    }

    static bool _menuGap;

    static void Replay()
    {
        var pad = _replayPad!;
        while (_replayPadAt < pad.Count && pad.Keys[_replayPadAt] <= _frame)
            _buttons = pad.Values[_replayPadAt++];
        var mac = _replayMacro!;
        while (_replayMacroAt < mac.Count && mac.Keys[_replayMacroAt] <= _frame && _macro == null)
            _macro = mac.Values[_replayMacroAt++];
    }

    static void LoadReplay(string path)
    {
        _replayPad = new SortedList<long, ushort>();
        _replayMacro = new SortedList<long, string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ', 3);
            if (parts.Length < 3 || !long.TryParse(parts[0], out long frame)) continue;
            if (parts[1] == "b") _replayPad[frame] = ushort.Parse(parts[2], NumberStyles.HexNumber);
            else if (parts[1] == "m") _replayMacro[frame] = parts[2];
        }
        Console.WriteLine($"[KF2-FUZZ] replay: {_replayPad.Count} pad changes, {_replayMacro.Count} macros");
    }

    /// <summary>
    /// End of stage 3: the main loop is running, and the loader may run. Queued
    /// macros happen here.
    /// </summary>
    public static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (!_armed) return;
        _lastStageFrame = _frame;
        _stallSaid = false;

        string? macro = _macro;
        if (macro == null) return;
        _macro = null;
        if (m.ReadU16(MaxHp) == 0 || m.ReadU8(State) == StateDead) return;

        var parts = macro.Split(' ');
        string done = macro;
        switch (parts[0])
        {
            case "warp":
            {
                string? err = AreaWarp.TryRun(c, m, int.Parse(parts[1]));
                _warps++;
                Say($"macro {macro} frame={_frame} -> {err ?? "ok"}");
                break;
            }
            case "load":
            {
                uint r = AutoReload.LoadSlot(c, m, byte.Parse(parts[1]), out uint area);
                _loads++;
                Say($"macro {macro} frame={_frame} -> {(r == 0 ? $"area {area}" : $"failed {r}")}");
                break;
            }
            case "goto":
            {
                // A random spot inside the tile map at the current height. It may be
                // inside a wall or over nothing -- that is the point of trying it,
                // and also why it is the macro to turn off first when triaging.
                int x, z;
                if (parts.Length >= 4) { x = int.Parse(parts[1]); z = int.Parse(parts[3]); }
                else
                {
                    x = _rng.Range(1, TileCount - 1) << TileShift | _rng.Next(1 << TileShift);
                    z = _rng.Range(1, TileCount - 1) << TileShift | _rng.Next(1 << TileShift);
                }
                int y = (int)m.ReadU32(PosY);
                m.WriteU32(PosX, (uint)x);
                m.WriteU32(PosZ, (uint)z);
                done = $"goto {x} {y} {z}";
                _gotos++;
                Say($"macro {done} frame={_frame}");
                break;
            }
        }
        _rec?.WriteLine($"{_frame} m {done}");
        _quietUntil = _frame + QuietFrames;
    }

    static void Check(IMemory m)
    {
        ushort maxHp = m.ReadU16(MaxHp);
        if (maxHp == 0) return;   // between areas, or reloading

        // The model heap's invariant (HeapGuard): no compaction has had to keep its
        // end, which is the game asking for more models than the heap holds.
        if (HeapGuard.Overruns != _heapOverruns)
        {
            _heapOverruns = HeapGuard.Overruns;
            Violation("heap-full", $"area={m.ReadU8(Area)}");
        }

        bool dead = m.ReadU8(State) == StateDead;
        if (dead && !_wasDead) { _deaths++; Say($"death frame={_frame} area={m.ReadU8(Area)}"); }
        _wasDead = dead;

        // A reload or a warp passes through states the invariants would read as
        // broken (the area byte reads 99 mid-reload), so they are not asked during
        // one: dead, or within QuietFrames of a death, a macro or an overlay load.
        if (dead) _quietUntil = _frame + QuietFrames;
        if (_frame < _quietUntil) return;

        ushort hp = m.ReadU16(Hp), mp = m.ReadU16(Mp), maxMp = m.ReadU16(MaxMp);
        if (hp > maxHp) Violation("hp-over-max", $"hp={hp} max={maxHp}");
        if (mp > maxMp) Violation("mp-over-max", $"mp={mp} max={maxMp}");

        int area = m.ReadU8(Area);
        if (area > 7) Violation("area-range", $"area={area}");

        int lv = m.ReadU8(Level);
        if (lv == 0 || lv > 99) Violation("level-range", $"level={lv}");

        int tx = (int)m.ReadU32(PosX) >> TileShift, tz = (int)m.ReadU32(PosZ) >> TileShift;
        if ((uint)tx >= TileCount || (uint)tz >= TileCount)
            Violation("pos-outside-map", $"tile=({tx},{tz}) area={area}");

        for (int i = 0; i < EntityCount; i++)
        {
            uint e = EntityTable + (uint)(i * EntityStride);
            if (m.ReadU8(e + 0x9) != 1) continue;   // the renderer's liveness test
            int type = m.ReadU8(e + 0x2);
            if (DescBase + (uint)((type + 1) * DescStride) > DescEnd)
                Violation("entity-type", $"slot={i} type={type} area={area}");
        }

        long sinceStage = _frame - _lastStageFrame;
        if (sinceStage > StallAfter && !_stallSaid)
        {
            _stallSaid = true;
            Violation("stall", $"stage 3 has not returned for {sinceStage} frames, overlay={_overlay} area={area} in {GameFrames()}");
        }
    }

    /// <summary>
    /// The recompiled routines under this frame boundary, innermost first. The
    /// boundary is raised on the game thread from inside whatever loop is drawing,
    /// so for a stall this names the loop.
    /// </summary>
    static string GameFrames()
    {
        var names = new List<string>();
        foreach (var f in new System.Diagnostics.StackTrace(false).GetFrames())
        {
            string? n = f.GetMethod()?.Name;
            if (n == null) continue;
            int at = n.IndexOf("func_", StringComparison.Ordinal);
            if (at < 0) continue;
            n = n.Substring(at, Math.Min(13, n.Length - at));
            if (names.Count == 0 || names[^1] != n) names.Add(n);
            if (names.Count == 6) break;
        }
        return names.Count == 0 ? "?" : string.Join("<", names);
    }

    static void Violation(string kind, string detail)
    {
        // Once per kind and detail, so an invariant broken every frame is one line.
        if (!_said.Add(kind + "|" + detail)) return;
        _violations++;
        Say($"violation {kind} frame={_frame} seed={_seed} {detail}");
        _rec?.WriteLine($"# violation {_frame} {kind} {detail}");
        if (_stop) Finish(3);
    }

    static void Finish(int code)
    {
        Say($"done frame={_frame} code={code} violations={_violations} warps={_warps} loads={_loads} " +
            $"gotos={_gotos} deaths={_deaths} titles={_titles}");
        _rec?.Flush();
        Console.Out.Flush();
        Environment.Exit(code);
    }

    static void Say(string what) => Console.WriteLine($"[KF2-FUZZ] {what}");

    /// <summary>xorshift32: small, seeded, and the same on every host.</summary>
    struct Rng
    {
        uint _s;
        public Rng(uint seed) => _s = seed == 0 ? 0x9E3779B9u : seed * 2654435761u | 1u;
        public int Next(int n)
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return (int)(_s % (uint)n);
        }
        public int Range(int lo, int hi) => lo + Next(hi - lo);
    }
}
