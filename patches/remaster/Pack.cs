using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kf2.Remaster;

/// <summary>
/// The working pack: the documents the editor writes and the features read.
///
/// A remaster pack is an upstream asset pack (<c>pack.json</c>) with a
/// <c>remaster/</c> directory beside it: <c>materials.json</c>, the named material
/// library, and <c>areas/&lt;n&gt;/surfaces.json</c>, tile halves to material names.
/// Documents are kept as JSON trees, so a field this version does not know survives a
/// round trip. Only the working pack is read in this phase; layering other packs
/// under it is later. See "Data model and file format" in docs/REMASTER.md.
///
/// Everything here runs on the game thread except the file watch, which parses on
/// its own thread and hands the result over at the next <see cref="Poll"/>.
/// </summary>
public static class Pack
{
    public const int FormatVersion = 1;
    public const string GameId = "SLUS-00158";

    public static string Root { get; private set; } = Path.GetFullPath(Path.Combine("packs", "working"));
    static string RemasterDir => Path.Combine(Root, "remaster");
    static string MaterialsPath => Path.Combine(RemasterDir, "materials.json");
    static string SurfacesPath(int area) => Path.Combine(RemasterDir, "areas", area.ToString(), "surfaces.json");

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Bumped on every change, loaded or edited; a feature re-applies when it moves.</summary>
    public static int Version { get; private set; }

    public static bool Dirty { get; private set; }
    public static string? LastError { get; private set; }
    public static DateTime? SavedAt { get; private set; }

    static Set _set = Set.Empty();

    sealed class Set
    {
        public JsonObject Materials = null!;
        public readonly Dictionary<int, JsonObject> Surfaces = new();

        public static Set Empty() => new() { Materials = NewMaterials() };
    }

    public static void Configure(string? root)
    {
        if (!string.IsNullOrWhiteSpace(root)) Root = Path.GetFullPath(root);
    }

    // ---- load, save, watch -------------------------------------------------

    public static void Load()
    {
        try
        {
            _set = Read();
            LastError = null;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Console.Error.WriteLine($"[KF2] remaster: cannot read {Root}: {e.Message}");
        }
        Dirty = false;
        _undo.Clear();
        _redo.Clear();
        Version++;
    }

    static Set Read()
    {
        var s = Set.Empty();
        if (File.Exists(MaterialsPath)) s.Materials = Migrate(ParseObject(MaterialsPath), "materials");
        var areas = Path.Combine(RemasterDir, "areas");
        if (Directory.Exists(areas))
            foreach (var dir in Directory.EnumerateDirectories(areas))
            {
                if (!int.TryParse(Path.GetFileName(dir), out int area)) continue;
                var path = Path.Combine(dir, "surfaces.json");
                if (File.Exists(path)) s.Surfaces[area] = Migrate(ParseObject(path), "tiles");
            }
        return s;
    }

    static JsonObject ParseObject(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node as JsonObject ?? throw new InvalidDataException($"{path}: not a JSON object");
    }

    /// <summary>Version 1 is the only one; a newer file is read as far as it goes.</summary>
    static JsonObject Migrate(JsonObject doc, string collection)
    {
        int v = Int(doc["formatVersion"]) ?? FormatVersion;
        if (v > FormatVersion)
            Console.Error.WriteLine($"[KF2] remaster: a formatVersion {v} document read by version {FormatVersion}; unknown fields are kept");
        if (collection == "materials" && doc["materials"] is not JsonObject) doc["materials"] = new JsonObject();
        if (collection == "tiles" && doc["tiles"] is not JsonArray) doc["tiles"] = new JsonArray();
        return doc;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(RemasterDir);
            WriteManifest();
            _set.Materials["formatVersion"] = FormatVersion;
            Write(MaterialsPath, _set.Materials);
            foreach (var (area, doc) in _set.Surfaces)
            {
                doc["formatVersion"] = FormatVersion;
                Directory.CreateDirectory(Path.GetDirectoryName(SurfacesPath(area))!);
                Write(SurfacesPath(area), doc);
            }
            Dirty = false;
            LastError = null;
            SavedAt = DateTime.Now;
            _ignoreUntil = Environment.TickCount64 + 1000;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Console.Error.WriteLine($"[KF2] remaster: cannot save {Root}: {e.Message}");
        }
    }

    static void Write(string path, JsonObject doc)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, doc.ToJsonString(Indented) + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Upstream's manifest, written once, so the folder is a pack its loader
    /// accepts; the working pack carries no textures of its own yet.</summary>
    static void WriteManifest()
    {
        var path = Path.Combine(Root, "pack.json");
        if (File.Exists(path)) return;
        var doc = new JsonObject
        {
            ["formatVersion"] = 1,
            ["id"] = "working",
            ["name"] = "Working pack",
            ["description"] = "What the remaster editor saves. Holds identifiers and the author's own values, never disc data.",
            ["priority"] = 1000,
            ["game"] = new JsonObject { ["id"] = GameId, ["strict"] = true },
        };
        File.WriteAllText(path, doc.ToJsonString(Indented) + "\n");
    }

    static FileSystemWatcher? _watch;
    static volatile Set? _pending;
    static long _ignoreUntil;
    static long _changedAt;

    /// <summary>Watch the remaster directory; a change is parsed off the game thread
    /// and swapped in at the next <see cref="Poll"/>. A save of ours is ignored.</summary>
    public static void Watch()
    {
        try
        {
            Directory.CreateDirectory(RemasterDir);
            _watch = new FileSystemWatcher(RemasterDir, "*.json") { IncludeSubdirectories = true };
            _watch.Changed += (_, _) => Touched();
            _watch.Created += (_, _) => Touched();
            _watch.Deleted += (_, _) => Touched();
            _watch.Renamed += (_, _) => Touched();
            _watch.EnableRaisingEvents = true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] remaster: not watching {RemasterDir}: {e.Message}");
        }
    }

    static void Touched()
    {
        long now = Environment.TickCount64;
        if (now < Interlocked.Read(ref _ignoreUntil)) return;
        Interlocked.Exchange(ref _changedAt, now);
        // An editor writes a file in several steps; parse once it has been quiet.
        Task.Delay(250).ContinueWith(_ =>
        {
            if (Environment.TickCount64 - Interlocked.Read(ref _changedAt) < 240) return;
            try { _pending = Read(); }
            catch (Exception e) { LastError = e.Message; }
        });
    }

    /// <summary>On the game thread, once a frame.</summary>
    public static void Poll()
    {
        var p = _pending;
        if (p == null) return;
        _pending = null;
        if (Dirty)
        {
            LastError = "the files changed on disk while there were unsaved edits; kept the edits (Reload to take the files)";
            return;
        }
        _set = p;
        LastError = null;
        _undo.Clear();
        _redo.Clear();
        Version++;
        Console.WriteLine($"[KF2] remaster: reloaded {Root}");
    }

    // ---- materials ---------------------------------------------------------

    public readonly record struct Material(string Name, float Reflectivity, float F0);

    static JsonObject NewMaterials() => new() { ["formatVersion"] = FormatVersion, ["materials"] = new JsonObject() };

    static JsonObject MaterialsObj => (JsonObject)_set.Materials["materials"]!;

    public static IEnumerable<Material> Materials()
    {
        foreach (var (name, node) in MaterialsObj)
            if (node is JsonObject o)
                yield return new Material(name, Num(o, "reflectivity"), Num(o, "f0"));
    }

    public static bool HasMaterial(string name) => MaterialsObj[name] is JsonObject;

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out int i) ? i : null;

    static float Num(JsonObject o, string field)
        => o[field] is JsonValue v && v.TryGetValue(out double d) ? (float)d : 0f;

    public static void AddMaterial(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || HasMaterial(name)) return;
        Edit($"add material {name}",
            () => MaterialsObj[name] = new JsonObject { ["reflectivity"] = 0.3, ["f0"] = 0.04 },
            () => MaterialsObj.Remove(name));
    }

    /// <summary>Removing a material leaves the tiles that name it naming nothing, and
    /// they are kept: undo puts it back and they resolve again.</summary>
    public static void RemoveMaterial(string name)
    {
        if (MaterialsObj[name] is not JsonObject o) return;
        var copy = o.DeepClone();
        Edit($"remove material {name}", () => MaterialsObj.Remove(name), () => MaterialsObj[name] = copy.DeepClone());
    }

    public static float GetField(string name, string field)
        => MaterialsObj[name] is JsonObject o ? Num(o, field) : 0f;

    /// <summary>A field change. <paramref name="from"/> is what an undo returns to, so a
    /// slider dragged over many frames is one entry.</summary>
    public static void SetField(string name, string field, float value, float? from = null)
    {
        if (MaterialsObj[name] is not JsonObject) return;
        float old = from ?? GetField(name, field);
        Edit($"{name}.{field} = {value:0.###}",
            () => ((JsonObject)MaterialsObj[name]!)[field] = Math.Round(value, 4),
            () => { if (MaterialsObj[name] is JsonObject o) o[field] = Math.Round(old, 4); });
    }

    /// <summary>A live change while a slider is held, with no undo entry of its own.</summary>
    public static void Preview(string name, string field, float value)
    {
        if (MaterialsObj[name] is not JsonObject o) return;
        o[field] = Math.Round(value, 4);
        Dirty = true;
        Version++;
    }

    // ---- surfaces ----------------------------------------------------------

    /// <summary>An area's surfaces document, made on first write.</summary>
    static JsonObject SurfacesDoc(int area, string fingerprint)
    {
        if (_set.Surfaces.TryGetValue(area, out var doc)) return doc;
        doc = new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["area"] = area,
            ["fingerprint"] = fingerprint,
            ["tiles"] = new JsonArray(),
        };
        _set.Surfaces[area] = doc;
        return doc;
    }

    /// <summary>The fingerprint an area's documents were authored against, or null.</summary>
    public static string? AreaFingerprint(int area)
        => _set.Surfaces.TryGetValue(area, out var d) ? Str(d["fingerprint"]) : null;

    public static IEnumerable<(TileKey Key, string Material)> Tiles(int area)
    {
        if (!_set.Surfaces.TryGetValue(area, out var doc) || doc["tiles"] is not JsonArray tiles) yield break;
        foreach (var n in tiles)
        {
            if (n is not JsonObject t) continue;
            if (t["x"] is not JsonValue xv || !xv.TryGetValue(out int x)) continue;
            if (t["z"] is not JsonValue zv || !zv.TryGetValue(out int z)) continue;
            int half = Str(t["half"]) == "upper" ? TileKey.Upper : TileKey.Lower;
            if (t["material"] is not JsonValue mv || !mv.TryGetValue(out string? m) || m == null) continue;
            yield return (new TileKey(area, x, z, half), m);
        }
    }

    public static string? TileMaterial(TileKey k)
    {
        foreach (var (key, m) in Tiles(k.Area))
            if (key == k) return m;
        return null;
    }

    /// <summary>Assign a material to a tile half, or clear it with null.</summary>
    public static void SetTile(TileKey k, string? material, string fingerprint)
    {
        string? old = TileMaterial(k);
        if (old == material) return;
        Edit($"{k} = {material ?? "none"}",
            () => PutTile(k, material, fingerprint),
            () => PutTile(k, old, fingerprint));
    }

    static void PutTile(TileKey k, string? material, string fingerprint)
    {
        var tiles = (JsonArray)SurfacesDoc(k.Area, fingerprint)["tiles"]!;
        for (int i = 0; i < tiles.Count; i++)
        {
            if (tiles[i] is not JsonObject t) continue;
            if (Int(t["x"]) != k.X || Int(t["z"]) != k.Z) continue;
            if ((Str(t["half"]) == "upper" ? 1 : 0) != k.Half) continue;
            if (material == null) tiles.RemoveAt(i);
            else t["material"] = material;
            return;
        }
        if (material != null)
            tiles.Add(new JsonObject { ["x"] = k.X, ["z"] = k.Z, ["half"] = k.HalfName, ["material"] = material });
    }

    // ---- undo --------------------------------------------------------------

    sealed record Entry(string Label, Action Apply, Action Revert);

    static readonly Stack<Entry> _undo = new(), _redo = new();

    public static string? UndoLabel => _undo.TryPeek(out var e) ? e.Label : null;
    public static string? RedoLabel => _redo.TryPeek(out var e) ? e.Label : null;

    static void Edit(string label, Action apply, Action revert)
    {
        apply();
        _undo.Push(new Entry(label, apply, revert));
        _redo.Clear();
        Dirty = true;
        Version++;
    }

    public static bool Undo()
    {
        if (!_undo.TryPop(out var e)) return false;
        e.Revert();
        _redo.Push(e);
        Dirty = true;
        Version++;
        return true;
    }

    public static bool Redo()
    {
        if (!_redo.TryPop(out var e)) return false;
        e.Apply();
        _undo.Push(e);
        Dirty = true;
        Version++;
        return true;
    }
}
