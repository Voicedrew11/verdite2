using System.Security.Cryptography;
using System.Text;
using RecompOne.Runtime.Cdrom;

namespace Verdite2.Launcher.Build;

/// <summary>
/// What identifies one built game assembly, so a second launch can skip the build
/// and a changed input cannot be served a stale one.
///
/// Three things go in, and each answers a way the cached assembly could be wrong:
///
///   - The disc's own code. Not the file's hash: an image can differ in padding,
///     track layout or the 180 MB of streamed media and still produce byte-identical
///     output. What the recompiler reads is the three executables and the nine
///     FDAT.T slices, so those are what is hashed -- two dumps that recompile the
///     same get one cache entry between them.
///
///   - The shipped sources. content/src is compiled into the assembly, so an
///     updated port must rebuild. Hashing the text catches that without asking
///     anyone to remember to bump anything.
///
///   - The launcher's version, which covers a change in how the build is done
///     rather than in what goes into it.
///
/// The absolute LBAs are deliberately NOT part of the key even though they are
/// baked into the output (Dispatcher arms an overlay swap on a CD read hitting an
/// exact sector), because they are read from the disc during the recompile itself
/// -- so a differently mastered dump of the same game produces a different
/// executable payload only if the code differs, and gets its own correct LBAs
/// either way. The recompile is under a second; the cache exists for the compile.
/// </summary>
static class BuildKey
{
    public static string Compute(string cuePath)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Add(hash, "verdite2");
        Add(hash, typeof(BuildKey).Assembly.GetName().Version?.ToString() ?? "0");

        using (var fs = CueFs.Open(cuePath))
            foreach (var file in new[] { "SYSTEM.CNF", "SLUS_001.58", "OPEN.EXE", "GAME.EXE", "END.EXE", "CD/COM/FDAT.T" })
            {
                Add(hash, file);
                try { hash.AppendData(SHA256.HashData(fs.ReadFile(file))); }
                catch { Add(hash, "<missing>"); }
            }

        foreach (var src in Sources.All())
        {
            Add(hash, Path.GetRelativePath(Paths.Content, src).Replace('\\', '/'));
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(src)));
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Length-prefixed, so "ab" + "c" and "a" + "bc" cannot hash the same. Cheap
    /// here and the sort of thing that is impossible to notice once it is wrong.
    /// </summary>
    static void Add(IncrementalHash hash, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}

/// <summary>The shipped C# the game assembly is built from, in a stable order.</summary>
static class Sources
{
    public static IEnumerable<string> All()
    {
        if (!Directory.Exists(Paths.ContentSrc)) yield break;

        var files = Directory.GetFiles(Paths.ContentSrc, "*.cs", SearchOption.AllDirectories);
        // Ordinal, so the key does not move with the host's locale.
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var f in files) yield return f;
    }
}
