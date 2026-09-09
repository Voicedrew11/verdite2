namespace RecompOne.Runtime.Hle;

public static class GpuHle
{
    public static bool Active { get; set; }
    public static IGpuBackend? Backend { get; set; }

    public static float WideAspect { get; set; }
    public static float OutputAspect { get; set; } = 4f / 3f;

    public static float SourceAspect { get; set; } = 4f / 3f;
    public static int LastDisplayW { get; set; }
    public static int LastDisplayH { get; set; }
    public static float TargetAspect { get; set; } = 4f / 3f;
    public const float BaseAspect = 4f / 3f;
    // KF2_PRESENT_PROBE: what each present picked -- the widened target, a plain
    // one, or a fallback to raw VRAM. GlCore.PresentDisplay counts; Program.cs
    // sets the switch.
    public static bool PresentProbe;
    public static long PresentWide, PresentPlain, PresentFallback;
    public static double PresentWindowStart = Environment.TickCount64 / 1000.0;
    // DisplayFlip counts genuine display-area flips (NotifyDisplay seeing a
    // different X,Y -- the per-frame flip, not the GP1(06)/(07)/(08)
    // re-notifications of the same rect). Per-target margin-content latches
    // stamp themselves with it; see GlDisplayRt.MarginContentFlip.
    public static int DisplayFlip = 0;
    // Set by the widescreen patch while a primitive it has itself widened is in
    // flight, and cleared by GpuRaster before each primitive's RenderPrimEvent
    // dispatch. The latch must record where the GAME drew past its own edge --
    // the port stretching a splash fade across the margins is not margin content,
    // and stamping from it is what made the boot splash flap between widths.
    public static bool PortWidenedPrim;

    public struct DispRect
    {
        public int X, Y, W, H;
        public long Stamp;
        public bool Valid;
    }

    private static readonly DispRect[] _rects = new DispRect[2];
    private static long _stamp;
    private static int _lastNotifiedX = -1, _lastNotifiedY = -1;

    public static void NotifyDisplay(int x, int y, int w, int h)
    {
        if (x != _lastNotifiedX || y != _lastNotifiedY) { DisplayFlip++; _lastNotifiedX = x; _lastNotifiedY = y; }
        if (w <= 0 || h <= 0) return;
        var slot = -1;
        for (var i = 0; i < _rects.Length; i++)
            if (_rects[i].Valid && _rects[i].X == x && _rects[i].Y == y)
            {
                slot = i;
                break;
            }

        if (slot < 0)
        {
            slot = 0;
            for (var i = 1; i < _rects.Length; i++)
                if (!_rects[i].Valid || _rects[i].Stamp < _rects[slot].Stamp)
                    slot = i;
        }

        _rects[slot] = new DispRect { X = x, Y = y, W = w, H = h, Stamp = ++_stamp, Valid = true };
        RectVersion++;
    }

    public static long RectVersion { get; private set; }

    public static int RectCount => _rects.Length;

    public static DispRect GetRect(int i)
    {
        return _rects[i];
    }

    public static int WideMargin(int w)
    {
        if (WideAspect <= 0f) return 0;
        var source = SourceAspect > 0f ? SourceAspect : BaseAspect;
        var wide = (int)MathF.Ceiling(w * WideAspect / source);
        return Math.Max(0, (wide - w + 1) / 2);
    }
}