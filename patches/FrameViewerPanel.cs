using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Window;
using Silk.NET.OpenGL;
using PsxGpu = RecompOne.Runtime.Gpu;

namespace Kf2;

/// <summary>
/// The frame viewer's window: Shift+F. Capture one run of stage 13, then scrub it a
/// command at a time -- the picture as it stood after each one, who emitted it,
/// what it cost, and the batch submits it forced. See <see cref="FrameCapture"/>.
/// </summary>
public sealed class FrameViewerPanel : IPanel
{
    public static readonly FrameViewerPanel Instance = new();
    FrameViewerPanel() { }

    public string Name => "kf2frameviewer";
    public string TitleKey => "kf2.frameviewer";
    public bool IsOpen { get; set; }

    enum Mode { Picture, Overdraw, Batches, Routines, Recovery, GpuCost }
    static readonly string[] ModeNames = ["Picture", "Overdraw", "Batches", "Routines", "Vertex recovery", "GPU cost"];

    readonly Replayer _replay = new();
    Capture? _cap, _base;
    bool _gpuSeen;
    int _cursor = -1;
    bool _checker = true;
    Mode _mode;
    int _selSlot = int.MinValue;
    bool _onlySel;
    bool _playing;
    float _speed = 300f;
    double _playAcc;
    float _zoom = 2f;
    string _status = "";
    string _fnInput = "game:80032588";

    // What the texture was last built from.
    Capture? _texCap;
    int _texCursor = int.MinValue, _texSel = int.MinValue;
    Mode _texMode;
    bool _texChecker;
    uint _tex;
    int _texW, _texH;
    byte[] _rgba = [];

    public void Draw()
    {
        var open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(980, 820), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(this.Title(), ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }
        IsOpen = open;

        if (FrameCapture.Latest != null && FrameCapture.Latest != _cap) Load(FrameCapture.Latest);
        if (_cap != null && _cap.GpuFinal != _gpuSeen) Load(_cap, true);

        DrawToolbar();
        if (_cap == null)
        {
            ImGui.TextWrapped("No capture yet. Capture takes the next run of stage 13 (the renderer), " +
                              "so the game has to be in GAME.EXE.");
            ImGui.End();
            return;
        }

        DrawSummary();
        if (ImGui.CollapsingHeader("Stage 13 timeline", ImGuiTreeNodeFlags.DefaultOpen)) DrawTimeline();
        if (ImGui.CollapsingHeader("Frame", ImGuiTreeNodeFlags.DefaultOpen)) DrawFrame();
        if (ImGui.CollapsingHeader("Routines", ImGuiTreeNodeFlags.DefaultOpen)) DrawRoutines();
        if (ImGui.CollapsingHeader("Runtime sections", ImGuiTreeNodeFlags.DefaultOpen)) DrawSections();
        if (ImGui.CollapsingHeader("Commands")) DrawCommands();
        if (ImGui.CollapsingHeader("Batch submits")) DrawFlushes();
        ImGui.End();
    }

    void Load(Capture cap, bool refresh = false)
    {
        _gpuSeen = cap.GpuFinal;
        _summary = [.. cap.Summary()];
        _texCap = null;
        _detailCap = null;
        if (!refresh)
        {
            _cap = cap;
            _cursor = cap.Cmds.Length - 1;
            _playing = false;
            _tlStart = 0;
            _tlEnd = cap.Ms;
        }
        _cmdRows = null;
        _sliderFormat = $"command %d of {cap.Cmds.Length}";
        BuildRoutineRows();
    }

    string _sliderFormat = "";

    // ---- toolbar --------------------------------------------------------------------------

    void DrawToolbar()
    {
        if (FrameCapture.Armed)
        {
            ImGui.BeginDisabled();
            ImGui.Button("Waiting for stage 13...");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("Capture next frame"))
            _status = FrameCapture.Arm();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Record the next run of stage 13: every GP0 command, every routine call and every GL batch " +
                             "submit. Hooks each routine for the rest of the session the first time.");

        ImGui.SameLine();
        ImGui.Checkbox("Hold the vertex map off", ref FrameCapture.HoldMapOff);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Capture with the GTE vertex map switched off for that one frame. Its cost lives in the " +
                             "game's own lw/sw, inside every routine's self time, so this is how to measure it: set a " +
                             "capture with the map on as the baseline, capture again with this ticked, and read the delta " +
                             "columns. The held frame draws affine and with no recovered depth.");

        ImGui.SameLine();
        ImGui.BeginDisabled(_cap == null);
        if (ImGui.Button(_base != null && _base == _cap ? "Baseline (this)" : "Set as baseline") && _cap != null)
        {
            _base = _base == _cap ? null : _cap;
            BuildRoutineRows();
            _sectionRows = null;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Compare later captures against this one: the routines and sections tables gain a delta " +
                             "column. One frame is noisy; repeat a capture before trusting a small delta.");
        ImGui.SameLine();
        if (ImGui.Button("Save CSV") && _cap != null)
        {
            var path = $"framecapture-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            try
            {
                _cap.WriteAllCsv(path);
                _status = $"wrote {Path.GetFullPath(path)}";
            }
            catch (Exception e)
            {
                _status = $"could not write {path}: {e.Message}";
            }
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        ImGui.InputTextWithHint("##kf2fvfn", "game:80032588", ref _fnInput, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add routine")) _status = FrameCapture.AddFunction(_fnInput);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Break a routine down further: hook a GAME.EXE function so its calls appear on the " +
                             "timeline and the packets it bumps the arena by are credited to it. Takes effect from " +
                             $"the next capture; {FrameCapture.MaxSlots} routines at most.");

        if (_status.Length > 0) ImGui.TextWrapped(_status);
    }

    string[] _summary = [];

    void DrawSummary()
    {
        foreach (var line in _summary) ImGui.TextWrapped(line.TrimStart());
        if (_base != null && _base != _cap)
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1), $"baseline: capture {_base.Number}, stage 13 {_base.Ms:0.00} ms " +
                              $"({_cap!.Ms - _base.Ms:+0.00;-0.00} ms); {_base.State}");
        if (_cap!.Truncated)
            ImGui.TextColored(new Vector4(1, 0.4f, 0.3f, 1), "The capture ran out of room; commands after that point are missing.");
    }

    // ---- colours --------------------------------------------------------------------------

    static Vector3 Hue(int n)
    {
        var h = (n * 0.618034f) % 1f;
        ImGui.ColorConvertHSVtoRGB(h < 0 ? h + 1 : h, 0.62f, 0.95f, out var r, out var g, out var b);
        return new Vector3(r, g, b);
    }

    static uint SlotColour(int slot, float alpha = 1f)
    {
        var c = slot < 0 ? new Vector3(0.55f, 0.55f, 0.55f) : Hue(slot + 3);
        return ImGui.GetColorU32(new Vector4(c, alpha));
    }

    int CmdSlot(int i) => _cap!.Cmds[i].Owner >= 0 ? _cap.Calls[_cap.Cmds[i].Owner].Slot : -1;

    int _selSection = -1;

    static uint GroupColour(ProfileGroup g, float alpha) => ImGui.GetColorU32(g switch
    {
        ProfileGroup.Game => new Vector4(0.55f, 0.75f, 0.95f, alpha),
        ProfileGroup.Hook => new Vector4(0.95f, 0.75f, 0.35f, alpha),
        ProfileGroup.Runtime => new Vector4(0.55f, 0.90f, 0.55f, alpha),
        ProfileGroup.Gpu => new Vector4(0.95f, 0.45f, 0.45f, alpha),
        _ => new Vector4(0.6f, 0.6f, 0.6f, alpha),
    });

    // ---- timeline -------------------------------------------------------------------------

    double _tlStart, _tlEnd;

    void DrawTimeline()
    {
        var cap = _cap!;
        if (ImGui.SmallButton("Fit stage 13")) (_tlStart, _tlEnd) = (0, cap.Ms);
        ImGui.SameLine();
        if (ImGui.SmallButton("Fit commands") && cap.Cmds.Length > 0)
            (_tlStart, _tlEnd) = (cap.RelMs(cap.Cmds[0].Start), cap.RelMs(cap.Cmds[^1].End));
        ImGui.SameLine();
        if (ImGui.SmallButton("Fit DrawOTag"))
            foreach (var call in cap.Calls)
                if (FrameCapture.SlotName[call.Slot].StartsWith("DrawOTag"))
                {
                    (_tlStart, _tlEnd) = (cap.RelMs(call.Enter), cap.RelMs(call.Leave));
                    break;
                }
        ImGui.SameLine();
        ImGui.TextDisabled($"{_tlStart:0.000} - {_tlEnd:0.000} ms   wheel zooms, right-drag pans, click a call to select its routine. " +
                           "Below the line: runtime sections (green runtime, amber hooks, red driver, grey waits)");

        var depth = 0;
        foreach (var call in cap.Calls) depth = Math.Max(depth, call.Depth);
        var secDepth = -1;
        foreach (var sec in cap.Sections) secDepth = Math.Max(secDepth, sec.Depth);
        const float rowH = 17f, cmdH = 14f, flushH = 8f, secH = 13f;
        var width = ImGui.GetContentRegionAvail().X;
        var secTop = (depth + 1) * rowH + cmdH + flushH + 8;
        var height = secTop + (secDepth + 1) * secH + 2;
        var p0 = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        ImGui.InvisibleButton("##kf2fvtimeline", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        dl.AddRectFilled(p0, p0 + new Vector2(width, height), ImGui.GetColorU32(ImGuiCol.FrameBg));
        dl.PushClipRect(p0, p0 + new Vector2(width, height), true);

        var span = Math.Max(1e-6, _tlEnd - _tlStart);
        float X(long t) => p0.X + (float)((cap.RelMs(t) - _tlStart) / span * width);
        var io = ImGui.GetIO();
        var mouseMs = _tlStart + (io.MousePos.X - p0.X) / width * span;

        if (hovered && io.MouseWheel != 0)
        {
            var k = io.MouseWheel > 0 ? 1 / 1.25 : 1.25;
            _tlStart = mouseMs - (mouseMs - _tlStart) * k;
            _tlEnd = mouseMs + (_tlEnd - mouseMs) * k;
        }
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Right))
        {
            var d = ImGui.GetMouseDragDelta(ImGuiMouseButton.Right).X / width * span;
            _tlStart -= d;
            _tlEnd -= d;
            ImGui.ResetMouseDragDelta(ImGuiMouseButton.Right);
        }

        var hoverCall = -1;
        for (var i = 0; i < cap.Calls.Length; i++)
        {
            ref var call = ref cap.Calls[i];
            float x0 = X(call.Enter), x1 = Math.Max(X(call.Leave), x0 + 1);
            if (x1 < p0.X || x0 > p0.X + width) continue;
            var y0 = p0.Y + call.Depth * rowH;
            var a = new Vector2(x0, y0 + 1);
            var b = new Vector2(x1, y0 + rowH - 1);
            dl.AddRectFilled(a, b, SlotColour(call.Slot, 0.85f));
            if (call.Slot == _selSlot) dl.AddRect(a, b, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), 0, 0, 2f);
            var name = FrameCapture.SlotName[call.Slot];
            if (x1 - x0 > ImGui.CalcTextSize(name).X + 6)
                dl.AddText(new Vector2(Math.Max(x0, p0.X) + 3, y0 + 1), ImGui.GetColorU32(new Vector4(0, 0, 0, 1)), name);
            if (hovered && io.MousePos.Y >= y0 && io.MousePos.Y < y0 + rowH && io.MousePos.X >= x0 && io.MousePos.X <= x1)
                hoverCall = i;
        }

        // Commands, one tick each, merged where several land on the same pixel column.
        var cy = p0.Y + (depth + 1) * rowH + 2;
        var lastX = float.MinValue;
        var lastSlot = int.MinValue;
        for (var i = 0; i < cap.Cmds.Length; i++)
        {
            var x = MathF.Floor(X(cap.Cmds[i].Start));
            if (x < p0.X || x > p0.X + width) continue;
            var slot = CmdSlot(i);
            if (x == lastX && slot == lastSlot) continue;
            (lastX, lastSlot) = (x, slot);
            dl.AddLine(new Vector2(x, cy), new Vector2(x, cy + cmdH), SlotColour(slot));
        }
        var fy = cy + cmdH + 2;
        foreach (var f in cap.Flushes)
        {
            var x = X(f.Start);
            if (x >= p0.X && x <= p0.X + width)
                dl.AddLine(new Vector2(x, fy), new Vector2(x, fy + flushH), ImGui.GetColorU32(new Vector4(1, 0.25f, 0.25f, 1)), 2f);
        }
        // Runtime sections: every profiler section inside the frame, merged where
        // several at one depth land on the same pixel.
        var hoverSec = -1;
        var sy = p0.Y + secTop;
        dl.AddLine(new Vector2(p0.X, sy - 3), new Vector2(p0.X + width, sy - 3), ImGui.GetColorU32(ImGuiCol.Separator));
        Span<float> lastSecX = stackalloc float[Math.Max(1, secDepth + 1)];
        lastSecX.Fill(float.MinValue);
        for (var i = 0; i < cap.Sections.Length; i++)
        {
            ref var sec = ref cap.Sections[i];
            float x0 = X(sec.Enter), x1 = Math.Max(X(sec.Leave), x0 + 1);
            if (x1 < p0.X || x0 > p0.X + width) continue;
            var y0 = sy + sec.Depth * secH;
            if (hovered && io.MousePos.Y >= y0 && io.MousePos.Y < y0 + secH && io.MousePos.X >= x0 - 1 && io.MousePos.X <= x1 + 1)
                hoverSec = i;
            if (x1 - x0 < 2 && MathF.Floor(x0) == lastSecX[sec.Depth]) continue;
            lastSecX[sec.Depth] = MathF.Floor(x0);
            var a = new Vector2(x0, y0 + 1);
            var b = new Vector2(x1, y0 + secH - 1);
            dl.AddRectFilled(a, b, GroupColour(Profiler.Group(sec.Id), 0.8f));
            if (sec.Id == _selSection) dl.AddRect(a, b, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), 0, 0, 1.5f);
            var name = Profiler.Name(sec.Id);
            if (x1 - x0 > ImGui.CalcTextSize(name).X * 0.8f + 6)
                dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * 0.8f, new Vector2(Math.Max(x0, p0.X) + 3, y0),
                    ImGui.GetColorU32(new Vector4(0, 0, 0, 1)), name);
        }

        if (_cursor >= 0)
        {
            var x = X(cap.Cmds[_cursor].Start);
            dl.AddLine(new Vector2(x, p0.Y), new Vector2(x, p0.Y + height), ImGui.GetColorU32(new Vector4(1, 0.9f, 0.2f, 1)), 1.5f);
        }
        dl.PopClipRect();

        if (hoverSec >= 0)
        {
            ref var sec = ref cap.Sections[hoverSec];
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(Capture.SectionName(sec.Id));
            ImGui.TextDisabled(Profiler.Group(sec.Id).ToString());
            ImGui.Text($"at {cap.RelMs(sec.Enter):0.000} ms, {sec.Incl * Capture.TicksToMs:0.000} ms inclusive, " +
                       $"{sec.Self * Capture.TicksToMs:0.000} ms self");
            var gpu = cap.SectionGpuNs(sec);
            if (gpu >= 0) ImGui.Text($"GPU {gpu / 1e6:0.000} ms");
            else if (sec.Flush >= 0 || sec.Work >= 0)
                ImGui.TextDisabled(cap.GpuFinal ? "GPU: no answer" : "GPU: waiting for the GPU to answer");
            if (sec.Flush >= 0) ImGui.TextDisabled($"submit #{sec.Flush}: {Capture.ReasonLabel(cap.Flushes[sec.Flush].Why)}");
            if (sec.Work >= 0 && cap.Works[sec.Work].What == GpuWork.AmbientOcclusion || sec.Id == Profiler.Display)
                ImGui.TextDisabled("This present shows the previous frame's picture; this frame's is in the summary's GPU line.");
            ImGui.EndTooltip();
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _selSection = _selSection == sec.Id ? -1 : sec.Id;
            return;
        }

        if (hoverCall >= 0)
        {
            ref var call = ref cap.Calls[hoverCall];
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(FrameCapture.SlotLabel(call.Slot));
            ImGui.Text($"at {cap.RelMs(call.Enter):0.000} ms, {call.Incl * Capture.TicksToMs:0.000} ms inclusive, " +
                       $"{call.Self * Capture.TicksToMs:0.000} ms self");
            ImGui.Text($"{call.Packets} primitives owned, {call.WalkTicks * Capture.TicksToMs * 1000:0} us to send them, " +
                       $"{call.Frags} fragments, {call.Flushes} submits forced");
            if (call.RangeHi > call.RangeLo) ImGui.TextDisabled($"arena 0x{call.RangeLo:X6}-0x{call.RangeHi:X6}, {call.RangeHi - call.RangeLo} bytes");
            ImGui.EndTooltip();
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) _selSlot = _selSlot == call.Slot ? int.MinValue : call.Slot;
        }
        else if (hovered && io.MousePos.Y >= cy && io.MousePos.Y < sy - 3 && cap.Cmds.Length > 0)
        {
            var near = NearestCommand(mouseMs);
            ImGui.SetTooltip($"#{near} {cap.KindLabel(cap.Cmds[near])} -- {cap.OwnerLabel(cap.Cmds[near])}\nclick to scrub here");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) Seek(near);
        }
    }

    int NearestCommand(double ms)
    {
        var cap = _cap!;
        int lo = 0, hi = cap.Cmds.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (cap.RelMs(cap.Cmds[mid].Start) < ms) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ---- the frame ------------------------------------------------------------------------

    void Seek(int i)
    {
        _cursor = Math.Clamp(i, -1, _cap!.Cmds.Length - 1);
    }

    int Find(int from, int dir, Func<int, bool> match)
    {
        for (var i = from + dir; i >= 0 && i < _cap!.Cmds.Length; i += dir)
            if (match(i)) return i;
        return from;
    }

    void DrawFrame()
    {
        var cap = _cap!;
        var n = cap.Cmds.Length;

        if (_playing)
        {
            _playAcc += ImGui.GetIO().DeltaTime * _speed;
            var steps = (int)_playAcc;
            _playAcc -= steps;
            if (steps > 0) Seek(_cursor + steps);
            if (_cursor >= n - 1) _playing = false;
        }

        if (ImGui.Button("|<")) Seek(-1);
        ImGui.SameLine();
        if (ImGui.ArrowButton("##kf2fvprev", ImGuiDir.Left)) Seek(_cursor - 1);
        ImGui.SameLine();
        if (ImGui.ArrowButton("##kf2fvnext", ImGuiDir.Right)) Seek(_cursor + 1);
        ImGui.SameLine();
        if (ImGui.Button(">|")) Seek(n - 1);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(120, ImGui.GetContentRegionAvail().X - 520));
        var c = _cursor;
        if (ImGui.SliderInt("##kf2fvcursor", ref c, -1, n - 1, c < 0 ? "before the first command" : _sliderFormat)) Seek(c);
        ImGui.SameLine();
        if (ImGui.Checkbox("Play", ref _playing) && _playing && _cursor >= n - 1) Seek(-1);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.SliderFloat("##kf2fvspeed", ref _speed, 10f, 5000f, "%.0f cmd/s", ImGuiSliderFlags.Logarithmic);
        ImGui.SameLine();
        if (ImGui.Button("< submit")) Seek(Find(_cursor, -1, i => cap.Cmds[i].Flushes > 0));
        ImGui.SameLine();
        if (ImGui.Button("submit >")) Seek(Find(_cursor, 1, i => cap.Cmds[i].Flushes > 0));
        if (_selSlot != int.MinValue)
        {
            ImGui.SameLine();
            if (ImGui.Button("< routine")) Seek(Find(_cursor, -1, i => CmdSlot(i) == _selSlot));
            ImGui.SameLine();
            if (ImGui.Button("routine >")) Seek(Find(_cursor, 1, i => CmdSlot(i) == _selSlot));
        }

        var mode = (int)_mode;
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("##kf2fvmode", ref mode, ModeNames, ModeNames.Length)) _mode = (Mode)mode;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Picture: VRAM after the command (a selected routine's pixels at full brightness).\n" +
                             "Overdraw: fragments per pixel so far, including transparent texels.\n" +
                             "Batches: which GL batch last wrote each pixel.\n" +
                             "Routines: which routine last wrote each pixel.\n" +
                             "Vertex recovery: whether the last primitive at each pixel recovered its vertices' depth and " +
                             "sub-pixel position -- green all, amber some, red none, blue never asked (perspective, sub-pixel " +
                             "and the depth features all off, or untextured with none wanting depth).\n" +
                             "GPU cost: the last primitive's share of its batch's GPU time per fragment, white the most.");
        ImGui.SameLine();
        ImGui.Checkbox("Checker under the frame", ref _checker);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Start from a checker instead of the picture the buffer held, so pixels this frame never " +
                             "covers stand out.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.SliderFloat("zoom", ref _zoom, 1f, 4f, "%.1fx");
        if (_selSlot != int.MinValue)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(Hue(_selSlot + 3), 1), $"selected: {(_selSlot < 0 ? "(no routine)" : FrameCapture.SlotName[_selSlot])}");
            ImGui.SameLine();
            if (ImGui.SmallButton("clear")) _selSlot = int.MinValue;
        }

        _replay.Seek(cap, _cursor, _checker);
        UpdateTexture();

        var imgSize = new Vector2(cap.ViewW * _zoom, cap.ViewH * _zoom);
        ImGui.BeginGroup();
        if (_tex != 0) ImGui.Image((nint)_tex, imgSize);
        else ImGui.Dummy(imgSize);
        var min = ImGui.GetItemRectMin();
        var imgHovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();

        Span<(int X, int Y)> v = stackalloc (int, int)[16];
        Vector2 P((int X, int Y) p) => min + new Vector2((p.X - cap.ViewL) * _zoom, (p.Y - cap.ViewT) * _zoom);

        dl.PushClipRect(min, min + imgSize, true);
        if (_selSlot != int.MinValue && _mode == Mode.Picture)
            for (var i = 0; i <= _cursor; i++)
                if (CmdSlot(i) == _selSlot) Outline(dl, i, v, SlotColour(_selSlot, 0.35f), 1f);
        if (_cursor >= 0) Outline(dl, _cursor, v, ImGui.GetColorU32(new Vector4(1, 0.9f, 0.2f, 1)), 2f);
        dl.PopClipRect();

        if (imgHovered)
        {
            var px = (ImGui.GetIO().MousePos - min) / _zoom;
            int x = cap.ViewL + (int)px.X, y = cap.ViewT + (int)px.Y;
            if (x >= 0 && y >= 0 && x < PsxGpu.VramWidth && y < PsxGpu.VramHeight)
            {
                var idx = y * PsxGpu.VramWidth + x;
                var owner = _replay.Owner[idx];
                ImGui.BeginTooltip();
                ImGui.Text($"({x},{y}): {_replay.Coverage[idx]} fragment(s) so far");
                if (owner >= 0)
                {
                    ImGui.Text($"last written by #{owner}: {cap.KindLabel(cap.Cmds[owner])}");
                    ImGui.Text(cap.OwnerLabel(cap.Cmds[owner]));
                    ImGui.TextDisabled("click to scrub to it");
                }
                else ImGui.TextDisabled("not written by this frame yet");
                ImGui.EndTooltip();
                if (owner >= 0 && ImGui.IsItemClicked()) Seek(owner);
            }
        }
        ImGui.EndGroup();

        ImGui.SameLine();
        ImGui.BeginChild("##kf2fvdetail", new Vector2(0, imgSize.Y), ImGuiChildFlags.None);
        DrawDetail(v);
        ImGui.EndChild();
    }

    void Outline(ImDrawListPtr dl, int i, Span<(int X, int Y)> v, uint colour, float thickness)
    {
        var cap = _cap!;
        var n = cap.Vertices(i, v);
        if (n < 2) return;
        Vector2 P((int X, int Y) p) => ImGui.GetItemRectMin() + new Vector2((p.X - cap.ViewL + 0.5f) * _zoom, (p.Y - cap.ViewT + 0.5f) * _zoom);
        if (cap.Cmds[i].Kind == CmdKind.Poly && n == 4)
        {
            // Packet order is a strip: 0-1-3-2 walks the outside.
            dl.AddLine(P(v[0]), P(v[1]), colour, thickness);
            dl.AddLine(P(v[1]), P(v[3]), colour, thickness);
            dl.AddLine(P(v[3]), P(v[2]), colour, thickness);
            dl.AddLine(P(v[2]), P(v[0]), colour, thickness);
            return;
        }
        var closed = cap.Cmds[i].Kind != CmdKind.Line;
        for (var k = 0; k + 1 < n; k++) dl.AddLine(P(v[k]), P(v[k + 1]), colour, thickness);
        if (closed && n > 2) dl.AddLine(P(v[n - 1]), P(v[0]), colour, thickness);
    }

    // The detail pane's lines, rebuilt when the cursor moves rather than every frame.
    readonly List<(string Text, Vector4 Colour)> _detail = new();
    Capture? _detailCap;
    int _detailCursor = int.MinValue, _ownerLine = -1;
    string _fragsText = "";
    long _fragsShown = -1;

    static readonly Vector4 Plain = new(-1);
    static readonly Vector4 Dim = new(-2);

    void DrawDetail(Span<(int X, int Y)> v)
    {
        var cap = _cap!;
        if (_replay.Gpu.Fragments != _fragsShown)
        {
            _fragsShown = _replay.Gpu.Fragments;
            _fragsText = $"fragments so far: {_fragsShown}";
        }
        ImGui.TextUnformatted(_fragsText);
        if (_cursor < 0)
        {
            ImGui.TextDisabled("Before the first command: the buffer as the frame found it.");
            return;
        }

        if (_detailCap != cap || _detailCursor != _cursor) BuildDetail(v);
        ImGui.Separator();
        for (var i = 0; i < _detail.Count; i++)
        {
            var (text, colour) = _detail[i];
            if (colour == Plain) ImGui.TextUnformatted(text);
            else if (colour == Dim) ImGui.TextDisabled(text);
            else ImGui.TextColored(colour, text);
            if (i == _ownerLine && ImGui.IsItemHovered())
                ImGui.SetTooltip("The routine whose arena bump holds this packet. \"by time\" means no hooked routine's " +
                                 "bump holds it, and it is credited to whatever was running when it was sent instead.");
        }
    }

    void BuildDetail(Span<(int X, int Y)> v)
    {
        var cap = _cap!;
        (_detailCap, _detailCursor) = (cap, _cursor);
        _detail.Clear();
        ref var c = ref cap.Cmds[_cursor];
        _detail.Add(($"#{_cursor}  {cap.KindLabel(c)}", Plain));
        _ownerLine = _detail.Count;
        _detail.Add((cap.OwnerLabel(c), new Vector4(Hue(CmdSlot(_cursor) + 3), 1)));
        if (c.RunIn >= 0) _detail.Add(($"sent during: {FrameCapture.SlotName[cap.Calls[c.RunIn].Slot]}", Plain));
        _detail.Add(($"at {cap.RelMs(c.Start):0.0000} ms, cost {c.Ticks * Capture.TicksToMs * 1000:0.0} us", Plain));
        _detail.Add(($"fragments: {c.Frags}", Plain));
        if (c.VtxAsked > 0)
            _detail.Add(($"vertices recovered: {c.VtxHits} of {c.VtxAsked}, lookup {c.LookupTicks * Capture.TicksToMs * 1000:0.0} us",
                new Vector4(Recovery(c), 1)));
        _detail.Add(($"GL batch {c.Batch}" + (c.HasGpu ? $", GPU share {c.GpuNs / 1e3:0.0} us" : cap.GpuFinal ? "" : ", GPU pending"), Plain));
        if (c.Flushes > 0)
            _detail.Add(($"forced {c.Flushes} submit(s): {Capture.ReasonLabel(c.Reason)}", new Vector4(1, 0.45f, 0.4f, 1)));
        if (c.OtEntry >= 0) _detail.Add(($"ordering table: entry {c.OtEntry}, slot {c.OtSlot}", Plain));
        _detail.Add((c.Src != 0 ? $"packet at 0x{c.Src - 4:X6}" : "written to GP0 directly", Plain));
        _detail.Add(($"clip ({c.ClipL},{c.ClipT})-({c.ClipR},{c.ClipB}), offset ({c.OffX},{c.OffY})", Plain));
        var n = cap.Vertices(_cursor, v);
        for (var k = 0; k < n; k++) _detail.Add(($"  v{k} ({v[k].X}, {v[k].Y})", Dim));
        _detail.Add((string.Join(" ", cap.Words.Skip(c.First).Take(Math.Min(c.Count, 12)).Select(w => w.ToString("X8"))) +
                     (c.Count > 12 ? " ..." : ""), Dim));
    }

    void UpdateTexture()
    {
        var cap = _cap!;
        if (_texCap == cap && _texCursor == _cursor && _texMode == _mode && _texSel == _selSlot && _texChecker == _checker && _tex != 0)
            return;
        var gl = GpuGlAccess.Gl;
        if (gl == null) return;
        (_texCap, _texCursor, _texMode, _texSel, _texChecker) = (cap, _cursor, _mode, _selSlot, _checker);

        int w = cap.ViewW, h = cap.ViewH;
        if (_rgba.Length < w * h * 4) _rgba = new byte[w * h * 4];
        _gpuPerFragMax = 0;
        if (_mode == Mode.GpuCost)
            foreach (var c in cap.Cmds)
                if (c.HasGpu && c.Frags > 0) _gpuPerFragMax = Math.Max(_gpuPerFragMax, c.GpuNs / (double)c.Frags);
        var vram = _replay.Gpu.Vram;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var idx = (cap.ViewT + y) * PsxGpu.VramWidth + cap.ViewL + x;
            var o = (y * w + x) * 4;
            var owner = _replay.Owner[idx];
            Vector3 rgb;
            switch (_mode)
            {
                case Mode.Overdraw:
                    rgb = Heat(_replay.Coverage[idx]);
                    break;
                case Mode.Batches:
                    rgb = owner >= 0 ? Hue(cap.Cmds[owner].Batch) : Pixel(vram[idx]) * 0.25f;
                    break;
                case Mode.Recovery:
                    rgb = owner < 0 ? Pixel(vram[idx]) * 0.25f : Recovery(cap.Cmds[owner]) * (0.55f + 0.45f * Luma(vram[idx]));
                    break;
                case Mode.GpuCost:
                    rgb = owner < 0 || !cap.Cmds[owner].HasGpu || cap.Cmds[owner].Frags == 0
                        ? Pixel(vram[idx]) * 0.2f
                        : Heat01(cap.Cmds[owner].GpuNs / (double)cap.Cmds[owner].Frags / Math.Max(1e-9, _gpuPerFragMax));
                    break;
                case Mode.Routines:
                    rgb = owner >= 0 ? (CmdSlot(owner) < 0 ? new Vector3(0.5f) : Hue(CmdSlot(owner) + 3)) : Pixel(vram[idx]) * 0.25f;
                    break;
                default:
                    rgb = Pixel(vram[idx]);
                    if (_selSlot != int.MinValue && (owner < 0 || CmdSlot(owner) != _selSlot)) rgb *= 0.3f;
                    break;
            }
            _rgba[o] = (byte)(rgb.X * 255);
            _rgba[o + 1] = (byte)(rgb.Y * 255);
            _rgba[o + 2] = (byte)(rgb.Z * 255);
            _rgba[o + 3] = 255;
        }

        var prevUnit = gl.GetInteger(GLEnum.ActiveTexture);
        gl.ActiveTexture(TextureUnit.Texture15);
        var prevTex = gl.GetInteger(GLEnum.TextureBinding2D);
        var prevAlign = gl.GetInteger(GLEnum.UnpackAlignment);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        if (_tex == 0)
        {
            _tex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _tex);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }
        else gl.BindTexture(TextureTarget.Texture2D, _tex);

        if (w != _texW || h != _texH)
        {
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, _rgba.AsSpan(0, w * h * 4));
            (_texW, _texH) = (w, h);
        }
        else
            gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, 0, 0, (uint)w, (uint)h,
                PixelFormat.Rgba, PixelType.UnsignedByte, _rgba.AsSpan(0, w * h * 4));

        gl.PixelStore(PixelStoreParameter.UnpackAlignment, prevAlign);
        gl.BindTexture(TextureTarget.Texture2D, (uint)prevTex);
        gl.ActiveTexture((TextureUnit)prevUnit);
    }

    static Vector3 Pixel(ushort p)
    {
        int r = p & 0x1F, g = (p >> 5) & 0x1F, b = (p >> 10) & 0x1F;
        return new Vector3(r / 31f, g / 31f, b / 31f);
    }

    static readonly Vector3[] HeatRamp =
    [
        new(0.02f, 0.02f, 0.05f), new(0.10f, 0.20f, 0.60f), new(0.10f, 0.60f, 0.30f), new(0.85f, 0.85f, 0.15f),
        new(0.95f, 0.55f, 0.10f), new(0.95f, 0.20f, 0.15f), new(0.95f, 0.30f, 0.80f), new(1f, 1f, 1f),
    ];

    static Vector3 Heat(int n) => HeatRamp[Math.Min(n, HeatRamp.Length - 1)];

    double _gpuPerFragMax;

    static Vector3 Heat01(double t)
    {
        var f = (float)Math.Clamp(Math.Sqrt(Math.Max(0, t)), 0, 1) * (HeatRamp.Length - 1);
        var i = Math.Min((int)f, HeatRamp.Length - 2);
        return Vector3.Lerp(HeatRamp[i], HeatRamp[i + 1], f - i);
    }

    static float Luma(ushort p) => Vector3.Dot(Pixel(p), new Vector3(0.3f, 0.55f, 0.15f));

    static Vector3 Recovery(in CapturedCmd c) =>
        c.VtxAsked == 0 ? new Vector3(0.25f, 0.45f, 0.95f)
        : c.VtxHits == c.VtxAsked ? new Vector3(0.25f, 0.85f, 0.3f)
        : c.VtxHits == 0 ? new Vector3(0.95f, 0.25f, 0.2f)
        : new Vector3(0.95f, 0.7f, 0.15f);

    // ---- routines ----------------------------------------------------------------------------

    readonly List<(int Slot, string[] Cells)> _routineRows = new();

    void BuildRoutineRows()
    {
        var cap = _cap!;
        _routineRows.Clear();
        for (var i = 0; i < cap.Slots.Length; i++)
        {
            var s = cap.Slots[i];
            if (s.Calls == 0 && s.Packets == 0 && s.Frags == 0) continue;
            var slot = i < cap.Slots.Length - 1 ? i : -1;
            var b = BaseSlot(i, cap);
            _routineRows.Add((slot,
            [
                $"{(slot < 0 ? "(no routine)" : FrameCapture.SlotLabel(slot))}##fvr{i}",
                $"{s.Calls}", $"{s.Incl * Capture.TicksToMs:0.000}", $"{s.Self * Capture.TicksToMs:0.000}",
                b is { } bs ? $"{(s.Self - bs.Self) * Capture.TicksToMs:+0.000;-0.000}" : "",
                $"{s.Packets}", $"{s.WalkTicks * Capture.TicksToMs:0.000}", $"{s.Frags}", $"{s.Flushes}",
                cap.GpuFinal && cap.TimerQueries ? $"{s.GpuNs / 1e6:0.000}" : "",
                $"{s.Vtx.Stores}", $"{s.Vtx.Bound}", s.VtxAsked > 0 ? $"{s.VtxHits}/{s.VtxAsked}" : "",
            ]));
        }
    }

    SlotStat? BaseSlot(int i, Capture cap)
    {
        if (_base == null || _base == cap) return null;
        // Slots are append-only for the session, so an index names the same routine in both;
        // the last entry is "(no routine)" and moves when a routine is added.
        if (i == cap.Slots.Length - 1) return _base.Slots[^1];
        return i < _base.Slots.Length - 1 ? _base.Slots[i] : null;
    }

    void DrawRoutines()
    {
        var cap = _cap!;
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp |
                                      ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##kf2fvroutines", 13, flags)) return;
        ImGui.TableSetupColumn("routine", ImGuiTableColumnFlags.WidthStretch, 3.2f);
        ImGui.TableSetupColumn("calls", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn("incl ms", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("self ms", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Δ self", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("prims", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("send ms", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("fragments", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("submits", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("GPU ms", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("stores", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("bound", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        ImGui.TableSetupColumn("verts", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableHeadersRow();

        foreach (var row in _routineRows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(new Vector4(Hue(row.Slot + 3), 1), "■");
            ImGui.SameLine();
            if (ImGui.Selectable(row.Cells[0], _selSlot == row.Slot, ImGuiSelectableFlags.SpanAllColumns))
                _selSlot = _selSlot == row.Slot ? int.MinValue : row.Slot;
            for (var k = 1; k < row.Cells.Length; k++)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Cells[k]);
            }
        }
        ImGui.EndTable();
        ImGui.TextWrapped("incl/self: time inside the routine, hooks included; the vertex map's cost is inside self, " +
                          "since it runs on the game's own stores and loads. Δ self: against the baseline. prims: primitives " +
                          "whose packets it built. send ms: the port's time to decode and batch those packets. fragments: " +
                          "pixels rasterized at 1x. submits: GL batch submits its primitives forced. GPU ms: its share of " +
                          "each batch's GPU time, split by fragments. stores: guest stores the vertex map watched in its own " +
                          "body; bound: of those, coordinates it tied to an address. verts: vertices of its primitives that " +
                          "recovered depth and sub-pixel position, of those asked.");
    }

    // ---- runtime sections --------------------------------------------------------------------

    string[][]? _sectionRows;
    int[] _sectionIds = [];
    Capture? _sectionCap;
    bool _sectionGpu;

    void DrawSections()
    {
        var cap = _cap!;
        if (_sectionRows == null || _sectionCap != cap || _sectionGpu != cap.GpuFinal)
        {
            (_sectionCap, _sectionGpu) = (cap, cap.GpuFinal);
            var baseById = new Dictionary<int, SectionStat>();
            if (_base != null && _base != cap)
                foreach (var st in _base.SectionStats) baseById[st.Id] = st;
            _sectionIds = cap.SectionStats.Select(x => x.Id).ToArray();
            _sectionRows = cap.SectionStats.Select(st => new[]
            {
                $"{Capture.SectionName(st.Id)}##fvs{st.Id}", Profiler.Group(st.Id).ToString(), $"{st.Calls}",
                $"{st.Incl * Capture.TicksToMs:0.000}", $"{st.Self * Capture.TicksToMs:0.000}",
                baseById.TryGetValue(st.Id, out var b) ? $"{(st.Self - b.Self) * Capture.TicksToMs:+0.000;-0.000}" : _base != null && _base != cap ? "new" : "",
                st.HasGpu ? $"{st.GpuNs / 1e6:0.000}" : "",
            }).ToArray();
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp |
                                      ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("##kf2fvsections", 7, flags, new Vector2(0, 260)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("section", ImGuiTableColumnFlags.WidthStretch, 4f);
            ImGui.TableSetupColumn("group", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableSetupColumn("calls", ImGuiTableColumnFlags.WidthStretch, 0.5f);
            ImGui.TableSetupColumn("incl ms", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("self ms", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Δ self", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("GPU ms", ImGuiTableColumnFlags.WidthStretch, 0.7f);
            ImGui.TableHeadersRow();
            for (var r = 0; r < _sectionRows.Length; r++)
            {
                var row = _sectionRows[r];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, GroupColour(Profiler.Group(_sectionIds[r]), 1f));
                ImGui.TextUnformatted("■");
                ImGui.PopStyleColor();
                ImGui.SameLine();
                if (ImGui.Selectable(row[0], _selSection == _sectionIds[r], ImGuiSelectableFlags.SpanAllColumns))
                    _selSection = _selSection == _sectionIds[r] ? -1 : _sectionIds[r];
                for (var k = 1; k < row.Length; k++)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row[k]);
                }
            }
            ImGui.EndTable();
        }
        ImGui.TextWrapped("Every profiler section that ran inside the frame, whether or not the profiler is recording: " +
                          "each hook's own delegate, the recompiled body of a hooked function, VSync and the present it " +
                          "makes (the previous frame's picture), every GL submit, the AO pass and composite, target " +
                          "writebacks, and the vertex attribute lookups. GPU ms is a GL timer query on the submits, the AO " +
                          "pass and the composite. A selected section is outlined on the timeline. The GPU line in the " +
                          "summary is this frame's own present, taken after the frame.");
    }

    // ---- commands ----------------------------------------------------------------------------

    int[]? _cmdRows;
    int _rowsSel = int.MinValue, _sortCol;
    bool _rowsOnly, _sortDesc;

    void DrawCommands()
    {
        var cap = _cap!;
        ImGui.Checkbox("Only the selected routine", ref _onlySel);

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.Sortable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##kf2fvcmds", 8, flags, new Vector2(0, 300))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.DefaultSort, 0.6f);
        ImGui.TableSetupColumn("t ms", ImGuiTableColumnFlags.NoSort, 0.8f);
        ImGui.TableSetupColumn("cost us", ImGuiTableColumnFlags.PreferSortDescending, 0.7f);
        ImGui.TableSetupColumn("kind", ImGuiTableColumnFlags.NoSort, 1.5f);
        ImGui.TableSetupColumn("routine", ImGuiTableColumnFlags.NoSort, 2f);
        ImGui.TableSetupColumn("fragments", ImGuiTableColumnFlags.PreferSortDescending, 0.8f);
        ImGui.TableSetupColumn("batch", ImGuiTableColumnFlags.NoSort, 0.5f);
        ImGui.TableSetupColumn("submit", ImGuiTableColumnFlags.PreferSortDescending, 1.6f);
        ImGui.TableHeadersRow();

        unsafe
        {
            var specs = ImGui.TableGetSortSpecs();
            if (specs.NativePtr != null && specs.SpecsDirty && specs.SpecsCount > 0)
            {
                _sortCol = specs.Specs.ColumnIndex;
                _sortDesc = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
                specs.SpecsDirty = false;
                _cmdRows = null;
            }
        }

        if (_cmdRows == null || _rowsSel != _selSlot || _rowsOnly != _onlySel)
        {
            (_rowsSel, _rowsOnly) = (_selSlot, _onlySel);
            var rows = Enumerable.Range(0, cap.Cmds.Length);
            if (_onlySel && _selSlot != int.MinValue) rows = rows.Where(i => CmdSlot(i) == _selSlot);
            rows = _sortCol switch
            {
                2 => rows.OrderBy(i => cap.Cmds[i].Ticks),
                5 => rows.OrderBy(i => cap.Cmds[i].Frags),
                7 => rows.OrderBy(i => cap.Cmds[i].Flushes),
                _ => rows,
            };
            _cmdRows = rows.ToArray();
            if (_sortDesc) Array.Reverse(_cmdRows);
        }

        unsafe
        {
            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(_cmdRows.Length);
            while (clipper.Step())
                for (var r = clipper.DisplayStart; r < clipper.DisplayEnd; r++)
                {
                    var i = _cmdRows[r];
                    ref var c = ref cap.Cmds[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{i}##fvc{i}", i == _cursor, ImGuiSelectableFlags.SpanAllColumns)) Seek(i);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{cap.RelMs(c.Start):0.000}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{c.Ticks * Capture.TicksToMs * 1000:0.0}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(cap.KindLabel(c));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(new Vector4(Hue(CmdSlot(i) + 3), 1), cap.OwnerLabel(c));
                    ImGui.TableNextColumn();
                    ImGui.Text($"{c.Frags}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{c.Batch}");
                    ImGui.TableNextColumn();
                    if (c.Flushes > 0) ImGui.TextUnformatted(Capture.ReasonLabel(c.Reason));
                }
            clipper.End();
            clipper.Destroy();
        }
        ImGui.EndTable();
    }

    void DrawFlushes()
    {
        var cap = _cap!;
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##kf2fvflushes", 7, flags, new Vector2(0, 200))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.None, 0.4f);
        ImGui.TableSetupColumn("t ms", ImGuiTableColumnFlags.None, 0.7f);
        ImGui.TableSetupColumn("submit us", ImGuiTableColumnFlags.None, 0.7f);
        ImGui.TableSetupColumn("GPU us", ImGuiTableColumnFlags.None, 0.7f);
        ImGui.TableSetupColumn("vertices", ImGuiTableColumnFlags.None, 0.6f);
        ImGui.TableSetupColumn("reason", ImGuiTableColumnFlags.None, 1.6f);
        ImGui.TableSetupColumn("at command", ImGuiTableColumnFlags.None, 1.8f);
        ImGui.TableHeadersRow();
        for (var i = 0; i < cap.Flushes.Length; i++)
        {
            var f = cap.Flushes[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"{i}");
            ImGui.TableNextColumn();
            ImGui.Text($"{cap.RelMs(f.Start):0.000}");
            ImGui.TableNextColumn();
            ImGui.Text(f.End > f.Start ? $"{(f.End - f.Start) * Capture.TicksToMs * 1000:0.0}" : "");
            ImGui.TableNextColumn();
            ImGui.Text(f.HasGpu ? $"{f.GpuNs / 1e3:0.0}" : "");
            ImGui.TableNextColumn();
            ImGui.Text($"{f.Verts}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Capture.ReasonLabel(f.Why));
            ImGui.TableNextColumn();
            var at = Math.Min(f.At, cap.Cmds.Length - 1);
            if (at >= 0 && ImGui.Selectable($"{(f.InCmd ? "" : "before ")}#{f.At} {cap.OwnerLabel(cap.Cmds[at])}##fvf{i}"))
                Seek(at);
        }
        ImGui.EndTable();
    }
}
