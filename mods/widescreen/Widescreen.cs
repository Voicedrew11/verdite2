// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using ImGuiNET;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.Widescreen;

/// <summary>
/// Renders the game wider than its 320-pixel screen.
///
/// The runtime already knows how to do this: a display buffer is drawn into a
/// render target that carries a margin of extra columns either side
/// (<c>GpuHle.WideMargin</c>), only the original 320 are written back to VRAM, and
/// the whole widened target is presented at <c>Display.WideAspect</c>. Setting
/// that one number is the whole hookup -- 16:9 gives 54 columns a side.
///
/// What that buys depends entirely on the game, and this is the distinction worth
/// keeping straight:
///
///   * It is *not* a stretch. The projection is untouched, so pixels keep their
///     aspect and the HUD is drawn at its authored size, sitting inset from the
///     new edges rather than smeared 33% wide across them.
///   * The extra picture is real only where the game submits geometry it expects
///     the GPU to clip. Anything the game culls itself, against its own idea of
///     the screen, never reaches the margin however wide the target is.
///
/// So the mod carries its own evidence: the counter below classifies every
/// primitive by whether any vertex falls outside the game's own clip rectangle.
/// Measured in the first area (`fdat02`), it is **a quarter** -- 25.4% and 25.1%
/// of about 1,000 primitives a frame in consecutive windows, and 22-25% on the
/// title screen. King's Field culls per object and by depth and leaves the screen
/// edge to the GPU, so a quarter of every frame it draws was being thrown away at
/// x=0 and x=319. That is the picture the margin recovers, and it is why this is
/// worth doing on this game specifically.
///
/// The counter is the thing to look at if a scene looks wrong at the sides. The
/// GAME.EXE menus measure 0.0% -- they are 2D, authored 320 wide, and no aspect
/// will widen them; what the sides show there is whatever the frame's background
/// clear covered, since PutDrawEnv extends an `isbg` clear across the margin but
/// nothing extends a full-screen rectangle the game drew itself.
///
/// The listener costs a couple of array writes per primitive and the raster gates
/// it on <c>HasAnyListeners</c>, so it is off unless the panel or
/// <c>KF2_WIDESCREEN_PROBE=1</c> asks for it.
/// </summary>
public sealed class WidescreenMod : IMod
{
    const string AspectKey = "kf2.widescreen.aspect";

    static readonly (string Name, float Ratio)[] Presets =
    [
        ("4:3 (off)", 4f / 3f),
        ("16:10",     16f / 10f),
        ("16:9",      16f / 9f),
        ("21:9",      64f / 27f),
    ];

    static float _aspect = 16f / 9f;
    static bool _measure;
    static bool _toConsole;

    // Per report window, split by whether the primitive crossed into the margin.
    static long _inside, _margin;
    static double _windowStart;
    static string _report = "no primitives seen yet";

    static double Now => Environment.TickCount64 / 1000.0;

    public void OnLoad()
    {
        _aspect = Parse(Environment.GetEnvironmentVariable("KF2_WIDESCREEN"))
                  ?? RecompOne.Runtime.Runtime.View.GetFloat(AspectKey, 16f / 9f);

        Apply();
        _windowStart = Now;
        Console.WriteLine($"[widescreen] {_aspect:0.###}:1, margin {Margin()} px a side");

        // The panel is the usual way to read the counter, but the counter is also
        // the answer to "is this aspect doing anything in this scene", which is a
        // question worth being able to ask from a headless run:
        //     KF2_WIDESCREEN_PROBE=1
        if (Environment.GetEnvironmentVariable("KF2_WIDESCREEN_PROBE") is { Length: > 0 } probe &&
            !probe.Equals("0", StringComparison.Ordinal))
        {
            _toConsole = true;
            SetMeasure(true);
        }
    }

    // Leave the presentation as the runtime found it, or the picture stays wide
    // with nothing left to fill the sides.
    public void OnUnload()
    {
        Event.RemoveListener<RenderPrimEvent>(OnPrim);
        Display.WideAspect = 0f;
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped("Renders extra columns either side of the game's 320-pixel " +
                          "screen. The projection is untouched, so nothing is stretched: " +
                          "the sides show geometry the game submitted and the GPU used to clip.");
        ImGui.Separator();

        for (int i = 0; i < Presets.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (ImGui.RadioButton(Presets[i].Name, Math.Abs(_aspect - Presets[i].Ratio) < 0.001f))
            {
                _aspect = Presets[i].Ratio;
                Save();
            }
        }

        if (ImGui.SliderFloat("Aspect", ref _aspect, 4f / 3f, 3f, "%.3f:1")) Save();

        ImGui.Text($"Margin: {Margin()} px a side ({320 + Margin() * 2} px wide at 320)");

        ImGui.Separator();
        bool measure = _measure;
        if (ImGui.Checkbox("Count primitives reaching the margin", ref measure)) SetMeasure(measure);

        if (_measure)
        {
            ImGui.TextWrapped(_report);
            ImGui.TextWrapped("A scene with no margin primitives is one the game clipped " +
                              "itself; widening the target cannot recover it.");
        }
    }

    static void SetMeasure(bool on)
    {
        if (on == _measure) return;
        _measure = on;

        if (!on) { Event.RemoveListener<RenderPrimEvent>(OnPrim); return; }

        _inside = _margin = 0;
        _windowStart = Now;
        _report = "measuring...";
        Event.AddListener<RenderPrimEvent>(OnPrim);
    }

    // WideMargin is the runtime's own sizing rule, so the number the panel shows and
    // the number the render target is built with are the same one. 320 is this
    // game's display width; a buffer of another width scales the same way.
    static int Margin() => Display.WideMargin(320);

    static void Apply()
    {
        // 4:3 means off, and off is 0 rather than 1.333 -- WideMargin returns no
        // margin for a non-positive aspect, and a zero-margin target presents at
        // SourceAspect, which is the untouched path.
        Display.WideAspect = _aspect > 4f / 3f + 0.001f ? _aspect : 0f;
    }

    static void Save()
    {
        _aspect = Math.Clamp(_aspect, 4f / 3f, 3f);
        Apply();
        RecompOne.Runtime.Runtime.View.SetFloat(AspectKey, _aspect);
        RecompOne.Runtime.Runtime.SaveView();
    }

    static float? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) return 4f / 3f;

        // "16:9" and "1.777" both, since the env var is typed by hand.
        int colon = value.IndexOf(':');
        if (colon > 0)
        {
            if (float.TryParse(value[..colon], out float w) &&
                float.TryParse(value[(colon + 1)..], out float h) && h > 0f)
                return w / h;
            return null;
        }

        return float.TryParse(value, out float ratio) && ratio > 0f ? ratio : null;
    }

    // The draw area is the game's clip rect in VRAM coordinates and the primitive's
    // are already offset into the same space, so "reaches the margin" is just a
    // vertex outside the clip -- no assumption about where the display buffer sits.
    static void OnPrim(RenderPrimEvent e)
    {
        bool outside = false;
        for (int i = 0; i < e.Count; i++)
            if (e.X[i] < e.DrawLeft || e.X[i] > e.DrawRight) { outside = true; break; }

        if (outside) _margin++; else _inside++;

        double window = Now - _windowStart;
        if (window < 2.0) return;

        long total = _inside + _margin;
        _report = total == 0
            ? "no primitives in the last window"
            : $"{_margin * 100.0 / total:F1}% of {total} prims reach the margin " +
              $"({total / window:F0}/s)";
        if (_toConsole) Console.WriteLine($"[widescreen] {_report}");
        _inside = _margin = 0;
        _windowStart = Now;
    }
}
