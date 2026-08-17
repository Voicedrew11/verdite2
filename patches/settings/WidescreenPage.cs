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
/// Four presets and a switch, and deliberately no slider: an arbitrary ratio is a
/// number to type, not a picture anyone wants to hunt for by dragging, and the
/// four cover what a display actually is. <c>KF2_WIDESCREEN=1.9</c> still takes
/// any value, and a value that is not one of the four shows up here as Custom
/// rather than being rounded away.
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
            ImGui.SetTooltip("Renders extra columns either side of the game's 320-pixel screen — 54 " +
                             "a side at 16:9. The projection is untouched, so nothing is stretched: " +
                             "the sides show geometry the game submitted and the GPU used to clip, " +
                             "about a quarter of what it draws in an area. A 2D screen has nothing " +
                             "out there and shows the frame's background clear instead.");

        bool anchor = Widescreen.AnchorHud;
        if (ImGui.Checkbox("Anchor the HUD to the new edges", ref anchor))
        {
            Widescreen.SetAnchorHud(anchor);
            PatchSettings.Set(Widescreen.AnchorKey, anchor);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off, the HUD keeps the 4:3 box it was authored in and sits inset from " +
                             "the sides. On, the HP/MP panel and the equipment icons move out to the " +
                             "edge they belong to. Only those two corners move, and only what the " +
                             "game draws in front of everything else — the world is never touched.");
    }
}
