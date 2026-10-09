using RecompOne.Runtime;
using RecompOne.Runtime.Events;

namespace Kf2;

/// <summary>
/// The fog's distance cue, one choice of three (PORT SETTINGS ▸ WORLD ▸ Fog):
///
/// - at the corners (the game's): the GTE cues fog at each corner, from its
///   view depth, and the face is shaded between them;
/// - by pixel depth: each pixel is fogged by its own view depth;
/// - by distance from the eye: the same as pixel depth at the picture's centre, more
///   at its edges, and still as the view turns (runtime patch <c>0100</c>); the
///   default since 2026-10-09, the user's decision, as in Verdite3.
///
/// A kept <c>kf2.fog</c>, <c>KF2_FOG</c> or <c>KF2_GPUWORLD_FOGZ=0</c> still wins over
/// the default, so a player who chose pixel depth keeps it.
///
/// The GTE cues fog by SZ, the view depth, so a pixel at the side of a wide picture,
/// or below a camera looking down, is fogged as if it were nearer than it is, and the
/// fog moves as the view turns. Distance takes the cue at the eye's distance in the
/// shaders that recover a pixel's view position (the retained world, the packets with
/// a depth, the reflection pass), so the fog stays put with the view.
///
///     KF2_FOG=0 | corners      at the corners (the game's)
///     KF2_FOG=1 | depth        by pixel depth (the default before 2026-10-09)
///     KF2_FOG=2 | distance     by distance from the eye (also: radial; the default)
///     KF2_GPUWORLD_FOGZ=0      the old spelling of KF2_FOG=corners, still honoured
///
/// See "Fog by distance from the eye" in docs/RENDERING.md.
/// </summary>
public static class Fog
{
    public const string Key = "kf2.fog";
    public const int Corners = 0, Depth = 1, Distance = 2, Default = Distance;

    static int? _forced;

    /// <summary>The fog as it is now: <see cref="Corners"/>, <see cref="Depth"/> or <see cref="Distance"/>.</summary>
    public static int Mode => GteDepth.RadialFog ? Distance : RetainedScene.MainFogFromZ ? Depth : Corners;

    public static void Set(int mode)
    {
        mode = Math.Clamp(mode, Corners, Distance);
        RetainedScene.MainFogFromZ = mode >= Depth;
        GteDepth.RadialFog = mode == Distance;
    }

    public static void Configure(string? fog)
    {
        _forced = fog?.Trim().ToLowerInvariant() switch
        {
            "0" or "corners" => Corners,
            "1" or "depth" => Depth,
            "2" or "distance" or "radial" => Distance,
            _ => null,
        };
        if (_forced is null && Environment.GetEnvironmentVariable("KF2_GPUWORLD_FOGZ")?.Trim() == "0") _forced = Corners;
    }

    public static void Install()
    {
        Set(_forced ?? Default);
        // The saved key is only readable once the runtime is up (see EvenFog).
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            int mode = _forced ?? RecompOne.Runtime.Runtime.View.GetInt(Key, Default);
            Set(mode);
            Console.WriteLine(Mode switch
            {
                Distance => "[KF2] fog: by distance from the eye",
                Depth => "[KF2] fog: by pixel depth",
                _ => "[KF2] fog: at the corners (the game's)",
            });
        });
    }
}
