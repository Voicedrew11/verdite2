using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The even fog and lighting switch, under Video ▸ Enhancements after per-pixel
/// lighting. It only runs from the C# assembler, so it dims without Fast geometry.
/// </summary>
public sealed class EvenFogPage : IPatchPage
{
    public string Id => "evenfog";
    public string Title => "Enhancements";
    public int Order => 26;

    const string Strings = """
    {
      "strings": {
        "kf2.evenfog.label": {
          "en": "Even fog and lighting",
          "pt-BR": "Neblina e iluminação uniformes",
          "es-419": "Niebla e iluminación uniformes"
        },
        "kf2.evenfog.tooltip": {
          "en": "Smooths out sudden jumps in fog and lighting between floor tiles.",
          "pt-BR": "Suaviza saltos bruscos de neblina e iluminação entre blocos do piso.",
          "es-419": "Suaviza saltos bruscos de niebla e iluminación entre bloques del piso."
        },
        "kf2.evenfog.needsfast": {
          "en": "Needs Fast geometry, under Frame pacing.",
          "pt-BR": "Requer Fast geometry, em Frame pacing.",
          "es-419": "Requiere Fast geometry, en Frame pacing."
        }
      }
    }
    """;

    public EvenFogPage() => Localization.Merge(Strings);

    public void Draw()
    {
        bool fast = PolyAssembler.FastGeometry;
        ImGui.BeginDisabled(!fast);

        bool on = EvenFog.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.evenfog.label"), ref on))
        {
            EvenFog.Enabled = on;
            PatchSettings.Set(EvenFog.OnKey, on);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Localization.T("kf2.evenfog.tooltip"));

        ImGui.EndDisabled();
        if (!fast) PatchSettings.Note(Localization.T("kf2.evenfog.needsfast"));
    }
}
