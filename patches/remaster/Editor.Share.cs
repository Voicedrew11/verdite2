using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Kf2.Remaster;

/// <summary>The editor's share section: the compatibility report per area, and the pack
/// exported as a zip. See <see cref="Compat"/> and "Phase 7, the first slice" in
/// docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        JsonObject? _report;
        string? _exported, _exportError;

        void DrawShare()
        {
            if (!ImGui.CollapsingHeader("Share")) return;
            ImGui.BeginDisabled(Pack.Dirty);
            if (ImGui.Button("Export as zip"))
            {
                try { _exported = Pack.Export(); _exportError = null; }
                catch (Exception e) { _exportError = e.Message; _exported = null; }
            }
            ImGui.EndDisabled();
            if (Pack.Dirty && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Save first: the zip holds the saved files.");
            ImGui.SameLine();
            if (ImGui.Button("Check this disc") || _report == null) _report = Compat.Report();
            if (_exported != null) ImGui.TextDisabled(_exported);
            if (_exportError != null) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _exportError);

            if (_report["areas"] is not JsonArray areas || areas.Count == 0)
            {
                ImGui.TextDisabled("The pack holds no area documents.");
                return;
            }
            ImGui.TextDisabled($"{_report["matched"]} area(s) match this disc, {_report["differ"]} differ, " +
                               $"{_report["unseen"]} not yet visited; {_report["textureRules"]} texture rule(s), keyed on content");
            if (!ImGui.BeginTable("compat", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
                return;
            ImGui.TableSetupColumn("Area");
            ImGui.TableSetupColumn("Documents");
            ImGui.TableSetupColumn("Last applied");
            ImGui.TableHeadersRow();
            foreach (var n in areas)
            {
                if (n is not JsonObject a) continue;
                string status = a["status"]?.GetValue<string>() ?? "";
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var colour = status switch
                {
                    "matches" => new Vector4(0.5f, 0.9f, 0.5f, 1f),
                    "differs" => new Vector4(1f, 0.4f, 0.4f, 1f),
                    _ => new Vector4(1f, 0.75f, 0.3f, 1f),
                };
                ImGui.TextColored(colour, $"{a["area"]} {status}");
                ImGui.TableNextColumn();
                foreach (var d in a["documents"] as JsonArray ?? [])
                    ImGui.TextUnformatted($"{d?["kind"]}: {d?["entries"]}, {d?["status"]}");
                ImGui.TableNextColumn();
                if (a["resolved"] is JsonObject r)
                    foreach (var (k, v) in r)
                        if (k != "at") ImGui.TextUnformatted($"{k}: {Brief(v)}");
                        else ImGui.TextDisabled(v?.ToString() ?? "");
                else ImGui.TextDisabled("not applied here yet");
            }
            ImGui.EndTable();
        }

        static string Brief(JsonNode? n)
            => n is JsonObject o ? string.Join(", ", o.Select(p => $"{p.Key} {p.Value}")) : n?.ToString() ?? "";
    }
}
