using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecompOne.Runtime.Assets;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>
/// The rewrite census: which tile halves the game itself rewrites as it plays -- a
/// door, a lift, the drawbridge, the minecart -- measured, so the editor can say that
/// an edit there is undone when the game does it. Reads guest memory and writes nothing.
///
/// Four times a second, while an area is settled on a block whose load was seen, the
/// live block is compared with what it should hold: the block as loaded, with the bits
/// the level edits hold over it. A half that differs anywhere but <c>+2</c> (where the
/// game writes a moving thing's footprint) was rewritten by the game. That includes a
/// half already rewritten when the area settled, from state a save carries.
///
/// A census accumulates across sessions, per area fingerprint, in
/// <c>dump/GAME/census/rewrites.json</c>: coordinates and which of a half's bytes
/// moved, nothing of the bytes themselves. See "Phase 6, the first slice" in docs/REMASTER.md.
/// </summary>
public static class TileRewrites
{
    const long PeriodMs = 250, SaveAfterMs = 5000;
    const int Words = (int)Identity.TileBytes / 4;

    /// <summary>Per word of the block, the bytes the census compares: all but each half's +2.</summary>
    static readonly uint[] Compared = BuildCompared();

    static uint[] BuildCompared()
    {
        var w = new uint[Words];
        for (int i = 0; i < Words * 4; i++)
            if (i % Identity.HalfBytes != TileField.Collision.Offset) w[i / 4] |= 0xFFu << (i % 4 * 8);
        return w;
    }

    /// <summary>A fingerprint's halves, each with the offsets that moved as bits.</summary>
    static readonly Dictionary<string, (int Area, Dictionary<(int X, int Z, int Half), int> Halves)> _census = new();
    static bool _loaded, _dirty;
    static long _pollAt, _dirtyAt;

    static byte[]? _expected;
    static (int Settles, int Held) _expectedFor = (-1, -1);

    /// <summary>Compares made, and halves newly found rewritten; never reset.</summary>
    public static long Polls, Found;

    public static string? LastError { get; private set; }

    /// <summary>Once a frame, on the game thread. <paramref name="held"/> is every byte
    /// the level edits hold, by address, with the bits they own; <paramref name="heldGeneration"/>
    /// moves whenever it changes.</summary>
    public static void Poll(IMemory m, IReadOnlyDictionary<uint, (byte Mask, byte Value)> held, int heldGeneration)
    {
        long now = Environment.TickCount64;
        if (_dirty && now - _dirtyAt >= SaveAfterMs) Save();
        if (now < _pollAt || !Identity.Settled || Identity.Baseline is not { } baseline) return;
        _pollAt = now + PeriodMs;
        EnsureLoaded();

        if (_expectedFor != (Identity.Settles, heldGeneration)) _expected = Expected(baseline.Span, held);
        _expectedFor = (Identity.Settles, heldGeneration);
        var expected = _expected!;

        Dictionary<(int, int, int), int>? found = null;
        for (int w = 0; w < Words; w++)
        {
            uint diff = (m.ReadU32(Identity.TileBase + (uint)w * 4) ^ BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(w * 4)))
                        & Compared[w];
            for (int b = 0; diff != 0; b++, diff >>= 8)
            {
                if ((diff & 0xFF) == 0) continue;
                int i = w * 4 + b;
                int z = i / (Identity.Span * Identity.Stride), r = i % (Identity.Span * Identity.Stride);
                int half = r % Identity.Stride / Identity.HalfBytes;
                found ??= Halves(Identity.FingerprintText, Identity.Area);
                var key = (r / Identity.Stride, z, half);
                int bit = 1 << (r % Identity.HalfBytes);
                found.TryGetValue(key, out int had);
                if ((had & bit) != 0) continue;
                if (had == 0) Found++;
                found[key] = had | bit;
                if (!_dirty) _dirtyAt = now;
                _dirty = true;
            }
        }
        Polls++;
    }

    static byte[] Expected(ReadOnlySpan<byte> baseline, IReadOnlyDictionary<uint, (byte Mask, byte Value)> held)
    {
        var e = baseline.ToArray();
        foreach (var (addr, (mask, value)) in held)
        {
            uint i = addr - Identity.TileBase;
            if (i < e.Length) e[i] = (byte)((e[i] & ~mask) | (value & mask));
        }
        return e;
    }

    static Dictionary<(int, int, int), int> Halves(string fingerprint, int area)
    {
        if (!_census.TryGetValue(fingerprint, out var c)) _census[fingerprint] = c = (area, new());
        return c.Halves;
    }

    /// <summary>The offsets of a half the game has been seen to rewrite, as bits (1 &lt;&lt;
    /// offset); 0 when it never has, in this area as fingerprinted.</summary>
    public static int Rewritten(TileKey k)
    {
        EnsureLoaded();
        return Identity.Settled && k.Area == Identity.Area
               && _census.TryGetValue(Identity.FingerprintText, out var c)
               && c.Halves.TryGetValue((k.X, k.Z, k.Half), out int bits) ? bits : 0;
    }

    /// <summary>Halves the current area's census holds.</summary>
    public static int Count
        => Identity.Settled && _census.TryGetValue(Identity.FingerprintText, out var c) ? c.Halves.Count : 0;

    public static IEnumerable<(TileKey Key, int Bytes)> List()
    {
        if (!Identity.Settled || !_census.TryGetValue(Identity.FingerprintText, out var c)) yield break;
        foreach (var ((x, z, h), bits) in c.Halves.OrderBy(p => p.Key.Item2).ThenBy(p => p.Key.Item1))
            yield return (new TileKey(Identity.Area, x, z, h), bits);
    }

    /// <summary>Forget the current area's census, on disk too.</summary>
    public static void Reset()
    {
        EnsureLoaded();
        if (Identity.Settled && _census.Remove(Identity.FingerprintText)) Save();
    }

    /// <summary>The offsets a census entry holds, as <c>+3,+4</c>.</summary>
    public static string DescribeBytes(int bits)
        => string.Join(",", Enumerable.Range(0, Identity.HalfBytes).Where(o => (bits & (1 << o)) != 0).Select(o => $"+{o}"));

    // ---- the file ----------------------------------------------------------

    public static string FilePath
        => Path.GetFullPath(Path.Combine("dump", AssetReplacerManager.Instance.GameId, "census", "rewrites.json"));

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            if (JsonNode.Parse(File.ReadAllText(FilePath))?["areas"] is not JsonObject areas) return;
            foreach (var (fp, node) in areas)
            {
                if (node is not JsonObject a || a["halves"] is not JsonArray list) continue;
                var halves = Halves(fp, a["area"] is JsonValue av && av.TryGetValue(out int area) ? area : -1);
                foreach (var h in list)
                    if (h is JsonObject o && o["x"] is JsonValue xv && xv.TryGetValue(out int x)
                        && o["z"] is JsonValue zv && zv.TryGetValue(out int z) && o["bytes"] is JsonArray bytes)
                    {
                        int bits = 0;
                        foreach (var b in bytes)
                            if (b is JsonValue bv && bv.TryGetValue(out int off) && off is >= 0 and < Identity.HalfBytes) bits |= 1 << off;
                        if (bits != 0) halves[(x, z, o["half"]?.GetValue<string>() == "upper" ? TileKey.Upper : TileKey.Lower)] = bits;
                    }
            }
        }
        catch (Exception e)
        {
            LastError = $"cannot read {FilePath}: {e.Message}";
            Console.Error.WriteLine($"[KF2] remaster: {LastError}");
        }
    }

    static void Save()
    {
        _dirty = false;
        var areas = new JsonObject();
        foreach (var (fp, (area, halves)) in _census.OrderBy(p => p.Value.Area))
            areas[fp] = new JsonObject
            {
                ["area"] = area,
                ["halves"] = new JsonArray(halves.OrderBy(p => p.Key.Z).ThenBy(p => p.Key.X).Select(p => (JsonNode)new JsonObject
                {
                    ["x"] = p.Key.X, ["z"] = p.Key.Z, ["half"] = p.Key.Half == TileKey.Upper ? "upper" : "lower",
                    ["bytes"] = new JsonArray(Enumerable.Range(0, Identity.HalfBytes).Where(o => (p.Value & (1 << o)) != 0)
                                                        .Select(o => (JsonNode)o).ToArray()),
                }).ToArray()),
            };
        var doc = new JsonObject { ["formatVersion"] = 1, ["areas"] = areas };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Move(tmp, FilePath, overwrite: true);
            LastError = null;
        }
        catch (Exception e)
        {
            LastError = $"cannot write {FilePath}: {e.Message}";
            Console.Error.WriteLine($"[KF2] remaster: {LastError}");
        }
    }
}
