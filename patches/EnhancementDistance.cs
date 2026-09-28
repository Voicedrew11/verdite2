using RecompOne.Runtime;
using RecompOne.Runtime.Events;

namespace Kf2;

/// <summary>
/// How far from the camera the port's enhancements reach; past it a surface is drawn
/// the game's own way (<c>GteDepth.PlainDepth</c>, 0083).
///
///     KF2_ENHANCEDIST=8     tiles of view depth; 0 is everywhere enhanced
///
/// Past the distance: the corner colours rather than per-pixel lighting, no authored
/// light or highlight, the console's one texel rather than the filter, no ripple, no
/// occlusion, no reflection or murk. Faded in over the tile before it. Perspective,
/// sub-pixel and the Z-buffer stay on. See "The enhancement distance" in
/// docs/RENDERING.md.
/// </summary>
public static class EnhancementDistance
{
    public const string Key = "kf2.enhancedistance";

    /// <summary>The slider's top, which reads as everywhere.</summary>
    public const float Max = 16f;
    public const float Min = 2f;

    static float? _forced;

    /// <summary>Tiles; 0 is everywhere enhanced.</summary>
    public static float Tiles { get; private set; }

    public static void Configure(string? tiles)
    {
        if (float.TryParse(tiles, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float t))
            _forced = t;
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Set(_forced ?? RecompOne.Runtime.Runtime.View.GetFloat(Key, 0f));
            Console.WriteLine($"[KF2] enhancement distance: {(Tiles > 0f ? $"{Tiles:0.#} tiles" : "everywhere")}");
        });
    }

    public static void Set(float tiles)
    {
        Tiles = tiles <= 0f || tiles >= Max ? 0f : Math.Max(tiles, Min);
        GteDepth.PlainDepth = Tiles * 2048f;
    }
}
