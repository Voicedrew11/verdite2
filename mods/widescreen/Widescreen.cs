// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;
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
/// it on <c>HasAnyListeners</c>, so it is off unless HUD anchoring, the panel or
/// <c>KF2_WIDESCREEN_PROBE=1</c> asks for it.
///
/// ## Anchoring the HUD
///
/// The world gets wider; the HUD does not, because it is drawn in screen space
/// and screen space is still 320 wide. Left alone it sits inset from the new
/// edges, hugging the 4:3 box it was authored in. Anchoring moves each element to
/// the edge it belongs to -- which needs a way to tell a HUD primitive from a
/// world one, and the primitive stream carries no such flag.
///
/// Two measurements, both in `fdat02`, say what it is and what it is not:
///
///   * **Where the HUD is.** Whole-frame dumps put it in two fixed clusters --
///     x 5..91 for the HP/MP panel, x 269..310 for the equipment icons, both at
///     y 11..60 -- and always in the **last ordering-table entries**, from about
///     65 from the end. That is the front of the OT, which is where a painter's
///     algorithm has to put anything that goes on top.
///   * **What it is not: the palette.** Every HUD primitive in that first dump
///     used a CLUT in VRAM column 0, and no world primitive in that frame did,
///     which looked like a clean test and is not one: walking further into the
///     area finds world geometry using column-0 palettes all the way back through
///     the OT. Anchoring on that rule shifted half the world sideways by exactly
///     the margin -- 54-pixel wedges of missing floor and ceiling, which is what
///     the artefact looks like when a rule like this is wrong.
///
/// So the test is structural and positional, not palette-based: a primitive is
/// HUD if it is in the last <see cref="HudTailEntries"/> ordering-table entries
/// *and* its box falls inside one of the two clusters. The OT gate is why the
/// mod walks the table itself -- the primitive event cannot say which entry a
/// primitive came from. Being wrong now costs one small triangle in a corner
/// rather than the whole frame after it.
///
/// Each HUD primitive then moves to its own side: entirely left of screen centre
/// moves out by the margin, entirely right moves out the other way, and anything
/// straddling the middle is left where it is, which is where centred text and
/// full-width elements want to be.
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

    const string AnchorKey = "kf2.widescreen.anchorhud";

    static float _aspect = 16f / 9f;
    static bool _measure;
    static bool _toConsole;
    static bool _anchorHud = true;

    // The HUD measured 65 entries from the end of the ordering table; this is that
    // with room to spare, and still a thousandth of the ~9,400-entry table, so
    // world geometry has to be practically touching the camera to reach it.
    const int HudTailEntries = 128;

    // Screen-space boxes the HUD was measured in, padded. Clip-relative, so they
    // do not care where in VRAM the frame buffer sits.
    const int HudBottom = 70;
    const int HudLeftEdge = 110;      // left cluster: x 5..91
    const int HudRightEdge = 250;     // right cluster: x 269..310

    // How far the current primitive is from the end of the OT walk; -1 outside it.
    static int _fromEnd = -1;

    // Per report window, split by whether the primitive crossed into the margin.
    static long _inside, _margin;
    static double _windowStart;
    static string _report = "no primitives seen yet";

    static double Now => Environment.TickCount64 / 1000.0;

    public void OnLoad()
    {
        _aspect = Parse(Environment.GetEnvironmentVariable("KF2_WIDESCREEN"))
                  ?? RecompOne.Runtime.Runtime.View.GetFloat(AspectKey, 16f / 9f);
        _anchorHud = RecompOne.Runtime.Runtime.View.GetBool(AnchorKey, true);

        Apply();
        ApplyAnchor();
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
        _measure = false;
        _anchorHud = false;
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
        bool anchor = _anchorHud;
        if (ImGui.Checkbox("Anchor the HUD to the new edges", ref anchor))
        {
            _anchorHud = anchor;
            ApplyAnchor();
            RecompOne.Runtime.Runtime.View.SetBool(AnchorKey, _anchorHud);
            RecompOne.Runtime.Runtime.SaveView();
        }
        ImGui.TextWrapped("Off, the HUD keeps the 4:3 box it was authored in and sits " +
                          "inset from the sides. On, the HP/MP panel and the equipment " +
                          "icons move out to the edge they belong to. Only those two " +
                          "corners move, and only what the game draws in front of " +
                          "everything else -- the world is never touched.");

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

        if (!on) { Listen(); return; }

        _inside = _margin = 0;
        _windowStart = Now;
        _report = "measuring...";
        Listen();
    }

    static void ApplyAnchor() => Listen();

    // One listener serves both jobs, so it is attached exactly when one of them
    // needs it -- the raster skips the whole dispatch when nothing is listening.
    static void Listen()
    {
        Event.RemoveListener<RenderPrimEvent>(OnPrim);
        if (_measure || _anchorHud) Event.AddListener<RenderPrimEvent>(OnPrim);
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

    // The same walk libgpu's DrawOTag does, with the entries numbered. Two passes:
    // the first only follows the `next` pointers to learn the length, which is what
    // makes "how far from the end" answerable while the primitives are being
    // emitted. Everything else here mirrors the runtime's own implementation,
    // including the custom-primitive ordering an asset pack relies on.
    [Replace("open", Address = 0x80016078)]
    [Replace("game", Address = 0x80060818)]
    [Replace("end", Address = 0x80013D80)]
    static void DrawOTag(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        var gpu = RecompOne.Runtime.Runtime.Gpu;
        if (!_anchorHud || gpu == null) { orig(c, m); return; }

        uint addr = c.A0 & 0x1FFFFCu;
        int entries = 0;
        for (int guard = 0; guard < 0x100000; guard++)
        {
            uint header = m.ReadU32(addr);
            entries++;
            uint next = header & 0xFFFFFFu;
            if (next == 0xFFFFFFu || (next & 0x800000u) != 0) break;
            addr = next & 0x1FFFFCu;
        }

        bool custom = GpuPrims.Any && GpuPrims.OtLength > 0;
        uint otBase = GpuPrims.OtBase & 0x1FFFFCu;
        uint otEnd = otBase + (uint)GpuPrims.OtLength * 4u;

        addr = c.A0 & 0x1FFFFCu;
        for (int guard = 0; guard < 0x100000; guard++)
        {
            _fromEnd = entries - 1 - guard;

            if (custom && addr >= otBase && addr < otEnd)
                gpu.EmitCustomOrder((int)((addr - otBase) >> 2));

            uint header = m.ReadU32(addr);
            uint count = header >> 24;
            for (uint i = 0; i < count; i++)
                gpu.WriteGp0(m.ReadU32(addr + 4u + i * 4u));

            uint next = header & 0xFFFFFFu;
            if (next == 0xFFFFFFu || (next & 0x800000u) != 0) break;
            addr = next & 0x1FFFFCu;
        }

        _fromEnd = -1;
        if (custom) GpuPrims.Clear();
    }

    // The draw area is the game's clip rect in VRAM coordinates and the primitive's
    // are already offset into the same space, so "reaches the margin" is just a
    // vertex outside the clip -- no assumption about where the display buffer sits.
    static void OnPrim(RenderPrimEvent e)
    {
        if (_anchorHud && _fromEnd >= 0 && _fromEnd < HudTailEntries) Anchor(e);

        if (!_measure) return;

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

    // Move a HUD element out to the side of the screen it sits on. The clip
    // rectangle is the screen, so everything here is relative to it: the boxes, the
    // dividing line, and the width the margin was sized from.
    static void Anchor(RenderPrimEvent e)
    {
        int width = e.DrawRight - e.DrawLeft + 1;
        int margin = Display.WideMargin(width);
        if (margin <= 0) return;

        int lo = int.MaxValue, hi = int.MinValue, top = int.MaxValue, bottom = int.MinValue;
        for (int i = 0; i < e.Count; i++)
        {
            lo = Math.Min(lo, e.X[i]); hi = Math.Max(hi, e.X[i]);
            top = Math.Min(top, e.Y[i]); bottom = Math.Max(bottom, e.Y[i]);
        }

        lo -= e.DrawLeft; hi -= e.DrawLeft;
        top -= e.DrawTop; bottom -= e.DrawTop;
        if (top < 0 || bottom > HudBottom) return;

        // A box that crosses the middle -- centred text, a full-width bar -- has no
        // side to move to.
        int shift = hi <= HudLeftEdge ? -margin : lo >= HudRightEdge ? margin : 0;
        if (shift == 0) return;

        for (int i = 0; i < e.Count; i++) e.X[i] += shift;
    }
}
