using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Kf2;

/// <summary>
/// The rectangle the game's picture is drawn into, on the screen, for the overlays
/// that sit on it: the message text, and the map mod's full-screen map.
///
/// <c>OutputView</c> (tools/RecompOne/patches/0029) publishes that rectangle from
/// the one place that knows it, once per frame, before any floating panel draws.
/// It falls back to the viewport's work area when no picture was drawn this frame
/// (before the first present, and while the Output panel is collapsed), so an
/// overlay always has something to fit.
///
/// This stays in core, and not in the map mod, because the message text needs it
/// with the map off. A mod is not a thing the port's own overlays may depend on.
/// </summary>
public static class GamePicture
{
    public static void Get(out Vector2 p0, out Vector2 p1)
    {
        if (OutputView.Valid && OutputView.Size.X >= 32f && OutputView.Size.Y >= 32f)
        {
            p0 = OutputView.Min;
            p1 = OutputView.Max;
            return;
        }

        var vp = ImGui.GetMainViewport();
        p0 = vp.WorkPos;
        p1 = vp.WorkPos + vp.WorkSize;
    }
}
