using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// Video ▸ Frame pacing: one switch for the C# polygon assembler, its clipper, the
/// vertex transforms, the lit model assembler and the GTE fast path. The per-routine
/// switches are <c>KF2_POLYASM_*</c> and <c>KF2_GTE_*</c> comparisons. See "The
/// polygon assembler in C#" in docs/PATCHES_AND_MODS.md.
///
/// Under it, GPU geometry: the GPU world renderer (<see cref="GpuWorld"/>), which
/// stands down without Fast geometry, so it dims with it off.
/// </summary>
public sealed class FastGeometryPage : IPatchPage
{
    public string Id => "fastgeometry";
    public string Title => "Frame pacing";
    public int Order => 12;

    const string Strings = """
    {
      "strings": {
        "kf2.gpuworld.label": {
          "en": "GPU geometry",
          "pt-BR": "Geometria na GPU",
          "es-419": "Geometría en la GPU"
        },
        "kf2.gpuworld.tooltip": {
          "en": "The world is drawn by the graphics card from a copy it keeps, instead of being rebuilt by the game's code every frame: the map, water, sky, creatures, objects and the arm. Faster in busy areas, and meant to look the same. Experimental.",
          "pt-BR": "O mundo é desenhado pela placa de vídeo a partir de uma cópia que ela guarda, em vez de ser reconstruído pelo código do jogo a cada quadro: o mapa, a água, o céu, as criaturas, os objetos e o braço. Mais rápido em áreas movimentadas, e feito para parecer igual. Experimental.",
          "es-419": "El mundo lo dibuja la tarjeta gráfica a partir de una copia que guarda, en lugar de reconstruirlo el código del juego en cada cuadro: el mapa, el agua, el cielo, las criaturas, los objetos y el brazo. Más rápido en áreas con mucho contenido, y pensado para verse igual. Experimental."
        },
        "kf2.gpuworld.blocked": {
          "en": "Standing down: it needs Fast geometry, the Z-buffer and perspective-correct textures, on the OpenGL renderer, and no texture pack loaded.",
          "pt-BR": "Inativo: precisa da Geometria rápida, do Z-buffer e das texturas com correção de perspectiva, no renderizador OpenGL, e de nenhum pacote de texturas carregado.",
          "es-419": "Inactivo: necesita la Geometría rápida, el Z-buffer y las texturas con corrección de perspectiva, en el renderizador OpenGL, y ningún paquete de texturas cargado."
        }
      }
    }
    """;

    public FastGeometryPage() => Localization.Merge(Strings);

    public void Draw()
    {
        bool on = PolyAssembler.FastGeometry;
        if (ImGui.Checkbox("Fast geometry", ref on))
        {
            PolyAssembler.SetFastGeometry(on);
            PatchSettings.Set(PolyAssembler.FastGeometryKey, PolyAssembler.FastGeometry);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Optimized polygon processing. The world and its creatures are " +
                             "transformed, lit and clipped by native code instead of the " +
                             "translated original, for a higher frame rate in busy areas. " +
                             "The picture is identical.");

        ImGui.Indent();
        ImGui.BeginDisabled(!PolyAssembler.FastGeometry);
        bool gpu = GpuWorld.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.gpuworld.label"), ref gpu))
        {
            GpuWorld.SetEnabled(gpu);
            PatchSettings.Set(GpuWorld.OnKey, gpu);
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(Localization.T("kf2.gpuworld.tooltip") +
                             (GpuWorld.Blocker != null ? "\n\n" + Localization.T("kf2.gpuworld.blocked") : ""));
        ImGui.EndDisabled();
        ImGui.Unindent();
    }
}
