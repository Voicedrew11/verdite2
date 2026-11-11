using System;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime.Cdrom;

namespace Kf2.Japanese;

/// <summary>
/// The Japanese King's Field II's text and pictures over the US King's Field's program,
/// for KF2_LANGUAGE=ja. The US program and all the gameplay data stay; the Japanese disc
/// (the player's own: SLPS-00069, or the PlayStation the Best SLPS-91003, either revision)
/// supplies the parts that are words and pictures.
///
///     KF2_LANGUAGE=ja        the layer on; en does nothing at all
///     KF2_LANGUAGE unset     the stored verdite.language decides (PORT SETTINGS ▸ GAMEPLAY)
///     KF2_JP_DISC=path       the Japanese .cue or .chd; else the launcher's saved one, then
///                            the US disc's folder and its "Japanese discs" subfolder
///     KF2_JP_PROBE=1         log each file's plan, and a served-sector count every 5 s
///                            when it changes
///     KF2_JP_DRYRUN=1        build the plan (and the menu's words), log it, and exit: 0 if everything resolved, 1 if not
///                            before the game starts (1 when it fails); a self-check
///
/// Installed before the game starts, it sets <see cref="DiscImage.Decorate"/>. Each US
/// image the runtime opens is wrapped in Verdite Core's MixedDiscImage, the plan built
/// once, at the first, from the Japanese disc and the US sectors. An open before the
/// settings store is loaded (the card icon's, from Program.cs) passes through unless
/// KF2_LANGUAGE=ja. A disc whose volume id is not SLUS-00158 is passed through. If no
/// Japanese disc is found, or the mix fails, one [KF2] line says so and the game plays
/// in English.
/// </summary>
public static class JapaneseDisc
{
    const string UsVolumeId = "SLUS-00158";

    /// <summary>The original release and PlayStation the Best (both revisions carry the second id).</summary>
    static readonly string[] JapaneseVolumeIds = ["SLPS-00069", "SLPS-91003"];

    const string Log = "[KF2] japanese";

    static readonly object Gate = new();
    static string? _jpDiscPath;
    static bool _dryRun, _probe, _planTried;
    static Plan? _plan;
    static volatile bool _active;
    static volatile DiscFs? _usFiles, _jpFiles;

    /// <summary>True once the layer is over the US disc: the run is in Japanese.</summary>
    public static bool Active => _active;

    /// <summary>The Japanese disc's volume id, once found.</summary>
    public static string? JapaneseVolumeId { get; private set; }

    public static bool Probe => _probe;

    public static bool DryRun => _dryRun;

    /// <summary>A whole file from the Japanese disc ("GAME.EXE", "CD/COM/FDAT.T"), or null
    /// when no Japanese disc is mixed in or the file is not there.</summary>
    public static byte[]? ReadJapaneseFile(string path) => ReadWhole(_jpFiles, path);

    /// <summary>A whole file from the US disc, under the same rules.</summary>
    public static byte[]? ReadUsFile(string path) => ReadWhole(_usFiles, path);

    static double? _choice;

    /// <summary>The language the settings page shows and keeps: 1 Japanese, 0 English. It starts
    /// as this run started, and a change is kept for the next start; it does not change the
    /// layer in this run.</summary>
    public static double Choice
    {
        get
        {
            lock (Gate)
            {
                _choice ??= (LanguageChoice.Variable ?? LanguageChoice.Stored()) == LanguageChoice.Japanese ? 1 : 0;
                return _choice.Value;
            }
        }
        set { lock (Gate) _choice = value; }
    }

    /// <summary>Called when the layer is in place, with both discs' file systems: the menu's
    /// words are built from them (in a dry run, before it exits).</summary>
    public static event Action? Mixed;

    static byte[]? ReadWhole(DiscFs? disc, string path)
    {
        if (disc is null) return null;
        try
        {
            lock (disc.Image) return disc.ReadFile(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"{Log}: cannot read {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>Reads KF2_LANGUAGE and sets the disc hook unless it is en.</summary>
    public static void Install()
    {
        var language = LanguageChoice.Variable;
        if (language == LanguageChoice.English) return;
        if (language is not null && language != LanguageChoice.Japanese)
        {
            Console.Error.WriteLine($"{Log}: KF2_LANGUAGE={language} is not ja or en; the game is in English");
            return;
        }

        _jpDiscPath = Verdite.Core.Game.Env("JP_DISC");
        _dryRun = Verdite.Core.Game.Env("JP_DRYRUN") == "1";
        _probe = Verdite.Core.Game.Env("JP_PROBE") == "1";
        LanguageChoice.Listen();
        DiscImage.Decorate = Wrap;
    }

    static IDiscImage Wrap(IDiscImage image, string path)
    {
        if (!LanguageChoice.IsJapanese()) return image;
        string id;
        try { id = DiscFinder.VolumeId(image); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { return image; }
        if (id != UsVolumeId) return image;

        Plan? plan;
        lock (Gate)
        {
            if (!_planTried)
            {
                _planTried = true;
                _plan = BuildPlan(path);
            }
            plan = _plan;
        }

        if (plan is null) return image;
        if (!_active)
        {
            _active = true;
            Mixed?.Invoke();
        }
        return new MixedDiscImage(image, plan.Jp, plan.Overrides, plan.Streams, plan.Served);
    }

    static Plan? BuildPlan(string usPath)
    {
        var found = DiscFinder.Find(usPath, JapaneseVolumeIds, _jpDiscPath, LanguageChoice.StoredJapaneseDisc(), Log);
        if (found is null)
        {
            Console.Error.WriteLine($"{Log}: no Japanese King's Field II ({string.Join(" or ", JapaneseVolumeIds)}) found beside {usPath}; the game is in English. Set KF2_JP_DISC to its .cue or .chd.");
            return Failed();
        }

        try
        {
            return MixPlan(usPath, found);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            found.Image.Dispose();
            Console.Error.WriteLine($"{Log}: {e.Message}; the game is in English");
            return Failed();
        }
    }

    static Plan MixPlan(string usPath, DiscFinder.Found found)
    {
        // The menu reads both discs' files later, from the game thread, while the CD thread
        // reads the images. The Japanese image is shared, and every read of it locks on it
        // (MixedDiscImage does the same); the US files come from an image of their own.
        var usFs = DiscFs.FromImage(DiscFinder.OpenByFormat(usPath));
        var jpFs = DiscFs.FromImage(found.Image);
        var notes = new List<string>();
        Dictionary<int, byte[]> overrides;
        lock (found.Image) overrides = JapaneseMix.BuildSectorOverrides(usFs, jpFs, notes);
        var streams = JapaneseMovies.BuildStreamRanges(usFs, jpFs, notes);
        if (_probe || _dryRun)
            foreach (var note in notes) Console.WriteLine($"{Log}: {note}");

        JapaneseVolumeId = found.VolumeId;
        _usFiles = usFs;
        _jpFiles = jpFs;
        Console.WriteLine($"{Log}: {found.VolumeId} from {Path.GetFileName(found.Path)} over {UsVolumeId}: {overrides.Count} sector(s) of pictures replaced, {streams.Count} video(s) served from it");
        var served = _probe ? new MixedDiscImage.ServedCount(Log) : null;
        return new Plan(found.Image, overrides, streams, served);
    }

    /// <summary>The dry run ends here once the menu's words are built: 0 when they were.</summary>
    public static void EndDryRun(bool ok)
    {
        if (!_dryRun) return;
        Console.WriteLine($"{Log}: KF2_JP_DRYRUN=1, exiting before the game starts");
        Environment.Exit(ok ? 0 : 1);
    }

    /// <summary>In a dry run a failure exits 1 rather than letting the game start in English.</summary>
    static Plan? Failed()
    {
        if (_dryRun) Environment.Exit(1);
        return null;
    }

    sealed record Plan(IDiscImage Jp, IReadOnlyDictionary<int, byte[]> Overrides, IReadOnlyList<StreamRange> Streams, MixedDiscImage.ServedCount? Served);
}
