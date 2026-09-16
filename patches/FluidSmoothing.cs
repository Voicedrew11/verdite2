using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Carry the animated textures between logic ticks, so water and a slime's skin
/// scroll at the render rate instead of jumping a texel every 50 ms.
///
///     KF2_SMOOTH_FLUID=0        leave them on the tick -- comparison only
///     KF2_SMOOTH_FLUID_PROBE=1  live slots, dest rects, and the leftover shift
///
/// ## The defect
///
/// The animated water at the start, the main-hall fire and the creatures'
/// scrolling skins are one system: eight slots at <c>0x80192D58</c> (stride
/// <c>0x18</c>). <c>func_8002DC78</c> advances each slot's phase and re-uploads a
/// vertically wrapped copy of the source texture into a dest RECT in VRAM. The
/// stage gate holds that updater to the world tick, so the rate per second is
/// right -- and VRAM only changes 20 times a second, so a 60 fps picture of water
/// still steps like 20 fps.
///
/// Billboard flames are a different system (<see cref="SpriteAnim"/>); those are
/// authored cels and have nothing to interpolate. These are a wrap in VRAM, so
/// the in-between is a blend of the two rows the leftover phase sits between.
///
/// ## Why the shader, and why not another VRAM upload
///
/// Re-uploading at the render rate would run the integer scroll faster, not
/// smoother, and the frame viewer measured the tick-rate upload as 0.76 ms on
/// the next primitive before <c>0054</c> stopped blitting each upload into the
/// scaled atlas. Offsetting the packet's 8-bit V is also not
/// enough: the rasterizer snaps UV to a whole texel, so a fractional shift
/// rounds away. The C# assemblers already hand the GPU float UVs, and both prim
/// shaders sample through <c>decode()</c> after the CLUT -- the same place
/// anisotropic filtering already blends neighbouring texels -- so a two-tap
/// blend along V, wrapping inside the dest rect the upload itself wraps, is the
/// in-between that exists.
///
/// Matching is on the dest RECT, not on a packet tag, so every assembler
/// (clipped, unclipped, lit, recompiled) is covered and Fast geometry is not
/// required. GL backend only; the software rasterizer keeps the last upload, the
/// way it keeps a single aniso sample.
///
/// ## Interpolate, not extrapolate
///
/// VRAM already holds this tick's integer phase by the time <c>DrawOTag</c>
/// walks. The leftover is <c>(1 - LogicPhase) * delta</c> added to V, so at
/// phase 0 the picture is last tick's upload (one step ahead in the current
/// wrap) and at phase 1 it is this one's -- the same clock
/// <see cref="FrameSmoothing"/> draws the view at, and continuous across a wrap
/// because delta is taken modulo the dest height. At or below the tick rate
/// <see cref="FramePacing.Extrapolating"/> is false and nothing is published, or
/// the picture would sit a texel behind forever (lerp at phase 0 is <c>prev</c>).
///
/// On with the rest of the smoothing tick. The picture of the water has not
/// been judged by eye.
/// </summary>
public static class FluidSmoothing
{
    const uint Slots = 0x80192D58;
    const int Count = 8;
    const uint Stride = 0x18;
    const uint DrawOTag = 0x80060818;

    public const string OnKey = "kf2.smoothing.fluid";

    /// <summary>Blend the scrolling textures between ticks. **On by default**:
    /// the shipped picture is 60 fps against a 20 Hz world, and without this the
    /// water still steps with that world.</summary>
    public static bool Enabled { get; private set; } = true;

    static bool _fromEnv, _probe, _hooked;
    static bool _primed, _carriable;
    static long _lastFrame;

    static readonly int[] _prev = new int[Count];
    static readonly int[] _cur = new int[Count];

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _reportedAt;
    static long _carried, _skipped;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.fluidsmoothing",
        Name = "Fluid smoothing",
        Version = "1.0",
        Description = "Blends the scrolling textures between the game's logic ticks.",
    };

    public static void Configure(string? on, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on)) { Enabled = on != "0"; _fromEnv = true; }
        _probe = probe == "1";
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            if (!_fromEnv) Enabled = RecompOne.Runtime.Runtime.View.GetBool(OnKey, Enabled);
        });

        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            _primed = false;
            _carriable = false;
            GteDepth.FluidN = 0;
        });

        HookAttach.OnOverlayLoad("fluid", Attach);
    }

    public static void SetEnabled(bool on)
    {
        if (on && !Enabled) { _primed = false; _carriable = false; }
        Enabled = on && _hooked;
        if (!Enabled) GteDepth.FluidN = 0;
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("game", null, DrawOTag);
        if (target == null)
        {
            Enabled = false;
            Console.Error.WriteLine($"[KF2] fluid: no game DrawOTag at 0x{DrawOTag:X8}; " +
                                    "scrolling textures will step at the tick.");
            return false;
        }

        var self = typeof(FluidSmoothing);
        bool queued = HookManager.AddPre(_self, target,
            self.GetMethod(nameof(BeforeDrawOTag), BindingFlags.Public | BindingFlags.Static)!);
        HookManager.Commit();
        _hooked = queued && HookAttach.Installed(target);
        if (!_hooked)
        {
            Enabled = false;
            Console.Error.WriteLine("[KF2] fluid: DrawOTag pre did not attach; " +
                                    "scrolling textures will step at the tick.");
            return false;
        }

        Console.WriteLine($"[KF2] fluid: {(Enabled ? "on" : "off")}, hooked DrawOTag at 0x{DrawOTag:X8}");
        return true;
    }

    /// <summary>
    /// Publish the dest rects and leftover V shifts just before the OT walk, so
    /// every batch of this frame reads the same leftover. Sampled after
    /// <c>func_8002DC78</c> has already run on a tick frame.
    /// </summary>
    public static void BeforeDrawOTag(CpuContext c, IMemory m)
    {
        if (!Enabled || !FramePacing.Extrapolating)
        {
            GteDepth.FluidN = 0;
            _skipped++;
            return;
        }

        bool ticked = FramePacing.FirstWalkOfTick(ref _lastFrame);
        if (ticked && _primed) _carriable = true;

        int n = 0;
        for (int i = 0; i < Count; i++)
        {
            uint rec = Slots + (uint)i * Stride;
            if (m.ReadU8(rec) != 1) continue;
            int destX = (short)m.ReadU16(rec + 6u);
            int destY = (short)m.ReadU16(rec + 8u);
            int destW = (short)m.ReadU16(rec + 0xAu);
            int destH = (short)m.ReadU16(rec + 0xCu);
            if (destW <= 0 || destH <= 0) continue;

            int phase = (short)m.ReadU16(rec + 4u);
            if (ticked)
            {
                _prev[i] = _primed ? _cur[i] : phase;
                _cur[i] = phase;
            }

            int delta = 0;
            if (_carriable)
            {
                delta = _cur[i] - _prev[i];
                if (delta < 0) delta += destH;
            }

            ref var s = ref GteDepth.Fluid[n];
            s.X = destX;
            s.Y = destY;
            s.W = destW;
            s.H = destH;
            s.Off = (float)((1.0 - FramePacing.LogicPhase) * delta);
            n++;
        }

        if (ticked) _primed = true;

        GteDepth.FluidN = n;
        if (n > 0) _carried++;
        else _skipped++;
        if (_probe) Report();
    }

    static void Report()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _reportedAt < 1.0) return;
        double dt = now - _reportedAt;
        _reportedAt = now;

        Console.WriteLine($"[KF2] fluid: {GteDepth.FluidN} slot(s), " +
                          $"{_carried / dt:0.0} published/s, {_skipped / dt:0.0} skipped/s, " +
                          $"phase {FramePacing.LogicPhase:0.00}, " +
                          $"uniform {(GteDepth.FluidLive ? "bound" : "NOT bound")}");
        for (int i = 0; i < GteDepth.FluidN; i++)
        {
            ref var s = ref GteDepth.Fluid[i];
            Console.WriteLine($"[KF2] fluid:   dest ({s.X:0},{s.Y:0}) {s.W:0}x{s.H:0}  off {s.Off:+0.00;-0.00}");
        }

        _carried = _skipped = 0;
    }
}
