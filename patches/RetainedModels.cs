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
    public static bool MainCapturing => GpuWorld.ModelsActive && ModelWalk.InWalk && !PolyAssembler.Verifying
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
