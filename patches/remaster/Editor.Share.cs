using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;

namespace Kf2.Remaster;

/// <summary>The editor's Pack tab: the working pack, the packs under it, the export,
/// and the compatibility report per area. See <see cref="Compat"/> and "Phase 7, the first slice" in
/// docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        JsonObject? _report;
        string? _exported, _exportError;

        void DrawPackTab()
        {
            ImGui.TextUnformatted("Working pack");
            Wrapped(Pack.Root, dim: true);
            if (ImGui.Button(L(Icon.Save, "Save"))) Pack.Save();
            ImGui.SameLine();
            if (ImGui.Button("Reload")) Pack.Load();
            Tip("Read the pack from disk again.");
            Flow("Export as zip");
            ImGui.BeginDisabled(Pack.Dirty);
            if (ImGui.Button("Export as zip"))
            {
                try { _exported = Pack.Export(); _exportError = null; }
                catch (Exception e) { _exportError = e.Message; _exported = null; }
            }
            ImGui.EndDisabled();
            Tip(Pack.Dirty ? "Save first: the zip holds the saved files." : "The saved pack, in upstream's layout, into exports/.");
            if (_exported != null) Wrapped(_exported, dim: true);
            if (_exportError != null) Wrapped(_exportError, Bad);
            if (Pack.WorkSetAside.Count > 0)
                Wrapped($"{Pack.WorkSetAside.Count} of the working pack's entries are set aside: " + string.Join("; ", Pack.WorkSetAside), Warn);

            ImGui.SeparatorText("Packs under it");
            if (Pack.Layers.Count == 0) Wrapped("No other pack has a remaster/ directory.", dim: true);
            foreach (var l in Pack.Layers)
            {
                bool on = l.Enabled;
                ImGui.BeginDisabled(l.Error != null);
                if (ImGui.Checkbox($"{l.Name}##{l.Id}", ref on)) Pack.SetLayerEnabled(l.Id, on);
                ImGui.EndDisabled();
                Tip($"{l.Path}\nSwitching a pack merges again and clears undo; unsaved edits are kept.");
                ImGui.SameLine();
                ImGui.TextDisabled($"{l.Documents} document(s)");
                if (l.Error != null) Wrapped(l.Error, Bad);
                if (l.SetAside.Count > 0) Wrapped($"set aside: {string.Join("; ", l.SetAside)}", Warn);
            }

            ImGui.SeparatorText("This disc");
            if (ImGui.Button("Check again") || _report == null) _report = Compat.Report();
            Tip("Whether each area document matches an area this disc has, and what resolved there when it was last applied.");
            if (_report["areas"] is not JsonArray areas || areas.Count == 0)
            {
                ImGui.TextDisabled("The pack holds no area documents.");
                return;
            }
            Wrapped($"{_report["matched"]} area(s) match this disc, {_report["differ"]} differ, " +
                    $"{_report["unseen"]} not yet visited; {_report["textureRules"]} texture rule(s), keyed on content.", dim: true);
            foreach (var n in areas)
            {
                if (n is not JsonObject a) continue;
                string status = a["status"]?.GetValue<string>() ?? "";
                var colour = status switch
                {
                    "matches" => new Vector4(0.5f, 0.9f, 0.5f, 1f),
                    "differs" => Bad,
                    _ => Warn,
                };
                ImGui.PushID(a["area"]?.ToString() ?? "");
                ImGui.PushStyleColor(ImGuiCol.Text, colour);
                bool open = ImGui.TreeNode($"Area {a["area"]}: {status}");
                ImGui.PopStyleColor();
                if (open)
                {
                    foreach (var d in a["documents"] as JsonArray ?? [])
                        Wrapped($"{d?["kind"]}: {d?["entries"]}, {d?["status"]}");
                    if (a["resolved"] is JsonObject r)
                        foreach (var (k, v) in r)
                            Wrapped(k != "at" ? $"{k}: {Brief(v)}" : $"last applied {v}", dim: k == "at");
                    else ImGui.TextDisabled("not applied here yet");
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
        }

        static string Brief(JsonNode? n)
            => n is JsonObject o ? string.Join(", ", o.Select(p => $"{p.Key} {p.Value}")) : n?.ToString() ?? "";
    }
}
