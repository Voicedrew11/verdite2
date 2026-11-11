using RecompOne.Runtime.Config;
using RecompOne.Runtime.Events;

namespace Verdite.Core;

/// <summary>
/// Which language a run plays in: <c>{Tag}_LANGUAGE</c> (ja or en) when set, else the
/// stored <see cref="Key"/>, else English. The settings store is loaded inside
/// <c>Entry.Run</c>, after Program.cs installs, so the stored value is read only from
/// <see cref="RuntimeReadyEvent"/> on; the variable decides at once. A change made in a
/// run is kept for the next start and changes nothing in this one.
/// </summary>
public static class LanguageChoice
{
    /// <summary>"ja", or none for English. Not under the game's own prefix: the launcher's
    /// first-run step writes it before setup, and onboarding's "existing" prefix would then
    /// take a new player for one who has played.</summary>
    public const string Key = "verdite.language";

    /// <summary>The Japanese disc the launcher's first-run step was pointed at.</summary>
    public const string JapaneseDiscKey = "verdite.language.jpdisc";

    /// <summary>Set once the launcher has asked, so a player is asked only once.</summary>
    public const string AskedKey = "verdite.language.asked";

    public const string Japanese = "ja", English = "en";

    static readonly object Gate = new();
    static volatile bool _storeReady;
    static bool _listening;
    static bool? _japanese;

    /// <summary>The variable's value, lowercased, or null when unset.</summary>
    public static string? Variable => Game.Env("LANGUAGE")?.Trim().ToLowerInvariant() is { Length: > 0 } v ? v : null;

    /// <summary>Starts listening for the store; call from an Install before Entry.Run.</summary>
    public static void Listen()
    {
        lock (Gate)
        {
            if (_listening) return;
            _listening = true;
        }
        Event.AddListener<RuntimeReadyEvent>(_ => _storeReady = true);
    }

    /// <summary>
    /// Whether this run is Japanese. The variable decides at once. The stored value waits
    /// for the store: an open of the disc before it (the card icon's, from Program.cs) is
    /// told false and decides nothing.
    /// </summary>
    public static bool IsJapanese()
    {
        lock (Gate)
        {
            if (_japanese is { } known) return known;
            if (Variable is { } variable) return (_japanese = variable == Japanese).Value;
            if (!_storeReady) return false;
            _japanese = Stored() == Japanese;
            return _japanese.Value;
        }
    }

    /// <summary>The stored language, lowercased, or English when none is stored.</summary>
    public static string Stored()
    {
        return ConfigManager.View.Values.TryGetValue(Key, out var stored) && !string.IsNullOrWhiteSpace(stored)
            ? stored.Trim().ToLowerInvariant()
            : English;
    }

    /// <summary>The stored Japanese disc path, or null.</summary>
    public static string? StoredJapaneseDisc()
    {
        return ConfigManager.View.Values.TryGetValue(JapaneseDiscKey, out var stored) && !string.IsNullOrWhiteSpace(stored)
            ? stored
            : null;
    }
}
