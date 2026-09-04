using System.Reflection;

namespace Verdite2.Launcher.Build;

/// <summary>
/// Drives the recompiler over the player's disc: MIPS out of the image, C# into a
/// directory. About a second for the whole game (measured 0.85 s for 2099
/// functions and 170k lines), so it is not cached and is simply re-run whenever a
/// build is needed.
///
/// It runs IN PROCESS. RecompOne.Recompiler is an Exe with top-level statements,
/// which the compiler emits as Program.&lt;Main&gt;$(string[]) -- an ordinary method,
/// reachable through Assembly.EntryPoint. That matters because the alternative is
/// launching a second process, and a self-contained publish has no `dotnet` on the
/// player's machine to launch it with; shipping a second apphost to do it would
/// double the runtime in the package for no gain.
/// </summary>
static class Recompile
{
    /// <summary>
    /// Recompile <paramref name="cuePath"/> into <paramref name="outDir"/>.
    /// Throws with the recompiler's own message on failure.
    /// </summary>
    public static void Run(string cuePath, string outDir)
    {
        Directory.CreateDirectory(outDir);

        // The recompiler resolves cue, funcMap and output relative to the config
        // FILE's directory. The edited config therefore cannot live beside the
        // shipped one: an installed build's content/ is read-only -- Program Files,
        // or an AppImage's own squashfs mount, which is read-only even for root.
        //
        // So the whole config directory is staged into the data directory, which
        // keeps every relative funcMap path in the config resolving exactly as it
        // does in the repository. It is 260 KB and only copied when it has changed.
        var staged = Stage();

        var cfgPath = Path.Combine(staged, "kf2.build.json");
        File.WriteAllText(cfgPath, Rewrite(File.ReadAllText(Path.Combine(staged, "kf2.json")), cuePath, outDir));

        try { Invoke(cfgPath); }
        finally { try { File.Delete(cfgPath); } catch { } }
    }

    /// <summary>Mirror content/config into the data directory, skipping files already current.</summary>
    static string Stage()
    {
        var dst = Path.Combine(Paths.Data, "config");
        Directory.CreateDirectory(dst);

        foreach (var src in Directory.EnumerateFiles(Paths.ContentConfig, "*.json", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(Paths.ContentConfig, src));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var from = new FileInfo(src);
            var to = new FileInfo(target);
            if (to.Exists && to.Length == from.Length && to.LastWriteTimeUtc >= from.LastWriteTimeUtc) continue;

            File.Copy(src, target, overwrite: true);
        }

        return dst;
    }

    /// <summary>
    /// Point "cue" at the player's image and "output" at our build directory,
    /// leaving every address, overlay and SDK patch in the shipped config alone.
    ///
    /// Done as a string edit rather than by parsing and re-emitting, because
    /// kf2.json carries comments and trailing commas -- the recompiler's loader
    /// accepts both and System.Text.Json will not write them back. Losing the
    /// comments would not break the build, but it would silently turn the one
    /// documented copy of the overlay layout into a machine-written blob the next
    /// time anyone looked at it.
    /// </summary>
    static string Rewrite(string json, string cuePath, string outDir)
    {
        json = ReplaceStringValue(json, "cue", cuePath);
        json = ReplaceStringValue(json, "output", outDir);
        return json;
    }

    static string ReplaceStringValue(string json, string key, string value)
    {
        var needle = $"\"{key}\"";
        int at = json.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"content/config/kf2.json has no \"{key}\" entry.");

        int colon = json.IndexOf(':', at + needle.Length);
        if (colon < 0) throw new InvalidOperationException($"content/config/kf2.json: \"{key}\" has no value.");

        int open = json.IndexOf('"', colon + 1);
        if (open < 0) throw new InvalidOperationException($"content/config/kf2.json: \"{key}\" is not a string.");

        int close = json.IndexOf('"', open + 1);
        if (close < 0) throw new InvalidOperationException($"content/config/kf2.json: \"{key}\" is unterminated.");

        return json[..(open + 1)] + System.Text.Json.JsonEncodedText.Encode(value) + json[close..];
    }

    static void Invoke(string cfgPath)
    {
        var asm = typeof(RecompOne.Recompiler.Config.ConfigLoader).Assembly;
        var main = asm.EntryPoint
            ?? throw new InvalidOperationException("The recompiler assembly has no entry point.");

        object? result;
        try
        {
            result = main.Invoke(null, [new[] { cfgPath }]);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw new InvalidOperationException($"The recompiler failed: {e.InnerException.Message}", e.InnerException);
        }

        // Its Main returns an exit code rather than throwing on a bad config or a
        // missing disc, and both of those are reachable from here.
        if (result is int code && code != 0)
            throw new InvalidOperationException(
                $"The recompiler exited with code {code}. See the log for what it reported.");
    }
}
