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
/// One class of primitive is refused on purpose rather than for lack of a
/// depth: the outdoor backdrop, which the game links at the extreme far end of
/// the ordering table yet (census-measured) projects only mid-depth, so its SZ
/// would reject the distant terrain it is meant to sit behind. It is recognised
/// by that disagreement — linked farther than its depth warrants — and handed
/// back to painter's order instead of believing a depth the game itself
/// overrode, while the genuinely-distant world beside it still depth-tests.
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

    /// <summary>KF2_ZBUFFER_PROBE=2: also take the frame's occlusion census — which
    /// large primitive is standing in front of which, and how much of the picture
    /// that costs.</summary>
    static bool _census;

    public static void Configure(string? on, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on))
            _forced = !on.Equals("0", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
        {
            _toConsole = true;
            _census = !probe.Equals("1", StringComparison.Ordinal);
            GteDepth.TriCensus = _census;
        }
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
        if (window < 2.0) { if (_census) GteDepth.ResetCensus(); return; }

        if (_census) { ReportCensus(); GteDepth.ResetCensus(); }

        long tested = GteDepth.ZTris, skipped = GteDepth.ZSkipped, rejected = GteDepth.ZRejects;
        long banded = GteDepth.ZBand;
        long total = tested + skipped + banded;

        // The tested rate is the measurement: it is the only thing that says the
        // recovered depth is actually reaching the rasterizer. A rate near zero
        // would mean every triangle quietly kept painter's order. Skipped is 2D
        // and anything the map missed; rejects are software-rasterizer pixels that
        // lost, and stay at zero on the hardware path. Band counts triangles the
        // port handed back to painter's order because they were the backdrop —
        // linked far ahead of what their depth warrants; they appear in neither of
        // the other rates. The check is the disagreement between the game's far link
        // and a shallower recovered SZ, so the number that calibrates it is the
        // frame's far depth, against which each position predicts a depth; the
        // parked range shows the backdrop sitting below its own prediction.
        float far = GteDepth.FarSz;
        string bandRange = GteDepth.ZBandMaxSz > 0f
            ? $" (parked z {GteDepth.ZBandMinSz:F0}..{GteDepth.ZBandMaxSz:F0})" : "";
        Console.WriteLine($"[KF2] zbuffer: {tested / window:F0} tris/s tested, " +
                          $"{skipped / window:F0} painter's/s, {banded / window:F0} far-band/s{bandRange}" +
                          $"{(total == 0 ? "" : $", {(100.0 * tested / total):F1}% of submitted")}" +
                          $"{(rejected == 0 ? "" : $", {rejected / window:F0} px rejected/s")}, " +
                          $"far z {far:F0}, " +
                          $"over {_frames / window:F0} frames/s");

        GteDepth.ResetZCounters();
        _frames = 0;
        _windowStart = Now;
    }

    /// <summary>
    /// One frame's occlusion census, for the question a rate cannot answer: when a
    /// depth-tested surface disappears, <i>what is standing in front of it</i>.
    ///
    /// <c>DrawOTag</c> walks back to front, so a primitive submitted earlier is one
    /// the game itself sorted as farther away. An early entry whose recovered depth
    /// puts it entirely in front of a later one is therefore a disagreement between
    /// the depth this port recovered and the game's own OTZ sort — and wherever the
    /// two disagree over a wide area, the later surface vanishes and the picture
    /// shows the earlier one instead. Blockers are ranked by how much of the picture
    /// they take that way, which is the hole, in pixels.
    /// </summary>
    static void ReportCensus()
    {
        var all = GteDepth.Census.ToArray();
        int tested = 0, clamped = 0, bigCount = 0;
        float near = float.MaxValue, far = 0f;
        foreach (var t in all)
        {
            if (t.Clamped) clamped++;
            if (t.Area >= GteDepth.CensusBigArea) bigCount++;
            if (!t.Tested) continue;
            tested++;
            if (t.MinZ < near) near = t.MinZ;
            if (t.MaxZ > far) far = t.MaxZ;
        }

        Console.WriteLine($"[KF2] zbuffer census: {GteDepth.CensusSubmitted} polys submitted, " +
                          $"{all.Length} kept ({bigCount} at least {GteDepth.CensusBigArea}px, " +
                          $"{tested} depth-tested, {clamped} with a clamped corner)" +
                          $"{(GteDepth.CensusDropped == 0 ? "" : $", {GteDepth.CensusDropped} dropped")}" +
                          $"{(tested == 0 ? "" : $", z {near:F0}..{far:F0}")}" +
                          $", table {GteDepth.OtLength} entries");

        // The head of the table in submission order. A skybox lives here: linked at
        // the far end so it draws first, and projecting near because it is a small
        // box around the camera. `ot` against `z` is the disagreement, in one line.
        int heads = 0;
        foreach (var t in all)
        {
            if (t.Area < GteDepth.CensusBigArea) continue;
            Console.WriteLine("    head " + Describe(t));
            if (++heads == 3) break;
        }

        // The frame's largest surfaces, whatever their table position. A skybox is
        // by construction one of these — it is the thing behind everything, so it
        // covers whatever the world does not — and this is the line that says
        // whether it depth-tested and at what depth. Sorted by area, so it does not
        // matter where in the table it turned up.
        var byArea = new int[all.Length];
        for (int i = 0; i < byArea.Length; i++) byArea[i] = i;
        Array.Sort(byArea, (a, b) => all[b].Area.CompareTo(all[a].Area));
        for (int i = 0; i < byArea.Length && i < 6; i++)
        {
            if (all[byArea[i]].Area < GteDepth.CensusBigArea) break;
            Console.WriteLine("    largest " + Describe(all[byArea[i]]));
        }

        // Entirely in front of, and overlapping: the later polygon loses every pixel
        // the two share. Every polygon is a candidate victim regardless of size — a
        // wall at distance is a crowd of small triangles, and one large surface
        // standing in front of the crowd is exactly the shape of a hole. Partial
        // overlaps in depth are left out on purpose: those are the interpenetrations
        // the Z-buffer exists to sort out.
        var cost = new long[all.Length];
        var hides = new int[all.Length];
        long hiddenArea = 0;
        int hiddenCount = 0;
        for (int i = 0; i < all.Length; i++)
        {
            if (!all[i].Tested) continue;
            bool lost = false;
            for (int j = 0; j < all.Length; j++)
            {
                if (j == i || !all[j].Tested) continue;
                // j is earlier in the walk, so the game sorted it as farther away.
                if (j > i) continue;
                if (all[j].MaxZ >= all[i].MinZ || !all[j].Overlaps(all[i])) continue;
                long w = Math.Min(all[j].X1, all[i].X1) - Math.Max(all[j].X0, all[i].X0) + 1;
                long h = Math.Min(all[j].Y1, all[i].Y1) - Math.Max(all[j].Y0, all[i].Y0) + 1;
                cost[j] += w * h;
                hides[j]++;
                lost = true;
            }
            if (lost) { hiddenCount++; hiddenArea += all[i].Area; }
        }

        var rank = new int[all.Length];
        for (int i = 0; i < rank.Length; i++) rank[i] = i;
        Array.Sort(rank, (a, b) => cost[b].CompareTo(cost[a]));

        int shown = 0;
        foreach (int i in rank)
        {
            if (cost[i] == 0 || shown++ == 6) break;
            Console.WriteLine($"    hides {Describe(all[i])} " +
                              $"-> in front of {hides[i]} later, {cost[i]}px hidden");
        }

        if (shown == 0)
            Console.WriteLine("    nothing is entirely in front of anything the table put nearer — " +
                              "the hole is not one primitive winning the depth test");
        else
            Console.WriteLine($"    {hiddenCount} polys ({hiddenArea}px of bbox) lose ground to " +
                              "something the table sorted farther away");

        ReportDepthMap();
        GteDepth.WantDepthMap = true;
    }

    /// <summary>
    /// The depth buffer the frame actually ended up with, as a picture made of
    /// digits: the nearest surface in each cell of the screen, on a scale from the
    /// frame's own nearest to its farthest. `.` is a cell nothing depth-tested at
    /// all — 2D, or geometry that stayed on painter's order.
    ///
    /// This is the one measurement that does not depend on reasoning about what
    /// should have happened. A hole in the picture with a *near* digit sitting in it
    /// is something claiming the foreground there; a hole with `.` in it was never
    /// depth-tested and the Z-buffer is not what emptied it.
    /// </summary>
    static void ReportDepthMap()
    {
        var map = GteDepth.DepthMap;
        if (map == null) return;

        float lo = float.MaxValue, hi = 0f;
        foreach (float v in map)
        {
            if (v >= GteDepth.DepthMapEmpty) continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        Console.WriteLine($"    depth batches: {GteDepth.ZBatchRt} to a target with a depth buffer, " +
                          $"{GteDepth.ZBatchVram} to VRAM, which has none" +
                          $"{(GteDepth.ZRtRecreated == 0 ? "" : $"; {GteDepth.ZRtRecreated} targets rebuilt, each losing its depth")}");
        GteDepth.ZBatchRt = GteDepth.ZBatchVram = GteDepth.ZRtRecreated = 0;

        if (lo > hi) { Console.WriteLine("    depth map: nothing written — no pixel depth-tested"); return; }

        Console.WriteLine($"    depth map (0 = nearest {lo:F0}, 9 = farthest {hi:F0}, . = untested):");
        float span = Math.Max(1f, hi - lo);
        var line = new char[GteDepth.DepthMapCols];
        for (int r = 0; r < GteDepth.DepthMapRows; r++)
        {
            for (int c = 0; c < GteDepth.DepthMapCols; c++)
            {
                float v = map[r * GteDepth.DepthMapCols + c];
                line[c] = v >= GteDepth.DepthMapEmpty ? '.' : (char)('0' + (int)Math.Min(9, (v - lo) * 10f / span));
            }
            Console.WriteLine("      " + new string(line));
        }
    }

    static string Describe(in GteDepth.BigTri t) =>
        $"#{t.Order,-4} ot {t.Ot,-5} {(t.Tested ? "z" : "PAINTER'S, z")} {t.MinZ,6:F0}..{t.MaxZ,-6:F0} " +
        $"({t.X0},{t.Y0})-({t.X1},{t.Y1}) {t.Area,6}px " +
        $"{(t.Tex ? "tex " : "flat")}{(t.Semi ? " semi" : "     ")}" +
        $"{(t.Clamped ? " CLAMPED" : "        ")} rgb({t.R},{t.G},{t.B})";
}
