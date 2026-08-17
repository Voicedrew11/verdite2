// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;

namespace Kf2.Mods.Debug;

/// <summary>
/// Position bookmarks and area warp.
///
/// Two very different operations. A bookmark is six words -- it puts the player
/// somewhere else in the area that is already loaded, and nothing else changes.
/// An area warp runs the game's own area-entry routine, which unloads the
/// current area module, loads another off the disc and re-enters it; that is a
/// real state transition and it can fail in ways a bookmark cannot.
/// </summary>
internal static class Warp
{
    internal const int SlotCount = 4;

    /// <summary>
    /// The valid area indices.
    ///
    /// FDAT.T runs in groups of three -- data, data, code module -- so area N is
    /// entries 3N, 3N+1, 3N+2 and the code module is entry 3N+2. The nine
    /// modules on the disc are entries 2, 5, 8, 11, 14, 17, 20, 23 and 32, which
    /// makes the areas 0..7 plus 10. Entries 24-29 are zero length, so areas 8
    /// and 9 do not exist.
    ///
    /// Area 10 is entry 32, and it is a cut area: every module this game loads is
    /// linked for 0x8019F07C and entry 32 is linked for 0x80193B38, so the loader
    /// cannot reach it and warping there hangs. See "fdat32 is a cut area" in
    /// NOTES.md. It is left out of this list deliberately.
    /// </summary>
    internal static readonly int[] Areas = [0, 1, 2, 3, 4, 5, 6, 7];

    internal const int CutArea = 10;

    internal static string Status = "";

    struct Bookmark
    {
        public bool Set;
        public int X, Y, Z, Pitch, Yaw, Roll;
    }

    static readonly Bookmark[] _slots = new Bookmark[SlotCount];

    internal static bool IsSet(int slot) =>
        slot >= 0 && slot < SlotCount && _slots[slot].Set;

    internal static (int X, int Y, int Z) SlotPosition(int slot) =>
        (_slots[slot].X, _slots[slot].Y, _slots[slot].Z);

    // ---- bookmarks ----

    internal static bool Save(int slot)
    {
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (mem == null || !GameState.IsInGame(mem))
        {
            Status = "nothing to bookmark -- no area running";
            return false;
        }
        if (slot < 0 || slot >= SlotCount) return false;

        var (x, y, z) = GameState.Position(mem);
        var (pitch, yaw, roll) = GameState.Angles(mem);

        _slots[slot] = new Bookmark
        {
            Set = true, X = x, Y = y, Z = z, Pitch = pitch, Yaw = yaw, Roll = roll,
        };

        Persist(slot);
        Status = $"bookmark {slot + 1} saved at {Noclip.Format((x, y, z))}";
        Console.WriteLine($"[kf2debug] {Status}");
        return true;
    }

    /// <summary>
    /// Put the player back. Writes the base angles and the composed triple with
    /// them, so the first frame drawn after the jump is already facing the right
    /// way, and drops the movement velocities so you do not arrive still walking.
    ///
    /// A bookmark carries no area, so restoring one taken in a different area
    /// lands you at those coordinates in the area you are actually in. That is
    /// the honest behaviour for six words of position and it is why the panel
    /// shows the coordinates next to each slot.
    /// </summary>
    internal static bool Restore(int slot)
    {
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (mem == null || !GameState.IsInGame(mem))
        {
            Status = "no area running";
            return false;
        }
        if (slot < 0 || slot >= SlotCount || !_slots[slot].Set)
        {
            Status = $"bookmark {slot + 1} is empty";
            return false;
        }

        ref Bookmark b = ref _slots[slot];
        GameState.SetPosition(mem, b.X, b.Y, b.Z);
        GameState.SetAngles(mem, b.Pitch, b.Yaw, b.Roll);
        GameState.StopMotion(mem);
        Noclip.Resync();

        Status = $"restored bookmark {slot + 1} at {Noclip.Format((b.X, b.Y, b.Z))}";
        Console.WriteLine($"[kf2debug] {Status}");
        return true;
    }

    internal static bool Teleport(int x, int y, int z)
    {
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (mem == null || !GameState.IsInGame(mem))
        {
            Status = "no area running";
            return false;
        }

        GameState.SetPosition(mem, x, y, z);
        GameState.StopMotion(mem);
        Noclip.Resync();
        Status = $"moved to {Noclip.Format((x, y, z))}";
        Console.WriteLine($"[kf2debug] {Status}");
        return true;
    }

    // ---- area warp ----

    // Queued from the panel, run from stage 3. func_80024154 waits on the CD by
    // looping func_80017818, and that calls VSync. The panel draws inside
    // Present, which is already inside VSync, so running the loader from the
    // button nested DoRender / ImGui and swapped the overlay under a frame that
    // was still drawing the area that had just unloaded. Stage 3 is the game's
    // own load path -- func_80029CBC and patches/AutoReload.cs both call it from here.
    static int _pendingArea = -1;

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
    /// Queue an area re-entry through the game's own routine.
    ///
    /// The call itself is func_80024154, the same six-argument wrapper the
    /// in-game menu's load path and patches/AutoReload.cs use. MIPS o32 passes the
    /// fifth and sixth on the caller's stack at sp+0x10 and sp+0x14. No overlay
    /// swap is driven by hand: func_8001689C, inside that wrapper, reads the
    /// module off the disc and the CD read is what arms the overlay.
    /// </summary>
    internal static bool ToArea(int area)
    {
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (mem == null || !GameState.IsInGame(mem))
        {
            Status = "no area running -- load a save first";
            return false;
        }

        if (area == CutArea)
        {
            // Belt and braces; the panel does not offer it.
            Status = $"area {CutArea} is the cut area (fdat32) and cannot load";
            Console.WriteLine($"[kf2debug] refused: {Status}");
            return false;
        }

        if (Array.IndexOf(Areas, area) < 0)
        {
            Status = $"area {area} does not exist";
            return false;
        }

        _pendingArea = area;
        Status = $"warping to area {area}...";
        Console.WriteLine($"[kf2debug] queued warp to area {area}");
        return true;
    }

    /// <summary>
    /// End of stage 3 -- the same site noclip, cheats and autoreload use, and
    /// the site the game itself re-enters an area from.
    /// </summary>
    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (_pendingArea < 0) return;
        int area = _pendingArea;
        _pendingArea = -1;
        RunToArea(c, m, area);
    }

    static void RunToArea(CpuContext c, IMemory m, int area)
    {
        if (!GameState.IsInGame(m))
        {
            Status = "no area running";
            return;
        }

        int from = m.ReadU8(GameState.Area);
        Console.WriteLine($"[kf2debug] warping from area {from} to area {area}");

        var saved = c.Snapshot();

        // Park inside the tile map before the loader's own floor snap runs.
        var (x, y, z) = GameState.Position(m);
        if (!InTileMap(x, z))
            GameState.SetPosition(m, SafeX, SafeY, SafeZ);

        c.SP -= 0x20u;
        m.WriteU32(c.SP + 0x14u, 0xFFu);
        m.WriteU32(c.SP + 0x10u, (uint)area);
        c.A0 = (uint)area;
        c.A1 = (uint)area;
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
        if (m.ReadU8(GameState.State) == GameState.StateDead)
            KingsField2.func_80029E5C(c, m);

        GameState.StopMotion(m);
        c.Restore(saved);

        Noclip.Resync();

        Status = $"warped to area {area} at {Noclip.Format(GameState.Position(m))}";
        Console.WriteLine($"[kf2debug] {Status}");
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

        GameState.SetPosition(m, x, y, z);
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

    internal static void Reset() => _pendingArea = -1;

    // ---- persistence ----
    //
    // Bookmarks are worth keeping across a restart -- the whole point is to get
    // back somewhere awkward without walking there again.

    static string Key(int slot, string field) => $"kf2.debug.bookmark{slot}.{field}";

    static void Persist(int slot)
    {
        var view = RecompOne.Runtime.Runtime.View;
        ref Bookmark b = ref _slots[slot];
        view.SetBool(Key(slot, "set"), b.Set);
        view.SetInt(Key(slot, "x"), b.X);
        view.SetInt(Key(slot, "y"), b.Y);
        view.SetInt(Key(slot, "z"), b.Z);
        view.SetInt(Key(slot, "pitch"), b.Pitch);
        view.SetInt(Key(slot, "yaw"), b.Yaw);
        view.SetInt(Key(slot, "roll"), b.Roll);
        RecompOne.Runtime.Runtime.SaveView();
    }

    internal static void LoadPersisted()
    {
        var view = RecompOne.Runtime.Runtime.View;
        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i] = new Bookmark
            {
                Set   = view.GetBool(Key(i, "set"), false),
                X     = view.GetInt(Key(i, "x"), 0),
                Y     = view.GetInt(Key(i, "y"), 0),
                Z     = view.GetInt(Key(i, "z"), 0),
                Pitch = view.GetInt(Key(i, "pitch"), 0),
                Yaw   = view.GetInt(Key(i, "yaw"), 0),
                Roll  = view.GetInt(Key(i, "roll"), 0),
            };
        }
    }

    internal static void Clear(int slot)
    {
        if (slot < 0 || slot >= SlotCount) return;
        _slots[slot] = default;
        Persist(slot);
        Status = $"bookmark {slot + 1} cleared";
    }
}
