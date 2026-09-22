// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Recompiled;
// Upstream 0409bc2 emits one class per overlay, because CoreCLR caps a class
// at 65535 methods. Every func_ named here is GAME.EXE's, so the alias names the
// overlay once and the call sites below are unchanged.
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Mods.Debug;

/// <summary>
/// Noclip flight: fly through walls, with the body coming along.
///
/// Forward is where the *camera* points, pitch included, so looking down and
/// pushing forward descends; the speed is units a second against real elapsed
/// time, because the hook below runs on the world tick when the frame gate is
/// paced and at the render rate when it is not. Input comes from two places at
/// once -- the left stick, and the pad word's own direction bits, which the
/// keyboard fills through the player's own bindings -- so a keyboard, a pad and
/// a rebind all reach it without a second table.
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

    // Units per *second* at full deflection, spent against real elapsed time
    // rather than per call. The hook is stage 3, which runs on the world's 20 Hz
    // tick when the frame gate is paced and at the render rate when it is not,
    // so a per-call step is a different speed on every machine and every
    // setting; a rate is the same flight everywhere.
    internal static float Speed = 7000f;
    internal static float FastMultiplier = 4f;

    // Wall clock between two flight frames. Clamped, because the gap across an
    // area load or a paused panel is seconds long and would fire the flight
    // across the map in one step.
    const double MaxStep = 0.1;
    static long _lastTicks;

    internal static bool InvertStrafe;

    // ---- the cinematic camera ----
    //
    // Flight and look both go through a first-order lag instead of landing on
    // the input: the flight carries a velocity that eases toward what the stick
    // is asking for, and the view is a smoothed copy of the angle the game just
    // integrated. Both time constants are seconds to about 63% of the target,
    // which is the same shape patches/FrameSmoothing.cs uses, and both are spent
    // against real elapsed time so the feel does not change with the frame rate.
    static bool _cinematic;
    internal static bool Cinematic
    {
        get => _cinematic;
        // The look filter tracks the angle the game wrote last frame, so
        // switching it on mid-flight has to re-seed from where the view is now
        // or the first frame turns the whole way from a stale angle.
        set
        {
            if (value == _cinematic) return;
            _cinematic = value;
            _lookPrimed = false;
            // A flythrough being filmed must not have anything fading in over
            // the picture: the hotkey toasts go quiet in Hotkeys.Notify, and
            // the pointer-capture glyph here. (Precedent for a mod driving a
            // host type: Warp calls Kf2.AreaWarp.)
            Kf2.MouseIndicator.Suppressed = value;
        }
    }

    internal static float MoveSmoothing = 0.35f;
    internal static float LookSmoothing = 0.25f;

    // The flight's velocity, units a second, eased toward the input.
    static double _vx, _vy, _vz;

    // The look filter. The target accumulates the deltas the game's own turn
    // code applied -- reading the angle back would read what we wrote, so the
    // input has to be recovered as a difference -- and the smoothed value is
    // what gets written to both the base and the composed triple.
    static bool _lookPrimed;
    static double _targetYaw, _targetPitch, _smoothYaw, _smoothPitch;
    static int _prevYaw, _prevPitch;

    // Which way "up" is on the height axis. Both spawn points in GAME.EXE put
    // the player at a negative Y (the resurrection warp at 0x8002AFBC uses
    // -9344, the other at 0x80025C44 uses -12800), and PSY-Q world space follows
    // screen space in having +Y point down, so "up" subtracts. That is a
    // convention, not a proof, and it is exactly the kind of sign patches/Analog.cs got
    // backwards on the pitch axis and had to settle by playing -- hence the
    // toggle rather than a hardcoded sign.
    internal static bool InvertVertical;

    // The flight's own position, in floats.
    //
    // Authoritative while flying, and it has to be: the game's floor clamp
    // rewrites Y every frame, so integrating from what is in memory would mean
    // adding a step to a snapped value and hovering rather than climbing. Floats
    // rather than ints for the same reason patches/Analog.cs carries a fraction -- at
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

        // A teleport must not arrive carrying the drift it left with.
        _vx = _vy = _vz = 0;
        _lookPrimed = false;
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
        _lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
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
        double dt = Elapsed();

        // Before the angles are read, so the flight follows the camera that is
        // actually on screen rather than the one the input asked for.
        if (_cinematic) SmoothLook(m, dt);

        // The *composed* view angles, not the base pair: that triple is what the
        // renderer reads, so it is literally where the camera points, and flight
        // follows the picture rather than the state behind it. 12-bit reads --
        // an s16 read misinterprets a negative pitch, and while sin/cos would
        // not care (they are 2PI-periodic), nothing should depend on that.
        int yaw   = GameState.ReadAngle12(m, GameState.ViewYaw);
        int pitch = GameState.ReadAngle12(m, GameState.ViewPitch);

        // Two sources, summed and clamped, so a pad and a keyboard both drive
        // this and neither has to be configured: the left stick, and the pad
        // word's own direction bits -- which the keyboard fills through the
        // player's bindings (W/S on Up/Down, A/D on L1/R1 in the shipped
        // layout), so this follows a rebind for free.
        var (sx, sy) = Shape(Controller.LeftX, Controller.LeftY);
        var (dfwd, dstrafe) = Digital();

        float forward = Math.Clamp(-sy + dfwd, -1f, 1f);      // stick up == forward
        float strafe  = Math.Clamp(sx + dstrafe, -1f, 1f);
        if (InvertStrafe) strafe = -strafe;
        float vertical = Hotkeys.FlyVertical();               // +1 up, -1 down

        // The velocity the input is asking for, units a second -- zero when
        // nothing is held, which is what the cinematic filter coasts down to.
        double tvx = 0, tvy = 0, tvz = 0;
        if (forward != 0f || strafe != 0f || vertical != 0f)
        {
            double rate = Speed * (Hotkeys.FlyFast() ? FastMultiplier : 1f);

            // The game's own heading vector, from func_80028080, with the camera's
            // pitch folded into forward: looking down and pushing forward
            // descends, which is what every other noclip does. Strafe stays level
            // -- pitch does not roll the flight -- and the up/down keys stay world
            // up, so there is always a way to climb while looking level.
            float fwdAngle    = GameState.AngleToRadians(yaw);
            float strafeAngle = GameState.AngleToRadians(yaw - 0x400);
            float pitchRad    = GameState.AngleToRadians(pitch);
            float level = MathF.Cos(pitchRad);   // the horizontal share of forward
            float dive  = MathF.Sin(pitchRad);   // and the vertical one

            // The Y delta one unit of "up" is worth. Y grows downwards in this
            // game's world space, hence the negative -- the same convention
            // InvertVertical exists to let a player overrule, which is why the
            // camera's own descent is hung off the same sign rather than a
            // second guess.
            float up = InvertVertical ? 1f : -1f;

            tvx = (-MathF.Sin(fwdAngle) * forward * level + -MathF.Sin(strafeAngle) * strafe) * rate;
            tvz = ( MathF.Cos(fwdAngle) * forward * level +  MathF.Cos(strafeAngle) * strafe) * rate;
            tvy = (vertical * up - forward * dive * up) * rate;
        }

        // Instantly at the input unless the cinematic camera is on, in which
        // case the velocity eases toward it and the flight keeps its glide for
        // a moment after the stick is let go.
        double k = _cinematic ? Lag(MoveSmoothing, dt) : 1.0;
        _vx += (tvx - _vx) * k;
        _vy += (tvy - _vy) * k;
        _vz += (tvz - _vz) * k;

        _x += _vx * dt;
        _y += _vy * dt;
        _z += _vz * dt;

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
    /// A first-order lag's blend factor for this step: the share of the way to
    /// the target a value moves in <paramref name="dt"/> seconds, given a time
    /// constant of <paramref name="tau"/>. Framed as an exponential rather than
    /// a fixed fraction so the filter is the same at any frame rate.
    /// </summary>
    static double Lag(double tau, double dt) =>
        tau <= 1e-4 ? 1.0 : 1.0 - Math.Exp(-dt / tau);

    /// <summary>
    /// The camera, trailing the input.
    ///
    /// The angles cannot simply be lerped in place: stage 3 has already added
    /// this frame's turn velocity to the base angle, so reading it back reads
    /// what *we* wrote last frame plus the new delta. The filter therefore
    /// recovers the input as a difference from its own last write, accumulates
    /// it into an unsmoothed target, and writes the smoothed value -- so the
    /// view lags but never loses ground, however long the turn is held.
    ///
    /// Both the base pair and the composed triple are written, the composed one
    /// keeping whatever offset stage 3 put between them (the deltaA/B/C the
    /// renderer's angle is built from), so nothing else the game does to the
    /// view is thrown away.
    /// </summary>
    static void SmoothLook(IMemory m, double dt)
    {
        // 12-bit reads, not s16: the game stores both angles masked
        // (func_80028DB8 folds pitch through `& 0xFFF`), so ReadS16 misreads
        // every negative pitch as +3396..+4095 -- the filter then chases a
        // phantom full-circle delta and the camera flips upside down.
        int baseYaw   = GameState.ReadAngle12(m, GameState.Yaw);
        int basePitch = GameState.ReadAngle12(m, GameState.Pitch);
        int yawOffset   = GameState.ReadAngle12(m, GameState.ViewYaw)   - baseYaw;
        int pitchOffset = GameState.ReadAngle12(m, GameState.ViewPitch) - basePitch;

        if (!_lookPrimed)
        {
            _targetYaw = _smoothYaw = baseYaw;
            _targetPitch = _smoothPitch = basePitch;
            _prevYaw = baseYaw;
            _prevPitch = basePitch;
            _lookPrimed = true;
            return;
        }

        // Shortest arc, because yaw is masked to 12 bits and a turn past zero
        // reads as a delta of almost a full circle the other way.
        int dYaw = (baseYaw - _prevYaw) & GameState.AngleMask;
        if (dYaw > GameState.AngleFull / 2) dYaw -= GameState.AngleFull;

        _targetYaw += dYaw;

        // Clamped, because the game's own base is. The look routine holds base
        // pitch inside +-PitchLimit, so while it sits at the limit the deltas
        // keep arriving and an unclamped target runs away past it -- then the
        // smoothed value overshoots on release. The bound is a no-op in steady
        // state (the target tracks a base that never leaves the range) and a
        // guard against exactly that runaway.
        _targetPitch = Math.Clamp(_targetPitch + (basePitch - _prevPitch),
                                  -GameState.PitchLimit, GameState.PitchLimit);

        double k = Lag(LookSmoothing, dt);
        _smoothYaw   += (_targetYaw - _smoothYaw) * k;
        _smoothPitch += (_targetPitch - _smoothPitch) * k;

        // Keep the pair from drifting out of a double's exact-integer range
        // over a long session, without moving the angle between them.
        if (_targetYaw > GameState.AngleFull * 64 || _targetYaw < -GameState.AngleFull * 64)
        {
            double turns = Math.Truncate(_targetYaw / GameState.AngleFull) * GameState.AngleFull;
            _targetYaw -= turns;
            _smoothYaw -= turns;
        }

        int yaw   = ((int)Math.Round(_smoothYaw)) & GameState.AngleMask;
        int pitch = (int)Math.Round(_smoothPitch);

        GameState.WriteAngle12(m, GameState.Yaw, yaw);
        GameState.WriteAngle12(m, GameState.Pitch, pitch);
        GameState.WriteAngle12(m, GameState.ViewYaw, yaw + yawOffset);
        GameState.WriteAngle12(m, GameState.ViewPitch, pitch + pitchOffset);

        _prevYaw = yaw;
        _prevPitch = pitch;
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

    /// <summary>Seconds since the last flight frame, clamped.</summary>
    static double Elapsed()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastTicks = now;
        return dt <= 0 ? 0 : Math.Min(dt, MaxStep);
    }

    /// <summary>
    /// Forward and strafe off the pad word, which is active LOW and carries the
    /// keyboard's bindings as well as a pad's buttons -- so one read covers both
    /// devices and follows whatever the player has bound.
    ///
    /// The shoulders are the exception: on a pad they fly up and down
    /// (<see cref="Hotkeys.FlyVertical"/>), so their strafe is dropped while the
    /// pad itself is holding them. A keyboard's A and D reach the same bits and
    /// keep strafing.
    /// </summary>
    static (float Forward, float Strafe) Digital()
    {
        ushort pad = Controller.State;
        bool Held(ushort bit) => (pad & bit) == 0;

        float f = 0f, s = 0f;
        if (Held(Controller.Up)) f += 1f;
        if (Held(Controller.Down)) f -= 1f;
        if (Held(Controller.R1) && !Hotkeys.PadDown(Hotkeys.PadRShoulder)) s += 1f;
        if (Held(Controller.L1) && !Hotkeys.PadDown(Hotkeys.PadLShoulder)) s -= 1f;
        return (f, s);
    }

    /// <summary>
    /// One stick as a radial-deadzoned, curved vector. Same shape as
    /// patches/Analog.cs -- the bytes are the runtime's 0..255 with 0x80
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
        _vx = _vy = _vz = 0;
        _lookPrimed = false;
        // The host outlives the mod: unloading mid-filming must not leave its
        // capture glyph muted for the rest of the session.
        Kf2.MouseIndicator.Suppressed = false;
    }
}
