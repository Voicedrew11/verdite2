using RecompOne.Runtime.Config;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using Silk.NET.Input;

namespace Kf2;

/// <summary>
/// A dynamic map: where you are, drawn from the game's own floor plan.
///
///     KF2_MAP=0                the whole feature off (on by default)
///     KF2_MAP_MINIMAP=1        the corner minimap on (off by default)
///     KF2_MAP_PROBE=1          dump the 80x80 grid as ASCII on each area load
///
/// King's Field is a maze and the original shipped no automap, so a player has
/// no way to tell where they are. Everything else in this port that knows a
/// position is a debug instrument — kf2debug's Warp tab, KF2_SHELL's `state`,
/// the [KF2-AGENT] beacon — rather than something you can look at while playing.
///
/// **The floor plan is already in RAM and is not polygon soup.** The area loader
/// (`func_8001689C`) copies 64,000 bytes off disc to `0x801C8484`, and that is an
/// **80 x 80 grid of 10-byte tile records** — `tile = 0x801C8484 + 800*z + 10*x`,
/// the resolution `func_80031B1C` performs before drawing each tile. A tile spans
/// 2048 world units, so `tileX = worldX &gt;&gt; 11` and the world runs 0..163,839 per
/// axis. The 24x24 grid at `0x80192EAC` that CullCone and CullGrid work on is
/// only a per-frame *visibility window* over this map, not the map.
///
/// A record is **two stacked 5-byte halves** — a lower floor at +0 and an upper
/// at +5, which is why a tile can be walked over and under. Per half:
///
///     +0  model index; 0xFF empty, drawn when &lt; 240   (func_80031B1C)
///     +1  height byte; the floor's Y is -(h) &lt;&lt; 7
///     +2  collision flags, &amp; 0xFC tested              (func_8002C700)
///     +3  collision-shape index into 0x801D8484       (func_8002B7D0)
///     +4  flags; bit 0x80 stops the visibility flood  (patches/CullGrid.cs)
///
/// **Occupancy here is the model index, because that is the renderer's own test.**
/// `+4 &amp; 0x80` is drawn as a separate tint rather than trusted as "wall": the
/// flood at patches/CullGrid.cs:510 marks such a cell lit and then *stops*, which
/// is an occluder, while docs/WIDESCREEN.md:499 labels the same bit "see through".
/// The two readings disagree and no counter here can settle it, so the panel's
/// hover readout shows all ten bytes and the picture is the user's to judge.
///
/// **Nothing is written to game memory and no game function is hooked.** Position,
/// map and area are plain reads, which is unusual for this repo and is what makes
/// the patch cheap: with the map closed it costs one bool test a frame.
///
/// **Every read happens inside the panels' own Draw(), and that is correct rather
/// than lazy.** `LibEtc.VSync` calls `Runtime.PresentFrame` -&gt; `HostWindow.Present`
/// -&gt; `PanelManager.DrawPanels`, all on one thread, so a panel draws *inside the
/// game's own VSync call* and sees exactly the memory the game left there. There
/// is no snapshot handoff to get wrong. The 64,000-byte tile copy is still only
/// taken four times a second, because it is 16,000 reads and the architecture it
/// carries — the drawbridge and the minecart are tiles, not models
/// (docs/GAME_INTERNALS.md "The map is an 80x80 tile grid") — does not move faster
/// than that.
///
/// See "A dynamic map" in docs/PATCHES_AND_MODS.md.
/// </summary>
public static class Map
{
    // ---- the grid ----------------------------------------------------------

    /// <summary>Tiles a side. Both of func_80031C94's loop bounds test 0x50.</summary>
    public const int Span = 80;

    /// <summary>Bytes a record, and bytes a row: 800 = 80 * 10.</summary>
    public const int Stride = 10;
    public const int RowBytes = Span * Stride;

    /// <summary>World units a tile: func_80031B1C positions half A at tileX &lt;&lt; 11.</summary>
    public const int TileUnits = 2048;

    /// <summary>A record is two of these, the lower floor then the upper.</summary>
    public const int HalfBytes = 5;

    /// <summary>Offsets inside a half.</summary>
    public const int Model = 0, HeightByte = 1, Collide = 2, Shape = 3, Flags = 4;

    /// <summary>A model index at or above this is not drawn; 0xFF is an empty half.</summary>
    public const int NotDrawn = 240;

    /// <summary>Bit of +4 at which the visibility flood stops.</summary>
    public const byte StopsFlood = 0x80;

    // ---- addresses (read only) --------------------------------------------

    const uint TileBase = 0x801C8484;   // 80 * 80 * 10
    const uint PosXAddr = 0x801994EC;   // s32
    const uint PosYAddr = 0x801994F0;   // s32, normally negative
    const uint PosZAddr = 0x801994F4;   // s32
    const uint YawAddr  = 0x80199506;   // s16, composed view — what the renderer reads
    const uint AreaAddr = 0x8017E060;   // u8, 0..7
    const uint MaxHpAddr = 0x80199426;  // u16; zero until an area is running
    const uint HalfSelAddr = 0x801D9C8E; // u16, 0 or 5 — the floor the player is on

    /// <summary>A full turn in the game's angles. Yaw increases turning *left*.</summary>
    public const int Turn = 0x1000;

    // ---- settings ----------------------------------------------------------

    public const string OnKey       = "kf2.map.on";
    public const string MinimapKey  = "kf2.map.minimap";
    public const string SizeKey     = "kf2.map.minimap.size";
    public const string RadiusKey   = "kf2.map.minimap.radius";
    public const string CornerKey   = "kf2.map.minimap.corner";
    public const string PadKey      = "kf2.map.minimap.pad";
    public const string ShapeKey    = "kf2.map.minimap.shape";
    public const string OpacityKey  = "kf2.map.minimap.opacity";
    public const string ShadeKey    = "kf2.map.shade";
    public const string WallsKey    = "kf2.map.walls";
    public const string FloorKey    = "kf2.map.floor";

    /// <summary>The feature. False leaves both panels unregistered.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>The corner minimap. Off by default: it is a picture nobody has
    /// judged by eye, and this repo's rule for those is that they default off.</summary>
    public static bool Minimap;

    /// <summary>Minimap side in logical pixels, and how many tiles either side of
    /// the player it covers.</summary>
    public static int MinimapSize = 220;
    public static int MinimapRadius = 12;

    /// <summary>Where the minimap is pinned. 0 top-left, 1 top-right, 2
    /// bottom-left, 3 bottom-right, 4 top-centre.
    ///
    /// **The low two bits are load-bearing for 0..3 and nothing else.** Those
    /// four were read as a bitmask — bit 0 the right edge, bit 1 the bottom — and
    /// the numbering is kept because it is what is already in a player's
    /// <c>interface.ini</c>; but a centred anchor has no such bit, so
    /// <c>MapOverlay</c> switches on the value rather than masking it.</summary>
    public static int MinimapCorner = 1;

    /// <summary>How far the minimap sits from the edges it is pinned to, in
    /// logical pixels — scaled by <c>Theme.Scale</c> alongside the size, so the
    /// gap does not shrink as the interface grows. 12 is what shipped.
    ///
    /// A centred anchor spends it on the top edge only; there is no horizontal
    /// edge to stand off from.</summary>
    public static int MinimapPad = 12;

    /// <summary>0 square, 1 circle. Square by default, which is what shipped and
    /// what the tile grid actually is; a circle costs the corners of the window
    /// and is the shape a player expects an overlay compass to be.</summary>
    public static int MinimapShape;

    /// <summary>How opaque the minimap's ground and tiles are drawn, 0.15..1.
    ///
    /// **1 is the shipped picture and is the default**, for the rule the rest of
    /// the port follows: a picture nobody has judged by eye does not become the
    /// default. Below 1 the game shows through the map, which is the point of an
    /// overlay — the player's arrow is deliberately exempt (see
    /// <c>MapRender.DrawPlayer</c>), since a marker you cannot find is not worth
    /// drawing at all.</summary>
    public static float MinimapOpacity = 1f;

    /// <summary>Shade a tile by its height byte.</summary>
    public static bool Shade = true;

    /// <summary>Tint the tiles whose +4 bit 0x80 is set.</summary>
    public static bool Walls = true;

    /// <summary>-1 follows the player (u16[0x801D9C8E]); 0 lower, 1 upper.</summary>
    public static int Floor = -1;

    static bool? _forcedOn, _forcedMinimap;
    static bool _probe;

    // ---- the reading -------------------------------------------------------

    /// <summary>The last copy of the grid. Indexed `Tiles[z * RowBytes + x * Stride + half + field]`.</summary>
    public static readonly byte[] Tiles = new byte[Span * RowBytes];

    /// <summary>True once <see cref="Refresh"/> has seen an area running.</summary>
    public static bool InGame { get; private set; }

    public static int PlayerX, PlayerY, PlayerZ, PlayerYaw, PlayerHalf, Area;

    /// <summary>The height bytes actually present in the loaded area, over the
    /// halves that are drawn. Recomputed with the copy, so a flat area does not
    /// come out uniformly black.</summary>
    public static int MinHeight, MaxHeight;

    /// <summary>Occupied halves in the last copy — the probe's headline, and the
    /// cheapest test that the addresses are still right.</summary>
    public static int Occupied;

    /// <summary>
    /// False while the grid is the cleared one the game leaves between areas.
    ///
    /// **A blank grid reads as a full one**, which is the trap here: the renderer
    /// draws a half whose model index is below 240, and a zeroed record's index is
    /// 0, so 80x80x2 zeroed halves all pass the test — measured, "12800 occupied
    /// halves, height 0..0" at the boot, against 5,126 and 6,313 in the two real
    /// areas. Drawn as-is that is a solid block covering the map. Every half
    /// occupied at exactly one height is not something a real area can be, so that
    /// is the test.
    /// </summary>
    public static bool Ready => Occupied < Span * Span * 2 && MaxHeight != MinHeight;

    static long _copiedAt = long.MinValue;
    static int _copiedArea = -1;
    static bool _dumpPending;

    /// <summary>Milliseconds between grid copies. Four a second is plenty for
    /// architecture that moves at a drawbridge's pace.</summary>
    const long CopyPeriodMs = 250;

    // ---- lifecycle ---------------------------------------------------------

    public static void Configure(string? on, string? minimap, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(on))
            _forcedOn = !on.Equals("0", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(minimap))
            _forcedMinimap = !minimap.Equals("0", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(probe) && !probe.Equals("0", StringComparison.Ordinal))
            _probe = true;
    }

    public static void Install()
    {
        Enabled = _forcedOn ?? true;
        Minimap = _forcedMinimap ?? false;

        // ConfigManager.Load runs inside HostWindow.Initialize, which is after
        // Program.cs — so the saved settings can only be read here, and an env var
        // beats them for the run. RuntimeReadyEvent is also the first moment
        // PanelManager and the ImGui context exist, which is why registration
        // shares the listener.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            Enabled       = _forcedOn ?? view.GetBool(OnKey, true);
            Minimap       = _forcedMinimap ?? view.GetBool(MinimapKey, false);
            MinimapSize   = view.GetInt(SizeKey, MinimapSize);
            MinimapRadius = view.GetInt(RadiusKey, MinimapRadius);
            MinimapCorner = view.GetInt(CornerKey, MinimapCorner);
            MinimapPad    = view.GetInt(PadKey, MinimapPad);
            MinimapShape   = view.GetInt(ShapeKey, MinimapShape);
            MinimapOpacity = view.GetFloat(OpacityKey, MinimapOpacity);
            Shade         = view.GetBool(ShadeKey, true);
            Walls         = view.GetBool(WallsKey, true);
            Floor         = view.GetInt(FloorKey, -1);

            // Registered whether or not the feature is on, so the switch under
            // Gameplay is not a dead control for the rest of the session. Enabled
            // gates the panels' own IsOpen instead.
            RegisterUi();

            Console.WriteLine(Enabled
                ? $"[KF2] map: on, M opens the map, N toggles the minimap " +
                  $"(minimap {(Minimap ? "on" : "off")}{(_probe ? ", probing" : "")})"
                : "[KF2] map: off");
        });

        // An area module load invalidates the copy, so the next Draw re-reads
        // rather than showing the old area until the timer runs out.
        //
        // **This is deliberately not hooked on the area loader, and that was
        // measured.** func_8001689C is where the 0x3E80-word copy into
        // 0x801C8484 lives (generated/game.cs:4231), so a post-hook on it looks
        // like the exact "the grid is now the new area's" signal -- but the
        // function is 1716 bytes with the load in a branch and **the main loop
        // calls it every frame**: 5,673 calls in 40 seconds at 144 fps. Hooking
        // it turned a four-a-second grid copy into a per-frame one and dumped the
        // probe 5,673 times. The copy is cheap and self-correcting instead.
        Event.AddListener<OverlayLoadedEvent>(_ => Invalidate());

        // The hotkeys come off the event bus rather than being polled, for the
        // reason patches/Mouse.cs records: every hook this port owns is in the
        // walking-around part of the game, so a polled toggle would be dead in the
        // in-game menu, on the title screen and through a load. PadReadEvent is
        // emphatically not the bus for this — that call is polled hundreds of
        // thousands of times a second.
        Event.AddListener<KeyboardEvent>(e =>
        {
            if (!Enabled || !e.Pressed || PopupManager.AnyOpen) return;
            if (e.Key == (int)Key.M) ToggleMap();
            else if (e.Key == (int)Key.N) SetMinimap(!Minimap);
        });
    }

    static bool _registered;

    static void RegisterUi()
    {
        if (_registered) return;
        _registered = true;

        // Localization.T falls back to English with a warning and then prints the
        // key itself, so a key the runtime has never heard of has to supply all
        // three of its languages. menu.game is new; the runtime has only
        // menu.system, menu.mods and menu.debug.
        Localization.Merge("""
        {
          "strings": {
            "menu.game":     { "en": "Game", "pt-BR": "Jogo",  "es-419": "Juego" },
            "menu.game.map": { "en": "Map",  "pt-BR": "Mapa",  "es-419": "Mapa"  }
          }
        }
        """);

        PanelManager.Register(MapPanel.Instance);
        PanelManager.Register(MapOverlay.Instance);

        // ConfigManager.ApplyViewToPanels runs inside HostWindow's Load, which is
        // *before* RuntimeReadyEvent -- so a panel registered here has already
        // missed it and would always come up closed. Apply it to ours by hand.
        // (MapOverlay ignores it: its open state is the setting, not the view.)
        ConfigManager.ApplyViewToPanels([MapPanel.Instance]);

        // Panels do not auto-populate the menu bar — MainMenuBar declares every
        // built-in one by hand — so without this the map is hotkey-only.
        MenuRegistry.Menu("menu.game", MenuRegistry.OrderGame)
                    .Panel<MapPanel>("menu.game.map")
                    .End();

        // The probe opens the map as well as dumping it. A headless run cannot
        // press M, so without this the panel's own draw path is never exercised
        // by anything that is not a person at the keyboard.
        if (_probe) MapPanel.Instance.IsOpen = true;
    }

    /// <summary>Turn the whole feature on or off at run time. Closes the full map
    /// on the way out, since a panel whose feature is off should not stay up.</summary>
    public static void SetEnabled(bool on)
    {
        Enabled = on;
        if (!on) MapPanel.Instance.IsOpen = false;
    }

    public static void ToggleMap()
    {
        var p = PanelManager.Get<MapPanel>();
        if (p != null) p.IsOpen = !p.IsOpen;
    }

    public static void SetMinimap(bool on)
    {
        Minimap = on;
        Settings.PatchSettings.Set(MinimapKey, on);
    }

    // ---- reading the game --------------------------------------------------

    /// <summary>
    /// Bring <see cref="Tiles"/> and the player fix up to date. Called at the top
    /// of each panel's Draw, which runs inside the game's own VSync call, so the
    /// memory read here is the memory the game left. Returns false when no area is
    /// running, which is the panels' cue to draw nothing.
    /// </summary>
    public static bool Refresh()
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) { InGame = false; return false; }

        // buf2 is cleared until an area is up, so a max HP of zero is "the title
        // screen or a load", not "a dead character".
        if (m.ReadU16(MaxHpAddr) == 0) { InGame = false; return false; }

        InGame = true;
        PlayerX = (int)m.ReadU32(PosXAddr);
        PlayerY = (int)m.ReadU32(PosYAddr);
        PlayerZ = (int)m.ReadU32(PosZAddr);
        PlayerYaw = (short)m.ReadU16(YawAddr);
        PlayerHalf = m.ReadU16(HalfSelAddr) == 0 ? 0 : HalfBytes;
        Area = m.ReadU8(AreaAddr);

        long now = Environment.TickCount64;
        if (Area != _copiedArea || now - _copiedAt >= CopyPeriodMs) Copy(m, now);
        return Ready;
    }

    /// <summary>The half a viewport should draw: the player's floor unless the
    /// settings pin one.</summary>
    public static int HalfOffset => Floor < 0 ? PlayerHalf : (Floor == 0 ? 0 : HalfBytes);

    public static int TileOf(int world) => world / TileUnits;

    /// <summary>Tile coordinates carrying the sub-tile fraction, for the arrow.</summary>
    public static float TileF(int world) => world / (float)TileUnits;

    /// <summary>
    /// The screen row a world Z draws on, sub-tile fraction and all.
    ///
    /// **A map is the plane seen from above and this world's Y axis points
    /// down** — a half's floor is <c>-(height &lt;&lt; 7)</c>, so up is -Y. Looking
    /// along +Y with +X to the right therefore puts +Z at the *top* of the
    /// screen, and laying screen Y out along +Z instead draws the area
    /// mirrored. That mirror is what made the arrow swing clockwise when the
    /// player turned left: the heading func_80028080 walks is
    /// <c>(-sin yaw, cos yaw)</c>, so yaw increasing takes you from +Z toward
    /// -X, which is left of forward only while the view is not flipped.
    ///
    /// Row 0 is tile <c>Span-1</c>, and the sub-tile fraction runs backwards with
    /// it — a point a tenth of the way into a tile is nine tenths of the way down
    /// its row — so this is <c>Span - TileF</c>, not <c>Span - 1 - TileF</c>.
    /// </summary>
    public static float RowF(int world) => Span - TileF(world);

    /// <summary>The tile a screen row draws, which is also its own inverse.</summary>
    public static int RowOf(int tile) => Span - 1 - tile;

    public static bool Drawn(int x, int z, int half)
        => (uint)x < Span && (uint)z < Span
           && Tiles[z * RowBytes + x * Stride + half + Model] < NotDrawn;

    public static byte Byte(int x, int z, int off)
        => (uint)x < Span && (uint)z < Span ? Tiles[z * RowBytes + x * Stride + off] : (byte)0xFF;

    static void Copy(IMemory m, long now)
    {
        _copiedArea = Area;
        _copiedAt = now;

        // 16,000 words rather than 64,000 bytes: ReadU32 does the same range
        // checks as ReadU8 and answers four times as much.
        for (int i = 0; i < Tiles.Length; i += 4)
        {
            uint w = m.ReadU32(TileBase + (uint)i);
            Tiles[i]     = (byte)w;
            Tiles[i + 1] = (byte)(w >> 8);
            Tiles[i + 2] = (byte)(w >> 16);
            Tiles[i + 3] = (byte)(w >> 24);
        }

        int lo = 255, hi = 0, n = 0;
        for (int i = 0; i < Tiles.Length; i += Stride)
            for (int half = 0; half < Stride; half += HalfBytes)
            {
                if (Tiles[i + half + Model] >= NotDrawn) continue;
                n++;
                int h = Tiles[i + half + HeightByte];
                if (h < lo) lo = h;
                if (h > hi) hi = h;
            }

        Occupied = n;
        MinHeight = n == 0 ? 0 : lo;
        MaxHeight = n == 0 ? 0 : hi;

        // **A dump waits for the reading to settle.** An overlay load moves the
        // area byte before func_8001689C has copied the new grid in, so the first
        // copy after one can be the area you just left -- measured, a player
        // 12,800 units off the floor the map was reading, on a tile the map
        // called empty. The player standing on a half the renderer draws is the
        // invariant that says the two agree, so the dump waits for it and the
        // 250 ms timer keeps re-copying until it holds. The map itself is stale
        // for the same window, which is a fraction of a second of loading screen.
        if (_probe && _dumpPending && Ready && Drawn(TileOf(PlayerX), TileOf(PlayerZ), PlayerHalf))
        {
            _dumpPending = false;
            Dump();
        }
    }

    /// <summary>An area module has loaded: re-copy on the next Draw rather than
    /// waiting out the timer, and arm a dump for once the reading settles.</summary>
    public static void Invalidate() { _copiedArea = -1; _dumpPending = true; }

    // ---- the probe ---------------------------------------------------------

    /// <summary>
    /// The grid as ASCII, one line a row, both halves. This is the only oracle
    /// short of a person looking at the screen: an empty dump means the address or
    /// the stride is wrong, and a dump whose occupied extent does not move between
    /// areas means the copy is not being taken.
    /// </summary>
    public static void Dump()
    {
        const string ramp = ".:-=+*#%@";
        int range = Math.Max(1, MaxHeight - MinHeight);

        int px = TileOf(PlayerX), pz = TileOf(PlayerZ);
        bool stands = Drawn(px, pz, PlayerHalf);
        int hb = Byte(px, pz, PlayerHalf + HeightByte);

        Console.WriteLine($"[KF2] map: area {Area}, {Occupied} occupied halves, " +
                          $"height {MinHeight}..{MaxHeight}");

        // The check worth printing every time: the player has to be standing on a
        // half the renderer draws, and the half's floor Y has to be near their own.
        // A "not drawn" here means the half selector or the model test is wrong; a
        // large gap means the height byte is not the floor.
        Console.WriteLine($"[KF2] map: player tile {px},{pz} on the " +
                          $"{(PlayerHalf == 0 ? "lower" : "upper")} floor, " +
                          $"{(stands ? "drawn" : "NOT DRAWN")}, " +
                          $"height byte {hb} -> floor Y {-(hb << 7)} against player Y {PlayerY} " +
                          $"(gap {PlayerY - -(hb << 7)})");

        for (int half = 0; half < Stride; half += HalfBytes)
        {
            Console.WriteLine($"[KF2] map: --- {(half == 0 ? "lower" : "upper")} floor ---");
            for (int z = 0; z < Span; z++)
            {
                var line = new char[Span];
                for (int x = 0; x < Span; x++)
                {
                    int b = z * RowBytes + x * Stride + half;
                    if (Tiles[b + Model] >= NotDrawn) { line[x] = ' '; continue; }
                    int h = (Tiles[b + HeightByte] - MinHeight) * (ramp.Length - 1) / range;
                    line[x] = ramp[Math.Clamp(h, 0, ramp.Length - 1)];
                }
                Console.WriteLine($"[KF2] map: {z,2} |{new string(line)}|");
            }
        }
    }
}
