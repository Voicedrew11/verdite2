using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// **One switch for all of the smoothing**, under Video, sharing the
/// "Enhancements" heading with the dither, perspective, sub-pixel and true-color
/// ones.
///
/// There were four checkboxes and two combos here, one per patch, and that was
/// the implementation's shape rather than the player's: nobody wants the camera
/// carried between ticks and the creatures left stepping, and the two combos
/// (<see cref="AnimSmoothing.Mode"/>, <see cref="ObjectSmoothing.Guard"/>) are
/// comparison controls for judging a picture rather than preferences to hold.
/// Four of the ten controls in Enhancements were this one idea spelled out in
/// parts. So the page is one tick that writes all four patches' state and all
/// four of their keys, and the parts stay separable from the console --
/// <c>KF2_SMOOTH</c>, <c>KF2_SMOOTH_POS</c>, <c>KF2_SMOOTH_OBJECTS</c>,
/// <c>KF2_SMOOTH_ANIM</c>, plus <c>KF2_SMOOTH_OBJECTS_GUARD</c> and the
/// <c>KF2_SMOOTH_ANIM</c> modes -- which is where a comparison belongs.
///
/// The tick reads <see cref="FrameSmoothing.Enabled"/> and writes all four, so a
/// config or an environment variable that turns one part on alone still does
/// that until the box is clicked, and clicking it harmonises them. The view is
/// the master because it is the part that cannot sensibly be off while the rest
/// are on: everything else is carried against the camera.
///
/// **The position half carries a known inconsistency and is in here anyway.**
/// Two of stage 13's own callees read the player position triple after
/// <see cref="FrameSmoothing.After"/> has put the raw value back, so on a
/// non-tick frame the first-person arm, the torches and the creatures shear
/// against the architecture by however far the carry moved the eye. Dropping it
/// back out is deleting the <c>SetPosition</c> pair below. See "Interpolate, not
/// extrapolate" in <see cref="FrameSmoothing"/>.
///
/// It only does anything above the world's tick rate -- at or below it the world
/// advances at most once per drawn frame and there is nothing between ticks to
/// carry -- so the control dims itself rather than disappearing, which would look
/// like the setting had been lost. The test is
/// <see cref="FramePacing.Extrapolating"/> and not <c>Gating</c>, which is now
/// true at every rate.
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
        if (ImGui.Checkbox("Smooth motion between game ticks", ref on)) Apply(on);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Smooths the camera, your movement and everything that moves to " +
                             "your frame rate.");

        if (!active)
        {
            ImGui.EndDisabled();
            Note($"Nothing to smooth at this frame rate: the world's {FramePacing.LogicHz:0.#} Hz " +
                 "is not below it, so every drawn frame lands on a tick.");
        }
    }

    /// <summary>
    /// All four patches and all four keys, from the one tick. Each patch's own
    /// <c>SetEnabled</c> may refuse -- <see cref="FrameSmoothing"/> and
    /// <see cref="ObjectSmoothing"/> need their hook pair, <see cref="AnimSmoothing"/>
    /// needs its clock sited -- so the key is written from what the patch ended up
    /// with rather than from what was asked for, and a part that could not attach
    /// does not come back claiming it had.
    /// </summary>
    static void Apply(bool on)
    {
        FrameSmoothing.SetEnabled(on);
        PatchSettings.Set(FrameSmoothing.OnKey, FrameSmoothing.Enabled);

        FrameSmoothing.SetPosition(on);
        PatchSettings.Set(FrameSmoothing.PosKey, FrameSmoothing.Position);

        ObjectSmoothing.SetEnabled(on);
        PatchSettings.Set(ObjectSmoothing.OnKey, ObjectSmoothing.Enabled);

        AnimSmoothing.SetEnabled(on);
        PatchSettings.Set(AnimSmoothing.OnKey, AnimSmoothing.Enabled);
    }

    static void Note(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
