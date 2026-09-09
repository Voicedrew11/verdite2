namespace RecompOne.Runtime.Hle;

public struct HleVertex
{
    public float X, Y;
    public float Z;
    public byte R, G, B;
    public float U, V;
    public bool HasGteZ;
    // Independent of HasGteZ: the depth buffer wants Z on untextured geometry
    // too, and putting that Z into clip W would turn perspective correction on
    // as a side effect. HasPersp is the original "use Z as gl_Position.w".
    public bool HasPersp;
    // Upstream's own pair, for PGXP and the frame interpolator. They sit beside
    // ours rather than replacing it: HasGteZ/HasPersp answer for the address-map
    // source, Depth/Transform for PGXP's.
    public float Depth;
    public int Transform;
}

public struct PrimFlags
{
    public bool Textured, SemiTrans, RawTexture, Gouraud;
    public ushort TPage;
    public ushort Clut;
    public int OtIndex;
    public bool UseImage;
    public int Image;

    public readonly int BlendMode => (TPage >> 5) & 3;
}

public struct HleRect
{
    public float X, Y;
    public int W, H;
    public short U, V;
    public byte R, G, B;
}

public struct HleDrawEnv
{
    public int ClipX0, ClipY0, ClipX1, ClipY1;
    public int TwMaskX, TwMaskY, TwOffX, TwOffY;
    public bool SetMask, CheckMask, Dither;
}

public struct HleDispEnv
{
    public int X, Y, W, H;
    public bool Rgb24, Interlace;
}