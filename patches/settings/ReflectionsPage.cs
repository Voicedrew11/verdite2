using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The water switches, under Video ▸ Enhancements after the Z-buffer: planar
/// reflections, the murk and the waves. Their tuning is no longer a setting: the
/// values are the constants in <c>PlanarWalk</c>, <c>Murk</c> and <c>Waves</c>. The screen march, the
/// retained scene's reflections and the reflection reach are no longer settings
/// (<c>KF2_SSR</c>, <c>KF2_RETAINED</c>, <c>KF2_REFLECT_REACH</c>); the rest of the
/// tuning is on the console, as the occlusion pass's is.
/// </summary>
public sealed class ReflectionsPage : IPatchPage
{
    public string Id => "ssr";
    public string Title => "Enhancements";
    public int Order => 28;

    const string Strings = """
    {
      "strings": {
        "kf2.murk.label": {
          "en": "Murky water",
          "pt-BR": "Água turva",
          "es-419": "Agua turbia"
        },
        "kf2.murk.tooltip": {
          "en": "Water darkens with depth: the further the view runs through it to the bottom, the murkier it gets. Needs no reflections. Experimental.",
          "pt-BR": "A água escurece com a profundidade: quanto mais a vista a atravessa até o fundo, mais turva fica. Não precisa de reflexos. Experimental.",
          "es-419": "El agua se oscurece con la profundidad: cuanto más la atraviesa la vista hasta el fondo, más turbia se ve. No necesita reflejos. Experimental."
        },
        "kf2.waves.label": {
          "en": "Water waves",
          "pt-BR": "Ondas na água",
          "es-419": "Olas en el agua"
        },
        "kf2.waves.tooltip": {
          "en": "Water moves: a slow swell lifts and lowers the surface, and ripples push and shade its texture, so the pattern no longer repeats tile by tile. The edges of the water stay where they are. Experimental.",
          "pt-BR": "A água se move: uma ondulação lenta sobe e desce a superfície, e marolas deslocam e sombreiam sua textura, para que o padrão não se repita ladrilho a ladrilho. As bordas da água ficam onde estão. Experimental.",
          "es-419": "El agua se mueve: un oleaje lento sube y baja la superficie, y ondas desplazan y sombrean su textura, para que el patrón no se repita baldosa a baldosa. Los bordes del agua se quedan donde están. Experimental."
        },
        "kf2.ssr.planar.label": {
          "en": "Planar reflections",
          "pt-BR": "Reflexos planares",
          "es-419": "Reflejos planares"
        },
        "kf2.ssr.planar.tooltip": {
          "en": "Water reflects the world: it is drawn a second time from below the water, including what is off screen or hidden from you. Costs a second walk of the scene whenever water is in view. Experimental.",
          "pt-BR": "A água reflete o mundo: ele é desenhado uma segunda vez por baixo da água, incluindo o que está fora da tela ou escondido de você. Custa um segundo percurso da cena sempre que há água à vista. Experimental.",
          "es-419": "El agua refleja el mundo: se dibuja una segunda vez desde debajo del agua, incluido lo que está fuera de pantalla u oculto para ti. Cuesta un segundo recorrido de la escena siempre que haya agua a la vista. Experimental."
        }
      }
    }
    """;

    public ReflectionsPage() => Localization.Merge(Strings);

    public void Draw()
    {
        bool planar = PlanarWalk.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.ssr.planar.label"), ref planar))
        {
            PlanarWalk.SetEnabled(planar);
            PatchSettings.Set(PlanarWalk.OnKey, planar);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("kf2.ssr.planar.tooltip"));

        bool murk = Murk.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.murk.label"), ref murk))
        {
            Murk.SetEnabled(murk);
            PatchSettings.Set(Murk.OnKey, murk);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("kf2.murk.tooltip"));

        bool waves = Waves.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.waves.label"), ref waves))
        {
            Waves.SetEnabled(waves);
            PatchSettings.Set(Waves.OnKey, waves);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("kf2.waves.tooltip"));
    }
}
