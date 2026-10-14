using System.Numerics;
using RecompOne.Runtime;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using PsxGpu = RecompOne.Runtime.Gpu;

namespace Kf2;

/// <summary>
/// A magic projectile gives off light: a point light at each glowing effect the object
/// walk submits, in the colour of its own texels.
///
///     KF2_PROJECTILE_LIGHTS=0|1        overrides the saved Video ▸ Enhancements switch (on by default)
///     KF2_PROJECTILE_LIGHTS_PROBE=1    a line per effect mesh the first time it is seen
///     KF2_PROJECTILE_LIGHTS_RADIUS=5120 KF2_PROJECTILE_LIGHTS_INTENSITY=1.5
///
/// Only stage 5's table (the effects, <see cref="ModelKind.Effect"/>) is looked at, and
/// of it only what the game draws as light: a submit whose faces are mostly added to the
/// picture (blend mode 1, or 3, the quarter add), by the forced-blend assembler or by
/// the mesh's own semi-transparent faces. An arrow or a thrown rock is a lit, opaque
/// model and never qualifies; neither does smoke (averaged) or a darkening effect
/// (subtracted). The colour is the mean of the added texels, read off the CPU's copy of
/// VRAM through each face's CLUT, and a mesh too dark to light anything is left out.
/// Decided once per mesh and area. The light goes out through
/// <see cref="Remaster.Lights"/>, which needs per-pixel lighting and Fast geometry, and
/// runs with or without the remaster. A light whose effect is gone is not dropped: it
/// fades out over <see cref="FadeSeconds"/> where it was last seen. See "Magic
/// projectiles give off light" in docs/RENDERING.md.
/// </summary>
public static class ProjectileLights
{
    public static bool On { get; private set; }
    static bool _probe;
    static bool? _forced;

    public const string OnKey = "kf2.projectilelights.on";
    public const bool DefaultOn = true;

    public static float Radius = 5120f, Intensity = 1.5f;

    /// <summary>A little life in the light: up to this much dimmer, at about this rate.</summary>
    public const float Flicker = 0.2f, FlickerHz = 6f;

    /// <summary>A mesh whose added texels are darker than this (the brightest channel
    /// of their mean, 0..1) lights nothing.</summary>
    const float MinBrightness = 0.2f;

    /// <summary>The share of a mesh's texel area that must be added for it to glow.</summary>
    const float MinAdded = 0.5f;

    const uint PolyModelTable = 0x8018E19C;

    public readonly record struct Glow(bool Lit, Vector3 Colour, float Brightness, float Added, int Faces);

    static readonly Dictionary<(uint Face, uint Count, uint Pick), Glow> _known = new();

    /// <summary>What an effect record draws, by the three bytes the walk hands the
    /// submitter: the model (<c>u8[+0x3] + 0x28</c>), the clip byte (<c>u8[+0x4]</c>)
    /// and the assembler (<c>u8[+0x9]</c>).</summary>
    static readonly Dictionary<(uint Model, uint Clip, uint Pick), Glow> _byRecord = new();
    static bool _listening;

    /// <summary>One light a walk found: where, its colour, how bright, and how many
    /// effects it stands for.</summary>
    public struct Spot
    {
        public Vector3 Position, Colour;
        public float Brightness;
        public int Count;
        /// <summary>1 while its effect is drawn; down to 0 once the effect is gone.</summary>
        public float Fade;
    }

    /// <summary>How long a light takes to go out once its effect is gone.</summary>
    public const float FadeSeconds = 0.35f;

    /// <summary>A light of the last walk that this one did not continue: its last
    /// place, and when it started going out.</summary>
    struct Fading
    {
        public Spot Spot;
        public long Since;
    }

    static readonly List<Fading> _fading = new();
    static Spot[] _previous = new Spot[32];
    static int _previousN;

    /// <summary>A light of this walk nearer than this to one of the last, of one colour,
    /// is the same light moved on: a bolt can cover a good way in a frame.</summary>
    const float Follow = 4096f;

    static Spot[] _walking = new Spot[32], _frame = new Spot[32];
    static int _walkingN, _frameN;

    /// <summary>The last walk's lights, a trail's segments merged into one.</summary>
    public static ReadOnlySpan<Spot> Frame => _frame.AsSpan(0, _frameN);

    /// <summary>Effects nearer than this, of one colour, are one light: a bolt is drawn
    /// as a head and a trail of segments.</summary>
    const float Merge = 2048f;

    /// <summary>Meshes decided, and of them found to glow; never reset.</summary>
    public static long Decided, Glowing, Notes;

    /// <summary>The last frame's lights.</summary>
    public static int Sent;

    public static void Configure(string? on, string? probe, string? radius, string? intensity)
    {
        if (!string.IsNullOrWhiteSpace(on)) _forced = on.Trim() != "0";
        On = _forced ?? DefaultOn;
        _probe = probe?.Trim() is { } p && p != "" && p != "0";
        static bool F(string? s, out float v)
            => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
        if (F(radius, out float r) && r > 0f) Radius = Math.Clamp(r, 256f, 16384f);
        if (F(intensity, out float k) && k >= 0f) Intensity = Math.Clamp(k, 0f, Remaster.Pack.MaxLight);
        // The saved key is only readable once the runtime is up (see EvenFog).
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            On = _forced ?? Runtime.View.GetBool(OnKey, DefaultOn);
            Console.WriteLine($"[KF2] projectile lights: {(On ? "on" : "off")}, radius {Radius}, intensity {Intensity}");
        });
    }

    /// <summary>Switched from the shell or the settings, for the session; the caller saves.</summary>
    public static void Set(bool on)
    {
        On = on;
        if (!on) Forget();
    }

    static void Forget()
    {
        _fading.Clear();
        _previousN = _frameN = 0;
    }

    /// <summary>From the submitter, for any effect it draws (the walk's, or the
    /// planar walk's replay of one), once its mesh is selected: the record's three
    /// bytes, <paramref name="sub"/> the sub-model and <paramref name="pick"/> the
    /// assembler the submitter took them to.</summary>
    public static void Learn(PSMemory mem, uint model, uint clip, uint rawPick, uint sub, uint pick)
    {
        if (!On && !_probe) return;
        Notes++;
        if (!_listening)
        {
            Event.AddListener<OverlayLoadedEvent>(_ => { _known.Clear(); _byRecord.Clear(); Forget(); });
            _listening = true;
        }
        uint table = mem.ReadU32(PolyModelTable);
        uint header = (sub & 0xFFFFu) * 28u + 0xCu + table;
        uint count = mem.ReadU32(header + 0x14u);
        uint face = mem.ReadU32(header + 0x10u) + 0xCu + table;
        var key = (face, count, pick);
        if (!_known.TryGetValue(key, out var g))
        {
            g = Measure(mem, face, count, pick);
            if (_known.Count > 4096) _known.Clear();
            _known[key] = g;
            Decided++;
            if (g.Lit) Glowing++;
            if (_probe)
                Console.WriteLine($"[KF2] projectile lights: effect model {model} clip 0x{clip:X2} assembler 0x{pick:X2}: " +
                                  $"{g.Faces} face(s), {g.Added:P0} added, mean ({g.Colour.X:F2} {g.Colour.Y:F2} {g.Colour.Z:F2}) " +
                                  $"brightness {g.Brightness:F2}: {(g.Lit ? "glows" : "no light")}");
        }
        if (_byRecord.Count > 4096) _byRecord.Clear();
        _byRecord[(model, clip, rawPick)] = g;
    }

    public static void BeginWalk() => _walkingN = 0;

    /// <summary>From the effect walk, for a record it will draw in the eye's view or the
    /// mirror's: a light if what it draws has been found to glow.</summary>
    public static void Seen(PSMemory mem, uint rec)
    {
        if (!On) return;
        uint model = mem.ReadU8(rec + 3u) + 0x28u, clip = mem.ReadU8(rec + 4u), pick = mem.ReadU8(rec + 9u);
        if (!_byRecord.TryGetValue((model, clip, pick), out var g) || !g.Lit) return;
        var at = new Vector3((int)mem.ReadU32(rec + 0x14u), (int)mem.ReadU32(rec + 0x18u), (int)mem.ReadU32(rec + 0x1Cu));
        for (int i = 0; i < _walkingN; i++)
        {
            ref var o = ref _walking[i];
            if (Vector3.Dot(o.Colour, g.Colour) < 0.95f * o.Colour.Length() * g.Colour.Length()) continue;
            if (Vector3.DistanceSquared(o.Position / o.Count, at) > Merge * Merge) continue;
            o.Position += at;
            o.Brightness = MathF.Max(o.Brightness, g.Brightness);
            o.Count++;
            return;
        }
        if (_walkingN == _walking.Length) Array.Resize(ref _walking, _walkingN * 2);
        _walking[_walkingN++] = new Spot { Position = at, Colour = g.Colour, Brightness = g.Brightness, Count = 1 };
    }

    /// <summary>At most this many projectile lights a frame, so a spell that scatters
    /// leaves the authored ones their slots; past it the nearest two are merged.</summary>
    public const int MaxLights = 6;

    public static void EndWalk()
    {
        int n = _walkingN;
        while (n > MaxLights)
        {
            int a = 0, b = 1;
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                float d = Vector3.DistanceSquared(_walking[i].Position / _walking[i].Count, _walking[j].Position / _walking[j].Count);
                if (d < best) { best = d; a = i; b = j; }
            }
            ref var x = ref _walking[a];
            ref readonly var y = ref _walking[b];
            x.Colour = Vector3.Normalize(x.Colour * x.Count + y.Colour * y.Count);
            x.Colour /= MathF.Max(x.Colour.X, MathF.Max(x.Colour.Y, x.Colour.Z));
            x.Position += y.Position;
            x.Brightness = MathF.Max(x.Brightness, y.Brightness);
            x.Count += y.Count;
            _walking[b] = _walking[--n];
        }
        for (int i = 0; i < n; i++)
        {
            _walking[i].Position /= _walking[i].Count;
            _walking[i].Count = Math.Max(1, _walking[i].Count);
        }

        // The last walk's lights that none of this walk's continues start going out.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int j = 0; j < _previousN; j++)
        {
            ref readonly var o = ref _previous[j];
            bool kept = false;
            for (int i = 0; i < n && !kept; i++)
                kept = Same(o, _walking[i]);
            if (!kept) _fading.Add(new Fading { Spot = o, Since = now });
        }
        // A fading light its effect has come back to is live again.
        _fading.RemoveAll(f => Fade(f, now) <= 0f || Continued(f.Spot, n));
        if (_fading.Count > MaxLights) _fading.RemoveRange(0, _fading.Count - MaxLights);

        if (_previous.Length < n) _previous = new Spot[_walking.Length];
        Array.Copy(_walking, _previous, n);
        _previousN = n;

        // The live ones first; the fading ones only in the slots they leave.
        int total = Math.Min(MaxLights, n + _fading.Count);
        if (_frame.Length < total) _frame = new Spot[Math.Max(total, _walking.Length)];
        for (int i = 0; i < n; i++)
        {
            _frame[i] = _walking[i];
            _frame[i].Fade = 1f;
        }
        for (int i = n, f = _fading.Count - 1; i < total; i++, f--)
        {
            _frame[i] = _fading[f].Spot;
            _frame[i].Fade = Fade(_fading[f], now);
        }
        _frameN = total;
    }

    static bool Same(in Spot a, in Spot b)
        => Vector3.Dot(a.Colour, b.Colour) >= 0.95f * a.Colour.Length() * b.Colour.Length()
           && Vector3.DistanceSquared(a.Position, b.Position) <= Follow * Follow;

    static bool Continued(in Spot s, int n)
    {
        for (int i = 0; i < n; i++)
            if (Same(s, _walking[i])) return true;
        return false;
    }

    /// <summary>1 when it started going out, 0 at <see cref="FadeSeconds"/>, eased so
    /// the light does not end on a corner.</summary>
    static float Fade(in Fading f, long now)
    {
        float t = (float)((now - f.Since) / (double)System.Diagnostics.Stopwatch.Frequency) / FadeSeconds;
        if (t >= 1f) return 0f;
        float k = 1f - t;
        return k * k;
    }

    /// <summary>How each face blends: the forced-blend assembler (<c>pick &lt; 0xFE</c>)
    /// draws every face in mode <c>pick &amp; 3</c>; the lit and clipped ones draw a face
    /// blended only where its own command says so, in its texture page's mode.</summary>
    static Glow Measure(PSMemory mem, uint face, uint count, uint pick)
    {
        var vram = Runtime.Gpu?.Vram;
        if (vram == null || count == 0 || count > 4096 || !InRam(face, 4)) return default;
        int forced = pick < 0xFEu ? (int)(pick & 3u) : -1;
        double addR = 0, addG = 0, addB = 0, addN = 0, area = 0, added = 0;
        int faces = 0;
        for (uint i = 0; i < count; i++)
        {
            if (!InRam(face, 0x20)) return default;
            uint word = mem.ReadU32(face);
            uint f = face + 4u;
            uint cmd = word >> 24;
            face = f + ((word >> 6) & 0x3FCu);
            int corners = (cmd & 0xFDu) switch { 0x24u or 0x34u => 3, 0x2Cu or 0x3Cu => 4, _ => 0 };
            if (corners == 0) continue;
            faces++;
            uint clut = mem.ReadU16(f + 2u), tpage = mem.ReadU16(f + 6u) & 0x1FFu;
            int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
            for (int k = 0; k < corners; k++)
            {
                uint uv = mem.ReadU16(f + (uint)k * 4u);
                int u = (int)(uv & 0xFF), v = (int)(uv >> 8);
                u0 = Math.Min(u0, u); v0 = Math.Min(v0, v); u1 = Math.Max(u1, u); v1 = Math.Max(v1, v);
            }
            int mode = forced >= 0 ? forced : (cmd & 2u) != 0 ? (int)((tpage >> 5) & 3u) : -1;
            double a = (u1 - u0 + 1) * (v1 - v0 + 1);
            area += a;
            if (mode is not (1 or 3)) continue;
            added += a;
            Sample(vram, (int)tpage, (int)clut, u0, v0, u1, v1, ref addR, ref addG, ref addB, ref addN);
        }
        if (faces == 0 || area <= 0) return default;
        float share = (float)(added / area);
        if (addN <= 0) return new Glow(false, Vector3.Zero, 0f, share, faces);
        var mean = new Vector3((float)(addR / addN), (float)(addG / addN), (float)(addB / addN));
        float bright = MathF.Max(mean.X, MathF.Max(mean.Y, mean.Z));
        bool lit = share >= MinAdded && bright >= MinBrightness;
        return new Glow(lit, bright > 0f ? mean / bright : Vector3.Zero, bright, share, faces);
    }

    /// <summary>The texels under a face's UV box, through its CLUT, summed as 0..1 RGB;
    /// a texel of 0 is transparent and left out. At most 32x32 samples a face.</summary>
    static void Sample(ushort[] vram, int tpage, int clut, int u0, int v0, int u1, int v1,
                       ref double r, ref double g, ref double b, ref double n)
    {
        int depth = (tpage >> 7) & 3;
        int px = (tpage & 0xF) * 64, py = ((tpage >> 4) & 1) * 256;
        int cx = (clut & 0x3F) * 16, cy = (clut >> 6) & 0x1FF;
        int su = Math.Max(1, (u1 - u0 + 1) / 32), sv = Math.Max(1, (v1 - v0 + 1) / 32);
        const int W = PsxGpu.VramWidth, H = PsxGpu.VramHeight;
        for (int v = v0; v <= v1; v += sv)
        for (int u = u0; u <= u1; u += su)
        {
            int y = py + v;
            if (y >= H) continue;
            ushort t;
            if (depth == 0)
            {
                int x = px + (u >> 2);
                if (x >= W) continue;
                int idx = (vram[y * W + x] >> ((u & 3) * 4)) & 0xF;
                if (cx + idx >= W || cy >= H) continue;
                t = vram[cy * W + cx + idx];
            }
            else if (depth == 1)
            {
                int x = px + (u >> 1);
                if (x >= W) continue;
                int idx = (vram[y * W + x] >> ((u & 1) * 8)) & 0xFF;
                if (cx + idx >= W || cy >= H) continue;
                t = vram[cy * W + cx + idx];
            }
            else
            {
                int x = px + u;
                if (x >= W) continue;
                t = vram[y * W + x];
            }
            if (t == 0) continue;
            r += (t & 0x1F) / 31.0;
            g += ((t >> 5) & 0x1F) / 31.0;
            b += ((t >> 10) & 0x1F) / 31.0;
            n++;
        }
    }

    /// <summary>The spell the cast button casts, <c>func_8002957C</c>'s argument to
    /// <c>func_80027DC0</c>.</summary>
    const uint SpellByte = 0x801994AB, MaxMp = 0x8019942A, Mp = 0x8019942C;

    /// <summary>The <c>projectiles</c> shell verb: <c>on</c>, <c>off</c>, or
    /// <c>spell N</c>, a test aid that selects spell N for the cast button and fills MP.</summary>
    public static string Shell(string arg)
    {
        var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length > 0 && a[0] is "on" or "1") Set(true);
        else if (a.Length > 0 && a[0] is "off" or "0") Set(false);
        else if (a.Length == 2 && a[0] == "spell" && byte.TryParse(a[1], out byte n) && Runtime.Mem is { } m)
        {
            m.WriteU8(SpellByte, n);
            m.WriteU16(Mp, m.ReadU16(MaxMp));
        }
        else if (a.Length > 0) return "{\"ok\":false,\"error\":\"projectiles [on|off|spell <n>]\"}";
        int spell = Runtime.Mem?.ReadU8(SpellByte) ?? -1;
        return $"{{\"ok\":true,\"cmd\":\"projectiles\",\"on\":{(On ? "true" : "false")},\"sent\":{Sent}," +
               $"\"fading\":{_fading.Count},\"decided\":{Decided},\"glowing\":{Glowing},\"spell\":{spell},\"notes\":{Notes},\"lights\":{_frameN},\"published\":{RemasterUniforms.LightCount},\"litBatches\":{RemasterUniforms.LitBatches},\"backend\":{(RemasterUniforms.Supported ? "true" : "false")}}}";
    }

    static bool InRam(uint a, uint bytes)
    {
        uint lo = a - 0x80000000u;
        return a >= 0x80000000u && lo + bytes <= Runtime.RamSize && (a & 3u) == 0;
    }
}
