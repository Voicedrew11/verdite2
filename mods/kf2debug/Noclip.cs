// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;

namespace Kf2.Mods.Debug;

/// <summary>
/// Noclip flight: fly through walls, with the body coming along.
///
/// The obvious implementation is to skip the collision queries -- func_8002C330
/// and func_8002C700 both take the player's position triple with radius 0x320,
/// and a [PreHook] returning false would stop them answering. That is wrong.
/// Those two have some thirty call sites in GAME.EXE and are shared with
/// everything else in the world, so switching them off drops every enemy and
/// item through the floor along with the walls.
///
/// So the mod integrates its own position instead and writes the triple
/// directly, after the game's own movement has run. The vector is the game's
/// own, lifted from func_80028080 -- the heading helper the walk code calls:
///
///     func_8005EB08(angle)  -> rsin, 1.12 fixed point
///     func_8005EC10(angle)  -> rcos
///     dX = (-rsin(angle) * dist) >> 12
///     dZ = ( rcos(angle) * dist) >> 12
///
/// which is why forward is -sin/+cos here and not the other way round. Angles
/// are 12-bit: 0x1000 is a full turn, and stage 3 masks yaw with 0xFFF after
/// every add.
///
/// ---- where the write goes ----
///
/// A [PostHook] on stage 3 (func_8002A550) is the last word in the frame, and
/// that is worth recording because it is the one thing that could quietly break
/// this.
///
/// The emitted C# holds no literal addresses -- it builds one as
/// `c.At = 0x801A0000u; m.ReadU32(c.At - 0x6B14u)` or loads a base into a
/// register -- so the writers of the position triple have to be found by
/// resolving the base, not by grepping for an address. Taking the static call
/// closure of each of the thirteen main-loop stages and intersecting it with
/// that writer set:
///
///     stage 2  func_80037C0C   writes X/Z (area-transition placement)
///     stage 3  func_8002A550   every player-side writer there is
///     stage 4  func_80040348   none in its whole subtree -- it only READS the
///                              position, as an input to the entity update
///     stage 7  func_8001689C   the area loader
///     stage 13 func_800342D8   none in its whole subtree
///
/// Stage 2 runs before stage 3 in the same iteration, so a post-stage-3 write
/// still wins that frame. Nothing after stage 3 writes the triple at all, and a
/// pre-hook on the renderer would be worse rather than safer: func_800342D8 has
/// two dozen call sites and fires many times a frame during menus and
/// transitions.
///
/// The one real exception is area 7 (fdat23), whose scripted sequences displace
/// the position themselves and call the renderer directly to draw their own
/// frames. Stage 3 is not running during those, so noclip is simply suspended
/// for the duration. No hook site fixes that, and it is the honest behaviour.
///
/// ---- nothing is skipped, and that is deliberate ----
///
/// The first build of this mod turned off the walk (func_800290D4) and the
/// gravity/floor routine (func_80028560) while flying, on the grounds that the
/// game was computing a position we then discarded. That was a mistake. Those
/// routines are also the game's own bookkeeping -- the surface id, the floor
/// reference, the fall state -- and switching them off makes the engine less
/// consistent with itself, not more, for no gain once the mod keeps its own
/// authoritative position.
///
/// So they run, and this hook overwrites the result. Two consequences are
/// handled rather than avoided:
///
///   * The floor clamp writes Y every frame. That is why the flight integrates
///     from its OWN position rather than from whatever is in memory: reading
///     back a floor-snapped Y and adding to it would leave you hovering one
///     step above the ground instead of climbing.
///
///   * func_80028560 carries fall damage (func_80024FE0) and two instant-death
///     checks -- the bottomless pit (func_800284BC) and crushed-or-below-floor
///     (func_80023ECC), both of which kill at full HP. Flight holds the fall
///     velocity at zero so none of them ever comes due, and blocks the death
///     latch outright for the cases that do not go through it.
///
/// ---- the limit that is not a bug ----
///
/// Flying far enough leaves the area the game has loaded, and the renderer then
/// walks an entity table full of stale pointers and dies on an unmapped read.
/// That is not something a noclip can fix from outside: the neighbouring area's
/// module and data are simply not in RAM. Changing area is what the area warp is
/// for. The panel says so, and the entry position is kept so one keypress
/// undoes a flight that went too far.
/// </summary>
internal static class Noclip
{
    // The radius and height the player's own collision calls pass, reused for
    // the floor query so "snap to floor" lands where walking would have.
    const int PlayerRadius = 0x320;
    const int PlayerHeight = 0x06A4;

    internal static bool Enabled;

    // Units per frame at full deflection. The game's own walk speed is 0xC8
    // (200), so the default is a little over four times walking -- fast enough
    // to cross an area without being impossible to place.
    internal static float Speed = 900f;
    internal static float FastMultiplier = 4f;

    internal static bool InvertStrafe;

    // Which way "up" is on the height axis. Both spawn points in GAME.EXE put
    // the player at a negative Y (the resurrection warp at 0x8002AFBC uses
    // -9344, the other at 0x80025C44 uses -12800), and PSY-Q world space follows
    // screen space in having +Y point down, so "up" subtracts. That is a
    // convention, not a proof, and it is exactly the kind of sign mods/analog got
    // backwards on the pitch axis and had to settle by playing -- hence the
    // toggle rather than a hardcoded sign.
    internal static bool InvertVertical;

    // The flight's own position, in floats.
    //
    // Authoritative while flying, and it has to be: the game's floor clamp
    // rewrites Y every frame, so integrating from what is in memory would mean
    // adding a step to a snapped value and hovering rather than climbing. Floats
    // rather than ints for the same reason mods/analog carries a fraction -- at
    // 30 fps a small stick deflection truncates to no movement at all.
    static double _x, _y, _z;

    static bool _wasEnabled;
    static (int X, int Y, int Z) _entryPosition;
    internal static (int X, int Y, int Z) EntryPosition => _entryPosition;

    /// <summary>How far the flight has strayed from where it started.</summary>
    internal static long DistanceFromEntry(IMemory m)
    {
        var (x, y, z) = GameState.Position(m);
        double dx = x - _entryPosition.X, dy = y - _entryPosition.Y, dz = z - _entryPosition.Z;
        return (long)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Back to where noclip was switched on. The undo for a bad flight.</summary>
    internal static bool ReturnToEntry()
    {
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (mem == null || !GameState.IsInGame(mem)) return false;

        GameState.SetPosition(mem, _entryPosition.X, _entryPosition.Y, _entryPosition.Z);
        GameState.StopMotion(mem);
        Sync(mem);
        Console.WriteLine($"[kf2debug] returned to {Format(_entryPosition)}");
        return true;
    }

    /// <summary>Re-seed the flight's own position from the game's.</summary>
    static void Sync(IMemory m)
    {
        var (x, y, z) = GameState.Position(m);
        _x = x; _y = y; _z = z;
    }

    /// <summary>
    /// The death latch, refused while flying. Between the bottomless-pit test
    /// and the crushed-or-below-the-floor test, ordinary flight walks into a
    /// full-HP death within seconds otherwise. Cheats blocks this too when
    /// invincibility is on; both refusing is harmless, since the hook only ever
    /// says "do not run the original".
    /// </summary>
    [PreHook("game", Address = 0x8002A264)]
    static bool BeforeDeathLatch(CpuContext c, IMemory m) => !Flying(m);

    /// <summary>
    /// Flying for real: switched on, and with a character in an area to fly.
    /// The second half keeps the attract demo on the ground if the flag was left
    /// set from a previous session.
    /// </summary>
    static bool Flying(IMemory m) => Enabled && GameState.IsInGame(m);

    /// <summary>
    /// End of main-loop stage 3, which is after the game's own walk, its
    /// collision, its floor correction and its angle fold -- so whatever the
    /// game decided about the position this frame, this overwrites it.
    /// </summary>
    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        if (!Enabled)
        {
            if (_wasEnabled) Leave(m);
            return;
        }

        // Never fly the attract demo, and never fly a corpse.
        if (!GameState.IsInGame(m)) return;

        if (!_wasEnabled) Enter(m);

        Fly(c, m);
    }

    static void Enter(IMemory m)
    {
        _wasEnabled = true;
        _entryPosition = GameState.Position(m);
        Sync(m);
        Console.WriteLine($"[kf2debug] noclip on at {Format(_entryPosition)}");
    }

    static void Leave(IMemory m)
    {
        _wasEnabled = false;

        // Log where the flight ended. Landing inside geometry is the normal way
        // a noclip goes wrong, and this line plus a bookmark is how it gets
        // undone -- as is "Snap to floor", which is why the message says so.
        if (GameState.IsInGame(m))
            Console.WriteLine($"[kf2debug] noclip off at {Format(GameState.Position(m))}" +
                              " (F7 snaps to the floor if you landed inside something)");
    }

    static void Fly(CpuContext c, IMemory m)
    {
        int yaw = GameState.ReadS16(m, GameState.Yaw);

        // Left stick walks and strafes, matching mods/analog's layout so the two
        // read the same way. The D-pad is bound to the left stick by the
        // runtime's default mapping, so the keyboard drives this too.
        var (sx, sy) = Shape(Controller.LeftX, Controller.LeftY);

        float forward = -sy;                                  // stick up == forward
        float strafe  = InvertStrafe ? -sx : sx;
        float vertical = Hotkeys.FlyVertical();               // +1 up, -1 down

        if (forward != 0f || strafe != 0f || vertical != 0f)
        {
            float speed = Speed * (Hotkeys.FlyFast() ? FastMultiplier : 1f);

            // The game's own heading vector, from func_80028080.
            float fwdAngle    = GameState.AngleToRadians(yaw);
            float strafeAngle = GameState.AngleToRadians(yaw - 0x400);

            _x += (-MathF.Sin(fwdAngle) * forward + -MathF.Sin(strafeAngle) * strafe) * speed;
            _z += ( MathF.Cos(fwdAngle) * forward +  MathF.Cos(strafeAngle) * strafe) * speed;
            _y += vertical * speed * (InvertVertical ? 1f : -1f);
        }

        // Our position is the answer, whatever the walk and the floor clamp
        // decided during the stage that just ran.
        GameState.SetPosition(m, (int)_x, (int)_y, (int)_z);

        // Hold the game's own motion state down. The velocities so the frame we
        // stop flying is not the frame the game lurches; the fall velocity so
        // the gravity routine never books a landing -- that is what would
        // otherwise charge fall damage the moment flight ends.
        GameState.StopMotion(m);
        m.WriteU16(GameState.FallVel, 0);
    }

    /// <summary>
    /// Put the player on the ground at their current X/Z, using the game's own
    /// floor query.
    ///
    /// func_8002C3A8(mode, x, z, radius, height) returns the ground Y for an
    /// X/Z column: func_80025B4C calls it with the player's own position and
    /// stores the result straight into the height word, and the entity code at
    /// 0x800355F0 calls it the same way with a per-object offset added. Passing
    /// the player's own radius (0x320) and height (0x6A4) is what makes this
    /// land where walking there would have.
    ///
    /// This is the way out of a flight that ended inside a wall.
    /// </summary>
    internal static bool SnapToFloor()
    {
        var cpu = RecompOne.Runtime.Runtime.Cpu;
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (cpu == null || mem == null || !GameState.IsInGame(mem)) return false;

        var (x, _, z) = GameState.Position(mem);

        var saved = cpu.Snapshot();
        cpu.SP -= 0x20u;
        mem.WriteU32(cpu.SP + 0x10u, (uint)PlayerHeight);
        cpu.A0 = 1u;
        cpu.A1 = (uint)x;
        cpu.A2 = (uint)z;
        cpu.A3 = (uint)PlayerRadius;
        KingsField2.func_8002C3A8(cpu, mem);
        int floor = (int)cpu.V0;
        cpu.SP += 0x20u;
        cpu.Restore(saved);

        GameState.WriteS32(mem, GameState.PosY, floor);
        GameState.StopMotion(mem);
        Resync();
        Console.WriteLine($"[kf2debug] snapped to floor Y {floor}");
        return true;
    }

    /// <summary>
    /// Tell the flight to re-read the game's position. Anything that moves the
    /// player from outside this file -- a bookmark, a typed coordinate, an area
    /// warp -- has to call this, or the next flight frame would drag you
    /// straight back to where the flight thought you were.
    /// </summary>
    internal static void Resync()
    {
        var mem = RecompOne.Runtime.Runtime.Mem;
        if (mem != null) Sync(mem);
    }

    /// <summary>
    /// One stick as a radial-deadzoned, curved vector. Same shape as
    /// mods/analog/Analog.cs -- the bytes are the runtime's 0..255 with 0x80
    /// centre, and InputManager already applies a 1.3x gain, so the byte
    /// saturates a little before the stick does.
    /// </summary>
    static (float X, float Y) Shape(byte bx, byte by)
    {
        const float deadzone = 0.15f;

        float x = (bx - 128) / 127f;
        float y = (by - 128) / 127f;
        float mag = MathF.Sqrt(x * x + y * y);
        if (mag <= deadzone || mag <= 0f) return (0f, 0f);

        float unit = Math.Clamp((mag - deadzone) / (1f - deadzone), 0f, 1f);
        float scaled = unit / mag;
        return (x * scaled, y * scaled);
    }

    internal static string Format((int X, int Y, int Z) p) => $"({p.X}, {p.Y}, {p.Z})";

    /// <summary>Drop all flight state, for mod unload.</summary>
    internal static void Reset()
    {
        Enabled = false;
        _wasEnabled = false;
    }
}
