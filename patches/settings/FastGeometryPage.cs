using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// Video ▸ Frame pacing: one switch for the C# polygon assembler, its clipper, the
/// vertex transforms, the lit model assembler and the GTE fast path. The per-routine
/// switches are <c>KF2_POLYASM_*</c> and <c>KF2_GTE_*</c> comparisons. See "The
/// polygon assembler in C#" in docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class FastGeometryPage : IPatchPage
{
    public string Id => "fastgeometry";
    public string Title => "Frame pacing";
    public int Order => 12;

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
    }
}
