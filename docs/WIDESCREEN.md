# Widescreen: the margin, the HUD and the three culls

The runtime already implements widescreen, so the picture costs one number — and
then everything the game does to avoid drawing what a 4:3 screen could not show
has to be dealt with in turn. This file is that chain: the margin, the HUD and
screen-space effects that were authored 320 wide, and the culls the extra picture
runs into.

**Status.** Mechanism measured (a quarter of every frame in an area is already
being thrown away at the screen edge); **picture never checked by eye**, which is
why the aspect defaults to 4:3 while its two sub-options default to on. One cull
is **still unexplained** — see "There is a third cull" at the end.

Picture-quality work (perspective correction, sub-pixel positions, Z-buffer,
dither) is in [RENDERING.md](RENDERING.md).

## Widescreen: the runtime renders the margin, the game fills a quarter of it

**The runtime already implements widescreen; nothing in the game had to be
touched.** `GpuHle.WideMargin` sizes a margin of extra columns either side of the
display buffer, `GlCore` builds the display render target that wide
(`GlDisplayRt.Wide1x`), and the pieces that would otherwise fight it are already
handled: only the original columns are blitted back to VRAM (`Writeback` starts at
`rt.Margin`), the GPU clip is widened to the whole target when the game clips to
the whole framebuffer, `PutDrawEnv` extends an `isbg` background clear across the
margin, and `PresentDisplay` returns `WideAspect` instead of `SourceAspect`
whenever the margin is non-zero. So `patches/Widescreen.cs` sets one number:

```csharp
Display.WideAspect = 16f / 9f;      // 320 -> 428, 54 columns a side
Display.WideAspect = 21f / 9f;      // 120 columns a side
```

**This is not a stretch, and the difference is the whole point.** The projection
is untouched — no GTE control register is written, `H` and the rotation matrix are
the game's own — so pixels keep their aspect and the HUD keeps its authored size.
Which means the extra picture is not synthesised: it is only there if the game
submits geometry past the screen edge and expects the GPU to clip it. A game that
culls per polygon against its own screen rectangle would gain black bars and
nothing else.

So it counts. It listens on `RenderPrimEvent` and classifies every primitive by
whether a vertex falls outside the game's own clip rectangle
(`KF2_WIDESCREEN_PROBE=1`):

```
[widescreen] 25.4% of 480 prims reach the margin (239/s)      title screen (open)
[widescreen] 0.0% of 600 prims reach the margin (300/s)       GAME.EXE menus
[widescreen] 25.4% of 58064 prims reach the margin (28787/s)  fdat02, in an area
[widescreen] 25.1% of 70500 prims reach the margin (35145/s)  fdat02, next window
```

**A quarter of everything King's Field draws in an area was being thrown away at
`x=0` and `x=319`** — about 1,000 primitives a frame, of which ~250 crossed the
edge. It culls per object and by depth and leaves the screen edge to the GPU,
which is exactly the property the margin needs. The 0.0% on the GAME.EXE menus is
the same measurement giving the other answer: those are 2D, authored 320 wide, and
no aspect ratio widens them.

**What has not been done is looking at it.** The measurement above ran with the
desktop session locked, so this is a primitive census, not a visual check. Two
things to check by eye first:

1. **2D screens and fades.** The margin is cleared only by the `isbg` path. A
   full-screen rectangle the game draws itself is 320 wide and will leave the
   sides showing the previous frame. **This one happened** — see "The screen-space
   effects are 320 wide too" below, where it is also fixed.
2. **Pop-in at the edges.** Per-object culling still uses the game's 4:3 frustum,
   so an object can be dropped while its polygons would have been visible in the
   margin. The counter cannot see this — it only counts what was submitted.

If either turns out to be bad enough to matter, the alternative is the classic
hor+ hack: scale the X row of the GTE rotation matrix by `source/target` so the
projection itself narrows, and present stretched. That needs the address of
whatever loads the matrix, costs correct HUD proportions, and — given a quarter of
the frame already crosses the edge — buys much less here than it does on a game
that clips its own polygons.

**Trap, found while measuring, then fixed:** the runtime only presents through
the widened render target if that target was drawn into within the last **4**
presented frames (`GlCore.PresentDisplay`) — but the counter counts *host*
presents, and with `VSync=False` the host presents far faster than the game
draws, so most presents found both targets stale, fell back to the plain VRAM
texture, and returned it at 4:3: **the margins flashed black, rapidly, through
whole sessions.** Any present rate more than about five times the game's does
it — a 144 Hz monitor with VSync on as well as VSync off. The gate is gone
(`patches/recompone/0022`): every present writes the targets' middle columns
back to VRAM first and direct VRAM writes are synced into the targets, so a
target that contains the display area is never staler than the fallback it
replaces, and idle targets are destroyed after 300 frames anyway.
`KF2_PRESENT_PROBE=1` counts what each present picked — wide, plain, VRAM
fallback — per two-second window, and is how the fallback rate is measured.
Still true and worth keeping: with the session locked, KWin stops sending frame
callbacks and the port blocks in `SwapBuffers` forever with `VSync=True` — 0%
CPU, no log output, and it looks exactly like a hang; set `VSync=False` in
`interface.ini` to run it without a visible window.

### The present gate

A wide target whose margin columns have never carried a world would present
invented picture at the sides, so `PresentDisplay` refuses any wide target that
has not latched margin content (`patches/recompone/0023` supplies the display-
flip counter, `0024` the latch). Latching is per target and lasts for the
overlay session: a single display flip that delivers 32 game vertices past the
game's own draw edge latches the target, a fill covering the widened target
latches it outright, primitives the widescreen patch itself widened never count,
and `Dispatcher.Load` clears every latch when a new executable loads.

Both halves of the rule earn their place, measured. The density threshold is
what keeps the boot splash out: OPEN.EXE clears each MDEC frame with an
oversized opaque rect whose corners sit outside the draw area -- genuine game
output, but two vertices of it per flip -- and a first cut that latched on any
crossing granted the margin to that scene, whose present then flapped between
widths again. A frame of gameplay crosses the edge hundreds of times; the title
never latches at all, through minutes of idle. The per-target monotone half is
what keeps menus wide, and it is why 0023's gate had to go: a scene that stops
the world render -- in-game menu, dialog, shop, sign -- produces no margin
content at all, so the old global stamp went quiet, both targets were demoted
after two idle flips, and the picture collapsed to the 320-wide fallback for as
long as the text was up.

`KF2_PRESENT_PROBE=1` shows splash and title windows as `vram fallback` only,
gameplay as `wide`, and menu windows stay `wide`.

## The HUD does not widen with the world, and finding it is the problem

The world gets wider; the HUD is drawn in screen space, so it keeps the 4:3 box it
was authored in and sits visibly inset from the new edges. Moving it means telling
a HUD primitive from a world one, and `RenderPrimEvent` carries no such flag. Two
whole-frame dumps in `fdat02` (599 and 522 primitives, before and after turning on
the spot) locate it:

* It is **two fixed clusters** — x 5..91 for the HP/MP panel, x 269..310 for the
  equipment icons, both y 11..60 — and 42 of its 66 primitives are pixel-identical
  across the two frames; the rest are digits and an icon that animate in place.
* It is always in the **last ordering-table entries**, from about 65 from the end
  of a ~9,400-entry table. That is the front of the OT, where a painter's
  algorithm has to put whatever goes on top. A frame is a single `DrawOTag` call,
  the game alternates two tables (`0x801860A4`, `0x8018E0A4`) one per frame, and
  the HUD has no table of its own.

**The rule that looked right and was not: the palette.** In the first dump every
HUD primitive used a CLUT in VRAM **column 0** (rows 493-495) and no world
primitive did — world palettes sat at (8,492), (16,506), (20,496), (36,258..260).
Anchoring on "the first column-0 palette opens the HUD tail" worked in the
starting corridor and wrecked the frame elsewhere: further into the area, world
geometry uses column-0 palettes all the way back through the table, so the tail
opened early and half the world moved sideways. **The artefact named the cause —
the missing wedges of floor and ceiling were 54 pixels across, which is the
margin.** Holes exactly one margin wide mean geometry is being shifted that should
not be.

What replaced it is structural and positional: a primitive is HUD if it is in the
last 128 OT entries *and* its box falls in one of the two clusters. The OT gate is
why this replaces `DrawOTag` and walks the table itself — one pass to count the
entries, one to emit them, mirroring `LibGpu.DrawOTag` including its
custom-primitive branch. `HookManager.Invoke` runs pre-hooks, then the replacement,
then post-hooks, so the frame pacing's `DrawOTag` post-hook still runs. Being
wrong now costs one small triangle in a corner instead of the whole frame after it,
and the anchoring has its own toggle.
## The screen-space effects are 320 wide too, and one drawer makes all of them

The first of the two things the census could not see, reported from a real
session: **the black fade on death and the red flash when you are hit covered only
the middle of a wide picture**, with the world carrying on brightly in the two
54-pixel margins. Reproduced and photographed — the fade blacks out a 4:3 box and
leaves a lit strip either side of it, which is a worse picture than no widescreen
at all.

**Every whole-screen effect in this game comes out of one request block and one
drawer**, so the whole class is a single finding rather than a list:

| address | what it is |
|---|---|
| `0x80192D45` | four bytes: blend mode, then R, G, B. Mode `0xFF` means "no tint" |
| `func_8003220C(mode, r, g, b)` | the only writer of that block — every tint goes through it |
| `func_8003214C` | reads it once a frame and, unless the mode is `0xFF`, submits the quad |
| `func_80031EE8(x0, y0, x1, y1, …)` | the 2D quad builder: a `POLY_FT4`, code `0x2C` or `0x2E`, `AddPrim`ed at the depth its last stack argument names |

`func_8003214C` calls it as `func_80031EE8(0, 0, 0x140, 0xF0)` — **(0,0) to
(320,240), the framebuffer exactly** — with UV `(0x80,0xD0)` over a 15×15 patch of
flat colour, CLUT `0x7BDC`, and the tpage word `((mode & 3) << 5) | 0x17`. That
last expression is the effect: the low two bits of the mode are the GPU's
semi-transparency mode, so `0x82` is `B−F`, subtractive, the fade to black, and
`0x81` is `B+F`, additive, a flash. Bit 7 picks the OT depth, 1 rather than `0x0C`,
which is why it lands at the very front of the table on top of the HUD.

Three sibling functions — `func_8003202C`, `func_800320BC`, `func_80032234` —
build the same `(0, y, 0x140, 0xF0)` quad for the other whole-screen washes. All
four pass the semi-transparent flag. The blocking fade loop `func_80037B5C(mode,
start, …)` ramps `min((n·n) >> 16, 255)` into `func_8003220C` and vsyncs, and
`fdat23` has its own copy of that loop with mode `0x81`; the death handler's
`32 ≤ n < 65` arm feeds it `(n − 32) << 7` directly.

**So the fix is a shape, not an address.** `patches/Widescreen.cs` widens, in its
`RenderPrimEvent` listener, any primitive that is **semi-transparent, flat
(non-Gouraud), and spans the clip rectangle's full width**; the vertices sitting on
either edge are snapped out to `left − margin` and `right + margin`. Keying on the
shape rather than on `func_80031EE8` covers OPEN.EXE's and END.EXE's own links of
the same drawer without hunting two more addresses, and it is checked per
primitive anyway.

Why those three tests and no more:

* **Semi-transparent** is what an *effect* is. A 2D picture authored 320 wide — a
  title screen, the menus, the FROM SOFTWARE logo — is opaque, and is deliberately
  left at its authored width: stretching a picture distorts it, pillar-boxing one
  does not.
* **Flat** separates the tint from the world. Every wide primitive measured in an
  area is Gouraud-shaded, semi-transparent ones included; the tints are `POLY_FT4`
  and never are.
* **Full width** is the definition of the thing being fixed, and the texture is a
  15×15 patch already stretched over 320 pixels, so another 108 of them cost
  nothing and show nothing.

The OT position was tempting as a fourth test — the tint is measured at entry 1
from the front — and is not used, so an effect drawn outside a `DrawOTag` walk is
still caught.

**The counter is the evidence.** `KF2_WIDESCREEN_PROBE=1` now reports the tints it
stretched alongside the margin census, and the three numbers are exactly right:
19 and 14 in the two windows straddling an area load (the fade-in), **zero in
every window of ordinary play** — the negative control that says no world geometry
is being caught — and then 60 per two-second window during a death, which is one a
frame at 30 fps. By eye, at `KF2_WIDESCREEN_EFFECTS=0` the fade stops at the old
edges and at the default it does not.

**Dying on demand, without a player.** The attract demo walks itself into `fdat05`
about a minute after boot, which is a whole live session with no input; from there
`AutoReload.Simulate()` (already in the tree, for the same reason) latches the
death, and holding the death clock at `0x8019951A` part-way through the `32..64`
fade arm freezes the screen mid-fade for as long as the shot takes. That rig was
temporary and is not in the tree — it was a `KF2_KILL` env var, a thread that set a
flag, and a call from inside the `DrawOTag` replacement so that the latch ran on
the game's own thread.

`KF2_WIDESCREEN_PROBE=2` is the listing the identification came from: every
primitive covering most of the clip rectangle, once per distinct shape, with its
flags, CLUT and OT position. It is a page of output per scene, which is why the
plain `=1` no longer includes it.

**The switch** is `kf2.widescreen.stretcheffects` / `KF2_WIDESCREEN_EFFECTS=0`, a
checkbox under Video below the HUD anchoring, **on by default**. This one is not
the sub-pixel argument: the picture *has* been looked at, before and after, and
the default-off picture is a defect rather than a taste.

## Widescreen became a patch, and the default stayed 4:3

`mods/widescreen` is now `patches/Widescreen.cs` plus
`patches/settings/WidescreenPage.cs`, for the reason the dither switch is a patch:
an aspect ratio is a picture the port should be able to offer without a package
having to load, and Video is where a player looks for it rather than a gear button
in the Mods popup. The conversion is the usual one — `[Replace]` attributes became
`SymbolRegistry.Resolve` plus `HookManager.AddReplace` from an `Attach` deferred to
the first `OverlayLoadedEvent`, and `OnLoad`'s config read became a
`RuntimeReadyEvent` listener. The `interface.ini` keys are unchanged
(`kf2.widescreen.aspect`, `kf2.widescreen.anchorhud`), so a player who had the mod
on keeps the picture they had.

**Where it differs from the four conversions before it: the switch stays off.**
Those flipped their default *on*, each with the same argument — a mod that can be
absent, whose absence is a defect the port should have dealt with. That argument
does not hold here. The census says a quarter of every frame in an area is there
to recover, but the two things that can go wrong at the sides are exactly the ones
a primitive counter cannot see (a 2D screen the game draws 320 wide; per-object
culling against the game's own 4:3 frustum), and **the picture has still never been
checked by eye**. That is the sub-pixel test, not the dither test, and it gives the
same answer: mechanism measured, picture not, so the default is 4:3 —
`WideAspect = 0`, which is the untouched path — and the presets are one click away
under Video. The census left the settings page with it and stayed on the console
under `KF2_WIDESCREEN_PROBE=1`, as the dither counters did.

**The page is a combo and a checkbox, drawn directly under the render scale** —
inside the runtime's own display section, not in a group of the port's below it.
An aspect ratio is an ordinary picture option and belongs among the ordinary
picture options; getting there is `patches/recompone/0013` and
`PatchSettings.RegisterSlot` (see "Patch settings" in
[PATCHES_AND_MODS.md](PATCHES_AND_MODS.md)). There is no
slider — the four presets are what a display actually is, an arbitrary ratio is a
number to type rather than to drag for, and `KF2_WIDESCREEN` still takes any of
them; a value that is none of the four shows in the combo as `Custom (1.9:1)`
instead of being rounded onto a preset.

**The conversion found a bug the mod had: the replacement dropped the source
address.** `LibGpu.DrawOTag` calls `gpu.WriteGp0(word, src)` — the address the word
was read from, which is what `GteVertexMap` keys the recovered depth and sub-pixel
fraction on (`patches/recompone/0012`). The mod's copy of that walk predates 0012
and called the one-argument `WriteGp0(word)`, i.e. `src = 0`, so **every frame with
the HUD anchored would have quietly gone back to affine texturing** — perspective
correction silently off, with nothing in any log to say so. Anything that mirrors a
runtime SDK function has this failure mode: the copy stops tracking the original
and the divergence is invisible. Fixed in the patch, and it is the first thing to
check if a future hook re-implements a libgpu walk.

Verified with the disc, 16:9 with the HUD anchored: `[KF2] widescreen: 3 hook(s)`
alongside the dither patch's 12 and perspective's 3 on the same function, no
`replace conflict`, and `KF2_PERSPECTIVE_PROBE=1` reporting **88-93% hit** over
30 fps in `fdat05` — which is the source-address fix stated as a measurement,
since without it that number is zero. The census through the same run: 25.4% of 465
prims on the title screen, 0.0% and 4.4% on the GAME.EXE menus, 19-55% per window
in an area, which is the same shape as the mod measured. At 4:3 the replacement is
a straight call to the original and no `RenderPrimEvent` listener is attached at
all, so the default costs nothing per primitive rather than merely little.

## The cull the margin runs into: a 24×24 tile grid, and a trapezoid drawn on it

The margin only ever shows what the game submitted, and the second of the two
things the primitive census could not see — "per-object culling still uses the
game's 4:3 frustum, so an object can be dropped while its polygons would have been
visible in the margin" — turned out to be the whole story, reported from a real
session: **things pop in and out at the sides of a wide picture.**

The cull is not per polygon and it is not a frustum test per object. King's Field
keeps **a 24×24 byte grid of tile visibility at `0x80192EAC`**, one byte per
2048-unit map tile, and rebuilds it every frame at the top of the renderer. Every
part of the frame is gated on it:

| address | what it does |
|---|---|
| `func_800342D8` | the renderer; its first call is the grid build |
| `func_8002D3A8` | builds the grid: clear, four edges, fill, then an occlusion flood |
| `func_8002CCE4` | clears the 576 bytes |
| `func_8002CD0C` | one Bresenham edge into the grid, dropping cells outside it |
| `func_8002CF0C` | the scanline fill between those edges |
| `func_80032D78` | *is this tile lit* — the point query, `grid[z][x] & mask` |
| `func_80032DE8` | the same over a box, for an object with extent |
| `func_800331B4` | the two renderer walks, both gated on those two queries |

`0x80192E98` / `0x80192E9C` are the per-axis offsets that turn `world >> 11` into
a grid index; they are written as words and read back as `u16`, so the arithmetic
is 16 bits wide and a negative index wraps out of the `< 0x18` bounds check rather
than failing it.

**The shape is the 4:3 frustum flattened onto the map.** `func_8002D3A8` reads
seven `s16` pairs from GAME.EXE's data at **`0x80068760`**, lerps each pair by
`0x1000 - rcos(pitch)` so the shape opens out as you look up or down, rotates the
result by the yaw, and hands four corners to `func_8002CD0C`. In units of 1/256 of
a tile, level:

| index | level | pitched | what it is |
|---|---|---|---|
| 0 | 1280 | 0 | how far ahead of the player the 24×24 window is centred (5 tiles) |
| 1, 2 | -2272, 2336 | -1248, 1312 | the far edge, left and right (±9 tiles) |
| 3 | 2688 | 1152 | the far edge's depth (10.5 tiles) |
| 4, 5 | -224, 288 | -1248, 1312 | the near edge, left and right (±1 tile) |
| 6 | -128 | -1152 | the near edge's depth (half a tile behind you) |

Nine tiles wide at ten and a half out is a half-angle of 36°, which is `160/H` for
`H ≈ 220` — the screen's own half-width, plus a tile of slack at the near end. So
widening the cull is four numbers: scale indices 1, 2, 4 and 5 by the ratio the
margin widens the picture by, and the trapezoid opens to the new screen edges. The
depths and the forward push are left alone; the cone gets wider, not longer, and
the push is where the occlusion flood starts.

**The 24×24 window fits that cone exactly and not one tile more.** Its centre sits
5 tiles ahead of the player, so the far corners are `sqrt(5.5² + 9²) = 10.55` tiles
from it against a reach of 11. That is not slack, it is a fit, and it is a designed
one — measured rather than argued: `KF2_WIDESCREEN_CULL_PROBE=1` at the stock table
reports **`0/60 frames reached the grid edge`**, every window, through a whole
attract-demo session. Any widening at all puts a corner off the grid at some yaw.

**Which breaks the fill, not merely clips it.** `func_8002CF0C` takes each of the
24 rows, scans in from the left for the first run of the marker and in from the
right for the other, and fills between them; `func_8002CD0C` drops the cells that
fall outside the grid. A row whose left edge left the grid therefore has *one*
boundary, the two scans meet, and **the row is not filled at all** — a whole rank
of tiles vanishes rather than being clipped. Widening the table on its own would
trade pop-in at the edges for chunks of the world blinking out.

So `patches/CullCone.cs` is the two halves together: the table scaled by
`(320 + 2·margin) / 320`, and a **post-hook on the fill** that re-walks the four
edges a pre-hook on `func_8002CD0C` recorded — with the game's own Bresenham, the
same major-axis choice and the same halved error term — takes each row's true span,
clamps it to the grid and writes the marker over it. It runs after the fill and
before the occlusion flood, exactly where the game's own fill sits, so recovered
tiles are shadowed by walls like every other tile. It returns immediately unless a
corner actually fell outside, so 4:3 is bit-identical: the cone fits, nothing is
recorded as clipped, and the hook has read four integers.

The table is read back and matched against the shipped values before anything is
written, and a mismatch refuses the patch rather than corrupting the renderer's
idea of what is visible. It is only written while GAME.EXE is the resident overlay
— OPEN.EXE and END.EXE link at the same base — and rewritten on every load of it.

**What it cannot buy, and what that is worth.** The window is 24 tiles and stays
24 tiles, which caps the cone at about 9.5 tiles from the centre in the worst yaw
against the 9 it ships with. 16:9 wants 12 and 21:9 wants 15, so the near and
middle distance — where edge pop-in is actually visible — widens fully and the far
corners of a very wide aspect stay clipped. The probe says how much that costs in
the window it just measured:

```
[cullcone] x1:     130.4 of 576 tiles lit,  0/60 frames reached the grid edge
[cullcone] x1.338: 171.5 of 576 tiles lit, 58/60 frames reached the grid edge,
                   97 tiles recovered over 6 rows the game's own fill dropped
[cullcone] x1.781: 211.7 of 576 tiles lit, 60/60 frames reached the grid edge,
                   3437 tiles recovered over 181 rows the game's own fill dropped
```

"Tiles lit" is the measurement that matters, and it is taken where the game itself
reads it — the count of non-zero cells straight after the fill, before the occlusion
pass. It answers "did the cull open" without depending on what the camera happens
to be pointed at, which the widescreen census's primitive count does. (The primitive
count moves the same way — about a quarter more submitted per window with the cone
widened — but the attract demo does not run in lockstep between sessions, so it is
the weaker of the two numbers.)

## The second cull: a view-space clipper, and it is set to twice the screen

The tile grid is not the only thing that removes geometry. `func_80030540` checks
every polygon's screen-space vertex deltas against the GPU's own limits — `|dy| ≤
511`, `|dx| ≤ 1023`, the `+0x1FF < 0x3FF` / `+0x3FF < 0x7FF` chains — and anything
too big goes to **`func_8005CAC8`**, which clips it in view space and re-projects
the pieces (`func_8005CCD8` builds 0x2C-byte vertex records, `func_8005CE98` is a
six-plane Sutherland–Hodgman, `func_8005D8E8` re-projects). A result of fewer than
three vertices is dropped.

**Which polygons are too big for the GPU? The near-camera floor and ceiling.** So
this is the cull that governs exactly the bottom corners of the picture.

The vertex record is `+0x08` X, `+0x0C` Y, `+0x10` Z, and the clip bounds are
carried per vertex: `+0x24 = (Z · *0x800FC97C) >> 12` and
`+0x28 = (Z · *0x800FC98C) >> 12`, i.e. `±X` and `±Y` limits at that depth. The
six planes are far (`Z < *0x8012E99C`, `0x10000`), near (`Z < *0x8017E07C`, `0`),
then `Y` against `±+0x28` and `X` against `±+0x24`. `0x800FC984` / `0x800FC994`
hold the reciprocals, used when interpolating a clipped vertex.

Those four words come from one call, `func_8005D7CC(ws, 0x140, 0xF0, 0x64)` —
**320 × 240 with a projection distance of 100**:

```
0x800FC97C = (320/2) << 12 / 100 = 6553   tan of the horizontal half-angle, 12.12
0x800FC98C = (240/2) << 12 / 100 = 4915   the vertical one
0x800FC984 = 100 << 12 / 160     = 2560   and their reciprocals
0x800FC994 = 100 << 12 / 120     = 3413
```

**But the GTE's projection distance is 200, not 100** — `func_8005B2D4` is
`SetGeomScreen` and is called with `0xC8` just before, `func_8005B2BC` is
`SetGeomOffset(160, 120)`. So the real frustum is `tan = 160/200 = 0.8` and the
clipper is set to `tan = 1.6`: **the clip volume is exactly twice the screen
frustum**, deliberately, as a guard band that keeps a subdivided polygon under the
GPU's 1023-pixel limit without ever cutting anything a 4:3 player could see.

Two things follow.

* **It confirms the tile cone's geometry independently.** `H = 200` gives a
  half-angle of 38.7°, against the cone's 36° plus its one-tile near offset — so
  the cone is a superset of the frustum right out to its 10.5-tile far edge, which
  is what a conservative tile cull has to be.
* **It is not aspect-aware, and it starts cutting the picture at 8:3.** The clip
  lands at `200 × 1.6 = 320` pixels either side of centre. 4:3 reaches 160, 16:9
  reaches 214, 21:9 (64/27) reaches 284 — all inside. Only past **2.67:1** does
  the picture reach the guard band, and `Widescreen.Widest` is 3.0, so the top of
  the allowed range does. Widening it is one number and its reciprocal, which is
  `patches/ViewClip.cs`; the guard band has to shrink rather than scale, since
  `GpuRaster` drops a primitive spanning more than 1023 pixels.

## Is the 24-tile window worth lifting? Measured: binding, and barely

**Confirmed measurement, decided against.**

The window is not a constant anyone can patch. It is *stride and bounds*, baked
into immediates across nine routines — `func_8002CCE4` (clear), `func_8002CD0C`
(edge), `func_8002CEA8` and `func_8002CF0C` (fill), `func_8002CFC8` and
`func_8002D15C` (the occlusion steps), `func_80032D78` and `func_80032DE8` (the
queries), and `func_8002D3A8` itself, whose ring loop passes the eight neighbour
offsets `±1, ±0x17, ±0x18, ±0x19` and the row step `0x18` as about forty
immediates. Lifting it means reimplementing all nine in C# against an N×N array
of our own, including the flood's map lookups (`0x801C8484 + 800·z + 10·x`, an
80×80 grid of 10-byte tile records, bit 0x80 of `+4` being "see through"). Note
the grid is addressed through a stored cursor at `0x801B69D0` rather than by its
base, so relocating it is not the hard part — the stride is.

Nine functions, one of them large, and **no oracle but a person looking at the
screen**: a wrong bit in the flood is a wall you can see through or a room that
vanishes, and no counter here can tell the difference between that and correct
occlusion.

So the question is whether the window is binding at all, and that *is* a counter's
question. `KF2_WIDESCREEN_CULL_PROBE=2` censuses the grid **after** the flood — the
grid exactly as `func_80032D78` will read it — by Chebyshev ring from the middle.
At 16:9, through `fdat05`:

```
lit per ring: 0:1.0  1:8.0  2:16.0  3:23.7  4:26.7  5:25.9
              6:22.4 7:17.9 8:12.0  9:9.0   10:6.1  11:6.1
```

Rings 0-3 are **saturated** — every cell within three tiles is lit, always — and it
falls away from there. The outermost ring the window has is occupied, ~6 cells of
its 88, so the window *is* binding: the cone genuinely reaches the edge and is cut
there. But ring 11 is 3.5% of the ~175 lit tiles, at eleven tiles out where the
game's own fog is, and a bigger window would extend that tail rather than open it
up — ring 12 and beyond would be smaller still.

**Conclusion: not necessary, and not worth it on these numbers.** Nine
reimplemented routines and an eyes-only correctness check, to recover a few percent
of tiles at the extreme far corners. Revisit only if the picture at 21:9 shows
something obviously missing out there — which is the one thing the census cannot
answer.

Worth separating from that: **the fill fix is load-bearing regardless of the window
size.** At x1.781 the probe reports 181 rows dropped over 60 frames, three of the
24 rows per frame, and those rows are wherever the trapezoid happens to leave the
grid — near the camera as readily as far from it. That is the failure that loses
chunks of the world, and it is not a far-corner problem.

**The switch** is `kf2.widescreen.widencull` / `KF2_WIDESCREEN_CULL`, a checkbox on
the widescreen page under Video, **on by default**: a wide picture whose cull is
still 4:3 shows the margin filling in and emptying as you turn, which is a worse
picture than no widescreen at all. It costs nothing at 4:3, where the factor is 1
and the table is written back as its own values. `KF2_WIDESCREEN_CULL=1.5` pins a
factor for measuring against the aspect's own.

## There is a third cull and it is none of the obvious ones

**Open.** Three candidates eliminated with numbers, cause still unknown.

Reported from a real session with the cone widening in: **the corners still cull
visibly**, floor and ceiling, occasionally. Three candidates have since been
eliminated, each with a number rather than an argument:

| candidate | verdict | evidence |
|---|---|---|
| the tile cone above | **ruled out** | `KF2_WIDESCREEN_CULL=2.5` pins it far wider than any aspect asks for and the corners still drop |
| the view-space clipper | **ruled out** | it cuts at `200 × 1.6 = 320` px either side of centre; 21:9 reaches 284. `SetGeomScreen` is called with `0xC8` at all three sites, so `H = 200` throughout and the guard band really is 2× |
| the primitive buffer | **ruled out** | peak 1108 of 1969 packets through the attract demo at a 2.5× cone, and **~25% spinning the camera at full speed in a real area** — four times the headroom, zero overflows |

That last one deserved the check it got: `func_8002DF80` hands out `0x19000` bytes
a frame from `0x800FC99C` and `0x8011599C`, `func_80030540` bumps a descriptor at
`0x8017E0A4` per polygon, and when the bump passes `end` it **returns, abandoning
the rest of the call** — a frame that runs out silently loses whatever it had not
drawn yet, which is the right shape for "occasionally". It simply never runs out.
`patches/PrimBuffer.cs` / `KF2_PRIMBUF_PROBE=1` is that measurement, kept because
it is the cheapest way to re-ask the question after anything that submits more
geometry.

**The edge was reported as straight**, which says a clip plane or a clip rectangle
rather than a per-object test. Two of those were then checked.

**The backend scissor is not it.** `GlCore.cs:588` widens the GPU scissor to the
whole render target only when the game's clip spans the framebuffer, so a batch
that misses the condition is confined to the game's own 320 columns — a straight
vertical edge exactly. A temporary probe in the checkout counted them: over a
200-second session at 21:9, **one** narrow batch and **three** targetless ones, all
during boot, none in play. Two shapes are worth recording anyway, since they are
real and will matter to something:

```
[scissor] no render target: clip (0,0)..(639,239) — no margin at all
[scissor] NOT widened: rt=(0,0) 640w margin=240, game clip x 0..319
```

The second is `Classify()` matching the clip against a **640-wide** display rect
(`clipInside` is satisfied by any rect that contains the clip), so the target is
built 640 wide with a 240 margin and the widen test then wants `clipX1 >= 639`
against the game's 319. Boot-only here, and left alone.

**And the game's own clipper is not it below 8:3** — see the section above: it cuts
at 320 px from centre and 21:9 reaches 284. `patches/ViewClip.cs` opens it anyway,
because it is one word and the cut *is* inside the picture at 3:1, but at every one
of the four presets it writes the shipped value back and changes nothing:

```
21:9  tan 1.6  (cut at ±319 px), picture reaches ±280 — the game's own, unchanged
3:1   tan 1.92 (cut at ±384 px), picture reaches ±360
```

So the corner culling at 21:9 is **still unexplained**, and the honest position is
that every clip plane and clip rectangle found so far is outside the picture at that
aspect. `KF2_VIEWCLIP=1.5` forces the clip volume open at any aspect and is the test
that would implicate it regardless of the arithmetic; if that does not change the
picture, the straight edge belongs to something not yet found.

Also still open, and cheap if the cone ever comes back into suspicion: the bottom
corners are the most extreme lateral-to-forward ratio anywhere in the frame, so a
flat lateral scale of the trapezoid is arguably the wrong shape for them — the near
end wants proportionally more than the far end. Pulling the near edge back widens
every positive depth (±2.09 → ±3.37 tiles at z=1 for `zn` from −0.5 to −3), but it
is dominated by the 2.5× test that already failed, and cells strictly behind the
camera can never hold visible geometry: the cells partition the world, so the cell
one step back ends at the camera's own position.

**Trap found while measuring:** `KF2_WIDESCREEN=16:9` was overridden mid-session by
the saved `kf2.widescreen.aspect`, part-way through the run — the console showed the
cone factor stepping from x1.338 to x1.781 with no other cause. Env-forced aspects
are not as forced as the `_forced ?? saved` in `Widescreen.Install` reads; anything
A/B-ing two aspects should pin the *saved* setting instead, and this is worth
tracking down.

