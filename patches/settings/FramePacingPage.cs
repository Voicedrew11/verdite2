using System.Numerics;
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
/// **It was a combo of presets plus a Custom slider, and it is one slider with
/// detents.** The rate is a continuous quantity — <see cref="FramePacing"/> takes
/// an arbitrary double, and "arbitrary" was the point of the whole patch — so a
/// drop-down was the wrong shape for it twice over: it made the free number a
/// *mode* you had to select before you could reach it, and it hid the ordinary
/// case (a panel's own refresh rate) behind an extra click for the sake of the
/// rare one. One slider from 10 to 300 covers both, and the presets become
/// **pins**: while a drag is in progress the value snaps to the nearest one it is
/// within a few pixels of, so 60, 144 and 165 are as easy to land on as a menu
/// entry while everything between them is still reachable. The pins are drawn on
/// the track as tick marks — a magnet the eye cannot see is a slider that feels
/// broken.
///
/// The snap is gated on the **left mouse button being held**, which is what
/// separates a drag from ImGui's ctrl-click text entry: a typed 61 is a rate the
/// player asked for by name and is left alone, while a dragged 61 is a miss. Nav
/// keys move the slider by ImGui's own step for the same reason.
///
/// **Uncapped is not one of the pins.** The entry existed in the combo, and what
/// it produced was not a working uncapped port, so offering it was offering a
/// defect. <c>KF2_FPS=off</c> still reaches it, which is where an unbounded
/// picture belongs until it is fixed — and a config already sitting there opens
/// the slider at the world's tick rate rather than at 0, since 0 is a position no
/// control here can express.
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

    const float Min = 10f;
    const float Max = 300f;

    /// <summary>The rates a player is actually likely to want: the world's own
    /// tick, the console's gate, and the panels people own. The slider reaches
    /// everything between them; these are only where it wants to stop.</summary>
    static readonly float[] Pins = [20f, 30f, 60f, 75f, 90f, 120f, 144f, 165f, 170f, 240f];

    static float _rate = 20f;
    static bool _dragging;

    public void Draw()
    {
        // The slider's own value is the master only while it is being held: any
        // other frame it re-reads FramePacing, so a rate set from the console or
        // by another page shows up here. Pacing switched off entirely has no
        // position on this scale, so the handle parks at the world's tick rate
        // and nothing is applied until it is moved.
        if (!_dragging)
        {
            double live = FramePacing.Enabled ? FramePacing.TargetFps : FramePacing.LogicHz;
            _rate = (float)Math.Clamp(live > 0.0 ? live : FramePacing.LogicHz, Min, Max);
        }

        // No SetNextItemWidth: this matches the render-scale slider it sits under.
        if (ImGui.SliderFloat("Frame rate", ref _rate, Min, Max, "%.0f fps",
                              ImGuiSliderFlags.AlwaysClamp))
        {
            // Only a drag snaps. Ctrl-click typing and keyboard nav are a rate
            // asked for by name, and a magnet would quietly refuse it.
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) _rate = Snap(_rate, ImGui.GetItemRectSize().X);
            Apply(_rate);
        }

        _dragging = ImGui.IsItemActive();
        DrawPins(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

        PatchSettings.Note(FramePacing.Measured > 0.0
            ? $"Measured: {FramePacing.Measured:F1} fps"
            : "Measured: waiting for the first second of frames");

        ImGui.Spacing();
        PatchSettings.Note("Picture only: the game's own speed does not change with this.");
    }

    /// <summary>
    /// The nearest pin, if the drag is within a few pixels of it.
    ///
    /// The tolerance is in **pixels rather than in fps**, because that is the unit
    /// the player's hand is working in — the same 4 fps is a third of the gap
    /// between 20 and 30 and a twentieth of the gap between 170 and 240. It is
    /// then capped at half the distance to the pin's nearest neighbour, so two
    /// close pins cannot both claim the space between them on a wide window.
    /// </summary>
    static float Snap(float value, float trackWidth)
    {
        float usable = Math.Max(1f, trackWidth - ImGui.GetStyle().FramePadding.X * 2f
                                               - ImGui.GetStyle().GrabMinSize);
        float fpsPerPixel = (Max - Min) / usable;
        float tolerance = 5f * fpsPerPixel;

        int nearest = -1;
        float best = float.MaxValue;
        for (int i = 0; i < Pins.Length; i++)
        {
            float d = Math.Abs(Pins[i] - value);
            if (d < best) { best = d; nearest = i; }
        }
        if (nearest < 0) return value;

        float gap = float.MaxValue;
        if (nearest > 0) gap = Math.Min(gap, Pins[nearest] - Pins[nearest - 1]);
        if (nearest < Pins.Length - 1) gap = Math.Min(gap, Pins[nearest + 1] - Pins[nearest]);

        float limit = Math.Min(tolerance, gap * 0.5f);
        return best <= limit ? Pins[nearest] : value;
    }

    /// <summary>
    /// A tick under each pin, on the slider's own track.
    ///
    /// The mapping is ImGui's: the grab travels between <c>FramePadding.x +
    /// GrabMinSize/2</c> and the far edge less the same, so a mark drawn at the
    /// raw fraction of the frame width would sit half a grab off at both ends and
    /// the 20 fps pin — the default — would look as if it missed. Drawn after the
    /// widget, so it lands on top of the track, and in the bottom third of it so
    /// the grab still reads.
    /// </summary>
    static void DrawPins(Vector2 min, Vector2 max)
    {
        var style = ImGui.GetStyle();
        float pad = style.FramePadding.X;
        float grab = style.GrabMinSize;
        float usable = (max.X - min.X) - pad * 2f - grab;
        if (usable <= 0f) return;

        var draw = ImGui.GetWindowDrawList();
        uint colour = ImGui.GetColorU32(ImGuiCol.TextDisabled, 0.55f);
        float top = max.Y - (max.Y - min.Y) * 0.28f;

        foreach (float pin in Pins)
        {
            float t = (pin - Min) / (Max - Min);
            float x = MathF.Round(min.X + pad + grab * 0.5f + t * usable);
            draw.AddLine(new Vector2(x, top), new Vector2(x, max.Y - 2f), colour, 1f);
        }
    }

    static void Apply(double rate)
    {
        FramePacing.SetTargetFps(rate);
        PatchSettings.Set(FramePacing.FpsKey, (float)(FramePacing.Enabled ? FramePacing.TargetFps : 0.0));
    }
}
