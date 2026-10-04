using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>The editor's Level tab: the selected half's fields, each as the game
/// has it and as edited, written to <c>level.json</c> through <see cref="Pack"/>. A
/// slider is one undo entry, taken when it is let go. See "Phase 6, the first slice"
/// in docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        /// <summary>The field a slider is being dragged on, and where it is.</summary>
        TileField? _levelHeld;
        int _levelDraft;
        string? _levelError;

        void DrawLevelTab()
        {
            bool on = Level.Enabled;
            if (ImGui.Checkbox("Apply level edits", ref on)) Level.SetEnabled(on);
            Tip("Tile edits change floors, collision and what is drawn, which is gameplay. " +
                "They apply only with this and the remaster on, and never reach a save.");
            ImGui.SameLine();
            ImGui.TextColored(Warn, $"({Level.Label})");

            var m = Runtime.Mem;
            if (m == null || Identity.Area < 0) { ImGui.TextDisabled("No area loaded."); return; }
            if (Level.Enabled && Level.Refused is { } refused) Wrapped(refused, Bad);
            Wrapped($"{Level.Applied} of {Level.Authored} edited half/halves applied; " +
                    $"the game rewrites {TileRewrites.Count} half/halves here.", dim: true);
            if (Selected is not { } k)
            {
                Wrapped("Select a half to edit it: click it on the picture, right-click it in the docked map (Shift+M), " +
                        "or take the player's with the header's button.", dim: true);
                return;
            }

            ImGui.SeparatorText(k.ToString());
            if (Level.Status(k) is { } status) ImGui.TextDisabled(status);
            if (TileRewrites.Rewritten(k) is int bits and > 0)
                Wrapped($"The game rewrites this half ({TileRewrites.DescribeBytes(bits)}): a door, a lift or an " +
                        "object on it. An edit is refused while it has, and undone when it does.", Warn);
            string? why = Level.CannotEdit(k);
            if (why != null) Wrapped(why, Bad);
            if (_levelError != null) Wrapped(_levelError, Bad);

            ImGui.BeginDisabled(why != null);
            var edit = Pack.LevelEdit(k);
            if (BeginGrid("##level"))
            {
                foreach (var f in TileField.All) DrawField(m, k, f);
                EndGrid();
            }
            if (edit != null && ImGui.Button("Reset the half")) Pack.ResetLevelHalf(k);
            ImGui.EndDisabled();
        }

        void DrawField(IMemory m, TileKey k, TileField f)
        {
            var (live, loadedOrNull, authored) = Level.State(m, k, f);
            int loaded = loadedOrNull ?? live;
            int value = _levelHeld == f ? _levelDraft : authored ?? live;
            string tip = $"{f.Description}. The game's: {loaded} as loaded, {live} now.";

            ImGui.PushID(f.Name);
            Row(f.Name, tip, button: authored != null, edited: authored != null);
            if (f.IsFlag)
            {
                bool flag = value != 0;
                if (ImGui.Checkbox("##flag", ref flag)) Commit(k, f, flag ? 1 : 0, loaded);
            }
            else if (f.IsBits)
            {
                bool first = true;
                for (int bit = 0x80; bit > 0; bit >>= 1)
                {
                    if ((f.Mask & bit) == 0) continue;
                    string label = $"0x{bit:X2}";
                    if (!first) Flow(label + "    ");
                    first = false;
                    bool set = (value & bit) != 0;
                    if (ImGui.Checkbox(label, ref set)) Commit(k, f, set ? value | bit : value & ~bit, loaded);
                }
            }
            else
            {
                if (f == TileField.Height || f == TileField.Light) ImGui.SliderInt("##value", ref value, 0, f.Max);
                else ImGui.InputInt("##value", ref value);
                if (ImGui.IsItemActive()) { _levelHeld = f; _levelDraft = value; }
                if (ImGui.IsItemDeactivatedAfterEdit()) { _levelHeld = null; Commit(k, f, value, loaded); }
                else if (!ImGui.IsItemActive() && _levelHeld == f) _levelHeld = null;
            }
            Tip(tip);
            if (authored != null && ResetButton("game")) Commit(k, f, loaded, loaded);
            if (f == TileField.Height)
            {
                Row("");
                Wrapped($"floor Y {-(value << 7)}; neighbours " +
                        string.Join(", ", Level.NeighbourHeights(m, k).Select(n => $"{n.Side} {n.Height?.ToString() ?? "-"}")), dim: true);
            }
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
