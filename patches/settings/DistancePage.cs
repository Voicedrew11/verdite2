using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The GPU world renderer, the render distance and the enhancement distance, at the
/// head of Video ▸ Experimental. Sorted before <see cref="ReflectionsPage"/> by id.
/// </summary>
public sealed class DistancePage : IPatchPage
{
    public string Id => "distance";
    public string Title => "Experimental";
    public int Order => 28;

    const string Strings = """
    {
      "strings": {
        "kf2.gpuworld.label": {
          "en": "GPU world renderer",
          "pt-BR": "Renderizador do mundo na GPU",
          "es-419": "Renderizador del mundo en la GPU"
        },
        "kf2.gpuworld.tooltip": {
          "en": "The map is drawn by the graphics card from a copy it keeps, instead of being rebuilt by the game's code every frame. Faster, and meant to look the same; creatures, objects and water are still drawn the old way. Experimental.",
          "pt-BR": "O mapa é desenhado pela placa de vídeo a partir de uma cópia que ela guarda, em vez de ser reconstruído pelo código do jogo a cada quadro. Mais rápido, e feito para parecer igual; criaturas, objetos e água ainda são desenhados do jeito antigo. Experimental.",
          "es-419": "El mapa lo dibuja la tarjeta gráfica a partir de una copia que guarda, en lugar de reconstruirlo el código del juego en cada cuadro. Más rápido, y pensado para verse igual; las criaturas, los objetos y el agua todavía se dibujan a la manera anterior. Experimental."
        },
        "kf2.gpuworld.blocked": {
          "en": "Standing down: it needs Fast geometry, the Z-buffer and perspective-correct textures, on the OpenGL renderer.",
          "pt-BR": "Inativo: precisa da Geometria rápida, do Z-buffer e das texturas com correção de perspectiva, no renderizador OpenGL.",
          "es-419": "Inactivo: necesita la Geometría rápida, el Z-buffer y las texturas con corrección de perspectiva, en el renderizador OpenGL."
        },
        "kf2.renderdistance.label": {
          "en": "Render distance",
          "pt-BR": "Distância de renderização",
          "es-419": "Distancia de dibujado"
        },
        "kf2.renderdistance.tooltip": {
          "en": "How far ahead the map is drawn, in floor tiles. The game stops at 10.5; past it the map is drawn further, up to 15, and the reflections see it too. The game's fog is unchanged, so in a fogged area what is added is drawn in the dark. Experimental.",
          "pt-BR": "Até onde o mapa é desenhado à frente, em ladrilhos do piso. O jogo para em 10,5; além disso o mapa é desenhado mais longe, até 15, e os reflexos também o veem. A neblina do jogo não muda, então numa área com neblina o que é acrescentado é desenhado no escuro. Experimental.",
          "es-419": "Hasta dónde se dibuja el mapa hacia adelante, en baldosas del piso. El juego se detiene en 10,5; más allá el mapa se dibuja más lejos, hasta 15, y los reflejos también lo ven. La niebla del juego no cambia, así que en un área con niebla lo agregado se dibuja en la oscuridad. Experimental."
        },
        "kf2.renderdistance.game": {
          "en": "The game's",
          "pt-BR": "A do jogo",
          "es-419": "La del juego"
        },
        "kf2.enhancedistance.label": {
          "en": "Enhancement distance",
          "pt-BR": "Distância das melhorias",
          "es-419": "Distancia de las mejoras"
        },
        "kf2.enhancedistance.tooltip": {
          "en": "How far from the camera the enhancements reach, in floor tiles. Past it a surface is drawn the game's own way: no per-pixel lighting, authored lights, texture filtering, ripples, ambient occlusion or reflections. Perspective correction, sub-pixel and the Z-buffer stay on. Experimental.",
          "pt-BR": "Até que distância da câmera as melhorias chegam, em ladrilhos do piso. Além dela uma superfície é desenhada como o jogo a desenha: sem iluminação por pixel, luzes criadas, filtragem de texturas, marolas, oclusão de ambiente ou reflexos. A correção de perspectiva, o subpixel e o Z-buffer continuam ativos. Experimental.",
          "es-419": "Hasta qué distancia de la cámara llegan las mejoras, en baldosas del piso. Más allá, una superficie se dibuja como la dibuja el juego: sin iluminación por píxel, luces creadas, filtrado de texturas, ondas, oclusión ambiental ni reflejos. La corrección de perspectiva, el subpíxel y el Z-buffer siguen activos. Experimental."
        },
        "kf2.enhancedistance.all": {
          "en": "Everywhere",
          "pt-BR": "Em toda parte",
          "es-419": "En todas partes"
        }
      }
    }
    """;

    public DistancePage() => Localization.Merge(Strings);

    public void Draw()
    {
        var flags = ImGuiSliderFlags.AlwaysClamp;

        bool gpu = GpuWorld.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.gpuworld.label"), ref gpu))
        {
            GpuWorld.SetEnabled(gpu);
            PatchSettings.Set(GpuWorld.OnKey, gpu);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Localization.T("kf2.gpuworld.tooltip") +
                             (GpuWorld.Blocker != null ? "\n\n" + Localization.T("kf2.gpuworld.blocked") : ""));

        float reach = RenderDistance.Tiles;
        string rf = RenderDistance.On ? "%.1f" : Escape(Localization.T("kf2.renderdistance.game"));
        if (ImGui.SliderFloat(Localization.T("kf2.renderdistance.label"), ref reach,
                              RenderDistance.Stock, RenderDistance.Max, rf, flags))
        {
            RenderDistance.Set(reach);
            PatchSettings.Set(RenderDistance.Key, RenderDistance.Tiles);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("kf2.renderdistance.tooltip"));

        float plain = EnhancementDistance.Tiles > 0f ? EnhancementDistance.Tiles : EnhancementDistance.Max;
        string pf = EnhancementDistance.Tiles > 0f ? "%.1f" : Escape(Localization.T("kf2.enhancedistance.all"));
        if (ImGui.SliderFloat(Localization.T("kf2.enhancedistance.label"), ref plain,
                              EnhancementDistance.Min, EnhancementDistance.Max, pf, flags))
        {
            EnhancementDistance.Set(plain);
            PatchSettings.Set(EnhancementDistance.Key, EnhancementDistance.Tiles);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("kf2.enhancedistance.tooltip"));
    }

    static string Escape(string s) => s.Replace("%", "%%");
}
