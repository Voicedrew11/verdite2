using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// A Z-buffer — per-pixel occlusion from the view depth the GTE already computed,
/// instead of the console's painter's algorithm.
///
///     KF2_ZBUFFER=1             on; 0 or unset leaves the ordering table in charge
///     KF2_ZBUFFER_PROBE=1       report how many triangles actually depth-tested
///
/// The PlayStation GPU has no depth buffer. The game sorts every polygon into an
/// ordering table by one number, the GTE's OTZ (the average of its vertices), and
/// <c>DrawOTag</c> walks that table back to front. Two surfaces that actually
/// interpenetrate — a floor meeting a wall, two cave rocks crossing — can only
/// take turns in front of each other, because each polygon is wholly in front or
/// wholly behind. That flicker is what this turns off.
///
/// The depth is the same SZ3 <see cref="Perspective"/> recovers for the texture:
/// caught at <c>Gte.Rtp</c>, followed through memory by <see cref="GteVertexMap"/>,
/// and asked for again by the address <c>DrawOTag</c> read the vertex word from.
/// Both renderers interpolate it per pixel (1/Z is linear in screen space for a
/// plane) and keep the pixel if it is closer than what is already there. Equal
/// depths still prefer the later table entry, so coplanar surfaces that the game
/// stacked on purpose keep the console's order.
///
/// A miss is the old behaviour, which is what makes this safe to leave available:
///
///   * The <b>HUD and the menus</b> never went through the GTE, so they never
///     hit, so they still draw on top in table order with the depth test off.
///   * A triangle is all-or-nothing. One vertex left without a depth would punch
///     a hole through the surface, so that triangle keeps painter's order rather
///     than testing.
///   * Semi-transparent primitives test against Z but do not write it, so two
///     overlapping additives still blend in the order the table named.
///   * Untextured geometry is tested too. Perspective correction only cares
///     about textured polygons; a flat-shaded wall still has a view depth.
///
/// Both renderers honour it. The hardware backend attaches a 24-bit depth buffer
/// to each display render target and writes window depth from SZ in the fragment
/// shader, so OpenGL does not clip the already-projected triangle; the software
/// rasterizer keeps a float per VRAM pixel and tests it in the same inner loop
/// that plots. Turning the setting off is an exact no-op: the attachment is
/// never tested, the clip W is unchanged, and a vertex with no depth still
/// writes <c>vec4(p, 0, 1)</c>.
///
/// <b>Off by default</b>, for the same reason as sub-pixel positioning: the
/// mechanism is the recovered depth that perspective correction already
/// measures, but the picture has not been checked by eye. The cave flicker in
/// "Following the value through memory" is the thing to look at. See "Z-buffer"
/// in NOTES.md.
///
/// As with perspective correction this patch is only the switch and the report;
/// the work is in the runtime (<c>patches/recompone/0014</c>), because a pixel's
/// depth is decided far below anything <c>HookManager</c> can reach. The one hook
/// is on <c>DrawOTag</c>, purely to have a frame boundary to count against, and
/// it is a post-hook so it composes with the widescreen patch's replacement of
/// the same function.
/// </summary>
public static class ZBuffer
{
    static readonly (string Overlay, uint Addr)[] DrawOTag =
    [
        ("open", 0x80016078), ("game", 0x80060818), ("end", 0x80013D80),
    ];

    /// <summary>Where the choice is kept between runs.</summary>
    public const string OnKey = "kf2.zbuffer.on";

    /// <summary>False leaves occlusion to the ordering table, as the console does.</summary>
    public static bool Enabled
    {
        get => GteDepth.ZBuffer;
        private set => GteDepth.ZBuffer = value;
    }

    /// <summary>KF2_ZBUFFER: an explicit choice on the command line, which wins
    /// over the saved setting for the run.</summary>
    static bool? _forced;

    /// <summary>KF2_ZBUFFER_PROBE: also write the report to the console.</summary>
    static bool _toConsole;

    static long _frames;
    static double _windowStart;

    static double Now => Environment.TickCount64 / 1000.0;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.zbuffer",
        Name = "Z-buffer",
        Version = "1.0",
        Description = "Per-pixel occlusion from recovered GTE depth, instead of the ordering table.",
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

        // Default is off: RuntimeReadyEvent is the first and only place the
        // setting is decided. ConfigManager only loads inside HostWindow.Initialize,
        // which is after Program.cs, so reading it here would read an empty config
        // and write it back over the real one.
        Enabled = _forced ?? false;

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, false);
            Console.WriteLine($"[KF2] zbuffer: {(Enabled ? "on" : "off (ordering table)")}");
        });

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached || !_toConsole) return;
            attached = true;
            Attach();
        });
    }

    /// <summary>Change the setting at run time. The next triangle starts or stops
    /// testing; a frame drawn during the change is at worst partly sorted, which
    /// is a frame of the flicker the setting is about.</summary>
    public static void SetEnabled(bool on) => Enabled = on;

    static void Attach()
    {
        SymbolRegistry.Build();
        var after = typeof(ZBuffer).GetMethod(nameof(AfterDrawOTag), BindingFlags.Public | BindingFlags.Static)!;

        int n = 0;
        foreach (var (overlay, addr) in DrawOTag)
        {
            var target = SymbolRegistry.Resolve(overlay, null, addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] zbuffer: no function at {overlay}/0x{addr:X8}");
                continue;
            }
            if (HookManager.AddPost(_self, target, after)) n++;
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] zbuffer: probe on, {n} hook(s)");
    }

    public static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        _frames++;
        double window = Now - _windowStart;
        if (window < 2.0) return;

        long tested = GteDepth.ZTris, skipped = GteDepth.ZSkipped, rejected = GteDepth.ZRejects;
        long total = tested + skipped;

        // The tested rate is the measurement: it is the only thing that says the
        // recovered depth is actually reaching the rasterizer. A rate near zero
        // would mean every triangle quietly kept painter's order. Skipped is 2D
        // and anything the map missed; rejects are software-rasterizer pixels that
        // lost, and stay at zero on the hardware path.
        Console.WriteLine($"[KF2] zbuffer: {tested / window:F0} tris/s tested, " +
                          $"{skipped / window:F0} painter's/s" +
                          $"{(total == 0 ? "" : $", {(100.0 * tested / total):F1}% of submitted")}" +
                          $"{(rejected == 0 ? "" : $", {rejected / window:F0} px rejected/s")}, " +
                          $"over {_frames / window:F0} frames/s");

        GteDepth.ResetZCounters();
        _frames = 0;
        _windowStart = Now;
    }
}
