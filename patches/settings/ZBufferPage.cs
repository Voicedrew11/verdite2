using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The Z-buffer switch, under Video ▸ Enhancements after even fog. The coplanar
/// tolerance's two terms (`0051`) are no longer settings: they are
/// <c>ZBuffer.DefaultBias</c> and <c>DefaultSlope</c>, or <c>KF2_ZBUFFER_BIAS</c> and
/// <c>KF2_ZBUFFER_SLOPE</c>. See "Coplanar panels fought at the seam" in docs/RENDERING.md.
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
    }

    static void Tip(string key)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Localization.T(key));
    }
}
