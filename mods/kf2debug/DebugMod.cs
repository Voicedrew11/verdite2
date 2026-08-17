// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.Debug;

/// <summary>
/// Debug tools: noclip flight, invincibility, infinite MP, a speed multiplier,
/// position bookmarks and area warp, with a live readout of the player state.
///
/// Everything here is off until it is turned on, and every hook returns on a
/// bool test plus one memory read when it is off -- the standard patches/Analog.cs and
/// patches/AutoReload.cs hold themselves to.
///
/// The state this reads is not new. NOTES.md "Player state: found, and it was in
/// stage 3 all along" and "The character's stats are buf2" between them give
/// every address in GameState.cs; what this mod adds is a place to see them and
/// a set of switches. That is also the point: inventory, equipment, magic and the
/// entity table are still unmapped, and a live readout is the instrument for
/// finding them.
/// </summary>
public sealed class DebugMod : IMod
{
    const string NoclipSpeedKey = "kf2.debug.noclip.speed";
    const string NoclipFastKey  = "kf2.debug.noclip.fast";
    const string InvertYKey     = "kf2.debug.noclip.inverty";
    const string InvertSKey     = "kf2.debug.noclip.invertstrafe";
    const string SpeedMultKey   = "kf2.debug.speed.multiplier";
    const string HotkeysKey     = "kf2.debug.hotkeys";

    // The panel has to be registered from the UI thread. On first boot OnLoad
    // runs on a worker while the main thread pumps the loading popup, so
    // registration is deferred to the first frame instead.
    static bool _panelRegistered;
    static Action<VSyncEvent>? _onVSync;

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;

        Noclip.Speed          = view.GetFloat(NoclipSpeedKey, 900f);
        Noclip.FastMultiplier = view.GetFloat(NoclipFastKey, 4f);
        Noclip.InvertVertical = view.GetBool(InvertYKey, false);
        Noclip.InvertStrafe   = view.GetBool(InvertSKey, false);
        Cheats.SpeedMultiplier = view.GetFloat(SpeedMultKey, 2f);
        Hotkeys.Enabled        = view.GetBool(HotkeysKey, true);

        ReadEnv("KF2_DEBUG_NOCLIP", ref Noclip.Enabled);
        ReadEnv("KF2_DEBUG_GODMODE", ref Cheats.Invincible);
        ReadEnv("KF2_DEBUG_INFINITEMP", ref Cheats.InfiniteMp);
        ReadEnv("KF2_DEBUG_HOTKEYS", ref Hotkeys.Enabled);
        ReadEnv("KF2_DEBUG_NOCLIP_SPEED", ref Noclip.Speed);
        ReadEnv("KF2_DEBUG_SPEED", ref Cheats.SpeedMultiplier);

        Warp.LoadPersisted();

        _onVSync = _ => RegisterPanel();
        Event.AddListener(_onVSync);

        Console.WriteLine("[kf2debug] loaded; F2 opens the panel, F3 noclip, F4 invincibility");
    }

    public void OnUnload()
    {
        if (_onVSync != null)
        {
            // Listeners are static and outlive the collectible load context, so a
            // missed removal leaks a handler into a dead assembly.
            Event.RemoveListener(_onVSync);
            _onVSync = null;
        }

        // Leave nothing switched on behind us. The speed multiplier in
        // particular is applied to a word the game rewrites every frame, so it
        // needs no restore -- but the flags do, or a reload comes back flying.
        Noclip.Reset();
        Cheats.Reset();
        Hotkeys.Reset();
        Warp.Reset();

        DebugPanel.Instance.IsOpen = false;
        _panelRegistered = false;

        Console.WriteLine("[kf2debug] unloaded");
    }

    /// <summary>
    /// Register the dockable panel and its menu entry, once, on the UI thread.
    ///
    /// Panels do not auto-populate the menu bar -- MainMenuBar declares every
    /// built-in one by hand -- so without the menu entry the panel would be
    /// reachable only by hotkey.
    /// </summary>
    static void RegisterPanel()
    {
        if (_panelRegistered) return;
        _panelRegistered = true;

        foreach (var p in PanelManager.Panels)
            if (ReferenceEquals(p, DebugPanel.Instance)) return;

        PanelManager.Register(DebugPanel.Instance);
        MenuRegistry.Menu("menu.debug", MenuRegistry.OrderDebug)
                    .Panel<DebugPanel>("KF2 Debug")
                    .End();
    }

    /// <summary>
    /// End of main-loop stage 3. One poll a frame for the hotkeys; the features
    /// themselves hook from their own files.
    /// </summary>
    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m) => Hotkeys.Poll();

    public void DrawSettings()
    {
        ImGui.TextWrapped("Noclip flight, invincibility, infinite MP, a speed multiplier, position "
                        + "bookmarks and area warp, plus a live readout of the player state. "
                        + "Everything is off until you switch it on.");
        ImGui.Separator();

        if (ImGui.Button("Open the debug panel"))
        {
            RegisterPanel();
            DebugPanel.Instance.IsOpen = true;
        }
        ImGui.SameLine();
        ImGui.TextDisabled("or press F2");

        ImGui.Separator();
        ImGui.TextWrapped("Settings that persist live here; the switches live in the panel, since "
                        + "they are things you flip while playing.");

        if (ImGui.SliderFloat("Noclip speed", ref Noclip.Speed, 100f, 4000f, "%.0f"))
            Persist(NoclipSpeedKey, Noclip.Speed);
        if (ImGui.SliderFloat("Noclip fast multiplier", ref Noclip.FastMultiplier, 1f, 10f, "x%.1f"))
            Persist(NoclipFastKey, Noclip.FastMultiplier);
        if (ImGui.SliderFloat("Speed multiplier", ref Cheats.SpeedMultiplier, 0.25f, 8f, "x%.2f"))
            Persist(SpeedMultKey, Cheats.SpeedMultiplier);

        bool invY = Noclip.InvertVertical;
        if (ImGui.Checkbox("Invert noclip up/down", ref invY))
        {
            Noclip.InvertVertical = invY;
            Persist(InvertYKey, invY);
        }

        bool invS = Noclip.InvertStrafe;
        if (ImGui.Checkbox("Invert noclip strafe", ref invS))
        {
            Noclip.InvertStrafe = invS;
            Persist(InvertSKey, invS);
        }

        bool keys = Hotkeys.Enabled;
        if (ImGui.Checkbox("Hotkeys enabled", ref keys))
        {
            Hotkeys.Enabled = keys;
            Persist(HotkeysKey, keys);
        }
    }

    static void Persist(string key, float value)
    {
        RecompOne.Runtime.Runtime.View.SetFloat(key, value);
        RecompOne.Runtime.Runtime.SaveView();
    }

    static void Persist(string key, bool value)
    {
        RecompOne.Runtime.Runtime.View.SetBool(key, value);
        RecompOne.Runtime.Runtime.SaveView();
    }

    static void ReadEnv(string name, ref bool value)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v)) return;
        v = v.Trim().ToLowerInvariant();
        value = v is "1" or "on" or "true" or "yes";
    }

    static void ReadEnv(string name, ref float value)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        if (float.TryParse(v, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float f))
            value = f;
    }
}
