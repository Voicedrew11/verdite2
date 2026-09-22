using System.Numerics;
using System.Reflection;
using ImGuiNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Draw sign and dialogue text as text, on an opaque box, instead of the game's 1:1
/// 16-colour picture of it. An experiment: off by default.
///
///     KF2_MESSAGETEXT=1         on
///     KF2_MESSAGETEXT_PROBE=1   each message's key and decoded text, or why it was left alone
///     KF2_MESSAGETEXT_TEST=3:0,6:360   open these messages from the main loop, 10 s in
///                               and then one a stage-9 call after each closes (KF2_AUTOPAD
///                               dismisses them)
///
/// A message is one 4-bit TIM that `func_80035B48(file, entry)` reads off the disc and
/// `func_80035684` uploads; `func_800356F4` fades it in and out. The TIM is decoded in
/// RAM before the upload, cell by cell against <see cref="MessageGlyphs"/>, so no text
/// is carried by the port. On a full decode its palette is zeroed -- colour 0 is
/// transparent -- so the game's two text quads draw nothing, and this panel draws the
/// lines at the same place. Anything that does not decode is left to the game. See
/// "Drawing message text" in docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class MessageText : IFloatingPanel
{
    public static readonly MessageText Instance = new();
    MessageText() { }

    const uint ShowMessage = 0x80035B48;
    const uint LoadTims = 0x80035684;
    const uint Fade = 0x800356F4;
    const uint Present = 0x8002E0FC;
    const uint MainLoopMarker = 0x800140AC;   // stage 9

    // Where func_800356F4 puts the picture: (32, 10), texel for pixel.
    const int ScreenX = 32, ScreenY = 10;
    const int CellW = 8, CellH = 14, OriginX = 4;
    static readonly int[] Phases = [3, 10];
    const int FullBright = 0x6C;   // the last brightness the fade presents

    public static bool Enabled { get; private set; }
    static bool _probe, _hooked, _queued;

    static bool _inMessage, _inFade;
    static uint _file, _entry;

    // What the panel draws; written on the game thread, read at present on the same one.
    static volatile bool _active;
    static int _bright;
    static int _top;
    static (int Col, string Text, uint Rgb)[] _lines = [];

    /// <summary>This message's text is ours: the game's picture of it is not drawn.</summary>
    public static bool Covering => _active && Enabled;

    public string Name => "kf2message";
    public string TitleKey => "panel.kf2.message";
    public bool IsOpen { get => _active && Enabled; set { } }

    static readonly ModInfo _self = new()
    {
        Id = "kf2.messagetext",
        Name = "Message text",
        Version = "0.1",
        Description = "Draws sign and dialogue text as text on an opaque box.",
    };

    static readonly Queue<(uint File, uint Entry)> _tests = new();
    static long _testAt = -1;

    public static void Configure(string? enabled, string? probe, string? test = null)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
        if (!string.IsNullOrWhiteSpace(probe)) _probe = probe != "0";
        foreach (var part in (test ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var fe = part.Split(':');
            if (fe.Length == 2 && uint.TryParse(fe[0], out uint f) && uint.TryParse(fe[1], out uint e))
                _tests.Enqueue((f, e));
        }
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Localization.Merge("""
            {
              "strings": {
                "panel.kf2.message": { "en": "Message text", "pt-BR": "Texto da mensagem",
                                       "es-419": "Texto del mensaje" }
              }
            }
            """);
            PanelManager.Register(Instance);
        });
        HookAttach.OnOverlayLoad("message text", Attach);
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        var show = SymbolRegistry.Resolve("game", null, ShowMessage);
        var load = SymbolRegistry.Resolve("game", null, LoadTims);
        var fade = SymbolRegistry.Resolve("game", null, Fade);
        var present = SymbolRegistry.Resolve("game", null, Present);
        var marker = SymbolRegistry.Resolve("game", null, MainLoopMarker);
        if (show == null || load == null || fade == null || present == null || marker == null) return false;

        MethodInfo M(string n) => typeof(MessageText).GetMethod(n, BindingFlags.Public | BindingFlags.Static)!;
        // Queued once; see MenuWorld.Attach for why Installed() cannot guard it.
        if (!_queued)
        {
            _queued = true;
            HookManager.AddPre(_self, show, M(nameof(BeforeShow)));
            HookManager.AddPost(_self, show, M(nameof(AfterShow)));
            HookManager.AddPre(_self, load, M(nameof(BeforeLoad)));
            HookManager.AddPre(_self, fade, M(nameof(BeforeFade)));
            HookManager.AddPost(_self, fade, M(nameof(AfterFade)));
            HookManager.AddPre(_self, present, M(nameof(BeforePresent)));
            if (_tests.Count > 0) HookManager.AddPre(_self, marker, M(nameof(MainLoop)));
        }
        HookManager.Commit();

        _hooked = HookAttach.Installed(show) && HookAttach.Installed(load) &&
                  HookAttach.Installed(fade) && HookAttach.Installed(present);
        Console.WriteLine($"[KF2] message text: {(Enabled && _hooked ? "on" : "off")}, {MessageGlyphs.Table.Count} glyphs");
        return _hooked;
    }

    /// <summary>KF2_MESSAGETEXT_TEST: open the next queued message through the game's
    /// own routine, from the main loop, where the game's modal loops open them too.</summary>
    public static void MainLoop(CpuContext c, IMemory m)
    {
        if (_tests.Count == 0 || _inMessage) return;
        long now = Environment.TickCount64;
        if (_testAt < 0) { _testAt = now + 10_000; return; }
        if (now < _testAt) return;

        var (file, entry) = _tests.Dequeue();
        Console.WriteLine($"[KF2] message text: test opens ({file}, {entry})");
        var saved = c.Snapshot();
        try
        {
            c.A0 = file;
            c.A1 = entry;
            Recompiled.KingsField2_game.func_80035B48(c, (PSMemory)m);
        }
        finally { c.Restore(saved); }
    }

    public static void BeforeShow(CpuContext c, IMemory m)
    {
        _inMessage = true;
        _file = c.A0 & 0xFFFFu;
        _entry = c.A1 & 0xFFFFu;
    }

    public static void AfterShow(CpuContext c, IMemory m)
    {
        if (_probe && _active)
            Console.WriteLine($"[KF2] message text: ({_file}, {_entry}) closed; panel drew {_drawn} frame(s), " +
                              $"peak opacity {_peakAlpha:0.00}, {_drawnLines} line(s) at {_size:0.0} px");
        _inMessage = false;
        _active = false;
    }

    static int _drawn, _drawnLines;
    static float _peakAlpha, _size;

    public static void BeforeFade(CpuContext c, IMemory m)
    {
        _inFade = true;
        _bright = (int)c.A0;
    }

    public static void AfterFade(CpuContext c, IMemory m) => _inFade = false;

    /// <summary>`func_800356F4` holds its brightness in `s2`, and presents through
    /// stage 13's presenter once a step.</summary>
    public static void BeforePresent(CpuContext c, IMemory m)
    {
        if (_inFade) _bright = (int)c.S2;
    }

    public static void BeforeLoad(CpuContext c, IMemory m)
    {
        if (!_inMessage || !Enabled || !_hooked) return;
        _active = false;
        uint buf = c.A0;
        string why = Decode(m, buf, out int top, out (int Col, string Text, uint Rgb)[] lines);
        if (why.Length > 0)
        {
            if (_probe) Console.WriteLine($"[KF2] message text: ({_file}, {_entry}) left to the game: {why}");
            return;
        }

        // Palette to colour 0, which a textured primitive does not draw.
        uint clut = buf + 8u + 12u;
        for (uint i = 0; i < 16; i++) m.WriteU16(clut + i * 2u, 0);

        _top = top;
        _lines = lines;
        _bright = 0;
        _drawn = 0;
        _peakAlpha = 0;
        _active = true;
        if (_probe)
        {
            Console.WriteLine($"[KF2] message text: ({_file}, {_entry}), {lines.Length} line(s)");
            foreach (var l in lines) Console.WriteLine($"[KF2] message text:   {new string(' ', l.Col)}{l.Text}");
        }
    }

    static double Lum(uint c) => ((c & 31) * 2 + ((c >> 5) & 31) * 5 + ((c >> 10) & 31)) / 8.0;

    /// <summary>The same cell rule as scripts/msg_glyphs.py. Empty string on success,
    /// otherwise why not.</summary>
    static string Decode(IMemory m, uint buf, out int top, out (int, string, uint)[] result)
    {
        top = 0;
        result = [];
        if (m.ReadU32(buf) != 0x10u || m.ReadU32(buf + 4u) != 8u) return "not a 4-bit TIM";

        var clut = new uint[16];
        for (uint i = 0; i < 16; i++) clut[i] = m.ReadU16(buf + 20u + i * 2u);
        uint img = buf + 8u + m.ReadU32(buf + 8u);
        int w = m.ReadU16(img + 8u) * 4, h = m.ReadU16(img + 10u);
        if (w <= 0 || h <= 0 || w > 1024 || h > 512) return "odd size";

        var px = new byte[w * h];
        var count = new int[16];
        for (int k = 0; k < px.Length; k += 2)
        {
            byte b = m.ReadU8(img + 12u + (uint)(k >> 1));
            px[k] = (byte)(b & 15);
            px[k + 1] = (byte)(b >> 4);
            count[b & 15]++;
            count[b >> 4]++;
        }

        int bg = 0;
        for (int i = 1; i < 16; i++) if (count[i] > count[bg]) bg = i;
        double mx = 0;
        for (int i = 0; i < 16; i++) if (i != bg && count[i] > 0) mx = Math.Max(mx, Lum(clut[i]));
        if (mx == 0) return "blank";

        var on = new bool[px.Length];
        for (int k = 0; k < px.Length; k++) on[k] = px[k] != bg && Lum(clut[px[k]]) >= 0.5 * mx;

        int r0 = -1;
        for (int y = 0; y < h && r0 < 0; y++)
            for (int x = 0; x < w; x++)
                if (on[y * w + x]) { r0 = y; break; }
        if (r0 < 0) return "blank";
        for (int y = 0; y < h; y++)
            for (int x = 0; x < OriginX; x++)
                if (on[y * w + x]) return "ink left of the grid";

        int best = int.MaxValue;
        foreach (int p in Phases)
        {
            int off = ((r0 - p) % CellH + CellH) % CellH;
            if (off <= 4) best = Math.Min(best, off);
        }
        if (best == int.MaxValue) return "not on the line grid";
        top = r0 - best;

        var lines = new List<(int, string, uint)>();
        var row = new byte[CellH];
        var sb = new System.Text.StringBuilder();
        for (int y0 = top; y0 < h; y0 += CellH)
        {
            sb.Clear();
            int first = -1;
            uint rgb = 0;
            double rgbLum = -1;
            for (int col = 0, x0 = OriginX; x0 + CellW <= w; col++, x0 += CellW)
            {
                bool any = false;
                for (int r = 0; r < CellH; r++)
                {
                    int v = 0, y = y0 + r;
                    if (y < h)
                        for (int cx = 0; cx < CellW; cx++)
                        {
                            int k = y * w + x0 + cx;
                            if (!on[k]) continue;
                            v |= 1 << cx;
                            double l = Lum(clut[px[k]]);
                            if (l > rgbLum) { rgbLum = l; rgb = clut[px[k]]; }
                        }
                    row[r] = (byte)v;
                    any |= v != 0;
                }
                if (!any) { sb.Append(' '); continue; }
                if (!MessageGlyphs.Table.TryGetValue(Fnv(row), out char ch))
                    return $"unknown glyph at line {(y0 - top) / CellH}, column {col}";
                if (first < 0) first = col;
                sb.Append(ch);
            }
            string s = sb.ToString().TrimEnd();
            lines.Add((first < 0 ? 0 : first, first < 0 ? "" : s[first..], rgb));
        }

        while (lines.Count > 0 && lines[^1].Item2.Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return "blank";
        result = lines.ToArray();
        return "";
    }

    static ulong Fnv(byte[] b)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (byte x in b) h = (h ^ x) * 0x100000001B3UL;
        return h;
    }

    public void Draw()
    {
        var lines = _lines;
        if (!_active || lines.Length == 0) return;
        float alpha = Math.Clamp(_bright / (float)FullBright, 0f, 1f);
        if (alpha <= 0f) return;

        MapRender.Picture(out var g0, out var g1);
        float scale = (g1.Y - g0.Y) / 240f;
        float cx = (g0.X + g1.X) * 0.5f;
        Vector2 Game(float x, float y) => new(cx + (x - 160f) * scale, g0.Y + y * scale);

        ImGui.SetNextWindowPos(g0);
        ImGui.SetNextWindowSize(g1 - g0);
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (ImGui.Begin("##kf2message", flags))
        {
            var dl = ImGui.GetWindowDrawList();

            // Over a live world the dim is ours too: the game's was the paste's colour,
            // 0x80 - b/2, which a black layer at (b/2)/0x80 matches exactly.
            if (MenuWorld.MessageLive)
                dl.AddRectFilled(g0, g1, Rgba(0f, 0f, 0f, Math.Clamp((_bright >> 1) / 128f, 0f, 1f)));

            float y0 = ScreenY + _top - 8f, y1 = ScreenY + _top + lines.Length * CellH + 6f;
            var b0 = Game(ScreenX - 4f, y0);
            var b1 = Game(ScreenX + 256f + 4f, y1);
            dl.AddRectFilled(b0, b1, Rgba(0.05f, 0.05f, 0.06f, alpha), 3f * scale);
            dl.AddRect(b0, b1, Rgba(0.55f, 0.50f, 0.40f, alpha), 3f * scale, ImDrawFlags.None, Math.Max(1f, scale * 0.75f));

            var font = ImGui.GetFont();
            float size = 12.5f * scale;
            for (int i = 0; i < lines.Length; i++)
            {
                var (col, text, rgb) = lines[i];
                if (text.Length == 0) continue;
                var p = Game(ScreenX + OriginX + col * CellW, ScreenY + _top + i * CellH);
                float r = (rgb & 31) / 31f, g = ((rgb >> 5) & 31) / 31f, b = ((rgb >> 10) & 31) / 31f;
                dl.AddText(font, size, p, Rgba(r, g, b, alpha), text);
                _drawnLines = i + 1;
            }
            _drawn++;
            _peakAlpha = Math.Max(_peakAlpha, alpha);
            _size = size;
        }
        ImGui.End();
    }

    static uint Rgba(float r, float g, float b, float a) => ImGui.GetColorU32(new Vector4(r, g, b, a));
}
