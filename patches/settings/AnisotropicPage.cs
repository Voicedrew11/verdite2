using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// Anisotropic filtering, under Video ▸ Enhancements.
///
/// A combo rather than a slider, and rather than a checkbox. Not a checkbox
/// because the level is a real cost/quality choice a player may want to make —
/// unlike the dither or true color, where there is one answer and only the
/// question of which. Not a slider because the useful values are the powers of
/// two the rest of the world labels this setting with, and a slider offers 16
/// values where four are meaningful; <see cref="FramePacingPage"/> makes the same
/// call about the frame rate for the same reason.
///
/// The entries write <see cref="Anisotropic.Level"/>, which is the tap ceiling
/// rather than a fixed cost: a fragment whose footprint is square takes one tap at
/// every setting, so Off and 16x differ only on the geometry that was actually
/// being undersampled. That is why the tooltip talks about floors rather than
/// about a percentage.
///
/// The level is clamped by the patch, not here, so a config carrying a value from
/// a build whose shader loop was bounded differently is corrected on load rather
/// than being drawn as an entry that does not exist.
/// </summary>
public sealed class AnisotropicPage : IPatchPage
{
    public string Id => "aniso";
    public string Title => "Enhancements";
    public int Order => 24;

    static readonly string[] Labels = ["Off", "2x", "4x", "8x", "16x"];
    static readonly int[] Levels = [1, 2, 4, 8, 16];

    public void Draw()
    {
        int index = Index();

        if (ImGui.Combo("Texture filtering", ref index, Labels, Labels.Length))
        {
            Anisotropic.SetLevel(Levels[index]);
            PatchSettings.Set(Anisotropic.LevelKey, Anisotropic.Level);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Anisotropic filtering. Steadies the crawling, sparkling " +
                             "texture on floors and walls seen at a glancing angle, by " +
                             "sampling the whole area a pixel covers instead of one point " +
                             "of it. Costs nothing where a surface faces the camera.");
    }

    /// <summary>The level the patch is actually at, folded onto the five entries.
    /// A level between two of them — reachable from KF2_ANISO=6 — opens as the
    /// nearest entry at or below it rather than earning a Custom entry, since
    /// unlike a widescreen aspect an odd tap ceiling is not a thing anyone means;
    /// it stays in force until the combo is touched.</summary>
    static int Index()
    {
        int level = Anisotropic.Level;
        int best = 0;
        for (int i = 0; i < Levels.Length; i++)
            if (Levels[i] <= level) best = i;
        return best;
    }
}
