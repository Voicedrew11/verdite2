using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The three smoothing switches, under Video, sharing the "Enhancements" heading
/// with the dither, perspective, sub-pixel and true-color ones.
///
/// They only do anything above the world's tick rate -- at or below it the world
/// advances at most once per drawn frame and there is nothing between ticks to
/// carry -- so the controls dim themselves rather than disappearing, which would
/// look like the setting had been lost. The test is
/// <see cref="FramePacing.Extrapolating"/> and not <c>Gating</c>, which is now
/// true at every rate. See <see cref="FrameSmoothing"/> and
/// <see cref="ObjectSmoothing"/>.
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
                             "walks each of them between the two positions the game gave it. " +
                             "Unlike the view it interpolates rather than guesses ahead, so it " +
                             "cannot overshoot. Off by default.");

        if (!active)
        {
            ImGui.EndDisabled();
            Note($"Nothing to smooth at this frame rate: the world's {FramePacing.LogicHz:0.#} Hz " +
                 "is not below it, so every drawn frame lands on a tick.");
        }
    }

    static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
