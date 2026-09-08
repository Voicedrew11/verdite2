using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The perspective-correction switch, first under Video ▸ Enhancements, above
/// sub-pixel positioning and the shading combo — three controls of one kind, each
/// about how faithful the picture should be to the hardware.
///
/// It is first because sub-pixel positioning is the other half of the same
/// recovered number and reads as its sibling; the pair go above the shading combo,
/// which is about colour rather than geometry.
///
/// The hit rate that says the vertex table is actually working belongs on the
/// console under <c>KF2_PERSPECTIVE_PROBE=1</c>, not here; see "Perspective
/// correction" in NOTES.md.
/// </summary>
public sealed class PerspectivePage : IPatchPage
{
    public string Id => "perspective";
    public string Title => "Enhancements";
    public int Order => 20;

    public void Draw()
    {
        bool on = Perspective.Enabled;
        if (ImGui.Checkbox("Perspective-correct textures", ref on))
        {
            Perspective.SetEnabled(on);
            PatchSettings.Set(Perspective.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stops textures swimming on floors, walls and stairs.");
    }
}
