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

    public static void Configure(string? mode)
    {
        _mode = mode?.Trim().ToLowerInvariant() switch
        {
            "0" or "off" => Mode.Off,
            "verify" => Mode.Verify,
            _ => Mode.On,
        };
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

        c.A0 = turn;
        c.A1 = ViewMatrix;
        c.RA = 0x8002E2F8u;
        KingsField2.func_80015048(c, mem);
        c.A0 = (uint)(short)mem.ReadU16(turn);
        c.A1 = PitchMatrix;
        c.RA = 0x8002E304u;
        KingsField2.func_80014E90(c, mem);

        c.RA = mem.ReadU32(sp + 0x1Cu);
        c.S0 = mem.ReadU32(sp + 0x18u);
        c.SP = sp + 0x20u;
    }
}
