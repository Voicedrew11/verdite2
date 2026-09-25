using RecompOne.Runtime;

namespace Kf2.Remaster;

/// <summary>
/// Authored materials on map tiles. The pack names a material for a whole tile half,
/// for faces of the mesh a half draws, and for faces of a mesh wherever the area uses
/// it; this resolves the names to <see cref="SurfaceMaterial"/> ids when the area
/// settles or the pack changes. <see cref="TileWalk"/> asks for the half it is
/// assembling and <see cref="Faces"/> for each face, and <c>PolyAssembler.SealDepth</c>
/// writes the id into the packet's depth record. From there it is 0067's: the surface
/// buffer carries it and the reflection pass reads the material's reflectivity and F0.
///
/// The most specific entry wins: the half's face, the whole half, the mesh's face, the
/// whole mesh. A face list applies only to the mesh it was authored on, and only while
/// that mesh hashes as it did (<see cref="Faces.MeshHash"/>); a mismatch drops the
/// entry whole. Off, and with nothing authored for the area, every answer is 0 and the
/// record's material is the None it always was.
/// </summary>
public sealed class Surfaces : IRemasterFeature
{
    public string Id => "surfaces";

    /// <summary>One id per tile half: (z * 80 + x) * 2 + half.</summary>
    static readonly byte[] _table = new byte[Identity.Span * Identity.Span * 2];

    /// <summary>Whether <see cref="_table"/> holds anything for the area on screen.</summary>
    static bool _active;

    /// <summary>Face lists by half index, each with the mesh it was authored on.</summary>
    static readonly Dictionary<int, (int Mesh, byte[] Ids)> _tileFaces = new();

    /// <summary>Area-wide rules by mesh: a whole-mesh id and a face list.</summary>
    static readonly Dictionary<int, byte> _meshWhole = new();
    static readonly Dictionary<int, byte[]> _meshFaces = new();

    /// <summary>Whether anything for the area is authored below the whole half, so
    /// the assemblers must ask per face.</summary>
    public static bool PerFace { get; private set; }

    // The half being assembled.
    static byte _half, _meshAll;
    static byte[]? _tf, _mf;

    /// <summary>Face lists dropped because their mesh no longer hashes as it did.</summary>
    public static int MeshRefused { get; private set; }

    /// <summary>The name each authored id was given, from <see cref="SurfaceMaterial.FirstAuthored"/>.</summary>
    public static readonly string?[] IdNames = new string?[SurfaceMaterial.Count];

    /// <summary>Why the area's surfaces are not applied, or null.</summary>
    public static string? Refused { get; private set; }

    /// <summary>Library entries past the ids 0067's table has room for.</summary>
    public static int Unallocated { get; private set; }

    public static int TilesApplied { get; private set; }

    /// <summary>Packets sealed with an authored material; never reset.</summary>
    public static long Packets;

    int _version = -1, _settle = -1, _tableSerial = -1;

    public void OnFrame()
    {
        if (!Host.Enabled || !Identity.Settled)
        {
            if (_active) Clear();
            _settle = _tableSerial = -1;
            TilesApplied = 0;
            if (!Host.Enabled) ClearIds();
            return;
        }
        // A face list is checked against its mesh, which is readable once the tile walk
        // has noted the table.
        if (_version == Pack.Version && _settle == Identity.Settles && _tableSerial == Faces.TableSerial) return;
        _version = Pack.Version;
        _settle = Identity.Settles;
        _tableSerial = Faces.TableSerial;
        Apply();
    }

    public void Detach()
    {
        Clear();
        ClearIds();
        _version = _settle = _tableSerial = -1;
    }

    static void Clear()
    {
        Array.Clear(_table);
        _tileFaces.Clear();
        _meshWhole.Clear();
        _meshFaces.Clear();
        _active = PerFace = false;
        _tf = _mf = null;
        _half = _meshAll = 0;
    }

    static void ClearIds()
    {
        for (int i = SurfaceMaterial.FirstAuthored; i < SurfaceMaterial.Count; i++)
        {
            SurfaceMaterial.Reflectivity[i] = 0f;
            SurfaceMaterial.F0[i] = 0f;
            IdNames[i] = null;
        }
    }

    static void Apply()
    {
        Clear();
        ClearIds();
        MeshRefused = 0;

        // Ids are handed out by name at load time, so two packs never collide on a
        // number. 0067's table has eight and the runtime keeps four.
        var ids = new Dictionary<string, byte>();
        byte next = SurfaceMaterial.FirstAuthored;
        int over = 0;
        foreach (var m in Pack.Materials())
        {
            if (next >= SurfaceMaterial.Count) { over++; continue; }
            ids[m.Name] = next;
            IdNames[next] = m.Name;
            SurfaceMaterial.Reflectivity[next] = Math.Clamp(m.Reflectivity, 0f, 1f);
            SurfaceMaterial.F0[next] = Math.Clamp(m.F0, 0f, 1f);
            next++;
        }
        Unallocated = over;

        int area = Identity.Area;
        string? fp = Pack.AreaFingerprint(area);
        if (fp != null && fp != Identity.FingerprintText)
        {
            Refused = $"area {area} was authored against fingerprint {fp}; this one is {Identity.FingerprintText}";
            TilesApplied = 0;
            return;
        }
        Refused = null;

        var mem = Runtime.Mem;
        int n = 0;
        foreach (var (k, name) in Pack.Tiles(area))
        {
            if (!ids.TryGetValue(name, out byte id)) continue;
            _table[Index(k.X, k.Z, k.Half)] = id;
            n++;
        }
        foreach (var e in Pack.TileFaceLists(area))
        {
            if (mem == null || !Gate(mem, e.Mesh, e.Hash)) { MeshRefused++; continue; }
            var list = Ids(e.Faces, ids);
            if (list == null) continue;
            _tileFaces[Index(e.Tile.X, e.Tile.Z, e.Tile.Half)] = (e.Mesh, list);
            n++;
        }
        foreach (var e in Pack.MeshRules(area))
        {
            if (mem == null || !Gate(mem, e.Mesh, e.Hash)) { MeshRefused++; continue; }
            if (e.Material != null && ids.TryGetValue(e.Material, out byte all)) _meshWhole[e.Mesh] = all;
            if (Ids(e.Faces, ids) is { } list) _meshFaces[e.Mesh] = list;
            n++;
        }
        TilesApplied = n;
        PerFace = _tileFaces.Count > 0 || _meshWhole.Count > 0 || _meshFaces.Count > 0;
        _active = n > 0;
    }

    static bool Gate(RecompOne.Runtime.Memory.IMemory m, int mesh, string? hash)
        => hash != null && Faces.MeshHash(m, mesh) == hash;

    /// <summary>A face list's names as ids, indexed by face; null when none resolves.</summary>
    static byte[]? Ids(IEnumerable<(int Face, string Material)> faces, Dictionary<string, byte> ids)
    {
        byte[]? list = null;
        foreach (var (f, name) in faces)
        {
            if (f is < 0 or > 4095 || !ids.TryGetValue(name, out byte id)) continue;
            if (list == null || list.Length <= f) Array.Resize(ref list, f + 1);
            list[f] = id;
        }
        return list;
    }

    static int Index(int x, int z, int half) => (z * Identity.Span + x) * 2 + half;

    /// <summary>The half whose record is at <paramref name="rec"/> is about to draw mesh
    /// <paramref name="model"/>: its id where no face says otherwise.</summary>
    public static byte EnterHalf(uint rec, int model)
    {
        _tf = _mf = null;
        _half = _meshAll = 0;
        if (!_active || !Identity.FromRecord(rec, out int x, out int z, out int half)) return 0;
        int i = Index(x, z, half);
        _half = _table[i];
        if (PerFace)
        {
            if (_tileFaces.TryGetValue(i, out var t) && t.Mesh == model) _tf = t.Ids;
            _meshFaces.TryGetValue(model, out _mf);
            _meshWhole.TryGetValue(model, out _meshAll);
        }
        return _half != 0 ? _half : _meshAll;
    }

    /// <summary>The id of face <paramref name="f"/> of the half being assembled; -1 is a
    /// face that could not be named.</summary>
    public static byte Face(int f)
    {
        byte id = 0;
        if (_tf != null && (uint)f < (uint)_tf.Length) id = _tf[f];
        if (id == 0) id = _half;
        if (id == 0 && _mf != null && (uint)f < (uint)_mf.Length) id = _mf[f];
        return id != 0 ? id : _meshAll;
    }

    public static byte IdOf(string? name)
    {
        if (name == null) return 0;
        for (int i = SurfaceMaterial.FirstAuthored; i < SurfaceMaterial.Count; i++)
            if (IdNames[i] == name) return (byte)i;
        return 0;
    }

    public string Probe()
        => $"surfaces {(Refused != null ? "refused: " + Refused : $"{TilesApplied} entries applied")}" +
           (MeshRefused > 0 ? $", {MeshRefused} dropped for a changed mesh" : "") + ", " +
           $"{Packets} packet(s) sealed with a material" +
           (Unallocated > 0 ? $", {Unallocated} material(s) past the id table" : "");
}
