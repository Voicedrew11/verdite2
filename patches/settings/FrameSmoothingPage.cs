using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The four smoothing switches, under Video, sharing the "Enhancements" heading
/// with the dither, perspective, sub-pixel and true-color ones.
///
/// They only do anything above the world's tick rate -- at or below it the world
/// advances at most once per drawn frame and there is nothing between ticks to
/// carry -- so the controls dim themselves rather than disappearing, which would
/// look like the setting had been lost. The test is
/// <see cref="FramePacing.Extrapolating"/> and not <c>Gating</c>, which is now
/// true at every rate. See <see cref="FrameSmoothing"/>,
/// <see cref="ObjectSmoothing"/> and <see cref="AnimSmoothing"/>.
/// </summary>
public sealed class FrameSmoothingPage : IPatchPage
{
    public string Id => "framesmoothing";
    public string Title => "Enhancements";

    public void Draw()
    {
        bool active = FramePacing.Extrapolating;
        if (!active) ImGui.BeginDisabled();

        bool on = FrameSmoothing.Enabled;
        if (ImGui.Checkbox("Smooth the view between game ticks", ref on))
        {
            FrameSmoothing.SetEnabled(on);
            PatchSettings.Set(FrameSmoothing.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Above the tick rate the game's world still advances only that " +
                             "many times a second. " +
                             "This carries the camera the rest of the way each frame, so turning " +
                             "and looking are as smooth as the frame rate rather than as smooth " +
                             "as the game. Off gives a faster picture of a camera that steps.");

        bool pos = FrameSmoothing.Position;
        if (ImGui.Checkbox("Also smooth movement", ref pos))
        {
            FrameSmoothing.SetPosition(pos);
            PatchSettings.Set(FrameSmoothing.PosKey, pos);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Carries the player's position between ticks as well as the view. " +
                             "It uses the distance the game actually moved you last tick, wall " +
                             "slide included -- but it is a guess about the next one, so walking " +
                             "into a wall can shimmer. Off by default.");

        bool objects = ObjectSmoothing.Enabled;
        if (ImGui.Checkbox("Smooth other things that move", ref objects))
        {
            ObjectSmoothing.SetEnabled(objects);
            PatchSettings.Set(ObjectSmoothing.OnKey, objects);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Smoothing the camera leaves enemies, doors and everything else " +
                             "that moves arriving in tick-sized steps, which against a smoothly " +
                             "sliding world is more obvious than it would be otherwise. This " +
                             "carries each of them on by however far it moved last tick, on " +
                             "the same clock as the view -- the two have to agree about what " +
                             "time it is, or the objects read as slower than the world. Off " +
                             "by default.");

        DrawPlacementGuard();

        bool anim = AnimSmoothing.Enabled;
        if (ImGui.Checkbox("Smooth model animation", ref anim))
        {
            AnimSmoothing.SetEnabled(anim);
            PatchSettings.Set(AnimSmoothing.OnKey, anim);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Position and facing are already carried from the entity and " +
                             "object tables. A creature's pose is a mesh morph (MO clip): this " +
                             "drives that clip's clock between ticks so the game's own blender " +
                             "fills the in-between shapes. The player's own first-person swing " +
                             "is the same kind of clip and is carried by this too. " +
                             "Off by default.");

        DrawPoseMode();

        if (!active)
        {
            ImGui.EndDisabled();
            Note($"Nothing to smooth at this frame rate: the world's {FramePacing.LogicHz:0.#} Hz " +
                 "is not below it, so every drawn frame lands on a tick.");
        }
    }

    // Indexed by (int)AnimSmoothing.Mode.
    static readonly string[] PoseLabels =
    [
        "Timeline - interpolate along the clip (default)",
        "Weight only - stay inside the game's segment",
        "Clip time - lerp between the two ticks",
    ];

    /// <summary>
    /// Which of the three pose interpolators runs, under the animation checkbox
    /// and indented to say it belongs to it.
    ///
    /// This is a comparison control rather than a preference: the three differ
    /// only in what they do between two ticks of the same clip, and the picture
    /// is the only thing that can separate them -- no counter in this repo can.
    /// Switching clears the per-slot state, so a creature on screen steps for one
    /// tick and then draws under the new mode, which is what makes an A/B while
    /// something is animating possible at all. See
    /// <see cref="AnimSmoothing.Mode"/>.
    /// </summary>
    static void DrawPoseMode()
    {
        if (!AnimSmoothing.Enabled) ImGui.BeginDisabled();

        ImGui.Indent();

        int mode = (int)AnimSmoothing.Carry;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 20f);
        if (ImGui.Combo("Pose interpolation", ref mode, PoseLabels, PoseLabels.Length))
        {
            AnimSmoothing.SetCarry((AnimSmoothing.Mode)mode);
            PatchSettings.Set(AnimSmoothing.ModeKey, mode);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How the in-between pose is worked out. Switching takes effect on " +
                             "the next tick, so the three can be compared while a creature is " +
                             "on screen. Look at a looping walk, a clip played in reverse (the " +
                             "drawbridge lever) and an attack the game restarts.");

        Note(PoseNote());
        ImGui.Unindent();

        if (!AnimSmoothing.Enabled) ImGui.EndDisabled();
    }

    static string PoseNote() => AnimSmoothing.Carry switch
    {
        AnimSmoothing.Mode.Timeline =>
            "Reads the clip's length out of the game's own segment table and treats the clip " +
            "time as a point on a circle of that length, so a loop turning over, a clip played " +
            "in reverse and a pose the game re-seeks are one test rather than four guesses. " +
            "Anything it does not recognise as playback is drawn at the tick rate, so a wrong " +
            "guess costs a stepped tick rather than a pose from elsewhere in the clip.",

        AnimSmoothing.Mode.Weight =>
            "The bounded comparison: the clip time is left exactly as the game wrote it and only " +
            "the blend weight moves, so the pose can never leave the segment the game chose. " +
            "Motion across a segment boundary is given up, which on a clip with short segments " +
            "is most of it.",

        _ =>
            "The clip time is interpolated as a plain number between the two ticks, with a " +
            "magnitude cutoff for a re-seek and a synthesised turnover at the end of a loop. " +
            "It can misjudge which of those a tick was, but it never asks for a pose outside " +
            "the two the game produced. The safer guess, and the coarser one.",
    };

    // Indexed by (int)ObjectSmoothing.Guard.
    static readonly string[] GuardLabels =
    [
        "Strict - 1024 units everywhere",
        "Sticky - creatures 8192, x4 while carried",
        "Continuous - creatures 8192, x4 once moving (default)",
    ];

    /// <summary>
    /// Which step counts as a placement rather than as motion, under the object
    /// checkbox and indented to say it belongs to it.
    ///
    /// The raised modes apply to **creatures only**. That is the whole compromise:
    /// a boss lunges far enough in a tick to be refused at 1024, and a refused
    /// root under a smoothed pose is what reads as its head snapping; the
    /// projectile tables recycle slots constantly and are wrong at anything above
    /// 1024. The tables want opposite answers, so the raise is scoped to the one
    /// that needs it. See <see cref="ObjectSmoothing.Guard"/>.
    /// </summary>
    static void DrawPlacementGuard()
    {
        if (!ObjectSmoothing.Enabled) ImGui.BeginDisabled();

        ImGui.Indent();

        int guard = (int)ObjectSmoothing.Placement;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 20f);
        if (ImGui.Combo("Placement guard", ref guard, GuardLabels, GuardLabels.Length))
        {
            ObjectSmoothing.SetPlacement((ObjectSmoothing.Guard)guard);
            PatchSettings.Set(ObjectSmoothing.GuardKey, guard);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far a thing may move in one tick before the port decides it " +
                             "was placed there rather than that it walked, and leaves it alone. " +
                             "Too low and a fast creature is carried on one tick and held on " +
                             "the next, which reads as its parts coming apart; too high and " +
                             "something that is teleported slides across the room. Switching " +
                             "takes effect on the next tick, so it can be compared while a " +
                             "creature is on screen.");

        Note(GuardNote());
        ImGui.Unindent();

        if (!ObjectSmoothing.Enabled) ImGui.EndDisabled();
    }

    static string GuardNote() => ObjectSmoothing.Placement switch
    {
        ObjectSmoothing.Guard.Strict =>
            "The old limit, on everything. A creature moving faster than this keeps the position " +
            "the game gave it, and its animation is held to the same rate so the two agree -- so " +
            "a fast boss is coherent, but steps.",

        ObjectSmoothing.Guard.Continuous =>
            "The widest limit for creatures: anything that moved on the previous tick gets it, " +
            "not just what was already being carried. Smoothest on a boss mid-attack. Projectiles " +
            "keep the old limit whatever this is set to.",

        _ =>
            "The raised limit for creatures, but only once one is already being carried, so a " +
            "creature that starts fast from a standstill is left stepping until it slows. Safer " +
            "against a creature spawning into a reused slot. Projectiles are unaffected.",
    };

    static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
