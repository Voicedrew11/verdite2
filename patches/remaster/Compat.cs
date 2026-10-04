using System.Text.Json;
using System.Text.Json.Nodes;
using RecompOne.Runtime.Assets;

namespace Kf2.Remaster;

/// <summary>
/// The compatibility report: for each area the working pack holds, whether each of its
/// documents was authored against an area this disc has, and how many of its keys
/// resolved when the area was last applied.
///
/// A fingerprint can only be known by loading the area, so the port keeps a census of
/// every fingerprint it has settled on as loaded, per disc, in
/// <c>dump/GAME/census/areas.json</c>, with the last resolution seen in each. It grows
/// as areas are visited; an area never visited on this install reads as unseen, not as
/// a mismatch. Only identifiers and counts are kept. See "Phase 7, the first slice" in
/// docs/REMASTER.md.
/// </summary>
public static class Compat
{
    sealed class Seen
    {
        public int Area;
        public JsonObject? Resolved;
    }

    static readonly Dictionary<string, Seen> _seen = new();
    static bool _loaded;
    static int _settles = -1;
    static long _settledAt, _nextAt;

    public static string? LastError { get; private set; }

    /// <summary>After a settle, how long the features get to apply before a count is taken.</summary>
    const long SettleMs = 1000, EveryMs = 2000;

    public static string FilePath
        => Path.GetFullPath(Path.Combine("dump", AssetReplacerManager.Instance.GameId, "census", "areas.json"));

    /// <summary>Once a frame, after the features: note the area's fingerprint, and what
    /// resolved in it.</summary>
    public static void Poll()
    {
        if (!Identity.Settled || !Identity.FromLoad) return;
        long now = Environment.TickCount64;
        EnsureLoaded();
        string fp = Identity.FingerprintText;
        if (Identity.Settles != _settles)
        {
            _settles = Identity.Settles;
            _settledAt = now;
            _nextAt = now + SettleMs;
            if (!_seen.ContainsKey(fp))
            {
                _seen[fp] = new Seen { Area = Identity.Area };
                Save();
            }
        }
        if (!Host.Enabled || now < _nextAt || now - _settledAt < SettleMs) return;
        _nextAt = now + EveryMs;
        var r = Resolution();
        var seen = _seen[fp];
        if (seen.Resolved is { } old && Same(old, r)) return;
        r["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        r["pack"] = Pack.Root;
        seen.Resolved = r;
        Save();
    }

    static bool Same(JsonObject a, JsonObject b)
    {
        var x = (JsonObject)a.DeepClone();
        x.Remove("at");
        if (x["pack"]?.ToString() != Pack.Root) return false;
        x.Remove("pack");
        return x.ToJsonString() == b.ToJsonString();
    }

    /// <summary>What the features applied and refused in the loaded area, now.</summary>
    static JsonObject Resolution()
    {
        static JsonObject Part(string? refused, params (string Key, int Value)[] counts)
        {
            var o = new JsonObject();
            if (refused != null) o["refused"] = refused;
            foreach (var (k, v) in counts) o[k] = v;
            return o;
        }
        return new JsonObject
        {
            ["surfaces"] = Part(Surfaces.Refused, ("applied", Surfaces.TilesApplied),
                                ("changedMesh", Surfaces.MeshRefused), ("noMaterial", Surfaces.NoMaterial)),
            ["lights"] = Part(Lights.Refused, ("authored", Lights.Authored)),
            ["atmosphere"] = Part(Atmosphere.Refused, ("applied", Atmosphere.Applied), ("changedRecord", Atmosphere.Stale)),
            ["props"] = Part(Props.Refused, ("resolved", Props.Resolved), ("unresolved", Props.Authored - Props.Resolved)),
            ["level"] = Level.Enabled
                ? Part(Level.Refused, ("applied", Level.Applied), ("refused", Level.RefusedHalves))
                : new JsonObject { ["off"] = true },
        };
    }

    /// <summary>The report, as the <c>pack report</c> verb and the editor show it.</summary>
    public static JsonObject Report()
    {
        EnsureLoaded();
        var areas = new JsonArray();
        int matched = 0, differ = 0, unseen = 0;
        foreach (var group in Pack.AreaDocs().GroupBy(d => d.Area))
        {
            int area = group.Key;
            var seenHere = _seen.Where(p => p.Value.Area == area).Select(p => p.Key).ToHashSet();
            var docs = new JsonArray();
            string worst = "matches";
            foreach (var d in group)
            {
                string status = d.Fingerprint == null ? "no fingerprint"
                              : seenHere.Contains(d.Fingerprint) ? "matches"
                              : seenHere.Count > 0 ? "differs" : "unseen";
                if (status == "differs" || (status != "matches" && worst == "matches")) worst = status;
                docs.Add(new JsonObject
                {
                    ["kind"] = d.Kind, ["entries"] = d.Entries, ["fingerprint"] = d.Fingerprint, ["status"] = status,
                });
            }
            switch (worst) { case "matches": matched++; break; case "differs": differ++; break; default: unseen++; break; }
            var o = new JsonObject
            {
                ["area"] = area,
                ["status"] = worst,
                ["documents"] = docs,
                ["seen"] = new JsonArray(seenHere.Order().Select(f => (JsonNode)f).ToArray()),
            };
            // The authored area's last count, or, when it was never seen, the one this
            // disc has, which says why it was refused.
            string? authored = group.Select(d => d.Fingerprint).FirstOrDefault(f => f != null && seenHere.Contains(f));
            string? from = authored ?? seenHere.Where(f => Current(_seen[f].Resolved)).Order().FirstOrDefault();
            if (from != null && _seen[from].Resolved is { } r && Current(r))
            {
                o["resolved"] = r.DeepClone();
                o["resolvedIn"] = from;
            }
            if (area == Identity.Area && Identity.Settled)
                o["loaded"] = Identity.FromLoad ? Identity.FingerprintText : $"{Identity.FingerprintText} (live; the load was not seen)";
            areas.Add(o);
        }
        return new JsonObject
        {
            ["pack"] = Pack.Root,
            ["areas"] = areas,
            ["matched"] = matched,
            ["differ"] = differ,
            ["unseen"] = unseen,
            ["textureRules"] = Pack.TextureRules().Count(),
            ["census"] = FilePath,
        };
    }

    /// <summary>A count taken with this pack; another pack's says nothing about it.</summary>
    static bool Current(JsonObject? r) => r?["pack"]?.ToString() == Pack.Root;

    static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            if (JsonNode.Parse(File.ReadAllText(FilePath))?["areas"] is not JsonObject areas) return;
            foreach (var (fp, node) in areas)
                if (node is JsonObject a && a["area"] is JsonValue v && v.TryGetValue(out int area))
                    _seen[fp] = new Seen { Area = area, Resolved = a["resolved"]?.DeepClone() as JsonObject };
        }
        catch (Exception e)
        {
            LastError = $"cannot read {FilePath}: {e.Message}";
            Console.Error.WriteLine($"[KF2] remaster: {LastError}");
        }
    }

    static void Save()
    {
        var areas = new JsonObject();
        foreach (var (fp, s) in _seen.OrderBy(p => p.Value.Area).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var o = new JsonObject { ["area"] = s.Area };
            if (s.Resolved != null) o["resolved"] = s.Resolved.DeepClone();
            areas[fp] = o;
        }
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
