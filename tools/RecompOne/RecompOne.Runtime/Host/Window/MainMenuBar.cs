using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

public static class MainMenuBar
{
    private static bool _registered;

    public static void RegisterBuiltins()
    {
        if (_registered) return;
        _registered = true;

        MenuRegistry.Menu("menu.system")
            .Popup<SettingsPopup>("menu.system.settings")
            .Separator("menu.system.view_separator")
            .Check("menu.system.show_menu_bar", () => !ConfigManager.View.HideTopBar, ShowMenuBar, "F1")
            .Check("menu.system.autohide_menu_bar", () => ConfigManager.View.AutoHideMenuBar, AutoHideMenuBar)
            .Disabled()
            .Check("menu.system.fullscreen", () => ConfigManager.View.Fullscreen, Fullscreen, "F11")
            .Separator("menu.system.reset_separator")
            .Item("menu.system.hard_reset", Runtime.HardReset)
            .Separator("menu.system.quit_separator")
            .Item("menu.system.quit", static () => Environment.Exit(0));

        MenuRegistry.Menu("menu.mods").After("menu.system")
            .Popup<ModsPopup>("menu.mods.manage")
            .Item("menu.mods.hub", static () => { }).Disabled();

        MenuRegistry.Menu("menu.debug")
            .Submenu("menu.debug.gpu")
            .Panel<OutputPanel>("panel.output")
            .Panel<VramViewerPanel>("panel.vram_viewer")
            .Panel<TextureInspectorPanel>("panel.texture_inspector")
            .Separator()
            .Check("menu.debug.dump_textures",
                static () => Assets.Textures.TextureDumper.Tiles,
                static on => Assets.Textures.TextureDumper.SetTiles(on))
            .Tooltip("menu.debug.dump_textures.tooltip")
            .Check("menu.debug.dump_pages",
                static () => Assets.Textures.TextureDumper.Pages,
                static on => Assets.Textures.TextureDumper.SetPages(on))
            .Tooltip("menu.debug.dump_pages.tooltip")
            .End()
            .Submenu("menu.debug.cpu")
            .Panel<CpuStatePanel>("panel.cpu_state")
            .Separator()
            .Custom(DrawTurbo)
            .End()
            .Submenu("menu.debug.memory")
            .Panel<RamMapPanel>("panel.ram_map")
            .Panel<MemoryEditorPanel>("panel.memory_editor")
            .End()
            .Submenu("menu.debug.audio")
            .Panel<SpuViewerPanel>("panel.spu_viewer")
            .End()
            .Submenu("menu.debug.cd")
            .Panel<CdDebugPanel>("panel.cd_debug")
            .End()
            .Submenu("menu.debug.system")
            .Panel<OverlayEventsPanel>("panel.overlay_events")
            .Panel<ConsolePanel>("panel.console")
            .End()
            .Separator()
            .Item("menu.debug.reset_view", ResetView);
    }

    public static void Draw()
    {
        MenuRegistry.RightAligned = DrawFps;
        MenuRegistry.Draw();
    }

    private static void DrawTurbo()
    {
        var turbo = Interrupts.Turbo;

        if (ImGuiNET.ImGui.MenuItem("Turbo", null, turbo))
        {
            Interrupts.Turbo = !turbo;
            if (!Interrupts.Turbo) Interrupts.ResyncVBlank();
        }
    }

    private static void DrawFps()
    {
        if (!ConfigManager.View.ShowFps) return;

        var text = $"{FrameClock.PresentFps:F0} / {FrameClock.Fps:F0} fps";
        var width = ImGuiNET.ImGui.CalcTextSize(text).X;
        ImGuiNET.ImGui.SameLine(ImGuiNET.ImGui.GetWindowWidth() - width -
                                ImGuiNET.ImGui.GetStyle().FramePadding.X * 2f);
        ImGuiNET.ImGui.TextUnformatted(text);
    }


    private static void ResetView()
    {
        ConfigManager.ResetView(PanelManager.Panels);

        // ResetView puts a default ViewConfig back, but nothing reads the scale or
        // the colours *out of* the config after startup: the UI scale lives on in
        // io.FontGlobalScale and in the sizes ScaleAllSizes baked into the style,
        // and the accent and background live on in Theme's own fields. Popups take
        // Theme.Scale live and shrink the same frame, so without this the reset
        // lands half-done -- small windows, giant text -- and the one thing a
        // person reaches for this item to undo is the one thing left behind.
        //
        // This is the escape hatch from a scale too large to work in, so it has to
        // finish the job: it is reachable from the menu bar, which is anchored at
        // the top-left corner and is the last thing to leave the window.
        ImGui.GetIO().FontGlobalScale = ConfigManager.View.UiScale;
        Theme.Load();

        HostWindow.RequestLayout();
    }

    private static void ShowMenuBar(bool show)
    {
        ConfigManager.View.HideTopBar = !show;
        ConfigManager.SaveView(PanelManager.Panels);
    }

    private static void AutoHideMenuBar(bool autoHide)
    {
        ConfigManager.View.AutoHideMenuBar = autoHide;
        ConfigManager.SaveView(PanelManager.Panels);
    }

    private static void Fullscreen(bool fullscreen)
    {
        ConfigManager.View.Fullscreen = fullscreen;
        HostWindow.SetFullscreen(fullscreen);
        ConfigManager.SaveView(PanelManager.Panels);
    }
}