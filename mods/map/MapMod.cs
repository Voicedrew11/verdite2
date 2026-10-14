// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using System.Linq;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;
using Kf2.Settings;

namespace Kf2;

/// <summary>
/// The dynamic map, as a mod. It was core (patches/Map.cs and its siblings) until
/// the port's own features outgrew it being on by default; a player who wants a
/// map turns it on in the Mods panel, and until then nothing here runs.
///
/// Its environment switches are the ones they were under Program.cs (KF2_MAP,
/// KF2_MAP_MINIMAP, KF2_MAP_PROBE, KF2_MAP_PAUSE, KF2_MAP_STYLE, KF2_MAP_SHADE,
/// KF2_MAP_WALLS, KF2_MAP_FLOOR, KF2_MAP_ARROW, KF2_MAP_MARKERS, KF2_MAP_FOG,
/// KF2_MAP_FOG_PROBE, KF2_MAP_FOG_LOS). They are read in OnLoad, so they take
/// effect only with this mod enabled.
///
/// The keys: G opens the full-screen map and Shift+G the docked readout, N the
/// minimap. M is not taken (the game's own map item, core's MapKey), and the pad
/// button defaults to R3; the touchpad is not offered at all, for the same reason.
///
/// Settings: the Gameplay page's Map combo (<see cref="MapPage"/>) is registered
/// from the mod, and the pad-button choice (<see cref="MapButtonPage"/>) goes into
/// the Input pane's gamepad tab through <see cref="InputSection.GamepadExtras"/>.
/// Both are removed again on unload.
///
/// The shell's `map` verb is added through <see cref="AgentServer.FastVerbs"/>.
///
/// Why the ready half waits for a frame: the port's RuntimeReadyEvent is dispatched
/// by Runtime.Initialize, before ModLoader.LoadAll, so a listener on it would never
/// run for a mod. The first VSync is the earliest point on the UI thread that the
/// panels, the Localization merge and the settings pages can all be set up.
/// </summary>
public sealed class MapMod : IMod
{
    static Action<VSyncEvent>? _firstFrame;

    public void OnLoad()
    {
        Map.Configure(Environment.GetEnvironmentVariable("KF2_MAP"),
                      Environment.GetEnvironmentVariable("KF2_MAP_MINIMAP"),
                      Environment.GetEnvironmentVariable("KF2_MAP_PROBE"),
                      Environment.GetEnvironmentVariable("KF2_MAP_PAUSE"),
                      Environment.GetEnvironmentVariable("KF2_MAP_STYLE"),
                      Environment.GetEnvironmentVariable("KF2_MAP_SHADE"),
                      Environment.GetEnvironmentVariable("KF2_MAP_WALLS"),
                      Environment.GetEnvironmentVariable("KF2_MAP_FLOOR"),
                      Environment.GetEnvironmentVariable("KF2_MAP_ARROW"));
        MapMarkers.Configure(Environment.GetEnvironmentVariable("KF2_MAP_MARKERS"));
        MapFog.Configure(Environment.GetEnvironmentVariable("KF2_MAP_FOG"),
                         Environment.GetEnvironmentVariable("KF2_MAP_FOG_PROBE"),
                         Environment.GetEnvironmentVariable("KF2_MAP_FOG_LOS"));

        Map.Install();
        MapFog.Install();

        AppExit.Leaving += MapFog.Flush;
        AgentServer.FastVerbs["map"] = MapVerb;

        _firstFrame = _ => OnFirstFrame();
        Event.AddListener(_firstFrame);
    }

    public void OnUnload()
    {
        if (_firstFrame != null)
        {
            Event.RemoveListener(_firstFrame);
            _firstFrame = null;
        }

        AppExit.Leaving -= MapFog.Flush;
        AgentServer.FastVerbs.TryRemove("map", out _);
        PatchSettings.Unregister("gameplay", "map");
        InputSection.GamepadExtras.RemoveAll(p => p is MapButtonPage);

        Map.Uninstall();
        MapFog.Uninstall();

        Console.WriteLine("[kf2.map] unloaded");
    }

    /// <summary>The first frame on the UI thread: the saved settings, the panels and
    /// the Gameplay and Input pages. Runs once per load.</summary>
    static void OnFirstFrame()
    {
        if (_firstFrame != null)
        {
            Event.RemoveListener(_firstFrame);
            _firstFrame = null;
        }

        Map.RunReady();
        MapFog.RunReady();

        PatchSettings.Register("gameplay", new MapPage());
        if (!InputSection.GamepadExtras.Exists(p => p is MapButtonPage))
            InputSection.GamepadExtras.Add(new MapButtonPage());
    }

    /// <summary>`map [on|off|toggle]`, on the fast queue: the full-screen map. It
    /// pauses the world unless KF2_MAP_PAUSE=0, and it is the only way a headless
    /// run can open the map, since the key and the pad need a person.</summary>
    static string MapVerb(string arg)
    {
        if (!Map.Enabled)
            return "{\"ok\":false,\"error\":\"the map is off (the Mods panel, or Gameplay > Map)\"}";

        bool open;
        switch (arg)
        {
            case "" or "toggle": open = !MapFullscreen.Instance.IsOpen; break;
            case "on" or "1": open = true; break;
            case "off" or "0": open = false; break;
            default:
                return "{\"ok\":false,\"error\":\"map: expected on, off or toggle, got '" + arg + "'\"}";
        }

        MapFullscreen.Instance.IsOpen = open;
        return "{\"ok\":true,\"cmd\":\"map\",\"open\":" + (open ? "true" : "false") +
               ",\"pauses\":" + (Map.Pause ? "true" : "false") + "}";
    }
}
