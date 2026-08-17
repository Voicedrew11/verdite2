using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The Z-buffer switch, under Video, sharing the "Enhancements" heading with the
/// dither, perspective and sub-pixel switches — four checkboxes, each one a
/// choice about how faithful the picture should be to the hardware, and none of
/// them deserving a rule and a name of its own.
///
/// It belongs beside perspective correction because both consume the view depth
/// the GTE computed and the packet dropped. The rate that says triangles are
/// actually testing belongs on the console under <c>KF2_ZBUFFER_PROBE=1</c>, not
/// here; see "Z-buffer" in NOTES.md.
/// </summary>
public sealed class ZBufferPage : IPatchPage
{
    public string Id => "zbuffer";
    public string Title => "Enhancements";

    public void Draw()
    {
        bool on = ZBuffer.Enabled;
        if (ImGui.Checkbox("Z-buffer", ref on))
        {
            ZBuffer.SetEnabled(on);
            PatchSettings.Set(ZBuffer.OnKey, on);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Occludes per pixel from recovered GTE depth, so intersecting " +
                             "walls and floors no longer take turns in front of each other. " +
                             "Off gives the console's own ordering-table sort.");
    }
}
