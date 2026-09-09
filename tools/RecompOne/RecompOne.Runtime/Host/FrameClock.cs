using System.Diagnostics;

namespace RecompOne.Runtime.Host;

internal static class FrameClock
{
    //Upstream's host rate, PAL-aware since the pin. It is what this paces to
    //while nothing has set TargetFps, so a PAL game still gets 50.
    private static double HostFrameMs => Sdk.LibGpu.Pal ? 1000.0 / 50.0 : 1000.0 / 60.0;

    private const double SpinMs = 1.5;

    private static readonly Stopwatch _clock = Stopwatch.StartNew();
    private static double _nextFrameMs;

    public static bool VSync { get; set; }

    //The rate this paces to, in frames a second; 0 hands pacing to whoever else
    //wants it. It used to be `const double FrameMs = 1000.0 / 60.0`, which is one
    //of three independent 60s in the runtime and the only one that is a *host*
    //rate rather than a guest one: the emulated vblank grid (Sdk.LibEtc) and the
    //game's own clocks are unaffected by it, so a port that wants to draw at 120
    //while its game logic keeps 60 Hz timing has to be able to move this one
    //without moving those.
    //
    //Note this paces per *VSync call*, not per rendered frame -- a frame can carry
    //more than one -- so a caller that needs an exact frame rate should set this
    //as a permissive ceiling and keep its own deadline at the frame boundary.
    public static double TargetFps
    {
        get => _targetFps < 0.0 ? (Sdk.LibGpu.Pal ? 50.0 : 60.0) : _targetFps;
        set => _targetFps = value > 0.0 ? value : 0.0;
    }

    //-1 means "never set": follow the host rate, which is what upstream does and
    //is the only value that knows about PAL. 0 is off.
    private static double _targetFps = -1.0;

    //Not cached across calls: TargetFps can change from the settings mid-session,
    //and a division is nothing beside the sleep it is sizing.
    private static double FrameMs =>
        _targetFps < 0.0 ? HostFrameMs :
        _targetFps > 0.0 ? 1000.0 / _targetFps : 0.0;

    public static double LastFrameMs { get; private set; }

    public static double Fps { get; private set; }
    private static double _fpsAccumMs;
    private static int _fpsFrames;

    public static double PresentFps { get; private set; }
    private static double _presentStartMs;
    private static int _presentFrames;
    public static double LastWaitMs { get; private set; }

    private static double _lastStart;


    public static void Throttle()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        LastFrameMs = now - _lastStart;
        _lastStart = now;

        _fpsAccumMs += LastFrameMs;
        _fpsFrames++;
        if (_fpsAccumMs >= 1000.0)
        {
            Fps = _fpsFrames * 1000.0 / _fpsAccumMs;
            _fpsAccumMs = 0;
            _fpsFrames = 0;
        }

        var frameMs = FrameMs;
        if (frameMs <= 0.0)
        {
            //Off. Keep the grid on the present, so that turning it back on does
            //not arrive with a frame of debt already owed.
            _nextFrameMs = now;
            LastWaitMs = 0;
            return;
        }

        _nextFrameMs += frameMs;
        var wait = _nextFrameMs - now;

        if (wait < -100)
        {
            _nextFrameMs = now;
            LastWaitMs = 0;
            return;
        }

        if (wait <= 0)
        {
            LastWaitMs = 0;
            return;
        }

        if (VSync && wait < frameMs * 0.75)
        {
            LastWaitMs = 0;
            return;
        }

        var sleepUntil = _nextFrameMs - SpinMs;
        if (now < sleepUntil)
        {
            var ms = (int)(sleepUntil - now);
            if (ms > 0) Thread.Sleep(ms);
        }

        while (_clock.Elapsed.TotalMilliseconds < _nextFrameMs)
            Thread.SpinWait(48);

        LastWaitMs = wait;
    }

    public static void MarkPresent()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        _presentFrames++;

        var elapsed = now - _presentStartMs;
        if (elapsed < 1000.0) return;

        PresentFps = _presentFrames * 1000.0 / elapsed;
        _presentStartMs = now;
        _presentFrames = 0;
    }

    public static void Resync()
    {
        _nextFrameMs = _clock.Elapsed.TotalMilliseconds;
    }
}
