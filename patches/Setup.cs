using System.Globalization;
using RecompOne.Runtime;
using RecompOne.Runtime.Host.Window;

namespace Kf2;

/// <summary>
/// The first-run setup (Verdite Core's <see cref="Onboarding"/>) run again from
/// the game: RUN SETUP on the settings page's DISPLAY page opens it over the menu,
/// and the choices are applied live, each through the setter the Settings window
/// uses for it. A key with no live setter is listed at the end as waiting for the
/// next launch. The launcher shows the same setup at first run, from the same
/// config/onboarding.json. See "Onboarding" in docs/SETTINGS.md.
/// </summary>
public static class Setup
{
    static OnboardingSpec? _spec;
    static OnboardingPopup? _popup;
    static volatile bool _opening;

    static Onboarding.Result? _last;

    public static void Install()
    {
        Onboarding.LiveApply = Apply;
        _spec = Onboarding.Load("KF2", Onboarding.Candidates("onboarding.json"));
    }

    /// <summary>From the game thread: ask the host to open setup. With no spec to run
    /// it from (a line in the log says why), <see cref="Busy"/> clears at once.</summary>
    public static void Open()
    {
        _opening = true;
        SettingsStore.OnHost(() =>
        {
            try
            {
                if (_spec is null) return;
                _popup ??= new OnboardingPopup(_spec, "KF2", inGame: true);
                PopupManager.Register(_popup);
                _popup.Open();
            }
            finally { _opening = false; }
        });
    }

    /// <summary>Asked for and not yet closed: the settings page waits on this.</summary>
    public static bool Busy => _opening || (_popup?.IsOpen ?? false);

    /// <summary>
    /// The shell's <c>setup</c>, from the game thread:
    ///   setup                       each group's tier now (-1 is set by hand) and the last apply
    ///   setup open                  open it, as RUN SETUP does
    ///   setup apply T [T T]         one tier (original, faithful, modern) for every group, or one
    ///                               per category in the spec's order; the display left alone.
    ///                               Applied on the host's next frame, so ask again for the result.
    /// </summary>
    public static string Shell(string args)
    {
        if (_spec is not { } spec) return "{\"ok\":false,\"cmd\":\"setup\",\"error\":\"no onboarding.json\"}";
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = a.Length > 0 ? a[0].ToLowerInvariant() : "state";
        switch (verb)
        {
            case "state": break;
            case "open": Open(); break;
            case "apply":
                var tiers = a.Skip(1).Select(t => Array.IndexOf(TierNames, t.ToLowerInvariant())).ToArray();
                if (tiers.Length is not (1 or 3) || tiers.Length == 3 && spec.Categories.Count != 3 || tiers.Any(t => t < 0))
                    return "{\"ok\":false,\"cmd\":\"setup\",\"error\":\"setup apply <original|faithful|modern> [two more, one per category]\"}";
                var chosen = spec.Categories.Select((c, i) => Enumerable.Repeat(tiers.Length == 1 ? tiers[0] : tiers[i], c.Groups.Count).ToArray()).ToArray();
                SettingsStore.OnHost(() => _last = Onboarding.Apply(spec, Onboarding.Sets(spec, [], chosen), "KF2"));
                break;
            default: return "{\"ok\":false,\"cmd\":\"setup\",\"error\":\"setup [open|apply ...]\"}";
        }
        var groups = spec.Categories.SelectMany(c => c.Groups.Zip(Onboarding.Detect(spec, c), (g, t) => $"\"{c.Id}.{g.Id}\":{t}"));
        var display = spec.Display.Select(f => $"\"{f.Id}\":{Onboarding.Detect(spec, f)}");
        string List(IReadOnlyList<string>? keys) => "[" + string.Join(',', (keys ?? []).Select(k => $"\"{k}\"")) + "]";
        return $"{{\"ok\":true,\"cmd\":\"setup\",\"verb\":\"{verb}\",\"open\":{(Busy ? "true" : "false")}," +
               $"\"due\":{(Onboarding.Due(spec) ? "true" : "false")},\"display\":{{{string.Join(',', display)}}}," +
               $"\"groups\":{{{string.Join(',', groups)}}},\"changed\":{List(_last?.Changed)},\"nextLaunch\":{List(_last?.NextLaunch)}}}";
    }

    static readonly string[] TierNames = ["original", "faithful", "modern"];

    /// <summary>One key, live, from what it now means. On the host thread, with the
    /// world held by the menu, as the Settings window's own controls run.</summary>
    static bool Apply(string key, string? text)
    {
        bool B(bool fallback) => text is null ? fallback : bool.TryParse(text, out var b) ? b : fallback;
        int I(int fallback) => text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : fallback;

        // The keys the game's page does not list: the Settings window's setters.
        switch (key.ToLowerInvariant())
        {
            case Analog.OnKey: Analog.Enabled = B(true); return true;
            case MenuMouse.OnKey: MenuMouse.Enabled = B(true); return true;
            case Perspective.OnKey: Perspective.SetEnabled(B(true)); return true;
            case Subpixel.OnKey: Subpixel.SetEnabled(B(true)); return true;
            case ZBuffer.OnKey: ZBuffer.SetEnabled(B(true)); return true;
            case NoDither.OnKey: NoDither.SetEnabled(B(true)); return true;
            case TrueColor.OnKey: TrueColor.SetEnabled(B(true)); return true;
            case EvenFog.OnKey: EvenFog.Enabled = B(true); return true;
            case FrameSmoothing.OnKey: FrameSmoothing.SetEnabled(B(true)); return true;
            case FrameSmoothing.PosKey: FrameSmoothing.SetPosition(B(true)); return true;
            case ObjectSmoothing.OnKey: ObjectSmoothing.SetEnabled(B(true)); return true;
            case AnimSmoothing.OnKey: AnimSmoothing.SetEnabled(B(true)); return true;
            case FluidSmoothing.OnKey: FluidSmoothing.SetEnabled(B(true)); return true;
            case AudioQuality.InterpKey: AudioQuality.SetInterpolation((SpuInterpolation)I(2)); return true;
            case AudioQuality.ReverbKey: AudioQuality.SetEnhancedReverb(I(1) == 1); return true;
            case PositionalAudio.ModeKey: PositionalAudio.Set((PositionalAudio.Mode)I(2)); return true;
            case Map.OnKey: Map.SetEnabled(B(true)); return true;
            case MapFog.OnKey: MapFog.SetEnabled(B(true)); return true;
        }

        // The game's page's own: its live setter, which is what a step there runs.
        var s = PortSettings.All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (s is null || s.AtBoot) return false;
        double value = text is null ? s.Default
            : bool.TryParse(text, out var on) ? (on ? 1 : 0)
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
            : s.Default;
        s.Apply(value);
        return true;
    }
}
