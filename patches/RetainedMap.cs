using System.Runtime.InteropServices;
using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// The area's map as a world-space mesh on the GPU (<see cref="RetainedScene"/>), so a
/// reflection draws the world again without the game's walks running twice.
///
///     KF2_RETAINED=1          reflections from the retained scene (off: not judged)
///     KF2_RETAINED_PLANAR=0   no planar reflections from it
///     KF2_RETAINED_CUBE=0     no camera cubemap
///     KF2_RETAINED_CUBESIZE=256  a cubemap face's size
///     KF2_RETAINED_CULL=0     draw the faces a mirror or a cube face sees from behind
///     KF2_RETAINED_GATE=0     reflect every map half, not only those the frame's walk drew
///     KF2_RETAINED_PROBE=1    the mesh, the check against the game's own vertices, the planes, GPU time
///     KF2_RETAINED_LIT=0      leave authored lights and glows out of the reflections
///     KF2_RETAINED_MIPS=0     no mip atlas in the reflections, only the anisotropic taps
///
/// **The map is data.** 80x80 tiles of two halves; a half names a mesh of the map's
/// model bank, a height, a quarter-turn and a light record (<see cref="TileWalk"/>).
/// The mesh's corners are placed as `func_80031950` places them -- the tile's centre,
/// the height times -128, the quarter-turn `func_80014B88` gives the view matrix --
/// and each face is lit once, flat, by the record's light matrix, colour matrix and
/// back colour through the GTE, exactly as `NormalColorCol` lights it in the
/// assembler: none of that depends on the camera. The depth cue does, so each corner
/// carries the record's DQA, DQB and curve and the vertex shader fogs it.
///
/// Rebuilt when the map block, the model bank or the remaster's materials change: a
/// hash once a walk (about 70 KB, a few microseconds). Only the light records a half
/// names are hashed, since the game rewrites others as it runs. Lit from the records
/// in the vertex shader (0085, <see cref="RecordsOn"/>), a corner carries its face's
/// normal and the records it is blended from, so a record the game rewrites is an
/// upload of the 64 records and not a rebuild; off, it carries its colour and cue.
///
/// See "The retained scene" in docs/RENDERING.md.
/// </summary>
public static class RetainedMap
{
    const uint MapBase = 0x801C8484;
    const uint MapBytes = 80u * 80u * 10u;
    const uint LightBase = 0x801930F0;
    const uint LightBytes = 64u * 104u;
    const uint Banks = 0x8018E18C;
    const uint LightColour = 0x8006E604;
    const uint VertexCache = 0x8018EB94;
    const uint ViewMatrix = CameraBlock.ViewMatrix;

    static bool? _forced;
    static bool _probe;

    public const string OnKey = "kf2.ssr.retained";

    public static bool Enabled => RetainedScene.Enabled;

    public static void Configure(string? on, string? planar, string? cube, string? cubeSize, string? cull, string? gate,
                                 string? probe, string? lit = null, string? mips = null)
    {
        RetainedScene.Lit = lit?.Trim() != "0";
        RetainedScene.Mips = mips?.Trim() != "0";
        RetainedScene.CullBack = cull?.Trim() != "0";
        RetainedScene.HalfGate = gate?.Trim() != "0";
        if (!string.IsNullOrWhiteSpace(on)) _forced = on != "0";
        RetainedScene.Planar = planar?.Trim() != "0";
        RetainedScene.Cube = cube?.Trim() != "0";
        if (int.TryParse(cubeSize, out int s) && s >= 16) RetainedScene.CubeSize = Math.Clamp(s, 16, 2048);
        _probe = !string.IsNullOrWhiteSpace(probe) && probe != "0";
        RetainedScene.Probe = _probe;
    }

    public static void Install()
    {
        RetainedScene.Enabled = _forced ?? false;
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            // No longer a setting: KF2_RETAINED=1 is the comparison, and the planar
            // walk, which draws into the same texture, wins over it.
            RetainedScene.Enabled = (_forced ?? false) && !PlanarReflections.Enabled;
            if (_forced == true && !Enabled) Console.WriteLine("[KF2] retained scene: reflections stood down for the planar walk");
            Console.WriteLine($"[KF2] retained scene: {(Enabled ? "on" : "off")}" +
                              (Enabled ? $", planar {(RetainedScene.Planar ? "on" : "off")}, " +
                                         $"cubemap {(RetainedScene.Cube ? $"{RetainedScene.CubeSize}px" : "off")}" : ""));
        });
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            _hash = 0; _lightHash = 0; _kept = false;
            _loadName = e.Name; _loadAt = Interrupts.ClockMs;
        });
    }

    // The probe's: the last overlay load, which each build is timed from.
    static string _loadName = "boot";
    static double _loadAt;

    public static void SetEnabled(bool on)
    {
        RetainedScene.Enabled = on;
        _hash = 0;
        _kept = false;
    }

    /// <summary>0085. The static map lit and fogged in the vertex shader from the light
    /// records, so a record the game rewrites is an upload and not a rebuild; off
    /// (<c>KF2_GPUWORLD_RECORDS=0</c>, <c>gpuworld records off</c>), each corner carries
    /// its colour and cue as before. A switch is a rebuild.</summary>
    public static bool RecordsOn = true;

    /// <summary>0085. Build the corners' colours and cues on the CPU as well and
    /// compare the shader's formula with them (<c>KF2_GPUWORLD_RECORDCHECK=1</c>).</summary>
    public static bool RecordCheck;

    static long _packs;
    static double _packMs;
    static string _lastPackWhy = "";
    public static long Packs => _packs;
    public static double LastPackMs => _packMs;
    public static string LastPackWhy => _lastPackWhy;
    public static long CheckCorners, CheckColour, CheckCue;
    public static float CheckCueWorst;

    /// <summary>Whether reflections are drawn from it; the reflection pass runs for
    /// it on its own. The backend draws only once its program built
    /// (<see cref="RetainedScene.Supported"/>).</summary>
    public static bool ReflectionsReady => RetainedScene.Enabled;

    /// <summary>Whether to build the static map: for reflections, for the authored
    /// lights' shadows (0077), which need nothing else of it, or for the GPU world (0085).</summary>
    public static bool Ready => ReflectionsReady || RetainedScene.ShadowsWanted || GpuWorld.Wanted;

    // ---- once a walk -------------------------------------------------------------

    static ulong _hash;
    static uint _table;

    /// <summary>From the start of <see cref="TileWalk"/>'s sweep, on the frame's own
    /// walk (not a mirrored one): rebuild if the map changed, and start the frame
    /// with the camera the walk is about to draw with.</summary>
    public static void AtWalk(CpuContext c, PSMemory mem)
    {
        if (!Ready) return;
        // What the hash carries of the water, read now rather than later in the frame.
        Reflections.ReadRects(mem);
        Waves.BeforeMap(mem);
        // The corners carry EvenFog's blends, so switching them is a rebuild too. Lit
        // from the records (0085), a record the game rewrites is only an upload.
        Hash(mem);
        ulong switches = (EvenFog.Enabled ? 1ul : 0ul) | (EvenFog.Blend ? 2ul : 0ul) | (EvenFog.Light ? 4ul : 0ul)
                       | (RecordsOn ? 8ul : 0ul);
        ulong geo = (_hm * 31 + _he) ^ switches << 60;
        bool lights = _hl != _lightHash;
        _lightHash = _hl;
        if (geo != _hash || (lights && !RecordsOn))
        {
            _hash = geo;
            // Only the halves changed: rebuild the cells near them (a door, a lift, a
            // Level edit). Anything else is a whole build.
            ulong rest = _he * 31 + switches;
            bool halvesOnly = rest == _builtRest && !(lights && !RecordsOn);
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool patched = halvesOnly && Patch(c, mem);
            if (patched && MapCheck) CheckPatch(c, mem);
            else if (!patched) Build(c, mem);
            _builtRest = rest;
            if (RecordsOn && !patched) PackRecords(c, mem);
            _buildMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _builds++;
            _lastWhy = _why;
            if (GpuWorld.Probing)
                Console.WriteLine($"[KF2] mapprobe: build {_builds}, {_buildMs:F2} ms " +
                                  $"({(patched ? $"patched {_patchedCells} changed cell(s)" : "whole")}: faces {_emitMs:F1}, " +
                                  $"weld {_seamsMs:F1}, scene {_publishMs:F1}), {_n / 3} triangle(s), for " +
                                  $"{(_why.Length > 0 ? _why : "the load's reset ")}" +
                                  $"{Interrupts.ClockMs - _loadAt:F0} ms after {_loadName}");
        }
        else if (lights)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            PackRecords(c, mem);
            _packMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _packs++;
            _lastPackWhy = _why;
        }
        // 0077. The models are captured for the lights' shadows too; 0085, the main
        // view draws the map from the frame's camera.
        if (!ReflectionsReady && !RetainedScene.ShadowModelsWanted && !GpuWorld.Wanted) return;
        RetainedScene.BeginFrame(ReadView(mem));
        GpuWorld.AtFrame();
        if (!ReflectionsReady) return;
        RetainedPlanes.Choose(mem);
        if (_probe) Report();
    }

    static void Hash(PSMemory mem)
    {
        var ram = mem.Ram;
        uint mask = (uint)ram.Length - 1u;
        ulong hm = MeshBytes(ram.Slice((int)(MapBase & mask), (int)MapBytes));
        ulong hl = LightHash(ram.Slice((int)(MapBase & mask), (int)MapBytes), ram.Slice((int)(LightBase & mask), (int)LightBytes));
        // 0085. The corners carry the water's rects and the swell's free positions.
        Span<uint> extra = [mem.ReadU32(Banks), (uint)Remaster.Surfaces.Serial, (uint)SurfaceMaterial.RectN,
                            (uint)WaterSwell.FreeHash, (uint)(WaterSwell.FreeHash >> 32), mem.ReadU32(LightColour)];
        ulong he = Mix(0xCBF29CE484222325ul, MemoryMarshal.AsBytes(extra));
        _why = (hm != _hm ? "map " : "") + (hl != _hl ? "lights" + LightDiff(ram.Slice((int)(LightBase & mask), (int)LightBytes)) + " " : "") + (he != _he ? $"bank/materials/rects/swell({extra[0]:X},{extra[1]},{extra[2]},{extra[3]}) " : "");
        _hm = hm; _hl = hl; _he = he;
    }

    /// <summary>Only what a half's mesh is built from -- its model, height, the low
    /// two bits of the turn and the low six of the light record -- since the game
    /// writes other bits of the map as it runs. A half that draws nothing (model 240
    /// or more) is one value whatever its other bytes hold: the game writes a standing
    /// object's footprint into the turn bits of empty upper halves (`0xFE`) as play
    /// goes on, and each write was a whole rebuild, 55-70 ms (see "A rebuild for a
    /// half that draws nothing" in docs/GPU_RENDERER.md).</summary>
    static ulong MeshBytes(ReadOnlySpan<byte> map)
    {
        ulong h = 0xCBF29CE484222325ul;
        for (int i = 0; i + 10 <= map.Length; i += 10)
        {
            ulong w = (ulong)HalfKey(map.Slice(i, 5)) | (ulong)HalfKey(map.Slice(i + 5, 5)) << 32;
            h = (h ^ w) * 0x100000001B3ul;
        }
        return h;
    }

    /// <summary>The bytes of one half its mesh is built from, or 0xFF for a half that
    /// draws nothing.</summary>
    public static uint HalfKey(ReadOnlySpan<byte> half) => half[0] >= 240
        ? 0xFFu
        : half[0] | (uint)half[1] << 8 | (uint)(half[2] & 3) << 16 | (uint)(half[4] & 0x3F) << 24;

    static ulong _hm, _hl, _he, _lightHash;

    /// <summary>The records a half names; the game rewrites others as it runs (area 6's
    /// record 63, every tick), and none of those lights the map.</summary>
    static ulong LightHash(ReadOnlySpan<byte> map, ReadOnlySpan<byte> lights)
    {
        ulong used = 0;
        for (int i = 0; i + 5 <= map.Length; i += 5)
            if (map[i] < 240) used |= 1ul << (map[i + 4] & 0x3F);
        ulong h = 0xCBF29CE484222325ul ^ used;
        for (int r = 0; r < 64; r++)
            if ((used >> r & 1ul) != 0) h = Mix(h * 0x100000001B3ul, lights.Slice(r * 104, 104));
        _usedRecords = used;
        return h;
    }
    static ulong _usedRecords;
    static readonly byte[] _lightsWas = new byte[LightBytes];

    // Which records moved, and at which of their fields (the probe's).
    static string LightDiff(ReadOnlySpan<byte> now)
    {
        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < 64; r++)
        {
            var a = now.Slice(r * 104, 104);
            var b = _lightsWas.AsSpan(r * 104, 104);
            if (a.SequenceEqual(b)) continue;
            string f = "";
            if (!a[..0x50].SequenceEqual(b[..0x50])) f += "L";
            if (!a[0x50..0x62].SequenceEqual(b[0x50..0x62])) f += "C";
            if (!a[0x62..0x65].SequenceEqual(b[0x62..0x65])) f += "B";
            if (!a[0x66..0x68].SequenceEqual(b[0x66..0x68])) f += "F";
            if (a[0x65] != b[0x65]) f += "p";
            sb.Append($" {r}:{f}");
        }
        now.CopyTo(_lightsWas);
        return sb.ToString();
    }
    static string _why = "";
    static string _lastWhy = "";

    static ulong Mix(ulong h, ReadOnlySpan<byte> bytes)
    {
        foreach (ulong w in MemoryMarshal.Cast<byte, ulong>(bytes)) h = (h ^ w) * 0x100000001B3ul;
        return h;
    }

    /// <summary>The camera block as the frame is drawn from: the GTE rotation and
    /// translation the view matrix holds, and the camera's world position.</summary>
    public static RetainedScene.View ReadView(IMemory mem)
    {
        var cam = Camera.Read(mem);
        Span<float> r = stackalloc float[9];
        for (int i = 0; i < 9; i++) r[i] = (short)mem.ReadU16(ViewMatrix + (uint)i * 2u) / 4096f;
        return new RetainedScene.View
        {
            R00 = r[0], R01 = r[1], R02 = r[2],
            R10 = r[3], R11 = r[4], R12 = r[5],
            R20 = r[6], R21 = r[7], R22 = r[8],
            CamX = cam.X, CamY = cam.Y, CamZ = cam.Z,
            Tx = (int)mem.ReadU32(ViewMatrix + 0x14u),
            Ty = (int)mem.ReadU32(ViewMatrix + 0x18u),
            Tz = (int)mem.ReadU32(ViewMatrix + 0x1Cu),
            H = GteDepth.ProjH, Cx = GteDepth.ProjCx, Cy = GteDepth.ProjCy,
        };
    }

    // ---- the mesh ------------------------------------------------------------------

    static RetainedScene.Vertex[] _tris = new RetainedScene.Vertex[1 << 16];
    static int _n;
    static (int X, int Y, int Z)[] _raw = new (int, int, int)[1 << 16];
    static readonly Gte.State _gteSaved = new();
    static readonly Dictionary<int, (float Dqa, float Dqb)> _cue = new();

    // What the last build made, for the probe.
    static int _halves, _faces, _skippedModels, _lightBlended, _fogBlended, _corners;
    static double _buildMs;
    static long _builds;

    /// <summary>Map builds, and what changed for the last; for the GPU world's probe.</summary>
    public static long Builds => _builds;
    public static string LastWhy => _lastWhy;
    public static double LastBuildMs => _buildMs;

    // ---- the build, whole or by the halves that changed ------------------------------

    const int HalfCount = 80 * 80 * 2;

    /// <summary>Where each half's corners begin in <see cref="_tris"/> (a half is
    /// `(tz * 80 + tx) * 2 + half`, the order the build walks), and its plane notes in
    /// <see cref="_notes"/>; the last entry is the end.</summary>
    static readonly int[] _halfStart = new int[HalfCount + 1], _noteStart = new int[HalfCount + 1];
    /// <summary>The same for a region built past the end of the map's corners.</summary>
    static readonly int[] _regionStart = new int[HalfCount + 1], _regionNoteStart = new int[HalfCount + 1];
    /// <summary>Each half's <see cref="HalfKey"/> as the kept map was built from it.</summary>
    static readonly uint[] _builtKey = new uint[HalfCount];
    /// <summary>Whether the kept map, its starts and keys may be patched: false until a
    /// whole build, and after a load.</summary>
    static bool _kept;
    /// <summary>Everything but the halves the kept map was built with: the bank, the
    /// materials, the water, the swell, the switches. A change there is a whole build.</summary>
    static ulong _builtRest;

    /// <summary>A patch rebuilds the cells within <see cref="PatchReach"/> of a changed
    /// half (EvenFog's blends read the neighbours' records, and the weld and the seam
    /// merge move corners a cell's mesh shares with the next), and builds one cell more
    /// around them so the weld and the merge see what the whole build sees there.</summary>
    const int PatchReach = 1;
    /// <summary>Past this many changed cells a whole build is as quick.</summary>
    const int PatchMostCells = 600;

    /// <summary>A plane-shaped face for <see cref="RetainedPlanes"/>, kept by half so a
    /// patch can hand the whole set over again.</summary>
    struct PlaneNote
    {
        public float X0, X1, X2, X3, Y0, Y1, Y2, Y3, Z0, Z1, Z2, Z3;
        public int Corners;
        public bool Semi;
        public byte Mat;
        public uint TPage, Rect;
    }

    static PlaneNote[] _notes = new PlaneNote[1024], _notesSpare = new PlaneNote[1024];
    static int _noteN;
    static RetainedScene.Vertex[] _trisSpare = new RetainedScene.Vertex[1 << 16];
    static (int X, int Y, int Z)[] _rawSpare = new (int, int, int)[1 << 16];
    static readonly bool[] _changed = new bool[80 * 80], _inner = new bool[80 * 80], _region = new bool[80 * 80];
    static int _patchedCells;
    static double _emitMs, _publishMs;
    static long _patches;

    /// <summary>KF2_GPUWORLD_MAPCHECK=1: after each patch, build the whole map as well
    /// and compare every corner; the whole build is kept.</summary>
    public static bool MapCheck = Environment.GetEnvironmentVariable("KF2_GPUWORLD_MAPCHECK")?.Trim() is "1";

    static void Build(CpuContext c, PSMemory mem)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        _n = 0;
        _noteN = 0;
        _patchedCells = 0;
        _checkPacked = false;
        _halves = _faces = _skippedModels = _lightBlended = _fogBlended = _corners = 0;
        _cue.Clear();
        _kept = false;
        _table = mem.ReadU32(Banks);
        if (_table == 0) { RetainedPlanes.Clear(); RetainedScene.SetStatic([]); return; }

        _slide.Clear();
        EmitCells(c, mem, null, _halfStart, _noteStart);
        _emitMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _seamsMoved = 0;
        var seamsAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Weld) { Slide(0, _n); CloseSeams(0, _n); }
        _seamsMs = System.Diagnostics.Stopwatch.GetElapsedTime(seamsAt).TotalMilliseconds;
        Publish();
        KeepKeys(mem);
        _kept = true;
        if (Environment.GetEnvironmentVariable("KF2_GPUWORLD_SEAMS") is { Length: > 0 } tj) SeamCensus(tj);
        if (Environment.GetEnvironmentVariable("KF2_GPUWORLD_DUMPMAP") is { Length: > 0 } dir) DumpMap(dir);
    }

    /// <summary>Rebuild only the cells near the halves whose <see cref="HalfKey"/>
    /// changed and splice them into the kept map, in place of a whole build. False when
    /// it cannot (nothing kept, another bank, too much changed): build it whole.</summary>
    static bool Patch(CpuContext c, PSMemory mem)
    {
        if (!_kept || mem.ReadU32(Banks) != _table) return false;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var map = mem.Ram.Slice((int)(MapBase & (uint)(mem.Ram.Length - 1)), (int)MapBytes);
        Array.Clear(_changed);
        int cells = 0;
        for (int h = 0; h < HalfCount; h++)
        {
            if (HalfKey(map.Slice(h * 5, 5)) == _builtKey[h] || _changed[h >> 1]) continue;
            _changed[h >> 1] = true;
            cells++;
        }
        if (cells > PatchMostCells) return false;
        Grow(_changed, _inner, PatchReach);
        Grow(_changed, _region, PatchReach + 1);

        // The region is built past the kept corners, then spliced.
        int keptN = _n, keptNotes = _noteN;
        _slide.Clear();
        EmitCells(c, mem, _region, _regionStart, _regionNoteStart);
        _emitMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _seamsMoved = 0;
        var seamsAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Weld) { Slide(keptN, _n); CloseSeams(keptN, _n); }
        _seamsMs = System.Diagnostics.Stopwatch.GetElapsedTime(seamsAt).TotalMilliseconds;
        Splice(keptN, keptNotes);
        Publish();
        KeepKeys(mem);
        _patchedCells = cells;
        _patches++;
        return true;
    }

    /// <summary>The cells within <paramref name="reach"/> of a marked one.</summary>
    static void Grow(bool[] from, bool[] to, int reach)
    {
        Array.Clear(to);
        for (int z = 0; z < 80; z++)
        for (int x = 0; x < 80; x++)
        {
            if (!from[z * 80 + x]) continue;
            for (int dz = -reach; dz <= reach; dz++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                int nz = z + dz, nx = x + dx;
                if ((uint)nz < 80u && (uint)nx < 80u) to[nz * 80 + nx] = true;
            }
        }
    }

    /// <summary>The kept map with each inner cell's halves taken from the region built
    /// past its end, into the spare buffers, which then become the kept ones.</summary>
    static void Splice(int keptN, int keptNotes)
    {
        int need = _n, notesNeed = _noteN;
        if (_trisSpare.Length < need) { _trisSpare = new RetainedScene.Vertex[_tris.Length]; _rawSpare = new (int, int, int)[_tris.Length]; }
        if (_notesSpare.Length < notesNeed) _notesSpare = new PlaneNote[_notes.Length];
        int n = 0, notes = 0;
        for (int h = 0; h < HalfCount; h++)
        {
            bool fresh = _inner[h >> 1];
            int from = fresh ? _regionStart[h] : _halfStart[h], to = fresh ? _regionStart[h + 1] : _halfStart[h + 1];
            int nFrom = fresh ? _regionNoteStart[h] : _noteStart[h], nTo = fresh ? _regionNoteStart[h + 1] : _noteStart[h + 1];
            _halfStart[h] = n;
            _noteStart[h] = notes;
            Array.Copy(_tris, from, _trisSpare, n, to - from);
            Array.Copy(_raw, from, _rawSpare, n, to - from);
            n += to - from;
            Array.Copy(_notes, nFrom, _notesSpare, notes, nTo - nFrom);
            notes += nTo - nFrom;
        }
        _halfStart[HalfCount] = n;
        _noteStart[HalfCount] = notes;
        (_tris, _trisSpare) = (_trisSpare, _tris);
        (_raw, _rawSpare) = (_rawSpare, _raw);
        (_notes, _notesSpare) = (_notesSpare, _notes);
        _n = n;
        _noteN = notes;
    }

    static void KeepKeys(PSMemory mem)
    {
        var map = mem.Ram.Slice((int)(MapBase & (uint)(mem.Ram.Length - 1)), (int)MapBytes);
        for (int h = 0; h < HalfCount; h++) _builtKey[h] = HalfKey(map.Slice(h * 5, 5));
    }

    /// <summary>The halves of the marked cells (all of them for null), appended to the
    /// corners from <see cref="_n"/>, each half's start written to <paramref name="start"/>.</summary>
    static void EmitCells(CpuContext c, PSMemory mem, bool[]? cells, int[] start, int[] noteStart)
    {
        uint rgbc = mem.ReadU32(LightColour);
        var snap = c.Snapshot();
        Gte.Save(_gteSaved);
        try
        {
            for (int h = 0; h < HalfCount; h++)
            {
                start[h] = _n;
                noteStart[h] = _noteN;
                int cell = h >> 1;
                if (cells != null && !cells[cell]) continue;
                uint rec = MapBase + (uint)(cell * 10 + (h & 1) * 5);
                uint model = mem.ReadU8(rec);
                if (model >= 240) continue;
                // The table's +4 is the far-model gate's limit (TileWalk.Beyond), not a
                // count: models past it are drawn unless that gate is set.
                if (!Sane(mem, model)) { _skippedModels++; continue; }
                Half(c, mem, rec, model, cell % 80, cell / 80, h & 1, rgbc);
            }
            start[HalfCount] = _n;
            noteStart[HalfCount] = _noteN;
        }
        finally
        {
            Gte.Load(_gteSaved);
            c.Restore(snap);
        }
    }

    /// <summary>The 45-degree walls' slides (<see cref="WeldFace"/>) over the corners
    /// in [from, to). A slide is taken only onto a corner something else is at: one the
    /// axis snap put there (the straight run beside the wall) or another wall's slide
    /// (two corner pieces meeting). Elsewhere the axis snap already meets the neighbour.</summary>
    static void Slide(int from, int to)
    {
        if (_slide.Count == 0) return;
        var corners = new HashSet<(int, int, int)>(to - from);
        for (int i = from; i < to; i++) corners.Add(((int)_tris[i].X, (int)_tris[i].Y, (int)_tris[i].Z));
        var met = new Dictionary<(int, int, int), int>();
        foreach (var (raw, at) in _slide)
            if (!float.IsNaN(at.X)) { var key = ((int)at.X, raw.Item2, (int)at.Z); met[key] = met.GetValueOrDefault(key) + 1; }
        foreach (var (key, count) in met) if (count > 1) corners.Add(key);
        for (int i = from; i < to; i++)
            if (_slide.TryGetValue(_raw[i], out var at) && !float.IsNaN(at.X)
                && corners.Contains(((int)at.X, _raw[i].Y, (int)at.Z)))
            { _tris[i].X = at.X; _tris[i].Z = at.Z; }
    }

    /// <summary>The corners to the scene and the plane notes to the planes.</summary>
    static void Publish()
    {
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        RetainedScene.SetStatic(_tris.AsSpan(0, _n));
        RetainedPlanes.Clear();
        Span<float> px = stackalloc float[4], py = stackalloc float[4], pz = stackalloc float[4];
        for (int i = 0; i < _noteN; i++)
        {
            ref readonly var p = ref _notes[i];
            px[0] = p.X0; px[1] = p.X1; px[2] = p.X2; px[3] = p.X3;
            py[0] = p.Y0; py[1] = p.Y1; py[2] = p.Y2; py[3] = p.Y3;
            pz[0] = p.Z0; pz[1] = p.Z1; pz[2] = p.Z2; pz[3] = p.Z3;
            RetainedPlanes.Note(px, py, pz, p.Corners, p.Semi, p.Mat, p.TPage, p.Rect);
        }
        RetainedPlanes.Finish();
        _publishMs = System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds;
    }

    static void NotePlane(Span<float> px, Span<float> py, Span<float> pz, int corners, bool semi, byte mat, uint tpage, uint rect)
    {
        if (_noteN == _notes.Length) Array.Resize(ref _notes, _notes.Length * 2);
        int last = corners - 1;
        _notes[_noteN++] = new PlaneNote
        {
            X0 = px[0], X1 = px[1], X2 = px[2], X3 = px[Math.Min(3, last)],
            Y0 = py[0], Y1 = py[1], Y2 = py[2], Y3 = py[Math.Min(3, last)],
            Z0 = pz[0], Z1 = pz[1], Z2 = pz[2], Z3 = pz[Math.Min(3, last)],
            Corners = corners, Semi = semi, Mat = mat, TPage = tpage, Rect = rect,
        };
    }

    /// <summary>KF2_GPUWORLD_MAPCHECK: the patched map against a whole build of the same
    /// bytes, corner by corner in the build's order; the whole build is kept.</summary>
    static void CheckPatch(CpuContext c, PSMemory mem)
    {
        var tris = _tris.AsSpan(0, _n).ToArray();
        var starts = (int[])_halfStart.Clone();
        int notes = _noteN;
        Build(c, mem);
        int bad = 0, badHalves = 0;
        var where = new System.Text.StringBuilder();
        for (int h = 0; h < HalfCount; h++)
        {
            int a = starts[h], b = _halfStart[h], len = starts[h + 1] - a;
            bool differs = len != _halfStart[h + 1] - b;
            for (int i = 0; !differs && i < len; i++)
                if (!MemoryMarshal.AsBytes(new ReadOnlySpan<RetainedScene.Vertex>(in tris[a + i]))
                        .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<RetainedScene.Vertex>(in _tris[b + i])))) { differs = true; bad++; }
            if (!differs) continue;
            badHalves++;
            if (badHalves <= 8) where.Append($" ({(h >> 1) % 80},{(h >> 1) / 80})h{h & 1}");
        }
        Console.WriteLine($"[KF2] mapcheck: patch {(badHalves == 0 ? "matches" : "DIFFERS from")} the whole build " +
                          $"({tris.Length} corner(s), {_n} whole; {notes} plane note(s), {_noteN} whole)" +
                          (badHalves > 0 ? $"; {badHalves} half(s) differ:{where}" : ""));
    }

    // KF2_GPUWORLD_DUMPMAP=dir: each build's triangles as x,y,z,flags per corner (CSV), for offline seam work.
    static void DumpMap(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"map_{_builds:D3}_{_table:X8}.csv");
        using var w = new StreamWriter(path);
        for (int i = 0; i < _n; i++)
            w.WriteLine($"{_tris[i].X},{_tris[i].Y},{_tris[i].Z},{_tris[i].Flags}");
        Console.WriteLine($"[KF2] dumpmap: {_n / 3} triangles, {_seamsMoved} seam corners merged in {_seamsMs:F1} ms -> {path}");
    }

    // KF2_GPUWORLD_SEAMS=x,z,radius[,ymax]: after a build, every map edge near (x,z) that only
    // one triangle uses, and every vertex lying on another triangle's edge.
    static void SeamCensus(string arg)
    {
        var a = arg.Split(',');
        double cx = double.Parse(a[0]), cz = double.Parse(a[1]), r = double.Parse(a[2]);
        double ymax = a.Length > 3 ? double.Parse(a[3]) : double.MaxValue;
        bool Near(in RetainedScene.Vertex v) => Math.Abs(v.X - cx) <= r && Math.Abs(v.Z - cz) <= r && v.Y <= ymax;
        var edges = new Dictionary<(long, long, long, long, long, long), int>();
        var points = new HashSet<(long, long, long)>();
        (long, long, long) K(in RetainedScene.Vertex v) => ((long)Math.Round(v.X), (long)Math.Round(v.Y), (long)Math.Round(v.Z));
        for (int i = 0; i + 2 < _n; i += 3)
        {
            if (!Near(_tris[i]) && !Near(_tris[i + 1]) && !Near(_tris[i + 2])) continue;
            for (int k = 0; k < 3; k++)
            {
                var p = K(_tris[i + k]); var q = K(_tris[i + (k + 1) % 3]);
                points.Add(p);
                var e = p.CompareTo(q) < 0 ? (p.Item1, p.Item2, p.Item3, q.Item1, q.Item2, q.Item3)
                                           : (q.Item1, q.Item2, q.Item3, p.Item1, p.Item2, p.Item3);
                edges[e] = edges.TryGetValue(e, out int n) ? n + 1 : 1;
            }
        }
        int open = 0, tj = 0;
        foreach (var (e, n) in edges)
        {
            if (n != 1) continue;
            open++;
            double ex = e.Item4 - e.Item1, ey = e.Item5 - e.Item2, ez = e.Item6 - e.Item3;
            double len2 = ex * ex + ey * ey + ez * ez;
            string on = "";
            foreach (var p in points)
            {
                double px = p.Item1 - e.Item1, py = p.Item2 - e.Item2, pz = p.Item3 - e.Item3;
                double t = (px * ex + py * ey + pz * ez) / len2;
                if (t <= 1e-4 || t >= 1 - 1e-4) continue;
                double dx = px - t * ex, dy = py - t * ey, dz = pz - t * ez;
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < 16) { on += $" [{p.Item1},{p.Item2},{p.Item3} t={t:F3} off={d:F2}]"; tj++; }
            }
            Console.WriteLine($"[KF2] seam: open edge ({e.Item1},{e.Item2},{e.Item3})-({e.Item4},{e.Item5},{e.Item6}){on}");
        }
        Console.WriteLine($"[KF2] seam: {edges.Count} edges near, {open} open, {tj} vertices on an open edge");
    }

    /// <summary>A mesh whose header points inside RAM and has a believable count.</summary>
    static bool Sane(PSMemory mem, uint model)
    {
        uint header = _table + model * 28u + 0xCu;
        uint ram = 0x80000000u + (uint)mem.Ram.Length;
        uint verts = _table + mem.ReadU32(header) + 0xCu, faces = _table + mem.ReadU32(header + 0x10u) + 0xCu;
        return verts >= 0x80000000u && verts < ram && faces >= 0x80000000u && faces < ram
            && mem.ReadU32(header + 4u) <= 4096u && mem.ReadU32(header + 0x14u) <= 4096u;
    }

    static void Half(CpuContext c, PSMemory mem, uint rec, uint model, int tx, int tz, int half, uint rgbc)
    {
        _halves++;
        uint rot = mem.ReadU8(rec + 2u) & 3u;
        uint light = LightBase + (mem.ReadU8(rec + 4u) & 0x3Fu) * 104u;
        for (int i = 0; i < 5; i++) Gte.WriteControl(8 + i, mem.ReadU32(light + rot * 20u + (uint)i * 4u));
        for (int i = 0; i < 5; i++) Gte.WriteControl(16 + i, mem.ReadU32(light + 0x50u + (uint)i * 4u));
        Gte.WriteControl(13, (uint)mem.ReadU8(light + 0x62u) << 4);
        Gte.WriteControl(14, (uint)mem.ReadU8(light + 0x63u) << 4);
        Gte.WriteControl(15, (uint)mem.ReadU8(light + 0x64u) << 4);
        int fog = (short)mem.ReadU16(light + 0x66u);
        var (dqa, dqb) = Cue(c, mem, fog);
        float curve = fog >= 32000 ? 0f : fog < 0 ? 1f : 2f;

        PolyAssembler.RetainedTile(rec, mem);
        if (RecordsOn) PolyAssembler.RetainedRecords(mem, rec);
        try { HalfFaces(c, mem, rec, model, tx, tz, half, rgbc, rot, fog, dqa, dqb, curve); }
        finally { PolyAssembler.EndTileRetained(); }
    }

    static void HalfFaces(CpuContext c, PSMemory mem, uint rec, uint model, int tx, int tz, int half, uint rgbc,
                          uint rot, int fog, float dqa, float dqb, float curve)
    {
        double wx = tx * 2048 + 1024, wz = tz * 2048 + 1024;
        double wy = -(int)mem.ReadU8(rec + 1u) * 128;

        uint header = _table + model * 28u + 0xCu;
        uint verts = _table + mem.ReadU32(header) + 0xCu;
        uint normals = _table + mem.ReadU32(header + 8u) + 0xCu;
        uint face = _table + mem.ReadU32(header + 0x10u) + 0xCu;
        uint count = mem.ReadU32(header + 0x14u);
        if (count > 4096) return;

        Span<float> px = stackalloc float[4], py = stackalloc float[4], pz = stackalloc float[4];
        Span<uint> uv = stackalloc uint[4], col = stackalloc uint[4];
        Span<float> qa = stackalloc float[4], qb = stackalloc float[4], qc = stackalloc float[4];
        Span<short> lx = stackalloc short[4], lz = stackalloc short[4], nzs = stackalloc short[4];
        Span<float> ex = stackalloc float[4], ez = stackalloc float[4];
        Span<uint> lw = stackalloc uint[4];
        for (int f = 0; f < (int)count; f++)
        {
            uint word = mem.ReadU32(face);
            uint at = face + 4u;
            uint type = (word >> 24) & 0xFDu;
            bool semi = ((word >> 24) & 2u) != 0;
            int corners = type == 0x2Cu ? 4 : type == 0x24u ? 3 : 0;
            if (corners != 0)
            {
                uint idx = at + (corners == 4 ? 0x12u : 0x0Eu);
                uint normalOff = mem.ReadU16(at + (corners == 4 ? 0x10u : 0x0Cu));
                for (int k = 0; k < corners; k++)
                {
                    uint v = verts + mem.ReadU16(idx + (uint)k * 2u);
                    lx[k] = (short)mem.ReadU16(v); lz[k] = (short)mem.ReadU16(v + 4u);
                    Rotate(rot, lx[k], (short)mem.ReadU16(v + 2u), lz[k], out float ox, out float oy, out float oz);
                    px[k] = (float)(wx + ox); py[k] = (float)(wy + oy); pz[k] = (float)(wz + oz);
                }
                uv[0] = mem.ReadU16(at);
                uv[1] = mem.ReadU16(at + 4u);
                uv[2] = mem.ReadU16(at + 8u);
                uv[3] = corners == 4 ? mem.ReadU16(at + 0xCu) : 0u;
                uint clut = mem.ReadU16(at + 2u), tpage = mem.ReadU16(at + 6u) & 0x1FFu;

                uint normal = normals + normalOff;
                uint lit = 0;
                if (!RecordsOn || RecordCheck)
                {
                    Gte.Write(0, mem.ReadU32(normal));
                    Gte.Write(1, mem.ReadU32(normal + 4u));
                    Gte.Write(6, rgbc);
                    Gte.NccsOp(12, true);
                    lit = Gte.Read(22);
                    // EvenFog's two blends, per corner, as the drawn tile has them.
                    for (int k = 0; k < corners; k++)
                    {
                        col[k] = PolyAssembler.RetainedLight(mem, normal, lit, lx[k], lz[k]);
                        (qa[k], qb[k], qc[k]) = CornerCue(c, mem, lx[k], lz[k], dqa, dqb, curve);
                        _corners++;
                        if (col[k] != lit) _lightBlended++;
                        if (qa[k] != dqa || qb[k] != dqb) _fogBlended++;
                    }
                }
                // 0085. Lit in the vertex shader: the normal, the records and the weights.
                if (RecordsOn)
                {
                    short nx = (short)mem.ReadU16(normal), ny = (short)mem.ReadU16(normal + 2u), nz = (short)mem.ReadU16(normal + 4u);
                    for (int k = 0; k < corners; k++)
                    {
                        lw[k] = PolyAssembler.RetainedLightWord(lx[k], lz[k], out int ax, out int az);
                        if (RecordCheck) Check(mem, c, lw[k], nx, ny, nz, ax, az, rgbc, col[k], qa[k], qb[k], qc[k]);
                        col[k] = (uint)(ushort)nx | (uint)(ushort)ny << 16;
                        nzs[k] = nz;
                        qa[k] = ax; qb[k] = az; qc[k] = 0f;
                    }
                }

                int u0 = 255, v0 = 255, u1 = 0, v1 = 0;
                for (int k = 0; k < corners; k++)
                {
                    int u = (int)(uv[k] & 0xFF), vv = (int)(uv[k] >> 8);
                    u0 = Math.Min(u0, u); v0 = Math.Min(v0, vv); u1 = Math.Max(u1, u); v1 = Math.Max(v1, vv);
                }
                uint rect = (uint)u0 | (uint)v0 << 8 | (uint)u1 << 16 | (uint)v1 << 24;
                byte mat = Remaster.Surfaces.FaceOf(tx, tz, half, (int)model, f);
                if (mat == 0 && Remaster.Surfaces.ByTexture
                    && Remaster.TextureKeys.Of((int)tpage, (int)clut, u0, v0, u1, v1, out var tex))
                    mat = Remaster.Surfaces.TextureId(tex);
                uint flags = RetainedScene.FlagRect | mat | RetainedScene.HalfFlag(tx, tz, half)
                           | (semi ? RetainedScene.FlagSemi | ((tpage >> 5) & 3u) << 8 : 0u);
                if (semi && SurfaceWater(tpage, u0, v0, u1, v1)) flags |= RetainedScene.FlagWater;
                // 0085. The corners the swell moves on the packets.
                uint swell = 0;
                if (semi)
                    for (int k = 0; k < corners; k++)
                        if (WaterSwell.IsFree(px[k], py[k], pz[k])) swell |= 1u << k;

                var t = new RetainedScene.Vertex
                {
                    R = lit & 0xFF, G = (lit >> 8) & 0xFF, B = (lit >> 16) & 0xFF,
                    Clut = clut & 0x7FFF, Texpage = tpage,
                    Dqa = dqa, Dqb = dqb, Curve = curve,
                    Rect = rect, Flags = flags, Rgbc = rgbc & 0xFFFFFFu,
                };
                // A quad is the strip 0,1,2 then 1,2,3, as the GPU draws it.
                px.CopyTo(ex); pz.CopyTo(ez);
                if (Weld) WeldFace(px, py, pz, ex, ez, corners);
                Emit(t, ex, py, ez, px, pz, uv, col, qa, qb, qc, lw, nzs, swell, 0, 1, 2);
                if (corners == 4)
                {
                    var tail = t;
                    tail.Flags |= RetainedScene.FlagQuadTail;
                    Emit(tail, ex, py, ez, px, pz, uv, col, qa, qb, qc, lw, nzs, swell, 1, 3, 2);
                }
                NotePlane(px, py, pz, corners, semi, mat, tpage, rect);
                _faces++;
            }
            face = at + ((word >> 6) & 0x3FCu);
        }
    }

    /// <summary>A tile's mesh reaches 10 units past its cell on each side, so two
    /// neighbours overlap by 20. Flat, the overlap hides the seam; on a slope it
    /// staggers the two faces vertically (10 units at a 1:2 grade), and a sight line
    /// grazing the ceiling passes between them: a hairline of background along the
    /// seam. A corner exactly 10 off a cell boundary is put on it, so the two edges
    /// are one. Translucent faces too, so a grate or the water stays on the wall it
    /// met; the swell's corners are interior, never on a boundary.
    /// <c>KF2_GPUWORLD_WELD=0</c> to compare. See "Hairlines in sloped ceilings" in
    /// docs/GPU_RENDERER.md.</summary>
    static readonly bool Weld = Environment.GetEnvironmentVariable("KF2_GPUWORLD_WELD")?.Trim() != "0";

    static float Overlap(float w)
    {
        float m = w - MathF.Round(w / 2048f) * 2048f;
        return MathF.Abs(m) == 10f ? m : 0f;
    }

    /// <summary>The weld: a corner 10 off a cell boundary is put on it, the axis alone,
    /// which on a face square to the grid keeps it on its face. A wall at 45 degrees in
    /// plan (a leaning wall's corner piece) has to slide the corner along its own run
    /// instead, onto the corner the straight run beside it has: snapping the axis alone
    /// left it 10 units off that corner, and on a leaning wall that is a hairline. It
    /// slides only onto a corner the game's mesh has, or onto another wall's slide. The slide is kept by the game's corner, not the face (<see cref="_slide"/>), so the
    /// ceiling and floor that share the wall's top and foot move with it and the weld
    /// stays a function of position. A corner off in both axes is a cell corner and
    /// snaps in both. See "Hairlines in leaning walls" in docs/GPU_RENDERER.md.</summary>
    static void WeldFace(Span<float> px, Span<float> py, Span<float> pz, Span<float> ex, Span<float> ez, int corners)
    {
        float ax = px[1] - px[0], ay = py[1] - py[0], az = pz[1] - pz[0];
        float bx = px[2] - px[0], by = py[2] - py[0], bz = pz[2] - pz[0];
        float nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        // The normal within 30 degrees of level and exactly 45 in plan, so a rock face,
        // irregular, keeps the axis snap. Its run is (-nz, nx).
        bool diagonal = len > 0f && MathF.Abs(ny) < 0.5f * len && nx != 0f && MathF.Abs(nx) == MathF.Abs(nz);
        for (int k = 0; k < corners; k++)
        {
            float mx = Overlap(px[k]), mz = Overlap(pz[k]);
            ex[k] -= mx; ez[k] -= mz;
            if (!diagonal || (mx != 0f) == (mz != 0f)) continue;
            (float X, float Z) to = mx != 0f ? (px[k] - mx, MathF.Round(pz[k] + mx * nx / nz))
                                             : (MathF.Round(px[k] + mz * nz / nx), pz[k] - mz);
            var key = ((int)px[k], (int)py[k], (int)pz[k]);
            // Two walls that disagree about a corner leave it on the axis snap.
            if (_slide.TryGetValue(key, out var had) && had != to) to = (float.NaN, float.NaN);
            _slide[key] = to;
        }
    }

    /// <summary>A seam the boundary weld cannot see: two parallel edges each used by one
    /// opaque face, lying within <see cref="SeamReach"/> of each other end for end, on an
    /// edge longer than twice that. The game's overlap on a 45-degree sloped ceiling sits
    /// 20 units off the square piece beside it and on no cell boundary; on a slope the
    /// two stagger like the overlap the weld closes, and a grazing sight line passes
    /// between them. Their corners are merged by position (each to the least of its
    /// group), so every face at a corner moves with it and the pass can only close an
    /// edge. See "Hairlines in leaning walls" in docs/GPU_RENDERER.md.</summary>
    const float SeamReach = 32f;

    static void CloseSeams(int from, int to)
    {
        static (int, int, int) Key(in RetainedScene.Vertex v) => ((int)MathF.Round(v.X), (int)MathF.Round(v.Y), (int)MathF.Round(v.Z));
        static ((int, int, int), (int, int, int)) Edge((int, int, int) p, (int, int, int) q) => p.CompareTo(q) < 0 ? (p, q) : (q, p);
        var uses = new Dictionary<((int, int, int), (int, int, int)), int>();
        for (int i = from; i + 2 < to; i += 3)
        {
            if ((_tris[i].Flags & RetainedScene.FlagSemi) != 0) continue;
            var a = Key(_tris[i]); var b = Key(_tris[i + 1]); var c = Key(_tris[i + 2]);
            if (a == b || b == c || a == c) continue;
            foreach (var e in (ReadOnlySpan<((int, int, int), (int, int, int))>)[Edge(a, b), Edge(b, c), Edge(c, a)])
                uses[e] = uses.GetValueOrDefault(e) + 1;
        }
        static (int, int, int) Cell((int X, int Y, int Z) p) => (p.X >> 6, p.Y >> 6, p.Z >> 6);
        var grid = new Dictionary<(int, int, int), List<((int, int, int), (int, int, int))>>();
        foreach (var (e, n) in uses)
        {
            if (n != 1) continue;
            foreach (var p in (ReadOnlySpan<(int, int, int)>)[e.Item1, e.Item2])
            {
                if (!grid.TryGetValue(Cell(p), out var list)) grid[Cell(p)] = list = new();
                list.Add(e);
            }
        }
        static float Dist((int X, int Y, int Z) p, (int X, int Y, int Z) q)
        { float x = p.X - q.X, y = p.Y - q.Y, z = p.Z - q.Z; return MathF.Sqrt(x * x + y * y + z * z); }
        var parent = new Dictionary<(int, int, int), (int, int, int)>();
        (int, int, int) Find((int, int, int) p) { while (parent.TryGetValue(p, out var q) && q != p) p = q; return p; }
        void Union((int, int, int) a, (int, int, int) b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (a.CompareTo(b) < 0) (a, b) = (b, a);
            parent[a] = b;
        }
        var near = new HashSet<((int, int, int), (int, int, int))>();
        foreach (var (e, n) in uses)
        {
            if (n != 1) continue;
            var (p0, p1) = e;
            near.Clear();
            foreach (var p in (ReadOnlySpan<(int X, int Y, int Z)>)[p0, p1])
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                    if (grid.TryGetValue(((p.X >> 6) + dx, (p.Y >> 6) + dy, (p.Z >> 6) + dz), out var list))
                        foreach (var f in list) near.Add(f);
            foreach (var f in near)
            {
                if (f.CompareTo(e) <= 0) continue;
                foreach (var (a, b) in (ReadOnlySpan<((int, int, int), (int, int, int))>)[(f.Item1, f.Item2), (f.Item2, f.Item1)])
                {
                    float d0 = Dist(p0, a), d1 = Dist(p1, b);
                    if (d0 <= SeamReach && d1 <= SeamReach && (d0 > 0f || d1 > 0f) && Dist(a, b) > 2f * SeamReach)
                    { Union(p0, a); Union(p1, b); }
                }
            }
        }
        if (parent.Count == 0) return;
        for (int i = from; i < to; i++)
        {
            var k = Key(_tris[i]);
            if (!parent.ContainsKey(k)) continue;
            var (x, y, z) = Find(k);
            _tris[i].X = x; _tris[i].Y = y; _tris[i].Z = z;
        }
        _seamsMoved = parent.Count;
    }

    static int _seamsMoved;
    static double _seamsMs;

    /// <summary>The game's corners a 45-degree wall slides, and where to.</summary>
    static readonly Dictionary<(int, int, int), (float X, float Z)> _slide = new();

    static void Emit(in RetainedScene.Vertex t, Span<float> px, Span<float> py, Span<float> pz, Span<float> rx, Span<float> rz, Span<uint> uv,
                     Span<uint> col, Span<float> qa, Span<float> qb, Span<float> qc, Span<uint> lw, Span<short> nz,
                     uint swell, int a, int b, int c)
    {
        if (_n + 3 > _tris.Length) { Array.Resize(ref _tris, _tris.Length * 2); Array.Resize(ref _raw, _tris.Length); }
        Put(t, px, py, pz, rx, rz, uv, col, qa, qb, qc, lw, nz, swell, a);
        Put(t, px, py, pz, rx, rz, uv, col, qa, qb, qc, lw, nz, swell, b);
        Put(t, px, py, pz, rx, rz, uv, col, qa, qb, qc, lw, nz, swell, c);
    }

    static void Put(in RetainedScene.Vertex t, Span<float> px, Span<float> py, Span<float> pz, Span<float> rx, Span<float> rz, Span<uint> uv,
                    Span<uint> col, Span<float> qa, Span<float> qb, Span<float> qc, Span<uint> lw, Span<short> nz,
                    uint swell, int k)
    {
        var v = t;
        if ((swell >> k & 1u) != 0) v.Flags |= RetainedScene.FlagSwell;
        v.X = px[k]; v.Y = py[k]; v.Z = pz[k];
        v.U = uv[k] & 0xFF; v.V = uv[k] >> 8;
        if (RecordsOn)
        {
            // The face's normal in place of a colour (RetainedScene.Vertex.Light).
            v.R = (short)(col[k] & 0xFFFF); v.G = (short)(col[k] >> 16); v.B = nz[k];
            v.Light = lw[k];
        }
        else { v.R = col[k] & 0xFF; v.G = (col[k] >> 8) & 0xFF; v.B = (col[k] >> 16) & 0xFF; }
        v.Dqa = qa[k]; v.Dqb = qb[k]; v.Curve = qc[k];
        _raw[_n] = ((int)rx[k], (int)py[k], (int)rz[k]);
        _tris[_n++] = v;
    }

    // ---- 0085. the light records, for the vertex shader -----------------------------

    static readonly int[] _records = new int[RetainedScene.RecordCount * RetainedScene.RecordInts];
    static readonly Gte.State _gtePack = new();

    /// <summary>The 64 records as RetainedScene.Records lays them out, handed to the
    /// backend. The fog word's DQA and DQB come from the game's own SetFogNear, which
    /// writes the GTE, so the registers and the CPU are put back.</summary>
    static void PackRecords(CpuContext c, PSMemory mem)
    {
        var snap = c.Snapshot();
        Gte.Save(_gtePack);
        try
        {
            for (int r = 0; r < RetainedScene.RecordCount; r++)
            {
                uint b = LightBase + (uint)r * 104u;
                int at = r * RetainedScene.RecordInts;
                for (int rot = 0; rot < 4; rot++)
                    for (int i = 0; i < 9; i++) _records[at + rot * 9 + i] = (short)mem.ReadU16(b + (uint)rot * 20u + (uint)i * 2u);
                for (int i = 0; i < 9; i++) _records[at + RetainedScene.RecLcm + i] = (short)mem.ReadU16(b + 0x50u + (uint)i * 2u);
                for (int i = 0; i < 3; i++) _records[at + RetainedScene.RecBk + i] = mem.ReadU8(b + 0x62u + (uint)i) << 4;
                int word = (short)mem.ReadU16(b + 0x66u);
                var (dqa, dqb) = Cue(c, mem, word);
                _records[at + RetainedScene.RecWord] = word;
                _records[at + RetainedScene.RecDqa] = (int)dqa;
                _records[at + RetainedScene.RecDqb] = (int)dqb;
                _records[at + RetainedScene.RecCurve] = word >= 32000 ? 0 : word < 0 ? 1 : 2;
            }
        }
        finally
        {
            Gte.Load(_gtePack);
            c.Restore(snap);
        }
        RetainedScene.SetRecords(_records);
    }

    /// <summary>The shader's formula (WorldVs's recordLit) in C#, against the colour and
    /// cue the CPU builds for the same corner. Needs the records packed first, so it
    /// packs them itself.</summary>
    static void Check(PSMemory mem, CpuContext c, uint l, short nx, short ny, short nz, int ax, int az, uint rgbc,
                      uint col, float qa, float qb, float qc)
    {
        if (!_checkPacked) { PackRecords(c, mem); _checkPacked = true; }
        var R = _records;
        int Rec(int r, int i) => R[r * RetainedScene.RecordInts + i];
        int own = (int)(l & 63u), rot = (int)(l >> 6 & 3u);
        int[] rec = [own, (int)(l >> 8 & 63u), (int)(l >> 14 & 63u), (int)(l >> 20 & 63u)];
        long[] k = [(4096L - ax) * (4096 - az), (l & 1u << 26) != 0 ? (long)ax * (4096 - az) : 0,
                    (l & 1u << 27) != 0 ? (4096L - ax) * az : 0, (l & 1u << 28) != 0 ? (long)ax * az : 0];
        long t = k[0] + k[1] + k[2] + k[3];
        bool Same(int a, int b)
        {
            for (int i = RetainedScene.RecLcm; i < RetainedScene.RecWord; i++) if (Rec(a, i) != Rec(b, i)) return false;
            return true;
        }
        bool mixL = (l & RetainedScene.LightLitBlend) != 0
            && ((k[1] != 0 && !Same(rec[1], own)) || (k[2] != 0 && !Same(rec[2], own)) || (k[3] != 0 && !Same(rec[3], own)));
        int Mix(int i)
        {
            if (!mixL) return Rec(own, i);
            long sum = 0;
            for (int j = 0; j < 4; j++) sum += k[j] * Rec(rec[j], i);
            return (int)Math.Round((double)sum / t, MidpointRounding.AwayFromZero);
        }
        Span<int> a = stackalloc int[3];
        for (int j = 0; j < 3; j++)
        {
            long v = (long)Rec(own, rot * 9 + 3 * j) * nx + (long)Rec(own, rot * 9 + 3 * j + 1) * ny + (long)Rec(own, rot * 9 + 3 * j + 2) * nz;
            a[j] = Math.Clamp((int)(v >> 12), 0, 0x7FFF);
        }
        uint rgb = 0;
        for (int ch = 0; ch < 3; ch++)
        {
            long v = (long)Mix(RetainedScene.RecBk + ch) << 12;
            for (int j = 0; j < 3; j++) v += (long)Mix(RetainedScene.RecLcm + 3 * ch + j) * a[j];
            int ir = Math.Clamp((int)(v >> 12), 0, 0x7FFF);
            int mac = (int)((((long)((rgbc >> (8 * ch)) & 0xFF) * ir) << 4) >> 12);
            rgb |= (uint)Math.Clamp(mac >> 4, 0, 0xFF) << (8 * ch);
        }
        int word = Rec(own, RetainedScene.RecWord);
        float da = Rec(own, RetainedScene.RecDqa), db = Rec(own, RetainedScene.RecDqb), dc = Rec(own, RetainedScene.RecCurve);
        bool mixF = (l & RetainedScene.LightFogBlend) != 0
            && ((k[1] != 0 && Rec(rec[1], RetainedScene.RecWord) != word) || (k[2] != 0 && Rec(rec[2], RetainedScene.RecWord) != word)
                || (k[3] != 0 && Rec(rec[3], RetainedScene.RecWord) != word));
        if (mixF)
        {
            double sa = 0, sb = 0;
            long most = 0;
            float bent = dc;
            for (int j = 0; j < 4; j++)
            {
                if (k[j] == 0) continue;
                int w = Rec(rec[j], RetainedScene.RecWord);
                if (w >= 32000) continue;
                sa += k[j] * (double)Rec(rec[j], RetainedScene.RecDqa);
                sb += k[j] * (double)Rec(rec[j], RetainedScene.RecDqb);
                if (k[j] > most) { most = k[j]; bent = w < 0 ? 1f : 2f; }
            }
            (da, db, dc) = ((float)(sa / t), (float)(sb / t), bent);
        }
        CheckCorners++;
        if (rgb != (col & 0xFFFFFFu)) CheckColour++;
        float e = Math.Max(Math.Abs(da - qa), Math.Abs(db - qb) / 4096f);
        if (e > 0f || dc != qc) CheckCue++;
        CheckCueWorst = Math.Max(CheckCueWorst, e);
    }

    static bool _checkPacked;

    /// <summary>0085. A blended face the reflection pass takes as water: in an averaging
    /// blend and on one of the water's rects, as SurfaceMaterial.Classify takes a packet.</summary>
    static bool SurfaceWater(uint tpage, int u0, int v0, int u1, int v1)
    {
        uint blend = (tpage >> 5) & 3u;
        if (blend is not (0u or 3u)) return false;
        int mode = (int)(tpage >> 7) & 3;
        int div = mode == 0 ? 4 : mode == 1 ? 2 : 1;
        int x0 = (int)(tpage & 0xF) * 64 + u0 / div, x1 = (int)(tpage & 0xF) * 64 + u1 / div + 1;
        int y0 = (int)((tpage >> 4) & 1) * 256 + v0, y1 = (int)((tpage >> 4) & 1) * 256 + v1 + 1;
        for (int i = 0; i < SurfaceMaterial.RectN; i++)
        {
            ref var r = ref SurfaceMaterial.Rects[i];
            if (x0 >= r.X + r.W || x1 <= r.X || y0 >= r.Y + r.H || y1 <= r.Y) continue;
            if (r.Material == SurfaceMaterial.Water) return true;
        }
        return false;
    }

    /// <summary>A corner's depth cue, blended between the fog words of the tiles around
    /// it with EvenFog's weights. The drawn tile blends each word's weight after its
    /// curve; a corner here carries one cue, so the words' DQA and DQB are blended
    /// before it, which agrees wherever the words share a curve and a side of its knee.
    /// A word with no fog weighs in as no cue.</summary>
    static (float, float, float) CornerCue(CpuContext c, PSMemory mem, short vx, short vz, float dqa, float dqb, float curve)
    {
        Span<int> words = stackalloc int[4];
        Span<long> k = stackalloc long[4];
        if (!PolyAssembler.RetainedFog(vx, vz, words, k)) return (dqa, dqb, curve);
        double a = 0, b = 0, total = 0;
        long most = 0;
        float bent = curve;
        for (int i = 0; i < 4; i++)
        {
            if (k[i] == 0 || words[i] == PolyAssembler.EmptyWord) continue;
            total += k[i];
            if (words[i] >= 32000) continue;
            var (wa, wb) = Cue(c, mem, words[i]);
            a += k[i] * (double)wa;
            b += k[i] * (double)wb;
            if (k[i] > most) { most = k[i]; bent = words[i] < 0 ? 1f : 2f; }
        }
        if (total <= 0) return (dqa, dqb, curve);
        return ((float)(a / total), (float)(b / total), bent);
    }

    /// <summary>A mesh corner turned as `func_80014B88` turns the view matrix for the
    /// half's quarter-turn: its columns, so the corner by the inverse.</summary>
    static void Rotate(uint rot, int x, int y, int z, out float ox, out float oy, out float oz)
    {
        switch (rot)
        {
            case 1: ox = z; oy = y; oz = -x; break;
            case 2: ox = -x; oy = y; oz = -z; break;
            case 3: ox = -z; oy = y; oz = x; break;
            default: ox = x; oy = y; oz = z; break;
        }
    }

    /// <summary>A light record's fog word as the GTE's DQA and DQB, from the game's
    /// own `SetFogNear` (`func_8002DDDC` hands it half the word's low fifteen bits
    /// and an H of 200).</summary>
    static (float, float) Cue(CpuContext c, PSMemory mem, int fog)
    {
        if (_cue.TryGetValue(fog, out var hit)) return hit;
        c.A0 = (uint)((fog & 0x7FFF) >> 1);
        c.A1 = 200u;
        c.RA = 0x8002DDFCu;
        KingsField2.SetFogNear(c, mem);
        var cue = ((float)(short)Gte.ReadControl(27), (float)(int)Gte.ReadControl(28));
        _cue[fog] = cue;
        return cue;
    }

    // ---- the check against the game's own vertices ----------------------------------

    public static bool Checking => _probe && Ready;

    static long _checked, _within, _worst;
    static double _sumErr;

    /// <summary>After the assembler has drawn a half, compare the vertex cache it
    /// left -- the GTE's own screen words for every corner of the mesh -- with the
    /// retained corners projected through the frame's camera. A subdivided mesh's
    /// cache holds the subdivider's corners, so it is left out.</summary>
    public static void CheckHalf(PSMemory mem, uint rec, uint model)
    {
        uint off = rec - MapBase;
        int tile = (int)(off / 10u), half = off % 10u >= 5u ? 1 : 0;
        int tx = tile % 80, tz = tile / 80;
        uint rot = mem.ReadU8(rec + 2u) & 3u;
        var v = ReadView(mem);
        double wx = tx * 2048 + 1024, wz = tz * 2048 + 1024, wy = -(int)mem.ReadU8(rec + 1u) * 128;
        uint header = _table + model * 28u + 0xCu;
        uint verts = _table + mem.ReadU32(header) + 0xCu;
        uint n = mem.ReadU32(header + 4u);
        if (n > 512) return;
        for (uint i = 0; i < n; i++)
        {
            uint p = verts + i * 8u;
            Rotate(rot, (short)mem.ReadU16(p), (short)mem.ReadU16(p + 2u), (short)mem.ReadU16(p + 4u),
                   out float ox, out float oy, out float oz);
            double dx = wx + ox - v.CamX, dy = wy + oy - v.CamY, dz = wz + oz - v.CamZ;
            double vx = v.R00 * dx + v.R01 * dy + v.R02 * dz + v.Tx;
            double vy = v.R10 * dx + v.R11 * dy + v.R12 * dz + v.Ty;
            double vz = v.R20 * dx + v.R21 * dy + v.R22 * dz + v.Tz;
            if (vz < 64.0) continue;
            double sx = v.Cx + v.H * vx / vz, sy = v.Cy + v.H * vy / vz;
            if (Math.Abs(sx) > 1000 || Math.Abs(sy) > 1000) continue;
            uint word = mem.ReadU32(VertexCache + i * 8u);
            // The GTE keeps the whole part of its 16.16 result, so compare with that.
            double ex = Math.Abs((short)(word & 0xFFFF) - Math.Floor(sx)), ey = Math.Abs((short)(word >> 16) - Math.Floor(sy));
            double e = Math.Max(ex, ey);
            _checked++;
            _sumErr += e;
            if (e <= 1.0) _within++;
            if ((long)e > _worst) _worst = (long)e;
        }
    }

    static long _mChecked, _mWithin, _mWorst;

    /// <summary>A model corner taken back to world space, projected through the
    /// frame's camera, against the screen word the transform left for it.</summary>
    public static void CheckCorner(in RetainedScene.View v, double x, double y, double z, uint word)
    {
        double dx = x - v.CamX, dy = y - v.CamY, dz = z - v.CamZ;
        double vx = v.R00 * dx + v.R01 * dy + v.R02 * dz + v.Tx;
        double vy = v.R10 * dx + v.R11 * dy + v.R12 * dz + v.Ty;
        double vz = v.R20 * dx + v.R21 * dy + v.R22 * dz + v.Tz;
        if (vz < 64.0) return;
        double sx = v.Cx + v.H * vx / vz, sy = v.Cy + v.H * vy / vz;
        if (Math.Abs(sx) > 1000 || Math.Abs(sy) > 1000) return;
        double e = Math.Max(Math.Abs((short)(word & 0xFFFF) - Math.Floor(sx)), Math.Abs((short)(word >> 16) - Math.Floor(sy)));
        _mChecked++;
        if (e <= 1.0) _mWithin++;
        if ((long)e > _mWorst) _mWorst = (long)e;
    }

    // ---- the probe ---------------------------------------------------------------

    static double _reportedAt;

    static void Report()
    {
        double now = Environment.TickCount64 / 1000.0;
        if (now < _reportedAt) return;
        _reportedAt = now + 2.0;
        int tris = RetainedScene.Static.Length / 3;
        Console.WriteLine($"[KF2] retained: {tris} static triangle(s) from {_faces} face(s) of {_halves} half/halves " +
                          $"({_skippedModels} refused as not a mesh), {_builds} build(s), last {_buildMs:F2} ms for {_lastWhy}; " +
                          $"ranges opaque {RetainedScene.StaticCount[0] / 3}, semi {RetainedScene.StaticCount[1] / 3}/" +
                          $"{RetainedScene.StaticCount[2] / 3}/{RetainedScene.StaticCount[3] / 3}/{RetainedScene.StaticCount[4] / 3}; " +
                          $"of {_corners} corner(s), {_lightBlended} lit and {_fogBlended} fogged between records (EvenFog {(EvenFog.Enabled ? "on" : "off")}); " +
                          $"into the draws {RetainedScene.LitLights} light(s), glow {(RetainedScene.LitGlow ? "on" : "off")}, " +
                          $"{RetainedScene.MipsFound} of {RetainedScene.MipsKeys} static texture(s) in the mip atlas");
        Console.WriteLine($"[KF2] retained: check {_checked} corner(s), {(_checked == 0 ? 0 : 100.0 * _within / _checked):F2}% within 1 px, " +
                          $"mean {(_checked == 0 ? 0 : _sumErr / _checked):F3} px, worst {_worst} px; models {_mChecked} corner(s), " +
                          $"{(_mChecked == 0 ? 0 : 100.0 * _mWithin / _mChecked):F2}% within 1 px, worst {_mWorst} px");
        _mChecked = _mWithin = _mWorst = 0;
        Console.WriteLine($"[KF2] retained: {RetainedPlanes.Describe()}");
        Console.WriteLine($"[KF2] retained: models captured by table: creature {RetainedModels.ByKind[0]}, object {RetainedModels.ByKind[1]}, " +
                          $"effect {RetainedModels.ByKind[2]}, sprite {RetainedModels.ByKind[3]}; faces no capture reads: " +
                          (RetainedModels.Unread.Count == 0 ? "none" : string.Join(", ", RetainedModels.Unread.Select(p => $"0x{p.Key:X2} x{p.Value}"))));
        long facing = RetainedScene.FrontPixels + RetainedScene.BackPixels;
        Console.WriteLine($"[KF2] retained: faces {(RetainedScene.CullBack ? "culled" : "not culled")}; " +
                          (RetainedScene.CullBack
                              ? $"{(facing == 0 ? 0 : 100.0 * RetainedScene.BackPixels / facing):F1}% of the planes' opaque pixels would show a face from behind with culling off"
                              : "no count") +
                          (RetainedScene.HalfGate
                              ? $"; with every half reflected, {(RetainedScene.FrontPixels + RetainedScene.UndrawnPixels == 0 ? 0 : 100.0 * RetainedScene.UndrawnPixels / (RetainedScene.FrontPixels + RetainedScene.UndrawnPixels)):F1}% of them would be a map half the game did not draw"
                              : "; every half reflected") +
                          $"; {RetainedScene.OldCullVisible} mirrored chunk draw(s) the old distance cull dropped, keeping up to {RetainedScene.OldCullKeep:F2} of their colour");
        // The reflection pass's readback (KF2_SSR_PROBE=1): what each reflective pixel
        // took, and where the planes and the cubemap both answered, how far apart.
        Console.WriteLine($"[KF2] retained: last readback {PlanarReflections.PlanarPct:F1}% of reflective pixels planar, " +
                          $"{ScreenReflections.HitPct:F1}% from the cubemap; {PlanarReflections.ComparedPct:F1}% of planar pixels " +
                          $"found by the cubemap too, brightness {PlanarReflections.MirrorDiff:F1} apart (read mirrored {PlanarReflections.ControlDiff:F1})");
        Console.WriteLine($"[KF2] retained: {RetainedScene.Found} frame(s) found at present, {RetainedScene.Missed} missed; " +
                          $"{RetainedScene.PlanarDraws} planar draw(s) at {RetainedScene.PlanarGpuNs / 1e6:F3} ms GPU, " +
                          $"{RetainedScene.CubeDraws} cubemap(s) at {RetainedScene.CubeGpuNs / 1e6:F3} ms GPU, " +
                          $"{RetainedScene.Triangles} triangle(s) submitted, chunks {RetainedScene.ChunksDrawn}/{RetainedScene.ChunksTested} drawn; " +
                          $"{RetainedModels.Models} model(s) ({RetainedModels.Placed} placed from their record, {RetainedModels.Props} of them props) and {RetainedModels.Faces} face(s) captured; " +
                          $"shadow casters in reach {RemasterUniforms.ShadowCasters}, {RemasterUniforms.ShadowModelRenders} model cubemap(s) drawn");
        RetainedModels.Models = RetainedModels.Faces = RetainedModels.Placed = RetainedModels.Props = 0;
        _checked = _within = _worst = 0;
        _sumErr = 0;
        RetainedScene.ResetCounters();
    }
}
