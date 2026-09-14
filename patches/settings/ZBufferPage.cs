using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The Z-buffer switch and its coplanar tolerance, under Video ▸ Enhancements after
/// even fog. The two sliders are the tolerance's two terms (`0051`); the button puts
/// both back. See "Coplanar panels fought at the seam" in docs/RENDERING.md.
/// </summary>
public sealed class ZBufferPage : IPatchPage
{
    public string Id => "zbuffer";
    public string Title => "Enhancements";
    public int Order => 27;

    const string Strings = """
    {
      "strings": {
        "kf2.zbuffer.label": {
          "en": "Z-buffer",
          "pt-BR": "Z-buffer",
          "es-419": "Z-buffer"
        },
        "kf2.zbuffer.tooltip": {
          "en": "Surfaces that cross each other are drawn by depth instead of taking turns.",
          "pt-BR": "Superfícies que se cruzam são desenhadas pela profundidade em vez de se alternarem.",
          "es-419": "Las superficies que se cruzan se dibujan por profundidad en vez de alternarse."
        },
        "kf2.zbuffer.bias": {
          "en": "Seam tolerance",
          "pt-BR": "Tolerância de emendas",
          "es-419": "Tolerancia de uniones"
        },
        "kf2.zbuffer.bias.tooltip": {
          "en": "Raise if two panels flicker where they meet; lower if crossings look shifted.",
          "pt-BR": "Aumente se dois painéis piscarem onde se encontram; diminua se os cruzamentos parecerem deslocados.",
          "es-419": "Súbelo si dos paneles parpadean donde se unen; bájalo si los cruces se ven desplazados."
        },
        "kf2.zbuffer.slope": {
          "en": "Angled seam tolerance",
          "pt-BR": "Tolerância de emendas inclinadas",
          "es-419": "Tolerancia de uniones inclinadas"
        },
        "kf2.zbuffer.slope.tooltip": {
          "en": "The same, for walls seen at a sharp angle.",
          "pt-BR": "O mesmo, para paredes vistas em ângulo acentuado.",
          "es-419": "Lo mismo, para paredes vistas en ángulo pronunciado."
        },
        "kf2.zbuffer.reset": {
          "en": "Reset tolerances",
          "pt-BR": "Restaurar tolerâncias",
          "es-419": "Restablecer tolerancias"
        }
      }
    }
    """;

    public ZBufferPage() => Localization.Merge(Strings);

    public void Draw()
    {
        bool on = ZBuffer.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.zbuffer.label"), ref on))
        {
            ZBuffer.SetEnabled(on);
            PatchSettings.Set(ZBuffer.OnKey, on);
        }
        Tip("kf2.zbuffer.tooltip");

        ImGui.BeginDisabled(!on);
        ImGui.Indent();

        float bias = GteDepth.DepthBias;
        if (ImGui.SliderFloat(Localization.T("kf2.zbuffer.bias"), ref bias, 0f, 8f, "%.2f", ImGuiSliderFlags.AlwaysClamp))
        {
            GteDepth.DepthBias = bias;
            PatchSettings.Set(ZBuffer.BiasKey, bias);
        }
        Tip("kf2.zbuffer.bias.tooltip");

        float slope = GteDepth.DepthSlope;
        if (ImGui.SliderFloat(Localization.T("kf2.zbuffer.slope"), ref slope, 0f, 4f, "%.2f", ImGuiSliderFlags.AlwaysClamp))
        {
            GteDepth.DepthSlope = slope;
            PatchSettings.Set(ZBuffer.SlopeKey, slope);
        }
        Tip("kf2.zbuffer.slope.tooltip");

        if (ImGui.Button(Localization.T("kf2.zbuffer.reset")))
        {
            GteDepth.DepthBias = ZBuffer.DefaultBias;
            GteDepth.DepthSlope = ZBuffer.DefaultSlope;
            PatchSettings.Set(ZBuffer.BiasKey, ZBuffer.DefaultBias);
            PatchSettings.Set(ZBuffer.SlopeKey, ZBuffer.DefaultSlope);
        }

        ImGui.Unindent();
        ImGui.EndDisabled();
    }

    static void Tip(string key)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Localization.T(key));
    }
}
