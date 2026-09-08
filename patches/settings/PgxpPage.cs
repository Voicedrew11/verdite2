using ImGuiNET;
using Rp = RecompOne.Runtime.Pgxp;
using Rt = RecompOne.Runtime.Runtime;

namespace Kf2.Settings;

/// <summary>
/// Where the two vertex-recovery mechanisms are chosen between, under Video, and
/// where the Z-buffer's checkbox comes back.
///
/// **This page has more controls than any other the port draws, and that is on
/// purpose rather than by neglect.** Everything else under Video is a picture
/// somebody has judged, offered as one tick or one combo; PGXP is a mechanism
/// nobody has judged yet, whose parts can each be turned off to find out which of
/// them is responsible for what. Upstream draws exactly these controls, so a
/// report against this port can be compared with a report against RecompOne. The
/// day the picture is settled, most of them should collapse the way the two
/// shading checkboxes did.
///
/// The one control that is *not* here is texture correction. Upstream gives PGXP
/// its own tick for it; this port already has one under Enhancements, and two
/// controls for one idea is the thing <see cref="ShadingPage"/> was written to
/// stop. <see cref="Perspective.SetEnabled"/> writes both.
/// </summary>
public sealed class PgxpPage : IPatchPage
{
    public string Id => "pgxp";
    public string Title => "Geometry precision";
    public int Order => 30;

    const int Legacy = 0;
    const int PgxpSource = 1;

    static readonly string[] Sources = ["Address map (original)", "PGXP"];

    public void Draw()
    {
        int source = Pgxp.Enabled ? PgxpSource : Legacy;

        if (ImGui.Combo("Vertex source", ref source, Sources, Sources.Length))
        {
            PatchSettings.Set(Rp.Pgxp.KeyEnable, source == PgxpSource);
            Pgxp.Reload();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How the per-vertex position and depth the console discarded are recovered.");

        bool on = Pgxp.Enabled;
        if (!on) ImGui.BeginDisabled();

        Toggle(Rp.Pgxp.KeyCulling, "Correct backface culling", true,
            "Decide which faces to drop from the precise positions, not the rounded ones.");
        Toggle(Rp.Pgxp.KeyCpu, "Track CPU registers", true,
            "Follow a vertex through the game's own arithmetic. Off, only stores are followed.");
        // Not independent of the tick above it: PgxpMemory.Store is only ever
        // reached from PgxpCpu, so with CPU tracking off the shadow stays empty
        // whatever this says. Dimmed rather than hidden, because upstream draws
        // them as two and a report against this port should still line up.
        if (!Rp.Pgxp.CpuTracking) ImGui.BeginDisabled();
        Toggle(Rp.Pgxp.KeyMemory, "Track memory", true,
            "Remember which word of RAM holds which vertex. Needs CPU tracking.");
        if (!Rp.Pgxp.CpuTracking) ImGui.EndDisabled();
        Toggle(Rp.Pgxp.KeyVertexCache, "Vertex cache", true,
            "Fall back to what last landed on the same pixel. 64 MB, and a guess.");

        if (!Rp.Pgxp.VertexCache) ImGui.BeginDisabled();
        Toggle(Rp.Pgxp.KeyCacheW, "Cache depth too", true,
            "Let that fallback answer with a depth as well as a position.");
        if (!Rp.Pgxp.VertexCache) ImGui.EndDisabled();

        float tolerance = Rt.View.GetFloat(Rp.Pgxp.KeyTolerance, Rp.Pgxp.DefaultTolerance);
        if (ImGui.SliderFloat("Tolerance", ref tolerance, -1f, 10f,
                tolerance < 0f ? "off" : "%.2f px"))
        {
            PatchSettings.Set(Rp.Pgxp.KeyTolerance, tolerance);
            Pgxp.Reload();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far a recovered position may sit from the one in the packet before it is refused.");

        if (!on) ImGui.EndDisabled();

        ImGui.Spacing();

        // Independent of the source: the depth buffer reads whichever mechanism
        // answered. It is here rather than under Enhancements because it is the
        // thing the source above exists to feed, and reading the two apart is what
        // made it look unfixable for as long as it did.
        bool zbuffer = ZBuffer.Enabled;
        if (ImGui.Checkbox("Depth buffer", ref zbuffer))
        {
            ZBuffer.SetEnabled(zbuffer);
            PatchSettings.Set(ZBuffer.OnKey, ZBuffer.Enabled);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hide surfaces behind others per pixel, instead of in ordering-table order.");

        if (!zbuffer) ImGui.BeginDisabled();

        float threshold = RecompOne.Runtime.GteDepth.DepthClearThreshold;
        if (ImGui.SliderFloat("Depth clear threshold", ref threshold, 0f, 2000f,
                threshold <= 0f ? "off" : "%.0f"))
        {
            RecompOne.Runtime.GteDepth.DepthClearThreshold = threshold;
            PatchSettings.Set(ZBuffer.ThresholdKey, threshold);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Start the depth buffer again when the scene jumps towards the camera. " +
                             "Off: this game draws one scene a frame.");

        if (!zbuffer) ImGui.EndDisabled();
    }

    static void Toggle(string key, string label, bool fallback, string hint)
    {
        bool value = Rt.View.GetBool(key, fallback);
        if (ImGui.Checkbox(label, ref value))
        {
            PatchSettings.Set(key, value);
            Pgxp.Reload();
        }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip(hint);
    }
}
