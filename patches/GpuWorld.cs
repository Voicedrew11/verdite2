using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// The GPU world renderer, Step 1 (0085): the map's opaque faces drawn from the
/// retained scene's static mesh into the frame, instead of assembled by the game's
/// code every frame.
///
///     KF2_GPUWORLD=1         draw the map on the GPU (off: not judged); 0 never
///                            (unset, Video ▸ Experimental ▸ GPU world renderer)
///     KF2_GPUWORLD_PROBE=1   a line every 2 s: draws, misses, halves skipped and kept,
///                            and the surface buffer read back against the frame's depth
///     KF2_GPUWORLD_SURFACES=0  leave the map out of the normal and surface buffers
///
/// The tile walk still decides: every half it visits is noted in the frame's gate
/// (<see cref="RetainedScene.NoteHalf"/>), and the backend draws exactly those halves
/// at the head of the ordering table's walk, past the sky. The half is then not
/// assembled at all, or, when its mesh has semi-transparent faces (water), only those
/// are (<see cref="PolyAssembler.BlendedOnly"/>): they stay on the game's packets so
/// the blend order, the swell, the ripples and the surface buffer see them as before.
/// The mirrored walk (<see cref="PlanarWalk"/>) is untouched.
///
/// See "Step 1, the first slice" in docs/GPU_RENDERER.md.
/// </summary>
public static class GpuWorld
{
    const uint ModelTable = 0x8018E19C;

    public const string OnKey = "kf2.gpuworld.on";

    static bool _on, _probe;
    static bool? _forced;

    public static bool Enabled => _on;

    public static void Configure(string? on, string? probe, string? surfaces = null)
    {
        RetainedScene.MainSurfaces = surfaces?.Trim() != "0";
        if (!string.IsNullOrWhiteSpace(on)) _forced = on.Trim() is "1" or "on";
        _probe = probe?.Trim() is not (null or "" or "0");
    }

    public static void Install()
    {
        _on = _forced ?? false;
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            _on = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, false);
            Console.WriteLine($"[KF2] gpu world: {(_on ? "on (the map drawn from the retained scene)" : "off")}" +
                              (_on && Blocker is { } why ? $", standing down: {why}" : ""));
        });
    }

    public static void SetEnabled(bool on)
    {
        _on = on;
        if (on) return;
        Active = false;
        RetainedScene.MainView = false;
        RetainedScene.MainSerial = 0;
    }

    /// <summary>Why the map cannot be drawn on the GPU with the settings as they are,
    /// or null. The renderer draws with perspective-correct textures only, and needs the
    /// depth buffer and the C# assemblers; each of these is a setting the player sees.</summary>
    public static string? Blocker =>
        !RetainedScene.Supported ? "needs the OpenGL core renderer"
        : !PolyAssembler.FastGeometry ? "needs Fast geometry"
        : !GteDepth.ZBuffer ? "needs the Z-buffer"
        : !Perspective.Enabled ? "needs perspective-correct textures"
        : null;

    /// <summary>Whether the map is wanted on the GPU: the retained map is built for it.</summary>
    public static bool Wanted => _on && RetainedScene.Supported;

    /// <summary>Whether this frame's walk hands its halves to the GPU. Everything it
    /// depends on is the C# walk and assemblers, the depth buffer, and a built map;
    /// any comparison that runs the recompiled routines stands it down.</summary>
    public static bool Active { get; private set; }

    /// <summary>From <see cref="RetainedMap.AtWalk"/>, once the frame has begun.</summary>
    public static void AtFrame()
    {
        Active = Wanted && Blocker == null && RetainedScene.StaticCount[0] > 0
              && TileWalk.Enabled && TileWalk.CellEnabled && TileWalk.TileEnabled && !TileWalk.Verifying
              && PolyAssembler.Enabled && PolyAssembler.UnclippedEnabled && !PolyAssembler.Verifying
              && !RecompOne.Runtime.Pgxp.Pgxp.CpuTracking;
        RetainedScene.MainView = Active;
        RetainedScene.MainSerial = Active ? RetainedScene.Serial : 0;
        if (_probe) Report();
    }

    // ---- which halves still need the game's assembler -------------------------------

    static readonly Dictionary<uint, bool> _blended = new();
    static int _blendedGen = -1;
    static uint _blendedTable;

    /// <summary>Whether the model's mesh has a semi-transparent face.</summary>
    public static bool HasBlended(PSMemory mem, uint model)
    {
        uint table = mem.ReadU32(ModelTable);
        if (_blendedGen != RetainedScene.StaticGeneration || _blendedTable != table)
        {
            _blended.Clear();
            _blendedGen = RetainedScene.StaticGeneration;
            _blendedTable = table;
        }
        uint header = table + model * 28u + 0xCu;
        if (_blended.TryGetValue(header, out bool any)) return any;
        uint face = table + mem.ReadU32(header + 0x10u) + 0xCu;
        uint count = Math.Min(mem.ReadU32(header + 0x14u), 4096u);
        for (uint i = 0; i < count && !any; i++)
        {
            uint word = mem.ReadU32(face);
            uint type = (word >> 24) & 0xFDu;
            if ((type == 0x2Cu || type == 0x24u) && ((word >> 24) & 2u) != 0u) any = true;
            face += 4u + ((word >> 6) & 0x3FCu);
        }
        _blended[header] = any;
        return any;
    }

    /// <summary>The probe's: halves left to the GPU whole, and those whose water was
    /// still assembled.</summary>
    public static long Skipped, Kept;

    /// <summary>The `gpuworld` shell verb: the state, or the switch.</summary>
    public static string Shell(string arg)
    {
        switch (arg.Trim().ToLowerInvariant())
        {
            case "on": SetEnabled(true); break;
            case "off": SetEnabled(false); break;
            case "surfaces on": RetainedScene.MainSurfaces = true; break;
            case "surfaces off": RetainedScene.MainSurfaces = false; break;
            case "": break;
            default: return "{\"ok\":false,\"error\":\"gpuworld [on|off|surfaces on|off]\"}";
        }
        return $"{{\"ok\":true,\"on\":{(_on ? "true" : "false")},\"active\":{(Active ? "true" : "false")}," +
               $"\"surfaces\":{(RetainedScene.MainSurfaces ? "true" : "false")}," +
               $"\"draws\":{RetainedScene.MainDraws},\"missed\":{RetainedScene.MainMissed}}}";
    }

    // ---- the probe -----------------------------------------------------------------

    static double _reportAt;
    static long _draws, _missed, _tris, _uploads, _nrmTris;

    static void Report()
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now < _reportAt) return;
        _reportAt = now + 2.0;
        long d = RetainedScene.MainDraws - _draws, m = RetainedScene.MainMissed - _missed, t = RetainedScene.MainTriangles - _tris;
        _draws = RetainedScene.MainDraws; _missed = RetainedScene.MainMissed; _tris = RetainedScene.MainTriangles;
        Console.WriteLine($"[KF2] gpu world: {(Active ? "active" : "standing down")}; {d} draw(s), {m} walk(s) missed, " +
                          $"{(d == 0 ? 0 : t / d)} static triangle(s) a draw; halves {Skipped} left whole to the GPU, " +
                          $"{Kept} with their blended faces assembled");
        double ms2(int i) => d == 0 ? 0 : RetainedScene.MipTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / d;
        double ms(int i) => d == 0 ? 0 : RetainedScene.MainTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / d;
        Console.WriteLine($"[KF2] gpu world: CPU ms a draw: shadows+static {ms(0):F3}, mips {ms(1):F3}, setup {ms(2):F3}, draw {ms(3):F3}; mip table uploads {RetainedScene.MipTableUploads - _uploads}, keys {RetainedScene.MipsKeys} ({RetainedScene.MipsFound} found), lookups {ms2(0):F3}, decode {ms2(1):F3}");
        Array.Clear(RetainedScene.MipTicks);
        _uploads = RetainedScene.MipTableUploads;
        Array.Clear(RetainedScene.MainTicks);
        long px = RetainedScene.SurfaceDepthPixels;
        Console.WriteLine($"[KF2] gpu world: surfaces: {RetainedScene.MainNormalTriangles - _nrmTris} map triangle(s) into the normal pass; " +
                          (RetainedScene.SurfaceChecks == 0 ? "no readback (needs the surface buffer: a reflection or the murk on)" :
                           $"of {px} pixel(s) with a depth, {(px == 0 ? 0 : 100.0 * RetainedScene.SurfaceBehind / px):F2}% whose surface lies behind it, " +
                           $"{(px == 0 ? 0 : 100.0 * RetainedScene.SurfaceMissing / px):F2}% with none"));
        _nrmTris = RetainedScene.MainNormalTriangles;
        RetainedScene.SurfaceDepthPixels = RetainedScene.SurfaceBehind = RetainedScene.SurfaceMissing = RetainedScene.SurfaceChecks = 0;
        RetainedScene.SurfaceCheck = true;
        Skipped = Kept = 0;
    }
}
