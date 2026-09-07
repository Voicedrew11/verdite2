using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// The aspect ratio, under Video — **directly below the render scale**, in among
/// the section's own controls rather than in a block of the port's underneath
/// them. How big the picture is drawn and how wide it is presented are the same
/// kind of choice and belong next to each other.
///
/// That position is the one thing here that needs the checkout patched:
/// <c>SettingsRegistry.Extend</c> only draws after a section's whole body, so
/// <c>patches/recompone/0013</c> adds <c>SettingsRegistry.DrawSlot</c> and one
/// call to it inside the display section. The page is registered with
/// <c>PatchSettings.RegisterSlot("display.render_scale", …)</c> and draws bare —
/// no heading, so <see cref="Title"/> is unused.
///
/// **One combo, and deliberately no slider and no sub-options**: an arbitrary
/// ratio is a number to type, not a picture anyone wants to hunt for by dragging,
/// and the four presets cover what a display actually is.
/// <c>KF2_WIDESCREEN=1.9</c> still takes any value, and a value that is not one of
/// the four shows up here as Custom rather than being rounded away.
///
/// Three checkboxes used to sit under it and none of them was a choice. Widening
/// the game's cull cone and stretching its full-screen tints are what the rest of
/// the picture needs to be *correct* once it is wide — off, the sides fill in and
/// empty as you turn, and a death fade blacks out the middle of the screen and
/// leaves the dungeon showing either side — so both follow the aspect now, on
/// whenever one is chosen. Anchoring the HUD went the other way and is off: it is
/// the one thing widescreen does that *moves* something the game placed
/// deliberately, rather than presenting geometry the GPU used to clip, and where
/// it lands has never been looked at by eye. All three are still switchable from
/// the console — <c>KF2_WIDESCREEN_CULL=0</c>, <c>KF2_WIDESCREEN_EFFECTS=0</c>,
/// <c>KF2_WIDESCREEN_HUD=1</c> — which is where a comparison belongs.
///
/// The primitive census the mod drew in its panel is not here. It is the answer to
/// "would this scene gain anything", which is worth asking from a headless run and
/// not worth a live percentage in a graphics settings window — so it stays on the
/// console under <c>KF2_WIDESCREEN_PROBE=1</c>, exactly as the dither patch's
/// counters did. See "Widescreen" in NOTES.md.
/// </summary>
public sealed class WidescreenPage : IPatchPage
{
    public string Id => "widescreen";

    /// <summary>Unused: a slot page draws no heading.</summary>
    public string Title => "Widescreen";

    public void Draw()
    {
        var presets = Widescreen.Presets;

        int index = -1;
        for (int i = 0; i < presets.Length; i++)
            if (Math.Abs(Widescreen.Aspect - presets[i].Ratio) < 0.001f) { index = i; break; }

        // An aspect from KF2_WIDESCREEN that is none of the four keeps its own
        // entry, so opening the settings cannot silently round it to a preset.
        string[] items = index >= 0
            ? [.. presets.Select(p => p.Name)]
            : [.. presets.Select(p => p.Name), $"Custom ({Widescreen.Aspect:0.###}:1)"];
        if (index < 0) index = items.Length - 1;

        if (ImGui.Combo("Aspect", ref index, items, items.Length) && index < presets.Length)
        {
            Widescreen.SetAspect(presets[index].Ratio);
            PatchSettings.Set(Widescreen.AspectKey, Widescreen.Aspect);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How wide the picture is. Wider shows more of the room, not a stretched 4:3.");
    }
}
