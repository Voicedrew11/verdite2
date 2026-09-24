using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The reflections switch, under Video ▸ Enhancements after the Z-buffer. Its
/// tuning is on the console (<c>KF2_SSR_*</c>), as the occlusion pass's is.
/// </summary>
public sealed class ReflectionsPage : IPatchPage
{
    public string Id => "ssr";
    public string Title => "Enhancements";
    public int Order => 28;

    const string Strings = """
    {
      "strings": {
        "kf2.ssr.label": {
          "en": "Water reflections",
          "pt-BR": "Reflexos na água",
          "es-419": "Reflejos en el agua"
        },
        "kf2.ssr.tooltip": {
          "en": "Water reflects what is on screen above it. Experimental.",
          "pt-BR": "A água reflete o que está na tela acima dela. Experimental.",
          "es-419": "El agua refleja lo que está en pantalla por encima de ella. Experimental."
        },
        "kf2.ssr.planar.label": {
          "en": "Planar reflections",
          "pt-BR": "Reflexos planares",
          "es-419": "Reflejos planares"
        },
        "kf2.ssr.planar.tooltip": {
          "en": "Draws the world a second time from below the water, so the reflection includes what is off screen or hidden. Costs a second walk of the scene whenever water is in view. Experimental.",
          "pt-BR": "Desenha o mundo uma segunda vez por baixo da água, para que o reflexo inclua o que está fora da tela ou escondido. Custa um segundo percurso da cena sempre que há água à vista. Experimental.",
          "es-419": "Dibuja el mundo una segunda vez desde debajo del agua, para que el reflejo incluya lo que está fuera de pantalla u oculto. Cuesta un segundo recorrido de la escena siempre que haya agua a la vista. Experimental."
        }
      }
    }
    """;

    public ReflectionsPage() => Localization.Merge(Strings);

    public void Draw()
    {
        bool on = Reflections.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.ssr.label"), ref on))
        {
            Reflections.SetEnabled(on);
            PatchSettings.Set(Reflections.OnKey, on);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("kf2.ssr.tooltip"));

        // Read by the reflection pass, so it means nothing with that off.
        ImGui.Indent();
        ImGui.BeginDisabled(!on);
        bool planar = PlanarWalk.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.ssr.planar.label"), ref planar))
        {
            PlanarWalk.SetEnabled(planar);
            PatchSettings.Set(PlanarWalk.OnKey, planar);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Localization.T("kf2.ssr.planar.tooltip"));
        ImGui.Unindent();
    }
}
