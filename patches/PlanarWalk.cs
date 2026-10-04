using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// Planar reflections: the world walked a second time from the camera mirrored in
/// the water, into an ordering table of the port's own, drawn into a texture the
/// reflection pass reads wherever a surface lies on the water's plane.
///
///     KF2_PLANAR=1            on (off by default: the picture has not been judged)
///     KF2_PLANAR_TOLERANCE=48 how far off the plane a surface may be and still take it, world units
///     KF2_PLANAR_RIPPLE=4     how far the water's own texture bends the reflection; 0 a flat mirror
///     KF2_PLANAR_BIAS=8       how far above the plane geometry has to be to be reflected
///     KF2_PLANAR_PROBE=1      the plane, the walk, the arena and the readback, every two seconds
///     KF2_PLANAR_FOG=0        fog the reflection at the mirrored camera's own depth (level by default)
///
/// **Why the walks and not the picture.** The screen-space pass can only reflect
/// what is on screen and in front of everything else, so a wall above the camera's
/// view or a creature behind a pillar is simply absent from the water. The port owns
/// the routines that enumerate the world (<see cref="TileWalk"/> and
/// <see cref="ModelWalk"/>), so it can ask for the world again from somewhere else.
///
/// **What "again" is.** After the game's own object walk returns, inside stage 13:
///
/// 1. The camera block at <c>0x80192E18</c> (view matrix, the pitch-only matrix,
///    position, angles, tile index) is saved, and `func_8002E22C` -- the routine
///    stage 13 builds it with -- is called again with the position mirrored in the
///    plane and the pitch and roll negated. That is the mirror image of the camera
///    as an ordinary camera: the game's culling, clipper and winding tests all stay
///    right, and the flip that makes it a mirror is the pass's, when it samples row
///    <c>2*OFY - y</c>.
/// 2. The frame's primitive descriptor and ordering-table pointer are pointed at the
///    arena <see cref="PrimBuffer"/> keeps past its two buffers, and the tile walk
///    `func_80031C94` runs over the same 24x24 grid the frame just used -- the
///    grid is a plan-view footprint, and a mirror in a horizontal plane does not move
///    anything in plan.
/// 3. Every call the object walk made to the model submitter `func_80032588` was
///    recorded -- its four registers, its nine stack words and the two things it
///    was handed in the walk's own frame -- and is made again, from the same stack
///    pointer, so every hook on the submitter (the pose carry, the depth records)
///    sees the call it saw the first time. The walk itself is not run again: it
///    plays ambient sounds, uploads texture pages and steps the sprite clock.
/// 4. Everything is put back -- the camera block, the two pointers, the model and
///    vertex bases the submitter moved, the fog word, the GTE and the registers.
///    Steps 2 and 4 are a <see cref="ScenePass"/>.
///
/// Then at the frame's own `DrawOTag`, before the game's table is drawn, the mirrored
/// table is handed to the runtime's `DrawOTag` with
/// <see cref="PlanarReflections.Capturing"/> set, which sends every primitive to the
/// current target's planar texture and discards what lies under the water.
///
/// **The plane comes from the picture.** The backend takes every triangle it
/// classifies as water back to world space with the camera published here and bins
/// the heights by screen area; the heaviest bin is the next frame's plane. No water
/// on screen, no mirrored walk: it costs nothing away from water.
///
/// Not reflected: the skybox and anything else outside the two walks (the pass's
/// march still finds them on screen), the player's arm, the view-space models
/// (matrix 0), and the object kind `0xF0`, whose submitter is handed a translation
/// the first walk already projected. See "Planar reflections" in docs/RENDERING.md.
/// </summary>
public static class PlanarWalk
{
    const uint Walk = 0x800331B4;       // the object walk: its submits are recorded
    const uint Submit = 0x80032588;     // the model submitter
    const uint DrawOTag = 0x80060818;

    const uint ViewMatrix = CameraBlock.ViewMatrix, CamPos = CameraBlock.Position;

    const uint WalkFrame = 0x300;

    public const string OnKey = "kf2.ssr.planar";
    public const float DefaultStrength = 0.6f;

    /// <summary>How much water reflects at a grazing angle: the water material's
    /// reflectivity, which only the planar walk now answers for.</summary>
    public static float Strength => SurfaceMaterial.Reflectivity[SurfaceMaterial.Water];

    public static void SetStrength(float s)
    {
        SurfaceMaterial.Reflectivity[SurfaceMaterial.Water] = Math.Clamp(s, 0f, 1f);
        SurfaceMaterial.Changed();
    }

    static bool? _forced;
    static bool _probe;
    static float _bias = 8f;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.planar",
        Name = "Planar reflections",
        Version = "1.0",
        Description = "Reflects the world in water from a mirrored camera, by walking it twice.",
    };

    public static bool Enabled => PlanarReflections.Enabled;

    /// <summary>The tolerance asked for; <see cref="Waves"/> adds the swell's height.</summary>
    public static float BaseTolerance { get; private set; } = PlanarReflections.Tolerance;

    public static void Configure(string? on, string? tolerance, string? ripple, string? bias, string? probe, string? fog = null)
    {
        PlanarReflections.LevelFog = fog != "0";
        if (!string.IsNullOrWhiteSpace(on)) _forced = on != "0";
        if (float.TryParse(tolerance, out float t) && t > 0f) PlanarReflections.Tolerance = BaseTolerance = t;
        if (float.TryParse(ripple, out float r) && r >= 0f) PlanarReflections.Ripple = r;
        if (float.TryParse(bias, out float b)) _bias = b;
        _probe = !string.IsNullOrWhiteSpace(probe) && probe != "0";
        PlanarReflections.Probe = _probe;
        // The share of the water that took the planar texture is only in the
        // reflection pass's readback.
        if (_probe) ScreenReflections.Probe = true;
    }

    public static void Install()
    {
        PlanarReflections.Enabled = _forced ?? true;
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            PlanarReflections.Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, true);
            StandOthersDown();
            if (!Reflections.StrengthForced)
                SetStrength(DefaultStrength);
            Console.WriteLine($"[KF2] planar reflections: {(Enabled ? "on" : "off")}" +
                              (Enabled ? $", strength {Strength:F2}, tolerance {PlanarReflections.Tolerance:F0}, ripple {PlanarReflections.Ripple:F1}, " +
                                         $"cull {(PlanarCull.On ? "its own" : "the eye's")}" : ""));
        });
        Event.AddListener<OverlayLoadedEvent>(_ => { _pending = false; _n = 0; });
        HookAttach.OnOverlayLoad("planar", Attach);
    }

    public static void SetEnabled(bool on)
    {
        PlanarReflections.Enabled = on;
        _pending = false;
        StandOthersDown();
    }

    /// <summary>The planar walk is the reflection: the world reflections, which draw
    /// into the same planar texture, stand down while it is on.</summary>
    static void StandOthersDown()
    {
        if (!Enabled || !RetainedMap.Enabled) return;
        RetainedMap.SetEnabled(false);
        Console.WriteLine("[KF2] planar reflections: the world reflections stand down while the planar walk is on");
    }

    static bool _queuedWalk, _queuedSubmit, _queuedDraw;

    static bool Attach()
    {
        SymbolRegistry.Build();
        var walk = SymbolRegistry.Resolve("game", null, Walk);
        var submit = SymbolRegistry.Resolve("game", null, Submit);
        var draw = SymbolRegistry.Resolve("game", null, DrawOTag);
        if (walk == null || submit == null || draw == null) return false;

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        var t = typeof(PlanarWalk);
        if (!_queuedWalk)
            _queuedWalk = HookManager.AddPre(_self, walk, t.GetMethod(nameof(BeforeWalk), flags)!)
                        && HookManager.AddPost(_self, walk, t.GetMethod(nameof(AfterWalk), flags)!);
        if (!_queuedSubmit)
            _queuedSubmit = HookManager.AddPre(_self, submit, t.GetMethod(nameof(BeforeSubmit), flags)!);
        if (!_queuedDraw)
            _queuedDraw = HookManager.AddPre(_self, draw, t.GetMethod(nameof(BeforeDrawOTag), flags)!);
        HookManager.Commit();

        bool ok = HookAttach.Installed(walk) && HookAttach.Installed(submit) && HookAttach.Installed(draw);
        Console.WriteLine(ok
            ? "[KF2] planar reflections: hooked the object walk, the submitter and DrawOTag"
            : "[KF2] planar reflections: not every hook attached; nothing will be mirrored.");
        return ok;
    }

    // ---- the object walk's submits, recorded -----------------------------------

    const int StackWords = 9;

    static bool _recording, _replaying;

    /// <summary>Set while the tile walk runs from the mirrored camera.</summary>
    public static bool Mirroring { get; private set; }

    /// <summary>Set while the object walk's submits are made again from it.</summary>
    public static bool Replaying => _replaying && !Mirroring;
    static uint _walkLo, _walkHi;
    static int _n;
    static uint[] _regs = new uint[4 * 128];
    static uint[] _stack = new uint[StackWords * 128];
    static uint[] _sp = new uint[128];
    static byte[] _pos = new byte[12 * 128];
    static byte[] _rot = new byte[8 * 128];
    static ModelKind[] _kind = new ModelKind[128];
    static int[] _slot = new int[128];
    static uint[] _record = new uint[128];
    static bool[] _taken = new bool[128];

    public static void BeforeWalk(CpuContext c, IMemory m)
    {
        _n = 0;
        _recording = PlanarReflections.Enabled && !_replaying;
        _walkHi = c.SP;
        _walkLo = c.SP - WalkFrame;
    }

    public static void BeforeSubmit(CpuContext c, IMemory m) => Record(c, m);

    /// <summary>One submit, as the walk is about to make it. Also called by
    /// <see cref="ModelWalk"/> in place of the submit for a model only
    /// <see cref="PlanarCull"/> admits, which the picture does not draw.</summary>
    public static void Record(CpuContext c, IMemory m)
    {
        if (!_recording || _replaying) return;
        if (_n == _sp.Length) Grow();
        int i = _n++;
        _regs[i * 4] = c.A0; _regs[i * 4 + 1] = c.A1; _regs[i * 4 + 2] = c.A2; _regs[i * 4 + 3] = c.A3;
        for (int k = 0; k < StackWords; k++) _stack[i * StackWords + k] = m.ReadU32(c.SP + 0x10u + (uint)k * 4u);
        _sp[i] = c.SP;
        // The walk hands a position or a rotation it built in its own frame; that
        // frame is gone by the time the call is made again.
        if (InFrame(c.A2)) for (int k = 0; k < 12; k++) _pos[i * 12 + k] = m.ReadU8(c.A2 + (uint)k);
        if (InFrame(c.A3)) for (int k = 0; k < 8; k++) _rot[i * 8 + k] = m.ReadU8(c.A3 + (uint)k);
        _kind[i] = ModelWalk.SubmitKind;
        _slot[i] = ModelWalk.SubmitSlot;
        _record[i] = ModelWalk.SubmitRecord;
        _taken[i] = false;
    }

    static bool InFrame(uint a) => a >= _walkLo && a < _walkHi;

    /// <summary>0085. The submit just recorded was drawn whole from its cached mesh: its
    /// instance is the mirror's too, and the replay leaves it out.</summary>
    public static void TakenLast()
    {
        if (_recording && !_replaying && _n > 0) _taken[_n - 1] = true;
    }

    static void Grow()
    {
        int n = _sp.Length * 2;
        Array.Resize(ref _regs, 4 * n);
        Array.Resize(ref _stack, StackWords * n);
        Array.Resize(ref _sp, n);
        Array.Resize(ref _pos, 12 * n);
        Array.Resize(ref _rot, 8 * n);
        Array.Resize(ref _kind, n);
        Array.Resize(ref _slot, n);
        Array.Resize(ref _record, n);
        Array.Resize(ref _taken, n);
    }

    // ---- the mirrored walk -----------------------------------------------------

    static readonly ScenePass _pass = new();
    static readonly short[] _r = new short[9];

    // What the next DrawOTag draws first, and the two planes it is drawn with.
    static bool _pending;
    static uint _pendingOt;
    static readonly float[] _clip = new float[4], _view = new float[4], _level = new float[3];

    static bool Ready =>
        PlanarReflections.Enabled && PlanarReflections.Supported
        && PrimBuffer.Relocated && !TileWalk.Verifying && !ModelWalk.Verifying
        && RecompOne.Runtime.Hle.GpuTrace.Sink == null;

    public static void AfterWalk(CpuContext c, IMemory m)
    {
        _recording = false;
        _pending = false;
        if (_replaying || m is not PSMemory mem || !PlanarReflections.Enabled) return;

        // The camera the frame is about to be drawn with, for finding the water in
        // it; published whether or not anything is mirrored, since the water is how
        // the plane is found in the first place.
        for (int i = 0; i < 9; i++) _r[i] = (short)mem.ReadU16(ViewMatrix + (uint)i * 2u);
        PlanarReflections.SetCamera(_r, (int)mem.ReadU32(CamPos), (int)mem.ReadU32(CamPos + 4u), (int)mem.ReadU32(CamPos + 8u));

        if (!Ready) PlanarReflections.TakePlane(out _, out _);
        else if (!PlanarReflections.TakePlane(out float plane, out double area)) _noWater++;
        // From under the water, or level with it, there is nothing above it to see
        // reflected; the mirrored camera would be the one above.
        else if ((int)mem.ReadU32(CamPos + 4u) >= plane - 16f) _below++;
        else
        {
            if (_walks == 0 || plane < _planeMin) _planeMin = plane;
            if (_walks == 0 || plane > _planeMax) _planeMax = plane;
            if (plane != _plane) _moves++;
            _plane = plane;
            _area = area;
            long start = Stopwatch.GetTimestamp();
            Mirror(c, mem, plane);
            _ms += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        if (_probe) Report();
    }

    static void Mirror(CpuContext c, PSMemory mem, float plane)
    {
        uint walkSp = c.SP;
        uint mainHead = mem.ReadU32(ScenePass.OtPointer) + (ScenePass.OtEntries - 1u) * 4u;
        var cam = Camera.Read(mem);
        int h = (int)MathF.Round(plane);
        int mirroredY = 2 * h - cam.Y;

        // The main view's plane, for the pass: a surface's height below the water,
        // Y being down, from its view position. Row 1 of R transposed.
        _view[0] = _r[1] / 4096f; _view[1] = _r[4] / 4096f; _view[2] = _r[7] / 4096f;
        _view[3] = cam.Y - plane;

        // The port's own arena and table.
        uint arena = PrimBuffer.MirrorArena;
        _pass.Begin(c, mem, PrimBuffer.MirrorScratch, PrimBuffer.MirrorOt, arena, arena + PrimBuffer.MirrorArenaBytes);
        _replaying = true;
        int replayed = 0;
        try
        {
            // The mirrored camera, built by the routine that builds the real one.
            CameraBlock.Build(c, mem, cam with
            {
                Y = mirroredY,
                Pitch = (short)-cam.Pitch,
                Roll = (short)-cam.Roll,
            });

            // Kept above the water, in the mirrored view: with R' that view's
            // rotation, a view position's world Y is R'^T row 1 . p + Y'.
            Span<short> rm = stackalloc short[9];
            for (int i = 0; i < 9; i++) rm[i] = (short)mem.ReadU16(ViewMatrix + (uint)i * 2u);
            _clip[0] = -rm[1] / 4096f; _clip[1] = -rm[4] / 4096f; _clip[2] = -rm[7] / 4096f;
            _clip[3] = plane - _bias - mirroredY;

            // The view's forward with its height taken out, in the mirrored view:
            // row 2 of R' is the forward in world axes.
            float fx = rm[6], fz = rm[8], fl = MathF.Sqrt(fx * fx + fz * fz);
            if (fl > 0f) { fx /= fl; fz /= fl; }
            _level[0] = (rm[0] * fx + rm[2] * fz) / 4096f;
            _level[1] = (rm[3] * fx + rm[5] * fz) / 4096f;
            _level[2] = (rm[6] * fx + rm[8] * fz) / 4096f;

            // 0085. The backend draws the mirror's opaque map and models; the walk and
            // the replay below leave them off the mirrored table.
            if (GpuWorld.MirrorActive) RetainedScene.BeginMirror(RetainedMap.ReadView(mem));

            c.SP = walkSp;
            c.RA = 0x80034684u;
            Mirroring = true;
            try { KingsField2.func_80031C94(c, mem); }
            finally { Mirroring = false; }

            for (int i = 0; i < _n; i++)
            {
                // Matrix 0 is a model placed in view space, already where it is
                // drawn: it belongs to the real camera and has no mirror image.
                if (_stack[i * StackWords + 2] == 0u) { _viewSpace++; continue; }
                // 0085. Drawn whole from its mesh: the main view's instance is the mirror's.
                if (_taken[i] && GpuWorld.MirrorModelsActive) { _instanced++; continue; }
                // The walk polls interrupts, so the loader may have evicted the model
                // since the walk tested it.
                if (!Resident(mem, _regs[i * 4 + 1])) { _gone++; continue; }
                uint sp = _sp[i];
                if (_regs[i * 4 + 2] >= _walkLo && _regs[i * 4 + 2] < _walkHi)
                    for (int k = 0; k < 12; k++) mem.WriteU8(_regs[i * 4 + 2] + (uint)k, _pos[i * 12 + k]);
                if (_regs[i * 4 + 3] >= _walkLo && _regs[i * 4 + 3] < _walkHi)
                    for (int k = 0; k < 8; k++) mem.WriteU8(_regs[i * 4 + 3] + (uint)k, _rot[i * 8 + k]);
                for (int k = 0; k < StackWords; k++) mem.WriteU32(sp + 0x10u + (uint)k * 4u, _stack[i * StackWords + k]);
                ModelWalk.SetSubmit(_kind[i], _slot[i], _record[i]);
                c.SP = sp;
                c.A0 = _regs[i * 4]; c.A1 = _regs[i * 4 + 1]; c.A2 = _regs[i * 4 + 2]; c.A3 = _regs[i * 4 + 3];
                c.RA = 0x800338C0u;
                KingsField2.func_80032588(c, mem);
                replayed++;
                if (Remaster.Props.NameOf(_record[i]) != null) _props++;
            }
        }
        finally
        {
            _replaying = false;
            _pass.End(c, mem);
        }

        _replayed += replayed;
        if (_pass.Used > _peak) _peak = _pass.Used;
        if (_pass.Overflowed) _overflows++;
        _pendingOt = _pass.Head;
        _pendingMain = mainHead;
        _pending = true;
        _walks++;
    }

    static uint _pendingMain;

    const uint ModelTable = 0x8018E1A0;

    /// <summary>`func_80032CD8`'s test: a model id past the 104 static ones is
    /// resident when its entry is set and the status byte before it is 1 or 2. The
    /// entry and the mesh table it leads to must also lie in RAM.</summary>
    static bool Resident(PSMemory mem, uint id)
    {
        uint lo = 0x80000010u, hi = 0x80000000u + RecompOne.Runtime.Runtime.RamSize - 0x10u;
        uint entry = mem.ReadU32(ModelTable + (id & 0xFFFFu) * 4u);
        if (entry < lo || entry >= hi) return false;
        if ((id & 0xFFFFu) >= 0x68u && ((mem.ReadU8(entry - 0xCu) - 1u) & 0xFFu) >= 2u) return false;
        uint table = entry + mem.ReadU32(entry + 8u);
        return table >= lo && table < hi;
    }

    /// <summary>The mirrored table first, into the planar texture of the target the
    /// game's own table is about to be drawn into.</summary>
    public static void BeforeDrawOTag(CpuContext c, IMemory m)
    {
        if (!_pending) return;
        _pending = false;
        uint mask = RecompOne.Runtime.Runtime.RamWordMask;
        // Not the table this frame's walk filled: a loop drawing its own frame.
        if ((c.A0 & mask) != (_pendingMain & mask)) { _mismatch++; return; }

        PlanarReflections.Serial++;
        _clip.CopyTo(PlanarReflections.ClipPlane, 0);
        _view.CopyTo(PlanarReflections.ViewPlane, 0);
        _level.CopyTo(PlanarReflections.LevelAxis, 0);
        PlanarReflections.Captures++;
        uint a0 = c.A0;
        PlanarReflections.Capturing = true;
        try
        {
            c.A0 = _pendingOt;
            RecompOne.Runtime.Sdk.LibGpu.DrawOTag(c, m);
        }
        finally
        {
            PlanarReflections.Capturing = false;
            c.A0 = a0;
        }
    }

    // ---- the probe -------------------------------------------------------------

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _reportedAt, _ms;
    static long _instanced;
    static long _walks, _replayed, _props, _viewSpace, _gone, _noWater, _below, _mismatch, _overflows, _peak;
    static float _plane, _planeMin, _planeMax;
    static long _moves, _mirrorOnlyAt;
    static double _area;

    static void Report()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _reportedAt;
        if (dt < 2.0) return;
        _reportedAt = now;

        Console.WriteLine($"[KF2] planar: plane Y {_plane:F0} over {_area:F0} px of water (min {_planeMin:F0}, max {_planeMax:F0}, moved {_moves / dt:F1}/s); " +
                          $"{_walks / dt:F1} mirrored walks/s at {(_walks == 0 ? 0 : _ms / _walks):F3} ms, " +
                          $"{_replayed / dt:F0} submits replayed/s ({_props / dt:F0} of them props, {_instanced / dt:F0} left to their instance, {_viewSpace / dt:F0} view-space skipped, {_gone} evicted before the replay in all), " +
                          $"{_noWater / dt:F1} frames/s with no water, {_below / dt:F1} under it; arena peak {_peak}/{PrimBuffer.MirrorArenaBytes} bytes, " +
                          $"{_overflows} overflow(s), {_mismatch} table mismatch(es); " +
                          $"its own cull {(PlanarCull.On ? $"{PlanarCull.Added / (double)Math.Max(PlanarCull.Frames, 1):F1} cells added a frame, " +
                                                            $"{(ModelWalk.MirrorOnlySubmits - _mirrorOnlyAt) / dt:F0} mirror-only models/s, cone {(float.IsInfinity(PlanarCull.Pitched) ? "open" : $"{PlanarCull.Pitched:F2}x")} for the pitch" : "off")}");
        PlanarCull.Frames = PlanarCull.Added = 0;
        _mirrorOnlyAt = ModelWalk.MirrorOnlySubmits;
        Console.WriteLine($"[KF2] planar: {PlanarReflections.Captures / dt:F1} captures/s, {PlanarReflections.Cleared / dt:F1} cleared/s, " +
                          $"{PlanarReflections.Dropped} dropped, {PlanarReflections.Read / dt:F1} passes read one; " +
                          $"{PlanarReflections.WaterTris / dt:F0} water tris/s binned ({PlanarReflections.WaterRested / dt:F0} at rest), {PlanarReflections.WaterTilted / dt:F0} not level; " +
                          $"clip ({_clip[0]:F2},{_clip[1]:F2},{_clip[2]:F2},{_clip[3]:F0}) view ({_view[0]:F2},{_view[1]:F2},{_view[2]:F2},{_view[3]:F0}); " +
                          $"last readback {PlanarReflections.PlanarPct:F1}% of reflective pixels planar, " +
                          $"{ScreenReflections.HitPct:F1}% marched to a surface, {ScreenReflections.SkyPct:F1}% the sky, " +
                          $"of {ScreenReflections.ReflectivePct:F1}% of the picture reflective; " +
                          $"{PlanarReflections.ComparedPct:F1}% of planar pixels marched to a surface too, " +
                          $"brightness {PlanarReflections.MirrorDiff:F1} apart (unmirrored {PlanarReflections.ControlDiff:F1})");
        if (PlanarReflections.FogCensus is { } fc) Console.WriteLine("[KF2] planar: fog by level depth, texels drawn and lit above 8/255:" + fc);
        PlanarReflections.FogCensus = null;
        PlanarReflections.WantFogCensus = true;
        if (ScreenReflections.Map is { } map && !Reflections.Probing) Console.Write(map);
        ScreenReflections.WantMap = true;

        _walks = _replayed = _props = _instanced = _viewSpace = _noWater = _below = _mismatch = _overflows = _peak = _moves = 0;
        _ms = 0;
        PlanarReflections.ResetCounters();
        PlanarReflections.WaterTris = PlanarReflections.WaterTilted = PlanarReflections.WaterRested = 0;
    }
}
