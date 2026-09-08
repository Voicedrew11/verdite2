using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The rate the port draws at, under Video beside vsync and render scale.
///
/// The frame rate sits there because that is where a user looks for one, but it is
/// not quite the graphics option it resembles: King's Field's speed *is* its frame
/// rate, so the port has to hold the world to a clock of its own whatever it draws
/// at, which is what <see cref="FramePacing"/> does. The note under the slider says
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
/// **It was a combo of presets, then a continuous slider with magnets, and it is a
/// slider with fixed positions.** The combo made the free number a *mode* to select
/// before you could reach it; the continuous slider fixed that but bought a
/// problem of its own — every integer from 10 to 300 was a position the handle
/// could land on, so the ordinary rates had to be recovered by a pixel-distance
/// magnet and drawn back onto the track as tick marks, and a value the player
/// could reach but the magnet would pull off was a control that felt like it was
/// arguing. A frame rate is not really a continuous quantity to a player: it is
/// the panel they own. So the slider's positions **are** the presets — an index
/// into <see cref="Pins"/>, formatted as the rate — which is the shape of the
/// render-scale slider it sits directly under, and needs neither a magnet nor a
/// drawn tick to say where it stops.
///
/// <see cref="FramePacing"/> still takes an arbitrary double and that is still the
/// point of the patch; <c>KF2_FPS</c> is where a rate that is not on this list
/// belongs. A config already holding one is not overwritten — the handle shows the
/// nearest position and the note says what is actually running, and nothing is
/// applied until the slider is moved.
///
/// **Uncapped is not one of the positions.** The entry existed in the combo, and
/// what it produced was not a working uncapped port, so offering it was offering a
/// defect. <c>KF2_FPS=off</c> still reaches it, which is where an unbounded
/// picture belongs until it is fixed — and a config already sitting there opens
/// the slider at the world's tick rate, since 0 is a position no control here can
/// express.
///
/// **The smoothing tick shares this heading**, directly under the slider. It is
/// greyed out whenever the rate is not above the world's tick — which is the
/// shipped default — and the control that decides that is this one, so the two
/// belong together rather than a group apart. See <see cref="FrameSmoothingPage"/>.
/// </summary>
public sealed class FramePacingPage : IPatchPage
{
    public string Id => "framepacing";
    public string Title => "Frame pacing";
    public int Order => 10;

    /// <summary>The positions the slider has: the world's own tick, the console's
    /// gate, and the panels people own. Anything between them is a console rate
    /// (<c>KF2_FPS</c>) rather than a position on this control.</summary>
    static readonly double[] Pins = [20, 30, 40, 50, 60, 75, 90, 100, 120, 144, 165, 180, 240];

    static int _index = 0;
    static bool _dragging;

    public void Draw()
    {
        // The slider's own position is the master only while it is being held: any
        // other frame it re-reads FramePacing, so a rate set from the console or
        // by another page shows up here. Pacing switched off entirely has no
        // position on this scale, so the handle parks at the world's tick rate
        // and nothing is applied until it is moved.
        double live = FramePacing.Enabled ? FramePacing.TargetFps : FramePacing.LogicHz;
        if (live <= 0.0) live = FramePacing.LogicHz;
        if (!_dragging) _index = Nearest(live);

        // No SetNextItemWidth: this matches the render-scale slider it sits under.
        // The format is a literal rather than a specifier, so the handle's index
        // never reaches the label -- ImGui's own trick for a named-position slider.
        // That is also why ctrl-click entry is off: the box would open on "144
        // fps" and there is no specifier to read a number back out of it, and a
        // typed rate has nowhere to land on a slider whose positions are a list.
        if (ImGui.SliderInt("Frame rate", ref _index, 0, Pins.Length - 1,
                            $"{Pins[_index]:0} fps",
                            ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoInput))
            Apply(Pins[_index]);

        _dragging = ImGui.IsItemActive();

        // A console rate that is not on the list is left running and said out
        // loud, since the handle beside it is showing something else.
        if (!_dragging && Math.Abs(live - Pins[_index]) > 0.5)
            PatchSettings.Note($"Running at {live:0.#} fps, set outside this menu.");

        PatchSettings.Note(FramePacing.Measured > 0.0
            ? $"Measured: {FramePacing.Measured:F1} fps"
            : "Measured: waiting for the first second of frames");

        ImGui.Spacing();
        PatchSettings.Note("Picture only: the game's own speed does not change with this.");
    }

    static int Nearest(double rate)
    {
        int best = 0;
        for (int i = 1; i < Pins.Length; i++)
            if (Math.Abs(Pins[i] - rate) < Math.Abs(Pins[best] - rate)) best = i;
        return best;
    }

    static void Apply(double rate)
    {
        FramePacing.SetTargetFps(rate);
        PatchSettings.Set(FramePacing.FpsKey, (float)(FramePacing.Enabled ? FramePacing.TargetFps : 0.0));
    }
}
