using ImGuiNET;
using RecompOne.Runtime.Hle;

namespace Kf2.Settings;

/// <summary>
/// Temporary: the KF2_GLDIAG experiments under Video, for the NVIDIA texture loss.
/// Live and never saved, so nothing outlives the investigation. Remove with
/// <c>GlDiag</c> once the cause is known.
/// </summary>
public sealed class DriverDiagPage : IPatchPage
{
    public string Id => "driverdiag";
    public string Title => "Driver diagnostics";
    public int Order => 29;

    static readonly string[] WLabels =
    [
        "Normal",
        "Clip W forced to 1 (no perspective)",
        "W scaled to 0..1 (same maths)",
        "Clip Z not zero",
    ];

    static readonly string[] ViewLabels =
    [
        "Normal",
        "Texture coordinates as colour",
        "Plain texel (no filter, no water blend)",
    ];

    public void Draw()
    {
        int w = GlDiag.WMode;
        if (ImGui.Combo("Clip W", ref w, WLabels, WLabels.Length)) GlDiag.WMode = w;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("How a 3D triangle's depth reaches the rasterizer. Not saved.");

        int v = GlDiag.View;
        if (ImGui.Combo("Texture view", ref v, ViewLabels, ViewLabels.Length)) GlDiag.View = v;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Colour view: red/green bands follow the texture, blue marks a real clip W. Not saved.");
    }
}
