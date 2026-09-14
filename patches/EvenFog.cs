using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Even fog on the map tiles, two parts under one switch:
///
/// - a polygon the clipper cuts is fogged on the tiles' curve, not at the half
///   `func_800302E8` applies;
/// - fog strength belongs to each tile's light record, so it changes at a tile edge;
///   each tile vertex's fog is blended between the tile centres around it instead.
///
///     KF2_EVENFOG=1         on (the saved setting otherwise; off by default)
///     KF2_EVENFOG_BLEND=0   the clipped fix only, the game's hard edge between records
///     KF2_EVENLIGHT=1       also blend the records' colour matrix and back colour, as the
///                           game does for objects (its own setting; off by default)
///
/// Both run from the C# assembler, so they need Fast geometry, and both stand down
/// under KF2_POLYASM=verify. See "A clipped tile is fogged at half, and that is the
/// block on the floor" and "Fog changes at a tile edge" in docs/RENDERING.md.
/// </summary>
public static class EvenFog
{
    public const string OnKey = "kf2.evenfog.on";
    public const string LightKey = "kf2.evenlight.on";

    /// <summary>func_80031950(half, &amp;position, flags): one map tile's half, drawn.</summary>
    const uint TileSubmit = 0x80031950;

    public static bool Enabled { get; set; }
    public static bool Blend { get; private set; } = true;
    public static bool Light { get; set; }

    static bool? _forced, _forcedLight;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.evenfog",
        Name = "Even fog",
        Version = "1.0",
        Description = "Clipped map tiles fogged like their neighbours, and fog blended across light-record edges.",
    };

    public static void Configure(string? on, string? blend, string? light)
    {
        if (!string.IsNullOrWhiteSpace(on)) _forced = on.Trim() != "0";
        if (!string.IsNullOrWhiteSpace(light)) _forcedLight = light.Trim() != "0";
        Blend = blend?.Trim() != "0";
    }

    public static void Install()
    {
        Enabled = _forced ?? false;
        Light = _forcedLight ?? false;
        // The saved key is only readable once the runtime is up (see AmbientOcclusion).
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, false);
            Light = _forcedLight ?? RecompOne.Runtime.Runtime.View.GetBool(LightKey, false);
            Console.WriteLine($"[KF2] even fog: {(Enabled ? Blend ? "on" : "on, no blend" : "off")}; " +
                              $"even lighting: {(Light ? "on" : "off")}");
        });
        HookAttach.OnOverlayLoad("even fog", Attach);
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("game", null, TileSubmit);
        if (target == null) return false;
        var self = typeof(EvenFog);
        HookManager.AddPre(_self, target, self.GetMethod(nameof(BeforeTile), BindingFlags.Public | BindingFlags.Static)!);
        HookManager.AddPost(_self, target, self.GetMethod(nameof(AfterTile), BindingFlags.Public | BindingFlags.Static)!);
        HookManager.Commit();
        bool ok = HookAttach.Installed(target);
        if (!ok) Console.Error.WriteLine("[KF2] even fog: the tile hook did not attach; fog and light are not blended.");
        return ok;
    }

    public static void BeforeTile(CpuContext c, IMemory m) => PolyAssembler.BeginTile(c.A0, m);

    public static void AfterTile(CpuContext c, IMemory m) => PolyAssembler.EndTile();
}
