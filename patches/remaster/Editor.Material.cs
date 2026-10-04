using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;
using Kf2.Settings;

namespace Kf2.Remaster;

/// <summary>The editor's Material tab: what is selected, which rule a material is
/// assigned to and what the selection ends up drawing with, then the library entry
/// being edited, which follows the selection's. A slider is one undo entry, taken when
/// it is let go. See "The Material tab" in docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        /// <summary>The library entry the material controls edit.</summary>
        string? _editMat;

        /// <summary>The selection's material when last drawn: a change moves the editor to it.</summary>
        string? _lastSelMat;

        /// <summary>Assign to the picked art in every area, rather than the half, faces or
        /// model; dropped on a new pick, since it reaches every area.</summary>
        bool _toTexture;
        TexKey? _scopeTex;

        string _newName = "";
        string? _held;
        float _heldFrom;
        Vector3 _heldColour;

        int _usesVersion = -1;
        string? _usesName;
        string _usesText = "";

        void DrawMaterialTab()
        {
            var m = Runtime.Mem;
            DrawSelectionCard(m);
            ImGui.Spacing();
            ImGui.SeparatorText("Edit material");
            DrawMaterialEditor(m);
        }

        void DrawSelectionCard(IMemory? m)
        {
            if (m == null || Identity.Area < 0) { ImGui.TextDisabled("No area loaded."); return; }
            if (SelectedTexture != _scopeTex) { _scopeTex = SelectedTexture; _toTexture = false; }
            if (SelectedModel is { } mk)
            {
                ImGui.TextUnformatted(mk.ToString());
                if (BeginGrid("##selection")) { ScopeRows(); MaterialRow(m); ResultRow(m); EndGrid(); }
                SelectionNotes(m, _toTexture ? null : Blocked(mk.Area));
                return;
            }
            if (Selected is not { } k)
            {
                Wrapped("Click the picture to pick the faces under the pointer, or a model; Shift+click adds or removes faces. " +
                        "Right-click a tile in the docked map (Shift+M) for its whole half.", dim: true);
                return;
            }
            if (SelectedFaces.Count == 0)
            {
                ImGui.TextUnformatted($"{k}, whole half");
                if (k.Area == Identity.Area)
                {
                    uint rec = Identity.HalfRecord(k.X, k.Z, k.Half);
                    ImGui.TextDisabled($"mesh {m.ReadU8(rec)}, height {m.ReadU8(rec + 1):X2}, light {m.ReadU8(rec + 4) & 0x3F:X2}");
                }
            }
            else
            {
                ImGui.TextUnformatted(SelectedFaces.Count == 1 ? "1 face" : $"{SelectedFaces.Count} faces");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(string.Join("\n", SelectedFaces.GroupBy(f => (f.Tile, f.Mesh)).Select(g =>
                        $"{g.Key.Tile} mesh {g.Key.Mesh}: faces {string.Join(", ", g.Select(f => f.Face).Order())}")));
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled("Select more:");
                Flow("Connected");
                if (ImGui.Button("Connected")) GrowSelection(m, Grow.Connected);
                Tip("Add the faces joined to these by an edge, with the same texture.");
                Flow("Same texture");
                if (ImGui.Button("Same texture")) GrowSelection(m, Grow.Texture);
                Tip("Add every face of the same mesh with the same texture.");
                Flow("Whole half");
                if (ImGui.Button("Whole half")) Select(k);
                Tip("Select the half itself: everything its mesh draws, whatever the mesh.");
            }

            if (BeginGrid("##selection")) { ScopeRows(); MaterialRow(m); ResultRow(m); EndGrid(); }
            SelectionNotes(m, _toTexture ? null : Blocked(k));
        }

        /// <summary>Which rule the material row writes: most specific first, as they win.</summary>
        void ScopeRows()
        {
            bool model = SelectedModel != null, faces = SelectedFaces.Count > 0;
            Row("Assign to", "The rule the material below is written to. The most specific rule wins: " +
                             "a face on the half, the half, a face on the mesh, the mesh, then the texture.");
            int cur = _toTexture ? 2 : MeshScope && !model ? 1 : 0;
            string here = model ? "this model" : !faces ? "this half" : SelectedFaces.Count == 1 ? "this face" : $"these {SelectedFaces.Count} faces";
            if (ImGui.RadioButton(here + "##here", cur == 0)) { _toTexture = false; MeshScope = false; }
            Tip(model ? "Every draw of this model in the area." : faces ? "These faces, on this half only." : "Everything this half draws.");
            if (!model)
            {
                if (ImGui.RadioButton((faces ? "same faces, every use of the mesh" : "its mesh, every use") + "##mesh", cur == 1))
                { _toTexture = false; MeshScope = true; }
                Tip(faces ? "These faces of the mesh wherever this area draws it. A rule on a half still wins on that half."
                          : "The whole mesh wherever this area draws it. A rule on a half still wins on that half.");
            }
            if (SelectedTexture != null)
            {
                if (ImGui.RadioButton("its texture, every area##texture", cur == 2)) _toTexture = true;
                Tip("Every face drawing this art, in every area. Any other rule wins over it.");
            }
            if (_toTexture && SelectedTexture is { } tk)
            {
                Row("Any palette", "On: this art wherever it is drawn, whatever its colours. Off: only in this palette.");
                ImGui.Checkbox("##anypalette", ref AnyPalette);
                ImGui.SameLine();
                ImGui.TextDisabled((AnyPalette ? tk.AnyClut : tk).ToString());
            }
        }

        void MaterialRow(IMemory m)
        {
            string current;
            string? blocked;
            if (_toTexture && SelectedTexture is { } tk)
            {
                current = Pack.TextureMaterial(AnyPalette ? tk.AnyClut : tk) ?? "(none)";
                blocked = null;
            }
            else
            {
                current = SelectionMaterial(m) ?? "(none)";
                blocked = SelectedModel is { } mk ? Blocked(mk.Area) : Selected is { } k ? Blocked(k) : "nothing selected";
            }
            Row("Material", "What this rule names. (none) clears the rule, and the next rule down applies.");
            ImGui.BeginDisabled(blocked != null);
            if (ImGui.BeginCombo("##material", current))
            {
                if (ImGui.Selectable("(none)", current == "(none)")) Set(null);
                foreach (var mat in Pack.Materials())
                    if (ImGui.Selectable(mat.Name, mat.Name == current)) Set(mat.Name);
                ImGui.EndCombo();
            }
            ImGui.EndDisabled();

            void Set(string? name)
            {
                if (_toTexture) AssignTexture(name);
                else Assign(m, name);
            }
        }

        static void ResultRow(IMemory m)
        {
            var (mat, from) = Effective(m);
            Row("Result", "What the selection draws with, and the rule that gave it.");
            ImGui.AlignTextToFramePadding();
            if (mat == null)
            {
                ImGui.TextDisabled(SelectedFaces.Count == 0 && SelectedModel == null ? "(none) -- faces may have their own" : "(none)");
                return;
            }
            ImGui.TextUnformatted(mat);
            if (from != null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"from {from}");
            }
        }

        void SelectionNotes(IMemory m, string? blocked)
        {
            if (blocked != null) Wrapped(blocked, dim: true);
            var (current, _) = Effective(m);
            if (current is not (null or "(mixed)") && !Pack.HasMaterial(current))
                Wrapped($"'{current}' is not in the library.", Warn);
        }

        void DrawMaterialEditor(IMemory? m)
        {
            var mats = Pack.Materials().ToList();
            string? selMat = m != null ? Effective(m).Material : null;
            if (selMat != _lastSelMat)
            {
                _lastSelMat = selMat;
                if (selMat != null && Pack.HasMaterial(selMat)) _editMat = selMat;
            }
            if (_editMat == null || !Pack.HasMaterial(_editMat)) _editMat = mats.Count > 0 ? mats[0].Name : null;

            float h = ImGui.GetFrameHeight(), sp = ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetNextItemWidth(-2f * (h + sp));
            if (ImGui.BeginCombo("##editmaterial", _editMat ?? "(the library is empty)"))
            {
                foreach (var x in mats)
                    if (ImGui.Selectable(x.Name, x.Name == _editMat)) _editMat = x.Name;
                ImGui.EndCombo();
            }
            byte id = Surfaces.IdOf(_editMat);
            Tip("The library material these controls edit. It follows the selection's.\n" +
                (id != 0 ? $"id {id}" : Host.Enabled ? "no id" : "the remaster is off"));
            ImGui.SameLine();
            if (IconButton("newmaterial", Icon.Add, "+", "A new material")) ImGui.OpenPopup("##newmaterial");
            ImGui.SameLine();
            ImGui.BeginDisabled(_editMat == null);
            if (IconButton("removematerial", Icon.Trash, "x", "Remove it from the library")) ImGui.OpenPopup("##removematerial");
            ImGui.EndDisabled();
            NewMaterialPopup();
            RemoveMaterialPopup();

            int at = mats.FindIndex(x => x.Name == _editMat);
            if (at < 0) return;
            var mat = mats[at];
            string name = mat.Name;
            Wrapped(UsesText(name), dim: true);
            if (Selected != null || SelectedModel != null)
            {
                if (selMat is null or "(mixed)")
                    Wrapped(selMat == null ? $"The selection has no material. These controls edit '{name}' in the library."
                                           : $"The selection's faces differ. These controls edit '{name}' in the library.", Warn);
                else if (selMat != name && Pack.HasMaterial(selMat))
                {
                    Wrapped($"The selection draws with '{selMat}'. These controls edit '{name}'.", Warn);
                    if (ImGui.Button($"Edit {selMat}")) _editMat = selMat;
                }
            }

            ImGui.PushID(name);
            if (ImGui.CollapsingHeader("Finish", ImGuiTreeNodeFlags.DefaultOpen) && BeginGrid("##finish"))
            {
                Slider(name, "roughness", "Roughness", mat.Roughness,
                       tip: "0 polished, 1 matte. Blurs the reflection, more the further what it shows is, " +
                            "and widens the highlight lights leave.");
                Slider(name, "metalness", "Metal", mat.Metalness,
                       tip: "0 stone, 1 metal. Reflections and highlights take its own colour, it reflects at least " +
                            "this much, as strongly looking straight at it, and its own colour darkens. " +
                            "The highlight works without reflections; the rest needs them.");
                EndGrid();
            }
            if (ImGui.CollapsingHeader("Reflection", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool on = Reflections.AnySource;
                if (!on)
                {
                    Wrapped("No reflections are on, so these do nothing yet.", Warn);
                    if (ImGui.Button("Turn on world reflections"))
                    {
                        RetainedMap.SetEnabled(true);
                        PatchSettings.Set(RetainedMap.OnKey, true);
                    }
                    Tip("Video > Experimental > World reflections, saved like the setting.");
                }
                ImGui.BeginDisabled(!on);
                if (BeginGrid("##reflection"))
                {
                    Slider(name, "reflectivity", "Edge reflection", mat.Reflectivity,
                           tip: "How much it reflects at a grazing angle.");
                    Slider(name, "f0", "F0", mat.F0,
                           tip: "How much it reflects looking straight at it (Fresnel F0). Usually well under the edge reflection.");
                    EndGrid();
                }
                ImGui.EndDisabled();
            }
            if (ImGui.CollapsingHeader("Shading", ImGuiTreeNodeFlags.DefaultOpen) && BeginGrid("##shading"))
            {
                Slider(name, "specular", "Shine from lights", mat.Specular,
                       tip: "The highlight authored lights (and glows) leave on it. Its size is the roughness.");
                Slider(name, "occlusion", "Ambient occlusion", mat.Occlusion,
                       tip: "How much ambient occlusion darkens it: 0 none, 1 full. A glowing material defaults to 0.");
                EndGrid();
            }
            if (ImGui.CollapsingHeader("Glow (the surface itself)", ImGuiTreeNodeFlags.DefaultOpen) && BeginGrid("##glow"))
            {
                Colour(name, "emissive", "Colour", mat.Emissive,
                       "The glow's colour, and the colour of the light it gives off unless that has its own.");
                Slider(name, "emissiveStrength", "Brightness", mat.EmissiveStrength, 4f,
                       tip: "How bright the surface glows, fogged like the game's own light. Needs per-pixel lighting.");
                Row("Blend");
                bool additive = mat.GlowAdditive;
                if (ImGui.RadioButton("Add over texture", additive)) Pack.SetText(name, "glowMode", null);
                Tip("The glow is added over the texture, so its dark texels light too.");
                if (ImGui.RadioButton("Brighten texture", !additive)) Pack.SetText(name, "glowMode", "lit");
                Tip("The glow lights the texture, which shows it brighter (1 is the texture at full).");
                Row("Fades in fog", "Off: the glow stays bright in the dark distance, like a lamp. Add over texture only.", dim: !additive);
                ImGui.BeginDisabled(!additive);
                bool fog = !mat.GlowUnfogged;
                if (ImGui.Checkbox("##fogged", ref fog)) Pack.SetFlag(name, "glowFog", fog ? null : false);
                ImGui.EndDisabled();
                EndGrid();
            }
            if (ImGui.CollapsingHeader("Gives off light (onto what is around it)", ImGuiTreeNodeFlags.DefaultOpen)
                && BeginGrid("##light"))
            {
                Slider(name, "light", "Strength", mat.Light, Pack.MaxLight,
                       tip: "The light it gives off, with or without a glow of its own. " +
                            "It lights what is around it and not the material itself.");
                ImGui.BeginDisabled(mat.Light <= 0f);
                Slider(name, "glowRadius", "Reach", mat.GlowRadius, Pack.MaxGlowRadius, "%.2f tiles",
                       "How far the light reaches. 0 gives no light.", scale: Identity.TileUnits);
                Row("Colour", "The light's colour: the glow's, or one of its own.");
                bool same = mat.LightColour == null;
                if (ImGui.Checkbox("Same as glow##lightsame", ref same))
                {
                    if (same) Pack.RemoveField(name, "lightColour");
                    else Pack.SetColourField(name, "lightColour", mat.Emissive);
                }
                if (mat.LightColour is { } lc) Colour(name, "lightColour", "", lc);
                ImGui.EndDisabled();
                EndGrid();
            }
            if ((mat.EmissiveStrength > 0f || mat.Light > 0f) && ImGui.CollapsingHeader("Pulse") && BeginGrid("##pulse"))
            {
                Slider(name, "pulseAmount", "Depth", mat.PulseAmount, tip: "How far the glow and its light dip, on the world clock.");
                Slider(name, "pulseHz", "Rate", mat.PulseHz, 10f, "%.2f Hz");
                Row("Flicker", "On: an irregular flicker, like a flame. Off: a steady breathing.");
                bool flicker = mat.PulseFlicker;
                if (ImGui.Checkbox("##flicker", ref flicker)) Pack.SetText(name, "pulseStyle", flicker ? "flicker" : null);
                EndGrid();
            }
            ImGui.PopID();
        }

        /// <summary>What names a material, in words; counted again only when the pack changes.</summary>
        string UsesText(string name)
        {
            if (_usesVersion == Pack.Version && _usesName == name) return _usesText;
            _usesVersion = Pack.Version;
            _usesName = name;
            var u = Pack.UsesOf(name);
            static string N(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
            var parts = new List<string>();
            if (u.Faces > 0) parts.Add(N(u.Faces, "face", "faces"));
            if (u.Halves > 0) parts.Add(N(u.Halves, "half", "halves"));
            if (u.Meshes > 0) parts.Add(N(u.Meshes, "mesh", "meshes"));
            if (u.Models > 0) parts.Add(N(u.Models, "model", "models"));
            if (u.Textures > 0) parts.Add(N(u.Textures, "texture", "textures"));
            return _usesText = u.None ? "Not used by anything yet." : "Used by " + string.Join(", ", parts) + ".";
        }

        void NewMaterialPopup()
        {
            if (!ImGui.BeginPopup("##newmaterial")) return;
            if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);
            bool enter = ImGui.InputTextWithHint("##name", "new material name", ref _newName, 48, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            string n = _newName.Trim();
            ImGui.BeginDisabled(n.Length == 0 || Pack.HasMaterial(n));
            if ((ImGui.Button("Add") || enter) && n.Length > 0 && !Pack.HasMaterial(n))
            {
                Pack.AddMaterial(n);
                _editMat = n;
                _newName = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            if (n.Length > 0 && Pack.HasMaterial(n)) ImGui.TextDisabled("That name is taken.");
            ImGui.EndPopup();
        }

        void RemoveMaterialPopup()
        {
            if (!ImGui.BeginPopup("##removematerial")) return;
            ImGui.TextUnformatted($"Remove '{_editMat}' from the library?");
            ImGui.TextDisabled("What names it keeps the name and draws nothing; Undo puts it back.");
            if (ImGui.Button("Remove") && _editMat != null)
            {
                Pack.RemoveMaterial(_editMat);
                _editMat = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        /// <summary>A grid row with a slider, live while held, one undo entry on release.
        /// <paramref name="scale"/> is stored units per shown unit.</summary>
        void Slider(string name, string field, string label, float value, float max = 1f, string format = "%.3f",
                    string? tip = null, float scale = 1f)
        {
            Row(label, tip);
            float v = value / scale;
            if (ImGui.SliderFloat("##" + field, ref v, 0f, max / scale, format)) Pack.Preview(name, field, v * scale);
            if (ImGui.IsItemActivated()) { _held = name + "." + field; _heldFrom = value; }
            if (ImGui.IsItemDeactivatedAfterEdit() && _held == name + "." + field)
            {
                Pack.SetField(name, field, Pack.GetField(name, field), _heldFrom);
                _held = null;
            }
            if (tip != null) Tip(tip);
        }

        /// <summary>A grid row with a colour, live while held, one undo entry on release.</summary>
        void Colour(string name, string field, string label, Vector3 value, string? tip = null)
        {
            Row(label, tip);
            var v = value;
            bool changed = ImGui.ColorEdit3("##" + field, ref v, ImGuiColorEditFlags.Float);
            if (ImGui.IsItemActivated()) { _held = name + "." + field; _heldColour = value; }
            if (changed)
            {
                if (ImGui.IsItemActive() && _held == name + "." + field) Pack.PreviewColour(name, field, v);
                else Pack.SetColourField(name, field, v);
            }
            if (ImGui.IsItemDeactivatedAfterEdit() && _held == name + "." + field)
            {
                Pack.SetColourField(name, field, Pack.GetColour(name, field), _heldColour);
                _held = null;
            }
        }
    }
}
