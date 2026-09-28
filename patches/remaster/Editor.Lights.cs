using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Remaster;

/// <summary>The editor's Lights tab: the area's authored lights, added at the eye or
/// on a click, and each drawn over the picture in the world (reach, a line to the floor,
/// a spot's cone) while the tab is open, dragged across the screen at its depth or
/// along a world axis by the selected light's arrows. See "Phase 2, the first slice"
/// and "The light gizmo is drawn in the world" in docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        bool _placing;
        JsonObject? _lightBefore;
        string? _dragging;
        float _dragZ;
        Vector2 _dragGrab;

        /// <summary>The arrow being dragged, or -1 for a drag across the screen.</summary>
        int _dragAxis = -1;

        /// <summary>Where the mouse ray met the dragged arrow's line when it was grabbed.</summary>
        float _dragT;

        static bool LightsBlocked(out string? why)
        {
            why = !Identity.Settled ? "the area is settling" : Lights.Refused;
            return why != null;
        }

        void AddLight(Vector3 at)
        {
            if (LightsBlocked(out _)) return;
            string name = Pack.FreeLightName(Identity.Area);
            if (Pack.AddLight(Identity.Area, Identity.FingerprintText, name, at)) SelectLight(name);
        }

        void DrawLightsTab()
        {
            var m = Runtime.Mem;
            if (m == null || Identity.Area < 0) { ImGui.TextDisabled("No area loaded."); return; }
            int area = Identity.Area;
            if (!PerPixelLighting.Enabled) Wrapped("Per-pixel lighting is off; a light is drawn only with it.", Warn);
            if (!RemasterUniforms.Supported) Wrapped("This renderer has no light term (GL core only).", Warn);
            bool blocked = LightsBlocked(out string? why);
            if (why != null && Lights.Refused != null) Wrapped(why, Bad);

            ImGui.BeginDisabled(blocked);
            if (ImGui.Button(L(Icon.Add, "Add at eye"))) AddLight(PlayerLightPosition(m));
            Tip("A point light where the eye is.");
            ImGui.SameLine();
            if (PlaceButton("placelight", _placing, "The next click on the picture adds a light just short of the surface under it. Esc cancels."))
            {
                _placing = !_placing;
                _placingProp = false;
            }
            ImGui.EndDisabled();

            var lights = Pack.Lights(area).ToList();
            if (lights.Count == 0) ImGui.TextDisabled("No lights in this area.");
            else
            {
                float rows = Math.Min(lights.Count, 6);
                var size = new Vector2(0f, rows * ImGui.GetTextLineHeightWithSpacing() + 2f * ImGui.GetStyle().WindowPadding.Y);
                if (ImGui.BeginChild("##lights", size, ImGuiChildFlags.Border))
                    foreach (var l in lights)
                        if (ImGui.Selectable($"{l.Name}  ({(l.Spot ? "spot" : "point")}{(l.Off ? ", off" : "")})", l.Name == SelectedLight))
                            SelectLight(l.Name);
                ImGui.EndChild();
                if (Host.Enabled) Tip($"{Lights.Authored} in the area, {Lights.Sent} drawn, {Lights.Culled} culled");
            }
            if (SelectedLight is not { } name || Pack.GetLight(area, name) is not { } sel)
            {
                Wrapped("Click a light's dot on the picture, or one in the list, to edit it.", dim: true);
                return;
            }

            ImGui.SeparatorText(name);
            ImGui.PushID("light:" + name);
            ImGui.BeginDisabled(blocked);
            if (BeginGrid("##light"))
            {
                Row("Type");
                int type = sel.Spot ? 1 : 0;
                if (ImGui.Combo("##type", ref type, "point\0spot\0"))
                    Pack.SetLight(area, name, $"type = {(type == 1 ? "spot" : "point")}", o => o["type"] = type == 1 ? "spot" : "point");

                const string posTip = "World units: a tile is 2048, a height step 128, and up is -Y.";
                Row("Position", posTip, button: true);
                var pos = sel.Position;
                Edited(area, name, "position", ImGui.DragFloat3("##position", ref pos, 8f, 0f, 0f, "%.0f"), o => Pack.SetPosition(o, pos));
                Tip(posTip);
                ImGui.SameLine();
                if (IconButton("toeye", Icon.Player, "Eye", "Move it to the eye"))
                    Pack.SetLight(area, name, "move to eye", o => Pack.SetPosition(o, PlayerLightPosition(m)));

                Row("Colour");
                var col = sel.Colour;
                Edited(area, name, "colour", ImGui.ColorEdit3("##colour", ref col, ImGuiColorEditFlags.Float), o => Pack.SetColour(o, col));

                Row("Intensity");
                float intensity = sel.Intensity;
                Edited(area, name, "intensity", ImGui.SliderFloat("##intensity", ref intensity, 0f, 4f, "%.2f"),
                       o => Pack.SetNumber(o, "intensity", intensity));

                Row("Radius", "How far it reaches, in world units (a tile is 2048).");
                float radius = sel.Radius;
                Edited(area, name, "radius", ImGui.SliderFloat("##radius", ref radius, 256f, 16384f, "%.0f", ImGuiSliderFlags.Logarithmic),
                       o => Pack.SetNumber(o, "radius", radius));

                if (sel.Spot)
                {
                    Row("Direction", "Where the spot points, in world axes.", button: true);
                    var dir = sel.Direction;
                    Edited(area, name, "direction", ImGui.DragFloat3("##direction", ref dir, 0.01f, -1f, 1f, "%.2f"), o => Pack.SetDirection(o, dir));
                    ImGui.SameLine();
                    if (IconButton("aim", Icon.Camera, "Aim", "Aim it along the view"))
                    {
                        var fwd = Lights.ReadView(m).ToWorld(new Vector3(0, 0, 1)) - Lights.ReadView(m).ToWorld(Vector3.Zero);
                        Pack.SetLight(area, name, "aim", o => Pack.SetDirection(o, fwd));
                    }
                    float inner = sel.ConeInner, outer = sel.ConeOuter;
                    Row("Inner cone", "Full brightness inside this angle.");
                    Edited(area, name, "cone", ImGui.SliderFloat("##inner", ref inner, 0f, 89f, "%.0f deg"), o => Pack.SetCone(o, inner, outer));
                    Row("Outer cone", "No light outside this angle.");
                    Edited(area, name, "cone", ImGui.SliderFloat("##outer", ref outer, 1f, 89f, "%.0f deg"), o => Pack.SetCone(o, inner, outer));
                }

                const string flickerTip = "How far the intensity dips. It steps with the world, so it holds while the editor pauses it.";
                float amount = sel.FlickerAmount, hz = sel.FlickerHz > 0f ? sel.FlickerHz : 7f;
                Row("Flicker", flickerTip);
                Edited(area, name, "flicker", ImGui.SliderFloat("##flicker", ref amount, 0f, 1f, "%.2f"), o => Pack.SetFlicker(o, amount, hz));
                if (amount > 0f)
                {
                    Row("Flicker rate");
                    Edited(area, name, "flicker", ImGui.SliderFloat("##flickerhz", ref hz, 0.5f, 10f, "%.1f Hz"), o => Pack.SetFlicker(o, amount, hz));
                }

                Row("On");
                bool on = !sel.Off;
                if (ImGui.Checkbox("##on", ref on))
                    Pack.SetLight(area, name, on ? "on" : "off", o => { if (on) o.Remove("enabled"); else o["enabled"] = false; });

                string shadowTip = Lights.ShadowsOn
                    ? $"The area's walls and floors{(RetainedScene.ShadowModels ? ", its creatures and its objects" : "")} cast shadows from this light. The nearest {RemasterUniforms.MaxShadows} shadowed lights in view get one."
                    : "Shadows are off (KF2_REMASTER_SHADOWS=0).";
                Row("Shadows", shadowTip, dim: !Lights.ShadowsOn);
                bool shadows = sel.Shadows;
                ImGui.BeginDisabled(!Lights.ShadowsOn);
                if (ImGui.Checkbox("##shadows", ref shadows))
                    Pack.SetLight(area, name, shadows ? "shadows" : "no shadows", o => { if (shadows) o.Remove("shadows"); else o["shadows"] = false; });
                ImGui.EndDisabled();
                Tip(shadowTip);
                EndGrid();
            }
            if (ImGui.Button(L(Icon.Trash, "Delete"))) { Pack.RemoveLight(area, name); SelectLight(null); }
            ImGui.EndDisabled();
            ImGui.PopID();
            Wrapped("On the picture: drag a light's dot to move it across the screen at its depth (Shift: straight up and down), " +
                    "or an arrow to move it along that axis (Ctrl: a height step at a time).", dim: true);
        }

        /// <summary>Live while a control is held, one undo entry on release; a change made
        /// in one step (typed in, or a colour picked) is its own entry.</summary>
        void Edited(int area, string name, string label, bool changed, Action<JsonObject> apply)
        {
            if (ImGui.IsItemActivated()) _lightBefore = Pack.LightSnapshot(area, name);
            if (changed)
            {
                if (ImGui.IsItemActive() && _lightBefore != null) Pack.PreviewLight(area, name, apply);
                else Pack.SetLight(area, name, label, apply);
            }
            if (ImGui.IsItemDeactivatedAfterEdit() && _lightBefore != null) Pack.CommitLight(area, name, label, _lightBefore);
            if (ImGui.IsItemDeactivated()) _lightBefore = null;
        }

        /// <summary>The light whose marker is under a window position.</summary>
        static string? LightUnder(RecompOne.Runtime.Memory.IMemory m, Vector2 mouse)
        {
            if (!OutputView.Valid || OutputView.GameW <= 0) return null;
            var v = Lights.ReadView(m);
            string? best = null;
            float bestD = 10f * 10f;
            foreach (var l in Pack.Lights(Identity.Area))
            {
                if (!v.Project(l.Position, out var s, out _)) continue;
                float d = Vector2.DistanceSquared(WindowPixel(s), mouse);
                if (d < bestD) { bestD = d; best = l.Name; }
            }
            return best;
        }

        void BeginDrag(RecompOne.Runtime.Memory.IMemory m, string name)
        {
            if (LightsBlocked(out _) || Pack.GetLight(Identity.Area, name) is not { } l) return;
            var v = Lights.ReadView(m);
            if (!v.Project(l.Position, out var s, out float z)) return;
            _dragging = name;
            _dragAxis = -1;
            _dragZ = z;
            _dragGrab = s;
            _lightBefore = Pack.LightSnapshot(Identity.Area, name);
        }

        /// <summary>The world axes the arrows move a light along; Y's points up, which is -Y.</summary>
        static readonly Vector3[] Axes = [Vector3.UnitX, -Vector3.UnitY, Vector3.UnitZ];
        static readonly Vector4[] AxisColours = [new(0.95f, 0.3f, 0.3f, 1f), new(0.35f, 0.9f, 0.35f, 1f), new(0.35f, 0.55f, 1f, 1f)];
        static readonly string[] AxisNames = ["X", "Y", "Z"];

        /// <summary>An arrow's length on the window, whatever the light's distance.</summary>
        const float ArrowPx = 70f;

        /// <summary>How far from the centre an arrow starts taking a click; the dot inside
        /// it is the drag across the screen.</summary>
        const float ArrowDeadPx = 12f;

        /// <summary>What Ctrl snaps an arrow drag to: a height step.</summary>
        const float AxisSnap = 128f;

        void BeginAxisDrag(RecompOne.Runtime.Memory.IMemory m, string name, int axis, Vector2 px)
        {
            if (LightsBlocked(out _) || Pack.GetLight(Identity.Area, name) is not { } l) return;
            if (AxisParam(Lights.ReadView(m), l.Position, Axes[axis], px) is not float t) return;
            _dragging = name;
            _dragAxis = axis;
            _dragT = t;
            _lightBefore = Pack.LightSnapshot(Identity.Area, name);
        }

        /// <summary>How far along the line through <paramref name="o"/> in direction
        /// <paramref name="a"/> the point nearest the eye's ray through game pixel
        /// <paramref name="px"/> lies; null when the line runs along the ray.</summary>
        static float? AxisParam(in Lights.View v, Vector3 o, Vector3 a, Vector2 px)
        {
            var eye = v.ToWorld(Vector3.Zero);
            var d = Vector3.Normalize(v.Unproject(px, 1024f) - eye);
            float b = Vector3.Dot(a, d), denom = 1f - b * b;
            if (denom < 1e-3f) return null;
            var w0 = o - eye;
            return (b * Vector3.Dot(d, w0) - Vector3.Dot(a, w0)) / denom;
        }

        /// <summary>The selected light's arrows on the window: its centre and each tip,
        /// false for a light behind the eye.</summary>
        static bool Arrows(in Lights.View v, Vector3 pos, out Vector2 centre, Span<Vector2> tips, Span<bool> ok)
        {
            centre = default;
            if (!v.Project(pos, out var s, out float z)) return false;
            centre = WindowPixel(s);
            float len = ArrowPx / WindowScale() * z / v.H;
            for (int i = 0; i < 3; i++)
            {
                ok[i] = v.Project(pos + Axes[i] * len, out var t, out _);
                tips[i] = ok[i] ? WindowPixel(t) : default;
            }
            return true;
        }

        /// <summary>The selected light's arrow under a window position.</summary>
        static int? AxisUnder(RecompOne.Runtime.Memory.IMemory m, Vector2 mouse)
        {
            if (!OutputView.Valid || OutputView.GameW <= 0 || SelectedLight is not { } name
                || Pack.GetLight(Identity.Area, name) is not { } l) return null;
            Span<Vector2> tips = stackalloc Vector2[3];
            Span<bool> ok = stackalloc bool[3];
            if (!Arrows(Lights.ReadView(m), l.Position, out var c, tips, ok)) return null;
            int? best = null;
            float bestD = 7f;
            for (int i = 0; i < 3; i++)
            {
                if (!ok[i]) continue;
                var dir = tips[i] - c;
                float len = dir.Length();
                if (len <= ArrowDeadPx) continue;
                dir /= len;
                float t = Math.Clamp(Vector2.Dot(mouse - c, dir), ArrowDeadPx, len + 4f);
                float d = Vector2.Distance(mouse, c + dir * t);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Across the screen at the light's depth; with Shift, straight up and
        /// down in the world, a height step at a time. By an arrow, along its world
        /// axis, snapped to a height step with Ctrl.</summary>
        void Drag(RecompOne.Runtime.Memory.IMemory m, Vector2 mouse)
        {
            if (_dragging is not { } name) return;
            int area = Identity.Area;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_lightBefore != null && Pack.LightSnapshot(area, name)?.ToJsonString() != _lightBefore.ToJsonString())
                    Pack.CommitLight(area, name, "move", _lightBefore);
                _dragging = null;
                _dragAxis = -1;
                _lightBefore = null;
                return;
            }
            if (!GamePixel(mouse, out var px) || _lightBefore == null) return;
            var v = Lights.ReadView(m);
            var origin = Vec3(_lightBefore["position"]);
            Vector3 to;
            if (_dragAxis >= 0)
            {
                if (AxisParam(v, origin, Axes[_dragAxis], px) is not float t) return;
                // A line nearly along the ray throws the nearest point a long way.
                float d = Math.Clamp(t - _dragT, -32768f, 32768f);
                if (ImGui.GetIO().KeyCtrl) d = MathF.Round(d / AxisSnap) * AxisSnap;
                to = origin + Axes[_dragAxis] * d;
            }
            else if (ImGui.GetIO().KeyShift)
            {
                float dy = (px.Y - _dragGrab.Y) * _dragZ / v.H;
                to = origin with { Y = origin.Y + MathF.Round(dy / 128f) * 128f };
            }
            else to = origin + (v.Unproject(px, _dragZ) - v.Unproject(_dragGrab, _dragZ));
            Pack.PreviewLight(area, name, o => Pack.SetPosition(o, to));
        }

        static Vector3 Vec3(JsonNode? n)
            => n is JsonArray a && a.Count >= 3
                ? new Vector3((float)a[0]!.GetValue<double>(), (float)a[1]!.GetValue<double>(), (float)a[2]!.GetValue<double>())
                : Vector3.Zero;

        /// <summary>A light's core in world units, so the dot shrinks with distance.</summary>
        const float CoreUnits = 96f;

        /// <summary>
        /// Each light drawn in the world rather than on the screen, so its depth reads:
        /// its reach as a ring at its own height (the selected light's as a sphere of
        /// three rings), a line dropped to the floor below it with a ring where it lands,
        /// a spot's cone, and a core that shrinks with distance. The selected light has
        /// an arrow per world axis.
        /// </summary>
        void DrawGizmos(RecompOne.Runtime.Memory.IMemory m, Vector2 mouse)
        {
            if (!OutputView.Valid || OutputView.GameW <= 0) return;
            var v = Lights.ReadView(m);
            float scale = WindowScale();
            var dl = ImGui.GetForegroundDrawList();
            dl.PushClipRect(OutputView.Min, OutputView.Max, true);
            Pack.Light? selected = null;
            foreach (var l in Pack.Lights(Identity.Area))
            {
                bool sel = l.Name == SelectedLight;
                if (sel) selected = l;
                uint ring = ImGui.GetColorU32(sel ? new Vector4(1f, 0.85f, 0.2f, 0.9f) : new Vector4(1f, 1f, 1f, 0.35f));
                uint faint = ImGui.GetColorU32(sel ? new Vector4(1f, 0.85f, 0.2f, 0.45f) : new Vector4(1f, 1f, 1f, 0.2f));
                var p = l.Position;

                Circle(dl, v, p, Vector3.UnitX, Vector3.UnitZ, l.Radius, ring, sel ? 1.5f : 1f);
                if (sel)
                {
                    Circle(dl, v, p, Vector3.UnitX, Vector3.UnitY, l.Radius, faint, 1f);
                    Circle(dl, v, p, Vector3.UnitZ, Vector3.UnitY, l.Radius, faint, 1f);
                }
                float? floor = FloorBelow(m, p);
                if (floor is float fy)
                {
                    var foot = p with { Y = fy };
                    if (Segment(v, p, foot, out var a, out var b)) dl.AddLine(a, b, faint, sel ? 1.5f : 1f);
                    Circle(dl, v, foot, Vector3.UnitX, Vector3.UnitZ, 128f, ring, 1f, 16);
                }
                if (l.Spot) Cone(dl, v, l, sel ? ring : faint, sel);

                if (!v.Project(p, out var s, out float z)) continue;
                var w = WindowPixel(s);
                uint fill = ImGui.GetColorU32(new Vector4(l.Colour.X, l.Colour.Y, l.Colour.Z, l.Off ? 0.3f : 1f));
                float core = Math.Clamp(CoreUnits * v.H / z * scale, 3f, 9f);
                dl.AddCircleFilled(w, core, fill);
                dl.AddCircle(w, core + 1f, ring, 16, 2f);
                if (sel)
                    dl.AddText(w + new Vector2(core + 6f, -core - 10f), ring,
                               floor is float f ? $"{l.Name}  {f - p.Y:0} above the floor" : l.Name);
            }
            if (selected is { } cur)
            {
                Span<Vector2> tips = stackalloc Vector2[3];
                Span<bool> ok = stackalloc bool[3];
                int hot = _dragging == cur.Name && _dragAxis >= 0 ? _dragAxis
                        : _dragging == null && OutputView.Hovered ? AxisUnder(m, mouse) ?? -1 : -1;
                if (Arrows(v, cur.Position, out var c, tips, ok))
                    for (int i = 0; i < 3; i++)
                        if (ok[i]) Arrow(dl, c, tips[i], i, i == hot);
            }
            dl.PopClipRect();
        }

        static void Arrow(ImDrawListPtr dl, Vector2 from, Vector2 tip, int axis, bool hot)
        {
            var dir = tip - from;
            float len = dir.Length();
            if (len < 1f) return;
            dir /= len;
            var side = new Vector2(-dir.Y, dir.X);
            uint col = ImGui.GetColorU32(hot ? new Vector4(1f, 0.95f, 0.4f, 1f) : AxisColours[axis]);
            float head = MathF.Min(12f, len * 0.5f);
            var foot = tip - dir * head;
            dl.AddLine(from + dir * MathF.Min(ArrowDeadPx, len * 0.3f), foot, col, hot ? 3.5f : 2.5f);
            dl.AddTriangleFilled(tip, foot + side * head * 0.45f, foot - side * head * 0.45f, col);
            dl.AddText(tip + dir * 4f + new Vector2(-4f, -7f), col, AxisNames[axis]);
        }

        /// <summary>A spot's outer cone out to its reach (a little way, unselected), and
        /// its inner cone's rim.</summary>
        static void Cone(ImDrawListPtr dl, in Lights.View v, Pack.Light l, uint col, bool sel)
        {
            if (l.Direction.LengthSquared() < 1e-8f) return;
            var d = Vector3.Normalize(l.Direction);
            var u = Vector3.Normalize(Vector3.Cross(d, MathF.Abs(d.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
            var w = Vector3.Cross(d, u);
            float len = sel ? l.Radius : MathF.Min(l.Radius, 1024f);
            float outer = Math.Clamp(l.ConeOuter, 1f, 89f) * MathF.PI / 180f;
            float inner = Math.Clamp(l.ConeInner, 0f, Math.Clamp(l.ConeOuter, 1f, 89f)) * MathF.PI / 180f;
            // A wide cone's rim at the reach runs off to the side; hold the slant at the reach instead.
            float along = len * MathF.Cos(outer), r = len * MathF.Sin(outer);
            var rim = l.Position + d * along;
            Circle(dl, v, rim, u, w, r, col, 1.5f, 32);
            for (int k = 0; k < 4; k++)
            {
                float a = k * MathF.PI / 2f;
                if (Segment(v, l.Position, rim + (u * MathF.Cos(a) + w * MathF.Sin(a)) * r, out var p, out var q))
                    dl.AddLine(p, q, col, 1f);
            }
            if (sel && inner > 0f)
                Circle(dl, v, l.Position + d * len * MathF.Cos(inner), u, w, len * MathF.Sin(inner), col, 1f, 32);
        }

        /// <summary>A circle in the world, in the plane of <paramref name="u"/> and
        /// <paramref name="w"/>, cut at the eye's near plane.</summary>
        static void Circle(ImDrawListPtr dl, in Lights.View v, Vector3 c, Vector3 u, Vector3 w, float r, uint col,
                           float thick, int segs = 48)
        {
            var prev = c + u * r;
            for (int i = 1; i <= segs; i++)
            {
                float a = i * 2f * MathF.PI / segs;
                var next = c + (u * MathF.Cos(a) + w * MathF.Sin(a)) * r;
                if (Segment(v, prev, next, out var p, out var q)) dl.AddLine(p, q, col, thick);
                prev = next;
            }
        }

        /// <summary>A world segment to the window, cut where it passes behind the eye.</summary>
        static bool Segment(in Lights.View v, Vector3 a, Vector3 b, out Vector2 wa, out Vector2 wb)
        {
            const float near = 16f;
            wa = wb = default;
            var p = v.ToView(a);
            var q = v.ToView(b);
            if (p.Z < near && q.Z < near) return false;
            if (p.Z < near) p = Vector3.Lerp(p, q, (near - p.Z) / (q.Z - p.Z));
            else if (q.Z < near) q = Vector3.Lerp(q, p, (near - q.Z) / (p.Z - q.Z));
            wa = WindowPixel(new Vector2(v.Cx + v.H * p.X / p.Z, v.Cy + v.H * p.Y / p.Z));
            wb = WindowPixel(new Vector2(v.Cx + v.H * q.X / q.Z, v.Cy + v.H * q.Y / q.Z));
            return true;
        }

        /// <summary>The floor under a point: the nearer below it of its tile's two halves'
        /// floors, or null off the map or with none below.</summary>
        static float? FloorBelow(RecompOne.Runtime.Memory.IMemory m, Vector3 at)
        {
            int tx = (int)MathF.Floor(at.X / Identity.TileUnits), tz = (int)MathF.Floor(at.Z / Identity.TileUnits);
            if ((uint)tx >= Identity.Span || (uint)tz >= Identity.Span) return null;
            float? best = null;
            for (int half = 0; half < 2; half++)
            {
                uint rec = Identity.HalfRecord(tx, tz, half);
                if (m.ReadU8(rec) >= 240) continue;
                float y = -(m.ReadU8(rec + (uint)TileField.Height.Offset) << 7);
                // Up is -Y, so below is the larger Y.
                if (y >= at.Y && (best == null || y < best)) best = y;
            }
            return best;
        }

        /// <summary>Window pixels per game pixel.</summary>
        static float WindowScale()
            => OutputView.Size.X / (OutputView.GameW + 2 * Display.WideMargin(OutputView.GameW));
    }
}
