using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The perspective-correction switch, under Video, sharing the "Enhancements"
/// heading with the dither switch — the two are the same kind of choice, one
/// checkbox each about how faithful the picture should be to the hardware.
///
/// The hit rate that says the vertex table is actually working belongs on the
/// console under <c>KF2_PERSPECTIVE_PROBE=1</c>, not here; see "Perspective
/// correction" in NOTES.md.
/// </summary>
public sealed class PerspectivePage : IPatchPage
{
    public string Id => "perspective";
    public string Title => "Enhancements";

    public void Draw()
    {
        bool on = Perspective.Enabled;
        if (ImGui.Checkbox("Perspective-correct textures", ref on))
        {
            Perspective.SetEnabled(on);
            PatchSettings.Set(Perspective.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stops textures swimming and rippling on floors, walls and stairs. " +
                             "Off gives the console's own affine mapping.");
    }
}
