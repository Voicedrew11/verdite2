using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The two smoothing switches, under Video, sharing the "Enhancements" heading
/// with the dither, perspective, sub-pixel and true-color ones.
///
/// They only do anything above 30 fps -- at 30 the world advances once per drawn
/// frame and there is nothing between ticks to carry -- so the controls dim
/// themselves rather than disappearing, which would look like the setting had
/// been lost. See <see cref="FrameSmoothing"/>.
/// </summary>
public sealed class FrameSmoothingPage : IPatchPage
{
    public string Id => "framesmoothing";
    public string Title => "Enhancements";

    public void Draw()
    {
        bool active = FramePacing.Gating;
        if (!active) ImGui.BeginDisabled();

        bool on = FrameSmoothing.Enabled;
        if (ImGui.Checkbox("Smooth the view between game ticks", ref on))
        {
            FrameSmoothing.SetEnabled(on);
            PatchSettings.Set(FrameSmoothing.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Above 30 fps the game's world still advances 30 times a second. " +
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

        if (!active)
        {
            ImGui.EndDisabled();
            Note("Nothing to smooth at this frame rate: the world advances once per drawn frame.");
        }
    }

    static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
