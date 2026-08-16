// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.NoDither;

/// <summary>
/// Turns off the GPU's ordered dither -- the 4x4 crosshatch that sits over every
/// shaded surface in the game.
///
/// The PlayStation frame buffer is 15-bit, so the GPU has to drop the low three
/// bits of each 8-bit colour component on the way in. With the dither bit set it
/// first adds a bias from a 4x4 table indexed by the pixel's own coordinates, so
/// the truncation error alternates per pixel and a gradient reads as a texture of
/// tiny checkers instead of as bands. Clearing the bit skips the bias: the same
/// quantization happens, straight, and gradients band instead. That trade is the
/// whole of this mod -- there is no third option at 15 bits.
///
/// The runtime honours the bit on both render paths, so nothing here is
/// backend-specific: the software rasterizer adds the table entry in
/// <c>GpuRaster.Plot</c>, and the hardware backend packs it into the vertex
/// texpage word and applies it in <c>quant5</c> in the fragment shader. Both read
/// one piece of GPU state, <c>_dither</c>, and that is set from exactly one place:
/// bit 9 of a GP0(E1) draw-mode word. So "remove the dithering" means "make sure
/// no E1 word with bit 9 set ever reaches the GPU", and two of the three ways one
/// gets there are hookable:
///
///   * **PutDrawEnv**, from the <c>dtd</c> flag at <c>DRAWENV+0x16</c>. The
///     library turns that byte straight into bit 9 of the E1 word it sends. This
///     is the one this game uses, and it uses only this one: the counter below
///     measures exactly one dithered draw env per frame -- 30 a second at 30 fps
///     -- on the title screen and in `fdat02` alike.
///   * **A DR_MODE or DR_TPAGE packet in the ordering table**, which is an E1
///     word the game linked in among the primitives. <c>SetDrawTPage</c> builds
///     one with the bit clear, <c>SetDrawMode</c> can build one with it set.
///     Measured over the same two, this game emits none at all -- that counter
///     stays at zero -- but the scan is cheap, it only writes when it finds one,
///     and a mid-OT E1 would silently undo the first hook for the rest of the
///     frame. So it is covered rather than assumed away.
///
/// The third way is the parts of libgpu that are still recompiled MIPS, writing
/// GP0 through the register <c>PSMemory</c> traps. No mod can hook that, so it is
/// checked rather than intercepted: GPUSTAT bit 9 is the state both renderers
/// actually read, and <c>KF2_NODITHER_PROBE=1</c> samples it after each frame is
/// drawn. It reading back 0 for a whole session is what says nothing took that
/// route -- without it, "both hooks fire" would only be evidence about the two
/// routes that were already known about.
///
/// Neither hooked route is rewritten, only borrowed: the <c>dtd</c> byte and any
/// E1 word found are cleared in the pre-hook and put back in the post-hook, so
/// the game's own memory is identical either side of the call and unloading the
/// mod leaves nothing behind. That matters for the packet buffer especially --
/// some of it is built once and re-sent for the rest of the run, so a bit cleared
/// in place would stay cleared long after the mod was switched off.
///
/// The ordering-table scan is a word-level walk rather than a search for bytes
/// that look like E1: a colour or a vertex word can perfectly well have 0xE1 in
/// its top byte, and clearing bit 9 of a coordinate would move geometry. So the
/// scan steps command by command using the GP0 command lengths, and stops at the
/// two commands whose length it cannot know without more state (a polyline, and
/// an image load followed by raw pixel data). Neither appears in this game's
/// ordering tables.
///
/// <see cref="_on"/> is a separate switch from the mod being loaded so the two
/// pictures can be compared without a reload; the hooks stay attached and do
/// nothing.
/// </summary>
public sealed class NoDitherMod : IMod
{
    const string OnKey = "kf2.nodither.on";

    static bool _on = true;
    static bool _toConsole;

    // PutDrawEnv's dtd byte, held across the call. The library is not reentrant
    // here -- one env is sent at a time -- so one slot is enough.
    static uint _envAddr;
    static byte _envDtd;
    static bool _envPatched;

    // Ordering-table E1 words cleared this frame, with what they were.
    static readonly List<(uint Addr, uint Word)> _saved = new();

    // Per report window: draw envs that asked for dither, and OT words that did.
    static long _envHits, _otHits, _frames;
    static uint _statDither;
    static double _windowStart;
    static string _report = "nothing drawn yet";

    static double Now => Environment.TickCount64 / 1000.0;

    public void OnLoad()
    {
        _on = RecompOne.Runtime.Runtime.View.GetBool(OnKey, true);
        _windowStart = Now;

        if (Environment.GetEnvironmentVariable("KF2_NODITHER_PROBE") is { Length: > 0 } probe &&
            !probe.Equals("0", StringComparison.Ordinal))
            _toConsole = true;

        Console.WriteLine($"[nodither] dither {(_on ? "off" : "on (pass-through)")}");
    }

    public void OnUnload()
    {
        _on = false;
        _saved.Clear();
        _envPatched = false;
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped("Clears the GPU's dither bit. The 4x4 crosshatch over shaded " +
                          "surfaces goes away; because the frame buffer is still 15-bit, " +
                          "what replaces it is banding in the same gradients.");
        ImGui.Separator();

        bool on = _on;
        if (ImGui.Checkbox("Dither off", ref on))
        {
            _on = on;
            RecompOne.Runtime.Runtime.View.SetBool(OnKey, _on);
            RecompOne.Runtime.Runtime.SaveView();
        }

        ImGui.Separator();
        ImGui.TextWrapped(_report);
        ImGui.TextWrapped("Draw envs are where this game's dither comes from. An ordering " +
                          "table count above zero means the game also sets the mode mid-frame, " +
                          "which is the case this mod covers and the draw-env hook alone " +
                          "would not.");
    }

    // ---- PutDrawEnv: DRAWENV.dtd (+0x16) becomes bit 9 of the E1 word --------

    [PreHook("open", Address = 0x800160D0)]
    [PreHook("game", Address = 0x80060870)]
    [PreHook("end",  Address = 0x80013DD8)]
    static void BeforePutDrawEnv(CpuContext c, IMemory m)
    {
        _envPatched = false;
        if (!_on) return;

        uint env = c.A0;
        byte dtd = m.ReadU8(env + 0x16);
        if (dtd == 0) return;

        m.WriteU8(env + 0x16, 0);
        _envAddr = env;
        _envDtd = dtd;
        _envPatched = true;
        _envHits++;
    }

    [PostHook("open", Address = 0x800160D0)]
    [PostHook("game", Address = 0x80060870)]
    [PostHook("end",  Address = 0x80013DD8)]
    static void AfterPutDrawEnv(CpuContext c, IMemory m)
    {
        if (!_envPatched) return;
        m.WriteU8(_envAddr + 0x16, _envDtd);
        _envPatched = false;
    }

    // ---- DrawOTag: an E1 word linked in among the primitives ----------------
    //
    // The walk is DrawOTag's own: follow the header's `next` field until the end
    // marker, and read each packet's words out of the count in its top byte. The
    // pre-hook runs before any other mod's replacement of this function, so this
    // composes with the widescreen mod, which owns the replacement.

    [PreHook("open", Address = 0x80016078)]
    [PreHook("game", Address = 0x80060818)]
    [PreHook("end",  Address = 0x80013D80)]
    static void BeforeDrawOTag(CpuContext c, IMemory m)
    {
        _saved.Clear();
        if (!_on) { Report(); return; }

        uint addr = c.A0 & 0x1FFFFCu;
        for (int guard = 0; guard < 0x100000; guard++)
        {
            uint header = m.ReadU32(addr);
            Scan(m, addr + 4u, header >> 24);

            uint next = header & 0xFFFFFFu;
            if (next == 0xFFFFFFu || (next & 0x800000u) != 0) break;
            addr = next & 0x1FFFFCu;
        }

        Report();
    }

    [PostHook("open", Address = 0x80016078)]
    [PostHook("game", Address = 0x80060818)]
    [PostHook("end",  Address = 0x80013D80)]
    static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        for (int i = 0; i < _saved.Count; i++) m.WriteU32(_saved[i].Addr, _saved[i].Word);
        _saved.Clear();

        // Proof rather than inference, for the probe run: GPUSTAT bit 9 is the
        // dither state the rasterizer and the shader both read, so sampling it
        // after a whole frame has been drawn says whether anything got through.
        // Only the read of bit 31 has a side effect and no game code sees this
        // register -- DrawSync is HLE'd and never polls it.
        if (_toConsole && RecompOne.Runtime.Runtime.Gpu is { } gpu)
            _statDither |= (gpu.ReadStat() >> 9) & 1u;
    }

    // Step through one packet's words as commands, clearing bit 9 of any E1.
    static void Scan(IMemory m, uint words, uint count)
    {
        for (uint i = 0; i < count; )
        {
            uint at = words + i * 4u;
            uint word = m.ReadU32(at);

            if (word >> 24 == 0xE1)
            {
                if ((word & 0x200u) != 0)
                {
                    _saved.Add((at, word));
                    m.WriteU32(at, word & ~0x200u);
                    _otHits++;
                }
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

        _report = $"{_envHits / window:F0} draw envs/s and {_otHits / window:F0} ordering-table " +
                  $"words/s asked for dither, over {_frames / window:F0} frames/s";
        if (_toConsole)
            Console.WriteLine($"[nodither] {_report}; GPUSTAT dither bit {_statDither}");

        _envHits = _otHits = _frames = 0;
        _statDither = 0;
        _windowStart = Now;
    }
}
