using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Window;

namespace Kf2;

/// <summary>
/// The mouse aim reticle, drawn over the game picture: a cross at the facing and
/// a ring where the mouse has the player turning to (<see cref="MouseAim"/>).
///
/// An <c>IFloatingPanel</c> with no window of its own. <c>Draw</c> puts its marks
/// on the foreground draw list, so nothing is docked, focused or given a frame
/// that could take input from the picture.
///
/// **The switch is <see cref="MouseAim.Active"/>, and the setter is a no-op** for
/// the same reason as <see cref="MapOverlay"/>: the runtime writes every panel's
/// open state back into the view, and a second store for "is aim on" would
/// disagree with the setting. <see cref="MouseAim.Shown"/> returning null (the
/// pointer free, a menu, a load) is what hides the reticle inside a drawn frame.
///
/// The marks are placed in game pixels, through the same mapping the editor and
/// the menu pointer use, so they sit on the same spot the game draws the facing
/// at, widescreen margin included. Their sizes are game pixels too, scaled by the
/// window's height, so they grow with the picture rather than staying at a
/// fixed number of screen pixels.
/// </summary>
public sealed class MouseAimOverlay : IFloatingPanel
{
    public static readonly MouseAimOverlay Instance = new();
    MouseAimOverlay() { }

    public string Name => "kf2mouseaim";

    /// <summary>On while mouse aim is on; <c>PanelManager.DrawPanels</c> skips a
    /// closed panel, so this is also the "only while the look is steering" test.
    /// The setter is a no-op because the setting is the one store, as on
    /// <see cref="MapOverlay"/>.</summary>
    public bool IsOpen
    {
        get => MouseAim.Active;
        set { }
    }

    public void Draw()
    {
        if (MouseAim.Shown is not { } off) return;
        if (!OutputView.Valid || OutputView.GameW <= 0 || OutputView.GameH <= 0) return;

        // The reticle's place in game pixels: the facing is the projection centre,
        // and an angle off it moves the point by the projection distance times its
        // tangent. Yaw is the game's Left, so a positive yaw moves it left.
        float h = GteDepth.ProjH;
        var centre = new Vector2(GteDepth.ProjCx, GteDepth.ProjCy);
        var reticle = new Vector2(
            GteDepth.ProjCx - h * MathF.Tan(off.Yaw),
            GteDepth.ProjCy + h * MathF.Tan(off.Pitch));

        float scale = OutputView.Size.Y / OutputView.GameH;
        var c = WindowPixel(centre);
        var r = WindowPixel(reticle);

        var dl = ImGui.GetForegroundDrawList();
        dl.PushClipRect(OutputView.Min, OutputView.Max, true);

        // The facing: a small cross, faint, so it reads as where the player is
        // looking without competing with the reticle.
        uint cross = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.5f));
        float arm = 4f * scale;
        float thick = 1.5f * scale;
        dl.AddLine(c - new Vector2(arm, 0), c + new Vector2(arm, 0), cross, thick);
        dl.AddLine(c - new Vector2(0, arm), c + new Vector2(0, arm), cross, thick);

        // A faint line from the facing to the reticle, once they are apart enough
        // to be worth one, stopping at the ring's edge rather than its centre.
        float gap = Vector2.Distance(centre, reticle);
        float ringGame = 6f;
        if (gap > 3f)
        {
            var dir = (r - c) / Vector2.Distance(c, r);
            float ringPx = ringGame * scale;
            dl.AddLine(c, r - dir * ringPx, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.25f)), 1f * scale);
        }

        // The reticle: a bright ring, with a thin dark ring just outside it so it
        // still shows over snow or a lit wall.
        float radius = ringGame * scale;
        dl.AddCircle(r, radius + 1f * scale, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.5f)), 0, 1f * scale);
        dl.AddCircle(r, radius, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.85f)), 0, 2f * scale);

        dl.PopClipRect();
    }

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
