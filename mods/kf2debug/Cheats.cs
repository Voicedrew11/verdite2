// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.Debug;

/// <summary>
/// Invincibility, infinite MP and the speed multiplier.
///
/// ---- invincibility: four hooks, because HP is not the whole story ----
///
/// NOTES.md names func_80024F90 as the add-HP routine. It is one of three, it is
/// not the combat path, and the thing that actually kills you can do it at full
/// HP -- so a mod that hooks only it dies in four different ways. Reading the
/// emitted C# gives the whole picture:
///
///   func_80024F90(delta)   hp += delta; if <= 0, clamp and latch death. ONE
///                          call site, inside stage 3, always delta = -1 -- the
///                          poison/starvation tick, not combat.
///
///   func_8002A3DC(delta)   the same shape but clamped to max HP as well. Called
///                          from the per-tick equipment regen/drain with +1 and
///                          with -1, so its -1 arm can take you from 1 HP to
///                          dead without touching either other routine.
///
///   func_80024FE0(a0,dmg)  the real take-damage routine: every weapon, trap and
///                          fall. Returns early if already dead, computes
///                          hp - dmg clamped at zero, and writes HP itself.
///
///   func_8002A264(vec)     the death LATCH, and the only writer of state 0x11
///                          in the game. Seven callers -- the three above, plus
///                          func_80023ECC twice (crushed against the ceiling, or
///                          below the floor) and the two bottomless-pit checks
///                          in func_800284BC and func_80028B0C.
///
/// Those last four kill you *at full HP*, so no amount of watching the HP word
/// sees them coming. That is why the latch is hooked too, and why it is the
/// backstop rather than the only hook: blocking the latch alone would leave the
/// HP bar visibly draining to zero and staying there.
///
/// So: clamp the argument on the three HP routines, so the bar never moves, and
/// refuse the latch, so nothing can mark you dead by any route. Neutralising the
/// *argument* rather than skipping the call matters for func_80024FE0 -- its own
/// `if (dmg == 0) goto <tail>` branch skips the subtraction, the clamp and the
/// store while still running the hit reaction, so a blocked hit still reads as a
/// hit.
///
/// There is deliberately no PSMemory.Freeze on the HP word. It would hold the
/// value, but eleven separate routines heal the player -- items, spells, rest,
/// the level-up refill, three area scripts -- and a frozen word drops all of
/// them silently, which would look like a bug in the game.
///
/// ---- enemies ignoring you is one register ----
///
/// A creature's behaviour is picked by a rule table, and the only thing the
/// picker is told about the player is **how far away they are**. Stage 4 runs
/// one creature in four per frame through `func_8003A3FC`, which is five
/// instructions of arithmetic and two calls:
///
///     dx = rec[+0x2C] - playerX          (the entity's copied position)
///     dz = rec[+0x34] - playerZ
///     a0 = func_800154E4(dx, dz)         the horizontal distance
///          func_8003A300(a0)             pick a behaviour for that distance
///
/// `func_8003A300` walks the sixteen rule pointers at `desc+0x38`, scores each
/// one against that distance with `func_80039E40`, and installs the winner. The
/// scorer's range fields are `u16`s at `rule+0x10` and `rule+0x12`, compared as
/// `(int)range < (int)dist`, so **any distance above 65535 fails every ranged
/// rule** and what is left is whatever the creature does when the player is
/// across the map. That is not an invented idle state: it is the game's own
/// far-away behaviour, chosen by the game's own scorer.
///
/// So the switch is a pre-hook on `func_8003A300` that overwrites `a0`. Nothing
/// else is touched -- creatures still activate, animate, draw, collide and take
/// damage, because none of that goes through the picker.
///
/// **What it deliberately does not do.** `func_8003A574` (the activation state
/// machine at `rec+0x9`) is left alone, and it must be: the renderer's first
/// loop draws a creature only when that byte is 1, so forcing it back to 0 --
/// which is what feeding the AI a decoy player position would have done, since
/// the acquire test `func_80015620` is on the same path -- would make enemies
/// *vanish* rather than ignore you. Whether a creature already mid-swing still
/// lands the hit, and whether one picks a far-away behaviour that happens to
/// face you anyway, are matters for the eye and have not been judged.
/// </summary>
internal static class Cheats
{
    internal static bool Invincible;
    internal static bool InfiniteMp;
    internal static bool SpeedEnabled;
    internal static bool Peaceful;

    // 1.0 is the game's own speed. The scale clamps to at least 1 unit, because
    // patches/Analog.cs treats a rate word of zero or less as "not controllable" and
    // returns -- a multiplier that rounded a rate to zero would silently switch
    // analog control off.
    internal static float SpeedMultiplier = 2f;

    internal static long BlockedHits;
    internal static long BlockedDeaths;
    internal static long RestoredHp;
    internal static long IgnoredPicks;

    // ---- the three HP routines ----

    /// <summary>
    /// The take-damage routine. a1 is the damage; zeroing it takes the
    /// function's own early branch past every write it makes to HP, while
    /// leaving the hit reaction, sound and knockback to run.
    /// </summary>
    [PreHook("game", Address = 0x80024FE0)]
    static void BeforeTakeDamage(CpuContext c, IMemory m)
    {
        if (!Invincible || c.A1 == 0u) return;
        if (!GameState.IsInGame(m)) return;

        c.A1 = 0u;
        BlockedHits++;
    }

    /// <summary>
    /// The poison/starvation tick. Only a negative delta is a loss; a positive
    /// one is a heal and must pass through untouched.
    /// </summary>
    [PreHook("game", Address = 0x80024F90)]
    static void BeforeAddHp(CpuContext c, IMemory m) => ClampDelta(c, m);

    /// <summary>
    /// The equipment regen/drain. Same shape as func_80024F90 and hooked the
    /// same way -- its -1 arm is a death route that reaches neither of the other
    /// two routines.
    /// </summary>
    [PreHook("game", Address = 0x8002A3DC)]
    static void BeforeAdjustHp(CpuContext c, IMemory m) => ClampDelta(c, m);

    static void ClampDelta(CpuContext c, IMemory m)
    {
        if (!Invincible) return;
        if (!GameState.IsInGame(m)) return;

        if ((int)c.A0 < 0)
        {
            c.A0 = 0u;
            BlockedHits++;
        }
    }

    // ---- enemies ignoring you ----

    // The distance handed to the behaviour picker while this is on. The scorer
    // compares against u16 range fields, so anything past 65535 fails every
    // ranged rule; this is twice that, and still a distance the map itself could
    // produce (80 tiles of 2048 is 163840 across), so nothing sees a number the
    // game could not have given it.
    const uint FarAway = 0x20000;

    /// <summary>
    /// The behaviour picker, told the player is across the map.
    ///
    /// `a0` is the horizontal distance to the player and is the picker's only
    /// input about them, so overwriting it is the whole cheat. The original
    /// still runs: the creature picks, and keeps picking, whatever it does when
    /// nobody is near.
    /// </summary>
    [PreHook("game", Address = 0x8003A300)]
    static void BeforeBehaviourPick(CpuContext c, IMemory m)
    {
        if (!Peaceful) return;
        if (!GameState.IsInGame(m)) return;

        c.A0 = FarAway;
        IgnoredPicks++;
    }

    // ---- the death latch ----

    /// <summary>
    /// The only writer of the dead state, 0x11, anywhere in the game. Skipping
    /// it is what makes "invincible" mean it: the crush check, the two
    /// bottomless-pit checks and the below-the-floor check all call this at full
    /// HP, and nothing that watches the HP word can see them coming.
    ///
    /// Returning false skips the original entirely, which is right here -- there
    /// is nothing in the routine but the latch, the death sound and two timer
    /// resets, and none of it should happen.
    /// </summary>
    [PreHook("game", Address = 0x8002A264)]
    static bool BeforeDeathLatch(CpuContext c, IMemory m)
    {
        if (!Invincible) return true;
        if (!GameState.IsInGame(m)) return true;

        BlockedDeaths++;
        return false;
    }

    // ---- per-frame ----

    /// <summary>
    /// End of stage 3. The catch-all for HP, and the whole of infinite MP.
    ///
    /// The catch-all earns its place because HP has a dozen writers and only
    /// three of them are the routines hooked above. Restoring here is what keeps
    /// "invincible" true against a writer nobody has classified yet.
    ///
    /// It deliberately does nothing once the state byte says dead. With the
    /// latch hooked that should be unreachable while invincible, but if the flag
    /// is switched on mid-death the dead-state handler forces HP to zero every
    /// frame, and fighting it would leave the player alive-but-dead rather than
    /// either.
    /// </summary>
    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (!GameState.IsInGame(m)) return;
        if (GameState.IsDead(m)) return;

        if (Invincible)
        {
            ushort hp = m.ReadU16(GameState.Hp);
            ushort maxHp = m.ReadU16(GameState.MaxHp);
            if (hp < maxHp)
            {
                m.WriteU16(GameState.Hp, maxHp);
                RestoredHp++;
            }
        }

        if (InfiniteMp)
        {
            ushort mp = m.ReadU16(GameState.Mp);
            ushort maxMp = m.ReadU16(GameState.MaxMp);
            if (mp < maxMp) m.WriteU16(GameState.Mp, maxMp);
        }
    }

    // ---- speed ----

    /// <summary>
    /// Scale this frame's walk speed and turn rate.
    ///
    /// Both words are re-derived by stage 3 in its own body -- walk speed 0xC8,
    /// turn rate 0x1C moving or 0x23 standing -- *before* it dispatches to
    /// func_80028DB8 (turn/look) and then func_800290D4 (walk/strafe). So the
    /// scaling has to sit between the write and the reads, and it has to be
    /// ahead of the turn: hooking the walk instead would scale the turn rate
    /// after the turn had already used it, and stage 3 would overwrite it before
    /// the next one. Nothing writes either word between here and the walk, so
    /// one hook on the turn covers both consumers.
    ///
    /// This composes with patches/Analog.cs, which reads the same two words to size
    /// its velocities and so inherits the multiplier -- subject to which mod's
    /// pre-hook on this address runs first, which is registration order.
    /// </summary>
    [PreHook("game", Address = 0x80028DB8)]
    static void BeforeLook(CpuContext c, IMemory m)
    {
        if (!SpeedEnabled || Noclip.Enabled) return;
        if (!GameState.IsInGame(m)) return;

        Scale(m, GameState.MoveSpeed);
        Scale(m, GameState.TurnRate);
    }

    static void Scale(IMemory m, uint addr)
    {
        int value = (int)m.ReadU32(addr);
        if (value <= 0) return;

        int scaled = Math.Clamp((int)MathF.Round(value * SpeedMultiplier), 1, 0x7FFF);
        m.WriteU32(addr, (uint)scaled);
    }

    internal static void Reset()
    {
        Invincible = false;
        InfiniteMp = false;
        SpeedEnabled = false;
        Peaceful = false;
    }
}
