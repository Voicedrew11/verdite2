namespace Verdite2.Launcher.Build;

/// <summary>
/// Where the launcher reads its payload from, and where everything it writes goes.
///
/// The runtime addresses every file it owns with a bare relative path --
/// ConfigManager's "settings.json" and "interface.ini", Runtime's "carda.sav" and
/// "cardb.sav", MapFog's "carda.fog" beside them, and ModLoader's
/// Path.GetFullPath("mods") with its .cache of compiled mod assemblies. All of
/// those resolve against the process working directory, which for a shortcut, a
/// desktop entry or an AppImage is wherever the launcher happened to be started
/// from, and for an installed build is a directory the player cannot write to.
///
/// So the launcher chdirs into the data directory before touching the runtime at
/// all. That is the entire fix, and it is why none of the paths above needed a
/// patch: they keep resolving relatively and land in the right place.
/// </summary>
static class Paths
{
    /// <summary>Beside the executable: read-only, and the only thing shipped.</summary>
    public static string Install { get; } = AppContext.BaseDirectory;

    public static string Content { get; } = Path.Combine(AppContext.BaseDirectory, "content");
    public static string ContentConfig { get; } = Path.Combine(AppContext.BaseDirectory, "content", "config");
    public static string ContentSrc { get; } = Path.Combine(AppContext.BaseDirectory, "content", "src");
    public static string ContentMods { get; } = Path.Combine(AppContext.BaseDirectory, "content", "mods");

    /// <summary>
    /// Per-user, writable, and stable across updates:
    ///   $VERDITE2_DATA, if set
    ///   %LOCALAPPDATA%\Verdite2
    ///   $XDG_DATA_HOME/verdite2, else ~/.local/share/verdite2
    /// Saves live here, so it deliberately does not move when the install does.
    /// </summary>
    public static string Data { get; } = ResolveData();

    /// <summary>Built game assemblies, one directory per cache key.</summary>
    public static string Builds => Path.Combine(Data, "builds");

    public static string BuildLog => Path.Combine(Data, "build.log");

    static string ResolveData()
    {
        // An explicit override, for a second install, a save directory on another
        // drive, or a test that must not write into the player's real one.
        var pinned = Environment.GetEnvironmentVariable("VERDITE2_DATA");
        if (!string.IsNullOrWhiteSpace(pinned))
            return Path.GetFullPath(pinned);

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "Verdite2");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg))
            return Path.Combine(xdg, "verdite2");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) home = Directory.GetCurrentDirectory();
        return Path.Combine(home, ".local", "share", "verdite2");
    }

    /// <summary>
    /// Make the data directory, move into it, and seed the mods the port ships.
    ///
    /// Seeding copies rather than links, and only files that are not there yet, so
    /// a player who has edited a shipped mod keeps their edit and a player who has
    /// deleted one does not get it back on every launch. mods/.cache is the
    /// ModLoader's own and is never seeded.
    /// </summary>
    public static void Prepare()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Builds);
        Directory.SetCurrentDirectory(Data);

        if (!Directory.Exists(ContentMods)) return;

        var seeded = Path.Combine(Data, ".mods-seeded");
        if (File.Exists(seeded)) return;

        foreach (var src in Directory.EnumerateFiles(ContentMods, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(ContentMods, src);
            var dst = Path.Combine(Data, "mods", rel);
            if (File.Exists(dst)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
        }

        File.WriteAllText(seeded, "");
    }
}
