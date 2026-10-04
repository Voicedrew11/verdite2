using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using Silk.NET.Input;
using HostWindow = RecompOne.Runtime.Host.HostWindow;

namespace Kf2.Remaster;

/// <summary>
/// The editor's free camera: every frame drawn from an eye of the editor's own through
/// <see cref="Stage13.ViewOverride"/>, the cull grid following it. Hold the right mouse
/// button on the picture to look, with WASD to fly, Q and E down and up, Shift faster,
/// Ctrl slower and the wheel for the speed. The first-person arm is left out while it
/// is on. Closing the editor gives the view back to the game. See "Phase 7, the first
/// slice" in docs/REMASTER.md.
/// </summary>
public static class EditorCamera
{
    public static bool On { get; private set; }

    /// <summary>The right button is held for looking, so WASD, Q and E fly.</summary>
    public static bool Looking => On && _looking;

    /// <summary>World units a second; a tile is 2048.</summary>
    public static float Speed = 4096f;

    public const float MinSpeed = 256f, MaxSpeed = 65536f;

    /// <summary>Angle units (0x1000 a turn) per pixel of mouse motion.</summary>
    public static float LookRate = 4f;

    static Vector3 _pos;
    static float _pitch, _yaw;
    static short _roll;
    static bool _looking;
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _last;

    /// <summary>The game's own pitch stops short of straight up and down; so does this.</summary>
    const float PitchLimit = 1000f;

    public static Camera Current => new((int)MathF.Round(_pos.X), (int)MathF.Round(_pos.Y), (int)MathF.Round(_pos.Z),
                                        (short)MathF.Round(_pitch), (short)((int)MathF.Round(_yaw) & 0xFFF), _roll);

    /// <summary>Turn it on from the player's eye, or off, giving the view back.</summary>
    public static void SetOn(bool on, IMemory? m)
    {
        if (on == On) return;
        if (on)
        {
            if (m == null) return;
            Place(Stage13.Handed ?? Camera.Read(m));
            On = true;
            Stage13.HideArmOnOverride = true;
            Publish();
        }
        else
        {
            On = false;
            _looking = false;
            Stage13.HideArmOnOverride = false;
            Stage13.ViewOverride = null;
        }
    }

    /// <summary>Put the eye at a camera.</summary>
    public static void Place(Camera cam)
    {
        _pos = new Vector3(cam.X, cam.Y, cam.Z);
        _pitch = cam.Pitch;
        _yaw = cam.Yaw & 0xFFF;
        _roll = cam.Roll;
        if (On) Publish();
    }

    /// <summary>Back to where the player is looking from.</summary>
    public static void ToPlayer(IMemory m) => Place(Stage13.Handed ?? Camera.Read(m));

    static void Publish() => Stage13.ViewOverride = Current;

    /// <summary>The eye's axes in world space, from its angles through the view matrix
    /// the last frame was drawn with: right, down (view Y) and forward.</summary>
    public static (Vector3 Right, Vector3 Down, Vector3 Forward) Axes(IMemory m)
    {
        var r = Lights.ReadView(m).R;
        return (new Vector3(r.M11, r.M12, r.M13), new Vector3(r.M21, r.M22, r.M23), new Vector3(r.M31, r.M32, r.M33));
    }

    /// <summary>Move by a distance along the eye's own axes (right, world up, forward).</summary>
    public static void Move(IMemory m, Vector3 local)
    {
        var (right, _, fwd) = Axes(m);
        _pos += right * local.X + new Vector3(0f, -local.Y, 0f) + fwd * local.Z;
        Publish();
    }

    /// <summary>Turn by angle units: yaw to the right, pitch up, both positive.</summary>
    public static void Turn(float right, float up)
    {
        _yaw = (_yaw + YawSign * right) % 4096f;
        if (_yaw < 0f) _yaw += 4096f;
        _pitch = Math.Clamp(_pitch + PitchSign * up, -PitchLimit, PitchLimit);
        Publish();
    }

    /// <summary>Measured with the <c>camera</c> verb against the axes the matrix gives:
    /// a larger yaw turns the view left, a larger pitch tips it down.</summary>
    const float YawSign = -1f, PitchSign = -1f;

    /// <summary>Once a frame, from the remaster's frame hook: the view goes back to the
    /// game when the editor closes.</summary>
    public static void Poll()
    {
        if (On && !Editor.Open) SetOn(false, null);
    }

    /// <summary>Mouse look and flying, from the editor panel's draw.</summary>
    public static void Input(IMemory m, bool pictureHovered)
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _last, 0.0, 0.1);
        _last = now;
        if (!On) return;

        var io = ImGui.GetIO();
        if (!_looking && pictureHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) _looking = true;
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Right)) _looking = false;
        if (!_looking || HotkeyGate.Typing) return;

        if (io.MouseDelta.X != 0f || io.MouseDelta.Y != 0f)
            Turn(io.MouseDelta.X * LookRate, -io.MouseDelta.Y * LookRate);
        if (io.MouseWheel != 0f)
            Speed = Math.Clamp(Speed * MathF.Pow(1.25f, io.MouseWheel), MinSpeed, MaxSpeed);

        var v = Vector3.Zero;
        if (HostWindow.IsKeyDown(Key.W)) v.Z += 1f;
        if (HostWindow.IsKeyDown(Key.S)) v.Z -= 1f;
        if (HostWindow.IsKeyDown(Key.D)) v.X += 1f;
        if (HostWindow.IsKeyDown(Key.A)) v.X -= 1f;
        if (HostWindow.IsKeyDown(Key.E)) v.Y += 1f;
        if (HostWindow.IsKeyDown(Key.Q)) v.Y -= 1f;
        if (v == Vector3.Zero) return;
        float speed = Speed;
        if (io.KeyShift) speed *= 4f;
        if (io.KeyCtrl) speed *= 0.25f;
        Move(m, Vector3.Normalize(v) * speed * dt);
    }
}
