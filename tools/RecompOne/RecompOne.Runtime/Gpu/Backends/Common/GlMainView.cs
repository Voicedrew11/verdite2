using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

/// <summary>
/// 0085. The retained map (<see cref="RetainedScene"/>) drawn into the frame itself:
/// its opaque range, from the frame's camera, gated to the halves the frame's tile walk
/// visited, into the display target the game is drawing, with the depth test and write.
/// The table walk calls it once a frame, past slot 0 (the sky), so everything the
/// table still carries is tested against it. See "Step 1, the first slice" in docs/GPU_RENDERER.md.
/// </summary>
public sealed partial class GlCore
{
    int _uwTrueColor, _uwPlainZ, _uwSnap, _uwPerPixel, _uwDither, _uwCueFromZ;

    // The map into the normal and surface buffers (WorldNormalVs, NormalFs).
    uint _progWorldNrm;
    int _uwnR, _uwnCam, _uwnT, _uwnH, _uwnC, _uwnFb, _uwnNear, _uwnHalfGate, _uwnSnap;
    int _uwnProjH, _uwnCentre, _uwnScale, _uwnDepthCull, _uwnDepthStep;
    int _uNrmDepthCull, _uNrmDepthStep;
    const int FrameDepthUnit = 18;

    void InitMainView()
    {
        if (_progWorld == 0) return;
        InitWorldNormals();
        _uwTrueColor = _gl.GetUniformLocation(_progWorld, "uTrueColor");
        _uwPlainZ = _gl.GetUniformLocation(_progWorld, "uPlainZ");
        _uwSnap = _gl.GetUniformLocation(_progWorld, "uWorldSnap");
        _uwPerPixel = _gl.GetUniformLocation(_progWorld, "uWorldPerPixel");
        _uwDither = _gl.GetUniformLocation(_progWorld, "uWorldDither");
        _uwCueFromZ = _gl.GetUniformLocation(_progWorld, "uCueFromZ");
        RetainedScene.MainDrawer = DrawWorldMain;
    }

    bool DrawWorldMain(int offX, int offY)
    {
        if (!RetainedScene.MainView || _progWorld == 0 || PlanarReflections.Capturing) return false;
        var f = RetainedScene.Find(RetainedScene.MainSerial);
        if (f == null || RetainedScene.StaticCount[0] == 0) return false;
        Flush(FlushReason.Target);
        var rt = ClassifyDisplay();
        if (rt == null) return false;

        uint query = BeginGpuTimer();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateShadows();
        UploadStatic();
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool mips = UpdateWorldMips(f, f.MainHalves);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        _gl.UseProgram(_progWorld);
        if (_uwMipOn >= 0) _gl.Uniform1(_uwMipOn, mips ? 1f : 0f);
        if (mips)
        {
            _gl.ActiveTexture(TextureUnit.Texture5);
            _gl.BindTexture(TextureTarget.Texture2D, _mip!.Texture);
        }
        BeginWorldLights();
        _gl.ActiveTexture(TextureUnit.Texture0 + HalvesUnit);
        _gl.BindTexture(TextureTarget.Texture2D, _halvesTex);
        _gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0, RetainedScene.HalvesW, RetainedScene.HalvesH,
            PixelFormat.RedInteger, PixelType.UnsignedByte, f.MainHalves);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _vram.SampleTexture);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _vram.SampleTexture);
        if (_uwHalfGate >= 0) _gl.Uniform1(_uwHalfGate, 1);
        if (_uwAniso >= 0) _gl.Uniform1(_uwAniso, (float)GteDepth.Anisotropy);
        if (_uwTrueColor >= 0) _gl.Uniform1(_uwTrueColor, GteDepth.TrueColor ? 1f : 0f);
        if (_uwPlainZ >= 0) _gl.Uniform1(_uwPlainZ, GteDepth.PlainDepth);
        if (_uwMirror >= 0) _gl.Uniform1(_uwMirror, 0);
        if (_uwMaskOn >= 0) _gl.Uniform1(_uwMaskOn, 0);
        if (_uwAtmosSkip >= 0) _gl.Uniform1(_uwAtmosSkip, 0);
        // The frame's own settings, as its packets take them: the GTE's whole pixels
        // without sub-pixel, the corner colours without light records, and the
        // crosshatch while the draw area dithers (a map face is textured and shaded).
        if (_uwSnap >= 0) _gl.Uniform1(_uwSnap, GteDepth.Subpixel ? 0 : 1);
        if (_uwPerPixel >= 0) _gl.Uniform1(_uwPerPixel, GteLightMap.Enabled ? 1 : 0);
        if (_uwDither >= 0) _gl.Uniform1(_uwDither, _env.Dither ? 1 : 0);
        // Fogged at each pixel's own depth: a face clipped at the eye has no corner
        // whose screen-affine fog holds at the clip.
        if (_uwCueFromZ >= 0) _gl.Uniform1(_uwCueFromZ, RetainedScene.MainFogFromZ ? Math.Max(1f, f.View.H) : 0f);
        SendWorldFluid();
        SendWorldAtmos();

        // The GTE's centre is in the draw area's pixels; the target's are offset
        // from them by the draw offset and the widescreen margin.
        var v = f.View;
        Span<float> r = [v.R00, v.R01, v.R02, v.R10, v.R11, v.R12, v.R20, v.R21, v.R22];
        float cx = v.Cx + offX - rt.X + rt.Margin, cy = v.Cy + offY - rt.Y;
        SetWorldView(r, v.CamX, v.CamY, v.CamZ, v.Tx, v.Ty, v.Tz, v.H, cx, cy, rt.Wide1x, rt.H, v.H, true, GlVram.Scale);
        if (_uwNear >= 0) _gl.Uniform1(_uwNear, RetainedScene.MainNear);
        SendWorldLights(r, v.CamX, v.CamY, v.CamZ, v.Tx, v.Ty, v.Tz, cx, cy, v.H, false, 0f);
        CullChunks(r, v.CamX, v.CamY, v.CamZ, v.Tx, v.Ty, v.Tz, v.H, cx, cy, rt.Wide1x, rt.H, false, 0f, v.H,
                   float.PositiveInfinity);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
        _gl.Viewport(0, 0, (uint)rt.TexW, (uint)rt.TexH);
        ClearStaleDepth(rt);
        // The game's clip, widened over the margin as FlushCore widens it.
        int s = GlVram.Scale;
        int clipX0 = _env.ClipX0 - rt.X + rt.Margin, clipY0 = _env.ClipY0 - rt.Y;
        int clipX1 = _env.ClipX1 - rt.X + rt.Margin, clipY1 = _env.ClipY1 - rt.Y;
        if (rt.Margin > 0 && _env.ClipX0 <= rt.X && _env.ClipX1 >= rt.X + rt.W - 1) { clipX0 = 0; clipX1 = rt.Wide1x - 1; }
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(clipX0 * s, clipY0 * s,
            (uint)Math.Max(0, (clipX1 - clipX0 + 1) * s), (uint)Math.Max(0, (clipY1 - clipY0 + 1) * s));

        // The game culls every map face on its facing; seen unmirrored, its front
        // faces are counter-clockwise here.
        if (RetainedScene.CullBack)
        {
            _gl.Enable(EnableCap.CullFace);
            _gl.FrontFace(FrontFaceDirection.Ccw);
            _gl.CullFace(TriangleFace.Back);
        }
        _gl.ColorMask(true, true, true, true);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        int drawn = DrawRange(0, null);

        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        if (_uwHalfGate >= 0) _gl.Uniform1(_uwHalfGate, 0);
        if (_uwSnap >= 0) _gl.Uniform1(_uwSnap, 0);
        if (_uwPerPixel >= 0) _gl.Uniform1(_uwPerPixel, 1);
        if (_uwDither >= 0) _gl.Uniform1(_uwDither, 0);
        if (_uwCueFromZ >= 0) _gl.Uniform1(_uwCueFromZ, 0f);
        EndWorldLights();
        _gl.BindVertexArray(0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        EndGpuTimer(query, GpuWork.Batch, 0, Diagnostics.GpuTimes.Pass.World);

        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        RetainedScene.MainTicks[0] += t1 - t0; RetainedScene.MainTicks[1] += t2 - t1;
        RetainedScene.MainTicks[2] += t3 - t2; RetainedScene.MainTicks[3] += t4 - t3;
        // The normal pass draws this map first (DrawWorldNormals).
        if (AoGeometry.Active)
        {
            rt.Geo.Frame(_frame, GteDepth.Generation);
            rt.Geo.WorldSerial = RetainedScene.MainSerial;
            rt.Geo.WorldCx = cx;
            rt.Geo.WorldCy = cy;
        }
        rt.Dirty = true;
        rt.LastDrawFrame = _frame;
        rt.RetainedSerial = RetainedScene.Serial;
        _lastZRt = rt;
        RetainedScene.MainDraws++;
        RetainedScene.MainTriangles += drawn / 3;
        return true;
    }

    // ---- the normal and surface buffers ------------------------------------------

    void InitWorldNormals()
    {
        _progWorldNrm = GlShaders.Build(_gl, GlShaders.WorldNormalVs, GlShaders.NormalFs, "worldnormal");
        if (_progNormal != 0)
        {
            _uNrmDepthCull = _gl.GetUniformLocation(_progNormal, "uDepthCull");
            _uNrmDepthStep = _gl.GetUniformLocation(_progNormal, "uDepthStep");
            _gl.UseProgram(_progNormal);
            int u = _gl.GetUniformLocation(_progNormal, "uFrameDepth");
            if (u >= 0) _gl.Uniform1(u, FrameDepthUnit);
            if (_uNrmDepthCull >= 0) _gl.Uniform1(_uNrmDepthCull, 0);
        }
        if (_progWorldNrm == 0) return;
        int L(string n) => _gl.GetUniformLocation(_progWorldNrm, n);
        _uwnR = L("uR"); _uwnCam = L("uCam"); _uwnT = L("uT"); _uwnH = L("uH"); _uwnC = L("uC"); _uwnFb = L("uFb");
        _uwnNear = L("uNear"); _uwnHalfGate = L("uHalfGate"); _uwnSnap = L("uWorldSnap");
        _uwnProjH = L("uProjH"); _uwnCentre = L("uCentre"); _uwnScale = L("uScale");
        _uwnDepthCull = L("uDepthCull"); _uwnDepthStep = L("uDepthStep");
        _gl.UseProgram(_progWorldNrm);
        void Unit(string n, int v) { int l = L(n); if (l >= 0) _gl.Uniform1(l, v); }
        Unit("uVram", 0); Unit("uHalves", HalvesUnit); Unit("uFrameDepth", FrameDepthUnit); Unit("uVeilPass", 0);
        _gl.UseProgram(0);
    }

    /// <summary>
    /// Before the normal pass draws the target's triangles: bind the frame's depth
    /// and, when the map was drawn on the GPU into this target, draw it first. The
    /// triangles after it then drop what lies behind the depth, since the order they
    /// are drawn in no longer says which surface is in front. False with no map, and
    /// the pass is the one it was.
    /// </summary>
    bool DrawWorldNormals(GlDisplayRt src, int scale)
    {
        var geo = src.Geo;
        if (geo.WorldSerial == 0 || src.Depth == 0 || !RetainedScene.MainSurfaces) return false;
        _gl.ActiveTexture(TextureUnit.Texture0 + FrameDepthUnit);
        _gl.BindTexture(TextureTarget.Texture2D, src.Depth);
        _gl.ActiveTexture(TextureUnit.Texture0);
        float stepX = (float)src.TexW / Math.Max(1, src.NormalW), stepY = (float)src.TexH / Math.Max(1, src.NormalH);

        var f = RetainedScene.Find(geo.WorldSerial);
        if (_progWorldNrm != 0 && f != null && RetainedScene.StaticCount[0] > 0)
        {
            var v = f.View;
            Span<float> r = [v.R00, v.R01, v.R02, v.R10, v.R11, v.R12, v.R20, v.R21, v.R22];
            CullChunks(r, v.CamX, v.CamY, v.CamZ, v.Tx, v.Ty, v.Tz, v.H, geo.WorldCx, geo.WorldCy, src.Wide1x, src.H,
                       false, 0f, v.H, float.PositiveInfinity);
            _gl.ActiveTexture(TextureUnit.Texture0 + HalvesUnit);
            _gl.BindTexture(TextureTarget.Texture2D, _halvesTex);
            _gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0, RetainedScene.HalvesW, RetainedScene.HalvesH,
                PixelFormat.RedInteger, PixelType.UnsignedByte, f.MainHalves);
            _gl.ActiveTexture(TextureUnit.Texture0);

            _gl.UseProgram(_progWorldNrm);
            if (_uwnR >= 0) _gl.UniformMatrix3(_uwnR, 1, true, r);
            if (_uwnCam >= 0) _gl.Uniform3(_uwnCam, (float)v.CamX, (float)v.CamY, (float)v.CamZ);
            if (_uwnT >= 0) _gl.Uniform3(_uwnT, (float)v.Tx, v.Ty, v.Tz);
            if (_uwnH >= 0) _gl.Uniform1(_uwnH, v.H);
            if (_uwnC >= 0) _gl.Uniform2(_uwnC, geo.WorldCx, geo.WorldCy);
            if (_uwnFb >= 0) _gl.Uniform2(_uwnFb, (float)src.Wide1x, src.H);
            if (_uwnNear >= 0) _gl.Uniform1(_uwnNear, RetainedScene.MainNear);
            if (_uwnHalfGate >= 0) _gl.Uniform1(_uwnHalfGate, 1);
            if (_uwnSnap >= 0) _gl.Uniform1(_uwnSnap, GteDepth.Subpixel ? 0 : 1);
            if (_uwnProjH >= 0) _gl.Uniform1(_uwnProjH, Math.Max(1f, v.H));
            if (_uwnCentre >= 0) _gl.Uniform2(_uwnCentre, geo.WorldCx, geo.WorldCy);
            if (_uwnScale >= 0) _gl.Uniform1(_uwnScale, (float)scale);
            if (_uwnDepthCull >= 0) _gl.Uniform1(_uwnDepthCull, 1);
            if (_uwnDepthStep >= 0) _gl.Uniform2(_uwnDepthStep, stepX, stepY);
            if (RetainedScene.CullBack)
            {
                _gl.Enable(EnableCap.CullFace);
                _gl.FrontFace(FrontFaceDirection.Ccw);
                _gl.CullFace(TriangleFace.Back);
            }
            RetainedScene.MainNormalTriangles += DrawStaticChunks(0) / 3;
            _gl.Disable(EnableCap.CullFace);
            _gl.BindVertexArray(0);
        }

        _gl.UseProgram(_progNormal);
        if (_uNrmDepthCull >= 0) _gl.Uniform1(_uNrmDepthCull, 1);
        if (_uNrmDepthStep >= 0) _gl.Uniform2(_uNrmDepthStep, stepX, stepY);
        return true;
    }

    float[]? _chkSurf, _chkDepth;

    /// <summary>The probe's: the surface buffer just drawn, read back against the
    /// target's depth on a 4-pixel grid. Waits on the GPU; the probe only.</summary>
    unsafe void CheckSurfaces(GlDisplayRt src)
    {
        RetainedScene.SurfaceCheck = false;
        int sw = src.NormalW, sh = src.NormalH, dw = src.TexW, dh = src.TexH;
        if (_chkSurf == null || _chkSurf.Length < sw * sh * 4) _chkSurf = new float[sw * sh * 4];
        if (_chkDepth == null || _chkDepth.Length < dw * dh) _chkDepth = new float[dw * dh];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, src.NormalFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment1);
        fixed (float* p = _chkSurf) _gl.ReadPixels(0, 0, (uint)sw, (uint)sh, PixelFormat.Rgba, PixelType.Float, p);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, src.Fbo);
        fixed (float* p = _chkDepth) _gl.ReadPixels(0, 0, (uint)dw, (uint)dh, PixelFormat.DepthComponent, PixelType.Float, p);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        for (int y = 0; y < sh; y += 4)
        for (int x = 0; x < sw; x += 4)
        {
            float d = _chkDepth[Math.Min(dh - 1, y * dh / sh) * dw + Math.Min(dw - 1, x * dw / sw)];
            if (d >= 0.99999f) continue;
            RetainedScene.SurfaceDepthPixels++;
            int i = (y * sw + x) * 4;
            float z = _chkSurf[i + 2] * 65536f, id = _chkSurf[i + 3], dz = d * 65536f;
            if (id < 0.5f || z <= 0f) { if (id < 2.5f || id > 3.5f) RetainedScene.SurfaceMissing++; continue; }
            if (z > dz + 64f + dz / 64f) RetainedScene.SurfaceBehind++;
        }
        RetainedScene.SurfaceChecks++;
    }
}
