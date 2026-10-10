using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Turns off the GPU's ordered dither — the 4x4 crosshatch that sits over every
/// shaded surface in the game.
///
///     KF2_NODITHER_PROBE=1   also report the two draw-mode sources to the console
///
/// It is a setting, under Video — one of the three entries of
/// Kf2.Settings.ShadingPage's combo, which is where it and true color are asked
/// as the one question they are. The saved choice is read on RuntimeReadyEvent
/// rather than in <see cref="Configure"/>, since ConfigManager only loads inside
/// HostWindow.Initialize, after Program.cs.
///
/// The PlayStation frame buffer is 15-bit, so the GPU has to drop the low three
/// bits of each 8-bit colour component on the way in. With the dither bit set it
/// first adds a bias from a 4x4 table indexed by the pixel's own coordinates, so
/// the truncation error alternates per pixel and a gradient reads as a texture of
/// tiny checkers instead of as bands. Clearing the bit skips the bias: the same
/// quantization happens, straight, and gradients band instead. That trade is the
/// whole of this patch — there is no third option at 15 bits.
///
/// The dither state is bit 9 of a GP0(E1) draw-mode word, and it reaches the GPU
/// two ways: PutDrawEnv's <c>dtd</c> byte at <c>DRAWENV+0x16</c> (the library
/// turns it straight into that bit), and E1 words linked into the ordering table.
/// Both end in the GPU's draw-mode decode, which the runtime's
/// <c>Gpu.SuppressDither</c> (RecompOne patch 0094) masks, so the game's memory is
/// never touched and nothing has to be cleared and put back around each call.
/// That used to be done here, by walking the whole ordering table a second time
/// before every DrawOTag; measured in Verdite3 the walk and the table clear it
/// forced cost 0.1 ms of a 1 ms frame, and the walk now runs only for the probe.
///
/// The probe's walk is a word-level scan rather than a search for bytes that look
/// like E1: a colour or a vertex word can perfectly well have 0xE1 in its top
/// byte. It steps command by command using the GP0 command lengths, and stops at
/// the two commands whose length it cannot know without more state (a polyline,
/// and an image load followed by raw pixel data). Neither appears in this game's
/// ordering tables.
///
/// GPUSTAT bit 9 is the state both renderers read, and <c>KF2_NODITHER_PROBE=1</c>
/// samples it after each frame is drawn. Reading back 0 for a whole session is
/// what says the mask caught every E1 word.
///
/// <see cref="Enabled"/> is live rather than fixed at startup, so the two pictures
/// can be compared without a restart; the hooks stay attached and do nothing.
///
/// The hooks are pre/post and not a replacement on purpose. <c>HookManager</c>
/// allows one <c>Replace</c> owner per function and the widescreen patch owns
/// <c>DrawOTag</c>; pre- and post-hooks compose with a replacement and with each
/// other, so this, that patch and the frame pacing's own post-hook all coexist.
/// </summary>
public static class NoDither
{
    // libgpu PutDrawEnv and DrawOTag, per overlay.
    static readonly (string Overlay, uint Addr)[] PutDrawEnv =
    [
        ("open", 0x800160D0), ("game", 0x80060870), ("end", 0x80013DD8),
    ];

    static readonly (string Overlay, uint Addr)[] DrawOTag =
    [
        ("open", 0x80016078), ("game", 0x80060818), ("end", 0x80013D80),
    ];

    /// <summary>Where the choice is kept between runs. Named as the mod named it,
    /// so a player who had the mod on keeps the picture they had.</summary>
    public const string OnKey = "kf2.nodither.on";

    /// <summary>False leaves every E1 word alone — the game's own dithered picture.
    /// This is the runtime's mask, <c>Gpu.SuppressDither</c>, read and set live.</summary>
    public static bool Enabled
    {
        get => RecompOne.Runtime.Gpu.SuppressDither;
        private set => RecompOne.Runtime.Gpu.SuppressDither = value;
    }

    /// <summary>KF2_NODITHER_PROBE: also write the report to the console.</summary>
    static bool _toConsole;

    // Per report window: draw envs that asked for dither, and OT words that did.
    static long _envHits, _otHits, _frames;

    static double _windowStart;

    // Under the probe: GPUSTAT bit 9 held across the window. 1 means an E1 word got
    // past the mask, which can only be recompiled libgpu writing GP0 directly.
    static uint _stat;

    static double Now => Environment.TickCount64 / 1000.0;

    // HookManager attributes hooks to a mod so they can be removed again. This is
    // in-project rather than a loaded package, so it declares its own identity.
    static readonly ModInfo _self = new()
    {
        Id = "kf2.nodither",
        Name = "No dithering",
        Version = "1.0",
        Description = "Clears the GPU's dither bit.",
    };

    public static void Configure(string? probe)
    {
        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
            _toConsole = true;
    }

    /// <summary>
    /// Attach the hooks. Deferred to the first overlay load because
    /// <see cref="SymbolRegistry"/> reads the dispatcher's overlay tables, and
    /// those are registered inside Entry.Run — after Program.cs has run, but before
    /// anything is loaded, so the first load event is the earliest moment every
    /// overlay is resolvable.
    /// </summary>
    public static void Install()
    {
        _windowStart = Now;

        // Until the saved choice is read, the mask is on: the setting's default.
        Enabled = true;
        Event.AddListener<RuntimeReadyEvent>(_ =>
            Enabled = RecompOne.Runtime.Runtime.View.GetBool(OnKey, true));

        // Attached whether or not the setting is on: "dithered" is a choice that
        // can be taken back, and hooks cannot be added once the game is running
        // past the overlay loads.
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    /// <summary>Change the setting at run time. Safe at any moment — the mask is
    /// read per GP0 draw-mode word.</summary>
    public static void SetEnabled(bool on) => Enabled = on;

    static void Attach()
    {
        SymbolRegistry.Build();
        var self = typeof(NoDither);
        var beforeEnv = self.GetMethod(nameof(BeforePutDrawEnv), BindingFlags.Public | BindingFlags.Static)!;
        var beforeOt = self.GetMethod(nameof(BeforeDrawOTag), BindingFlags.Public | BindingFlags.Static)!;
        var afterOt = self.GetMethod(nameof(AfterDrawOTag), BindingFlags.Public | BindingFlags.Static)!;

        int n = 0;
        n += Hook(PutDrawEnv, beforeEnv, null);
        n += Hook(DrawOTag, beforeOt, afterOt);

        HookManager.Commit();
        Console.WriteLine($"[KF2] dither: {(Enabled ? "off" : "on (pass-through)")}, {n} hook(s)");

        if (n == 0)
            Console.Error.WriteLine("[KF2] dither: nothing hooked — the probe will read nothing. " +
                                    "The picture is unaffected: the GPU mask does not need the hooks.");
    }

    static int Hook((string Overlay, uint Addr)[] sites, MethodInfo pre, MethodInfo? post)
    {
        int n = 0;
        foreach (var (overlay, addr) in sites)
        {
            var target = SymbolRegistry.Resolve(overlay, null, addr);
            if (target == null)
            {
                Console.Error.WriteLine($"[KF2] dither: no function at {overlay}/0x{addr:X8}");
                continue;
            }
            if (HookManager.AddPre(_self, target, pre)) n++;
            if (post != null && HookManager.AddPost(_self, target, post)) n++;
        }
        return n;
    }

    // ---- PutDrawEnv: the probe's count of draw envs that asked for dither ----

    public static void BeforePutDrawEnv(CpuContext c, IMemory m)
    {
        if (_toConsole && m.ReadU8(c.A0 + 0x16) != 0) _envHits++;
    }

    // ---- DrawOTag: the probe's count of E1 words linked in among the primitives --
    //
    // Read-only. The walk is DrawOTag's own: follow the header's `next` field until
    // the end marker, and read each packet's words out of the count in its top byte.

    public static void BeforeDrawOTag(CpuContext c, IMemory m)
    {
        if (_toConsole)
        {
            uint mask = RecompOne.Runtime.Runtime.RamWordMask;
            uint addr = c.A0 & mask;
            for (int guard = 0; guard < 0x100000; guard++)
            {
                uint header = m.ReadU32(addr);
                Scan(m, addr + 4u, header >> 24);

                uint next = header & 0xFFFFFFu;
                if (next == 0xFFFFFFu || (next & 0x800000u) != 0) break;
                addr = next & mask;
            }
        }

        Report();
    }

    public static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        // Proof rather than inference, for the probe run: GPUSTAT bit 9 is the
        // dither state the rasterizer and the shader both read, so sampling it
        // after a whole frame has been drawn says whether anything got through.
        // Only the read of bit 31 has a side effect and no game code sees this
        // register -- DrawSync is HLE'd and never polls it.
        if (_toConsole && RecompOne.Runtime.Runtime.Gpu is { } gpu)
            _stat |= (gpu.ReadStat() >> 9) & 1u;
    }

    // Step through one packet's words as commands, counting any E1 that asks for dither.
    static void Scan(IMemory m, uint words, uint count)
    {
        for (uint i = 0; i < count; )
        {
            uint at = words + i * 4u;
            uint word = m.ReadU32(at);

            if (word >> 24 == 0xE1)
            {
                if ((word & 0x200u) != 0) _otHits++;
                i++;
                continue;
            }

            int len = Length(word);
            if (len <= 0) return;      // variable-length command: stop guessing
            i += (uint)len;
        }
    }

    // GP0 command lengths, as the runtime's own GPU decodes them. Negative means
    // the length depends on data that follows -- a polyline runs to its 0x55555555
    // terminator, an image load is followed by raw pixels -- and neither can be
    // walked past safely from here.
    static int Length(uint word)
    {
        uint op = word >> 24;
        switch (op)
        {
            case 0x02: return 3;
            case >= 0x20 and <= 0x3F:
            {
                int n = (word & (1u << 27)) != 0 ? 4 : 3;
                bool shaded = (word & (1u << 28)) != 0;
                bool tex = (word & (1u << 26)) != 0;
                return 1 + n + (shaded ? n - 1 : 0) + (tex ? n : 0);
            }
            case >= 0x40 and <= 0x5F:
            {
                if ((word & (1u << 27)) != 0) return -1;
                bool shaded = (word & (1u << 28)) != 0;
                return 1 + 2 + (shaded ? 1 : 0);
            }
            case >= 0x60 and <= 0x7F:
            {
                int sz = (int)((word >> 27) & 3);
                bool tex = (word & (1u << 26)) != 0;
                return 1 + 1 + (tex ? 1 : 0) + (sz == 0 ? 1 : 0);
            }
            case >= 0x80 and <= 0x9F: return 4;
            case >= 0xA0 and <= 0xBF: return -1;
            case >= 0xC0 and <= 0xDF: return 3;
            default: return 1;
        }
    }

    static void Report()
    {
        _frames++;
        double window = Now - _windowStart;
        if (window < 2.0) return;

        if (_toConsole)
            Console.WriteLine($"[KF2] dither: {_envHits / window:F0} draw envs/s and {_otHits / window:F0} " +
                              $"ordering-table words/s asked for dither, over {_frames / window:F0} frames/s; " +
                              $"GPUSTAT dither bit {_stat}");

        _envHits = _otHits = _frames = 0;
        _stat = 0;
        _windowStart = Now;
    }
}
