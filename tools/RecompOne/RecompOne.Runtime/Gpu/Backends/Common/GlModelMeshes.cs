using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

/// <summary>
/// 0085. The models drawn from meshes kept on the GPU (Step 3's second slice): each
/// mesh's opaque faces uploaded once (<see cref="RetainedScene.MeshCorners"/>), and
/// each frame only the posed vertices, into a buffer texture per frame of the ring, and
/// a record per model. The world programs place, light and cull every corner from
/// those (<c>ModelGlsl</c>). See "Step 3, the second slice" in docs/GPU_RENDERER.md.
/// The third slice keeps the vertices too (<see cref="RetainedScene.PoseStore"/>): a
/// rigid model's as they are, an MO pose's keyframe and deltas, blended in the shader,
/// so an instance drawn from the store uploads nothing a frame.
/// </summary>
public sealed partial class GlCore
{
    const int ModelVertsUnit = 19, PoseUnit = 20;

    uint _poseBuf, _poseTex;
    int _poseUploaded, _poseCap;

    uint _meshVbo, _meshMipVbo, _meshVao;
    int _meshUploaded, _meshCap, _meshGen = -1;
    uint[] _meshMip = [];

    // The meshes' textures: a key each, and a table of their atlas entries the corners
    // index (bound on the static map's unit while models draw).
    readonly Dictionary<(int, int, uint), int> _mdlKeyAt = new();
    readonly List<(int TPage, int Clut, uint Rect)> _mdlKeys = new();
    readonly Dictionary<int, int[]> _meshKeys = new();
    uint[] _mdlKeyEntry = [];
    int[] _mdlKeyLooked = [];
    int _mdlLookSerial;
    bool _mdlTableDirty;
    uint _mdlTableBuf, _mdlTableTex;

    // The posed vertices, per frame of the ring, and the mesh store they were drawn with.
    readonly uint[] _mvBuf = new uint[ModelRing], _mvTex = new uint[ModelRing];
    readonly int[] _mvSerial = new int[ModelRing], _mvGen = new int[ModelRing], _mvCap = new int[ModelRing];

    int _uwModel = -1, _uwModelBase, _uwModelR, _uwModelT, _uwModelFar, _uwModelNear, _uwModelLlm, _uwModelCue, _uwModelRgbc, _uwModelMat, _uwModelGteC = -1;
    int _uwnModel = -1, _uwnModelBase, _uwnModelR, _uwnModelT, _uwnModelFar, _uwnModelNear, _uwnModelMat, _uwnModelGteC = -1;
    int _uwModelPose = -1, _uwModelPoseW = -1, _uwnModelPose = -1, _uwnModelPoseW = -1;

    void InitModelMeshes()
    {
        if (_progWorld == 0) return;
        int L(string n) => _gl.GetUniformLocation(_progWorld, n);
        _uwModel = L("uModel"); _uwModelBase = L("uModelBase"); _uwModelR = L("uModelR"); _uwModelT = L("uModelT");
        _uwModelFar = L("uModelFar"); _uwModelNear = L("uModelNear"); _uwModelLlm = L("uModelLlm"); _uwModelCue = L("uModelCue");
        _uwModelRgbc = L("uModelRgbc"); _uwModelMat = L("uModelMat"); _uwModelGteC = L("uModelGteC");
        _uwModelPose = L("uModelPose"); _uwModelPoseW = L("uModelPoseW");
        _gl.UseProgram(_progWorld);
        if (_uwModel >= 0) _gl.Uniform1(_uwModel, 0);
        int u = L("uModelVerts");
        if (u >= 0) _gl.Uniform1(u, ModelVertsUnit);
        u = L("uModelPoses");
        if (u >= 0) _gl.Uniform1(u, PoseUnit);
        if (_progWorldNrm != 0)
        {
            int N(string n) => _gl.GetUniformLocation(_progWorldNrm, n);
            _uwnModel = N("uModel"); _uwnModelBase = N("uModelBase"); _uwnModelR = N("uModelR"); _uwnModelT = N("uModelT");
            _uwnModelFar = N("uModelFar"); _uwnModelNear = N("uModelNear"); _uwnModelMat = N("uModelMat"); _uwnModelGteC = N("uModelGteC");
            _uwnModelPose = N("uModelPose"); _uwnModelPoseW = N("uModelPoseW");
            _gl.UseProgram(_progWorldNrm);
            if (_uwnModel >= 0) _gl.Uniform1(_uwnModel, 0);
            int v = N("uModelVerts");
            if (v >= 0) _gl.Uniform1(v, ModelVertsUnit);
            v = N("uModelPoses");
            if (v >= 0) _gl.Uniform1(v, PoseUnit);
        }
        _gl.UseProgram(0);
        _meshVbo = _gl.GenBuffer();
        _meshMipVbo = _gl.GenBuffer();
        _meshVao = MakeWorldVao(_meshVbo, _meshMipVbo);
        // The table holds a word before any mesh has a texture.
        _mdlTableBuf = _gl.GenBuffer();
        _mdlTableTex = _gl.GenTexture();
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, _mdlTableBuf);
        _gl.BufferData<uint>(BufferTargetARB.TextureBuffer, [0u], BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
        _gl.BindTexture(TextureTarget.TextureBuffer, _mdlTableTex);
        _gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.R32ui, _mdlTableBuf);
        _gl.BindTexture(TextureTarget.TextureBuffer, 0);
        // The pose store holds a texel before any pose is kept.
        _poseBuf = _gl.GenBuffer();
        _poseTex = _gl.GenTexture();
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, _poseBuf);
        _gl.BufferData<short>(BufferTargetARB.TextureBuffer, [0, 0, 0, 0], BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
        _gl.BindTexture(TextureTarget.TextureBuffer, _poseTex);
        _gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.Rgba16i, _poseBuf);
        _gl.BindTexture(TextureTarget.TextureBuffer, 0);
    }

    /// <summary>Whether both programs can draw an instance.</summary>
    bool InstancesReady => _uwModel >= 0 && _meshVao != 0;

    /// <summary>The store's corners not yet on the GPU, and their textures' keys; the
    /// whole store again after it was emptied.</summary>
    unsafe void UploadMeshes()
    {
        if (_meshGen != RetainedScene.MeshGeneration)
        {
            _meshGen = RetainedScene.MeshGeneration;
            _meshUploaded = 0;
            _poseUploaded = 0;
            _mdlKeyAt.Clear();
            _mdlKeys.Clear();
            _meshKeys.Clear();
        }
        UploadPoses();
        int n = RetainedScene.MeshCornerCount;
        if (n <= _meshUploaded) return;
        var all = RetainedScene.MeshCorners;
        if (_meshMip.Length < n) Array.Resize(ref _meshMip, Math.Max(n, _meshMip.Length * 2));
        for (int i = _meshUploaded; i + 2 < n; i += 3)
            _meshMip[i] = _meshMip[i + 1] = _meshMip[i + 2] = (uint)(ModelKey(all[i]) + 1);

        int from = _meshUploaded;
        if (n > _meshCap)
        {
            // Grown: everything again, into a buffer with room.
            _meshCap = Math.Max(n, _meshCap * 2);
            from = 0;
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshVbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_meshCap * sizeof(RetainedScene.Vertex)), null, BufferUsageARB.DynamicDraw);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshMipVbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_meshCap * 4), null, BufferUsageARB.DynamicDraw);
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshVbo);
        _gl.BufferSubData<RetainedScene.Vertex>(BufferTargetARB.ArrayBuffer, from * sizeof(RetainedScene.Vertex),
            new ReadOnlySpan<RetainedScene.Vertex>(all, from, n - from));
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshMipVbo);
        _gl.BufferSubData<uint>(BufferTargetARB.ArrayBuffer, from * 4, new ReadOnlySpan<uint>(_meshMip, from, n - from));
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _meshUploaded = n;
        if (_mdlKeyEntry.Length < _mdlKeys.Count)
        {
            Array.Resize(ref _mdlKeyEntry, Math.Max(_mdlKeys.Count, _mdlKeyEntry.Length * 2));
            Array.Resize(ref _mdlKeyLooked, _mdlKeyEntry.Length);
            _mdlTableDirty = true;
        }
    }

    /// <summary>The pose store's texels not yet on the GPU; all of them into a larger
    /// buffer when it has outgrown its own.</summary>
    unsafe void UploadPoses()
    {
        int n = RetainedScene.PoseTexels;
        if (n <= _poseUploaded) return;
        int from = _poseUploaded;
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, _poseBuf);
        if (n > _poseCap)
        {
            _poseCap = Math.Max(n, _poseCap * 2);
            from = 0;
            _gl.BufferData(BufferTargetARB.TextureBuffer, (nuint)(_poseCap * 8), null, BufferUsageARB.DynamicDraw);
            // The texture holds the buffer's storage, which BufferData replaced.
            _gl.BindTexture(TextureTarget.TextureBuffer, _poseTex);
            _gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.Rgba16i, _poseBuf);
            _gl.BindTexture(TextureTarget.TextureBuffer, 0);
        }
        _gl.BufferSubData<short>(BufferTargetARB.TextureBuffer, from * 8,
            new ReadOnlySpan<short>(RetainedScene.PoseStore, from * 4, (n - from) * 4));
        _gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
        RetainedScene.PoseTexelsUploaded += n - from;
        _poseUploaded = n;
    }

    int ModelKey(in RetainedScene.Vertex v)
    {
        int tp = (int)(v.Texpage + 0.5f), cl = (int)(v.Clut + 0.5f);
        if ((tp & 0x8000) != 0 || (v.Flags & RetainedScene.FlagRect) == 0) return -1;
        var key = (tp & 0x1FF, cl, v.Rect);
        if (_mdlKeyAt.TryGetValue(key, out int k)) return k;
        _mdlKeyAt[key] = k = _mdlKeys.Count;
        _mdlKeys.Add(key);
        return k;
    }

    /// <summary>A mesh's distinct textures, by its first corner.</summary>
    int[] MeshKeys(int start, int count)
    {
        if (_meshKeys.TryGetValue(start, out var keys)) return keys;
        var set = new HashSet<int>();
        for (int i = start; i < start + count && i < _meshUploaded; i += 3)
            if (_meshMip[i] != 0) set.Add((int)_meshMip[i] - 1);
        return _meshKeys[start] = set.ToArray();
    }

    /// <summary>
    /// A view's instances ready to draw, before the target is bound (the atlas decode
    /// draws): the store uploaded, the frame's posed vertices in its slot of the ring,
    /// and the atlas entries of the textures these instances' meshes use. The slot, or
    /// -1 with nothing to draw.
    /// </summary>
    unsafe int PrepareInstances(RetainedScene.Frame f, List<RetainedScene.ModelInstance> list, bool mips)
    {
        if (list.Count == 0 || !InstancesReady) return -1;
        UploadMeshes();
        int slot = f.Serial & (ModelRing - 1);
        if (_mvSerial[slot] != f.Serial || _mvGen[slot] != _meshGen)
        {
            if (_mvBuf[slot] == 0) { _mvBuf[slot] = _gl.GenBuffer(); _mvTex[slot] = _gl.GenTexture(); }
            _gl.BindBuffer(BufferTargetARB.TextureBuffer, _mvBuf[slot]);
            // Every instance may come from the pose store; the buffer still needs storage.
            int bytes = Math.Max(f.VertCount, 1) * 8;
            if (bytes > _mvCap[slot])
            {
                _mvCap[slot] = Math.Max(bytes, _mvCap[slot] * 2);
                _gl.BufferData(BufferTargetARB.TextureBuffer, (nuint)_mvCap[slot], null, BufferUsageARB.StreamDraw);
            }
            _gl.BufferSubData<short>(BufferTargetARB.TextureBuffer, 0, new ReadOnlySpan<short>(f.Verts, 0, f.VertCount * 4));
            _gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
            _gl.BindTexture(TextureTarget.TextureBuffer, _mvTex[slot]);
            _gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.Rgba16i, _mvBuf[slot]);
            _gl.BindTexture(TextureTarget.TextureBuffer, 0);
            _mvSerial[slot] = f.Serial;
            _mvGen[slot] = _meshGen;
            RetainedScene.InstanceVertices += f.VertCount;
        }

        bool on = mips && _mip != null;
        _mdlLookSerial++;
        foreach (var m in list)
            if (m.MeshGen == _meshGen)
            foreach (int k in MeshKeys(m.MeshStart, m.MeshCount))
            {
                if (_mdlKeyLooked[k] == _mdlLookSerial) continue;
                _mdlKeyLooked[k] = _mdlLookSerial;
                var (tp, cl, rect) = _mdlKeys[k];
                uint e = on ? MipOf(tp, cl, rect) : 0u;
                if (e != _mdlKeyEntry[k]) { _mdlKeyEntry[k] = e; _mdlTableDirty = true; }
            }
        if (_mdlTableDirty)
        {
            _gl.BindBuffer(BufferTargetARB.TextureBuffer, _mdlTableBuf);
            _gl.BufferData<uint>(BufferTargetARB.TextureBuffer, _mdlKeyEntry, BufferUsageARB.DynamicDraw);
            _gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
            _gl.BindTexture(TextureTarget.TextureBuffer, _mdlTableTex);
            _gl.TexBuffer(TextureTarget.TextureBuffer, SizedInternalFormat.R32ui, _mdlTableBuf);
            _gl.BindTexture(TextureTarget.TextureBuffer, 0);
            _mdlTableDirty = false;
        }
        if (on && _mip!.HasPending) _mip.Process(_vram.SampleTexture);
        return slot;
    }

    /// <summary>The instances' textures in place of the static map's, and the frame's
    /// posed vertices; <see cref="EndInstances"/> puts the map's back.</summary>
    void BeginInstances(int slot, bool colour)
    {
        _gl.BindVertexArray(_meshVao);
        _gl.ActiveTexture(TextureUnit.Texture0 + ModelVertsUnit);
        _gl.BindTexture(TextureTarget.TextureBuffer, _mvTex[slot]);
        _gl.ActiveTexture(TextureUnit.Texture0 + PoseUnit);
        _gl.BindTexture(TextureTarget.TextureBuffer, _poseTex);
        _gl.ActiveTexture(TextureUnit.Texture0 + MipTableUnit);
        _gl.BindTexture(TextureTarget.TextureBuffer, _mdlTableTex);
        _gl.ActiveTexture(TextureUnit.Texture0);
        if (colour)
        {
            if (_uwMipIndirect >= 0) _gl.Uniform1(_uwMipIndirect, 1);
            _gl.Uniform1(_uwModel, 1);
        }
        else if (_uwnModel >= 0) _gl.Uniform1(_uwnModel, 1);
    }

    void EndInstances(bool colour)
    {
        if (colour)
        {
            if (_uwMipIndirect >= 0) _gl.Uniform1(_uwMipIndirect, 0);
            _gl.Uniform1(_uwModel, 0);
        }
        else if (_uwnModel >= 0) _gl.Uniform1(_uwnModel, 0);
        _gl.ActiveTexture(TextureUnit.Texture0 + MipTableUnit);
        _gl.BindTexture(TextureTarget.TextureBuffer, _mipTableTex);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    readonly float[] _m9 = new float[9];

    /// <summary>One instance's record into the bound program's uniforms: the colour
    /// program's, or the normal program's (placement, the cull and the material).</summary>
    void SendInstance(in RetainedScene.ModelInstance m, bool colour)
    {
        _m9[0] = m.R00; _m9[1] = m.R01; _m9[2] = m.R02; _m9[3] = m.R10; _m9[4] = m.R11; _m9[5] = m.R12;
        _m9[6] = m.R20; _m9[7] = m.R21; _m9[8] = m.R22;
        // The store's first texel, or -1 for the frame's vertices; -1 a rigid weight.
        int pose = m.Pose - 1, weight = m.PoseMorph ? m.PoseWeight : -1;
        if (!colour)
        {
            _gl.Uniform1(_uwnModelBase, m.VertBase);
            if (_uwnModelPose >= 0) _gl.Uniform1(_uwnModelPose, pose);
            if (_uwnModelPoseW >= 0) _gl.Uniform1(_uwnModelPoseW, weight);
            _gl.UniformMatrix3(_uwnModelR, 1, true, _m9);
            _gl.Uniform3(_uwnModelT, m.Tx, m.Ty, m.Tz);
            _gl.Uniform1(_uwnModelFar, m.Far);
            _gl.Uniform1(_uwnModelNear, m.Near);
            if (_uwnModelMat >= 0) _gl.Uniform1(_uwnModelMat, m.Material);
            return;
        }
        _gl.Uniform1(_uwModelBase, m.VertBase);
        if (_uwModelPose >= 0) _gl.Uniform1(_uwModelPose, pose);
        if (_uwModelPoseW >= 0) _gl.Uniform1(_uwModelPoseW, weight);
        _gl.UniformMatrix3(_uwModelR, 1, true, _m9);
        _gl.Uniform3(_uwModelT, m.Tx, m.Ty, m.Tz);
        _gl.Uniform1(_uwModelFar, m.Far);
        _gl.Uniform1(_uwModelNear, m.Near);
        _m9[0] = m.Llm0; _m9[1] = m.Llm1; _m9[2] = m.Llm2; _m9[3] = m.Llm3; _m9[4] = m.Llm4; _m9[5] = m.Llm5;
        _m9[6] = m.Llm6; _m9[7] = m.Llm7; _m9[8] = m.Llm8;
        if (_uwModelLlm >= 0) _gl.UniformMatrix3(_uwModelLlm, 1, true, _m9);
        if (_uwModelCue >= 0) _gl.Uniform3(_uwModelCue, m.Dqa, m.Dqb, m.Curve);
        if (_uwModelRgbc >= 0) _gl.Uniform1(_uwModelRgbc, m.Rgbc);
        if (_uwModelMat >= 0) _gl.Uniform1(_uwModelMat, m.Material);
        if (_uwBk >= 0) _gl.Uniform3(_uwBk, m.Bk0, m.Bk1, m.Bk2);
        if (_uwLcmR >= 0) _gl.Uniform3(_uwLcmR, m.L0, m.L1, m.L2);
        if (_uwLcmG >= 0) _gl.Uniform3(_uwLcmG, m.L3, m.L4, m.L5);
        if (_uwLcmB >= 0) _gl.Uniform3(_uwLcmB, m.L6, m.L7, m.L8);
    }

    /// <summary>
    /// A view's instances after the map, as <see cref="DrawWorldModels"/> draws the
    /// captured models: 0051's true depth first with colour off, then colour against
    /// it pulled towards the camera. Not culled by GL: the shader keeps the faces the
    /// lit assembler keeps.
    /// </summary>
    void DrawInstances(List<RetainedScene.ModelInstance> list, int slot)
    {
        _gl.Disable(EnableCap.CullFace);
        BeginInstances(slot, true);
        bool bias = GteDepth.ZBuffer && (GteDepth.DepthBias > 0f || GteDepth.DepthSlope > 0f);
        if (bias)
        {
            _gl.ColorMask(false, false, false, false);
            foreach (var m in list)
            {
                if (m.MeshGen != _meshGen) continue;
                SendInstance(m, true);
                _gl.DrawArrays(PrimitiveType.Triangles, m.MeshStart, (uint)m.MeshCount);
            }
            _gl.ColorMask(true, true, true, true);
            _gl.DepthMask(false);
            if (_uwDepthBias >= 0) _gl.Uniform1(_uwDepthBias, GteDepth.DepthBias / 65536f);
            if (_uwDepthSlope >= 0) _gl.Uniform1(_uwDepthSlope, GteDepth.DepthSlope);
        }
        foreach (var m in list)
        {
            // A store emptied since the instance was made holds other meshes there now.
            if (m.MeshGen != _meshGen) continue;
            SendInstance(m, true);
            _gl.DrawArrays(PrimitiveType.Triangles, m.MeshStart, (uint)m.MeshCount);
            RetainedScene.InstanceCorners += m.MeshCount;
        }
        if (bias)
        {
            if (_uwDepthBias >= 0) _gl.Uniform1(_uwDepthBias, 0f);
            if (_uwDepthSlope >= 0) _gl.Uniform1(_uwDepthSlope, 0f);
            _gl.DepthMask(true);
        }
        EndInstances(true);
    }

    /// <summary>The frame's instances into the normal and surface buffers, through the
    /// world normal program; nothing if the frame's vertices have left their slot.</summary>
    void DrawInstanceNormals(RetainedScene.Frame f)
    {
        int slot = f.Serial & (ModelRing - 1);
        if (f.Instances.Count == 0 || _uwnModel < 0 || _mvSerial[slot] != f.Serial || _mvGen[slot] != _meshGen) return;
        BeginInstances(slot, false);
        foreach (var m in f.Instances)
        {
            if (m.MeshGen != _meshGen) continue;
            SendInstance(m, false);
            _gl.DrawArrays(PrimitiveType.Triangles, m.MeshStart, (uint)m.MeshCount);
        }
        EndInstances(false);
    }
}
