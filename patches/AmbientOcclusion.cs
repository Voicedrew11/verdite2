using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Ambient occlusion — contact shading in the corners, under the doorframes and
/// where a pillar meets the floor, from the same recovered view depth perspective
/// correction and the Z-buffer already use.
///
///     KF2_AO=1              on; 0 or unset leaves the picture flat
///     KF2_AO_RADIUS=512     how far a surface reaches to shade its neighbour,
///                           in the game's own world units (a floor tile is 2048)
///     KF2_AO_STRENGTH=0.8   how dark a fully occluded pixel goes, 0..1
///     KF2_AO_BIAS=0.08      the angular bias that keeps a flat wall from
///                           shading itself out of its own depth quantisation
///     KF2_AO_SAMPLES=16     samples per pixel in the occlusion pass
///     KF2_AO_MAXDEPTH=24000 beyond this view depth the pass returns unoccluded
///     KF2_AO_PROBE=1        the coverage, the projection it recovered, and the
///                           passes it actually ran
///     KF2_AO_PROBE=2        also read the occlusion texture back and census it:
///                           how dark, how much of the frame, and where
///
/// **The interesting part is not the SSAO, it is where the depth comes from.** The
/// PlayStation's GPU has no depth buffer and is handed none: the game sorts whole
/// polygons into an ordering table by their average OTZ and <c>DrawOTag</c> walks
/// it back to front. So there is no G-buffer to run a screen-space pass against,
/// and building one the usual way would mean a depth prepass — a second submission
/// of the frame's geometry, which this port cannot do, because the geometry arrives
/// incrementally through GP0 and nothing knows the frame is complete until it is.
///
/// The depth is recovered per vertex exactly as <see cref="Perspective"/> recovers
/// it, and then **painter's order does the rest**: with every 3D triangle writing
/// its depth and the test left at <c>GL_ALWAYS</c>, the last write at a pixel is
/// the last thing drawn there, which back to front is the nearest visible surface.
/// That is a correct visible-surface depth buffer arrived at with no depth test,
/// no prepass and no second pass over the geometry — and, because nothing is ever
/// rejected, with the ordering table still in sole charge of what is visible. This
/// is why it is not the Z-buffer: that one *tests*, and its picture has never come
/// out right (see "Z-buffer" in docs/RENDERING.md). Occlusion shading reads the
/// same numbers without letting them decide anything.
///
/// **What is not shaded is as load-bearing as what is.** Everything with no
/// recovered depth writes the *far plane* rather than the clip Z it used to, so the
/// HUD, the menus, the item pictures and any triangle the vertex map missed read as
/// "no surface here": they are neither shaded nor allowed to occlude anything, and
/// a hole in the depth buffer costs occlusion instead of inventing it. Semi-
/// transparent primitives write nothing at all, so a death fade or a damage flash
/// — which you see the world *through* — does not erase the world's depth
/// underneath it and does not make the shading blink off for the frames it covers.
///
/// The pass runs at present, between the finished render target and the blit to
/// the window, and writes only its own texture. Nothing the game can read back
/// carries the shading: not VRAM, not either display buffer, and not the frame a
/// modal loop stores and restores — so a menu cannot bake it in and re-shade it
/// every iteration.
///
/// **Off by default, for the sub-pixel reason.** Every claim above is a mechanism
/// with a counter behind it, and the picture has not been judged by eye. It is also
/// deliberately not authentic: the console could not have drawn this. Its switch is
/// under Video ▸ Enhancements with the others, and the tuning is on the console —
/// a radius and a strength are the port's question to answer, not the player's.
///
/// GL backend only. The work is in the runtime (<c>patches/recompone/0040</c>);
/// this is the switch and the report, and the one hook is a post on
/// <c>DrawOTag</c>, purely to have a frame boundary to count against.
/// See "Ambient occlusion" in docs/RENDERING.md.
/// </summary>
public static class AmbientOcclusion
{
    // libgpu DrawOTag, per overlay -- the same three addresses Subpixel hooks.
    static readonly (string Overlay, uint Addr)[] DrawOTag =
    [
        ("open", 0x80016078), ("game", 0x80060818), ("end", 0x80013D80),
    ];

    /// <summary>Where the choice is kept between runs.</summary>
    public const string OnKey = "kf2.ao.on";

    /// <summary>False leaves the picture exactly as it was: no depth is written
    /// for the pass, no primitive changes zMode, and the present multiplies by
    /// nothing rather than by one.</summary>
    public static bool Enabled
    {
        get => GteDepth.AmbientOcclusion;
        private set => GteDepth.AmbientOcclusion = value;
    }

    static bool? _forced;
    static bool _toConsole;
    static bool _census;

    static long _frames;
    static double _windowStart;

    static double Now => Environment.TickCount64 / 1000.0;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.ao",
        Name = "Ambient occlusion",
        Version = "1.0",
        Description = "Contact shading in the corners, from the depth the GTE throws away.",
    };

    public static void Configure(string? on, string? radius, string? strength,
                                 string? bias, string? samples, string? maxDepth, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on))
            _forced = !on.Equals("0", StringComparison.Ordinal);

        if (float.TryParse(radius, out float r) && r > 0f) GteDepth.AoRadius = r;
        if (float.TryParse(strength, out float st) && st >= 0f) GteDepth.AoStrength = Math.Clamp(st, 0f, 1f);
        if (float.TryParse(bias, out float b) && b >= 0f) GteDepth.AoBias = Math.Clamp(b, 0f, 0.9f);
        if (int.TryParse(samples, out int n) && n > 0) GteDepth.AoSamples = Math.Clamp(n, 1, 64);
        if (float.TryParse(maxDepth, out float md) && md > 0f) GteDepth.AoMaxDepth = md;

        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
            _toConsole = true;
        if (probe == "2") _census = true;
    }

    public static void Install()
    {
        _windowStart = Now;

        // The default is off, so like Subpixel there is nothing to decide before
        // the config file is read. ConfigManager only loads inside
        // HostWindow.Initialize, which is after Program.cs -- reading the saved
        // key here would read an empty config and write it back over the real one.
        Enabled = _forced ?? false;
        GteDepth.AoProbe = _toConsole;

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, false);
            Console.WriteLine($"[KF2] ambient occlusion: {(Enabled ? "on" : "off")}" +
                              (Enabled ? $", radius {GteDepth.AoRadius:F0}, strength {GteDepth.AoStrength:F2}, " +
                                         $"{GteDepth.AoSamples} samples" : ""));
        });

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached || !_toConsole) return;
            attached = true;
            Attach();
        });
    }

    /// <summary>Change the setting at run time. The next frame's triangles start or
    /// stop writing depth and the generation is bumped, so the target's attachment
    /// is cleared rather than shaded against what was left in it.</summary>
    public static void SetEnabled(bool on) => Enabled = on;

    static void Attach()
    {
        SymbolRegistry.Build();
        var after = typeof(AmbientOcclusion).GetMethod(nameof(AfterDrawOTag), BindingFlags.Public | BindingFlags.Static)!;

        int n = 0;
        foreach (var (overlay, addr) in DrawOTag)
        {
            var target = SymbolRegistry.Resolve(overlay, null, addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] ao: no function at {overlay}/0x{addr:X8}");
                continue;
            }
            if (HookManager.AddPost(_self, target, after)) n++;
        }

        HookManager.Commit();
        Console.WriteLine($"[KF2] ao: probe on, {n} hook(s)");
    }

    public static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        _frames++;
        double window = Now - _windowStart;
        if (window < 2.0) return;

        // Three separate claims, and each has its own way of reading zero.
        //
        // *Coverage* is how much of the frame the pass can see at all: a triangle
        // that recovered a depth on all three corners writes it, one that did not
        // stamps the far plane and is left alone. This is the same count the
        // Z-buffer's probe takes, because it is the same write.
        //
        // *The projection* is the pass's one assumption about the game, and it is
        // not an assumption -- H and the OFX/OFY centre are read out of the GTE.
        // A ProjSeen of zero means nothing projected in the window and the numbers
        // printed are the fallback constants, not a reading.
        //
        // *Passes* is whether the shading ran at all. A present with no render
        // target behind it -- the VRAM fallback, an MDEC frame -- has no depth
        // attachment to read, and a run that is all `no target` is a picture with
        // no shading in it however healthy the other two numbers look.
        long tris = GteDepth.ZTris, skipped = GteDepth.ZSkipped;
        double cover = tris + skipped == 0 ? 0.0 : 100.0 * tris / (tris + skipped);

        Console.WriteLine($"[KF2] ao: {tris / window:F0} tris/s carrying a depth, {cover:F1}% of 3D, " +
                          $"H {GteDepth.ProjH:F0} centre {GteDepth.ProjCx:F1},{GteDepth.ProjCy:F1} " +
                          $"-> {GteDepth.AoCentreX:F3},{GteDepth.AoCentreY:F3} of the picture " +
                          $"({GteDepth.ProjSeen / window:F0} reads/s), " +
                          $"{GteDepth.AoPasses / window:F1} passes/s, {GteDepth.AoNoTarget / window:F1} no target/s, " +
                          $"over {_frames / window:F0} frames/s");

        if (_census) Census();

        GteDepth.ResetZCounters();
        GteDepth.ResetAoCounters();
        _frames = 0;
        _windowStart = Now;
    }

    /// <summary>
    /// The occlusion texture itself, read back once a window.
    ///
    /// **This is the one number that separates "the pass ran" from "the pass did
    /// something".** Everything the line above prints stays exactly the same if the
    /// shader returns 1.0 on every pixel: the depth still arrives, the projection
    /// is still read, the two draws are still issued and the present still
    /// multiplies. Nobody here is allowed to go and look at the picture, so the
    /// picture is reduced to a darkest value, a mean, a shaded share and a 32x16
    /// map — and a run where the shaded share is 0.0% is a run with no ambient
    /// occlusion in it whatever the rest of the report says.
    ///
    /// A map cell reading exactly 1.00 is not a gap: the HUD and everything else
    /// the vertex map missed stamped the far plane on purpose, and the pass leaves
    /// the far plane alone. The bottom rows of the map should therefore be 1.00
    /// wherever the HP/MP panel is.
    /// </summary>
    static void Census()
    {
        if (GteDepth.AoMap is { } map)
        {
            Console.WriteLine($"[KF2] ao: darkest {GteDepth.AoMin:F2}, mean {GteDepth.AoMean:F3}, " +
                              $"{GteDepth.AoShadedPct:F1}% of the picture shaded, " +
                              $"{GteDepth.AoCoveragePct:F1}% of it carrying a surface");
            Console.WriteLine("[KF2] ao:  occlusion (9 = faint, 0 = black)      surface (# full, + partial, . none)");
            var cov = GteDepth.AoCoverage;
            for (int r = 0; r < GteDepth.AoMapRows; r++)
            {
                var row = new System.Text.StringBuilder("[KF2] ao:  ");
                for (int c = 0; c < GteDepth.AoMapCols; c++)
                {
                    float v = map[r * GteDepth.AoMapCols + c];
                    // Ten steps from unshaded to black, so a corner reads as a
                    // gradient rather than as a threshold.
                    row.Append(v >= 0.995f ? '.' : " 9876543210"[Math.Clamp((int)((1f - v) * 10f) + 1, 1, 10)]);
                }
                // The mask, in the same cells, from the pass's own second channel.
                // A blank column in the occlusion map means one of two quite
                // different things and this is what tells them apart: no surface
                // there at all (the HUD, or a triangle the vertex map missed, both
                // stamping the far plane on purpose), or a surface the pass found
                // nothing near enough to shade -- which is a radius too small or a
                // reconstruction that is wrong, and is a bug where the first is
                // the design working.
                if (cov is not null)
                {
                    row.Append("   ");
                    for (int c = 0; c < GteDepth.AoMapCols; c++)
                    {
                        float f = cov[r * GteDepth.AoMapCols + c];
                        row.Append(f >= 0.99f ? '#' : f > 0.01f ? '+' : '.');
                    }
                }
                Console.WriteLine(row.ToString());
            }
        }
        GteDepth.WantAoMap = true;
    }
}
