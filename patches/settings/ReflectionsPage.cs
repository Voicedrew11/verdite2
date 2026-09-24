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
    }
}
