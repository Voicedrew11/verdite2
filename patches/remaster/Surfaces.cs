using RecompOne.Runtime;

namespace Kf2.Remaster;

/// <summary>
/// Authored materials on map tiles. The pack names a material per tile half; this
/// resolves the names to <see cref="SurfaceMaterial"/> ids when the area settles or
/// the pack changes, and <see cref="TileWalk"/> reads the id for the half it is
/// assembling, which <c>PolyAssembler.SealDepth</c> writes into the packet's depth
/// record. From there it is 0067's: the surface buffer carries it and the reflection
/// pass reads the material's reflectivity and F0.
///
/// Off, and with nothing authored for the area, <see cref="At"/> answers 0 and the
/// record's material is the None it always was.
/// </summary>
public sealed class Surfaces : IRemasterFeature
{
    public string Id => "surfaces";

    /// <summary>One id per tile half: (z * 80 + x) * 2 + half.</summary>
    static readonly byte[] _table = new byte[Identity.Span * Identity.Span * 2];

    /// <summary>Whether <see cref="_table"/> holds anything for the area on screen.</summary>
    static bool _active;

    /// <summary>The name each authored id was given, from <see cref="SurfaceMaterial.FirstAuthored"/>.</summary>
    public static readonly string?[] IdNames = new string?[SurfaceMaterial.Count];

    /// <summary>Why the area's surfaces are not applied, or null.</summary>
    public static string? Refused { get; private set; }

    /// <summary>Library entries past the ids 0067's table has room for.</summary>
    public static int Unallocated { get; private set; }

    public static int TilesApplied { get; private set; }

    /// <summary>Packets sealed with an authored material; never reset.</summary>
    public static long Packets;

    int _version = -1, _settle = -1;

    public void OnFrame()
    {
        if (!Host.Enabled || !Identity.Settled)
        {
            if (_active) Array.Clear(_table);
            _active = false;
            _settle = -1;
            TilesApplied = 0;
            if (!Host.Enabled) ClearIds();
            return;
        }
        if (_version == Pack.Version && _settle == Identity.Settles) return;
        _version = Pack.Version;
        _settle = Identity.Settles;
        Apply();
    }

    public void Detach()
    {
        Array.Clear(_table);
        _active = false;
        ClearIds();
        _version = _settle = -1;
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
        Array.Clear(_table);
        _active = false;
        ClearIds();

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

        int n = 0;
        foreach (var (k, name) in Pack.Tiles(area))
        {
            if (!ids.TryGetValue(name, out byte id)) continue;
            _table[Index(k.X, k.Z, k.Half)] = id;
            n++;
        }
        TilesApplied = n;
        _active = n > 0;
    }

    static int Index(int x, int z, int half) => (z * Identity.Span + x) * 2 + half;

    /// <summary>The material id for the tile half whose record is at <paramref name="rec"/>.</summary>
    public static byte At(uint rec)
    {
        if (!_active || !Identity.FromRecord(rec, out int x, out int z, out int half)) return 0;
        return _table[Index(x, z, half)];
    }

    public static byte IdOf(string? name)
    {
        if (name == null) return 0;
        for (int i = SurfaceMaterial.FirstAuthored; i < SurfaceMaterial.Count; i++)
            if (IdNames[i] == name) return (byte)i;
        return 0;
    }

    public string Probe()
        => $"surfaces {(Refused != null ? "refused: " + Refused : $"{TilesApplied} tile half/halves applied")}, " +
           $"{Packets} packet(s) sealed with a material" +
           (Unallocated > 0 ? $", {Unallocated} material(s) past the id table" : "");
}
