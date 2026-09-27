using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Kf2.Remaster;

/// <summary>What sharing a pack needs from it: each area document's fingerprint and
/// size for the compatibility report (<see cref="Compat"/>), and the pack as one zip.
/// See "Phase 7, the first slice" in docs/REMASTER.md.</summary>
public static partial class Pack
{
    /// <summary>One area document: which file, the fingerprint it was authored against
    /// and how many entries it holds.</summary>
    public readonly record struct AreaDocument(int Area, string Kind, string? Fingerprint, int Entries);

    /// <summary>Every area document the pack holds, by area.</summary>
    public static IEnumerable<AreaDocument> AreaDocs()
    {
        static int Count(JsonObject d, params string[] keys)
            => keys.Sum(k => d[k] is JsonArray a ? a.Count : 0);
        var all = new List<AreaDocument>();
        foreach (var (area, d) in _set.Surfaces)
            all.Add(new(area, "surfaces", Str(d["fingerprint"]), Count(d, "tiles", "meshes", "models")));
        foreach (var (area, d) in _set.Lights)
            all.Add(new(area, "lights", Str(d["fingerprint"]), Count(d, "lights")));
        foreach (var (area, d) in _set.Atmosphere)
            all.Add(new(area, "atmosphere", Str(d["fingerprint"]), Count(d, "records")));
        foreach (var (area, d) in _set.Level)
            all.Add(new(area, "level", Str(d["fingerprint"]), Count(d, "halves")));
        return all.OrderBy(d => d.Area).ThenBy(d => d.Kind, StringComparer.Ordinal);
    }

    /// <summary>Where <see cref="Export"/> writes by default: <c>exports/</c>, dated. Not
    /// <c>packs/</c>, where upstream would load it as a second pack beside this one.</summary>
    public static string DefaultExportPath
        => Path.GetFullPath(Path.Combine("exports", $"{Path.GetFileName(Root)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip"));

    /// <summary>The pack's saved files as one zip in upstream's zip-pack layout,
    /// <c>pack.json</c> at its root, so the zip dropped into <c>packs/</c> is the pack.
    /// Refused while there are unsaved edits, since the zip would not hold them.
    /// Returns the path, or throws.</summary>
    public static string Export(string? path = null)
    {
        if (Dirty) throw new InvalidOperationException("the pack has unsaved edits; save first");
        if (!File.Exists(Path.Combine(Root, "pack.json")))
            throw new InvalidOperationException($"{Root} holds no pack.json; save once first");
        path = Path.GetFullPath(path ?? DefaultExportPath);
        if (path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("the zip cannot go inside the pack it holds");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".part";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/');
                // Only what a pack is: the manifest, the remaster's documents and upstream's asset files.
                if (rel.Split('/').Any(p => p.StartsWith('.')) || rel.EndsWith(".part", StringComparison.Ordinal)) continue;
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
            }
        }
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
