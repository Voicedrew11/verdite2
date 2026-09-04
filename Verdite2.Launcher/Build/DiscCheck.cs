using RecompOne.Runtime.Cdrom;

namespace Verdite2.Launcher.Build;

/// <summary>
/// The disc validator the runtime has always had a slot for and nobody ever
/// filled.
///
/// Runtime.DiscValidator is a Func&lt;string,string?&gt; consulted by
/// WaitForValidDisc; left null, every existing file passes and any dump at all is
/// accepted. That was survivable while the only user was a developer pointing at
/// a known-good image, and it is not survivable in a release: a wrong disc here
/// does not fail, it recompiles into a game that is wrong in ways that surface
/// hours later.
///
/// Two failures are worth naming precisely rather than generically:
///
///   - King's Field II (SLUS-00255) is the game most people will reach for, and it
///     is a DIFFERENT GAME. The series was renumbered for the West: this port is
///     of King's Field (SLUS-00158), the US release of the Japanese King's Field
///     II. Every address in config/ is wrong for SLUS-00255 and it would build.
///
///   - CD/COM/FDAT.T carries the nine per-area code modules, sliced out by
///     absolute byte offset. A truncated or differently built archive would let
///     the recompile pass and then produce area modules made of whatever bytes
///     were at those offsets.
/// </summary>
static class DiscCheck
{
    public const string Serial = "SLUS-00158";
    const string BootExe = "SLUS_001.58";

    /// <summary>Files the recompile reads, and the smallest each may be.</summary>
    static readonly (string Path, uint MinSize)[] Required =
    [
        ("OPEN.EXE", 0x800),
        ("GAME.EXE", 0x800),
        ("END.EXE", 0x800),
        // FDAT.T is checked against the config rather than a constant -- see
        // FdatFloor below.
        ("CD/COM/FDAT.T", 0),
    ];

    /// <summary>Null if the image is usable, otherwise the reason it is not.</summary>
    public static string? Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "No disc image selected.";
        if (!File.Exists(path)) return $"Not found: {path}";

        CueFs fs;
        try { fs = CueFs.Open(path); }
        catch (Exception e) { return $"Could not read this image as a cue/bin pair: {e.Message}"; }

        using (fs)
        {
            string boot;
            try { boot = System.Text.Encoding.ASCII.GetString(fs.ReadFile("SYSTEM.CNF")); }
            catch { return "No SYSTEM.CNF on this disc, so it is not a PlayStation game image."; }

            if (boot.IndexOf(BootExe, StringComparison.OrdinalIgnoreCase) < 0)
                return Wrong(boot);

            foreach (var (file, min) in Required)
            {
                if (!fs.Locate(file, out _, out uint size))
                    return $"This disc is missing {file}, which the recompiler needs. The image may be incomplete.";
                uint floor = file == "CD/COM/FDAT.T" ? FdatFloor() : min;
                if (size < floor)
                    return $"{file} is {size} bytes on this disc; {Serial} has at least {floor}. The image may be truncated.";
            }
        }

        return null;
    }

    /// <summary>
    /// The end of the last area-module slice in config/kf2.json.
    ///
    /// Read out of the config rather than written here, because the two would
    /// otherwise drift silently: adding an area module past the current end would
    /// leave this check passing a disc whose FDAT.T is too short for it, and the
    /// recompile would slice whatever bytes happened to follow.
    /// </summary>
    static uint FdatFloor()
    {
        if (_fdatFloor is { } cached) return cached;

        uint end = 0;
        try
        {
            var json = File.ReadAllText(Path.Combine(Paths.ContentConfig, "kf2.json"));
            using var doc = System.Text.Json.JsonDocument.Parse(json,
                new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            foreach (var overlay in doc.RootElement.GetProperty("overlays").EnumerateArray())
            {
                if (!overlay.TryGetProperty("file", out var f)) continue;
                if (!string.Equals(f.GetString(), "CD/COM/FDAT.T", StringComparison.OrdinalIgnoreCase)) continue;

                uint offset = overlay.TryGetProperty("offset", out var o) ? o.GetUInt32() : 0;
                uint size = overlay.TryGetProperty("size", out var z) ? z.GetUInt32() : 0;
                end = Math.Max(end, offset + size);
            }
        }
        catch
        {
            // A payload we cannot parse is a broken install, not a bad disc. Fall
            // through to 0 so this check passes and the recompile reports the real
            // problem instead of blaming the player's dump.
        }

        return (_fdatFloor = end).Value;
    }

    static uint? _fdatFloor;

    /// <summary>
    /// Name the disc the player actually inserted, so the message is about their
    /// disc rather than about ours. The serial in SYSTEM.CNF is written
    /// "cdrom:\SLUS_002.55;1", i.e. the boot file name, so it is recovered from
    /// that rather than looked up.
    /// </summary>
    static string Wrong(string systemCnf)
    {
        var found = Serials(systemCnf);
        string got = found is null ? "" : $" This one is {found}.";

        if (found == "SLUS-00255")
            return "This is King's Field II (SLUS-00255), which is a different game. " +
                   "The series was renumbered for the West: this port is of King's Field " +
                   $"({Serial}), the US release of the Japanese King's Field II.";

        return $"This is not King's Field ({Serial}).{got}";
    }

    static string? Serials(string systemCnf)
    {
        foreach (var raw in systemCnf.Split('\n'))
        {
            int at = raw.IndexOf("cdrom", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            var name = raw[at..].Trim().TrimEnd('\r');
            int slash = name.LastIndexOfAny(['\\', '/', ':']);
            if (slash >= 0) name = name[(slash + 1)..];
            name = name.Split(';')[0].Trim();

            // SLUS_001.58 -> SLUS-00158
            if (name.Length == 11 && name[4] == '_' && name[8] == '.')
                return $"{name[..4]}-{name[5..8]}{name[9..]}".ToUpperInvariant();
        }
        return null;
    }
}
