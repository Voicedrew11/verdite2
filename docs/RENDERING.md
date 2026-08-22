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
| Z-buffer | **measured**, same recovered SZ | skybox parked by SZ/OT disagreement in the far band; longer check owed | off |
| Dithering (removal) | **measured**, all three routes | **checked**, twice-drawn pair | off (no crosshatch) |

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

## Z-buffer: the same depth, used as occlusion

**Mechanism confirmed; the skybox is parked on the SZ/OT disagreement, restricted to the far band it links in so a mid-table wall is never parked and seen through; the default still owes the picture a longer look before it flips.**

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
measures at 92% hit, and the outdoor defect below is fixed by parking the skybox
on its SZ/OT disagreement; the default still waits for a longer look across a real
session — and a twice-drawn ordering table cannot take this pair, because the
second pass would fail every test against the first. The cave is the test. The
switch is under Video with the others; `KF2_ZBUFFER=1` forces it on for the run.

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

**This did not fix the picture.** Checked by eye after `0016`: the sky still
draws over walls a few metres ahead. So the clear timing was a real defect —
the buffer measurably did not survive its own frame, and now does — but it was
not the cause of the reported symptom.

### The second cause: the backdrop is linked far ahead of the depth it projects

The census above never caught it because every window it ran was indoors, where
the far end of the ordering table genuinely holds far depths. Outdoors one
specific thing differs, and the resolution rule was already written down in
`GteDepth.OtEntry`'s doc comment before anything acted on it: **where the
recovered SZ and the game's table position disagree, the game overrode depth on
purpose.**

The first guess at the shape of that disagreement was wrong, and the outdoor
census (`KF2_ZBUFFER=1 KF2_ZBUFFER_PROBE=2`) corrected it. The sky is **not** a
small box projecting *near*: its recovered SZ is mid-range — `ot 4 z 4674..6667`,
`ot 12 z 6408..7260` in one window — squarely overlapping real geometry, so no
"near" cut can isolate it. What is true is that it is linked at the **extreme far
end** (`ot` 4–13, the first nodes the walk visits) while the terrain genuinely
behind it reaches `z` 14000–23000 at `ot` 5000–8000. For real geometry position
and depth agree — a low `ot` carries a high SZ (`head #59 ot 5341 z
10563..11394`). The backdrop inverts that: linked farthest, projecting only
mid-depth. Believing its SZ makes it draw first, stamp mid-near depths across the
top of the screen, and reject every world primitive **farther** than it — the
distant terrain it is supposed to sit behind.

Two earlier attempts missed, both instructive:

- **A fixed far band** (`OtEntry < 64`) keyed on table position alone. `OtEntry`
  ticks once per linked-list node — empty filler slots and world primitives alike
  — so the band's contents depend on how the game packed its table; it sliced
  through a backdrop spread past node 64 (leaving part of it depth-writing) and
  over-captured any real primitive linked in the first 64 nodes.
- **A near-SZ cut** (`SZ < FarSz × 0.3`) assumed the backdrop projected near. The
  census killed it outright: the backdrop's SZ (~5000–7260) sits *above* the cut
  (`FarSz 13581 → 4074`), so it was never parked and the sky returned.

The fix keys on the disagreement measured the right way.
`GteDepth.IsBackgroundPark(a.W, b.W, c.W)` predicts the depth a table position
*would* carry if position and SZ agreed — `FarSz × (1 − OtEntry / OtLength)` — and
parks a primitive projecting below `SkyParkMargin` (0.7) of that prediction:

- `FarSz` is the scale the prediction measures against, published one walk late
  beside `OtLength` in both `LibGpu.DrawOTag` and the widescreen replacement of
  it. It began as the frame's own farthest recovered depth; why that could not
  hold is the next subsection.
- At the extreme far end the prediction is ≈ `FarSz`, so the backdrop projecting
  a third to a half of that is caught wherever it links; a genuinely-distant wall
  carries at least its predicted depth and keeps testing; the near half predicts a
  near depth, so nothing there is ever parked. Measured across five outdoor
  windows the backdrop ran at ≤ 0.55 of predicted and the nearest real primitive
  at ≥ 1.14, so the 0.7 cut sits with margin on both sides.
- `maxSz` (the farthest corner) is what is tested, so a near-to-far spanning wall
  is never mistaken for the flat backdrop. Parked primitives are counted as
  `ZBand`. The far end's other occupants, the 2D tints, never recover a depth and
  are untouched.

Parking was first expressed as it reads — a vertex flag cleared, so the backend
skipped the test. That moved the hardware backend's batch key
(`GlCore.DrawTri` derives `zMode` from whether vertices carry recovered depth at
all), and every parked triangle sitting between tested ones in submission order
flushed the whole GL batch: outdoor windows measured **40–90 `Flush()`es per
frame** against 3 when nothing parked, and any scene drawing a skybox collapsed
to ~20 fps while scenes without one were untouched. The fix expresses the same
semantics as a depth instead: a parked primitive keeps the test but carries
`GteDepth.ParkedFarSz` (65535, SZ3 saturated — the deepest window depth there
is) as its depth attribute, which the shader maps just under the cleared
attachment. It passes against empty space, writes a depth nothing real sits
behind, and ties with fellow backdrop fragments under LEQUAL — painter's order
survives by arithmetic and the batch key never moves. One semantic edge: a
backdrop quad linked *after* deeper world geometry now loses to that geometry's
real depth instead of overdrawing it — strictly more correct than the
unconditional draw, and not observable in the game's own ordering.

`KF2_ZBUFFER_PROBE=1` prints the frame's `far z`, the `parked z` range, how far
along the table the backdrop spread (`parked entries`), and `z-batches/frame` —
the flush counter that caught the cost above. The backdrop sits below the depth
its position predicts, and the table can be seen holding that story;
`KF2_ZBUFFER_PROBE=2`'s per-primitive census (`ot` against `z`) is the instrument
that calibrates `SkyParkMargin` per area. The setting stays off by default until
the picture has had the longer look a default flip wants — both the outdoor
backdrop and the cave flicker are things to watch across a real session.

### The scale has to outlive the frame

The disagreement test above was still intermittent outdoors: windows where the
sky drew over the terrain alternated with windows where it sat correctly behind.
The arithmetic says why. The sky is unparked exactly when
`maxSz ≥ 0.7 · FarSz · (1 − OtEntry/OtLength)`; at its far-end link that is
`FarSz ≲ 7260/0.7 ≈ 10.4k`, and the census numbers put the terrain at
14000–23000. So the park held only while `FarSz` stayed near the area's real
maximum — and `FarSz` was the *previous walk's* max. Any walk that followed a
walk without deep world geometry — facing a wall, a street canyon, a camera
swing — collapsed it below 10.4k, the sky depth-tested, stamped SZ 4674–7260
across the top of the frame, and rejected the terrain behind it until some deep
view refilled the scale.

Latent beside that was the opposite failure: the sky fed the same scale it is
measured against, because the accumulator took every full-depth primitive,
parked or not. An area whose real max depth stayed under ~1.43× the sky's SZ
(`Zmax_true/FarSz < 0.7`) would have had its *real* far geometry over-parked —
the shape of the z-fighting regression the fixed band caused, waiting in a
different area.

The scale now has three properties, all in `GteDepth`:

- **Sticky per overlay session.** `PublishFarSz` only ever raises `FarSz`
  (`if (_farSzAccum > FarSz)`), so a near-only walk cannot shrink it. The
  walk's own max is kept beside it as `FarSzWalk`, and `KF2_ZBUFFER_PROBE=1`
  prints it as `(last walk N)` whenever it dips under the held scale — the
  dips the old mechanism died on, made visible.
- **Blind to the far band.** Primitives linked fewer than `SkyScaleBand`
  (512) nodes from the far end never feed the accumulation. The backdrop
  links at the extreme far end and spread past node 64 when the fixed band
  tried to capture it, so 512 covers it with margin. Excluding genuinely
  far-clamped geometry only lowers the scale, which is the safe direction:
  real geometry then projects at or above its prediction more easily and is
  never over-parked.
- **Reset per area.** A static constructor listens for
  `OverlayLoadedEvent` and zeroes `FarSz`, so one area's scale cannot leak
  into the next; the first walks after a load run un-parked at scale 0,
  which the loading transition covers.

Why this cannot bring the z-fighting back: the game sorts by recovered depth,
so for real geometry `maxSz/predicted ≈ Zmax_area/Scale`. Once the sky is out
of the accumulation, `Scale ≤ Zmax_area` always holds, the ratio is ≥ 1, and
nothing real is ever below the 0.7 cut — while the sky, at ≈ 0.32 of the area
scale it can no longer prop up (7260 against 23000), parks permanently once an
area has shown anything deeper than ~10.4k. An area shallower than that never
parks its sky, but it also has nothing farther for the sky to occlude.

### The park has to stay in the far band, or a wall is seen through

The disagreement test still had a residual, and it was the mirror image of the one it
fixed: not the sky drawing *over* a wall, but a *wall* vanishing to show the room
behind it. `IsBackgroundPark` was applied at **every** `OtEntry`, with the threshold
scaled down by position (`FarSz × (1 − OtEntry/OtLength)`). `OtEntry` is a linked-list
node index — only a rough proxy for depth — and `FarSz` is sticky across the whole
area, so in any view shallower than the area's deepest the prediction is inflated and
**mid-table walls** fall under the cut and park. (The 40–90 `Flush()`es a frame that
the flag-clear scheme cost were the same false positives, scattered through submission
order — the contiguous sky alone could never cross the batch key that often.)

While parking meant "drop the depth test, keep painter's order," a false positive was
harmless: the wall still drew over what it should. The performance fix changed that —
parking now rides to the backend as a far-plane depth (`ParkedFarSz`, above), and a
wall pushed to the far plane is the farthest thing in the buffer. The room behind it is
linked *farther* in the table, so it draws *first* and writes its real, nearer depth;
the parked wall then loses `LEQUAL` to it and is rejected, and the room shows through
the wall. It is intermittent because it rides on `FarSz` history and table packing.

The fix is one clause: park only inside the far band the backdrop links in,
`OtEntry < SkyScaleBand`. This is not merely a tighter heuristic — it removes the
*mechanism*. A see-through needs a parked primitive drawn **after** the geometry it
should occlude; a far-band park is among the **first** nodes the walk emits, and
anything it should sit in front of is nearer, hence higher `OtEntry`, hence drawn
after — so when the park draws, nothing it would hide has drawn yet, and the real
geometry that follows wins on its own depth. Far-band parks tie each other at
`ParkedFarSz` under `LEQUAL`, so later-wins reproduces the table's own order exactly.
Even a stale-high `FarSz` that over-parks a genuinely-distant far-band primitive now
renders it *behind* everything rather than as a hole. `SkyScaleBand` (512) is already
the region excluded from the `FarSz` accumulation, so one constant now partitions the
table: at or past it a primitive feeds the scale and never parks, before it a primitive
may park and never feeds the scale. It cannot bring back either earlier defect — the
sky (`ot 4–13`) is still inside the band and still parked, and real intersecting
geometry is mid-table (`ot ≫ 512`) and always tests — and it costs nothing, since the
test short-circuits on the gate for the ~99% of primitives outside the band.

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
