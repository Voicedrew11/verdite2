using System.Numerics;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>
/// Port-drawn props (Phase 8): the pack's <c>areas/&lt;n&gt;/props.json</c>, each an
/// object model the area already has, placed where the author put it.
///
///     KF2_REMASTER_PROPS=0   leave the pack's props out (they apply with the remaster by default)
///
/// A prop is an object record of the port's own, above 2 MB
/// (<see cref="PrimBuffer.PropScratch"/>), copied from a live object of the same model
/// when the area settles and then given the prop's position, rotation, scale and half.
/// The C# object walk submits each after the game's own objects, through the same code
/// (<see cref="ModelWalk.Ordinary"/>), so a prop is culled by the game's grid, lit from
/// its tile's record and drawn by the game's own assembler, and so gets depth, fog,
/// per-pixel light, occlusion, materials, reflections and shadows as the model does.
/// Nothing but the renderer reads the record: a prop has no collision, no behaviour and
/// never reaches a save. See "Phase 8, the first slice" in docs/REMASTER.md.
/// </summary>
public sealed class Props : IRemasterFeature
{
    public string Id => "props";

    static bool _on = true;

    public static void Configure(string? on)
    {
        if (!string.IsNullOrWhiteSpace(on)) _on = on.Trim() != "0";
    }

    const uint Objects = 0x80177714, ObjectStride = 0x44;
    const int ObjectCount = 396;
    const uint ObjectDefs = 0x80175914;
    const int Defs = 320;

    /// <summary>A submit's model id is the definition index plus this.</summary>
    public const int ModelBase = 0x100;

    public static int Max => (int)(PrimBuffer.PropBytes / ObjectStride);

    /// <summary>The records the walk submits, and what each is.</summary>
    static int _count;
    static readonly string[] _names = new string[Max];

    static int _builtSettles = -1;

    public static string? Refused { get; private set; }
    public static int Authored { get; private set; }
    public static int Resolved => _count;

    /// <summary>Why each authored prop is not drawn; absent is drawn.</summary>
    static readonly Dictionary<string, string> _why = new();

    /// <summary>Props submitted by the last walk, and in all; never reset.</summary>
    public static int Submitted { get; private set; }
    public static long Walks, Submits;

    static (int Version, int Settles, bool Want) _appliedFor = (-1, -1, false);
    static long _retryAt;

    public static string? Status(string name)
    {
        if (!Host.Enabled) return "not drawn: the remaster is off";
        if (!_on) return "not drawn: KF2_REMASTER_PROPS=0";
        if (Refused != null) return "not drawn: " + Refused;
        return _why.TryGetValue(name, out var w) ? "not drawn: " + w : "drawn";
    }

    /// <summary>Why no prop can be drawn at all, or null.</summary>
    static string? Blocked()
        => !PrimBuffer.Relocated ? "the primitive buffers were not moved above 2 MB (KF2_PRIMBUF=1), so there is no room for a prop's record"
         : !ModelWalk.Enabled || !ModelWalk.WalkEnabled ? "the object walk is the recompiled one (KF2_MODELWALK)"
         : ModelWalk.Verifying ? "KF2_MODELWALK=verify compares the walk against the game's, which draws none"
         : null;

    public void OnFrame()
    {
        var m = Runtime.Mem;
        if (m == null) return;
        (int, int, bool) key = (Pack.Version, Identity.Settles, _on && Host.Enabled && Identity.Settled);
        long now = Environment.TickCount64;
        // A model the area loads later (a creature's drop, a door opened) resolves then.
        bool retry = _why.Count > 0 && now >= _retryAt;
        if (key == _appliedFor && !retry) return;
        _appliedFor = key;
        _retryAt = now + 1000;
        Build(m, key.Item3);
    }

    static void Build(IMemory m, bool want)
    {
        _count = 0;
        _why.Clear();
        Refused = null;
        Authored = 0;
        Submitted = 0;
        if (!want) return;
        int area = Identity.Area;
        var props = Pack.Props(area).ToList();
        Authored = props.Count;
        if (props.Count == 0) return;
        if (Pack.PropsFingerprint(area) is { } fp && fp != Identity.FingerprintText)
        {
            Refused = $"area {area} was authored against fingerprint {fp}; this one is {Identity.FingerprintText}";
            return;
        }
        if (Blocked() is { } why) { Refused = why; return; }

        foreach (var p in props)
        {
            if (p.Off) { _why[p.Name] = "switched off"; continue; }
            if (_count == Max) { _why[p.Name] = $"past the {Max} props an area holds"; continue; }
            if (Unusable(m, p.Model) is { } bad) { _why[p.Name] = bad; continue; }
            uint rec = PrimBuffer.PropScratch + (uint)_count * ObjectStride;
            uint template = Template(m, p.Model - ModelBase);
            for (uint b = 0; b < ObjectStride; b += 4) m.WriteU32(rec + b, m.ReadU32(template + b));
            Place(m, rec, p);
            _names[_count] = p.Name;
            _count++;
        }
        _builtSettles = Identity.Settles;
    }

    /// <summary>Why a model cannot be a prop here, or null: it must be an ordinary
    /// object model some live object of the area is drawn with, since the prop's record
    /// is a copy of that object's.</summary>
    public static string? Unusable(IMemory m, int model)
    {
        int def = model - ModelBase;
        if ((uint)def >= Defs) return $"model {model} is not an object model ({ModelBase}-{ModelBase + Defs - 1})";
        return Template(m, def) == 0 ? $"no object in this area is drawn with model {model}" : null;
    }

    /// <summary>A live, ordinary object with this definition, or 0.</summary>
    static uint Template(IMemory m, int def)
    {
        uint rec = Objects;
        for (int i = 0; i < ObjectCount; i++, rec += ObjectStride)
        {
            if (m.ReadU16(rec + 0x6u) != def) continue;
            uint kind = m.ReadU8(rec + 4u);
            if (kind is 0x1Fu or 0xF0u or 0xFFu) continue;
            return rec;
        }
        return 0;
    }

    /// <summary>The prop's own fields over the copy: position, rotation (the walk adds
    /// half a turn to the yaw, as it does an object's), scale relative to the model's
    /// own, and the half it is lit and culled on.</summary>
    static void Place(IMemory m, uint rec, Pack.Prop p)
    {
        m.WriteU32(rec + 0x14u, (uint)(int)MathF.Round(p.Position.X));
        m.WriteU32(rec + 0x18u, (uint)(int)MathF.Round(p.Position.Y));
        m.WriteU32(rec + 0x1Cu, (uint)(int)MathF.Round(p.Position.Z));
        m.WriteU16(rec + 0x24u, Angle(p.Rotation.X));
        m.WriteU16(rec + 0x26u, Angle(p.Rotation.Y));
        m.WriteU16(rec + 0x28u, Angle(p.Rotation.Z));
        for (uint k = 0; k < 3; k++)
        {
            float s = k == 0 ? p.Scale.X : k == 1 ? p.Scale.Y : p.Scale.Z;
            // A model the game grows in from nothing keeps a zero scale until it does.
            int v = (short)m.ReadU16(rec + 0x2Cu + k * 2u);
            if (v == 0) v = 4096;
            m.WriteU16(rec + 0x2Cu + k * 2u, (ushort)(short)Math.Clamp((int)MathF.Round(v * s), -32768, 32767));
        }
        m.WriteU8(rec, (byte)(p.Upper ? 2 : 1));
        m.WriteU8(rec + 3u, (byte)(m.ReadU8(rec + 3u) & 0x7Fu));
    }

    public static ushort Angle(float degrees) => (ushort)((int)MathF.Round(degrees * 4096f / 360f) & 0xFFF);

    /// <summary>From the C# object walk, after the table: each prop through the
    /// ordinary object path. One test while there are none.</summary>
    public static void Walk(CpuContext c, PSMemory mem, uint sp)
    {
        if (_count == 0) return;
        if (!Host.Enabled || !_on || !Identity.Settled || _builtSettles != Identity.Settles || ModelWalk.Verifying)
        {
            Submitted = 0;
            return;
        }
        long before = ModelWalk.SubmitCalls;
        for (int i = 0; i < _count; i++)
        {
            uint rec = PrimBuffer.PropScratch + (uint)i * ObjectStride;
            uint kindByte = mem.ReadU8(rec + 3u);
            mem.WriteU8(rec + 3u, (byte)(kindByte & 0x7Fu));
            ModelWalk.Ordinary(c, mem, sp, rec, ObjectCount + i, kindByte);
        }
        Submitted = (int)(ModelWalk.SubmitCalls - before);
        Submits += Submitted;
        Walks++;
    }

    /// <summary>Is this record one of the props, and which.</summary>
    public static string? NameOf(uint rec)
    {
        uint off = rec - PrimBuffer.PropScratch;
        if (off >= (uint)_count * ObjectStride || off % ObjectStride != 0) return null;
        return _names[off / ObjectStride];
    }

    // ---- the area's own objects, for choosing a model -------------------------

    public readonly record struct ObjectInfo(int Slot, int Model, byte Kind, byte DefKind, byte Half, byte Clip,
                                             byte Assembler, byte Flags, Vector3 Position, Vector3 Rotation, Vector3 Scale);

    /// <summary>The live ordinary objects, as the walk reads them.</summary>
    public static IEnumerable<ObjectInfo> LiveObjects(IMemory m)
    {
        uint rec = Objects;
        for (int i = 0; i < ObjectCount; i++, rec += ObjectStride)
        {
            ushort def = m.ReadU16(rec + 0x6u);
            if (def == 0xFFu || def >= Defs) continue;
            byte kind = m.ReadU8(rec + 4u);
            if (kind is 0x1F or 0xF0 or 0xFF) continue;
            static float Deg(ushort a) => (a & 0xFFF) * 360f / 4096f;
            yield return new ObjectInfo(i, def + ModelBase, kind, m.ReadU8(ObjectDefs + def * 24u), m.ReadU8(rec),
                m.ReadU8(rec + 1u), m.ReadU8(rec + 2u), m.ReadU8(rec + 3u),
                new Vector3((int)m.ReadU32(rec + 0x14u), (int)m.ReadU32(rec + 0x18u), (int)m.ReadU32(rec + 0x1Cu)),
                new Vector3(Deg(m.ReadU16(rec + 0x24u)), Deg(m.ReadU16(rec + 0x26u)), Deg(m.ReadU16(rec + 0x28u))),
                new Vector3((short)m.ReadU16(rec + 0x2Cu), (short)m.ReadU16(rec + 0x2Eu), (short)m.ReadU16(rec + 0x30u)));
        }
    }

    public void Detach()
    {
        _count = 0;
        _why.Clear();
        Refused = null;
        Authored = 0;
        Submitted = 0;
        _appliedFor = (-1, -1, false);
    }

    public string Probe()
    {
        if (!_on) return "props off";
        if (Refused != null) return $"props refused ({Refused})";
        return $"props {_count} of {Authored} resolved, {Submitted} drawn last walk" +
               (_why.Count > 0 ? $", {_why.Count} not ({string.Join("; ", _why.Take(3).Select(p => $"{p.Key}: {p.Value}"))})" : "");
    }
}
