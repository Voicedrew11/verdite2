using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime;

public sealed partial class Gpu
{
    /// <summary>0085. The retained map's opaque range into the frame, with the draw
    /// area and offset the GPU holds now; the table walk calls it once a frame, past
    /// slot 0 (the sky). False when nothing was drawn.</summary>
    public bool DrawRetainedMain()
    {
        if (!HleOn || RetainedScene.MainDrawer is not { } draw) return false;
        GpuHle.Backend!.SetDrawEnv(CurEnv());
        return draw(_drawOffsetX, _drawOffsetY);
    }
}
