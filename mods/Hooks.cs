using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace Kf2.Mods;

/// <summary>
/// The attachment points mods hang off, and the only names config/kf2.json ever
/// has to know. A mod claims one of these at run time; the config entry that
/// creates it never changes, so adding or removing a mod costs no recompile.
///
/// Two kinds:
///
///   <see cref="AfterDrawOTag"/>   a `post` hook on libgpu's DrawOTag in all
///                                 three overlays -- the end of a rendered frame.
///
///   <see cref="Stages"/> entries  `pre` hooks on the thirteen calls the main
///                                 game loop makes each iteration. A pre hook that
///                                 returns false makes the recompiled body not
///                                 run at all (PreHook.Run propagates the bool),
///                                 which is what lets a mod gate part of the
///                                 frame without touching the rest.
/// </summary>
public static class Hooks
{
    /// <summary>Fires once per rendered frame, after the ordering table is walked.</summary>
    public static event Action? FrameDrawn;

    /// <summary>Fires once per VSync(0), i.e. per vblank the game charges itself.</summary>
    public static event Action? VBlank;

    /// <summary>
    /// Consulted before each main-loop stage. Returning false skips that stage
    /// for this iteration. Null means "run everything", the unmodded behaviour.
    /// </summary>
    public static Func<uint, bool>? StageGate;

    /// <summary>Called before each main-loop stage that is about to run, for probes.</summary>
    public static event Action<uint, IMemory>? StageEntered;

    // VSync(0) calls charged to the frame currently being drawn.
    static int _vblanks;

    public static void Install() => Event.AddListener<VSyncEvent>(_ =>
    {
        _vblanks++;
        VBlank?.Invoke();
    });

    /// <summary>
    /// The frame boundary. A second ordering table with no vblank between it and
    /// the first belongs to the frame already in flight -- King's Field draws one
    /// OT per frame, but defining the boundary by the vblank rather than by the
    /// call keeps anything that draws two from being charged twice.
    /// </summary>
    public static void AfterDrawOTag(CpuContext c, IMemory m)
    {
        if (_vblanks == 0) return;
        VBlanksThisFrame = _vblanks;
        _vblanks = 0;
        FrameDrawn?.Invoke();
    }

    /// <summary>Vblanks the frame just ended was charged. Valid inside FrameDrawn.</summary>
    public static int VBlanksThisFrame { get; private set; }

    // ---- main game loop stages ---------------------------------------------
    //
    // GAME.EXE's loop is func_8001369C's tail: a flat list of thirteen calls with
    // a backward branch at 0x80013918, the renderer last. Recovered from the
    // emitted C# rather than guessed -- see "The main loop, stage by stage" in
    // NOTES.md for what each one reaches.
    //
    // The addresses are the callees, so a stage is gated wherever it is called
    // from; all thirteen are called from the loop and, as far as the sweep shows,
    // nowhere else per frame.

    public static readonly uint[] Stages =
    [
        0x8002C944, 0x80037C0C, 0x8002A550, 0x80040348, 0x80046A60, 0x8004910C,
        0x8001689C, 0x80025A1C, 0x800140AC, 0x8002CA74, 0x80016FC8, 0x80014534,
        0x800342D8,
    ];

    /// <summary>The last stage, the one that walks the scene into the ordering table.</summary>
    public const uint RenderStage = 0x800342D8;

    static bool Stage(uint address, IMemory m)
    {
        if (StageGate != null && !StageGate(address)) return false;
        StageEntered?.Invoke(address, m);
        return true;
    }

    // One thin entry point per stage: the pre-hook signature carries no address,
    // and deriving it from c.RA (the loop's return address, which is unique per
    // call site) would work but reads as a trick. Thirteen lines is cheaper.
    public static bool Stage_8002C944(CpuContext c, IMemory m) => Stage(0x8002C944, m);
    public static bool Stage_80037C0C(CpuContext c, IMemory m) => Stage(0x80037C0C, m);
    public static bool Stage_8002A550(CpuContext c, IMemory m) => Stage(0x8002A550, m);
    public static bool Stage_80040348(CpuContext c, IMemory m) => Stage(0x80040348, m);
    public static bool Stage_80046A60(CpuContext c, IMemory m) => Stage(0x80046A60, m);
    public static bool Stage_8004910C(CpuContext c, IMemory m) => Stage(0x8004910C, m);
    public static bool Stage_8001689C(CpuContext c, IMemory m) => Stage(0x8001689C, m);
    public static bool Stage_80025A1C(CpuContext c, IMemory m) => Stage(0x80025A1C, m);
    public static bool Stage_800140AC(CpuContext c, IMemory m) => Stage(0x800140AC, m);
    public static bool Stage_8002CA74(CpuContext c, IMemory m) => Stage(0x8002CA74, m);
    public static bool Stage_80016FC8(CpuContext c, IMemory m) => Stage(0x80016FC8, m);
    public static bool Stage_80014534(CpuContext c, IMemory m) => Stage(0x80014534, m);
    public static bool Stage_800342D8(CpuContext c, IMemory m) => Stage(0x800342D8, m);
}
