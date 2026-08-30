# Game internals: the reverse-engineered map of King's Field

Addresses and routines inside `GAME.EXE` and the area modules — what the game
does per frame, where the player lives in RAM, how it dies, how it loads and how
it saves. Everything here was read out of the emitted C# or measured at run time;
where something is inferred rather than confirmed it says so.

All addresses are `GAME.EXE`'s unless stated. The archive and overlay side of the
area modules is in [RECOMPILATION.md](RECOMPILATION.md); the port's own patches
that drive these routines are in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md) and
[INPUT.md](INPUT.md).

**Game speed is frame rate here** — everything advances a fixed amount per loop
iteration, and the loop waits an integer number of vblanks — which is why frame
pacing is a correctness fix rather than a comfort setting. See "Frame pacing" in
[PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

## The main game loop, stage by stage

`GAME.EXE`'s per-frame loop is the tail of `func_8001369C` — everything before it
in that function is area setup, starting with nine `memset`s that are the game's
own declaration of where its per-area state lives. The loop itself is a **flat
list of thirteen calls with a backward branch at `0x80013918`, and the renderer
is last**:

| # | stage | instrs | subtree | reaches | words/entry | into |
|---|---|---|---|---|---|---|
| 1 | `func_8002C944` | 76 | 1 | — | 0.0 | — |
| 2 | `func_80037C0C` | 1917 | 224 | VSync DrawOTag CdControl | 4.0 | buf5 (5 words) |
| 3 | `func_8002A550` | 1113 | 452 | VSync DrawOTag CdControl | 0.4 | buf2 buf3 |
| 4 | `func_80040348` | 119 | 118 | — | 12–16 | buf6 buf7 |
| 5 | `func_80046A60` | 63 | 110 | — | 0–7 | buf7 |
| 6 | `func_8004910C` | 12 | 1 | — | 0.0 | — |
| 7 | `func_8001689C` | 429 | 77 | VSync DrawSync CdControl | 0.0 | — (66 while loading) |
| 8 | `func_80025A1C` | 30 | 1 | — | 0.0 | — |
| 9 | `func_800140AC` | 43 | 2 | — | 3.0 | buf8 (5 words) |
| 10 | `func_8002CA74` | 32 | 2 | — | 0.0 | — (81 while loading) |
| 11 | `func_80016FC8` | 159 | 13 | DrawSync CdControl | 0.0 | — |
| 12 | `func_80014534` | 79 | 35 | CdControl | 0.0 | — |
| 13 | `func_800342D8` | 253 | 159 | VSync DrawOTag … | 310–470 | buf1 |

**Take the write counts from a steady window, not the first one.** Stages 7 and
10 look like the busiest state writers in the window where the area loads (66 and
81 words a frame) and write *nothing at all* once it has — that is loading work,
and reading it as per-frame behaviour is the easy mistake here.

"subtree" is how many distinct functions the stage can reach, "reaches" is which
mapped SDK entry points are in that subtree — both from the emitted C#, so they
are static facts, not guesses. The renderer is confirmed dynamically as well: a
managed stack taken mid-frame reads
`func_8001369C → func_800342D8 → func_8002E0FC → func_80060818 (DrawOTag)`.

The nine buffers, straight out of the `memset` arguments:

```
buf0 0x8017E05C 0x0007    buf3 0x801B3084 0x0E46    buf6 0x8016C544 0x24F3
buf1 0x8017E084 0x5F3C    buf4 0x801C8484 0x4611    buf7 0x8019C5EC 0x0AA3
buf2 0x80199414 0x0058    buf5 0x80175914 0x21D1    buf8 0x80198574 0x03A7
```

The write counts came from a `loopprobe` mod (since removed), which snapshotted
those 66 KB at every stage boundary and attributed each changed word to the stage
that ran before it. What a steady in-area window says:

- **buf1 is the display list**, and stage 13 is the only thing that fills it —
  310 to 470 words a frame, varying with what is on screen, which is also how you
  can tell the camera is moving while the demo plays.
- **Almost nothing else moves.** Outside the display list the entire per-frame
  churn across all nine buffers is about **twenty words**: five in buf5 from stage
  2, five in buf8 from stage 9, a dozen in buf6/buf7 from stages 4 and 5. Those
  are counters, not state.
- **So the player's position is not in these buffers.** The demo is visibly
  walking — the display list changes every frame — and the game's own per-area
  clears do not cover whatever holds the camera. Two places to look next: the
  gaps between the nine buffers (`0x8017E068`, the area-module pointer, falls in
  one), and the area module's own data at `0x8019F07C`.
- **Stage 2 is the biggest function in the loop by far** — 1,917 instructions,
  224 functions reachable, `VSync` and `DrawOTag` in its subtree — and writes four
  words *through literal addresses*. Everything else it writes goes through the
  object-table base register, which is why the probe saw almost nothing. See
  "Stage 2 is the object-table state machine" below.

The probe is not free — 220k memory reads a frame drops the port to ~26 fps and
shifts the band histogram — so it is a diagnostic to run deliberately, not to
leave on.

### The loop's own rate gate is `func_80017880`, and the number is a literal 2

**Confirmed, read straight out of the emitted C#.** "The loop waits an integer
number of vblanks" is not a `VSync` argument — every `VSync` call in all three
overlays passes 0. The wait is the game's own, and it is three pieces:

| address | what |
|---|---|
| `func_80017850` | the **vblank callback**. Increments `0x801B6CA8` and `0x801B6CAC` and does nothing else. |
| `func_80018690` | registers it, on event `0xF2000003` spec `0x0002` (RCntCNT3/EvSpINT), and zeroes `0x801B6CA8`. |
| `func_80017880` | the **frame gate**. `EnterCriticalSection`; while `*(u32*)0x801B6CA8 < 2`, `ExitCriticalSection` and `VSync(0)`, and loop; then write `0` and return. |

**Stage 13 calls the gate as its last act** (`0x800346B4`), right after the
presenter `func_8002E0FC` — which is the frame's other `VSync(0)`. So a rendered
frame costs two `VSync` calls: one to show the picture, and however many the gate
needs to see two vblanks go by.

The literal `2` at `0x800178A4` **is the frame rate the game asks for**, in
software. Here, where `patches/recompone/0021-vblank-wall-clock.patch` advances
the emulated vblank on a wall-clock 60 Hz grid, it paces the port to exactly
30 fps whatever the host is doing, and nothing above 30 is reachable while it
runs — which is why `patches/FramePacing.cs` hooks it.

**What it asks for is not what the console delivered, and the port now says so.**
King's Field is heavy enough that the loop misses that deadline under load and the
frame costs three vblanks; since the game's speed *is* its frame rate, 20 is the
speed it was played at. The port's HLE GPU makes the two-vblank deadline every
time and never bands down, so the gate is now **skipped at every rate, not only
above 30** — it decides the render rate and the world rate together and knows one
answer for both. The world runs on `FramePacing.LogicHz` instead, 20 by default
and 30 as a setting. See "The reference band is 3 vblanks, not 2" in
[PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

`0x801B6CAC` is bumped by the same callback and never reset by anything.

**With the gate skipped, a rendered frame costs exactly one `VSync` call** — the
presenter's, which `func_8002E0FC` makes immediately *before* its `DrawOTag`. That
is what the port's frame boundary is keyed on, and since the gate is now skipped at
every rate it is the only case that arises: a `2` on `mods/framestats`' calls-per-
frame count means the gate is back. (The ordering is why it also worked while the
gate still ran below 30 — the presenter's call landed before the ordering table and
the gate's spin calls after it, so the count at the boundary was non-zero exactly
once per table either way.) See "Any frame rate" in
[PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

### What in the loop holds per-call state, and what of it can draw

Answered while fixing the frame pacing, and recorded here because the second half
is the constraint: a stage that submits primitives cannot be skipped on a frame,
whatever counters it also owns. Reachability is static, walking the call subtree in
the emitted C# for `DrawOTag`, `VSync`, `PutDispEnv` or `PutDrawEnv`.

| what | per-call state | can it draw |
|---|---|---|
| stage 3 `func_8002A550` | the player, the death counter, the poison tick, the buff timers, the global frame counter `0x80199488` | only through a nested sub-loop — see below |
| stage 4 `func_80040348` | the 200-record entity table | no |
| stage 5 `func_80046A60` | 128 effect lifetimes at `rec+0x0E` | no |
| stage 6 `func_8004910C` | the module's own per-frame logic | **no**, in all nine modules |
| stage 2 `func_80037C0C` | the object table at `0x80177714` — every world prop that moves | **yes**, but through one edge only — see below; gated regardless |
| stage 13 `func_800342D8` | the jitter accumulator at `0x8006E608`, in its own body | yes, it is the renderer |
| — `func_80033FBC` | the fade state machine, called by stage 13 | **no** — three functions, none of them draw |

### Stage 2 is the object-table state machine

`func_80037C0C` is where doors, the drawbridge, the minecart and the crystals
move. Its shape, read off the emitted C#:

- It walks the **object table at `0x80177714`** — `0x18C` (396) slots of `0x44`,
  count held on the stack, two base registers advanced by `0x44` at the loop tail.
  A slot whose **type byte at `rec+0x4`** is `0xFF` is skipped; that is the same
  free test `AreaWarp` and `AgentServer` use and the value the loader writes when
  it clears the table.
- Each iteration **publishes two pointers to globals**: the record itself to
  `0x8017E04C`, and its definition record to `0x8017E048`. The definition is
  `0x80175914 + (u16 at rec+0x6) * 0x18` — so `rec+0x6` is a definition index and
  `0x80175914` (inside `buf5`) is a `0x18`-stride table of object *kinds*.
  `0x8017E04C` is cleared to 0 on loop exit, so it means "the object being
  stepped right now" and nothing outside the loop can read it.
- It then **dispatches on the type byte**: `v1 = type - 2`, rejected if
  `v1 > 0xDF`, so valid types are `0x02..0xE1` — a **224-entry jump table at
  `0x8001191C`**, collapsing to thirty distinct arms. One arm is an indirect call
  through `*(u32*)(*(u32*)0x8017E068 + 0x24)`, slot 9 of the area module's header,
  which is how a module gives its own props behaviour.

What it writes, by record offset (site counts over the whole function):

| offset | width | sites | what |
|---|---|---|---|
| `+0x08` | u16 | 43 | the per-object state word — the state machine's own program counter |
| `+0x40` | u16/u8/u32 | 20 | a per-object timer |
| `+0x18` | u32 | 7 | the position VECTOR's **Y** lane — the bob, and a door or lift rising |
| `+0x26` | u16 | 7 | |
| `+0x24` | u16 | 6 | |
| `+0x38` | u8 | 10 | |
| `+0x3E` | u16 | 8 | |
| `+0x14`, `+0x1C` | u32 | 2, 3 | position **X** and **Z** |
| `+0x10`, `+0x0E`, `+0x28`, `+0x2C..0x30`, `+0x01`, `+0x04` | | 1–4 | `+0x04` itself, so a slot can retire or change kind |

`+0x18`, `+0x24` and `+0x40` are the three fields the rate census measured running
at the render rate — and `+0x24` is **not a timer**. It is the first lane of a
three-`s16` rotation at `rec+0x24`/`+0x26`/`+0x28`, which the object loop of
`func_800331B4` reads into the `a3` triple it hands `func_80032588`, applying the
same `0x800` yaw bias to `+0x26` that the entity table gets on its own `+0x40`.
The census could not tell the two apart because its sampler is 4-byte aligned, so
the one word at `+0x24` covers `+0x24` and `+0x26` together. Measured, the four
objects moving in a quiet area turn **0x80 a tick** — 1/32 of a turn, the constant
the arm at `0x80038BA8` adds — while they bob in Y.

**`func_80037B5C` is the transition fade**, and it is the one thing in stage 2
that draws. It steps a tint from `a1` to `a2` by `a3` and, for each step, calls
the tint drawer `func_8003220C` and then stages 11, 12, 8 and **13** — a modal
loop rendering its own frames, entered from an in-bounds trigger arm. It is also
what stage 3 reaches, which is why both stages carry the same gating exception.

**Stage 3 reaches the drawing entry points the same way stage 2 does**, and
the distinction from the frame's own render is what makes gating both safe. The
path is
`func_8002A550 -> func_80037B5C -> func_800342D8` — it calls **stage 13**, the
renderer, from inside itself. That is a modal sub-loop (the in-game menu and the
transitions around it) that takes over the main loop and renders its own frames
while it runs, not a per-frame contribution to the frame the outer loop is
building. Skipping stage 3 on a frame therefore never chops such a sub-loop in
half; it only decides whether one is entered. **Unverified by eye:** whether a menu
open at 120 fps flickers, since on the frames stage 3 is skipped nothing redraws it
and stage 13 still presents the world underneath.

**Stage 6 dispatches through the module header, so its target is read out of the
module image rather than the call graph**: `*(u32*)(*(u32*)0x8017E068 + 4)` is slot
1 of the 32-word table at the start of each `fdat` image in `CD/COM/FDAT.T`, at the
overlay's own `offset`. Six of the nine leave it an empty `jr $ra` — `fdat02`
(`0x8019F10C`), `05` (`0x8019F4FC`), `08` (`0x8019F1CC`), `17` (`0x8019F158`), `23`
(`0x8019F0FC`), `32` (`0x80193FB8`). The three that use it are `fdat11`
(`0x8019F424`), `fdat14` (`0x8019F5CC`) and `fdat20` (`0x8019F53C`), and it is
scripted-trigger work: `fdat11`'s reads the player position, calls the angle
helpers `func_80015394` and `func_80015328`, and writes state bytes.

**The fade, `func_80033FBC`**, is a four-state machine on the byte at `0x80192D42`:

```
state 0 -> 1   brightness 0x80192D44 += 0x14 each call, until >= 0x64
state 2        hold counter 0x80192D43 -= 1 each call, until zero
state 3        brightness 0x80192D44 += 0xEC (i.e. -0x14), until zero
```

Its only callees are `func_80033FAC` (one byte write) and `func_80022B20` (a small
byte fill), so it cannot draw and can be run on the game's clock rather than the
renderer's.

**The jitter accumulator at `0x8006E608`** is inline in `func_800342D8` itself,
just after a `func_80015374()` call: it adds the result, then subtracts an eighth
of it (`(v + 7) >> 3` with the sign fixup), so it is a damped accumulator driving
the screen shake, not a counter. No hook can reach it — `HookManager` detours whole
functions and stage 13 must draw.

### Stage 8 is the render camera, and it is the only copy

The stage table above credits stage 8 (`func_80025A1C`) with no writes, which is
true of the *buffers the probe watched* and misleading about what it does. It is
28 instructions with no branches, called from the loop as
`func_80025A1C(sp+0x28, sp+0x38)`, and it is the whole of "build the render
camera from the player state":

```
a0[0] = 0x801994EC (X)      a1[0] = 0x80199504 (composed pitch)
a0[8] = 0x801994F4 (Z)      a1[2] = 0x80199506 (composed yaw)
a0[4] = 0x801994F0 (Y) + s16 0x80199548 + s16 0x8019954C - 0x640
                            a1[4] = 0x80199508 (composed roll)
```

`0x640` is the eye height; `0x80199548` is the head bob `func_80028560`
maintains and `0x8019954C` the landing offset. The two stack blocks it fills are
handed on to **stage 9** (`func_800140AC`, the 3D sound listener) and **stage 13**
(`func_800342D8` → `func_8002E22C`, which copies them again into `0x80192E78` /
`0x80192E88`, derives the tile index `X >> 11`, `Z >> 11`, and builds the view
matrix that reaches the GTE).

So the composed **angles** are read once per frame, by one function, and nothing
between there and the picture reads them again. That makes stage 8 the only place
a port can move the camera without moving the game: a pre hook that nudges those
globals and a post hook that puts them back is visible to the renderer and to
nothing else. `patches/FrameSmoothing.cs` is that hook.

**The same is not true of the position, and this used to say it was.** Two of
stage 13's own callees read the player position triple directly, *after* stage 8
has run and after `FrameSmoothing.After` has put the un-nudged values back:
`func_80032400` reads `0x801994EC` and `0x801994F4`, and `func_800331B4` — the
world and object walks — reads all three. Found by listing every function in the
emitted C# that loads through the `0x801A0000 - 0x6B14/0x6B10/0x6B0C` base and
offsets stage 8 uses. It does not break the smoothing, whose position half is off
by default and which nudges the globals stage 8 copies rather than the copy, but
it does mean "one reader" is a claim about the angles alone.

### What in the renderer draws what

Stage 13 fills the whole display list, but which of its callees draws the world,
the map, the HUD and the player's own weapon was never written down, and it has to
be known before anything can be said about one of them updating at the wrong rate.
`patches/DrawCensus.cs` (`KF2_DRAWCENSUS=1`) answers it by attribution rather than
by reading code: the game bumps a `{start, end, current}` descriptor at
`0x8017E0A4` once per polygon, so the bytes a routine drew are the bump across it —
two hooks per routine, nothing per polygon, and a stack so nesting is charged once.

Stage 13 calls twenty-one routines. **Three of them draw**, and stage 13's own body
draws nothing (its exclusive count is 0):

| routine | what | measured, standing in an area |
|---|---|---|
| `func_80031C94` | the map: a 24x24 walk of the visibility grid at `0x80192EAC` | 27 packets still, 67 turning |
| `func_800331B4` → `func_80032588` | the object model submitter | 48 packets, ~2 calls |
| `func_80031D5C` | the HUD: HP/MP digits and gauges | 57 packets |
| `func_80032400` | **the player's first-person arm** | 0 standing still, 19.1-19.4 packets mid-swing |

**A fourth routine draws, and the census could not see it.** `func_80032400` — the
row the table above used to label "early 2D" — returns before it draws anything
unless `(s16)u16[0x801994A4]` is something other than `-1`, and that word is the
swing clock. Standing in an area it costs nothing, so a census taken standing in
an area credits it nothing, and a two-second averaging window over a swing that
lasts thirteen ticks barely moves it either.

That is how **the arm was written up as a 2D sprite in the HUD builder's row, and
stayed that way for four documents**. The reading behind it was that pressing
attack moved `func_80031D5C`'s packet count and nothing else — but it moved
*downward* (56.9 → 54.0 → 52.7), which is the wrong direction for an arm
appearing. Attacking collapses the HP/MP gauge widths, which are entries 9 and 10
of the HUD table, and that is what the difference was measuring.

**The arm is a 3D MO-animated mesh, on the same animation system as every creature
in the game** (`generated/game.cs:38436-38545`):

| step | what |
|---|---|
| guard | `if ((s16)u16[0x801994A4] == -1) return;` — nothing is drawn while idle |
| light | samples the map at the player's own position for a `0x68`-stride record at `0x801930F0`, then `SetColorMatrix`/`SetLightMatrix`/`SetBackColor` |
| place | translation from `[0x80199494]+0x34/36/38`, rotation from `+0x3C` — the **equipped weapon's record**, and static per weapon |
| pose | `func_80034834(0x20)` selects model `0x20`, then `func_80034DA8(0x8019949C, 0x20, u8[0x801994AE], (s16)u16[0x801994A4], vertexCount)` |
| draw | `func_8002E650` transforms the vertices, `func_8002F214(0, 0x64)` assembles them at OT depth 100 |

The arm's matrix is loaded straight into the GTE with no camera composed into it,
which is what "welded to the screen" turns out to mean mechanically — a view-space
3D model, not a screen-space sprite. Nothing swing-dependent reaches the matrix:
**the swing clock's only two destinations are the draw guard and the blender's
clip time**, so what changes during a swing is the mesh, not its placement.

The animation addresses, none of which were written down before:

| address | width | what |
|---|---|---|
| `0x801994A4` | s16 | the **swing clock**: `-1` idle, otherwise 0..4095. Fed to `func_80034DA8` as the MO clip time |
| `0x801994AE` | u8 | the **clip byte** — which attack |
| `0x80199494` | ptr | the equipped weapon's record: placement at `+0x34/36/38` and `+0x3C`, hit-window start at `+0x1E`, and the per-tick step `func_800271D0` adds to the clock |
| `0x801994A6` | s16 | the hit window's start, compared against the clock |
| `0x80199474` | s16 | must be `0` or `func_800262C8` refuses to start a swing |
| `0x801994AF` | u8 | player action state; `0xFF` also refuses a swing |

`func_800262C8` starts a swing (clock ← 0, kind ← `a0`) and `func_800271D0`, called
from **stage 3** and so once a tick, steps the clock by the weapon's own increment
until it passes 4095 and parks it back at `-1`. Measured on the weapon equipped in
the test save: **300 a tick, thirteen ticks a swing**, and the clip is 4096 long
like every other clip in the game. Because the clock is a per-tick counter feeding an MO clip
time, `patches/AnimSmoothing.cs` carries it exactly as it carries a creature's —
see "The player's arm is the same bug after all" in
[PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).
### The map is an 80x80 tile grid, and a tile's height is one byte

`func_80031C94` (the "map tiles" line in `KF2_DRAWCENSUS`, and 78% of a frame's
packets in a corridor) walks the 24x24 visibility window at `0x80192EAC` and calls
`func_80031B1C(tileX, tileZ, flags)` for each lit tile. That resolves the tile
record as (`generated/game.cs:37716-37727`):

    tile = 0x801C8484 + 800 * tileZ + 10 * tileX

so the map is **80 x 80 tiles of 10 bytes**, 800 bytes a row — the 24x24 grid is a
window into it, not its size. Both loop bounds test against `0x50` = 80.

Each tile has **two drawn halves**, and the pair is the whole record:

| offset | what |
|---|---|
| `+0x0` | model index for half A; drawn when `< 240` and bit 0 of `flags` |
| `+0x1` | **height byte** for half A |
| `+0x5` | model index for half B; drawn when `< 240` and bit 1 of `flags` |
| `+0x6` | **height byte** for half B |

The position handed to the submit routine `func_80031950(model, &vec, flags)` is
built at `generated/game.cs:37746-37762`, camera-relative:

    X = (tileX << 11) - [0x80192E78] + 0x400
    Z = (tileZ << 11) - [0x80192E80] + 0x400
    Y = (-(u8 height) << 7) - [0x80192E7C]

Three consequences worth keeping:

* **A tile's Y is a byte scaled by `<< 7`**, so map geometry moves in 128-unit
  quanta and cannot express anything finer without changing the computation.
* **`&vec` is a stack temp** in `func_80031B1C`'s frame, not game state. Anything
  that rewrites it before `func_80031950` reads it needs no restore and cannot
  leak into the AI or a save — unlike every other smoothing site in the port.
* **Moving architecture lives here, not in the model tables.** A `KF2_DRAWCENSUS=2`
  run taken while a drawbridge cycled listed 11 models, all byte-identical in
  position and rotation across two windows two seconds apart, while the map pass
  kept drawing ~593 packets a frame. The bridge is a tile, not a model.

### The model pipeline has no skeleton

In `func_80032588`'s 62-function subtree, `SetRotMatrix` and `SetTransMatrix` are
each called from **`func_80032588` itself and from none of its 61 callees**. One
matrix pair is loaded per model and every vertex is transformed under it, so a
model is rigid: there is no per-part transform stack and therefore no skeleton to
interpolate. The complete set of per-object transforms in the pipeline is the
position `VECTOR` and the Euler triple, both of which `patches/ObjectSmoothing.cs`
already carries. Anything that animates *shape* is doing it in the vertex data or
by swapping the model index at `a1` (`rec+0x6 + 0x100`), neither of which any
matrix interpolation can reach.

**Creature pose is an MO morph; most *submits* are still rigid architecture.**
`a1` is the mesh. **The clip and its time are stack arguments, not table
fields** — `func_80032588`'s eighth stack word (`caller SP+0x1C`) is the clip
byte and its ninth (`caller SP+0x20`) is the integer clip time. That is worth
stating in the argument list rather than as an offset, because
`func_800331B4` calls it from **five** sites over four tables with four
strides: the entity loop takes the pair from `S0+0x9` and `S0+0x15`, the
object loop from `S0-0x5` and `S0+0x9` off a `0x48` cursor, another from
`S0-0x13`/`S0-0xA`, and one passes a **literal 0** for the time. The
argument list is the one description true for all of them. (KingsFieldRE names
these `CurAnim` and the clip time on its own struct; those offsets are its
frame of reference, not this function's.)

`func_80032588` runs the blender `func_80034DA8` when the clip byte is
`< 0x80`, else `L800329F8` publishes a pointer *into the model* via
`func_8002E1F0` and the vertices never move. Walls and props take the second
path and dominate a frame's submit count — measured 60-122 rigid submits a
second against 0-61 morph ones. A walking enemy takes the first: MO is a base
TMD plus packed vertex deltas, and `func_80034A74` is that decoder into
`0x80190AD8`.

`func_8003486C(bank, clip, time, &segment, &weight)` walks the clip: it
accumulates the per-segment durations at `segment+0x2` until the time falls
inside one, publishes `((time - segmentStart) << 12) / duration` as a 12.12
weight, and returns the segment record in `v0`. **When the flag `u16` at
`segment+0x0` is set it publishes `0x1000 - that`**, so the weight runs *down*
as the clip runs forward — anything adding to it has to know which.

**The whole clip record is reachable from that walk, and its total duration is
the one number the pose smoother needs.** Read off the loop at `L800348B4`:

| what | where |
|---|---|
| clip table | `bank + u32[bank + 0x10]` |
| clip record | `bank + u32[clipTable + clip*4]` |
| segment count | `u16[clipRec + 0x0]` |
| segment pointers | `u32[clipRec + 4 + 4*i]`, `bank`-relative |
| segment: reversed flag | `u16[seg + 0x0]` |
| segment: duration | `u16[seg + 0x2]` |

so the clip's length is `sum(u16[seg_i + 2])` — exactly the accumulator the
clock compares the time against, which is why a time past it answers with the
last segment at a full `0x1000` weight. Measured live: every clip reached in
areas 0, 2 and 7 has a length of **4096**, and the highest clip time the legacy
probe ever saw on one is **4095**, which is the same fact read two ways. The
step a clip takes in a tick is 64-290 units there, so a cycle is 14-64 ticks
long and a wrap arrives as a step of `rate - 4096` — 3936, 3942, 4032 observed
against rates of 160, 154 and 64. `patches/AnimSmoothing.cs` reads this table so
it can treat the clip time as a point on a circle of that circumference; before
it did, the end of a cycle had to be guessed at from where the time landed.

**Why the clock reaches the mesh at all is `L80034FCC`**, and it is the
load-bearing part. When the clip *and* the segment index are both unchanged,
`func_80034DA8` skips rebuilding its keyframe cache — but it still copies the
base mesh into `0x80190AD8` and still calls the decoder with the weight
`func_8003486C` just wrote. Stage 13 therefore re-morphs on every rendered
frame; the only thing stuck on the tick is the time it morphs *to*. Move the
weight and the mesh moves. `patches/AnimSmoothing.cs` (`KF2_SMOOTH_ANIM=1`)
does exactly that — `floor(lerp(prev, cur, LogicPhase))` into `3486C` so the
segment pick matches the in-between instant, the leftover fraction added (or
subtracted, on a flagged segment) onto the weight. Vertex-fetch lerp at
`RotTransPers` was the previous attempt and did not change the picture. The
arm is `func_80032400`'s MO clip, carried by `AnimSmoothing` like any other;
origin and Euler are `ObjectSmoothing`.

- **`func_80032588` is fed from *four* tables, one loop each** — and for a long
  time only the first two were written down, which is how two separate "the
  animation runs at a low frame rate" reports were caused and then chased. The
  whole inventory, read off `func_800331B4` end to end:

  | # | base | stride | count | free test | position | rotation |
  |---|---|---|---|---|---|---|
  | 1 | `0x8016C544` | `0x7C` | 200 | byte `+0x0` == `0xFF` | `+0x2C` | `+0x40` |
  | 2 | `0x80177714` | `0x44` | 396 | **u16 `+0x6` == `0xFF`** | `+0x14` | `+0x24` |
  | 3 | `0x8019CC6C` | `0x48` | 128 | byte `+0x0` == `0xFF` | `+0x14` | `+0x24` |
  | 4 | `0x80195174` | `0x18` | 128 | u16 `+0x0` == `0xFFFF` | `+0x8` | none — zeroed |

  Every rotation lane is three `s16` with the yaw (`+2`) biased by `0x800`, the
  same convention throughout. Table 3 is **stage 5's** table, the one FramePacing
  describes as "128 effect/projectile lifetimes at `rec+0x0E`" — that name is a
  guess from one field, and the renderer draws it with a full position *and*
  facing, which makes it a general table of things that move. Table 4 is
  billboards; the renderer zeroes their rotation triple, and their positions are
  computed once when `func_80035550` fills the table from the area's own `0x10`-byte
  definitions rather than stepped per frame -- so there is no position to carry.

  **Table 4's animation is a different matter, and it was running at the render
  rate.** A billboard is a strip of authored cels, and four bytes at the head of
  the record drive it: `+0x2` the visibility mask, `+0x3` the number of cels, `+0x4`
  the interval, `+0x5` the current cel -- seeded at load to `(rand * cels) >> 15`, so
  two torches never flicker in step. The object loop draws slot `i` with the cel
  index `u8[rec+0x5] + 0x80`, then steps it whenever the **single global counter at
  `0x80195170`** divides exactly by the slot's interval -- and the last instruction
  of `func_800331B4` increments that counter. It is therefore a count of *rendered
  frames*: three sites touch it in all of `GAME.EXE` (the init zero in
  `func_8002DF80`, the modulus, the increment), and `func_800331B4` is called once,
  from stage 13. `patches/SpriteAnim.cs` holds the word and the 128 cel bytes across
  a walk the world did not tick on; see "The flames run at the render rate" in
  [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

  **The object table has two different emptiness tests and they are not
  interchangeable.** Stage 2 steps a slot when the byte at `+0x4` is not `0xFF`;
  the renderer draws it when the `u16` at `+0x6` — the definition index — is not
  `0xFF`. A patch that wants to interpolate *what is drawn* must use the second,
  and `patches/ObjectSmoothing.cs` used the first, silently dropping any slot that
  is drawn but not stepped by stage 2.

- **The first two loops, in the detail they were originally recorded:** `func_800331B4`
  loops the **entity table** `0x8016C544` (200 records, `0x7C` stride, free at
  `+0x0`) first — **creatures/enemies**, position at `rec+0x2C` and a three-`s16`
  rotation at `rec+0x40` (yaw biased by `0x800`) passed as `a3` — then loops the
  **object table** `0x80177714` (`0x44` stride) for static props and sprites. A
  `KF2_DRAWCENSUS=2` reading of `a2` reported only `0x80177714 + slot*0x44 + 0x14`,
  which for a while read as "the renderer never touches the entity record"; it was
  measured in a scene with props and **no creatures near**, so it caught the second
  loop alone. The truth is that the entity record is both AI state *and* what the
  first loop draws creatures from — stage 4 copies the object position into
  `rec+0x2C`, but the renderer then reads that copy, plus a rotation the object
  table has its own copy of at `+0x24`. This is why `patches/ObjectSmoothing.cs` interpolates
  **both** tables, and the entity table's rotation on top: smoothing `0x80177714`
  alone left every enemy stepping in position and facing.

Two traps in measuring it this way, both hit:

- **The arena is swapped, not just rewound.** `func_8002E064` at stage 13's head
  picks this frame's of two buffers (`0x800FC99C` / `0x8011599C`) and then rewinds
  it, so across stage 13 the entry and exit bump pointers are in *different*
  buffers and subtracting them reads as ±0x19000. Comparing the descriptor
  address, not the pointer, is what catches it.
- **A routine that unwinds to depth 0 is not necessarily a frame.**
  `func_80015374` is called from outside stage 13 as well as inside it, and
  counting frames on "the stack emptied" inflated the frame count 2.5x. Count on
  the renderer's own slot.

### The frame's applied position delta is a triple of its own

`0x801994FC` / `0x801994FE` / `0x80199500`, s16 each. `func_80028080` writes X and
Z there after the collision test (`0x80028478`), so it is **the movement that was
actually applied**, wall slide included, not what was asked for;
`func_800290D4` zeroes them when neither movement branch ran, and the state arm at
`0x8002A95C` writes all three as `target - current`. It is the game's own answer
to "how far did you move this tick", which is what makes extrapolating a position
between ticks need no trigonometry and no guess about which way strafe points.

## Player state: found, and it was in stage 3 all along

The probe above could not find the player because it was watching the wrong 66 KB
— the per-area buffers do not contain it. Reading the *emitted C#* found it in
an afternoon, and the route in was the pad: `func_8005F564` is libetc's
`PadRead(id)`, it has 17 call sites in `GAME.EXE`, and exactly one of them is a
main-loop stage. That stage is **3, `func_8002A550`**, and everything the player
does hangs off it. **Stage 2 was the wrong guess**; the four busy `buf5` words it
writes are not the view block.

The pad word is read once a frame and stored to **`0x80199554`**; every consumer
reads that global, and the `fdat` area modules never touch it, so all player
control is `GAME.EXE` code. The word is active *high* — libetc returns
`~*(u_long*)buf` — and its two button bytes are in the opposite order to the
runtime's `Controller` bit layout, so a mask decoded straight comes out as
Triangle/Cross/Square/Circle where the game means Up/Down/Left/Right.

Stage 3's own body, in order:

```
func_80023FCC        zero the three delta vectors, copy base angles to composed
PadRead(1)        -> 0x80199554
func_8002957C        player control: attacks, items, magic (reads pad + masks)
func_80028DB8        turn and look
func_800290D4        walk and strafe
func_80029EE0        base angles += delta vector A, then zero A
0x8002B330           composed = base + A + B + C
```

### The state, by address

| address | width | what |
|---|---|---|
| `0x801994EC` / `F0` / `F4` | u32 ×3 | position X / height Y / Z — the triple the collision queries (`func_8002C330`, `func_8002C700`) take with radius `0x320`. Read as **signed**: a normal Y is negative. Written by `func_80028080` (X/Z), `func_80028560` (Y) and `func_80028B0C`; nothing after stage 3 writes it — see "Debug tools" |
| `0x8019950C` / `0E` / `10` | s16 ×3 | base view angles: pitch / **yaw** / roll |
| `0x8019951C` / `1E` / `20` | s16 ×3 | delta vector A — zeroed each frame, folded into the base by `func_80029EE0` |
| `0x80199514` / `16` / `18` | s16 ×3 | delta vector B |
| `0x80199524` / `26` / `28` | s16 ×3 | delta vector C |
| `0x80199504` / `06` / `08` | s16 ×3 | composed view = base + A + B + C, what the renderer reads |
| `0x8019953E` / `0x80199540` | s16 | strafe velocity / forward velocity |
| `0x80199542` | s16 | resulting walk magnitude, `sqrt(vx² + vz²)` via `func_8005B890` |
| `0x80199544` / `0x80199546` | s16 | turn velocity / pitch velocity |
| `0x80199554` / `0x80199556` | u16 | this frame's pad word / its companion for edge detection |
| `0x80199558` / `0x8019955C` | u32 | this frame's walk speed (`0xC8`) / turn rate (`0x1C`, `0x23` standing still) |

Yaw is not a guess: `func_800290D4` turns the two velocities into a heading by
calling `func_80028080` with `yaw`, `(yaw + 0x800) & 0xFFF`, or `yaw ∓ 0x400` —
180° for backwards and 90° either side for strafing, which only makes sense for a
facing angle. A debug warp at `0x8002AFBC` writes the same three words as
`0, 0x0C00, 0` alongside a literal position, which confirms the triple's order.

**You turn faster standing still than walking** — 35 units a frame against 28 —
and that is the game's own rule, set in stage 3 from whether a movement button is
down. Worth knowing before blaming a mod for it.

### The action-mask table, and why nothing should hardcode a pad bit

The control code never tests a pad bit directly. It tests `pad & mask[i]` against
a table of 24 words at **`0x8006E568`–`0x8006E5D0`**, which is what makes the
game's own control-config screen work. Dumped at run time (the `analog` mod does
this) it reads:

| mask word | button | action |
|---|---|---|
| `0x8006E59C` / `0x8006E598` | Left / Right | turn: yaw += / −= `rate>>2` |
| `0x8006E590` / `0x8006E594` | Up / Down | walk forward / back |
| `0x8006E580` / `0x8006E588` | R1 / L1 | strafe right / left |
| `0x8006E584` / `0x8006E58C` | R2 / L2 | pitch += / −= 3 (R2 looks **down**) |

So **yaw increases when you turn left** and **pitch increases when you look
down**, which is the sort of sign a mod gets backwards until it reads this table.
The table settles the buttons but not which way the view tips: that half of the
pitch sign came from playing it, after the first build had the look axis
inverted.

**The table can be read straight out of `GAME.EXE`, and the first six entries are
the ones the movement code never touches.** The values are stored in the *game's*
byte order, which is `Hardware.Controller`'s with the halves swapped, because
`BiosB.PadRead` writes `(s >> 8) | (s << 8)` into the pad buffer and libetc hands
the game `~buffer`. Swap back and the whole table decodes:

| entry | value | button | action |
|---|---|---|---|
| `0x8006E568` | `0x0040` | Cross | confirm |
| `0x8006E56C` | `0x0020` | **Circle** | **open the in-game menu** |
| `0x8006E570` | `0x0080` | Square | |
| `0x8006E574` | `0x0010` | Triangle | |
| `0x8006E578` | `0x0100` | Select | |
| `0x8006E57C` | `0x0800` | Start | |

The swap is not a guess: it is what turns entries 12/13 (`0x2000`/`0x8000`) into
Right/Left, which is the turn pair the movement table above already names.

Entry 1 is read at `0x80029D8C`, in `func_80029CBC`, as a just-pressed test
guarding the one `jal` to **`func_80018E80` — the in-game menu**, whose −2 return
is what writes the quit-to-title exit reason. One call site, behind a
just-pressed edge, means that function blocks for the whole menu session rather
than being re-entered per frame.

### Inside the menu: the cursor, its repeat, and its frame head

Found while chasing a cursor that scrolled faster the higher `KF2_FPS` was set.
`docs/TODO.md` still lists the menu's screens as unmapped; these five routines are
the input and frame plumbing under all of them, and are shared by the start menu
as well.

| function | role |
|---|---|
| `func_80018E80` | **the in-game menu** — the modal loop, dispatching a 7-arm jump table at `0x80011098` on a screen index kept in a stack local |
| `func_8001EA14` | the fixed option-list cursor: `(cursor, maxIndex, *selected, *confirmed, *cancelled) -> newCursor` |
| `func_8001EB70` | the scrolling-list cursor, taking a descriptor in `a0` |
| `func_80022E58` | `PadRead(1)`, latching `0x8006E5C4` to 1 whenever anything is down |
| `func_80022E90` | the auto-repeat delay — consumes that latch and spins on up to six `VSync(0)` calls |
| `func_80022530` | the menu's frame head: buffer swap, OT pointer, `ClearOTag`, and the cursor blink |
| `func_800226A8` | the menu's presenter — `DrawSync`, `VSync(0)`, `PutDrawEnv`, `PutDispEnv`, `LoadImage`, `DrawOTag` |
| `func_80022EFC` | wait for every button to come up; the loop behind the menu deadlock in [RUNTIME.md](RUNTIME.md) |

`func_8001EB70`'s descriptor is five bytes, deduced from its up and down arms:

| offset | meaning |
|---|---|
| `+0x1E` | item count (also the "list is non-empty" guard) |
| `+0x1F` | visible window height, in rows |
| `+0x20` | scroll offset — the index of the top visible row |
| `+0x21` | the absolute selected index |
| `+0x22` | the cursor's row within the window |

**Neither cursor edge-detects.** Both call `func_80022E90` and then
`func_80022E58`, and then test Up (`0x8006E590`) and Down (`0x8006E594`) against
the word they just read — so holding a direction steps the cursor once per
iteration of the menu loop, and the repeat delay is the only thing that makes it
usable. That delay is six `VSync(0)` calls, which is 100 ms of vblanks on
hardware and produced 37 steps a second at 144 fps here until
`patches/MenuPacing.cs`; see "The menu's cursor repeat is outside the gate by
construction" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

Two words carry the cursor's blink. `func_80022530` steps `0x8006E5CC` (u32) by
±1 according to the direction in `0x8006E5D0` — 0 counts up, 1 counts down,
anything else is frozen — clamping to 7 at the top and latching `0xFFFFFFFF` at
the bottom, and `func_80021A84` reads the counter as `(v + 0x1F4) << 6` into a
sprite's `+0xE`, which is one of eight cursor frames. `func_8001EA14` zeroes the
direction on every accepted move (restarting the ramp) and `func_80022E90` zeroes
the counter when a repeat fires.

**The bottom latch means it is one wink per move, not a continuous pulse**: after
sixteen steps the direction is `0xFFFFFFFF` and nothing moves until the next
accepted move zeroes it. Measured, sitting still in a menu steps it zero times a
second. And **`func_80022530` runs twice per iteration** of `func_80018E80` — the
menu's inner loop makes two passes and each presents — so the frame-head rate is
the rendered frame rate, which is what put the blink on the render rate until
`patches/MenuPacing.cs` capped it.

### Every control axis has the same three branches

Turn, pitch, forward and strafe are all velocity based and all written the same
way:

```c
if      (pad & maskInc)  vel += rate >> 2;   /* clamped to +rate */
else if (pad & maskDec)  vel -= rate >> 2;   /* clamped to -rate */
else                     vel decays toward 0;
angle_or_position += vel;
```

(pitch steps by a flat 3 to a limit of 32, and the angle itself is held inside
±`0x2BC`; forward decays at `speed>>3` and everything else at `>>2`.)

That shape is the whole reason analog control is cheap here — see "Analog
twin-stick control" in [INPUT.md](INPUT.md).

### The character's stats are buf2, and the memory card is what found them

The position-and-angles block above is the *camera*. HP, MP, EXP and level are
somewhere else entirely, and the route in was the **save title**. Every save's
64-byte Shift-JIS name carries the decimal EXP and level — `ＥＸＰ　３１３　ＬＶ　５`
— so the routine that stamps those digits has to read both from RAM.
`func_80023CC0` is it, and it reads `0x80199414` and `0x8019941C`. That address
is **`buf2`**, the 0x58-byte buffer already in the `memset` list above.

| address | width | what |
|---|---|---|
| `0x80199414` | u32 | EXP |
| `0x8019941C` | u8 | level |
| `0x80199426` / `0x80199428` | u16 | max HP / **current HP** |
| `0x8019942A` / `0x8019942C` | u16 | max MP / current MP |
| `0x801994E1` | u8 | player action state; `0x11` is **dead** |

Which pair is HP and which is MP is not a guess — three things agree:

1. **`func_80024F90(delta)` is "add `delta` to HP".** It reads `0x80199428`,
   and when the sum is `<= 0` it clamps to zero **and calls `func_8002A264(0)`**.
   It touches no other stat, and nothing else in the block has a death branch.
   (It is *an* add-HP routine, not *the* damage path — see "A correction:
   `func_80024F90` is not the damage path" under "Debug tools". `func_80024FE0`
   is what a monster's hit goes through, and it writes HP itself.)
2. **State `0x11`'s handler opens by forcing `*(u16*)0x80199428 = 0`.**
3. **The HUD reads the four in bar order** — current HP, max HP, current MP, max
   MP — which is the order the two bars are drawn in.

Two more shapes in the block confirm the pairing generally: a full heal is
`cur = max` for both pairs written back to back, and a level-up writes the new
maxima and then refills from them.

`func_8002A264` is the death **latch**: it returns immediately if the state byte
is already `0x11`, otherwise sets it, plays sound `(0x0B, 0x6E)` and zeroes two
timers. `func_80024FE0`, the take-damage routine, tests the same byte on entry
and returns early — once you are dead you take no more damage, which is also why
the byte is a safe thing for a mod to key off.

The state byte drives a **jump table at `0x80011300`**, `0x80011300 + state*4`,
states `0x00`–`0x12`, dispatched from stage 3. `0x11` lands at `0x8002ADAC`.
`func_80029E5C` is its inverse: it clears the byte to 0 along with eleven timers,
which is the game's own "back to normal" reset.

### The status screen names the rest of buf2, and the text is a font-index table

The section above got HP, MP, EXP and level out of the save title. The other
twenty-eight words came from the other direction: **the status screen has to draw
a label beside every number it shows**, so the two routines that draw it are a
complete, ordered, self-labelling map of the block.

The route in was the text. `GAME.EXE` holds no English UI strings — a grep for
ASCII finds only PSY-Q's own diagnostics — because the game's text is **indices
into its font**, one byte a character, `0x00` = `A`, `0x7F` = space, `0xFF` =
terminator, in 24-byte records. `0x0B 0x04 0x15 0x04 0x0B 0xFF` is `LEVEL`. Once
that is known the whole table decodes, and it is worth knowing for its own sake:
every item, spell, weapon and armour name in the game is in it, from `0x80065794`
(`EXPERIENCE`) to `0x800663F0` (`LIGHT CRYSTAL`).

Labels reach the drawer two ways, and both are readable statically. A long one is
a pointer into that table; a short one the routine **builds on its own stack**,
`sb` by `sb`, so ` SLASH` is `0x7F 0x12 0x0B 0x00 0x12 0x07 0xFF` written to
`sp+28`. Walking each routine and pairing every label with the next address it
loads out of buf2 gives the map whole:

**Page one, `func_8001F230`:**

| address | width | label |
|---|---|---|
| `0x80199414` | u32 | `EXPERIENCE` |
| `0x80199418` | u32 | the EXP the next level needs — not drawn, but `func_80024CAC` compares against it |
| `0x8019941C` | u8 | `LEVEL` |
| `0x80199426` / `0x80199428` | u16 | `HP`, max then current |
| `0x8019942A` / `0x8019942C` | u16 | `MP`, max then current |
| `0x8019943C` | u16 | `STR POWER` |
| `0x8019943E` | u16 | `MAG POWER` |
| `0x80199468` | s16 | `CONDITION` ▸ `POISON` |
| `0x8019946A` | s16 | `CONDITION` ▸ `CURSE` |
| `0x8019946E` | s16 | `CONDITION` ▸ `DARK` |
| `0x80199472` | s16 | `CONDITION` ▸ `SLOW` |
| `0x80199474` | s16 | `CONDITION` ▸ `PARALYZE` |
| `0x80199440` | u32 | `GOLD` |

`GOOD` is the fifth condition label and has no word of its own: it is what the
screen prints when all five timers are zero.

**Page two, `func_8001FB4C`** — two blocks of one heading each, `OFFENSE` at
`0x800657C4` and `DEFENSE` at `0x800657DC`:

| offense | | defense | |
|---|---|---|---|
| `0x80199444` | `SLASH` | `0x80199456` | `SLASH` |
| `0x80199446` | `CHOP` | `0x80199458` | `CHOP` |
| `0x80199448` | `STAB` | `0x8019945A` | `STAB` |
| `0x8019944A` | `HOLY MAGIC` | `0x8019945C` | `POISON` |
| `0x8019944C` | `FIRE MAGIC` | `0x8019945E` | `DARK MAGIC` |
| `0x8019944E` | `EARTH MAGIC` | `0x80199460` | `FIRE MAGIC` |
| `0x80199450` | `WIND MAGIC` | `0x80199462` | `EARTH MAGIC` |
| `0x80199452` | `WATER MAGIC` | `0x80199464` | `WIND MAGIC` |
| | | `0x80199466` | `WATER MAGIC` |

All seventeen are `u16`. The offense block has no poison or dark entry and the
defense block has no holy one, which is why they are eight and nine rather than
a matched pair.

### Nineteen of those words are a cache, and `func_800244CC` owns all of them

The important finding is not the map but the **split inside it**, and it is
`func_800244CC` that draws the line. The routine opens by storing zero to all
seventeen offense and defense words in one unrolled run, copies `0x80199438` into
`0x8019943C` and `0x8019943A` into `0x8019943E`, subtracts 20 from the first if
`0x8019946A` (`CURSE`) is non-zero, and then adds each equipped item's
contribution through seven calls to `func_800243B0`.

So:

- **`0x80199438` and `0x8019943A` (u16) are the real attributes** — base strength
  and base magic. They are what a level-up raises, they are what the save carries,
  and nothing recomputes them.
- **`STR POWER`, `MAG POWER` and the seventeen ratings are derived**, rebuilt from
  the equipment every time `func_800244CC` runs. Twelve sites call it: equip and
  unequip (`func_80024B7C`, `func_80024C14`), item use (`func_80025FD0`,
  `func_80026210`, `func_8002658C` twice), the level-up (`func_80024CAC`), the
  post-load fixup (`func_800240B8`), the new-game setup (`func_800253F0`), the
  status screen's own opener (`func_800197D4`), and **four sites inside stage 3**.

That is the fact anything editing a character has to respect: writing `STR POWER`
lasts until the next equip and no longer, while writing base strength is
permanent and shows the moment `func_800244CC` next runs. See "Editing the
character" under "Debug tools".

### `func_80024CAC` is the level-up, and it is callable

`func_80024CAC(s16 gain)` is the whole of levelling, and it is short enough to
state:

```c
exp += gain;  if (exp > 999999) exp = 999999;   /* 0x80199414 */
if (exp < *(u32*)0x80199418) return;            /* not there yet */
if (level == 255) return;                       /* 0x8019941C */
level++;
if (level < 100) {
    maxHp += table_delta;  maxMp += table_delta;     /* 0x80199426, 0x8019942A */
    baseStr += table_delta;  baseMag += table_delta; /* 0x80199438, 0x8019943A */
    *(u32*)0x80199418 = next threshold from the table at 0x8007xxxx;
}
```

Every constant it adds comes from a table in `GAME.EXE`'s data, so **calling it is
strictly better than imitating it**: top `0x80199414` up to the threshold word,
pass a gain of zero, and it levels exactly once by the game's own numbers. That is
what the debug mod's "Level up" button does. Note the `< 100` guard — past level
99 the level byte still increments and nothing else does.

## Saving and loading

Both halves of the memory card now work. Saving was confirmed first — three files
written to `carda.sav` across a 30-minute session — and **loading one back has now
been confirmed too**: slot 2, entered from the title screen, came up in the right
area with the right character state, and the process ran on past it without an
`unmapped call`, a VRAM collision or an exception.

Loading is worth calling out separately because it is *not* the same path as
saving. It reaches an area module from the **title screen** rather than from an
area already running, so it drives the module load described in "GAME.EXE loads
code" (in [RECOMPILATION.md](RECOMPILATION.md)) from a cold start rather than as
a swap over a resident module.

The card image is a stock PS1 memory card and can be read without the game
running, which makes it the cheap way to check what is on it:

```
$ python3 - <<'EOF'
d = open('carda.sav','rb').read()            # 131072 bytes, block 0 = directory
for i in range(1, 16):                        # 15 dir entries, 128 bytes each
    e = d[i*128:(i+1)*128]
    if int.from_bytes(e[0:4],'little') == 0xA0: continue      # free
    print(i, e[10:30].split(b'\x00')[0].decode())             # filename
for blk in (1, 3, 5):                         # each save's first block
    print(d[blk*8192+4:blk*8192+68].split(b'\x00')[0].decode('shift_jis'))
EOF
BASLUS-001581 / BASLUS-001582 / BASLUS-001583
ＫＩＮＧ’Ｓ　ＦＩＥＬＤ　２−１　ＥＸＰ　　　３１３　ＬＶ　５
ＫＩＮＧ’Ｓ　ＦＩＥＬＤ　２−２　ＥＸＰ　　　２６１　ＬＶ　４
ＫＩＮＧ’Ｓ　ＦＩＥＬＤ　２−３　ＥＸＰ　　　２９１　ＬＶ　５
```

Three saves, each two blocks (16 KB), `state=0x51` first-block-in-use, `0x53`
continuation — all well-formed. The trailing `2−N` in the title is the **slot
number, not the area**; the level and EXP are the only per-save state visible from
outside. Titles are full-width Shift-JIS even in the NTSC-U release, which is what
a straight localization of the Japanese KFII would look like.

**`carda.sav`'s mtime is useless as evidence.** It is rewritten at process start,
not only when the game writes a save, so a fresh timestamp means the port booted
— nothing more. Check the directory entries or the titles instead.

### The card code is all in GAME.EXE, and loading is one call

`OPEN.EXE` has **no card code at all** — no `BASLUS`, no `bu00:`, no `cdrom:`
string anywhere in it. Everything works off the filename template `BASLUS-00158`
at `0x80067564` and the title template at `0x80067574`, both in `GAME.EXE`:

| function | role |
|---|---|
| `func_80023638(slot)` | **load** |
| `func_80023764(...)` | save — builds the `SC` header, the title, the filename |
| `func_80023CC0(hdr, slot)` | stamps the slot, EXP and level into the title |
| `func_8001B4F4` | the load-slot menu; `func_8001B35C` is the start menu above it |
| `func_80023DD0(buf)` | the checksum |
| `func_8004A040(buf)` | unpacks a loaded block into game state |

`func_80023638` is short enough to state whole: build
`bu00:BASLUS-00158<slot+'0'>`, `open` it (retrying up to three times), read
`0x4000` bytes into `*(u32*)0x8006E98C`, checksum `buf+0x400` and compare against
`buf+0x200`, unpack with `func_8004A040`, record the slot, return **0 / 1 (no
file) / 2 (bad checksum)**. On a non-zero return the unpack never ran, so a
failed load leaves game state untouched.

**`0x8006E5D4` (u8) is "the current save slot".** Both the load and the save
write it, and it is zero in the executable image, so zero means neither has run.
Slots are 1–3; the `2−N` in a save's title is this number.

### The game can load a save without leaving the area

Worth knowing before building anything that reloads: `func_80029CBC` handles the
in-game menu's result, and its `-3` arm — "the menu loaded a save" — is twelve
instructions at `0x80029E0C`:

```c
func_800240B8();                    /* post-load fixup: music, equipment, and
                                       func_80023FCC to re-zero the deltas    */
area = *(u8*)0x8017E060;            /* the loaded save's area, in buf0        */
func_80024154(area, area, area, area, /*sp+0x10*/ area, /*sp+0x14*/ 0xFF);
func_80025D38();
```

So a complete reload from anywhere is `func_80023638(slot)` followed by that,
with **no overlay swap and no title screen**. That is what the `autoreload` mod
does; see "Auto reload" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

`func_80029CBC` itself is dispatched only from the state machine's arms for
states 1 and 2, so it stops being called the moment the player dies — anything
that wants to run at that point has to hook stage 3 instead.

### The boot stub's handoff, and the other way back to the title

Found while looking for a return-to-title path, and worth having written down
even though the mod does not use it.

`SLUS_001.58` picks its next executable from `*(u32*)0x80010268`, an index into
the filename table at `0x80010254` — **0 = `OPEN.EXE`, 1 = `GAME.EXE`,
2 = `END.EXE`**. After each `Exec` returns it refills that index from
`*(u8*)0x800102F0` (reached through the pointer at `0x8001026C`).

`GAME.EXE` is the only executable that writes it, from the tail of the main loop
at `0x800139CC`–`0x800139EC`, off an **exit-reason global at `0x80199574`**:

| `*(u32*)0x80199574` | effect |
|---|---|
| `0` | keep looping — this is the normal state |
| `1` | `*(u8*)0x800102F0 = 2` → `END.EXE` |
| `9` | `*(u8*)0x800102F0 = 0` → `OPEN.EXE`, and `*(u8*)0x800102F8 = 1` |
| anything else | no write; the index stays `1`, so `GAME.EXE` re-execs |

`*(u8*)0x800102F8` is read by `OPEN.EXE` at `0x80011BCC` and **skips the three
intro movies** when it is 1 — the only thing `OPEN.EXE` ever reads from the stub.
The in-game menu's "quit" is what writes 9, via `func_80018E80` returning −2.

What `END.EXE` then does, and why it needed a patch of its own, is "The ending
screen" in [RUNTIME.md](RUNTIME.md).

## Debug tools

`mods/kf2debug` is noclip flight, invincibility, infinite MP, a speed multiplier,
position bookmarks and area warp, behind a dockable panel and a set of hotkeys.
Most of it is a front end for addresses already in "Player state" and "The
character's stats are buf2" — but building it turned up seven things those sections
did not have, and one correction.

### The player's own movement is three routines, not one

Stage 3's `func_800290D4` is **only velocity bookkeeping**. It never writes the
position. The three that do:

| function | what it writes | called from |
|---|---|---|
| `func_80028080` | X and Z — the horizontal move and the **wall slide** | `func_800290D4`, up to three times a frame with `yaw`, `yaw+0x800`, `yaw±0x400` |
| `func_80028560` | Y only, eight sites — **gravity, the floor clamp and the step** | **stage 3 directly**, not through the walk; every walking arm converges on it |
| `func_80028B0C` | all three — the thrown/knockback mover | stage 3's knockback states |

`func_80028080` commits only when `func_8002C700` returns zero, and latches the
surface id into `0x8019953C` when it does. `func_80028560` opens with
`func_8002C330` on the position triple and integrates against the fall velocity
at **`0x8019954E`**.

**`func_80028560` is also where the game kills you**, which is what makes it worth
knowing: it carries fall damage (`func_80024FE0`), the bottomless-pit check
(`func_800284BC`) and the crushed-or-below-the-floor check (`func_80023ECC`).

### `func_8002C3A8` is the floor-height query

`func_8002C3A8(mode, x, z, radius, /*sp+0x10*/ height)` returns the ground `Y` for
an X/Z column. Two independent call sites prove it: `func_80025DA8` calls it with
the player's own position, radius `0x320` and height `0x6A4` and stores the result
straight into the height word, and the entity code at `0x800355F0` calls it the
same way and adds a per-object offset. It is the game's own "put this thing on the
floor", and it is what the debug mod's "snap to floor" uses.

### Death has exactly one chokepoint

**`func_8002A264` is the only writer of state `0x11` in the whole game.** Seven
callers, and only three of them are about HP:

| caller | condition |
|---|---|
| `func_80024F90` | HP reached 0 |
| `func_80024FE0` | HP reached 0 |
| `func_8002A3DC` | HP reached 0 |
| `func_80023ECC` ×2 | crushed against the ceiling, or below the floor |
| `func_800284BC` | bottomless pit — `floor − Y > 32000`, and it also sets `0x801994E9 = 1`. Called unconditionally from `func_80028560`'s falling arms, so the drop distance is the whole test |
| `func_80028B0C` | the same pit test in the thrown path |

**The last four kill you at full HP**, so nothing that watches `0x80199428` sees
them coming. A `[PreHook]` returning `false` on `func_8002A264` is therefore the
whole of invincibility in one hook; the HP-side hooks only exist to stop the bar
visibly draining.

### A correction: `func_80024F90` is not the damage path

"The character's stats are buf2" presents `func_80024F90` as *the* add-HP routine.
It is one of three, and it is not the one a monster's hit takes:

- `func_80024F90(delta)` — **one** call site, inside stage 3, always `delta = -1`.
  The poison/starvation tick.
- `func_8002A3DC(delta)` — the same shape but clamped to max HP as well. The
  per-tick equipment regen/drain, called with `+1` and with `-1`; **its `-1` arm
  reaches neither of the other two**.
- `func_80024FE0(a0, dmg, flags)` — **two** call sites and the real one: every
  weapon, trap and fall. It computes `hp - dmg`, clamps at zero and writes
  `0x80199428` itself, never through `func_80024F90`.

`func_80024FE0`'s second branch is `if (dmg == 0) goto <tail>`, which skips the
subtraction, the clamp and the store while still running the hit reaction — so
zeroing the *argument* blocks a hit without making it look like it missed.

HP is written from ten sites in the direct form alone, spread over `GAME.EXE`,
`fdat05` and `fdat11`, and most of them are *heals* — items, spells, rest, the
level-up refill, area scripts. That is why a `PSMemory.Freeze` on the HP word is
the wrong tool: it would drop every one of them silently, and the failure would
read as a bug in the game.

```bash
grep -n "WriteU16((c\.\w* - 0x6BD8u)" generated/*.cs   # direct form
grep -n -- "- 0x6BD8u;" generated/*.cs                 # register-base form
```

### The rate words are written before they are read, inside stage 3

`0x80199558` (walk speed, `0xC8`) and `0x8019955C` (turn rate, `0x1C` moving /
`0x23` standing) are written **early in stage 3's own body**, at `0x8002A6E4` and
`0x8002A724` — *before* it dispatches to `func_80028DB8` (turn) and then
`func_800290D4` (walk). Anything scaling them has to sit between, and ahead of the
turn: hooking the walk scales the turn rate after the turn has already used it,
and stage 3 overwrites it before the next one. One pre-hook on `func_80028DB8`
covers both consumers, since nothing writes either word in between.

### Nothing after stage 3 writes the position

Taking the static call closure of each of the thirteen main-loop stages and
intersecting it with the set of routines that write `0x801994EC/F0/F4`:

```
stage 2  func_80037C0C   writes X/Z (area-transition placement)
stage 3  func_8002A550   every player-side writer there is
stage 4  func_80040348   none in its whole subtree -- it only READS the position
stage 7  func_8001689C   the area loader
stage 13 func_800342D8   none in its whole subtree
```

So a `[PostHook]` on stage 3 is the last word on both the position and the
composed view angles, and that is where the debug mod writes. Stage 2 runs before
stage 3 in the same iteration, so it loses. A pre-hook on the renderer would be
worse, not safer — `func_800342D8` has two dozen call sites and fires repeatedly
during menus and transitions.

The exception is **area 7 (`fdat23`)**, whose scripted sequences displace the
position themselves and call the renderer directly to draw their own frames.
Stage 3 is not running during those, so a position hook is simply suspended for
the cutscene.

### `func_80024154`'s six arguments

It is a wrapper around three calls to `0x800162DC`, which is "request these five
resource slots", where **`0xFF` in a slot means keep the current one**:

| arg | slot | when |
|---|---|---|
| 1 | 0 — **the area index** | first pass |
| 2 | 1 | first pass |
| 3 | 2 | first pass |
| 6 | 2 again, on its own | intermediate pass, **skipped entirely when `0xFF`** |
| 4 | 3 | final pass |
| 5 | 4 | final pass |

Slot 0 is the area index, and the evidence is `0x800162DC`'s own
`if (*(u8*)0x801B3088 < s4) *(u8*)0x801B3088 = s4` — a furthest-reached
high-water mark, which only makes sense for an area. `s4` is the first argument:
it is `s1 = a0` on the path that takes a new area, and the currently-loaded area
byte `*(u8*)0x8017E060` on the path where the caller passed `0xFF` to keep it.

`func_80024154(area, area, area, area, area, 0xFF)` is not a guess either:
`func_800474D0`, the game's own door warp, calls `0x800162DC` twice with exactly
that split.

**Valid areas are 0–7.** `FDAT.T` is groups of three with the code module at
`3n+2`, and the modules are entries 2, 5, 8, 11, 14, 17, 20, 23 — areas 0–7.
Entries 24–29 are zero length, so areas 8 and 9 do not exist, and area 10 is
entry 32, the cut one.

### The loading screen is a blocking wait, and its figure is three words

A disc read never returns to the main loop. `func_80024154` calls
**`func_80017CA8`**, which spins `func_80017818(); if (*(u8*)0x800864DC)
func_8001883C() x4;` until the CD job queue at `0x801B6F44` drains;
**`func_800181B0`** is the other shape, `CdRead` then a spin on `CdReadSync`
calling `func_8001883C` once an iteration.

**`func_8001883C` is the loading screen's animator** — the little figure that
walks across the screen while an area loads. It draws with no ordering table at
all: `ClearImage` (`0x800605D4`) over the rectangle the sprite occupied, then
`MoveImage` (`0x8006069C`) from a 32x32 source in VRAM at (`0x3C0`, `0x1C0`/
`0x1E0`) to the new position. Its whole state is three globals, and they are the
only ones it writes:

| address | width | what |
|---|---|---|
| `0x8006E5A4` | u32 | frame counter, `++` as the function's last act; the sub-steps are gated on `& 3` and `& 7` of it |
| `0x8006E5A8` | u32 | the sequence's state; `state - 5 < 0x16` skips the walking branch |
| `0x8006E5AC` | u32 | the figure's **x**, `+= 3` while state < 5 and `+= 5` once state is past the middle band, only while it is under 288 |
| `0x8006E5B0` | u32 | its y — read here, written elsewhere |

They sit in the same block of GAME.EXE globals as the menu cursor's blink
(`0x8006E5CC`/`0x8006E5D0`).

The function ends in `DrawSync(0); VSync(0)`, so **one call was one vblank**, and
the drain loop's five vblanks an iteration for four steps is where the console's
48 steps a second comes from. That is what `patches/LoadPacing.cs` restores; see
"The loading screen's walking figure" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

### Noclip's hard limit is the loaded area

Flying far enough leaves the geometry the game has in RAM, and the crash is
immediate and specific: `func_80032CD8` reads an object pointer from the table at
`0x8018E1A0` for an index past its 104 static entries, gets a stale word, and
`func_80017764` dies dereferencing it — reached from the renderer as
`func_800342D8 → func_800331B4 → func_80032CD8`.

Nothing a mod does from outside fixes that; the neighbouring area's module and
data are simply not loaded. **Area warp is the supported way to change area**, and
the mod says so where it will be read. The first build also skipped
`func_800290D4` and `func_80028560` while flying, which was a mistake for a
different reason: those routines are the engine's own bookkeeping, and switching
them off makes it less self-consistent, not more. They now run and the mod
overwrites the result — which means the flight has to keep its **own**
authoritative position, since the floor clamp rewrites `Y` every frame and
integrating from memory would leave you hovering a step above the ground instead
of climbing.

### Area warp cannot run from the panel

The first build called `func_80024154` from the Warp button. That is
`IPanel.Draw`, which runs inside `HostWindow.Present`, which runs from `VSync`.
`func_80024154` waits for the CD by looping `func_80017818`, and that calls
`VSync`. So the button nested `DoRender` / ImGui, and once the overlay swapped it
returned to a frame still drawing the area that had just unloaded.

The same six-argument call from a `[PostHook]` on stage 3 is the game's own load
path (`func_80029CBC` at `0x80029E0C`, and `patches/AutoReload.cs`) and is safe: VSync
is then a child of the game loop, not of Present. The panel only queues the
index; the hook does the work.

A second failure survives that move. `func_80024154` floor-snaps at the current
X/Z via `func_80025DA8`. The 80×80 tile map at `0x801C8484` is indexed by
`X>>11, Z>>11` with no clamp, and the renderer then walks object indices for
tiles the new module does not own — the same death as flying out of the loaded
geometry. After the load, the warp sits the player on the centroid of the new
area's object table (`0x80177714`, `0x44`-byte records, empty when `+4 == 0xFF`)
and runs `func_80025DA8` there. A noclip position that has already left the tile
map is parked on the new-game spawn (`func_80025B4C`: `0x11800, -12800, 0x18000`)
for the duration of the load, so the first snap cannot OOB. Empty tiles in the
new map can still trip the below-floor latch; `func_80029E5C` clears it, the same
way autoreload does when it arrives from state `0x11`.


### Editing the character: the split is the design

`mods/kf2debug`'s Attributes tab writes every stat the status screen shows, and
almost all of the thought in it went into one distinction, which is the one
"Nineteen of those words are a cache" draws.

**The character is plain memory.** EXP, the next-level threshold, level, HP and
MP with their maxima, gold, base strength, base magic and the five condition
timers are written and stick. `func_80049A88` packs every one of those addresses
into the save block, so an edit survives a save and a load. Nothing here needs a
hook: the panel writes from the UI thread the same way the warp tab's coordinate
boxes already do.

**The nineteen ratings are not**, and offering an edit box over them without
saying so would be a lie — `func_800244CC` runs on equip, on item use, on load,
on level-up and from four sites inside stage 3, and each run zeroes them. So the
tab holds them behind a switch (`Hold these values`, off by default) whose
mechanism is a `[PostHook]` on `func_800244CC` itself. A post rather than a pre,
because the point is to overwrite what it just wrote, and hooking the recompute
rather than the twelve callers means equip, unequip, load and level-up are all
covered by one hook. Switching the hold on primes it from live memory first, so
turning it on changes nothing until a number is typed.

The honest control is the pair above it: **base strength and base magic are real
attributes**, they persist, and the recompute picks them up. The tab says which
is which rather than presenting nineteen boxes as if they were all the same kind
of thing.

Two buttons run recompiled MIPS instead of writing memory, and both are **queued
and run from the stage-3 post hook** for the reason "Area warp cannot run from
the panel" gives — the panel draws inside `Present`, which is inside `VSync`.
"Level up" calls `func_80024CAC` (see above: EXP topped up to the threshold, gain
of zero, one level by the game's own table), and "Recalculate" calls
`func_800244CC` so an edited base attribute reaches the status screen without
waiting for an equip. Both snapshot and restore the `CpuContext` around the call,
which is `patches/AreaWarp.cs`'s idiom.

**None of this has been looked at by eye.** The addresses and the routine
behaviour are read out of the executable and the code compiles; what a person
still has to check is that the status screen shows the numbers the panel does,
that "Level up" lands on the same level and maxima the game would have given, and
that a held rating is actually felt in combat rather than merely displayed.
