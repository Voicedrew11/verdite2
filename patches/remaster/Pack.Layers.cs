using System.IO.Compression;
using System.Text.Json.Nodes;
using RecompOne.Runtime.Assets;
using Kf2.Settings;

namespace Kf2.Remaster;

/// <summary>
/// Layering: the other packs under <c>packs/</c> that carry a <c>remaster/</c>
/// directory, beneath the working pack. From the bottom: the game, each pack in
/// upstream's priority order, the working pack on top. Each layer overrides those
/// below it per key -- a material by name, a tile half by its key, a light or a prop by
/// its name, a record by its number, a texture rule by its key -- and an entry with
/// <c>"removed": true</c> (for a material, <c>null</c>) takes a key below it away.
///
/// The documents the features and the editor read are the merge. Every edit therefore
/// works as it did; <see cref="Save"/> writes only what differs from the layers below,
/// so the working pack holds the author's changes and nothing inherited. An area
/// document authored against another fingerprint than the one above it is set aside
/// for that area. See "Versions, layers and removal" in docs/REMASTER.md.
/// </summary>
public static partial class Pack
{
    public sealed class Layer
    {
        public required string Id, Name, Path;
        public int Priority;
        public bool Enabled;
        public string? Error;
        public int Documents;
        public readonly List<string> SetAside = new();
        internal Set? Docs;
    }

    static List<Layer> _layers = new();

    /// <summary>The working pack's removals, kept whether or not a layer below holds the
    /// key now, so switching a layer off and on again does not lose one. A removal goes
    /// when the working pack puts an entry of that key back.</summary>
    static readonly Dictionary<(string Kind, int Area, string List), Dictionary<string, JsonObject>> _removals = new();
    static readonly HashSet<string> _removedMaterials = new();

    /// <summary>Note the removals a working set holds; called before it is merged.</summary>
    static void NoteRemovals(Set work)
    {
        _removals.Clear();
        _removedMaterials.Clear();
        foreach (var (name, node) in (JsonObject)work.Materials["materials"]!)
            if (node == null) _removedMaterials.Add(name);
        foreach (var (kind, lists) in Collections)
            foreach (var (area, doc) in Docs(work, kind))
                foreach (var c in lists)
                    foreach (var n in doc[c] as JsonArray ?? [])
                        if (n is JsonObject o && Removed(o) && EntryKey(c, o) is { } k)
                        {
                            if (!_removals.TryGetValue((kind, area, c), out var d)) _removals[(kind, area, c)] = d = new();
                            d[k] = (JsonObject)o.DeepClone();
                        }
        var texs = new Dictionary<string, JsonObject>();
        foreach (var n in work.Textures["textures"] as JsonArray ?? [])
            if (n is JsonObject o && Removed(o) && EntryKey("textures", o) is { } k) texs[k] = (JsonObject)o.DeepClone();
        if (texs.Count > 0) _removals[("textures", 0, "textures")] = texs;
    }

    /// <summary>The merge of every enabled layer below the working pack.</summary>
    static Set _base = Set.Empty();

    /// <summary>The packs found under the working pack, lowest first.</summary>
    public static IReadOnlyList<Layer> Layers => _layers;

    static string LayerKey(string id) => "kf2.remaster.layer." + id;

    /// <summary>Switch a layer and merge again; the working pack's unsaved edits are kept
    /// (as what they change over the old layers), though not their undo.</summary>
    public static void SetLayerEnabled(string id, bool on)
    {
        PatchSettings.Set(LayerKey(id), on);
        var work = WorkingDiff();
        NoteRemovals(work);
        bool dirty = Dirty;
        foreach (var l in _layers)
            if (l.Id == id && l.Error == null) l.Enabled = on;
        foreach (var l in _layers) l.SetAside.Clear();
        _base = BuildBase(_layers);
        WorkSetAside.Clear();
        _set = Merge(_base, work, "the working pack", WorkSetAside);
        _undo.Clear();
        _redo.Clear();
        Dirty = dirty;
        Version++;
    }

    /// <summary>The working pack as <see cref="Save"/> would write it.</summary>
    static Set WorkingDiff()
    {
        var s = Set.Empty();
        s.Materials = DiffMaterials(_base, _set);
        s.Textures = DiffTextures(_base, _set);
        foreach (var kind in Collections.Keys)
            foreach (var (area, doc) in Docs(_set, kind))
                if (DiffDoc(kind, area, doc) is { } d) Docs(s, kind)[area] = d;
        return s;
    }

    public static string PacksDir => System.IO.Path.GetDirectoryName(Root) ?? System.IO.Path.GetFullPath("packs");

    /// <summary>Every pack beside the working pack with a <c>remaster/</c> directory,
    /// read. Upstream's own list is not used: it is filled when the CD comes up, which
    /// can be after the remaster loads, and it keeps no enable state of its own.</summary>
    static List<Layer> ReadLayers()
    {
        var found = new List<Layer>();
        string dir = PacksDir;
        if (!Directory.Exists(dir)) return found;
        var paths = Directory.EnumerateDirectories(dir).Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))
            .Concat(Directory.EnumerateFiles(dir, "*.zip"));
        foreach (var path in paths)
        {
            if (string.Equals(System.IO.Path.GetFullPath(path), Root, StringComparison.Ordinal)) continue;
            var pack = AssetPack.Open(path, out var err);
            if (pack == null) continue;
            var files = RemasterFiles(path);
            if (files.Count == 0) continue;
            var l = new Layer
            {
                Id = pack.Id, Name = pack.DisplayName, Path = path, Priority = pack.Manifest.Priority,
                Enabled = PatchSettings.Get(LayerKey(pack.Id), true),
            };
            if (!pack.TargetsGame(GameId)) { l.Error = "targets another game"; l.Enabled = false; }
            else
                try
                {
                    l.Docs = ReadSet(files);
                    l.Documents = files.Count;
                }
                catch (Exception e) { l.Error = e.Message; }
            found.Add(l);
        }
        found.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : string.CompareOrdinal(a.Id, b.Id));
        return found;
    }

    /// <summary>The pack's <c>remaster/**.json</c>, relative path to text.</summary>
    static Dictionary<string, string> RemasterFiles(string path)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var e in zip.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (!name.StartsWith("remaster/", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)) continue;
                using var r = new StreamReader(e.Open());
                files[name] = r.ReadToEnd();
            }
        }
        else if (Directory.Exists(System.IO.Path.Combine(path, "remaster")))
            foreach (var f in Directory.EnumerateFiles(System.IO.Path.Combine(path, "remaster"), "*.json", SearchOption.AllDirectories))
                files[System.IO.Path.GetRelativePath(path, f).Replace('\\', '/')] = File.ReadAllText(f);
        return files;
    }

    /// <summary>Documents from their text, by the same layout the working pack has.</summary>
    static Set ReadSet(Dictionary<string, string> files)
    {
        var s = Set.Empty();
        foreach (var (rel, text) in files)
        {
            var parts = rel.Split('/');
            if (parts.Length == 2 && parts[1] == "materials.json") s.Materials = Migrate(ParseText(text, rel), "materials");
            else if (parts.Length == 2 && parts[1] == "textures.json") s.Textures = Migrate(ParseText(text, rel), "textures");
            else if (parts.Length == 4 && parts[1] == "areas" && int.TryParse(parts[2], out int area))
            {
                var (docs, collection) = KindOf(s, parts[3]);
                if (docs != null) docs[area] = Migrate(ParseText(text, rel), collection!);
            }
        }
        return s;
    }

    static (Dictionary<int, JsonObject>?, string?) KindOf(Set s, string file) => file switch
    {
        "surfaces.json" => (s.Surfaces, "tiles"),
        "lights.json" => (s.Lights, "lights"),
        "atmosphere.json" => (s.Atmosphere, "records"),
        "level.json" => (s.Level, "halves"),
        "props.json" => (s.Props, "props"),
        _ => (null, null),
    };

    static JsonObject ParseText(string text, string what)
    {
        var node = JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions
        {
            CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true,
        });
        return node as JsonObject ?? throw new InvalidDataException($"{what}: not a JSON object");
    }

    // ---- the merge -------------------------------------------------------------

    /// <summary>The per-key identity of an entry in each collection, or null for an
    /// entry that has none (it is kept as it is).</summary>
    static string? EntryKey(string collection, JsonObject o) => collection switch
    {
        "tiles" or "halves" => Int(o["x"]) is { } x && Int(o["z"]) is { } z ? $"{x}:{z}:{(Str(o["half"]) == "upper" ? 1 : 0)}" : null,
        "meshes" => Int(o["mesh"])?.ToString(),
        "models" => Int(o["model"]) is { } m ? $"{Str(o["kind"])}:{m}" : null,
        "lights" or "props" => Str(o["name"]),
        "records" => Str(o["record"]) ?? Int(o["record"])?.ToString(),
        "textures" => Str(o["index"]) is { } i ? $"{i}:{Str(o["clut"])}" : null,
        _ => null,
    };

    static readonly Dictionary<string, string[]> Collections = new()
    {
        ["surfaces"] = ["tiles", "meshes", "models"],
        ["lights"] = ["lights"],
        ["atmosphere"] = ["records"],
        ["level"] = ["halves"],
        ["props"] = ["props"],
    };

    static bool Removed(JsonObject o) => o["removed"] is JsonValue v && v.TryGetValue(out bool b) && b;

    /// <summary>A keyed list with an upper one over it: the lower's entries in order,
    /// each replaced by the upper's of the same key, then the upper's new ones; a
    /// removal takes the key out.</summary>
    static JsonArray MergeList(string collection, JsonArray? lower, JsonArray? upper)
    {
        var result = new List<JsonObject>();
        var at = new Dictionary<string, int>();
        void Put(JsonObject o)
        {
            string? k = EntryKey(collection, o);
            if (k != null && at.TryGetValue(k, out int i)) result[i] = o;
            else
            {
                if (k != null) at[k] = result.Count;
                result.Add(o);
            }
        }
        foreach (var n in lower ?? []) if (n is JsonObject o && !Removed(o)) Put((JsonObject)o.DeepClone());
        foreach (var n in upper ?? []) if (n is JsonObject o) Put((JsonObject)o.DeepClone());
        return new JsonArray(result.Where(o => !Removed(o)).Select(o => (JsonNode)o).ToArray());
    }

    /// <summary>One set over another. <paramref name="upperName"/> names the upper layer
    /// in what is set aside.</summary>
    static Set Merge(Set lower, Set upper, string upperName, List<string>? setAside)
    {
        var s = Set.Empty();
        var mats = new JsonObject();
        foreach (var src in new[] { (JsonObject)lower.Materials["materials"]!, (JsonObject)upper.Materials["materials"]! })
            foreach (var (name, node) in src)
            {
                if (node is JsonObject o) mats[name] = o.DeepClone();
                else mats.Remove(name);
            }
        s.Materials = (JsonObject)upper.Materials.DeepClone();
        s.Materials["materials"] = mats;
        s.Textures = (JsonObject)upper.Textures.DeepClone();
        s.Textures["textures"] = MergeList("textures", lower.Textures["textures"] as JsonArray, upper.Textures["textures"] as JsonArray);

        foreach (var (kind, lists) in Collections)
        {
            var lo = Docs(lower, kind);
            var up = Docs(upper, kind);
            var into = Docs(s, kind);
            foreach (int area in lo.Keys.Union(up.Keys))
            {
                lo.TryGetValue(area, out var l);
                up.TryGetValue(area, out var u);
                if (l == null || u == null) { into[area] = Strip((JsonObject)(u ?? l)!.DeepClone(), lists); continue; }
                string? lf = Str(l["fingerprint"]), uf = Str(u["fingerprint"]);
                if (lf != null && uf != null && lf != uf)
                {
                    // Another mastering's area: the upper layer's stands alone.
                    setAside?.Add($"area {area} {kind}: authored against {lf}, under {upperName}'s {uf}");
                    into[area] = Strip((JsonObject)u.DeepClone(), lists);
                    continue;
                }
                var d = (JsonObject)u.DeepClone();
                if (uf == null && lf != null) d["fingerprint"] = lf;
                foreach (var c in lists)
                {
                    var merged = MergeList(c, l[c] as JsonArray, u[c] as JsonArray);
                    if (merged.Count > 0 || d[c] != null) d[c] = merged;
                }
                into[area] = d;
            }
        }
        return s;
    }

    /// <summary>A document with its removals taken out, as a merge leaves it.</summary>
    static JsonObject Strip(JsonObject d, string[] lists)
    {
        foreach (var c in lists)
            if (d[c] is JsonArray a)
                d[c] = MergeList(c, null, a);
        return d;
    }

    static Dictionary<int, JsonObject> Docs(Set s, string kind) => kind switch
    {
        "surfaces" => s.Surfaces,
        "lights" => s.Lights,
        "atmosphere" => s.Atmosphere,
        "level" => s.Level,
        "props" => s.Props,
        _ => throw new ArgumentException(kind),
    };

    /// <summary>The layers below the working pack, merged in order.</summary>
    static Set BuildBase(List<Layer> layers)
    {
        var s = Set.Empty();
        foreach (var l in layers)
            if (l.Enabled && l.Docs != null)
                s = Merge(s, l.Docs, l.Name, l.SetAside);
        return s;
    }

    // ---- what the working pack holds: the difference -------------------------------

    /// <summary>What <paramref name="top"/> changes over <paramref name="below"/>: every
    /// entry that differs or is new, and a removal for every key it took away.</summary>
    static JsonArray DiffList(string collection, JsonArray? below, JsonArray? top,
                              Dictionary<string, JsonObject>? removals = null)
    {
        var baseAt = new Dictionary<string, JsonObject>();
        foreach (var n in below ?? [])
            if (n is JsonObject o && EntryKey(collection, o) is { } k) baseAt[k] = o;
        var diff = new JsonArray();
        var seen = new HashSet<string>();
        foreach (var n in top ?? [])
        {
            if (n is not JsonObject o) continue;
            string? k = EntryKey(collection, o);
            if (k != null) seen.Add(k);
            if (k != null && baseAt.TryGetValue(k, out var b) && JsonNode.DeepEquals(b, o)) continue;
            diff.Add(o.DeepClone());
        }
        foreach (var (k, b) in baseAt)
            if (!seen.Contains(k)) diff.Add(Removal(collection, b));
        foreach (var (k, r) in removals ?? [])
            if (!seen.Contains(k) && !baseAt.ContainsKey(k)) diff.Add(r.DeepClone());
        return diff;
    }

    /// <summary>An entry's key fields and nothing else, marked removed.</summary>
    static JsonObject Removal(string collection, JsonObject b)
    {
        string[] keys = collection switch
        {
            "tiles" or "halves" => ["x", "z", "half"],
            "meshes" => ["mesh"],
            "models" => ["kind", "model"],
            "lights" or "props" => ["name"],
            "records" => ["record"],
            "textures" => ["index", "clut"],
            _ => [],
        };
        var o = new JsonObject();
        foreach (var k in keys)
            if (b[k] is { } v) o[k] = v.DeepClone();
        o["removed"] = true;
        return o;
    }

    static JsonObject DiffMaterials(Set below, Set top)
    {
        var b = (JsonObject)below.Materials["materials"]!;
        var t = (JsonObject)top.Materials["materials"]!;
        var d = new JsonObject();
        foreach (var (name, node) in t)
            if (b[name] is not { } was || !JsonNode.DeepEquals(was, node)) d[name] = node?.DeepClone();
        foreach (var (name, _) in b)
            if (!t.ContainsKey(name)) d[name] = null;
        foreach (var name in _removedMaterials)
            if (!t.ContainsKey(name) && !b.ContainsKey(name)) d[name] = null;
        var doc = (JsonObject)top.Materials.DeepClone();
        doc["materials"] = d;
        return doc;
    }

    static JsonObject DiffTextures(Set below, Set top)
    {
        var doc = (JsonObject)top.Textures.DeepClone();
        doc["textures"] = DiffList("textures", below.Textures["textures"] as JsonArray, top.Textures["textures"] as JsonArray,
                                   _removals.GetValueOrDefault(("textures", 0, "textures")));
        return doc;
    }

    /// <summary>An area document as the working pack keeps it, or null when it holds
    /// nothing of its own.</summary>
    static JsonObject? DiffDoc(string kind, int area, JsonObject top)
    {
        Docs(_base, kind).TryGetValue(area, out var below);
        if (below != null && Str(below["fingerprint"]) is { } bf && Str(top["fingerprint"]) is { } tf && bf != tf) below = null;
        var d = (JsonObject)top.DeepClone();
        bool any = false;
        foreach (var c in Collections[kind])
        {
            var list = DiffList(c, below?[c] as JsonArray, top[c] as JsonArray, _removals.GetValueOrDefault((kind, area, c)));
            if (list.Count > 0) { d[c] = list; any = true; }
            else if (c == Collections[kind][0]) d[c] = list;
            else d.Remove(c);
        }
        return any ? d : null;
    }
}
