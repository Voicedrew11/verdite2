using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The rate the port runs at, under Display beside vsync and render scale.
///
/// It sits there because that is where a user looks for a frame rate, but it is
/// not the graphics option it resembles: King's Field's speed *is* its frame
/// rate, and 60 makes the game run twice as fast unless the main-loop stages
/// behind it are gated to every other frame -- which is why
/// <see cref="FramePacing"/> hooks that gate whether or not 60 was asked for.
/// See "Frame pacing" in NOTES.md.
/// </summary>
public sealed class FramePacingPage : IPatchPage
{
    public string Id => "framepacing";
    public string Title => "Frame pacing";

    // Index into Caps; both arrays are read together.
    static readonly int[] Caps = [2, 1, 3, 4, 0];

    static readonly string[] Labels =
    [
        "30 fps - hardware's fastest",
        "60 fps - world gated to 30",
        "20 fps",
        "15 fps",
        "Uncapped - runs too fast",
    ];

    public void Draw()
    {
        int index = Array.IndexOf(Caps, FramePacing.Cap);
        if (index < 0) index = 0;

        // No SetNextItemWidth: this matches the GPU backend combo it sits under.
        if (ImGui.Combo("Frame rate", ref index, Labels, Labels.Length))
        {
            FramePacing.SetCap(Caps[index]);
            PatchSettings.Set(FramePacing.VBlankKey, Caps[index]);
        }

        Note(FramePacing.Measured > 0.0
            ? $"Measured: {FramePacing.Measured:F1} fps"
            : "Measured: waiting for the first second of frames");

        ImGui.Spacing();
        Note(Caps[index] switch
        {
            0 => "No floor at all. The port draws as fast as it can and the game advances a " +
                 "fixed amount per frame, so everything moves faster than it ever did on " +
                 "hardware. A diagnostic setting.",
            1 => "Renders every vblank and runs the main loop's movement stages on every other " +
                 "one, so the picture is 60 and the world stays at 30. KF2_FPS_GATE picks " +
                 "different stages to gate.",
            _ => "Holds every rendered frame to a whole number of vblanks, which is how the game " +
                 "banded on hardware. 30 is its ceiling there.",
        });
    }

    /// <summary>Wrapped and dimmed. TextDisabled does not wrap, and unwrapped prose
    /// runs straight out of the settings window.</summary>
    static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
