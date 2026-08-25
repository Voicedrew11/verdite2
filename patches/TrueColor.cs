using RecompOne.Runtime;
using RecompOne.Runtime.Events;

namespace Kf2;

/// <summary>
/// True color (24-bit) output for the GL backend.
///
///     KF2_TRUECOLOR=1          on; 0 or unset keeps the console's 15-bit output
///
/// The PlayStation renders into 15-bit VRAM (RGB5A1), so every shaded pixel is
/// quantised to five bits per channel. On a smooth fog gradient — a wall darkening
/// with distance — that steps into visible bands, and the GPU's ordered dither is
/// the only thing that hides them, at the cost of the 4x4 crosshatch. With the
/// dither off (this port's default) the bands show raw.
///
/// True color removes the banding without the crosshatch: the GL backend's display
/// render target becomes RGBA8 and the fragment shader's <c>quant5</c> keeps eight
/// bits instead of truncating to five. Only the shaded gradient gains precision —
/// textures still live in 15-bit VRAM and are sampled at five bits, so the picture
/// stays authentic where the console's own precision was the texture, and only
/// stops banding where the console's precision was the framebuffer.
///
/// Like the other picture switches this patch is only the switch; the work is in
/// the runtime (<c>patches/recompone/0021</c>), because a pixel's precision is
/// decided in the render target's format and the fragment shader, far below
/// anything <c>HookManager</c> can reach. Off by default, so the default picture is
/// the console's. The software rasterizer is always 15-bit; this affects the GL
/// backend alone. See "True color" in NOTES.md.
/// </summary>
public static class TrueColor
{
    /// <summary>Where the choice is kept between runs.</summary>
    public const string OnKey = "kf2.truecolor.on";

    /// <summary>False keeps the console's 15-bit output and the fragment shader's
    /// five-bit truncation.</summary>
    public static bool Enabled
    {
        get => GteDepth.TrueColor;
        private set => GteDepth.TrueColor = value;
    }

    /// <summary>KF2_TRUECOLOR: an explicit choice on the command line, which wins
    /// over the saved setting for the run.</summary>
    static bool? _forced;

    public static void Configure(string? on)
    {
        if (!string.IsNullOrWhiteSpace(on))
            _forced = !on.Equals("0", StringComparison.Ordinal);
    }

    public static void Install()
    {
        // Default is off: RuntimeReadyEvent is the first and only place the saved
        // setting is read. ConfigManager only loads inside HostWindow.Initialize,
        // which is after Program.cs, so reading it here would read an empty config
        // and write it back over the real one.
        Enabled = _forced ?? false;

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, false);
            Console.WriteLine($"[KF2] truecolor: {(Enabled ? "on (24-bit)" : "off (15-bit)")}");
        });
    }

    /// <summary>Change the setting at run time. The GL backend rebuilds its display
    /// targets in the new pixel format at the next present.</summary>
    public static void SetEnabled(bool on) => Enabled = on;
}
