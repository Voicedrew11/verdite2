using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// The comparison mode behind <c>KF2_FPS_LOGIC=full</c>: run every main-loop
/// stage every rendered frame and scale the movement deltas down instead of
/// ticking the world at its own rate.
///
/// It exists because the two models answer different questions and only one of
/// them can be argued from a counter. <see cref="FramePacing"/>'s fixed timestep
/// keeps every clock in the game exactly right and moves the *camera* between
/// ticks (see <see cref="FrameSmoothing"/>), so input reaches the world at the
/// tick rate however often the picture updates. This mode reaches the world at the
/// render rate -- so turning, walking and collision are genuinely sampled 120
/// times a second -- and pays for it by running every per-tick counter in the game
/// at that rate too.
///
/// **What is wrong with it, up front rather than as a discovery:**
///
/// * The **death sequence** (`0x8019951A`, respawn at 65), the **poison tick**
///   (`func_80024F90(-1)` every 30th frame off `0x80199468`), the **equipment
///   regen** (`func_8002A3DC` gated on `0x80199488 % period`), the **buff timers**
///   at `0x80199472`-`0x80199482`, **spell and effect lifetimes** (`rec+0x0E` in
///   stage 5's 128 slots) and every **animation counter** all run at the render
///   rate. At 120 fps a spell lasts a quarter as long.
/// * **Pitch does not scale.** It steps by a flat 3 per tick to a limit of 32
///   (`func_80028DB8`, `0x80028F4C`/`0x80028F94`), not off a rate word, so looking
///   up and down stays as fast at 120 as it is at the tick rate.
/// * **Gravity does not scale.** The fall velocity at `0x8019954E` is integrated
///   per tick and accelerated by a flat `0x28` (or `5`, or `0x64` on the landing
///   arm) in `func_80028560`. A velocity scales with the step and an acceleration
///   with its square, and neither is done here, so falls are faster and fall
///   damage -- which triggers on velocity `>= 480` -- arrives sooner.
///
/// What it *does* get right is the two words the game re-derives every frame, and
/// that covers turning, walking and strafing:
///
///     0x80199558  this frame's walk speed  (0xC8, then multiplied by the run ramp
///                 at 0x80199422 and halved by 0x801994B4)
///     0x8019955C  this frame's turn rate   (0x1C moving, 0x23 standing)
///
/// Both are written early in stage 3's own body, at `0x8002A6E4` and `0x8002A724`,
/// and modified again up to `0x8002A8CC` -- all of it *before* stage 3 dispatches
/// to `func_80028DB8` (turn) and then `func_800290D4` (walk). So the scaling has
/// to sit between, and ahead of the turn: hooking the walk would scale the turn
/// rate after the turn had already used it, and stage 3 overwrites both before the
/// next frame. One pre-hook on the turn covers both consumers, which is the same
/// seam and the same arithmetic <c>mods/kf2debug</c>'s speed multiplier uses.
///
/// The scale is applied *multiplicatively*, never by assignment: by the time this
/// runs the words already carry the run ramp, the encumbered halving and the
/// hit-stun zeroing, and overwriting them would throw all three away.
/// </summary>
public static class FullRateLogic
{
    const uint LookRoutine = 0x80028DB8;   // stage 3's turn and look
    const uint MoveSpeed   = 0x80199558;   // u32, this frame's walk speed
    const uint TurnRate    = 0x8019955C;   // u32, this frame's turn rate

    /// <summary>
    /// Hook the turn routine. Attached whatever the mode, so that
    /// <c>KF2_FPS_LOGIC</c> could in principle become a setting; <see cref="Before"/>
    /// returns without touching memory in every other mode, which is what makes
    /// that free.
    /// </summary>
    internal static int Attach(ModInfo owner)
    {
        var target = SymbolRegistry.Resolve("game", null, LookRoutine);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] pacing: no game function at 0x{LookRoutine:X8}; " +
                                    "KF2_FPS_LOGIC=full cannot scale the movement deltas.");
            return 0;
        }

        var before = typeof(FullRateLogic).GetMethod(nameof(Before), BindingFlags.Public | BindingFlags.Static)!;
        return HookManager.AddPre(owner, target, before) ? 1 : 0;
    }

    /// <summary>The factor the deltas are scaled by: a tick of the game's own clock
    /// divided by a rendered frame. 1 at the tick rate, 0.17 at 120 against a 20 Hz
    /// world. The upper bound is above 1 because the render rate is allowed to sit
    /// *below* the tick rate now -- clamping it at 1 there would silently stop
    /// scaling, which is the one failure this mode cannot report.</summary>
    public static double Scale =>
        FramePacing.LogicMode != FramePacing.Logic.Full || !FramePacing.Enabled
            ? 1.0
            : Math.Clamp(FramePacing.LogicHz / FramePacing.TargetFps, 0.01, 4.0);

    public static void Before(CpuContext c, IMemory m)
    {
        double scale = Scale;
        if (scale >= 0.999) return;

        Apply(m, MoveSpeed, scale);
        Apply(m, TurnRate, scale);
    }

    // Floored at 1 rather than 0: the game's three-branch control shape derives
    // its acceleration as `rate >> 2` and its clamp as `rate`, so a rate of zero
    // is not "slow", it is "the axis does not move at all".
    static void Apply(IMemory m, uint addr, double scale)
    {
        int value = (int)m.ReadU32(addr);
        if (value <= 0) return;

        int scaled = Math.Clamp((int)Math.Round(value * scale), 1, 0x7FFF);
        m.WriteU32(addr, (uint)scaled);
    }
}
