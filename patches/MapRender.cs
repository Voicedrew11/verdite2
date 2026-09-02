using System.Numerics;
using ImGuiNET;

namespace Kf2;

/// <summary>
/// The one drawing routine every map viewport calls.
///
/// The full-screen map (patches/MapFullscreen.cs), the docked panel
/// (patches/MapPanel.cs) and the corner minimap (patches/MapOverlay.cs) differ
/// only in their rectangle, their scale and their chrome, so the tiles, the
/// palette and the player live here and none of the three owns them. All are
/// **north-up**, deliberately: a rotating minimap disagrees with the maps about
/// which way the area faces, and a maze is easier to hold in your head when north
/// stays put.
///
/// The <c>state</c> predicate is fog of war, and patches/MapFog.cs fills it in:
/// it answers **0 unexplored, 1 remembered, 2 in view right now** for a tile, and
/// <c>null</c> — everything revealed — is what both viewports pass while fog is
/// off. It takes a **world tile**, not a screen row, which is the thing a caller
/// working in screen space gets wrong.
///
/// It is a tri-state rather than the bool it was first seamed as because the
/// third state costs nothing: the accumulator has the frame's own visibility grid
/// in its hand anyway, so "you can see this from where you stand" is free and
/// gives the map a live cone.
/// </summary>
public static class MapRender
{
    // The palette is built once. Tiles are the lit thing and the ground is dark,
    // so a corridor reads as a bright line on a dark field rather than as a hole.
    const int Shades = 24;
    static readonly uint[] _shade = new uint[Shades];
    static uint _flat, _wall, _lit, _grid, _arrow, _arrowEdge, _ground, _dot, _dotEdge;
    static uint _creature, _object, _effect, _sprite, _markEdge;
    static bool _built;

    static void Build()
    {
        if (_built) return;
        _built = true;

        for (int i = 0; i < Shades; i++)
        {
            // Low ground cool and dark, high ground warm and pale: a ramp that
            // still separates when the area is nearly flat.
            float t = i / (float)(Shades - 1);
            _shade[i] = Rgba(0.24f + 0.52f * t, 0.30f + 0.50f * t, 0.38f + 0.34f * t, 1f);
        }

        _flat      = Rgba(0.55f, 0.62f, 0.66f, 1f);
        _wall      = Rgba(0.16f, 0.18f, 0.24f, 0.85f);
        _lit       = Rgba(1f, 0.97f, 0.85f, 0.14f);
        _grid      = Rgba(1f, 1f, 1f, 0.05f);
        _arrow     = Rgba(1.00f, 0.82f, 0.25f, 1f);
        _arrowEdge = Rgba(0.10f, 0.08f, 0.02f, 0.9f);
        _ground    = Rgba(0.07f, 0.08f, 0.10f, 1f);

        // The dot is the arrow's colour with a heavier rim: it sits inside a
        // tile rather than on top of one, so its edge is what separates it from
        // the pale end of the height ramp.
        _dot       = Rgba(1.00f, 0.86f, 0.36f, 1f);
        _dotEdge   = Rgba(0.08f, 0.06f, 0.02f, 1f);

        // The marker palette. Chosen to separate from the tile ramp, which runs
        // cool-dark to warm-pale: a creature is the one thing a player is looking
        // for in a hurry, so it gets the only saturated red on the map.
        _creature  = Rgba(0.95f, 0.25f, 0.22f, 1f);
        _object    = Rgba(0.35f, 0.80f, 0.95f, 1f);
        _effect    = Rgba(0.85f, 0.40f, 0.95f, 1f);
        _sprite    = Rgba(0.95f, 0.75f, 0.30f, 1f);
        _markEdge  = Rgba(0.05f, 0.05f, 0.07f, 0.85f);
    }

    static uint Rgba(float r, float g, float b, float a) => ImGui.GetColorU32(new Vector4(r, g, b, a));

    public static uint Ground { get { Build(); return _ground; } }

    /// <summary>
    /// The same colour, drawn at <paramref name="alpha"/> of its own opacity.
    ///
    /// The palette is built once as packed <c>IM_COL32</c> words — R in the low
    /// byte, **A in the high one** — so a viewport that wants the game to show
    /// through it scales that byte rather than rebuilding a palette of its own.
    /// A colour that is already translucent (the wall tint, the lit tint, the
    /// grid) stays proportionally so, which is what keeps the tints reading as
    /// tints at any opacity.
    /// </summary>
    public static uint Fade(uint c, float alpha)
    {
        if (alpha >= 0.999f) return c;
        uint a = (uint)Math.Clamp((c >> 24) * Math.Clamp(alpha, 0f, 1f), 0f, 255f);
        return (c & 0x00FFFFFFu) | (a << 24);
    }

    /// <summary>
    /// Fill the tiles of <paramref name="half"/> that fall in the inclusive
    /// window x0..x1 / z0..z1. <paramref name="origin"/> is where the grid's
    /// top-left corner lands in screen space and <paramref name="cell"/> is a
    /// tile's side in pixels, so a viewport is expressed entirely by those two.
    ///
    /// **x0..x1 are tiles and z0..z1 are screen rows**, because the map is drawn
    /// from above and the world's Y points down, so screen Y runs along -Z — see
    /// <c>Map.RowF</c>. Both axes are 0..Span-1 and <c>Map.RowOf</c> converts, so
    /// a caller that works in screen space (which both viewports do) needs no
    /// arithmetic of its own.
    ///
    /// <paramref name="state"/> is fog of war: see the class comment. Null draws
    /// every tile the renderer would.
    /// </summary>
    public static void Draw(ImDrawListPtr dl, Vector2 origin, float cell,
                            int x0, int z0, int x1, int z1,
                            int half, bool shade, bool walls, bool grid,
                            Func<int, int, int>? state,
                            float alpha = 1f,
                            Vector2? circleCentre = null, float circleRadius = 0f)
    {
        Build();

        bool circle = circleCentre.HasValue && circleRadius > 0f;
        float ccx = circle ? circleCentre!.Value.X : 0f;
        float ccy = circle ? circleCentre!.Value.Y : 0f;
        float rr = circleRadius * circleRadius;

        x0 = Math.Max(x0, 0); z0 = Math.Max(z0, 0);
        x1 = Math.Min(x1, Map.Span - 1); z1 = Math.Min(z1, Map.Span - 1);

        int range = Math.Max(1, Map.MaxHeight - Map.MinHeight);

        for (int r = z0; r <= z1; r++)
        {
            int z = Map.RowOf(r);
            int row = z * Map.RowBytes;
            for (int x = x0; x <= x1; x++)
            {
                int b = row + x * Map.Stride + half;
                if (Map.Tiles[b + Map.Model] >= Map.NotDrawn) continue;

                int fog = state == null ? 1 : state(x, z);
                if (fog == 0) continue;

                uint c = _flat;
                if (shade)
                {
                    int s = (Map.Tiles[b + Map.HeightByte] - Map.MinHeight) * (Shades - 1) / range;
                    c = _shade[Math.Clamp(s, 0, Shades - 1)];
                }

                var a = new Vector2(origin.X + x * cell, origin.Y + r * cell);
                var e = new Vector2(a.X + cell, a.Y + cell);

                // A round viewport, done by cutting each tile back to the disc
                // rather than by clipping: ImGui's clip rects are rectangles and a
                // draw list cannot erase what it has already drawn, so a mask ring
                // would have to be painted in the ground colour — which is exactly
                // what must *not* be opaque here. Each tile is clamped instead to
                // the chords the circle allows at its far edges, which never
                // reaches outside the disc and can fall a fraction of a tile short
                // of it: the edge is scalloped by up to one cell at the diagonals.
                if (circle && !ClipToCircle(ref a, ref e, ccx, ccy, rr)) continue;

                dl.AddRectFilled(a, e, Fade(c, alpha));

                // The bit the visibility flood stops on. Drawn over the tile
                // rather than instead of it, because whether it means "wall" or
                // "see through" is not settled — see the class comment on Map.
                if (walls && (Map.Tiles[b + Map.Flags] & Map.StopsFlood) != 0)
                    dl.AddRectFilled(a, e, Fade(_wall, alpha));

                // In the cull cone as of the last sample: drawn over the tile, so
                // the shading underneath still reads.
                if (fog == 2)
                    dl.AddRectFilled(a, e, Fade(_lit, alpha));
            }
        }

        if (!grid || cell < 6f) return;
        uint gridCol = Fade(_grid, alpha);
        for (int x = x0; x <= x1 + 1; x++)
            dl.AddLine(new Vector2(origin.X + x * cell, origin.Y + z0 * cell),
                       new Vector2(origin.X + x * cell, origin.Y + (z1 + 1) * cell), gridCol);
        for (int z = z0; z <= z1 + 1; z++)
            dl.AddLine(new Vector2(origin.X + x0 * cell, origin.Y + z * cell),
                       new Vector2(origin.X + (x1 + 1) * cell, origin.Y + z * cell), gridCol);
    }

    /// <summary>
    /// Shrink a tile's rect to what fits inside the disc, or return false when
    /// none of it does.
    ///
    /// The clamp is taken at each axis's **far** edge — the corner of the tile
    /// furthest from the centre — so the chord it allows is the narrowest the
    /// tile spans and the result is always inside the circle. Taking the near
    /// edge instead would let the corners spill past the border ring, which is
    /// the one artefact a drawn outline makes obvious.
    /// </summary>
    static bool ClipToCircle(ref Vector2 a, ref Vector2 e, float cx, float cy, float rr)
    {
        float dxFar = Math.Max(Math.Abs(a.X - cx), Math.Abs(e.X - cx));
        float dyFar = Math.Max(Math.Abs(a.Y - cy), Math.Abs(e.Y - cy));

        float wSq = rr - dyFar * dyFar;
        float hSq = rr - dxFar * dxFar;
        if (wSq <= 0f || hSq <= 0f) return false;

        float halfW = MathF.Sqrt(wSq), halfH = MathF.Sqrt(hSq);

        a.X = Math.Max(a.X, cx - halfW); e.X = Math.Min(e.X, cx + halfW);
        a.Y = Math.Max(a.Y, cy - halfH); e.Y = Math.Min(e.Y, cy + halfH);

        return e.X > a.X && e.Y > a.Y;
    }

    /// <summary>
    /// The world tables, over the tiles: patches/MapMarkers.cs sampled them and
    /// this draws them.
    ///
    /// <paramref name="origin"/> and <paramref name="cell"/> are the same two
    /// numbers <see cref="Draw"/> takes, so a viewport expresses the marker layer
    /// exactly the way it expresses the floor plan. A marker's screen row is
    /// <c>Map.RowF</c> of its world Z, sub-tile fraction and all — the map is the
    /// plane seen from above, so screen Y runs along -Z.
    ///
    /// **A marker is a shape as well as a colour**, because the minimap draws them
    /// at four or five pixels and a colour alone is not enough at that size: a
    /// creature is a triangle, a prop a square, an effect a diamond, a billboard a
    /// dot. Each carries a dark outline so it reads over both ends of the height
    /// ramp.
    ///
    /// **They do not fade with the minimap's opacity**, for the reason the
    /// player's arrow does not: the opacity setting exists so the *ground* stops
    /// hiding the game, and a marker you cannot see is not worth drawing. The
    /// outline is what keeps them legible over the game picture instead.
    /// </summary>
    public static void DrawMarkers(ImDrawListPtr dl, Vector2 origin, float cell,
                                   int half, Func<int, int, int>? state, float size,
                                   Vector2? circleCentre = null, float circleRadius = 0f)
    {
        Build();

        bool circle = circleCentre.HasValue && circleRadius > 0f;
        float rr = circleRadius * circleRadius;

        foreach (var mk in MapMarkers.Live)
        {
            if (!MapMarkers.Visible(mk, half, state)) continue;

            var p = new Vector2(origin.X + Map.TileF(mk.X) * cell,
                                origin.Y + Map.RowF(mk.Z) * cell);

            // The disc is cut on the marker's centre rather than on its shape:
            // a marker is a few pixels across, so clamping it the way a tile is
            // clamped would only ever produce a sliver.
            if (circle)
            {
                float dx = p.X - circleCentre!.Value.X, dy = p.Y - circleCentre.Value.Y;
                if (dx * dx + dy * dy > rr) continue;
            }

            uint c = mk.Kind switch
            {
                MapMarkers.Kind.Creature => _creature,
                MapMarkers.Kind.Object   => _object,
                MapMarkers.Kind.Effect   => _effect,
                _                        => _sprite,
            };

            switch (mk.Kind)
            {
                case MapMarkers.Kind.Creature:
                {
                    // North-up like everything else on the map, so the triangle
                    // points up rather than along the creature's heading; the
                    // heading is the optional spoke below.
                    float s = size;
                    var a = new Vector2(p.X, p.Y - s);
                    var b = new Vector2(p.X - s * 0.85f, p.Y + s * 0.75f);
                    var d = new Vector2(p.X + s * 0.85f, p.Y + s * 0.75f);
                    dl.AddTriangleFilled(a, b, d, c);
                    dl.AddTriangle(a, b, d, _markEdge, 1f);

                    if (MapMarkers.Facing && size >= 3f)
                    {
                        float ang = -(mk.Yaw * (float)(2 * Math.PI) / Map.Turn + (float)(Math.PI / 2));
                        var tip = new Vector2(p.X + MathF.Cos(ang) * s * 2.2f,
                                              p.Y + MathF.Sin(ang) * s * 2.2f);
                        dl.AddLine(p, tip, c, 1.4f);
                    }
                    break;
                }

                case MapMarkers.Kind.Object:
                {
                    float s = size * 0.8f;
                    var a = new Vector2(p.X - s, p.Y - s);
                    var b = new Vector2(p.X + s, p.Y + s);
                    dl.AddRectFilled(a, b, c);
                    dl.AddRect(a, b, _markEdge);
                    break;
                }

                case MapMarkers.Kind.Effect:
                {
                    float s = size * 0.9f;
                    dl.AddQuadFilled(new Vector2(p.X, p.Y - s), new Vector2(p.X + s, p.Y),
                                     new Vector2(p.X, p.Y + s), new Vector2(p.X - s, p.Y), c);
                    break;
                }

                default:
                    dl.AddCircleFilled(p, MathF.Max(1.5f, size * 0.55f), c, 6);
                    break;
            }
        }
    }

    /// <summary>
    /// The player, as an arrow pointing the way they face.
    ///
    /// **The angle is derived from the game's own movement, not guessed.**
    /// func_80028080 is what a walk step goes through, and it adds
    /// `-sin(yaw) * d` to the X at 0x801994EC and `+cos(yaw) * d` to the Z at
    /// 0x801994F4 (generated/game.cs:26043-26071; func_8005EB08 is odd, so it is
    /// sine, and func_8005EC10 takes |a0|, so it is cosine). So the heading on the
    /// ground is `(-sin yaw, cos yaw)` — yaw 0 faces +Z and yaw 0x400 faces -X.
    ///
    /// Since the world's own up is -Y, that heading turns you **left** as yaw
    /// increases: up x forward is `(-Y) x (+Z) = -X`. The map is the same plane
    /// seen from above, so screen X is world X and screen Y runs along **-Z**
    /// (`Map.RowF`), which makes the ground heading `(-sin yaw, -cos yaw)` in
    /// screen space — `(cos, sin)` of `-(yaw + pi/2)`.
    ///
    /// **The screen angle therefore decreases as yaw increases**, and that is the
    /// whole of the correction: the first version drew the plane with screen Y
    /// along +Z, which is the view from *below*, and a mirrored map turns the
    /// right way round the wrong way — reported as the arrow swinging right when
    /// the player turned left. Negating the angle alone would have squared the
    /// arrow with the report and left it pointing across the direction of travel,
    /// because the mirror was in the map rather than in the arrow.
    ///
    /// **<paramref name="dot"/> draws the other marker entirely**, and it is the
    /// default: see <see cref="DrawPlayerDot"/>. The arrow above is kept as the
    /// setting's other entry, so everything this comment records about the angle
    /// stays live rather than becoming archaeology.
    ///
    /// **The arrow does not fade with the rest of the map.** The minimap's
    /// opacity is there so the game shows through the ground and the tiles; a
    /// "you are here" marker that dims with them is the one thing that has to
    /// stay findable, so it is drawn at full opacity whatever the setting says.
    /// </summary>
    public static void DrawPlayer(ImDrawListPtr dl, Vector2 origin, float cell, float size, bool dot)
    {
        Build();

        if (dot) { DrawPlayerDot(dl, origin, cell); return; }

        var p = new Vector2(origin.X + Map.TileF(Map.PlayerX) * cell,
                            origin.Y + Map.RowF(Map.PlayerZ) * cell);

        float a = -(Map.PlayerYaw * (float)(2 * Math.PI) / Map.Turn + (float)(Math.PI / 2));
        float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);

        Vector2 At(float fx, float fy) =>
            new(p.X + (fx * ca - fy * sa) * size, p.Y + (fx * sa + fy * ca) * size);

        var tip  = At(1.0f, 0f);
        var left = At(-0.6f, -0.7f);
        var back = At(-0.25f, 0f);
        var right = At(-0.6f, 0.7f);

        dl.AddQuadFilled(tip, left, back, right, _arrow);
        dl.AddQuad(tip, left, back, right, _arrowEdge, 1.2f);
    }

    /// <summary>
    /// The player as a dot sitting in the middle of the tile they stand in.
    ///
    /// **This is a claim about accuracy, not a change of icon.** The arrow says
    /// two things the game never told the player: exactly where in the room they
    /// are, and exactly which way they are pointing — which is a satellite fix in
    /// a game that shipped no map at all. A dot in the centre of the occupied
    /// tile says only "you are in this square", which is what someone drawing
    /// their own map on paper would have known, and it is what this port offers
    /// by default.
    ///
    /// It is therefore drawn on the **tile**, not on the position: the world
    /// coordinate is put through <c>Map.TileOf</c> and back, so the dot steps a
    /// square at a time and does not slide across the cell as you walk. The
    /// centre is <c>tile + 0.5</c> along X and <c>Map.RowOf(tile) + 0.5</c> down
    /// the screen — the same row arithmetic the tiles themselves are drawn with,
    /// so the dot lands in the square the fill drew rather than half a cell off
    /// it.
    ///
    /// The radius follows the cell rather than the caller's size, because
    /// "occupies the square" is the whole point: at the minimap's four or five
    /// pixels a tile it is a dot, and on the full map it fills the square it is
    /// in. Full opacity for the reason the arrow is: the minimap's opacity is
    /// there so the ground stops hiding the game, and a "you are here" you cannot
    /// find is not worth drawing.
    /// </summary>
    public static void DrawPlayerDot(ImDrawListPtr dl, Vector2 origin, float cell)
    {
        Build();

        int tx = Map.TileOf(Map.PlayerX);
        int row = Map.RowOf(Map.TileOf(Map.PlayerZ));

        var p = new Vector2(origin.X + (tx + 0.5f) * cell, origin.Y + (row + 0.5f) * cell);
        float r = MathF.Max(2.5f, cell * 0.34f);

        dl.AddCircleFilled(p, r, _dot, 0);
        dl.AddCircle(p, r, _dotEdge, 0, MathF.Max(1f, cell * 0.06f));
    }
}
