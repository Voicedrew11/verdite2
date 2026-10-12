using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// Keep the wall slide <c>func_80028080</c> out of the one state it never leaves.
///
///     KF2_WALLGUARD=0     the recompiled routine alone: the hang back -- comparison only
///
/// The routine moves the player by a speed along an angle and, when
/// <c>func_8002C700</c> reports a collision, retries: a shorter step, the X alone,
/// the Z alone, a diagonal, and at last no move at all. **The no-move retry tests
/// where the player already stands**, and when that spot answers map collision
/// bit <c>0x2</c> (and no creature or object bit, <c>0x30</c>) the routine's
/// branches cycle between <c>0x800283E4</c> and <c>0x80028408</c> with both
/// deltas zero, forever, without a call: the game thread stops and nothing inside
/// the loop can be hooked to stop it. Stage 3 calls the routine every tick with the
/// forward speed even when it is 0, so standing still is enough once the player
/// is inside a wall. See "The wall slide never leaves a wall it starts in" in
/// docs/GAME_INTERNALS.md.
///
/// So before each call the player's own spot is tested against the map, with the
/// routine's own radius and height, and the query's scratch words are put back.
/// Free (every call in normal play), the recompiled routine runs untouched. In collision, the player is nudged to the nearest free spot within a
/// tile, on rings of 64 units, and the routine then runs from there; with none,
/// the move is refused for this call and the game goes on.
/// </summary>
public static class WallGuard
{
    const uint Slide = 0x80028080;
    const uint Collide = 0x8002C700;

    const uint PosX = 0x801994EC, PosY = 0x801994F0, PosZ = 0x801994F4;
    const uint Surface = 0x8019953C;     // u16, latched from the floor query on a committed move
    const uint FloorHalf = 0x801D9C8E;   // u16, the half the last query resolved
    const uint DeltaX = 0x801994FC, DeltaZ = 0x80199500;   // s16, the move the routine applied
    const uint AreaByte = 0x8017E060;

    // func_80028080's own arguments to func_8002C700. The test of the player's
    // spot asks for the map alone (bit 0x1): the creature and object checks are
    // not the loop's, and the map path writes nothing outside the query's scratch
    // words, which are put back so the routine runs on what it would have.
    const uint Radius = 0x320, Height = 0x6A4, Mask = 0x31, MapOnly = 0x01;
    const uint Scratch = 0x801D9C80;   // to 0x801D9CB0: the floor query's tile, half, floor Y, and the hit words
    const int ScratchWords = 12;

    const int Ring = 64, Reach = 2048, Directions = 16;

    static bool _on = true, _queued;
    static long _stuck, _nudged, _refused;
    static double _lastLog = double.NegativeInfinity;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.wallguard",
        Name = "Wall guard",
        Version = "1.0",
        Description = "Keeps func_80028080's wall slide from looping forever when the player stands inside map collision.",
    };

    public static void Configure(string? mode) =>
        _on = !(mode?.Trim() is "0" or "off");

    public static void Install()
    {
        if (!_on)
        {
            Console.WriteLine("[KF2] wall guard: off (KF2_WALLGUARD=0); the slide can hang inside a wall");
            return;
        }
        HookAttach.OnOverlayLoad("wall guard", Attach);
    }

    static bool Attach()
    {
        var target = SymbolRegistry.Resolve("game", null, Slide);
        if (target == null) return false;

        if (!_queued)
        {
            var impl = typeof(WallGuard).GetMethod(nameof(Replace),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            _queued = HookManager.AddReplace(_self, target, impl);
            if (!_queued) return false;
        }

        HookManager.Commit();
        bool ok = HookAttach.Installed(target);
        Console.WriteLine(ok
            ? $"[KF2] wall guard: on, the slide at 0x{Slide:X8}"
            : "[KF2] wall guard: not installed; the slide can hang inside a wall.");
        return ok;
    }

    static void Replace(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var entry = c.Snapshot();
        int x = (int)m.ReadU32(PosX), y = (int)m.ReadU32(PosY), z = (int)m.ReadU32(PosZ);

        Span<uint> saved = stackalloc uint[ScratchWords];
        for (int i = 0; i < ScratchWords; i++) saved[i] = m.ReadU32(Scratch + (uint)i * 4);
        bool stuck = Hangs(Test(c, m, x, y, z, MapOnly));
        if (!stuck)
        {
            for (int i = 0; i < ScratchWords; i++) m.WriteU32(Scratch + (uint)i * 4, saved[i]);
            c.Restore(entry);
            orig(c, m);
            return;
        }

        _stuck++;
        if (Unstick(c, m, x, y, z, out int nx, out int nz))
        {
            _nudged++;
            Log(m, $"stuck in a wall at ({x}, {y}, {z}); nudged {nx - x}, {nz - z} to a free spot");
            c.Restore(entry);
            orig(c, m);
            return;
        }

        // Nothing free within reach: no move this call, which is what the routine
        // reports when every retry collided.
        _refused++;
        Log(m, $"stuck in a wall at ({x}, {y}, {z}); nothing free within {Reach}, the move refused");
        c.Restore(entry);
        m.WriteU16(DeltaX, 0);
        m.WriteU16(DeltaZ, 0);
        c.V0 = 0;
    }

    /// <summary>What makes the no-move retry cycle: map bit 0x2. (With the
    /// creature and object checks too, a 0x30 bit would take the counted branch
    /// out instead; the map-only test treats that rarer case as stuck as well.)</summary>
    static bool Hangs(uint flags) => (flags & 0x2) != 0 && (flags & 0x30) == 0;

    /// <summary>The nearest spot on rings of <see cref="Ring"/> units that collides
    /// with nothing at all, committed as the routine commits a move.</summary>
    static bool Unstick(CpuContext c, IMemory m, int x, int y, int z, out int nx, out int nz)
    {
        for (int r = Ring; r <= Reach; r += Ring)
            for (int d = 0; d < Directions; d++)
            {
                double a = d * (2 * Math.PI / Directions);
                nx = x + (int)Math.Round(-Math.Sin(a) * r);
                nz = z + (int)Math.Round(Math.Cos(a) * r);
                if (Test(c, m, nx, y, nz, Mask) != 0) continue;

                m.WriteU32(PosX, (uint)nx);
                m.WriteU32(PosZ, (uint)nz);
                m.WriteU16(Surface, m.ReadU16(FloorHalf));
                return true;
            }

        nx = x;
        nz = z;
        return false;
    }

    /// <summary><c>func_8002C700(x, y, z, 0x320, 0x6A4, mask)</c>, as the slide
    /// calls it. Clobbers the caller-saved registers; the caller restores.</summary>
    static uint Test(CpuContext c, IMemory m, int x, int y, int z, uint mask)
    {
        uint sp = c.SP;
        c.SP = sp - 0x20;
        m.WriteU32(c.SP + 0x10, Height);
        m.WriteU32(c.SP + 0x14, mask);
        c.A0 = (uint)x;
        c.A1 = (uint)y;
        c.A2 = (uint)z;
        c.A3 = Radius;
        c.RA = Slide;
        KingsField2.func_8002C700(c, m);
        c.SP = sp;
        return c.V0;
    }

    static void Log(IMemory m, string what)
    {
        double now = Environment.TickCount64;
        if (now - _lastLog < 1000) return;
        _lastLog = now;
        Console.WriteLine($"[KF2] wall guard: area {m.ReadU8(AreaByte)}, {what} " +
                          $"({_stuck} stuck, {_nudged} nudged, {_refused} refused)");
    }
}
