using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The projectile lights switch, under Video ▸ Enhancements after the fog. The lights
/// are added in the per-pixel lit shader from the C# assembler's records, so it dims
/// without either. See <see cref="ProjectileLights"/>.
/// </summary>
public sealed class ProjectileLightsPage : IPatchPage
{
    public string Id => "projectilelights";
    public string Title => "Enhancements";
    public int Order => 26;
    public IReadOnlyList<string> Patches => ["kf2.projectilelights"];

    const string Strings = """
    {
      "strings": {
        "kf2.projectilelights.label": {
          "en": "Glowing spells",
          "pt-BR": "Magias luminosas",
          "es-419": "Hechizos luminosos"
        },
        "kf2.projectilelights.tooltip": {
          "en": "Magic projectiles light the walls, floor and creatures around them in their own colour.",
          "pt-BR": "Projéteis mágicos iluminam as paredes, o chão e as criaturas ao redor com sua própria cor.",
          "es-419": "Los proyectiles mágicos iluminan las paredes, el suelo y las criaturas a su alrededor con su propio color."
        },
        "kf2.projectilelights.needs": {
          "en": "Needs Per-pixel lighting, and Fast geometry under Frame pacing.",
          "pt-BR": "Requer Per-pixel lighting, e Fast geometry em Frame pacing.",
          "es-419": "Requiere Per-pixel lighting, y Fast geometry en Frame pacing."
        }
      }
    }
    """;

    public ProjectileLightsPage() => Localization.Merge(Strings);

    public void Draw()
    {
        bool can = PolyAssembler.FastGeometry && PerPixelLighting.Enabled;
        ImGui.BeginDisabled(!can);

        bool on = ProjectileLights.On;
        if (ImGui.Checkbox(Localization.T("kf2.projectilelights.label"), ref on))
        {
            ProjectileLights.Set(on);
            PatchSettings.Set(ProjectileLights.OnKey, on);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Localization.T("kf2.projectilelights.tooltip"));

        ImGui.EndDisabled();
        if (!can) PatchSettings.Note(Localization.T("kf2.projectilelights.needs"));
    }
}
