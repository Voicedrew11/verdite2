using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>The editor's level section: the selected half's fields, each as the game
/// has it and as edited, written to <c>level.json</c> through <see cref="Pack"/>. A
/// slider is one undo entry, taken when it is let go. See "Phase 6, the first slice"
/// in docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        static readonly Vector4 LevelWarn = new(1f, 0.75f, 0.3f, 1f), LevelBad = new(1f, 0.4f, 0.4f, 1f);

        /// <summary>The field a slider is being dragged on, and where it is.</summary>
        TileField? _levelHeld;
        int _levelDraft;
        string? _levelError;

        void DrawLevel()
        {
            ImGui.Text("Level");
            ImGui.SameLine();
            ImGui.TextColored(LevelWarn, $"({Level.Label})");
            bool on = Level.Enabled;
            if (ImGui.Checkbox("Apply level edits", ref on)) Level.SetEnabled(on);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Tile edits change floors, collision and what is drawn, which is gameplay. " +
                                 "They apply only with this and the remaster on, and never reach a save.");

            var m = Runtime.Mem;
            if (m == null || Identity.Area < 0) { ImGui.TextDisabled("No area loaded."); return; }
            if (Level.Enabled && Level.Refused is { } refused) ImGui.TextColored(LevelBad, refused);
            ImGui.TextDisabled($"{Level.Applied} of {Level.Authored} edited half/halves applied; " +
                               $"the game rewrites {TileRewrites.Count} half/halves here");
            if (Selected is not { } k) { ImGui.TextDisabled("Select a half to edit it."); return; }

            ImGui.Text(k.ToString());
            if (Level.Status(k) is { } status) { ImGui.SameLine(); ImGui.TextDisabled(status); }
            if (TileRewrites.Rewritten(k) is int bits and > 0)
                ImGui.TextColored(LevelWarn, $"The game rewrites this half ({TileRewrites.DescribeBytes(bits)}): a door, a lift or an " +
                                             "object on it. An edit is refused while it has, and undone when it does.");
            string? why = Level.CannotEdit(k);
            if (why != null) ImGui.TextColored(LevelBad, why);
            if (_levelError != null) ImGui.TextColored(LevelBad, _levelError);

            ImGui.BeginDisabled(why != null);
            var edit = Pack.LevelEdit(k);
            foreach (var f in TileField.All) DrawField(m, k, f);
            if (edit != null && ImGui.Button("Reset the half")) Pack.ResetLevelHalf(k);
            ImGui.EndDisabled();
        }

        void DrawField(IMemory m, TileKey k, TileField f)
        {
            var (live, loadedOrNull, authored) = Level.State(m, k, f);
            int loaded = loadedOrNull ?? live;
            int value = _levelHeld == f ? _levelDraft : authored ?? live;

            ImGui.PushID(f.Name);
            if (f.IsFlag)
            {
                bool flag = value != 0;
                if (ImGui.Checkbox(f.Name, ref flag)) Commit(k, f, flag ? 1 : 0, loaded);
            }
            else if (f.IsBits)
            {
                ImGui.Text(f.Name);
                for (int bit = 0x80; bit > 0; bit >>= 1)
                {
                    if ((f.Mask & bit) == 0) continue;
                    ImGui.SameLine();
                    bool set = (value & bit) != 0;
                    if (ImGui.Checkbox($"0x{bit:X2}", ref set)) Commit(k, f, set ? value | bit : value & ~bit, loaded);
                }
            }
            else
            {
                ImGui.SetNextItemWidth(180);
                if (f == TileField.Height || f == TileField.Light) ImGui.SliderInt(f.Name, ref value, 0, f.Max);
                else ImGui.InputInt(f.Name, ref value);
                if (ImGui.IsItemActive()) { _levelHeld = f; _levelDraft = value; }
                if (ImGui.IsItemDeactivatedAfterEdit()) { _levelHeld = null; Commit(k, f, value, loaded); }
                else if (!ImGui.IsItemActive() && _levelHeld == f) _levelHeld = null;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{f.Description}. The game's: {loaded} as loaded, {live} now.");
            if (authored != null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("game's")) Commit(k, f, loaded, loaded);
            }
            if (f == TileField.Height)
                ImGui.TextDisabled($"  floor Y {-(value << 7)}; neighbours " +
                                   string.Join(", ", Level.NeighbourHeights(m, k).Select(n => $"{n.Side} {n.Height?.ToString() ?? "-"}")));
            ImGui.PopID();
        }

        /// <summary>One undo entry; a value equal to the game's as loaded clears the field.</summary>
        void Commit(TileKey k, TileField f, int value, int loaded)
        {
            _levelError = Level.Unusable(f, value);
            if (_levelError != null) return;
            Pack.SetLevelField(k, f, value == loaded ? null : value, Identity.FingerprintText);
        }
    }
}
