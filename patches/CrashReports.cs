using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// What this port adds to the runtime's crash, fault and hang reports (runtime
/// 0107): its version, where the player is (the agent beacon's snapshot), and
/// every switch that is not the default. And a way to make each kind of report
/// on purpose, so the paths can be run without waiting for a real one.
///
/// Complements <see cref="CrashDump"/>, which prints the game's own state
/// (weapon, entity table) to stderr at a crash; a report's console section already
/// carries those lines, so the two overlap there and neither needs the other.
/// </summary>
public static class CrashReports
{
    public static void Configure(string? hang)
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion
                      ?? typeof(CrashReports).Assembly.GetName().Version?.ToString() ?? "?";
        CrashReport.Title = $"Verdite2 {version}";
        if (double.TryParse(hang, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            Watchdog.HangSeconds = seconds;

        CrashReport.AddSection("Game", AgentBeacon.Snapshot);
        CrashReport.AddSection("Settings (kf2.*)", () =>
        {
            var values = RecompOne.Runtime.Config.ConfigManager.View.Values;
            return string.Join('\n', values.Where(v => v.Key.StartsWith("kf2.", StringComparison.OrdinalIgnoreCase))
                                           .OrderBy(v => v.Key, StringComparer.Ordinal)
                                           .Select(v => $"{v.Key}={v.Value}"));
        });
        CrashReport.AddSection("Environment (KF2_*)", () =>
        {
            var set = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                                 .Where(k => k.StartsWith("KF2_", StringComparison.Ordinal))
                                 .OrderBy(k => k, StringComparer.Ordinal)
                                 .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}");
            return string.Join('\n', set) is { Length: > 0 } s ? s : "(none)";
        });
    }

    // KF2_FAULT=hook|crash|hang[:seconds], timed from the first fdat module: a
    // test of each report's path, nothing a player sets.
    //     hook   a hook throws: a fault report, a notice, this hook off, the game goes on
    //     crash  the game's own code throws: a crash report and the crash screen
    //     hang   the game thread spins on a hardware register: a hang report with its stack
    // The hook belongs to this file's own mod (kf2.faultinject), which a hook fault
    // turns off for the session; hook@<mod id> (hook@kf2.zbuffer) makes it that
    // patch's instead, so that patch is the one turned off and its settings page
    // greys (PatchSettings.Failed).
    static readonly ModInfo _self = new() { Id = "kf2.faultinject", Name = "Fault injection", Version = "1.0" };
    static ModInfo _owner = _self;
    static string _mode = "";
    static double _after = 20;
    static readonly System.Diagnostics.Stopwatch _clock = new();

    // The main loop's stage 9, called by the main loop every iteration (MenuWorld,
    // LoopPacing use it the same way), so a hook here fires every frame.
    const uint MainLoopMarker = 0x800140AC;

    public static void InstallFault(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Trim().ToLowerInvariant().Split(':');
        _mode = parts[0];
        if (_mode.StartsWith("hook@", StringComparison.Ordinal))
        {
            var id = _mode["hook@".Length..];
            _mode = "hook";
            if (FindMod(id) is not { } owner)
            {
                Console.Error.WriteLine($"[KF2] KF2_FAULT: no patch with the mod id '{id}'");
                return;
            }
            _owner = owner;
        }
        if (_mode is not ("hook" or "crash" or "hang"))
        {
            Console.Error.WriteLine($"[KF2] KF2_FAULT: unknown '{spec}', want hook, crash or hang");
            return;
        }

        if (parts.Length > 1) double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out _after);
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            if (!_clock.IsRunning && e.Name.StartsWith("fdat", StringComparison.Ordinal)) _clock.Start();
        });
        HookAttach.OnOverlayLoad("fault", () =>
        {
            SymbolRegistry.Build();
            if (SymbolRegistry.Resolve("game", null, MainLoopMarker) is not { } t) return false;
            HookManager.AddPre(_owner, t, typeof(CrashReports).GetMethod(nameof(Fire),
                                         BindingFlags.Public | BindingFlags.Static)!);
            HookManager.Commit();
            return HookAttach.Installed(t);
        });
        Console.WriteLine($"[KF2] fault injection: {_mode} {_after:F0}s after the first area" +
                          (_owner == _self ? "" : $", as {_owner.Id}"));
    }

    /// <summary>A patch's own ModInfo, by its id: the static <c>_self</c> each patch keeps
    /// it in. Reading one runs that type's static constructor, which a test switch may.</summary>
    static ModInfo? FindMod(string id)
    {
        foreach (var type in typeof(CrashReports).Assembly.GetTypes())
        {
            if (type.IsGenericTypeDefinition) continue;
            foreach (var f in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (f.Name == "_self" && f.FieldType == typeof(ModInfo) && f.GetValue(null) is ModInfo m && m.Id == id) return m;
        }
        return null;
    }

    public static void Fire(CpuContext c, IMemory m)
    {
        if (!_clock.IsRunning || _clock.Elapsed.TotalSeconds < _after) return;
        _clock.Reset();
        Console.WriteLine($"[KF2] fault injection: {_mode} now");
        switch (_mode)
        {
            case "hook":
                throw new InvalidOperationException("KF2_FAULT=hook: a hook throwing on purpose");
            case "crash":
                throw HookManager.NotAHookFault(new InvalidOperationException("KF2_FAULT=crash: a crash on purpose"));
            case "hang":
                while (true) m.ReadU32(0x1F801070u);
        }
    }
}
