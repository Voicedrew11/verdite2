using System.Numerics;
using ImGuiNET;

namespace Kf2;

/// <summary>
/// The one drawing routine both map viewports call.
///
/// The full map (patches/MapPanel.cs) and the corner minimap
/// (patches/MapOverlay.cs) differ only in their viewport and their chrome, so the
/// tiles, the palette and the arrow live here and neither owns them. Both are
/// **north-up**, deliberately: a rotating minimap disagrees with the full map
/// about which way the area faces, and a maze is easier to hold in your head when
/// north stays put.
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
    static uint _flat, _wall, _lit, _grid, _arrow, _arrowEdge, _ground;
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
    /// **The arrow does not fade with the rest of the map.** The minimap's
    /// opacity is there so the game shows through the ground and the tiles; a
    /// "you are here" marker that dims with them is the one thing that has to
    /// stay findable, so it is drawn at full opacity whatever the setting says.
    /// </summary>
    public static void DrawPlayer(ImDrawListPtr dl, Vector2 origin, float cell, float size)
    {
        Build();

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
}
