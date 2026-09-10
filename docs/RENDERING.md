# Rendering: recovering what the GP0 packet threw away

Everything in this file comes from one fact: **the GPU is handed no depth at
all.** The GTE does the perspective divide, the game writes the resulting 2D
screen coordinate into a GP0 packet, and what arrives at the GPU is screen
positions, UVs and colours with nothing left to say how far away any of it is —
so textures can only be interpolated affinely, vertices can only sit on whole
pixels, and occlusion can only be the order of the ordering table. Each of the
three is recovered here from the same discarded number, one step upstream in
`Gte.Rtp`.

Aspect ratio, the HUD and the culls are in [WIDESCREEN.md](WIDESCREEN.md).

| feature | mechanism | picture | default |
|---|---|---|---|
| Perspective correction | **measured**, 92% hit | **checked**, 76.6% of pixels | **on** |
| Sub-pixel vertex positions | **measured**, offsets uniform | **not checked** | off |
| Z-buffer | **measured**, same recovered SZ | **checked and still wrong** — a second cause remains | off |
| Dithering (removal) | **measured**, all three routes | **checked**, twice-drawn pair | off (no crosshatch) |
| True color (24-bit) | **measured**, RGBA8 target + shader | the point of the switch | off (authentic 15-bit) |
| Anisotropic filtering | **measured**, sparkle sd 51.2 -> 11.4 | **not checked** | off |

That "mechanism measured / picture never checked" split is the rule the whole
port is written to: a feature whose mechanism has counters behind it but whose
picture nobody has looked at ships switched off, and the reason is recorded with
it.

## Perspective correction: the depth is one step upstream, and the screen position is the key

**Confirmed and on by default:** 92% hit rate measured, 76.6% of a frame's pixels
changed, HUD provably untouched.

The swimming, rippling texture on every floor and wall — the most recognisable
thing about a PlayStation picture — is not a bug in anything here. **The GPU has
no depth at all.** The GTE does the perspective divide, the game writes the
resulting 2D screen coordinate into a GP0 packet, and what arrives at the GPU is
screen positions, UVs and colours with nothing left to say how far away any of it
is. Linear interpolation of U and V across the screen is the only thing it *can*
do, and that is exact only for a surface square-on to the camera. Hence the
sliding texture on a floor you walk over, and the crease down the diagonal of a
quad where its two triangles disagree about where the middle of the texture went.

So it cannot be fixed at the GPU. It has to be fixed by not losing the depth in
the first place, and the useful observation is that **one function knows both
halves at the same moment**: `Gte.Rtp` computes the screen coordinate *and* the
view depth `SZ3` that produced it, in the same call. Better, the screen coordinate
it produces is bit-for-bit what turns up in the packet — `SatX`/`SatY` clamp it to
11 bits signed, which is exactly the 11 bits `GpuRaster.CoordX` decodes back out.

That makes the screen position a key the two ends can share. `GteDepth` is a hash
table of `(x, y) -> z` that the GTE writes as it projects and the GPU reads as it
decodes a vertex word, and it reunites the two halves **without following a single
register or store** — no tracking of the game's own copies, no knowledge of its
packet layout, nothing to identify. This is the cheap half of what PGXP does in
the emulator world; PGXP proper follows the values through memory, which buys the
cases below and needs far more machinery.

`Gpu._drawOffsetX/Y` is added *after* the lookup and the `RenderPrimEvent` fires
after that, so a hook is still free to move X — widescreen does — and the depth
stays attached to the vertex.

### Why leaving it on is safe: a miss is the old behaviour

Every route out of the table falls back to what the port did before, which is what
makes this a default rather than an experiment:

- **2D corrects itself.** A HUD sprite, a menu box, a font glyph — the CPU
  computed those coordinates and they were never in the table, so they miss and
  keep the affine mapping 2D actually wants. Measured: the title screen, which is
  entirely 2D, asks the table ~930 times a second and hits **0.0%**.
- **A primitive is all-or-nothing.** Correction is applied only when *every*
  vertex of the triangle hit. One vertex left at W = 1 among two real depths would
  shear the triangle's texture in half, which is worse than the problem.
- **Saturated coordinates used to be dropped, and that was the remaining pop.**
  A vertex projecting off screen clamps to ±1024, so several different vertices
  at different depths can share one key. The first version of the table refused
  to record those at all, which made every large nearby wall and floor — the
  polygons that want correction most — fall back to affine the moment one vertex
  left the window. The clamp is still the key, because that is what the packet
  carries; uniqueness is recovered by keeping the last few samples at each key
  and picking the set whose depths belong on this primitive. A leftover that is
  still an obvious high outlier is dropped, so that triangle stays affine rather
  than tearing. See "The table is not unique" below.
- **Nothing is corrected bit-differently.** A vertex with no depth carries W
  exactly 1 and the vertex shader writes the *original* expression for that case
  (`vec4(p, 0, 1)`, not `vec4(p*1, 0, 1)`), so untouched geometry lands on the
  same pixels to the last bit.

The table itself has no frame boundary in it and never gets cleared: entries carry
a monotonic sequence number and go stale once a table's worth of vertices — 16384,
about a frame or two of geometry — has been written past them. That deliberately
keeps double buffering, `DrawOTag` and the frame loop out of the file entirely.

### Both renderers, two different mechanisms

The software rasterizer interpolates U/W, V/W and 1/W with the barycentrics it
already has and divides per pixel. The hardware backend does not interpolate
anything by hand: it puts the recovered depth in `gl_Position.w` and lets the
rasterizer's own perspective divide do it, which is exact and free. Colour is
marked `noperspective` in the core-profile shaders so Gouraud shading stays as
flat-interpolated as the console's — the only thing corrected is the texture
coordinate. GLSL 120 has no `noperspective`, so on the **GL 2.1 backend alone**
colour is corrected along with the texture; that shows as a slightly different
Gouraud gradient on a steeply angled textured polygon, and that backend was not
available to test on (Mesa gives 4.6 here).

### What it measures, and what it looks like

`KF2_PERSPECTIVE_PROBE=1` reports the table's hit rate per two-second window. The
hit rate is the whole measurement: it is the only thing that says the coordinate
the GTE saturates into SX2/SY2 really is the coordinate that reaches the packet.
A rate near zero would mean the two ends never agreed on a key and every polygon
had quietly stayed affine.

Steady state in `fdat05`, walking:

```
[KF2] perspective: 46576 vertices projected/s, 91878 looked up/s, 92.3% hit, over 30 frames/s
```

— 85–94% in the world, **0.0%** on the title screen, and 30 fps throughout, so it
costs nothing measurable.

The picture: one ordering table drawn twice, once with the table live and once
with it switched off, so the pair is identical geometry, lighting and textures one
bit apart — the same trick "Getting pixels out without a screenshot" in
[DEVELOPMENT.md](DEVELOPMENT.md) describes for the dither.

| | affine (off) | corrected (on) |
| --- | --- | --- |
| pixels differing from the other | 76.6% | — |
| mean absolute difference | 22.7/255 | — |
| the HUD panel, glyphs and bars | identical | identical |

76.6% of the frame changing is what "the largest single change to the picture"
means numerically. In `fdat05` the stone wall in the middle distance goes from a
curved smear to straight courses of brick, and the vaulted ceiling stops sliding.

**Run the control before believing a pair.** Drawing the ordering table twice can
itself change the picture — a semi-transparent primitive blends twice on the
second pass — so the same shot was taken with *both* passes left on: **0.0% of
pixels differ, 0 in the HUD box**. That is what licenses reading the 76.6% as the
setting and not the method. It also turned a false alarm around: 3298 pixels
differ inside a 120x40 box over the HUD, which looked like the HUD being corrected
until the crop showed the panel, the glyphs and both bars pixel-identical and the
*wall showing through the semi-transparent panel* carrying all of the difference.

**Two things that do not work for the pair, both already learned from the dither
work** (and both written up with the method in [DEVELOPMENT.md](DEVELOPMENT.md)).
Consecutive frames of one run are different views, and the same frame
number in two runs is not the same view — disc timing drifts, and frame 120 was a
320-wide menu in one run and a 640-wide screen in the next. One ordering table,
twice, in one run is the only honest comparison.

## Sub-pixel vertex positioning: the same number's other half

**Confirmed mechanism, unchecked picture — off by default.**

The depth is not the only thing `Gte.Rtp` computes and the packet does not carry.
The projection is done in **16.16 fixed point** — `sx` and `sy` in that function
are exact to a 65536th of a pixel — and then `SX2`/`SY2` keep the whole part and
drop the rest. The game copies the whole part into the GP0 packet, and so a vertex
that should drift a twentieth of a pixel per frame holds still for twenty frames
and then jumps a whole one. Every corner of a polygon jumps on its own schedule, so
the polygon twitches and shears between jumps; walk slowly towards a wall and its
edges crawl. That is the wobble, and it is the other recognisable half of a
PlayStation picture.

It is the *same discarded number* as the depth, one shift earlier in the same
expression, so it needs no new mechanism at all:

```csharp
int rx = (int)(sx >> 16), ry = (int)(sy >> 16);
...
if (GteDepth.Active)
    GteDepth.Record(nx, ny, sz, sx * (1f / 65536f), sy * (1f / 65536f), rx != nx || ry != ny);
```

`GteDepth` grew two floats per slot and a second switch; `Enabled` serves the depth
and `Subpixel` serves the fraction, one probe of the table either way. Everything
that makes the depth safe to recover makes the fraction safe too — a miss leaves
the vertex on the whole pixel the packet named, 2D never hits the table so the HUD
stays on the pixel grid it wants, and a vertex that saturated off screen is still
recorded for its depth against the clamped key, and left on the packet coordinate
so a shared edge does not open (see "The table is not unique"). **A vertex behind
the eye is dropped for both halves rather than one**, which is what keeps turning
the fraction on from changing which vertices carry a depth.

Note `>> 16` on a negative `long` floors, so `nx + fx` is the projected position on
the left of the screen exactly as it is on the right; a truncation-toward-zero
shift would have put the left half of every polygon a pixel out.

### The rule that is deliberately not carried over

Perspective correction is **all-or-nothing per primitive** — one vertex left at
W = 1 among two real depths shears the triangle's texture in half. The fraction is
**per vertex**, and the difference is what the two things are. W is an
interpolation parameter, so a corner disagreeing about it corrupts the whole
surface. A fraction is just where a corner is: a triangle with one corner moved a
half pixel is a triangle with one corner moved a half pixel.

The thing that could have gone wrong here is a **crack along a shared edge**, if
two triangles disagreed about where their common vertices are. They cannot: both
look up the same key and get the same answer, so a shared edge keeps identical
endpoints on both sides of it.

### The software rasterizer had to learn a finer grid

The hardware backend needed **nothing**. `HleVertex.X` has been a `float` the whole
time and `GlCore` passes it straight through, so adding the fraction in
`GpuHleForward.HV` is the entire hardware path.

`GpuRaster` is the one that walks whole pixels with integer edge functions, and it
now works in **sixteenths of a pixel** for any triangle where some vertex recovered
a fraction. What makes that a safe edit rather than a rewrite is that scaling every
coordinate by 16 scales the three edge functions and the area by 256 and leaves
every ratio taken from them — the barycentrics, the UVs, the Gouraud colours —
identical. So:

- a triangle where nothing was recovered runs at **shift zero**, which is the
  arithmetic the file always did, to the bit;
- the pixel is still sampled at its own coordinate, and the bounds are shifted back
  down with an arithmetic shift, which floors, so the covered pixels are the ones
  whose sample point lies inside the span;
- the `-1` fill-rule bias stays `-1`. It is applied to the edge *function*, not to
  a coordinate, so at either shift it breaks an exact tie on a shared edge and
  nothing else;
- 1024 pixels is the widest primitive the GPU accepts, so a coordinate stays under
  2^15 and the products stay far short of overflowing the `long` they already used.

`IsTopLeft` takes coordinates rather than vertices now, because by that point the
triangle is in the rasterizer's units and those may not be pixels.

**Exercising that path at all takes an edit.** `HostWindow` sets
`GpuHle.Active = _glBackend.Ready`, so on any machine where GL comes up — which is
every machine this has run on, Gl45 here — `GpuRaster.RasterTriangle` is dead code
and a change to it will be silently untested. Forcing it is one throwaway line in
`Program.cs`:

```csharp
Event.AddListener<RuntimeReadyEvent>(_ => RecompOne.Runtime.Hle.GpuHle.Active = false);
```

Both renderers were run that way for this: the software path holds 30 fps in the
attract demo and reports the same offsets as the hardware one.

### What it measures

`KF2_SUBPIXEL_PROBE=1` reports the **displacement**, not the hit rate — a different
question from the one `KF2_PERSPECTIVE_PROBE` asks, and it reads and resets only its
own counters so the two probes can be on at once without eating each other's
windows.

Steady state in the attract demo:

```
[KF2] subpixel: 47480 vertices/s carrying a fraction, mean offset 0.770 px, max 1.411 px, over 30 frames/s
```

**The mean is the measurement.** A point spread evenly inside a pixel sits
0.7652 of a pixel from that pixel's corner on average, and at most √2 = 1.4142 from
it. Measured across the demo: **0.760–0.773, max 1.413**. That is the recovered
fraction being a genuinely uniform fraction rather than a table full of zeroes or a
rounding artefact, and it is the number that says the low sixteen bits really were
being thrown away.

The hit rate is shared with perspective correction and is the same 90%: 47k
vertices a second recovered at 30 fps, so the second half costs nothing measurable
either. With the setting **off**, the perspective probe reports what it always did
(85–94%, 30 fps) — the extra table lookups only happen for untextured polygons when
the fraction is actually wanted.

### Why it is off by default, unlike its sibling

Not because it is riskier. The "a miss is the old behaviour" argument that licensed
perspective correction covers this identically, and the mechanism above is
measured. What is *not* done is **the picture**: perspective correction became a
default on the strength of an ordering table drawn twice and the two frames
differenced (76.6% of pixels, HUD provably untouched), and that pair has not been
taken for this.

It is takeable the same way and should be, since the flag is read at vertex-decode
time and so can be flipped between two `DrawOTag` passes exactly as the dither bit
was — see "Getting pixels out without a screenshot" in
[DEVELOPMENT.md](DEVELOPMENT.md). What to expect is *not* a large
pixel count: a change of at most one pixel on a polygon edge will move far fewer
pixels than a texture-mapping change that repaints every interior texel. The honest
measurement is probably edges only, and a still frame is the wrong instrument for
an artefact that is defined by motion. **Flip the default once that pair exists.**

## The table is not unique: remaining wobble and the "far away" pop

The 90% hit rate was never "10% of vertices the two ends disagreed about". It was
almost entirely vertices the first version of the table **refused to record**:
anything `SatX`/`SatY` had clamped to ±1024. Walk up to a wall, turn past a long
floor, and one corner of a large quad leaves that window. All-or-nothing then
drops the whole primitive back to affine, and affine on a floor you are standing
on looks exactly like the camera jumped to the horizon — the foreshortening
vanishes and the texture lies down. The same primitive, a step later, has every
vertex on-screen again and pops back to corrected. That is the "suddenly far
away" texture.

The other half is a collision, not a miss. Screen position is the key because it
is what survives into the packet, but it is not unique. Two vertices of different
depths land on the same pixel constantly — a distant wall behind a nearby
column, two off-screen corners stuck on the same clamp — and last-write-wins
hands one polygon the other's W. A nearby surface that inherits a far Z is
interpolated as if that corner were at the horizon, which is the same picture,
only tearing instead of flattening. The fraction is stolen the same way, so an
edge jumps by up to a pixel every time the winner changes, which is wobble that
sub-pixel recovery cannot kill because the vertex is being given *someone else's*
fraction.

`patches/recompone/0011-gte-depth-collisions.patch` is the rest of the same
mechanism, not a new one:

- **Saturated vertices are recorded for their depth**, so a large nearby polygon
  can stay perspective-correct instead of falling back to affine. They are **not**
  moved off the clamp wall. The first version of this patch placed them at the
  GTE's true 16.16 position; any neighbour still stuck at ±1024 then failed to
  meet, which showed as gaps in the geometry. The packet coordinate is what the
  GPU would have drawn, and a shared edge has to agree with it. Only the
  [0, 1) fraction of an on-screen vertex is served, and it is a function of the
  key alone — two triangles that share a vertex look up the same fraction even
  when they pick different depths.
- **Each key keeps the last four samples**, not the last one. A primitive is
  bound all at once: newest-at-the-key is the first guess, then any key with
  several samples is rebound to the depth that sits with the rest of the
  primitive (closest in log Z to the geometric mean of the hits).
- **A leftover high outlier is dropped.** Two depths in geometric progression
  (a corridor floor at 100, 500, 2000) pass; a cliff (100, 120, 8000) is a
  collision, that corner loses its W, and the triangle stays affine rather than
  tearing. Only the far end is tested — a vertex next to the camera among two
  distant ones is legitimate.

2D still corrects itself: a HUD sprite was never in the table, so it still
misses. The title-screen 0.0% hit rate is the control that this has not started
correcting menus.

What this does *not* do is follow the value through memory. That is PGXP proper,
and it is what would recover a vertex the game copied, offset, or interpolated
on the CPU after `RTPS`. The remaining wobble after 0011, if any, is that case,
or a primitive whose colliding samples are all similarly wrong so the pick has
nothing true to choose.

`KF2_PERSPECTIVE_PROBE=1` now also reports `saturated/s`, `refined/s` and
`rejected/s`. Saturated is the extra vertices that used to miss on purpose;
refined is a collision the pick resolved; rejected is a cliff it refused.

## Following the value through memory: the address is the vertex

The heuristics above did not fix the collision, they scored it, and a wrong score
is not a slightly wrong texture — W is the denominator of the perspective divide,
so a corner given a stranger's depth throws its texture across the screen. The
same wrong pick hands a corner a stranger's sub-pixel fraction, which is a vertex
that snaps a pixel for no reason the player can see. Both were still happening
after `0011`.

So the thing the previous section called "PGXP proper" and put out of scope turned
out to be the smaller change, because the recompilation makes the association
findable. What the disassembly says:

- **A screen coordinate leaves the GTE only through `swc2`.** There is not one
  `mfc2` of SXY0/1/2 in the whole recompilation (`Gte.Read(12|13|14)` has zero
  call sites; `Gte.StoreWord(12|13|14)` has thirteen). It emits as
  `m.WriteU32(addr, Gte.StoreWord(14))` — **the destination address is in hand at
  the store, and C# evaluates the argument before the call**, which is the entire
  plumbing. No `InstructionEmitter` change, so nothing has to be recompiled.
- **The game keeps a transform cache.** `func_8005D8E8` (`RotTransPers`) and
  `func_8005D914` (`RotTransPers3`) write the coordinate wherever the caller
  points them, and the caller is a loop like `func_8002E650` filling an
  8-byte-per-vertex array at `0x8018EB94` with `{sxy, otz, fog}` for a whole
  vertex list at once. Polygons are assembled afterwards, out of that array —
  which is why "the newest depth at this pixel" carried no information about the
  polygon being drawn: the table held the entire scene at once.
- **The assembler copies the coordinate as a whole word.** In `func_80030540`,
  `c.V0 = m.ReadU32(c.S4); m.WriteU32((c.S0 + 0x8u), c.V0);` and the same again at
  `+0x14`, `+0x20`, `+0x2C` — `xy0..xy3` of a POLY_GT3/POLY_GT4. Load and store are
  adjacent instructions. Only the UVs and the CLUT go by halfword.
- **The packet reaches the GPU from an address the runtime knows.**
  `LibGpu.DrawOTag` and `Dma.TransferGpu` both do
  `gpu.WriteGp0(m.ReadU32(addr…))`.

`patches/recompone/0012-exact-gte-vertex-map.patch` connects those four facts.
`GteVertexMap` is a map from **RAM word address** to `(z, fx, fy, the packed XY
word)`, filled by three exact hops:

1. `Gte.Rtp` keeps the depth and the truncated 16.16 fraction per screen-coordinate
   FIFO slot, shifted with `SX`/`SY`, so a read of SXY0/1/2 hands out the numbers
   belonging to *that* slot. `Gte.Read` of one of those registers publishes
   `(value, attributes)` into a small pending ring.
2. `PSMemory.WriteU32` of a value sitting in that ring binds the destination
   address to those attributes; `PSMemory.ReadU32` of an address the map knows
   publishes it again, so the attributes follow the game's `lw`/`sw` out of the
   transform cache and into the packet. A store with no match *clears* the
   destination, so a rewritten word stops answering.
3. `Gpu` keeps `_fifoSrc` beside `_fifo` — the address each command word was read
   from — and `DrawPolygon` asks the map for each vertex by its own address,
   **verifying the stored word against the word it is about to draw**.

The ring is what avoids tainting registers, which would have meant instrumenting
every instruction the recompiler emits. It is searched newest-first, preferring an
entry nothing has taken yet, so three `swc2`s of three coordinates that clamped
onto the same pixel still bind in the order they were stored.

Loading a coordinate *back into* the GTE invalidates the slot (`Write` cases
12-15). `func_8005DC6C` is `NormalClip` and hands all three vertices of a polygon
back for the cross product; without that, a later read would publish a stale depth
against a value that matches.

What this buys, measured at the attract-mode flythrough with
`KF2_PERSPECTIVE_PROBE=1`:

```
52k vertices projected/s, 52k caught/s, 87k copied/s, 94k looked up/s, 92.5% hit
```

`caught` equals `projected`, so every coordinate the GTE produced is picked up;
`copied` is half again as many, which is the assembler re-reading a shared vertex
for each polygon that uses it. The **92.5% hit rate is the same as the screen
position table's 92.0%** on the same scene (`KF2_PERSPECTIVE_FALLBACK=1` reports
both), so exactness costs no coverage — and the position table's figure was never
all correct answers, since a HUD quad it "hit" was being handed some 3D vertex's
depth. The remaining ~8% is 2D and anything the CPU computed, which wants affine.

`KF2_PERSPECTIVE_FALLBACK=1` keeps the old table filling and consults it for
vertices the map missed. It is off by default and exists to A/B the two in one
build; `refined/s` and `rejected/s` are gone from the report because there is
nothing left to pick between.

**Open, and this is now an option:** the cave section shows polygons alternating in
front of and behind each other. The map changes W and the sub-pixel position, and
the draw order is the game's ordering table, which the GPU walks back to front with
no depth buffer at all. Two coplanar surfaces the game sorted by a single OTZ per
polygon will flicker on hardware too. `patches/recompone/0014` is a Z-buffer from
the same recovered SZ; it is off by default until the picture has been looked at
in that cave. See "Z-buffer".

The cost lands on `ReadU32`/`WriteU32`, which is the hottest path in the port, so
it is gated twice: on `GteVertexMap.Active` (a static bool, false when perspective
correction, sub-pixel positioning and the Z-buffer are all off) and then on one bit of a
presence bitmap — 64 KB for the retail 2 MB of RAM. The attribute array itself is
10 MB, allocated on first use, and only touched on a bitmap hit. The frame rate
does not move.

### The RAM fast path went round both hooks

**Symptom, after the merge to upstream `0409bc2`:** textures affine and vertices
back on whole pixels, with every switch still reporting itself on — `[KF2]
perspective: on`, `[KF2] subpixel: on`. `KF2_PERSPECTIVE_PROBE=1` names the
failure exactly: `36720 vertices projected/s, 0 caught/s, 0 copied/s, 91872
looked up/s, 0.0% hit`. The GTE was projecting and the GPU was asking; nothing in
between was being *bound*.

**Cause.** Upstream added RAM fast paths to `PSMemory.ReadU32` and `WriteU32` —
an `Unsafe.ReadUnaligned`/`WriteUnaligned` straight into the array, taken by every
`lw` and `sw` in the game, returning before `ReadU32Slow`/`WriteU32Slow` is
reached. `GteVertexMap.NoteRead` and `NoteWrite` live in those slow paths, and the
merge kept them there. This whole mechanism *is* following a value through the
game's `lw`/`sw`, so a fast path that skips the hooks skips the mechanism: nothing
is ever published to an address, so every `TryGet` misses and both halves silently
fall back to what they do on a miss — affine, and the whole pixel.

**Fix:** offer the word to the map in the fast path too, still gated on the same
`GteVertexMap.Active` static bool, so the fast path keeps its speed and the
association is made where the game actually makes it.

**What the counters say to look at.** `Roots` (`caught/s`) reading exactly zero
while `projected/s` is healthy means the *store* side is not being seen; the hit
rate alone would not distinguish that from a game that stopped copying vertices.
Measured after, in area 2 at 144 fps: `364896-383616 projected/s, 369648-388368
caught/s, 465984-524100 copied/s, 93.0-93.7% hit`, inside the 92.2-97.1% band this
mechanism was first measured at, and 144.0 fps at 20.0 ticks/s with it on.

## Z-buffer: the same depth, used as occlusion

**Confirmed mechanism; picture checked and still wrong — a second cause is Open.**

The GPU has no depth buffer. The game sorts every polygon into an ordering table
by one number — the GTE's OTZ, the average of its vertices — and `DrawOTag` walks
that table back to front. Two surfaces that actually interpenetrate can only take
turns in front of each other, because each polygon is wholly in front or wholly
behind. That is the cave flicker noted at the end of "Following the value through
memory" above, and it is what a Z-buffer turns off.

The depth is the same SZ3 perspective correction already recovers. Nothing new is
caught; the rasterizer is just allowed to test it per pixel instead of throwing
it away after the texture divide. `GteVertexMap` already follows the word from
`Gte.Rtp` into the packet, and `DrawPolygon` already asks by the address the
coordinate was read from. `patches/recompone/0014` is the rest:

- **All-or-nothing per triangle**, same rule as W. A corner left without a depth
  among two real ones would punch a hole, so that triangle keeps painter's
  order.
- **2D never hits**, so the HUD, the menus and the death fade still draw on top
  in table order with the depth test off.
- **Semi-transparent tests and does not write**, so two overlapping additives
  still blend in the order the table named.
- **Untextured geometry is tested too.** Perspective correction only cares about
  textured polygons; a flat-shaded wall still has a view depth. `HasPersp` and
  `HasGteZ` are independent on `HleVertex` so putting SZ into clip W does not
  turn perspective correction on as a side effect.
- **Equal depths prefer the later table entry** (`GL_LEQUAL` / `>` reject), which
  is the painter's-algorithm tie the console had, so coplanar surfaces the game
  stacked on purpose keep their order.
- **The hardware path** attaches a 24-bit depth renderbuffer to each display RT
  and writes window depth `SZ/65536` from the fragment shader. Clip-space Z stays
  0, same as before this existed: these vertices are already projected, and
  putting SZ into `gl_Position.z` lets OpenGL clip them against a far plane the
  GPU never had — a hard line across the floor, far closer than the game's own
  fog. A vertex with no depth still emits `vec4(p, 0, 1)`, bit-identical to
  before. The first draw onto an RT after `Present` — or after the setting is
  flipped — clears the attachment, because this game's `PutDrawEnv` has `isbg=0`
  and would otherwise test against last frame.
- **The software path** keeps a float per VRAM pixel and tests it in the same
  inner loop that plots. Punch-through (texel 0) and a mask-bit reject skip the
  write, so a hole in the texture does not occlude what is behind it. Dead on
  any machine where GL comes up, as `GpuRaster.RasterTriangle` always was.

`KF2_ZBUFFER_PROBE=1` reports triangles tested against painter's-order fallbacks
per two-second window. The tested rate is the measurement: a rate near zero would
mean every triangle quietly kept the ordering table. Pixel rejects are a
software-rasterizer number and stay at zero on the hardware path.

**Off by default.** The recovered number is the one perspective correction already
measures at 92% hit, but the picture has not been checked by eye — and a twice-
drawn ordering table cannot take this pair, because the second pass would fail
every test against the first. The cave is the test.

**There is no longer a user-facing switch.** The player-facing checkbox was
removed from Video: recovering a usable depth here is effectively unbridgeable —
DuckStation's mature PGXP depth buffer, given the same per-polygon OTZ averages
this game submits, cannot produce a clean picture either, so offering a switch
that only ever half-works is worse than not offering it. The mechanism stays for
diagnosis, driven from the console alone: `KF2_ZBUFFER=1` forces it on for the
run and `KF2_ZBUFFER_PROBE=2` takes the census below. `patches/ZBuffer.cs` and
`patches/recompone/0014` are unchanged; only `patches/settings/ZBufferPage.cs`
and its registration are gone.

### The clear landed at the tail of the frame, not the head

Reported symptom: with the Z-buffer on, a large region of the picture shows the
background instead of the geometry in front of it — the sky drawn over houses and
walls a few metres ahead outdoors, a black hole in a cave. It moves with the
camera and it is not every frame.

`GlCore.PresentDisplay` opened with `_frame++; Flush();`. The depth clear above
keys on `rt.LastDrawFrame != _frame`, so incrementing first made that **trailing**
flush — the tail of the frame that is ending — look like the head of the next
one:

1. Frame N's draws stamp `LastDrawFrame = N`.
2. `PresentDisplay` sets `_frame = N+1`, then flushes. The guard sees `N != N+1`,
   **clears the depth buffer**, draws the last batch, stamps `LastDrawFrame = N+1`.
3. Frame N+1's first real draw sees `N+1 == N+1` and **skips its clear**.

So the clear happened one flush too late and suppressed the one that mattered.
Frame N+1 began with whatever depth frame N's trailing batch left. Nothing
rescues it: `PutDrawEnv` has `isbg=0` here, so there is no game-side full-screen
fill to reach `FillRtFull` and clear the attachment by another route. The trailing
batch is usually the 2D HUD, which does not write depth — that case leaves the
buffer wiped and looks correct, which is why the fault is intermittent. When the
last batch is 3D it stamps *near* depths, and the next frame's geometry is
rejected wherever they landed, leaving the earliest-drawn thing — the far
background — on screen. `patches/recompone/0016` swaps the two statements.

**The confirmation is a counter, not a screenshot.** `KF2_ZBUFFER_PROBE=2` reads
the depth attachment of the target the frame's depth batches actually went to and
prints a 32×16 min-per-cell map. Before: **67 of 91 blocks read `nothing
written`** on a target taking ~1500 depth batches a window, which had been written
off as a broken instrument. After: **11 of 11 populated**, a smooth near-to-far
gradient with no untested cells. The empty reads were the defect — the buffer was
being cleared at the end of every frame.

Two theories died on the way, both on the census's own evidence, and both worth
not re-deriving:

- *An early-sorted primitive claims a near depth.* Ruled out by `head` and
  `largest`: the far end of the table holds far depths (`ot 7492, z 5002..6088`)
  and the near end holds near ones (`ot 8490, z 803..3047`), in every area
  measured. The frame-wide maxima (22266, 23215) sit on small primitives.
- *A big surface wins the depth test against another.* `nothing is entirely in
  front of anything the table put nearer`, every block. The blocker was never in
  the frame.

The ordering table and the recovered SZ agree, so the "where they disagree, trust
the table" fix sketched for this would have had nothing to act on. **Note the OT
length varies per area** (8348, 8898, 9101, 9162, 9315 measured), so an `ot` is
only comparable inside its own frame.

`patches/recompone/0015` is the census itself: `GteDepth` keeps every polygon's
bbox, depth range, table position and flags for the window; `LibGpu.DrawOTag` and
`Widescreen`'s replacement of it publish the walk position (`OtEntry`, counted
from the far end); `GlCore` remembers which RT the depth batches went to, since
the presented one is last frame's under double buffering and the most recently
drawn one may have just been cleared by a fill. Diagnostic only.

**This did not fix the picture, and the cause turned out to be the paragraph at
the end of this section.** See "The clip W was the Z-buffer's second cause" under
"PGXP" below: `vDepth` was interpolated screen-linearly on every triangle whose
texture was not being corrected, which is most of the architecture. What follows
is the state of the investigation before that was found, kept because it is what
ruled the other explanations out.

Checked by eye after `0016`: the sky still
draws over walls a few metres ahead. So the clear timing was a real defect —
the buffer measurably did not survive its own frame, and now does — but it is
not the cause of the reported symptom, or not the only one. A second cause
remains; that unresolved symptom, together with DuckStation showing the same
class of problem is unbridgeable even with a proper PGXP depth buffer, is why the
user-facing switch was retired and the mechanism kept for diagnosis only.

That rules out a whole class of explanation, which is worth keeping: **the
depth the world is being tested against is now known to be this frame's**. Any
remaining theory has to work with a correctly cleared buffer, correct per-frame
depths, a table position that agrees with the recovered SZ everywhere measured,
and no primitive standing entirely in front of one the table put nearer. What
has *not* been measured is the geometry the depth is interpolated across
between the vertices — every census number above is per polygon, taken from its
corners, and the map is a 32×16 minimum-per-cell reduction. Screen-linear
interpolation of a view depth is wrong (it is 1/z that is linear in screen
space), which biases a polygon's interior; whether that bias is large enough to
lose a wall in front of the sky is the next thing to measure, not to assume.

## PGXP: upstream's own recovery, and what taking it actually bought

**Mechanism confirmed and measured; the picture has not been looked at.**

Everything above this point recovers the same two numbers — a vertex's true
screen position and the view depth the GTE divided by — and carries them from
`Gte.Rtp` to the GP0 packet by watching `PSMemory`'s words go past
(`GteVertexMap`, "Following the value through memory"). RecompOne grew a second
answer to that after our pin: a full **PGXP**, in `39fb337a`, `91c20fcf`,
`95f0585b` and `6aae910a` (2026-08-31 to 09-07). It is backported here as
`patches/recompone/0034`-`0036`, and both mechanisms ship, chosen between by
`KF2_PGXP` — **and by nothing in the settings window**. See "PGXP has no control
in the window" below.

The difference is where the following happens. `GteVertexMap` sees only that a
word left one address and arrived at another, and pairs the two by value; a
coordinate the game *computes* rather than copies is invisible to it. PGXP is
told what every register holds, by hooks the recompiler emits beside every load,
store, move, shift, add, multiply and divide (`0035`), and keeps a `PgxpValue`
per word of RAM. Nothing is inferred.

**Three things about the backport are ours rather than upstream's.**

- **The tolerance is spent.** Upstream defines `pgxp.tolerance`, draws a slider
  for it, and never reads the value. It is exactly the guard this wants: a
  recovered position more than a couple of pixels from the one in the packet is
  not a more precise version of this vertex, it is a different vertex, and
  believing it moves geometry. See "Picking the tolerance" below.
- **`PgxpStats`** counts where each answer came from — the RAM shadow, the
  screen-position cache, or the per-primitive ambiguity pass — because the whole
  question about a second mechanism is how often it answers and with what.
- **A depth-tested triangle is given a real clip W** whether or not its texture is
  being corrected, and that one is a genuine fix. See below.

### The clip W was the Z-buffer's second cause

The section above left this open: with a correctly cleared buffer and correct
per-vertex depths, the sky still drew through nearby walls, and the untested
suspect was *"screen-linear interpolation of a view depth is wrong (it is 1/z
that is linear in screen space), which biases a polygon's interior"*.

That is exactly what was happening, and only on the GL path. `vDepth` is an
ordinary varying, so OpenGL interpolates it in `1/w` — which is exact when `w` is
the view depth and **screen-linear when `w` is 1**. `HleTri` set the clip W only
for triangles whose *texture* was being corrected:

```csharp
bool persp = tex && a.HasW && b.HasW && c.HasW;   // before
```

An untextured wall never asked for texture correction, so it arrived with `w = 1`
and every depth between its corners came out linear in screen space. The software
rasterizer never had the bug — it interpolates the reciprocals and takes one back
(`useZ` in `DrawPolygon`) — so the two renderers disagreed about the interior of
every flat-shaded surface in the game, which is most of the architecture.

```csharp
bool persp = z || (tex && a.HasW && b.HasW && c.HasW);   // after
```

The cost is that a textured triangle drawn with the depth buffer on and
perspective correction *off* is now corrected anyway. That pair is a comparison
rather than a picture anyone ships, and a depth buffer fed wrong depths is not a
comparison of anything. **This fix applies to both sources**, so it is not a
reason to prefer PGXP — it is the reason the Z-buffer was worth revisiting at all.

### The depth-clear threshold, and why it is off

A frame is not one scene. `GteDepth.DepthClearThreshold` (DuckStation's
`pgxp_depth_clear_threshold`) watches the mean view depth of consecutive
primitives and starts the buffer again when it falls by more than that, which is a
scene the game began afresh under the same projection. It reaches the existing
per-frame clear by bumping `Generation` rather than adding a clear path of its
own, and it flushes the GL batch first so the clear cannot take this frame's
earlier triangles with it. `KF2_ZBUFFER_THRESHOLD`; zero turns it off, **and zero
is the default**.

DuckStation ships 300 and this port shipped 300 with it, and that was wrong. The
value is pickable the same way any threshold in this project is: look for two
populations and put the cut in the gap. `KF2_ZBUFFER_PROBE=1` censuses **every**
forward step between consecutive primitives, fired on or not, so the histogram is
of the game rather than of the setting. Measured over a walk through area 2:

```
forward steps 15696/s: 39.4% under 10, 47.7% under 50, 9.2% under 150,
                       2.8% under 300, 0.9% under 1000, 0.0% beyond; widest 318
```

**One population, decaying smoothly, with no gap anywhere in it** — which is what
ordinary depth sorting inside a single scene looks like, and there is nothing
else. King's Field draws one world and a 2D HUD, and 2D never recovers a depth so
it never enters the mean; there is no second scene to detect. The widest step in
the whole run is 318, barely past DuckStation's 300, so any threshold that fires
at all is firing on the game's own geometry.

Confirmed from the other side by turning it on:

```
2575.2 depth clear(s)/s at threshold 300
```

Some twenty clears a frame — the depth buffer thrown away and rebuilt over and
over inside one picture, which is worse than not having one — and the frame rate
went from 144 to 34-76 fps with it, because each clear flushes the GL batch. The
mechanism is kept because it is the right one for a game that needs it. This game
does not.

### Picking the tolerance

Same method, and the same answer shape. `KF2_PGXP_PROBE=1` buckets the
disagreement between every recovered position and the coordinate in the packet,
taken **before** the tolerance test so both populations would show if both
existed. Two windows, area 2, `KF2_PGXP_TOLERANCE=-1` so nothing was filtered:

```
disagreement with the packet, 409642/s: 29.2% under 0.5px, 68.2% under 1,
                              2.6% under 2, 0.0% under 4, 0.0% under 8,
                              0.0% beyond; widest 1.27px
disagreement with the packet,  57888/s: 24.1% under 0.5px, 68.2% under 1,
                              7.7% under 2, 0.0% under 4, 0.0% under 8,
                              0.0% beyond; widest 1.87px
```

**Nothing at all past 2 px, in either window, and the widest ever seen is 1.87.**
There is one population again: PGXP has never once answered with a different
vertex in anything measured here, so the guard has nothing to catch and any value
of 2 or above is inert.

The interesting part is the 2.6-7.7% *between* 1 and 2 pixels, because a genuinely
more precise version of the same vertex can only differ by the fraction the GTE
truncated, which is under one pixel by construction. That excess is not a wrong
vertex, it is **the divider**: `PushPrecise` recomputes the projection in doubles
as `OFX/65536 + IR1·(H/w)`, while the GTE used its own reciprocal lookup
(`Divide(H, SZ3)`, the `Unr` table). The two disagree by up to about two pixels on
a far vertex, and that is a real difference between what PGXP believes and what
the packet says, not noise.

So: **2 is the right number and it is right by luck rather than by argument** —
below 2 it starts refusing correct answers (7.7% of them at 1 px), above 2 it
refuses nothing anyone has ever seen. It stays at 2 as a tripwire: if a scene ever
does produce a wrong-vertex population, the refusal counter is what will say so.
`-1` turns it off and costs nothing measurable today.

### Measured: the coverage was already there, and PGXP costs a fifth of the frame

Both sources, autostart into slot 2, `warp 2` over the shell once the loader had
finished, then walked around for 45 s at `KF2_FPS=144`. Eight probe windows each:

| | address map (`0012`) | PGXP, full | PGXP, `KF2_PGXP_CPU=0` |
|---|---|---|---|
| vertices answered | 92.2 - 97.1% | 93.4 - 96.7% | 79.2 - 85.7% |
| of those, from the RAM shadow | — | 93 - 97% | **0** |
| triangles depth-tested | 90.2 - 96.2% | 91.4 - 95.6% | 77.4 - 79.3% |
| frames a second | **144.0** | **106.7 - 114.9** | **143.8 - 144.1** |

That third column is the cost isolated, and it also shows that **upstream's two
tracking options are not independent**: `PgxpMemory.Store` is only ever reached
from `PgxpCpu`, so turning CPU tracking off empties the RAM shadow whatever the
memory tick says, and PGXP degrades to the screen-position cache — the same class
of guess the address map was written to replace, and it measures like one.

**The honest reading is that PGXP did not buy coverage in this game.** The address
map was already answering for 95% of vertices, because King's Field assembles its
packets with whole-word `lw`/`sw` out of a transform cache — the one shape a
value-matching ring follows perfectly. What PGXP costs is real and measured: a
fifth of the frame rate under load, and the third column above places all of it in
the emitted hooks rather than in the lookup. With PGXP *off* the recompiled binary
still reads 144.0 fps at 20.0 ticks/s, so `0035`'s emitted
`if (Pgxp.CpuTracking)` branch is free when nothing is using it.

What PGXP does have that the address map cannot:

- **Backface culling on precise positions** (`Nclip`). A sliver polygon whose true
  area is a fraction of a pixel can come out the wrong sign from three truncated
  screen positions and drop out of the frame entirely. Nothing in this port
  measures how often that happens, and nobody has looked.
- **Positions rather than fractions.** PGXP produces the projection in floats;
  `GteVertexMap` produces the fraction the GTE truncated, which is the same number
  only where the packet coordinate is the one the GTE wrote.
- **Correctness by construction.** The address map's 95% is a rate that happens to
  be high for this game's packet assembly. PGXP's is what a tracked value does.

So it stays off, and it stays. The thing to keep in mind before reaching for it
is that **the number that fixed the Z-buffer here was the clip W, not the source
of the depth.**

### PGXP has no control in the window

It had one — a *Vertex source* combo and six checkboxes under Video ▸ Geometry
precision, plus upstream's own PGXP block, which the vendoring merge brought back
into `DisplaySettingsSection` alongside it. Both are gone, and so is the depth
buffer's checkbox that shared that heading.

The test is the one the map's style, the widescreen ticks and the two shading
checkboxes were each measured against: **is this a choice the player owns?** PGXP
is not. It buys no coverage in this game (92.2-97.1% against 93.4-96.7%), it costs
a fifth of the frame rate, and its picture has never been judged by eye — which
makes it a comparison between two mechanisms, and every comparison in this port
lives on the console. Nine controls asking a player to arbitrate between two
implementations of a number the console discarded is the pane describing the
implementation rather than the game.

Two consequences worth stating:

- **The saved key is no longer read.** `Pgxp.Reload` forces `pgxp.enable` false
  unless `KF2_PGXP` says otherwise, so a config that ticked it while the combo
  was drawn is not left running a fifth slower with nothing in the window to
  explain it. That is the `kf2.framepacing.logichz` rule applied again.
- **Upstream's frame-rate slider went with it.** It is the *interpolated* rate —
  it writes `Interp`'s key and disables itself unless PGXP is on — and this port
  never enters `PresentLoop`, so `Interp.Backend` stays null and the slider
  changes nothing it claims to. The port's own rate is `FramePacingPage`, under
  Video, and two frame-rate sliders in one pane is one of them lying. The comment
  left in `DisplaySettingsSection.Draw` says so, for the next merge.

## Dithering: one flag, and it lives in the draw environment

The 4x4 crosshatch over every shaded surface is the GPU's ordered dither, and the
port reproduces it faithfully on both render paths — the software rasterizer adds
the table entry in `GpuRaster.Plot`, the hardware backend packs it into the vertex
texpage word and applies it in `quant5` in the fragment shader. Both read the same
single bit of GPU state, `Gpu._dither`, and `SetDrawMode` sets that from bit 9 of a
GP0(E1) word and from nowhere else.

So removing the dithering is not a rendering change at all. It is one question:
**can an E1 word with bit 9 set still reach the GPU?** There are three routes, and
they are worth separating because only two of them are hookable:

1. **`PutDrawEnv`**, from `DRAWENV.dtd` at `env+0x16` — `LibGpu.GetMode` turns that
   byte straight into bit 9. **This is the only route this game uses**: exactly one
   dithered draw env per frame, 30 a second at 30 fps, on the title screen and in
   `fdat02` alike.
2. **A `DR_MODE` or `DR_TPAGE` packet linked into the ordering table.** Measured
   over the same two, **zero** — the game sets the mode once a frame and never
   mid-frame. (`SetDrawTPage` would build one with the bit already clear;
   `SetDrawMode` is the one that could set it.)
3. **Recompiled MIPS writing GP0 through the trapped register**, from the parts of
   libgpu that are not mapped to the runtime's HLE. **Nothing here can hook this**,
   so it is checked instead of intercepted: `KF2_NODITHER_PROBE=1` samples GPUSTAT
   bit 9 after every frame, and it read 0 for whole sessions of title screen and
   play. That register
   read is what closes the loop — without it, "the two hooks fire" would only be
   evidence about the two routes that were already known.

`patches/NoDither.cs` covers routes 1 and 2 and reports on all three under
`KF2_NODITHER_PROBE=1`; the switch itself is in System ▸ Settings ▸ Video. It
**restores what it clears** — the `dtd` byte and any E1 word are cleared in the
pre-hook and put back in the post-hook — so game memory is identical either side
of the call and nothing survives switching it off. That matters most for the
packet buffer: some of it is built once and re-sent for the rest of the run, so a
bit cleared in place would stay cleared long after the setting was turned off.

It was `mods/nodither` first, and the conversion left the measurement exactly
where it was: **the counters and the GPUSTAT sample stay behind
`KF2_NODITHER_PROBE=1`, on the console.** The settings page is one checkbox and a
tooltip. A per-frame report is what you want while establishing which of the three
routes a game uses; it is not what belongs in a graphics settings window next to
vsync, and putting it there would have been the conversion quietly promoting a
diagnostic into UI.

The ordering-table scan steps **command by command** using the GP0 command lengths
rather than searching for bytes that look like E1: a colour or a vertex word can
perfectly well carry 0xE1 in its top byte, and clearing bit 9 of a coordinate moves
geometry. It stops at the two commands whose length depends on data that follows (a
polyline, and an image load); neither occurs in this game's tables.

**It is a pre/post hook, not a replacement, and that is deliberate.**
`HookManager` allows exactly one `Replace` owner per function — a second owner's
replacement is refused with `replace conflict on …` — and the widescreen patch owns
`DrawOTag`. Pre- and post-hooks compose with a replacement and with each other, so
the patch (`dither: off, 12 hook(s)` — pre and post on both entry points in all
three overlays), the widescreen patch and the frame pacing's own `DrawOTag`
post-hook all coexist.

Steady state with the patch in, probe on: `30 draw envs/s and 0 ordering-table
words/s asked for dither, over 30 frames/s; GPUSTAT dither bit 0` — one dithered
draw env per frame intercepted, route 2 unused, route 3 never taken.

## True color: the other answer to 15-bit banding

**Confirmed mechanism; picture is the point of the switch.**

The console renders into 15-bit VRAM (RGB5A1), so every shaded pixel is quantised
to five bits per channel — 32 levels. A wall that darkens with distance is a smooth
per-vertex Gouraud shade interpolated across the polygon; where that gradient
crosses each 1/32 step the framebuffer bands. The dither above is the hardware's
own answer: it adds the 4×4 table before the truncation and spreads the step, at
the cost of the crosshatch. With the dither off — this port's default — the bands
show raw. That is the banding a player sees on a fog wall.

True color is the second answer, and it removes the banding *without* the
crosshatch. Two things enforce the five-bit truncation, and both have to give:

- **The render-target format.** Geometry rasterises into a `GlDisplayRt` whose
  colour attachment is an `Rgb5A1` texture, so even a full-precision fragment is
  crushed to five bits on write. `patches/recompone/0021` makes that attachment
  `Rgba8` when `GteDepth.TrueColor` is set. The mask/STP bit rides the alpha either
  way — one bit in 1555, the top of an 8-bit alpha in RGBA8 — and reads back
  `>= 0.5` in both.
- **The fragment shader.** `quant5` (both the GLSL 330 and the 120 path) ends in
  `min(c8 >> 3, 31) / 31.0`. Under `uTrueColor` it returns `c8 / 255.0` instead and
  skips the dither, which has nothing to dither into on an 8-bit target.

**Only the shaded gradient gains precision, not the texture.** Textures live in
15-bit VRAM and are sampled at five bits (`t8 = floor(texel*31+0.5)*8`, and the
CLUT/`fetch16` paths through `u5`), so the texture palette stays exactly the
console's — the picture is authentic where its precision *was* the texture, and
stops banding only where its precision was the framebuffer. The
writeback/​sync blits between an `Rgba8` target and 15-bit VRAM convert
automatically (`BlitFramebuffer` down/up-samples), so a true-colour target still
leaves 15-bit content in VRAM for anything that later samples it. Presentation
reads the target texture directly (`PresentFs` is a plain `texture()` fetch, no
re-quantise), so an `Rgba8` target presents smooth.

**GL backend only.** The software rasterizer keeps a 15-bit VRAM and is always the
console's precision; the switch does nothing there. Toggling at run time is safe:
`GlCore` compares `GteDepth.TrueColor` against the format its live targets were
built with and, when they differ, writes each target back to VRAM and drops it at
the next present — the next draw recreates it in the new format and re-syncs from
VRAM, costing one transition frame that presents from VRAM.

**Off by default**, so the default picture is the console's 15-bit output. Unlike
sub-pixel and the Z-buffer this is not off because the picture is unchecked — it is
off because 24-bit shading is deliberately *not* what the hardware did, and a
player who wants the authentic look should get it without a package to load.
(`KF2_TRUECOLOR=1` forces it on for the run.)

**It is not a checkbox of its own any more, and neither is the dither.** The two
are answers to the one question this section is named for, so they are asked once,
as the three-entry `Shading` combo under Video ▸ Enhancements — `Dither
(original)` / `None` / `Smooth (24-bit)`. `None` is what both switches' defaults
already were, so nothing anybody had saved changed meaning. See "Two shading
checkboxes were one question asked twice" in `docs/PATCHES_AND_MODS.md`.

## Anisotropic filtering: a pixel covers an area, and the console read a point

**Confirmed mechanism; picture never checked by eye.**

This is the *minification* half of the story perspective correction tells about
interpolation. A screen pixel does not cover a point of a texture, it covers an
area, and the shape of that area is the parallelogram spanned by the two screen
derivatives of the texture coordinate. Square-on to a wall it is about a square.
On a floor running away to the horizon, or along a corridor wall seen edge-on, it
is a long thin sliver — many texels along one axis and barely one across the
other. The console read a single texel out of that sliver, and *which* texel it
read changes completely for a sub-pixel movement of the camera. That is the
crawling, sparkling floor, and it is the artefact the port's own render scale
makes more visible rather than less: more output pixels means more independently
sparkling samples of the same sliver.

`patches/recompone/0041` samples along the sliver instead, in both prim fragment
shaders, driven by `GteDepth.Anisotropy`, with `patches/Anisotropic.cs` as the
switch (`KF2_ANISO=<1..16>`, `KF2_ANISO_PROBE=1`, and a combo under
Video ▸ Enhancements).

### Why it is a shader change and not a sampler setting

The obvious implementation — `GL_TEXTURE_MAX_ANISOTROPY_EXT` on the VRAM sampler
— does nothing at all here, and both reasons are what make this a shader change.
They are the same two the port records against any filter placed on this
geometry:

- **The VRAM texture is not a texture.** It is a 1024×512 (×`GlVram.Scale`) sheet
  holding every texture page, every CLUT and both display buffers at once. A
  filter running across it bleeds one texture page into the next, and bleeds a
  *palette* into the palette beside it.
- **A paletted texel is an index, not a colour.** In the 4-bit and 8-bit modes the
  value read from the page is a CLUT index, and the average of index 3 and index 4
  is index 3.5 — a different colour entirely, with no relation to either. Any
  filter has to run *after* the palette lookup, which is inside the shader by
  construction.

So `decode(raw)` does the whole per-texel job — texture window, the 8-bit page
wrap, the page fetch, the nibble/byte extract, the CLUT lookup — and the kernel
calls it once per tap. The single-sample path calls the same function, so with
the filter off the fragment is the one the port drew before, to the bit.

### There is no mip chain, and there cannot be one

A modern GPU does anisotropy as *N bilinear taps at a LOD chosen from the minor
axis*, and the LOD is what stops the major axis needing a tap per texel. The
first reason above forbids a mip chain outright: one page's lower level would
average in the pages beside it and a CLUT's would average in the palette next to
it. So this is supersampling — the taps run along the axis the footprint is
longest on, **one texel apart**, `min(ceil(len), uAniso)` of them, centred on the
pixel.

**The spacing is the part that had to be got right, and the first version got it
wrong**; see "The taps were spread over the whole footprint" below. Spreading
`uAniso` taps across the *whole* major axis is what a mipmapped filter does, and
it is only correct there because each tap is a pre-filtered sample covering the
gap to the next one. Here a tap is a point sample, so the honest choice is
between estimating the whole footprint from a strided handful and filtering the
part of it nearest the pixel exactly. This does the second.

Three consequences are worth recording rather than glossed:

- **Past `uAniso` texels of footprint it filters a part rather than the whole.**
  A distant floor may read fifty texels per pixel; at 16 taps the kernel averages
  the middle sixteen and degrades toward nearest beyond that, rather than toward
  a full box filter. That is the price of the reach cap, and it is the right one:
  the alternative reads other textures (below).
- **A footprint large on *both* axes is still averaged along one of them only.**
  The short axis keeps the console's single sample. That is the right trade in
  this game — it magnifies far more often than it minifies, and the artefact being
  chased is the anisotropic one — but it is a limit, not a completeness.
- **The cost is paid only where the footprint is actually long.** `taps` is
  `ceil(length(majorAxis))` clamped to the setting, so a surface facing the camera
  takes one tap at 16× exactly as it does at Off. That is why the measurement
  below shows no frame-rate difference rather than a small one.

### The taps were spread over the whole footprint

Reported from play, at oblique angles, against the first version: *it barely looks
different, there are white artefacts on the floor, and the fire billboards show a
bit of texture overlap at the edge of the texture area.* All three are one line —

```glsl
vec2 t = vUV + axis * ((float(i) + 0.5) / float(taps) - 0.5);   // the defect
```

— which places the taps across the **whole** major axis whatever its length, so
the spacing is `len / taps` texels and the reach is `±len/2`. At the very angles
the filter exists for, `len` is not 8 or 16 texels but tens or hundreds.

**A derivative is in unclamped texture-space units, and a texture is not.** A
floor tile is 64 texels wide inside a 256×256 page that holds three others beside
it; the page itself sits in a 1024×512 sheet holding every page, every CLUT and
both display buffers. Once the reach passes the texture's own width the taps wrap
— by the texture window, or by the page's `& 0xff` — onto **other art**. In the 4-
and 8-bit modes that art is a table of *indices*, and they are read through *this*
primitive's CLUT, so the colour that comes back is not a blurred neighbour but an
arbitrary entry of an unrelated palette. Bright entries are the **white speckle on
the floor**. On a billboard, whose sprite is one small rectangle among many in its
page, it is the **neighbouring sprite arriving at this one's edge**.

And it barely looked different because a strided undersample of a two-hundred-
texel span is not a low-pass of anything: it is sixteen more chances to sparkle.
The probe shows that directly at a 64-texel footprint, where the old kernel's
spread *rises* from 2 taps to 4 —

| `uAniso` | spread (sd), taps over the whole span | spread (sd), taps one texel apart |
|---|---|---|
| 1 | 59.42 | 59.42 |
| 2 | **21.18** | 48.44 |
| 4 | **25.06** | 33.90 |
| 8 | 19.55 | 16.86 |
| 16 | 12.97 | **7.09** |

— a filter whose output gets *noisier* as you give it more taps is not filtering.
The one-texel spacing is monotonic. (At a 16-texel footprint the two are the same
kernel at 16 taps by construction, which is why the table further down is
unchanged there.)

**What the probe cannot see is the reach itself**, and that is worth stating
plainly: its texture is white noise, where every texel is independent, so reading
a texel a hundred away and reading the one next door are statistically the same
draw. The reach is arithmetic rather than a measurement — `±len/2` before,
`±taps/2` after — and the picture it produces is the user's to judge.

**The second defect was the silhouette.** The colour came from a coverage vote —
average the solid taps, discard below half — while `texel.a` came from the centre
tap, so the two disagreed about what the pixel was. A fragment whose own texel is
transparent, which the console did not draw at all, was drawn whenever half its
taps came back solid: the sprite grows outward by up to half a footprint, into
exactly the neighbouring art the reach was already reading. The centre tap decides
the silhouette now, alone, so it is bit-for-bit where truncation put it, and the
kernel does not run at all on a transparent fragment.

### Two things the hardware's encoding forces

These are shared with any filter placed after the CLUT and neither is optional.

**A transparent texel is black.** The PlayStation marks a texel transparent by
storing it as all zero — RGB 0 with the STP bit clear — so a plain average next to
a punch-through edge averages *black* in and draws a dark fringe round every
grate, torch and bush in the game. Each tap is therefore weighed by whether it is
solid, and the colour renormalised by the surviving weight.

**The silhouette is not the filter's to decide.** Weighing the transparent taps
out is a statement about colour only: whether the fragment is drawn at all is the
centre tap's answer and nothing else's, which is where truncation put it. An
earlier version discarded below half coverage instead, on the argument that half
is where the edge sits — see "The taps were spread over the whole footprint" — and
that both grows and shrinks a punch-through edge by up to half a footprint. An
even tap count puts no tap at the centre, so every tap can come back transparent
on a fragment that is drawn; the average is then simply not applied.

**The semi-transparency bit is a mode, not a colour.** `texel.a` selects whether
the fragment goes through the blend equation at all — it is what picks between
`uBlend` and `uBlendOpaque` on the dual-source output. Interpolating it would ask
the GPU for a state halfway between two blend equations. It is taken whole from
the centre tap, which is the texel the unfiltered path would have read.

### It needs no "is this 3D" test, and that is the part worth keeping

Any filter that softens texels has to be kept off the HUD, the menus, the 2D
screens and the billboard sprites, which are drawn at or near 1:1 where there is
no detail to recover and only text and icons to blur. That normally costs a test,
and the only test available here is whether the exact GTE vertex map answered for
the primitive's vertices — which **exists only while perspective correction is
on**, so it has to fold in `!Enabled` or the whole picture silently reverts with
the combo still claiming otherwise.

Anisotropy needs none of it. A 2D primitive is axis-aligned and unminified, so
both derivatives are about one texel, the major axis spans one texel, `taps` comes
out 1, and the fragment takes the single-sample path — the same `decode()` call,
bit for bit. **The kernel is self-gating on exactly the geometry it should be**,
with no varying to carry, no dependence on the vertex map, and nothing to go wrong
when perspective correction is switched off. It is the one place in this file
where the cheap answer is also the complete one.

**A billboard is not in that list, and the first version of this section said it
was.** The HUD, the menus and the 2D screens are drawn at 1:1 and do gate
themselves out. A billboard sprite is a *world-space quad*: it minifies with
distance like anything else, so its footprint is genuinely several texels and the
kernel genuinely runs on it. That is correct — a distant torch flame should be
filtered — and it is why the two defects above showed up on the fire before they
showed up anywhere else: a sprite is a small rectangle in a shared page, so it is
the geometry with the least room either side of it for a filter to reach.

### Scope

**GL backend only**, on both the core-profile and the GLSL 120 paths; the software
rasterizer is always a single sample. **The native VRAM paths only** (`texMode` 0,
1, 2/3, and only while `vRepClut` is clear): an asset pack's replacement textures
and replacement CLUTs already sample real GL textures through their own sampler
state, which is where their filtering belongs. Nothing is rebuilt when the setting
changes — it is a plain uniform the next batch reads, unlike true color, which has
to rebuild its render targets.

### What it measures

The shader loop is bounded at 16 and `Anisotropic.Level` is clamped to 1..16, so
raising the ceiling needs both changed together.

`GteDepth.AnisotropyLive` is the counter that matters, and it exists because of
what this port has already been bitten by twice: the RAM fast path went round the
vertex map's hooks and left `[KF2] perspective: on` printing at boot over a dead
mechanism, and a hook summary counted registrations rather than detours. A picture
switch that cannot reach the shader should say so. It is set from the one place
that uploads the uniform, and `KF2_ANISO_PROBE=1` reports it on the first frame
that has actually presented — the prim program is built on the first present, so
it cannot be asked at `RuntimeReadyEvent`:

```
[KF2] aniso: on, up to 8 taps
[KF2] aniso: level 8, uniform bound and uploading
```

Both shader pairs were compiled and linked through Mesa directly — headless EGL,
the same driver the runtime uses — because `glslangValidator` cannot parse GLSL
120 at all and its SPIR-V mode rejects the dual-source outputs the prim shader
needs. Both link, and `uAniso` survives optimisation in both (locations 16 and
18), which is what says the kernel is reachable rather than folded away. That was
the check the GLSL 120 path actually needed: its loop bound is a constant with a
`break` inside precisely because 1.20 does not promise dynamic loop bounds.

The port's own acceptance run at `KF2_ANISO=8`: `open` → `game` → `fdat02` →
`fdat05`, slot 2 restored at HP 46/86 in area 1, **144.0 fps drawn at 20.0
ticks/s**, no exceptions and no `compile failed` or `link failed`. The same at
`KF2_ANISO=16`, with **no frame-boundary warning** — worth stating because a
short run killed during the area load does print one, and that reads as a
regression when it is only a load that never reached steady state.

### The frame rate cannot answer this one, and what can

Uncapped in area 1, the port draws **861.6 fps at `KF2_ANISO=1` and 860.7 at
`KF2_ANISO=16`** — no difference at all. That number is worth nothing on its own,
and it is the exact shape of mistake this file keeps recording: the port is
CPU-bound at ~860 fps, so the whole GPU cost of the kernel hides under the
recompiled MIPS, and "costs nothing" and "never runs" produce the same reading.
`AnisotropyLive` says the uniform arrives; it does not say a pixel changed.

`scripts/shader_probe.c` answers that directly — it drives the **real** fragment
shader headless, with a substitute vertex shader supplying its varyings, over a
noise texture at a chosen footprint, and reads the pixels back. Extract `PrimFs`
from `GlShaders.cs` into `PrimFs.frag` (strip the eight leading spaces of the raw
string literal), then:

```bash
gcc -O0 -o /tmp/shader_probe scripts/shader_probe.c -lEGL -lGL -lm
FOOT=16 /tmp/shader_probe PrimFs.frag 1 2 4 8 16
```

**The statistic is the spread across neighbouring pixels, not the mean.** Pixels
covering nearly the same texels returning wildly different colours *is* the
sparkle; a filter that works collapses that spread. Over a 16-texel footprint:

| `uAniso` | min | max | mean | spread (sd) | range |
|---|---|---|---|---|---|
| 1 | 99 | 247 | 173.75 | **51.23** | 148 |
| 2 | 107 | 238 | 167.00 | 35.57 | 131 |
| 4 | 123 | 222 | 169.62 | 24.87 | 99 |
| 8 | 132 | 206 | 170.69 | 24.38 | 74 |
| 16 | 140 | 181 | 162.88 | **11.41** | 41 |

A monotonic 4.5x collapse, which is the mechanism working and is the number to
re-take after any change to either shader. (Re-taken after the tap spacing was
fixed; the 1 and 16 rows are unchanged, since at a 16-texel footprint sixteen taps
one texel apart *are* sixteen taps over the whole span. The rows between moved
because they no longer stride.)

**The self-gating claim is measured rather than argued.** At a footprint of 1.0
texel per pixel — a HUD sprite, a menu box, a font glyph — `uAniso=1` and
`uAniso=16` return *identical* pixels (min 66, max 247, mean 170.98, sd 53.86,
both), and the same at 0.5 texels. Filtering begins at 2 texels (sd 61.24 ->
41.33) and reaches the table above at 16. So "a 2D primitive takes the unfiltered
path by construction" is a reading rather than a hope.

**One honest limitation the probe found.** Tap spacing is uniform, so it can alias
against periodic texture content when the taps are fewer than the texels spanned.
The first version of this test used one-texel stripes, and a period-2 pattern
against an even tap spacing puts *every* tap on one parity — which read as the
filter doing nothing at 2 and 4 taps and as a wildly dark result at 8. That is a
property of mip-free supersampling rather than of this kernel (a mipmapped
anisotropy prefilters along the minor axis and cannot hit it), it needs texture
content periodic at almost exactly the tap spacing to show, and the noise figures
above are the fair case. It is recorded because that stripe run looks like a bug
report and is not one.

**Off by default.** The mechanism is measured; the picture **has** been looked at
once, and that is where the two defects above came from — the frame rate and the
noise probe both read healthy while the kernel was reading other textures, which
is the same shape of mistake this file keeps recording. What has not been looked
at is the picture since they were fixed: whether a receding floor stops crawling,
whether the white speckle and the billboard's edge are gone, and how the average
reads against the 15-bit quantisation, since `quant5` still crushes the filtered
result to five bits unless true color is also on and the two have never been seen
together.

## The render scale did not survive a menu

**Measured mechanism; the picture is what the report was.**

Reported from play: *in subscenes like when you open the menu, or talk to an NPC,
the render scale is overwritten in the 4:3 area in the middle of the screen, and
it renders at 1x.* That sentence names the mechanism precisely, because the middle
of the screen is exactly the region that goes through VRAM.

A modal sub-loop keeps the world behind it without redrawing it: it reads the
finished frame out of VRAM into system RAM once (`StoreImage`, GP0 `0xC0`) and
blits that copy back at the head of every iteration (`LoadImage`, GP0 `0xA0`), so
each pass erases the last one's menu boxes and text against a still world. **That
roundtrip is 1x by construction** — VRAM is the console's own 320×240, and
`GlCore.ReadVram` reads it back at that resolution whatever `GlVram.Scale` is — so
the restore stamped a one-sample-per-game-pixel picture over the display area
every single frame the menu was up.

Why the *middle* and not the whole picture: `Writeback` copies a render target's
middle `W` columns into VRAM and nothing else, because the widescreen margin lives
nowhere but in the render target ("The margin's only clear is the game's own" in
`docs/WIDESCREEN.md`). So the game's own 320 columns are the only ones a VRAM
roundtrip can reach, and the margin stayed at full scale beside them — which is
the seam the report describes.

Measured with `KF2_VRAMPROBE` (an ad-hoc census, not committed), at 16:9 and scale
4: in an area, every VRAM upload is at `x = 320` — texture space, outside the
display area — and the display columns are never written. Press Circle and the
census reads `read 1` followed by `write … 0,240 320x240` at 60 a second, forever.

**The fix is to keep a scaled copy of what was read.** `ReadVram` now also blits
the region into a snapshot texture at `GlVram.Scale` (`SnapTake`), and `WriteVram`
compares the 1x pixels it has been handed against that snapshot's; on a match it
blits the scaled copy back into VRAM instead of uploading (`SnapRestore`). The key
is **the content and the size, not the address** — the frame may be restored into
either display buffer, and identical pixels are identical wherever they land.
Anything the game actually built or changed in RAM fails the compare and takes the
upload path exactly as before, so a texture, an MDEC frame or a decoded sprite is
untouched; a readback smaller than 64×64 is not snapshotted at all, being a tile
rather than a frame. Two slots, LRU, invalidated by a scale change.

Measured at `KF2_FPS=144`, 16:9, scale 4, autostart into slot 2, with
`KF2_VRAMSNAP_PROBE=1`: in the menu, **120-121 restores per two seconds and 0
uploads that missed**, with the present census still reading `wide 180, plain 0,
vram fallback 0` and the world still at 20.0 ticks/s. In an area the counters are
`0, 0` — nothing reads the frame back, so the mechanism costs nothing. Boot and
the area transitions read `39 restored, 13 uploaded` and similar: the loading
screen and the fades do the same roundtrip and gain the same way, and the misses
there are the real image loads.

**Counting restores says the path fires, not that it wrote the right pixels**, so
under the probe the first restore of a run is read straight back out of VRAM at 1x
and compared against the upload it replaced — a scaled copy of the same frame must
downsample to the same picture, so any disagreement is the blit's geometry rather
than the resolution. Measured: `verify 320x240 at 0,0: 0 of 76800 pixels differ`.
That it lands at `0,0` and the menu's at `0,240` is also the evidence for the
address-independent key: both display buffers are restored from the one snapshot.

`KF2_VRAMSNAP=0` is the comparison and puts the 1x upload back. There is no
control in the window, because a render scale that survives a menu is not a
choice. **Not looked at by eye** — what is measured is that the restore is served
from the scaled copy on every frame of a menu, not that the menu now looks like
the world behind it.

The mechanism is `patches/recompone/0039`. GL backend only: the software
rasterizer has a 1x VRAM and nothing to preserve.

## The display list cannot name a face: why packet-level smoothing failed

The port smooths between logic ticks by carrying *tables* — the camera in
`patches/FrameSmoothing.cs`, four model tables and the entity records in
`patches/ObjectSmoothing.cs`. That set has been found incomplete three times, each
time by a person noticing something step in play, and item 6 in
[TODO.md](TODO.md) is the standing request to stop finding them that way.

The generic alternative tried here was **packet-level interpolation**: one layer
below the tables, at `DrawOTag`, the frame is a finished list of primitives; if the
same primitive can be recognised in the previous tick's list, its screen position
can be carried by the tick phase, and one hook covers doors, tiles, enemies and
animation alike. It does not work for models, for a reason worth writing down
because it is not obvious and because the measurement that appeared to endorse it
was measuring the wrong thing.

`patches/PacketMatch.cs` (`KF2_PACKETMATCH=1`) is the probe, and it is kept. It is
a measurement only — it never writes to game memory.

### Where a primitive's identity comes from

A read-only pre-hook on `DrawOTag` walks the ordering table exactly as
`Widescreen`'s replacement does (`patches/Widescreen.cs:409-450`). Not
`RenderPrimEvent`: that event carries four screen positions, four flags and a CLUT,
and nothing else — no texture coordinates and no source address — so nothing
intrinsic to a face is visible from it.

Which object a packet belongs to is read off the **primitive arena**, the same
mechanism `patches/DrawCensus.cs` attributes bytes with: the game bumps a
`{start, end, current}` descriptor at `0x8017E0A4` once per polygon, so the packets
a call produced are the addresses between its entry and exit `current`. Two calls
are attributed:

* `func_80032588`, the model submitter — `a2` is the position pointer, which names
  the table slot and covers all four model tables at once.
* `func_80031950`, the map-tile submitter — `a0` is the tile record address **plus
  the half offset** (`S0` or `S0+5` in `func_80031B1C`), so it already distinguishes
  a tile's two drawn halves and needs no separate counter.

Three keys were measured, and a fourth number decided it:

| key | definition |
|---|---|
| **K1 ordinal** | `(kind, contextId, index within context)` |
| **K2 lerpable** | K1 matched *and* the same primitive shape |
| **K3 intrinsic** | `(kind, contextId, hash of command byte, UVs, CLUT/texpage)` |
| **correctness** | of the primitives K1 matched, how many matched a face with the **same texture coordinates** |

### The ordinal matches, and names the wrong triangle

K1's hit rate is high and it is **meaningless on its own**. Back-face culling
submits only the faces pointing at the eye, so dropping one polygon shifts every
ordinal after it by one: the key `(slot, 5)` still exists on both sides of the
tick, so it counts as a match, while now naming a different face. Measured, models,
one second a window:

| what the player was doing | K1 matched | …and was the same face |
|---|---|---|
| standing perfectly still | 100% | **100%** |
| walking or turning | 92–100% | **14–40%** |

Map tiles survive it — 76–100% correct under the same motion — because a tile is a
simple static mesh whose visible-face set barely changes. Models do not.

**This is the trap to remember.** A match rate answers "did the key find
something", and the question is "did it find the *right* thing". The first reading
of this experiment reported 96.6–99.3% and concluded the design was viable; the
second key, K3, was disagreeing with it at 56–94% the whole time, and that
disagreement was read as K3 being the weaker key rather than as the two keys
contradicting each other. Applying it looked exactly like what it was: **"every
object becomes super garbled the moment I move"**, because two thirds of the
carried motion belonged to some other triangle.

### The other thing a screen-space carry gets wrong

Even with a correct key, a primitive's whole displacement is the wrong quantity to
carry. The frame is already drawn through a camera `FrameSmoothing` has advanced to
this frame's phase, and through positions `ObjectSmoothing` has already
interpolated; adding the full per-tick delta adds both a second time and runs
objects ahead of the world on every turn. The quantity to carry is the **deviation
from the context's mean** — the mean being the object moving as one, which is
already carried, and the deviation being the pose. The probe reports that split as
`pose split`, gated on the camera having held completely still, because parallax
from a moving *eye* survives the mean and would otherwise be counted as pose.

That measurement is worth keeping whatever happens next, because it is the evidence
that there is anything to fix: with the camera frozen and two enemies attacking,
over twenty-five consecutive windows, the objects' own translation was 0.1–0.8 px a
tick while the motion of their primitives *relative to each other* was **3.4–13 px
a tick on 9–13% of contexts, peaking at 36**. There is real animation the tables
cannot reach — the model pipeline has no skeleton, so a pose is vertex data or a
swapped model index (see "The model pipeline has no skeleton" in
[GAME_INTERNALS.md](GAME_INTERNALS.md)) — it simply cannot be recovered from the
display list.

### Two defects in the probe itself, both fixed and both instructive

* **Vertex colours are not identity.** The intrinsic key hashed them at first and
  read 0–5% on the world against 92–99% for the ordinal. They are the game's own
  per-frame shading, recomputed as the camera moves, so a face never hashed the same
  twice; the HUD, whose colours are constant, was the only thing that matched, which
  is what named the cause.
* **The UV word's upper half is pad on the third and fourth vertices.** Only the
  first two carry the CLUT and the texpage. PSY-Q does not clear the rest, and the
  arena hands out memory two frames stale, so hashing it made the key differ from
  itself with the camera standing still. `Gpu.DrawPolygon` reads exactly the three
  fields that mean anything.

### What would work instead

The identity problem is created by projection and culling, so it does not exist
before them. Inside `func_80032588`, a vertex is identified by its index in the
mesh — exact, not inferred — and a morph applied there comes out through the
game's own transform, so there is no camera to subtract and no mean to take out.
`patches/AnimSmoothing.cs` no longer hooks that fetch. Creature pose is an
MO clip: `func_8003486C` already produces a 12.12 weight from integer time,
and `func_80034A74` already morphs. Driving that clock between ticks lets the
decoder write the in-between mesh; interpolating object-space `SVECTOR` reads
did not, because most submits are rigid architecture (`CurAnim >= 0x80`) and
the first-person arm is posed by the MO clip clock like everything else. Tile
height is a different, smaller
problem and is not this. See "The model pipeline has no skeleton" in
[GAME_INTERNALS.md](GAME_INTERNALS.md).

## "No textures on the other machine": splitting the three layers

Reported from play on a Windows PC with an Nvidia card: the game runs, the HUD is
correct, and every wall, floor and ceiling is one flat colour. The dev machine
(AMD RX 9070 XT, Mesa 26.2, GL 4.6, `Gl45`) draws the same scene textured.

A picture cannot say which layer failed, and there are three, each of which
produces *exactly* the same flat-shaded surface:

1. **The game submitted flat polygons.** Textured-ness is a bit in the GP0
   command the game itself wrote — `tex = (cmd & 0x04)` in `GpuRaster` — decided
   long before any GL call. If the game's own state is wrong, no renderer change
   puts a texture back.
2. **The VRAM page is uniform.** A textured polygon sampling a page that never
   received its upload draws one colour per surface. Indistinguishable from (1)
   by eye.
3. **The fetch is wrong.** Textured prims, populated pages, and still no texture
   leaves `PrimFs`'s `texelFetch` — the only part of this that is vendor-specific.

`patches/TexProbe.cs` (`KF2_TEXPROBE=1`) separates them, writing to
`texprobe.log` beside the saves as well as to the console, because a Windows
release is a `WinExe` and its console output goes nowhere a player can send. It
hooks nothing and writes nothing to game memory: the census is a
`RenderPrimEvent` listener, which **both** renderers pass through and which
carries `Textured`, `Raw`, `Gouraud` and `Clut`; the page count is a
`ReadVram(0,0,1024,512)` from a `VSyncEvent` listener — the backend's own thread,
once a second, since the read stalls the pipeline — reduced to the number of
*distinct* 16-bit words in each of the 32 texture pages. A page holding art holds
hundreds; a page that never received its upload holds one.

Measured on the dev machine, in an area, at `KF2_FPS=60`:

```
gl: AMD | AMD Radeon RX 9070 XT (radeonsi, gfx1201, ACO) | 4.6 (Core Profile) Mesa 26.2.0-devel
backend Gl45  ready True  active True  scale 4  truecolor False  perspective True  subpixel False  zbuffer False
prims/s 70500  textured 70440 (99%)  flat 60  raw 0  gouraud 69600  semitrans 17400  distinct cluts 11
vram y0  :  150  147   98  142  196 1000 1000    2 1000 1000 1000 1000 1000 1000 1000 1000
vram y256:  358  452  446  479  394 1000 1000 1000  428  270  800 1000 1000  423  122   76
```

**That reading is from the shipped launcher, not the developer build**, run
against a fresh data directory (`VERDITE2_DATA`) so every setting is at its
release default — true color off, sub-pixel off, dither on, 4:3. It is the same
99% textured picture the developer build gives, so **the release path is not the
cause and this is not a packaging regression**; the difference is the other
machine. The developer build reads the same census with the dev config
(true color on, sub-pixel on, 16:9).

### What the other machine can be asked without a new build

Three controls in the shipped release already split the remaining space, and all
three are in System ▸ Settings ▸ Video (each needs a restart):

* **Backend → `gl33`.** Same fragment shader, completely different VRAM path: no
  `glTextureBarrier`, no `glCopyImageSubData`, ping-pong FBOs for the destination
  read. Textures returning here indicts `Gl45Vram`.
* **Backend → `gl21`.** A *different shader* (`PrimFs120`, GLSL 120, no
  `texelFetch`, no dual-source blending) and a different VRAM path again.
  Textures returning only here indicts the 330 shader.
* **Render scale → 1.** `uScale` becomes 1, so `fetch()` stops striding
  (`texelFetch(uVram, c * uScale, 0)`) and `WriteRect`'s upload blit stops
  magnifying 4x. Textures returning at 1x indicts the scaled VRAM texture.

And **Debug ▸ VRAM viewer** answers layer (2) on its own, by eye, in one look.

Never checked: any of this on Nvidia hardware. Nothing here owns an Nvidia GPU,
so the vendor half of the question is a report rather than a measurement.
