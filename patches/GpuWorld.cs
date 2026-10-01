using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// The GPU world renderer, Step 1 (0085): the map's opaque faces drawn from the
/// retained scene's static mesh into the frame, instead of assembled by the game's
/// code every frame.
///
///     KF2_GPUWORLD=1         draw the map on the GPU (off: not judged); 0 never
///                            (unset, Video ▸ Frame pacing ▸ GPU geometry)
///     KF2_GPUWORLD_PROBE=1   a line every 2 s: draws, misses, halves skipped and kept,
///                            and the surface buffer read back against the frame's depth
///     KF2_GPUWORLD_SURFACES=0  leave the map out of the normal and surface buffers
///     KF2_GPUWORLD_MODELS=0  leave the object walk's models on the packets
///
/// The map is lit and fogged in the vertex shader from the area's light records, so
/// a record the game rewrites is an upload and not a rebuild (<see cref="RetainedMap.RecordsOn"/>;
/// <c>KF2_GPUWORLD_RECORDS=0</c> to compare, <c>KF2_GPUWORLD_RECORDCHECK=1</c> checks the
/// shader's formula against the CPU's on every corner).
///
/// The tile walk still decides: every half it visits is noted in the frame's gate
/// (<see cref="RetainedScene.NoteHalf"/>), and the backend draws exactly those halves
/// at the head of the ordering table's walk, past the sky. The half is then not
/// assembled at all. The map's water is drawn by the backend too, a slice of view
/// depth at each point of the table's walk where 0079 would have sent its packets, with
/// the swell and the ripples; with it off (<c>KF2_GPUWORLD_WATER=0</c>) a half with
/// semi-transparent faces keeps only those (<see cref="PolyAssembler.BlendedOnly"/>).
/// The mirrored walk
/// (<see cref="PlanarWalk"/>) hands its halves and its replayed models to the backend
/// the same way (<c>KF2_GPUWORLD_MIRROR=0</c> to compare), which draws them into the
/// planar texture as the capture's table walk reaches slot 1.
///
/// The object walk's models lit by the models' assembler are drawn by the backend too,
/// after the map, their opaque faces taken off the packets by <see cref="RetainedModels.CaptureMain"/>;
/// their blended faces, and the models the other assemblers draw, stay on the packets.
/// Those placed in the world are drawn from meshes kept on the GPU, an instance each
/// (<see cref="RetainedModels.TryInstance"/>; <c>KF2_GPUWORLD_MESHES=0</c> to compare,
/// <c>KF2_GPUWORLD_MESHCHECK=1</c> checks the shader's cull against the assembler's).
/// The first-person arm is drawn from its mesh too, in the game's painter's order
/// (<see cref="RetainedModels.TryArm"/>; <c>KF2_GPUWORLD_ARM=0</c> to compare), and so
/// is the sky, before the map (<see cref="RetainedModels.TrySpecial"/>; <c>KF2_GPUWORLD_SKY=0</c>),
/// and the objects near the camera that the clipped map assembler would draw (<c>KF2_GPUWORLD_TILE=0</c>).
/// The mirror's blended faces are drawn by the backend too (<c>KF2_GPUWORLD_MIRRORBLEND=0</c>), and a
/// model's blended faces reach the surface buffer as their packets did (<c>KF2_GPUWORLD_BLENDSURFACES=0</c>).
/// The cell walk notes a half the GPU draws whole without calling the half routine (<c>KF2_GPUWORLD_CELL=0</c>).
///
/// See "Step 1, the first slice", "Step 1, the second slice", "Step 2, the third slice", "Step 3, the first slice", "Step 3, the second slice",
/// "Step 3, the fourth slice", "Step 3, the fifth slice", "Step 3, the sixth slice", "Step 3, the seventh slice",
/// "Step 3, the eighth slice", "Step 3, the ninth slice", "Step 4, the fallback census" and "Step 5, the first slice"
/// in docs/GPU_RENDERER.md.
/// </summary>
public static class GpuWorld
{
    const uint ModelTable = 0x8018E19C;

    public const string OnKey = "kf2.gpuworld.on";

    static bool _on, _probe, _water = true, _models = true, _mirror = true;

    /// <summary>Whether this frame's planar walk hands its opaque map and models to the
    /// GPU too: the mirror drawn by the renderer from the mirrored camera.</summary>
    public static bool MirrorActive { get; private set; }

    /// <summary>Whether the mirror's replayed models are drawn on the GPU.</summary>
    public static bool MirrorModelsActive { get; private set; }

    /// <summary>Whether the mirror's water is drawn on the GPU (the water switch).</summary>
    public static bool MirrorWaterActive { get; private set; }

    /// <summary>Whether this frame's water is drawn on the GPU too: the switch, and
    /// 0079's reorder, whose barriers are where each slice goes.</summary>
    public static bool WaterActive { get; private set; }

    /// <summary>Whether this frame's lit models are drawn on the GPU (Step 3).</summary>
    public static bool ModelsActive { get; private set; }
    static bool? _forced;

    public static bool Enabled => _on;

    public static void Configure(string? on, string? probe, string? surfaces = null)
    {
        _water = Environment.GetEnvironmentVariable("KF2_GPUWORLD_WATER")?.Trim() != "0";
        _models = Environment.GetEnvironmentVariable("KF2_GPUWORLD_MODELS")?.Trim() != "0";
        _mirror = Environment.GetEnvironmentVariable("KF2_GPUWORLD_MIRROR")?.Trim() != "0";
        RetainedModels.MeshesOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_MESHES")?.Trim() != "0";
        RetainedModels.Checking = Environment.GetEnvironmentVariable("KF2_GPUWORLD_MESHCHECK")?.Trim() is "1";
        RetainedModels.PosesOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_POSES")?.Trim() != "0";
        RetainedModels.ArmOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_ARM")?.Trim() != "0";
        RetainedModels.BlendOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_BLEND")?.Trim() != "0";
        RetainedModels.SkyOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_SKY")?.Trim() != "0";
        RetainedModels.BlendSurfacesOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_BLENDSURFACES")?.Trim() != "0";
        RetainedModels.TileOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_TILE")?.Trim() != "0";
        RetainedModels.MirrorBlendOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_MIRRORBLEND")?.Trim() != "0";
        TileWalk.TakeInCell = Environment.GetEnvironmentVariable("KF2_GPUWORLD_CELL")?.Trim() != "0";
        RetainedMap.RecordsOn = Environment.GetEnvironmentVariable("KF2_GPUWORLD_RECORDS")?.Trim() != "0";
        RetainedMap.RecordCheck = Environment.GetEnvironmentVariable("KF2_GPUWORLD_RECORDCHECK")?.Trim() is "1";
        MoPose.Checking = Environment.GetEnvironmentVariable("KF2_GPUWORLD_POSECHECK")?.Trim() is "1";
        RetainedScene.MainSurfaces = surfaces?.Trim() != "0";
        if (float.TryParse(Environment.GetEnvironmentVariable("KF2_GPUWORLD_NEAR"), System.Globalization.CultureInfo.InvariantCulture, out float near))
            RetainedScene.MainNear = Math.Max(near, 0.01f);
        if (Environment.GetEnvironmentVariable("KF2_GPUWORLD_FOGZ")?.Trim() == "0") RetainedScene.MainFogFromZ = false;
        if (!string.IsNullOrWhiteSpace(on)) _forced = on.Trim() is "1" or "on";
        _probe = probe?.Trim() is not (null or "" or "0");
    }

    public static void Install()
    {
        _on = _forced ?? false;
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            _on = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, false);
            Console.WriteLine($"[KF2] gpu world: {(_on ? "on (the map drawn from the retained scene)" : "off")}" +
                              (_on && Blocker is { } why ? $", standing down: {why}" : ""));
        });
        // A model bank of the next area may put other meshes at the same addresses.
        Event.AddListener<OverlayLoadedEvent>(_ => RetainedModels.ForgetMeshes());
    }

    public static void SetEnabled(bool on)
    {
        _on = on;
        if (on) return;
        Active = false;
        ModelsActive = false;
        MirrorActive = MirrorModelsActive = MirrorWaterActive = false;
        RetainedScene.MainView = false;
        RetainedScene.MainSerial = 0;
    }

    /// <summary>Why the map cannot be drawn on the GPU with the settings as they are,
    /// or null. The renderer draws with perspective-correct textures only, and needs the
    /// depth buffer and the C# assemblers; each of these is a setting the player sees.
    /// It has no texture replacement (0073), so a loaded texture pack stands it down.</summary>
    public static string? Blocker =>
        !RetainedScene.Supported ? "needs the OpenGL core renderer"
        : !PolyAssembler.FastGeometry ? "needs Fast geometry"
        : !GteDepth.ZBuffer ? "needs the Z-buffer"
        : !Perspective.Enabled ? "needs perspective-correct textures"
        : RecompOne.Runtime.Assets.Textures.TextureResolver.Enabled && RecompOne.Runtime.Assets.AssetReplacerManager.Instance.HasTextures
            ? "a texture pack is loaded, and the GPU map does not replace textures"
        : null;

    /// <summary>Whether the map is wanted on the GPU: the retained map is built for it.</summary>
    public static bool Wanted => _on && RetainedScene.Supported;

    /// <summary>Whether this frame's walk hands its halves to the GPU. Everything it
    /// depends on is the C# walk and assemblers, the depth buffer, and a built map;
    /// any comparison that runs the recompiled routines stands it down.</summary>
    public static bool Active { get; private set; }

    /// <summary>From <see cref="RetainedMap.AtWalk"/>, once the frame has begun.</summary>
    public static void AtFrame()
    {
        Active = ActiveNow;
        RetainedScene.MainView = Active;
        RetainedScene.MainSerial = Active ? RetainedScene.Serial : 0;
        WaterActive = Active && _water && BlendOrder.Active;
        ModelsActive = ModelsNow(Active);
        RetainedScene.MainWater = WaterActive;
        MirrorActive = Active && _mirror && PlanarReflections.Enabled;
        MirrorModelsActive = MirrorActive && ModelsActive;
        MirrorWaterActive = MirrorActive && _water;
        RetainedScene.MirrorWater = MirrorWaterActive;
        RetainedModels.AtFrame();
        if (_probe)
        {
            // BK and LCM generations the last frame started (GteLightMap keeps eight).
            long g = GteLightMap.Generations - _gensAt;
            _gensAt = GteLightMap.Generations;
            _gensMax = Math.Max(_gensMax, g);
            Report();
        }
    }

    static bool ActiveNow => Wanted && Blocker == null && RetainedScene.StaticCount[0] > 0
        && TileWalk.Enabled && TileWalk.CellEnabled && TileWalk.TileEnabled && !TileWalk.Verifying
        && PolyAssembler.Enabled && PolyAssembler.UnclippedEnabled && !PolyAssembler.Verifying
        && !RecompOne.Runtime.Pgxp.Pgxp.CpuTracking;

    // The models' packets carry per-pixel lighting records; the GPU draws them only
    // in that mode, and only from the C# walk, submitter and lit assembler.
    static bool ModelsNow(bool active) => active && _models && GteLightMap.Active
        && ModelWalk.Enabled && ModelWalk.WalkEnabled && ModelWalk.SubmitEnabled && !ModelWalk.Verifying
        && PolyAssembler.LitEnabled;

    /// <summary>Whether the models will be drawn on the GPU in the frame about to begin,
    /// by the switches as they are now: the arm is drawn before the frame begins, and a
    /// switch thrown between the two must not leave it with neither path.</summary>
    public static bool ModelsReady => ModelsNow(ActiveNow);

    // ---- which halves still need the game's assembler -------------------------------

    public enum Faces { Opaque, Blended, Subtractive }

    static readonly Dictionary<uint, Faces> _blended = new();
    static int _blendedGen = -1;
    static uint _blendedTable;

    /// <summary>Whether the model's mesh has a semi-transparent face, and whether one
    /// of them subtracts.</summary>
    public static Faces KindOf(PSMemory mem, uint model)
    {
        uint table = mem.ReadU32(ModelTable);
        if (_blendedGen != RetainedScene.StaticGeneration || _blendedTable != table)
        {
            _blended.Clear();
            _blendedGen = RetainedScene.StaticGeneration;
            _blendedTable = table;
        }
        uint header = table + model * 28u + 0xCu;
        if (_blended.TryGetValue(header, out var kind)) return kind;
        uint face = table + mem.ReadU32(header + 0x10u) + 0xCu;
        uint count = Math.Min(mem.ReadU32(header + 0x14u), 4096u);
        for (uint i = 0; i < count && kind != Faces.Subtractive; i++)
        {
            uint word = mem.ReadU32(face);
            uint type = (word >> 24) & 0xFDu;
            if ((type == 0x2Cu || type == 0x24u) && ((word >> 24) & 2u) != 0u)
                kind = ((mem.ReadU16(face + 4u + 6u) >> 5) & 3u) == 2u ? Faces.Subtractive : Faces.Blended;
            face += 4u + ((word >> 6) & 0x3FCu);
        }
        _blended[header] = kind;
        return kind;
    }

    /// <summary>The probe's: halves left to the GPU whole, and those whose water was
    /// still assembled.</summary>
    public static long Skipped, Kept;

    /// <summary>The same for the mirrored walk.</summary>
    public static long MirrorSkipped, MirrorKept;

    /// <summary>The `gpuworld` shell verb: the state, or the switch.</summary>
    public static string Shell(string arg)
    {
        var word = arg.Trim().ToLowerInvariant();
        if (word.StartsWith("blend only ") && int.TryParse(word.AsSpan(11), out int only)) { RetainedScene.BlendOnly = only; word = ""; }
        if (word.StartsWith("at ")) return At(word[3..]);
        switch (word)
        {
            case "": break;
            case "on": SetEnabled(true); break;
            case "off": SetEnabled(false); break;
            case "surfaces on": RetainedScene.MainSurfaces = true; break;
            case "surfaces off": RetainedScene.MainSurfaces = false; break;
            case "water on": _water = true; break;
            case "water off": _water = false; break;
            case "models on": _models = true; break;
            case "models off": _models = false; break;
            case "mirror on": _mirror = true; break;
            case "mirror off": _mirror = false; break;
            case "mirror hide": RetainedScene.MirrorShown = false; break;
            case "mirror show": RetainedScene.MirrorShown = true; break;
            case "meshes on": RetainedModels.MeshesOn = true; break;
            case "meshes off": RetainedModels.MeshesOn = false; break;
            case "poses on": RetainedModels.PosesOn = true; break;
            case "poses off": RetainedModels.PosesOn = false; break;
            case "records on": RetainedMap.RecordsOn = true; break;
            case "records off": RetainedMap.RecordsOn = false; break;
            case "blend on": RetainedModels.BlendOn = true; break;
            case "blend off": RetainedModels.BlendOn = false; break;
            case "blend lit": RetainedModels.BlendRoutes = 1; break;
            case "blend twin": RetainedModels.BlendRoutes = 2; break;
            case "blend both": RetainedModels.BlendRoutes = 3; break;
            case "blend depth on": RetainedScene.BlendDepth = true; break;
            case "blend depth off": RetainedScene.BlendDepth = false; break;
            case "blend all": RetainedScene.BlendOnly = -1; break;
            case "blend hide": RetainedScene.BlendShown = false; break;
            case "blend show": RetainedScene.BlendShown = true; break;
            case "sky on": RetainedModels.SkyOn = true; break;
            case "sky off": RetainedModels.SkyOn = false; break;
            case "sky hide": RetainedScene.SkyShown = false; break;
            case "sky show": RetainedScene.SkyShown = true; break;
            case "blend surfaces on": RetainedModels.BlendSurfacesOn = true; break;
            case "blend surfaces off": RetainedModels.BlendSurfacesOn = false; break;
            case "tile on": RetainedModels.TileOn = true; break;
            case "tile off": RetainedModels.TileOn = false; break;
            case "mirror blend on": RetainedModels.MirrorBlendOn = true; break;
            case "mirror blend off": RetainedModels.MirrorBlendOn = false; break;
            case "cell on": TileWalk.TakeInCell = true; break;
            case "cell off": TileWalk.TakeInCell = false; break;
            case "arm on": RetainedModels.ArmOn = true; break;
            case "arm off": RetainedModels.ArmOn = false; break;
            case "models hide": RetainedScene.MainModelsShown = false; break;
            case "models show": RetainedScene.MainModelsShown = true; break;
            case "perpixel on": GteLightMap.Enabled = true; break;
            case "perpixel off": GteLightMap.Enabled = false; break;
            case "scene":
            {
                // The last walk's submits, and the frame's forward in world axes (row 2 of R).
                var v = RetainedScene.Find(RetainedScene.Serial)?.View ?? default;
                var items = string.Join(",", ModelWalk.Scene.ToArray().Select(m =>
                    $"{{\"kind\":\"{m.Kind}\",\"model\":{m.Model},\"asm\":{m.Assembler},\"pos\":[{m.X},{m.Y},{m.Z}]}}"));
                return $"{{\"ok\":true,\"forward\":[{v.R20:F3},{v.R21:F3},{v.R22:F3}],\"cam\":[{v.CamX},{v.CamY},{v.CamZ}],\"models\":[{items}]}}";
            }
            case "blended":
            {
                // The model bank's models a prop may use that have a blended face.
                if (RecompOne.Runtime.Runtime.Mem is not PSMemory mem) return "{\"ok\":false,\"error\":\"not running\"}";
                var ids = new List<string>();
                for (uint id = 0; id < 1024; id++)
                {
                    // An id past the bank's end reads garbage, which may leave RAM.
                    try
                    {
                        if (Remaster.Props.Unusable(mem, (int)id) != null) continue;
                        var kind = KindOf(mem, id);
                        if (kind != Faces.Opaque) ids.Add($"{id}{(kind == Faces.Subtractive ? "s" : "")}");
                    }
                    catch (Exception) { }
                }
                return $"{{\"ok\":true,\"blended\":\"{string.Join(" ", ids)}\"}}";
            }
            case "instances":
            {
                // The last frame's instances: mesh range, placement, cue, the far test and the light.
                var f = RetainedScene.Find(RetainedScene.Serial);
                if (f == null) return "{\"ok\":false,\"error\":\"no frame\"}";
                string One(RetainedScene.ModelInstance m) =>
                    $"{{\"mesh\":[{m.MeshStart},{m.MeshCount}],\"verts\":{m.VertBase},\"t\":[{m.Tx:F0},{m.Ty:F0},{m.Tz:F0}]," +
                    $"\"cue\":[{m.Dqa},{m.Dqb},{m.Curve}],\"range\":[{m.Near},{m.Far}],\"rgbc\":\"{m.Rgbc:x6}\",\"mat\":{m.Material}," +
                    $"\"bk\":[{m.Bk0},{m.Bk1},{m.Bk2}],\"mirrored\":{(m.Mirrored ? "true" : "false")}}}";
                return $"{{\"ok\":true,\"main\":[{string.Join(",", f.Instances.Select(One))}],\"mirror\":[{string.Join(",", f.MirrorInstances.Select(One))}]}}";
            }
            default: return "{\"ok\":false,\"error\":\"gpuworld [on|off|surfaces on|off|water on|off|models on|off|hide|show|meshes on|off|poses on|off|tile on|off|cell on|off|arm on|off|sky on|off|hide|show|blend on|off|hide|show|blend surfaces on|off|records on|off|mirror on|off|hide|show|mirror blend on|off|scene|instances|perpixel on|off]\"}";
        }
        return $"{{\"ok\":true,\"on\":{(_on ? "true" : "false")},\"active\":{(Active ? "true" : "false")}," +
               $"\"surfaces\":{(RetainedScene.MainSurfaces ? "true" : "false")},\"water\":{(_water ? "true" : "false")}," +
               $"\"records\":{(RetainedMap.RecordsOn ? "true" : "false")},\"models\":{(_models ? "true" : "false")},\"meshes\":{(RetainedModels.MeshesOn ? "true" : "false")},\"poses\":{(RetainedModels.PosesOn ? "true" : "false")},\"arm\":{(RetainedModels.ArmOn ? "true" : "false")},\"tile\":{(RetainedModels.TileOn ? "true" : "false")},\"mirrorBlend\":{(RetainedModels.MirrorBlendOn ? "true" : "false")},\"sky\":{(RetainedModels.SkyOn ? "true" : "false")},\"blend\":{(RetainedModels.BlendOn ? "true" : "false")},\"mirror\":{(_mirror ? "true" : "false")}," +
               $"\"draws\":{RetainedScene.MainDraws},\"missed\":{RetainedScene.MainMissed}}}";
    }

    /// <summary>`gpuworld at X Y`: the static map triangles that cover game pixel (X, Y)
    /// in the last frame's view (X in the 320-wide picture, the margin left of 0), each
    /// with its range, half, the frame's gate on it, its view depth and its table key.</summary>
    static string At(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out float px)
            || !float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float py))
            return "{\"ok\":false,\"error\":\"gpuworld at X Y\"}";
        var f = RetainedScene.Find(RetainedScene.Serial - 1) ?? RetainedScene.Find(RetainedScene.Serial);
        if (f == null) return "{\"ok\":false,\"error\":\"no frame\"}";
        var v = f.View;
        var st = RetainedScene.Static;
        var hits = new List<string>();
        Span<float> sx = stackalloc float[3], sy = stackalloc float[3], sz = stackalloc float[3];
        for (int r = 0; r < 5; r++)
            for (int i = RetainedScene.StaticStart[r]; i + 2 < RetainedScene.StaticStart[r] + RetainedScene.StaticCount[r]; i += 3)
            {
                bool ok = true;
                for (int k = 0; k < 3 && ok; k++)
                {
                    ref readonly var c = ref st[i + k];
                    double dx = c.X - v.CamX, dy = c.Y - v.CamY, dz = c.Z - v.CamZ;
                    float x = (float)(v.R00 * dx + v.R01 * dy + v.R02 * dz) + v.Tx;
                    float y = (float)(v.R10 * dx + v.R11 * dy + v.R12 * dz) + v.Ty;
                    float z = (float)(v.R20 * dx + v.R21 * dy + v.R22 * dz) + v.Tz;
                    if (z < 1f) { ok = false; break; }
                    sx[k] = v.Cx + v.H * x / z; sy[k] = v.Cy + v.H * y / z; sz[k] = z;
                }
                if (!ok) continue;
                float d1 = (px - sx[1]) * (sy[0] - sy[1]) - (sx[0] - sx[1]) * (py - sy[1]);
                float d2 = (px - sx[2]) * (sy[1] - sy[2]) - (sx[1] - sx[2]) * (py - sy[2]);
                float d3 = (px - sx[0]) * (sy[2] - sy[0]) - (sx[2] - sx[0]) * (py - sy[0]);
                if ((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0)) continue;
                uint hid = (st[i].Flags >> RetainedScene.HalfShift) & RetainedScene.HalfBits;
                int h = (int)hid - 1;
                float area = (sx[1] - sx[0]) * (sy[2] - sy[0]) - (sy[1] - sy[0]) * (sx[2] - sx[0]);
                float zm = (sz[0] + sz[1] + sz[2]) / 3f;
                hits.Add($"{{\"range\":{r},\"tri\":{i},\"half\":[{(h >= 0 ? h / 2 % 80 : -1)},{(h >= 0 ? h / 160 : -1)},{(h >= 0 ? h & 1 : -1)}]," +
                         $"\"gate\":{(h >= 0 ? f.MainHalves[h] : -1)},\"z\":{zm:F0},\"key\":{(int)(zm / 4f) + 0xF0},\"area\":{area:F1}," +
                         $"\"flags\":\"{st[i].Flags:X8}\",\"rgb\":[{st[i].R:F0},{st[i].G:F0},{st[i].B:F0}]}}");
            }
        return $"{{\"ok\":true,\"serial\":{f.Serial},\"hits\":[{string.Join(",", hits)}]}}";
    }

    // ---- the probe -----------------------------------------------------------------

    static double _reportAt;
    static long _draws, _missed, _tris, _uploads, _nrmTris, _wSlices, _wEmpty, _wTris, _wNoted, _wSorted, _wDeferred;
    static long _gensMax, _gensAt, _builds, _packs, _recUploads;
    static long _mDraws, _mMissed, _mStatic, _mModelTris, _mMirModels, _mWater;
    static long _iTile, _bMirNoted, _bMirDrawn;
    static long _iIns, _iWhole, _iMir, _iDrawn, _iCorners, _iVerts, _iMirDrawn, _iArm, _iArmDrawn, _iArmCalls;
    static long _bNoted, _bSorted, _bDrawn, _bRuns, _sIns, _sDrawn, _sFaces, _wRuns;
    static long _pPosed, _pRigid, _pDeferred, _pMat, _pTexels;
    static long _mModels, _mFaces, _mCulled, _mOut, _mTris, _mGroups, _mNrm, _mTile, _mClip, _mSat;

    static void Report()
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now < _reportAt) return;
        _reportAt = now + 2.0;
        long d = RetainedScene.MainDraws - _draws, m = RetainedScene.MainMissed - _missed, t = RetainedScene.MainTriangles - _tris;
        _draws = RetainedScene.MainDraws; _missed = RetainedScene.MainMissed; _tris = RetainedScene.MainTriangles;
        Console.WriteLine($"[KF2] gpu world: {(Active ? "active" : "standing down")}; {d} draw(s), {m} walk(s) missed, " +
                          $"{(d == 0 ? 0 : t / d)} static triangle(s) a draw; halves {Skipped} left whole to the GPU, " +
                          $"{Kept} with their blended faces assembled");
        long ws = RetainedScene.MainWaterSlices - _wSlices, we = RetainedScene.MainWaterEmpty - _wEmpty;
        long wt = RetainedScene.MainWaterTriangles - _wTris, wn = RetainedScene.MainWaterNoted - _wNoted;
        _wSlices = RetainedScene.MainWaterSlices; _wEmpty = RetainedScene.MainWaterEmpty;
        _wTris = RetainedScene.MainWaterTriangles; _wNoted = RetainedScene.MainWaterNoted;
        long wso = RetainedScene.MainWaterSorted - _wSorted, wd = RetainedScene.MainWaterDeferred - _wDeferred;
        _wSorted = RetainedScene.MainWaterSorted; _wDeferred = RetainedScene.MainWaterDeferred;
        long wr = RetainedScene.MainWaterRuns - _wRuns;
        _wRuns = RetainedScene.MainWaterRuns;
        double sortMs = d == 0 ? 0 : RetainedScene.MainWaterSortTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / d;
        RetainedScene.MainWaterSortTicks = 0;
        Console.WriteLine($"[KF2] gpu world: water {(WaterActive ? "on the GPU" : "on the packets")}; " +
                          $"{(d == 0 ? 0 : (double)ws / d):F2} slice(s) a draw in {(d == 0 ? 0 : (double)wr / d):F1} run(s), {(d == 0 ? 0 : (double)we / d):F2} empty, {(d == 0 ? 0 : (double)wd / d):F2} waited, " +
                          $"{(ws == 0 ? 0 : wt / ws)} blended triangle(s) a slice, {(d == 0 ? 0 : wso / d)} sorted a draw in {sortMs:F3} ms, " +
                          $"{(d == 0 ? 0 : wn / d)} noted for the plane a draw; " +
                          $"static ranges {RetainedScene.StaticCount[0] / 3}/{RetainedScene.StaticCount[1] / 3}/{RetainedScene.StaticCount[2] / 3}/{RetainedScene.StaticCount[3] / 3}/{RetainedScene.StaticCount[4] / 3}");
        double ms2(int i) => d == 0 ? 0 : RetainedScene.MipTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / d;
        double ms(int i) => d == 0 ? 0 : RetainedScene.MainTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / d;
        Console.WriteLine($"[KF2] gpu world: CPU ms a draw: shadows+static {ms(0):F3}, mips {ms(1):F3}, setup {ms(2):F3}, draw {ms(3):F3}; mip table uploads {RetainedScene.MipTableUploads - _uploads}, keys {RetainedScene.MipsKeys} ({RetainedScene.MipsFound} found), lookups {ms2(0):F3}, decode {ms2(1):F3}");
        Array.Clear(RetainedScene.MipTicks);
        _uploads = RetainedScene.MipTableUploads;
        Array.Clear(RetainedScene.MainTicks);
        long mm = RetainedModels.MainModels - _mModels, mf = RetainedModels.MainFaces - _mFaces;
        long mc = RetainedModels.MainCulled - _mCulled, mo = RetainedModels.MainOutOfTable - _mOut;
        long mt = RetainedScene.MainModelTriangles - _mTris, mg = RetainedScene.MainModelGroups - _mGroups;
        long mn = RetainedScene.MainModelNormalTriangles - _mNrm;
        _mModels = RetainedModels.MainModels; _mFaces = RetainedModels.MainFaces; _mCulled = RetainedModels.MainCulled;
        _mOut = RetainedModels.MainOutOfTable; _mTris = RetainedScene.MainModelTriangles; _mGroups = RetainedScene.MainModelGroups;
        _mNrm = RetainedScene.MainModelNormalTriangles;
        long mtl = RetainedModels.MainTileModels - _mTile, mcl = RetainedModels.MainClipped - _mClip;
        _mTile = RetainedModels.MainTileModels; _mClip = RetainedModels.MainClipped;
        long msat = RetainedModels.MainSaturated - _mSat;
        _mSat = RetainedModels.MainSaturated;
        Console.WriteLine($"[KF2] gpu world: models {(ModelsActive ? "on the GPU" : "on the packets")}; a draw: " +
                          $"{(d == 0 ? 0 : (double)mm / d):F1} model(s) ({(d == 0 ? 0 : (double)mtl / d):F1} from the clipped assembler), " +
                          $"{(d == 0 ? 0 : mf / d)} face(s) taken ({(d == 0 ? 0 : mcl / d)} it would clip, culled by the GPU; {(d == 0 ? 0 : msat / d)} with a corner nearer than H/2), " +
                          $"{(d == 0 ? 0 : mc / d)} facing away, {(d == 0 ? 0 : mo / d)} outside the table, " +
                          $"{(d == 0 ? 0 : mt / d)} triangle(s) drawn in {(d == 0 ? 0 : (double)mg / d):F1} light group(s), " +
                          $"{(d == 0 ? 0 : mn / d)} into the normal pass");
        long ins = RetainedModels.Instances - _iIns, iw = RetainedModels.InstancesWhole - _iWhole, im = RetainedModels.InstancesMirror - _iMir;
        long idr = RetainedScene.InstancesDrawn - _iDrawn, ic = RetainedScene.InstanceCorners - _iCorners, iv = RetainedScene.InstanceVertices - _iVerts;
        long imd = RetainedScene.MirrorInstancesDrawn - _iMirDrawn, ia = RetainedModels.ArmInstances - _iArm, iad = RetainedScene.ArmDraws - _iArmDrawn, iac = RetainedScene.ArmCalls - _iArmCalls;
        long itl = RetainedModels.TileInstances - _iTile;
        _iTile = RetainedModels.TileInstances;
        _iIns = RetainedModels.Instances; _iWhole = RetainedModels.InstancesWhole; _iMir = RetainedModels.InstancesMirror;
        _iDrawn = RetainedScene.InstancesDrawn; _iCorners = RetainedScene.InstanceCorners; _iVerts = RetainedScene.InstanceVertices;
        _iMirDrawn = RetainedScene.MirrorInstancesDrawn;
        _iArm = RetainedModels.ArmInstances;
        _iArmDrawn = RetainedScene.ArmDraws;
        _iArmCalls = RetainedScene.ArmCalls;
        Console.WriteLine($"[KF2] gpu world: meshes {(RetainedModels.MeshesOn ? "on" : "off")}; a draw: " +
                          $"{(d == 0 ? 0 : (double)ins / d):F1} instance(s) made ({(d == 0 ? 0 : (double)iw / d):F1} whole, {(d == 0 ? 0 : (double)im / d):F1} in the mirror's replay, {(d == 0 ? 0 : (double)itl / d):F1} the clipped assembler's), " +
                          $"{(d == 0 ? 0 : (double)idr / d):F1} drawn with {(d == 0 ? 0 : ic / d)} corner(s), {(d == 0 ? 0 : iv / d)} posed vert(ices) uploaded, " +
                          $"{(d == 0 ? 0 : (double)imd / d):F1} in the mirror; store {RetainedScene.MeshCornerCount} corner(s), " +
                          $"{RetainedModels.MeshBuilds} mesh(es) built, {RetainedModels.MeshStale} found changed, {RetainedModels.InstanceRefused} refused in all; " +
                          $"arm {(RetainedModels.ArmOn ? "on" : "off")}, {ia} placed, {iad} drawn in {iac} call(s), " +
                          $"{RetainedModels.ArmLost} lost and {RetainedScene.ArmMissed} missed in all" +
                          (RetainedModels.Checking ? $"; checked {RetainedModels.CheckFaces} face(s), {RetainedModels.CheckDiffer} kept or dropped differently " +
                                                       $"(by twice their area, under 0.01/0.1/1/10 px² and more: {string.Join("/", RetainedModels.CheckArea)})" : ""));
        long pp = RetainedModels.InstancesPosed - _pPosed, pr = RetainedModels.InstancesRigid - _pRigid;
        long pd = MoPose.Deferred - _pDeferred, pm = MoPose.Materialized - _pMat, pt = RetainedScene.PoseTexelsUploaded - _pTexels;
        _pPosed = RetainedModels.InstancesPosed; _pRigid = RetainedModels.InstancesRigid;
        _pDeferred = MoPose.Deferred; _pMat = MoPose.Materialized; _pTexels = RetainedScene.PoseTexelsUploaded;
        Console.WriteLine($"[KF2] gpu world: poses {(RetainedModels.PosesOn ? "on" : "off")}; a draw: " +
                          $"{(d == 0 ? 0 : (double)pp / d):F1} instance(s) blended from an MO pose, {(d == 0 ? 0 : (double)pr / d):F1} from a rigid model's vertices, " +
                          $"{(d == 0 ? 0 : (double)pd / d):F1} pose(s) left undecoded, {(d == 0 ? 0 : (double)pm / d):F1} decoded after all; " +
                          $"{pt} texel(s) uploaded; store {RetainedScene.PoseTexels} texel(s), {MoPose.PoseBuilds} pose(s) and {MoPose.RigidBuilds} rigid model(s) kept, " +
                          $"{MoPose.PoseRefused} pose(s) refused in all" +
                          (MoPose.Checking ? $"; checked {MoPose.CheckVertices} vert(ices), {MoPose.CheckDiffer} placed differently" : ""));
        long bn = RetainedScene.BlendNoted - _bNoted, bs = RetainedScene.BlendSorted - _bSorted;
        long bd = RetainedScene.BlendDrawn - _bDrawn, br = RetainedScene.BlendRuns - _bRuns;
        _bNoted = RetainedScene.BlendNoted; _bSorted = RetainedScene.BlendSorted;
        _bDrawn = RetainedScene.BlendDrawn; _bRuns = RetainedScene.BlendRuns;
        Console.WriteLine($"[KF2] gpu world: blend {(RetainedModels.BlendOn ? "on" : "off")}; a draw: " +
                          $"{(d == 0 ? 0 : (double)bn / d):F1} blended face(s) noted, {(d == 0 ? 0 : (double)bs / d):F1} sorted, " +
                          $"{(d == 0 ? 0 : (double)bd / d):F1} drawn in {(d == 0 ? 0 : (double)br / d):F1} run(s); " +
                          $"{RetainedModels.Subtractive} subtractive submit(s) and {string.Join("/", ModelWalk.ViewSpaceSubmits)} in view space (lit/clipped/twin) in all; in the mirror " +
                          $"{(d == 0 ? 0 : (double)(RetainedScene.MirrorBlendNoted - _bMirNoted) / d):F1} noted, " +
                          $"{(d == 0 ? 0 : (double)(RetainedScene.MirrorBlendDrawn - _bMirDrawn) / d):F1} drawn; " +
                          $"{RetainedScene.BlendNormalInstances} instance draw(s) into the surface buffer in all");
        _bMirNoted = RetainedScene.MirrorBlendNoted; _bMirDrawn = RetainedScene.MirrorBlendDrawn;
        long sk = RetainedModels.SkyInstances - _sIns, sd = RetainedScene.SkyDrawn - _sDrawn, sf = RetainedScene.SkyFacesDrawn - _sFaces;
        _sIns = RetainedModels.SkyInstances; _sDrawn = RetainedScene.SkyDrawn; _sFaces = RetainedScene.SkyFacesDrawn;
        Console.WriteLine($"[KF2] gpu world: sky {(RetainedModels.SkyOn ? "on" : "off")}; a draw: {(d == 0 ? 0 : (double)sk / d):F1} object(s) placed, " +
                          $"{(d == 0 ? 0 : (double)sd / d):F1} drawn with {(d == 0 ? 0 : sf / d)} face(s); {RetainedModels.SkyRefused} refused and " +
                          $"{RetainedScene.SkyMissed} missed in all");
        Console.WriteLine($"[KF2] gpu world: light generations: at most {_gensMax} in a frame; map builds {RetainedMap.Builds - _builds}, " +
                          $"the last {RetainedMap.LastBuildMs:F2} ms for {RetainedMap.LastWhy}; " +
                          (RetainedMap.RecordsOn
                              ? $"lit from the records: {RetainedMap.Packs - _packs} record change(s), the last {RetainedMap.LastPackMs:F3} ms for {RetainedMap.LastPackWhy}, " +
                                $"{RetainedScene.RecordUploads - _recUploads} upload(s)"
                              : "lit on the CPU") +
                          (RetainedMap.RecordCheck
                              ? $"; checked {RetainedMap.CheckCorners} corner(s): {RetainedMap.CheckColour} coloured and {RetainedMap.CheckCue} fogged otherwise " +
                                $"(worst {RetainedMap.CheckCueWorst:G3})"
                              : ""));
        _builds = RetainedMap.Builds;
        _packs = RetainedMap.Packs;
        _recUploads = RetainedScene.RecordUploads;
        _gensMax = 0;
        long px = RetainedScene.SurfaceDepthPixels;
        Console.WriteLine($"[KF2] gpu world: surfaces: {RetainedScene.MainNormalTriangles - _nrmTris} map triangle(s) into the normal pass; " +
                          (RetainedScene.SurfaceChecks == 0 ? "no readback (needs the surface buffer: a reflection or the murk on)" :
                           $"of {px} pixel(s) with a depth, {(px == 0 ? 0 : 100.0 * RetainedScene.SurfaceBehind / px):F2}% whose surface lies behind it, " +
                           $"{(px == 0 ? 0 : 100.0 * RetainedScene.SurfaceMissing / px):F2}% with none; by id none/opaque/water/overlay/authored/blended " +
                           string.Join("/", RetainedScene.SurfaceIds)));
        Array.Clear(RetainedScene.SurfaceIds);
        _nrmTris = RetainedScene.MainNormalTriangles;
        long md = RetainedScene.MirrorDraws - _mDraws;
        Console.WriteLine($"[KF2] gpu world: mirror {(MirrorActive ? "on the GPU" : "on the packets")}; {md} draw(s), " +
                          $"{RetainedScene.MirrorMissed - _mMissed} capture(s) missed; a draw: " +
                          $"{(md == 0 ? 0 : (RetainedScene.MirrorTriangles - _mStatic) / md)} static and " +
                          $"{(md == 0 ? 0 : (RetainedScene.MirrorModelTriangles - _mModelTris) / md)} model and " +
                          $"{(md == 0 ? 0 : (RetainedScene.MirrorWaterTriangles - _mWater) / md)} blended triangle(s); " +
                          $"halves {MirrorSkipped} left whole to the GPU, {MirrorKept} with their blended faces assembled; " +
                          $"{RetainedModels.MirrorModels - _mMirModels} model(s) taken");
        _mDraws = RetainedScene.MirrorDraws; _mMissed = RetainedScene.MirrorMissed; _mStatic = RetainedScene.MirrorTriangles;
        _mModelTris = RetainedScene.MirrorModelTriangles; _mMirModels = RetainedModels.MirrorModels;
        _mWater = RetainedScene.MirrorWaterTriangles;
        MirrorSkipped = MirrorKept = 0;
        RetainedScene.SurfaceDepthPixels = RetainedScene.SurfaceBehind = RetainedScene.SurfaceMissing = RetainedScene.SurfaceChecks = 0;
        RetainedScene.SurfaceCheck = true;
        Skipped = Kept = 0;
    }
}
