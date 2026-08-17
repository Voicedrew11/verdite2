using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Perspective-correct texture mapping — the fix for the swimming, rippling
/// textures that are the most recognisable thing about a PlayStation picture.
///
///     KF2_PERSPECTIVE=0             off; 1 or unset leaves it on
///     KF2_PERSPECTIVE_PROBE=1       report the vertex table's hit rate
///
/// The console's GPU has no depth. The GTE does the perspective divide, the game
/// copies the resulting 2D screen coordinate into a GP0 packet, and the GPU
/// receives a polygon that is nothing but screen positions, UVs and colours. It
/// can only interpolate U and V linearly across the screen, which is exact for a
/// surface square-on to the camera and progressively wrong as the surface turns
/// away — so a floor's texture slides as you walk over it, and a quad shows a
/// crease along the diagonal where its two triangles disagree about where the
/// middle of the texture went.
///
/// Nothing in the packet can fix that; the depth is simply gone by then. So it is
/// caught earlier, in <see cref="GteDepth"/>: <c>Gte.Rtp</c> knows the screen
/// position and the view depth SZ3 in the same call, and the screen position is
/// exactly what ends up in the packet — <c>SatX</c>/<c>SatY</c> clamp it to the
/// same 11 signed bits the GPU decodes. Recording (x, y) -> z there and asking for
/// it again as a vertex word is decoded reunites the two halves without following
/// a single register or store, and without the game noticing anything.
///
/// A vertex that misses is left alone, which is what makes this safe to leave on:
///
///   * The **HUD and the menus** never went through the GTE, so they never hit,
///     so they keep the affine mapping 2D work actually wants.
///   * A polygon is corrected **only if every one of its vertices hit**, so it can
///     never mix a recovered depth with a fallback and shear itself apart.
///   * A vertex that projected **off screen** saturates at ±1024, where several
///     different vertices share one key; those are never recorded, so such a
///     polygon simply stays as it was.
///
/// Both renderers honour it. The software rasterizer interpolates U/W, V/W and 1/W
/// and divides per pixel; the hardware backend puts the depth in
/// <c>gl_Position.w</c> and lets the rasterizer do it, with <c>noperspective</c> on
/// the colour so Gouraud shading stays exactly as flat as the console's. A vertex
/// with no depth carries W = 1 and the vertex shader writes the original expression
/// for it, so everything that is not corrected is bit-identical to before.
///
/// This patch itself is only the switch and the report — the work is all in the
/// runtime, since a texture coordinate is decided a long way below anything
/// <c>HookManager</c> can reach. The one hook is on <c>DrawOTag</c>, purely to have
/// a frame boundary to count against, and it is a post-hook so it composes with the
/// widescreen mod's replacement of the same function.
/// </summary>
public static class Perspective
{
    // libgpu DrawOTag, per overlay -- the same three addresses NoDither hooks.
    static readonly (string Overlay, uint Addr)[] DrawOTag =
    [
        ("open", 0x80016078), ("game", 0x80060818), ("end", 0x80013D80),
    ];

    /// <summary>Where the choice is kept between runs.</summary>
    public const string OnKey = "kf2.perspective.on";

    /// <summary>False restores the console's affine mapping, warts and all.</summary>
    public static bool Enabled
    {
        get => GteDepth.Enabled;
        private set => GteDepth.Enabled = value;
    }

    /// <summary>KF2_PERSPECTIVE: an explicit choice on the command line, which wins
    /// over the saved setting for the run.</summary>
    static bool? _forced;

    /// <summary>KF2_PERSPECTIVE_PROBE: also write the report to the console.</summary>
    static bool _toConsole;

    static long _frames;
    static double _windowStart;

    static double Now => Environment.TickCount64 / 1000.0;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.perspective",
        Name = "Perspective-correct textures",
        Version = "1.0",
        Description = "Recovers per-vertex depth from the GTE so textures stop swimming.",
    };

    public static void Configure(string? on, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on))
            _forced = !on.Equals("0", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
            _toConsole = true;
    }

    public static void Install()
    {
        _windowStart = Now;

        // On from the first projected vertex, before the config file has been read,
        // so the title screen is already correct; RuntimeReadyEvent then applies
        // whatever was saved. ConfigManager only loads inside HostWindow.Initialize,
        // which is after Program.cs -- reading the setting here would read an empty
        // config and write it back over the real one.
        Enabled = _forced ?? true;

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, true);
            Console.WriteLine($"[KF2] perspective: {(Enabled ? "on" : "off (affine)")}");
        });

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached || !_toConsole) return;
            attached = true;
            Attach();
        });
    }

    /// <summary>Change the setting at run time. The table starts or stops filling;
    /// a frame drawn during the change is at worst partly corrected.</summary>
    public static void SetEnabled(bool on) => Enabled = on;

    // Only attached under the probe: without it there is nothing to count and the
    // patch has no reason to touch the game's code at all.
    static void Attach()
    {
        SymbolRegistry.Build();
        var after = typeof(Perspective).GetMethod(nameof(AfterDrawOTag), BindingFlags.Public | BindingFlags.Static)!;

        int n = 0;
        foreach (var (overlay, addr) in DrawOTag)
        {
            var target = SymbolRegistry.Resolve(overlay, null, addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] perspective: no function at {overlay}/0x{addr:X8}");
                continue;
            }
            if (HookManager.AddPost(_self, target, after)) n++;
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] perspective: probe on, {n} hook(s)");
    }

    public static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        _frames++;
        double window = Now - _windowStart;
        if (window < 2.0) return;

        long hits = GteDepth.Hits, misses = GteDepth.Misses, recorded = GteDepth.Recorded;
        long asked = hits + misses;

        // The hit rate is the whole measurement: it says the coordinate the GTE
        // saturates into SX2/SY2 really is the coordinate that reaches the packet.
        // A rate near zero would mean the two ends never agreed on a key and every
        // polygon quietly stayed affine.
        Console.WriteLine($"[KF2] perspective: {recorded / window:F0} vertices projected/s, " +
                          $"{asked / window:F0} looked up/s, {(asked == 0 ? 0.0 : 100.0 * hits / asked):F1}% hit, " +
                          $"over {_frames / window:F0} frames/s");

        GteDepth.ResetCounters();
        _frames = 0;
        _windowStart = Now;
    }
}
