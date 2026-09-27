using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecompOne.Runtime.Config;
using Verdite2.Launcher.Build;

namespace Verdite2.Launcher;

/// <summary>
/// Asks GitHub whether a newer release exists. Notify only: nothing is downloaded.
///
/// Runs once per launch on a worker thread, and reaches the network at most once a
/// day; between checks the last answer is reused from update.json in the data
/// directory, so a player who launches twice still sees the notice. See
/// "Telling the player about a new release" in docs/PACKAGING.md.
/// </summary>
static class UpdateCheck
{
    const string Api = "https://api.github.com/repos/Voicedrew11/verdite2/releases/latest";

    /// <summary>The View config key behind the Interface checkbox.</summary>
    public const string SettingKey = "Verdite2.UpdateCheck";

    static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public sealed record Release(string Tag, string Url);

    /// <summary>A newer release the player has not skipped; null until the check finishes, or if there is none.</summary>
    public static volatile Release? Available;

    static string StatePath => Path.Combine(Paths.Data, "update.json");

    public static bool Enabled
    {
        get => ConfigManager.View.GetBool(SettingKey, true);
        set => ConfigManager.View.SetBool(SettingKey, value);
    }

    /// <summary>
    /// VERDITE2_UPDATE_CHECK=0 turns it off, =force ignores the daily throttle (a
    /// skipped version stays skipped).
    /// </summary>
    public static void Start()
    {
        var env = Environment.GetEnvironmentVariable("VERDITE2_UPDATE_CHECK");
        if (env == "0") return;
        var force = string.Equals(env, "force", StringComparison.OrdinalIgnoreCase);
        if (!force && !Enabled) return;

        new Thread(() => Run(force)) { IsBackground = true, Name = "verdite2-update" }.Start();
    }

    static void Run(bool force)
    {
        try
        {
            var state = LoadState();
            var checkedAt = state["checked"]?.GetValue<DateTime>();

            if (force || checkedAt is null || DateTime.UtcNow - checkedAt.Value > Interval)
            {
                var (tag, url) = Fetch();
                state["checked"] = DateTime.UtcNow;
                state["tag"] = tag;
                state["url"] = url;
                SaveState(state);
            }

            var latest = state["tag"]?.GetValue<string>();
            var page = state["url"]?.GetValue<string>();
            if (latest is null || page is null) return;
            if (state["skipped"]?.GetValue<string>() == latest)
            {
                Console.WriteLine($"[Verdite2] {latest} is available and was skipped");
                return;
            }

            if (IsNewer(latest, Ver.Number))
            {
                Console.WriteLine($"[Verdite2] update available: {latest} (running {Ver.Number})");
                Available = new Release(latest, page);
            }
        }
        catch (Exception e)
        {
            // Offline, rate-limited, or GitHub is down: nothing to tell the player.
            Console.WriteLine($"[Verdite2] update check failed: {e.Message}");
        }
    }

    static (string Tag, string Url) Fetch()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Verdite2", Ver.Number));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        // /releases/latest skips drafts and prereleases, so a draft release.yml has
        // opened is not announced until it is published.
        var json = JsonNode.Parse(http.GetStringAsync(Api).GetAwaiter().GetResult())
                   ?? throw new InvalidDataException("empty response");
        var tag = json["tag_name"]?.GetValue<string>() ?? throw new InvalidDataException("no tag_name");
        var url = json["html_url"]?.GetValue<string>() ?? throw new InvalidDataException("no html_url");
        return (tag, url);
    }

    public static bool IsNewer(string tag, string current) =>
        Version.TryParse(tag.TrimStart('v', 'V'), out var a) &&
        Version.TryParse(current, out var b) &&
        a > b;

    /// <summary>Stop announcing this release; a later one is announced again.</summary>
    public static void Skip(string tag)
    {
        try
        {
            var state = LoadState();
            state["skipped"] = tag;
            SaveState(state);
            Console.WriteLine($"[Verdite2] skipped {tag}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Verdite2] could not record the skipped version: {e.Message}");
        }
    }

    static JsonObject LoadState()
    {
        try
        {
            if (File.Exists(StatePath) && JsonNode.Parse(File.ReadAllText(StatePath)) is JsonObject o) return o;
        }
        catch (JsonException)
        {
            // A damaged file is only a cache; start again.
        }

        return new JsonObject();
    }

    static readonly object _stateGate = new();

    static void SaveState(JsonObject state)
    {
        lock (_stateGate)
            File.WriteAllText(StatePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
