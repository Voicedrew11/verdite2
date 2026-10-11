using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Window;

namespace Kf2;

/// <summary>
/// The mouse aim marks, drawn over the game picture (<see cref="MouseAim"/>): a
/// small dot at the screen middle, the projection centre where the view points, and
/// a thin line from that dot to a thin circle.
///
/// The circle is where the mouse has the player turning to. With head look
/// (<see cref="MouseAim.HeadActive"/>) the view itself follows the mouse, so the
/// middle is the aim and the circle marks where the body faces instead: the
/// negated offset, on the far side of the middle. In the held view
/// (<see cref="MouseAim.HoldActive"/>) the picture holds still while the body
/// turns, so the dot, the facing, leaves the middle and travels to the circle, and
/// both come back to the middle as the view catches up. The line is brighter while a
/// committed turn is under way (<see cref="MouseAim.Steering"/>), and can be left
/// out (<see cref="MouseAim.Line"/>).
///
/// **The marks are there only while they say something** (<see cref="MouseAim.Marks"/>,
/// "while aiming" by default). Reported from play: marks held at the middle of the
/// picture, moving with every small motion of the hand, drew the eye and kept it
/// there. So they fade in only while a turn is under way or the circle is off the
/// middle by more than half the dead ring, and fade out completely once it is
/// over; tremor inside the ring shows nothing. The dead ring is not drawn, and the
/// strokes are dim. "Always" keeps them on, fading to a floor when idle; "off" draws
/// nothing, which suits head look, where the middle of the picture is the aim.
///
/// An <c>IFloatingPanel</c> with no window of its own. <c>Draw</c> puts its marks
/// on the foreground draw list, so nothing is docked, focused or given a frame
/// that could take input from the picture.
///
/// **The switch is <see cref="MouseAim.Active"/>, and the setter is a no-op** for
/// the same reason as <see cref="MapOverlay"/>: the runtime writes every panel's
/// open state back into the view, and a second store for "is aim on" would
/// disagree with the setting. <see cref="MouseAim.Shown"/> returning null (the
/// pointer free, a menu, a load) is what hides the marks inside a drawn frame.
///
/// The marks are placed in game pixels, through the same mapping the editor and
/// the menu pointer use, so they sit on the same spot the game draws the facing
/// at, widescreen margin included. Their sizes are game pixels too, scaled by the
/// window's height, so they grow with the picture rather than staying at a
/// fixed number of screen pixels; a stroke is never thinner than one window pixel.
/// </summary>
public sealed class MouseAimOverlay : IFloatingPanel
{
    public static readonly MouseAimOverlay Instance = new();
    MouseAimOverlay() { }

    public string Name => "kf2mouseaim";

    // Sizes and alphas, in game pixels and 0..1 opacity.
    const float DotRadius = 1.25f;
    const float DotAlpha = 0.5f;
    const float LineThickness = 0.75f;
    const float LineAlpha = 0.2f;
    const float LineAlphaSteering = 0.35f;
    const float CircleRadius = 5f;
    const float CircleThickness = 0.75f;
    const float CircleAlpha = 0.5f;
    const float OutlineThickness = 0.75f;
    const float OutlineAlpha = 0.3f;

    // The fade, in seconds: in quickly, so a turn is shown as it starts; held a
    // moment once it is over, then out. "Always" fades only to its floor.
    const float FadeIn = 0.1f;
    const float FadeHold = 0.15f;
    const float FadeOut = 0.35f;
    const float AlwaysFloor = 0.3f;

    float _fade;
    long _drawnAt;
    long _wantedAt;

    /// <summary>On while mouse aim is on and its marks are; <c>PanelManager.DrawPanels</c>
    /// skips a closed panel. The setter is a no-op because the setting is the one
    /// store, as on <see cref="MapOverlay"/>.</summary>
    public bool IsOpen
    {
        get => MouseAim.Active && MouseAim.Marks != MouseAim.MarksOff;
        set { }
    }

    public void Draw()
    {
        if (MouseAim.Shown is not { } off) { _fade = 0f; return; }
        if (!OutputView.Valid || OutputView.GameW <= 0 || OutputView.GameH <= 0) return;

        // The marks' places in game pixels: the middle is the projection centre, and
        // an angle off it moves a point by the projection distance times its tangent.
        // Yaw is the game's Left, so a positive yaw moves it left. With head look the
        // body faces the negated offset, so the circle goes on the far side.
        // In the held view the camera points off the facing by the lead, so the dot,
        // the facing, sits at the lead's negation and the circle at the aim less it.
        float h = GteDepth.ProjH;
        var (leadYaw, leadPitch) = MouseAim.HeldLead;
        var (dotYaw, dotPitch) = (-leadYaw, -leadPitch);
        var (aimYaw, aimPitch) = MouseAim.HeadActive ? (-off.Yaw, -off.Pitch)
                               : (off.Yaw - leadYaw, off.Pitch - leadPitch);
        var centreGame = Place(h, dotYaw, dotPitch);
        var reticleGame = Place(h, aimYaw, aimPitch);
        float gap = Vector2.Distance(centreGame, reticleGame);

        // Something to say: a turn under way, an aim off the facing by more than the
        // tremor the dead ring absorbs, or a held view still to come back.
        float half = MouseAim.RingRadians * 0.5f;
        bool aiming = MouseAim.Steering
                   || MathF.Sqrt(off.Yaw * off.Yaw + off.Pitch * off.Pitch) > half
                   || MathF.Sqrt(leadYaw * leadYaw + leadPitch * leadPitch) > half;
        float floor = MouseAim.Marks == MouseAim.MarksAlways ? AlwaysFloor : 0f;
        float fade = Fade(aiming, floor);
        if (fade <= 0.01f) return;

        float scale = OutputView.Size.Y / OutputView.GameH;
        var c = WindowPixel(centreGame);
        var r = WindowPixel(reticleGame);

        var dl = ImGui.GetForegroundDrawList();
        dl.PushClipRect(OutputView.Min, OutputView.Max, true);

        // The line joins the middle to the circle's centre, once they are apart enough
        // to be worth one. It is drawn before the circle and the dot, so they sit on it.
        if (MouseAim.Line && gap > 1f)
        {
            float alpha = MouseAim.Steering ? LineAlphaSteering : LineAlpha;
            dl.AddLine(c, r, Colour(1f, alpha, fade), Thickness(LineThickness, scale));
        }

        // The circle: a thin ring, with a faint dark ring just outside it so it
        // still shows over snow or a lit wall.
        float radius = CircleRadius * scale;
        float circleT = Thickness(CircleThickness, scale);
        float outlineT = Thickness(OutlineThickness, scale);
        dl.AddCircle(r, radius + circleT * 0.5f + outlineT * 0.5f, Colour(0f, OutlineAlpha, fade), 0, outlineT);
        dl.AddCircle(r, radius, Colour(1f, CircleAlpha, fade), 0, circleT);

        // The dot at the middle, with a faint dark outline so it shows on a bright
        // wall too.
        float dotRadius = DotRadius * scale;
        dl.AddCircleFilled(c, dotRadius, Colour(1f, DotAlpha, fade));
        dl.AddCircle(c, dotRadius + outlineT * 0.5f, Colour(0f, OutlineAlpha, fade), 0, outlineT);

        dl.PopClipRect();
    }

    /// <summary>
    /// The marks' opacity this frame, stepped by the time since the last one: up to 1
    /// over <see cref="FadeIn"/> while <paramref name="aiming"/>, held for
    /// <see cref="FadeHold"/> once it stops, then down to <paramref name="floor"/> over
    /// <see cref="FadeOut"/>.
    /// </summary>
    float Fade(bool aiming, float floor)
    {
        long now = Environment.TickCount64;
        float dt = _drawnAt == 0 ? 0f : Math.Clamp((now - _drawnAt) / 1000f, 0f, 0.1f);
        _drawnAt = now;
        if (aiming) _wantedAt = now;

        bool held = now - _wantedAt <= FadeHold * 1000f;
        _fade = aiming || held
            ? MathF.Min(1f, _fade + dt / FadeIn)
            : MathF.Max(floor, _fade - dt / FadeOut);
        return MathF.Max(_fade, floor);
    }

    /// <summary>An angle off the view, yaw positive to the game's Left and pitch
    /// positive down, as a place in game pixels: the projection distance times its
    /// tangent off the projection centre.</summary>
    static Vector2 Place(float h, float yaw, float pitch) =>
        new(GteDepth.ProjCx - h * MathF.Tan(yaw), GteDepth.ProjCy + h * MathF.Tan(pitch));

    /// <summary>A stroke's width in window pixels: its game-pixel size scaled to the
    /// window, never under one window pixel, so a thin mark never vanishes.</summary>
    static float Thickness(float gamePixels, float scale) => MathF.Max(gamePixels * scale, 1f);

    /// <summary>A white or black stroke colour at <paramref name="alpha"/>, multiplied
    /// by the fade.</summary>
    static uint Colour(float grey, float alpha, float fade) =>
        ImGui.GetColorU32(new Vector4(grey, grey, grey, alpha * fade));

    /// <summary>A game pixel to a window pixel, the mapping the editor uses (its
    /// <c>WindowPixel</c>), so the marks and the picture agree.</summary>
    static Vector2 WindowPixel(Vector2 game)
    {
        int margin = Display.WideMargin(OutputView.GameW);
        float picW = OutputView.GameW + 2 * margin;
        var min = OutputView.Min;
        var size = OutputView.Size;
        return new Vector2(min.X + (game.X + margin) / picW * size.X, min.Y + game.Y / OutputView.GameH * size.Y);
    }
}
