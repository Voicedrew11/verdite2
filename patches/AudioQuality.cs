using RecompOne.Runtime;
using RecompOne.Runtime.Events;

namespace Kf2;

/// <summary>
/// Voice interpolation and reverb. The DSP is in the runtime's <c>Spu</c>
/// (<c>patches/recompone/0043</c>); this is the switch. See docs/AUDIO.md.
///
///     KF2_SPU_INTERP=gauss|cubic|sinc
///     KF2_REVERB=legacy|hardware|enhanced
/// </summary>
public static class AudioQuality
{
    public const string InterpKey = "kf2.audio.interp";
    public const string ReverbKey = "kf2.audio.reverb";

    // What the settings page calls Original. Hardware (the console's FIR) once it has been listened to.
    public const SpuReverbMode OriginalReverb = SpuReverbMode.Legacy;

    static SpuInterpolation? _interp;
    static SpuReverbMode? _reverb;

    public static void Configure(string? interp, string? reverb)
    {
        if (!string.IsNullOrWhiteSpace(interp))
        {
            _interp = interp.Trim().ToLowerInvariant() switch
            {
                "gauss" or "gaussian" => SpuInterpolation.Gaussian,
                "cubic" => SpuInterpolation.Cubic,
                "sinc" => SpuInterpolation.Sinc,
                _ => null
            };
            if (_interp == null) Console.Error.WriteLine($"[KF2] audio: KF2_SPU_INTERP=\"{interp}\" is not gauss, cubic or sinc");
        }

        if (!string.IsNullOrWhiteSpace(reverb))
        {
            _reverb = reverb.Trim().ToLowerInvariant() switch
            {
                "legacy" => SpuReverbMode.Legacy,
                "hardware" => SpuReverbMode.Hardware,
                "enhanced" => SpuReverbMode.Enhanced,
                _ => null
            };
            if (_reverb == null) Console.Error.WriteLine($"[KF2] audio: KF2_REVERB=\"{reverb}\" is not legacy, hardware or enhanced");
        }
    }

    public static void Install()
    {
        Spu.Interpolation = _interp ?? SpuInterpolation.Gaussian;
        Spu.ReverbMode = _reverb ?? OriginalReverb;

        // The saved choice can only be read once ConfigManager has loaded, which is after Program.cs.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            Spu.Interpolation = _interp ?? (SpuInterpolation)Math.Clamp(view.GetInt(InterpKey, 0), 0, 2);
            Spu.ReverbMode = _reverb ?? (view.GetInt(ReverbKey, 0) == 1 ? SpuReverbMode.Enhanced : OriginalReverb);
            Console.WriteLine($"[KF2] audio: interpolation {Spu.Interpolation}, reverb {Spu.ReverbMode}");
        });
    }

    public static void SetInterpolation(SpuInterpolation mode) => Spu.Interpolation = mode;

    public static void SetEnhancedReverb(bool on) => Spu.ReverbMode = on ? SpuReverbMode.Enhanced : OriginalReverb;
}
