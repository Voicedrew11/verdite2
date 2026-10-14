using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Hold a picked-up item at the centre of the view. Verdite3's <c>ItemEye</c>, which
/// fixed the head bob, and a second correction KF3 does not have: the item's own
/// geometry.
///
///     KF2_ITEMEYE=0          the game's own placement, to compare (on by default)
///     KF2_ITEMEYE_PROBE=1    a line for each target, and the item's centre in the
///                            camera's frame twice a second while it is held
///
/// <b>The eye.</b> The pickup (<c>func_8004831C</c>) aims the item with
/// <c>func_80046C64</c>, which ends by adding the player's position and taking the eye
/// height <c>0x640</c> off y. The pickup then hands stage 13 a rotation with no
/// position, so the camera keeps the eye of the last frame drawn: <c>player y + bob
/// (0x80199548) + landing (0x8019954C) - 0x640</c>, and under smoothing the position
/// carried between ticks. A post-hook on the three calls that place the item (the
/// hold for an item made fresh, return <c>0x8004857C</c>; for one already in the
/// world, <c>0x80048610</c>; the fly-out once taken, <c>0x80048830</c>) moves the out
/// VECTOR by the camera block's eye less the eye the routine assumed.
///
/// <b>The geometry.</b> The game aims the item's <i>origin</i> 500 below the eye line,
/// and an item's mesh stands on its origin: every one measured runs y from its height
/// up to 0 (a dagger 0..-644, a staff 0..-1331), and some sit off it in depth. So the
/// middle of what is drawn sat 10 to 28 game pixels below the centre of the picture, by
/// item, and swung about as it spun. At draw time (ModelWalk.Ordinary, after
/// ItemTurn's turn) <see cref="Place"/> moves the record's position for the submit
/// alone so the middle of the mesh's box sits on the view's axis at the origin's
/// distance, whatever the rotation: the item spins and turns about its middle. It is
/// blended in over the fly-in and out over the fly-out, carried between ticks like
/// the record, and the record is put back as soon as the submit returns.
///
/// See "The item held up at the eye" in docs/INPUT.md.
/// </summary>
public static class ItemEye
{
    const uint TargetRoutine = 0x80046C64;  // func_80046C64: the item's target, out VECTOR at sp+0x1C
    const uint FreshReturn = 0x8004857C;    // a fresh record's hold, into the record itself
    const uint HoldReturn = 0x80048610;     // a world record's hold, the fly-in's end
    const uint FlyOutReturn = 0x80048830;   // the fly-out when the item is taken
    const uint OutSlot = 0x1C;              // the caller's stack slot holding the out pointer
    const uint Pickup = 0x8004831C;
    const uint Renderer = 0x800342D8;
    // Stage 13's return addresses inside the pickup.
    const uint FlyInDraw = 0x8004868C, HoldDraw = 0x8004870C, FaceBackDraw = 0x800488A8, FlyBackDraw = 0x80048984;
    const uint RecordAt = 0x80;             // the pickup's sp+0x80 holds the item's record

    const uint PlayerX = 0x801994EC, PlayerY = 0x801994F0, PlayerZ = 0x801994F4;
    const uint Bob = 0x80199548, Landing = 0x8019954C;
    const int EyeHeight = 0x640;

    public static bool Enabled { get; private set; } = true;
    static bool _probe, _queued;
    static long _probeFrames = -1, _placeProbeFrames = -1;

    // The pickup as stage 13 sees it: the record, and how far the centring is in,
    // at the loop's last two passes (drawn between them as the record is).
    static bool _inPickup;
    static uint _record;
    static double _weightWas, _weight;
    static bool _weighed;

    // The mesh's box, by where its vertices are.
    static uint _meshVerts, _meshCount;
    static double _cx, _cy, _cz;

    // What Place wrote over, for Restore.
    static uint _moved;
    static uint _ox, _oy, _oz;

    static readonly ModInfo _self = new() { Id = "kf2.itemeye", Name = "Item eye", Version = "1.0" };

    public static void Configure(string? spec)
    {
        if (!string.IsNullOrWhiteSpace(spec))
            Enabled = spec.Trim().ToLowerInvariant() is not ("0" or "off" or "false" or "no");
        _probe = Environment.GetEnvironmentVariable("KF2_ITEMEYE_PROBE")?.Trim().ToLowerInvariant() is "1" or "on" or "true";
    }

    public static void Install()
    {
        // Off, the probe still measures the game's own target, to compare.
        if (!Enabled && !_probe) return;
        HookAttach.OnOverlayLoad("item eye", Attach,
            "A picked-up item sits below the centre of the view.");
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("game", null, TargetRoutine);
        var pickup = SymbolRegistry.Resolve("game", null, Pickup);
        var renderer = SymbolRegistry.Resolve("game", null, Renderer);
        if (target == null || pickup == null || renderer == null) return false;
        if (!_queued)
        {
            _queued = HookManager.AddPost(_self, target, Own(nameof(After)))
                      && HookManager.AddPre(_self, pickup, Own(nameof(Enter)))
                      && HookManager.AddPost(_self, pickup, Own(nameof(Leave)))
                      && HookManager.AddPre(_self, renderer, Own(nameof(BeforeStage13)), Stage13.HookOrder.World);
            if (_queued && _probe)
                HookManager.AddPre(_self, renderer, Own(nameof(Probe)));
            if (!_queued) return false;
        }
        HookManager.Commit();
        bool ok = HookAttach.Installed(target) && HookAttach.Installed(pickup) && HookAttach.Installed(renderer);
        if (ok) Console.WriteLine($"[KF2] item eye: {(Enabled ? "on" : "off, probe only")}");
        else Console.Error.WriteLine("[KF2] item eye: a hook did not install");
        return ok;
    }

    static MethodInfo Own(string name) =>
        typeof(ItemEye).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    public static void Enter(CpuContext c, IMemory m)
    {
        _inPickup = true;
        _record = 0;
        _weighed = false;
        _weightWas = _weight = 0.0;
    }

    public static void Leave(CpuContext c, IMemory m)
    {
        _inPickup = false;
        _record = 0;
    }

    /// <summary>How far the centring is in, from where stage 13 was called: the fly-in's
    /// and fly-back's step <c>t</c> is <c>s0 - 0x200</c> at the call (the loop steps it
    /// before drawing). Taken on the loop's own pass, not loop pacing's redraws.</summary>
    public static void BeforeStage13(CpuContext c, IMemory m)
    {
        if (!_inPickup || LoopPacing.InRedraw) return;
        double t = Math.Clamp(((int)c.S0 - 0x200) / 4096.0, 0.0, 1.0);
        double w;
        switch (c.RA)
        {
            case FlyInDraw: w = t; break;
            case HoldDraw:
            case FaceBackDraw: w = 1.0; break;
            case FlyBackDraw: w = 1.0 - t; break;
            default: return;
        }
        _record = m.ReadU32(c.SP + RecordAt);
        _weightWas = _weighed ? _weight : w;
        _weight = w;
        _weighed = true;
    }

    /// <summary>
    /// Move the held item's record so the middle of its mesh is drawn on the view's
    /// axis, for this submit alone; <paramref name="rot"/> is the rotation the walk is
    /// about to hand the submitter (ItemTurn's turn in it), <paramref name="clip"/> the
    /// clip byte (a rigid mesh at <c>0x80</c> and up, its sub-model the low seven bits),
    /// <paramref name="bank"/> the model's bank as <c>func_80032CD8</c> returned it.
    /// True if the record was moved: <see cref="Restore"/> after the submit.
    /// </summary>
    public static bool Place(IMemory m, uint rec, uint rot, uint clip, uint bank)
    {
        if (!Enabled || !_inPickup || rec != _record || rec == 0 || clip < 0x80u) return false;
        double w = _weightWas + (_weight - _weightWas) * FramePacing.LogicPhase;
        if (w <= 0.0 || !Mesh(m, bank, clip & 0x7Fu)) return false;

        // The mesh's middle, scaled and turned as the submitter will (func_80014FE0,
        // then ScaleMatrix on its columns).
        double sx = (short)m.ReadU16(rec + 0x2Cu) / 4096.0, sy = (short)m.ReadU16(rec + 0x2Eu) / 4096.0,
               sz = (short)m.ReadU16(rec + 0x30u) / 4096.0;
        var r = ItemTurn.Rotation((short)m.ReadU16(rot + 2u), (short)m.ReadU16(rot), (short)m.ReadU16(rot + 4u));
        double lx = _cx * sx, ly = _cy * sy, lz = _cz * sz;
        double rcx = r[0, 0] * lx + r[0, 1] * ly + r[0, 2] * lz;
        double rcy = r[1, 0] * lx + r[1, 1] * ly + r[1, 2] * lz;
        double rcz = r[2, 0] * lx + r[2, 1] * ly + r[2, 2] * lz;

        // The origin's distance along the view, and the point on the axis there.
        var cam = Camera.Read(m);
        int ox = (int)m.ReadU32(rec + 0x14u), oy = (int)m.ReadU32(rec + 0x18u), oz = (int)m.ReadU32(rec + 0x1Cu);
        double fx = View(m, 2, 0), fy = View(m, 2, 1), fz = View(m, 2, 2);
        double depth = fx * (ox - cam.X) + fy * (oy - cam.Y) + fz * (oz - cam.Z);
        double px = cam.X + fx * depth - rcx, py = cam.Y + fy * depth - rcy, pz = cam.Z + fz * depth - rcz;

        _moved = rec;
        _ox = (uint)ox; _oy = (uint)oy; _oz = (uint)oz;
        m.WriteU32(rec + 0x14u, (uint)(int)Math.Round(ox + (px - ox) * w));
        m.WriteU32(rec + 0x18u, (uint)(int)Math.Round(oy + (py - oy) * w));
        m.WriteU32(rec + 0x1Cu, (uint)(int)Math.Round(oz + (pz - oz) * w));

        if (_probe && w >= 1.0 && FramePacing.Frames / 32 != _placeProbeFrames)
        {
            _placeProbeFrames = FramePacing.Frames / 32;
            var was = Eye(m, cam, ox + rcx, oy + rcy, oz + rcz);
            var now = Eye(m, cam, px + rcx, py + rcy, pz + rcz);
            Console.WriteLine($"[KF2] item eye: mesh middle from the eye ({was.X:0}, {was.Y:0}, {was.Z:0}) " +
                              $"drawn at ({now.X:0}, {now.Y:0}, {now.Z:0}); origin moved by " +
                              $"({px - ox:0}, {py - oy:0}, {pz - oz:0})");
        }
        return true;
    }

    /// <summary>Put back what <see cref="Place"/> moved.</summary>
    public static void Restore(IMemory m)
    {
        if (_moved == 0) return;
        m.WriteU32(_moved + 0x14u, _ox);
        m.WriteU32(_moved + 0x18u, _oy);
        m.WriteU32(_moved + 0x1Cu, _oz);
        _moved = 0;
    }

    /// <summary>The middle of sub-model <paramref name="sub"/>'s box, in its own space.
    /// The submitter makes the bank's table current (<c>func_80034834</c>: the bank plus
    /// its u32 at <c>+8</c>, into <c>0x8018E19C</c>), and then reads a header of 28 bytes
    /// a sub-model at <c>table + 0xC</c>, its vertex offset at <c>+0</c> and count at
    /// <c>+4</c> (func_8002E1BC, func_8002E1F0), eight bytes a vertex.</summary>
    static bool Mesh(IMemory m, uint bank, uint sub)
    {
        if (bank < 0x80010000u || bank >= 0x80200000u) return false;
        uint table = bank + m.ReadU32(bank + 8u);
        if (table < 0x80010000u || table >= 0x80200000u) return false;
        uint header = table + 0xCu + sub * 28u;
        uint verts = table + 0xCu + m.ReadU32(header);
        uint count = m.ReadU32(header + 4u);
        if (count == 0 || count > 4096 || verts < 0x80010000u || verts + count * 8u > 0x80200000u) return false;
        if (verts == _meshVerts && count == _meshCount) return true;

        Span<int> lo = [int.MaxValue, int.MaxValue, int.MaxValue], hi = [int.MinValue, int.MinValue, int.MinValue];
        for (uint i = 0; i < count; i++)
            for (int k = 0; k < 3; k++)
            {
                int v = (short)m.ReadU16(verts + i * 8u + (uint)k * 2u);
                lo[k] = Math.Min(lo[k], v);
                hi[k] = Math.Max(hi[k], v);
            }
        _meshVerts = verts;
        _meshCount = count;
        _cx = (lo[0] + hi[0]) * 0.5;
        _cy = (lo[1] + hi[1]) * 0.5;
        _cz = (lo[2] + hi[2]) * 0.5;
        if (_probe)
            Console.WriteLine($"[KF2] item eye: mesh {count} vertices, x {lo[0]}..{hi[0]} y {lo[1]}..{hi[1]} z {lo[2]}..{hi[2]}, " +
                              $"middle ({_cx:0}, {_cy:0}, {_cz:0}) from the origin");
        return true;
    }

    /// <summary>The block's view matrix, 1.0 = 4096: row 0 the camera's right, 2 its forward.</summary>
    static double View(IMemory m, int row, int col) =>
        (short)m.ReadU16(CameraBlock.ViewMatrix + (uint)(row * 6 + col * 2)) / 4096.0;

    static (double X, double Y, double Z) Eye(IMemory m, Camera cam, double x, double y, double z)
    {
        x -= cam.X; y -= cam.Y; z -= cam.Z;
        return (View(m, 0, 0) * x + View(m, 0, 1) * y + View(m, 0, 2) * z,
                View(m, 1, 0) * x + View(m, 1, 1) * y + View(m, 1, 2) * z,
                View(m, 2, 0) * x + View(m, 2, 1) * y + View(m, 2, 2) * z);
    }

    public static void After(CpuContext c, IMemory m)
    {
        string? phase = c.RA switch
        {
            FreshReturn => "hold, fresh",
            HoldReturn => "hold",
            FlyOutReturn => "fly-out",
            _ => null,
        };
        if (phase == null) return;

        uint outAt = m.ReadU32(c.SP + OutSlot);
        var cam = Camera.Read(m);
        int dx = cam.X - (int)m.ReadU32(PlayerX);
        int dy = cam.Y - ((int)m.ReadU32(PlayerY) - EyeHeight);
        int dz = cam.Z - (int)m.ReadU32(PlayerZ);

        if (!Enabled) dx = dy = dz = 0;
        int x = (int)m.ReadU32(outAt) + dx;
        int y = (int)m.ReadU32(outAt + 4u) + dy;
        int z = (int)m.ReadU32(outAt + 8u) + dz;
        m.WriteU32(outAt, (uint)x);
        m.WriteU32(outAt + 4u, (uint)y);
        m.WriteU32(outAt + 8u, (uint)z);

        if (_probe)
            Console.WriteLine($"[KF2] item eye: {phase} target moved by ({dx}, {dy}, {dz}) to ({x}, {y}, {z}), " +
                              $"view pitch {m.ReadU16(0x80199504) & 0xFFF}, bob {(short)m.ReadU16(Bob)} " +
                              $"landing {(short)m.ReadU16(Landing)}, bob option {m.ReadU8(0x801994E0)}, ticked {FramePacing.TickedThisFrame}");
    }

    /// <summary>The probe: where the held item's origin sits in the camera's frame, from
    /// the block's view matrix (1.0 = 4096) and eye, about twice a second while it is
    /// held. x is across, y down, z ahead; centred is x = 0.</summary>
    public static void Probe(CpuContext c, IMemory m)
    {
        if (c.RA != HoldDraw || LoopPacing.InRedraw) return;
        long frames = FramePacing.Frames / 32;
        if (frames == _probeFrames) return;
        _probeFrames = frames;
        uint record = m.ReadU32(c.SP + RecordAt);
        if (record == 0) return;
        var cam = Camera.Read(m);
        long wx = (int)m.ReadU32(record + 0x14u) - cam.X;
        long wy = (int)m.ReadU32(record + 0x18u) - cam.Y;
        long wz = (int)m.ReadU32(record + 0x1Cu) - cam.Z;
        var v = new long[3];
        for (int i = 0; i < 3; i++)
        {
            uint row = CameraBlock.ViewMatrix + (uint)(i * 6);
            v[i] = ((short)m.ReadU16(row) * wx + (short)m.ReadU16(row + 2u) * wy + (short)m.ReadU16(row + 4u) * wz) >> 12;
        }
        Console.WriteLine($"[KF2] item eye: origin held at ({v[0]}, {v[1]}, {v[2]}) from the eye, " +
                          $"view pitch {cam.Pitch & 0xFFF}, eye y {cam.Y}, bob {(short)m.ReadU16(Bob)} landing {(short)m.ReadU16(Landing)}");
    }
}
