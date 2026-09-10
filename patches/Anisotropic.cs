using RecompOne.Runtime;
using RecompOne.Runtime.Events;

namespace Kf2;

/// <summary>
/// Anisotropic filtering — the fix for the crawling, sparkling floor that a
/// receding textured surface turns into.
///
///     KF2_ANISO=8              taps along the footprint's long axis; 1 or off is
///                              the console's own single sample
///     KF2_ANISO_PROBE=1        report the level, and whether it reaches the shader
///
/// **What the artefact is.** A screen pixel does not cover a point of a texture,
/// it covers an area, and the shape of that area is the parallelogram spanned by
/// the two screen derivatives of the texture coordinate. Square-on to a wall it is
/// about a square. On a floor running away to the horizon, or along a corridor
/// wall seen edge-on, it is a long thin sliver — many texels along one axis and
/// barely one across the other. The console read a single texel out of that
/// sliver, and *which* texel it read changes completely for a sub-pixel movement
/// of the camera, so the floor crawls and sparkles as you walk. It is the
/// minification half of the same story <see cref="Perspective"/> tells about
/// interpolation: the hardware had one texel of budget and spent it at the centre.
///
/// **Why it is a shader change and not a sampler setting.** The obvious
/// implementation — <c>GL_TEXTURE_MAX_ANISOTROPY</c> on the VRAM sampler — does
/// nothing at all here, and the two reasons are the interesting part:
///
/// <list type="bullet">
/// <item>The game's textures are never sampled by a GL sampler. The fragment
/// shader <c>texelFetch</c>es a 1024x512 sheet that holds every texture page,
/// every CLUT and both display buffers at once, and no filter may run across it —
/// linear sampling on that sheet bleeds one texture page into the next and one
/// palette into the palette beside it.</item>
/// <item>In the 4-bit and 8-bit modes the value read from the page is a CLUT
/// *index*, not a colour. The average of index 3 and index 4 is index 3.5, an
/// unrelated colour with no relation to either. Any filter has to run after the
/// palette lookup, which is inside the shader by construction.</item>
/// </list>
///
/// So <c>patches/recompone/0041</c> does the work — a <c>decode()</c> function
/// holding the whole per-texel job (texture window, page wrap, nibble extract,
/// CLUT lookup) and a kernel that calls it once per texel along the long axis and
/// averages what comes back — and this patch is only the switch and the probe.
/// Both prim shaders,
/// core profile and GLSL 120.
///
/// **There is no mip chain and there cannot be one**, for the first reason above,
/// so this is supersampling rather than the mipmapped anisotropy a modern GPU
/// does: the taps run along the axis the footprint is longest on, **one texel
/// apart**, <c>min(ceil(len), level)</c> of them, centred on the pixel.
///
/// **The spacing is the part that had to be got right.** The first version spread
/// the taps across the *whole* major axis instead, which is what a mipmapped
/// filter does and is only correct there because each of its taps is a
/// pre-filtered sample covering the gap to the next. Here a tap is a point sample,
/// and a derivative is in unclamped texture-space units while a texture is not: at
/// the oblique angles this exists for the axis is tens or hundreds of texels, so
/// the taps wrapped past the texture's own width — by the texture window or by the
/// page — onto other art in the same 256x256 page, read through *this* primitive's
/// CLUT, which is an arbitrary entry of an unrelated palette. That was reported
/// from play as white speckle on the floor and the neighbouring sprite showing at
/// a fire billboard's edge, and as barely looking different, a strided
/// undersample of a long span being sixteen more chances to sparkle rather than a
/// low-pass of anything. The cost of the cap is that past <c>level</c> texels of
/// footprint the kernel filters a *part* of the footprint exactly rather than
/// estimating the whole of it, degrading toward nearest rather than toward a full
/// box filter. The other consequence worth recording is that a footprint that is
/// large on *both* axes is still averaged along one of them only — the short axis
/// keeps the console's single sample. That is the right trade here (this game
/// magnifies far more often than it minifies, and the artefact being chased is the
/// anisotropic one) but it is a limit rather than a completeness.
///
/// **Two things the hardware's encoding forces**, both shared with any filter
/// placed after the CLUT. A transparent texel is stored as black with the STP bit
/// clear, so a plain average next to a punch-through edge averages *black* in and
/// draws a dark fringe round every grate, torch and bush in the game; each tap is
/// therefore weighed by whether it is solid and the result renormalised by what
/// survived. **That is a statement about colour only** — the silhouette is the
/// centre tap's alone, so it is bit-for-bit where truncation put it, and the
/// kernel does not run on a transparent fragment at all. Deciding it by coverage
/// instead, which the first version did while still taking <c>texel.a</c> from the
/// centre tap, draws pixels the console did not: a sprite grows outward by up to
/// half a footprint, into the neighbouring art. And the semi-transparency
/// bit is a *mode*, not a colour: it picks whether the fragment goes through the
/// blend equation at all, so interpolating it would ask the GPU for a state
/// halfway between two blend equations. It is taken whole from the centre tap.
///
/// **It needs no "is this 3D" test, and that is worth stating** because the
/// port's other texture work does need one. The HUD, the menus and the 2D screens
/// are drawn at or near 1:1 and axis-aligned, so both
/// derivatives are about one texel, the long axis spans one texel, the tap count
/// comes out 1 and the fragment takes the unfiltered path — bit for bit, since it
/// is the same <c>decode()</c> call the single-sample path makes. **A billboard is
/// not in that list**, and the first version of this note said it was: a sprite is
/// a world-space quad and minifies with distance like anything else, so the kernel
/// genuinely runs on it. That is correct, and it is why the two defects above
/// showed on the fire first — a sprite is a small rectangle in a shared page, with
/// the least room either side of it for a filter to reach. The kernel is
/// self-gating on exactly the geometry it should be, with no varying to carry, no
/// dependence on whether the GTE vertex map answered, and nothing to go wrong when
/// perspective correction is switched off.
///
/// **Off by default.** The mechanism is measured — the shaders compile, link and
/// render at every level, <see cref="GteDepth.AnisotropyLive"/> says the uniform
/// reaches the program, and <c>scripts/shader_probe.c</c> reads the spread
/// collapsing monotonically — and the picture *has* now been looked at once, which
/// is where the two defects above came from. What has not been looked at is the
/// picture since they were fixed: whether a receding floor stops crawling, whether
/// the white speckle and the billboard's edge are gone, and how the average reads
/// against the 15-bit quantisation, since <c>quant5</c> still crushes the filtered
/// result to five bits unless true color is also on and the two have never been
/// seen together.
///
/// GL backend only; the software rasterizer is always a single sample. See
/// "Anisotropic filtering" in docs/RENDERING.md.
/// </summary>
public static class Anisotropic
{
    /// <summary>Where the choice is kept between runs.</summary>
    public const string LevelKey = "kf2.aniso.level";

    /// <summary>The most taps the kernel may take along the footprint's long axis.
    /// The shader's own loop is bounded at this, so raising it needs the shader
    /// changed too.</summary>
    public const int Max = 16;

    /// <summary>1 is off — the console's single point sample.</summary>
    public static int Level
    {
        get => GteDepth.Anisotropy;
        private set => GteDepth.Anisotropy = Clamp(value);
    }

    public static bool Enabled => Level > 1;

    /// <summary>KF2_ANISO: an explicit choice on the command line, which wins over
    /// the saved setting for the run.</summary>
    static int? _forced;

    /// <summary>KF2_ANISO_PROBE: report the level and whether it is reaching the
    /// shader.</summary>
    static bool _toConsole;

    static bool _reported;

    /// <summary>Vblanks waited for a batch before reporting the negative. The
    /// uniform is only uploaded when a primitive batch is submitted, so at a
    /// screen that has drawn none yet — the very first frames — "not bound" and
    /// "nothing drawn yet" look the same. Two seconds is long enough that only the
    /// first is left.</summary>
    const int ReportAfter = 120;

    static int _waited;

    static int Clamp(int v) => v < 1 ? 1 : v > Max ? Max : v;

    public static void Configure(string? level, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(level))
        {
            // "0" and "off" both mean the console's single sample, so the switch
            // reads the same way as the port's other picture switches even though
            // its value is a count rather than a flag.
            if (level.Equals("off", StringComparison.OrdinalIgnoreCase))
                _forced = 1;
            else if (int.TryParse(level, out int n))
                _forced = Clamp(n < 1 ? 1 : n);
        }

        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
            _toConsole = true;
    }

    public static void Install()
    {
        // Default is off. RuntimeReadyEvent is the first and only place the saved
        // setting is read: ConfigManager only loads inside HostWindow.Initialize,
        // which is after Program.cs, so reading it here would read an empty config
        // and write it back over the real one.
        Level = _forced ?? 1;

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Level = _forced ?? RecompOne.Runtime.Runtime.View.GetInt(LevelKey, 1);
            Console.WriteLine($"[KF2] aniso: {(Enabled ? $"on, up to {Level} taps" : "off (one sample)")}");
        });

        if (!_toConsole) return;

        // The prim program is built on the first present, so the uniform cannot be
        // asked about at RuntimeReady. Report once, on the first frame that has
        // actually uploaded it -- and report the negative too, which is the whole
        // point of the flag.
        Event.AddListener<VSyncEvent>(_ =>
        {
            if (_reported) return;

            // Report as soon as a batch has actually uploaded it, and otherwise
            // only once enough frames have gone by that no batch is the answer --
            // reporting the negative on the first vblank would be reporting that
            // nothing had been drawn yet.
            if (!GteDepth.AnisotropyLive && ++_waited < ReportAfter) return;

            _reported = true;
            Console.WriteLine($"[KF2] aniso: level {Level}, uniform " +
                              (GteDepth.AnisotropyLive
                                  ? "bound and uploading"
                                  : $"NOT bound after {ReportAfter} frames -- the kernel is not running"));
        });
    }

    /// <summary>Change the level at run time. Nothing is rebuilt — it is a plain
    /// uniform the next batch reads.</summary>
    public static void SetLevel(int level) => Level = level;
}
