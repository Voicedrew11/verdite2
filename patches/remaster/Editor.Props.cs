using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>The editor's props section: add a prop of the selected object model where
/// the player stands or on the picture, and move, turn and scale it. A slider is one
/// undo entry, taken when it is let go. See "Phase 8, the first slice" in
/// docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        bool _placingProp;
        JsonObject? _propBefore;

        /// <summary>The model a new prop draws: the selected object model, if any.</summary>
        static int? PropModel(IMemory m)
            => SelectedModel is { Kind: ModelKind.Object } k && k.Area == Identity.Area && Props.Unusable(m, k.Model) == null
                ? k.Model : null;

        void AddProp(IMemory m, Vector3 at)
        {
            if (PropModel(m) is not { } model) return;
            string name = Pack.FreePropName(Identity.Area);
            bool upper = Identity.PlayerTile(m) is { Half: TileKey.Upper };
            if (Pack.AddProp(Identity.Area, Identity.FingerprintText, name, model, at, 0f, upper)) SelectProp(name);
        }

        /// <summary>A click on the picture while placing: true when it was spent here.</summary>
        bool PlacePropClick(IMemory m, Vector2 px)
        {
            if (!_placingProp) return false;
            _placingProp = false;
            if (PropPlaceAt(m, px) is { } at) AddProp(m, at);
            else _pickWhy = "no surface under that pixel for a prop";
            return true;
        }

        void DrawProps()
        {
            ImGui.Text("Props");
            var m = Runtime.Mem;
            if (m == null || Identity.Area < 0 || !Identity.Settled) { ImGui.TextDisabled("No settled area."); return; }
            int area = Identity.Area;
            if (Props.Refused is { } refused) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), refused);

            int? model = PropModel(m);
            ImGui.BeginDisabled(model == null || Props.Refused != null);
            if (ImGui.Button("Add here")) AddProp(m, PlayerFeet(m));
            ImGui.SameLine();
            ImGui.Checkbox("Place on the picture##prop", ref _placingProp);
            ImGui.EndDisabled();
            ImGui.TextDisabled(model is { } id
                ? $"A new prop draws model {id}, the selected object."
                : "Select an object in the picture; a prop draws that object's model.");
            if (Host.Enabled)
                ImGui.TextDisabled($"{Props.Resolved} of {Props.Authored} resolved, {Props.Submitted} drawn this frame");

            foreach (var p in Pack.Props(area))
                if (ImGui.Selectable($"{p.Name}  (model {p.Model}{(p.Off ? ", off" : "")})", p.Name == SelectedProp))
                    SelectProp(p.Name);
            if (SelectedProp is not { } name || Pack.GetProp(area, name) is not { } sel) return;

            ImGui.PushID("prop:" + name);
            if (Props.Status(name) is { } status && status != "drawn") ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), status);
            var pos = sel.Position;
            ImGui.SetNextItemWidth(260);
            PropEdited(area, name, "position", ImGui.DragFloat3("Position", ref pos, 8f, 0f, 0f, "%.0f"), o => Pack.SetPosition(o, pos));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("World units: a tile is 2048, a height step 128, and up is -Y.");
            var rot = sel.Rotation;
            ImGui.SetNextItemWidth(260);
            PropEdited(area, name, "rotation", ImGui.DragFloat3("Rotation", ref rot, 1f, 0f, 0f, "%.0f deg"), o => Pack.SetRotation(o, rot));
            float scale = sel.Scale.X;
            ImGui.SetNextItemWidth(200);
            PropEdited(area, name, "scale", ImGui.SliderFloat("Scale", ref scale, 0.1f, 4f, "%.2f", ImGuiSliderFlags.Logarithmic),
                       o => Pack.SetScale(o, new Vector3(scale)));
            bool upper = sel.Upper;
            if (ImGui.Checkbox("Upper half", ref upper))
                Pack.SetProp(area, name, upper ? "upper half" : "lower half", o => { if (upper) o["half"] = "upper"; else o.Remove("half"); });
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Which of its tile's two halves the prop is lit and culled with: the upper for a prop on a raised floor or a bridge.");
            ImGui.SameLine();
            bool on = !sel.Off;
            if (ImGui.Checkbox("On", ref on))
                Pack.SetProp(area, name, on ? "on" : "off", o => { if (on) o.Remove("enabled"); else o["enabled"] = false; });
            if (model is { } newModel && newModel != sel.Model)
            {
                if (ImGui.Button($"Draw model {newModel}")) Pack.SetProp(area, name, $"model = {newModel}", o => o["model"] = newModel);
                ImGui.SameLine();
            }
            if (ImGui.Button("Move here")) Pack.SetProp(area, name, "move here", o => Pack.SetPosition(o, PlayerFeet(m)));
            ImGui.SameLine();
            if (ImGui.Button("Delete")) { Pack.RemoveProp(area, name); SelectProp(null); }
            ImGui.PopID();
        }

        void PropEdited(int area, string name, string label, bool changed, Action<JsonObject> apply)
        {
            if (ImGui.IsItemActivated()) _propBefore = Pack.PropSnapshot(area, name);
            if (changed)
            {
                if (ImGui.IsItemActive() && _propBefore != null) Pack.PreviewProp(area, name, apply);
                else Pack.SetProp(area, name, label, apply);
            }
            if (ImGui.IsItemDeactivatedAfterEdit() && _propBefore != null) Pack.CommitProp(area, name, label, _propBefore);
            if (ImGui.IsItemDeactivated()) _propBefore = null;
        }
    }
}
