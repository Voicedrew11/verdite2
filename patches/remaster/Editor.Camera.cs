using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;

namespace Kf2.Remaster;

/// <summary>The editor's camera section: the free camera's switch, its speed and where
/// it is. See <see cref="EditorCamera"/> and "Phase 7, the first slice" in
/// docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        void DrawCamera()
        {
            var m = Runtime.Mem;
            bool on = EditorCamera.On;
            ImGui.BeginDisabled(m == null || Identity.Area < 0 || Stage13.Handed == null);
            if (ImGui.Checkbox("Free camera", ref on)) EditorCamera.SetOn(on, m);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold the right mouse button on the picture to look; WASD to fly, Q and E down and up, " +
                                 "Shift faster, Ctrl slower, the wheel for the speed. The player stays where they are.");
            if (!EditorCamera.On || m == null) return;

            ImGui.SameLine();
            if (ImGui.Button("Back to the player")) EditorCamera.ToPlayer(m);
            float speed = EditorCamera.Speed;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Speed", ref speed, EditorCamera.MinSpeed, EditorCamera.MaxSpeed, "%.0f /s",
                                  ImGuiSliderFlags.Logarithmic))
                EditorCamera.Speed = speed;
            var c = EditorCamera.Current;
            ImGui.TextDisabled($"at {c.X} {c.Y} {c.Z}, tile {c.X >> 11},{c.Z >> 11}; pitch {c.Pitch}, yaw {c.Yaw}");
        }
    }
}
