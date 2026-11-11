using System.Text;
using RecompOne.Runtime.Cdrom;

namespace Verdite.Core;

/// <summary>
/// Finds the player's second disc (the Japanese release, for the language option) and
/// reads a disc's volume id. Opens images by their format directly, never through
/// <see cref="DiscImage.Open"/>, so a <see cref="DiscImage.Decorate"/> hook does not see them.
/// </summary>
public static class DiscFinder
{
    /// <summary>The subfolders of the US disc's folder that are searched too.</summary>
    public static readonly string[] JapaneseFolders = ["Japanese discs", "japanese"];

    /// <summary>The ISO 9660 primary volume descriptor, and where its volume id lies in it.</summary>
    const int PrimaryVolumeSector = 16;
    const int VolumeIdAt = 40;
    const int VolumeIdBytes = 32;
    const int SectorBytes = 2048;

    /// <summary>A disc found and identified, still open. The caller owns the image.</summary>
    public sealed record Found(IDiscImage Image, string Path, string VolumeId);

    /// <summary>
    /// The first disc whose volume id is one of <paramref name="volumeIds"/>. With
    /// <paramref name="explicitPath"/> set only it is tried. Otherwise <paramref name="savedPath"/>
    /// (the launcher's choice) first, then every .cue and .chd in the US disc's folder and in
    /// its <see cref="JapaneseFolders"/> subfolders, in ordinal order, so "(Rev 1)" comes before
    /// the bare title when both are there. Null when none is found.
    /// </summary>
    public static Found? Find(string usPath, IReadOnlyCollection<string> volumeIds, string? explicitPath, string? savedPath, string logPrefix)
    {
        foreach (var candidate in Candidates(usPath, explicitPath, savedPath))
            if (OpenIfOneOf(candidate, volumeIds, logPrefix) is { } found)
                return found;
        return null;
    }

    static IEnumerable<string> Candidates(string usPath, string? explicitPath, string? savedPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            yield return explicitPath;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(savedPath))
            yield return savedPath;

        var folder = Path.GetDirectoryName(Path.GetFullPath(usPath)) ?? ".";
        foreach (var dir in JapaneseFolders.Select(sub => Path.Combine(folder, sub)).Prepend(folder))
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir).Where(IsDiscFile).Order(StringComparer.Ordinal))
                yield return file;
        }
    }

    static bool IsDiscFile(string path)
    {
        return path.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".chd", StringComparison.OrdinalIgnoreCase);
    }

    static Found? OpenIfOneOf(string path, IReadOnlyCollection<string> volumeIds, string logPrefix)
    {
        IDiscImage image;
        try
        {
            image = OpenByFormat(path);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{logPrefix}: cannot open {path}: {e.Message}");
            return null;
        }

        string id;
        try
        {
            id = VolumeId(image);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            image.Dispose();
            Console.Error.WriteLine($"{logPrefix}: cannot read {path}'s volume id: {e.Message}");
            return null;
        }

        if (volumeIds.Contains(id))
            return new Found(image, path, id);
        image.Dispose();
        return null;
    }

    /// <summary>Opens a disc by its format, not through DiscImage.Open, so the hook does not see it.</summary>
    public static IDiscImage OpenByFormat(string path)
    {
        return DiscImage.Detect(path) switch
        {
            DiscFormat.Chd => ChdImage.Open(path),
            DiscFormat.CueBin => CueBinImage.Open(path),
            _ => throw new NotSupportedException($"not a disc image: {path}"),
        };
    }

    /// <summary>The volume id in the primary volume descriptor, sector 16, without its padding.</summary>
    public static string VolumeId(IDiscImage image)
    {
        var sector = image.ReadSectorData(PrimaryVolumeSector, SectorBytes);
        return Encoding.ASCII.GetString(sector, VolumeIdAt, VolumeIdBytes).TrimEnd();
    }
}
