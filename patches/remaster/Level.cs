using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using Kf2.Settings;

namespace Kf2.Remaster;

/// <summary>
/// Level edits (Phase 6): the pack's <c>areas/&lt;n&gt;/level.json</c> written into the
/// area's tile block once it has settled. The game reads these bytes for its floor,
/// its collision and its visibility, so **an edit changes gameplay**, and it applies
/// only behind its own switch, off by default, on top of the remaster's:
///
///     KF2_REMASTER_LEVEL=1   apply level edits (0 never; unset, the saved setting)
///
/// An area's edits apply whole or not at all: never over a fingerprint other than the
/// one they were authored against, nor over a block whose load was not seen. A half's
/// edit applies whole or not at all too: never with a value out of range, a mesh or a
/// collision shape the area's own block does not use, nor over a half the game has
/// rewritten (a door, a lift): the game's rewrites win.
///
/// Every write is held with the byte it replaced, and put back only while the bits it
/// owns still read as written, so a rewrite the game made since is kept. A write is
/// void once the loader copies a new block in. The tile block never reaches a save
/// (<see cref="SaveCheck"/>). See "Phase 6, the first slice" in docs/REMASTER.md.
/// </summary>
public sealed class Level : IRemasterFeature
{
    public string Id => "level";

    public const string OnKey = "kf2.remaster.level";
    public const string Label = "changes gameplay";

    static bool? _forced;

    /// <summary>The switch: level edits apply only with it and the remaster both on.</summary>
    public static bool Enabled { get; private set; }

    public static void Configure(string? on)
    {
        if (!string.IsNullOrWhiteSpace(on)) _forced = on.Trim() != "0";
    }

    public static void Install()
        => Event.AddListener<RuntimeReadyEvent>(_ => Enabled = _forced ?? PatchSettings.Get(OnKey, false));

    public static void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        Enabled = on;
        PatchSettings.Set(OnKey, on);
    }

    // ---- what is held --------------------------------------------------------

    /// <summary>Each byte written: the bits the edit owns and what they were set to.</summary>
    static readonly Dictionary<uint, (byte Mask, byte Value)> _holding = new();
    static readonly Dictionary<uint, byte> _before = new();
    static int _heldLoad = -1;

    /// <summary>What the edits hold over the block, for the rewrite census.</summary>
    public static IReadOnlyDictionary<uint, (byte Mask, byte Value)> Holding => _holding;

    /// <summary>Moves whenever <see cref="Holding"/> does.</summary>
    public static int Generation { get; private set; }

    // ---- status --------------------------------------------------------------

    /// <summary>Why the area's edits are not applied, or null.</summary>
    public static string? Refused { get; private set; }

    /// <summary>Why each authored half of the area is not applied; a half absent here
    /// and present in the document is applied.</summary>
    static readonly Dictionary<TileKey, string> _why = new();

    public static int Authored { get; private set; }
    public static int Applied { get; private set; }

    /// <summary>Held bytes left as the game rewrote them when the edits were put back; never reset.</summary>
    public static long KeptGames { get; private set; }

    /// <summary>Whether a half's edit is applied, and if not why; null when it has none.</summary>
    public static string? Status(TileKey k)
    {
        if (Pack.LevelEdit(k) == null) return null;
        if (!Enabled) return "not applied: level edits are off";
        if (!Host.Enabled) return "not applied: the remaster is off";
        if (!Identity.Settled || k.Area != Identity.Area) return "not applied: not the settled area";
        if (_appliedFor != (Pack.Version, Identity.Settles, true)) return "pending: applied on the next frame";
        return Refused ?? (_why.TryGetValue(k, out var why) ? why : "applied");
    }

    /// <summary>What the held writes were made for: the pack version, the settle, and
    /// whether edits were wanted at all.</summary>
    static (int Version, int Settles, bool Want) _appliedFor = (-1, -1, false);

    public void OnFrame()
    {
        var m = Runtime.Mem;
        if (m == null) return;
        if (_heldLoad != Identity.BlockLoads) Forget();
        (int Version, int Settles, bool Want) key = (Pack.Version, Identity.Settles, Enabled && Host.Enabled && Identity.Settled);
        if (key == _appliedFor) return;
        _appliedFor = key;
        Revert(m);
        Refused = null;
        _why.Clear();
        Authored = Applied = 0;
        if (!key.Want) return;

        int area = Identity.Area;
        var edits = Pack.LevelEdits(area).ToList();
        Authored = edits.Count;
        if (edits.Count == 0) return;
        Refused = AreaRefusal(area);
        if (Refused != null) return;
        Apply(m, Plan(m, Identity.Baseline!.Value.Span, edits));
    }

    /// <summary>Why the settled area's edits cannot apply or be authored, or null.</summary>
    static string? AreaRefusal(int area)
        => Identity.Baseline == null ? "the area's load was not seen, so its block as the disc holds it is unknown"
         : Pack.LevelFingerprint(area) is { } fp && fp != Identity.FingerprintText
             ? $"area {area} was authored against fingerprint {fp}; this one is {Identity.FingerprintText}"
             : null;

    // ---- reading a half ----------------------------------------------------------

    /// <summary>A field of a half three ways: as the game has it now, as the loader
    /// copied it in (null when that is unknown), and as the document edits it.</summary>
    public readonly record struct FieldState(int Live, int? Loaded, int? Edit);

    public static FieldState State(IMemory m, TileKey k, TileField f)
    {
        uint rec = Identity.HalfRecord(k.X, k.Z, k.Half);
        bool here = Identity.Settled && k.Area == Identity.Area;
        int? loaded = here && Identity.Baseline is { } b ? f.Read(b.Span[(int)(rec - Identity.TileBase) + f.Offset]) : null;
        int? edit = Pack.LevelEdit(k) is { } e && e.Values.TryGetValue(f, out int v) ? v : null;
        return new FieldState(f.Read(m.ReadU8(rec + (uint)f.Offset)), loaded, edit);
    }

    /// <summary>The floor heights of the four halves beside one on the same level; null
    /// where a neighbour draws nothing, absent past the map's edge.</summary>
    public static IEnumerable<(string Side, int? Height)> NeighbourHeights(IMemory m, TileKey k)
    {
        foreach (var (side, dx, dz) in new[] { ("x-1", -1, 0), ("x+1", 1, 0), ("z-1", 0, -1), ("z+1", 0, 1) })
        {
            int x = k.X + dx, z = k.Z + dz;
            if ((uint)x >= Identity.Span || (uint)z >= Identity.Span) continue;
            uint n = Identity.HalfRecord(x, z, k.Half);
            yield return (side, m.ReadU8(n + (uint)TileField.Mesh.Offset) < 240 ? m.ReadU8(n + (uint)TileField.Height.Offset) : null);
        }
    }

    // ---- what the area's own block uses ------------------------------------------

    static HashSet<int> _meshes = new(), _shapes = new();
    static ReadOnlyMemory<byte>? _setsFor;

    /// <summary>Why a value cannot go in a field of this area, or null: a mesh or a
    /// collision shape must be one the area's own block (as loaded) already uses, since a
    /// half can only draw a mesh the area has and a shape is a slot in its block.</summary>
    public static string? Unusable(TileField f, int value)
    {
        if (!f.Valid(value)) return $"{f.Name} takes {f.Takes}";
        if (f != TileField.Mesh && f != TileField.Shape) return null;
        if (Identity.Baseline is not { } b) return "the area's block as loaded is unknown";
        if (!_setsFor.Equals(b))
        {
            _meshes = new();
            _shapes = new();
            var span = b.Span;
            for (int i = 0; i < span.Length; i += Identity.HalfBytes)
            {
                _meshes.Add(span[i + TileField.Mesh.Offset]);
                _shapes.Add(span[i + TileField.Shape.Offset]);
            }
            _setsFor = b;
        }
        return f == TileField.Mesh
            ? _meshes.Contains(value) ? null : $"mesh {value} is not one the area's block draws"
            : _shapes.Contains(value) ? null : $"collision shape {value} is not one the area's block uses";
    }

    /// <summary>Why the half cannot be edited now, or null: edits are authored only
    /// against a settled area whose block as loaded is known.</summary>
    public static string? CannotEdit(TileKey k)
        => !Identity.Settled ? "the area is settling"
         : k.Area != Identity.Area ? "the half is in another area"
         : AreaRefusal(k.Area);

    readonly record struct ByteWrite(uint Addr, byte Mask, byte Value);

    /// <summary>The document compiled against the settled area: the bytes to write,
    /// with every half that cannot be applied named in <see cref="_why"/>.</summary>
    static List<ByteWrite> Plan(IMemory m, ReadOnlySpan<byte> baseline, List<Pack.HalfEdit> edits)
    {
        var writes = new List<ByteWrite>();
        foreach (var e in edits)
        {
            if (Refuse(m, baseline, e) is { } why) { _why[e.Key] = why; continue; }
            uint rec = Identity.HalfRecord(e.Key.X, e.Key.Z, e.Key.Half);
            foreach (var group in e.Values.GroupBy(v => v.Key.Offset))
            {
                byte mask = 0, value = 0;
                foreach (var (field, v) in group)
                {
                    mask |= field.Mask;
                    value |= field.Bits(v);
                }
                writes.Add(new ByteWrite(rec + (uint)group.Key, mask, value));
            }
        }
        return writes;
    }

    static string? Refuse(IMemory m, ReadOnlySpan<byte> baseline, Pack.HalfEdit e)
    {
        if (e.Problems.Count > 0) return string.Join("; ", e.Problems);
        foreach (var (f, v) in e.Values)
            if (Unusable(f, v) is { } why) return why;
        uint rec = Identity.HalfRecord(e.Key.X, e.Key.Z, e.Key.Half);
        int at = (int)(rec - Identity.TileBase);
        // Every byte but +2, whose footprint bit the game moves as things walk over it.
        var moved = new List<string>();
        for (int off = 0; off < Identity.HalfBytes; off++)
            if (off != TileField.Collision.Offset && m.ReadU8(rec + (uint)off) != baseline[at + off])
                moved.Add($"+{off}");
        return moved.Count > 0 ? $"the game has rewritten this half ({string.Join(", ", moved)}); its rewrites win" : null;
    }

    static void Apply(IMemory m, List<ByteWrite> writes)
    {
        var halves = new HashSet<uint>();
        foreach (var w in writes)
        {
            byte live = m.ReadU8(w.Addr);
            byte value = (byte)((live & ~w.Mask) | (w.Value & w.Mask));
            _before[w.Addr] = live;
            _holding[w.Addr] = (w.Mask, value);
            m.WriteU8(w.Addr, value);
            halves.Add((w.Addr - Identity.TileBase) / Identity.HalfBytes);
        }
        Applied = halves.Count;
        _heldLoad = Identity.BlockLoads;
        Generation++;
    }

    /// <summary>Put back every held byte whose owned bits still read as written.</summary>
    static void Revert(IMemory m)
    {
        if (_heldLoad == Identity.BlockLoads)
            foreach (var (addr, (mask, wrote)) in _holding)
            {
                byte live = m.ReadU8(addr);
                if (((live ^ wrote) & mask) == 0) m.WriteU8(addr, (byte)((live & ~mask) | (_before[addr] & mask)));
                else KeptGames++;
            }
        Forget();
    }

    /// <summary>Drop what is held without writing: the block it was written into is gone.</summary>
    static void Forget()
    {
        if (_holding.Count == 0 && _heldLoad == Identity.BlockLoads) return;
        _holding.Clear();
        _before.Clear();
        _heldLoad = Identity.BlockLoads;
        Generation++;
    }

    public void Detach()
    {
        if (Runtime.Mem is { } m) Revert(m);
        _appliedFor = (-1, -1, false);
        Refused = null;
        _why.Clear();
        Authored = Applied = 0;
    }

    public string Probe()
    {
        string census = $"census {TileRewrites.Count} rewritten half/halves";
        if (!Enabled) return $"level off ({census})";
        if (Refused != null) return $"level refused ({Refused}; {census})";
        return $"level {Applied} of {Authored} half edit(s) applied" +
               (_why.Count > 0 ? $", {_why.Count} refused ({string.Join("; ", _why.Take(3).Select(p => $"{p.Key}: {p.Value}"))})" : "") +
               $", {census}";
    }
}
