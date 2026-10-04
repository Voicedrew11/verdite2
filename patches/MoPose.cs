using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// <c>func_80034DA8(slot, bank, clip, time)</c>, the MO blender, in C#, and the pose it
/// leaves for the GPU. The blender keeps a keyframe per (clip, segment) in a buffer of
/// the slot's record, rebuilt only when either moves, and on every call copies it to
/// the posed buffer at <c>0x80190AD8</c> and decodes the segment's deltas into it at
/// the clock's weight (<c>func_80034A74</c>): each listed vertex becomes
/// <c>key + (short)(((short)(target - key) * weight) &gt;&gt; 12)</c>.
///
///     KF2_MOPOSE=0        the recompiled routine
///     KF2_MOPOSE=verify   run both on every call and compare RAM, registers and the GTE
///
/// While <see cref="Defer"/> is set (the submitter, for a lit model the GPU world
/// renderer may draw from its mesh) the copy and the decode are left undone and the
/// pose is kept (<see cref="Pending"/>): <see cref="Store"/> puts its keyframe and
/// deltas in the pose store once per segment, and the vertex shader blends them by the
/// instance's weight. Anything that needs the posed vertices in RAM calls
/// <see cref="Materialize"/> first; only the renderer's routines read that buffer. See
/// "Step 3, the third slice" in docs/GPU_RENDERER.md.
/// </summary>
public static class MoPose
{
    const uint Routine = 0x80034DA8;
    const uint BankTable = 0x8018E1A0;
    const uint VertexBase = 0x8018EAA0;
    const uint Posed = 0x80190AD8;

    enum Mode { Off, On, Verify }
    static Mode _mode = Mode.On;
    static bool _queued;
    static readonly Differential _check = new("mopose", "func_80034DA8", 0x800);

    static readonly ModInfo _self = new()
    {
        Id = "kf2.mopose",
        Name = "MO blender",
        Version = "1.0",
        Description = "func_80034DA8, the MO pose blender, in C#.",
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

    public static void Install() => HookAttach.OnOverlayLoad("mo pose", Attach);

    static bool Attach()
    {
        var target = SymbolRegistry.Resolve("game", null, Routine);
        if (target == null) return false;
        if (!_queued)
        {
            var impl = typeof(MoPose).GetMethod(nameof(Replace), BindingFlags.NonPublic | BindingFlags.Static)!;
            _queued = HookManager.AddReplace(_self, target, impl);
            if (!_queued) return false;
        }
        HookManager.Commit();
        bool ok = HookAttach.Installed(target);
        Console.WriteLine(ok ? $"[KF2] mo pose: {_mode.ToString().ToLowerInvariant()}" : "[KF2] mo pose: not installed");
        return ok;
    }

    /// <summary>Whether the C# blender runs, so a deferral can be asked of it.</summary>
    public static bool Active => _mode == Mode.On && !RecompOne.Runtime.Pgxp.Pgxp.CpuTracking;

    /// <summary>Set by the submitter around its call: leave the pose undecoded.</summary>
    public static bool Defer;

    /// <summary>The last deferred call's pose, not yet in RAM.</summary>
    public static bool Pending { get; private set; }
    static uint _key, _stream, _weight, _count;

    /// <summary>Calls, calls deferred, and deferred poses decoded into RAM after all; never reset.</summary>
    public static long Calls, Deferred, Materialized;

    /// <summary>Calls that let a record go for a rigid bank, set a record up, and rebuilt
    /// its keyframe; never reset.</summary>
    public static long Rigid, Inits, Rebuilds;
    static double _pathsAt;

    static void Replace(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        bool defer = Defer;
        Defer = false;
        Pending = false;
        // PGXP's RAM shadow is kept by the recompiled stores, which C# stores skip.
        if (_mode == Mode.Off || RecompOne.Runtime.Pgxp.Pgxp.CpuTracking || m is not PSMemory mem)
        {
            orig(c, m);
            return;
        }
        if (_mode == Mode.Verify)
        {
            _check.Run(orig, c, mem, (cc, mm) => Run(cc, mm, false));
            double now = Environment.TickCount64 / 1000.0;
            if (now >= _pathsAt)
            {
                _pathsAt = now + 2.0;
                Console.WriteLine($"[mopose] paths in all: {Calls} call(s), {Rigid} rigid, {Inits} record(s) set up, {Rebuilds} keyframe rebuild(s)");
            }
        }
        else Run(c, mem, defer);
    }

    static void Run(CpuContext c, PSMemory mem, bool defer)
    {
        Calls++;
        uint slot = c.A0, bank = c.A1, clip = c.A2, time = c.A3;
        uint entry = c.SP, sp = entry - 0x48u;
        uint ra = c.RA;
        uint count = mem.ReadU32(sp + 0x58u);
        c.SP = sp;

        uint s3 = mem.ReadU32(BankTable + (bank << 2));
        uint rec = mem.ReadU32(slot);
        uint v0;
        if (mem.ReadU32(s3 + 4u) == 0u)
        {
            // Not an MO bank: the record is let go and the model's own vertices drawn.
            if (rec != 0u) Call(c, mem, KingsField2.func_800353E8, rec, 0x80034E1Cu);
            Call(c, mem, KingsField2.func_80034834, bank, 0x80034E24u);
            Call(c, mem, KingsField2.func_8002E1F0, 0u, 0x80034E2Cu);
            v0 = 1u;
            Rigid++;
            goto done;
        }

        bool init;
        if (rec == 0u)
        {
            c.RA = 0x80034E44u;
            KingsField2.func_80035508(c, mem);
            rec = c.V0;
            if (rec == 0u) { v0 = 0u; goto done; }
            init = true;
        }
        else if (mem.ReadU16(rec + 2u) == bank) init = false;
        else
        {
            Call(c, mem, KingsField2.func_800353E8, rec, 0x80034E98u);
            mem.WriteU16(rec + 4u, 0xFF);
            init = true;
        }
        if (init)
        {
            Inits++;
            mem.WriteU16(rec + 2u, (ushort)bank);
            mem.WriteU32(rec + 0x10u, slot);
            while (true)
            {
                Interrupts.Poll(c, mem);
                Call(c, mem, KingsField2.func_80017798, count << 3, 0x80034E60u);
                mem.WriteU32(rec + 0xCu, c.V0);
                if (c.V0 != 0u) break;
                c.RA = 0x80034E70u;
                KingsField2.func_80035430(c, mem);
            }
            mem.WriteU32(slot, rec);
        }

        // The clock writes the segment's index at SP+0x18 and its weight through SP+0x10.
        mem.WriteU32(sp + 0x10u, sp + 0x1Cu);
        c.A0 = s3;
        c.A1 = clip;
        c.A2 = time;
        c.A3 = sp + 0x18u;
        c.RA = 0x80034EC0u;
        KingsField2.func_8003486C(c, mem);
        uint seg = c.V0;

        if (mem.ReadU16(rec + 4u) != clip || mem.ReadU16(rec + 6u) != mem.ReadU32(sp + 0x18u))
        {
            // The keyframe for this segment, from model 0's own vertices.
            Rebuilds++;
            Call(c, mem, KingsField2.func_8002E1F0, 0u, 0x80034EECu);
            uint table = s3 + mem.ReadU32(s3 + 0xCu);
            uint keys = mem.ReadU16(seg + 6u);
            if (keys == 0u) Copy(c, mem, mem.ReadU32(VertexBase), mem.ReadU32(rec + 0xCu), count);
            else
            {
                uint list = seg + 0xAu;
                keys -= 1u;
                c.A0 = mem.ReadU32(rec + 0xCu);
                c.A1 = mem.ReadU32(VertexBase);
                c.A2 = s3 + mem.ReadU32(table + ((uint)mem.ReadU16(seg + 8u) << 2));
                c.RA = 0x80034F2Cu;
                KingsField2.func_80034934(c, mem);
                while ((keys & 0xFFFFu) != 0u)
                {
                    Interrupts.Poll(c, mem);
                    uint k = mem.ReadU16(list);
                    list += 2u;
                    keys += 0xFFFFu;
                    c.A1 = s3 + mem.ReadU32(table + (k << 2));
                    c.A0 = mem.ReadU32(rec + 0xCu);
                    c.RA = 0x80034F5Cu;
                    KingsField2.func_800349F8(c, mem);
                }
            }
            mem.WriteU32(rec + 8u, mem.ReadU32(table + ((uint)mem.ReadU16(seg + 4u) << 2)));
        }

        mem.WriteU16(rec + 4u, (ushort)clip);
        mem.WriteU16(rec + 6u, mem.ReadU16(sp + 0x18u));
        _key = mem.ReadU32(rec + 0xCu);
        _stream = s3 + mem.ReadU32(rec + 8u);
        _weight = mem.ReadU32(sp + 0x1Cu);
        _count = count;
        if (defer)
        {
            Pending = true;
            Deferred++;
        }
        else
        {
            Copy(c, mem, _key, Posed, count);
            Decode(c, mem, Posed, _stream, _weight);
        }
        mem.WriteU32(VertexBase, Posed);
        mem.WriteU16(rec, 2);
        v0 = rec;

        done:
        c.V0 = v0;
        c.SP = entry;
        c.RA = ra;
    }

    static void Call(CpuContext c, PSMemory mem, Action<CpuContext, IMemory> f, uint a0, uint ra)
    {
        c.A0 = a0;
        c.RA = ra;
        f(c, mem);
    }

    /// <summary>The deferred pose into RAM, as the blender would have left it.</summary>
    public static void Materialize(CpuContext? c, PSMemory mem)
    {
        if (!Pending) return;
        Pending = false;
        Materialized++;
        Copy(c, mem, _key, Posed, _count);
        Decode(c, mem, Posed, _stream, _weight);
    }

    /// <summary>The blender's copy: a vertex count taken in sixteen bits, 0 being 65536.</summary>
    static void Copy(CpuContext? c, PSMemory mem, uint from, uint to, uint count)
    {
        if (c != null) Interrupts.Poll(c, mem);
        uint n = (count & 0xFFFFu) == 0u ? 0x10000u : count & 0xFFFFu;
        for (uint i = 0; i < n; i++, from += 8u, to += 8u)
        {
            mem.WriteU32(to, mem.ReadU32(from));
            mem.WriteU32(to + 4u, mem.ReadU32(from + 4u));
        }
    }

    /// <summary>
    /// <c>func_80034A74</c>: a count, then per entry a vertex's target (x, y, z) or 0x8000
    /// and a skip. The routine gathers three entries' deltas into a matrix and scales it
    /// (<c>ScaleMatrix</c>, each element times the weight, shifted 12, in 16 bits) before
    /// adding; the three are consecutive vertices, each read only for its own delta, so
    /// adding each at once is the same arithmetic.
    /// </summary>
    static void Decode(CpuContext? c, PSMemory mem, uint dst, uint stream, uint weight)
    {
        int n = (short)mem.ReadU16(stream);
        if (n < 0 && c != null)
        {
            // Never seen; the routine's own loop would run the count round 32 bits.
            c.A0 = dst; c.A1 = stream; c.A2 = weight; c.RA = 0x80035030u;
            KingsField2.func_80034A74(c, mem);
            return;
        }
        if (c != null) Interrupts.Poll(c, mem);
        stream += 2u;
        uint at = dst;
        for (; n > 0; n--)
        {
            ushort x = mem.ReadU16(stream);
            if (x == 0x8000)
            {
                at += (uint)((short)mem.ReadU16(stream + 2u) << 3);
                stream += 4u;
                continue;
            }
            ushort y = mem.ReadU16(stream + 2u), z = mem.ReadU16(stream + 4u);
            stream += 6u;
            ushort px = mem.ReadU16(at), py = mem.ReadU16(at + 2u), pz = mem.ReadU16(at + 4u);
            mem.WriteU16(at, (ushort)(px + Scale((short)(x - px), weight)));
            mem.WriteU16(at + 2u, (ushort)(py + Scale((short)(y - py), weight)));
            mem.WriteU16(at + 4u, (ushort)(pz + Scale((short)(z - pz), weight)));
            at += 8u;
        }
    }

    /// <summary>One ScaleMatrix element: the low word of the product, shifted 12.</summary>
    static ushort Scale(short d, uint w) => (ushort)(unchecked((int)((uint)d * w)) >> 12);

    // ---- the pose store --------------------------------------------------------------

    sealed class Entry
    {
        public int Texel;
        public uint StreamBytes;
        public ulong StreamHash;
    }

    static readonly Dictionary<(ulong Key, uint Stream, uint Count), Entry> _poses = new();
    static readonly Dictionary<(ulong Hash, uint At, uint Count), int> _rigid = new();
    static int _gen = -1;
    static short[] _texels = new short[4096];
    static bool[] _touched = new bool[1024];

    /// <summary>Poses and rigid models put in the store, found there, and refused
    /// (a stream the shader's blend cannot express); never reset.</summary>
    public static long PoseBuilds, PoseHits, PoseRefused, RigidBuilds, RigidHits;

    /// <summary>
    /// The pending pose in the pose store: its keyframe and each vertex's delta to the
    /// segment's target, two texels a vertex, found by a hash of the keyframe, the
    /// stream's address and the count, and checked every time by a hash of the stream.
    /// One past the first texel, or 0 when the stream visits a vertex twice or leaves
    /// the model, which the shader's per-vertex blend cannot express.
    /// </summary>
    public static int Store(PSMemory mem, uint vertices, out int weight)
    {
        weight = (int)_weight;
        uint n = _count & 0xFFFFu;
        if (!Pending || n == 0u || n != vertices || n > 8192u || !InRam(_key, n * 8u) || !InRam(_stream, 2u)) return 0;
        Forget();
        var ram = mem.Ram;
        ulong key = RetainedModels.Hash(ram, _key, n * 8u);
        if (_poses.TryGetValue((key, _stream, n), out var e) && InRam(_stream, e.StreamBytes)
            && RetainedModels.Hash(ram, _stream, e.StreamBytes) == e.StreamHash)
        {
            PoseHits++;
            return e.Texel + 1;
        }
        e = Build(mem, n);
        if (e == null) { PoseRefused++; return 0; }
        _poses[(key, _stream, n)] = e;
        PoseBuilds++;
        return e.Texel + 1;
    }

    static Entry? Build(PSMemory mem, uint n)
    {
        int texels = (int)n * 2;
        if (_texels.Length < texels * 4) _texels = new short[texels * 4 * 2];
        if (_touched.Length < n) _touched = new bool[n * 2];
        Array.Clear(_touched, 0, (int)n);
        var t = _texels.AsSpan(0, texels * 4);
        for (uint i = 0; i < n; i++)
        {
            uint a = _key + i * 8u;
            int o = (int)i * 8;
            t[o] = (short)mem.ReadU16(a); t[o + 1] = (short)mem.ReadU16(a + 2u);
            t[o + 2] = (short)mem.ReadU16(a + 4u); t[o + 3] = (short)mem.ReadU16(a + 6u);
            t[o + 4] = t[o + 5] = t[o + 6] = t[o + 7] = 0;
        }
        uint s = _stream;
        int count = (short)mem.ReadU16(s);
        if (count < 0) return null;
        s += 2u;
        long at = 0;
        for (; count > 0; count--)
        {
            if (!InRam(s, 6u)) return null;
            ushort x = mem.ReadU16(s);
            if (x == 0x8000)
            {
                at += (short)mem.ReadU16(s + 2u);
                s += 4u;
                continue;
            }
            if (at < 0 || at >= n || _touched[at]) return null;
            _touched[at] = true;
            int o = (int)at * 8;
            t[o + 4] = (short)(x - (ushort)t[o]);
            t[o + 5] = (short)(mem.ReadU16(s + 2u) - (ushort)t[o + 1]);
            t[o + 6] = (short)(mem.ReadU16(s + 4u) - (ushort)t[o + 2]);
            s += 6u;
            at++;
        }
        uint bytes = s - _stream;
        return new Entry
        {
            Texel = RetainedScene.AddPose(t),
            StreamBytes = bytes,
            StreamHash = RetainedModels.Hash(mem.Ram, _stream, bytes),
        };
    }

    /// <summary>A rigid model's vertices in the store, found by a hash of them, their
    /// address and count; one past the first texel, or 0.</summary>
    public static int StoreRigid(PSMemory mem, uint verts, uint n)
    {
        if (n == 0u || n > 8192u || !InRam(verts, n * 8u)) return 0;
        Forget();
        ulong h = RetainedModels.Hash(mem.Ram, verts, n * 8u);
        if (_rigid.TryGetValue((h, verts, n), out int at)) { RigidHits++; return at + 1; }
        var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(
            mem.Ram.Slice((int)(verts & (Runtime.RamSize - 1)), (int)n * 8));
        at = RetainedScene.AddPose(src);
        _rigid[(h, verts, n)] = at;
        RigidBuilds++;
        return at + 1;
    }

    /// <summary>The store was emptied with the meshes.</summary>
    static void Forget()
    {
        if (_gen == RetainedScene.MeshGeneration) return;
        _gen = RetainedScene.MeshGeneration;
        _poses.Clear();
        _rigid.Clear();
    }

    // ---- the check -----------------------------------------------------------------

    /// <summary><c>KF2_GPUWORLD_POSECHECK=1</c>: every pose drawn from the store is decoded
    /// into RAM as well, and each vertex compared with the shader's blend of its texels.</summary>
    public static bool Checking;
    public static long CheckVertices, CheckDiffer;

    /// <summary>The shader's blend of the store at <paramref name="pose"/> against the
    /// posed vertices now in RAM at <paramref name="verts"/>.</summary>
    public static void Check(PSMemory mem, int pose, bool morph, int weight, uint verts, uint n)
    {
        var st = RetainedScene.PoseStore;
        for (uint i = 0; i < n; i++)
        {
            int o = (pose - 1 + (int)(morph ? 2 * i : i)) * 4;
            CheckVertices++;
            for (int k = 0; k < 3; k++)
            {
                int p = st[o + k];
                if (morph) p = (short)(p + (short)((st[o + 4 + k] * weight) >> 12));
                if ((short)p != (short)mem.ReadU16(verts + i * 8u + (uint)k * 2u)) { CheckDiffer++; break; }
            }
        }
    }

    static bool InRam(uint a, uint bytes)
    {
        uint lo = a - 0x80000000u;
        return a >= 0x80000000u && lo + bytes <= Runtime.RamSize && (a & 1u) == 0;
    }
}
