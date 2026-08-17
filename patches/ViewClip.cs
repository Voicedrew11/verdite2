using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Opens the game's view-space clip volume so a wide picture is not cut by it.
///
///     KF2_VIEWCLIP=0      leave the clip volume at its 4:3 shape
///     KF2_VIEWCLIP=1.5    force a widening factor instead of the aspect's
///     KF2_VIEWCLIP_PROBE=1  what it wrote, and where the cut lands on screen
///
/// ## The second cull, and the one that leaves a straight edge
///
/// `func_80030540` checks every polygon's screen-space vertex deltas against the
/// GPU's own limits — `|dy| ≤ 511`, `|dx| ≤ 1023` — and sends anything too big to
/// **`func_8005CAC8`**, a six-plane Sutherland–Hodgman clipper in view space
/// (`func_8005CCD8` builds the records, `func_8005CE98` clips, `func_8005D8E8`
/// re-projects), dropping the polygon if fewer than three vertices survive.
///
/// Which polygons are too big for the GPU? **The near-camera floor and ceiling.**
/// So this is the cull that governs the bottom and top corners of the picture, and
/// because it is a plane rather than a per-object test, what it removes has a
/// **straight edge** — which is how it was told apart from everything else.
///
/// Its bounds are two words, set once by
/// `func_8005D7CC(ws, 320, 240, 100, near, far)`:
///
///     0x800FC97C = (320/2) &lt;&lt; 12 / 100 = 6553   tan of the horizontal half-angle
///     0x800FC984 = 100 &lt;&lt; 12 / (320/2) = 2560   and its reciprocal, for
///                                               interpolating a clipped vertex
///     0x800FC98C / 0x800FC994                   the vertical pair, 4915 / 3413
///
/// The projection distance it is handed is **100, while the GTE's is 200** —
/// `func_8005B2D4` is `SetGeomScreen` and is called with `0xC8` at all three of its
/// sites. So the clip volume is deliberately **twice the screen frustum**: a guard
/// band that keeps a subdivided polygon under the GPU's 1023-pixel limit without
/// ever cutting anything a 4:3 player could see.
///
/// Twice the frustum puts the cut at `200 × 1.6 = 320` pixels either side of
/// centre. The picture reaches 160 at 4:3, 214 at 16:9, 284 at 21:9 — and **360 at
/// 3:1**, which is `Widescreen.Widest`. So past about **8:3 (2.67:1)** the game's
/// own clipper starts cutting the visible picture, floor and ceiling first because
/// they are what goes through it, with a straight vertical edge 320 pixels out.
///
/// <see cref="Apply"/> raises the horizontal tangent so the cut always lands
/// outside the picture, and never lowers it: below 2.67:1 it writes the shipped
/// values back and changes nothing. The vertical pair is left alone — a wider
/// aspect does not make the picture taller.
///
/// **The cap is the reason the guard band exists.** `GpuRaster` drops a primitive
/// whose span exceeds 1023 pixels, faithfully to hardware, so widening the clip
/// without a limit would trade a cut edge for whole polygons vanishing. The
/// tangent is capped at <see cref="MaxSpan"/> / (2 · H).
///
/// See "The second cull" in NOTES.md.
/// </summary>
public static class ViewClip
{
    /// <summary>func_8005D7CC, which computes the four words. One caller, in the
    /// game's init, so a post-hook on it is the moment they first exist.</summary>
    const uint SetupRoutine = 0x8005D7CC;

    /// <summary>tan of the clip volume's horizontal half-angle, 12.12.</summary>
    const uint TanX = 0x800FC97C;

    /// <summary>Its reciprocal, 12.12, used when interpolating a clipped vertex.</summary>
    const uint RecipX = 0x800FC984;

    /// <summary>What the game computes: 160&lt;&lt;12/100 and 100&lt;&lt;12/160.</summary>
    const int StockTan = 6553;
    const int StockRecip = 2560;

    /// <summary>The GTE's projection distance — `SetGeomScreen(0xC8)`, at all three
    /// call sites. A view-space `X/Z` of `t` lands `H · t` pixels from centre.</summary>
    const int H = 200;

    /// <summary>This game's screen, and so the half-width the margin is added to.</summary>
    const int ScreenWidth = 320;

    /// <summary>How far outside the picture the cut should stay, in pixels. Small:
    /// the point is only that the clip is not the thing you can see.</summary>
    const int Slack = 24;

    /// <summary>The widest span a primitive may have before <c>GpuRaster</c> drops
    /// it, which is what the game's guard band exists to respect. Left under the
    /// hardware's 1023 so a clipped polygon has somewhere to round to.</summary>
    const int MaxSpan = 1000;

    /// <summary>Where the choice is kept between runs.</summary>
    public const string Key = "kf2.widescreen.widenclip";

    /// <summary>Open the clip volume with the aspect. On by default, and a no-op
    /// until the picture is wider than the guard band the game already has.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>The tangent in force, 12.12. <see cref="StockTan"/> means the
    /// game's own.</summary>
    public static int Tan { get; private set; } = StockTan;

    static bool? _forced;
    static float? _forcedFactor;
    static bool _measure;

    // Set by the post-hook: the words exist and match what the game should have
    // computed. Nothing is written before that, and nothing at all if they did not.
    static bool _ready, _refused;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.viewclip",
        Name = "View clip",
        Version = "1.0",
        Description = "Opens the game's view-space clip volume to the widescreen aspect.",
    };

    public static void Configure(string? widen, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(widen))
        {
            if (widen.Equals("0", StringComparison.Ordinal) ||
                widen.Equals("off", StringComparison.OrdinalIgnoreCase))
                _forced = false;
            else if (widen.Equals("1", StringComparison.Ordinal) ||
                     widen.Equals("on", StringComparison.OrdinalIgnoreCase))
                _forced = true;
            else if (float.TryParse(widen, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float f) && f > 0f)
            {
                _forced = true;
                _forcedFactor = Math.Clamp(f, 1f, 4f);
            }
        }

        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
            _measure = true;
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(Key, true);
            Apply();
        });

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;

            SymbolRegistry.Build();
            var target = SymbolRegistry.Resolve("game", null, SetupRoutine);
            var impl = typeof(ViewClip)
                .GetMethod(nameof(AfterSetup), BindingFlags.Public | BindingFlags.Static)!;

            if (target == null || !HookManager.AddPost(_self, target, impl))
            {
                Console.Error.WriteLine(
                    $"[KF2] view clip: nothing hooked at game/0x{SetupRoutine:X8} — the game's own " +
                    "clip volume will cut the picture past 2.67:1. See \"The second cull\" in NOTES.md.");
                _refused = true;
                return;
            }

            HookManager.Commit();
            Console.WriteLine($"[KF2] view clip: {(Enabled ? "follows the aspect" : "off")}");
        });
    }

    /// <summary>Turn the widening on or off at run time.</summary>
    public static void SetEnabled(bool on)
    {
        Enabled = on;
        Apply();
    }

    /// <summary>
    /// The game has just computed the four words, so this is the first moment they
    /// are the shipped values and the only moment they can be checked against them.
    /// </summary>
    public static void AfterSetup(CpuContext c, IMemory m)
    {
        int tan = (int)m.ReadU32(TanX), recip = (int)m.ReadU32(RecipX);
        if (tan != StockTan || recip != StockRecip)
        {
            _refused = true;
            Console.Error.WriteLine(
                $"[KF2] view clip: 0x{TanX:X8} is {tan}/{recip}, expected {StockTan}/{StockRecip} — " +
                "not the clip volume this was written against. Leaving it alone. " +
                "See \"The second cull\" in NOTES.md.");
            return;
        }

        _ready = true;
        Apply();
    }

    /// <summary>Called by <see cref="Widescreen"/> when the aspect moves, so the
    /// clip volume follows the picture.</summary>
    public static void Apply()
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null || _refused || !_ready) return;

        int want = StockTan;
        if (Enabled)
        {
            if (_forcedFactor is { } factor)
            {
                want = (int)MathF.Round(StockTan * factor);
            }
            else if (Widescreen.On)
            {
                // The cut lands H*tan pixels from centre; the picture reaches
                // half its widened width. Raise the tangent until the first is
                // outside the second, and never lower it -- below 8:3 the game's
                // own guard band is already wider than the picture.
                int half = ScreenWidth / 2 + Display.WideMargin(ScreenWidth);
                want = Math.Max(StockTan, ((half + Slack) * 4096 + H - 1) / H);
            }
        }

        // GpuRaster drops a primitive spanning more than 1023 pixels, which is the
        // whole reason the game clips at all, so the guard band shrinks rather than
        // scales once the picture is wide enough to need it.
        Tan = Math.Clamp(want, StockTan, MaxSpan * 4096 / (2 * H));

        m.WriteU32(TanX, (uint)Tan);
        m.WriteU32(RecipX, (uint)((4096L * 4096L + Tan / 2) / Tan));

        if (_measure)
            Console.WriteLine($"[KF2] view clip: tan {Tan / 4096.0:0.###} " +
                              $"(cut at ±{H * Tan / 4096} px from centre), " +
                              $"picture reaches ±{ScreenWidth / 2 + Display.WideMargin(ScreenWidth)} px" +
                              (Tan == StockTan ? " — the game's own, unchanged" : ""));
    }
}
