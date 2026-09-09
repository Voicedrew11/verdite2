using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

public sealed class GlDisplayRt
{
    public int X, Y, W, H;
    public int Margin;
    public uint Tex, Fbo;
    // A texture rather than a renderbuffer, because the ambient-occlusion pass
    // reads the finished buffer back in a full-screen shader and a renderbuffer
    // cannot be sampled. Nothing else changes: it is still the same
    // DEPTH_COMPONENT24 attachment, still cleared at the head of a frame that
    // draws to this target, and ReadPixels off the FBO (the KF2_ZBUFFER_PROBE=2
    // census) reads it exactly as before.
    public uint Depth;
    public bool Dirty;
    public long Stamp;
    public long LastDrawFrame;
    // Whether this target's margin columns have ever carried a world's worth of
    // content: latched once a single display flip delivers enough game vertices
    // past the game's own draw edge, or a fill covers the target. Negative means
    // never. The density test is what keeps an oversized clear rect -- genuine
    // game output, but two vertices of it per flip -- from granting the margin
    // to a scene that never renders anything out there.
    public int MarginContentFlip = -1000;
    public int MarginVerts;
    public int MarginVertFlip = -1;
    public int ZGen = -1;
    // Upstream's own, kept beside ours rather than in place of it.
    public long LastMarginFrame = -1;

    public int Wide1x => W + Margin * 2;
    public int TexW => Wide1x * GlVram.Scale;
    public int TexH => H * GlVram.Scale;

    public bool Contains(int cx0, int cy0, int cx1, int cy1)
    {
        return cx0 >= X && cx1 <= X + W - 1 && cy0 >= Y && cy1 <= Y + H - 1;
    }

    public bool Covers(int cx0, int cy0, int cx1, int cy1)
    {
        return cx0 <= X && cx1 >= X + W - 1 && cy0 <= Y && cy1 >= Y + H - 1;
    }

    public bool Intersects(int rx, int ry, int rw, int rh)
    {
        return rx < X + W && X < rx + rw && ry < Y + H && Y < ry + rh;
    }

    public unsafe void Create(GL gl)
    {
        Tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // True color keeps eight bits per channel so the shaded fog gradient does
        // not band; the default RGB5A1 matches the console's 15-bit VRAM and lets
        // the fragment shader's quant5 stand. The mask/STP bit rides the alpha
        // either way — 1-bit in 1555, the top of an 8-bit alpha in RGBA8, and both
        // read back as >= 0.5. The blits to and from VRAM (Rgb5A1) convert
        // automatically, so a true-color target still writes 15-bit content back.
        if (GteDepth.TrueColor)
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)TexW, (uint)TexH, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, new byte[TexW * TexH * 4].AsSpan());
        else
            gl.TexImage2D<ushort>(TextureTarget.Texture2D, 0, InternalFormat.Rgb5A1, (uint)TexW, (uint)TexH, 0,
                PixelFormat.Rgba, PixelType.UnsignedShort1555Rev, new ushort[TexW * TexH].AsSpan());

        Fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, Tex, 0);

        Depth = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Depth);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // Explicitly not a shadow sampler: with the compare mode left at whatever
        // the driver defaults to, an ordinary sampler2D read of this texture is
        // undefined rather than the stored depth.
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.None);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)TexW, (uint)TexH, 0,
            PixelFormat.DepthComponent, PixelType.Float, null);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, Depth, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        gl.ClearColor(0f, 0f, 0f, 0f);
        gl.ClearDepth(1.0);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(true);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    public void Destroy(GL gl)
    {
        if (Fbo != 0) gl.DeleteFramebuffer(Fbo);
        if (Tex != 0) gl.DeleteTexture(Tex);
        if (Depth != 0) gl.DeleteTexture(Depth);
        Fbo = Tex = Depth = 0;
    }
}