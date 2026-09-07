using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The rate the port draws at, under Video beside vsync and render scale.
///
/// The frame rate sits there because that is where a user looks for one, but it is
/// not quite the graphics option it resembles: King's Field's speed *is* its frame
/// rate, so the port has to hold the world to a clock of its own whatever it draws
/// at, which is what <see cref="FramePacing"/> does. The note under the combo says
/// so rather than leaving a player to find out by playing.
///
/// **The world's tick rate was the second control and it is gone.** It offered
/// two entries -- 20, the band the console actually landed in, and 30, the fastest
/// the game's own frame gate permits -- and offering both made the *player* settle
/// a question about what King's Field's speed is. It is 20: that is the speed the
/// game was built and played at, it is what every rate in this port is measured
/// against, and 30 is a comparison rather than a preference. <c>KF2_TICKRATE</c> still takes any rate, which is where a
/// comparison belongs. See FramePacing.LogicHz.
///
/// What is left is one number, and one line under it saying that the number is the
/// picture only -- which is the part a frame-rate combo in any other game would not
/// have to explain. It used to be a paragraph that changed with the rate, spelling
/// out the below-tick case, the 1:1 case and the extrapolated case; a player
/// choosing 144 does not need the other two, and the only fact any of them carried
/// that a player acts on is that the game does not speed up.
///
/// The frame-rate list is the panels people own plus a free number, because
/// "arbitrary" is the point. **Uncapped came out**: the entry existed, and what it
/// produced was not a working uncapped port, so offering it was offering a defect.
/// <c>KF2_FPS=off</c> still reaches it, which is where an unbounded picture belongs
/// until it is fixed -- and a config already sitting there still opens here, as
/// Custom, rather than snapping to a preset.
/// </summary>
public sealed class FramePacingPage : IPatchPage
{
    public string Id => "framepacing";
    public string Title => "Frame pacing";

    // Index into Rates; both arrays are read together. -1 is "whatever the custom
    // slider says".
    static readonly double[] Rates =
        [20.0, 30.0, 60.0, 75.0, 90.0, 120.0, 144.0, 165.0, 170.0, 240.0, -1.0];

    static readonly string[] Labels =
    [
        "20 fps",
        "30 fps",
        "60 fps",
        "75 fps",
        "90 fps",
        "120 fps",
        "144 fps",
        "165 fps",
        "170 fps",
        "240 fps",
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
            if (ImGui.SliderFloat("Rate", ref _custom, 10f, 300f, "%.0f fps",
                                  ImGuiSliderFlags.AlwaysClamp))
                Apply(_custom);
        }

        Note(FramePacing.Measured > 0.0
            ? $"Measured: {FramePacing.Measured:F1} fps"
            : "Measured: waiting for the first second of frames");

        ImGui.Spacing();
        Note("Picture only: the game's own speed does not change with this.");
    }

    static int Index()
    {
        if (_customChosen) return Rates.Length - 1;

        double rate = FramePacing.Enabled ? FramePacing.TargetFps : 0.0;
        for (int i = 0; i < Rates.Length; i++)
            if (Rates[i] >= 0.0 && Math.Abs(Rates[i] - rate) < 0.01) return i;

        // A rate that is not one of the presets -- from KF2_FPS, or from a config
        // written by an older build -- shows as Custom rather than silently
        // snapping to 30. Pacing switched off entirely (KF2_FPS=off) has no entry
        // at all now, so it lands here too: the slider is clamped rather than
        // parked at 0, since 0 is a rate no control on this page can produce and
        // one drag of the slider would leave it anyway.
        _custom = (float)Math.Clamp(rate > 0.0 ? rate : FramePacing.LogicHz, 10.0, 300.0);
        _customChosen = true;
        return Rates.Length - 1;
    }

    static void Apply(double rate)
    {
        FramePacing.SetTargetFps(rate);
        PatchSettings.Set(FramePacing.FpsKey, (float)(FramePacing.Enabled ? FramePacing.TargetFps : 0.0));
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
