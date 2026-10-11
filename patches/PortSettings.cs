using System.Globalization;
using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using Rt = RecompOne.Runtime.Runtime;
using static Verdite.Core.PortSetting;

namespace Kf2;

/// <summary>
/// Every port setting the game's own PORT SETTINGS page shows, declared once. See
/// docs/SETTINGS.md for the rules and what each piece does.
///
/// This list does not read the saved values at boot: each feature's own start-up
/// still does, as before, so moving a setting here changes nothing a player has.
/// What this adds at boot is a check that every declared default is what the
/// feature really starts at, which the session's reset relies on.
///
/// Verdite2's Settings window keeps its own pages (patches/settings/*Page.cs) and
/// does not draw from this list, so every row here is <see cref="Ui.None"/>.
/// </summary>
public static class PortSettings
{
    public const int MaxRows = SettingsPage.MaxRows;   // what the frame fits at the menu's spacing

    /// <summary>A switch kept as a bool, the text <c>ViewConfig.SetBool</c> writes
    /// ("True" or "False"), so a key keeps the format it had and no file is migrated.
    /// Null (the default) removes the key.</summary>
    static PortSetting Switch(string key, string env, string label, string? page, string? menu,
        Func<bool> get, Action<bool> set, bool on, string? tip = null, Func<bool>? usable = null) => new()
    {
        Key = key, Envs = [env], Label = label, Tip = tip,
        Page = page, MenuLabel = menu, Steps = [0, 1], MenuValue = v => v != 0 ? "ON" : "OFF",
        Default = on ? 1 : 0, Live = () => get() ? 1 : 0, Apply = v => set(v != 0),
        Keys = v => [(key, v is double d ? (d != 0 ? "True" : "False") : null)],
        Usable = usable, Ui = Ui.None,
    };

    /// <summary>A row of the game's page standing for several settings (see
    /// <see cref="PortSetting.Parts"/>); declared after its parts, whose defaults it reads.</summary>
    static PortSetting Combine(string key, string page, string menu, PortSetting[] parts, double[] steps,
        Func<double, string> text, Func<Func<PortSetting, double>, double> join,
        Func<double, Func<PortSetting, double>, double[]> split) => new()
    {
        Key = key, Envs = [.. parts.SelectMany(p => p.Envs).Distinct()], Label = menu,
        Page = page, MenuLabel = menu, Steps = steps, MenuValue = text,
        Parts = parts, Join = join, Split = split,
        Default = join(p => p.Default), Live = () => join(p => p.Live()),
        Apply = v =>
        {
            var values = split(v, p => p.Live());
            for (int i = 0; i < parts.Length; i++)
                if (!double.IsNaN(values[i])) parts[i].Apply(values[i]);
        },
        Ui = Ui.None,
    };

    /// <summary>A combined row's value when its parts match none of its steps (set in the
    /// Settings window or by a variable): between the first two steps, so Left and Right
    /// both reach one.</summary>
    const double Custom = 0.5;

    /// <summary>A part a combined row's step leaves where it was when the page opened.</summary>
    public const double Unchanged = double.NaN;

    // The pages, in the order the game's menu shows them, and their rows in Menu below.
    public const string DisplayPage = "DISPLAY", Graphics = "GRAPHICS", World = "WORLD", Gameplay = "GAMEPLAY";
    public static readonly string[] Pages = [DisplayPage, Graphics, World, Gameplay];

    // ---- Display ----

    public static readonly PortSetting Display = new()
    {
        Key = WindowMode.FullscreenKey, Label = "Display mode",
        Page = DisplayPage, MenuLabel = "DISPLAY", Steps = [0, 1, 2], MenuValue = Named([0, 1, 2], "WINDOWED", "FULLSCREEN", "BORDERLESS"),
        Default = 0, Live = () => WindowMode.Mode, Apply = v => WindowMode.Set((int)Math.Round(v)),
        Keys = WindowMode.Keys, Ui = Ui.None,
    };

    static readonly double[] Scales = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>The render scale, named by the lines it draws: the game's 240 times the
    /// scale. The backend takes it at the next present (0097), at most its MaxScale.
    /// The runtime's own key, kept as a float as its slider writes it.</summary>
    public static readonly PortSetting Resolution = new()
    {
        Key = "RenderScale", Label = "Render scale",
        Page = DisplayPage, MenuLabel = "RESOLUTION", Steps = Scales, MenuValue = v => Number(v * 240) + "P",
        Default = 4, Live = () => RecompOne.Runtime.Hle.GlVram.Requested is > 0 and var r ? r : RecompOne.Runtime.Hle.GlVram.Scale,
        Apply = v => RecompOne.Runtime.Hle.GlVram.Requested = (int)Math.Clamp(Math.Round(v), 1, 8),
        Stored = Stored.Float, Ui = Ui.None,
    };

    static readonly double[] Aspects = [.. Widescreen.Presets.Select(p => (double)p.Ratio)];

    /// <summary>The four presets <see cref="Widescreen.Presets"/> offers, in its order.</summary>
    public static readonly PortSetting Aspect = new()
    {
        Key = Widescreen.AspectKey, Envs = ["KF2_WIDESCREEN"], Label = "Aspect ratio",
        Page = DisplayPage, MenuLabel = "ASPECT", Steps = Aspects, MenuValue = Named(Aspects, "4/3", "16/10", "16/9", "21/9"),
        Default = Widescreen.SixteenNine, Live = () => Widescreen.Aspect, Apply = v => Widescreen.SetAspect((float)v),
        Stored = Stored.Float, Ui = Ui.None,
    };

    /// <summary>The HUD at the screen edges: the health panel and the compass moved out
    /// to the edges of a wide picture. Dimmed at 4:3, where there is no edge to move to.</summary>
    public static readonly PortSetting HudAnchor = Switch(Widescreen.HudKey, "KF2_WIDESCREEN_HUD", "HUD at the screen edges",
        DisplayPage, "HUD AT EDGES", () => Widescreen.AnchorHud, Widescreen.SetAnchorHud, false,
        tip: "Moves the health panel and the compass out to the edges of the wide picture.",
        usable: () => Widescreen.On);

    /// <summary>The frame rate. KF2 keeps one key for it, and 0 in that key is no pacing:
    /// the picture is drawn as fast as the host allows, with the world still on its tick.
    /// KF2 has no pacing that keeps the game's own frame gate (FramePacing skips it always),
    /// so that step is UNCAPPED rather than ORIGINAL.</summary>
    static readonly double[] Rates = [30, 60, 72, 75, 90, 100, 120, 144, 165, 180, 240, 360, 0];

    public static readonly PortSetting FrameRate = new()
    {
        Key = FramePacing.FpsKey, Envs = ["KF2_FPS"], Label = "Frame rate",
        Page = DisplayPage, MenuLabel = "FRAME RATE", Steps = Rates, MenuValue = v => v <= 0 ? "UNCAPPED" : Number(v),
        Default = FramePacing.DefaultFps, Live = () => Math.Max(0, FramePacing.TargetFps), Apply = v => FramePacing.SetTargetFps(v),
        Stored = Stored.Float, Ui = Ui.None,
    };

    // ---- Graphics ----

    static readonly double[] Taps = [1, 2, 4, 8, 16];

    /// <summary>The mipmaps switch, a part of <see cref="TextureFilter"/>.</summary>
    static readonly PortSetting Mipmaps = Switch(Anisotropic.MipKey, "KF2_MIPMAPS", "Mipmaps",
        null, null, () => Anisotropic.Mipmaps, Anisotropic.SetMipmaps, true);

    /// <summary>The anisotropic taps, a part of <see cref="TextureFilter"/>. One is the
    /// console's single sample; any tap count turns the mipmaps on.</summary>
    static readonly PortSetting Anisotropy = new()
    {
        Key = Anisotropic.LevelKey, Envs = ["KF2_ANISO"], Label = "Texture filtering",
        Steps = Taps, MenuValue = v => v <= 1 ? "OFF" : Number(v) + "X",
        Default = Anisotropic.Max, Live = () => Anisotropic.Level, Apply = v => Anisotropic.SetLevel((int)Math.Clamp(Math.Round(v), 1, Anisotropic.Max)),
        Stored = Stored.Int, Ui = Ui.None, Min = 1, Max = Anisotropic.Max,
    };

    static readonly double[] FilterRows = [0, 1, 2, 4, 8, 16];

    /// <summary>OFF is the console's single texel. TRILINEAR is mipmaps alone. 2X to 16X are
    /// the anisotropic kernel over the mip chain, so any of them turns the mipmaps on. Taps
    /// without mipmaps (the Settings window can still set it) show as "NO MIPMAPS", half a
    /// step below the tap count, so Left and Right each reach one.</summary>
    public static readonly PortSetting TextureFilter = Combine("row.texturefilter", Graphics, "TEXTURE FILTER", [Mipmaps, Anisotropy],
        FilterRows, v => v <= 0 ? "OFF" : v == 1 ? "TRILINEAR" : v % 1 != 0 ? Number(v + .5) + "X NO MIPMAPS" : Number(v) + "X",
        get => get(Mipmaps) != 0 ? Math.Max(1, get(Anisotropy)) : get(Anisotropy) <= 1 ? 0 : get(Anisotropy) - .5,
        (v, _) => v <= 0 ? [0, 1] : [1, v]);

    public static readonly PortSetting PerPixel = Switch(PerPixelLighting.OnKey, "KF2_PERPIXEL", "Per-pixel lighting",
        Graphics, "PER-PIXEL LIGHT", () => PerPixelLighting.Enabled, PerPixelLighting.SetEnabled, true);

    /// <summary>The on/off switch of <see cref="AmbientOcclusionRow"/>, a part of it.</summary>
    static readonly PortSetting Ao = Switch(AmbientOcclusion.OnKey, "KF2_AO", "Ambient occlusion",
        null, null, () => AmbientOcclusion.Enabled, AmbientOcclusion.SetEnabled, true);

    /// <summary>The quality of <see cref="AmbientOcclusionRow"/>, a part of it. Medium is the default.</summary>
    static readonly PortSetting AoQuality = new()
    {
        Key = AmbientOcclusion.QualityKey, Envs = ["KF2_AO_QUALITY"], Label = "Ambient occlusion quality",
        Steps = [0, 1, 2], MenuValue = Named([0, 1, 2], "LOW", "MEDIUM", "HIGH"), Names = ["Low", "Medium", "High"],
        Default = (int)AmbientOcclusion.Quality.Medium, Live = () => (int)AmbientOcclusion.CurrentQuality,
        Apply = v => AmbientOcclusion.SetQuality((AmbientOcclusion.Quality)(int)Math.Clamp(Math.Round(v), 0, 2)),
        Stored = Stored.Int, Ui = Ui.None,
    };

    /// <summary>Off, or on at one of the three qualities; OFF leaves the kept quality.</summary>
    public static readonly PortSetting AmbientOcclusionRow = Combine("row.ao", Graphics, "AMB. OCCLUSION", [Ao, AoQuality],
        [0, 1, 2, 3], v => v <= 0 ? "OFF" : AoQuality.MenuValue(v - 1),
        get => get(Ao) == 0 ? 0 : get(AoQuality) + 1,
        (v, _) => v <= 0 ? [0, Unchanged] : [1, v - 1]);

    // ---- World ----

    /// <summary>The game's far edge, the one the game draws to, and the most the
    /// setting reaches. ENHANCED is the furthest a tile can be placed (see
    /// <see cref="RenderDistance"/>), which is what the mechanism was measured at.</summary>
    public static readonly PortSetting RenderDist = new()
    {
        Key = RenderDistance.Key, Envs = ["KF2_RENDERDIST"], Label = "Render distance",
        Page = World, MenuLabel = "RENDER DISTANCE", Steps = [RenderDistance.Stock, RenderDistance.Max],
        MenuValue = v => Math.Abs(v - RenderDistance.Stock) < .01 ? "ORIGINAL" : Math.Abs(v - RenderDistance.Max) < .01 ? "ENHANCED" : Number(v),
        Default = RenderDistance.Stock, Live = () => RenderDistance.Tiles, Apply = v => RenderDistance.Set((float)v),
        Stored = Stored.Float, Ui = Ui.None,
    };

    /// <summary>The fog's distance cue (<see cref="Fog"/>): at the corners, by pixel
    /// depth, or by distance from the eye (the default). Applied at boot by Fog itself.</summary>
    public static readonly PortSetting FogMode = new()
    {
        Key = Fog.Key, Envs = ["KF2_FOG", "KF2_GPUWORLD_FOGZ"], Label = "Fog",
        Page = World, MenuLabel = "FOG", Steps = [0, 1, 2], MenuValue = Named([0, 1, 2], "CORNERS", "PIXEL DEPTH", "DISTANCE"),
        Names = ["At the corners (the game's)", "By pixel depth", "By distance from the eye"],
        Default = Fog.Default, Live = () => Fog.Mode, Apply = v => Fog.Set((int)Math.Round(v)),
        Stored = Stored.Int, Ui = Ui.None,
    };

    /// <summary>The murky water, a part of <see cref="Water"/>.</summary>
    static readonly PortSetting MurkyWater = Switch(Murk.OnKey, "KF2_MURK", "Murky water",
        null, null, () => Murk.Enabled, Murk.SetEnabled, Murk.DefaultOn);

    /// <summary>The swell and ripples of the water, a part of <see cref="Water"/>.</summary>
    static readonly PortSetting WaterWaves = Switch(Waves.OnKey, "KF2_WAVES", "Water waves",
        null, null, () => Waves.Enabled, Waves.SetEnabled, Waves.DefaultOn);

    /// <summary>The planar reflections, a part of <see cref="Water"/>: the one that costs a second view.</summary>
    static readonly PortSetting Planar = Switch(PlanarWalk.OnKey, "KF2_PLANAR", "Planar reflections",
        null, null, () => PlanarWalk.Enabled, PlanarWalk.SetEnabled, true);

    /// <summary>ENHANCED is the surface (the swell and the murk); FULL adds the reflections.
    /// Every feature is on by default, so a first run boots at FULL.</summary>
    public static readonly PortSetting Water = Combine("row.water", World, "WATER", [MurkyWater, WaterWaves, Planar],
        [0, 1, 2], v => v == 0 ? "ORIGINAL" : v == 1 ? "ENHANCED" : v == 2 ? "FULL" : "CUSTOM",
        get => (get(MurkyWater), get(WaterWaves), get(Planar)) switch
        {
            (0, 0, 0) => 0,
            (1, 1, 0) => 1,
            (1, 1, 1) => 2,
            _ => Custom,
        },
        (v, _) => v >= 2 ? [1, 1, 1] : v >= 1 ? [1, 1, 0] : [0, 0, 0]);

    // ---- Gameplay ----

    /// <summary>2D is the twin sticks, the left one walking and strafing; 3D is one
    /// stick, as tank controls are: the left stick walking and turning as the D-pad
    /// does, the right stick unused (<see cref="Analog.Tank"/>). Kept as a switch,
    /// "True" for 3D, the text the Settings window's ControlsPage writes.</summary>
    public static readonly PortSetting Controls = new()
    {
        Key = Analog.ControlsKey, Envs = ["KF2_TANK"], Label = "Controls",
        Tip = "2D: the left stick walks and strafes, the right stick turns and looks.\n" +
              "3D: one stick, as tank controls. The left stick walks forward and back and turns;\n" +
              "L1 and R1 strafe, L2 and R2 look up and down, and the right stick is not used.",
        Page = Gameplay, MenuLabel = "CONTROLS", Steps = [0, 1], MenuValue = Named([0, 1], "2D", "3D"),
        Names = ["2D (twin sticks)", "3D (one stick)"], Default = 0,
        Live = () => Analog.Tank ? 1 : 0, Apply = v => Analog.Tank = v != 0,
        Keys = v => [(Analog.ControlsKey, v is double d ? (d != 0 ? "True" : "False") : null)],
        Ui = Ui.None,
    };

    /// <summary>The right stick's look up and down reversed, as a plane's flight stick:
    /// push up to look down. The same key as Analog's "Invert look Y". Dimmed in 3D,
    /// where the right stick does not look.</summary>
    public static readonly PortSetting InvertedCamera = Switch(Analog.InvertPitchKey, "KF2_ANALOG_INVERTY", "Inverted camera",
        Gameplay, "INVERTED CAMERA", () => Analog.InvertPitch, v => Analog.InvertPitch = v, false,
        tip: "Reverses the right stick's up and down, like a plane's flight stick: push up to look down, pull back to look up.\n" +
             "2D controls only: in 3D the right stick does not look.",
        usable: () => !Analog.Tank);

    public static readonly PortSetting AutoReloadOn = Switch(AutoReload.OnKey, "KF2_AUTORELOAD", "Reload the last save on death",
        Gameplay, "RELOAD ON DEATH", () => AutoReload.Enabled, AutoReload.SetEnabled, true,
        tip: "Puts you back at your last save instead of the menus.");

    public static readonly PortSetting GearCompareOn = Switch(GearCompare.OnKey, "KF2_GEARCOMPARE", "Compare gear",
        Gameplay, "COMPARE GEAR", () => GearCompare.Enabled, v => GearCompare.Enabled = v, true,
        tip: "Choosing equipment or browsing a shop shows every stat the item would change, now and after.");

    /// <summary>Mouse look. Turning it off releases the pointer, as the game's own page does.</summary>
    public static readonly PortSetting GyroAimOn = Switch(GyroAim.OnKey, "KF2_GYROAIM", "Gyro aims a drawn bow",
        Gameplay, "GYRO", () => GyroAim.Enabled, GyroAim.SetEnabled, true,
        tip: "While a bow or the arbalest is drawn, turn and tip the pad to aim. Needs a pad with a gyroscope, such as a DualShock 4, DualSense or Switch Pro.");
    public static readonly PortSetting RumbleOn = Switch(Rumble.OnKey, "KF2_RUMBLE", "Rumble",
        null, null, () => Rumble.Enabled, Rumble.SetEnabled, true,
        tip: "The pad rumbles as you draw a bow and let the arrow fly.");
    public static readonly PortSetting RumbleHd = Switch(Rumble.HdKey, "KF2_RUMBLE_HD", "HD rumble",
        null, null, () => Rumble.Hd, Rumble.SetHd, true,
        tip: "On a Switch Pro Controller or a single Joy-Con, rumble with HD rumble's finer texture; on a DualSense plugged in by USB, with its haptics. Other pads use their two motors.",
        usable: () => Rumble.Enabled);

    /// <summary>OFF, ON (two motors) or HD; OFF leaves the kept HD choice.</summary>
    public static readonly PortSetting RumbleRow = Combine("row.rumble", Gameplay, "RUMBLE", [RumbleOn, RumbleHd],
        [0, 1, 2], v => v == 0 ? "OFF" : v == 1 ? "ON" : "HD",
        get => get(RumbleOn) == 0 ? 0 : get(RumbleHd) == 0 ? 1 : 2,
        (v, _) => v <= 0 ? [0, Unchanged] : v >= 2 ? [1, 1] : [1, 0]);

    public static readonly PortSetting MouseLook = Switch(Mouse.OnKey, "KF2_MOUSE", "Mouse look",
        null, null, () => Mouse.Enabled,
        v => { if (!v && Mouse.Enabled) Mouse.SetCaptured(false); Mouse.Enabled = v; }, true,
        tip: "Turns and looks with the mouse. The pointer has to be captured before anything happens.");
    public static readonly PortSetting MouseAimOn = Switch(MouseAim.OnKey, "KF2_MOUSE_AIM", "Mouse aim",
        null, null, () => MouseAim.Enabled,
        v => { if (!v) MouseAim.Reset(); MouseAim.Enabled = v; }, false,
        tip: "The mouse moves a reticle; you turn toward it at the game's own speed.");

    /// <summary>OFF, LOOK (the mouse turns the view) or AIM (the mouse moves a reticle
    /// and the player turns toward it); AIM turns mouse look on, and OFF leaves the
    /// kept aim choice.</summary>
    public static readonly PortSetting MouseRow = Combine("row.mouse", Gameplay, "MOUSE LOOK", [MouseLook, MouseAimOn],
        [0, 1, 2], v => v == 0 ? "OFF" : v == 1 ? "LOOK" : "AIM",
        get => get(MouseLook) == 0 ? 0 : get(MouseAimOn) == 0 ? 1 : 2,
        (v, _) => v <= 0 ? [0, Unchanged] : v >= 2 ? [1, 1] : [1, 0]);

    // ---- The game's page ----

    /// <summary>The game's page, in order. Each row is one setting or a combined row.</summary>
    public static readonly PortSetting[] Menu =
    [
        Display, Resolution, Aspect, HudAnchor, FrameRate,
        TextureFilter, PerPixel, AmbientOcclusionRow,
        RenderDist, FogMode, Water,
        Controls, InvertedCamera, AutoReloadOn, GearCompareOn, MouseRow, GyroAimOn, RumbleRow,
    ];

    /// <summary>Every setting and every combined row's part, the list the shell's
    /// <c>settings</c> verb and the start-up check read.</summary>
    public static readonly PortSetting[] All =
    [
        Display, Resolution, Aspect, HudAnchor, FrameRate,
        Mipmaps, Anisotropy, PerPixel, Ao, AoQuality,
        RenderDist, FogMode, MurkyWater, WaterWaves, Planar,
        Controls, InvertedCamera, AutoReloadOn, GearCompareOn, MouseLook, MouseAimOn, GyroAimOn, RumbleOn, RumbleHd,
    ];

    /// <summary>Every setting and every combined row, for the shell's <c>settings</c> verb.</summary>
    public static IEnumerable<PortSetting> Listed => All.Concat(Menu.Where(s => s.Parts is not null));

    public static IEnumerable<PortSetting> OnPage(string page) => Menu.Where(s => s.Page == page);

    public static PortSetting? Find(string key) =>
        Listed.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    static bool _installed;

    /// <summary>After every feature's Install, so the boot check runs after their own
    /// start-up has read the saved values.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Validate();
        SettingsStore.Install();
        Event.AddListener<RuntimeReadyEvent>(_ => CheckDefaults());
    }

    /// <summary>A mistake in the list is the programmer's, so it stops the boot.</summary>
    static void Validate() =>
        SettingsCheck.Validate(Listed, Pages, OnPage, Menu, MaxRows, SettingsPage.LabelChars, SettingsPage.ValueChars);

    /// <summary>A setting with no key and no variable must boot at its declared default,
    /// or a reset would not put back what the player had.</summary>
    static void CheckDefaults()
    {
        int kept = 0, wrong = 0;
        foreach (var s in All)
        {
            if (Rt.View.Has(s.Key)) { kept++; continue; }
            if (s.LockedBy is not null || PortSetting.Same(s.Live(), s.Default)) continue;
            wrong++;
            Console.WriteLine($"[KF2] settings: {s.Key} boots at {Number(s.Live())}, declared default {Number(s.Default)}");
        }
        Console.WriteLine($"[KF2] settings: {All.Length} declared on {Pages.Length} pages, {kept} kept in interface.ini, {wrong} default(s) wrong");
    }
}
