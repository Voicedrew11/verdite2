using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>One floor surface: an area, a tile, and the lower or upper half of it.</summary>
public readonly record struct TileKey(int Area, int X, int Z, int Half)
{
    public const int Lower = 0, Upper = 1;

    public string HalfName => Half == Lower ? "lower" : "upper";

    public override string ToString() => $"tile:{Area}:{X}:{Z}:{HalfName}";

    /// <summary><c>tile:1:35:36:upper</c>.</summary>
    public static bool TryParse(string s, out TileKey key)
    {
        key = default;
        var p = s.Split(':');
        if (p.Length != 5 || p[0] != "tile") return false;
        if (!int.TryParse(p[1], out int a) || !int.TryParse(p[2], out int x) || !int.TryParse(p[3], out int z))
            return false;
        int half = p[4] switch { "lower" or "0" => Lower, "upper" or "1" => Upper, _ => -1 };
        if (half < 0 || (uint)x >= Identity.Span || (uint)z >= Identity.Span) return false;
        key = new TileKey(a, x, z, half);
        return true;
    }
}

/// <summary>
/// What authored data attaches to: the area, its fingerprint, and the tile halves in
/// it. Reads guest memory and writes nothing.
///
/// An area has *settled* once the area byte has held since the last module load and
/// the player stands on a half the renderer draws, at that half's floor height --
/// the invariant the map already prints, and the one moment the tile block is known
/// to be this area's. The fingerprint is taken then, before anything is applied.
/// See "Identity: what authored data attaches to" in docs/REMASTER.md.
/// </summary>
public static class Identity
{
    public const int Span = 80;
    public const int Stride = 10;
    public const int HalfBytes = 5;
    public const int TileUnits = 2048;

    public const uint TileBase = 0x801C8484;     // 80 * 80 * 10
    const uint TileBytes = Span * Span * Stride;
    const uint ShapeBase = 0x801D8484;            // the collision shapes
    const uint ShapeBytes = 0x600;
    const uint AreaAddr = 0x8017E060;
    const uint MaxHpAddr = 0x80199426;
    const uint PosXAddr = 0x801994EC, PosYAddr = 0x801994F0, PosZAddr = 0x801994F4;
    const uint HalfSelAddr = 0x801D9C8E;

    /// <summary>How far the player may stand from the half's floor and still count as
    /// on it. The map's dump reads a gap of 0 standing still.</summary>
    const int FloorSlack = 64;

    public static int Area { get; private set; } = -1;
    public static ulong Fingerprint { get; private set; }
    public static bool Settled { get; private set; }

    /// <summary>Settles taken this session, and the floor gap the last one saw.</summary>
    public static int Settles { get; private set; }
    public static int LastGap { get; private set; }

    public static string FingerprintText => Fingerprint.ToString("x16");

    /// <summary>Raised on the game thread once an area has settled.</summary>
    public static event Action? AreaSettled;

    static bool _dirty = true;

    /// <summary>An area module has loaded since the last executable did. Before one
    /// has, the block is whatever the last area left, and it can pass the floor test:
    /// measured, a New Game settled once on it before fdat02 arrived.</summary>
    static bool _module;

    /// <summary>A candidate fingerprint, and when it was taken; it settles only if a
    /// second reading this long after agrees, so a block still being copied in does not.</summary>
    static ulong _candidate;
    static long _candidateAt = -1;
    const long StableMs = 200;

    /// <summary>A module loaded: the tile block may be about to change.</summary>
    public static void Invalidate(string overlay)
    {
        _dirty = true;
        Settled = false;
        _candidateAt = -1;
        _module = overlay.StartsWith("fdat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Once a frame, on the game thread.</summary>
    public static void Poll(IMemory m)
    {
        if (m.ReadU16(MaxHpAddr) == 0)
        {
            Settled = false;
            Area = -1;
            return;
        }

        int area = m.ReadU8(AreaAddr);
        if (area != Area || _dirty)
        {
            Area = area;
            Settled = false;
            _dirty = false;
            _candidateAt = -1;
        }
        if (Settled || !_module) return;

        int px = (int)m.ReadU32(PosXAddr), py = (int)m.ReadU32(PosYAddr), pz = (int)m.ReadU32(PosZAddr);
        int half = m.ReadU16(HalfSelAddr) == 0 ? TileKey.Lower : TileKey.Upper;
        int tx = px / TileUnits, tz = pz / TileUnits;
        if ((uint)tx >= Span || (uint)tz >= Span) return;
        uint rec = HalfRecord(tx, tz, half);
        if (m.ReadU8(rec) >= 240) return;
        int gap = py - -(m.ReadU8(rec + 1u) << 7);
        if (Math.Abs(gap) > FloorSlack) { _candidateAt = -1; return; }

        long now = Environment.TickCount64;
        if (_candidateAt < 0) { _candidate = Take(m); _candidateAt = now; return; }
        if (now - _candidateAt < StableMs) return;
        ulong fp = Take(m);
        if (fp != _candidate) { _candidate = fp; _candidateAt = now; return; }

        if (Probe) Compare(m, area, fp);
        Fingerprint = fp;
        LastGap = gap;
        Settled = true;
        Settles++;
        AreaSettled?.Invoke();
    }

    /// <summary>KF2_REMASTER_PROBE: name the tile bytes that differ when an area
    /// settles on a fingerprint it has not settled on before.</summary>
    public static bool Probe;

    static readonly Dictionary<int, (ulong Fp, byte[] Block)> _seen = new();

    static void Compare(IMemory m, int area, ulong fp)
    {
        var block = new byte[TileBytes];
        for (uint i = 0; i < TileBytes; i++) block[i] = m.ReadU8(TileBase + i);
        if (_seen.TryGetValue(area, out var old) && old.Fp != fp)
        {
            var lines = new List<string>();
            int halves = 0;
            for (int h = 0; h < Span * Span * 2; h++)
            {
                int o = h * HalfBytes;
                if (block.AsSpan(o, HalfBytes).SequenceEqual(old.Block.AsSpan(o, HalfBytes))) continue;
                halves++;
                int z = o / (Span * Stride), x = o % (Span * Stride) / Stride;
                if (lines.Count < 24)
                    lines.Add($"{x},{z},{(h % 2 == 0 ? "lower" : "upper")} " +
                              $"{Convert.ToHexString(old.Block, o, HalfBytes)}->{Convert.ToHexString(block, o, HalfBytes)}");
            }
            Console.WriteLine($"[KF2] remaster: area {area} fingerprint {old.Fp:x16} -> {fp:x16}; " +
                              $"{halves} tile half/halves differ" + (halves == 0 ? " (the collision shapes do)" : ":"));
            foreach (var l in lines) Console.WriteLine($"[KF2] remaster:   {l}");
        }
        _seen[area] = (fp, block);
    }

    /// <summary>
    /// FNV-1a 64 over the tile block and the collision shapes. A fingerprint, not a
    /// copy: it cannot be turned back into either.
    ///
    /// Each half's +2, the collision flags, is left out: the game writes a moving
    /// thing's footprint into it. Measured, area 0 from a New Game against area 0
    /// from save slot 2: 32 empty lower halves in two 4x4 blocks, +2 bit 0x04 set in
    /// one and cleared in the other, and nothing else in the block different.
    /// </summary>
    static ulong Take(IMemory m)
    {
        ulong h = 0xCBF29CE484222325UL;
        void Run(uint start, uint bytes, bool tiles)
        {
            for (uint i = 0; i < bytes; i += 4)
            {
                uint w = m.ReadU32(start + i);
                for (uint b = 0; b < 4; b++)
                {
                    if (tiles && (i + b) % HalfBytes == 2) continue;
                    h ^= (byte)(w >> (int)(b * 8));
                    h *= 0x100000001B3UL;
                }
            }
        }
        Run(TileBase, TileBytes, true);
        Run(ShapeBase, ShapeBytes, false);
        return h;
    }

    public static uint HalfRecord(int x, int z, int half)
        => TileBase + (uint)(z * Span * Stride + x * Stride + half * HalfBytes);

    /// <summary>The tile half a record address names; false outside the block.</summary>
    public static bool FromRecord(uint rec, out int x, out int z, out int half)
    {
        uint off = rec - TileBase;
        x = z = half = 0;
        if (off >= TileBytes) return false;
        z = (int)(off / (Span * Stride));
        uint r = off % (Span * Stride);
        x = (int)(r / Stride);
        half = r % Stride >= HalfBytes ? TileKey.Upper : TileKey.Lower;
        return true;
    }

    /// <summary>The player's own tile half, or null outside an area.</summary>
    public static TileKey? PlayerTile(IMemory m)
    {
        if (Area < 0) return null;
        int px = (int)m.ReadU32(PosXAddr), pz = (int)m.ReadU32(PosZAddr);
        int tx = px / TileUnits, tz = pz / TileUnits;
        if ((uint)tx >= Span || (uint)tz >= Span) return null;
        return new TileKey(Area, tx, tz, m.ReadU16(HalfSelAddr) == 0 ? TileKey.Lower : TileKey.Upper);
    }
}
