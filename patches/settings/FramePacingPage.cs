using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The rate the port draws at and the rate the world runs at, under Video beside
/// vsync and render scale.
///
/// The frame rate sits there because that is where a user looks for one, but it is
/// not quite the graphics option it resembles: King's Field's speed *is* its frame
/// rate, so the port has to hold the world to a clock of its own whatever it draws
/// at, which is what <see cref="FramePacing"/> does. The note under the combo says
/// so rather than leaving a player to find out by playing.
///
/// **The tick rate is the second control and it is not a graphics option at all.**
/// It is here because the two numbers are only meaningful against each other -- a
/// frame rate below the tick rate makes the smoothing pointless, one above it
/// makes it load-bearing -- and splitting them across two panes would hide that.
/// It has two entries because the argument has two answers: 20, the band the
/// console actually landed in under load, and 30, what the game's own frame gate
/// asks for. See FramePacing.LogicHz.
///
/// The frame-rate list is presets plus a free number, because "arbitrary" is the
/// point: a player on a 165 Hz panel should be able to say 165. The slider only
/// appears once Custom is chosen, so the common case stays one control.
/// </summary>
public sealed class FramePacingPage : IPatchPage
{
    public string Id => "framepacing";
    public string Title => "Frame pacing";

    // Index into Rates; both arrays are read together. 0 is uncapped, -1 is
    // "whatever the custom slider says".
    static readonly double[] Rates = [20.0, 30.0, 60.0, 90.0, 120.0, 144.0, 0.0, -1.0];

    static readonly string[] Labels =
    [
        "20 fps - the rate on hardware",
        "30 fps - what the code asks for",
        "60 fps",
        "90 fps",
        "120 fps",
        "144 fps",
        "Uncapped - the world still ticks",
        "Custom...",
    ];

    // The tick rate has no custom entry: the game's own achievable bands are 60/n,
    // and the only two anyone can argue for are these. KF2_TICKRATE takes any
    // number for experimenting.
    static readonly double[] TickRates = [20.0, 30.0];

    static readonly string[] TickLabels =
    [
        "20 Hz - the speed the console played at",
        "30 Hz - the speed the code asks for",
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
            if (ImGui.SliderFloat("Rate", ref _custom, 10f, 300f, "%.0f fps",
                                  ImGuiSliderFlags.AlwaysClamp))
                Apply(_custom);
        }

        Note(FramePacing.Measured > 0.0
            ? $"Measured: {FramePacing.Measured:F1} fps"
            : "Measured: waiting for the first second of frames");

        ImGui.Spacing();

        int tick = TickIndex();
        if (ImGui.Combo("World tick rate", ref tick, TickLabels, TickLabels.Length))
        {
            FramePacing.SetLogicHz(TickRates[tick]);
            PatchSettings.Set(FramePacing.LogicHzKey, (float)FramePacing.LogicHz);
        }

        ImGui.Spacing();
        Note(Describe());
    }

    /// <summary>A tick rate from KF2_TICKRATE that is neither preset shows as the
    /// nearer of the two rather than being silently snapped, since the combo has
    /// nowhere to put it and the note below spells the real number out.</summary>
    static int TickIndex()
    {
        int best = 0;
        for (int i = 1; i < TickRates.Length; i++)
            if (Math.Abs(TickRates[i] - FramePacing.LogicHz)
                < Math.Abs(TickRates[best] - FramePacing.LogicHz)) best = i;
        return best;
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
        string hz = $"{FramePacing.LogicHz:0.#} Hz";

        if (!FramePacing.Enabled)
            return $"No pacing at all: the port draws as fast as it can. The world still runs at " +
                   $"{hz}, so the game does not speed up -- only the picture is unbounded, and " +
                   "the view between ticks is carried by the frame smoothing below.";

        if (FramePacing.TargetFps < FramePacing.LogicHz - 0.001)
            return $"Drawing at {FramePacing.TargetFps:0.#} fps, below the world's own {hz}. A " +
                   "stage can be skipped but not run twice, so the world cannot catch up: it " +
                   $"ticks once per frame and the whole game plays slower than {hz}. A " +
                   "diagnostic setting.";

        if (FramePacing.TargetFps <= FramePacing.LogicHz + 0.001)
            return $"One drawn frame per tick, at {hz} -- the console's own arrangement, where " +
                   "the game's speed and its frame rate are the same number. Nothing is " +
                   "extrapolated, so the frame smoothing below does nothing here.";

        return $"Draws at {FramePacing.TargetFps:0.#} and runs the game's own stages on a {hz} " +
               "clock, so the world keeps a console's timing while the picture updates more " +
               "often. The view is carried between ticks by the frame smoothing below.";
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
