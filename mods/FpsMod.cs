using System.Diagnostics;

namespace Kf2.Mods;

/// <summary>
/// What frame rate the port runs at.
///
///     KF2_MODS=fps=30     (default) floor every rendered frame at two vblanks
///     KF2_MODS=fps=60     render at one vblank, gate the world to every other one
///     KF2_MODS=fps=off    no floor: the raw port, which bursts past 60
///
/// King's Field's speed *is* its frame rate -- everything advances a fixed amount
/// per loop iteration, and the loop waits a whole number of vblanks, so rates
/// quantise to 60/n. On hardware a frame costs 2, 3 or 4 vblanks, which is the
/// 30/20/15 fps banding the game is known for; 30 is its ceiling.
///
/// This port never bands down, because DrawOTag returns as soon as the ordering
/// table is walked on an HLE GPU and the MIPS is native code. Sampling the game
/// thread puts about 90% of a frame in the pacing sleep and roughly a millisecond
/// in actual game code, so nothing here is ever load-limited the way hardware was.
///
/// `fps=30` is therefore a *floor*, not a rescale, and it is the whole fix for
/// running too fast: hold each rendered frame to two vblanks and the port sits on
/// NTSC's fastest band exactly, without the wander that made the original's speed
/// depend on what was on screen. Verified: over 3,200 frames no report window came
/// out above 30.0 fps, with eight consecutive in-area windows at 30.0 and a band
/// histogram of 2:100%.
/// </summary>
public sealed class FpsMod : Mod
{
    const double VBlankMs = 1000.0 / 60.0;
    const double SpinMs = 1.5;   // spin the last stretch; Thread.Sleep granularity is a few ms

    public override string Name => "fps";
    public override string Summary => "frame rate: 30 (floor, default), 60 (world gated), off";
    public override bool DefaultEnabled => true;
    public override string State => Enabled ? $"{60 / MinVBlanks}" : "off";

    /// <summary>Minimum vblanks a rendered frame may occupy. 2 = 30 fps, 1 = 60 fps.</summary>
    public int MinVBlanks { get; private set; } = 2;

    /// <summary>
    /// Main-loop stages to skip on odd frames when rendering at 60. Empty means
    /// nothing is gated, which at 60 fps means the whole game runs at double
    /// speed -- see the warning in <see cref="OnEnabled"/>.
    /// </summary>
    public HashSet<uint> GatedStages { get; } = [];

    static readonly Stopwatch _clock = Stopwatch.StartNew();

    // When the current frame is allowed to end. Absolute rather than now+min, so
    // a frame that overruns is paid for out of the next one and the rate averages
    // to exactly 60/MinVBlanks instead of drifting down by the accumulated jitter.
    double _due;
    long _frames;

    protected internal override void Configure(string value)
    {
        // fps=60            rate only
        // fps=60:gate=80037C0C+8002A550   rate and the stages to halve
        foreach (var part in value.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("gate=", StringComparison.OrdinalIgnoreCase))
            {
                GatedStages.Clear();
                foreach (var a in part[5..].Split('+', StringSplitOptions.RemoveEmptyEntries))
                {
                    uint addr = Convert.ToUInt32(a.Trim(), 16);
                    if (!Hooks.Stages.Contains(addr))
                        throw new ArgumentException(
                            $"fps: 0x{addr:X8} is not a main-loop stage. Stages: " +
                            string.Join(" ", Hooks.Stages.Select(s => s.ToString("X8"))));
                    GatedStages.Add(addr);
                }
            }
            else if (int.TryParse(part, out int fps))
            {
                MinVBlanks = fps switch
                {
                    >= 60 => 1,
                    >= 30 => 2,
                    >= 20 => 3,
                    _ => 4,
                };
            }
            else throw new ArgumentException($"fps: cannot read '{part}'");
        }
    }

    protected internal override void OnEnabled()
    {
        Hooks.FrameDrawn += OnFrameDrawn;
        Hooks.StageGate = StageAllowed;
    }

    protected internal override void OnDisabled()
    {
        Hooks.FrameDrawn -= OnFrameDrawn;
        Hooks.StageGate = null;
    }

    /// <summary>
    /// Called once the mod list is settled, so the warning is not printed for a
    /// setting a later KF2_MODS entry overrides.
    /// </summary>
    public void Validate()
    {
        if (!Enabled || MinVBlanks > 1) return;

        if (GatedStages.Count == 0)
            Console.WriteLine(
                "[KF2] fps=60 with no gated stages: the whole game will run at DOUBLE SPEED. " +
                "This is rendering-only 60 fps; see \"60 fps\" in NOTES.md.");
        else
            Console.WriteLine(
                $"[KF2] fps=60 gating {GatedStages.Count} stage(s) to every other frame: " +
                string.Join(" ", GatedStages.Select(s => s.ToString("X8"))) +
                ". Stages left ungated still advance at 60 -- twice their intended rate.");
    }

    // Odd frames skip the gated stages. The render stage is never gated: skipping
    // it would drop the frame that the 60 fps rate exists to draw.
    bool StageAllowed(uint address)
    {
        if (MinVBlanks > 1 || GatedStages.Count == 0) return true;
        if (address == Hooks.RenderStage) return true;
        return (_frames & 1) == 0 || !GatedStages.Contains(address);
    }

    void OnFrameDrawn()
    {
        _frames++;

        // At one vblank there is nothing to enforce: the runtime's FrameClock
        // already holds a single VSync to a vblank, which is why the unfloored
        // port tops out around 50 fps rather than running away entirely.
        if (MinVBlanks > 1) Floor();
    }

    void Floor()
    {
        double min = MinVBlanks * VBlankMs;
        double now = _clock.Elapsed.TotalMilliseconds;

        // More than a frame of debt means the game stopped drawing rather than ran
        // late -- a disc read, a module swap, the first frame of all. Restart the
        // cadence instead of running flat out to pay it off.
        if (_due < now - min) _due = now;

        if (now < _due)
        {
            double sleepUntil = _due - SpinMs;
            if (now < sleepUntil)
            {
                int ms = (int)(sleepUntil - now);
                if (ms > 0) Thread.Sleep(ms);
            }
            while (_clock.Elapsed.TotalMilliseconds < _due) Thread.SpinWait(48);
        }

        _due += min;
    }
}
