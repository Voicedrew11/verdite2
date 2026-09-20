using System.Text;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;

namespace Kf2;

/// <summary>
/// What the display area actually held, vblank by vblank, across an area change.
///
///     KF2_BLACKPROBE=1   sample the picture at 60 Hz and dump the window around
///                        every fdat load
///
/// A "hitch" reported as a black flash is either the game's own transition fade
/// (luminance ramping down over frames and back up), or frames arriving with
/// nothing in them (a cliff to 0 and back). Nothing else the port carries can
/// tell those apart: the profiler measures work, the pacing probe measures rate,
/// and both read healthy while the picture is black.
///
/// Three one-pixel-tall rows out of the current display rect, read straight out
/// of the backend's VRAM the way `TexProbe` does, averaged as 5-bit luminance and
/// scaled to 0..255. Diagnostic only, off by default, and it costs a readback a
/// vblank while it is on.
/// </summary>
public static class BlackProbe
{
    const int Samples = 1200;        // ~7 s of frames at 165 fps
    const int AfterMs = 2500;        // keep printing this long past a load
    const int BeforeMs = 1500;       // and dump this much of what came before
    const int DarkLevel = 3;         // 0..255; below this the picture reads as black
    const int DarkMs = 80;           // a dark stretch this long marks itself

    public static bool Enabled { get; private set; }

    static readonly double[] _t = new double[Samples];
    static readonly int[] _lum = new int[Samples];
    static readonly int[] _y = new int[Samples];
    static readonly int[] _pres = new int[Samples];
    static readonly int[] _vx = new int[Samples];
    static readonly int[] _vz = new int[Samples];
    static readonly int[] _yaw = new int[Samples];
    static readonly int[] _carry = new int[Samples];
    static int _head, _count;
    static long _lastPresents;

    static ushort[]? _row;
    static double _markMs = -1.0, _runStart = -1.0;
    static string _markName = "";

    const int FlashMax = 40;         // a black run shorter than this is a flash
    static int _flashFrames, _flashY, _flashOther, _flashPres;
    static double _flashStart;

    public static void Configure(string? enabled)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
    }

    public static void Install()
    {
        if (!Enabled) return;
        Console.WriteLine("[KF2] black probe: on, sampling the display area every vblank");

        // Per drawn frame, not per vblank: a flash one or two frames long at 165
        // fps aliases straight past a 60 Hz sampler, which is how three crossings
        // came back reading clean.
        HookAttach.OnOverlayLoad("black probe", Attach);

        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            if (!e.Name.StartsWith("fdat", StringComparison.Ordinal)) return;
            _markMs = Now;
            _markName = e.Name;
        });
    }

    static double Now => Environment.TickCount64;

    // libgpu DrawOTag per overlay, the same three FramePacing takes the frame
    // boundary from. A post: Widescreen owns the one Replace.
    static readonly (string Overlay, uint Addr)[] DrawOTag =
    [
        ("open", 0x80016078u), ("game", 0x80060818u), ("end", 0x80013D80u),
    ];

    static readonly HashSet<uint> _hooked = [];

    static readonly RecompOne.Runtime.Modding.ModInfo _self = new()
    {
        Id = "kf2.blackprobe",
        Name = "Black probe",
        Version = "1.0",
        Description = "Samples the display area every drawn frame.",
    };

    static bool Attach()
    {
        RecompOne.Runtime.Modding.SymbolRegistry.Build();
        var after = typeof(BlackProbe).GetMethod(nameof(AfterDrawOTag),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

        foreach (var (overlay, addr) in DrawOTag)
        {
            if (_hooked.Contains(addr)) continue;
            var fn = RecompOne.Runtime.Modding.SymbolRegistry.Resolve(overlay, null, addr);
            if (fn == null) continue;
            RecompOne.Runtime.Modding.HookManager.AddPost(_self, fn, after);
            RecompOne.Runtime.Modding.HookManager.Commit();
            if (HookAttach.Installed(fn)) _hooked.Add(addr);
        }

        return _hooked.Count == DrawOTag.Length;
    }

    public static void AfterDrawOTag(RecompOne.Runtime.Context.CpuContext c,
                                     RecompOne.Runtime.Memory.IMemory m) => Sample();

    static void Sample()
    {
        var be = GpuHle.Backend;
        var gpu = RecompOne.Runtime.Runtime.Gpu;
        if (be is not { Ready: true } || gpu == null) return;

        _row ??= new ushort[320];
        int x = gpu.DisplayX, y = gpu.DisplayY;

        int shown = Rows(be, x, y);
        int other = Rows(be, x, y == 0 ? 240 : 0);
        if (shown < 0) return;

        // Presents since the last sample: a black picture that is still being
        // presented is the game drawing black, and one that is not is the game
        // thread blocked with the last frame stuck on screen.
        long presents = RecompOne.Runtime.Sdk.LibEtc.VSyncCalls +
                        RecompOne.Runtime.Sdk.LibGpu.AutoPresents;
        int delta = (int)Math.Min(presents - _lastPresents, 99);
        _lastPresents = presents;

        // A masked display is black on screen whatever VRAM holds, and VRAM is
        // what the rows above read -- so it is recorded as its own state rather
        // than inferred from a luminance that stays lit right through it.
        int lum = gpu.DisplayEnabled ? shown : -1;
        Store(Now, lum, y, delta);
        StoreView();

        // Report a short flash where it happens. A run this brief never reaches
        // the dump's threshold and is exactly what "black for a fraction of a
        // second" is, so it says so on its own line, with what the OTHER display
        // buffer held at the same moment: black in both is the game clearing, and
        // black in one with the other lit is a flip to a buffer nothing had
        // finished drawing.
        if (lum >= DarkLevel && _flashFrames is > 0 and <= FlashMax)
            Console.WriteLine($"[black] flash: {_flashFrames} frame(s), " +
                              $"~{Now - _flashStart:0} ms, buffer y{_flashY}, " +
                              $"other buffer {_flashOther}, {_flashPres} present(s) during, " +
                              $"back to {lum}");
        if (lum < DarkLevel)
        {
            if (_flashFrames++ == 0) { _flashStart = Now; _flashY = y; _flashOther = other; _flashPres = 0; }
            _flashPres += delta;
        }
        else _flashFrames = 0;

        // A crossing that reuses a resident module fires no OverlayLoadedEvent,
        // and the picture is what is being asked about -- so any dark stretch
        // long enough to see marks itself too.
        if (lum < DarkLevel) _runStart = _runStart < 0 ? Now : _runStart;
        else
        {
            if (_runStart >= 0 && Now - _runStart >= DarkMs && _markMs < 0)
            {
                _markMs = _runStart;
                _markName = "dark";
            }
            _runStart = -1.0;
        }

        if (_markMs >= 0 && Now - _markMs > AfterMs) Dump();
    }

    /// <summary>Mean 5-bit luminance of three rows of one display buffer, 0..255,
    /// or -1 if the read failed.</summary>
    static int Rows(IGpuBackend be, int x, int y)
    {
        long sum = 0;
        int n = 0;
        foreach (int dy in stackalloc[] { 60, 120, 180 })
        {
            try { be.ReadVram(x, y + dy, 320, 1, _row!); }
            catch { return -1; }

            for (int i = 0; i < 320; i++)
            {
                ushort p = _row![i];
                sum += (p & 0x1F) + ((p >> 5) & 0x1F) + ((p >> 10) & 0x1F);
                n += 3;
            }
        }
        return n == 0 ? -1 : (int)(sum * 255 / (n * 31L));
    }

    // The view this frame was actually drawn with. FrameSmoothing writes the
    // carried camera into the game's own globals before the render, so these are
    // the values the frame used, not the tick's.
    const uint PosX = 0x801994ECu, PosZ = 0x801994F4u, ComposedYaw = 0x80199506u;

    static long _lastFrames, _lastCarries;

    static void StoreView()
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return;
        int i = (_head - 1 + Samples) % Samples;
        _vx[i] = (int)m.ReadU32(PosX);
        _vz[i] = (int)m.ReadU32(PosZ);
        _yaw[i] = m.ReadU16(ComposedYaw);

        // Did FrameSmoothing carry the view into this frame? The globals above are
        // restored by its post-hook, so they read the tick's value here whatever
        // the frame was drawn with; this is the only honest per-frame answer.
        long f = FrameSmoothing.Frames;
        _carry[i] = f != _lastFrames ? FrameSmoothing.LastVerdict : -1;
        _lastFrames = f;
    }

    static void Store(double t, int lum, int y, int presents)
    {
        _t[_head] = t;
        _lum[_head] = lum;
        _y[_head] = y;
        _pres[_head] = presents;
        _head = (_head + 1) % Samples;
        if (_count < Samples) _count++;
    }

    /// <summary>One line per 20 frames: the offset from the load, then each
    /// sample's luminance, with `|` where the display buffer flipped.</summary>
    static void Dump()
    {
        double mark = _markMs;
        string name = _markName;
        _markMs = -1.0;

        var sb = new StringBuilder();
        int start = (_head - _count + Samples) % Samples;
        int col = 0, lastY = -1;
        int dark = 0, darkRun = 0, worstRun = 0;
        double runStart = 0, worstMs = 0;
        int presInDark = 0, stalled = 0;
        int off = 0, offRun = 0, worstOff = 0;
        int still = 0, stillRun = 0, worstStill = 0;
        double stillStart = 0, worstStillMs = 0;
        int prevX = int.MinValue, prevZ = 0, prevYaw = 0;
        var moves = new StringBuilder();

        for (int k = 0; k < _count; k++)
        {
            int i = (start + k) % Samples;
            double rel = _t[i] - mark;
            if (rel < -BeforeMs || rel > AfterMs) continue;

            if (col % 20 == 0)
            {
                if (col > 0) sb.Append('\n');
                sb.Append($"[black] {name} {rel,+6:0} ms:");
            }

            if (lastY >= 0 && _y[i] != lastY) sb.Append(" |");
            lastY = _y[i];
            sb.Append(' ').Append(_lum[i] < 0 ? "off" : _lum[i].ToString().PadLeft(3));
            col++;

            if (_lum[i] < 0) { off++; offRun++; worstOff = Math.Max(worstOff, offRun); }
            else offRun = 0;

            if (_lum[i] == 0)
            {
                dark++;
                if (darkRun++ == 0) runStart = _t[i];
                worstRun = Math.Max(worstRun, darkRun);
                worstMs = Math.Max(worstMs, _t[i] - runStart);
                presInDark += _pres[i];
            }
            else darkRun = 0;

            if (_pres[i] == 0) stalled++;

            // Did the view move at all this frame? At 165 fps with the smoothing
            // carrying, every frame moves; a run of identical frames is the world
            // stepping at the tick rate, which is the stutter.
            if (prevX != int.MinValue)
            {
                long dx = _vx[i] - prevX, dz = _vz[i] - prevZ;
                int dyaw = Math.Abs(((_yaw[i] - prevYaw + 2048) & 0xFFF) - 2048);
                bool moved = dx != 0 || dz != 0 || dyaw != 0;

                if (moves.Length > 0 && (col - 1) % 20 == 0) moves.Append('\n');
                if ((col - 1) % 20 == 0) moves.Append($"[black] {name} carry {rel,+6:0} ms:");
                moves.Append(' ').Append(_carry[i] switch
                {
                    3 => "C",     // carried: the view was interpolated into this frame
                    2 => ".",     // declined: nothing moved, so there was nothing to carry
                    1 => "U",     // declined: unprimed -- the stutter, if it is one
                    0 => "o",     // off, or the rate is at the tick rate
                    _ => "-",     // stage 8 did not run for this frame at all
                });
                _ = moved;

                if (_carry[i] is 1 or -1)
                {
                    if (stillRun++ == 0) stillStart = _t[i];
                    still++;
                    worstStill = Math.Max(worstStill, stillRun);
                    worstStillMs = Math.Max(worstStillMs, _t[i] - stillStart);
                }
                else stillRun = 0;
            }
            prevX = _vx[i]; prevZ = _vz[i]; prevYaw = _yaw[i];
        }

        Console.WriteLine(sb.ToString());
        if (moves.Length > 0) Console.WriteLine(moves.ToString());
        Console.WriteLine($"[black] {name}: {col} sample(s), {dark} fully black, " +
                          $"longest black run {worstRun} frame(s) (~{worstMs:0} ms), " +
                          $"{presInDark} present(s) while black, " +
                          $"{stalled} frame(s) with no present at all; " +
                          $"display masked off for {off} frame(s), longest run {worstOff}; " +
                          $"view unprimed on {still} frame(s), longest run {worstStill} " +
                          $"(~{worstStillMs:0} ms)");
    }
}
