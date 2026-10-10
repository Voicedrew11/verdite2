using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The fog's distance cue, one choice of three, under Video ▸ Enhancements after even
/// fog and lighting. See <see cref="Fog"/>.
/// </summary>
public sealed class FogPage : IPatchPage
{
    public string Id => "fog";
    public string Title => "Enhancements";
    public int Order => 26;
    public IReadOnlyList<string> Patches => [];

    const string Strings = """
    {
      "strings": {
        "kf2.fog.label": {
          "en": "Fog",
          "pt-BR": "Neblina",
          "es-419": "Niebla"
        },
        "kf2.fog.corners": {
          "en": "At the corners (the game's)",
          "pt-BR": "Nos vértices (a do jogo)",
          "es-419": "En los vértices (la del juego)"
        },
        "kf2.fog.depth": {
          "en": "By pixel depth",
          "pt-BR": "Pela profundidade do pixel",
          "es-419": "Según la profundidad del píxel"
        },
        "kf2.fog.distance": {
          "en": "By distance from the eye",
          "pt-BR": "Pela distância do olho",
          "es-419": "Según la distancia al ojo"
        },
        "kf2.fog.tooltip": {
          "en": "At the edges of a wide picture the game fogs a wall as if it were nearer than it is, so the fog shifts as you turn. By distance from the eye keeps it still.",
          "pt-BR": "Nas bordas de uma imagem larga, o jogo aplica a neblina a uma parede como se ela estivesse mais perto do que está, então a neblina se move ao virar. Pela distância do olho a mantém parada.",
          "es-419": "En los bordes de una imagen ancha, el juego aplica la niebla a una pared como si estuviera más cerca de lo que está, así que la niebla se mueve al girar. Según la distancia al ojo la mantiene quieta."
        }
      }
    }
    """;

    public FogPage() => Localization.Merge(Strings);

    public void Draw()
    {
        string[] names = [Localization.T("kf2.fog.corners"), Localization.T("kf2.fog.depth"), Localization.T("kf2.fog.distance")];
        int index = Fog.Mode;
        if (ImGui.Combo(Localization.T("kf2.fog.label"), ref index, names, names.Length))
        {
            Fog.Set(index);
            PatchSettings.Set(Fog.Key, index);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Localization.T("kf2.fog.tooltip"));
    }
}
