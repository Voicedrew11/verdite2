using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// How the shaded gradient is drawn, under Video ▸ Enhancements — **one combo
/// where there were two checkboxes**.
///
/// The dither switch and the true-color switch are the two answers to one
/// question. The console renders into 15-bit VRAM, so a fog gradient steps into
/// 32 levels; the GPU's ordered dither hides that with a 4x4 crosshatch, and
/// <see cref="TrueColor"/> removes it instead by keeping eight bits
/// (<c>patches/recompone/0021</c>). Offered as two ticks they cross into four
/// states carrying three meanings, and the fourth — dither on *and* 24-bit — is a
/// smooth gradient with a crosshatch laid over it, which is nobody's answer to
/// anything. That state is what the combo exists to stop being reachable.
///
/// The three entries and what each writes:
///
/// <list type="table">
/// <item><term>Dither (original)</term><description>the crosshatch, 15-bit — what the hardware did</description></item>
/// <item><term>None</term><description>neither; the bands show raw. The port's shipped default</description></item>
/// <item><term>Smooth (24-bit)</term><description>eight bits and no crosshatch</description></item>
/// </list>
///
/// Both patches keep their own key (<c>kf2.nodither.on</c>,
/// <c>kf2.truecolor.on</c>) and their own environment variable
/// (<c>KF2_NODITHER</c>, <c>KF2_TRUECOLOR</c>), so merging the control strands no
/// config and takes no comparison off the console. Note the polarity:
/// <see cref="NoDither.Enabled"/> true means the crosshatch is *off*.
///
/// **Reading back follows <see cref="FrameSmoothingPage"/>'s shape, not
/// <see cref="WidescreenPage"/>'s.** A widescreen aspect that matches no preset
/// gets a Custom entry, because an arbitrary ratio is a real thing to have asked
/// for. The contradictory shading state is not: it is only reachable from
/// <c>KF2_NODITHER=0 KF2_TRUECOLOR=1</c>, and giving it an entry would put the
/// pointless combination back on the page. So true color is read as the master —
/// on means Smooth — and any click writes both patches, harmonising the pair.
/// </summary>
public sealed class ShadingPage : IPatchPage
{
    public string Id => "shading";
    public string Title => "Enhancements";
    public int Order => 22;

    const int Dither = 0;
    const int None = 1;
    const int Smooth = 2;

    static readonly string[] Labels = ["Dither (original)", "None", "Smooth (24-bit)"];

    public void Draw()
    {
        int index = Index();

        if (ImGui.Combo("Shading", ref index, Labels, Labels.Length)) Apply(index);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How the shaded gradient is drawn: the console's crosshatch, " +
                             "plain 15-bit, or smooth.");
    }

    /// <summary>The state the two patches are actually in, folded onto the three
    /// entries. True color wins, so the fourth state opens as Smooth rather than
    /// earning an entry of its own; one click on any entry then makes the pair
    /// consistent.</summary>
    static int Index()
    {
        if (TrueColor.Enabled) return Smooth;
        return NoDither.Enabled ? None : Dither;
    }

    /// <summary>Both patches and both keys, from the one combo. Each patch's own
    /// <c>SetEnabled</c> is what decides, so the key is written from what the patch
    /// ended up with rather than from what was asked for.</summary>
    static void Apply(int index)
    {
        NoDither.SetEnabled(index != Dither);
        PatchSettings.Set(NoDither.OnKey, NoDither.Enabled);

        TrueColor.SetEnabled(index == Smooth);
        PatchSettings.Set(TrueColor.OnKey, TrueColor.Enabled);
    }
}
