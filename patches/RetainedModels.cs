using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// The frame's models in the retained scene: every face the lit assembler is handed
/// inside the object walk, taken back to world space before the assembler culls a
/// single one (a reflection sees the side the camera does not), and handed to
/// <see cref="RetainedScene.AddDynamic"/>.
///
/// The model's corners are read from the vertex base the transform just read them
/// from, so an animated pose is the pose drawn. The GTE still holds the model's
/// rotation and translation into view space; the frame's camera takes that back to
/// the world. The colour is the same `NormalColorCol` the assembler's
/// `NormalColorDpq` makes, without the depth cue, which the vertex shader applies
/// from the reflection's own camera with the DQA, DQB and curve the model was
/// fogged with.
///
/// Billboards and effects are captured too, since the walk hands both to the
/// blended lit assembler; a billboard is its card as the real camera faces it. Not
/// captured: anything drawn outside the object walk (the arm, the HUD).
///
/// Also captured for the authored lights' shadows (runtime <c>0077</c>), with
/// reflections off; then nothing is lit, since a shadow wants only the corners. A
/// door's blended model is marked solid (<see cref="RetainedScene.FlagSolid"/>), so it
/// casts as the wall it stands for, and an effect or a billboard is marked to cast
/// nothing: a billboard's card faces the player, not the light.
/// </summary>
static class RetainedModels
{
    const uint VertexBase = 0x8018EAA0;
    const uint VertexCache = 0x8018EB94;
    const uint LightColour = 0x8006E604;
    const uint FogMode = 0x80192EA8;

    static RetainedScene.Vertex[] _tris = new RetainedScene.Vertex[1024];

    /// <summary>Whether the lit assembler should hand its faces over.</summary>
    public static bool Capturing => (RetainedMap.ReflectionsReady || RetainedScene.ShadowModelsWanted)
                                    && ModelWalk.InWalk && !PolyAssembler.Verifying;

    public static long Models, Faces, Props;

    /// <summary>Models captured by the table they came from, and faces of a kind no
    /// capture reads, by command byte; never reset.</summary>
    public static readonly long[] ByKind = new long[4];
    public static readonly Dictionary<uint, long> Unread = new();

    /// <summary>One model: its header, normals and faces as the lit assembler found
    /// them; <paramref name="abr"/> the blend mode a blended submit forces, or
    /// <see cref="uint.MaxValue"/> for an opaque one.</summary>
    public static void Capture(PSMemory mem, uint header, uint normals, uint face, uint count, uint abr)
    {
        var frame = RetainedScene.Find(RetainedScene.Serial);
        if (frame == null || count > 4096) return;
        var v = frame.View;
        var xf = Transform.Read(v);
        float dqa = (short)Gte.ReadControl(27), dqb = (int)Gte.ReadControl(28);
        int mode = (int)mem.ReadU32(FogMode);
        float curve = mode >= 32000 ? 0f : (mode & 0x8000) != 0 ? 1f : 2f;
        uint rgbc = mem.ReadU32(LightColour);
        uint verts = mem.ReadU32(VertexBase);
        byte mat = PolyAssembler.TileMaterial;
        bool lit = RetainedMap.ReflectionsReady;
        uint solid = ModelWalk.SubmitKind is ModelKind.Effect or ModelKind.Sprite ? RetainedScene.FlagNoShadow
                   : abr != uint.MaxValue && ModelWalk.SubmitKind == ModelKind.Object
                     && ModelWalk.SolidKind(ModelWalk.ObjectKind(mem, ModelWalk.SubmitRecord)) ? RetainedScene.FlagSolid : 0u;

        int n = 0;
        Span<uint> idx = stackalloc uint[4], nrm = stackalloc uint[4], uv = stackalloc uint[4];
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4], wz = stackalloc float[4];
        Span<uint> col = stackalloc uint[4];
        for (uint i = 0; i < count; i++)
        {
            uint word = mem.ReadU32(face);
            uint f = face + 4u;
            uint cmd = word >> 24;
            int corners;
            bool gouraud;
            switch (cmd & 0xFDu)
            {
                case 0x24u: corners = 3; gouraud = false; idx[0] = 0x0E; idx[1] = 0x10; idx[2] = 0x12; nrm[0] = 0x0C; break;
                case 0x2Cu: corners = 4; gouraud = false; idx[0] = 0x12; idx[1] = 0x14; idx[2] = 0x16; idx[3] = 0x18; nrm[0] = 0x10; break;
                case 0x34u: corners = 3; gouraud = true; idx[0] = 0x0E; idx[1] = 0x12; idx[2] = 0x16; nrm[0] = 0x0C; nrm[1] = 0x10; nrm[2] = 0x14; break;
                case 0x3Cu: corners = 4; gouraud = true; idx[0] = 0x12; idx[1] = 0x16; idx[2] = 0x1A; idx[3] = 0x1E; nrm[0] = 0x10; nrm[1] = 0x14; nrm[2] = 0x18; nrm[3] = 0x1C; break;
                default: corners = 0; gouraud = false; break;
            }
            face = f + ((word >> 6) & 0x3FCu);
            if (corners == 0) { Unread[cmd] = Unread.GetValueOrDefault(cmd) + 1; continue; }

            for (int k = 0; k < corners; k++)
            {
                uint p = verts + mem.ReadU16(f + idx[k]);
                xf.World(v, (short)mem.ReadU16(p), (short)mem.ReadU16(p + 2u), (short)mem.ReadU16(p + 4u),
                         out wx[k], out wy[k], out wz[k]);
                if (RetainedMap.Checking) RetainedMap.CheckCorner(v, wx[k], wy[k], wz[k], mem.ReadU32(VertexCache + mem.ReadU16(f + idx[k])));
                if (!lit) col[k] = 0u;
                else if (gouraud || k == 0) col[k] = Light(mem, normals + mem.ReadU16(f + nrm[gouraud ? k : 0]), rgbc);
                else col[k] = col[0];
            }
            uv[0] = mem.ReadU16(f);
            uv[1] = mem.ReadU16(f + 4u);
            uv[2] = mem.ReadU16(f + 8u);
            uv[3] = corners == 4 ? mem.ReadU16(f + 0xCu) : 0u;
            uint clut = mem.ReadU16(f + 2u), tpage = mem.ReadU16(f + 6u);
            bool semi = abr != uint.MaxValue || (cmd & 2u) != 0;
            if (abr != uint.MaxValue) tpage = (tpage & 0xFF9Fu) | abr;
            tpage &= 0x1FFu;

            int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
            for (int k = 0; k < corners; k++)
            {
                int u = (int)(uv[k] & 0xFF), vv = (int)(uv[k] >> 8);
                u0 = Math.Min(u0, u); v0 = Math.Min(v0, vv); u1 = Math.Max(u1, u); v1 = Math.Max(v1, vv);
            }
            var t = new RetainedScene.Vertex
            {
                Clut = clut & 0x7FFF, Texpage = tpage, Dqa = dqa, Dqb = dqb, Curve = curve,
                Rect = (uint)u0 | (uint)v0 << 8 | (uint)u1 << 16 | (uint)v1 << 24,
                Flags = RetainedScene.FlagRect | mat | solid | (semi ? RetainedScene.FlagSemi | ((tpage >> 5) & 3u) << 8 : 0u),
                Rgbc = lit ? rgbc & 0xFFFFFFu : 0u,
            };
            if (n + 6 > _tris.Length) Array.Resize(ref _tris, _tris.Length * 2);
            Put(ref n, t, wx, wy, wz, uv, col, 0); Put(ref n, t, wx, wy, wz, uv, col, 1); Put(ref n, t, wx, wy, wz, uv, col, 2);
            if (corners == 4) { Put(ref n, t, wx, wy, wz, uv, col, 1); Put(ref n, t, wx, wy, wz, uv, col, 3); Put(ref n, t, wx, wy, wz, uv, col, 2); }
            Faces++;
        }
        RetainedScene.AddDynamic(_tris.AsSpan(0, n));
        Models++;
        ByKind[(int)ModelWalk.SubmitKind]++;
        if (Remaster.Props.NameOf(ModelWalk.SubmitRecord) != null) Props++;
    }

    // ---- 0085: the main view --------------------------------------------------------

    /// <summary>Whether the lit assembler's opaque models are taken off the packets and
    /// drawn by the GPU world renderer: the object walk's own submits only, with
    /// per-pixel lighting on and the far colour black, as the packets' records need.</summary>
    public static bool MainCapturing => GpuWorld.ModelsActive && (ModelWalk.InWalk || ModelWalk.InArm && Instanced) && !PolyAssembler.Verifying
                                        && Gte.ReadControl(21) == 0 && Gte.ReadControl(22) == 0 && Gte.ReadControl(23) == 0;

    /// <summary>The same for the planar walk's replay of the walk's submits, into the
    /// frame's mirror: faces taken by the assemblers' tests on the mirrored camera's
    /// screen corners.</summary>
    public static bool MirrorCapturing => GpuWorld.MirrorModelsActive && PlanarWalk.Replaying && !PolyAssembler.Verifying
                                          && Gte.ReadControl(21) == 0 && Gte.ReadControl(22) == 0 && Gte.ReadControl(23) == 0;

    /// <summary>Models taken into the mirror; never reset.</summary>
    public static long MirrorModels;

    /// <summary>Models and faces taken, faces the facing test dropped and faces the
    /// table's range dropped; never reset.</summary>
    public static long MainModels, MainFaces, MainCulled, MainOutOfTable;

    static RetainedScene.Vertex[] _main = new RetainedScene.Vertex[1024], _mainClip = new RetainedScene.Vertex[256];
    // Per model: each vertex's world position and each normal's light dots, by offset / 8.
    static readonly float[] _vx = new float[8192 * 3], _nd = new float[8192 * 3];
    static readonly int[] _vAt = new int[8192], _nAt = new int[8192];
    static int _stamp;
    static readonly float[] _light = new float[12];

    /// <summary>
    /// A model an assembler is about to assemble, its opaque faces taken to the frame's
    /// main view instead (<see cref="RetainedScene.AddMainModel"/>); the assembler then
    /// builds only its blended faces. A face is taken only if the game would have drawn
    /// it, by the assembler's own tests on the screen corners its transform just cached.
    /// The lit assembler (`func_8002F214`): the facing, fractional corners and all, and
    /// the mean depth inside the table; each corner as its packet's record has it for
    /// per-pixel lighting, a flat face's lit colour before its saturation and a gouraud
    /// corner's three light dots with the BK and LCM they are lit by. The clipped map
    /// assembler (<paramref name="tile"/>, `func_80030540`, which draws an object near
    /// the camera): the facing of a face that fits the screen, and a face it hands the
    /// clipper left to the GPU's own cull; the face's NormalColorCol colour, fogged on
    /// its two curves.
    /// </summary>
    public static void CaptureMain(PSMemory mem, uint normals, uint face, uint count, uint bias, bool tile = false, bool mirror = false)
    {
        var frame = RetainedScene.Find(RetainedScene.Serial);
        if (frame == null || count > 4096 || mirror && !frame.MirrorOn) return;
        var v = mirror ? frame.MirrorView : frame.View;
        var xf = Transform.Read(v);
        float dqa = (short)Gte.ReadControl(27), dqb = (int)Gte.ReadControl(28);
        int mode = (int)mem.ReadU32(FogMode);
        float curve = mode >= 32000 ? 0f : tile ? 2f : (mode & 0x8000) != 0 ? 1f : 2f;
        uint rgbc = mem.ReadU32(LightColour) & 0xFFFFFFu;
        uint verts = mem.ReadU32(VertexBase);
        byte mat = PolyAssembler.TileMaterial;
        for (int i = 0; i < 3; i++) _light[i] = (int)Gte.ReadControl(13 + i);
        for (int i = 0; i < 9; i++)
            _light[3 + i] = i == 8 ? (short)Gte.ReadControl(20) : (short)(Gte.ReadControl(16 + i / 2) >> ((i & 1) * 16));
        if (++_stamp == int.MaxValue) { _stamp = 1; Array.Clear(_vAt); Array.Clear(_nAt); }

        int n = 0, nc = 0;
        Span<uint> idx = stackalloc uint[4], nrm = stackalloc uint[4], uv = stackalloc uint[4];
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4], wz = stackalloc float[4];
        Span<float> cr = stackalloc float[4], cg = stackalloc float[4], cb = stackalloc float[4];
        for (uint i = 0; i < count; i++)
        {
            uint word = mem.ReadU32(face);
            uint f = face + 4u;
            uint cmd = word >> 24;
            face = f + ((word >> 6) & 0x3FCu);
            if ((cmd & 2u) != 0) continue;
            int corners;
            bool gouraud;
            switch (cmd & 0xFDu)
            {
                case 0x24u: corners = 3; gouraud = false; idx[0] = 0x0E; idx[1] = 0x10; idx[2] = 0x12; nrm[0] = 0x0C; break;
                case 0x2Cu: corners = 4; gouraud = false; idx[0] = 0x12; idx[1] = 0x14; idx[2] = 0x16; idx[3] = 0x18; nrm[0] = 0x10; break;
                case 0x34u when !tile: corners = 3; gouraud = true; idx[0] = 0x0E; idx[1] = 0x12; idx[2] = 0x16; nrm[0] = 0x0C; nrm[1] = 0x10; nrm[2] = 0x14; break;
                case 0x3Cu when !tile: corners = 4; gouraud = true; idx[0] = 0x12; idx[1] = 0x16; idx[2] = 0x1A; idx[3] = 0x1E; nrm[0] = 0x10; nrm[1] = 0x14; nrm[2] = 0x18; nrm[3] = 0x1C; break;
                default: continue;
            }

            // The assembler's own tests, on the vertex cache its transform filled.
            uint p0 = VertexCache + mem.ReadU16(f + idx[0]), p1 = VertexCache + mem.ReadU16(f + idx[1]);
            uint p2 = VertexCache + mem.ReadU16(f + idx[2]);
            uint p3 = corners == 4 ? VertexCache + mem.ReadU16(f + idx[3]) : 0u;
            bool clip = false;
            if (tile)
            {
                clip = PolyAssembler.TileFaceClips(mem, corners, p0, p1, p2, p3);
                if (!clip && !PolyAssembler.TileFaceKept(mem, corners, p0, p1, p2, p3)) { if (!mirror) MainCulled++; continue; }
            }
            else
            {
                if (!PolyAssembler.FaceKept(mem, p0, p1, p2)) { if (!mirror) MainCulled++; continue; }
                int z = (short)mem.ReadU16(p0 + 4u) + (short)mem.ReadU16(p1 + 4u) + (short)mem.ReadU16(p2 + 4u);
                z = corners == 4 ? (z + (short)mem.ReadU16(p3 + 4u)) >> 2 : z / 3;
                if (z <= 0 || (uint)z + bias >= 0x2000u && !RenderDistance.Any) { if (!mirror) MainOutOfTable++; continue; }
                // A corner nearer than H/2, where the GTE's divide saturates: its packet
                // was placed where the corner's projection is not.
                int near = (int)(GteDepth.ProjH / 8f);
                if ((short)mem.ReadU16(p0 + 4u) < near || (short)mem.ReadU16(p1 + 4u) < near || (short)mem.ReadU16(p2 + 4u) < near
                    || corners == 4 && (short)mem.ReadU16(p3 + 4u) < near)
                    if (!mirror) MainSaturated++;
            }

            for (int k = 0; k < corners; k++)
            {
                uint off = mem.ReadU16(f + idx[k]);
                int s = (int)(off >> 3) & 8191;
                if (_vAt[s] != _stamp)
                {
                    uint p = verts + off;
                    xf.World(v, (short)mem.ReadU16(p), (short)mem.ReadU16(p + 2u), (short)mem.ReadU16(p + 4u),
                             out _vx[s * 3], out _vx[s * 3 + 1], out _vx[s * 3 + 2]);
                    _vAt[s] = _stamp;
                }
                wx[k] = _vx[s * 3]; wy[k] = _vx[s * 3 + 1]; wz[k] = _vx[s * 3 + 2];
                if (gouraud)
                {
                    uint no = mem.ReadU16(f + nrm[k]);
                    int t = (int)(no >> 3) & 8191;
                    if (_nAt[t] != _stamp)
                    {
                        uint q = normals + no;
                        Gte.LightDots((short)mem.ReadU16(q), (short)mem.ReadU16(q + 2u), (short)mem.ReadU16(q + 4u),
                                      out _nd[t * 3], out _nd[t * 3 + 1], out _nd[t * 3 + 2]);
                        _nAt[t] = _stamp;
                    }
                    cr[k] = _nd[t * 3]; cg[k] = _nd[t * 3 + 1]; cb[k] = _nd[t * 3 + 2];
                }
            }
            if (tile)
            {
                uint lit = Light(mem, normals + mem.ReadU16(f + nrm[0]), rgbc);
                for (int k = 0; k < corners; k++) { cr[k] = lit & 0xFF; cg[k] = (lit >> 8) & 0xFF; cb[k] = (lit >> 16) & 0xFF; }
            }
            else if (!gouraud)
            {
                uint q = normals + mem.ReadU16(f + nrm[0]);
                Gte.LightProducts((short)mem.ReadU16(q), (short)mem.ReadU16(q + 2u), (short)mem.ReadU16(q + 4u),
                                  out int i1, out int i2, out int i3);
                cr[0] = (rgbc & 0xFF) * i1 / 4096f; cg[0] = ((rgbc >> 8) & 0xFF) * i2 / 4096f; cb[0] = ((rgbc >> 16) & 0xFF) * i3 / 4096f;
                for (int k = 1; k < corners; k++) { cr[k] = cr[0]; cg[k] = cg[0]; cb[k] = cb[0]; }
            }
            uv[0] = mem.ReadU16(f);
            uv[1] = mem.ReadU16(f + 4u);
            uv[2] = mem.ReadU16(f + 8u);
            uv[3] = corners == 4 ? mem.ReadU16(f + 0xCu) : 0u;
            uint clut = mem.ReadU16(f + 2u), tpage = mem.ReadU16(f + 6u) & 0x1FFu;
            int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
            for (int k = 0; k < corners; k++)
            {
                int u = (int)(uv[k] & 0xFF), vv = (int)(uv[k] >> 8);
                u0 = Math.Min(u0, u); v0 = Math.Min(v0, vv); u1 = Math.Max(u1, u); v1 = Math.Max(v1, vv);
            }
            var t0 = new RetainedScene.Vertex
            {
                Clut = clut & 0x7FFF, Texpage = tpage, Dqa = dqa, Dqb = dqb, Curve = curve,
                Rect = (uint)u0 | (uint)v0 << 8 | (uint)u1 << 16 | (uint)v1 << 24,
                Flags = RetainedScene.FlagRect | mat | (gouraud ? RetainedScene.FlagDots : 0u),
                Rgbc = rgbc,
            };
            ref var dst = ref clip ? ref _mainClip : ref _main;
            ref int at = ref clip ? ref nc : ref n;
            if (at + 6 > dst.Length) Array.Resize(ref dst, dst.Length * 2);
            PutMain(dst, ref at, t0, wx, wy, wz, uv, cr, cg, cb, 0); PutMain(dst, ref at, t0, wx, wy, wz, uv, cr, cg, cb, 1); PutMain(dst, ref at, t0, wx, wy, wz, uv, cr, cg, cb, 2);
            if (corners == 4) { PutMain(dst, ref at, t0, wx, wy, wz, uv, cr, cg, cb, 1); PutMain(dst, ref at, t0, wx, wy, wz, uv, cr, cg, cb, 3); PutMain(dst, ref at, t0, wx, wy, wz, uv, cr, cg, cb, 2); }
            if (!mirror) MainFaces++;
            if (clip && !mirror) MainClipped++;
        }
        RetainedScene.AddMainModel(_main.AsSpan(0, n), _light, mirror: mirror);
        RetainedScene.AddMainModel(_mainClip.AsSpan(0, nc), _light, cull: true, mirror: mirror);
        if (mirror) { MirrorModels++; return; }
        MainModels++;
        if (tile) MainTileModels++;
    }

    /// <summary>Of <see cref="MainModels"/>, those from the clipped map assembler, and of
    /// <see cref="MainFaces"/>, those it would have clipped.</summary>
    public static long MainTileModels, MainClipped;

    // ---- 0085: models drawn from cached meshes (Step 3's second slice) --------------

    /// <summary>Draw the lit assembler's models from meshes kept on the GPU; off, each
    /// model's faces are taken to world space on the CPU every frame
    /// (<see cref="CaptureMain"/>), the comparison (<c>KF2_GPUWORLD_MESHES=0</c>).</summary>
    public static bool MeshesOn = true;

    /// <summary>Whether the submit about to transform a lit model placed in the world
    /// can draw it from its mesh: the main view's or the mirror's capture, and nothing
    /// else wanting the faces themselves (the retained reflections, the shadows).</summary>
    public static bool InstanceWanted => MeshesOn && (MainCapturing || MirrorCapturing) && !Capturing;

    /// <summary>Set while the current submit's opaque faces went to an instance: the lit
    /// assembler builds only its blended faces and captures nothing.</summary>
    public static bool Instanced;

    /// <summary>Meshes built, found, found changed and rebuilt; instances made, those with
    /// no blended face (transform and assembler skipped), and those for the mirror's
    /// replay; submits whose vertices or mesh could not be read. Never reset.</summary>
    public static long MeshBuilds, MeshHits, MeshStale, Instances, InstancesWhole, InstancesMirror, InstanceRefused;

    sealed class Mesh
    {
        /// <summary>The opaque faces' corners, then (at <c>Start + Count</c>) the blended
        /// faces', in the store.</summary>
        public int Start, Count, SemiCount, MaxVertex;
        public uint FaceBytes, NormalLo, NormalHi;
        public ulong FaceHash, NormalHash;
        public bool Blended;
        /// <summary>Per face, in the game's order: its first corner in the store (-1 for a
        /// face the assembler skips), its corner count (3 or 6), whether it is blended and
        /// its blend mode (the face's own, bits 5-6 of its tpage word).</summary>
        public int[] FaceAt = [];
        public byte[] FaceN = [], FaceMode = [];
        public bool[] FaceSemi = [];
        /// <summary>Blended faces in the mesh, and those of them subtractive.</summary>
        public int SemiFaces, SubtractiveFaces;
        /// <summary>Faces on one of the water's rects (their corners carry
        /// <see cref="RetainedScene.FlagWater"/>), blended and in all.</summary>
        public int WaterSemiFaces, WaterFaces;
    }

    static readonly Dictionary<(uint Face, uint Count, uint Normals, bool Water), Mesh> _meshes = new();
    static int _meshGen = -1;
    static RetainedScene.Vertex[] _corners = new RetainedScene.Vertex[1024];
    static readonly float[] _ins = new float[12];

    /// <summary>The store is emptied past this many corners, and on every overlay load.</summary>
    const int MeshStoreCap = 1 << 20;

    public static void ForgetMeshes()
    {
        _meshes.Clear();
        _skyMeshes.Clear();
        RetainedScene.ClearMeshes();
        _meshGen = RetainedScene.MeshGeneration;
    }

    /// <summary>
    /// A lit model (sub-model <paramref name="sub"/> of the mesh the submitter just
    /// selected, <paramref name="vertices"/> posed vertices at the vertex base) drawn
    /// from its cached mesh: the mesh built on first sight and checked by a hash of its
    /// faces and normals every time, the posed vertices copied to the frame, and an
    /// instance placed and lit from the GTE as the submitter left it. True when the model
    /// has no blended face, so neither the transform nor the assembler need run.
    /// </summary>
    public static bool TryInstance(PSMemory mem, uint sub, uint bias, uint vertices, int twin = -1, bool tile = false) =>
        Instance(mem, sub, bias, vertices, arm: false, twin, tile);

    /// <summary>Instances of objects near the camera, which the clipped map assembler
    /// (<c>func_80030540</c>) would have assembled; never reset.</summary>
    public static long TileInstances;

    /// <summary>Whether the last <see cref="TryInstance"/> made an instance the mirror
    /// draws too, so the planar walk's replay may leave the submit out.</summary>
    public static bool LastMirrored;

    /// <summary>A model's blended faces are drawn by the backend (<c>KF2_GPUWORLD_BLEND=0</c>
    /// puts them back on the packets).</summary>
    public static bool BlendOn = true;

    /// <summary>A model's blended faces drawn by the backend reach the surface buffer as
    /// their packets did (<c>KF2_GPUWORLD_BLENDSURFACES=0</c> leaves them out, the comparison).</summary>
    public static bool BlendSurfacesOn = true;

    /// <summary>Objects near the camera drawn from their meshes; off, the clipped map
    /// assembler's faces are taken to world space on the CPU every frame
    /// (<c>KF2_GPUWORLD_TILE=0</c>, the comparison).</summary>
    public static bool TileOn = true;

    /// <summary>The mirror's blended faces drawn by the backend too
    /// (<c>KF2_GPUWORLD_MIRRORBLEND=0</c> leaves them on the mirrored table's packets).</summary>
    public static bool MirrorBlendOn = true;

    /// <summary>Which routes the backend draws blended faces for: 1 the lit routine's
    /// blended faces, 2 the forced-blend twin's (effects, billboards). A comparison.</summary>
    public static int BlendRoutes = 3;

    /// <summary>Main-view submits with a subtractive face (a model's own, or the twin's
    /// rate); never reset.</summary>
    public static long Subtractive;

    /// <summary>The first-person arm (<c>func_80032400</c>: sub-model 0, slot bias 100)
    /// drawn from its mesh as <see cref="TryInstance"/> draws a model. Stage 13 draws
    /// it before the tile walk begins the frame, so the instance is held, in view space,
    /// until <see cref="AtFrame"/>, and placed in the world with that frame's camera;
    /// it is never in the mirror.</summary>
    public static bool TryArm(PSMemory mem, uint vertices) => Instance(mem, 0u, 0x64u, vertices, arm: true);

    /// <summary>Whether the arm about to be posed may be drawn from its mesh: the
    /// models on the GPU in the frame about to begin, and the far colour black as their
    /// records need.</summary>
    public static bool ArmWanted => MeshesOn && ArmOn && GpuWorld.ModelsReady && !PolyAssembler.Verifying
                                    && Gte.ReadControl(21) == 0 && Gte.ReadControl(22) == 0 && Gte.ReadControl(23) == 0;

    /// <summary>The arm drawn from its mesh; off, on the packets (<c>KF2_GPUWORLD_ARM=0</c>).</summary>
    public static bool ArmOn = true;

    /// <summary>An arm instance waiting for its frame; cleared by the arm routine on entry.</summary>
    public static bool ArmPending;
    static RetainedScene.ModelInstance _arm;
    static Transform _armXf;
    static int _armSerial, _armMeshGen, _armVertCount;
    static short[] _armVerts = new short[4096];

    /// <summary>Arm instances made, and those whose frame never took them (the arm then
    /// missing its opaque faces for a frame); never reset.</summary>
    public static long ArmInstances, ArmLost;

    /// <summary>From <see cref="GpuWorld.AtFrame"/>, once the frame has begun: the held
    /// arm, placed with the frame's camera, which is the one it was drawn under.</summary>
    public static void AtFrame()
    {
        // A mesh's faces on the water's rects are marked when it is built.
        ulong rk = RectKey();
        if (rk != _rectKey) { _rectKey = rk; ForgetMeshes(); }
        if (!ArmPending) return;
        ArmPending = false;
        var f = RetainedScene.Find(RetainedScene.Serial);
        if (f == null || _armSerial != RetainedScene.Serial - 1 || !GpuWorld.ModelsActive
            || _armMeshGen != RetainedScene.MeshGeneration)
        { ArmLost++; return; }
        var m = _arm;
        if (m.Pose == 0)
        {
            m.VertBase = RetainedScene.AddModelVertices(_armVerts.AsSpan(0, _armVertCount * 4));
            if (m.VertBase < 0) { ArmLost++; return; }
        }
        _armXf.ToWorld(f.View, _ins);
        Place(ref m, _ins);
        RetainedScene.SetArm(m, _armIndices.AsSpan(0, _armIndexCount), _armRunKey.AsSpan(0, _armRuns), _armRunAt.AsSpan(0, _armRuns), _armBox);
        ArmInstances++;
    }

    static int _armIndexCount, _armRuns;
    static int[] _armRunKey = new int[64], _armRunAt = new int[64];
    static int[] _armIndices = new int[1024];
    static (int Key, int Face, int Corner, int Corners)[] _armFaces = new (int, int, int, int)[256];

    /// <summary>
    /// The arm's faces in the order the table walks its packets, as corner indices into
    /// the mesh store, in runs of one key. The lit assembler
    /// links a face at the mean of its corners' SZ over four plus the bias; the walk
    /// takes the table from the far end (slot 0x1FFF less the key), and within a key
    /// the face linked last first. The corners from the posed vertices as the shader
    /// will have them (the store's blend, or the copy), each SZ as the GTE makes it,
    /// (R v &gt;&gt; 12) + T in integers: a float depth swaps two faces whose keys differ
    /// by one. A face the assembler drops by its depth has no packet; one turned away
    /// is left for the shader to drop, as the assembler drops it. Also the box on the
    /// screen its corners reach, as RTPS places them, which the walk tests a packet
    /// against before it stops to draw the arm.
    /// </summary>
    static void ArmOrder(Mesh mesh, in RetainedScene.ModelInstance ins, int pose, bool morph, int weight, int bias)
    {
        int r0 = ins.V20, r1 = ins.V21, r2 = ins.V22, tz = ins.Vtz;
        int a0 = ins.V00, a1 = ins.V01, a2 = ins.V02, tx = ins.Vtx, b0 = ins.V10, b1 = ins.V11, b2 = ins.V12, ty = ins.Vty;
        float h = Gte.ReadControl(26) & 0xFFFF, ofx = (int)Gte.ReadControl(24) / 65536f, ofy = (int)Gte.ReadControl(25) / 65536f;
        float bx0 = float.MaxValue, by0 = float.MaxValue, bx1 = float.MinValue, by1 = float.MinValue;
        var store = RetainedScene.PoseStore;
        var corners = RetainedScene.MeshCorners;
        int Z(uint i)
        {
            int x, y, z;
            if (pose == 0) { int a = (int)i * 4; x = _armVerts[a]; y = _armVerts[a + 1]; z = _armVerts[a + 2]; }
            else if (!morph) { int a = (pose - 1 + (int)i) * 4; x = store[a]; y = store[a + 1]; z = store[a + 2]; }
            else
            {
                int k = (pose - 1 + 2 * (int)i) * 4, d = k + 4;
                x = (short)(store[k] + (short)((store[d] * weight) >> 12));
                y = (short)(store[k + 1] + (short)((store[d + 1] * weight) >> 12));
                z = (short)(store[k + 2] + (short)((store[d + 2] * weight) >> 12));
            }
            int vz = ((r0 * x + r1 * y + r2 * z) >> 12) + tz;
            int vx = Math.Clamp(((a0 * x + a1 * y + a2 * z) >> 12) + tx, -32768, 32767);
            int vy = Math.Clamp(((b0 * x + b1 * y + b2 * z) >> 12) + ty, -32768, 32767);
            float q = h / Math.Max(Math.Clamp(vz, 0, 65535), h * 0.5f);
            float sx = Math.Clamp(ofx + vx * q, -1024f, 1023f), sy = Math.Clamp(ofy + vy * q, -1024f, 1023f);
            bx0 = Math.Min(bx0, sx); bx1 = Math.Max(bx1, sx); by0 = Math.Min(by0, sy); by1 = Math.Max(by1, sy);
            return vz;
        }
        int n = 0, face = 0;
        for (int c = mesh.Start; c + 2 < mesh.Start + mesh.Count; c += 3)
        {
            ref var k = ref corners[c];
            if ((k.Flags & RetainedScene.FlagQuadTail) != 0) continue;
            bool quad = k.Rgbc != uint.MaxValue;
            int sum = (int)Math.Clamp(Z((uint)k.Dqa), 0, 65535) >> 2;
            sum += (int)Math.Clamp(Z((uint)k.Dqb), 0, 65535) >> 2;
            sum += (int)Math.Clamp(Z((uint)k.Curve), 0, 65535) >> 2;
            int zz = quad ? (sum + ((int)Math.Clamp(Z(k.Rgbc), 0, 65535) >> 2)) >> 2 : sum / 3;
            face++;
            if (zz <= 0 || (uint)(zz + bias) >= 0x2000u) continue;
            if (n == _armFaces.Length) Array.Resize(ref _armFaces, n * 2);
            _armFaces[n++] = (zz + bias, face, c, quad ? 6 : 3);
        }
        // Far to near; within a key, the last linked first.
        Array.Sort(_armFaces, 0, n, Comparer<(int Key, int Face, int Corner, int Corners)>.Create(
            (a, b) => a.Key != b.Key ? b.Key.CompareTo(a.Key) : b.Face.CompareTo(a.Face)));
        _armIndexCount = 0;
        _armRuns = 0;
        for (int i = 0; i < n; i++)
        {
            var (key, _, c, m) = _armFaces[i];
            if (i == 0 || key != _armFaces[i - 1].Key)
            {
                if (_armRuns == _armRunKey.Length) { Array.Resize(ref _armRunKey, _armRuns * 2); Array.Resize(ref _armRunAt, _armRuns * 2); }
                _armRunKey[_armRuns] = key;
                _armRunAt[_armRuns++] = _armIndexCount;
            }
            if (_armIndexCount + m > _armIndices.Length) Array.Resize(ref _armIndices, _armIndices.Length * 2);
            for (int j = 0; j < m; j++) _armIndices[_armIndexCount++] = c + j;
        }
        // A pixel over, for the sub-pixel positions and the rasterizer's edges.
        _armBox[0] = bx0 - 1f; _armBox[1] = by0 - 1f; _armBox[2] = bx1 + 1f; _armBox[3] = by1 + 1f;
    }

    static readonly float[] _armBox = new float[4];

    /// <summary>The GTE's own rotation and translation into the instance's view-space
    /// fields, as RTPS will place each corner (the arm takes these too).</summary>
    static void ReadGteMatrix(ref RetainedScene.ModelInstance m)
    {
        uint r0 = Gte.ReadControl(0), r1 = Gte.ReadControl(1), r2 = Gte.ReadControl(2), r3 = Gte.ReadControl(3);
        m.V00 = (short)r0; m.V01 = (short)(r0 >> 16); m.V02 = (short)r1; m.V10 = (short)(r1 >> 16);
        m.V11 = (short)r2; m.V12 = (short)(r2 >> 16); m.V20 = (short)r3; m.V21 = (short)(r3 >> 16);
        m.V22 = (short)Gte.ReadControl(4);
        m.Vtx = (int)Gte.ReadControl(5); m.Vty = (int)Gte.ReadControl(6); m.Vtz = (int)Gte.ReadControl(7);
    }

    static short[] _blendVerts = new short[4096];
    static (int Key, int Corner, int Corners, int Mode)[] _blendFaces = new (int, int, int, int)[256];

    /// <summary>
    /// The instance just added's blended faces, each with the slot the lit assembler
    /// would link it into the table at: the mean of its corners' SZ over four, averaged
    /// as the game averages them, plus the slot bias. Taken with the GTE's own rotation
    /// and translation in integers, from the posed vertices (the frame's copy, or the
    /// pose store's blend), so no transform runs and no packet is built. A face the
    /// assembler drops by its depth has no entry; one turned away is left for the
    /// shader, which drops it as the assembler does. With <paramref name="twin"/> (the
    /// forced-blend routine) every face is blended, at that mode.
    /// </summary>
    static void NoteBlended(Mesh mesh, in RetainedScene.ModelInstance ins, ReadOnlySpan<short> posed, int pose, bool morph,
                            int weight, int bias, int twin, bool tile, bool mirror)
    {
        if (pose == 0)
        {
            if (_blendVerts.Length < posed.Length) _blendVerts = new short[posed.Length];
            posed.CopyTo(_blendVerts);
        }
        int r0 = ins.V20, r1 = ins.V21, r2 = ins.V22, tz = ins.Vtz;
        int a0 = ins.V00, a1 = ins.V01, a2 = ins.V02, tx = ins.Vtx, b0 = ins.V10, b1 = ins.V11, b2 = ins.V12, ty = ins.Vty;
        float h = Gte.ReadControl(26) & 0xFFFF, ofx = (int)Gte.ReadControl(24) / 65536f, ofy = (int)Gte.ReadControl(25) / 65536f;
        float bx0 = float.MaxValue, by0 = float.MaxValue, bx1 = float.MinValue, by1 = float.MinValue;
        var store = RetainedScene.PoseStore;
        var corners = RetainedScene.MeshCorners;
        var verts = _blendVerts;
        bool far = RenderDistance.Any;
        int Z(uint i, bool box)
        {
            int x, y, z;
            if (pose == 0) { int a = (int)i * 4; x = verts[a]; y = verts[a + 1]; z = verts[a + 2]; }
            else if (!morph) { int a = (pose - 1 + (int)i) * 4; x = store[a]; y = store[a + 1]; z = store[a + 2]; }
            else
            {
                int k = (pose - 1 + 2 * (int)i) * 4, d = k + 4;
                x = (short)(store[k] + (short)((store[d] * weight) >> 12));
                y = (short)(store[k + 1] + (short)((store[d + 1] * weight) >> 12));
                z = (short)(store[k + 2] + (short)((store[d + 2] * weight) >> 12));
            }
            int vz = ((r0 * x + r1 * y + r2 * z) >> 12) + tz;
            if (box)
            {
                int vx = Math.Clamp(((a0 * x + a1 * y + a2 * z) >> 12) + tx, -32768, 32767);
                int vy = Math.Clamp(((b0 * x + b1 * y + b2 * z) >> 12) + ty, -32768, 32767);
                float q = h / Math.Max(Math.Clamp(vz, 0, 65535), h * 0.5f);
                float sx = Math.Clamp(ofx + vx * q, -1024f, 1023f), sy = Math.Clamp(ofy + vy * q, -1024f, 1023f);
                bx0 = Math.Min(bx0, sx); bx1 = Math.Max(bx1, sx); by0 = Math.Min(by0, sy); by1 = Math.Max(by1, sy);
            }
            return vz;
        }
        int n = 0;
        for (int i = 0; i < mesh.FaceAt.Length; i++)
        {
            int c = mesh.FaceAt[i];
            if (c < 0 || twin < 0 && !mesh.FaceSemi[i]) continue;
            ref var k = ref corners[c];
            bool quad = mesh.FaceN[i] == 6;
            int sum = Math.Clamp(Z((uint)k.Dqa, false), 0, 65535) >> 2;
            sum += Math.Clamp(Z((uint)k.Dqb, false), 0, 65535) >> 2;
            sum += Math.Clamp(Z((uint)k.Curve, false), 0, 65535) >> 2;
            int zz = quad ? (sum + (Math.Clamp(Z(k.Rgbc, false), 0, 65535) >> 2)) >> 2 : sum / 3;
            int key = zz + bias;
            // The clipped assembler links every face: at slot 16 at least, wrapped.
            if (tile) key = Math.Max(key, 16) & 0x1FFF;
            else
            {
                if (zz <= 0) continue;
                if ((uint)key >= 0x2000u) { if (!far) continue; key = 0x1FFE; }
            }
            if (n == _blendFaces.Length) Array.Resize(ref _blendFaces, n * 2);
            _blendFaces[n++] = (key, c, quad ? 6 : 3, twin >= 0 ? twin : mesh.FaceMode[i]);
            Z((uint)k.Dqa, true); Z((uint)k.Dqb, true); Z((uint)k.Curve, true);
            if (quad) Z(k.Rgbc, true);
        }
        if (n == 0) return;
        // A pixel over, for the sub-pixel positions and the rasterizer's edges.
        bx0 -= 1f; by0 -= 1f; bx1 += 1f; by1 += 1f;
        for (int i = 0; i < n; i++)
        {
            var (key, c, m, mode) = _blendFaces[i];
            RetainedScene.AddBlendFace(key, c, m, mode, bx0, by0, bx1, by1, mirror);
        }
    }

    // ---- 0085: the sky (Step 3's sixth slice) ---------------------------------------

    /// <summary>The objects of kind 0xF0 drawn from their meshes; off, on the packets
    /// (<c>KF2_GPUWORLD_SKY=0</c>).</summary>
    public static bool SkyOn = true;

    /// <summary>Sky objects instanced, and those refused; never reset.</summary>
    public static long SkyInstances, SkyRefused;

    static readonly Dictionary<(uint Face, uint Count, uint Normals), Mesh> _skyMeshes = new();
    static (int Corner, int Corners, int Key, int Mode)[] _skyFaces = new (int, int, int, int)[256];

    /// <summary>
    /// An object of kind 0xF0 (<c>func_80032AC4</c>), its sub-model 0 posed with
    /// <paramref name="vertices"/> vertices, drawn from its mesh as the sky: in view space
    /// with the GTE's own matrix (the camera's rotation and no translation), lit as
    /// <c>func_8002F918</c> lights it, every face at slot <paramref name="key"/>, the
    /// textured ones at blend rate <paramref name="rate"/> where they are blended. Only
    /// the table's last two slots, which the walk reaches before the map, are taken. True
    /// when the transform and the assembler need not run.
    /// </summary>
    public static bool TrySpecial(PSMemory mem, uint vertices, int key, int rate)
    {
        Instanced = false;
        if (!SkyOn || !MainCapturing || key < 0x1FFE || key > 0x1FFF) return false;
        var frame = RetainedScene.Find(RetainedScene.Serial);
        if (frame == null) return false;

        uint table = mem.ReadU32(PolyModelTable);
        uint header = 0xCu + table;
        uint count = mem.ReadU32(header + 0x14u);
        uint normals = mem.ReadU32(header + 8u) + 0xCu + table;
        uint face = mem.ReadU32(header + 0x10u) + 0xCu + table;
        uint verts = mem.ReadU32(VertexBase);
        if (count == 0 || count > 4096 || vertices == 0 || vertices > 8192
            || !InRam(face, 4) || !InRam(normals, 8) || !InRam(verts, vertices * 8u)) { SkyRefused++; return false; }

        if (_meshGen != RetainedScene.MeshGeneration) { _meshes.Clear(); _skyMeshes.Clear(); _meshGen = RetainedScene.MeshGeneration; }
        var ram = mem.Ram;
        var mkey = (face, count, normals);
        if (_skyMeshes.TryGetValue(mkey, out var mesh)
            && (Hash(ram, face, mesh.FaceBytes) != mesh.FaceHash
                || mesh.NormalHi > mesh.NormalLo && Hash(ram, normals + mesh.NormalLo, mesh.NormalHi - mesh.NormalLo) != mesh.NormalHash))
        { MeshStale++; mesh = null; }
        if (mesh == null)
        {
            if (RetainedScene.MeshCornerCount > MeshStoreCap) ForgetMeshes();
            mesh = BuildSky(mem, face, count, normals);
            if (mesh == null) { SkyRefused++; return false; }
            _skyMeshes[mkey] = mesh;
            MeshBuilds++;
        }
        else MeshHits++;
        if (mesh.MaxVertex >= (int)vertices) { SkyRefused++; return false; }

        int pose = 0, weight = 0;
        bool morph = false;
        if (PosesOn)
        {
            if (MoPose.Pending) morph = (pose = MoPose.Store(mem, vertices, out weight)) != 0;
            else if (verts != PosedBuffer) pose = MoPose.StoreRigid(mem, verts, vertices);
        }
        if (pose == 0 || MoPose.Checking) MoPose.Materialize(null, mem);
        int vb = 0;
        if (pose == 0)
        {
            var posed = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(
                ram.Slice((int)(verts & (Runtime.RamSize - 1)), (int)vertices * 8));
            vb = RetainedScene.AddModelVertices(posed);
            if (vb < 0) return false;
        }

        var v = frame.View;
        Transform.Read(v).ToWorld(v, _ins);
        var m = new RetainedScene.ModelInstance
        {
            MeshStart = mesh.Start, MeshCount = mesh.Count, MeshAll = mesh.Count + mesh.SemiCount, VertBase = vb,
            Pose = pose, PoseWeight = weight, PoseMorph = morph,
            Far = 1e30f, Near = -1e30f,
            Rgbc = mem.ReadU32(LightColour) & 0xFFFFFFu,
            ViewSpace = true, Sky = true,
        };
        Place(ref m, _ins);
        ReadGteMatrix(ref m);
        uint l0 = Gte.ReadControl(8), l1 = Gte.ReadControl(9), l2 = Gte.ReadControl(10), l3 = Gte.ReadControl(11);
        m.Llm0 = (short)l0 / 4096f; m.Llm1 = (short)(l0 >> 16) / 4096f; m.Llm2 = (short)l1 / 4096f;
        m.Llm3 = (short)(l1 >> 16) / 4096f; m.Llm4 = (short)l2 / 4096f; m.Llm5 = (short)(l2 >> 16) / 4096f;
        m.Llm6 = (short)l3 / 4096f; m.Llm7 = (short)(l3 >> 16) / 4096f; m.Llm8 = (short)Gte.ReadControl(12) / 4096f;
        m.Bk0 = (int)Gte.ReadControl(13); m.Bk1 = (int)Gte.ReadControl(14); m.Bk2 = (int)Gte.ReadControl(15);
        uint c0 = Gte.ReadControl(16), c1 = Gte.ReadControl(17), c2 = Gte.ReadControl(18), c3 = Gte.ReadControl(19);
        m.L0 = (short)c0; m.L1 = (short)(c0 >> 16); m.L2 = (short)c1; m.L3 = (short)(c1 >> 16);
        m.L4 = (short)c2; m.L5 = (short)(c2 >> 16); m.L6 = (short)c3; m.L7 = (short)(c3 >> 16);
        m.L8 = (short)Gte.ReadControl(20);

        // Every face, in the order the assembler links them; the shader drops one turned away.
        int n = 0;
        if (_skyFaces.Length < mesh.FaceAt.Length) _skyFaces = new (int, int, int, int)[mesh.FaceAt.Length];
        for (int i = 0; i < mesh.FaceAt.Length; i++)
        {
            if (mesh.FaceAt[i] < 0) continue;
            _skyFaces[n++] = (mesh.FaceAt[i], mesh.FaceN[i], key, mesh.FaceSemi[i] ? rate : -1);
        }
        RetainedScene.AddSky(m, _skyFaces.AsSpan(0, n));
        Instanced = true;
        SkyInstances++;
        return true;
    }

    /// <summary>A sky mesh in <c>func_8002F918</c>'s four face types: gouraud triangles
    /// and quads, textured (`0x34`, `0x3C`, blended with bit 1) or in the face's own colour
    /// (`0x30`, `0x38`, never blended). The textured faces' blend rate is the record's, so
    /// it is the instance's, not the mesh's.</summary>
    static Mesh? BuildSky(PSMemory mem, uint face, uint count, uint normals)
    {
        var mesh = new Mesh();
        uint start = face;
        uint nlo = uint.MaxValue, nhi = 0;
        int n = 0, ns = 0, maxV = -1;
        mesh.FaceAt = new int[count];
        mesh.FaceN = new byte[count];
        mesh.FaceMode = new byte[count];
        mesh.FaceSemi = new bool[count];
        Span<uint> idx = stackalloc uint[4], nrm = stackalloc uint[4], uv = stackalloc uint[4], vi = stackalloc uint[4];
        for (uint i = 0; i < count; i++)
        {
            mesh.FaceAt[i] = -1;
            if (!InRam(face, 4)) return null;
            uint word = mem.ReadU32(face);
            uint f = face + 4u;
            uint cmd = word >> 24;
            face = f + ((word >> 6) & 0x3FCu);
            int corners;
            bool textured = true;
            switch (cmd & 0xFDu)
            {
                case 0x34u: corners = 3; idx[0] = 0x0E; idx[1] = 0x12; idx[2] = 0x16; nrm[0] = 0x0C; nrm[1] = 0x10; nrm[2] = 0x14; break;
                case 0x3Cu: corners = 4; idx[0] = 0x12; idx[1] = 0x16; idx[2] = 0x1A; idx[3] = 0x1E; nrm[0] = 0x10; nrm[1] = 0x14; nrm[2] = 0x18; nrm[3] = 0x1C; break;
                case 0x30u: corners = 3; textured = false; idx[0] = 0x06; idx[1] = 0x0A; idx[2] = 0x0E; nrm[0] = 0x04; nrm[1] = 0x08; nrm[2] = 0x0C; break;
                case 0x38u: corners = 4; textured = false; idx[0] = 0x06; idx[1] = 0x0A; idx[2] = 0x0E; idx[3] = 0x12; nrm[0] = 0x04; nrm[1] = 0x08; nrm[2] = 0x0C; nrm[3] = 0x10; break;
                default: continue;
            }
            bool semi = textured && (cmd & 2u) != 0;
            if (semi) mesh.Blended = true;
            for (int k = 0; k < corners; k++)
            {
                uint off = mem.ReadU16(f + idx[k]);
                if ((off & 7u) != 0) return null;
                vi[k] = off >> 3;
                maxV = Math.Max(maxV, (int)vi[k]);
                uint no = mem.ReadU16(f + nrm[k]);
                nlo = Math.Min(nlo, no); nhi = Math.Max(nhi, no + 8u);
            }
            var t = new RetainedScene.Vertex
            {
                Dqa = vi[0], Dqb = vi[1], Curve = vi[2],
                Rgbc = corners == 4 ? vi[3] : uint.MaxValue,
                Flags = RetainedScene.FlagDots | (semi ? RetainedScene.FlagSemi : 0u),
            };
            if (textured)
            {
                uv[0] = mem.ReadU16(f);
                uv[1] = mem.ReadU16(f + 4u);
                uv[2] = mem.ReadU16(f + 8u);
                uv[3] = corners == 4 ? mem.ReadU16(f + 0xCu) : 0u;
                int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
                for (int k = 0; k < corners; k++)
                {
                    int u = (int)(uv[k] & 0xFF), vv = (int)(uv[k] >> 8);
                    u0 = Math.Min(u0, u); v0 = Math.Min(v0, vv); u1 = Math.Max(u1, u); v1 = Math.Max(v1, vv);
                }
                t.Clut = mem.ReadU16(f + 2u) & 0x7FFF;
                t.Texpage = mem.ReadU16(f + 6u) & 0x19Fu;
                t.Rect = (uint)u0 | (uint)v0 << 8 | (uint)u1 << 16 | (uint)v1 << 24;
                t.Flags |= RetainedScene.FlagRect;
            }
            else
            {
                uv[0] = uv[1] = uv[2] = uv[3] = 0u;
                t.Texpage = 0x8000;
                t.Light = RetainedScene.FaceColour | (mem.ReadU32(f) & 0xFFFFFFu);
            }
            var dst = semi ? _semiCorners : _corners;
            int at = semi ? ns : n;
            if (at + 6 > dst.Length)
            {
                Array.Resize(ref dst, dst.Length * 2);
                if (semi) _semiCorners = dst; else _corners = dst;
            }
            ReadOnlySpan<int> order = corners == 4 ? [0, 1, 2, 1, 3, 2] : [0, 1, 2];
            mesh.FaceAt[i] = at;
            mesh.FaceN[i] = (byte)order.Length;
            mesh.FaceSemi[i] = semi;
            if (semi) mesh.SemiFaces++;
            for (int j = 0; j < order.Length; j++)
            {
                int k = order[j];
                var c = t;
                uint q = normals + mem.ReadU16(f + nrm[k]);
                c.X = vi[k];
                c.R = (short)mem.ReadU16(q); c.G = (short)mem.ReadU16(q + 2u); c.B = (short)mem.ReadU16(q + 4u);
                c.U = uv[k] & 0xFF; c.V = uv[k] >> 8;
                if (j >= 3) c.Flags |= RetainedScene.FlagQuadTail;
                dst[at + j] = c;
            }
            if (semi) ns += order.Length; else n += order.Length;
        }
        mesh.FaceBytes = face - start;
        if (!InRam(start, mesh.FaceBytes)) return null;
        if (nhi > nlo)
        {
            mesh.NormalLo = nlo & ~3u;
            mesh.NormalHi = (nhi + 3u) & ~3u;
            if (!InRam(normals + mesh.NormalLo, mesh.NormalHi - mesh.NormalLo)) return null;
            mesh.NormalHash = Hash(mem.Ram, normals + mesh.NormalLo, mesh.NormalHi - mesh.NormalLo);
        }
        mesh.FaceHash = Hash(mem.Ram, start, mesh.FaceBytes);
        mesh.MaxVertex = maxV;
        mesh.Count = n;
        mesh.SemiCount = ns;
        if (n + ns > 0)
        {
            if (_both.Length < n + ns) _both = new RetainedScene.Vertex[Math.Max(n + ns, _both.Length * 2)];
            Array.Copy(_corners, _both, n);
            Array.Copy(_semiCorners, 0, _both, n, ns);
            mesh.Start = RetainedScene.AddMesh(_both.AsSpan(0, n + ns));
            for (int i = 0; i < mesh.FaceAt.Length; i++)
                if (mesh.FaceAt[i] >= 0) mesh.FaceAt[i] += mesh.Start + (mesh.FaceSemi[i] ? n : 0);
        }
        return mesh;
    }

    static void Place(ref RetainedScene.ModelInstance m, float[] o)
    {
        m.R00 = o[0]; m.R01 = o[1]; m.R02 = o[2]; m.R10 = o[3]; m.R11 = o[4]; m.R12 = o[5];
        m.R20 = o[6]; m.R21 = o[7]; m.R22 = o[8]; m.Tx = o[9]; m.Ty = o[10]; m.Tz = o[11];
    }

    static bool Instance(PSMemory mem, uint sub, uint bias, uint vertices, bool arm, int twin = -1, bool tile = false)
    {
        Instanced = false;
        LastMirrored = false;
        bool mirror = !arm && MirrorCapturing;
        var frame = RetainedScene.Find(RetainedScene.Serial);
        if (!arm && (frame == null || mirror && !frame.MirrorOn)) return false;

        uint table = mem.ReadU32(PolyModelTable);
        uint header = (sub & 0xFFFFu) * 28u + 0xCu + table;
        uint count = mem.ReadU32(header + 0x14u);
        uint normals = mem.ReadU32(header + 8u) + 0xCu + table;
        uint face = mem.ReadU32(header + 0x10u) + 0xCu + table;
        uint verts = mem.ReadU32(VertexBase);
        if (count == 0 || count > 4096 || vertices == 0 || vertices > 8192
            || !InRam(face, 4) || !InRam(normals, 8) || !InRam(verts, vertices * 8u)) { InstanceRefused++; return false; }

        if (_meshGen != RetainedScene.MeshGeneration) { _meshes.Clear(); _skyMeshes.Clear(); _meshGen = RetainedScene.MeshGeneration; }
        var ram = mem.Ram;
        // A model's faces in the water's texture are water only if ModelWater says so.
        bool waterOk = !arm && !PolyAssembler.NotWater;
        var key = (face, count, normals, waterOk);
        if (_meshes.TryGetValue(key, out var mesh))
        {
            if (Hash(ram, face, mesh.FaceBytes) != mesh.FaceHash
                || mesh.NormalHi > mesh.NormalLo && Hash(ram, normals + mesh.NormalLo, mesh.NormalHi - mesh.NormalLo) != mesh.NormalHash)
            {
                MeshStale++;
                mesh = null;
            }
            else MeshHits++;
        }
        if (mesh == null)
        {
            if (RetainedScene.MeshCornerCount > MeshStoreCap) ForgetMeshes();
            mesh = Build(mem, face, count, normals, waterOk);
            if (mesh == null) { InstanceRefused++; return false; }
            _meshes[key] = mesh;
            MeshBuilds++;
        }
        if (mesh.MaxVertex >= (int)vertices) { InstanceRefused++; return false; }
        // A model's blended faces: drawn by the backend in the main view when it can,
        // else built as packets. The forced-blend twin (effects, billboards) draws every
        // face so.
        bool twinRoute = twin >= 0;
        bool gpuBlend = !arm && BlendOn && (BlendRoutes & (twinRoute ? 2 : 1)) != 0 && RetainedScene.MainBlend
                        && (mirror ? RetainedScene.MirrorWater && MirrorBlendOn : RetainedScene.MainWater)
                        && (twinRoute ? mesh.FaceAt.Length > 0 : mesh.SemiFaces > 0);
        if (!arm && !mirror && (twin == 2 || !twinRoute && mesh.SubtractiveFaces > 0)) Subtractive++;
        if (twinRoute && !gpuBlend) return false;
        bool mirrorWhole = !twinRoute && !mesh.Blended;
        bool whole = twinRoute ? gpuBlend : mirrorWhole || gpuBlend;
        if (mesh.Count == 0 && !gpuBlend) { Instanced = true; return whole; }

        // The vertices from the pose store (Step 3's third slice): an MO pose the blender
        // left undecoded, or a rigid model's own; the posed buffer copied per frame else.
        int pose = 0, weight = 0;
        bool morph = false;
        if (PosesOn)
        {
            if (MoPose.Pending) morph = (pose = MoPose.Store(mem, vertices, out weight)) != 0;
            else if (verts != PosedBuffer) pose = MoPose.StoreRigid(mem, verts, vertices);
        }
        if (!whole || pose == 0 || MoPose.Checking) MoPose.Materialize(null, mem);
        if (pose != 0 && MoPose.Checking) MoPose.Check(mem, pose, morph, weight, verts, vertices);
        int vb = 0;
        var posed = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(
            ram.Slice((int)(verts & (Runtime.RamSize - 1)), (int)vertices * 8));
        if (pose == 0 && arm)
        {
            // Into the frame when it begins.
            if (_armVerts.Length < posed.Length) _armVerts = new short[posed.Length];
            posed.CopyTo(_armVerts);
            _armVertCount = (int)vertices;
        }
        else if (pose == 0)
        {
            vb = RetainedScene.AddModelVertices(posed);
            if (vb < 0) return false;
        }
        else if (morph) InstancesPosed++;
        else InstancesRigid++;

        // The arm's frame has not begun: its camera is read now only for the check.
        var v = arm ? RetainedMap.ReadView(mem) : mirror ? frame!.MirrorView : frame!.View;
        var xf = Transform.Read(v);
        xf.ToWorld(v, _ins);
        int mode = (int)mem.ReadU32(FogMode);
        var m = new RetainedScene.ModelInstance
        {
            MeshStart = mesh.Start, MeshCount = twinRoute ? 0 : mesh.Count, MeshAll = mesh.Count + mesh.SemiCount, VertBase = vb,
            Pose = pose, PoseWeight = weight, PoseMorph = morph,
            Dqa = (short)Gte.ReadControl(27), Dqb = (int)Gte.ReadControl(28),
            // The clipped assembler's transform fogs on the knee alone.
            Curve = mode >= 32000 ? 0f : (mode & 0x8000) != 0 && !tile ? 1f : 2f,
            // The table's test is (uint)(z + bias) >= 0x2000: a negative bias wraps it.
            // The clipped assembler clamps and wraps its slot instead of dropping a face.
            Far = RenderDistance.Any || tile ? 1e30f : 8192f - (int)bias,
            Near = tile ? -1e30f : RenderDistance.Any ? 0f : -(int)bias,
            Tile = tile,
            Rgbc = mem.ReadU32(LightColour) & 0xFFFFFFu,
            Material = PolyAssembler.TileMaterial,
            Mirrored = !mirror && mirrorWhole && !arm,
            Solid = gpuBlend && ModelWalk.SubmitKind == ModelKind.Object
                    && ModelWalk.SolidKind(ModelWalk.ObjectKind(mem, ModelWalk.SubmitRecord)),
            TwinMode = twinRoute ? twin + 1 : 0,
        };
        m.BlendSurfaces = gpuBlend && !mirror && BlendSurfacesOn
                          && (m.Solid || m.Material != 0 || (twinRoute ? mesh.WaterFaces : mesh.WaterSemiFaces) > 0);
        Place(ref m, _ins);
        uint l0 = Gte.ReadControl(8), l1 = Gte.ReadControl(9), l2 = Gte.ReadControl(10), l3 = Gte.ReadControl(11);
        m.Llm0 = (short)l0 / 4096f; m.Llm1 = (short)(l0 >> 16) / 4096f; m.Llm2 = (short)l1 / 4096f;
        m.Llm3 = (short)(l1 >> 16) / 4096f; m.Llm4 = (short)l2 / 4096f; m.Llm5 = (short)(l2 >> 16) / 4096f;
        m.Llm6 = (short)l3 / 4096f; m.Llm7 = (short)(l3 >> 16) / 4096f; m.Llm8 = (short)Gte.ReadControl(12) / 4096f;
        m.Bk0 = (int)Gte.ReadControl(13); m.Bk1 = (int)Gte.ReadControl(14); m.Bk2 = (int)Gte.ReadControl(15);
        uint c0 = Gte.ReadControl(16), c1 = Gte.ReadControl(17), c2 = Gte.ReadControl(18), c3 = Gte.ReadControl(19);
        m.L0 = (short)c0; m.L1 = (short)(c0 >> 16); m.L2 = (short)c1; m.L3 = (short)(c1 >> 16);
        m.L4 = (short)c2; m.L5 = (short)(c2 >> 16); m.L6 = (short)c3; m.L7 = (short)(c3 >> 16);
        m.L8 = (short)Gte.ReadControl(20);
        if (arm)
        {
            // The GTE's own matrix, as RTPS will place each corner.
            uint r0 = Gte.ReadControl(0), r1 = Gte.ReadControl(1), r2 = Gte.ReadControl(2), r3 = Gte.ReadControl(3);
            m.ViewSpace = true;
            m.V00 = (short)r0; m.V01 = (short)(r0 >> 16); m.V02 = (short)r1; m.V10 = (short)(r1 >> 16);
            m.V11 = (short)r2; m.V12 = (short)(r2 >> 16); m.V20 = (short)r3; m.V21 = (short)(r3 >> 16);
            m.V22 = (short)Gte.ReadControl(4);
            m.Vtx = (int)Gte.ReadControl(5); m.Vty = (int)Gte.ReadControl(6); m.Vtz = (int)Gte.ReadControl(7);
            _arm = m;
            _armXf = xf;
            ArmOrder(mesh, m, pose, morph, weight, (int)bias);
            _armSerial = RetainedScene.Serial;
            _armMeshGen = RetainedScene.MeshGeneration;
            ArmPending = true;
        }
        else
        {
            if (gpuBlend) ReadGteMatrix(ref m);
            RetainedScene.AddInstance(m, mirror);
            if (gpuBlend) NoteBlended(mesh, m, posed, pose, morph, weight, (int)bias, twin, tile, mirror);
        }
        LastMirrored = !mirror && mirrorWhole && !arm;
        if (tile) TileInstances++;
        _lastTile = tile;
        if (Checking && !tile) { _last = m; _lastView = v; _lastMirror = mirror; _lastFace = face; _lastCount = count; _lastBias = bias; _lastVerts = vertices; }
        Instanced = true;
        Instances++;
        if (whole) InstancesWhole++;
        if (mirror) InstancesMirror++;
        return whole;
    }

    const uint PolyModelTable = 0x8018E19C;

    /// <summary>The MO blender's posed buffer: a model whose vertex base points here was
    /// posed this frame, and is not a rigid model's own vertices.</summary>
    const uint PosedBuffer = 0x80190AD8;

    /// <summary>Draw instances' vertices from the pose store; off, every instance's posed
    /// vertices are copied into the frame (<c>KF2_GPUWORLD_POSES=0</c>, the comparison).</summary>
    public static bool PosesOn = true;

    /// <summary>Instances drawn from an MO pose in the store, and from a rigid model's
    /// vertices there; never reset.</summary>
    public static long InstancesPosed, InstancesRigid;

    /// <summary>
    /// What a skipped transform would have left behind: each <c>Gte.Rtp</c> notes the
    /// projection and the depth cue it ran under for the frame's screen passes, last
    /// writer wins, so the murk and the reflections fog with the last model's DQA and
    /// DQB. A model drawn whole from its mesh notes them as its transform would have.
    /// </summary>
    public static void NoteSkippedTransform()
    {
        if (!GteDepth.Active || !GteDepth.SurfacesWanted) return;
        GteDepth.NoteProjection(Gte.ReadControl(26) & 0xFFFF, (int)Gte.ReadControl(24) / 65536f, (int)Gte.ReadControl(25) / 65536f);
        GteDepth.NoteDepthCue((short)Gte.ReadControl(27), (int)Gte.ReadControl(28));
    }

    /// <summary><c>KF2_GPUWORLD_MESHCHECK=1</c>: the transform runs for every instance, and
    /// each of its faces is kept or dropped by a replica of the shader's test and by the
    /// lit assembler's own, on the vertex cache; the disagreements are counted.</summary>
    public static bool Checking;
    public static long CheckFaces, CheckDiffer, CheckPlaced, CheckLarge, CheckArm;
    /// <summary>The disagreements by twice the face's area on the screen, ours: under
    /// 0.01, 0.1, 1 and 10 square pixels, and more.</summary>
    public static readonly long[] CheckArea = new long[5];
    static RetainedScene.ModelInstance _last;
    static RetainedScene.View _lastView;
    static bool _lastMirror, _lastTile;
    static uint _lastFace, _lastCount, _lastBias, _lastVerts;

    /// <summary>After the transform of a model just instanced: each opaque face's keep
    /// decision both ways.</summary>
    public static void Check(PSMemory mem)
    {
        // The clipped assembler's faces are tested otherwise; the check is the lit assembler's.
        if (_lastTile) return;
        var v = _lastView;
        var m = _last;
        uint face = _lastFace;
        {
            // Each posed vertex placed by the instance's record against the capture's transform.
            var xf = Transform.Read(v);
            uint vb = mem.ReadU32(VertexBase);
            double worst = 0;
            for (uint k = 0; k < _lastVerts; k++)
            {
                uint p = vb + k * 8u;
                double x = (short)mem.ReadU16(p), y = (short)mem.ReadU16(p + 2u), z = (short)mem.ReadU16(p + 4u);
                xf.World(v, x, y, z, out float wx, out float wy, out float wz);
                double ix = m.R00 * x + m.R01 * y + m.R02 * z + m.Tx, iy = m.R10 * x + m.R11 * y + m.R12 * z + m.Ty;
                double iz = m.R20 * x + m.R21 * y + m.R22 * z + m.Tz;
                worst = Math.Max(worst, Math.Max(Math.Abs(ix - wx), Math.Max(Math.Abs(iy - wy), Math.Abs(iz - wz))));
            }
            if (worst > 1.0 && CheckPlaced++ < 8)
                Console.WriteLine($"[KF2] mesh check: of {_lastVerts} vertices, one placed {worst:F1} units from the capture's (placed {ModelWalk.Placed})");
        }
        Span<uint> idx = stackalloc uint[4];
        for (uint i = 0; i < _lastCount; i++)
        {
            uint word = mem.ReadU32(face);
            uint f = face + 4u;
            uint cmd = word >> 24;
            face = f + ((word >> 6) & 0x3FCu);
            int corners;
            switch (cmd & 0xFDu)
            {
                case 0x24u: corners = 3; idx[0] = 0x0E; idx[1] = 0x10; idx[2] = 0x12; break;
                case 0x2Cu: corners = 4; idx[0] = 0x12; idx[1] = 0x14; idx[2] = 0x16; idx[3] = 0x18; break;
                case 0x34u: corners = 3; idx[0] = 0x0E; idx[1] = 0x12; idx[2] = 0x16; break;
                case 0x3Cu: corners = 4; idx[0] = 0x12; idx[1] = 0x16; idx[2] = 0x1A; idx[3] = 0x1E; break;
                default: continue;
            }
            if ((cmd & 2u) != 0) continue;
            uint p0 = VertexCache + mem.ReadU16(f + idx[0]), p1 = VertexCache + mem.ReadU16(f + idx[1]);
            uint p2 = VertexCache + mem.ReadU16(f + idx[2]);
            uint p3 = corners == 4 ? VertexCache + mem.ReadU16(f + idx[3]) : 0u;
            bool game = PolyAssembler.FaceKept(mem, p0, p1, p2);
            int gz = (short)mem.ReadU16(p0 + 4u) + (short)mem.ReadU16(p1 + 4u) + (short)mem.ReadU16(p2 + 4u);
            gz = corners == 4 ? (gz + (short)mem.ReadU16(p3 + 4u)) >> 2 : gz / 3;
            if (gz <= 0 || (uint)gz + _lastBias >= 0x2000u && !RenderDistance.Any) game = false;

            // The shader's test, from the instance and the posed vertices.
            Span<double> sx = stackalloc double[4], sy = stackalloc double[4];
            int zs = 0;
            uint verts = mem.ReadU32(VertexBase);
            for (int k = 0; k < corners; k++)
            {
                uint p = verts + mem.ReadU16(f + idx[k]);
                double x = (short)mem.ReadU16(p), y = (short)mem.ReadU16(p + 2u), z = (short)mem.ReadU16(p + 4u);
                double vx, vy, vz;
                if (m.ViewSpace)
                {
                    // The shader's integer path (modelEye).
                    int ix = (int)x, iy = (int)y, iz = (int)z;
                    vx = Math.Clamp(((m.V00 * ix + m.V01 * iy + m.V02 * iz) >> 12) + m.Vtx, -32768, 32767);
                    vy = Math.Clamp(((m.V10 * ix + m.V11 * iy + m.V12 * iz) >> 12) + m.Vty, -32768, 32767);
                    vz = ((m.V20 * ix + m.V21 * iy + m.V22 * iz) >> 12) + m.Vtz;
                }
                else
                {
                    double wx = m.R00 * x + m.R01 * y + m.R02 * z + m.Tx - v.CamX;
                    double wy = m.R10 * x + m.R11 * y + m.R12 * z + m.Ty - v.CamY;
                    double wz = m.R20 * x + m.R21 * y + m.R22 * z + m.Tz - v.CamZ;
                    vx = v.R00 * wx + v.R01 * wy + v.R02 * wz + v.Tx;
                    vy = v.R10 * wx + v.R11 * wy + v.R12 * wz + v.Ty;
                    vz = v.R20 * wx + v.R21 * wy + v.R22 * wz + v.Tz;
                }
                zs += (int)Math.Clamp(vz, 0, 65535) >> 2;
                double q = Math.Max(Math.Clamp(vz, 0, 65535), v.H * 0.5);
                sx[k] = Math.Clamp(v.Cx + v.H * Math.Clamp(vx, -32768, 32767) / q, -1024, 1023);
                sy[k] = Math.Clamp(v.Cy + v.H * Math.Clamp(vy, -32768, 32767) / q, -1024, 1023);
            }
            int mz = corners == 4 ? zs >> 2 : zs / 3;
            double cross = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sy[1] - sy[0]) * (sx[2] - sx[0]);
            bool ours = !(mz <= 0 || mz < m.Near || mz >= m.Far) && cross > 0;
            CheckFaces++;
            if (ours == game) continue;
            double ac = Math.Abs(cross);
            CheckArea[ac < 0.01 ? 0 : ac < 0.1 ? 1 : ac < 1 ? 2 : ac < 10 ? 3 : 4]++;
            if (CheckDiffer++ < 12 || ac >= 10 && CheckLarge++ % 500 == 0 || m.ViewSpace && CheckArm++ < 40)
                Console.WriteLine($"[KF2] mesh check: face {i} game {(game ? "kept" : "dropped")} (z {gz}, " +
                                  $"screen {(short)mem.ReadU16(p0)},{(short)mem.ReadU16(p0 + 2u)} {(short)mem.ReadU16(p1)},{(short)mem.ReadU16(p1 + 2u)} {(short)mem.ReadU16(p2)},{(short)mem.ReadU16(p2 + 2u)}), " +
                                  $"ours {(ours ? "kept" : "dropped")} (z {mz}, screen {sx[0]:F1},{sy[0]:F1} {sx[1]:F1},{sy[1]:F1} {sx[2]:F1},{sy[2]:F1}); " +
                                  $"{(_lastMirror ? "mirror" : "main")}, H {v.H} against the GTE's {Gte.ReadControl(26) & 0xFFFF}, far {m.Far}");
        }
    }

    static bool InRam(uint a, uint bytes)
    {
        uint lo = a - 0x80000000u;
        return a >= 0x80000000u && lo + bytes <= Runtime.RamSize && (a & 3u) == 0;
    }

    /// <summary>A mesh's faces as corners in the store (see
    /// <see cref="RetainedScene.MeshCorners"/>): the opaque faces', then the blended
    /// faces', with a table of where each face's corners are; and the byte ranges its
    /// hashes cover.</summary>
    static Mesh? Build(PSMemory mem, uint face, uint count, uint normals, bool waterOk)
    {
        var mesh = new Mesh();
        uint start = face;
        uint nlo = uint.MaxValue, nhi = 0;
        int n = 0, ns = 0, maxV = -1;
        mesh.FaceAt = new int[count];
        mesh.FaceN = new byte[count];
        mesh.FaceMode = new byte[count];
        mesh.FaceSemi = new bool[count];
        Span<uint> idx = stackalloc uint[4], nrm = stackalloc uint[4], uv = stackalloc uint[4], vi = stackalloc uint[4];
        for (uint i = 0; i < count; i++)
        {
            mesh.FaceAt[i] = -1;
            if (!InRam(face, 4)) return null;
            uint word = mem.ReadU32(face);
            uint f = face + 4u;
            uint cmd = word >> 24;
            face = f + ((word >> 6) & 0x3FCu);
            int corners;
            bool gouraud;
            switch (cmd & 0xFDu)
            {
                case 0x24u: corners = 3; gouraud = false; idx[0] = 0x0E; idx[1] = 0x10; idx[2] = 0x12; nrm[0] = 0x0C; break;
                case 0x2Cu: corners = 4; gouraud = false; idx[0] = 0x12; idx[1] = 0x14; idx[2] = 0x16; idx[3] = 0x18; nrm[0] = 0x10; break;
                case 0x34u: corners = 3; gouraud = true; idx[0] = 0x0E; idx[1] = 0x12; idx[2] = 0x16; nrm[0] = 0x0C; nrm[1] = 0x10; nrm[2] = 0x14; break;
                case 0x3Cu: corners = 4; gouraud = true; idx[0] = 0x12; idx[1] = 0x16; idx[2] = 0x1A; idx[3] = 0x1E; nrm[0] = 0x10; nrm[1] = 0x14; nrm[2] = 0x18; nrm[3] = 0x1C; break;
                default: continue;
            }
            bool semi = (cmd & 2u) != 0;
            if (semi) mesh.Blended = true;

            for (int k = 0; k < corners; k++)
            {
                uint off = mem.ReadU16(f + idx[k]);
                if ((off & 7u) != 0) return null;
                vi[k] = off >> 3;
                maxV = Math.Max(maxV, (int)vi[k]);
            }
            for (int k = 0; k < (gouraud ? corners : 1); k++)
            {
                uint no = mem.ReadU16(f + nrm[k]);
                nlo = Math.Min(nlo, no); nhi = Math.Max(nhi, no + 8u);
            }
            uv[0] = mem.ReadU16(f);
            uv[1] = mem.ReadU16(f + 4u);
            uv[2] = mem.ReadU16(f + 8u);
            uv[3] = corners == 4 ? mem.ReadU16(f + 0xCu) : 0u;
            uint clut = mem.ReadU16(f + 2u), tpage = mem.ReadU16(f + 6u) & 0x1FFu;
            int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
            for (int k = 0; k < corners; k++)
            {
                int u = (int)(uv[k] & 0xFF), vv = (int)(uv[k] >> 8);
                u0 = Math.Min(u0, u); v0 = Math.Min(v0, vv); u1 = Math.Max(u1, u); v1 = Math.Max(v1, vv);
            }
            uint mode = (tpage >> 5) & 3u;
            bool water = waterOk && OnWaterRect(tpage, u0, v0, u1, v1);
            if (water) { mesh.WaterFaces++; if (semi) mesh.WaterSemiFaces++; }
            var t = new RetainedScene.Vertex
            {
                Clut = clut & 0x7FFF, Texpage = tpage,
                Dqa = vi[0], Dqb = vi[1], Curve = vi[2],
                Rgbc = corners == 4 ? vi[3] : uint.MaxValue,
                Rect = (uint)u0 | (uint)v0 << 8 | (uint)u1 << 16 | (uint)v1 << 24,
                Flags = RetainedScene.FlagRect | RetainedScene.FlagDots | (semi ? RetainedScene.FlagSemi | mode << 8 : 0u)
                        | (water ? RetainedScene.FlagWater : 0u),
            };
            var dst = semi ? _semiCorners : _corners;
            int at = semi ? ns : n;
            if (at + 6 > dst.Length)
            {
                Array.Resize(ref dst, dst.Length * 2);
                if (semi) _semiCorners = dst; else _corners = dst;
            }
            // Two triangles a quad, as the packets' are cut.
            ReadOnlySpan<int> order = corners == 4 ? [0, 1, 2, 1, 3, 2] : [0, 1, 2];
            mesh.FaceAt[i] = at;
            mesh.FaceN[i] = (byte)order.Length;
            mesh.FaceSemi[i] = semi;
            mesh.FaceMode[i] = (byte)mode;
            if (semi) { mesh.SemiFaces++; if (mode == 2) mesh.SubtractiveFaces++; }
            for (int j = 0; j < order.Length; j++)
            {
                int k = order[j];
                var c = t;
                uint q = normals + mem.ReadU16(f + nrm[gouraud ? k : 0]);
                c.X = vi[k];
                c.R = (short)mem.ReadU16(q); c.G = (short)mem.ReadU16(q + 2u); c.B = (short)mem.ReadU16(q + 4u);
                c.U = uv[k] & 0xFF; c.V = uv[k] >> 8;
                if (j >= 3) c.Flags |= RetainedScene.FlagQuadTail;
                dst[at + j] = c;
            }
            if (semi) ns += order.Length; else n += order.Length;
        }
        mesh.FaceBytes = face - start;
        if (!InRam(start, mesh.FaceBytes)) return null;
        if (nhi > nlo)
        {
            mesh.NormalLo = nlo & ~3u;
            mesh.NormalHi = (nhi + 3u) & ~3u;
            if (!InRam(normals + mesh.NormalLo, mesh.NormalHi - mesh.NormalLo)) return null;
            mesh.NormalHash = Hash(mem.Ram, normals + mesh.NormalLo, mesh.NormalHi - mesh.NormalLo);
        }
        mesh.FaceHash = Hash(mem.Ram, start, mesh.FaceBytes);
        mesh.MaxVertex = maxV;
        mesh.Count = n;
        mesh.SemiCount = ns;

        // The opaque corners, then the blended ones, in one run of the store.
        if (n + ns > 0)
        {
            if (_both.Length < n + ns) _both = new RetainedScene.Vertex[Math.Max(n + ns, _both.Length * 2)];
            Array.Copy(_corners, _both, n);
            Array.Copy(_semiCorners, 0, _both, n, ns);
            mesh.Start = RetainedScene.AddMesh(_both.AsSpan(0, n + ns));
            for (int i = 0; i < mesh.FaceAt.Length; i++)
                if (mesh.FaceAt[i] >= 0) mesh.FaceAt[i] += mesh.Start + (mesh.FaceSemi[i] ? n : 0);
        }
        return mesh;
    }

    static RetainedScene.Vertex[] _semiCorners = new RetainedScene.Vertex[1024], _both = new RetainedScene.Vertex[2048];

    /// <summary>Whether a face's texels lie on one of the water's rects, whatever its
    /// blend: the surface pass takes it as water in an averaging blend, as
    /// <c>SurfaceMaterial.Classify</c> takes a packet.</summary>
    static bool OnWaterRect(uint tpage, int u0, int v0, int u1, int v1)
    {
        int mode = (int)(tpage >> 7) & 3;
        int div = mode == 0 ? 4 : mode == 1 ? 2 : 1;
        int x0 = (int)(tpage & 0xF) * 64 + u0 / div, x1 = (int)(tpage & 0xF) * 64 + u1 / div + 1;
        int y0 = (int)((tpage >> 4) & 1) * 256 + v0, y1 = (int)((tpage >> 4) & 1) * 256 + v1 + 1;
        for (int i = 0; i < SurfaceMaterial.RectN; i++)
        {
            ref var r = ref SurfaceMaterial.Rects[i];
            if (x0 >= r.X + r.W || x1 <= r.X || y0 >= r.Y + r.H || y1 <= r.Y) continue;
            if (r.Material == SurfaceMaterial.Water) return true;
        }
        return false;
    }

    // The water's rects the cached meshes were classified against.
    static ulong _rectKey;

    static ulong RectKey()
    {
        ulong h = (ulong)SurfaceMaterial.RectN * 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < SurfaceMaterial.RectN; i++)
        {
            ref var r = ref SurfaceMaterial.Rects[i];
            h = (h ^ (uint)(r.X | r.Y << 16)) * 0x100000001B3UL;
            h = (h ^ (uint)(r.W | r.H << 16 | r.Material << 28)) * 0x100000001B3UL;
        }
        return h;
    }

    internal static ulong Hash(ReadOnlySpan<byte> ram, uint a, uint bytes)
    {
        var s = ram.Slice((int)(a & (Runtime.RamSize - 1)), (int)bytes);
        var w = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(s);
        ulong h = 0x9E3779B97F4A7C15UL ^ bytes;
        foreach (ulong x in w) h = (System.Numerics.BitOperations.RotateLeft(h ^ x, 27) + 0x632BE59BD9B4E019UL) * 0x9E3779B97F4A7C15UL;
        for (int i = w.Length * 8; i < s.Length; i++) h = (h ^ s[i]) * 0x100000001B3UL;
        return h;
    }

    /// <summary>Of <see cref="MainFaces"/>, the lit assembler's with a corner nearer than
    /// the GTE's divide can place (H/2).</summary>
    public static long MainSaturated;

    static void PutMain(RetainedScene.Vertex[] dst, ref int n, in RetainedScene.Vertex t, Span<float> x, Span<float> y, Span<float> z,
                        Span<uint> uv, Span<float> r, Span<float> g, Span<float> b, int k)
    {
        var v = t;
        v.X = x[k]; v.Y = y[k]; v.Z = z[k];
        v.U = uv[k] & 0xFF; v.V = uv[k] >> 8;
        v.R = r[k]; v.G = g[k]; v.B = b[k];
        dst[n++] = v;
    }

    /// <summary>One model the object walk hands the tile assemblers
    /// (`func_80030540`, `func_8002FECC`): the map's face format, lit flat per face
    /// by whatever light matrix the submitter set. <paramref name="twoCurves"/> is
    /// the clipped assembler's transform, whose fog has no offset curve.</summary>
    public static void CaptureFlat(PSMemory mem, uint header, uint normals, uint face, uint count, bool twoCurves)
    {
        var frame = RetainedScene.Find(RetainedScene.Serial);
        if (frame == null || count > 4096) return;
        var v = frame.View;
        var xf = Transform.Read(v);
        float dqa = (short)Gte.ReadControl(27), dqb = (int)Gte.ReadControl(28);
        int mode = (int)mem.ReadU32(FogMode);
        float curve = mode >= 32000 ? 0f : twoCurves ? 2f : (mode & 0x8000) != 0 ? 1f : 2f;
        uint noShadow = ModelWalk.SubmitKind is ModelKind.Effect or ModelKind.Sprite ? RetainedScene.FlagNoShadow : 0u;
        uint rgbc = mem.ReadU32(LightColour);
        uint verts = mem.ReadU32(VertexBase);
        byte mat = PolyAssembler.TileMaterial;

        int n = 0;
        Span<uint> uv = stackalloc uint[4], col = stackalloc uint[4];
        Span<float> wx = stackalloc float[4], wy = stackalloc float[4], wz = stackalloc float[4];
        for (uint i = 0; i < count; i++)
        {
            uint word = mem.ReadU32(face);
            uint f = face + 4u;
            uint cmd = word >> 24;
            uint type = cmd & 0xFDu;
            face = f + ((word >> 6) & 0x3FCu);
            int corners = type == 0x2Cu ? 4 : type == 0x24u ? 3 : 0;
            if (corners == 0) continue;
            uint idx = f + (corners == 4 ? 0x12u : 0x0Eu);
            for (int k = 0; k < corners; k++)
            {
                uint off = mem.ReadU16(idx + (uint)k * 2u);
                uint p = verts + off;
                xf.World(v, (short)mem.ReadU16(p), (short)mem.ReadU16(p + 2u), (short)mem.ReadU16(p + 4u),
                         out wx[k], out wy[k], out wz[k]);
                if (RetainedMap.Checking) RetainedMap.CheckCorner(v, wx[k], wy[k], wz[k], mem.ReadU32(VertexCache + off));
            }
            uint lit = RetainedMap.ReflectionsReady ? Light(mem, normals + mem.ReadU16(f + (corners == 4 ? 0x10u : 0x0Cu)), rgbc) : 0u;
            for (int k = 0; k < corners; k++) col[k] = lit;
            uv[0] = mem.ReadU16(f);
            uv[1] = mem.ReadU16(f + 4u);
            uv[2] = mem.ReadU16(f + 8u);
            uv[3] = corners == 4 ? mem.ReadU16(f + 0xCu) : 0u;
            uint clut = mem.ReadU16(f + 2u), tpage = mem.ReadU16(f + 6u) & 0x1FFu;
            bool semi = (cmd & 2u) != 0;
            int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
            for (int k = 0; k < corners; k++)
            {
                int u = (int)(uv[k] & 0xFF), vv = (int)(uv[k] >> 8);
                u0 = Math.Min(u0, u); v0 = Math.Min(v0, vv); u1 = Math.Max(u1, u); v1 = Math.Max(v1, vv);
            }
            var t = new RetainedScene.Vertex
            {
                Clut = clut & 0x7FFF, Texpage = tpage, Dqa = dqa, Dqb = dqb, Curve = curve,
                Rect = (uint)u0 | (uint)v0 << 8 | (uint)u1 << 16 | (uint)v1 << 24,
                Flags = RetainedScene.FlagRect | mat | noShadow | (semi ? RetainedScene.FlagSemi | ((tpage >> 5) & 3u) << 8 : 0u),
                Rgbc = RetainedMap.ReflectionsReady ? rgbc & 0xFFFFFFu : 0u,
            };
            if (n + 6 > _tris.Length) Array.Resize(ref _tris, _tris.Length * 2);
            Put(ref n, t, wx, wy, wz, uv, col, 0); Put(ref n, t, wx, wy, wz, uv, col, 1); Put(ref n, t, wx, wy, wz, uv, col, 2);
            if (corners == 4) { Put(ref n, t, wx, wy, wz, uv, col, 1); Put(ref n, t, wx, wy, wz, uv, col, 3); Put(ref n, t, wx, wy, wz, uv, col, 2); }
            Faces++;
        }
        RetainedScene.AddDynamic(_tris.AsSpan(0, n));
        Models++;
        ByKind[(int)ModelWalk.SubmitKind]++;
        if (Remaster.Props.NameOf(ModelWalk.SubmitRecord) != null) Props++;
    }

    /// <summary>A model corner to world space. A model the walk placed in the world
    /// is its own rotation about its record's position, which does not move when the
    /// camera does; anything else goes back through the camera, whose 1/4096 rotation
    /// moves the result by up to a unit or two as it turns.</summary>
    readonly struct Transform
    {
        readonly bool _placed;
        readonly double _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22, _tx, _ty, _tz;

        Transform(bool placed, double m00, double m01, double m02, double m10, double m11, double m12,
                  double m20, double m21, double m22, double tx, double ty, double tz)
        {
            _placed = placed;
            _m00 = m00; _m01 = m01; _m02 = m02; _m10 = m10; _m11 = m11; _m12 = m12;
            _m20 = m20; _m21 = m21; _m22 = m22; _tx = tx; _ty = ty; _tz = tz;
        }

        public static Transform Read(in RetainedScene.View v)
        {
            if (ModelWalk.Placed)
            {
                var r = ModelWalk.PlacedRot;
                Placed++;
                return new Transform(true, r[0] / 4096.0, r[1] / 4096.0, r[2] / 4096.0, r[3] / 4096.0, r[4] / 4096.0,
                                     r[5] / 4096.0, r[6] / 4096.0, r[7] / 4096.0, r[8] / 4096.0,
                                     ModelWalk.PlacedX, ModelWalk.PlacedY, ModelWalk.PlacedZ);
            }
            // The model's view transform, as the GTE holds it.
            uint r0 = Gte.ReadControl(0), r1 = Gte.ReadControl(1), r2 = Gte.ReadControl(2), r3 = Gte.ReadControl(3);
            return new Transform(false, (short)r0 / 4096.0, (short)(r0 >> 16) / 4096.0, (short)r1 / 4096.0,
                                 (short)(r1 >> 16) / 4096.0, (short)r2 / 4096.0, (short)(r2 >> 16) / 4096.0,
                                 (short)r3 / 4096.0, (short)(r3 >> 16) / 4096.0, (short)Gte.ReadControl(4) / 4096.0,
                                 (int)Gte.ReadControl(5), (int)Gte.ReadControl(6), (int)Gte.ReadControl(7));
        }

        /// <summary>The same as one rotation and translation into the world: nine
        /// entries row by row, then the translation.</summary>
        public void ToWorld(in RetainedScene.View v, float[] o)
        {
            if (_placed)
            {
                o[0] = (float)_m00; o[1] = (float)_m01; o[2] = (float)_m02;
                o[3] = (float)_m10; o[4] = (float)_m11; o[5] = (float)_m12;
                o[6] = (float)_m20; o[7] = (float)_m21; o[8] = (float)_m22;
                o[9] = (float)_tx; o[10] = (float)_ty; o[11] = (float)_tz;
                return;
            }
            // world = R^T (M p + t - T) + cam
            double tx = _tx - v.Tx, ty = _ty - v.Ty, tz = _tz - v.Tz;
            o[0] = (float)(v.R00 * _m00 + v.R10 * _m10 + v.R20 * _m20);
            o[1] = (float)(v.R00 * _m01 + v.R10 * _m11 + v.R20 * _m21);
            o[2] = (float)(v.R00 * _m02 + v.R10 * _m12 + v.R20 * _m22);
            o[3] = (float)(v.R01 * _m00 + v.R11 * _m10 + v.R21 * _m20);
            o[4] = (float)(v.R01 * _m01 + v.R11 * _m11 + v.R21 * _m21);
            o[5] = (float)(v.R01 * _m02 + v.R11 * _m12 + v.R21 * _m22);
            o[6] = (float)(v.R02 * _m00 + v.R12 * _m10 + v.R22 * _m20);
            o[7] = (float)(v.R02 * _m01 + v.R12 * _m11 + v.R22 * _m21);
            o[8] = (float)(v.R02 * _m02 + v.R12 * _m12 + v.R22 * _m22);
            o[9] = (float)(v.R00 * tx + v.R10 * ty + v.R20 * tz + v.CamX);
            o[10] = (float)(v.R01 * tx + v.R11 * ty + v.R21 * tz + v.CamY);
            o[11] = (float)(v.R02 * tx + v.R12 * ty + v.R22 * tz + v.CamZ);
        }

        public void World(in RetainedScene.View v, double x, double y, double z, out float wx, out float wy, out float wz)
        {
            double px = _m00 * x + _m01 * y + _m02 * z + _tx;
            double py = _m10 * x + _m11 * y + _m12 * z + _ty;
            double pz = _m20 * x + _m21 * y + _m22 * z + _tz;
            if (_placed) { wx = (float)px; wy = (float)py; wz = (float)pz; return; }
            px -= v.Tx; py -= v.Ty; pz -= v.Tz;
            // The frame's R is a rotation: its transpose takes view back to world.
            wx = (float)(v.R00 * px + v.R10 * py + v.R20 * pz + v.CamX);
            wy = (float)(v.R01 * px + v.R11 * py + v.R21 * pz + v.CamY);
            wz = (float)(v.R02 * px + v.R12 * py + v.R22 * pz + v.CamZ);
        }
    }

    /// <summary>Models placed from their record, of <see cref="Models"/>.</summary>
    public static long Placed;

    static void Put(ref int n, in RetainedScene.Vertex t, Span<float> x, Span<float> y, Span<float> z,
                    Span<uint> uv, Span<uint> col, int k)
    {
        var v = t;
        v.X = x[k]; v.Y = y[k]; v.Z = z[k];
        v.U = uv[k] & 0xFF; v.V = uv[k] >> 8;
        v.R = col[k] & 0xFF; v.G = (col[k] >> 8) & 0xFF; v.B = (col[k] >> 16) & 0xFF;
        _tris[n++] = v;
    }

    /// <summary>NormalColorCol: the assembler's NormalColorDpq without the cue.</summary>
    static uint Light(PSMemory mem, uint normal, uint rgbc)
    {
        Gte.Write(0, mem.ReadU32(normal));
        Gte.Write(1, mem.ReadU32(normal + 4u));
        Gte.Write(6, rgbc);
        Gte.NccsOp(12, true);
        return Gte.Read(22);
    }
}
