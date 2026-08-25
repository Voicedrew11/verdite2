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

        if (area == Kf2.AreaWarp.CutArea)
        {
            // Belt and braces; the panel does not offer it.
            Status = $"area {Kf2.AreaWarp.CutArea} is the cut area (fdat32) and cannot load";
            Console.WriteLine($"[kf2debug] refused: {Status}");
            return false;
        }

        if (Array.IndexOf(Kf2.AreaWarp.Areas, area) < 0)
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
        int from = m.ReadU8(GameState.Area);
        Console.WriteLine($"[kf2debug] warping from area {from} to area {area}");

        string? err = Kf2.AreaWarp.TryRun(c, m, area);
        if (err != null) { Status = err; return; }

        Noclip.Resync();
        Status = $"warped to area {area} at {Noclip.Format(GameState.Position(m))}";
        Console.WriteLine($"[kf2debug] {Status}");
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
