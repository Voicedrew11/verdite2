using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// <see cref="EnemyTurn.Multiplier"/> and <see cref="EnemyTurn.Track"/>, under
/// Gameplay: how fast creatures turn at the player, as a multiple of the game's own,
/// and whether a standing creature keeps turning to follow. Applied on the next
/// tick, so they can be changed in a fight. See "Enemies turn faster" in
/// docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class EnemyTurnPage : IPatchPage
{
    public string Id => "enemyturn";

    public string Title => "";

    public int Order => 45;

    public IReadOnlyList<string> Patches => ["kf2.enemyturn"];

    public void Draw()
    {
        float k = EnemyTurn.Multiplier;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat("Enemy turn speed", ref k, EnemyTurn.Min, EnemyTurn.Max,
                              k <= EnemyTurn.Min ? "x1 (original)" : "x%.2f"))
            EnemyTurn.SetMultiplier(MathF.Round(k * 4f) / 4f);
        if (ImGui.IsItemDeactivatedAfterEdit())
            PatchSettings.Set(EnemyTurn.Key, EnemyTurn.Multiplier);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How fast enemies turn to follow you, as a multiple of the game's own speed.\n" +
                             "Higher makes them harder to circle around.");

        // Dimmed rather than hidden at x1, where it does nothing: AutoReloadPage's rule.
        ImGui.Indent();
        ImGui.BeginDisabled(EnemyTurn.Multiplier <= EnemyTurn.Min);

        bool track = EnemyTurn.Track;
        if (ImGui.Checkbox("Track you while standing", ref track))
        {
            EnemyTurn.Track = track;
            PatchSettings.Set(EnemyTurn.TrackKey, track);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("An enemy standing still keeps turning to face you, instead of only between moves.");

        ImGui.EndDisabled();
        ImGui.Unindent();
    }
}
