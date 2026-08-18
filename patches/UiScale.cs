using ImGuiNET;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;

namespace Kf2;

/// <summary>
/// The way back from an interface scaled too large to use.
///
///     KF2_UISCALE=1     force the interface scale for this run, and save it
///
/// The runtime multiplies its own <c>DpiScale</c> by the saved <c>UiScale</c> to get
/// <see cref="Theme.Scale"/>, and every popup is sized from that. The settings popup
/// is 780x500 logical against a 1280x720 window, so it stops fitting at a scale of
/// 1.44 — well inside UiScale's own 0.5-3 range, and reachable at a UiScale of 1 on
/// its own, because <c>QueryDpiScale</c> takes the primary monitor's GLFW content
/// scale and GLFW's Wayland path reports the integer <c>wl_output</c> scale: a
/// display the compositor runs at 1.15 arrives as 2.
///
/// `patches/recompone/0019` is the fix for that — a popup is clamped to the viewport,
/// so an oversized scale now costs scrolling rather than costing the controls, and
/// System > Settings > Interface stays reachable at any scale. This is the second
/// way out, for a configuration that is already unusable and a build that predates
/// the clamp: the value is written to interface.ini as well as applied, so one run
/// with the variable set repairs the saved config rather than having to be kept
/// around forever. Debug > Reset view is the third — it puts every view setting
/// back, not just this one.
///
/// The scale is applied on RuntimeReadyEvent rather than in <see cref="Configure"/>:
/// ConfigManager only loads inside HostWindow.Initialize, which is after Program.cs
/// has run, so a value set any earlier would be read straight back off disk. By
/// RuntimeReadyEvent the ImGui context exists and the window loop has not started,
/// and both fire on the same thread — the game and the interface share one here.
///
/// Nothing is registered under Settings: the runtime already draws this field under
/// Interface, and a second copy of one number is how the two disagree.
/// </summary>
public static class UiScale
{
    /// <summary>The runtime's own bounds, from ViewConfig.UiScale.</summary>
    const float Min = 0.5f, Max = 3f;

    static float? _forced;

    public static void Configure(string? scale)
    {
        if (string.IsNullOrWhiteSpace(scale)) return;

        if (!float.TryParse(scale.Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float v))
        {
            Console.Error.WriteLine($"[KF2] ui scale: cannot read '{scale}', ignored");
            return;
        }

        _forced = Math.Clamp(v, Min, Max);
    }

    public static void Install()
    {
        if (_forced is not { } scale) return;

        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            float was = view.UiScale;
            view.UiScale = scale;

            // The same three steps the settings field takes: the config holds the
            // number, io.FontGlobalScale scales the text, and Theme.Apply re-bakes
            // the style sizes — ScaleAllSizes multiplies a fresh set of base values
            // rather than the ones already scaled, so this does not compound.
            ImGui.GetIO().FontGlobalScale = scale;
            Theme.Apply();
            RecompOne.Runtime.Runtime.SaveView();

            Console.WriteLine($"[KF2] ui scale: {was:0.##}x -> {scale:0.##}x (saved)");
        });
    }
}
