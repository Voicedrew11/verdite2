using System.Numerics;
using System.Reflection;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Remaster;

/// <summary>
/// Authored point and spot lights (runtime <c>0071</c>).
///
///     KF2_REMASTER_LIGHTS=0   leave the pack's lights out (they apply with the remaster by default)
///
/// The pack's <c>areas/&lt;n&gt;/lights.json</c>, behind the area's fingerprint. Before
/// each <c>DrawOTag</c> -- the camera block then holds the view the table was built
/// with -- every light is taken into the GTE's view space, culled against the eye and
/// the draw window, the nearest <see cref="RemasterUniforms.MaxLights"/> kept, and
/// the list published for the prim shader, which adds each one to the lit colour
/// before the depth cue. Flicker steps on the world tick, so it holds while the world
/// is paused. Needs per-pixel lighting and the C# assemblers, whose records carry the
/// colour a light is added in. See "Phase 2, the first slice" in docs/REMASTER.md.
/// </summary>
public sealed class Lights : IRemasterFeature
{
    public string Id => "lights";

    static bool _on = true;

    /// <summary>The area's lights, resolved; the shader list is built from these.</summary>
    static Pack.Light[] _lights = [];

    /// <summary>Why the area's lights are not applied, or null.</summary>
    public static string? Refused { get; private set; }

    public static int Authored => _lights.Length;

    /// <summary>The last frame's count sent, and culled.</summary>
    public static int Sent { get; private set; }
    public static int Culled { get; private set; }

    /// <summary>Frames the list was published on; never reset.</summary>
    public static long Frames;

    static long _ticks, _tickFrame = -1;

    /// <summary>The world tick the flicker is evaluated at.</summary>
    public static long Ticks => _ticks;

    int _version = -1, _settle = -1;

    /// <summary>How far past the eye a light may sit and still reach the draw window:
    /// twelve tiles each way.</summary>
    const float Reach = 12f * Identity.TileUnits;

    public static void Configure(string? on)
    {
        if (!string.IsNullOrWhiteSpace(on)) _on = on.Trim() != "0";
    }

    public static void Install() => HookAttach.OnOverlayLoad("remaster lights", Attach);

    static readonly (string Overlay, uint Addr)[] DrawOTag =
        [("open", 0x80016078), ("game", 0x80060818), ("end", 0x80013D80)];

    static readonly ModInfo _self = new()
    {
        Id = "kf2.remaster.lights",
        Name = "Remaster lights",
        Version = "1.0",
        Description = "Publishes a pack's authored lights, in view space, before each DrawOTag.",
    };

    static readonly HashSet<string> _hooked = new();

    static bool Attach()
    {
        SymbolRegistry.Build();
        var pre = typeof(Lights).GetMethod(nameof(BeforeDrawOTag), BindingFlags.Public | BindingFlags.Static)!;
        bool queued = false;
        foreach (var (overlay, addr) in DrawOTag)
        {
            if (_hooked.Contains(overlay)) continue;
            var target = SymbolRegistry.Resolve(overlay, null, addr);
            if (target == null) continue;
            if (HookManager.AddPre(_self, target, pre)) queued = true;
        }
        if (queued) HookManager.Commit();
        foreach (var (overlay, addr) in DrawOTag)
            if (SymbolRegistry.Resolve(overlay, null, addr) is { } t && HookAttach.Installed(t)) _hooked.Add(overlay);
        // GAME.EXE's is the one that draws an area.
        return _hooked.Contains("game");
    }

    public void OnFrame()
    {
        bool want = _on && Host.Enabled && Identity.Settled;
        if (!want)
        {
            if (_lights.Length > 0 || RemasterUniforms.Enabled) Clear();
            _version = _settle = -1;
            return;
        }
        if (_version == Pack.Version && _settle == Identity.Settles) return;
        _version = Pack.Version;
        _settle = Identity.Settles;
        Resolve();
    }

    public void Detach()
    {
        Clear();
        _version = _settle = -1;
    }

    static void Clear()
    {
        _lights = [];
        Refused = null;
        RemasterUniforms.Enabled = false;
        RemasterUniforms.Publish(0);
        PolyAssembler.KeepUnfogged = false;
        Sent = Culled = 0;
    }

    static void Resolve()
    {
        int area = Identity.Area;
        string? fp = Pack.AreaFingerprint(area);
        if (fp != null && fp != Identity.FingerprintText)
        {
            Clear();
            Refused = $"area {area} was authored against fingerprint {fp}; this one is {Identity.FingerprintText}";
            return;
        }
        Refused = null;
        _lights = Pack.Lights(area).Where(l => !l.Off && l.Radius > 0f && l.Intensity != 0f).ToArray();
        bool any = _lights.Length > 0;
        RemasterUniforms.Enabled = any;
        PolyAssembler.KeepUnfogged = any;
        if (!any) RemasterUniforms.Publish(0);
    }

    /// <summary>The frame's view: <c>v = R (w - cam) + T</c>, as <c>func_80031950</c>
    /// feeds the tile walk.</summary>
    public struct View
    {
        public Matrix4x4 R;
        public Vector3 T, Cam;
        public float H, Cx, Cy;

        public readonly Vector3 ToView(Vector3 w)
        {
            var d = w - Cam;
            return new Vector3(R.M11 * d.X + R.M12 * d.Y + R.M13 * d.Z,
                               R.M21 * d.X + R.M22 * d.Y + R.M23 * d.Z,
                               R.M31 * d.X + R.M32 * d.Y + R.M33 * d.Z) + T;
        }

        public readonly Vector3 Rotate(Vector3 d)
            => new(R.M11 * d.X + R.M12 * d.Y + R.M13 * d.Z,
                   R.M21 * d.X + R.M22 * d.Y + R.M23 * d.Z,
                   R.M31 * d.X + R.M32 * d.Y + R.M33 * d.Z);

        public readonly Vector3 ToWorld(Vector3 v)
        {
            var d = v - T;
            return Cam + new Vector3(R.M11 * d.X + R.M21 * d.Y + R.M31 * d.Z,
                                     R.M12 * d.X + R.M22 * d.Y + R.M32 * d.Z,
                                     R.M13 * d.X + R.M23 * d.Y + R.M33 * d.Z);
        }

        /// <summary>A world point to game pixels and its view depth; false behind the eye.</summary>
        public readonly bool Project(Vector3 w, out Vector2 screen, out float z)
        {
            var p = ToView(w);
            z = p.Z;
            screen = default;
            if (p.Z < 16f) return false;
            screen = new Vector2(Cx + H * p.X / p.Z, Cy + H * p.Y / p.Z);
            return true;
        }

        /// <summary>The world point at game pixel <paramref name="s"/> and view depth <paramref name="z"/>.</summary>
        public readonly Vector3 Unproject(Vector2 s, float z)
            => ToWorld(new Vector3((s.X - Cx) * z / H, (s.Y - Cy) * z / H, z));
    }

    public static View ReadView(IMemory m)
    {
        const uint vm = CameraBlock.ViewMatrix;
        float E(uint off) => (short)m.ReadU16(vm + off) / 4096f;
        var cam = Camera.Read(m);
        return new View
        {
            R = new Matrix4x4(E(0), E(2), E(4), 0, E(6), E(8), E(10), 0, E(12), E(14), E(16), 0, 0, 0, 0, 1),
            T = new Vector3((int)m.ReadU32(vm + 0x14), (int)m.ReadU32(vm + 0x18), (int)m.ReadU32(vm + 0x1C)),
            Cam = new Vector3(cam.X, cam.Y, cam.Z),
            H = Math.Max(1f, GteDepth.ProjH),
            Cx = GteDepth.ProjCx,
            Cy = GteDepth.ProjCy,
        };
    }

    static readonly (float Key, int Index)[] _order = new (float, int)[256];

    public static void BeforeDrawOTag(CpuContext c, IMemory m)
    {
        if (!RemasterUniforms.Enabled || _lights.Length == 0) return;
        if (FramePacing.FirstWalkOfTick(ref _tickFrame)) _ticks++;

        var v = ReadView(m);
        int n = 0, culled = 0;
        for (int i = 0; i < _lights.Length && n < _order.Length; i++)
        {
            ref readonly var l = ref _lights[i];
            var p = v.ToView(l.Position);
            float r = l.Radius;
            // Behind the eye, or past the draw window in any direction.
            if (p.Z + r < 0f || p.Length() - r > Reach) { culled++; continue; }
            _order[n++] = (MathF.Max(p.Length() - r, 0f), i);
        }
        Array.Sort(_order, 0, n, Comparer<(float Key, int Index)>.Create((a, b) => a.Key.CompareTo(b.Key)));
        int sent = Math.Min(n, RemasterUniforms.MaxLights);
        culled += n - sent;

        double t = _ticks / 20.0;
        for (int k = 0; k < sent; k++)
        {
            ref readonly var l = ref _lights[_order[k].Index];
            var p = v.ToView(l.Position);
            int o = k * 4;
            RemasterUniforms.LightPos[o] = p.X;
            RemasterUniforms.LightPos[o + 1] = p.Y;
            RemasterUniforms.LightPos[o + 2] = p.Z;
            RemasterUniforms.LightPos[o + 3] = l.Radius;
            float s = l.Intensity * Flicker(l, t, _order[k].Index);
            RemasterUniforms.LightCol[o] = l.Colour.X * s;
            RemasterUniforms.LightCol[o + 1] = l.Colour.Y * s;
            RemasterUniforms.LightCol[o + 2] = l.Colour.Z * s;
            if (l.Spot)
            {
                var d = v.Rotate(l.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(l.Direction) : Vector3.UnitY);
                float outer = Math.Clamp(l.ConeOuter, 1f, 89f), inner = Math.Clamp(l.ConeInner, 0f, outer);
                RemasterUniforms.LightCol[o + 3] = MathF.Cos(inner * MathF.PI / 180f);
                RemasterUniforms.LightDir[o] = d.X;
                RemasterUniforms.LightDir[o + 1] = d.Y;
                RemasterUniforms.LightDir[o + 2] = d.Z;
                RemasterUniforms.LightDir[o + 3] = MathF.Cos(outer * MathF.PI / 180f);
                // Equal cones would make the edge a step smoothstep cannot take.
                if (RemasterUniforms.LightDir[o + 3] >= RemasterUniforms.LightCol[o + 3])
                    RemasterUniforms.LightDir[o + 3] = RemasterUniforms.LightCol[o + 3] - 1e-4f;
            }
            else
            {
                RemasterUniforms.LightCol[o + 3] = -1f;
                RemasterUniforms.LightDir[o] = RemasterUniforms.LightDir[o + 1] = RemasterUniforms.LightDir[o + 2] = 0f;
                RemasterUniforms.LightDir[o + 3] = -2f;
            }
        }
        RemasterUniforms.Publish(sent);
        Sent = sent;
        Culled = culled;
        Frames++;
    }

    /// <summary>1 less up to the light's amount, varying smoothly at about its rate:
    /// value noise on the world clock, a different phase per light.</summary>
    static float Flicker(in Pack.Light l, double t, int seed)
    {
        if (l.FlickerAmount <= 0f || l.FlickerHz <= 0f) return 1f;
        double x = t * l.FlickerHz;
        long k = (long)Math.Floor(x);
        float f = (float)(x - k);
        f = f * f * (3f - 2f * f);
        float a = Hash(k, seed), b = Hash(k + 1, seed);
        return 1f - Math.Clamp(l.FlickerAmount, 0f, 1f) * (a + (b - a) * f);
    }

    static float Hash(long k, int seed)
    {
        ulong h = (ulong)k * 0x9E3779B97F4A7C15ul ^ (ulong)(seed + 1) * 0xC2B2AE3D27D4EB4Ful;
        h ^= h >> 29;
        h *= 0xBF58476D1CE4E5B9ul;
        h ^= h >> 32;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }

    public string Probe()
        => Refused != null ? $"lights refused: {Refused}"
         : $"lights {Authored} authored, {Sent} sent, {Culled} culled; " +
           $"{RemasterUniforms.Uploads} upload(s), {RemasterUniforms.LitBatches} lit batch(es)" +
           (RemasterUniforms.Supported ? "" : " (the backend has no light term)") +
           (Authored > 0 && !PerPixelLighting.Enabled ? " (per-pixel lighting is off, so nothing is lit)" : "");
}
