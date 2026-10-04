using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>The editor's header, above the tabs and never scrolled: the remaster's
/// switch, save and undo, the area and the selection, the free camera, and the
/// warnings. See <see cref="EditorCamera"/> and "The editor" in docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        void DrawHeader()
        {
            var m = Runtime.Mem;
            bool on = Host.Enabled;
            if (ImGui.Checkbox("Remaster", ref on)) Host.SetEnabled(on);
            Tip("Apply the working pack. Off, nothing it holds reaches the picture.");
            ImGui.SameLine();
            if (IconButton("save", Icon.Save, "Save", "Save the working pack (Ctrl+S)")) Pack.Save();
            ImGui.SameLine();
            ImGui.BeginDisabled(Pack.UndoLabel == null);
            if (IconButton("undo", Icon.Undo, "Undo", Pack.UndoLabel is { } u ? $"Undo {u} (Ctrl+Z)" : "Nothing to undo")) Pack.Undo();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(Pack.RedoLabel == null);
            if (IconButton("redo", Icon.Redo, "Redo", Pack.RedoLabel is { } r ? $"Redo {r} (Ctrl+Y)" : "Nothing to redo")) Pack.Redo();
            ImGui.EndDisabled();
            if (Pack.Dirty) RightText("unsaved", Warn);
            else if (Pack.SavedAt is { } t) RightText($"saved {t:HH:mm:ss}");

            float h = ImGui.GetFrameHeight(), sp = ImGui.GetStyle().ItemSpacing.X;
            float buttons = 3f * h + 2f * sp;
            string where = Identity.Area < 0 ? "No area loaded"
                         : Identity.Settled ? $"Area {Identity.Area}" : $"Area {Identity.Area}, settling...";
            string line = SelectionSummary() is { } sel ? $"{where}  |  {sel}" : where;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Fit(line, ImGui.GetContentRegionAvail().X - buttons - sp));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Identity.Settled ? $"{line}\nfingerprint {Identity.FingerprintText}" : line);
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, ImGui.GetContentRegionAvail().X - buttons));
            if (IconToggle("highlight", Icon.Eye, "Hl", _highlight, "Tint the selection on the picture")) _highlight = !_highlight;
            ImGui.SameLine();
            ImGui.BeginDisabled(m == null || Identity.Area < 0);
            if (IconButton("player", Icon.Player, "Here", "Select the tile half the player stands on") && m != null)
                Select(Identity.PlayerTile(m));
            ImGui.EndDisabled();
            ImGui.SameLine();
            bool cam = EditorCamera.On;
            ImGui.BeginDisabled(m == null || Identity.Area < 0 || Stage13.Handed == null);
            if (IconToggle("camera", Icon.Camera, "Cam", cam,
                    "Free camera: hold the right mouse button on the picture to look; WASD to fly, Q and E down and up, " +
                    "Shift faster, Ctrl slower, the wheel for the speed. The player stays where they are."))
                EditorCamera.SetOn(!cam, m);
            ImGui.EndDisabled();
            if (EditorCamera.On && m != null) DrawCameraRow(m);

            if (_pickWhy != null) Wrapped($"Nothing picked: {_pickWhy}.", dim: true);
            DrawWarnings();
        }

        /// <summary>The selection in a few words.</summary>
        static string? SelectionSummary()
        {
            if (SelectedModel is { } mk) return mk.ToString();
            if (Selected is not { } k) return null;
            int n = SelectedFaces.Count, halves = SelectedFaces.Select(f => f.Tile).Distinct().Count();
            return n == 0 ? $"{k}, whole half"
                 : halves > 1 ? $"{n} faces on {halves} halves"
                 : n == 1 ? $"{k}, 1 face" : $"{k}, {n} faces";
        }

        void DrawCameraRow(IMemory m)
        {
            float speed = EditorCamera.Speed;
            ImGui.SetNextItemWidth(-(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));
            if (ImGui.SliderFloat("##speed", ref speed, EditorCamera.MinSpeed, EditorCamera.MaxSpeed, "fly %.0f /s",
                                  ImGuiSliderFlags.Logarithmic))
                EditorCamera.Speed = speed;
            var c = EditorCamera.Current;
            Tip($"The free camera's speed (the wheel, while looking).\nAt {c.X} {c.Y} {c.Z}, tile {c.X >> 11},{c.Z >> 11}; pitch {c.Pitch}, yaw {c.Yaw}.");
            ImGui.SameLine();
            if (IconButton("toplayer", Icon.Reset, "Back", "Back to the player")) EditorCamera.ToPlayer(m);
        }

        /// <summary>What stops the pack applying: one inline, several as a count with the list on hover.</summary>
        static void DrawWarnings()
        {
            var list = new List<(string Text, bool Bad)>();
            if (Pack.LastError is { } e) list.Add((e, true));
            if (Surfaces.Refused is { } r) list.Add((r, true));
            if (Surfaces.MeshRefused > 0)
                list.Add(($"{Surfaces.MeshRefused} face list(s) not applied: their mesh no longer hashes as it did.", false));
            if (Surfaces.Unallocated > 0)
                list.Add(($"Only {SurfaceMaterial.Count - SurfaceMaterial.FirstAuthored} materials have an id; " +
                          $"{Surfaces.Unallocated} past them apply nowhere.", false));
            if (list.Count == 0) return;
            var colour = list.Any(w => w.Bad) ? Bad : Warn;
            if (list.Count == 1) { Wrapped(list[0].Text, colour); return; }
            ImGui.TextColored(colour, L(Icon.Warning, $"{list.Count} warnings"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", list.Select(w => w.Text)));
        }
    }
}
