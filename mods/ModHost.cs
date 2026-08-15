namespace Kf2.Mods;

/// <summary>
/// A unit of game-behaviour change that can be turned on and off at run time.
///
/// Mods exist because everything interesting about this port beyond "run the
/// game" is a behaviour change hung off an SDK call or a game function, and
/// those want to be tried, compared and reverted without editing the recompiler
/// output. A mod owns its own state and reads its own configuration; the
/// framework only decides which are on.
///
/// The one thing a mod cannot do at run time is invent a new hook site: the
/// recompiler bakes the hook into the emitted function, so a *new* attachment
/// point means an entry in config/kf2.json and a recompile. Everything already
/// listed in <see cref="Hooks"/> is free to be claimed and released.
/// </summary>
public abstract class Mod
{
    /// <summary>Name used in KF2_MODS. Lower case, no spaces.</summary>
    public abstract string Name { get; }

    /// <summary>One line, printed by the startup banner.</summary>
    public abstract string Summary { get; }

    /// <summary>Whether the mod is on when KF2_MODS says nothing about it.</summary>
    public virtual bool DefaultEnabled => false;

    public bool Enabled { get; private set; }

    /// <summary>What the banner shows after the name -- "60 fps", "off", "10s".</summary>
    public virtual string State => Enabled ? "on" : "off";

    /// <summary>The text after `=` in KF2_MODS, "" if the name was given bare.</summary>
    protected internal virtual void Configure(string value) { }

    protected internal virtual void OnEnabled() { }
    protected internal virtual void OnDisabled() { }

    internal void SetEnabled(bool on)
    {
        if (on == Enabled) return;
        Enabled = on;
        if (on) OnEnabled(); else OnDisabled();
    }
}

/// <summary>
/// The mod registry, and the parser for KF2_MODS.
///
///     KF2_MODS=fps=60              turn the fps mod on and hand it "60"
///     KF2_MODS=fps=off             turn it off even though it defaults on
///     KF2_MODS=framestats=10,fps   several, comma separated
///
/// A name with no `=` is just "on". `=off` is the only value that disables; a
/// mod that wants an "off" of its own meaning should use a different word.
/// </summary>
public static class ModHost
{
    static readonly List<Mod> _mods = [];

    public static IReadOnlyList<Mod> All => _mods;

    public static void Register(Mod mod)
    {
        if (_mods.Any(m => m.Name == mod.Name))
            throw new ArgumentException($"duplicate mod name '{mod.Name}'");
        _mods.Add(mod);
        mod.SetEnabled(mod.DefaultEnabled);
    }

    public static T Get<T>() where T : Mod => _mods.OfType<T>().Single();

    public static Mod? Find(string name) =>
        _mods.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Apply a KF2_MODS spec. Unknown names throw rather than being ignored --
    /// a silently misspelled mod name is a lost afternoon.</summary>
    public static void Load(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return;

        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = item.IndexOf('=');
            string name = (eq < 0 ? item : item[..eq]).Trim();
            string value = eq < 0 ? "" : item[(eq + 1)..].Trim();

            var mod = Find(name)
                ?? throw new ArgumentException(
                    $"KF2_MODS: no mod called '{name}'. Known: {string.Join(", ", _mods.Select(m => m.Name))}");

            if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
            {
                mod.SetEnabled(false);
                continue;
            }

            mod.SetEnabled(true);
            mod.Configure(value);
        }
    }

    public static void PrintStatus()
    {
        var on = _mods.Where(m => m.Enabled).ToList();
        Console.WriteLine(on.Count == 0
            ? "[KF2] mods: none"
            : $"[KF2] mods: {string.Join(", ", on.Select(m => $"{m.Name}={m.State}"))}");
    }
}
