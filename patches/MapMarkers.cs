using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// What is *in* the area, on top of the floor plan: creatures, props, effects and
/// billboard sprites, drawn where the game says they stand.
///
///     KF2_MAP_MARKERS=0        the whole layer off (on by default)
///     KF2_MAP_PROBE=1          also dumps a census of the four tables
///
/// patches/Map.cs draws the *architecture* — the 80x80 tile grid the area loader
/// copies to 0x801C8484 — and that is the half of a map that does not move. The
/// other half is the four world tables the renderer itself walks, which is the
/// authoritative list of everything drawn in the area that is not a tile:
///
///     # | base       | stride | count | drawn when        | position | rotation
///     1 | 0x8016C544 | 0x7C   | 200   | u8[+0x9] == 1     | +0x2C    | +0x40     creatures
///     2 | 0x80177714 | 0x44   | 396   | u16[+0x6] != 0xFF | +0x14    | +0x24     props, doors, items
///     3 | 0x8019CC6C | 0x48   | 128   | u8[+0x0] != 0xFF  | +0x14    | +0x24     effects, projectiles
///     4 | 0x80195174 | 0x18   | 128   | u16[+0x0] != 0xFFFF | +0x8   | none      billboards
///
/// (docs/GAME_INTERNALS.md, "func_80032588 is fed from *four* tables". The same
/// four rows patches/ObjectSmoothing.cs carries and patches/AgentServer.cs's
/// `nearby` reports from — this is a third reader of one already-measured fact,
/// not a new address hunt.)
///
/// **The liveness test is the renderer's, not the owning stage's**, and the two
/// are not the same way round: an object is *drawn* on u16[+0x6] while stage 2
/// *steps* it on u8[+0x4], and a creature is drawn on u8[+0x9] == 1 while stage 4
/// and AgentServer use u8[+0x0] != 0xFF. Using the stage's test for a map would
/// mark records the game never draws — the exact defect that cost ObjectSmoothing
/// a mean carried offset of 2600 units. A map should show what is on screen, so
/// it uses the same predicate the screen does.
///
/// **What a marker is called is not claimed.** The object table's type byte at
/// +0x4 dispatches through a 224-entry jump table with thirty distinct arms and
/// nothing in this repo maps those arms to nouns, so a chest, a door and a lever
/// are all "object" here and the readout prints the raw type and definition index
/// instead. The one identity that *is* confirmed is the creature's: u8[+0x2] is
/// the index into the per-area descriptor block at 0x80172624 (120-byte records,
/// docs/TODO.md #14 and patches/HitGuard.cs), so two markers of the same kind
/// carry the same number and the readout can say so.
///
/// **Which floor a marker is on is derived, not stored.** The tile record holds
/// two stacked halves and a half's floor is -(height &lt;&lt; 7); the map draws one
/// half at a time, so a marker picks the drawn half whose floor Y is nearest its
/// own. That is the same equality the whole map patch rests on — the player
/// stands on a half the renderer draws and that half's -(h &lt;&lt; 7) *equals* their Y,
/// measured gap 0 — applied to everything else in the area.
///
/// **Fog of war is respected, and moving things get the stricter rule.** A tile
/// you merely remember does not tell you what is standing in it now, so a
/// creature or an effect is drawn only where the tile is lit *this sample*
/// (state 2), while a prop or a billboard — architecture in all but name — is
/// drawn wherever the tile is at least remembered (state 1). With fog off the
/// predicate is null and everything shows.
///
/// **The object table outlives the area it belongs to, and the map must not
/// believe it.** The renderer's test is right for the renderer because the
/// renderer runs after the loader; a layer sampling on its own clock is not so
/// lucky. Measured across an area change: 258 slots still read *drawn*, 0 read
/// *stepped*, and the positions were the previous area's verbatim — the loader
/// clears the behaviour byte at +0x4 and leaves +0x6 and the VECTOR alone. So the
/// sample is held while not one slot passes +0x4 != 0xFF, which is the shape
/// Map.Ready already has for the tile grid. In a settled area the two tests still
/// disagree (measured in area 0: 258 drawn, 139 stepped) and that difference is
/// kept rather than filtered — a slot the renderer draws is on screen — with
/// Marker.Stepped carrying it to the readout.
///
/// **Nothing is written to game memory and no game function is hooked**, which is
/// the property the map patch has and this must not lose. It is 852 records of a
/// few fields each, sampled four times a tick rather than per frame.
/// </summary>
public static class MapMarkers
{
    // ---- what a marker is --------------------------------------------------

    public enum Kind { Creature, Object, Effect, Sprite }

    /// <summary>One live record, in world units. <c>Half</c> is 0 or
    /// <see cref="Map.HalfBytes"/> — the half of the tile record this marker was
    /// matched to, so a viewport showing one floor can filter on it.</summary>
    public readonly record struct Marker(
        Kind Kind, int Slot, int X, int Y, int Z, int Type, int Def, int Yaw, int Half,
        bool Stepped);

    sealed record Spec(
        Kind Kind, string Label, uint Base, int Stride, int Count,
        int TestOff, int TestWidth, int TestValue, bool DrawnWhenEqual,
        int PosOff, int RotOff, int TypeOff, int TypeWidth, int DefOff, int DefWidth);

    // The four rows, in the renderer's own order. TypeOff/DefOff are what the
    // readout prints; -1 means the table has no such field.
    static readonly Spec[] Tables =
    [
        // Creatures. +0x2 is the descriptor index -- the creature's *kind*, and the
        // one identity in any of these tables this repo has confirmed.
        new(Kind.Creature, "creature", 0x8016C544, 0x7C, 0xC8, 0x9, 1, 0x1, true,
            0x2C, 0x40, 0x2, 1, 0x0, 1),

        // Props, doors, levers, chests. +0x4 is the behaviour type stage 2
        // dispatches on; +0x6 is the definition index, which is also the test.
        new(Kind.Object, "object", 0x80177714, 0x44, 0x18C, 0x6, 2, 0xFF, false,
            0x14, 0x24, 0x4, 1, 0x6, 2),

        // Stage 5's table: projectiles, spell effects, anything with a lifetime at
        // +0x0E. Recycled hard, which is why it is sampled rather than remembered.
        new(Kind.Effect, "effect", 0x8019CC6C, 0x48, 0x80, 0x0, 1, 0xFF, false,
            0x14, 0x24, 0x0, 1, -1, 0),

        // Billboards: torches, flames, the animated sprite strips. Their position is
        // written once when func_80035550 fills the table, so these never move.
        new(Kind.Sprite, "sprite", 0x80195174, 0x18, 0x80, 0x0, 2, 0xFFFF, false,
            0x8, -1, 0x3, 1, -1, 0),
    ];

    // ---- settings ----------------------------------------------------------

    public const string OnKey        = "kf2.map.markers";
    public const string CreaturesKey = "kf2.map.markers.creatures";
    public const string ObjectsKey   = "kf2.map.markers.objects";
    public const string EffectsKey   = "kf2.map.markers.effects";
    public const string SpritesKey   = "kf2.map.markers.sprites";
    public const string FacingKey    = "kf2.map.markers.facing";

    /// <summary>The layer. Off leaves the tables unread entirely.</summary>
    public static bool Enabled = true;

    /// <summary>Per class. Billboards default off: a torch-lit corridor holds
    /// dozens of them and they are decoration rather than information, so they
    /// would bury the four markers a player is actually looking for.</summary>
    public static bool Creatures = true, Objects = true, Effects = true, Sprites;

    /// <summary>Draw a creature's facing as a spoke off its dot. The rotation is
    /// read the way the renderer builds it (see <see cref="Yaw"/>), which has been
    /// derived but never judged by eye — so it defaults off, the rule this port
    /// applies to any picture nobody has looked at.</summary>
    public static bool Facing;

    static bool? _forced;

    public static void Configure(string? on)
    {
        if (!string.IsNullOrWhiteSpace(on))
            _forced = !on.Equals("0", StringComparison.Ordinal);
    }

    /// <summary>Read the saved settings. Called from <c>Map.Install</c>'s
    /// RuntimeReadyEvent listener, which is the first moment ConfigManager.Load
    /// has run; an env var beats them for the run.</summary>
    public static void LoadSettings(RecompOne.Runtime.Config.ViewConfig view)
    {
        Enabled   = _forced ?? view.GetBool(OnKey, true);
        Creatures = view.GetBool(CreaturesKey, true);
        Objects   = view.GetBool(ObjectsKey, true);
        Effects   = view.GetBool(EffectsKey, true);
        Sprites   = view.GetBool(SpritesKey, false);
        Facing    = view.GetBool(FacingKey, false);
    }

    // ---- the sample --------------------------------------------------------

    static Marker[] _markers = new Marker[Tables.Sum(t => t.Count)];
    static int _count;

    /// <summary>The last sample, newest first by table order. Valid only for the
    /// frame it is read in, like everything else the map reads.</summary>
    public static ReadOnlySpan<Marker> Live => _markers.AsSpan(0, _count);

    /// <summary>Live records per table in the last sample, for the probe and the
    /// panel's status line.</summary>
    public static readonly int[] Counts = new int[4];

    static long _sampledAt = long.MinValue;
    static int _sampledArea = -1;

    /// <summary>Milliseconds between samples. The world ticks at 20 Hz by default,
    /// so 50 ms is one sample a tick — reading these tables faster cannot show
    /// anything the game has not produced, and reading them slower makes a
    /// creature's dot lag its model.</summary>
    const long PeriodMs = 50;

    /// <summary>
    /// Bring <see cref="Live"/> up to date. Called from <see cref="Map.Refresh"/>,
    /// so it runs inside the game's own VSync call on the game thread and sees the
    /// memory the game left there — the same guarantee the tile copy has.
    /// </summary>
    public static void Refresh(IMemory m, int area, long now)
    {
        if (!Enabled) { _count = 0; Array.Clear(Counts); return; }
        if (area == _sampledArea && now - _sampledAt < PeriodMs) return;
        if (!ObjectTableSettled(m)) return;   // keep the last good sample, not a stale one

        _sampledArea = area;
        _sampledAt = now;
        _count = Scan(m, _markers, Counts, Shown);
    }

    /// <summary>
    /// **The object table survives an area change and the map must not believe
    /// it.** The renderer draws an object slot on <c>u16[+0x6] != 0xFF</c>, and
    /// that is the right test for the renderer because it runs after the loader
    /// has filled the table. A map sampling on its own clock does not have that
    /// guarantee, and measured, the difference is total: mid-reload the table
    /// reads **258 records drawn, 0 stepped**, carrying the *previous* area's
    /// positions verbatim (slot 2 at 141311,-12800,28416 in both areas) — because
    /// the loader clears the behaviour byte at <c>+0x4</c> and leaves <c>+0x6</c>
    /// and the position where they were.
    ///
    /// So the guard is the byte the loader *does* write: an area with objects in
    /// it has at least one slot stage 2 will step, and a table where not one slot
    /// passes <c>+0x4 != 0xFF</c> is a table between areas. The last good sample
    /// is held rather than replaced, which is a fraction of a second of loading
    /// screen — the same trade patches/Map.cs makes for the tile grid.
    ///
    /// Note this does **not** filter the 119 slots that are drawn but never
    /// stepped in a settled area (measured in area 0: 139 stepped, 258 drawn).
    /// Those are the renderer's, so they are the map's; the readout says which is
    /// which and <c>Marker.Stepped</c> carries it.
    /// </summary>
    static bool ObjectTableSettled(IMemory m)
    {
        var t = Tables[(int)Kind.Object];
        for (int i = 0; i < t.Count; i++)
            if (m.ReadU8(t.Base + (uint)(i * t.Stride) + 0x4) != 0xFF) return true;
        return false;
    }

    /// <summary>The read itself, into a caller's buffer. Shared by the live
    /// sample and the probe's census, which wants every class whatever the
    /// settings say.</summary>
    static int Scan(IMemory m, Marker[] dst, int[] counts, Func<Kind, bool> want)
    {
        int n = 0;
        Array.Clear(counts);

        foreach (var t in Tables)
        {
            if (!want(t.Kind)) continue;

            for (int i = 0; i < t.Count; i++)
            {
                uint rec = t.Base + (uint)(i * t.Stride);

                uint test = t.TestWidth == 1 ? m.ReadU8(rec + (uint)t.TestOff)
                                             : m.ReadU16(rec + (uint)t.TestOff);
                bool live = t.DrawnWhenEqual ? test == (uint)t.TestValue
                                             : test != (uint)t.TestValue;
                if (!live) continue;

                int x = (int)m.ReadU32(rec + (uint)t.PosOff);
                int y = (int)m.ReadU32(rec + (uint)t.PosOff + 4);
                int z = (int)m.ReadU32(rec + (uint)t.PosOff + 8);

                // A record can be live and still be nowhere: the loader clears the
                // tables to zero, and the origin is a legal tile, so 0,0,0 would
                // stack every uninitialised slot in the area's corner. Nothing the
                // game draws sits exactly there.
                if ((x | y | z) == 0) continue;
                if ((uint)Map.TileOf(x) >= Map.Span || (uint)Map.TileOf(z) >= Map.Span) continue;

                counts[(int)t.Kind]++;

                int type = t.TypeOff < 0 ? -1
                    : t.TypeWidth == 1 ? m.ReadU8(rec + (uint)t.TypeOff)
                                       : m.ReadU16(rec + (uint)t.TypeOff);
                int def = t.DefOff < 0 ? -1
                    : t.DefWidth == 1 ? m.ReadU8(rec + (uint)t.DefOff)
                                      : m.ReadU16(rec + (uint)t.DefOff);

                int yaw = t.RotOff < 0 ? 0 : Yaw(m, rec + (uint)t.RotOff);

                // Only the object table has a second, different liveness test, and
                // it means something worth carrying: a slot the renderer draws but
                // stage 2 does not step is a prop that cannot move.
                bool stepped = t.Kind != Kind.Object || m.ReadU8(rec + 0x4) != 0xFF;

                if (n < dst.Length)
                    dst[n++] = new Marker(t.Kind, i, x, y, z, type, def, yaw, HalfAt(x, y, z), stepped);
            }
        }

        return n;
    }

    static bool Shown(Kind k) => k switch
    {
        Kind.Creature => Creatures,
        Kind.Object   => Objects,
        Kind.Effect   => Effects,
        _             => Sprites,
    };

    /// <summary>
    /// The heading the renderer draws this record with.
    ///
    /// Every rotation lane in these tables is three s16 and **the yaw at +2 is
    /// biased by 0x800** — the object loop of func_800331B4 adds it before handing
    /// the triple to func_80032588, for both the entity and the object table
    /// (docs/GAME_INTERNALS.md). So the value that means the same thing as the
    /// player's yaw at 0x80199506 is the stored one plus the bias, and the screen
    /// angle is then MapRender's own -(yaw + pi/2). Derived, never looked at.
    /// </summary>
    static int Yaw(IMemory m, uint rot) => (short)m.ReadU16(rot + 2) + 0x800;

    /// <summary>
    /// Which half of the tile record this world position belongs to: the drawn
    /// half whose floor Y is nearest, or the shown half when the tile draws
    /// neither. A half's floor is -(height &lt;&lt; 7), the equality the map's own
    /// player check is built on.
    /// </summary>
    static int HalfAt(int x, int y, int z)
    {
        int tx = Map.TileOf(x), tz = Map.TileOf(z);
        bool lower = Map.Drawn(tx, tz, 0), upper = Map.Drawn(tx, tz, Map.HalfBytes);

        if (!lower && !upper) return Map.HalfOffset;
        if (!upper) return 0;
        if (!lower) return Map.HalfBytes;

        int lo = -(Map.Byte(tx, tz, Map.HeightByte) << 7);
        int up = -(Map.Byte(tx, tz, Map.HalfBytes + Map.HeightByte) << 7);
        return Math.Abs(y - lo) <= Math.Abs(y - up) ? 0 : Map.HalfBytes;
    }

    // ---- what a viewport needs to know ------------------------------------

    /// <summary>
    /// Should this marker be drawn on a map showing <paramref name="half"/>?
    ///
    /// <paramref name="state"/> is patches/MapFog.cs's predicate, taking a **world
    /// tile**. A thing that moves needs the tile lit in the last sample; a thing
    /// that cannot move is as much a fact about the area as its walls are, so a
    /// remembered tile is enough.
    /// </summary>
    public static bool Visible(in Marker mk, int half, Func<int, int, int>? state)
    {
        if (mk.Half != half) return false;
        if (state == null) return true;

        int fog = state(Map.TileOf(mk.X), Map.TileOf(mk.Z));
        return mk.Kind is Kind.Object or Kind.Sprite ? fog != 0 : fog == 2;
    }

    public static string Noun(Kind k) => k switch
    {
        Kind.Creature => "creature",
        Kind.Object   => "object",
        Kind.Effect   => "effect",
        _             => "sprite",
    };

    // ---- the probe ---------------------------------------------------------

    /// <summary>
    /// A census of the four tables, printed beside <see cref="Map.Dump"/>.
    ///
    /// This is the instrument for the thing the layer deliberately does not claim:
    /// a histogram of the object table's type byte, area by area, is what would
    /// let someone pair "type 0x1C" with "a door" without guessing. It prints the
    /// live count, the distinct type bytes and how many records carry each.
    /// </summary>
    public static void Dump(IMemory m)
    {
        // Its own buffer and its own scan: a census that only counted the classes
        // the settings happen to show would report zero sprites and read as the
        // billboard table being wrong.
        var all = new Marker[_markers.Length];
        var counts = new int[Counts.Length];
        int n = Scan(m, all, counts, _ => true);

        if (!ObjectTableSettled(m))
            Console.WriteLine("[KF2] map markers: the object table reads between areas " +
                              "(no slot stepped) — the census below is the previous area's");

        Console.WriteLine($"[KF2] map markers: {counts[0]} creatures, {counts[1]} objects, " +
                          $"{counts[2]} effects, {counts[3]} sprites");

        int stepped = 0;
        for (int i = 0; i < n; i++)
            if (all[i].Kind == Kind.Object && all[i].Stepped) stepped++;
        Console.WriteLine($"[KF2] map markers: of the objects, {stepped} are stepped by stage 2 " +
                          $"and {counts[1] - stepped} are drawn but never stepped");

        foreach (Kind k in Enum.GetValues<Kind>())
        {
            var hist = new SortedDictionary<int, int>();
            for (int i = 0; i < n; i++)
                if (all[i].Kind == k)
                    hist[all[i].Type] = hist.GetValueOrDefault(all[i].Type) + 1;
            if (hist.Count == 0) continue;

            Console.WriteLine($"[KF2] map markers: {Noun(k)} types: " +
                              string.Join(", ", hist.Select(p => $"{p.Key:X2}x{p.Value}")));
        }
    }
}
