using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// Fog of war for the map: the tiles the player has actually *seen*, remembered
/// per save slot and kept between sessions.
///
/// ## It is the game's own visibility grid, accumulated
///
/// **Nothing here is new reverse engineering.** The renderer's first call each
/// frame is func_8002D3A8, which rebuilds a **24×24 byte grid of tile visibility
/// at 0x80192EAC** — the 4:3 frustum flattened onto the map, filled by scanlines
/// and then flooded for occlusion, which is what gates every object and every
/// block of geometry the frame draws. That grid *is* "what the player can see",
/// walls already taken into account, and a cell is visible iff its byte is
/// nonzero (patches/CullGrid.cs, and the game's own walker func_80031C94 at
/// generated/game.cs:37862). Fog is that grid ORed into an 80×80 bitset.
///
/// The cell/world mapping is the queries' own: `cell = (world &gt;&gt; 11) + Offset`,
/// so the world tile of cell (0,0) is the negated pair at 0x80192EA0/0xA4 —
/// **tile coordinates, stored as words**, which is why a negative origin wraps to
/// a huge unsigned value and is rejected by the same `&lt; 80` bound the game uses
/// rather than indexing backwards.
///
/// ## The sampling clock is the vblank, and that is why there is no hook
///
/// The accumulator has to run **with the map closed**, so it cannot live in a
/// panel's Draw the way every other read in patches/Map.cs does. The obvious seam
/// is a post-hook on func_8002D3A8 — and it is the seam to fall back to — but
/// <c>VSyncEvent</c> costs no hook at all, and since patches/recompone/0021 it
/// fires on a **wall-clock 60 Hz grid** rather than per rendered frame, so the
/// sample rate is 60/s at 20 fps and 60/s at 144 fps. The grid is stable for the
/// whole frame once built, so a vblank read gets a complete one.
///
/// Two consequences, both accepted rather than discovered later:
///
/// * At the 20 fps default each grid is sampled three times. The OR is idempotent,
///   so that costs 576 byte reads and changes nothing.
/// * At 144 fps, 60 of the 144 grids are sampled. A cone 72° wide would need the
///   player to turn faster than 72° in 16 ms to leave a gap, which is faster than
///   the game turns; if play ever shows slivers, the fix is the post-hook above.
///
/// ## The staleness guard, which is the trap
///
/// The area byte at 0x8017E060 moves when the save's area is unpacked, and the
/// grid in RAM is still the last frame the *previous* area rendered — the same
/// window patches/Map.cs's 250 ms re-copy self-corrects through. ORing that into
/// the new area's record would write a permanent lie, since nothing ever clears
/// it. So a sample is taken only when the **camera** (0x80192E78/0x80192E80, world
/// units like the player's own) is within two tiles of the player, and never in
/// the 250 ms after an overlay load. A cutscene camera somewhere else stops the
/// accumulation, which is the conservative way round.
///
/// ## Identity: the game's slot byte, and a scratch bucket for slot 0
///
/// A record is keyed `(slot, area)`. The slot is the game's own record at
/// 0x8006E5D4, which both the load (func_80023638) and the save (func_80023764)
/// write and which is **zero until one of them has run** — so a New Game
/// accumulates into a scratch bucket, and the moment the byte becomes 1..3 the
/// scratch is merged into that slot and dropped. The consequence to know is that
/// the identity is the *slot*, not the save: starting a New Game over slot 2
/// inherits slot 2's old fog.
///
/// ## What has not been looked at
///
/// The mechanism is measured; the picture is the user's to judge. Whether the
/// revealed shape matches where they walked, whether the brightened in-view cone
/// reads or distracts, whether fog belongs on by default, and whether revealing a
/// whole tile — both stacked floor halves at once, since the visibility grid has
/// no notion of the halves — is noticeable on a map that draws one half at a time.
///
///     KF2_MAP_FOG=1          fog on for the run (kf2.map.fog; off by default)
///     KF2_MAP_FOG_PROBE=1    a line a second: revealed, seen, records, flushes
/// </summary>
public static class MapFog
{
    public const string OnKey = "kf2.map.fog";

    // ---- the game's visibility grid ---------------------------------------

    /// <summary>The 24×24 array itself, row-major on Z: `zCell * 24 + xCell`.</summary>
    const uint Legacy = 0x80192EAC;

    /// <summary>The negated index offsets — the world tile of grid cell (0,0),
    /// per axis, as words.</summary>
    const uint MirrorX = 0x80192EA0;
    const uint MirrorZ = 0x80192EA4;

    /// <summary>The camera's world position, in the player's own units.</summary>
    const uint CamWorldX = 0x80192E78;
    const uint CamWorldZ = 0x80192E80;

    const int GridSpan = 24;

    // ---- who is playing ----------------------------------------------------

    /// <summary>u8, the game's own "current slot"; 0 until a save or load has run
    /// this session. See patches/AutoReload.cs.</summary>
    const uint CurrentSlot = 0x8006E5D4;

    /// <summary>Slots are 1..3 and areas 0..7 plus the cut area 10 (see
    /// patches/AreaWarp.cs); a byte outside those is one being rewritten.</summary>
    const int MaxSlot = 3;
    const int MaxArea = 10;

    const uint AreaAddr  = 0x8017E060;   // u8
    const uint MaxHpAddr = 0x80199426;   // u16, 0 until an area is running
    const uint PosXAddr  = 0x801994EC;   // s32
    const uint PosZAddr  = 0x801994F4;   // s32

    // ---- the store ---------------------------------------------------------

    /// <summary>80 × 80 bits, `bit = z * 80 + x`.</summary>
    public const int Bytes = Map.Span * Map.Span / 8;

    /// <summary>Keyed `slot &lt;&lt; 8 | area`.</summary>
    static readonly Dictionary<int, byte[]> _seen = new();

    static byte[]? _live;
    static int _liveKey = -1;
    static int _lastSlot = -1;

    /// <summary>Which sample last saw each tile lit, against <see cref="_samples"/>
    /// — a stamp rather than an array to clear, and read by the panels between
    /// samples, so "in view now" means "in view at the last sample".</summary>
    static readonly int[] _litStamp = new int[Map.Span * Map.Span];
    static int _samples;

    static bool _dirty;
    static long _flushedAt;
    static long _holdUntil;
    const long FlushPeriodMs = 10_000;
    const long HoldMs = 250;

    /// <summary>Tiles either side of the player the camera may be before a sample
    /// is refused as stale.</summary>
    const int CamSlackTiles = 2;

    // ---- settings ----------------------------------------------------------

    public static bool Enabled;
    static bool? _forced;
    static int _probe;

    static string _path = "carda.fog";
    static bool _loaded;

    // probe counters
    static long _probeAt;
    static int _revealed, _flushes;

    public static void Configure(string? on, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on))
            _forced = !on.Equals("0", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
            _probe = probe.Equals("2", StringComparison.Ordinal) ? 2 : 1;
    }

    public static void Install()
    {
        Enabled = _forced ?? false;

        // ConfigManager.Load runs inside HostWindow.Initialize, which is after
        // Program.cs, so both the saved switch and the memory card's path can only
        // be read here. An env var beats the saved value for the run.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            Enabled = _forced ?? RecompOne.Runtime.Runtime.View.GetBool(OnKey, false);
            _path = PathFor();
            Load();
            Console.WriteLine($"[KF2] map fog: {(Enabled ? "on" : "off")}, " +
                              $"{_seen.Count} record(s) in {_path}" +
                              (_probe > 0 ? ", probing" : ""));
        });

        // An overlay load is an area change often enough to be worth flushing on,
        // and it is the moment the grid in RAM stops describing where the player
        // is. Both halves of that are handled here.
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            _holdUntil = Environment.TickCount64 + HoldMs;
            Flush();
        });

        // The sampling clock. See the class comment: a vblank rather than a hook,
        // and a wall-clock 60 Hz rather than a rendered frame.
        Event.AddListener<VSyncEvent>(_ => Sample());

        // The one exit that runs managed code. A kill does not, which is why the
        // periodic flush is the one that matters -- patches/RateCensus.cs:135.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    public static void SetEnabled(bool on) => Enabled = on;

    // ---- reading the game --------------------------------------------------

    /// <summary>
    /// One vblank's worth of accumulation. Gated on the *map* rather than on the
    /// fog switch, so turning fog on mid-session shows a true history rather than
    /// a blank area; the switch decides drawing alone.
    /// </summary>
    static void Sample()
    {
        if (!Map.Enabled) return;

        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return;

        // buf2 is cleared until an area is running, so a max HP of zero is the
        // title screen or a load rather than a dead character (patches/Map.cs).
        if (m.ReadU16(MaxHpAddr) == 0) return;

        long now = Environment.TickCount64;
        if (now < _holdUntil) return;

        int px = (int)m.ReadU32(PosXAddr), pz = (int)m.ReadU32(PosZAddr);
        int cx = (int)m.ReadU32(CamWorldX), cz = (int)m.ReadU32(CamWorldZ);

        // The staleness guard. See the class comment.
        int slack = CamSlackTiles * Map.TileUnits;
        if (Math.Abs(cx - px) > slack || Math.Abs(cz - pz) > slack) return;

        int slot = m.ReadU8(CurrentSlot);
        int area = m.ReadU8(AreaAddr);

        // **Neither byte is always a byte the game means.** Slots are 1..3 (0 is
        // "no save or load yet") and areas are 0..7 plus the cut area 10, so
        // anything else is the byte being rewritten under us -- measured, a
        // record for "area 99" written while a reload was unpacking buf0, on a
        // frame whose HP and camera both still read as a live area.
        if (slot > MaxSlot || area > MaxArea) return;

        // The slot byte has gone from "neither a save nor a load has run" to a
        // real slot: the scratch bucket is that slot's after all.
        if (_lastSlot == 0 && slot != 0) MergeScratch(slot);
        _lastSlot = slot;

        var bits = Live(slot, area);

        // The origin, plus the crop bias KF2_CULLGRID=on introduces between the
        // 32×32 grid the mirror words then describe and the 24×24 array it crops
        // into. Zero in every normal run.
        int bias = CullGrid.LegacyBias;
        int ox = (int)m.ReadU32(MirrorX) + bias;
        int oz = (int)m.ReadU32(MirrorZ) + bias;

        int newly = 0, drawn = 0;

        // 144 words rather than 576 bytes: ReadU32 does the same range checks as
        // ReadU8 and answers four times as much (patches/Map.cs's own idiom).
        for (int i = 0; i < GridSpan * GridSpan; i += 4)
        {
            uint w = m.ReadU32(Legacy + (uint)i);
            if (w == 0) continue;

            for (int b = 0; b < 4; b++)
            {
                // **The two low bits, not "nonzero".** A cell byte carries the
                // flood's marker in bit 0 or bit 1 -- which one depends on the
                // stacked half the player is on -- and func_80031B1C draws half A
                // on bit 0 and half B on bit 1, so those two bits are "the game
                // drew this tile". The other bits are the flood's own working
                // state: the ray-alive bit it clears behind itself, and the 0xC0
                // patches/CullCone.cs ORs over the near-camera rescue discs. A
                // cell holding only those was never drawn, and counting it lit
                // over-reveals -- measured 190 lit against a cone that cannot
                // hold more than about 110 tiles.
                if (((w >> (b * 8)) & 3) == 0) continue;

                int cell = i + b;
                int wx = (ox + cell % GridSpan) & 0xFFFF;
                int wz = (oz + cell / GridSpan) & 0xFFFF;
                if (wx >= Map.Span || wz >= Map.Span) continue;

                drawn++;
                newly += Mark(bits, wx, wz);
                _litStamp[wz * Map.Span + wx] = _samples + 1;
            }
        }

        // **An empty grid is not a view of anywhere**, and the frames between a
        // New Game and its area being placed are exactly that: HP is up, the
        // player and the camera both read 0,0, and the cone has drawn nothing.
        // Without this the player's tile below writes tile 0,0 into a record for
        // whatever the area byte happens to say.
        if (drawn == 0) return;

        // The tile the player is standing on, whatever the cone did with it.
        int ptx = px >> 11, ptz = pz >> 11;
        if ((uint)ptx < Map.Span && (uint)ptz < Map.Span)
        {
            newly += Mark(bits, ptx, ptz);
            _litStamp[ptz * Map.Span + ptx] = _samples + 1;
        }

        _samples++;
        if (newly > 0) { _revealed += newly; _dirty = true; }

        if (_dirty && now - _flushedAt >= FlushPeriodMs) Flush();
        if (_probe > 0 && now - _probeAt >= 1000) Probe(now, slot, area, bits, ptx, ptz);
    }

    static int Mark(byte[] bits, int x, int z)
    {
        int bit = z * Map.Span + x;
        byte mask = (byte)(1 << (bit & 7));
        if ((bits[bit >> 3] & mask) != 0) return 0;
        bits[bit >> 3] |= mask;
        return 1;
    }

    static int Key(int slot, int area) => (slot << 8) | area;

    static byte[] Live(int slot, int area)
    {
        int key = Key(slot, area);
        if (key == _liveKey && _live != null) return _live;

        // A record change is an area change or a load; the outgoing one is worth
        // writing now rather than at the next tick of the timer.
        if (_dirty) Flush();

        if (!_seen.TryGetValue(key, out var bits)) _seen[key] = bits = new byte[Bytes];
        _liveKey = key;
        _live = bits;
        return bits;
    }

    /// <summary>OR every slot-0 record into <paramref name="slot"/>'s and drop it.
    /// See "Identity" in the class comment.</summary>
    static void MergeScratch(int slot)
    {
        var scratch = _seen.Where(kv => kv.Key >> 8 == 0).ToList();
        if (scratch.Count == 0) return;

        foreach (var (key, bits) in scratch)
        {
            int area = key & 0xFF, into = Key(slot, area);
            if (!_seen.TryGetValue(into, out var dst)) _seen[into] = dst = new byte[Bytes];
            for (int i = 0; i < Bytes; i++) dst[i] |= bits[i];
            _seen.Remove(key);
        }

        _liveKey = -1;
        _live = null;
        _dirty = true;
        Console.WriteLine($"[KF2] map fog: merged {scratch.Count} unsaved record(s) into slot {slot}");
    }

    // ---- what the viewports ask -------------------------------------------

    /// <summary>Unexplored (0), remembered (1), or in the cull cone at the last
    /// sample (2). Takes a **world tile**, not a screen row.</summary>
    public static int State(int x, int z)
    {
        if ((uint)x >= Map.Span || (uint)z >= Map.Span) return 0;
        var bits = _live;
        if (bits == null) return 0;

        int bit = z * Map.Span + x;
        if ((bits[bit >> 3] & (1 << (bit & 7))) == 0) return 0;
        return _litStamp[bit] == _samples ? 2 : 1;
    }

    /// <summary>Cached so neither viewport allocates a delegate per frame.</summary>
    static readonly Func<int, int, int> StateFn = State;

    /// <summary>
    /// What both viewports pass to <see cref="MapRender.Draw"/>: the lookup while
    /// fog is on and a record for the area on screen exists, and **null** — draw
    /// everything — otherwise.
    ///
    /// The null is not only the switch being off. Until the first sample lands
    /// there is no record, and a fog map with no record is a blank rectangle
    /// rather than a map, which reads as the feature being broken; drawing the
    /// whole area for that fraction of a second is the honest fallback.
    /// </summary>
    public static Func<int, int, int>? Predicate => Enabled && _live != null ? StateFn : null;

    /// <summary>The two maintenance buttons on the settings page, which are what
    /// make the picture judgeable inside one session.</summary>
    public static void ForgetArea()
    {
        if (_live == null) return;
        Array.Clear(_live);
        Array.Clear(_litStamp);
        _dirty = true;
        Flush();
    }

    public static void RevealArea()
    {
        if (_live == null) return;
        Array.Fill(_live, (byte)0xFF);
        _dirty = true;
        Flush();
    }

    // ---- the file ----------------------------------------------------------

    // "KF2FOG\0" + version, then a u16 count and that many fixed-size records.
    static readonly byte[] Magic = "KF2FOG\0"u8.ToArray();
    const byte Version = 1;
    const int Header = 10;          // magic + version + count
    const int Record = 2 + Bytes;   // slot, area, bitset

    /// <summary>
    /// Beside the memory card, because that is where this game's saves are and
    /// what the slot in the key refers to. Resolved once — a card path changed
    /// mid-session is not followed.
    /// </summary>
    static string PathFor()
    {
        string card = RecompOne.Runtime.Config.ConfigManager.Game.CardAPath;
        if (string.IsNullOrWhiteSpace(card)) card = "carda.sav";
        return Path.ChangeExtension(card, ".fog") ?? "carda.fog";
    }

    static void Load()
    {
        _loaded = true;
        if (!File.Exists(_path)) return;

        try
        {
            var b = File.ReadAllBytes(_path);
            if (b.Length < Header || !b.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                b[Magic.Length] != Version)
            {
                Console.Error.WriteLine($"[KF2] map fog: {_path} is not a fog store this " +
                                        "version reads; starting empty.");
                return;
            }

            int count = b[8] | (b[9] << 8);
            if (b.Length < Header + count * Record)
            {
                Console.Error.WriteLine($"[KF2] map fog: {_path} is truncated; starting empty.");
                return;
            }

            for (int i = 0; i < count; i++)
            {
                int o = Header + i * Record;
                var bits = new byte[Bytes];
                Array.Copy(b, o + 2, bits, 0, Bytes);
                _seen[Key(b[o], b[o + 1])] = bits;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] map fog: could not read {_path}: {e.Message}");
        }
    }

    /// <summary>
    /// Write the whole store through a temp file, so killing the process mid-write
    /// leaves the old store rather than half a new one.
    /// </summary>
    public static void Flush()
    {
        if (!_loaded || !_dirty || _seen.Count == 0) return;
        _dirty = false;
        _flushedAt = Environment.TickCount64;

        try
        {
            var b = new byte[Header + _seen.Count * Record];
            Magic.CopyTo(b, 0);
            b[Magic.Length] = Version;
            b[8] = (byte)_seen.Count;
            b[9] = (byte)(_seen.Count >> 8);

            int i = 0;
            foreach (var (key, bits) in _seen)
            {
                int o = Header + i++ * Record;
                b[o] = (byte)(key >> 8);
                b[o + 1] = (byte)key;
                bits.CopyTo(b, o + 2);
            }

            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, b);
            File.Move(tmp, _path, overwrite: true);
            _flushes++;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] map fog: could not write {_path}: {e.Message}");
        }
    }

    // ---- the probe ---------------------------------------------------------

    static void Probe(long now, int slot, int area, byte[] bits, int ptx, int ptz)
    {
        _probeAt = now;

        int seen = 0;
        foreach (byte v in bits) seen += System.Numerics.BitOperations.PopCount((uint)v);

        int lit = 0;
        for (int i = 0; i < _litStamp.Length; i++) if (_litStamp[i] == _samples) lit++;

        var m = RecompOne.Runtime.Runtime.Mem;
        int ox = m == null ? 0 : (int)m.ReadU32(MirrorX) + CullGrid.LegacyBias;
        int oz = m == null ? 0 : (int)m.ReadU32(MirrorZ) + CullGrid.LegacyBias;

        // The invariant, in the shape patches/Map.cs's floor-Y check has: the tile
        // the player stands on is always one of the tiles fog has revealed.
        bool ok = State(ptx, ptz) != 0;

        Console.WriteLine($"[KF2] map fog: slot {slot} area {area}: {seen} tiles seen " +
                          $"(+{_revealed}/s), {lit} lit now, {_seen.Count} records, " +
                          $"{_flushes} flush(es); player {ptx},{ptz} window " +
                          $"{ox & 0xFFFF},{oz & 0xFFFF}" +
                          $"{(ok ? "" : " -- PLAYER TILE NOT SEEN")}");
        _revealed = 0;
        _flushes = 0;

        // =2: the raw 24x24 array, so a byte's meaning can be argued from the
        // bytes rather than from the flood's source. Digits are the low two bits
        // (the two stacked halves, which is what func_80031B1C draws on), '+' is
        // a cell carrying only the flood's own high bits, '.' is clear.
        if (_probe < 2 || m == null) return;
        for (int z = 0; z < GridSpan; z++)
        {
            var sb = new System.Text.StringBuilder(GridSpan);
            for (int x = 0; x < GridSpan; x++)
            {
                byte v = m.ReadU8(Legacy + (uint)(z * GridSpan + x));
                sb.Append(v == 0 ? '.' : (v & 3) != 0 ? (char)('0' + (v & 3)) : '+');
            }
            Console.WriteLine($"[KF2] fog grid z{(oz + z) & 0xFFFF,3}: {sb}");
        }
    }
}
