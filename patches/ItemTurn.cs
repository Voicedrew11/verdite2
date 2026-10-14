using System.Diagnostics;
using System.Reflection;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using HostMouseButton = Silk.NET.Input.MouseButton;

namespace Kf2;

/// <summary>
/// Turn an item by hand: the one the game holds up when it is picked up, and the
/// menu's item preview. Verdite3's <c>ItemTurn</c>; the pickup is the same routine
/// in both games, and this one also shows an item in its menus.
///
///     KF2_ITEMTURN=0          the game's own spin only
///     KF2_ITEMTURN_GYRO=0     the gyroscope does not turn it too
///     KF2_ITEMTURN_PROBE=1    a line a second while an item is held up or previewed
///     KF2_ITEMTURN_TEST=id    hold up item <c>id</c> from the player's tick 10 s after
///                             the first area load, with the right stick taken as full
///                             right and half down for the hold's second second;
///                             dismiss with KF2_AUTOPAD
///     KF2_ITEMTURN_TEST=preview  the same stick for the second second after a
///                             preview is first drawn
///
/// <b>The pickup</b> is <c>func_8004831C(record, id)</c>, a loop that draws its own
/// frames. It flies the item before the eye (stage 13 called from <c>0x8004868C</c>),
/// spins it <c>0x40</c> a pass on its yaw (record <c>+0x26</c>) until a new button
/// (<c>0x8004870C</c>), then gives it, or turns it back (<c>0x800488A8</c>) and flies
/// it out or back (<c>0x80048984</c>). The record is at the routine's <c>sp+0x80</c>;
/// its rotation is three s16 at <c>+0x24</c> (x), <c>+0x26</c> (y), <c>+0x28</c> (z),
/// which the object walk hands the submitter with <c>0x800</c> added to y.
///
/// The turn is drawn, not written: the record keeps the angles the game wrote, so its
/// spin, its turn back and its comparisons are untouched; the walk asks
/// <see cref="Present"/> for the rotation to draw (ModelWalk.Ordinary), every drawn
/// frame, after the smoother. The one write is undoing the spin's step while the
/// player is turning the item, so it holds still under the hand and spins again
/// <see cref="ResumeMs"/> after they let go.
///
/// The game's rotation is <c>func_80014FE0</c>: <c>Ry'(y) Rx(x) Rz(z)</c>, where
/// <c>Ry'(t)</c> is <c>[[c,0,-s],[0,1,0],[s,0,c]]</c> (<c>func_80014F00</c>), exactly
/// KF3's. The camera's yaw is the s16 at <c>0x80192E8A</c>. The yaw the player adds
/// goes on the record's own, about the vertical; the tilt goes about the camera's
/// right, outside it, so the item tips toward and away from the eye however far it
/// has spun (a turntable). The mouse is the captured look's, as in Verdite3.
///
/// <b>The preview</b> is one model in bank 3 posed by a block at <c>0x8006E934</c>:
/// translation (three s16, <c>0, 0, 1000</c>), rotation (three s16 at
/// <c>0x8006E93C</c>, x y z) and the spin's step (u32 at <c>0x8006E944</c>, 4).
/// <c>func_80022CAC(item)</c> loads the model and resets the block;
/// <c>func_8002156C(item)</c>, called once a menu frame by every page that shows
/// an item, adds the step to y, builds the rotation with <c>RotMatrix</c> and the
/// light matrix from it, and draws the model with <c>func_800346CC</c>. It does
/// nothing while <c>u8[0x801994DF]</c> is zero or the item is <c>0xFF</c>. A pre on
/// <c>func_8002156C</c> writes the angles to draw (the game's, with the player's turn
/// added) and the step it should take, and the post puts the game's own back.
/// <c>RotMatrix</c> is PSY-Q's, <c>Rx(x) Ry(y) Rz(z)</c> with
/// <c>Ry = [[c,0,s],[0,1,0],[-s,0,c]]</c>; the preview's camera is the identity.
/// The mouse is a drag on the model there, since the menu's pointer is free: the box
/// is the vertex cache <c>func_800346CC</c>'s transform wrote, and MenuMouse asks
/// <see cref="Grab"/> before it takes a press as a click.
///
/// Sign: moving right brings the side nearest the eye to the right, and moving
/// down brings it down, for the mouse, the stick and the gyro alike. See "Turning
/// a picked-up item" and "Turning the item preview" in docs/INPUT.md.
/// </summary>
public static class ItemTurn
{
    // The pickup.
    const uint Pickup = 0x8004831C;
    const uint Renderer = 0x800342D8;
    const uint PlayerTick = 0x8002A550;  // stage 3, where KF2_ITEMTURN_TEST holds an item up

    // Stage 13's return addresses inside the pickup: which part of it is drawing.
    const uint Hold = 0x8004870C, FaceBack = 0x800488A8, FlyBack = 0x80048984;

    const uint ViewYaw = CameraBlock.Angles + 2u;  // s16, the camera's yaw
    const uint RecordAt = 0x80;          // the pickup's sp+0x80 holds the item's record
    const uint YawField = 0x26;          // s16, the yaw the spin steps
    const int Spin = 0x40;               // the spin's step a pass

    /// <summary>How quickly a held item's turn eases back once it is let go: the time
    /// constant, in seconds.</summary>
    const double EaseSeconds = 0.08;

    // The menu's preview.
    const uint Draw = 0x8002156C;
    const uint Load = 0x80022CAC;
    const uint Model = 0x800346CC;

    const uint Shown = 0x801994DF;       // u8, the preview's routines do nothing while 0
    const uint RotX = 0x8006E93C, RotY = 0x8006E93E, RotZ = 0x8006E940;
    const uint Step = 0x8006E944;        // u32, the spin's step a call

    const uint Bank = 0x8018E19C;        // the current model bank; func_800346CC selects bank 3
    const uint VertexCache = 0x8018EB94; // {sxy, otz, fog}, 8 bytes a vertex (PolyAssembler)
    const int MaxVertices = 1024;

    /// <summary>The tilt's limit either way: a quarter turn shows the top or the underside.</summary>
    const float PitchLimit = 0x400;

    /// <summary>The stick at full deflection, in angle units a second (about 180 degrees).</summary>
    const float StickRate = 0x800;

    /// <summary>A drag, in angle units a game pixel: a half turn for 128 pixels.</summary>
    const float DragRate = 16f;

    /// <summary>A gyro rate below this, in radians a second, is a hand at rest, not a turn.</summary>
    const float GyroRest = 0.05f;

    /// <summary>How long after the last turn the game's spin comes back.</summary>
    const double ResumeMs = 1500;

    /// <summary>A gap this long between draws is a preview that went away; the next is fresh.</summary>
    const double GoneMs = 250;

    /// <summary>Game pixels round the model's box that still grab it.</summary>
    const float GrabSlack = 6f;

    const float Units = 4096f / (2f * MathF.PI);     // angle units a radian

    public const string OnKey = "kf2.itemturn.on";
    public const string GyroKey = "kf2.itemturn.gyro";

    public static bool Enabled = true;
    public static bool UseGyro = true;

    static readonly HashSet<string> _fromEnv = [];
    static readonly ModInfo _self = new()
    {
        Id = "kf2.itemturn",
        Name = "Item turn",
        Version = "1.0",
        Description = "Turns a picked-up item and the menu's item preview with the mouse, the right stick or the gyroscope.",
    };

    // ---- the pickup ----
    enum Phase { Idle, FlyIn, Hold, Return }

    static Phase _phase;
    static uint _record;
    static float _holdYaw, _holdPitch;   // what the player added: yaw on the record's, tilt about the camera's right
    static long _stepped = -1;           // the drawn frame the input was last taken on
    static double _holdLastMs, _holdTurnedMs = double.NegativeInfinity, _holdMs;
    static long _presented, _undone;

    static int _testItem = -1;
    static bool _testing;
    static readonly Stopwatch _testClock = new();

    // ---- the preview ----
    static float _yaw, _pitch;           // what the player added: about the screen's vertical, then its right
    static double _lastMs = double.NegativeInfinity, _turnedMs = double.NegativeInfinity, _drawnMs = double.NegativeInfinity;
    static readonly Stopwatch _clock = Stopwatch.StartNew();

    // What the pre wrote over, for the post: the game's angles, its step, and the step taken.
    static bool _swapped;
    static ushort _gx, _gy, _gz;
    static uint _gStep, _taken;
    static (short X, short Y, short Z) _meant;

    // The model on screen, in game pixels: the centre and the half-size, grown per item.
    static float _cx, _cy, _half;
    static bool _boxed;

    // The drag: held from a press over the model until the button comes up.
    static bool _dragging;
    static System.Numerics.Vector2 _dragAt;

    static bool _probe, _queued;
    static double _probeAt;
    static long _draws, _turned, _mouseFrames, _stickFrames, _gyroFrames;
    static double _worst;

    static bool _test, _testDone;
    static double _testFrom = double.NaN;

    /// <summary>Inside the pickup, so the mouse is the item's.</summary>
    public static bool HoldsMouse => Enabled && _phase != Phase.Idle;

    /// <summary>The preview is being dragged: the menu's hover stands still.</summary>
    public static bool Dragging => _dragging && Showing && HostWindow.MouseAvailable
                                   && HostWindow.IsMouseButtonDown(HostMouseButton.Left);

    /// <summary>When the last drag ended, on <c>Environment.TickCount64</c>: hover waits
    /// for the pointer to move after it.</summary>
    public static long ReleasedAt { get; private set; } = long.MinValue;

    /// <summary>A preview is on screen now.</summary>
    public static bool Showing => Enabled && _clock.Elapsed.TotalMilliseconds - _drawnMs < GoneMs;

    /// <summary>An item is held up or previewed: the bow's gyro aim stands down.</summary>
    public static bool Busy => Showing || HoldsMouse;

    public static void Configure()
    {
        Kept.Env("KF2_ITEMTURN", OnKey, ref Enabled, _fromEnv);
        Kept.Env("KF2_ITEMTURN_GYRO", GyroKey, ref UseGyro, _fromEnv);
        _probe = Environment.GetEnvironmentVariable("KF2_ITEMTURN_PROBE")?.Trim() is "1" or "on" or "true";
        var test = Environment.GetEnvironmentVariable("KF2_ITEMTURN_TEST")?.Trim();
        if (string.Equals(test, "preview", StringComparison.OrdinalIgnoreCase)) _test = true;
        else if (!string.IsNullOrEmpty(test))
        {
            bool hex = test.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!int.TryParse(hex ? test[2..] : test, hex ? System.Globalization.NumberStyles.HexNumber
                                                          : System.Globalization.NumberStyles.Integer, null, out _testItem))
                throw new ArgumentException($"KF2_ITEMTURN_TEST: bad item id '{test}'");
        }
        if (_test || _testItem >= 0) _probe = true;
    }

    public static void Install()
    {
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            if (_testItem >= 0 && !_testClock.IsRunning && e.Name.StartsWith("fdat", StringComparison.Ordinal))
                _testClock.Start();
        });
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Kept.Saved(OnKey, ref Enabled, _fromEnv);
            Kept.Saved(GyroKey, ref UseGyro, _fromEnv);
            SetGyro(UseGyro);
        });
        HookAttach.OnOverlayLoad("item turn", Attach, "See \"Turning the item preview\" in docs/INPUT.md.");
    }

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        if (!on) Forget();
        SetGyro(UseGyro);
    }

    /// <summary>The pad's gyroscope is switched on only while something would use it:
    /// a pad streams a larger report once it is (runtime 0102). GyroAim asks for both.</summary>
    public static void SetGyro(bool on)
    {
        UseGyro = on;
        GyroAim.WantGyro();
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        uint[] wanted = [Pickup, Renderer, Draw, Load, Model];
        var found = wanted.Select(a => SymbolRegistry.Resolve("game", null, a)).ToArray();
        int missing = Array.IndexOf(found, null);
        if (missing >= 0)
        {
            Console.Error.WriteLine($"[KF2] item turn: not installed (no game function at 0x{wanted[missing]:X8})");
            return false;
        }
        var (pickup, renderer, draw, load, model) = (found[0]!, found[1]!, found[2]!, found[3]!, found[4]!);

        if (!_queued)
        {
            _queued = HookManager.AddPre(_self, pickup, Own(nameof(Enter)))
                      && HookManager.AddPost(_self, pickup, Own(nameof(Leave)))
                      && HookManager.AddPre(_self, renderer, Own(nameof(BeforeStage13)), Stage13.HookOrder.World)
                      && HookManager.AddPre(_self, draw, Own(nameof(BeforeDraw)))
                      && HookManager.AddPost(_self, draw, Own(nameof(AfterDraw)))
                      && HookManager.AddPre(_self, load, Own(nameof(BeforeLoad)))
                      && HookManager.AddPost(_self, model, Own(nameof(AfterModel)));
            if (_queued && _testItem >= 0 && SymbolRegistry.Resolve("game", null, PlayerTick) is { } tick)
                HookManager.AddPost(_self, tick, Own(nameof(Test)));
            if (!_queued) return false;
        }

        HookManager.Commit();
        bool ok = found.All(f => HookAttach.Installed(f!));
        Console.WriteLine(ok ? $"[KF2] item turn: {(Enabled ? "on" : "off")}, gyro {(UseGyro ? "on" : "off")}"
                             : "[KF2] item turn: not installed");
        return ok;
    }

    static MethodInfo Own(string name) =>
        typeof(ItemTurn).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    // ---- the pickup ------------------------------------------------------------

    public static void Enter(CpuContext c, IMemory m)
    {
        _phase = Phase.FlyIn;
        _record = 0;
        _holdYaw = _holdPitch = 0f;
        _holdTurnedMs = double.NegativeInfinity;
        _stepped = -1;
    }

    public static void Leave(CpuContext c, IMemory m)
    {
        if (_probe && _phase != Phase.Idle)
            Console.WriteLine($"[KF2] item turn: put away at yaw {_holdYaw:0} tilt {_holdPitch:0}");
        if (_phase == Phase.Hold || _phase == Phase.Return)
            // Motion gathered while the item was held is nobody's turn of the view.
            Mouse.TakeLook();
        _phase = Phase.Idle;
        _record = 0;
        _holdYaw = _holdPitch = 0f;
    }

    /// <summary>Which part of the pickup is drawing, by where stage 13 was called from.
    /// A redraw of loop pacing's comes back with the same return address, so the spin
    /// is undone only on the loop's own call. Ordered before the smoothers sample the
    /// record (<see cref="Stage13.HookOrder.World"/>), so the carry never sees the step.</summary>
    public static void BeforeStage13(CpuContext c, IMemory m)
    {
        if (_phase == Phase.Idle || !Enabled) return;

        switch (c.RA)
        {
            case Hold:
                if (_phase != Phase.Hold)
                {
                    _phase = Phase.Hold;
                    _holdLastMs = _holdMs = _clock.Elapsed.TotalMilliseconds;
                }
                _record = m.ReadU32(c.SP + RecordAt);
                if (!LoopPacing.InRedraw && HoldTurning && _record != 0)
                {
                    uint at = _record + YawField;
                    m.WriteU16(at, (ushort)((m.ReadU16(at) - Spin) & 0xFFF));
                    if (_probe) _undone++;
                }
                break;
            case FaceBack:
            case FlyBack:
                if (_probe && _phase != Phase.Return)
                    Console.WriteLine($"[KF2] item turn: let go at yaw {_holdYaw:0} tilt {_holdPitch:0}, " +
                                      (c.RA == FaceBack ? "turning back" : "flying out"));
                _phase = Phase.Return;
                break;
        }

        if (_probe) HoldProbe(m);
    }

    static bool HoldTurning => _clock.Elapsed.TotalMilliseconds - _holdTurnedMs < ResumeMs;

    /// <summary>Whether this object record is the item held up, for the walk.</summary>
    public static bool Shows(uint record) =>
        Enabled && _phase is Phase.Hold or Phase.Return && record == _record && _record != 0;

    /// <summary>Draw the item with the player's turn added: <paramref name="rot"/> is the
    /// walk's copy of the record's rotation (carried, y with <c>0x800</c> added), three
    /// s16, rewritten in place. Takes the input once a drawn frame.</summary>
    public static void Present(IMemory m, uint rot)
    {
        HoldStep();
        if (_holdYaw == 0f && _holdPitch == 0f) return;
        if (_probe) _presented++;

        short x = (short)m.ReadU16(rot), y = (short)m.ReadU16(rot + 2u), z = (short)m.ReadU16(rot + 4u);
        int view = (short)m.ReadU16(ViewYaw);
        var turned = Mul(Mul(Mul(RyP(view), Rx(_holdPitch)), RyP(-view)), GameEuler(y + _holdYaw, x, z));
        (x, y, z) = GameAngles(turned);
        m.WriteU16(rot, (ushort)x);
        m.WriteU16(rot + 2u, (ushort)y);
        m.WriteU16(rot + 4u, (ushort)z);
    }

    static void HoldStep()
    {
        long frame = FramePacing.Frames;
        if (frame == _stepped) return;
        _stepped = frame;

        double now = _clock.Elapsed.TotalMilliseconds;
        float dt = (float)Math.Clamp((now - _holdLastMs) / 1000.0, 0.0, 0.1);
        _holdLastMs = now;

        if (_phase == Phase.Return)
        {
            float keep = (float)Math.Exp(-dt / EaseSeconds);
            _holdYaw = Settle(Wrap(_holdYaw) * keep);
            _holdPitch = Settle(_holdPitch * keep);
            return;
        }

        float dYaw = 0f, dPitch = 0f;

        if (Mouse.Enabled && Mouse.Captured)
        {
            // TakeLook's turn is the look routine's, right negative; its pitch is
            // positive down with the player's inversion already in it.
            var (turn, look) = Mouse.TakeLook();
            if (turn != 0f || look != 0f)
            {
                dYaw -= turn;
                dPitch += look;
                if (_probe) _mouseFrames++;
            }
        }

        if (Analog.Enabled)
        {
            var (sx, sy) = Analog.RightStick;
            // KF2_ITEMTURN_TEST: the stick full right and half down for the hold's second second.
            if (_testing && now - _holdMs is >= 1000.0 and < 2000.0) (sx, sy) = (1f, 0.5f);
            if (sx != 0f || sy != 0f)
            {
                dYaw += sx * StickRate * Analog.TurnSens * dt * (Analog.InvertTurn ? -1f : 1f);
                dPitch += sy * StickRate * Analog.PitchSens * dt * (Analog.InvertPitch ? -1f : 1f);
                if (_probe) _stickFrames++;
            }
        }

        if (UseGyro && Controller.Gyro)
        {
            // SDL's axes for a pad held in front of you: x across it, y up, z toward
            // you, anticlockwise positive. Turning the pad to the left (+y) brings the
            // near side right, and tipping its far edge up (+x) brings it down: the
            // item turns as the pad does.
            float gx = Rest(Controller.GyroX), gy = Rest(Controller.GyroY);
            if (gx != 0f || gy != 0f)
            {
                dYaw += gy * Units * dt;
                dPitch += gx * Units * dt;
                if (_probe) _gyroFrames++;
            }
        }

        if (dYaw == 0f && dPitch == 0f) return;
        _holdTurnedMs = now;
        _holdYaw = Wrap(_holdYaw + dYaw);
        _holdPitch = Math.Clamp(_holdPitch + dPitch, -PitchLimit, PitchLimit);
    }

    static float Settle(float a) => MathF.Abs(a) < 0.5f ? 0f : a;

    /// <summary>Hold the test item up from the player's tick, once, as the use handler
    /// would: <c>func_8004831C(0, id)</c> makes the record itself. Only on a frame the
    /// world ticks, as the use handler inside the gated stages is.</summary>
    public static void Test(CpuContext c, IMemory m)
    {
        if (_testItem < 0 || !_testClock.IsRunning || _testClock.Elapsed.TotalSeconds < 10.0) return;
        if (!FramePacing.TickedThisFrame || m is not PSMemory mem) return;
        int id = _testItem;
        _testItem = -1;
        Console.WriteLine($"[KF2] item turn: test item 0x{id:X}");
        var saved = c.Snapshot();
        c.A0 = 0;
        c.A1 = (uint)id;
        _testing = true;
        Recompiled.KingsField2_game.func_8004831C(c, mem);
        _testing = false;
        c.Restore(saved);
        Console.WriteLine($"[KF2] item turn: test item 0x{id:X} put away");
        Console.Out.Flush();
    }

    static void HoldProbe(IMemory m)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_probeAt <= 0.0) { _probeAt = now; return; }
        if (now - _probeAt < 1000.0) return;
        Console.WriteLine($"[KF2] item turn: {_phase} record 0x{_record:X8} yaw {_holdYaw:0} tilt {_holdPitch:0} " +
                          $"drawn {_presented} spin held {_undone} | frames mouse {_mouseFrames} stick {_stickFrames} " +
                          $"gyro {_gyroFrames} (pad gyro {(Controller.Gyro ? "on" : "off")}) | {AxisCheck(m)}");
        _probeAt = now;
        _presented = _undone = _mouseFrames = _stickFrames = _gyroFrames = 0;
    }

    /// <summary>The tilt's axis, <c>Ry'(view)</c> applied to x, against the camera's
    /// right as the view matrix has it (its row 0): a convention gone wrong reads far
    /// from 1.</summary>
    static string AxisCheck(IMemory m)
    {
        int view = (short)m.ReadU16(ViewYaw);
        var r = RyP(view);
        double ax = r[0, 0], ay = r[1, 0], az = r[2, 0];
        double vx = (short)m.ReadU16(CameraBlock.ViewMatrix), vy = (short)m.ReadU16(CameraBlock.ViewMatrix + 2u),
               vz = (short)m.ReadU16(CameraBlock.ViewMatrix + 4u);
        double len = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        return len < 1.0 ? "axis unread" : $"tilt axis . camera right {(ax * vx + ay * vy + az * vz) / len:0.000}";
    }

    // ---- the preview -------------------------------------------------------------

    static void Forget()
    {
        _yaw = _pitch = 0f;
        _turnedMs = double.NegativeInfinity;
        _boxed = false;
        _half = 0f;
        if (_dragging) ReleasedAt = Environment.TickCount64;
        _dragging = false;
    }

    /// <summary>A new item is loaded into the preview, and the game resets its pose: so is ours.</summary>
    public static void BeforeLoad(CpuContext c, IMemory m)
    {
        if (_probe && (_yaw != 0f || _pitch != 0f))
            Console.WriteLine($"[KF2] item turn: new item 0x{c.A0 & 0xFF:X2}, put down at yaw {_yaw:0} tilt {_pitch:0}");
        Forget();
    }

    public static void BeforeDraw(CpuContext c, IMemory m)
    {
        _swapped = false;
        if (!Enabled) return;
        // The routine's own gate: nothing is stepped or drawn.
        if (m.ReadU8(Shown) == 0 || (c.A0 & 0xFF) == 0xFF) return;

        double now = _clock.Elapsed.TotalMilliseconds;
        if (now - _drawnMs >= GoneMs)
        {
            // A preview that went away and came back without a new item (the menu
            // closed and reopened on the same one) keeps the game's pose, so ours goes.
            Forget();
            _lastMs = now;
            if (_test && !_testDone) _testFrom = now;
        }
        _drawnMs = now;
        if (_probe) _draws++;

        Input(now);

        _gx = m.ReadU16(RotX);
        _gy = m.ReadU16(RotY);
        _gz = m.ReadU16(RotZ);
        _gStep = m.ReadU32(Step);
        _taken = Turning(now) ? 0u : _gStep;
        if (_yaw == 0f && _pitch == 0f && _taken == _gStep) return;

        // The routine adds the step to y and masks all three; what it builds the
        // matrix from is (x, y + step, z), so the player's turn goes on that.
        int y = (_gy + (int)_taken) & 0xFFF;
        var turned = Mul(Mul(Rx(_pitch), Ry(_yaw)), Euler(_gx & 0xFFF, y, _gz & 0xFFF));
        _meant = Angles(turned);
        m.WriteU16(RotX, (ushort)_meant.X);
        m.WriteU16(RotY, (ushort)((_meant.Y - (int)_taken) & 0xFFF));
        m.WriteU16(RotZ, (ushort)_meant.Z);
        m.WriteU32(Step, _taken);
        _swapped = true;
        if (_probe && (_yaw != 0f || _pitch != 0f)) _turned++;
    }

    public static void AfterDraw(CpuContext c, IMemory m)
    {
        if (!_swapped) return;
        _swapped = false;

        // The matrix RotMatrix built is still in the routine's frame, at its sp+0x10.
        if (_probe) Check(m, c.SP - 0xE0u + 0x10u);

        m.WriteU16(RotX, (ushort)(_gx & 0xFFF));
        m.WriteU16(RotY, (ushort)((_gy + (int)_taken) & 0xFFF));
        m.WriteU16(RotZ, (ushort)(_gz & 0xFFF));
        m.WriteU32(Step, _gStep);

        if (_probe) Probe();
    }

    /// <summary>The model's box on screen, from the vertex cache its transform just
    /// filled: bank 3's model 0, <c>u32[entry+4]</c> vertices.</summary>
    public static void AfterModel(CpuContext c, IMemory m)
    {
        if (!Enabled) return;
        uint bank = m.ReadU32(Bank);
        if (bank < 0x80010000u || bank >= 0x80200000u) return;
        int n = (int)m.ReadU32(bank + 0xCu + 4u);
        if (n <= 0 || n > MaxVertices) return;

        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            uint sxy = m.ReadU32(VertexCache + (uint)i * 8u);
            int x = (short)(sxy & 0xFFFF), y = (short)(sxy >> 16);
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }
        if (x1 < x0 || y1 < y0) return;

        // A square round the centre, as large as the model has been: it spins, and
        // a box that shrinks as it turns edge-on would drop the drag's next press.
        _cx = (x0 + x1) * 0.5f;
        _cy = (y0 + y1) * 0.5f;
        _half = Math.Max(_half, Math.Max(x1 - x0, y1 - y0) * 0.5f);
        _boxed = true;
    }

    /// <summary>
    /// A left press at a game pixel: true if it lands on the preview, which then
    /// owns the drag until the button comes up, and the menu must not take it as a
    /// click. MenuMouse asks on every fresh press.
    /// </summary>
    public static bool Grab(float gameX, float gameY)
    {
        if (!Showing || !_boxed || float.IsNaN(gameX)) return false;
        float r = _half + GrabSlack;
        if (MathF.Abs(gameX - _cx) > r || MathF.Abs(gameY - _cy) > r) return false;
        _dragging = true;
        _dragAt = ImGui.GetCurrentContext() == IntPtr.Zero ? default : ImGui.GetIO().MousePos;
        if (_probe) Console.WriteLine($"[KF2] item turn: grabbed at {gameX:0},{gameY:0} (model {_cx:0},{_cy:0} ±{_half:0})");
        return true;
    }

    static bool Turning(double now) => _dragging || now - _turnedMs < ResumeMs;

    /// <summary>The player's turn since the last draw: the drag, the stick, the gyro.</summary>
    static void Input(double now)
    {
        float dt = (float)Math.Clamp((now - _lastMs) / 1000.0, 0.0, 0.1);
        _lastMs = now;

        float dYaw = 0f, dPitch = 0f;

        if (_dragging)
        {
            if (!HostWindow.MouseAvailable || !HostWindow.IsMouseButtonDown(HostMouseButton.Left)
                || ImGui.GetCurrentContext() == IntPtr.Zero)
            {
                _dragging = false;
                _turnedMs = now;
                ReleasedAt = Environment.TickCount64;
            }
            else
            {
                var pos = ImGui.GetIO().MousePos;
                var d = pos - _dragAt;
                _dragAt = pos;
                // Window pixels to game pixels: the picture is GameH game pixels high.
                float scale = OutputView.Valid && OutputView.Size.Y > 1f ? OutputView.GameH / OutputView.Size.Y : 1f;
                if (!float.IsNaN(d.X) && (d.X != 0f || d.Y != 0f))
                {
                    // Right brings the side nearest the eye right, down brings it down.
                    dYaw -= d.X * scale * DragRate;
                    dPitch += d.Y * scale * DragRate;
                    if (_probe) _mouseFrames++;
                }
            }
        }

        if (Analog.Enabled)
        {
            var (sx, sy) = Analog.RightStick;
            if (_test && !double.IsNaN(_testFrom))
            {
                double t = now - _testFrom;
                if (t is >= 1000.0 and < 2000.0) (sx, sy) = (1f, 0.5f);
                else if (t >= 2000.0) { _testDone = true; _testFrom = double.NaN; }
            }
            if (sx != 0f || sy != 0f)
            {
                dYaw -= sx * StickRate * Analog.TurnSens * dt * (Analog.InvertTurn ? -1f : 1f);
                dPitch += sy * StickRate * Analog.PitchSens * dt * (Analog.InvertPitch ? -1f : 1f);
                if (_probe) _stickFrames++;
            }
        }

        if (UseGyro && Controller.Gyro)
        {
            // SDL's axes for a pad held in front of you: x across it, y up, z toward
            // you, anticlockwise positive. Turning the pad to the left (+y) brings its
            // near edge right, and tipping its far edge up (+x) brings it down: the
            // item turns as the pad does.
            float gx = Rest(Controller.GyroX), gy = Rest(Controller.GyroY);
            if (gx != 0f || gy != 0f)
            {
                dYaw -= gy * Units * dt;
                dPitch += gx * Units * dt;
                if (_probe) _gyroFrames++;
            }
        }

        if (dYaw == 0f && dPitch == 0f) return;
        _turnedMs = now;
        _yaw = Wrap(_yaw + dYaw);
        _pitch = Math.Clamp(_pitch + dPitch, -PitchLimit, PitchLimit);
    }

    static float Rest(float rate) => MathF.Abs(rate) < GyroRest ? 0f : rate;

    /// <summary>Into -0x800..0x800.</summary>
    static float Wrap(float a)
    {
        a %= 4096f;
        if (a >= 2048f) a -= 4096f;
        if (a < -2048f) a += 4096f;
        return a;
    }

    // ---- the preview's rotation: PSY-Q's RotMatrix, in doubles ----------------------

    static double Rad(double units) => units * (2.0 * Math.PI / 4096.0);

    static double[,] Rx(double units)
    {
        double c = Math.Cos(Rad(units)), s = Math.Sin(Rad(units));
        return new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
    }

    static double[,] Ry(double units)
    {
        double c = Math.Cos(Rad(units)), s = Math.Sin(Rad(units));
        return new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
    }

    static double[,] Rz(double units)
    {
        double c = Math.Cos(Rad(units)), s = Math.Sin(Rad(units));
        return new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } };
    }

    /// <summary>PSY-Q's <c>RotMatrix</c>: <c>Rx(x) Ry(y) Rz(z)</c>.</summary>
    static double[,] Euler(double x, double y, double z) => Mul(Mul(Rx(x), Ry(y)), Rz(z));

    static double[,] Mul(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            r[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
        return r;
    }

    /// <summary>The three angles of a rotation, <see cref="Euler"/>'s inverse. Row 0 is
    /// <c>(cos y cos z, -cos y sin z, sin y)</c> and column 2
    /// <c>(sin y, -sin x cos y, cos x cos y)</c>; with y a quarter turn, z is taken
    /// as 0 and x read off column 1, <c>(0, cos x, sin x)</c>.</summary>
    static (short X, short Y, short Z) Angles(double[,] m)
    {
        double y = Math.Asin(Math.Clamp(m[0, 2], -1.0, 1.0)), x, z;
        if (Math.Sqrt(m[0, 0] * m[0, 0] + m[0, 1] * m[0, 1]) > 1e-9)
        {
            x = Math.Atan2(-m[1, 2], m[2, 2]);
            z = Math.Atan2(-m[0, 1], m[0, 0]);
        }
        else
        {
            x = Math.Atan2(m[2, 1], m[1, 1]);
            z = 0.0;
        }
        return (Angle(x), Angle(y), Angle(z));
    }

    static short Angle(double rad) => (short)((int)Math.Round(rad * (4096.0 / (2.0 * Math.PI))) & 0xFFF);

    // ---- the pickup's rotation: the game's own, func_80014FE0 ------------------------

    /// <summary><c>func_80014F00</c>: <c>[[c,0,-s],[0,1,0],[s,0,c]]</c>.</summary>
    static double[,] RyP(double units)
    {
        double c = Math.Cos(Rad(units)), s = Math.Sin(Rad(units));
        return new[,] { { c, 0, -s }, { 0, 1, 0 }, { s, 0, c } };
    }

    /// <summary>The rotation the game builds from an object's three angles
    /// (<c>func_80014FE0</c>), as unit-scale doubles.</summary>
    public static double[,] Rotation(double y, double x, double z) => GameEuler(y, x, z);

    /// <summary><c>func_80014FE0</c>: <c>Ry'(y) Rx(x) Rz(z)</c>.</summary>
    static double[,] GameEuler(double y, double x, double z) => Mul(Mul(RyP(y), Rx(x)), Rz(z));

    /// <summary>The game's three angles of a rotation, <see cref="GameEuler"/>'s inverse.
    /// Column 2 is <c>(-sin y cos x, -sin x, cos y cos x)</c> and row 1
    /// <c>(cos x sin z, cos x cos z, -sin x)</c>; straight up or down, z is taken as 0
    /// and y read off column 0, <c>(cos y, 0, sin y)</c>.</summary>
    static (short X, short Y, short Z) GameAngles(double[,] m)
    {
        double sx = Math.Clamp(-m[1, 2], -1.0, 1.0);
        double x = Math.Asin(sx), y, z;
        if (Math.Sqrt(m[1, 0] * m[1, 0] + m[1, 1] * m[1, 1]) > 1e-9)
        {
            y = Math.Atan2(-m[0, 2], m[2, 2]);
            z = Math.Atan2(m[1, 0], m[1, 1]);
        }
        else
        {
            y = Math.Atan2(m[2, 0], m[0, 0]);
            z = 0.0;
        }
        return (Angle(x), Angle(y), Angle(z));
    }

    // ---- the probe ------------------------------------------------------------------

    /// <summary>The worst element between the matrix the game built and the one meant,
    /// in 4.12 units: a convention gone wrong reads in the thousands.</summary>
    static void Check(IMemory m, uint at)
    {
        var meant = Euler(_meant.X, _meant.Y, _meant.Z);
        for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
        {
            double got = (short)m.ReadU16(at + (uint)(i * 6 + j * 2));
            _worst = Math.Max(_worst, Math.Abs(got - meant[i, j] * 4096.0));
        }
    }

    static void Probe()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_probeAt <= 0.0) { _probeAt = now; return; }
        if (now - _probeAt < 1000.0) return;
        Console.WriteLine($"[KF2] item turn: yaw {_yaw:0} tilt {_pitch:0} spin {(Turning(now) ? "held" : "on")} " +
                          $"| draws {_draws} turned {_turned} matrix off by {_worst:0} " +
                          $"| model {(_boxed ? $"{_cx:0},{_cy:0} ±{_half:0}" : "unseen")} " +
                          $"| frames drag {_mouseFrames} stick {_stickFrames} gyro {_gyroFrames} " +
                          $"(pad gyro {(Controller.Gyro ? "on" : "off")})");
        _probeAt = now;
        _draws = _turned = _mouseFrames = _stickFrames = _gyroFrames = 0;
        _worst = 0;
    }
}
