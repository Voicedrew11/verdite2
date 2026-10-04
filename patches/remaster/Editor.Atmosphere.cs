using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Host.Window;
using IMemory = RecompOne.Runtime.Memory.IMemory;

namespace Kf2.Remaster;

/// <summary>The editor's Atmosphere tab: one light record, followed or chosen, over two
/// groups, Light and Fog, each with the whole area's settings above the record's. A
/// field shows the game's value until edited. See "The Atmosphere tab" in
/// docs/REMASTER.md.</summary>
public static partial class Editor
{
    sealed partial class Panel
    {
        int _record = -1;
        bool _followRecord = true;
        JsonObject? _recBefore;
        bool _recHeld;
        int[] _usage = [];
        int _usageSettle = -1;
        bool _showHalves, _showDirections = true;
        readonly HashSet<int> _copyTo = new();
        readonly float[] _lastYaw = new float[3];

        static string fp0() => Identity.FingerprintText;

        static int[] Rgb255(Vector3 c) => [(int)MathF.Round(c.X * 255f), (int)MathF.Round(c.Y * 255f), (int)MathF.Round(c.Z * 255f)];

        /// <summary>A record's light colour shown in the back colour's units, where 256 is
        /// full light (4096 in 4.12), so the two read on one scale.</summary>
        const float LightToWidget = 256f / 255f;

        /// <summary>View units in a tile, for the fog's start.</summary>
        const float TileUnits = 2048f;

        enum FogShape { Knee, Linear, None }

        static readonly string[] FogShapeNames = ["Knee (most areas)", "Linear", "None"];

        static FogShape ShapeOf(int word)
            => (word & 0x8000) != 0 ? FogShape.Linear : (word & 0x7FFF) >= 32000 ? FogShape.None : FogShape.Knee;

        static int StartOf(int word) => (word & 0x7FFF) >> 1;

        static int FogWord(FogShape shape, int start)
            => shape switch
            {
                FogShape.None => 32000,
                FogShape.Linear => 0x8000 | (Math.Clamp(start, 0, 15999) << 1),
                _ => Math.Clamp(start, 0, 15999) << 1,
            };

        static string DescribeFog(int word)
            => ShapeOf(word) == FogShape.None ? "no fog"
             : $"{StartOf(word) / TileUnits:0.##} tiles ({StartOf(word)} units), {(ShapeOf(word) == FogShape.Linear ? "linear" : "knee")}";

        void DrawAtmosphereTab()
        {
            var m = Runtime.Mem;
            if (m == null || Identity.Area < 0 || !Identity.Settled) { ImGui.TextDisabled("No settled area."); return; }
            int area = Identity.Area;
            if (Atmosphere.Refused is { } why) Wrapped(why, Bad);
            if (!Host.Enabled) Wrapped("The remaster is off; edits apply when it is on.", dim: true);
            if (_usageSettle != Identity.Settles) { _usage = Atmosphere.Usage(m); _usageSettle = Identity.Settles; }

            int rec = DrawRecordPicker(m, area);
            string hash = Atmosphere.SourceHash(m, rec);
            var o = Pack.GetRecord(area, rec);
            var g = Atmosphere.Game(m, rec);
            var e = Atmosphere.Effective(m, rec, o, dark: false);
            var all = Pack.GetRecord(area, Pack.AllRecords);

            ImGui.PushID("record:" + rec);
            if (ImGui.CollapsingHeader("Light", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SeparatorText("Whole area");
                if (BeginGrid("##arealight")) { DrawDarkness(area, all); EndGrid(); }
                ImGui.SeparatorText(RecordHeading(rec));
                if (Atmosphere.Darkness > 0f && rec < Atmosphere.Darkened)
                    Wrapped($"The area's darkness scales this record's light to {100f * (1f - Atmosphere.Darkness):0}% after these edits.", dim: true);
                if (BeginGrid("##reclight")) { DrawRecordLight(area, rec, hash, o, g, e); EndGrid(); }
            }
            if (ImGui.CollapsingHeader("Fog", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.SeparatorText("Whole area");
                if (BeginGrid("##areafog")) { DrawAreaFog(area, all); EndGrid(); }
                ImGui.SeparatorText(RecordHeading(rec));
                if (BeginGrid("##recfog")) { DrawRecordFog(area, rec, hash, o, g, e); EndGrid(); }
            }
            ImGui.PopID();
        }

        string RecordHeading(int rec) => $"Record {rec}" + (_followRecord ? " (under you)" : "");

        string RecordLabel(int area, int r, int under)
            => $"record {r}  ({(r < _usage.Length ? _usage[r] : 0)} halves)" +
               (r == under ? ", under you" : "") + (Pack.GetRecord(area, r) != null ? ", edited" : "");

        /// <summary>The record combo, what is shown on the picture, and the record's own
        /// actions; returns the record the groups below edit.</summary>
        int DrawRecordPicker(IMemory m, int area)
        {
            int under = Atmosphere.UnderPlayer(m);
            if (_followRecord && under >= 0) _record = under;
            if (_record < 0) _record = under >= 0 ? under : 0;
            int rec = _record;
            var o = Pack.GetRecord(area, rec);

            if (BeginGrid("##record"))
            {
                Row("Record", "Every tile half names one of 80 light records: its three lights, back colour and fog. " +
                              "Following, this is the record of the half you stand on.");
                if (ImGui.BeginCombo("##record", RecordLabel(area, rec, under) + (_followRecord ? " - following" : "")))
                {
                    if (ImGui.Selectable("Follow the player", _followRecord)) _followRecord = true;
                    ImGui.Separator();
                    for (int r = 0; r < Atmosphere.Records; r++)
                    {
                        bool show = (r < _usage.Length && _usage[r] > 0) || r == under || Pack.GetRecord(area, r) != null;
                        if (show && ImGui.Selectable(RecordLabel(area, r, under), !_followRecord && r == _record)) { _record = r; _followRecord = false; }
                    }
                    ImGui.EndCombo();
                }
                Row("On the picture", "Halves: tint the halves that use this record. Light directions: an arrow from a point " +
                                      "ahead of the eye towards each of the record's three lights, in its colour.");
                ImGui.Checkbox("Halves", ref _showHalves);
                Flow("Light directions");
                ImGui.Checkbox("Light directions", ref _showDirections);
                EndGrid();
            }

            if (o is { Hash: { } h } && h != Atmosphere.SourceHash(m, rec))
                Wrapped("The game's record has changed since this was authored; it is not applied.", Warn);

            ImGui.BeginDisabled(o == null);
            if (ImGui.Button("Copy to...")) { _copyTo.Clear(); ImGui.OpenPopup("##copyrecord"); }
            Tip("This record's overrides onto other records: each part set here replaces theirs, and they keep the rest.");
            ImGui.SameLine();
            if (ImGui.Button("Reset record")) Pack.RemoveRecord(area, rec);
            Tip("Every override on this record back to the game's.");
            ImGui.EndDisabled();
            ImGui.SameLine();
            int fields = o is { } ov ? (ov.Back != null ? 1 : 0) + (ov.Fog != null ? 1 : 0)
                                       + ov.Direction.Count(d => d != null) + ov.Colour.Count(c => c != null) : 0;
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(fields == 0 ? "nothing overridden" : $"{fields} field(s) overridden");
            Tip($"In the whole area: {Atmosphere.Applied} record override(s) written on the last frame" +
                (Atmosphere.Stale > 0 ? $", {Atmosphere.Stale} refused because the game's record changed." : "."));
            DrawCopyPopup(m, area, rec, under);
            return rec;
        }

        void DrawCopyPopup(IMemory m, int area, int rec, int under)
        {
            if (!ImGui.BeginPopup("##copyrecord")) return;
            ImGui.TextUnformatted($"Copy record {rec}'s overrides to:");
            var used = Enumerable.Range(0, Atmosphere.Records)
                                 .Where(r => r != rec && ((r < _usage.Length && _usage[r] > 0) || Pack.GetRecord(area, r) != null))
                                 .ToList();
            if (ImGui.SmallButton("Every one listed")) foreach (int r in used) _copyTo.Add(r);
            ImGui.SameLine();
            if (ImGui.SmallButton("None")) _copyTo.Clear();
            float line = ImGui.GetFrameHeightWithSpacing();
            if (ImGui.BeginChild("##copylist", new Vector2(ImGui.GetFontSize() * 18f, MathF.Min(used.Count, 12) * line + 4f), ImGuiChildFlags.None))
                foreach (int r in used)
                {
                    bool on = _copyTo.Contains(r);
                    if (ImGui.Checkbox(RecordLabel(area, r, under) + "##c" + r, ref on))
                    {
                        if (on) _copyTo.Add(r);
                        else _copyTo.Remove(r);
                    }
                }
            ImGui.EndChild();
            ImGui.BeginDisabled(_copyTo.Count == 0);
            if (ImGui.Button($"Copy to {_copyTo.Count} record(s)"))
            {
                Pack.CopyRecord(area, rec, _copyTo.Order().Select(r => (r, Atmosphere.SourceHash(m, r))).ToList(), Identity.FingerprintText);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        void DrawDarkness(int area, Pack.RecordOverride? a)
        {
            const int all = Pack.AllRecords;
            // The area's darkness: a scale on the game's own light, not an edit of it.
            const string darkTip = "How much of the game's own light the area loses: 0% is the game's, 100% leaves only authored lights and glows. " +
                                   "Scales every tile record's back colour and light colours after the record's own edits; the HUD keeps its light. Ctrl+click to type.";
            float pct = (a?.Darkness ?? 0f) * 100f;
            Row("Darkness", darkTip, button: pct > 0f, edited: pct > 0f);
            RecEdited(area, all, "", "darkness", ImGui.SliderFloat("##darkness", ref pct, 0f, 100f, "%.0f%%"),
                x => Pack.SetDarkness(x, pct / 100f));
            if (pct > 0f && ResetButton("darkness", "No darkness: the game's light"))
                Pack.SetRecord(area, all, "", fp0(), "darkness = game", x => Pack.SetDarkness(x, null));
        }

        void DrawAreaFog(int area, Pack.RecordOverride? a)
        {
            const int all = Pack.AllRecords;
            const string fogTip = "What the distance fades into; the game's is black. Needs per-pixel lighting and Fast geometry.";
            bool fogCol = a?.FogColour != null;
            var fc = a?.FogColour is { } c0 ? new Vector3(c0[0], c0[1], c0[2]) / 255f : Vector3.Zero;
            Row("Colour", fogTip, button: fogCol, edited: fogCol);
            RecEdited(area, all, "", "fog colour", ImGui.ColorEdit3("##fogcolour", ref fc), x => Pack.SetFogColour(x, Rgb255(fc)));
            if (fogCol && ResetButton("fogcolour"))
                Pack.SetRecord(area, all, "", fp0(), "fog colour = game", x => Pack.SetFogColour(x, null));

            const string curveTip = "Bends the game's own fog: below 1 thickens it close by, above 1 holds it off until further out. 1 is the game's.";
            float power = a?.FogPower ?? 1f, most = (a?.FogMax ?? 1f) * 100f;
            Row("Curve", curveTip, button: a?.FogPower != null, edited: a?.FogPower != null);
            RecEdited(area, all, "", "fog curve", ImGui.SliderFloat("##fogcurve", ref power, 0.25f, 4f, "%.2f", ImGuiSliderFlags.Logarithmic),
                x => Pack.SetFogCurve(x, power, a?.FogMax));
            if (a?.FogPower != null && ResetButton("fogcurve"))
                Pack.SetRecord(area, all, "", fp0(), "fog curve = game", x => Pack.SetFogCurve(x, null, a?.FogMax));

            const string mostTip = "The most the fog ever takes: below 100% the far distance never quite disappears. 100% is the game's.";
            Row("At most", mostTip, button: a?.FogMax != null, edited: a?.FogMax != null);
            RecEdited(area, all, "", "fog at most", ImGui.SliderFloat("##fogmost", ref most, 0f, 100f, "%.0f%%"),
                x => Pack.SetFogCurve(x, a?.FogPower, most / 100f));
            if (a?.FogMax != null && ResetButton("fogmost"))
                Pack.SetRecord(area, all, "", fp0(), "fog at most = game", x => Pack.SetFogCurve(x, a?.FogPower, null));

            const string skyTip = "The colour past the draw distance, where nothing is drawn. Unset, it is the fog colour (black without one).";
            bool sky = a?.Sky != null;
            var sk = a?.Sky is { } s0 ? new Vector3(s0[0], s0[1], s0[2]) / 255f : fc;
            Row("Sky", skyTip, button: sky, edited: sky);
            RecEdited(area, all, "", "sky", ImGui.ColorEdit3("##sky", ref sk), x => Pack.SetSky(x, Rgb255(sk)));
            if (sky && ResetButton("sky", "Back to the fog colour"))
                Pack.SetRecord(area, all, "", fp0(), "sky = fog", x => Pack.SetSky(x, null));
        }

        void DrawRecordLight(int area, int rec, string hash, Pack.RecordOverride? o, Atmosphere.Record g, Atmosphere.Record e)
        {
            string fp = Identity.FingerprintText;
            const string unitTip = " In one scale with the lights' colours: 256 is full light.";

            bool backOn = o?.Back != null;
            Row("Back colour", "The light every face gets, whichever way it faces. The game's is " +
                               $"{g.Back[0]} {g.Back[1]} {g.Back[2]}." + unitTip, button: backOn, edited: backOn);
            var back = new Vector3(e.Back[0], e.Back[1], e.Back[2]) / 255f;
            RecEdited(area, rec, hash, "back", ImGui.ColorEdit3("##back", ref back), x => Pack.SetBack(x, Rgb255(back)));
            if (backOn && ResetButton("back"))
                Pack.SetRecord(area, rec, hash, fp, "back = game", x => Pack.SetBack(x, null));

            for (int j = 0; j < 3; j++)
            {
                ImGui.PushID(j);
                DrawRecordLightRows(area, rec, hash, j, o, g, e, unitTip);
                ImGui.PopID();
            }
        }

        /// <summary>Light <paramref name="j"/>: its colour, the way it comes from as a
        /// compass bearing and an elevation, and its strength (the direction's length),
        /// each reset on its own.</summary>
        void DrawRecordLightRows(int area, int rec, string hash, int j, Pack.RecordOverride? o,
                                 Atmosphere.Record g, Atmosphere.Record e, string unitTip)
        {
            string fp = Identity.FingerprintText;
            int jj = j;

            bool colOn = o?.Colour[j] != null;
            var gc = g.Colour[j] * 256f;
            Row($"Light {j + 1}", $"Light {j + 1}'s colour. The game's is {gc.X:0} {gc.Y:0} {gc.Z:0}." + unitTip,
                button: colOn, edited: colOn);
            var col = e.Colour[j] * LightToWidget;
            RecEdited(area, rec, hash, $"light {j} colour", ImGui.ColorEdit3("##col", ref col, ImGuiColorEditFlags.HDR),
                x => Pack.SetRecordLight(x, jj, "colour", col / LightToWidget));
            if (colOn && ResetButton("col"))
                Pack.SetRecord(area, rec, hash, fp, $"light {j} colour = game", x => Pack.SetRecordLight(x, jj, "colour", null));

            var gd = g.Direction[j];
            var d = e.Direction[j];
            float gLen = gd.Length(), len = d.Length();
            var dirN = len > 1e-5f ? d / len : Vector3.Zero;
            var gN = gLen > 1e-5f ? gd / gLen : Vector3.Zero;
            bool dirSet = o?.Direction[j] != null;
            bool bearingEdited = dirSet && Vector3.Dot(dirN, gN) < 0.99996f;
            bool strengthEdited = dirSet && MathF.Abs(len - gLen) > 0.0005f;

            // Up is -Y. A bearing of 0 faces +Z, 90 faces +X.
            float horiz = MathF.Sqrt(dirN.X * dirN.X + dirN.Z * dirN.Z);
            float yaw = horiz > 1e-4f ? MathF.Atan2(dirN.X, dirN.Z) * 180f / MathF.PI : _lastYaw[j];
            _lastYaw[j] = yaw;
            float pitch = MathF.Asin(Math.Clamp(-dirN.Y, -1f, 1f)) * 180f / MathF.PI;
            var angles = new Vector2(yaw, pitch);
            const string dirTip = "Where the light comes from: a compass bearing (0 faces +Z, 90 faces +X) and an elevation " +
                                  "(90 from straight above, -90 from straight below). A face turned that way takes the light's whole colour. " +
                                  "The arrows on the picture show it.";
            Row("   direction", dirTip, button: bearingEdited, edited: bearingEdited);
            bool dirChanged = ImGui.DragFloat2("##dir", ref angles, 0.5f, 0f, 0f, "%.0f°");
            angles.X = ((angles.X + 180f) % 360f + 360f) % 360f - 180f;
            angles.Y = Math.Clamp(angles.Y, -90f, 90f);
            var newDir = FromAngles(angles.X, angles.Y) * (len > 1e-5f ? len : 1f);
            RecEdited(area, rec, hash, $"light {j} direction", dirChanged,
                x => Pack.SetRecordLight(x, jj, "direction", SameAsGame(newDir, gd) ? null : newDir));
            if (bearingEdited && ResetButton("dir", "The game's direction, keeping this strength"))
            {
                var r = gLen > 1e-5f ? gN * len : gd;
                Pack.SetRecord(area, rec, hash, fp, $"light {j} direction = game",
                    x => Pack.SetRecordLight(x, jj, "direction", SameAsGame(r, gd) ? null : r));
            }

            Row("   strength", "How strongly a face turned towards it takes the light's colour: the direction's length. " +
                               $"The game's is {gLen:0.00}.", button: strengthEdited, edited: strengthEdited);
            float strength = len;
            bool sChanged = ImGui.SliderFloat("##strength", ref strength, 0f, 2f, "%.2f");
            var byStrength = (len > 1e-5f ? dirN : gLen > 1e-5f ? gN : -Vector3.UnitY) * MathF.Max(strength, 0f);
            RecEdited(area, rec, hash, $"light {j} strength", sChanged,
                x => Pack.SetRecordLight(x, jj, "direction", SameAsGame(byStrength, gd) ? null : byStrength));
            if (strengthEdited && ResetButton("strength", "The game's strength, keeping this direction"))
            {
                var r = len > 1e-5f ? dirN * gLen : gd;
                Pack.SetRecord(area, rec, hash, fp, $"light {j} strength = game",
                    x => Pack.SetRecordLight(x, jj, "direction", SameAsGame(r, gd) ? null : r));
            }
        }

        static Vector3 FromAngles(float yawDeg, float pitchDeg)
        {
            float y = yawDeg * MathF.PI / 180f, p = pitchDeg * MathF.PI / 180f;
            return new Vector3(MathF.Cos(p) * MathF.Sin(y), -MathF.Sin(p), MathF.Cos(p) * MathF.Cos(y));
        }

        /// <summary>Within what the 4.12 store keeps.</summary>
        static bool SameAsGame(Vector3 v, Vector3 game) => Vector3.DistanceSquared(v, game) < 1e-7f;

        void DrawRecordFog(int area, int rec, string hash, Pack.RecordOverride? o, Atmosphere.Record g, Atmosphere.Record e)
        {
            string fp = Identity.FingerprintText;
            bool fogOn = o?.Fog != null;
            FogShape shape = ShapeOf(e.Fog), gShape = ShapeOf(g.Fog);
            int gStart = gShape == FogShape.None ? 8000 : StartOf(g.Fog);
            int start = shape == FogShape.None ? gStart : StartOf(e.Fog);
            bool startEdited = fogOn && shape != FogShape.None && gShape != FogShape.None && start != gStart;
            bool shapeEdited = fogOn && shape != gShape;
            int Word(FogShape s, int st) => FogWord(s, st);
            void Put(JsonObject x, int word) => Pack.SetFog(x, word == g.Fog ? null : word);

            string startTip = $"Where this record's fog begins, in tiles (a tile is {TileUnits:0} view units). The game's is {DescribeFog(g.Fog)}.";
            Row("Starts at", startTip, dim: shape == FogShape.None, button: startEdited, edited: startEdited);
            float tiles = start / TileUnits;
            ImGui.BeginDisabled(shape == FogShape.None);
            bool moved = ImGui.SliderFloat("##fogstart", ref tiles, 0f, 15999f / TileUnits, "%.2f tiles");
            int newStart = Math.Clamp((int)MathF.Round(tiles * TileUnits), 0, 15999);
            RecEdited(area, rec, hash, "fog start", moved, x => Put(x, Word(shape, newStart)));
            ImGui.EndDisabled();
            Tip(startTip);
            if (startEdited && ResetButton("fogstart", "The game's start, keeping this shape"))
                Pack.SetRecord(area, rec, hash, fp, "fog start = game", x => Put(x, Word(shape, gStart)));

            Row("Shape", "The game's fog curves: a knee that turns black quickly (most areas), a straight ramp, or none. " +
                         $"The game's is {FogShapeNames[(int)gShape].ToLowerInvariant()}.", button: shapeEdited, edited: shapeEdited);
            int pick = (int)shape;
            if (ImGui.Combo("##fogshape", ref pick, FogShapeNames, FogShapeNames.Length) && pick != (int)shape)
            {
                var s = (FogShape)pick;
                Pack.SetRecord(area, rec, hash, fp, $"fog {s.ToString().ToLowerInvariant()}", x => Put(x, Word(s, start)));
            }
            if (shapeEdited && ResetButton("fogshape", "The game's shape, keeping this start"))
                Pack.SetRecord(area, rec, hash, fp, "fog shape = game", x => Put(x, Word(gShape, start)));
        }

        /// <summary>On the picture while the tab is open: the halves using the record, and
        /// its three lights' directions from a point ahead of the eye.</summary>
        void DrawAtmosOverlay(IMemory m)
        {
            if (!OutputView.Valid || OutputView.GameW <= 0 || _record < 0 || (!_showHalves && !_showDirections)) return;
            var dl = ImGui.GetForegroundDrawList();
            dl.PushClipRect(OutputView.Min, OutputView.Max, true);
            if (_showHalves)
            {
                uint col = ImGui.GetColorU32(new Vector4(0.35f, 1f, 0.6f, 0.3f));
                uint lastRec = 0;
                bool lastHit = false;
                foreach (var t in Faces.Last)
                {
                    if (t.Rec == 0) continue;
                    if (t.Rec != lastRec) { lastRec = t.Rec; lastHit = (m.ReadU8(t.Rec + 4) & 0x3F) == _record; }
                    if (!lastHit) continue;
                    dl.AddTriangleFilled(WindowPixel(new(t.X0, t.Y0)), WindowPixel(new(t.X1, t.Y1)), WindowPixel(new(t.X2, t.Y2)), col);
                }
            }
            if (_showDirections) DrawRecordDirections(dl, m);
            dl.PopClipRect();
        }

        /// <summary>How far ahead of the eye the arrows start, and how long they are.</summary>
        const float DirAhead = 1800f, DirLength = 700f;

        void DrawRecordDirections(ImDrawListPtr dl, IMemory m)
        {
            var e = Atmosphere.Effective(m, _record, Pack.GetRecord(Identity.Area, _record), dark: false);
            var v = Lights.ReadView(m);
            var anchor = v.Unproject(new Vector2(v.Cx, v.Cy), DirAhead);
            uint faint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.25f));
            // The level ring reads the elevation: an arrow above it is a light from above.
            Circle(dl, v, anchor, Vector3.UnitX, Vector3.UnitZ, DirLength, faint, 1f, 48);
            for (int j = 0; j < 3; j++)
            {
                var d = e.Direction[j];
                float len = d.Length();
                if (len < 1e-4f) continue;
                var c = e.Colour[j];
                float peak = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
                // A dark light is still drawn, in grey, so its direction reads.
                var shown = peak > 0.05f ? c / peak : Vector3.One * 0.5f;
                uint col = ImGui.GetColorU32(new Vector4(shown.X, shown.Y, shown.Z, 0.95f));
                var tip = anchor + d / len * DirLength;
                if (!Segment(v, anchor, tip, out var a, out var b)) continue;
                dl.AddLine(a, b, col, 2.5f);
                float r = Math.Clamp(4f + 4f * len, 4f, 12f);
                dl.AddCircleFilled(b, r, col);
                dl.AddCircle(b, r + 1f, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f)), 16, 1.5f);
                dl.AddText(b + new Vector2(r + 4f, -r - 6f), col, (j + 1).ToString());
            }
        }

        /// <summary>As <see cref="Edited"/>, for a record: live while held, one undo entry on release.</summary>
        void RecEdited(int area, int rec, string hash, string label, bool changed, Action<JsonObject> apply)
        {
            string fp = Identity.FingerprintText;
            if (ImGui.IsItemActivated()) { _recBefore = Pack.RecordSnapshot(area, rec); _recHeld = true; }
            if (changed)
            {
                if (ImGui.IsItemActive() && _recHeld) Pack.PreviewRecord(area, rec, hash, fp, apply);
                else Pack.SetRecord(area, rec, hash, fp, label, apply);
            }
            if (ImGui.IsItemDeactivatedAfterEdit() && _recHeld) Pack.CommitRecord(area, rec, fp, label, _recBefore);
            if (ImGui.IsItemDeactivated()) { _recBefore = null; _recHeld = false; }
        }
    }
}
