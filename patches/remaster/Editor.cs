using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Window;
using Silk.NET.Input;
using HostWindow = RecompOne.Runtime.Host.HostWindow;

namespace Kf2.Remaster;

/// <summary>
/// The remaster editor, Shift+E. It writes documents only -- never a side table or a
/// uniform -- so undo, save and live reload are one operation. Open, it pauses the
/// world through <see cref="FramePacing.PauseWhen"/> as the full-screen map does;
/// the renderer keeps drawing, so an edit shows on the frozen scene.
///
/// Tiles are picked by a ray from a click on the picture, from the docked map
/// (right-click, Shift+M), or as the tile the player stands on. See "The editor" in
/// docs/REMASTER.md.
/// </summary>
public static class Editor
{
    public static bool Open => Panel.Instance.IsOpen;

    public static TileKey? Selected { get; private set; }

    /// <summary>The next click on the picture picks a tile.</summary>
    public static bool Picking;

    public static void SetOpen(bool open) => Panel.Instance.IsOpen = open;

    public static void Select(TileKey? key) => Selected = key;

    public static void Install()
    {
        FramePacing.PauseWhen(() => Open && Identity.Area >= 0);

        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!e.Pressed || e.Repeat || e.Key != (int)Key.E || PopupManager.AnyOpen) return;
            if (!HostWindow.IsKeyDown(Key.ShiftLeft) && !HostWindow.IsKeyDown(Key.ShiftRight)) return;
            Panel.Instance.IsOpen = !Panel.Instance.IsOpen;
        });
    }

    public static void Register()
    {
        Localization.Merge("""
        {
          "strings": {
            "kf2.remaster.editor": { "en": "Remaster editor", "pt-BR": "Editor de remasterização",
                                     "es-419": "Editor de remasterización" }
          }
        }
        """);
        PanelManager.Register(Panel.Instance);
        // Not restored from the saved view: open, it pauses the world, and a boot
        // that reopened it froze the area's fade-in on a dark frame.
        Panel.Instance.IsOpen = false;
    }

    sealed class Panel : IPanel
    {
        public static readonly Panel Instance = new();
        Panel() { }

        public string Name => "kf2remaster";
        public string TitleKey => "kf2.remaster.editor";
        public bool IsOpen { get; set; }

        string _newName = "";
        string? _pickStop;
        string? _held;
        float _heldFrom;

        public void Draw()
        {
            bool open = IsOpen;
            ImGui.SetNextWindowSize(new Vector2(440, 560), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin(this.Title(), ref open))
            {
                IsOpen = open;
                ImGui.End();
                return;
            }
            IsOpen = open;

            DrawStatus();
            ImGui.Separator();
            DrawSelection();
            ImGui.Separator();
            DrawMaterials();
            bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows);
            ImGui.End();

            var m = Runtime.Mem;
            if (m == null || Identity.Area < 0) return;
            var view = Pick.Read(m);
            if (Picking && !hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && GamePixel(ImGui.GetIO().MousePos, out var px))
            {
                var hit = Pick.Floor(m, view, px, out _, out _pickStop);
                if (hit != null) { Selected = hit; Picking = false; }
            }
            if (Selected is { } k && k.Area == Identity.Area) Outline(m, view, k);
        }

        void DrawStatus()
        {
            bool on = Host.Enabled;
            if (ImGui.Checkbox("Remaster on", ref on)) Host.SetEnabled(on);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Apply the working pack. Off, nothing it holds reaches the picture.");

            if (!Reflections.Enabled)
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                    "Water reflections are off; a material is only read by them so far.");

            if (Identity.Area < 0) ImGui.TextDisabled("No area loaded.");
            else
                ImGui.TextDisabled($"Area {Identity.Area}, " +
                                   (Identity.Settled ? $"fingerprint {Identity.FingerprintText}" : "settling..."));
            if (Surfaces.Refused != null) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), Surfaces.Refused);

            ImGui.TextDisabled(Pack.Root);
            if (ImGui.Button("Save")) Pack.Save();
            ImGui.SameLine();
            if (ImGui.Button("Reload")) Pack.Load();
            ImGui.SameLine();
            ImGui.BeginDisabled(Pack.UndoLabel == null);
            if (ImGui.Button("Undo")) Pack.Undo();
            if (Pack.UndoLabel != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Pack.UndoLabel);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(Pack.RedoLabel == null);
            if (ImGui.Button("Redo")) Pack.Redo();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled(Pack.Dirty ? "unsaved" : Pack.SavedAt is { } t ? $"saved {t:HH:mm:ss}" : "");
            if (Pack.LastError != null) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), Pack.LastError);
        }

        void DrawSelection()
        {
            var m = Runtime.Mem;
            ImGui.BeginDisabled(m == null || Identity.Area < 0);
            if (ImGui.Button("Player's tile") && m != null) Selected = Identity.PlayerTile(m);
            ImGui.SameLine();
            ImGui.Checkbox("Pick on the picture", ref Picking);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The next click on the game picture selects the floor under it. " +
                                 "Right-click a tile in the docked map (Shift+M) does the same.");
            ImGui.EndDisabled();
            if (Picking && _pickStop != null) ImGui.TextDisabled($"No floor there: {_pickStop}.");

            if (Selected is not { } k) { ImGui.TextDisabled("Nothing selected."); return; }
            ImGui.Text(k.ToString());
            if (m != null && k.Area == Identity.Area)
            {
                uint rec = Identity.HalfRecord(k.X, k.Z, k.Half);
                ImGui.SameLine();
                ImGui.TextDisabled($"model {m.ReadU8(rec):X2} height {m.ReadU8(rec + 1):X2} " +
                                   $"light {m.ReadU8(rec + 4) & 0x3F:X2}");
            }

            string current = Pack.TileMaterial(k) ?? "(none)";
            bool canEdit = Identity.Settled && k.Area == Identity.Area && Surfaces.Refused == null;
            ImGui.BeginDisabled(!canEdit);
            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("Material", current))
            {
                if (ImGui.Selectable("(none)", current == "(none)")) Assign(k, null);
                foreach (var mat in Pack.Materials())
                    if (ImGui.Selectable(mat.Name, mat.Name == current)) Assign(k, mat.Name);
                ImGui.EndCombo();
            }
            ImGui.EndDisabled();
            if (Pack.TileMaterial(k) is { } name && !Pack.HasMaterial(name))
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f), $"'{name}' is not in the library.");
        }

        static void Assign(TileKey k, string? material) => Pack.SetTile(k, material, Identity.FingerprintText);

        void DrawMaterials()
        {
            ImGui.Text("Materials");
            if (Surfaces.Unallocated > 0)
                ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
                    $"Only {SurfaceMaterial.Count - SurfaceMaterial.FirstAuthored} materials have an id; " +
                    $"{Surfaces.Unallocated} past them apply nowhere.");

            string? remove = null;
            foreach (var mat in Pack.Materials().ToList())
            {
                ImGui.PushID(mat.Name);
                byte id = Surfaces.IdOf(mat.Name);
                ImGui.Text(mat.Name);
                ImGui.SameLine();
                ImGui.TextDisabled(id != 0 ? $"id {id}" : Host.Enabled ? "no id" : "");
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20);
                if (ImGui.SmallButton("x")) remove = mat.Name;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove from the library");
                Slider(mat.Name, "reflectivity", "Reflectivity", mat.Reflectivity);
                Slider(mat.Name, "f0", "F0", mat.F0);
                ImGui.PopID();
            }
            if (remove != null) Pack.RemoveMaterial(remove);

            ImGui.SetNextItemWidth(200);
            bool enter = ImGui.InputTextWithHint("##new", "new material name", ref _newName, 48,
                                                 ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Add") || enter) && _newName.Trim().Length > 0)
            {
                Pack.AddMaterial(_newName.Trim());
                _newName = "";
            }
        }

        /// <summary>Live while held, one undo entry on release.</summary>
        void Slider(string name, string field, string label, float value)
        {
            ImGui.SetNextItemWidth(200);
            float v = value;
            if (ImGui.SliderFloat(label, ref v, 0f, 1f, "%.3f")) Pack.Preview(name, field, v);
            if (ImGui.IsItemActivated()) { _held = name + "." + field; _heldFrom = value; }
            if (ImGui.IsItemDeactivatedAfterEdit() && _held == name + "." + field)
            {
                Pack.SetField(name, field, Pack.GetField(name, field), _heldFrom);
                _held = null;
            }
        }

        /// <summary>A window position to a game pixel, margin and all (the MenuMouse
        /// conversion).</summary>
        static bool GamePixel(Vector2 pos, out Vector2 game)
        {
            game = default;
            if (!OutputView.Valid || OutputView.GameW <= 0 || OutputView.GameH <= 0) return false;
            var min = OutputView.Min;
            var size = OutputView.Size;
            if (pos.X < min.X || pos.Y < min.Y || pos.X > OutputView.Max.X || pos.Y > OutputView.Max.Y) return false;
            int margin = Display.WideMargin(OutputView.GameW);
            float picW = OutputView.GameW + 2 * margin;
            game = new Vector2((pos.X - min.X) / size.X * picW - margin, (pos.Y - min.Y) / size.Y * OutputView.GameH);
            return true;
        }

        static Vector2 WindowPixel(Vector2 game)
        {
            int margin = Display.WideMargin(OutputView.GameW);
            float picW = OutputView.GameW + 2 * margin;
            var min = OutputView.Min;
            var size = OutputView.Size;
            return new Vector2(min.X + (game.X + margin) / picW * size.X, min.Y + game.Y / OutputView.GameH * size.Y);
        }

        /// <summary>The selected floor's outline over the picture.</summary>
        static void Outline(RecompOne.Runtime.Memory.IMemory m, in Pick.View view, TileKey k)
        {
            if (!OutputView.Valid || OutputView.GameW <= 0) return;
            var corners = Pick.Corners(m, k);
            var pts = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                if (!Pick.Project(view, corners[i], out var s)) return;
                pts[i] = WindowPixel(s);
            }
            var dl = ImGui.GetForegroundDrawList();
            dl.PushClipRect(OutputView.Min, OutputView.Max, true);
            uint col = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 0.95f));
            for (int i = 0; i < 4; i++) dl.AddLine(pts[i], pts[(i + 1) % 4], col, 2f);
            dl.PopClipRect();
        }
    }
}
