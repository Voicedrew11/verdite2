using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

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

    // ---- the native palette ------------------------------------------------
    //
    // **Read off the game's own map screen rather than chosen**, which is the
    // whole point of the style: the map item King's Field II hands the player is
    // a slate-green board in a pale metal frame, and every colour below is a
    // pixel sampled out of a capture of it.
    //
    //     field   #3A523A   (58, 82, 58)    the board, and the void alike
    //     ink     #0E200E   (14, 32, 14)    the floor plan, drawn as outline
    //     under   #2D3A2D   (45, 58, 45)    the mottled shading in the plan
    //     frame   #7B8C7F   (123,140,127)   the bevel's highlight
    //     player  #E7C4C5 / #F78272        the pale pointer and its salmon boss
    //
    // The single most important of them is that **the field and the room
    // interiors are the same colour**. The original does not fill its corridors;
    // it draws their walls, so the plan reads as line art and a maze looks like
    // something drawn on paper rather than something a satellite photographed.
    // That is the difference this style is for, and it is why the outline pass
    // below exists at all instead of a recolouring of the fill.
    static readonly uint[] _nShade = new uint[Shades];
    static uint _nField, _nInk, _nUnder, _nWall, _nLit;
    static uint _nFrameLo, _nFrameHi, _nFrameIn, _nText, _nTextDim;
    static uint _nBody, _nCore, _nEdge;
    static uint _nCreature, _nObject, _nEffect, _nSprite;

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

        _nField   = Rgba(0.227f, 0.322f, 0.227f, 1f);
        _nInk     = Rgba(0.055f, 0.125f, 0.055f, 1f);
        _nUnder   = Rgba(0.176f, 0.227f, 0.176f, 1f);

        // Height, in the board's own green. The ramp is deliberately narrow —
        // about a fifth of the blueprint's contrast — because a room interior
        // that reads as a *fill* is the thing this style exists to undo. It says
        // "this floor is higher than that one" without saying "this square is a
        // different kind of thing from the board".
        for (int i = 0; i < Shades; i++)
        {
            float t = i / (float)(Shades - 1);
            _nShade[i] = Rgba(0.196f + 0.086f * t, 0.278f + 0.106f * t, 0.196f + 0.062f * t, 1f);
        }

        _nWall    = Rgba(0.055f, 0.125f, 0.055f, 0.35f);
        _nLit     = Rgba(0.82f, 0.90f, 0.70f, 0.10f);

        _nFrameLo = Rgba(0.259f, 0.259f, 0.227f, 1f);
        _nFrameHi = Rgba(0.482f, 0.549f, 0.498f, 1f);
        _nFrameIn = Rgba(0.314f, 0.314f, 0.314f, 1f);

        _nText    = Rgba(0.78f, 0.84f, 0.74f, 1f);
        _nTextDim = Rgba(0.60f, 0.68f, 0.58f, 1f);

        _nBody    = Rgba(0.906f, 0.769f, 0.773f, 1f);
        _nCore    = Rgba(0.969f, 0.510f, 0.447f, 1f);
        _nEdge    = Rgba(0.055f, 0.125f, 0.055f, 1f);

        // The markers have no original to copy — the game's map shows nothing
        // standing in the area — so they are pulled *towards* the board instead:
        // desaturated to sit on slate green, with the creature keeping the only
        // warm hue for the reason the blueprint palette gives it one.
        _nCreature = Rgba(0.87f, 0.38f, 0.32f, 1f);
        _nObject   = Rgba(0.72f, 0.78f, 0.62f, 1f);
        _nEffect   = Rgba(0.70f, 0.62f, 0.82f, 1f);
        _nSprite   = Rgba(0.85f, 0.72f, 0.42f, 1f);
    }

    /// <summary>True while the map is drawn as the game's own board.</summary>
    static bool Native => Map.Style == Map.StyleNative;

    static uint Rgba(float r, float g, float b, float a) => ImGui.GetColorU32(new Vector4(r, g, b, a));

    /// <summary>The colour behind the tiles: the slate board in the native
    /// style, the near-black field in the blueprint one.</summary>
    public static uint Ground { get { Build(); return Native ? _nField : _ground; } }

    /// <summary>The native board's text colours, so the three viewports' chrome
    /// is the map's rather than the interface's.</summary>
    public static uint Text { get { Build(); return Native ? _nText : ImGui.GetColorU32(ImGuiCol.Text); } }
    public static uint TextDim { get { Build(); return Native ? _nTextDim : Fade(ImGui.GetColorU32(ImGuiCol.Text), 0.45f); } }

    /// <summary>
    /// The rectangle the game picture actually occupies, which is what a viewport
    /// drawn *over the game* must fit rather than the window.
    ///
    /// The picture is an <c>Image</c> inside the runtime's Output panel, fitted to
    /// that panel's content region at the display's aspect and centred in it — so
    /// the menu bar, the dockspace border and any docked panel take their share
    /// off it, and a window whose shape does not match the display's leaves a bar
    /// on two sides. A full-screen map sized to the viewport therefore covered the
    /// chrome as well as the game and did not line up with either: it looked like
    /// a window over the port rather than a screen the game had put up.
    /// <c>OutputView</c> (patches/recompone/0029) publishes the rectangle from the
    /// one place that knows it, once per frame, before any floating panel draws —
    /// <c>PanelManager</c> draws in registration order and the Output panel is
    /// registered by <c>HostWindow</c> long before <c>Program.cs</c> adds these.
    ///
    /// It **falls back to the viewport's work area** when no picture was drawn
    /// this frame, which is the state before the first frame is presented and
    /// while the panel is collapsed. Fitting nothing would be a blank map; fitting
    /// the window is what the map did already.
    /// </summary>
    public static void Picture(out Vector2 p0, out Vector2 p1)
    {
        if (OutputView.Valid && OutputView.Size.X >= 32f && OutputView.Size.Y >= 32f)
        {
            p0 = OutputView.Min;
            p1 = OutputView.Max;
            return;
        }

        var vp = ImGui.GetMainViewport();
        p0 = vp.WorkPos;
        p1 = vp.WorkPos + vp.WorkSize;
    }

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

        if (Native)
        {
            DrawNative(dl, origin, cell, x0, z0, x1, z1, half, shade, walls, state,
                       alpha, circle, ccx, ccy, rr);
            return;
        }

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
    /// The floor plan as the game itself draws it: **outlines, not fills**.
    ///
    /// This is the whole of the native style, and the difference is one decision.
    /// The blueprint pass above paints every walkable tile pale on a dark ground,
    /// so a corridor is a bright ribbon and the map reads like a satellite photo
    /// of the maze. The game's own map paints nothing: the board is one flat
    /// green and the plan is the *wall* between a walkable tile and a void one,
    /// inked in a green so dark it reads as black. A room is therefore a box you
    /// see the edges of, exactly as it would be if someone had drawn it on paper
    /// while walking it — which is what a map in a game with no automap is.
    ///
    /// An edge is drawn by the tile that is **visible**, on each side whose
    /// neighbour is not, so a shared wall is inked exactly once and no seam is
    /// doubled. <c>Vis</c> asks the grid rather than the window, so a plan does
    /// not sprout a border along the edge of whatever rectangle the caller
    /// happened to ask for — the window clamps which tiles are *iterated*, never
    /// what they are compared against.
    ///
    /// The mottled darker shapes in the original are reproduced as **the other
    /// stacked half**: a tile record holds two floors, and a square with
    /// something drawn on the floor you are not standing on is washed in
    /// <c>_nUnder</c> before the plan goes over it. That is a reading of the
    /// original's picture rather than a measurement of it — nothing here proves
    /// the game shaded the other floor — but it is the only thing in the tile
    /// record that produces shapes of that kind, and it is information the
    /// blueprint style throws away.
    /// </summary>
    static void DrawNative(ImDrawListPtr dl, Vector2 origin, float cell,
                           int x0, int z0, int x1, int z1,
                           int half, bool shade, bool walls,
                           Func<int, int, int>? state, float alpha,
                           bool circle, float ccx, float ccy, float rr)
    {
        int other = half == 0 ? Map.HalfBytes : 0;
        int range = Math.Max(1, Map.MaxHeight - Map.MinHeight);

        // Thin enough to stay a line at a minimap's four pixels a tile, heavy
        // enough to read as a wall on the full map. The original's is about a
        // sixth of a cell.
        float ink = MathF.Max(1f, cell * 0.17f);
        uint inkCol = Fade(_nInk, alpha);

        for (int r = z0; r <= z1; r++)
        {
            int z = Map.RowOf(r);
            int row = z * Map.RowBytes;
            for (int x = x0; x <= x1; x++)
            {
                int b = row + x * Map.Stride;
                int fog = state == null ? 1 : state(x, z);
                if (fog == 0) continue;

                bool here  = Map.Tiles[b + half + Map.Model] < Map.NotDrawn;
                bool below = Map.Tiles[b + other + Map.Model] < Map.NotDrawn;
                if (!here && !below) continue;

                var a = new Vector2(origin.X + x * cell, origin.Y + r * cell);
                var e = new Vector2(a.X + cell, a.Y + cell);
                if (circle && !ClipToCircle(ref a, ref e, ccx, ccy, rr)) continue;

                if (below)
                    dl.AddRectFilled(a, e, Fade(_nUnder, alpha));

                if (!here) continue;

                if (shade)
                {
                    int sh = (Map.Tiles[b + half + Map.HeightByte] - Map.MinHeight) * (Shades - 1) / range;
                    dl.AddRectFilled(a, e, Fade(_nShade[Math.Clamp(sh, 0, Shades - 1)], alpha));
                }
                else if (below)
                {
                    // The plan's own floor has to win back the square the other
                    // half washed, or a room over a room reads as a hole in it.
                    dl.AddRectFilled(a, e, Fade(_nField, alpha));
                }

                if (walls && (Map.Tiles[b + half + Map.Flags] & Map.StopsFlood) != 0)
                    dl.AddRectFilled(a, e, Fade(_nWall, alpha));

                if (fog == 2)
                    dl.AddRectFilled(a, e, Fade(_nLit, alpha));
            }
        }

        // The plan, in a second pass: an edge drawn as each tile is filled would
        // be painted over by the next tile's fill along a shared boundary.
        for (int r = z0; r <= z1; r++)
        {
            int z = Map.RowOf(r);
            for (int x = x0; x <= x1; x++)
            {
                if (!Vis(x, z, half, state)) continue;

                float ax = origin.X + x * cell, ay = origin.Y + r * cell;
                float ex = ax + cell, ey = ay + cell;

                if (circle)
                {
                    float dx = (ax + ex) * 0.5f - ccx, dy = (ay + ey) * 0.5f - ccy;
                    if (dx * dx + dy * dy > rr) continue;
                }

                // Screen Y runs along -Z (Map.RowF), so the tile above on screen
                // is z + 1 in the world and the one below is z - 1. Getting that
                // pair the wrong way round mirrors nothing visible — the plan is
                // symmetric under it — which is exactly why it is spelled out.
                if (!Vis(x, z + 1, half, state))
                    dl.AddLine(new Vector2(ax, ay), new Vector2(ex, ay), inkCol, ink);
                if (!Vis(x, z - 1, half, state))
                    dl.AddLine(new Vector2(ax, ey), new Vector2(ex, ey), inkCol, ink);
                if (!Vis(x - 1, z, half, state))
                    dl.AddLine(new Vector2(ax, ay), new Vector2(ax, ey), inkCol, ink);
                if (!Vis(x + 1, z, half, state))
                    dl.AddLine(new Vector2(ex, ay), new Vector2(ex, ey), inkCol, ink);
            }
        }
    }

    /// <summary>Is this world tile part of the plan? Off the grid is not, an
    /// undrawn half is not, and a tile the fog has not revealed is not — so the
    /// explored frontier is inked like a wall, which is what makes a half-walked
    /// area read as a plan rather than as a shape with a torn edge.</summary>
    static bool Vis(int x, int z, int half, Func<int, int, int>? state)
    {
        if ((uint)x >= Map.Span || (uint)z >= Map.Span) return false;
        if (Map.Tiles[z * Map.RowBytes + x * Map.Stride + half + Map.Model] >= Map.NotDrawn) return false;
        return state == null || state(x, z) != 0;
    }

    /// <summary>
    /// The board's bevelled metal frame, drawn just outside <paramref name="p0"/>
    /// / <paramref name="p1"/>.
    ///
    /// Sampled across the original's edge, the bevel is a soft ramp — dark at the
    /// outside, brightening to #7B8C7F, then dropping to a grey shadow where it
    /// meets the slate. Three concentric rectangles reproduce that at any size
    /// without a texture, and the width is a share of the board rather than a
    /// pixel count, so a minimap and a full-screen map wear the same frame.
    ///
    /// It does nothing in the blueprint style, whose edge is the interface's own
    /// border.
    /// </summary>
    public static void Frame(ImDrawListPtr dl, Vector2 p0, Vector2 p1, float alpha = 1f)
    {
        Build();
        if (!Native) return;

        float w = FrameWidth(p0, p1);

        // **Drawn inside the rectangle, not around it.** Both callers hand this a
        // rect that is already the edge of something clipped — an ImGui window in
        // the minimap's case — so a bevel painted outside would simply be cut
        // away on the two edges the map is pinned to. The plan is drawn under it
        // and the frame goes over, which costs the outermost tile ring nothing a
        // physical map does not also lose to its own frame.
        //
        // ImGui strokes a rect centred on its path, so each band is inset by half
        // its own thickness to sit flush.
        Band(w * 0.5f, w, _nFrameLo);
        Band(w * 1.25f, w * 0.9f, _nFrameHi);
        Band(w * 1.8f, MathF.Max(1f, w * 0.3f), _nFrameIn);

        void Band(float inset, float thick, uint col)
            => dl.AddRect(new Vector2(p0.X + inset, p0.Y + inset),
                          new Vector2(p1.X - inset, p1.Y - inset),
                          Fade(col, alpha), 0f, ImDrawFlags.None, thick);
    }

    /// <summary>How much of a rectangle the native frame eats on each side —
    /// zero in the blueprint style, which has no frame. A caller that wants its
    /// plan to clear the bevel insets by this.</summary>
    public static float FrameWidth(Vector2 p0, Vector2 p1)
    {
        Build();
        if (!Native) return 0f;
        return MathF.Max(2f, MathF.Min(p1.X - p0.X, p1.Y - p0.Y) * 0.022f);
    }

    /// <summary>
    /// The player as the original's marker: a pale pointer with a salmon boss at
    /// its middle, laid along the cardinal direction they are facing.
    ///
    /// **Its shape is traced off the marker in the game's own map**, not
    /// invented: a long thin blade through the tile, a short crossbar at the
    /// centre, a filled salmon square on that centre with a pale pip in it and
    /// short salmon arms running down the blade, and — a third of the way
    /// forward — a **two-step chevron**, a wide plate the height of the crossbar
    /// with a narrower one the height of the boss stepping out in front of it.
    /// That step is what a pixel triangle looks like at this size, and it is the
    /// part that says which end is the point; everything else about the marker is
    /// symmetric, which is why the earlier plain cross said nothing at all about
    /// facing until it was rotated, and why it read as a cross rather than as a
    /// pointer once it was.
    ///
    /// Every dimension below is a share of <c>h</c>, the marker's length along
    /// the axis it points down, taken from the capture at 63 px long: the blade
    /// is 5/63 thick, the crossbar 26/63 across, the boss 16/63 square, its arms
    /// 27/63 long, the pip 4/63 square, and the chevron's two plates sit at
    /// 0.29-0.36 and 0.29-0.44 forward with half-widths 0.21 and 0.13. The blade
    /// runs -0.45 to 0.53 rather than symmetrically, which is the capture's own
    /// bias: it is the **boss** that sits on the centre of the tile, and the
    /// blade carries a couple of pixels further past the chevron than it does
    /// behind the crossbar.
    ///
    /// **It still turns only to a quarter**, which is the bargain the cross
    /// struck and the pointer keeps — see <see cref="DrawPlayerDot"/>. The
    /// arrow's objection was never that a heading is shown at all; it was that a
    /// heading good to a twelfth of a degree, on top of a sub-tile position, is a
    /// satellite fix in a maze whose difficulty is being lost in it. "North,
    /// roughly" is what someone standing in a corridor with a paper map and a
    /// sense of the building knows, and a quarter turn is the only rotation that
    /// leaves a marker drawn out of axis-aligned rectangles axis-aligned.
    ///
    /// The snap is taken in the **game's own angle units**, not in radians:
    /// `q = round(yaw / (Turn/4)) mod 4`, so the four orientations are exact and
    /// the bars stay square instead of landing a fraction of a degree off it. The
    /// rotation that follows is `-q * pi/2` — see <see cref="DrawPlayer"/> for
    /// why the screen angle *decreases* as yaw increases, which is the whole of
    /// the mirror correction and the one thing here that is easy to get
    /// backwards.
    ///
    /// <see cref="Cardinals"/> is 4 because that is what "cardinal" means; 8 is
    /// one number away, and the only thing it would cost is the axis alignment
    /// above.
    ///
    /// Drawn on the **tile**, like the dot, so it steps a square at a time.
    /// </summary>
    public static void DrawPlayerPointer(ImDrawListPtr dl, Vector2 origin, float cell)
    {
        Build();

        int tx = Map.TileOf(Map.PlayerX);
        int row = Map.RowOf(Map.TileOf(Map.PlayerZ));
        var p = new Vector2(origin.X + (tx + 0.5f) * cell, origin.Y + (row + 0.5f) * cell);

        // The original's marker is about five tiles long on an eighty-tile board,
        // which at a minimap's scale would be a smear -- so it is a share of the
        // cell with a floor under it, the way every other marker here is sized.
        // The floor is high enough that the chevron's two steps are a pixel each
        // rather than nothing.
        float h = MathF.Max(11f, cell * 2.8f);
        float t = MathF.Max(1.5f, h * 0.079f);  // the blade, and the boss's arms

        // The quarter turn the player is nearest, and the exact cosine and sine of
        // the screen rotation it asks for. A table rather than MathF.Cos of a
        // multiple of pi/2, because the answers are 0 and +/-1 and a float sine
        // of pi/2 is not quite either -- which at this size is a marker visibly
        // off square.
        int q = Cardinal(Map.PlayerYaw);
        float ca = _quarterCos[q], sa = _quarterSin[q];

        // Local space is (lateral, forward) with the point at -Y, which is the way
        // the shares above are written; the rotation carries that to the heading.
        Vector2 At(float fx, float fy)
            => new(p.X + fx * ca - fy * sa, p.Y + fx * sa + fy * ca);

        // An axis-aligned rectangle of the marker, in forward and lateral shares
        // of h, grown by g on every side -- which is how the ink pass under the
        // body is drawn, so the marker reads over the pale end of the height ramp
        // as well as over the board.
        void Rect(float f0, float f1, float s0, float s1, float g, uint c)
            => dl.AddQuadFilled(At(s0 * h - g, -(f0 * h - g)), At(s1 * h + g, -(f0 * h - g)),
                                At(s1 * h + g, -(f1 * h + g)), At(s0 * h - g, -(f1 * h + g)), c);

        float ht = t * 0.5f / h;   // the blade's half-thickness, as a share of h

        void Silhouette(float g, uint c)
        {
            Rect(-0.45f, 0.53f, -ht, ht, g, c);                  // the blade
            Rect(-ht, ht, -0.206f, 0.206f, g, c);                // the crossbar
            Rect(0.294f, 0.357f, -0.206f, 0.206f, g, c);         // the chevron, back plate
            Rect(0.294f, 0.437f, -0.127f, 0.127f, g, c);         // and its narrower step
            Rect(-0.127f, 0.127f, -0.127f, 0.127f, g, c);        // the boss
            Rect(-0.215f, 0.215f, -ht, ht, g, c);                // and its arms
        }

        Silhouette(t * 0.35f, _nEdge);
        Silhouette(0f, _nBody);

        Rect(-0.127f, 0.127f, -0.127f, 0.127f, 0f, _nCore);
        Rect(-0.215f, 0.215f, -ht, ht, 0f, _nCore);
        Rect(-0.032f, 0.032f, -0.032f, 0.032f, 0f, _nBody);
    }

    /// <summary>How many directions the marker is allowed to point in. Four, as
    /// the name says; eight would be a finer compass and no other change.</summary>
    public const int Cardinals = 4;

    // cos and sin of -q * (2pi / Cardinals), which is the screen rotation for
    // quarter turn q. Exact, for the reason DrawPlayerPointer gives.
    static readonly float[] _quarterCos = [1f, 0f, -1f, 0f];
    static readonly float[] _quarterSin = [0f, -1f, 0f, 1f];

    /// <summary>Which of the <see cref="Cardinals"/> directions a game yaw is
    /// nearest, 0..3. Nearest rather than truncated, so the marker turns halfway
    /// between two headings and not at one of them; and the modulo is written to
    /// survive a negative yaw, which the game's angle is not promised not to
    /// be.</summary>
    static int Cardinal(int yaw)
    {
        int step = Map.Turn / Cardinals;
        int q = (int)MathF.Floor((yaw + step * 0.5f) / step);
        return ((q % Cardinals) + Cardinals) % Cardinals;
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
                MapMarkers.Kind.Creature => Native ? _nCreature : _creature,
                MapMarkers.Kind.Object   => Native ? _nObject   : _object,
                MapMarkers.Kind.Effect   => Native ? _nEffect   : _effect,
                _                        => Native ? _nSprite   : _sprite,
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
                    dl.AddTriangle(a, b, d, Native ? _nEdge : _markEdge, 1f);

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
                    dl.AddRect(a, b, Native ? _nEdge : _markEdge);
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

        // The native board has its own "you are here", and it makes the dot's
        // claim about position -- the tile you stand in, not where in it -- while
        // still pointing, to the nearest quarter.
        if (dot)
        {
            if (Native) DrawPlayerPointer(dl, origin, cell);
            else        DrawPlayerDot(dl, origin, cell);
            return;
        }

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

        dl.AddQuadFilled(tip, left, back, right, Native ? _nBody : _arrow);
        dl.AddQuad(tip, left, back, right, Native ? _nEdge : _arrowEdge, 1.2f);
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

        dl.AddCircleFilled(p, r, Native ? _nCore : _dot, 0);
        dl.AddCircle(p, r, Native ? _nEdge : _dotEdge, 0, MathF.Max(1f, cell * 0.06f));
    }
}
