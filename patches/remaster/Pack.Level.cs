using System.Text.Json.Nodes;

namespace Kf2.Remaster;

/// <summary>
/// The level document, <c>areas/&lt;n&gt;/level.json</c>: under <c>halves</c>, a tile
/// half's key and the values the author gave its fields (<see cref="TileField"/>), each
/// optional. Only the author's values are held, never the game's bytes; the area's
/// fingerprint is the document's own and gates it whole. See "Phase 6, the first
/// slice" in docs/REMASTER.md.
///
///     { "x": 26, "z": 36, "half": "upper", "height": 5, "stopsFlood": true }
/// </summary>
public static partial class Pack
{
    /// <summary>One half's edit as the document holds it: the fields it sets, and what it
    /// names that cannot be applied (a value out of a field's range).</summary>
    public readonly record struct HalfEdit(TileKey Key, IReadOnlyDictionary<TileField, int> Values,
                                           IReadOnlyList<string> Problems);

    const string HalvesKey = "halves";

    static JsonArray? HalvesOf(int area)
        => _set.Level.TryGetValue(area, out var doc) ? doc[HalvesKey] as JsonArray : null;

    /// <summary>The fingerprint the area's level edits were authored against, or null.</summary>
    public static string? LevelFingerprint(int area)
        => _set.Level.TryGetValue(area, out var d) ? Str(d["fingerprint"]) : null;

    public static IEnumerable<HalfEdit> LevelEdits(int area)
    {
        if (HalvesOf(area) is not { } halves) yield break;
        foreach (var n in halves)
            if (n is JsonObject o && HalfKey(o, area) is { } key)
                yield return ParseHalf(o, key);
    }

    public static HalfEdit? LevelEdit(TileKey k)
        => HalvesOf(k.Area) is { } halves && FindTile(halves, k) is { } o ? ParseHalf(o, k) : null;

    static TileKey? HalfKey(JsonObject o, int area)
    {
        if (Int(o["x"]) is not { } x || Int(o["z"]) is not { } z) return null;
        if ((uint)x >= Identity.Span || (uint)z >= Identity.Span) return null;
        return new TileKey(area, x, z, Str(o["half"]) == "upper" ? TileKey.Upper : TileKey.Lower);
    }

    static HalfEdit ParseHalf(JsonObject o, TileKey key)
    {
        var values = new Dictionary<TileField, int>();
        var problems = new List<string>();
        foreach (var f in TileField.All)
        {
            if (o[f.Name] is not JsonValue v) continue;
            int? value = f.IsFlag && v.TryGetValue(out bool b) ? (b ? 1 : 0) : Int(v);
            if (value is { } x && f.Valid(x)) values[f] = x;
            else problems.Add($"{f.Name} {v.ToJsonString()}: it takes {f.Takes}");
        }
        return new HalfEdit(key, values, problems);
    }

    /// <summary>Set one field of a half, or clear it with null, as one undo entry; a half
    /// left with no field goes.</summary>
    public static void SetLevelField(TileKey k, TileField field, int? value, string fingerprint)
    {
        if (value is { } v && !field.Valid(v))
            throw new ArgumentOutOfRangeException(nameof(value), $"{field.Name} takes {field.Takes}");
        int? current = LevelEdit(k) is { } now && now.Values.TryGetValue(field, out int cur) ? cur : null;
        if (current == value) return;
        EditDoc(s => s.Level, HalvesKey, k.Area, fingerprint, $"{k} {field.Name} = {value?.ToString() ?? "game's"}", doc =>
        {
            var halves = (JsonArray)doc[HalvesKey]!;
            var e = FindTile(halves, k);
            if (e == null)
            {
                if (value == null) return;
                halves.Add(e = new JsonObject { ["x"] = k.X, ["z"] = k.Z, ["half"] = k.HalfName });
            }
            if (value is not { } x) e.Remove(field.Name);
            else if (field.IsFlag) e[field.Name] = x != 0;
            else e[field.Name] = x;
            if (!TileField.All.Any(f => e.ContainsKey(f.Name))) halves.Remove(e);
        });
    }

    /// <summary>Clear every field of a half, as one undo entry.</summary>
    public static void ResetLevelHalf(TileKey k)
    {
        if (HalvesOf(k.Area) is not { } halves || FindTile(halves, k) == null) return;
        EditDoc(s => s.Level, HalvesKey, k.Area, LevelFingerprint(k.Area) ?? "", $"{k} = game's", doc =>
        {
            if (doc[HalvesKey] is JsonArray list && FindTile(list, k) is { } e) list.Remove(e);
        });
    }
}
