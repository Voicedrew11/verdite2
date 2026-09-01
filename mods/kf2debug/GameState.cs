// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using RecompOne.Runtime.Memory;

namespace Kf2.Mods.Debug;

/// <summary>
/// GAME.EXE's player state, by address, with signed accessors.
///
/// Every address here is already in NOTES.md -- "Player state: found, and it was
/// in stage 3 all along" for the position, angles and velocities, and "The
/// character's stats are buf2" for HP, MP, EXP and level. Nothing in this file is
/// a new identification; it is the same map `patches/Analog.cs` and `patches/AutoReload.cs`
/// each carry a copy of, written down once.
///
/// `IMemory` has no signed accessors -- it is ReadU16/ReadU32 and the caller
/// casts -- and three of the values here are meaningfully negative (the height Y,
/// and every velocity), so the casts live here rather than at each use.
/// </summary>
internal static class GameState
{
    // ---- position and view (the block stage 3 maintains) ----
    //
    // The position triple is the one the collision queries func_8002C330 and
    // func_8002C700 take with radius 0x320. Y is height, and a legitimate Y is
    // routinely negative -- the game's own warp at 0x8002AFBC writes 0xFFFFDB80,
    // which is -9344 -- so all three are read signed.
    internal const uint PosX      = 0x801994EC;   // s32
    internal const uint PosY      = 0x801994F0;   // s32, height
    internal const uint PosZ      = 0x801994F4;   // s32

    // Base view angles. The renderer does not read these directly: it reads the
    // composed triple below, which stage 3 rebuilds every frame as
    // base + deltaA + deltaB + deltaC. Writing the base is what sticks.
    internal const uint Pitch     = 0x8019950C;   // s16, increases looking DOWN
    internal const uint Yaw       = 0x8019950E;   // s16, 12-bit, increases turning LEFT
    internal const uint Roll      = 0x80199510;   // s16

    internal const uint ViewPitch = 0x80199504;   // s16, composed = base + A + B + C
    internal const uint ViewYaw   = 0x80199506;   // s16
    internal const uint ViewRoll  = 0x80199508;   // s16

    // Per-frame control velocities, all applied by the game's own code.
    internal const uint StrafeVel = 0x8019953E;   // s16
    internal const uint FwdVel    = 0x80199540;   // s16
    internal const uint WalkMag   = 0x80199542;   // s16, sqrt(vx^2 + vz^2)
    internal const uint TurnVel   = 0x80199544;   // s16
    internal const uint PitchVel  = 0x80199546;   // s16

    // This frame's rates, re-derived by stage 3 each frame. The walk speed is
    // 0xC8; the turn rate is 0x1C while a movement button is down and 0x23
    // standing still, which is the game's own rule and not a bug.
    internal const uint MoveSpeed = 0x80199558;   // u32
    internal const uint TurnRate  = 0x8019955C;   // u32

    // Fall velocity, integrated by the gravity routine func_80028560, and the
    // surface id func_80028080 latches when a move commits. Flight holds the
    // fall velocity at zero so the game never books a landing it should not.
    internal const uint FallVel   = 0x8019954E;   // s16
    internal const uint SurfaceId = 0x8019953C;   // u16

    // The pad word PadRead(1) stored this frame. Active HIGH, and its two button
    // bytes are in the opposite order to the runtime's Controller layout -- see
    // patches/AnalogProbe.cs. Read here only for the readout.
    internal const uint Pad       = 0x80199554;   // u16

    // ---- stats: buf2, the 0x58-byte per-area buffer at 0x80199414 ----
    //
    // The whole block is mapped in "The status screen names the rest of buf2" in
    // docs/GAME_INTERNALS.md. What matters here is the split: a handful of these
    // words are the character, and nineteen of them are a *cache* that
    // func_800244CC rebuilds from the equipment. Attributes.cs is where that
    // distinction is acted on.
    internal const uint Exp       = 0x80199414;   // u32, capped at 999999 by func_80024CAC
    internal const uint ExpNext   = 0x80199418;   // u32, EXP the next level needs
    internal const uint Level     = 0x8019941C;   // u8
    internal const uint MaxHp     = 0x80199426;   // u16
    internal const uint Hp        = 0x80199428;   // u16
    internal const uint MaxMp     = 0x8019942A;   // u16
    internal const uint Mp        = 0x8019942C;   // u16

    // The two real attributes: what a level-up raises and what the save carries.
    // func_800244CC opens by copying these into the two POWER words below.
    internal const uint BaseStr   = 0x80199438;   // u16
    internal const uint BaseMag   = 0x8019943A;   // u16

    // "STR POWER" and "MAG POWER" on the status screen: base plus equipment,
    // minus 20 while cursed. Rebuilt by func_800244CC, so a write here lasts
    // only until the next equipment change.
    internal const uint StrPower  = 0x8019943C;   // u16
    internal const uint MagPower  = 0x8019943E;   // u16

    internal const uint Gold      = 0x80199440;   // u32

    // The status screen's second page. func_800244CC zeroes all seventeen and
    // then adds each equipped item's contribution, in this order.
    internal const uint OffSlash  = 0x80199444;   // u16
    internal const uint OffChop   = 0x80199446;
    internal const uint OffStab   = 0x80199448;
    internal const uint OffHoly   = 0x8019944A;
    internal const uint OffFire   = 0x8019944C;
    internal const uint OffEarth  = 0x8019944E;
    internal const uint OffWind   = 0x80199450;
    internal const uint OffWater  = 0x80199452;

    internal const uint DefSlash  = 0x80199456;   // u16
    internal const uint DefChop   = 0x80199458;
    internal const uint DefStab   = 0x8019945A;
    internal const uint DefPoison = 0x8019945C;
    internal const uint DefDark   = 0x8019945E;
    internal const uint DefFire   = 0x80199460;
    internal const uint DefEarth  = 0x80199462;
    internal const uint DefWind   = 0x80199464;
    internal const uint DefWater  = 0x80199466;

    // CONDITION on the status screen, which reads GOOD when all five are zero.
    // Signed counters -- the screen loads every one of them with lh.
    internal const uint CondPoison   = 0x80199468;   // s16
    internal const uint CondCurse    = 0x8019946A;   // s16
    internal const uint CondDark     = 0x8019946E;   // s16
    internal const uint CondSlow     = 0x80199472;   // s16
    internal const uint CondParalyze = 0x80199474;   // s16

    // The player's action state, dispatched through a jump table at
    // 0x80011300 + state*4 in stage 3. 0x11 is dead.
    internal const uint State     = 0x801994E1;   // u8
    internal const byte StateDead = 0x11;

    internal const uint DeathFrames = 0x8019951A; // u16, the game's post-death clock

    // The area the loaded save belongs to, and the slot last saved to or loaded
    // from. Both in GAME.EXE, both outside buf2.
    internal const uint Area        = 0x8017E060; // u8
    internal const uint CurrentSlot = 0x8006E5D4; // u8

    // The angle unit: a full turn is 0x1000, not 0x10000. Everything angular in
    // this game is 12-bit -- stage 3 masks yaw with 0xFFF after every add.
    internal const int AngleMask = 0xFFF;
    internal const int AngleFull = 0x1000;

    // The pitch limit the game holds itself to. Not enforced on our writes, but
    // worth having: a pitch outside it is a view the game never produces.
    internal const int PitchLimit = 0x2BC;

    // ---- typed reads ----

    internal static int   ReadS32(IMemory m, uint a) => (int)m.ReadU32(a);
    internal static short ReadS16(IMemory m, uint a) => (short)m.ReadU16(a);

    internal static void WriteS32(IMemory m, uint a, int v) => m.WriteU32(a, (uint)v);
    internal static void WriteS16(IMemory m, uint a, int v) => m.WriteU16(a, (ushort)(short)v);

    internal static (int X, int Y, int Z) Position(IMemory m) =>
        (ReadS32(m, PosX), ReadS32(m, PosY), ReadS32(m, PosZ));

    internal static void SetPosition(IMemory m, int x, int y, int z)
    {
        WriteS32(m, PosX, x);
        WriteS32(m, PosY, y);
        WriteS32(m, PosZ, z);
    }

    /// <summary>The base angles, which is what a warp should write.</summary>
    internal static (int Pitch, int Yaw, int Roll) Angles(IMemory m) =>
        (ReadS16(m, Pitch), ReadS16(m, Yaw), ReadS16(m, Roll));

    /// <summary>
    /// Set the base angles, and the composed triple with them. Stage 3 rebuilds
    /// the composed view from the base every frame, so writing the base alone is
    /// enough to stick -- but writing both means the very next frame rendered is
    /// already correct, instead of showing one frame at the old angle.
    /// </summary>
    internal static void SetAngles(IMemory m, int pitch, int yaw, int roll)
    {
        yaw &= AngleMask;
        WriteS16(m, Pitch, pitch);
        WriteS16(m, Yaw, yaw);
        WriteS16(m, Roll, roll);
        WriteS16(m, ViewPitch, pitch);
        WriteS16(m, ViewYaw, yaw);
        WriteS16(m, ViewRoll, roll);
    }

    /// <summary>
    /// Drop every movement velocity. Used after a teleport so you do not arrive
    /// still carrying the walk you left with.
    /// </summary>
    internal static void StopMotion(IMemory m)
    {
        m.WriteU16(StrafeVel, 0);
        m.WriteU16(FwdVel, 0);
        m.WriteU16(WalkMag, 0);
    }

    /// <summary>
    /// Is there a character in an area right now?
    ///
    /// Max HP is the test rather than current HP, and rather than the state byte:
    /// buf2 is cleared until an area is up, so a zero max HP means there is no
    /// character at all, while current HP is legitimately zero on the frame you
    /// die. Without this every feature here fires during the attract demo, which
    /// runs stage 3 like anything else -- the bug patches/AutoReload.cs hit on its
    /// first run.
    /// </summary>
    internal static bool IsInGame(IMemory m) => m.ReadU16(MaxHp) != 0;

    internal static bool IsDead(IMemory m) => m.ReadU8(State) == StateDead;

    /// <summary>A 12-bit game angle in radians, for the flight vector.</summary>
    internal static float AngleToRadians(int angle) =>
        angle * (2f * MathF.PI / AngleFull);
}
