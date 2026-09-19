using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// Draw the world live behind a menu or a message box, instead of the 320-wide copy of
/// the frozen frame.
///
///     KF2_MENUWORLD=0         the game's frozen copy -- comparison only
///     KF2_MENUWORLD_PROBE=1   world passes a second, primitive bytes used, overflows
///
/// Every menu (the in-game menu, the shops, the save list) runs on one framework:
/// `func_80022754` shrinks the primitive buffers and stores the displayed frame,
/// `func_80022530` is the frame head and `func_800226A8` the presenter, which pastes
/// the stored frame back with `LoadImage` each frame. That paste is 1x, 320 wide and
/// has no depth, so the margin is never drawn and nothing that needs a depth (AO,
/// the Z-buffer) reaches it.
///
/// The presenter is replaced: stage 13's drawing routines run into an ordering table
/// of their own in scratch RAM, that table's terminator is linked to the head of the
/// menu's table, and one `DrawOTag` draws both. Nothing that advances the world is
/// called. See "Menus draw the world live" in docs/PATCHES_AND_MODS.md.
///
/// A message box (`func_80035B48`) is the same shape with its own loop, the fade
/// `func_800356F4`, which pastes a `MoveImage` copy of the frame dimmed by its
/// brightness. <see cref="MessageFade"/> is that loop in C# with the world drawn live;
/// see "Messages draw the world live" there.
/// </summary>
public static class MenuWorld
{
    const uint Presenter = 0x800226A8;
    const uint Enter = 0x80022754;
    const uint Leave = 0x800228C8;
    const uint Renderer = 0x800342D8;
    const uint MainLoopMarker = 0x800140AC;   // stage 9, called by the main loop only
    const uint Fade = 0x800356F4;             // a message box's fade loop

    // Sound players a world pass must not reach: the centred one (ambient sources in
    // the object walk) and the placed one.
    const uint SoundCentred = 0x80014158;
    const uint SoundPlaced = 0x80013D08;

    const uint BufferIndex = 0x8017E084;      // u8
    const uint Descriptor0 = 0x8017E08C, Descriptor1 = 0x8017E098;
    const uint ActiveDescriptor = 0x8017E0A4;
    const uint OtPointer = 0x8018E0A8;
    const uint DrawEnvs = 0x8018E0AC, DrawEnvStride = 0x5C;
    const uint DispEnvs = 0x8018E164, DispEnvStride = 0x14;
    const uint SavedDescriptor1 = 0x8006EB30; // func_80022754's copy of the world's
    const uint CurrentBank = 0x8018E19C, CurrentMesh = 0x8018EAA0;

    // Zeroed by stage 13's head, func_8002E064, which a pass does not call.
    static readonly uint[] FrameCounters = [0x801DA554, 0x80192D54, 0x80192D50];

    const uint OtEntries = 0x2000;
    const uint MenuPrimBytes = 0xC800;        // both shrunk buffers
    const uint MessageVramSave = 0x25800;     // func_80035B48's StoreImage after them, restored at the end
    const uint MessageRect = 0x8006E610;      // RECT func_80035B48 saves, MoveImages into, restores
    const uint MessageSave = 0x8017E09C;      // where it saved it: start + 0xC800
    const uint ScratchBytes = 0x10 + OtEntries * 4;

    public static bool Enabled { get; private set; } = true;
    static bool _probe;

    static bool _hooked, _queued, _worldSeen, _session;
    static int _depth;
    static bool _drawing;

    /// <summary>A message box is being drawn over the live world, so its dim is the
    /// overlay's to draw (<see cref="MessageText"/>) when the text is.</summary>
    public static bool MessageLive { get; private set; }
    static int _messages, _messagesRefused;

    // Where a session's pass writes: a descriptor and an OT in the frozen frame's
    // store, which nothing reads once the paste is gone, and a world-sized buffer.
    static uint _scratch, _bufLo, _bufHi;

    // The world's state as the last stage 13 left it, put back before a pass.
    static readonly Gte.State _worldGte = new(), _menuGte = new();
    static uint _worldBank, _worldMesh;

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _windowMs;
    static int _passes, _overflows, _refused;
    static uint _peak;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.menuworld",
        Name = "Menu world",
        Version = "1.0",
        Description = "Draws the world live behind menus and shops instead of a frozen 320-wide copy.",
    };

    public static void Configure(string? enabled, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
        if (!string.IsNullOrWhiteSpace(probe)) _probe = probe != "0";
    }

    public static void Install()
    {
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            _worldSeen = false;
            _session = false;
            _depth = 0;
        });
        HookAttach.OnOverlayLoad("menu world", Attach);
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        var presenter = SymbolRegistry.Resolve("game", null, Presenter);
        var enter = SymbolRegistry.Resolve("game", null, Enter);
        var leave = SymbolRegistry.Resolve("game", null, Leave);
        var renderer = SymbolRegistry.Resolve("game", null, Renderer);
        var marker = SymbolRegistry.Resolve("game", null, MainLoopMarker);
        var centred = SymbolRegistry.Resolve("game", null, SoundCentred);
        var placed = SymbolRegistry.Resolve("game", null, SoundPlaced);
        var fade = SymbolRegistry.Resolve("game", null, Fade);
        if (fade == null || presenter == null || enter == null || leave == null || renderer == null ||
            marker == null || centred == null || placed == null)
        {
            Console.Error.WriteLine("[KF2] menu world: game functions not found; menus keep the frozen frame.");
            return false;
        }

        MethodInfo M(string name) => typeof(MenuWorld).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
        // Queued once: Installed() is true for a function any patch has hooked, so it
        // cannot say whether *these* delegates are on it. A retry only re-commits.
        if (!_queued)
        {
            _queued = true;
            HookManager.AddReplace(_self, presenter, M(nameof(Present)));
            HookManager.AddPost(_self, enter, M(nameof(AfterEnter)));
            HookManager.AddPost(_self, leave, M(nameof(AfterLeave)));
            HookManager.AddPost(_self, renderer, M(nameof(AfterRenderer)));
            HookManager.AddPre(_self, marker, M(nameof(MainLoop)));
            HookManager.AddPre(_self, centred, M(nameof(Quiet)));
            HookManager.AddPre(_self, placed, M(nameof(Quiet)));
            HookManager.AddReplace(_self, fade, M(nameof(MessageFade)));
        }
        HookManager.Commit();

        _hooked = HookAttach.Installed(presenter) && HookAttach.Installed(enter) &&
                  HookAttach.Installed(leave) && HookAttach.Installed(renderer) &&
                  HookAttach.Installed(marker) && HookAttach.Installed(centred) &&
                  HookAttach.Installed(placed) && HookAttach.Installed(fade);
        Console.WriteLine($"[KF2] menu world: {(Enabled && _hooked ? "on" : "off")}" +
                          (_hooked ? "" : " (hooks incomplete)"));
        return _hooked;
    }

    public static bool Quiet(CpuContext c, IMemory m) => !_drawing;

    public static void MainLoop(CpuContext c, IMemory m)
    {
        // The main loop running means no menu is: a session left some other way ends here.
        _session = false;
        _depth = 0;
    }

    public static void AfterRenderer(CpuContext c, IMemory m)
    {
        if (_drawing) return;
        _worldSeen = true;
        Gte.Save(_worldGte);
        _worldBank = m.ReadU32(CurrentBank);
        _worldMesh = m.ReadU32(CurrentMesh);
    }

    public static void AfterEnter(CpuContext c, IMemory m)
    {
        if (_depth++ > 0) return;
        _session = Enabled && _hooked && _worldSeen && Layout(m);
        if (!_session && Enabled && _hooked && _worldSeen) _refused++;
    }

    public static void AfterLeave(CpuContext c, IMemory m)
    {
        if (_depth > 0 && --_depth == 0) _session = false;
    }

    /// <summary>Check the shrunk layout is the one expected, and find room for a pass.</summary>
    static bool Layout(IMemory m)
    {
        uint start0 = m.ReadU32(Descriptor0);
        if (m.ReadU32(Descriptor0 + 4u) != start0 + 0x6400u ||
            m.ReadU32(Descriptor1) != start0 + 0x6400u ||
            m.ReadU32(Descriptor1 + 4u) != start0 + MenuPrimBytes)
            return false;

        uint scratch = start0 + MenuPrimBytes;
        uint scratchEnd = scratch + ScratchBytes;

        if (!PrimBuffer.TrySecondBuffer(out uint lo, out uint hi))
        {
            lo = m.ReadU32(SavedDescriptor1);
            hi = m.ReadU32(SavedDescriptor1 + 4u);
        }

        uint ramEnd = 0x80000000u + Runtime.RamSize;
        if (hi <= lo || hi > ramEnd || scratchEnd > ramEnd) return false;
        if (lo < scratchEnd && start0 < hi) return false;   // must not touch the menu's own

        _scratch = scratch;
        _bufLo = lo;
        _bufHi = hi;
        return true;
    }

    /// <summary>Where a message box's pass can go: its shrunk layout and VRAM save take
    /// `start .. start + 0x32000`, so the table goes after them and the primitives in
    /// PrimBuffer's second buffer. Needs the relocated buffers; the stock ones are full.</summary>
    static bool MessageLayout(IMemory m, out uint scratch, out uint lo, out uint hi)
    {
        scratch = lo = hi = 0;
        uint start0 = m.ReadU32(Descriptor0);
        if (m.ReadU32(Descriptor0 + 4u) != start0 + 0x6400u ||
            m.ReadU32(Descriptor1) != start0 + 0x6400u ||
            m.ReadU32(Descriptor1 + 4u) != start0 + MenuPrimBytes)
            return false;
        if (!PrimBuffer.TrySecondBuffer(out lo, out hi)) return false;
        if (m.ReadU32(MessageSave) != start0 + MenuPrimBytes) return false;
        scratch = start0 + MenuPrimBytes + MessageVramSave;
        uint ramEnd = 0x80000000u + Runtime.RamSize;
        return hi > lo && hi <= ramEnd && scratch + ScratchBytes <= lo && scratch >= start0;
    }

    /// <summary>
    /// `func_800356F4(brightness, step)`: one step of the fade a frame until the
    /// brightness leaves 1..0x77, or until a button once all were up. Returns -1 or -2
    /// when the fade ran out (-2: the buttons came up) and the brightness when a press
    /// cut it short -- `func_80035B48` reads exactly that.
    ///
    /// The game's step is stage 13's head, four quads, `DrawSync`, stage 13's presenter.
    /// Here the world pass runs into its own table ahead of the message's, and the two
    /// quads that pasted the dimmed frame are gone. A message <see cref="MessageText"/>
    /// has decoded draws nothing at all -- the overlay dims and writes -- and any other
    /// keeps the game's two text quads over a 50% black quad.
    /// </summary>
    public static void MessageFade(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (!(Enabled && _hooked && _worldSeen && MessageLayout(m, out uint scratch, out uint lo, out uint hi)))
        {
            if (Enabled && _hooked) _messagesRefused++;
            orig(c, m);
            return;
        }

        var mem = (PSMemory)m;
        var saved = c.Snapshot();
        (uint, uint, uint) menu = (_scratch, _bufLo, _bufHi);
        _scratch = scratch; _bufLo = lo; _bufHi = hi;
        MessageLive = true;
        _messages++;

        int b = (int)c.A0, step = (int)c.A1, state = -1, frames = 0;
        bool ranOut = false;
        uint result;
        try
        {
            // The MoveImage copy sits on world texture pages; the pass needs them back.
            c.A0 = MessageRect;
            c.A1 = mem.ReadU32(MessageSave);
            KingsField2.func_80060624(c, mem);                  // LoadImage
            c.A0 = 0u;
            KingsField2.func_800605A4(c, mem);

            while (true)
            {
                Step(c, mem, b);
                frames++;

                b += step;
                if ((uint)(b - 1) >= 0x77u) { result = (uint)state; ranOut = true; break; }

                if (Pad(c, mem, ref state)) { result = (uint)b; break; }
            }

            // Faded in: the caller waits for a button drawing nothing, which leaves the
            // wide target idle. Wait here instead, still drawing, and hand it the 0x50
            // its own wait would have.
            if (step > 0 && ranOut)
            {
                int held = b - step;
                while (!Pad(c, mem, ref state)) { Step(c, mem, held); frames++; }
                result = 0x50u;
            }

            c.A0 = 0u;
            KingsField2.func_800605A4(c, mem);
        }
        finally
        {
            (_scratch, _bufLo, _bufHi) = menu;
            MessageLive = false;
        }

        if (_probe)
            Console.WriteLine($"[KF2] menu world: message fade {step:+0;-0} over the live world, {frames} frame(s), " +
                              $"{(MessageText.Covering ? "text drawn by the overlay" : "the game's picture kept")}");
        c.Restore(saved);
        c.V0 = result;
    }

    /// <summary>One frame of the fade: stage 13's head, the world pass ahead of the
    /// message's table, DrawSync, stage 13's presenter.</summary>
    static void Step(CpuContext c, PSMemory mem, int b)
    {
        KingsField2.func_8002E064(c, mem);                      // head: flip, OT, descriptor
        uint ot = mem.ReadU32(OtPointer);
        if (!MessageText.Covering) MessageQuads(mem, ot, b);

        uint head = DrawWorld(c, mem, ot + (OtEntries - 1u) * 4u);

        c.A0 = 0u;
        KingsField2.func_800605A4(c, mem);                      // DrawSync(0)
        mem.WriteU32(OtPointer, head - (OtEntries - 1u) * 4u);
        c.S2 = (uint)b;                                          // MessageText reads it here
        KingsField2.func_8002E0FC(c, mem);                      // stage 13's presenter
        mem.WriteU32(OtPointer, ot);
        if (_probe) Report();
    }

    /// <summary>The fade's button test: -1 until every button is up, then -2 until one
    /// is pressed, which is true.</summary>
    static bool Pad(CpuContext c, PSMemory mem, ref int state)
    {
        c.A0 = 1u;
        KingsField2.PadRead_game(c, mem);
        uint pad = c.V0;
        if (state == -1) { if (pad == 0) state = -2; return false; }
        return pad != 0;
    }

    /// <summary>What is left of the game's four quads without the paste: a black quad at
    /// 50% once the fade is half up (the console has no multiply to match its
    /// `0x80 - b/2` exactly), then the picture subtractive and additive, as it drew them.</summary>
    static void MessageQuads(PSMemory mem, uint ot, int b)
    {
        uint desc = mem.ReadU32(ActiveDescriptor);
        uint cur = mem.ReadU32(desc + 8u), end = mem.ReadU32(desc + 4u);
        if (cur + 0x28u * 2u + 0x18u + 0x0Cu > end) return;

        uint tpage0 = TPage(0, 1, 0x3C0, 0x100), tpage1 = TPage(0, 2, 0x3C0, 0x100);
        uint clut = (511u << 6) | ((576u >> 4) & 0x3Fu);

        // Bucket 1 (subtractive), then 0 (additive), drawn after 2 because the table
        // is walked from the far end.
        Ft4(mem, cur, (uint)b, clut, tpage1); Link(mem, ot + 4u, cur); cur += 0x28u;
        Ft4(mem, cur, (uint)b, clut, tpage0); Link(mem, ot, cur); cur += 0x28u;

        if (b >= 0x36)
        {
            // POLY_F4, semi-transparent, black: B/2 + 0 under mode 0, which the DR_MODE
            // linked after it (so walked before it) selects.
            mem.WriteU32(cur, 0x05000000u);
            mem.WriteU32(cur + 4u, 0x2A000000u);
            mem.WriteU32(cur + 8u, Xy(-512, 0));
            mem.WriteU32(cur + 12u, Xy(832, 0));
            mem.WriteU32(cur + 16u, Xy(-512, 240));
            mem.WriteU32(cur + 20u, Xy(832, 240));
            Link(mem, ot + 8u, cur); cur += 0x18u;

            mem.WriteU32(cur, 0x02000000u);
            mem.WriteU32(cur + 4u, 0xE1000000u | 0x200u);   // tpage 0, mode 0, dither on
            mem.WriteU32(cur + 8u, 0xE2000000u);
            Link(mem, ot + 8u, cur); cur += 0x0Cu;
        }

        mem.WriteU32(desc + 8u, cur);
    }

    static uint TPage(uint tp, uint abr, uint x, uint y) =>
        ((tp & 3u) << 7) | ((abr & 3u) << 5) | ((y & 0x100u) >> 4) | ((x & 0x3FFu) >> 6) | ((y & 0x200u) << 2);

    static uint Xy(int x, int y) => ((uint)(ushort)(short)y << 16) | (ushort)(short)x;

    static void Ft4(PSMemory mem, uint p, uint rgb, uint clut, uint tpage)
    {
        mem.WriteU32(p, 0x09000000u);
        mem.WriteU32(p + 4u, 0x2E000000u | (rgb << 16) | (rgb << 8) | rgb);   // textured, semi-transparent
        mem.WriteU32(p + 8u, Xy(0x20, 0x0A));
        mem.WriteU32(p + 12u, (clut << 16) | 0x0000u);
        mem.WriteU32(p + 16u, Xy(0x11F, 0x0A));
        mem.WriteU32(p + 20u, (tpage << 16) | 0x00FFu);
        mem.WriteU32(p + 24u, Xy(0x20, 0x109));
        mem.WriteU32(p + 28u, 0xFF00u);
        mem.WriteU32(p + 32u, Xy(0x11F, 0x109));
        mem.WriteU32(p + 36u, 0xFFFFu);
    }

    /// <summary>AddPrim.</summary>
    static void Link(PSMemory mem, uint entry, uint prim)
    {
        uint e = mem.ReadU32(entry);
        mem.WriteU32(prim, (mem.ReadU32(prim) & 0xFF000000u) | (e & 0x00FFFFFFu));
        mem.WriteU32(entry, (e & 0xFF000000u) | (prim & 0x00FFFFFFu));
    }

    /// <summary>`func_800226A8`: DrawSync, VSync, PutDrawEnv, PutDispEnv, the paste,
    /// DrawOTag -- with the world in place of the paste.</summary>
    public static void Present(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (!_session)
        {
            orig(c, m);
            return;
        }

        var mem = (PSMemory)m;
        uint sp = c.SP, s0 = c.S0, ra = c.RA;

        c.A0 = 0u;
        KingsField2.func_800605A4(c, mem);          // DrawSync(0)

        uint menuOt = mem.ReadU32(OtPointer);
        uint head = DrawWorld(c, mem, menuOt + (OtEntries - 1u) * 4u);

        c.A0 = 0u;
        KingsField2.func_8005FCC8(c, mem);          // VSync(0)

        uint idx = mem.ReadU8(BufferIndex);
        c.A0 = DrawEnvs + idx * DrawEnvStride;
        KingsField2.func_80060870(c, mem);          // PutDrawEnv
        c.A0 = DispEnvs + idx * DispEnvStride;
        KingsField2.func_80060990(c, mem);          // PutDispEnv

        c.A0 = head;
        KingsField2.func_80060818(c, mem);          // DrawOTag

        c.SP = sp;
        c.S0 = s0;
        c.RA = ra;
        if (_probe) Report();
    }

    /// <summary>Stage 13's drawing half into the pass's own table, linked in front of
    /// <paramref name="menuHead"/>. Returns the entry to walk from.</summary>
    static uint DrawWorld(CpuContext c, PSMemory mem, uint menuHead)
    {
        uint desc = _scratch;
        uint ot = _scratch + 0x10u;

        var regs = c.Snapshot();
        Gte.Save(_menuGte);
        uint menuOt = mem.ReadU32(OtPointer);
        uint menuDesc = mem.ReadU32(ActiveDescriptor);
        uint menuBank = mem.ReadU32(CurrentBank);
        uint menuMesh = mem.ReadU32(CurrentMesh);

        // ClearOTagR: each entry points at the one below it, entry 0 ends the list.
        mem.WriteU32(ot, 0x00FFFFFFu);
        for (uint i = 1; i < OtEntries; i++)
            mem.WriteU32(ot + i * 4u, (ot + (i - 1u) * 4u) & 0x00FFFFFFu);

        mem.WriteU32(desc, _bufLo);
        mem.WriteU32(desc + 4u, _bufHi);
        mem.WriteU32(desc + 8u, _bufLo);
        mem.WriteU32(OtPointer, ot);
        mem.WriteU32(ActiveDescriptor, desc);
        mem.WriteU32(CurrentBank, _worldBank);
        mem.WriteU32(CurrentMesh, _worldMesh);
        foreach (uint a in FrameCounters) mem.WriteU32(a, 0u);
        Gte.Load(_worldGte);

        _drawing = true;
        SpriteAnim.Hold = true;
        try
        {
            c.A0 = 0u; c.A1 = 0u;
            KingsField2.func_8002E22C(c, mem);      // the stored view, as a0 = a1 = 0 asks
            KingsField2.func_8002D3A8(c, mem);      // the cull grid
            KingsField2.func_80032400(c, mem);      // the arm
            KingsField2.func_80031D5C(c, mem);      // the HUD
            KingsField2.func_80033E78(c, mem);
            KingsField2.func_80031C94(c, mem);      // the map tiles
            KingsField2.func_800331B4(c, mem);      // objects, creatures, effects, sprites
            KingsField2.func_8003202C(c, mem);      // the four screen tints
            KingsField2.func_800320BC(c, mem);
            KingsField2.func_8003214C(c, mem);
            KingsField2.func_80032234(c, mem);
        }
        finally
        {
            _drawing = false;
            SpriteAnim.Hold = false;

            uint cur = mem.ReadU32(desc + 8u);
            uint used = cur - _bufLo;
            if (used > _peak) _peak = used;
            if (cur > _bufHi) _overflows++;
            _passes++;

            mem.WriteU32(OtPointer, menuOt);
            mem.WriteU32(ActiveDescriptor, menuDesc);
            mem.WriteU32(CurrentBank, menuBank);
            mem.WriteU32(CurrentMesh, menuMesh);
            Gte.Load(_menuGte);
            c.Restore(regs);
        }

        mem.WriteU32(ot, menuHead & 0x00FFFFFFu);
        return ot + (OtEntries - 1u) * 4u;
    }

    static void Report()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double elapsed = now - _windowMs;
        if (elapsed < 1000.0) return;
        Console.WriteLine($"[KF2] menu world: {_passes * 1000.0 / elapsed:0.#} passes/s, " +
                          $"peak {_peak}/{_bufHi - _bufLo} bytes at 0x{_bufLo:X8}, {_overflows} overflow(s), " +
                          $"table at 0x{_scratch + 0x10u:X8}, {_refused} session(s) refused, " +
                          $"{_messages} message(s) live, {_messagesRefused} left to the game");
        _windowMs = now;
        _passes = _overflows = 0;
        _peak = 0;
    }
}
