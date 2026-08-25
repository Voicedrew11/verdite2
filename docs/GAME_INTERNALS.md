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
  words. Whatever it does, it does somewhere else.

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
| stage 2 `func_80037C0C` | yes, unaudited in detail | **yes** — all four entry points are in its 268-function subtree |
| stage 13 `func_800342D8` | the jitter accumulator at `0x8006E608`, in its own body | yes, it is the renderer |
| — `func_80033FBC` | the fade state machine, called by stage 13 | **no** — three functions, none of them draw |

**Stage 3 reaches the drawing entry points, but not the way stage 2 does**, and
the distinction is what makes gating it safe. The path is
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

So the composed angles and the position triple are read *once* per frame, by one
function, and nothing between there and the picture reads them again. That makes
stage 8 the only place a port can move the camera without moving the game: a pre
hook that nudges those globals and a post hook that puts them back is visible to
the renderer and to nothing else. `patches/FrameSmoothing.cs` is that hook.

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

