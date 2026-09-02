using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using Recompiled;

namespace Kf2;

/// <summary>
/// The area warp as shared core, rather than a feature of the debug mod.
///
/// This is a transcription of <c>mods/kf2debug/Warp.cs</c>'s RunToArea and its
/// helpers, unchanged in behaviour and in step order -- every step is there for
/// a measured reason, recorded in its comment. It lives in <c>patches/</c> for
/// the KF2_AUTOPAD reason: agent tooling (patches/AgentServer.cs, the
/// <c>warp</c> command) must work with no package enabled, because mods are off
/// by default and silent when disabled. The mod assembly can see this class --
/// ModCompiler references every loaded assembly, host included -- so the mod
/// delegates here and there is still exactly one implementation.
///
/// Single entry point: <see cref="TryRun"/>, which reports failure as a string
/// and success as null.
/// </summary>
public static class AreaWarp
{
    /// <summary>
    /// The eight areas the shipped game uses.
    ///
    /// FDAT.T runs in groups of three -- data, data, code module -- so area N is
    /// entries 3N, 3N+1, 3N+2 and the code module is entry 3N+2. The nine
    /// modules on the disc are entries 2, 5, 8, 11, 14, 17, 20, 23 and 32, which
    /// makes the areas 0..7 plus 10. Entries 24-29 are zero length, so areas 8
    /// and 9 do not exist.
    /// </summary>
    public static readonly int[] Areas = [0, 1, 2, 3, 4, 5, 6, 7];

    /// <summary>The cut eleventh area: FDAT.T entries 30, 31 and 32. Its map,
    /// objects and textures are all on the disc and load through the game's own
    /// routine; its code module is linked for a different build and is never
    /// run. <see cref="Kf2.Area10"/> is the whole of what that costs.</summary>
    public const int CutArea = 10;

    /// <summary>Every area this port can enter, which is <see cref="Areas"/> plus
    /// <see cref="CutArea"/> while <see cref="Kf2.Area10"/> is on.</summary>
    public static int[] Reachable =>
        Area10.Enabled ? [.. Areas, CutArea] : Areas;

    // ---- GAME.EXE player state ----
    //
    // The same map patches/AgentBeacon.cs and mods/kf2debug/GameState.cs carry.
    const uint MaxHp = 0x80199426;   // u16; nonzero only while an area is up
    const uint State = 0x801994E1;   // u8
    const byte StateDead = 0x11;
    const uint Area   = 0x8017E060;  // u8, the loaded save's area
    const uint PosX   = 0x801994EC;  // s32
    const uint PosY   = 0x801994F0;  // s32, height
    const uint PosZ   = 0x801994F4;  // s32
    const uint StrafeVel = 0x8019953E;  // s16
    const uint FwdVel    = 0x80199540;  // s16
    const uint WalkMag   = 0x80199542;  // s16

    // New-game spawn, written by func_80025B4C. Used only as a parking spot
    // whose X>>11 / Z>>11 land inside the 80x80 tile map; func_80025DA8 indexes
    // that map with no clamp, and a noclip flight that left it would OOB during
    // the load. The real landing is the new area's object centroid, below.
    const int SafeX = 0x00011800;
    const int SafeY = unchecked((int)0xFFFFCE00);
    const int SafeZ = 0x00018000;
    const int TileShift = 11;
    const int TileCount = 0x50;

    const uint ObjectTable  = 0x80177714;
    const int  ObjectStride = 0x44;
    const int  ObjectCount  = 0x18C;
    const uint EntityTable  = 0x8016C544;
    const int  EntityStride = 0x7C;
    const int  EntityCount  = 0xC8;

    /// <summary>
    /// Re-enter an area through the game's own routine, right now. The caller
    /// must already be on the game thread at a point where the loader may run --
    /// main-loop stage 3, the site the game itself re-enters an area from. Never
    /// from the VSync event: func_80024154 waits on the CD by looping
    /// func_80017818, which calls VSync, and nesting it swaps overlays under a
    /// live frame.
    /// </summary>
    /// <returns>null on success, else the failure reason.</returns>
    public static string? TryRun(CpuContext c, IMemory m, int area)
    {
        // buf2 is cleared until an area is up, so a zero max HP means there is
        // no character to move -- the same gate GameState.IsInGame applies.
        if (m.ReadU16(MaxHp) == 0)
            return "no area running";

        if (Array.IndexOf(Reachable, area) < 0)
            return area == CutArea
                ? $"area {CutArea} is the cut area; KF2_AREA10=0 is refusing it"
                : $"area {area} does not exist";

        var saved = c.Snapshot();

        int from = m.ReadU8(Area);

        // Park inside the tile map before the loader's own floor snap runs.
        var x = (int)m.ReadU32(PosX);
        var y = (int)m.ReadU32(PosY);
        var z = (int)m.ReadU32(PosZ);
        if (!InTileMap(x, z))
        {
            m.WriteU32(PosX, (uint)SafeX);
            m.WriteU32(PosY, unchecked((uint)SafeY));
            m.WriteU32(PosZ, (uint)SafeZ);
        }

        // The call itself is func_80024154, the same six-argument wrapper the
        // in-game menu's load path and patches/AutoReload.cs use. MIPS o32 passes
        // the fifth and sixth on the caller's stack at sp+0x10 and sp+0x14. No
        // overlay swap is driven by hand: func_8001689C, inside that wrapper,
        // reads the module off the disc and the CD read is what arms the overlay.
        c.SP -= 0x20u;
        m.WriteU32(c.SP + 0x14u, 0xFFu);
        m.WriteU32(c.SP + 0x10u, (uint)area);
        // Slot 1 is the RTMD.T entry -- the area's model data -- and RTMD.T holds
        // nine entries against RTIM.T's seventy-five, so area 10 has none and the
        // lookup is unbounded. Area10 answers with 0xFF ("keep the loaded one")
        // for it and the area index for everything else.
        c.A0 = (uint)area;
        c.A1 = Area10.RtmdSlotFor(area);
        c.A2 = (uint)area;
        c.A3 = (uint)area;
        KingsField2.func_80024154(c, m);
        c.SP += 0x20u;

        KingsField2.func_80025D38(c, m);

        // The wrapper floor-snaps at the previous area's X/Z. That is the
        // previous area, so the new module's tiles there are empty and the
        // renderer then walks object indices it does not own -- the same crash
        // as flying out of the loaded geometry. Sit on the new area's objects
        // and snap there. Same-area is already standing on valid tiles.
        if (from != area)
            PlaceInLoadedArea(c, m);

        // The dummy parking spot, or empty tiles in the new map, can trip the
        // below-floor latch inside func_80025DA8. The game's own load path
        // never has to clear it because it arrives from a live state; we might.
        if (m.ReadU8(State) == StateDead)
            KingsField2.func_80029E5C(c, m);

        // Stop motion: do not arrive still carrying the walk we left with.
        m.WriteU16(StrafeVel, 0);
        m.WriteU16(FwdVel, 0);
        m.WriteU16(WalkMag, 0);

        c.Restore(saved);

        Console.WriteLine($"[KF2] area warp: from {from} to {area}");
        return null;
    }

    static bool InTileMap(int x, int z)
    {
        uint tx = (uint)(x >> TileShift);
        uint tz = (uint)(z >> TileShift);
        return tx < TileCount && tz < TileCount;
    }

    /// <summary>
    /// Put the player on the centroid of the loaded area's object table, then
    /// run the game's own floor snap.
    ///
    /// Objects are 0x44 bytes at 0x80177714, 0x18C of them; a slot is empty
    /// when byte +4 is 0xFF -- the test stage 2 uses, and the value the loader
    /// writes when it clears the table. Position is the VECTOR at +0x14. If
    /// nothing survived the load, the entity table (buf6, 0x7C bytes, byte 0
    /// is 0xFF when disabled, VECTOR at +0x2C) is the fallback.
    /// </summary>
    static void PlaceInLoadedArea(CpuContext c, IMemory m)
    {
        if (!Centroid(m, ObjectTable, ObjectStride, ObjectCount, 0x4, 0x14,
                      out int x, out int y, out int z) &&
            !Centroid(m, EntityTable, EntityStride, EntityCount, 0x0, 0x2C,
                      out x, out y, out z))
        {
            return;
        }

        m.WriteU32(PosX, (uint)x);
        m.WriteU32(PosY, (uint)y);
        m.WriteU32(PosZ, (uint)z);
        KingsField2.func_80025DA8(c, m);
    }

    static bool Centroid(IMemory m, uint table, int stride, int count,
                         int emptyOff, int posOff,
                         out int x, out int y, out int z)
    {
        long sx = 0, sy = 0, sz = 0;
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            uint rec = table + (uint)(i * stride);
            if (m.ReadU8(rec + (uint)emptyOff) == 0xFF) continue;
            sx += (int)m.ReadU32(rec + (uint)posOff);
            sy += (int)m.ReadU32(rec + (uint)posOff + 4u);
            sz += (int)m.ReadU32(rec + (uint)posOff + 8u);
            n++;
        }
        if (n == 0)
        {
            x = y = z = 0;
            return false;
        }
        x = (int)(sx / n);
        y = (int)(sy / n);
        z = (int)(sz / n);
        return true;
    }
}
