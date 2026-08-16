// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using RecompOne.Runtime.Memory;
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

    /// <summary>
    /// Re-enter an area through the game's own routine.
    ///
    /// This is the call the in-game menu's load path makes and the one
    /// mods/autoreload transcribes: func_80024154 takes six arguments, and MIPS
    /// o32 passes the fifth and sixth on the caller's stack at sp+0x10 and
    /// sp+0x14 -- which is what the stack window here is for. func_80025D38
    /// afterwards is the post-load arm the game itself runs.
    ///
    /// No overlay swap is driven by hand: the area module comes off the disc
    /// because func_8001689C, inside func_80024154, reads it, and the CD read is
    /// what arms the overlay.
    /// </summary>
    internal static bool ToArea(int area)
    {
        var cpu = RecompOne.Runtime.Runtime.Cpu;
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (cpu == null || mem == null || !GameState.IsInGame(mem))
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

        Console.WriteLine($"[kf2debug] warping to area {area}");

        var saved = cpu.Snapshot();
        cpu.SP -= 0x20u;
        mem.WriteU32(cpu.SP + 0x14u, 0xFFu);
        mem.WriteU32(cpu.SP + 0x10u, (uint)area);
        cpu.A0 = (uint)area;
        cpu.A1 = (uint)area;
        cpu.A2 = (uint)area;
        cpu.A3 = (uint)area;
        KingsField2.func_80024154(cpu, mem);
        cpu.SP += 0x20u;

        KingsField2.func_80025D38(cpu, mem);
        cpu.Restore(saved);

        // The area's own entry code has placed the player; the flight has to
        // start from there rather than from the area we just left.
        Noclip.Resync();

        Status = $"warped to area {area} at {Noclip.Format(GameState.Position(mem))}";
        Console.WriteLine($"[kf2debug] {Status}");
        return true;
    }

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
