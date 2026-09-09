using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

public sealed class GlCore : IGpuBackend
{
    [StructLayout(LayoutKind.Sequential)]
    // W is the clip W the vertex shader divides by: the view depth GteDepth
    // recovered, or exactly 1 for everything that had none, which is every vertex
    // this renderer ever saw before.
    struct GlVertex { public float X, Y; public float R, G, B; public float Clut, Texpage; public float U, V; public float W, Z; }

    const int MaxVerts = 0x40000;

    readonly GL _gl;
    readonly IGlVram _vram;
    readonly List<uint> _images = [];
    readonly GlDisplayRt?[] _rts = new GlDisplayRt?[2];
    long _rtStamp;
    long _frame;

    /// <summary>
    /// Upstream advances the frame counter from HostWindow, once per host frame.
    /// This backend cannot: 0016 requires the advance to sit immediately after
    /// the trailing Flush in PresentDisplay, because the depth clear keys on
    /// LastDrawFrame != _frame and bumping it anywhere else makes the tail of the
    /// outgoing frame look like the head of the next one -- which is the bug that
    /// left every frame inheriting the last batch's depths. So this is a no-op and
    /// the counter is advanced where it must be.
    /// </summary>
    public void AdvanceFrame()
    {
    }

    uint _vao, _vbo, _presentVao, _presentVbo, _progPrim, _progPresent, _progPresent24;
    uint _presentFbo, _presentTex;
    int _presentW, _presentH;
    bool _presentNearest;

    uint _postProg, _postFbo, _postTex;
    int _postW, _postH, _postVersion = -1;
    int _uPostTexSize, _uPostOutputSize, _uPostTime, _uPostFrame;
    int _postFrame, _postParamVersion = -1;
    (string Name, float Value)[] _postParams = [];
    int[] _postParamLoc = [];
    readonly System.Diagnostics.Stopwatch _postClock = System.Diagnostics.Stopwatch.StartNew();

    readonly GlVertex[] _verts = new GlVertex[MaxVerts];
    int _count;
    float _drawMinX, _drawMinY, _drawMaxX, _drawMaxY;

    HleDrawEnv _env;

    GlDisplayRt? _kTarget;
    bool _kTransparent;
    int _kImage = -1;
    int _kBlend, _kSetMask, _kCheckMask;
    int _kZMode;
    // The last render target a depth-testing batch was drawn to, for the diagnostic
    // readback: it is the only unambiguous answer to "which depth buffer is this
    // frame's". Diagnostic only — nothing else reads it.
    GlDisplayRt? _lastZRt;
    int _kTwAndX, _kTwAndY, _kTwOrX, _kTwOrY;
    int _kClipX0, _kClipY0, _kClipX1, _kClipY1;
    uint _kRepTex, _kRepClut;
    float _kRepX, _kRepY, _kRepW, _kRepH;
    int _kRepClutCount;
    int _uTexWindow, _uBlend, _uBlendOpaque, _uSetMask, _uCheckMask, _uPosBias, _uFbInv;
    int _uTrueColor;
    int _uAniso;
    // The true-color flag the live display targets were built with. When it drifts
    // from GteDepth.TrueColor the targets carry the wrong pixel format, so they are
    // torn down at the next present and rebuilt (their content survives in VRAM).
    bool _rtsTrueColor;
    int _uRepRect, _uRepClutCount;
    int _uPresentOrigin, _uPresentSize, _uPresentTexSize, _uPresent24Origin, _uPresent24Size;

    public bool Ready { get; private set; }

    readonly bool _legacy;
    int _uVramSize, _uDestSize, _uSemiTrans, _uBlendMode;

    public GlCore(GL gl, IGlVram vram, bool legacy = false)
    {
        _gl = gl;
        _vram = vram;
        _legacy = legacy;
    }

    public unsafe void InitGl()
    {
        _vram.Init();

        string primVs = _legacy ? GlShaders.PrimVs120 : GlShaders.PrimVs;
        string primFs = _legacy ? GlShaders.PrimFs120 : GlShaders.PrimFs;
        string fullVs = _legacy ? GlShaders.FullscreenVs120 : GlShaders.FullscreenVs;
        string presentFs = _legacy ? GlShaders.PresentFs120 : GlShaders.PresentFs;
        string present24Fs = _legacy ? GlShaders.Present24Fs120 : GlShaders.Present24Fs;

        _progPrim = GlShaders.BuildPrim(_gl, primVs, primFs, "prim");
        _progPresent = GlShaders.BuildFullscreen(_gl, fullVs, presentFs, "present");
        _progPresent24 = GlShaders.BuildFullscreen(_gl, fullVs, present24Fs, "present24");
        if (_progPrim == 0 || _progPresent == 0 || _progPresent24 == 0) return;

        _uVramSize = _gl.GetUniformLocation(_progPrim, "uVramSize");
        _uDestSize = _gl.GetUniformLocation(_progPrim, "uDestSize");
        _uSemiTrans = _gl.GetUniformLocation(_progPrim, "uSemiTrans");
        _uBlendMode = _gl.GetUniformLocation(_progPrim, "uBlendMode");

        _uTexWindow = _gl.GetUniformLocation(_progPrim, "uTexWindow");
        _uBlend = _gl.GetUniformLocation(_progPrim, "uBlend");
        _uBlendOpaque = _gl.GetUniformLocation(_progPrim, "uBlendOpaque");
        _uSetMask = _gl.GetUniformLocation(_progPrim, "uSetMask");
        _uCheckMask = _gl.GetUniformLocation(_progPrim, "uCheckMask");
        _uPosBias = _gl.GetUniformLocation(_progPrim, "uPosBias");
        _uFbInv = _gl.GetUniformLocation(_progPrim, "uFbInv");
        _uTrueColor = _gl.GetUniformLocation(_progPrim, "uTrueColor");
        _uAniso = _gl.GetUniformLocation(_progPrim, "uAniso");
        _rtsTrueColor = GteDepth.TrueColor;
        _uRepRect = _gl.GetUniformLocation(_progPrim, "uRepRect");
        _uRepClutCount = _gl.GetUniformLocation(_progPrim, "uRepClutCount");

        _gl.UseProgram(_progPrim);
        _gl.Uniform1(_gl.GetUniformLocation(_progPrim, "uVram"), 0);
        _gl.Uniform1(_gl.GetUniformLocation(_progPrim, "uDest"), 1);
        _gl.Uniform1(_gl.GetUniformLocation(_progPrim, "uExtTex"), 2);
        _gl.Uniform1(_gl.GetUniformLocation(_progPrim, "uRepTex"), 3);
        _gl.Uniform1(_gl.GetUniformLocation(_progPrim, "uRepClut"), 4);
        SetScaleUniform(_progPrim);
        if (_uVramSize >= 0) _gl.Uniform2(_uVramSize, (float)GlVram.Width, GlVram.Height);

        _uPresentOrigin = _gl.GetUniformLocation(_progPresent, "uOrigin");
        _uPresentSize = _gl.GetUniformLocation(_progPresent, "uSize");
        _uPresentTexSize = _gl.GetUniformLocation(_progPresent, "uTexSize");
        _gl.UseProgram(_progPresent);
        _gl.Uniform1(_gl.GetUniformLocation(_progPresent, "uVram"), 0);

        _uPresent24Origin = _gl.GetUniformLocation(_progPresent24, "uOrigin");
        _uPresent24Size = _gl.GetUniformLocation(_progPresent24, "uSize");
        _gl.UseProgram(_progPresent24);
        _gl.Uniform1(_gl.GetUniformLocation(_progPresent24, "uVram"), 0);
        SetScaleUniform(_progPresent24);
        int uVramSize24 = _gl.GetUniformLocation(_progPresent24, "uVramSize");
        if (uVramSize24 >= 0) _gl.Uniform2(uVramSize24, (float)GlVram.Width, GlVram.Height);

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(MaxVerts * sizeof(GlVertex)), null, BufferUsageARB.DynamicDraw);
        uint stride = (uint)sizeof(GlVertex);
        _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)8);
        _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)20);
        _gl.EnableVertexAttribArray(3); _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)24);
        _gl.EnableVertexAttribArray(4); _gl.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, stride, (void*)28);
        _gl.EnableVertexAttribArray(5); _gl.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, stride, (void*)36);
        _gl.EnableVertexAttribArray(6); _gl.VertexAttribPointer(6, 1, VertexAttribPointerType.Float, false, stride, (void*)40);

        // fullscreen quad for present, real vbo since gl_VertexID without arrays does not draw on mesa for some reason?? or i did it wrong?
        _presentVao = _gl.GenVertexArray();
        _presentVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_presentVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _presentVbo);
        float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
        fixed (float* qp = quad)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), qp, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        _presentTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _presentTex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _presentFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _presentFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _presentTex, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        _kClipX1 = 1023; _kClipY1 = 511;
        Ready = true;
    }

    public void SetDrawEnv(in HleDrawEnv env) => _env = env;

    const int FbSlackW = 64;
    const int FbSlackH = 32;

    GlDisplayRt? Classify()
    {
        int clipX = _env.ClipX0, clipY = _env.ClipY0;
        int clipW = _env.ClipX1 - _env.ClipX0 + 1, clipH = _env.ClipY1 - _env.ClipY0 + 1;
        if (clipW <= 0 || clipH <= 0) return null;

        long bestStamp = -1;
        int fbX = 0, fbY = 0, fbW = 0, fbH = 0;
        for (int i = 0; i < GpuHle.RectCount; i++)
        {
            var r = GpuHle.GetRect(i);
            if (!r.Valid || r.W <= 0 || r.H <= 0 || r.Stamp <= bestStamp) continue;

            bool clipInside = clipX >= r.X && clipX + clipW <= r.X + r.W &&
                              clipY >= r.Y && clipY + clipH <= r.Y + r.H;
            bool clipIsFb = clipX <= r.X && clipX + clipW >= r.X + r.W &&
                            clipY <= r.Y && clipY + clipH >= r.Y + r.H &&
                            clipW - r.W <= FbSlackW && clipH - r.H <= FbSlackH;
            if (clipInside) { bestStamp = r.Stamp; fbX = r.X; fbY = r.Y; fbW = r.W; fbH = r.H; }
            else if (clipIsFb) { bestStamp = r.Stamp; fbX = clipX; fbY = clipY; fbW = clipW; fbH = clipH; }
        }
        return bestStamp < 0 ? null : GetOrCreateRt(fbX, fbY, fbW, fbH);
    }

    GlDisplayRt GetOrCreateRt(int fbX, int fbY, int fbW, int fbH)
    {
        int slot = -1;
        for (int i = 0; i < _rts.Length; i++)
            if (_rts[i] is { } rt && rt.X == fbX && rt.Y == fbY)
            {
                bool sameW = rt.W == fbW;
                bool fitsH = rt.H >= fbH && rt.H - fbH <= FbSlackH;
                if (sameW && fitsH && rt.Margin == GpuHle.WideMargin(rt.W))
                {
                    rt.Stamp = ++_rtStamp;
                    return rt;
                }
                slot = i;
                break;
            }

        if (slot < 0)
        {
            slot = 0;
            for (int i = 1; i < _rts.Length; i++)
            {
                if (_rts[i] == null) { slot = i; break; }
                if (_rts[slot] != null && _rts[i]!.Stamp < _rts[slot]!.Stamp) slot = i;
            }
        }

        if (_rts[slot] is { } old)
        {
            if (old.Dirty) Writeback(old);
            old.Destroy(_gl);
            if (old == _lastZRt) _lastZRt = null;
            // A target thrown away takes its depth attachment with it, so anything
            // already depth-tested into it stops occluding what comes next. Worth
            // counting: if this happens inside a frame the Z-buffer is being reset
            // halfway through one.
            if (GteDepth.ZBuffer) GteDepth.ZRtRecreated++;
        }

        var fresh = new GlDisplayRt { X = fbX, Y = fbY, W = fbW, H = fbH, Margin = GpuHle.WideMargin(fbW), Stamp = ++_rtStamp, LastDrawFrame = _frame };
        fresh.Create(_gl);
        _rts[slot] = fresh;
        SyncRtFromVram(fresh, fbX, fbY, fbW, fbH);
        return fresh;
    }

    void Writeback(GlDisplayRt rt)
    {
        int s = GlVram.Scale;
        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, rt.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _vram.Fbo);
        _gl.BlitFramebuffer(rt.Margin * s, 0, (rt.Margin + rt.W) * s, rt.H * s,
            rt.X * s, rt.Y * s, (rt.X + rt.W) * s, (rt.Y + rt.H) * s,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        rt.Dirty = false;
        Assets.Textures.VramTracker.MarkGpuWrite(rt.X, rt.Y, rt.W, rt.H);
    }

    void SyncRtFromVram(GlDisplayRt rt, int rx, int ry, int rw, int rh)
    {
        int x0 = Math.Max(rx, rt.X), y0 = Math.Max(ry, rt.Y);
        int x1 = Math.Min(rx + rw, rt.X + rt.W), y1 = Math.Min(ry + rh, rt.Y + rt.H);
        if (x0 >= x1 || y0 >= y1) return;
        int s = GlVram.Scale;
        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _vram.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, rt.Fbo);
        _gl.BlitFramebuffer(x0 * s, y0 * s, x1 * s, y1 * s,
            (x0 - rt.X + rt.Margin) * s, (y0 - rt.Y) * s, (x1 - rt.X + rt.Margin) * s, (y1 - rt.Y) * s,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    void WritebackDirtyIntersecting(int x, int y, int w, int h)
    {
        foreach (var rt in _rts)
            if (rt is { Dirty: true } && rt.Intersects(x, y, w, h)) Writeback(rt);
    }

    void SyncRtsFromVram(int x, int y, int w, int h)
    {
        foreach (var rt in _rts)
            if (rt != null && rt.Intersects(x, y, w, h)) SyncRtFromVram(rt, x, y, w, h);
    }

    void CheckTextureFeedback(in PrimFlags f)
    {
        if (!f.Textured || f.UseImage) return;
        int px = (f.TPage & 0xF) * 64;
        int py = ((f.TPage >> 4) & 1) * 256;
        int depth = (f.TPage >> 7) & 3;
        int pw = depth == 0 ? 64 : depth == 1 ? 128 : 256;
        foreach (var rt in _rts)
            if (rt is { Dirty: true } && rt.Intersects(px, py, pw, 256))
            {
                Flush();
                Writeback(rt);
            }
    }

    bool DesiredMatches(bool transparent, int blend, int image, int zMode)
    {
        int twAndX = ~(_env.TwMaskX * 8) & 0xFF, twAndY = ~(_env.TwMaskY * 8) & 0xFF;
        int twOrX = (_env.TwOffX & _env.TwMaskX) * 8, twOrY = (_env.TwOffY & _env.TwMaskY) * 8;
        return _kRepTex == _pendingRepTex && _kRepClut == _pendingRepClut
            && (_pendingRepTex == 0 || (_kRepX == _pendingRepX && _kRepY == _pendingRepY
                                        && _kRepW == _pendingRepW && _kRepH == _pendingRepH))
            && _kTransparent == transparent && _kBlend == blend && _kImage == image
            && _kZMode == zMode
            && _kSetMask == (_env.SetMask ? 1 : 0) && _kCheckMask == (_env.CheckMask ? 1 : 0)
            && _kTwAndX == twAndX && _kTwAndY == twAndY && _kTwOrX == twOrX && _kTwOrY == twOrY
            && _kClipX0 == _env.ClipX0 && _kClipY0 == _env.ClipY0 && _kClipX1 == _env.ClipX1 && _kClipY1 == _env.ClipY1;
    }

    void Begin(in PrimFlags f, int vertsNeeded, int zMode = 0)
    {
        bool transparent = f.SemiTrans;
        int blend = f.BlendMode;
        int image = f.UseImage ? f.Image : -1;
        var target = Classify();
        if (_count > 0 && (target != _kTarget || !DesiredMatches(transparent, blend, image, zMode))) Flush();
        if (_count + vertsNeeded > MaxVerts) Flush();
        CheckTextureFeedback(f);

        _kTarget = target;
        _kImage = image;
        _kTransparent = transparent; _kBlend = blend;
        _kZMode = zMode;
        _kSetMask = _env.SetMask ? 1 : 0; _kCheckMask = _env.CheckMask ? 1 : 0;
        _kTwAndX = ~(_env.TwMaskX * 8) & 0xFF; _kTwAndY = ~(_env.TwMaskY * 8) & 0xFF;
        _kTwOrX = (_env.TwOffX & _env.TwMaskX) * 8; _kTwOrY = (_env.TwOffY & _env.TwMaskY) * 8;
        _kClipX0 = _env.ClipX0; _kClipY0 = _env.ClipY0; _kClipX1 = _env.ClipX1; _kClipY1 = _env.ClipY1;
        _kRepTex = _pendingRepTex; _kRepClut = _pendingRepClut; _kRepClutCount = _pendingRepClutCount;
        _kRepX = _pendingRepX; _kRepY = _pendingRepY; _kRepW = _pendingRepW; _kRepH = _pendingRepH;
    }

    uint _pendingRepTex, _pendingRepClut;
    int _pendingRepClutCount = 16;
    float _pendingRepX, _pendingRepY, _pendingRepW = 1, _pendingRepH = 1;

    readonly Dictionary<Assets.ReplacementTexture, uint> _repTextures = [];
    readonly Dictionary<Assets.ReplacementClut, uint> _repCluts = [];

    void ResolveReplacement(in PrimFlags f, int uMin, int vMin, int uMax, int vMax)
    {
        _pendingRepTex = 0;
        _pendingRepClut = 0;

        if (!f.Textured || f.UseImage) return;

        int twAndX = ~(_env.TwMaskX * 8) & 0xFF, twAndY = ~(_env.TwMaskY * 8) & 0xFF;
        int twOrX = (_env.TwOffX & _env.TwMaskX) * 8, twOrY = (_env.TwOffY & _env.TwMaskY) * 8;

        if (!Assets.Textures.TextureResolver.Resolve(f.TPage, f.Clut, uMin, vMin, uMax, vMax,
                twAndX, twAndY, twOrX, twOrY, out var res))
            return;

        if (res.Texture is { Mode: Assets.TextureMode.Rgba } tex)
        {
            _pendingRepTex = EnsureRepTexture(tex);
            _pendingRepX = res.Rect.U0;
            _pendingRepY = res.Rect.V0;
            _pendingRepW = res.Rect.W;
            _pendingRepH = res.Rect.H;
        }

        if (_pendingRepTex == 0 && res.Clut is { } clut && res.Rect.ClutCount > 0 && clut.Count == res.Rect.ClutCount)
        {
            _pendingRepClut = EnsureRepClut(clut);
            _pendingRepClutCount = clut.Count;
        }
    }

    unsafe uint EnsureRepTexture(Assets.ReplacementTexture tex)
    {
        if (_repTextures.TryGetValue(tex, out uint handle)) return handle;

        _gl.ActiveTexture(TextureUnit.Texture7);
        handle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, handle);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)tex.Width, (uint)tex.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, tex.Rgba);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _repTextures[tex] = handle;
        return handle;
    }

    unsafe uint EnsureRepClut(Assets.ReplacementClut clut)
    {
        if (_repCluts.TryGetValue(clut, out uint handle)) return handle;

        _gl.ActiveTexture(TextureUnit.Texture7);
        handle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, handle);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)clut.Count, 1, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, clut.Rgba);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _repCluts[clut] = handle;
        return handle;
    }

    bool DitherOf(in PrimFlags f) => _env.Dither && (f.Gouraud || (f.Textured && !f.RawTexture));

    // Crossing vertices a single display flip must deliver before the target's
    // margin counts as carrying a world. See the latch in V.
    const int MarginVertsToLatch = 32;

    GlVertex V(in HleVertex v, in PrimFlags f, bool dither)
    {
        // The latch records where the game itself drew past its own edge, and it
        // takes a world's worth of crossings in one display flip to do it: a
        // frame of gameplay puts hundreds of vertices out there, while an
        // oversized clear rect -- genuine game output, and the splash draws one
        // every MDEC frame -- contributes two. A primitive the widescreen patch
        // widened crosses the edge by construction and never counts at all.
        if (!GpuHle.PortWidenedPrim && _kTarget is { Margin: > 0 } && (v.X < _env.ClipX0 || v.X > _env.ClipX1))
        {
            if (_kTarget.MarginVertFlip != GpuHle.DisplayFlip)
            {
                _kTarget.MarginVertFlip = GpuHle.DisplayFlip;
                _kTarget.MarginVerts = 0;
            }
            if (++_kTarget.MarginVerts >= MarginVertsToLatch)
                _kTarget.MarginContentFlip = GpuHle.DisplayFlip;
        }
        bool raw = f.Textured && f.RawTexture;
        float cr = raw ? 128f : v.R, cg = raw ? 128f : v.G, cb = raw ? 128f : v.B;
        int tpage = f.UseImage ? 0x4000 : f.Textured ? (f.TPage & 0x1FF) : 0x8000;
        if (dither && _pendingRepTex == 0) tpage |= 0x400;
        if (_pendingRepTex != 0) tpage |= 0x2000;
        else if (_pendingRepClut != 0) tpage |= 0x1000;
        if (_count == 0)
        {
            _drawMinX = _drawMaxX = v.X;
            _drawMinY = _drawMaxY = v.Y;
        }
        else
        {
            if (v.X < _drawMinX) _drawMinX = v.X;
            if (v.X > _drawMaxX) _drawMaxX = v.X;
            if (v.Y < _drawMinY) _drawMinY = v.Y;
            if (v.Y > _drawMaxY) _drawMaxY = v.Y;
        }

        return new GlVertex
        {
            X = v.X, Y = v.Y,
            R = cr, G = cg, B = cb,
            Clut = f.Clut & 0x7FFF,
            Texpage = tpage,
            U = v.U, V = v.V,
            W = v.HasPersp && v.Z > 0f ? v.Z : 1f,
            Z = v.HasGteZ && v.Z > 0f ? v.Z : 0f,
        };
    }

    public void DrawTri(in HleVertex a, in HleVertex b, in HleVertex c, in PrimFlags f)
    {
        ResolveReplacement(f,
            (int)Math.Min(a.U, Math.Min(b.U, c.U)), (int)Math.Min(a.V, Math.Min(b.V, c.V)),
            (int)Math.Max(a.U, Math.Max(b.U, c.U)), (int)Math.Max(a.V, Math.Max(b.V, c.V)));
        // 0 = painter's (2D, or a vertex missed). 1 = opaque 3D, test and write.
        // 2 = semi-transparent 3D, test but leave Z so overlapping additives still
        // blend in table order.
        int zMode = 0;
        if (a.HasGteZ && b.HasGteZ && c.HasGteZ)
            zMode = f.SemiTrans ? 2 : 1;
        Begin(f, 3, zMode);
        bool dith = DitherOf(f);
        _verts[_count++] = V(a, f, dith); _verts[_count++] = V(b, f, dith); _verts[_count++] = V(c, f, dith);
    }

    public void DrawRect(in HleRect r, in PrimFlags f)
    {
        ResolveReplacement(f, r.U, r.V, r.U + Math.Max(0, r.W - 1), r.V + Math.Max(0, r.H - 1));
        Begin(f, 6);
        var a = new HleVertex { X = r.X, Y = r.Y, R = r.R, G = r.G, B = r.B, U = r.U, V = r.V };
        var b = new HleVertex { X = r.X + r.W, Y = r.Y, R = r.R, G = r.G, B = r.B, U = (short)(r.U + r.W), V = r.V };
        var c = new HleVertex { X = r.X, Y = r.Y + r.H, R = r.R, G = r.G, B = r.B, U = r.U, V = (short)(r.V + r.H) };
        var d = new HleVertex { X = r.X + r.W, Y = r.Y + r.H, R = r.R, G = r.G, B = r.B, U = (short)(r.U + r.W), V = (short)(r.V + r.H) };
        _verts[_count++] = V(a, f, false); _verts[_count++] = V(b, f, false); _verts[_count++] = V(c, f, false);
        _verts[_count++] = V(b, f, false); _verts[_count++] = V(d, f, false); _verts[_count++] = V(c, f, false);
    }

    public void DrawLine(in HleVertex a, in HleVertex b, in PrimFlags f)
    {
        _pendingRepTex = 0;
        _pendingRepClut = 0;
        Begin(f, 6);
        bool dith = _env.Dither;
        float x1 = a.X, y1 = a.Y;
        float x2 = b.X, y2 = b.Y;
        float dx = x2 - x1, dy = y2 - y1;

        if (dx == 0 && dy == 0)
        {
            LineVert(x1, y1, a, f, dith); LineVert(x1 + 1, y1, a, f, dith); LineVert(x1 + 1, y1 + 1, a, f, dith);
            LineVert(x1 + 1, y1 + 1, a, f, dith); LineVert(x1, y1 + 1, a, f, dith); LineVert(x1, y1, a, f, dith);
            return;
        }

        float xo, yo;
        if (Math.Abs(dx) > Math.Abs(dy)) { xo = 0; yo = 1; if (dx > 0) x2++; else x1++; }
        else { xo = 1; yo = 0; if (dy > 0) y2++; else y1++; }

        LineVert(x1, y1, a, f, dith); LineVert(x2, y2, b, f, dith); LineVert(x2 + xo, y2 + yo, b, f, dith);
        LineVert(x2 + xo, y2 + yo, b, f, dith); LineVert(x1 + xo, y1 + yo, a, f, dith); LineVert(x1, y1, a, f, dith);
    }

    void LineVert(float x, float y, in HleVertex src, in PrimFlags f, bool dither)
    {
        var v = src; v.X = x; v.Y = y;
        _verts[_count++] = V(v, f, dither);
    }

    public void FillRect(int x, int y, int w, int h, ushort color15)
    {
        Flush();
        _vram.Fill(x, y, w, h, color15);
        foreach (var rt in _rts)
        {
            if (rt == null || !rt.Intersects(x, y, w, h)) continue;
            if (rt.Covers(x, y, x + w - 1, y + h - 1))
            {
                FillRtFull(rt, color15);
                rt.Dirty = false;
                rt.LastDrawFrame = _frame;
                if (rt.Margin > 0) rt.MarginContentFlip = GpuHle.DisplayFlip;
            }
            else SyncRtFromVram(rt, x, y, w, h);
        }
    }

    void FillRtFull(GlDisplayRt rt, ushort color15)
    {
        float r = (color15 & 0x1F) / 31f, g = ((color15 >> 5) & 0x1F) / 31f, b = ((color15 >> 10) & 0x1F) / 31f;
        float a = (color15 & 0x8000) != 0 ? 1f : 0f;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ClearColor(r, g, b, a);
        _gl.ClearDepth(1.0);
        _gl.DepthMask(true);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        rt.ZGen = GteDepth.Generation;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // ---- framebuffer readback snapshots -------------------------------------
    // A modal sub-loop -- the in-game menu, a shop, an NPC's message box -- keeps
    // the world behind it by reading the finished frame out of VRAM into system
    // RAM once (StoreImage) and blitting it back at the head of every iteration
    // (LoadImage), so each pass erases the last one's drawing without redrawing
    // the world. That roundtrip goes through the console's own resolution: VRAM
    // is read at 1x whatever the render scale is, so the restore stamped a 1x
    // picture over the display area every frame and every one of those scenes
    // rendered at one sample per game pixel. It shows as exactly the game's own
    // 320 columns dropping to 1x with the widescreen margin -- which never goes
    // through VRAM (Writeback copies a target's middle W columns only) -- staying
    // at full scale beside them.
    //
    // So a readback also takes a *scaled* copy of the region on the GPU, and an
    // upload whose 1x pixels are byte-identical to that readback is served by
    // blitting the copy back rather than by uploading. The key is the content and
    // the size, not the address, because the frame may be restored into either
    // display buffer. Anything the game actually changed in RAM between the read
    // and the write fails the compare and takes the 1x path, which is what every
    // upload did before -- so a texture built in RAM, an MDEC frame or a decoded
    // sprite is untouched.
    sealed class VramSnap
    {
        public int W, H, Scale;
        public uint Tex, Fbo;
        public ushort[] Data = [];
        public long Stamp;
    }

    readonly VramSnap?[] _snaps = new VramSnap?[2];
    long _snapStamp;
    static int _snapHit, _snapMiss;
    bool _snapVerified;
    static double _snapWindow;

    // Below this a readback is a sprite or a small tile rather than a frame, and
    // a snapshot of it would evict the one that matters.
    const int SnapMinArea = 64 * 64;

    void SnapProbe()
    {
        if (!GlVram.SnapshotProbe) return;
        double now = Environment.TickCount64 / 1000.0;
        if (now - _snapWindow < 2.0) return;
        Console.WriteLine($"[vramsnap] restored {_snapHit}, uploaded 1x {_snapMiss}");
        _snapHit = _snapMiss = 0;
        _snapWindow = now;
    }

    void SnapTake(int x, int y, int w, int h, ReadOnlySpan<ushort> px)
    {
        if (!GlVram.Snapshots) return;
        int n = w * h;
        if (w <= 0 || h <= 0 || n < SnapMinArea || px.Length < n) return;
        // Neither ReadRect nor WriteRect wraps, so a rect off the end of VRAM is
        // already undefined; do not carry one into a copy that could be restored
        // somewhere else entirely.
        if (x < 0 || y < 0 || x + w > VramShadow.Width || y + h > VramShadow.Height) return;

        int s = GlVram.Scale;
        int slot = -1;
        for (int i = 0; i < _snaps.Length; i++)
            if (_snaps[i] is { } fit && fit.W == w && fit.H == h && fit.Scale == s) { slot = i; break; }
        if (slot < 0)
        {
            slot = 0;
            for (int i = 1; i < _snaps.Length; i++)
            {
                if (_snaps[i] == null) { slot = i; break; }
                if (_snaps[slot] != null && _snaps[i]!.Stamp < _snaps[slot]!.Stamp) slot = i;
            }
        }

        var snap = _snaps[slot];
        if (snap == null || snap.W != w || snap.H != h || snap.Scale != s)
        {
            if (snap != null) SnapDestroy(snap);
            snap = new VramSnap { W = w, H = h, Scale = s, Data = new ushort[n] };
            snap.Tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, snap.Tex);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _gl.TexImage2D<ushort>(TextureTarget.Texture2D, 0, InternalFormat.Rgb5A1, (uint)(w * s), (uint)(h * s), 0,
                PixelFormat.Rgba, PixelType.UnsignedShort1555Rev, new ushort[(long)w * s * h * s].AsSpan());
            snap.Fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, snap.Fbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, snap.Tex, 0);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _snaps[slot] = snap;
        }

        px[..n].CopyTo(snap.Data);
        snap.Stamp = ++_snapStamp;

        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _vram.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, snap.Fbo);
        _gl.BlitFramebuffer(x * s, y * s, (x + w) * s, (y + h) * s,
            0, 0, w * s, h * s, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    bool SnapRestore(int x, int y, int w, int h, ReadOnlySpan<ushort> px)
    {
        if (!GlVram.Snapshots) return false;
        int n = w * h;
        if (w <= 0 || h <= 0 || n < SnapMinArea || px.Length < n) return false;

        int s = GlVram.Scale;
        foreach (var snap in _snaps)
        {
            if (snap is null || snap.W != w || snap.H != h || snap.Scale != s) continue;
            if (!px[..n].SequenceEqual(snap.Data)) continue;

            _gl.Disable(EnableCap.ScissorTest);
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, snap.Fbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _vram.Fbo);
            _gl.BlitFramebuffer(0, 0, w * s, h * s,
                x * s, y * s, (x + w) * s, (y + h) * s,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            snap.Stamp = ++_snapStamp;
            _snapHit++;
            // Counting restores says the path fires, not that it wrote the right
            // pixels. Under the probe the first one is read straight back out of
            // VRAM at 1x and compared against the upload it replaced: a scaled
            // copy of the same frame must downsample to the same picture, so any
            // disagreement is the blit's geometry and not the resolution.
            if (GlVram.SnapshotProbe && !_snapVerified)
            {
                _snapVerified = true;
                var back = new ushort[n];
                _vram.ReadRect(x, y, w, h, back);
                int diff = 0;
                for (int i = 0; i < n; i++) if (back[i] != px[i]) diff++;
                Console.WriteLine($"[vramsnap] verify {w}x{h} at {x},{y}: {diff} of {n} pixels differ " +
                                  $"({diff * 100.0 / n:F2}%)");
            }
            return true;
        }
        _snapMiss++;
        return false;
    }

    void SnapDestroy(VramSnap snap)
    {
        if (snap.Fbo != 0) _gl.DeleteFramebuffer(snap.Fbo);
        if (snap.Tex != 0) _gl.DeleteTexture(snap.Tex);
        snap.Fbo = snap.Tex = 0;
    }

    public void CopyVram(int sx, int sy, int dx, int dy, int w, int h)
    {
        Flush();
        WritebackDirtyIntersecting(sx, sy, w, h);
        _vram.CopyRect(sx, sy, dx, dy, w, h);
        SyncRtsFromVram(dx, dy, w, h);
    }

    public void WriteVram(int x, int y, int w, int h, ReadOnlySpan<ushort> px)
    {
        Flush();
        // A restore of a frame this backend read out at scale writes the scaled
        // copy instead of the 1x pixels the game is handing back; everything else
        // uploads as it always did.
        if (!SnapRestore(x, y, w, h, px)) _vram.WriteRect(x, y, w, h, px);
        SyncRtsFromVram(x, y, w, h);
        SnapProbe();
    }

    public void ReadVram(int x, int y, int w, int h, Span<ushort> px)
    {
        Flush();
        WritebackDirtyIntersecting(x, y, w, h);
        _vram.ReadRect(x, y, w, h, px);
        SnapTake(x, y, w, h, px);
    }

    public int RegisterImage(ReadOnlySpan<byte> rgba, int width, int height)
    {
        _gl.ActiveTexture(TextureUnit.Texture7);
        uint t = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, t);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        _gl.ActiveTexture(TextureUnit.Texture0);

        _images.Add(t);
        return _images.Count - 1;
    }

    public void Flush()
    {
        if (_count == 0) return;

        var rt = _kTarget;
        uint destTex;
        if (rt == null)
        {
            _vram.BindDraw();
            destTex = _vram.Texture;
        }
        else
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
            _gl.Viewport(0, 0, (uint)rt.TexW, (uint)rt.TexH);
            destTex = rt.Tex;
        }
        int destW = rt == null ? GlVram.Width : rt.TexW;
        int destH = rt == null ? GlVram.Height : rt.TexH;

        GpuGlAccess.Gl = _gl;
        GpuGlAccess.TargetFbo = rt == null ? _vram.Fbo : rt.Fbo;
        GpuGlAccess.TargetWidth = destW;
        GpuGlAccess.TargetHeight = destH;
        GpuGlAccess.TargetOriginX = rt == null ? 0 : rt.X;
        GpuGlAccess.TargetOriginY = rt == null ? 0 : rt.Y;
        GpuGlAccess.TargetMargin = rt == null ? 0 : rt.Margin;

        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.ScissorTest);
        int s = GlVram.Scale;

        // Depth test is per-batch: opaque 3D writes, semi-transparent 3D tests
        // without writing, 2D (and everything while the setting is off) keeps
        // the console's painter's algorithm. First draw onto an RT after Present
        // — or after the setting was flipped — clears the attachment so last
        // frame's depths cannot occlude this one. The clear is not gated on this
        // batch's mode, so a 2D primitive arriving first cannot skip it.
        if (rt != null && GteDepth.ZBuffer && (rt.LastDrawFrame != _frame || rt.ZGen != GteDepth.Generation))
        {
            _gl.Disable(EnableCap.ScissorTest);
            _gl.DepthMask(true);
            _gl.ClearDepth(1.0);
            _gl.Clear(ClearBufferMask.DepthBufferBit);
            _gl.Enable(EnableCap.ScissorTest);
            rt.ZGen = GteDepth.Generation;
        }
        if (_kZMode != 0)
        {
            if (rt != null) { GteDepth.ZBatchRt++; _lastZRt = rt; } else GteDepth.ZBatchVram++;
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(_kZMode == 1);
        }
        else
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
        }

        int clipX0, clipY0, clipX1, clipY1;
        if (rt == null)
        {
            clipX0 = _kClipX0; clipY0 = _kClipY0; clipX1 = _kClipX1; clipY1 = _kClipY1;
        }
        else
        {
            clipX0 = _kClipX0 - rt.X + rt.Margin; clipY0 = _kClipY0 - rt.Y;
            clipX1 = _kClipX1 - rt.X + rt.Margin; clipY1 = _kClipY1 - rt.Y;
            if (rt.Margin > 0 && _kClipX0 <= rt.X && _kClipX1 >= rt.X + rt.W - 1) { clipX0 = 0; clipX1 = rt.Wide1x - 1; }
        }

        int bx0 = (int)Math.Floor(_drawMinX) + (rt == null ? 0 : rt.Margin - rt.X);
        int by0 = (int)Math.Floor(_drawMinY) - (rt == null ? 0 : rt.Y);
        int bx1 = (int)Math.Ceiling(_drawMaxX) + (rt == null ? 0 : rt.Margin - rt.X);
        int by1 = (int)Math.Ceiling(_drawMaxY) - (rt == null ? 0 : rt.Y);

        int rx0 = Math.Max(clipX0, bx0), ry0 = Math.Max(clipY0, by0);
        int rx1 = Math.Min(clipX1, bx1), ry1 = Math.Min(clipY1, by1);

        _gl.Scissor(clipX0 * s, clipY0 * s,
            (uint)Math.Max(0, (clipX1 - clipX0 + 1) * s), (uint)Math.Max(0, (clipY1 - clipY0 + 1) * s));

        int readX = Math.Max(0, rx0 * s);
        int readY = Math.Max(0, ry0 * s);
        int readW = Math.Max(0, (rx1 - rx0 + 1) * s);
        int readH = Math.Max(0, (ry1 - ry0 + 1) * s);
        destTex = _vram.BeginDestRead(destTex, destW, destH, readX, readY, readW, readH);
        RebindTarget(rt);

        _gl.UseProgram(_progPrim);
        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _vram.Texture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, destTex);
        if (_kImage >= 0 && _kImage < _images.Count)
        {
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, _images[_kImage]);
        }
        if (_kRepTex != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture3);
            _gl.BindTexture(TextureTarget.Texture2D, _kRepTex);
            _gl.Uniform4(_uRepRect, _kRepX, _kRepY, _kRepW, _kRepH);
        }
        if (_kRepClut != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture4);
            _gl.BindTexture(TextureTarget.Texture2D, _kRepClut);
            _gl.Uniform1(_uRepClutCount, (float)_kRepClutCount);
        }
        _gl.ActiveTexture(TextureUnit.Texture0);
        if (rt != null)
        {
            _gl.Uniform2(_uPosBias, (float)(rt.Margin - rt.X), (float)(-rt.Y));
            _gl.Uniform2(_uFbInv, 2f / rt.Wide1x, 2f / rt.H);
        }
        else
        {
            _gl.Uniform2(_uPosBias, 0f, 0f);
            _gl.Uniform2(_uFbInv, 2f / VramShadow.Width, 2f / VramShadow.Height);
        }
        if (_uTrueColor >= 0) _gl.Uniform1(_uTrueColor, GteDepth.TrueColor ? 1f : 0f);
        // A plain uniform the next batch reads: unlike true color, changing the
        // anisotropy rebuilds nothing.
        GteDepth.AnisotropyLive = _uAniso >= 0;
        if (_uAniso >= 0) _gl.Uniform1(_uAniso, (float)GteDepth.Anisotropy);
        if (_legacy)
        {
            _gl.Uniform4(_uTexWindow, (float)_kTwAndX, _kTwAndY, _kTwOrX, _kTwOrY);
            _gl.Uniform1(_uSetMask, _kSetMask == 1 ? 1f : 0f);
            _gl.Uniform1(_uCheckMask, _kCheckMask == 1 ? 1f : 0f);
            if (_uDestSize >= 0) _gl.Uniform2(_uDestSize, (float)destW, destH);
            if (_uSemiTrans >= 0) _gl.Uniform1(_uSemiTrans, _kTransparent ? 1f : 0f);
            if (_uBlendMode >= 0) _gl.Uniform1(_uBlendMode, (float)_kBlend);
        }
        else
        {
            _gl.Uniform4(_uTexWindow, _kTwAndX, _kTwAndY, _kTwOrX, _kTwOrY);
            _gl.Uniform1(_uSetMask, _kSetMask == 1 ? 1f : 0f);
            _gl.Uniform1(_uCheckMask, _kCheckMask);
            _gl.Uniform4(_uBlendOpaque, 1f, 1f, 1f, 0f);
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferSubData<GlVertex>(BufferTargetARB.ArrayBuffer, 0, _verts.AsSpan(0, _count));

        if (_legacy)
        {
            _gl.Disable(EnableCap.Blend);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
        }
        else if (!_kTransparent)
        {
            _gl.Disable(EnableCap.Blend);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
        }
        else
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFuncSeparate(BlendingFactor.Src1Color, BlendingFactor.Src1Alpha, BlendingFactor.One, BlendingFactor.Zero);
            if (_kBlend == 2)
            {
                _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                SetBlend(0f, 1f);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);

                _vram.BeginDestRead(destTex, destW, destH, readX, readY, readW, readH);
                RebindTarget(rt);
                _gl.BlendEquationSeparate(BlendEquationModeEXT.FuncReverseSubtract, BlendEquationModeEXT.FuncAdd);
                SetBlend(1f, 1f);
                _gl.Uniform4(_uBlendOpaque, 0f, 0f, 0f, 1f);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
            }
            else
            {
                _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                SetBlend(_kBlend switch { 0 => 0.5f, 3 => 0.25f, _ => 1f }, _kBlend == 0 ? 0.5f : 1f);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
            }
        }

        _gl.Disable(EnableCap.ScissorTest);
        if (rt != null) { rt.Dirty = true; rt.LastDrawFrame = _frame; }
        else
        {
            int x0 = Math.Max(_kClipX0, (int)Math.Floor(_drawMinX));
            int y0 = Math.Max(_kClipY0, (int)Math.Floor(_drawMinY));
            int x1 = Math.Min(_kClipX1, (int)Math.Ceiling(_drawMaxX));
            int y1 = Math.Min(_kClipY1, (int)Math.Ceiling(_drawMaxY));
            if (x1 >= x0 && y1 >= y0)
                Assets.Textures.VramTracker.MarkGpuWrite(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }
        _count = 0;
    }

    void SetBlend(float src, float dst) => _gl.Uniform4(_uBlend, src, src, src, dst);

    void SetScaleUniform(uint prog)
    {
        int loc = _gl.GetUniformLocation(prog, "uScale");
        if (loc < 0) return;
        if (_legacy) _gl.Uniform1(loc, (float)GlVram.Scale);
        else _gl.Uniform1(loc, GlVram.Scale);
    }

    /// <summary>Read the finished frame's depth attachment back and hand it to
    /// GteDepth to reduce. Diagnostic only, one frame when asked: it stalls the
    /// pipeline, and it is the only way to see what the Z-buffer actually decided
    /// rather than what the submission order suggests it should have.</summary>
    unsafe void CaptureDepthMap(GlDisplayRt rt)
    {
        int w = rt.TexW, h = rt.TexH;
        if (w <= 0 || h <= 0) return;

        var buf = new float[(long)w * h];
        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, rt.Fbo);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        fixed (float* p = buf)
            _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.DepthComponent, PixelType.Float, p);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        GteDepth.SetDepthMap(buf, w, h);
    }

    void RebindTarget(GlDisplayRt? rt)
    {
        if (rt == null)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _vram.Fbo);
            _gl.Viewport(0, 0, (uint)GlVram.Width, (uint)GlVram.Height);
        }
        else
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
            _gl.Viewport(0, 0, (uint)rt.TexW, (uint)rt.TexH);
        }
    }

    public void ClearMarginLatches()
    {
        foreach (var t in _rts)
        {
            if (t == null) continue;
            t.MarginContentFlip = -1000;
            t.MarginVerts = 0;
            t.MarginVertFlip = -1;
        }
    }

    public void Present(in HleDispEnv disp) => PresentDisplay(disp.X, disp.Y, disp.W, disp.H, disp.Rgb24);

    public unsafe (uint tex, int w, int h, float aspect) PresentDisplay(int dispX, int dispY, int w, int h, bool rgb24 = false, int outW = 0, int outH = 0)
    {
        if (!Ready || w <= 0 || h <= 0) return (0, 0, 0, GpuHle.OutputAspect);
        // Flush before advancing the counter. The depth clear keys on
        // LastDrawFrame != _frame, so bumping the frame first makes this trailing
        // flush — the tail of the frame that is ending — look like the head of the
        // next one: it clears the depth buffer and stamps the new frame number, so
        // the next frame's real first draw skips its clear and inherits whatever
        // this last batch wrote.
        Flush();
        _frame++;

        // True color was toggled: the live targets have the wrong pixel format.
        // Flush already drained this frame's batch, so no draw is mid-flight; write
        // each target's content back to VRAM and drop it. The next draw recreates
        // it in the new format and re-syncs from VRAM, and this present falls back
        // to VRAM (which just received the writeback) for the one transition frame.
        if (_rtsTrueColor != GteDepth.TrueColor)
        {
            _rtsTrueColor = GteDepth.TrueColor;
            for (int i = 0; i < _rts.Length; i++)
                if (_rts[i] is { } rt)
                {
                    if (rt.Dirty) Writeback(rt);
                    rt.Destroy(_gl);
                    _rts[i] = null;
                }
            _kTarget = null;
            _lastZRt = null;
        }

        for (int i = 0; i < _rts.Length; i++)
        {
            if (_rts[i] is not { } rt) continue;
            if (rt.Dirty) Writeback(rt);
            if (_frame - rt.LastDrawFrame > 300)
            {
                rt.Destroy(_gl);
                _rts[i] = null;
            }
        }

        GlDisplayRt? src = null;
        if (!rgb24)
            foreach (var rt in _rts)
            {
                // No freshness gate on the present counter: the host can present
                // many times between two drawn frames -- VSync off, or a monitor
                // refresh the game's 30 fps cannot match -- and a count-based gate
                // then rejects both targets and drops the picture to the plain
                // VRAM texture at 4:3, which is the wide margins flashing black.
                // Writeback above copies every dirty target's middle columns into
                // VRAM on every present and WriteVram syncs direct writes back
                // into targets, so a target containing the display area is never
                // staler than the fallback it replaces; idle targets are
                // destroyed at 300 frames below.
                if (rt == null) continue;
                if (dispX < rt.X || dispY < rt.Y || dispX + w > rt.X + rt.W || dispY + h > rt.Y + rt.H) continue;
                if (src == null || rt.LastDrawFrame > src.LastDrawFrame) src = rt;
            }
        // A wide target whose margin columns no scene has ever drawn would present
        // invented picture at the sides -- the boot splash, whose frames arrive by
        // MDEC, flapped between such a target and the 4:3 fallback. Refuse targets
        // that never latched margin content; a target that did keeps serving, which
        // is what keeps the in-game menu, dialogs, shops and signs wide instead of
        // collapsing to the 320-wide 4:3 fallback the moment the world render stops.
        if (src is { Margin: > 0 } && src.MarginContentFlip < 0)
            src = null;
        // Only the non-rgb24 path searches for a target, so src is meaningful only
        // there; an rgb24 present (FMV) draws raw VRAM by a different route and would
        // otherwise be miscounted as the 4:3 margin fallback the census is watching for.
        if (GpuHle.PresentProbe && !rgb24)
        {
            if (src is { Margin: > 0 }) GpuHle.PresentWide++;
            else if (src != null) GpuHle.PresentPlain++;
            else GpuHle.PresentFallback++;
            double now = Environment.TickCount64 / 1000.0;
            if (now - GpuHle.PresentWindowStart >= 2.0)
            {
                Console.WriteLine($"[present] wide {GpuHle.PresentWide}, plain {GpuHle.PresentPlain}, " +
                                  $"vram fallback {GpuHle.PresentFallback}");
                GpuHle.PresentWide = GpuHle.PresentPlain = GpuHle.PresentFallback = 0;
                GpuHle.PresentWindowStart = now;
            }
        }

        // The target the frame's depth batches actually went to — not the one being
        // presented (with two buffers that is last frame's) and not the most
        // recently drawn (a full-screen fill stamps LastDrawFrame too, so that can
        // be a buffer which was just cleared).
        // A target that has since been destroyed has Fbo 0, which is the *default*
        // framebuffer — reading that would report an empty depth buffer rather than
        // no answer. Leave the request standing and try the next frame instead.
        if (GteDepth.WantDepthMap && _lastZRt is { Fbo: not 0 }) CaptureDepthMap(_lastZRt);

        int w1x = src != null ? w + src.Margin * 2 : w;
        int h1x = h;
        float aspect = src is { Margin: > 0 } ? GpuHle.WideAspect : src != null ? GpuHle.SourceAspect : GpuHle.OutputAspect;


        GpuHle.LastDisplayW = w;
        GpuHle.LastDisplayH = h;

        int presentScale = GlVram.Scale;
        int fbW = w1x * presentScale;
        int fbH = h1x * presentScale;
        EnsurePresentSize(fbW, fbH, GlVram.Scale == 1);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _presentFbo);
        _gl.Viewport(0, 0, (uint)fbW, (uint)fbH);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.CullFace);

        _gl.UseProgram(rgb24 ? _progPresent24 : _progPresent);
        _gl.BindVertexArray(_presentVao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, src?.Tex ?? _vram.Texture);
        if (rgb24)
        {
            _gl.Uniform2(_uPresent24Origin, (float)dispX, dispY);
            _gl.Uniform2(_uPresent24Size, (float)w, h);
        }
        else if (src != null)
        {
            _gl.Uniform2(_uPresentOrigin, (float)(dispX - src.X), dispY - src.Y);
            _gl.Uniform2(_uPresentSize, (float)w1x, h1x);
            _gl.Uniform2(_uPresentTexSize, (float)src.Wide1x, src.H);
        }
        else
        {
            _gl.Uniform2(_uPresentOrigin, (float)dispX, dispY);
            _gl.Uniform2(_uPresentSize, (float)w, h);
            _gl.Uniform2(_uPresentTexSize, (float)VramShadow.Width, VramShadow.Height);
        }
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        uint outTex = ApplyPostFx(_presentTex, fbW, fbH);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (outTex, fbW, fbH, aspect);
    }
    
    //support for post-fx shaders to be loaded, so you can have cool shaders (this was too anonying to implement)
    unsafe uint ApplyPostFx(uint srcTex, int w, int h)
    {
        if (!PostFx.Active) return srcTex;
        if (!EnsurePostProgram()) return srcTex;

        if (_postTex == 0)
        {
            _postTex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _postTex);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            _postFbo = _gl.GenFramebuffer();
        }
        if (w != _postW || h != _postH)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _postTex);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _postFbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _postTex, 0);
            _postW = w; _postH = h;
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _postFbo);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ScissorTest);

        _gl.UseProgram(_postProg);
        _gl.BindVertexArray(_presentVao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, srcTex);
        if (_uPostTexSize >= 0) _gl.Uniform2(_uPostTexSize, (float)w, h);
        if (_uPostOutputSize >= 0) _gl.Uniform2(_uPostOutputSize, (float)w, h);
        if (_uPostTime >= 0) _gl.Uniform1(_uPostTime, (float)_postClock.Elapsed.TotalSeconds);
        if (_uPostFrame >= 0) _gl.Uniform1(_uPostFrame, _postFrame++);
        ApplyPostParams();
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        return _postTex;
    }

    void ApplyPostParams()
    {
        int version = PostFx.ParamVersion;
        if (version != _postParamVersion)
        {
            _postParamVersion = version;
            _postParams = PostFx.SnapshotParams();
            _postParamLoc = new int[_postParams.Length];
            for (int i = 0; i < _postParams.Length; i++)
                _postParamLoc[i] = _gl.GetUniformLocation(_postProg, _postParams[i].Name);
        }

        for (int i = 0; i < _postParams.Length; i++)
            if (_postParamLoc[i] >= 0) _gl.Uniform1(_postParamLoc[i], _postParams[i].Value);
    }

    bool EnsurePostProgram()
    {
        int version = PostFx.Version;
        if (version == _postVersion) return _postProg != 0;
        _postVersion = version;

        if (_postProg != 0) { _gl.DeleteProgram(_postProg); _postProg = 0; }

        string? src = PostFx.Source;
        if (src == null) return false;

        _postProg = GlShaders.Build(_gl, GlShaders.FullscreenVs, src, "postfx", out string? error);
        if (_postProg == 0)
        {
            PostFx.Error = error ?? "shader fails to build";
            Console.WriteLine($"[gpu-pfx] {PostFx.Error}");
            return false;
        }

        PostFx.Error = null;
        _gl.UseProgram(_postProg);
        _gl.Uniform1(_gl.GetUniformLocation(_postProg, "uTex"), 0);
        _uPostTexSize = _gl.GetUniformLocation(_postProg, "uTexSize");
        _uPostOutputSize = _gl.GetUniformLocation(_postProg, "uOutputSize");
        _uPostTime = _gl.GetUniformLocation(_postProg, "uTime");
        _uPostFrame = _gl.GetUniformLocation(_postProg, "uFrame");
        _postParamVersion = -1;
        return true;
    }

    unsafe void EnsurePresentSize(int w, int h, bool nearest)
    {
        if (w == _presentW && h == _presentH && nearest == _presentNearest) return;
        _gl.BindTexture(TextureTarget.Texture2D, _presentTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        var filter = nearest ? GLEnum.Nearest : GLEnum.Linear;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)filter);
        _presentW = w; _presentH = h; _presentNearest = nearest;
    }

    public void Dispose()
    {
        foreach (var rt in _rts) rt?.Destroy(_gl);
        foreach (var snap in _snaps) if (snap != null) SnapDestroy(snap);
        _vram.Dispose();
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_presentVbo != 0) _gl.DeleteBuffer(_presentVbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_presentVao != 0) _gl.DeleteVertexArray(_presentVao);
        if (_progPrim != 0) _gl.DeleteProgram(_progPrim);
        if (_progPresent != 0) _gl.DeleteProgram(_progPresent);
        if (_progPresent24 != 0) _gl.DeleteProgram(_progPresent24);
        if (_presentTex != 0) _gl.DeleteTexture(_presentTex);
        if (_presentFbo != 0) _gl.DeleteFramebuffer(_presentFbo);
        if (_postProg != 0) _gl.DeleteProgram(_postProg);
        if (_postTex != 0) _gl.DeleteTexture(_postTex);
        if (_postFbo != 0) _gl.DeleteFramebuffer(_postFbo);
    }
}
