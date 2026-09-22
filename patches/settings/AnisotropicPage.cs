using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// Texture filtering, under Video ▸ Enhancements: one slider with fixed positions,
/// the shape of the frame-rate slider.
///
/// Off is the console's single texel. Trilinear is mipmaps alone. 2x to 16x are
/// the anisotropic kernel over the mip chain, so every position past Off turns
/// mipmaps on: the kernel without them costs the same and cannot average a
/// footprint past 16 texels, so it is not a position (<c>KF2_MIPMAPS=0</c> is the
/// comparison). A level set from the console that is not a position opens at the
/// nearest one below it and stays in force until the slider is moved.
/// </summary>
public sealed class AnisotropicPage : IPatchPage
{
    public string Id => "aniso";
    public string Title => "Enhancements";
    public int Order => 24;

    static readonly string[] Labels = ["Off", "Trilinear", "2x", "4x", "8x", "16x"];
    static readonly int[] Levels = [1, 1, 2, 4, 8, 16];

    public void Draw()
    {
        int index = Index();
        if (ImGui.SliderInt("Texture filtering", ref index, 0, Labels.Length - 1, Labels[index],
                            ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoInput))
        {
            bool mip = index > 0;
            Anisotropic.SetLevel(Levels[index]);
            Anisotropic.SetMipmaps(mip);
            PatchSettings.Set(Anisotropic.LevelKey, Levels[index]);
            PatchSettings.Set(Anisotropic.MipKey, mip);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Steadies textures seen at a glancing angle.");
    }

    static int Index()
    {
        int level = Anisotropic.Level;
        if (level <= 1) return Anisotropic.Mipmaps ? 1 : 0;
        int best = 2;
        for (int i = 2; i < Levels.Length; i++)
            if (Levels[i] <= level) best = i;
        return best;
    }
}
