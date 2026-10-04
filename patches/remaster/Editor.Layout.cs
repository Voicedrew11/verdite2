using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Remaster;

/// <summary>The editor's layout helpers: a two-column grid of labels and controls that
/// fills the panel's width, icon buttons, and the reset button an override row carries.
/// See "The editor" in docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        /// <summary>Font Awesome solid, merged into the interface font by <see cref="FontSet"/>.</summary>
        static class Icon
        {
            public const string Material = "", Light = "", Atmos = "", Level = "",
                                Prop = "", Pack = "", Save = "", Undo = "",
                                Redo = "", Reset = "", Player = "", Camera = "",
                                Place = "", Add = "", Trash = "", Warning = "",
                                Eye = "";
        }

        static readonly Vector4 Warn = new(1f, 0.75f, 0.3f, 1f), Bad = new(1f, 0.4f, 0.4f, 1f);

        /// <summary>An icon before a word, or the word alone without the icon font.</summary>
        static string L(string icon, string text) => FontSet.Loaded ? $"{icon}  {text}" : text;

        static bool BeginGrid(string id)
        {
            if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchProp)) return false;
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthStretch, 0.38f);
            ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch, 0.62f);
            return true;
        }

        static void EndGrid() => ImGui.EndTable();

        /// <summary>The colour of an overridden field's label and its dot.</summary>
        static readonly Vector4 EditedColour = new(0.45f, 0.78f, 1f, 1f);

        /// <summary>A grid row: the label (dimmed for a control that does nothing now; in
        /// the accent colour with a dot after it for a value the pack overrides) with its
        /// tooltip, then the value cell, the next item filling it short of a button at its
        /// end when <paramref name="button"/>.</summary>
        static void Row(string label, string? tip = null, bool dim = false, bool button = false, bool edited = false)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (dim) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            else if (edited) ImGui.PushStyleColor(ImGuiCol.Text, EditedColour);
            ImGui.TextUnformatted(label);
            if (dim || edited) ImGui.PopStyleColor();
            if (tip != null) Tip(edited ? tip + "\n\nOverridden by the pack; the button at the row's end puts the game's back." : tip);
            if (edited && label.Length > 0)
            {
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                float r = MathF.Max(2f, ImGui.GetFontSize() * 0.16f);
                ImGui.GetWindowDrawList().AddCircleFilled(new Vector2(max.X + r * 2.5f, (min.Y + max.Y) * 0.5f), r,
                                                          ImGui.GetColorU32(EditedColour));
            }
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(button ? -(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X) : -float.Epsilon);
        }

        /// <summary>The last item's tooltip, disabled or not.</summary>
        static void Tip(string tip)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(tip);
        }

        /// <summary>The button at the end of a row made with <c>button</c>: back to the game's.</summary>
        static bool ResetButton(string id, string tip = "Back to the game's")
        {
            ImGui.SameLine();
            return IconButton(id, Icon.Reset, "x", tip);
        }

        /// <summary>A square button showing a glyph, or a word without the icon font.</summary>
        static bool IconButton(string id, string icon, string fallback, string tip)
        {
            float h = ImGui.GetFrameHeight();
            bool hit = FontSet.Loaded ? ImGui.Button($"{icon}##{id}", new Vector2(h, h)) : ImGui.Button($"{fallback}##{id}");
            Tip(tip);
            return hit;
        }

        /// <summary>An icon button drawn pressed while <paramref name="on"/>.</summary>
        static bool IconToggle(string id, string icon, string fallback, bool on, string tip)
        {
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            bool hit = IconButton(id, icon, fallback, tip);
            if (on) ImGui.PopStyleColor();
            return hit;
        }

        /// <summary>A button that arms the next click on the picture, drawn pressed while armed.</summary>
        static bool PlaceButton(string id, bool armed, string tip)
        {
            if (armed) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            bool hit = ImGui.Button(L(Icon.Place, armed ? "Click the picture..." : "Place") + "###" + id);
            if (armed) ImGui.PopStyleColor();
            Tip(tip);
            return hit;
        }

        /// <summary>Before a button in a row of them: on the same line while it still fits.</summary>
        static void Flow(string label)
        {
            float w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
            ImGui.SameLine();
            if (ImGui.GetContentRegionAvail().X < w) ImGui.NewLine();
        }

        /// <summary>Text wrapped at the panel's edge, taken literally (no format).</summary>
        static void Wrapped(string s, Vector4? colour = null, bool dim = false)
        {
            if (colour is { } c) ImGui.PushStyleColor(ImGuiCol.Text, c);
            else if (dim) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(s);
            ImGui.PopTextWrapPos();
            if (colour != null || dim) ImGui.PopStyleColor();
        }

        /// <summary>Text at the right end of the current line.</summary>
        static void RightText(string s, Vector4? colour = null)
        {
            float w = ImGui.CalcTextSize(s).X;
            ImGui.SameLine();
            float avail = ImGui.GetContentRegionAvail().X;
            if (avail > w) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - w);
            ImGui.PushStyleColor(ImGuiCol.Text, colour is { } c ? ImGui.GetColorU32(c) : ImGui.GetColorU32(ImGuiCol.TextDisabled));
            ImGui.TextUnformatted(s);
            ImGui.PopStyleColor();
        }

        /// <summary>A string cut to a width, with an ellipsis.</summary>
        static string Fit(string s, float width)
        {
            if (ImGui.CalcTextSize(s).X <= width) return s;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (ImGui.CalcTextSize(s[..mid] + "...").X <= width) lo = mid; else hi = mid - 1;
            }
            return s[..lo] + "...";
        }
    }
}
