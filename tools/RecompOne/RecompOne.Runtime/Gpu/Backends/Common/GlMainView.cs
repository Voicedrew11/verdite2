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
    int _uwTrueColor, _uwPlainZ, _uwSnap, _uwPerPixel, _uwDither;

    void InitMainView()
    {
        if (_progWorld == 0) return;
        _uwTrueColor = _gl.GetUniformLocation(_progWorld, "uTrueColor");
        _uwPlainZ = _gl.GetUniformLocation(_progWorld, "uPlainZ");
        _uwSnap = _gl.GetUniformLocation(_progWorld, "uWorldSnap");
        _uwPerPixel = _gl.GetUniformLocation(_progWorld, "uWorldPerPixel");
        _uwDither = _gl.GetUniformLocation(_progWorld, "uWorldDither");
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
        SendWorldFluid();
        SendWorldAtmos();

        // The GTE's centre is in the draw area's pixels; the target's are offset
        // from them by the draw offset and the widescreen margin.
        var v = f.View;
        Span<float> r = [v.R00, v.R01, v.R02, v.R10, v.R11, v.R12, v.R20, v.R21, v.R22];
        float cx = v.Cx + offX - rt.X + rt.Margin, cy = v.Cy + offY - rt.Y;
        SetWorldView(r, v.CamX, v.CamY, v.CamZ, v.Tx, v.Ty, v.Tz, v.H, cx, cy, rt.Wide1x, rt.H, v.H, true, GlVram.Scale);
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
        EndWorldLights();
        _gl.BindVertexArray(0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        EndGpuTimer(query, GpuWork.Batch, 0, Diagnostics.GpuTimes.Pass.World);

        long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        RetainedScene.MainTicks[0] += t1 - t0; RetainedScene.MainTicks[1] += t2 - t1;
        RetainedScene.MainTicks[2] += t3 - t2; RetainedScene.MainTicks[3] += t4 - t3;
        rt.Dirty = true;
        rt.LastDrawFrame = _frame;
        rt.RetainedSerial = RetainedScene.Serial;
        _lastZRt = rt;
        RetainedScene.MainDraws++;
        RetainedScene.MainTriangles += drawn / 3;
        return true;
    }
}
