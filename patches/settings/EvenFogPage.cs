using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The even-fog switch, under Video ▸ Enhancements after per-pixel lighting.
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
          "en": "Even fog",
          "pt-BR": "Neblina uniforme",
          "es-419": "Niebla uniforme"
        },
        "kf2.evenfog.tooltip": {
          "en": "Removes two steps in the original's fog: floor and wall tiles it has to clip get half the fog of their neighbours, and fog strength changes abruptly from one tile to the next where an area's lighting changes. This fogs clipped tiles like their neighbours and fades the change in over a tile. Needs Fast geometry.",
          "pt-BR": "Remove dois degraus na neblina do original: pisos e paredes que ele precisa recortar recebem metade da neblina dos vizinhos, e a intensidade da neblina muda de repente de um bloco para o outro onde a iluminação da área muda. Isto aplica aos blocos recortados a mesma neblina dos vizinhos e suaviza a mudança ao longo de um bloco. Requer Geometria rápida.",
          "es-419": "Quita dos escalones en la niebla del original: los pisos y paredes que tiene que recortar reciben la mitad de la niebla de sus vecinos, y la intensidad de la niebla cambia de golpe de un bloque al siguiente donde cambia la iluminación del área. Esto aplica a los bloques recortados la misma niebla que a sus vecinos y suaviza el cambio a lo largo de un bloque. Requiere Geometría rápida."
        }
      }
    }
    """;

    public EvenFogPage() => Localization.Merge(Strings);

    public void Draw()
    {
        bool on = EvenFog.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.evenfog.label"), ref on))
        {
            EvenFog.Enabled = on;
            PatchSettings.Set(EvenFog.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Localization.T("kf2.evenfog.tooltip"));
    }
}
