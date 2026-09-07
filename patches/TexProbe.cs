using System.Text;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;

namespace Kf2;

/// <summary>
/// What the renderer was asked to draw, and what was in VRAM to draw it from.
///
///     KF2_TEXPROBE=1   a line a second, on the console and into texprobe.log
///
/// It exists for one report — "the game runs but has no textures" on a machine
/// nobody here can attach a debugger to — and it is built to separate the three
/// places that can go wrong, because a picture cannot tell them apart:
///
///   * **the game submitted flat polygons.** Textured-ness is a bit in the GP0
///     command the game itself wrote, decided long before any GL call. If the
///     census reads mostly `flat`, the fault is in the game's own state — its
///     model or texture data — and no shader change will put a texture back.
///   * **VRAM has no texture in it.** The page census counts how many *distinct*
///     16-bit words each of the 32 texture pages holds. A page the game uploaded
///     art into holds hundreds; a page that never received its upload holds one
///     (all zero, or one fill colour). A textured polygon sampling a uniform page
///     draws one flat colour per surface, which looks exactly like an untextured
///     one.
///   * **the sampling is wrong.** Textured prims, populated pages, and still no
///     texture on screen leaves the fragment shader's fetch, which is the only
///     part of this that is vendor-specific.
///
/// The GL banner is repeated here rather than left to the runtime's own startup
/// line because a Windows release is a WinExe with no console of its own: the
/// line the runtime prints at startup goes to the in-app console panel and
/// nowhere a player can send. This writes a file.
///
/// Nothing is hooked and nothing is written to game memory. The census is a
/// <c>RenderPrimEvent</c> listener, which both renderers pass through, and the
/// page count is a <c>ReadVram</c> from a <c>VSyncEvent</c> listener — the same
/// thread the backend draws on, once a second, because the read stalls the
/// pipeline.
/// </summary>
public static class TexProbe
{
    public static bool On { get; private set; }

    const string LogFile = "texprobe.log";

    static long _prims, _tex, _flat, _raw, _gouraud, _semi;
    static readonly HashSet<int> _cluts = [];
    static double _next;
    static bool _banner;

    static ushort[]? _vram;

    public static void Configure(string? probe)
    {
        if (probe != "1") return;
        On = true;

        Event.AddListener<RenderPrimEvent>(OnPrim);
        Event.AddListener<VSyncEvent>(_ => Tick());

        Say($"--- texprobe start {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
    }

    static void OnPrim(RenderPrimEvent e)
    {
        _prims++;
        if (e.Textured)
        {
            _tex++;
            if (e.Raw) _raw++;
            if (_cluts.Count < 64) _cluts.Add(e.Clut);
        }
        else _flat++;
        if (e.Gouraud) _gouraud++;
        if (e.SemiTransparent) _semi++;
    }

    static void Tick()
    {
        double now = Environment.TickCount64 / 1000.0;
        if (_next == 0) { _next = now + 1.0; return; }
        if (now < _next) return;
        _next = now + 1.0;

        if (!_banner) { _banner = true; Banner(); }

        long prims = _prims, tex = _tex, flat = _flat;
        int pct = prims > 0 ? (int)(tex * 100 / prims) : 0;
        Say($"prims/s {prims}  textured {tex} ({pct}%)  flat {flat}  raw {_raw}  " +
            $"gouraud {_gouraud}  semitrans {_semi}  distinct cluts {_cluts.Count}");

        Pages();

        _prims = _tex = _flat = _raw = _gouraud = _semi = 0;
        _cluts.Clear();
    }

    /// <summary>
    /// Distinct 16-bit words in each of the 32 texture pages, read back out of the
    /// backend's own VRAM. A page holding one word never received an upload (or was
    /// filled and never written); a page holding art holds hundreds.
    /// </summary>
    static void Pages()
    {
        var be = GpuHle.Backend;
        if (be is not { Ready: true }) { Say("vram: no backend"); return; }

        _vram ??= new ushort[1024 * 512];
        try { be.ReadVram(0, 0, 1024, 512, _vram); }
        catch (Exception ex) { Say($"vram: read failed: {ex.Message}"); return; }

        var seen = new HashSet<ushort>();
        for (int py = 0; py < 2; py++)
        {
            var row = new StringBuilder($"vram y{py * 256,-3}:");
            for (int px = 0; px < 16; px++)
            {
                seen.Clear();
                for (int y = py * 256; y < py * 256 + 256; y++)
                {
                    int b = y * 1024 + px * 64;
                    for (int x = 0; x < 64; x++)
                    {
                        seen.Add(_vram[b + x]);
                        if (seen.Count > 999) break;
                    }
                    if (seen.Count > 999) break;
                }
                row.Append(' ').Append(seen.Count.ToString().PadLeft(4));
            }
            Say(row.ToString());
        }
    }

    static void Banner()
    {
        var gl = GpuGlAccess.Gl;
        string vendor = "?", renderer = "?", version = "?";
        if (gl != null)
        {
            try
            {
                vendor = gl.GetStringS(Silk.NET.OpenGL.StringName.Vendor) ?? "?";
                renderer = gl.GetStringS(Silk.NET.OpenGL.StringName.Renderer) ?? "?";
                version = gl.GetStringS(Silk.NET.OpenGL.StringName.Version) ?? "?";
            }
            catch { }
        }
        Say($"gl: {vendor} | {renderer} | {version}");
        Say($"backend {GpuBackendFactory.Selected}  ready {GpuHle.Backend?.Ready}  " +
            $"active {GpuHle.Active}  scale {GlVram.Scale}  truecolor {RecompOne.Runtime.GteDepth.TrueColor}  " +
            $"perspective {RecompOne.Runtime.GteDepth.Enabled}  subpixel {RecompOne.Runtime.GteDepth.Subpixel}  zbuffer {RecompOne.Runtime.GteDepth.ZBuffer}");
    }

    static void Say(string line)
    {
        Console.WriteLine($"[KF2-TEXPROBE] {line}");
        try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
    }
}
