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
/// The <c>visible</c> predicate is the seam for fog of war. It is
/// <c>null</c> — everything revealed — in this pass, because that is what was
/// asked for; filling it in needs no new reverse engineering. The 24x24 grid at
/// 0x80192EAC that func_8002D3A8 rebuilds each frame *is* the set of tiles the
/// player can see, occlusion already computed, and the world tile of its cell
/// (0,0) is the pair at 0x80192EA0/0xA4. OR that into an 80x80 bitset per area
/// (u8[0x8017E060]) per save slot (u8[0x8006E5D4]) and pass its lookup here. A
/// cell is visible iff its byte is nonzero — patches/CullGrid.cs:607.
/// </summary>
public static class MapRender
{
    // The palette is built once. Tiles are the lit thing and the ground is dark,
    // so a corridor reads as a bright line on a dark field rather than as a hole.
    const int Shades = 24;
    static readonly uint[] _shade = new uint[Shades];
    static uint _flat, _wall, _grid, _arrow, _arrowEdge, _ground;
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
        _grid      = Rgba(1f, 1f, 1f, 0.05f);
        _arrow     = Rgba(1.00f, 0.82f, 0.25f, 1f);
        _arrowEdge = Rgba(0.10f, 0.08f, 0.02f, 0.9f);
        _ground    = Rgba(0.07f, 0.08f, 0.10f, 1f);
    }

    static uint Rgba(float r, float g, float b, float a) => ImGui.GetColorU32(new Vector4(r, g, b, a));

    public static uint Ground { get { Build(); return _ground; } }

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
    /// </summary>
    public static void Draw(ImDrawListPtr dl, Vector2 origin, float cell,
                            int x0, int z0, int x1, int z1,
                            int half, bool shade, bool walls, bool grid,
                            Func<int, int, bool>? visible)
    {
        Build();

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
                if (visible != null && !visible(x, z)) continue;

                uint c = _flat;
                if (shade)
                {
                    int s = (Map.Tiles[b + Map.HeightByte] - Map.MinHeight) * (Shades - 1) / range;
                    c = _shade[Math.Clamp(s, 0, Shades - 1)];
                }

                var a = new Vector2(origin.X + x * cell, origin.Y + r * cell);
                dl.AddRectFilled(a, new Vector2(a.X + cell, a.Y + cell), c);

                // The bit the visibility flood stops on. Drawn over the tile
                // rather than instead of it, because whether it means "wall" or
                // "see through" is not settled — see the class comment on Map.
                if (walls && (Map.Tiles[b + Map.Flags] & Map.StopsFlood) != 0)
                    dl.AddRectFilled(a, new Vector2(a.X + cell, a.Y + cell), _wall);
            }
        }

        if (!grid || cell < 6f) return;
        for (int x = x0; x <= x1 + 1; x++)
            dl.AddLine(new Vector2(origin.X + x * cell, origin.Y + z0 * cell),
                       new Vector2(origin.X + x * cell, origin.Y + (z1 + 1) * cell), _grid);
        for (int z = z0; z <= z1 + 1; z++)
            dl.AddLine(new Vector2(origin.X + x0 * cell, origin.Y + z * cell),
                       new Vector2(origin.X + (x1 + 1) * cell, origin.Y + z * cell), _grid);
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
