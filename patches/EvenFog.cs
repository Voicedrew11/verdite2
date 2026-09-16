using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Even fog and lighting on the map tiles, three parts under one switch:
///
/// - a polygon the clipper cuts is fogged on the tiles' curve, not at the half
///   `func_800302E8` applies;
/// - fog strength belongs to each tile's light record, so it changes at a tile edge;
///   each tile vertex's fog is blended between the tile centres around it instead;
/// - the records' colour matrix and back colour are blended the same way, as the
///   game does for objects.
///
///     KF2_EVENFOG=0         off (on by default; needs Fast geometry)
///     KF2_EVENFOG_BLEND=0   no fog blend: the game's hard fog edge between records
///     KF2_EVENLIGHT=0       no light blend: the game's hard light edge
///
/// All run from the C# assembler, so they need Fast geometry, and all stand down
/// under KF2_POLYASM=verify. See "A clipped tile is fogged at half, and that is the
/// block on the floor" and "Fog changes at a tile edge" in docs/RENDERING.md.
/// </summary>
public static class EvenFog
{
    public const string OnKey = "kf2.evenfog.on";

    /// <summary>func_80031950(half, &amp;position, flags): one map tile's half, drawn.</summary>
    const uint TileSubmit = 0x80031950;

    public static bool Enabled { get; set; }
    public static bool Blend { get; private set; } = true;
    static bool _lightPart = true;
    public static bool Light => Enabled && _lightPart;

    static bool? _forced;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.evenfog",
        Name = "Even fog and lighting",
        Version = "1.0",
        Description = "Clipped map tiles fogged like their neighbours, and fog and light blended across light-record edges.",
    };

    public static void Configure(string? on, string? blend, string? light)
    {
        if (!string.IsNullOrWhiteSpace(on)) _forced = on.Trim() != "0";
        Blend = blend?.Trim() != "0";
        _lightPart = light?.Trim() != "0";
    }

    public static void Install()
    {
        Enabled = _forced ?? true;
        // The saved key is only readable once the runtime is up (see AmbientOcclusion).
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, true);
            Console.WriteLine(!Enabled ? "[KF2] even fog and lighting: off"
                : $"[KF2] even fog and lighting: on{(Blend ? "" : ", no fog blend")}{(_lightPart ? "" : ", no light blend")}");
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
