using System.Globalization;
using RecompOne.Runtime;
using RecompOne.Runtime.Events;
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
        // After every feature's own listener, which is where each reads its saved value.
        if (_spec is { } spec) Event.AddListener<RuntimeReadyEvent>(_ => Check(spec));
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
    ///   setup preset original|faithful|recommended
    ///                               the welcome's Original, Faithful or Recommended choice (tiers 0, 1, 2),
    ///                               applied the same way.
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
            case "preset":
            {
                var which = a.Length == 2 ? a[1].ToLowerInvariant() : "";
                if (which is not ("original" or "faithful" or "recommended"))
                    return "{\"ok\":false,\"cmd\":\"setup\",\"error\":\"setup preset <original|faithful|recommended>\"}";
                int presetTier = which switch { "original" => 0, "faithful" => 1, _ => 2 };
                var (presetDisplay, presetTiers) = Onboarding.Preset(spec, presetTier);
                SettingsStore.OnHost(() => _last = Onboarding.Apply(spec, Onboarding.Sets(spec, presetDisplay, presetTiers), "KF2"));
                break;
            }
            default: return "{\"ok\":false,\"cmd\":\"setup\",\"error\":\"setup [open|apply ...|preset original|faithful|recommended]\"}";
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
        if (Window.TryGetValue(key, out var w))
        {
            w.Set(text);
            return true;
        }

        // The game's page's own: its live setter, which is what a step there runs.
        var s = PortSettings.All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
        if (s is null || s.AtBoot) return false;
        s.Apply(Number(text, s.Default));
        return true;
    }

    static bool B(string? text, bool fallback) => text is not null && bool.TryParse(text, out var b) ? b : fallback;

    static double Number(string? text, double fallback) =>
        text is null ? fallback
        : bool.TryParse(text, out var on) ? (on ? 1 : 0)
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
        : fallback;

    static int I(string? text, int fallback) => (int)Math.Round(Number(text, fallback));

    static string Text(bool b) => b ? "True" : "False";

    /// <summary>A key the game's page does not list: the Settings window's setter, the
    /// value it reads now, and the variables that override it (under which the
    /// value is not the saved one, so the boot check leaves it alone).</summary>
    sealed record WindowKey(Action<string?> Set, Func<string> Live, params string[] Envs);

    static readonly Dictionary<string, WindowKey> Window = new(StringComparer.OrdinalIgnoreCase)
    {
        [Analog.OnKey] = new(t => Analog.Enabled = B(t, true), () => Text(Analog.Enabled)),
        [MenuMouse.OnKey] = new(t => MenuMouse.Enabled = B(t, true), () => Text(MenuMouse.Enabled), "KF2_MENUMOUSE"),
        [MouseAim.LineKey] = new(t => MouseAim.Line = B(t, true), () => Text(MouseAim.Line), "KF2_MOUSE_AIM_LINE"),
        [MouseAim.DotKey] = new(t => MouseAim.Dot = B(t, true), () => Text(MouseAim.Dot), "KF2_MOUSE_AIM_DOT"),
        [Perspective.OnKey] = new(t => Perspective.SetEnabled(B(t, true)), () => Text(Perspective.Enabled), "KF2_PERSPECTIVE"),
        [Subpixel.OnKey] = new(t => Subpixel.SetEnabled(B(t, true)), () => Text(Subpixel.Enabled), "KF2_SUBPIXEL"),
        [ZBuffer.OnKey] = new(t => ZBuffer.SetEnabled(B(t, true)), () => Text(ZBuffer.Enabled), "KF2_ZBUFFER"),
        [NoDither.OnKey] = new(t => NoDither.SetEnabled(B(t, true)), () => Text(NoDither.Enabled), "KF2_NODITHER"),
        [TrueColor.OnKey] = new(t => TrueColor.SetEnabled(B(t, true)), () => Text(TrueColor.Enabled), "KF2_TRUECOLOR"),
        [EvenFog.OnKey] = new(t => EvenFog.Enabled = B(t, true), () => Text(EvenFog.Enabled), "KF2_EVENFOG"),
        [FrameSmoothing.OnKey] = new(t => FrameSmoothing.SetEnabled(B(t, true)), () => Text(FrameSmoothing.Enabled), "KF2_SMOOTH"),
        [FrameSmoothing.PosKey] = new(t => FrameSmoothing.SetPosition(B(t, true)), () => Text(FrameSmoothing.Position), "KF2_SMOOTH_POS"),
        [ObjectSmoothing.OnKey] = new(t => ObjectSmoothing.SetEnabled(B(t, true)), () => Text(ObjectSmoothing.Enabled), "KF2_SMOOTH_OBJECTS"),
        [AnimSmoothing.OnKey] = new(t => AnimSmoothing.SetEnabled(B(t, true)), () => Text(AnimSmoothing.Enabled), "KF2_SMOOTH_ANIM"),
        [FluidSmoothing.OnKey] = new(t => FluidSmoothing.SetEnabled(B(t, true)), () => Text(FluidSmoothing.Enabled), "KF2_SMOOTH_FLUID"),
        [AudioQuality.InterpKey] = new(t => AudioQuality.SetInterpolation((SpuInterpolation)I(t, 2)),
            () => ((int)Spu.Interpolation).ToString(CultureInfo.InvariantCulture), "KF2_SPU_INTERP"),
        [AudioQuality.ReverbKey] = new(t => AudioQuality.SetEnhancedReverb(I(t, 1) == 1),
            () => Spu.ReverbMode == SpuReverbMode.Enhanced ? "1" : "0", "KF2_REVERB"),
        [PositionalAudio.ModeKey] = new(t => PositionalAudio.Set((PositionalAudio.Mode)I(t, 2)),
            () => ((int)PositionalAudio.Current).ToString(CultureInfo.InvariantCulture), "KF2_POSAUDIO"),
        [Map.OnKey] = new(t => Map.SetEnabled(B(t, true)), () => Text(Map.Enabled), "KF2_MAP"),
        [MapFog.OnKey] = new(t => MapFog.SetEnabled(B(t, true)), () => Text(MapFog.Enabled), "KF2_MAP_FOG"),
    };

    /// <summary>
    /// onboarding.json against the code, once every feature has read its saved value:
    /// each key it writes must have a live setter, and each key not saved must read
    /// what the spec's <c>defaults</c> say it means, or Modern (which writes nothing)
    /// is not the defaults and a fresh install reads as Custom. A line per mismatch;
    /// the game boots either way. The spec's own shape is checked as it loads. See "What setup writes, and when" in docs/SETTINGS.md.
    /// </summary>
    static void Check(OnboardingSpec spec)
    {
        var keys = spec.Display.SelectMany(f => f.Options).SelectMany(o => o.Set.Keys)
            .Concat(spec.Categories.SelectMany(c => c.Groups).SelectMany(g => g.Tiers).SelectMany(t => t.Set.Keys))
            .Where(k => !Onboarding.IsWindowKey(k))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var view = RecompOne.Runtime.Runtime.View;
        int problems = 0;
        void Problem(string line) { problems++; Console.WriteLine($"[KF2] setup check: {line}"); }

        foreach (var key in keys)
        {
            var port = PortSettings.All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            bool window = Window.TryGetValue(key, out var w);
            if (!window && (port is null || port.AtBoot))
            {
                Problem($"{key} has no live setter; setup would leave it for the next launch");
                continue;
            }
            if (!spec.Defaults.TryGetValue(key, out var declared))
            {
                Problem($"{key} has no entry in defaults");
                continue;
            }
            if (view.Has(key)) continue;    // saved: the value read is the player's, not the default
            var envs = window ? w!.Envs : port!.Envs;
            if (envs.Any(e => Environment.GetEnvironmentVariable(e) is not null)) continue;

            string live = window ? w!.Live() : port!.Live().ToString(CultureInfo.InvariantCulture);
            string want = window ? declared : Number(declared, double.NaN).ToString(CultureInfo.InvariantCulture);
            if (!Onboarding.SameText(live, want))
                Problem($"{key} reads {live} with nothing saved, but defaults says {declared}");
        }
        Console.WriteLine($"[KF2] setup check: {keys.Count} key(s), {problems} problem(s)");
    }
}
