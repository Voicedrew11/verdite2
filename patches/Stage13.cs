using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Stage 13, the renderer <c>func_800342D8(VECTOR *pos, SVECTOR *rot)</c>, in C#.
///
///     KF2_STAGE13=0         the recompiled routine
///     KF2_STAGE13=verify    the recompiled routine draws the frame; this one is then
///                           run against a record of it and the two compared
///
/// The routine is nineteen calls in a fixed order with no branch around any of them,
/// and one block of arithmetic for the HUD (<see cref="HudState"/>). Every call is
/// made through a delegate to the recompiled function, so each one passes through its
/// detour and every hook the port has on it -- FramePacing's gates and the frame
/// gate, the smoothers, the C# walks -- exactly as the recompiled body's calls do.
///
/// **What having it here buys.** <see cref="ViewOverride"/> draws the frame from a
/// camera of the port's instead of the one the main loop hands over, and everything
/// downstream follows it, the cull grid included, because the grid reads its eye from
/// the camera block. <see cref="DrawScene"/> is the drawing half as one call, the list
/// <see cref="MenuWorld"/> used to keep a copy of. And the compass needle's spring
/// (<see cref="NeedleSpeed"/>), which no hook could reach inside this body, is now a
/// line of C#.
///
/// **Verify cannot run the routine twice**, since it presents a frame and passes the
/// frame gate. It records the recompiled run at every call instead -- the registers,
/// the GTE and, where the body does work of its own next, the RAM -- and then runs
/// this transcription with each call replayed from that record, comparing what it
/// hands each callee. See "Stage 13 in C#" in docs/PATCHES_AND_MODS.md.
/// </summary>
public static class Stage13
{
    const uint Routine = 0x800342D8;

    /// <summary>The calls, in the order the routine makes them.</summary>
    public enum Site
    {
        View,             // the camera block, from a0/a1 (CameraBlock)
        AnimatedTextures, // gated by FramePacing
        Fade,             // the fade stepper, gated by FramePacing
        CullGrid,         // the 24x24 visibility window round the eye
        FrameHead,        // flip the buffers, clear the ordering table
        SoundMark,        // every live sound slot set to 1
        Arm,              // the first-person arm
        CompassError,     // the wrapped difference of two angles
        Hud,              // HP, MP, the gauges and the compass
        Overlays,
        Tiles,            // the map (TileWalk)
        Models,           // creatures, objects, effects, sprites (ModelWalk)
        Tint1,            // four full-screen quads; the third is the one Widescreen stretches
        Tint2,
        Tint3,
        Tint4,
        Present,          // DrawSync, VSync, PutDrawEnv, PutDispEnv, DrawOTag
        FrameGate,        // skipped by FramePacing
        SoundService,     // each slot marked 1, serviced
    }

    /// <summary>Each site's callee, and the return address its <c>jal</c> leaves in RA.</summary>
    static readonly (uint Callee, uint Return)[] Calls =
    [
        (0x8002E22C, 0x800342E8),
        (0x8002DC78, 0x800342F0),
        (0x80033FBC, 0x800342F8),
        (0x8002D3A8, 0x80034300),
        (0x8002E064, 0x80034308),
        (0x800353AC, 0x80034310),
        (0x80032400, 0x80034318),
        (0x80015374, 0x800343B0),
        (0x80031D5C, 0x8003466C),
        (0x80033E78, 0x80034674),
        (0x80031C94, 0x8003467C),
        (0x800331B4, 0x80034684),
        (0x8003202C, 0x8003468C),
        (0x800320BC, 0x80034694),
        (0x8003214C, 0x8003469C),
        (0x80032234, 0x800346A4),
        (0x8002E0FC, 0x800346AC),
        (0x80017880, 0x800346B4),
        (0x8003549C, 0x800346BC),
    ];

    /// <summary>The HUD's records, 0x24 bytes each: +0 drawn when 1 (0xFF ends the
    /// list), +4 the model, +8 a gauge's length, +0x18 a rotation. Record 0 is the
    /// compass, 3-5 and 6-8 the digits of HP and MP, 9 and 10 the two gauges.</summary>
    const uint HudRecords = 0x80067774, HudStride = 0x24, HudCount = 14;
    const uint Shown = 0x0, Model = 0x4, Length = 0x8, Pitch = 0x18, Yaw = 0x1A;

    /// <summary>What record 0's and every other record's drawn byte is copied from.</summary>
    const uint CompassShown = 0x801994DE, OthersShown = 0x801994DD;

    const uint Hp = 0x80199428, Mp = 0x8019942C;
    const uint GaugeA = 0x8019942E, GaugeB = 0x80199432;

    /// <summary>A digit's model is the digit plus this.</summary>
    const uint DigitModel = 3;

    /// <summary>The compass needle's angular velocity: the damped spring its yaw
    /// chases the camera's on. Long documented as a screen-shake accumulator.</summary>
    public const uint NeedleSpeed = 0x8006E608;

    enum Mode { Off, On, Verify }
    static Mode _mode = Mode.On;
    static bool _queued;
    static Action<CpuContext, IMemory>[]? _callees;

    /// <summary>
    /// A camera to draw the frame from instead of the one stage 13 is handed, or null
    /// for the game's. Needs the C# routine: under <c>KF2_STAGE13=0</c>, under
    /// <c>verify</c> and with PGXP's CPU tracking on, the recompiled one draws and
    /// this is not read.
    /// </summary>
    public static Camera? ViewOverride { get; set; }

    static readonly ModInfo _self = new()
    {
        Id = "kf2.stage13",
        Name = "Stage 13",
        Version = "1.0",
        Description = "func_800342D8, the renderer, in C#.",
    };

    public static void Configure(string? mode)
    {
        _mode = mode?.Trim().ToLowerInvariant() switch
        {
            "0" or "off" => Mode.Off,
            "verify" => Mode.Verify,
            _ => Mode.On,
        };
    }

    public static void Install() => HookAttach.OnOverlayLoad("stage 13", Attach);

    static bool Attach()
    {
        var target = SymbolRegistry.Resolve("game", null, Routine);
        if (target == null) return false;
        if (!Bind())
        {
            Console.Error.WriteLine("[KF2] stage 13: a callee is not mapped; the recompiled routine stays.");
            return false;
        }
        if (!_queued)
        {
            var impl = typeof(Stage13).GetMethod(nameof(Replace), BindingFlags.NonPublic | BindingFlags.Static)!;
            _queued = HookManager.AddReplace(_self, target, impl);
            if (!_queued) return false;
        }
        HookManager.Commit();
        bool ok = HookAttach.Installed(target);
        Console.WriteLine(ok ? $"[KF2] stage 13: {_mode.ToString().ToLowerInvariant()}"
                             : "[KF2] stage 13: not installed");
        return ok;
    }

    /// <summary>A delegate per callee. Calling one runs the function's detour, so
    /// every hook on it fires as it does for the recompiled body's direct call.</summary>
    static bool Bind()
    {
        if (_callees != null) return true;
        if (Calls.Length != Enum.GetValues<Site>().Length) return false;
        var fns = new Action<CpuContext, IMemory>[Calls.Length];
        for (int i = 0; i < Calls.Length; i++)
        {
            var mi = SymbolRegistry.Resolve("game", null, Calls[i].Callee);
            if (mi == null) return false;
            fns[i] = mi.CreateDelegate<Action<CpuContext, IMemory>>();
        }
        _callees = fns;
        return true;
    }

    static void Replace(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        // PGXP's RAM shadow is kept by the recompiled stores, which C# stores skip. A
        // stage 13 inside one being recorded is the recompiled one's too.
        if (_mode == Mode.Off || RecompOne.Runtime.Pgxp.Pgxp.CpuTracking || m is not PSMemory mem
            || Verifier.Recording)
        {
            orig(c, m);
            return;
        }
        if (_mode == Mode.Verify) Verifier.Run(orig, c, mem);
        else Run(c, mem);
    }

    /// <summary>The routine, transcribed.</summary>
    static void Run(CpuContext c, PSMemory mem)
    {
        uint sp = c.SP - 0x18u;
        c.SP = sp;
        mem.WriteU32(sp + 0x10u, c.RA);

        if (ViewOverride is { } view && !Verifier.Replaying)
        {
            CameraBlock.Store(mem, view);
            c.A0 = 0u;
            c.A1 = 0u;
        }
        Call(c, mem, Site.View);
        Call(c, mem, Site.AnimatedTextures);
        Call(c, mem, Site.Fade);
        Call(c, mem, Site.CullGrid);
        Call(c, mem, Site.FrameHead);
        Call(c, mem, Site.SoundMark);
        Call(c, mem, Site.Arm);
        HudState(c, mem);
        Submit(c, mem);
        Call(c, mem, Site.Present);
        Call(c, mem, Site.FrameGate);
        Call(c, mem, Site.SoundService);

        c.RA = mem.ReadU32(sp + 0x10u);
        c.SP = sp + 0x18u;
    }

    /// <summary>
    /// Stage 13's drawing half -- the view, the cull grid, and every call that adds to
    /// the ordering table -- into whatever table and primitive descriptor are current.
    /// Nothing that advances the world, flips a buffer or presents. A null
    /// <paramref name="view"/> is the stored one; any other is stored first and left
    /// in the block, as stage 13 leaves its own. False when a callee is not mapped.
    /// </summary>
    public static bool DrawScene(CpuContext c, PSMemory mem, Camera? view = null)
    {
        if (!Bind()) return false;
        if (view is { } v) CameraBlock.Store(mem, v);
        c.A0 = 0u;
        c.A1 = 0u;
        Call(c, mem, Site.View);
        Call(c, mem, Site.CullGrid);
        Call(c, mem, Site.Arm);
        Submit(c, mem);
        return true;
    }

    /// <summary>Every call after the HUD state that adds to the ordering table.</summary>
    static void Submit(CpuContext c, PSMemory mem)
    {
        Call(c, mem, Site.Hud);
        Call(c, mem, Site.Overlays);
        Call(c, mem, Site.Tiles);
        Call(c, mem, Site.Models);
        Call(c, mem, Site.Tint1);
        Call(c, mem, Site.Tint2);
        Call(c, mem, Site.Tint3);
        Call(c, mem, Site.Tint4);
    }

    static void Call(CpuContext c, PSMemory mem, Site site)
    {
        c.RA = Calls[(int)site].Return;
        if (Verifier.Replaying) Verifier.Take(c, mem, site);
        else _callees![(int)site](c, mem);
    }

    // ---- the HUD state, the one block of the body that is not a call ------------

    static uint Record(uint i) => HudRecords + i * HudStride;

    /// <summary>
    /// Which HUD records are drawn, the digits of HP and MP, the two gauges' lengths,
    /// and the compass's rotation: its pitch is the camera's, and its yaw chases the
    /// camera's through <see cref="SwingNeedle"/>.
    /// </summary>
    static void HudState(CpuContext c, PSMemory mem)
    {
        uint compass = mem.ReadU8(CompassShown), others = mem.ReadU8(OthersShown);
        mem.WriteU8(Record(0) + Shown, (byte)compass);
        for (uint i = 1; i < HudCount; i++) mem.WriteU8(Record(i) + Shown, (byte)others);

        c.A0 = (uint)(short)mem.ReadU16(Record(0) + Yaw);
        c.A1 = (uint)(short)mem.ReadU16(CameraBlock.Angles + 2u);
        Call(c, mem, Site.CompassError);
        SwingNeedle(mem, (int)c.V0);

        Digits(mem, 3, mem.ReadU16(Hp) % 1000u);
        Digits(mem, 6, mem.ReadU16(Mp) % 1000u);
        mem.WriteU16(Record(9) + Length, Gauge(mem.ReadU16(GaugeA)));
        mem.WriteU16(Record(10) + Length, Gauge(mem.ReadU16(GaugeB)));

        int speed = (int)mem.ReadU32(NeedleSpeed);
        mem.WriteU16(Record(0) + Yaw, (ushort)(mem.ReadU16(Record(0) + Yaw) + (speed >> 6)));
        mem.WriteU16(Record(0) + Pitch, mem.ReadU16(CameraBlock.Angles));
    }

    /// <summary>
    /// The needle's spring, stepped once a call and so once a rendered frame: the
    /// error is added to the speed <c>v</c>, which then loses about an eighth of
    /// itself -- <c>(v + 7) &gt;&gt; 3</c> when positive, <c>(v - 7) &gt;&gt; 3</c> when
    /// negative. The needle turns by a 64th of the speed in <see cref="HudState"/>.
    /// </summary>
    static void SwingNeedle(PSMemory mem, int error)
    {
        int v = (int)mem.ReadU32(NeedleSpeed) + error;
        mem.WriteU32(NeedleSpeed, (uint)v);
        if (v != 0) mem.WriteU32(NeedleSpeed, (uint)(v - ((v > 0 ? v + 7 : v - 7) >> 3)));
    }

    /// <summary>Three digits' models, from a value already below 1000.</summary>
    static void Digits(PSMemory mem, uint first, uint value)
    {
        mem.WriteU16(Record(first) + Model, (ushort)(value / 100u + DigitModel));
        mem.WriteU16(Record(first + 1u) + Model, (ushort)(value % 100u / 10u + DigitModel));
        mem.WriteU16(Record(first + 2u) + Model, (ushort)(value % 10u + DigitModel));
    }

    /// <summary>A gauge's length: 204 at 5000.</summary>
    static ushort Gauge(uint value) => (ushort)((int)(value * 204u) / 5000);

    // ---- KF2_STAGE13=verify ------------------------------------------------------

    /// <summary>
    /// Records the recompiled routine at each of its calls, then runs
    /// <see cref="Run"/> with every call replayed from the record.
    ///
    /// A pre and a post on each callee take what that call was handed and what it
    /// left: the registers and the GTE always, and the whole of RAM where the body's
    /// own work comes next -- on entry to <see cref="Site.View"/>,
    /// <see cref="Site.CompassError"/> and <see cref="Site.Hud"/>, which is where the
    /// two runs are compared, and on exit from <see cref="Site.Arm"/> and
    /// <see cref="Site.CompassError"/>, which is where the replay picks up. Between
    /// any other two calls the body does nothing, so there is nothing to compare.
    ///
    /// The hooks are added on the first verified call, after every patch has attached,
    /// so the posts run last and a callee's record includes what every other hook on
    /// it did. A call is matched to its site by the return address and by the order
    /// the sites come in; one made from inside another is nested and ignored.
    /// </summary>
    static class Verifier
    {
        static readonly int N = Calls.Length;

        public static bool Recording { get; private set; }
        public static bool Replaying { get; private set; }

        static bool? _hooked;

        /// <summary>Where the two runs are compared: each call that follows the body's own work.</summary>
        static readonly Site[] Compared = [Site.View, Site.CompassError, Site.Hud];

        /// <summary>Where the replay takes RAM from the record: each call the body's own work follows.</summary>
        static readonly Site[] Resumed = [Site.Arm, Site.CompassError];

        // The record: what each call was handed, what it left, and RAM where it counts.
        static readonly CpuSnapshot[] _entry = new CpuSnapshot[N], _exit = new CpuSnapshot[N];
        static readonly Gte.State[] _exitGte = Enumerable.Range(0, N).Select(_ => new Gte.State()).ToArray();
        static readonly byte[]?[] _entryRam = new byte[]?[N], _exitRam = new byte[]?[N];
        static int _next, _open, _nested;

        static byte[] _before = [], _final = [];
        static readonly Gte.State _gteBefore = new(), _gteFinal = new();
        static int _taken;

        static long _calls, _bad, _incomplete, _stray;
        static readonly List<string> _samples = [];
        static double _reportAt;

        public static void Run(Action<CpuContext, IMemory> orig, CpuContext c, PSMemory mem)
        {
            _hooked ??= Hook();
            if (_hooked == false)
            {
                orig(c, mem);
                return;
            }

            var ram = Differential.Ram(mem);
            Allocate(ram.Length);
            ram.CopyTo(_before);
            var entry = c.Snapshot();
            Gte.Save(_gteBefore);

            _next = 0;
            _open = -1;
            _nested = 0;
            Recording = true;
            try { orig(c, mem); }
            finally { Recording = false; }
            ram.CopyTo(_final);
            var theirs = c.Snapshot();
            Gte.Save(_gteFinal);

            _calls++;
            if (_next != N)
            {
                _incomplete++;
                Sample($"the record stops at {(Site)_next}: {_next} of {N} calls seen");
            }
            else
            {
                _before.CopyTo(ram);
                c.Restore(entry);
                Gte.Load(_gteBefore);
                _taken = 0;
                Replaying = true;
                try { Stage13.Run(c, mem); }
                finally { Replaying = false; }

                if (_taken != N) Mismatch($"ours made {_taken} of {N} calls");
                var ours = c.Snapshot();
                if (!Differential.CalleeSavedEqual(ours, theirs))
                    Mismatch($"on return: sp {theirs.SP:X}/{ours.SP:X} ra {theirs.RA:X}/{ours.RA:X}");
            }

            // The recompiled result stands.
            _final.CopyTo(ram);
            c.Restore(theirs);
            Gte.Load(_gteFinal);
            Report();
        }

        /// <summary>One call of ours, answered from the record.</summary>
        public static void Take(CpuContext c, PSMemory mem, Site site)
        {
            int i = (int)site;
            if (i != _taken) Mismatch($"ours called {site} where the routine called {(Site)_taken}");
            _taken = i + 1;

            ref readonly var theirs = ref _entry[i];
            if (c.SP != theirs.SP || c.RA != theirs.RA || !Differential.CalleeSavedEqual(c.Snapshot(), theirs))
                Mismatch($"{site}: sp {theirs.SP:X}/{c.SP:X} ra {theirs.RA:X}/{c.RA:X}");
            // The two callees that read arguments.
            if (site is Site.View or Site.CompassError && (c.A0 != theirs.A0 || c.A1 != theirs.A1))
                Mismatch($"{site}: a0 {theirs.A0:X}/{c.A0:X} a1 {theirs.A1:X}/{c.A1:X}");
            if (_entryRam[i] is { } before
                && Differential.Describe(Differential.Ram(mem), before, 0, before.Length) is { } diff)
                Mismatch($"entering {site}: {diff}");

            c.Restore(_exit[i]);
            Gte.Load(_exitGte[i]);
            if (site == Site.SoundService) _final.CopyTo(Differential.Ram(mem));
            else _exitRam[i]?.CopyTo(Differential.Ram(mem));
        }

        public static void Enter(CpuContext c, IMemory m)
        {
            if (!Recording) return;
            if (_open >= 0) { _nested++; return; }
            if (_next >= N || c.RA != Calls[_next].Return) { _stray++; return; }
            _open = _next;
            _entry[_open] = c.Snapshot();
            if (_entryRam[_open] is { } buf) Differential.Ram((PSMemory)m).CopyTo(buf);
        }

        public static void Exit(CpuContext c, IMemory m)
        {
            if (!Recording) return;
            if (_nested > 0) { _nested--; return; }
            if (_open < 0) return;
            _exit[_open] = c.Snapshot();
            Gte.Save(_exitGte[_open]);
            if (_exitRam[_open] is { } buf) Differential.Ram((PSMemory)m).CopyTo(buf);
            _next = _open + 1;
            _open = -1;
        }

        static bool Hook()
        {
            var enter = typeof(Verifier).GetMethod(nameof(Enter), BindingFlags.Public | BindingFlags.Static)!;
            var exit = typeof(Verifier).GetMethod(nameof(Exit), BindingFlags.Public | BindingFlags.Static)!;
            var targets = new MethodInfo[N];
            for (int i = 0; i < N; i++)
                targets[i] = SymbolRegistry.Resolve("game", null, Calls[i].Callee)!;
            foreach (var t in targets)
            {
                HookManager.AddPre(_self, t, enter);
                HookManager.AddPost(_self, t, exit);
            }
            HookManager.Commit();
            bool ok = targets.All(HookAttach.Installed);
            Console.WriteLine(ok ? "[stage13] verify: recording every call"
                                 : "[stage13] verify: a callee could not be hooked; not verifying");
            return ok;
        }

        static void Allocate(int bytes)
        {
            if (_before.Length == bytes) return;
            _before = new byte[bytes];
            _final = new byte[bytes];
            foreach (var s in Compared) _entryRam[(int)s] = new byte[bytes];
            foreach (var s in Resumed) _exitRam[(int)s] = new byte[bytes];
        }

        static void Mismatch(string what)
        {
            _bad++;
            Sample(what);
        }

        static void Sample(string s)
        {
            if (_samples.Count < 8) _samples.Add(s);
        }

        static void Report()
        {
            double now = Environment.TickCount64 / 1000.0;
            if (now < _reportAt) return;
            _reportAt = now + 2.0;
            Console.WriteLine($"[stage13] verify func_800342D8: {_calls} call(s), {_bad} mismatch(es), " +
                              $"{_incomplete} incomplete record(s), {_stray} stray call(s) ignored");
            foreach (var s in _samples) Console.WriteLine($"[stage13]   {s}");
            _samples.Clear();
            _calls = _bad = _incomplete = _stray = 0;
        }
    }
}
