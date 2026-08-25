using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The rate the port draws at, under Video beside vsync and render scale.
///
/// It sits there because that is where a user looks for a frame rate, but it is
/// not quite the graphics option it resembles: King's Field's speed *is* its
/// frame rate, so anything above 30 also means "and tick the world on its own
/// 30 Hz clock", which is what <see cref="FramePacing"/> does. The note under the
/// combo says so rather than leaving a player to find out by playing.
///
/// The list is presets plus a free number, because "arbitrary" is the point: a
/// player on a 165 Hz panel should be able to say 165. The slider only appears
/// once Custom is chosen, so the common case stays one control.
/// </summary>
public sealed class FramePacingPage : IPatchPage
{
    public string Id => "framepacing";
    public string Title => "Frame pacing";

    // Index into Rates; both arrays are read together. 0 is uncapped, -1 is
    // "whatever the custom slider says".
    static readonly double[] Rates = [30.0, 60.0, 90.0, 120.0, 144.0, 0.0, -1.0];

    static readonly string[] Labels =
    [
        "30 fps - the game's own rate",
        "60 fps",
        "90 fps",
        "120 fps",
        "144 fps",
        "Uncapped - runs too fast",
        "Custom...",
    ];

    static float _custom = 75f;
    static bool _customChosen;

    public void Draw()
    {
        int index = Index();

        // No SetNextItemWidth: this matches the GPU backend combo it sits under.
        if (ImGui.Combo("Frame rate", ref index, Labels, Labels.Length))
        {
            _customChosen = Rates[index] < 0.0;
            if (_customChosen) Apply(_custom);
            else Apply(Rates[index]);
        }

        if (_customChosen)
        {
            if (ImGui.SliderFloat("Rate", ref _custom, 20f, 300f, "%.0f fps",
                                  ImGuiSliderFlags.AlwaysClamp))
                Apply(_custom);
        }

        Note(FramePacing.Measured > 0.0
            ? $"Measured: {FramePacing.Measured:F1} fps"
            : "Measured: waiting for the first second of frames");

        ImGui.Spacing();
        Note(Describe());
    }

    static int Index()
    {
        if (_customChosen) return Rates.Length - 1;

        double rate = FramePacing.Enabled ? FramePacing.TargetFps : 0.0;
        for (int i = 0; i < Rates.Length; i++)
            if (Rates[i] >= 0.0 && Math.Abs(Rates[i] - rate) < 0.01) return i;

        // A rate that is not one of the presets -- from KF2_FPS, or from a config
        // written by an older build -- shows as Custom rather than silently
        // snapping to 30.
        _custom = (float)rate;
        _customChosen = true;
        return Rates.Length - 1;
    }

    static void Apply(double rate)
    {
        FramePacing.SetTargetFps(rate);
        PatchSettings.Set(FramePacing.FpsKey, (float)(FramePacing.Enabled ? FramePacing.TargetFps : 0.0));
    }

    static string Describe()
    {
        if (!FramePacing.Enabled)
            return "No pacing at all. The port draws as fast as it can and the game advances a " +
                   "fixed amount per frame, so everything moves far faster than it ever did on " +
                   "hardware. A diagnostic setting.";

        if (FramePacing.TargetFps <= FramePacing.LogicHz + 0.001)
            return "The game paces itself, exactly as it does on hardware: it waits for two " +
                   "vblanks and advances one step. 30 is its ceiling there.";

        return $"Draws at {FramePacing.TargetFps:0.#} and runs the game's own stages on a 30 Hz " +
               "clock, so the world keeps hardware timing while the picture updates more often. " +
               "The view is carried between ticks by the frame smoothing below.";
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
