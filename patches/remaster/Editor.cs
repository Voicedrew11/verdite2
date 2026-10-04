using System.Numerics;
using System.Text.Json.Nodes;
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
/// A header that never scrolls (the switch, save and undo, the area and the selection,
/// the free camera) sits over six tabs, and a click on the picture does what the open
/// tab is for: it picks the faces under it from the frame's own triangles
/// (<see cref="Faces.PickAt"/>), which is what the depth buffer drew there, or the model
/// drawn there; on the Lights tab it first grabs a light's arrow or dot. A Place button
/// arms the next click instead. The docked map (right-click, Shift+M) and the player's
/// tile select a whole half. See "The editor", "Faces, picked from the frame" and
/// "Phase 2, the first slice" in docs/REMASTER.md.
/// </summary>
public static partial class Editor
{
    public static bool Open => Panel.Instance.IsOpen;

    /// <summary>The selected half, or the half of the first selected face.</summary>
    public static TileKey? Selected { get; private set; }

    /// <summary>The selected faces; empty selects the whole of <see cref="Selected"/>.</summary>
    public static readonly List<FaceRef> SelectedFaces = new();

    /// <summary>The selected model; a model and a tile are never selected together.</summary>
    public static ModelKey? SelectedModel { get; private set; }

    public static void SelectModel(ModelKey? k)
    {
        SelectedModel = k;
        Selected = null;
        SelectedFaces.Clear();
        SelectedTexture = null;
    }

    /// <summary>The art the last pick drew there, or the first selected face's: what
    /// a texture assignment names, in every area.</summary>
    public static TexKey? SelectedTexture { get; private set; }

    public static void SelectTexture(TexKey? k) => SelectedTexture = k;

    /// <summary>A texture assignment names the art under any palette.</summary>
    public static bool AnyPalette = true;

    /// <summary>The art a tile face draws, read from its mesh.</summary>
    public static TexKey? TextureOf(RecompOne.Runtime.Memory.IMemory m, FaceRef f)
    {
        var faces = Faces.Mesh(m, f.Mesh);
        if (faces == null || (uint)f.Face >= (uint)faces.Length || faces[f.Face].Verts.Length == 0) return null;
        var mf = faces[f.Face];
        return TextureKeys.Of(mf.Tpage, mf.Clut, (int)(mf.Rect & 0xFF), (int)((mf.Rect >> 8) & 0xFF),
                              (int)((mf.Rect >> 16) & 0xFF), (int)(mf.Rect >> 24), out var k) ? k : null;
    }

    /// <summary>Give the selected art a material in every area, or clear it.</summary>
    public static string? AssignTexture(string? material)
    {
        if (SelectedTexture is not { } k) return "no texture selected (pick a face or a model)";
        Pack.SetTextureMaterial(AnyPalette ? k.AnyClut : k, material);
        return null;
    }

    /// <summary>Assignments go to the mesh wherever the area uses it, not to the half.</summary>
    public static bool MeshScope;

    public static void SetOpen(bool open) => Panel.Instance.IsOpen = open;

    public enum Tab { Material, Lights, Atmos, Level, Props, Pack }

    /// <summary>The open tab, as a word.</summary>
    public static string ActiveTab => Panel.Instance.Current.ToString().ToLowerInvariant();

    /// <summary>Open a tab by name; false for no such tab.</summary>
    public static bool ShowTab(string name)
    {
        if (!Enum.TryParse<Tab>(name, true, out var t)) return false;
        Panel.Instance.Show(t);
        return true;
    }

    /// <summary>Whether the panel was docked, and where it lay, when last drawn.</summary>
    public static bool Docked { get; private set; }
    public static Vector2 PanelMin { get; private set; }
    public static Vector2 PanelMax { get; private set; }

    /// <summary>The selected light, by name, in the loaded area.</summary>
    public static string? SelectedLight { get; private set; }

    public static void SelectLight(string? name) => SelectedLight = name;

    /// <summary>The selected prop, by name, in the loaded area.</summary>
    public static string? SelectedProp { get; private set; }

    public static void SelectProp(string? name) => SelectedProp = name;

    /// <summary>How far short of a picked surface a light is placed, towards the eye.</summary>
    const float PlaceBack = 192f;

    /// <summary>Where a light goes for a click at a game pixel: the nearest surface the
    /// last frame drew there, pulled back towards the eye so it is not inside it.</summary>
    public static Vector3? PlaceAt(RecompOne.Runtime.Memory.IMemory m, Vector2 px)
    {
        if (Faces.Nearest(px, out float z) < 0 || !float.IsFinite(z)) return null;
        var v = Lights.ReadView(m);
        var p = new Vector3((px.X - v.Cx) * z / v.H, (px.Y - v.Cy) * z / v.H, z);
        float len = p.Length();
        p *= MathF.Max(len - PlaceBack, len * 0.5f) / len;
        return v.ToWorld(p);
    }

    /// <summary>The nearest surface the last frame drew at a game pixel, where a prop
    /// is placed.</summary>
    public static Vector3? SurfaceAt(RecompOne.Runtime.Memory.IMemory m, Vector2 px)
    {
        if (Faces.Nearest(px, out float z) < 0 || !float.IsFinite(z)) return null;
        var v = Lights.ReadView(m);
        return v.ToWorld(new Vector3((px.X - v.Cx) * z / v.H, (px.Y - v.Cy) * z / v.H, z));
    }

    /// <summary>Where a prop goes for a click at a game pixel: stood on the floor of the
    /// tile half under the surface there (the picked half's, or the player's for a
    /// model), and pulled towards the eye when the surface is a wall, so it stands in
    /// front of it rather than inside it.</summary>
    public static Vector3? PropPlaceAt(RecompOne.Runtime.Memory.IMemory m, Vector2 px)
    {
        int n = Faces.Nearest(px, out _);
        if (n < 0 || SurfaceAt(m, px) is not { } p) return null;
        int half = Identity.PlayerTile(m)?.Half ?? TileKey.Lower;
        if (Faces.Last[n].Rec != 0 && Identity.FromRecord(Faces.Last[n].Rec, out _, out _, out int h)) half = h;
        float Floor(Vector3 at)
        {
            int tx = (int)at.X / Identity.TileUnits, tz = (int)at.Z / Identity.TileUnits;
            if ((uint)tx >= Identity.Span || (uint)tz >= Identity.Span) return at.Y;
            return -(m.ReadU8(Identity.HalfRecord(tx, tz, half) + (uint)TileField.Height.Offset) << 7);
        }
        if (MathF.Abs(p.Y - Floor(p)) > PropWallGap)
        {
            var eye = PlayerLightPosition(m);
            var flat = new Vector3(eye.X - p.X, 0f, eye.Z - p.Z);
            if (flat.LengthSquared() > 1f) p += Vector3.Normalize(flat) * MathF.Min(PropWallGap * 2f, flat.Length() * 0.5f);
        }
        p.Y = Floor(p);
        return p;
    }

    /// <summary>A surface this far off the floor is a wall; a prop is stood twice this far out from it.</summary>
    const float PropWallGap = 256f;

    /// <summary>Where the player stands, which is where "Add here" puts a prop: on the
    /// floor of their own tile half, since the position the game keeps is the eye's.</summary>
    public static Vector3 PlayerFeet(RecompOne.Runtime.Memory.IMemory m)
    {
        var at = new Vector3((int)m.ReadU32(0x801994ECu), (int)m.ReadU32(0x801994F0u), (int)m.ReadU32(0x801994F4u));
        if (Identity.PlayerTile(m) is { } k)
            at.Y = -(m.ReadU8(Identity.HalfRecord(k.X, k.Z, k.Half) + (uint)TileField.Height.Offset) << 7);
        return at;
    }

    /// <summary>The eye, which is where "Add at eye" puts a light.</summary>
    public static Vector3 PlayerLightPosition(RecompOne.Runtime.Memory.IMemory m) => Lights.ReadView(m).ToWorld(Vector3.Zero);

    /// <summary>A whole half.</summary>
    public static void Select(TileKey? key)
    {
        Selected = key;
        SelectedModel = null;
        SelectedFaces.Clear();
        SelectedTexture = null;
    }

    /// <summary>Faces; with <paramref name="toggle"/>, each is added or, if already
    /// selected, removed.</summary>
    public static void SelectFaces(IReadOnlyList<FaceRef> faces, bool toggle)
    {
        if (!toggle) SelectedFaces.Clear();
        SelectedModel = null;
        foreach (var f in faces)
            if (!toggle || !SelectedFaces.Remove(f)) SelectedFaces.Add(f);
        Selected = SelectedFaces.Count > 0 ? SelectedFaces[0].Tile : toggle ? Selected : null;
        SelectedTexture = SelectedFaces.Count > 0 && RecompOne.Runtime.Runtime.Mem is { } m ? TextureOf(m, SelectedFaces[0]) : null;
    }

    /// <summary>The mesh a half draws now, or -1 when it draws none.</summary>
    public static int MeshOf(RecompOne.Runtime.Memory.IMemory m, TileKey k)
    {
        int model = m.ReadU8(Identity.HalfRecord(k.X, k.Z, k.Half));
        return model < 240 ? model : -1;
    }

    /// <summary>The shell's grow verb keeps Mesh; the panel has no button for it, since
    /// Whole half assigns the same faces.</summary>
    public enum Grow { Connected, Texture, Mesh }

    /// <summary>Widen the face selection within each selected half's mesh.</summary>
    public static void GrowSelection(RecompOne.Runtime.Memory.IMemory m, Grow how)
    {
        var seeds = SelectedFaces.ToList();
        foreach (var f in seeds)
        {
            IEnumerable<int> more = how switch
            {
                Grow.Connected => Faces.Connected(m, f.Mesh, f.Face, sameTexture: true),
                Grow.Texture => Faces.SameTexture(m, f.Mesh, f.Face),
                _ => Enumerable.Range(0, Faces.Mesh(m, f.Mesh)?.Length ?? 0),
            };
            foreach (int i in more)
            {
                var g = f with { Face = i };
                if (!SelectedFaces.Contains(g)) SelectedFaces.Add(g);
            }
        }
    }

    /// <summary>Whether the selection's area can be edited now, and why not.</summary>
    public static string? Blocked(TileKey k) => Blocked(k.Area);

    public static string? Blocked(int area)
        => !Identity.Settled ? "the area is settling"
         : area != Identity.Area ? "the selection is in another area"
         : Surfaces.Refused;

    /// <summary>What the selection is given at the current scope, or "(mixed)".</summary>
    public static string? SelectionMaterial(RecompOne.Runtime.Memory.IMemory m)
    {
        if (SelectedModel is { } mk) return Pack.ModelMaterial(mk);
        if (Selected is not { } k) return null;
        if (SelectedFaces.Count == 0)
            return MeshScope ? MeshOf(m, k) is int mesh and >= 0 ? Pack.MeshMaterial(k.Area, mesh) : null
                             : Pack.TileMaterial(k);
        var first = Pack.FaceMaterial(SelectedFaces[0], MeshScope);
        foreach (var f in SelectedFaces)
            if (Pack.FaceMaterial(f, MeshScope) != first) return "(mixed)";
        return first;
    }

    /// <summary>The material the selection draws with and the rule it comes from, in
    /// <see cref="Surfaces"/>' order: a face on the half, the half, a face on the mesh,
    /// the mesh, the art. Material "(mixed)" when the faces differ; null for none.</summary>
    public static (string? Material, string? From) Effective(RecompOne.Runtime.Memory.IMemory m)
    {
        (string?, string?) ByTexture(TexKey? k)
            => k is { } t && (Pack.TextureMaterial(t) ?? Pack.TextureMaterial(t.AnyClut)) is { } tm ? (tm, "its texture") : (null, null);

        if (SelectedModel is { } mk)
            return Pack.ModelMaterial(mk) is { } mm ? (mm, "the model") : ByTexture(SelectedTexture);
        if (Selected is not { } k) return (null, null);
        if (SelectedFaces.Count == 0)
        {
            if (Pack.TileMaterial(k) is { } hm) return (hm, "the half");
            return MeshOf(m, k) is int mesh and >= 0 && Pack.MeshMaterial(k.Area, mesh) is { } am ? (am, "its mesh") : (null, null);
        }
        (string?, string?) Face(FaceRef f)
        {
            if (Pack.FaceMaterial(f, false) is { } a) return (a, "a face rule on the half");
            if (Pack.TileMaterial(f.Tile) is { } b) return (b, "the half");
            if (Pack.FaceMaterial(f, true) is { } c) return (c, "a face rule on the mesh");
            if (Pack.MeshMaterial(f.Tile.Area, f.Mesh) is { } d) return (d, "the mesh");
            return ByTexture(TextureOf(m, f));
        }
        var first = Face(SelectedFaces[0]);
        foreach (var f in SelectedFaces)
        {
            var e = Face(f);
            if (e.Item1 != first.Item1) return ("(mixed)", null);
            if (e.Item2 != first.Item2) first.Item2 = "several rules";
        }
        return first;
    }

    /// <summary>Give the selection a material at the current scope, or clear it.</summary>
    public static string? Assign(RecompOne.Runtime.Memory.IMemory m, string? material)
    {
        if (SelectedModel is { } mk)
        {
            if (Blocked(mk.Area) is { } w) return w;
            Pack.SetModelMaterial(mk, material, Identity.FingerprintText);
            return null;
        }
        if (Selected is not { } k) return "nothing selected";
        if (Blocked(k) is { } why) return why;
        string fp = Identity.FingerprintText;
        if (SelectedFaces.Count > 0)
        {
            foreach (var f in SelectedFaces)
                if (Faces.MeshHash(m, f.Mesh) == null) return $"mesh {f.Mesh} cannot be read";
            Pack.SetFaces(SelectedFaces, material, MeshScope, mesh => Faces.MeshHash(m, mesh), fp);
            return null;
        }
        if (!MeshScope) { Pack.SetTile(k, material, fp); return null; }
        int model = MeshOf(m, k);
        if (model < 0 || Faces.MeshHash(m, model) is not { } hash) return "the half draws no mesh";
        Pack.SetMeshMaterial(k.Area, model, hash, material, fp);
        return null;
    }

    public static void Install()
    {
        FramePacing.PauseWhen(() => Open && Identity.Area >= 0);

        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!e.Pressed || PopupManager.AnyOpen || HotkeyGate.Typing || EditorCamera.Looking) return;
            bool shift = HostWindow.IsKeyDown(Key.ShiftLeft) || HostWindow.IsKeyDown(Key.ShiftRight);
            bool ctrl = HostWindow.IsKeyDown(Key.ControlLeft) || HostWindow.IsKeyDown(Key.ControlRight);
            if (e.Key == (int)Key.E && shift && !e.Repeat)
            {
                bool closing = Panel.Instance.IsOpen;
                Panel.Instance.IsOpen = !closing;
                // Closed from the keyboard, the player is going back to the game: give it the pointer.
                if (closing && Mouse.Enabled && !Mouse.Captured) Mouse.SetCaptured(true);
                return;
            }
            // Not while a control or a light is held: its edit is still a preview.
            if (!Open || !ctrl || ImGui.IsAnyItemActive() || ImGui.IsMouseDown(ImGuiMouseButton.Left)) return;
            if (e.Key == (int)Key.Z) { if (shift) Pack.Redo(); else Pack.Undo(); }
            else if (e.Key == (int)Key.Y) Pack.Redo();
            else if (e.Key == (int)Key.S && !e.Repeat) Pack.Save();
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

    sealed partial class Panel : IPanel
    {
        public static readonly Panel Instance = new();
        Panel() { }

        public string Name => "kf2remaster";
        public string TitleKey => "kf2.remaster.editor";

        /// <summary>Opening gives the pointer back from mouse look, and closing
        /// captures it again if opening took it, as the in-game menu does.</summary>
        public bool IsOpen
        {
            get => _open;
            set
            {
                if (value == _open) return;
                _open = value;
                if (value)
                {
                    _tookCapture = Mouse.Captured;
                    if (_tookCapture) Mouse.SetCaptured(false);
                }
                else if (_tookCapture)
                {
                    _tookCapture = false;
                    if (Mouse.Enabled) Mouse.SetCaptured(true);
                }
            }
        }

        bool _open, _tookCapture;

        string? _pickWhy;
        bool _highlight = true;

        int _lastFrame = -2;

        public Tab Current { get; private set; }
        Tab? _want;

        public void Show(Tab t) { _want = t; Current = t; }

        public void Draw()
        {
            bool open = IsOpen;
            // Drawn only while open, so a gap in the frames is an opening.
            int frame = ImGui.GetFrameCount();
            bool opened = frame != _lastFrame + 1;
            _lastFrame = frame;
            ImGui.SetNextWindowSize(new Vector2(Width, 560), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin(this.Title(), ref open))
            {
                IsOpen = open;
                ImGui.End();
                return;
            }
            IsOpen = open;
            bool docked = ImGui.IsWindowDocked();
            Docked = docked;
            PanelMin = ImGui.GetWindowPos();
            PanelMax = PanelMin + ImGui.GetWindowSize();
            // A slider's value is centred over its grab; a see-through grab keeps it readable.
            var colours = ImGui.GetStyle().Colors;
            var grab = colours[(int)ImGuiCol.SliderGrab];
            var grabActive = colours[(int)ImGuiCol.SliderGrabActive];
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, grab with { W = grab.W * 0.45f });
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, grabActive with { W = grabActive.W * 0.6f });

            DrawHeader();
            ImGui.Spacing();
            var was = Current;
            if (ImGui.BeginTabBar("##tabs", ImGuiTabBarFlags.FittingPolicyResizeDown))
            {
                TabItem(Tab.Material, Icon.Material, "Material", DrawMaterialTab);
                TabItem(Tab.Lights, Icon.Light, "Lights", DrawLightsTab);
                TabItem(Tab.Atmos, Icon.Atmos, "Atmos", DrawAtmosphereTab);
                TabItem(Tab.Level, Icon.Level, "Level", DrawLevelTab);
                TabItem(Tab.Props, Icon.Prop, "Props", DrawPropsTab);
                TabItem(Tab.Pack, Icon.Pack, "Pack", DrawPackTab);
                ImGui.EndTabBar();
            }
            // A Place armed on one tab is not carried to another.
            if (Current != was) _placing = _placingProp = false;
            bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows);
            ImGui.PopStyleColor(2);
            ImGui.End();
            if (opened && !docked) DockRight(this.Title());

            var m = Runtime.Mem;
            if (m == null || Identity.Area < 0) return;
            EditorCamera.Input(m, !hovered && OutputView.Hovered);
            var mouse = ImGui.GetIO().MousePos;
            bool lights = Current == Tab.Lights;
            if ((_placing || _placingProp) && ImGui.IsKeyPressed(ImGuiKey.Escape)) _placing = _placingProp = false;
            if (!hovered && OutputView.Hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && GamePixel(mouse, out var px))
            {
                if (PlacePropClick(m, px)) { }
                else if (_placing)
                {
                    _placing = false;
                    if (PlaceAt(m, px) is { } at) AddLight(at);
                    else _pickWhy = "no surface under that pixel for a light";
                }
                else if (lights && AxisUnder(m, mouse) is int axis)
                    BeginAxisDrag(m, SelectedLight!, axis, px);
                else if (lights && LightUnder(m, mouse) is { } name)
                {
                    SelectLight(name);
                    BeginDrag(m, name);
                }
                else
                {
                    bool toggle = ImGui.GetIO().KeyShift;
                    var hit = Faces.PickAt(px, out var model, out _pickWhy, out var tex);
                    if (hit != null) SelectFaces(hit, toggle);
                    else if (model != null) SelectModel(model);
                    if (model != null && Faces.PickedProp is { } prop) SelectProp(prop);
                    if (hit != null || model != null) SelectTexture(tex);
                }
            }
            Drag(m, mouse);
            if (_highlight && Selected is { } k && k.Area == Identity.Area) Highlight(k);
            if (_highlight && SelectedModel is { } mk && mk.Area == Identity.Area) HighlightModel(mk);
            if (lights) DrawGizmos(m, mouse);
            if (Current == Tab.Atmos) DrawAtmosOverlay(m);
            if ((_placing || _placingProp) && OutputView.Hovered && !hovered)
                ImGui.GetForegroundDrawList().AddText(mouse + new Vector2(18f, 14f), ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 1f)),
                    _placing ? "click to place a light (Esc cancels)" : "click to place a prop (Esc cancels)");
        }

        /// <summary>One tab, its body in a scrolling child of its own so the header and
        /// the tab bar stay put (the Input pane's shape).</summary>
        void TabItem(Tab tab, string icon, string name, Action body)
        {
            // SetSelected takes effect on a later frame, so it is asked for until the tab opens.
            var flags = _want == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            bool open = BeginTabItem(L(icon, name) + "###" + name, flags);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(name);
            if (!open) return;
            if (_want == tab) _want = null;
            if (_want == null) Current = tab;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            bool visible = ImGui.BeginChild("##body" + name, Vector2.Zero, ImGuiChildFlags.None);
            ImGui.PopStyleVar();
            if (visible) body();
            ImGui.EndChild();
            ImGui.EndTabItem();
        }

        /// <summary>A tab with no close button: ImGui.NET's wrapper takes flags only with a <c>ref bool</c>, which draws one.</summary>
        static unsafe bool BeginTabItem(string label, ImGuiTabItemFlags flags)
        {
            int n = System.Text.Encoding.UTF8.GetByteCount(label);
            byte* buf = stackalloc byte[n + 1];
            fixed (char* c = label) System.Text.Encoding.UTF8.GetBytes(c, label.Length, buf, n);
            buf[n] = 0;
            return ImGuiNative.igBeginTabItem(buf, null, flags) != 0;
        }

        /// <summary>The panel's width at the interface's base font size.</summary>
        const float Width = 440f;

        /// <summary>
        /// Dock the panel at the right edge of the picture: the Output panel's node is
        /// split and the panel takes the right side. Only when it opens undocked; a
        /// closed docked panel keeps its node, hidden, and ImGui reopens it there, so
        /// the split is made once and wherever the user moves it after, it stays.
        /// </summary>
        static void DockRight(string title)
        {
            uint node = OutputView.DockId;
            if (node == 0 || igDockBuilderGetNode(node) == IntPtr.Zero) return;
            float avail = MathF.Max(OutputView.Valid ? ImGui.GetMainViewport().WorkSize.X : 0f, 1f);
            float ratio = Math.Clamp(Width * ImGui.GetFontSize() / 16f / avail, 0.2f, 0.45f);
            igDockBuilderSplitNode(node, ImGuiDir.Right, ratio, out uint right, out _);
            igDockBuilderDockWindow(title, right);
            igDockBuilderFinish(node);
        }

        [System.Runtime.InteropServices.DllImport("cimgui")]
        static extern IntPtr igDockBuilderGetNode(uint nodeId);

        [System.Runtime.InteropServices.DllImport("cimgui")]
        static extern uint igDockBuilderSplitNode(uint nodeId, ImGuiDir dir, float ratioAtDir, out uint idAtDir, out uint idOpposite);

        [System.Runtime.InteropServices.DllImport("cimgui")]
        static extern void igDockBuilderDockWindow(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

        [System.Runtime.InteropServices.DllImport("cimgui")]
        static extern void igDockBuilderFinish(uint nodeId);


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

        /// <summary>The selected model's triangles from the last frame, tinted over the picture.</summary>
        static void HighlightModel(ModelKey k)
        {
            if (!OutputView.Valid || OutputView.GameW <= 0) return;
            var dl = ImGui.GetForegroundDrawList();
            dl.PushClipRect(OutputView.Min, OutputView.Max, true);
            uint col = ImGui.GetColorU32(new Vector4(0.3f, 0.85f, 1f, 0.35f));
            foreach (var t in Faces.Last)
            {
                if (t.Rec != 0 || t.Model != k.Model || t.Kind != k.Kind) continue;
                dl.AddTriangleFilled(WindowPixel(new(t.X0, t.Y0)), WindowPixel(new(t.X1, t.Y1)), WindowPixel(new(t.X2, t.Y2)), col);
            }
            dl.PopClipRect();
        }

        /// <summary>The selection's triangles from the last frame, tinted over the picture.</summary>
        static void Highlight(TileKey k)
        {
            if (!OutputView.Valid || OutputView.GameW <= 0) return;
            uint whole = SelectedFaces.Count == 0 ? Identity.HalfRecord(k.X, k.Z, k.Half) : 0u;
            var faces = new HashSet<(uint, int)>();
            foreach (var f in SelectedFaces) faces.Add((Identity.HalfRecord(f.Tile.X, f.Tile.Z, f.Tile.Half), f.Face));
            var dl = ImGui.GetForegroundDrawList();
            dl.PushClipRect(OutputView.Min, OutputView.Max, true);
            uint col = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 0.35f));
            foreach (var t in Faces.Last)
            {
                if (t.Rec == 0 || (whole != 0 ? t.Rec != whole : !faces.Contains((t.Rec, t.Face)))) continue;
                dl.AddTriangleFilled(WindowPixel(new(t.X0, t.Y0)), WindowPixel(new(t.X1, t.Y1)), WindowPixel(new(t.X2, t.Y2)), col);
            }
            dl.PopClipRect();
        }
    }
}
