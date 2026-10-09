using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// The camera stage 13 draws with, as a value: <c>func_8002E22C</c>'s two arguments,
/// a VECTOR position in world units (Y down) and an SVECTOR of angles, 0x1000 a turn.
/// </summary>
public readonly record struct Camera(int X, int Y, int Z, short Pitch, short Yaw, short Roll)
{
    /// <summary>The camera the block holds, which is the one the last frame was drawn with.</summary>
    public static Camera Read(IMemory m) => new(
        (int)m.ReadU32(CameraBlock.Position),
        (int)m.ReadU32(CameraBlock.Position + 4u),
        (int)m.ReadU32(CameraBlock.Position + 8u),
        (short)m.ReadU16(CameraBlock.Angles),
        (short)m.ReadU16(CameraBlock.Angles + 2u),
        (short)m.ReadU16(CameraBlock.Angles + 4u));
}

/// <summary>
/// <c>func_8002E22C(VECTOR *pos, SVECTOR *rot)</c> in C#: the camera block stage 13
/// opens with, and the one place the frame's view comes from.
///
///     KF2_CAMERABLOCK=0        the recompiled routine
///     KF2_CAMERABLOCK=verify   run both on every call and compare RAM, registers and the GTE
///
/// A non-null <c>pos</c> is copied to <see cref="Position"/> and the eye's tile derived
/// from it; a non-null <c>rot</c> is copied to <see cref="Angles"/>; then the view
/// matrix is built from the stored angles as <c>(pitch, -yaw, roll)</c> and the
/// pitch-only matrix beside it. Both null rebuilds the stored view, which is how the
/// port's own passes have called it.
///
/// <see cref="Build"/> is the same thing taking a <see cref="Camera"/> instead of two
/// pointers into guest RAM, so a pass that wants a view of its own no longer stages
/// one there first. The cull grid (<c>func_8002D3A8</c>) reads its eye from this
/// block, so it follows. See "Stage 13 in C#" in docs/PATCHES_AND_MODS.md.
/// </summary>
public static class CameraBlock
{
    const uint Routine = 0x8002E22C;

    /// <summary>The view matrix: rotation, then the translation at +0x14.</summary>
    public const uint ViewMatrix = 0x80192E18;

    /// <summary>The same camera's pitch alone.</summary>
    public const uint PitchMatrix = 0x80192E38;

    /// <summary>VECTOR: X, Y, Z, and a pad word copied with them.</summary>
    public const uint Position = 0x80192E78;

    /// <summary>SVECTOR: pitch, yaw, roll, and a pad halfword copied with them.</summary>
    public const uint Angles = 0x80192E88;

    /// <summary>The eye's tile, <c>X &gt;&gt; 11</c> and <c>Z &gt;&gt; 11</c>, the cull grid's centre.</summary>
    public const uint TileX = 0x80192E90, TileZ = 0x80192E94;

    /// <summary>The whole block, the view matrix through the tile, for saving and
    /// putting back around a pass of the port's own (<see cref="ScenePass"/>).</summary>
    public const uint Start = ViewMatrix, Bytes = TileZ + 4u - Start;

    enum Mode { Off, On, Verify }
    static Mode _mode = Mode.On;
    static bool _queued;

    static readonly Differential _check = new("camerablock", "func_8002E22C", 0x400);

    static readonly ModInfo _self = new()
    {
        Id = "kf2.camerablock",
        Name = "Camera block",
        Version = "1.0",
        Description = "func_8002E22C, the view stage 13 draws with, in C#.",
    };

    public static void Configure(string? mode, string? fraction = null, string? fractionProbe = null)
    {
        _mode = mode?.Trim().ToLowerInvariant() switch
        {
            "0" or "off" => Mode.Off,
            "verify" => Mode.Verify,
            _ => Mode.On,
        };
        string f = fraction?.Trim().ToLowerInvariant() ?? "";
        if (f is "1" or "on") FractionOn = true;
        else if (f is "0" or "off") FractionOn = false;
        _fractionProbe = fractionProbe?.Trim() is "1" or "on";
    }

    public static void Install() => HookAttach.OnOverlayLoad("camera block", Attach);

    static bool Attach()
    {
        var target = SymbolRegistry.Resolve("game", null, Routine);
        if (target == null) return false;
        if (!_queued)
        {
            var impl = typeof(CameraBlock).GetMethod(nameof(Replace),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            _queued = HookManager.AddReplace(_self, target, impl);
            if (!_queued) return false;
        }
        HookManager.Commit();
        bool ok = HookAttach.Installed(target);
        Console.WriteLine(ok ? $"[KF2] camera block: {_mode.ToString().ToLowerInvariant()}"
                             : "[KF2] camera block: not installed");
        return ok;
    }

    static void Replace(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        // PGXP's RAM shadow is kept by the recompiled stores, which C# stores skip.
        if (_mode == Mode.Off || RecompOne.Runtime.Pgxp.Pgxp.CpuTracking || m is not PSMemory mem)
        {
            orig(c, m);
            return;
        }
        if (_mode == Mode.Verify) _check.Run(orig, c, mem, Run);
        else Run(c, mem);
    }

    /// <summary>
    /// Write a camera into the block as the routine's two copies would, leaving both
    /// pad fields as they are. The view is not rebuilt; <see cref="Build"/> does that.
    /// </summary>
    public static void Store(IMemory m, in Camera cam)
    {
        m.WriteU32(Position, (uint)cam.X);
        m.WriteU32(Position + 4u, (uint)cam.Y);
        m.WriteU32(Position + 8u, (uint)cam.Z);
        m.WriteU32(TileX, (uint)(cam.X >> 11));
        m.WriteU32(TileZ, (uint)(cam.Z >> 11));
        m.WriteU16(Angles, (ushort)cam.Pitch);
        m.WriteU16(Angles + 2u, (ushort)cam.Yaw);
        m.WriteU16(Angles + 4u, (ushort)cam.Roll);
    }

    /// <summary>
    /// Make <paramref name="cam"/> the view: store it and rebuild the matrices through
    /// <c>func_8002E22C</c>, so every hook on the routine sees the call. Clobbers what
    /// a call to the routine does.
    /// </summary>
    public static void Build(CpuContext c, IMemory m, in Camera cam)
    {
        Store(m, cam);
        c.A0 = 0u;
        c.A1 = 0u;
        KingsField2.func_8002E22C(c, m);
    }

    /// <summary><see cref="Build"/>, with the view's angles a fraction of a unit past
    /// <paramref name="cam"/>'s whole ones (<see cref="FractionOf"/>, or a mirror of it).</summary>
    public static void Build(CpuContext c, IMemory m, in Camera cam, (float Pitch, float Yaw) fraction)
    {
        _explicit = fraction;
        try { Build(c, m, cam); }
        finally { _explicit = null; }
    }

    // ---- the view's fraction -------------------------------------------------------
    //
    // See "The view turns by a fraction of a unit" in docs/INPUT.md. The angles are
    // whole 12-bit units, 0.088 deg, and the frame's interpolated and mouse-led view
    // is rounded to one: a steady pan of 1.5 units a frame was drawn 1, 2, 1, 2. The
    // matrices are 4.12, fine enough for about a sixth of a unit, so they are built at
    // the whole angles either side and blended by the fraction FrameSmoothing rounded
    // off. At a whole angle the blend is the routine's own matrix, bit for bit.

    /// <summary>Whether the view's matrices carry the fraction (<c>KF2_VIEW_FRACTION=0</c>
    /// to compare, the <c>viewfrac</c> verb).</summary>
    public static bool FractionOn { get; set; } = true;

    static bool _fractionProbe;

    // The frame's fraction, and the whole angles it is past: published by
    // FrameSmoothing at stage 8 with the angles it handed on, cleared there on every
    // frame it hands on none. Stage 13 builds the view from exactly those angles, so a
    // build from any others (an override, the editor's camera) gets no fraction.
    static bool _keyed;
    static ushort _keyPitch, _keyYaw;
    static float _fracPitch, _fracYaw;
    static (float Pitch, float Yaw)? _explicit;

    /// <summary>Publish the frame's view as <paramref name="pitch"/> and
    /// <paramref name="yaw"/>, the whole angles handed to the renderer, plus a
    /// fraction of a unit each, in [-0.5, 0.5].</summary>
    public static void SetFraction(ushort pitch, ushort yaw, double fracPitch, double fracYaw)
    {
        _keyed = fracPitch != 0.0 || fracYaw != 0.0;
        _keyPitch = pitch;
        _keyYaw = yaw;
        _fracPitch = (float)Math.Clamp(fracPitch, -0.5, 0.5);
        _fracYaw = (float)Math.Clamp(fracYaw, -0.5, 0.5);
    }

    public static void ClearFraction() => _keyed = false;

    /// <summary>The fraction the frame's view has past <paramref name="cam"/>'s angles:
    /// zero unless they are the angles it was published with.</summary>
    public static (float Pitch, float Yaw) FractionOf(in Camera cam) =>
        FractionOf((ushort)cam.Pitch, (ushort)cam.Yaw);

    static (float Pitch, float Yaw) FractionOf(ushort pitch, ushort yaw) =>
        _keyed && ((pitch ^ _keyPitch) & 0xFFF) == 0 && ((yaw ^ _keyYaw) & 0xFFF) == 0
            ? (_fracPitch, _fracYaw)
            : (0f, 0f);

    public static string Shell(string arg)
    {
        var w = arg.Trim().ToLowerInvariant();
        if (w == "on") FractionOn = true;
        else if (w == "off") FractionOn = false;
        else if (w == "toggle") FractionOn = !FractionOn;
        else if (w != "") return "{\"ok\":false,\"error\":\"viewfrac [on|off|toggle]\"}";
        Console.WriteLine($"[KF2] view fraction: {(FractionOn ? "on" : "off")}");
        return $"{{\"ok\":true,\"on\":{(FractionOn ? "true" : "false")}}}";
    }

    /// <summary>The routine, transcribed: its frame, its two copies in the order it
    /// makes them (every word read before any is written), and its two calls.</summary>
    static void Run(CpuContext c, PSMemory mem)
    {
        uint sp = c.SP - 0x20u;
        c.SP = sp;
        mem.WriteU32(sp + 0x1Cu, c.RA);
        mem.WriteU32(sp + 0x18u, c.S0);

        uint pos = c.A0, rot = c.A1;
        if (pos != 0u)
        {
            uint x = mem.ReadU32(pos), y = mem.ReadU32(pos + 4u), z = mem.ReadU32(pos + 8u), pad = mem.ReadU32(pos + 12u);
            mem.WriteU32(Position, x);
            mem.WriteU32(Position + 4u, y);
            mem.WriteU32(Position + 8u, z);
            mem.WriteU32(Position + 12u, pad);
            mem.WriteU32(TileX, (uint)((int)mem.ReadU32(Position) >> 11));
            mem.WriteU32(TileZ, (uint)((int)mem.ReadU32(Position + 8u) >> 11));
        }

        if (rot != 0u)
        {
            // lwl/lwr pairs: the SVECTOR need not be word-aligned.
            Span<byte> angles = stackalloc byte[8];
            for (int i = 0; i < 8; i++) angles[i] = mem.ReadU8(rot + (uint)i);
            for (int i = 0; i < 8; i++) mem.WriteU8(Angles + (uint)i, angles[i]);
        }

        uint turn = sp + 0x10u;
        mem.WriteU16(turn, mem.ReadU16(Angles));
        mem.WriteU16(turn + 2u, (ushort)(0u - mem.ReadU16(Angles + 2u)));
        mem.WriteU16(turn + 4u, mem.ReadU16(Angles + 4u));

        // The neighbours first, so the routine's own build is the last thing to touch
        // the GTE and leaves it as it always has.
        var (fp, fy) = _mode == Mode.On && FractionOn
            ? _explicit ?? FractionOf(mem.ReadU16(Angles), mem.ReadU16(Angles + 2u))
            : (0f, 0f);
        bool blend = fp != 0f || fy != 0f;
        if (blend) Neighbours(c, mem, sp, turn, fp, fy);

        c.A0 = turn;
        c.A1 = ViewMatrix;
        c.RA = 0x8002E2F8u;
        KingsField2.func_80015048(c, mem);
        c.A0 = (uint)(short)mem.ReadU16(turn);
        c.A1 = PitchMatrix;
        c.RA = 0x8002E304u;
        KingsField2.func_80014E90(c, mem);

        if (blend) Blend(mem, MathF.Abs(fp), MathF.Abs(fy));
        if (_fractionProbe) _lastBlend = blend;

        c.RA = mem.ReadU32(sp + 0x1Cu);
        c.S0 = mem.ReadU32(sp + 0x18u);
        c.SP = sp + 0x20u;
    }

    // Scratch below the routine's frame: an SVECTOR and the four neighbours. The
    // calls are made with SP under it.
    const uint ScratchBytes = 0xB0u;
    const uint SvecAt = 0x00u, PitchAt = 0x10u, YawAt = 0x30u, BothAt = 0x50u, FlatAt = 0x70u;
    static readonly short[] _n = new short[4 * 9];

    /// <summary>The view matrix at the whole angle past the fraction in pitch, in yaw
    /// and in both, and the pitch-only matrix at the pitch past it, built by the
    /// routine's own two calls into scratch below its frame.</summary>
    static void Neighbours(CpuContext c, PSMemory mem, uint sp, uint turn, float fp, float fy)
    {
        uint scratch = sp - ScratchBytes;
        uint ra = c.RA;
        c.SP = scratch;
        ushort p = mem.ReadU16(turn), y = mem.ReadU16(turn + 2u), r = mem.ReadU16(turn + 4u);
        ushort p1 = (ushort)(p + (fp > 0f ? 1 : -1));
        // turn holds -yaw: a yaw one unit on is one unit back here.
        ushort y1 = (ushort)(y - (fy > 0f ? 1 : -1));
        if (fp != 0f) View(c, mem, scratch, p1, y, r, PitchAt);
        if (fy != 0f) View(c, mem, scratch, p, y1, r, YawAt);
        if (fp != 0f && fy != 0f) View(c, mem, scratch, p1, y1, r, BothAt);
        if (fp != 0f)
        {
            c.A0 = (uint)(short)p1;
            c.A1 = scratch + FlatAt;
            c.RA = 0x8002E304u;
            KingsField2.func_80014E90(c, mem);
        }
        for (int k = 0; k < 4; k++)
        {
            uint at = scratch + k switch { 0 => PitchAt, 1 => YawAt, 2 => BothAt, _ => FlatAt };
            for (int i = 0; i < 9; i++) _n[k * 9 + i] = (short)mem.ReadU16(at + (uint)i * 2u);
        }
        c.SP = sp;
        c.RA = ra;
    }

    static void View(CpuContext c, PSMemory mem, uint scratch, ushort p, ushort y, ushort r, uint at)
    {
        mem.WriteU16(scratch + SvecAt, p);
        mem.WriteU16(scratch + SvecAt + 2u, y);
        mem.WriteU16(scratch + SvecAt + 4u, r);
        c.A0 = scratch + SvecAt;
        c.A1 = scratch + at;
        c.RA = 0x8002E2F8u;
        KingsField2.func_80015048(c, mem);
    }

    /// <summary>The routine's matrices blended toward the neighbours: bilinear in
    /// pitch and yaw for the view, linear in pitch for the pitch-only one.</summary>
    static void Blend(PSMemory mem, float wp, float wy)
    {
        float w00 = (1f - wp) * (1f - wy), wP = wp * (1f - wy), wY = (1f - wp) * wy, wB = wp * wy;
        for (int i = 0; i < 9; i++)
        {
            uint at = ViewMatrix + (uint)i * 2u;
            float v = (short)mem.ReadU16(at) * w00;
            if (wP != 0f) v += _n[i] * wP;
            if (wY != 0f) v += _n[9 + i] * wY;
            if (wB != 0f) v += _n[18 + i] * wB;
            mem.WriteU16(at, (ushort)(short)MathF.Round(v));
        }
        if (wp == 0f) return;
        for (int i = 0; i < 9; i++)
        {
            uint at = PitchMatrix + (uint)i * 2u;
            float v = (short)mem.ReadU16(at) * (1f - wp) + _n[27 + i] * wp;
            mem.WriteU16(at, (ushort)(short)MathF.Round(v));
        }
    }

    // ---- the probe -----------------------------------------------------------------
    //
    // KF2_VIEW_FRACTION_PROBE=1: the yaw the frame's view matrix was built at, read
    // back out of the matrix, so what is measured is what the GTE and the GPU world
    // were handed. Over the frames of a turn, how far each frame's step is from the
    // steady step of its tick: a whole-unit view at 1.5 units a frame is 0.5 off on
    // every frame, a blended one a few hundredths.

    static bool _lastBlend;
    static double _probeYaw = double.NaN, _probeAt = -1.0, _devSum, _stepSum, _wholeDevSum;
    static long _probeFrames, _probeBlends, _probeTurning;
    static int _probeWhole;
    static readonly double[] _steps = new double[8], _wholeSteps = new double[8];
    static int _stepCount;

    /// <summary>Called by stage 13 once the frame's own view is built.</summary>
    internal static void ProbeView(PSMemory mem)
    {
        if (!_fractionProbe) return;
        // Row 0 is the yaw's alone when the roll is level: Rx leaves it as Ry made it.
        double r00 = (short)mem.ReadU16(ViewMatrix), r02 = (short)mem.ReadU16(ViewMatrix + 4u);
        double yaw = Math.Atan2(r02, r00) * (4096.0 / (2.0 * Math.PI));
        int whole = mem.ReadU16(Angles + 2u) & 0xFFF;
        _probeFrames++;
        if (_lastBlend) _probeBlends++;
        if (!double.IsNaN(_probeYaw))
        {
            double d = yaw - _probeYaw;
            if (d > 2048.0) d -= 4096.0; else if (d < -2048.0) d += 4096.0;
            int dw = (whole - _probeWhole + 0x800 & 0xFFF) - 0x800;
            // Deviation from the mean of the frames around it: a steady pan is flat.
            if (Math.Abs(d) > 0.05 && Math.Abs(d) < 64.0)
            {
                _steps[_stepCount % 8] = d;
                _wholeSteps[_stepCount % 8] = dw;
                _stepCount++;
                if (_stepCount >= 8)
                {
                    double mean = 0, wmean = 0;
                    for (int i = 0; i < 8; i++) { mean += _steps[i]; wmean += _wholeSteps[i]; }
                    mean /= 8; wmean /= 8;
                    _devSum += Math.Abs(d - mean);
                    _wholeDevSum += Math.Abs(dw - wmean);
                    _stepSum += Math.Abs(d);
                    _probeTurning++;
                }
            }
            else _stepCount = 0;
        }
        _probeYaw = yaw;
        _probeWhole = whole;

        double now = Environment.TickCount64 / 1000.0;
        if (_probeAt < 0.0) _probeAt = now;
        if (now - _probeAt < 1.0) return;
        Console.WriteLine(_probeTurning == 0
            ? $"[KF2] view fraction: {(FractionOn ? "on" : "off")}, {_probeFrames} frame(s), {_probeBlends} blended, not turning"
            : $"[KF2] view fraction: {(FractionOn ? "on" : "off")}, {_probeFrames} frame(s), {_probeBlends} blended, " +
              $"{_probeTurning} turning at {_stepSum / _probeTurning:0.00} unit(s) a frame; " +
              $"each step off the steady one by {_devSum / _probeTurning:0.000} drawn, " +
              $"{_wholeDevSum / _probeTurning:0.000} in whole units");
        _probeAt = now;
        _probeFrames = _probeBlends = _probeTurning = 0;
        _devSum = _stepSum = _wholeDevSum = 0;
    }
}
