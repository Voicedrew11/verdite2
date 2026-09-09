# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A static recompilation of **King's Field (NTSC-U, `SLUS-00158`)** — the North
American release of the Japanese *King's Field II* (`SLPS-00069`) — using
[RecompOne](https://github.com/BlackLabelHQ/RecompOne). The series was renumbered
for the West: the US-boxed "King's Field II" (`SLUS-00255`) is a *different game*
and every address here is wrong for it.

There is no decompilation, no ELF and no `.map`. Function boundaries come from a
linear sweep and PSY-Q library functions are identified by hand, so most work in
this repo is *reverse engineering*, not application coding: find an SDK function's
address in the disc image, map it to the runtime's HLE implementation, re-run the
recompiler, run the game, read the logs.

**`NOTES.md` is the index, and `docs/` is the working log.** `NOTES.md` carries
what the project is and where it stands, plus a map of the nine documents under
`docs/` and the exact section titles in each. Read the index before starting
anything, then the one or two documents your task touches — they are split by what
you would be doing when you need them:

| file | when |
|---|---|
| `docs/DEVELOPMENT.md` | build, run, diagnose, measure |
| `docs/RECOMPILATION.md` | config, overlays, function maps, SDK addresses |
| `docs/RUNTIME.md` | interrupts, HLE, the `patches/recompone/` stack |
| `docs/RENDERING.md` | perspective correction, sub-pixel, Z-buffer, dither |
| `docs/WIDESCREEN.md` | aspect ratio, the HUD, the three culls |
| `docs/GAME_INTERNALS.md` | the game's own addresses and routines |
| `docs/PATCHES_AND_MODS.md` | hooking, settings UI, frame pacing, auto reload |
| `docs/INPUT.md` | pad, sticks, keyboard, mouse |
| `docs/PACKAGING.md` | the redistributable: the launcher, the first-run build, CI |
| `docs/TODO.md` | next steps and open, undiagnosed questions |

Update the right document when you learn something — that is where findings
belong, not in commit messages. Source comments still say `See "X" in NOTES.md`,
and **the text they name is not in `NOTES.md` any more** — the titles are
unchanged, so the index resolves X to a document, but it is a hop rather than a
direct hit. Grep `docs/` for the title, not `NOTES.md`.

## Build and run

Nothing here builds without the disc (gitignored, `disc/KingsField2.cue`).
`tools/RecompOne` is **vendored** — its sources are tracked here, so a fresh
clone already has it and nothing needs cloning.

```bash
bash scripts/setup_tools.sh          # build the vendored recompiler

# recompile MIPS -> C# into generated/ (~2099 functions, ~163k lines)
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build -- config/kf2.json

dotnet build KingsField2Recomp.csproj -c Release
dotnet run --project KingsField2Recomp.csproj -- disc/KingsField2.cue
```

`setup_tools.sh` builds; `--sync-upstream` starts the next three-way merge from
upstream, and `--signatures` fetches the 15.7 MB PSY-Q bank (gitignored, read
only by the standalone `--autoconfigure`). The cue path is needed at *play* time
as well as at recompile time.

There are no tests. Verification is empirical: run the game with log channels on
and check the trace against what the SDK sequence should look like (see the
steady-state `KF2_LOG=sdk` excerpt under "Status" in `NOTES.md`).

**Anything that has to be judged by eye is the user's job, not yours.** Do not
try to capture, screenshot or otherwise scrape the game window — it burns a lot
of context and produces nothing a person could not tell you in one sentence.
Measure what a counter can measure, then say plainly what still needs looking at
and ask. That distinction is already all over `docs/`, which repeatedly
separates "mechanism measured" from "picture never checked"; keep writing it down
that way.

### Diagnostics

```bash
KF2_LOG=bios,cd,gpu,dma,sdk,spu,mdec  # or KF2_LOG=all; wired up in Program.cs
KF2_CDTRACE=1                          # stack trace on first CD register access (patch 0002)
KF2_AUTOPAD=8:Start:400,20:Circle:200  # scripted pad input: seconds:button:holdMs
KF2_FPS=120                            # 20 (default), any number, or off; see "Any frame rate"
KF2_TICKRATE=30                        # ticks a second the world runs at (20, and no longer a setting)
KF2_FPS_GATE=80037C0C+8002A550+80040348+80046A60+8004910C+80033FBC+8002DC78  # what is ticked
KF2_FPS_LOGIC=full                     # no gating; scale the movement deltas instead
KF2_FPS_PROBE=1                        # a line a second: fps drawn, ticks taken, and what each smoother is doing
KF2_MENUPACING=0                       # menu cursor repeat and blink back on the frame clock (on by default)
KF2_MENUPACING_PROBE=1                 # what each repeat cost, and the blink's step rate
KF2_LOOPPACING=0                       # loops that render their own frames back on the render rate (on by default)
KF2_LOOPPACING=pace                    # hold such a loop but do not redraw: right speed, tick-rate picture
KF2_LOOPPACING=nocarry                 # redraw, but do not carry a view the loop pans itself
KF2_LOOPPACING_PROBE=1                 # modal frames a second, world and interface, against the main loop's
KF2_LOOPPACING_PROBE=2                 # also how far the loop's own view moves per iteration
KF2_LOADPACING=0                       # the loading screen's walking figure back on the host ceiling (paced by default)
KF2_LOADPACING_PROBE=1                 # its steps a second, and what one load cost
KF2_SPRITEANIM=0                       # billboard sprite animation back on the render rate (paced by default)
KF2_SPRITEANIM_PROBE=1                 # cel changes a second, live slots, and how many walks stepped
KF2_RATECENSUS=1                       # rank memory by whether it moves at the render rate
KF2_RATECENSUS_RANGE=80060000:801C0000 # the window to watch (this is the default)
KF2_RATECENSUS_OUT=path KF2_RATECENSUS_PERIOD=5   # where to dump, and how often
KF2_SMOOTH=1 KF2_SMOOTH_POS=1          # carry the view between ticks (off by default); carry position too
KF2_SMOOTH_PROBE=1                     # how far the view is being carried, per second
KF2_SMOOTH_OBJECTS=1                   # carry enemies, doors and everything else that moves (off by default)
KF2_SMOOTH_OBJECTS_PROBE=1             # how much is being carried, per second
KF2_SMOOTH_OBJECTS_GUARD=continuous    # strict|sticky|continuous: what counts as a placement (creatures)
KF2_SMOOTH_ANIM=1                      # carry MO pose between ticks (off by default)
KF2_SMOOTH_ANIM=time                   # lerp the clip time between the two ticks (the default mode)
KF2_SMOOTH_ANIM=timeline               # comparison: interpolate on the clip's own timeline
KF2_SMOOTH_ANIM=weight                 # comparison: the blend weight only, inside the game's segment
KF2_SMOOTH_ANIM_PROBE=1                # morph vs rigid submits, the verdict census, carries
KF2_HITGUARD=0                         # let the hit path's reaction lookup fault (it is fenced by default; docs/TODO.md #14)
KF2_CRASHDUMP=0                        # no game-state dump on an unhandled exception (it dumps by default)
KF2_HITPROBE=1                         # census what the hit check saw; =2 every call
KF2_DRAWCENSUS=1                       # which renderer routine drew how much of the frame; =2 names the models
KF2_TEXPROBE=1                         # textured vs flat prims a second, and a per-page VRAM census, into texprobe.log
KF2_WIDESCREEN=16:9 KF2_WIDESCREEN_PROBE=1  # aspect (4:3 by default), and the margin census
KF2_WIDESCREEN_PROBE=2                   # the census plus every wide primitive, once per shape
KF2_WIDESCREEN_EFFECTS=0                 # leave the death fade and damage flash 320 wide (stretched by default)
KF2_WIDESCREEN_HUD=1                     # anchor the HP/MP panel and icons to the new edges (off; no longer a setting)
KF2_WIDESCREEN_CULL=0                    # leave the game's view cone at its 4:3 shape (widened by default)
KF2_WIDESCREEN_CULL=1.5                  # pin a widening factor instead of the aspect's
KF2_WIDESCREEN_CULL_PROBE=1              # tiles lit, and what the 24x24 grid clipped
KF2_WIDESCREEN_CULL_PROBE=2              # also lit-per-ring after the occlusion flood
KF2_PRIMBUF_PROBE=1                      # the frame's primitive budget: peak, capacity, overflows
KF2_VIEWCLIP=0 KF2_VIEWCLIP_PROBE=1      # the game's view-space clip volume, and where it cuts
KF2_NODITHER_PROBE=1                   # where the dither bit comes from, and GPUSTAT bit 9
KF2_TRUECOLOR=1                        # 24-bit shaded output, no 15-bit banding (off by default; GL backend only)
KF2_VRAMSNAP=0                         # a menu restores the frozen frame at 1x again (the scaled copy is kept by default)
KF2_VRAMSNAP_PROBE=1                   # frame restores served from that copy, against uploads that missed
KF2_VSYNC=block                        # upstream's blocking vblank timeline instead of the port's grid (caps the picture at 60)
KF2_PERSPECTIVE=0                      # affine textures again (correction is on by default)
KF2_PERSPECTIVE_PROBE=1                # the GTE vertex map's hit rate
KF2_PERSPECTIVE_FALLBACK=1             # also guess by screen position on a miss (the old mechanism)
KF2_SUBPIXEL=1                         # sub-pixel vertex positions (off by default)
KF2_SUBPIXEL_PROBE=1                   # how far vertices actually move, in pixels
KF2_PGXP=1                             # upstream's PGXP as the vertex source (off; the address map answers)
KF2_PGXP_TEXTURE=0                     # its share of perspective correction off
KF2_PGXP_CULLING=0                     # leave backface culling on truncated positions
KF2_PGXP_CPU=0                         # no per-instruction register tracking (and so no RAM shadow)
KF2_PGXP_MEMORY=0                      # no RAM shadow
KF2_PGXP_VERTEXCACHE=0                 # no screen-position fallback
KF2_PGXP_CACHEW=0                      # let that fallback answer positions but not depths
KF2_PGXP_TOLERANCE=2                   # how far a recovered position may sit from the packet's; -1 off
KF2_PGXP_PROBE=1                       # its coverage, and where each answer came from
KF2_ZBUFFER=1                          # per-pixel occlusion from GTE depth (off by default)
KF2_ZBUFFER_THRESHOLD=300              # restart the depth buffer when the scene jumps forward (0, off)
KF2_ZBUFFER_PROBE=1                    # how many triangles actually depth-tested
KF2_ZBUFFER_PROBE=2                    # the frame's polygon census, and a map of the depth buffer
KF2_ANALOG=0                             # twin-stick control off (it is on by default)
KF2_ANALOG_TURN=1.0 KF2_ANALOG_MOVE=1.0 KF2_ANALOG_DEADZONE=0.15  # its sensitivities
KF2_ANALOG_INVERTY=1 KF2_ANALOG_PROBE=1  # look-Y inversion, and the control-state report
KF2_KEYS=stock                           # RecompOne's own key bindings; the port ships WASD
KF2_MOUSE=1                              # mouse look (off by default; Escape captures the pointer)
KF2_MOUSE_TURN=1.0 KF2_MOUSE_LOOK=1.0 KF2_MOUSE_INVERTY=1   # its sensitivities and look-Y
KF2_MOUSE_BUTTONS=Square,Triangle,Cross  # left, right, middle, as pad buttons
KF2_MOUSE_KEY=Escape                     # the key that captures and releases
KF2_MENUMOUSE=0                          # the menu pointer off (on by default)
KF2_MENUMOUSE_PROBE=1                    # the layout table, the pointer's row, and what it did
KF2_AUTORELOAD=1 KF2_AUTORELOAD_SLOT=0   # reload the last save on death
KF2_AUTORELOAD_DELAY=2.0                 # seconds of the death first (2.0; no longer a setting)
KF2_AUTOSTART=2                          # boot straight into save slot 1..3, past the title menus
KF2_BOOTEXE=end                          # boot straight into OPEN.EXE, GAME.EXE or END.EXE
KF2_ENDINGEXIT=0                         # leave "The End" hanging, as the original does (a button exits by default)
KF2_AGENT=1                              # [KF2-AGENT] state lines on stdout: overlay, inGame, HP/MP/area/slot
KF2_SHELL=1                              # TCP 127.0.0.1:27900 line protocol: state|nearby|load|warp|press|kill
KF2_UISCALE=1                            # force the interface scale, and save it
KF2_MAP=0                                # the map off entirely (on by default); M opens it
KF2_MAP_MINIMAP=1                        # the corner minimap on (off; no longer a setting); N toggles it
KF2_MAP_MARKERS=1                        # creatures, objects, effects and sprites on (off; no longer a setting)
KF2_MAP_STYLE=blueprint                  # the port's original blueprint plan (the game's own map; no longer a setting)
KF2_MAP_SHADE=1                          # colour each tile by its height byte (off; no longer a setting)
KF2_MAP_WALLS=1                          # tint the tiles whose +4 bit 0x80 is set (off; no longer a setting)
KF2_MAP_PAUSE=0                          # leave the world running while the full map is up (it pauses; no longer a setting)
KF2_MAP_FLOOR=lower                      # pin a stacked half instead of following the player (no longer a setting)
KF2_MAP_ARROW=1                          # the player as an arrow with a heading, not a dot (no longer a setting)
KF2_MAP_PROBE=1                          # dump the 80x80 tile grid as ASCII, its occupied extent and a marker census, and open both maps
KF2_MAP_FOG=1                            # fog of war: only the tiles you have seen (off by default)
KF2_MAP_FOG_LOS=0                        # its line-of-sight gate off (on; no longer a setting)
KF2_MAP_FOG_PROBE=1                      # tiles seen, tiles lit now, tiles refused, records, flushes
KF2_MAP_FOG_PROBE=2                      # also the raw 24x24 grid, the gate's verdict and its walls
```

Patch settings live in `patches/settings/`. A patch registers an `IPatchPage`
against one of the runtime's own settings sections —
`PatchSettings.Register("display", new FramePacingPage())` — and is drawn inside
it, so the frame rate and the dither switch sit in System ▸ Settings ▸ Video
beside vsync rather than in a panel of their own. That is where a mod's
`DrawSettings` body goes when the mod becomes a patch. Pages that give the same
`Title` share one heading, so single checkboxes group under "Enhancements"
instead of each getting a rule of its own; `IPatchPage.Order` (defaulted to 0)
decides the order and the title is only the heading, which is what stopped
Video's group order being an accident of `E` sorting before `F` — pages sharing
a title need adjacent orders or the heading is drawn twice. Video reads *Frame
pacing* (the rate, then the smoothing tick that is inert below it) and then
*Enhancements* (perspective, sub-pixel, shading). That section is the runtime's
`display` — still that id everywhere in code; the port renames only its *label*,
through `Localization.Merge`, which needs no patch to the checkout. See "Patch
settings" in `docs/PATCHES_AND_MODS.md`.

**Input is the one pane the port takes over outright.** Everything above joins a
runtime section through `Extend`, which can only append; the runtime's own Input
body fills the popup, so the port's four input pages landed below the fold and the
keyboard-layout buttons sat a screen from the table they write.
`patches/settings/InputSection.cs` registers with `Id => "input"` —
`SettingsRegistry.Register` replaces by id, so this needs no patch to the checkout
either — and draws one tab bar over **Keyboard / Gamepad / Mouse**, each device's
port settings above its own binding table (`patches/settings/BindingTable.cs`,
which is a copy of the runtime's, since `InputSettingsSection` is `internal`).
Three things came out of that pass. **`Extend` has no un-extend**, so the four
pages had to be *dropped* from `PatchSettings.Install` rather than reordered — a
page still registered against `"input"` would draw again outside every tab,
irreversibly — and `Register` refuses that id now. **The Pad 1 / Pad 2 tab bar is
gone**: `BiosB.PadRead` packs pad 2 into the high half of the pad word and the
game keeps only the low sixteen bits at `0x80199554`, so it was a tab of bindings
that could not reach the game; `Keys2`/`Pad2` are never read or written, so a
config carrying them keeps them. **The table names the action** — a dimmed
`In King's Field` column, out of the action-mask table and `func_8002957C` — which
reverses the standing rule against naming the verb, on the grounds that the header
and a note under the table both say these are the game's *defaults*. The
`controls` section `docs/INPUT.md` was going to build instead is dead:
`settings.input` already reads **"Controles"** in pt-BR and es-419, so a second
sidebar entry by that name collides with the first in two of three languages —
**check a new sidebar entry against every language of the ones already there.**
`0032` is the one thing it needed from the checkout. See "The Input pane is the
port's" in `docs/INPUT.md`.

**`gameplay` is the one section the port adds itself**, for patches that change
how the *game* behaves rather than how the machine does — auto reload is not a
video option and not an input option. `ISettingsSection` is public and
`SettingsRegistry.Register` takes any implementation, so it needs no patch to the
checkout either; `patches/settings/GameplaySection.cs` is an empty shell and
everything in the pane is a page registered against `"gameplay"`. A new key has to
supply all three of the runtime's languages, unlike an override of an existing
one.

Frame pacing is load-bearing: without it the port runs faster than the game can on
hardware, so it lives in `patches/` and is always on. **It is also where an
arbitrary frame rate lives, and where the world's own tick rate does.** What pinned
the port to 30 is the *game's* own frame gate — `func_80017880`, which spins on the
vblank credit at `0x801B6CA8` until it reaches 2 and is called by stage 13 — not
the runtime. `FramePacing` **skips it at every rate**, paces the frame itself, and
runs what holds per-tick state on a wall-clock accumulator at `LogicHz`.

**`LogicHz` is 20, not 30, and that is a judgement rather than a reading.** The
literal 2 is a **ceiling, not a target** — `func_80017880` spins *while* the vblank
credit is below 2, so it forbids a frame faster than 30 and asks nothing of a
slower one, and a limit that was never the binding constraint says nothing about
intended speed. The console missed that deadline under load and landed in the
three-vblank band, and since King's Field's speed *is* its frame rate, 20 is the
speed it was played at and the only one of the two with a claim to being the speed
it was built at. (**Open:** the JP original `SLPS-00069` is reported to be capped
at 20 outright, which would settle it — unchecked, since this project has only
`SLUS-00158`.) The port's HLE GPU makes the 2-vblank
deadline every frame and never bands down, so it has to be told. No counter here
can settle it — the port cannot observe hardware, and the 30-minute vblank
histogram that looks like it can is a measurement *of the port* — **and that is not
a reason to make the player settle it**. It had a combo under Video offering both
answers and that combo is gone: 20 is the rate, it is what every measurement in
this port is taken against, and 30 is a comparison, which lives on the console
under `KF2_TICKRATE` with the rest of them. The saved key
(`kf2.framepacing.logichz`) is deliberately no longer read — a config left saying
30 with no control to show it would be a session running half again too fast and
nothing in the window to say why.
Because the gate decides the render rate and the world rate together and knows one
answer for both, leaving it running at the 20 fps default would pin the world back
to 30, which is why it is skipped everywhere rather than only above 30. **The
default render rate is 20 too**, 1:1 with the tick, which is the console's own
arrangement. Measured: 20.00 ticks/s at 20, 30, 60, 120, 144 and uncapped, with
`KF2_TICKRATE=30` reproducing the 2.14 s death clock the 30 Hz world had.
**A frame boundary is a `DrawOTag` that follows a `VSync` call, and that is
load-bearing**: it used to be a `DrawOTag` that followed an emulated *vblank*, and
since the vblank is a fixed 60 Hz wall-clock grid, above 60 fps most frames were
neither paced nor logic-clocked and the world ran at `30 × frames-per-vblank` —
measured double speed at `KF2_FPS=60`. **That boundary is a single point of failure
and it fails open**: `Floor()` has one call site and `_tickThisFrame` one writer,
both there, and `_tickThisFrame` starts `true` — so a boundary that stops arriving
uncaps the picture *and* runs every gated stage on every frame, which at 165 fps
against a 20 Hz world is the whole game at 8× from the title onward, silently. The
stage gate therefore carries a **watchdog**: a gated stage is only reached from the
main loop and the main loop draws, so one running while no boundary has arrived for
500 ms means the boundary is gone, and `FallbackTick` runs the same wall-clock grid
and paces the loop from inside `BeforeStage` instead (measured: `walk` reports 2844
units/2 s at 165 fps both with the boundary intact and with every `DrawOTag` hook
removed). Losing only the `VSync` pre degrades to charging every `DrawOTag` as a
frame rather than to no boundary at all, and `Attach` reports the boundary as a
pair (`boundary 3/3 DrawOTag + 3/3 VSync`), claims only what it **installed** and
is retried. That distinction is the fix rather than the wording: `AddPre`/`AddPost`
only queue a delegate, the detour is created later in `HookManager.Commit`, and
since `0027` that fails per function without throwing — so a summary built from the
`Add*` returns could report `3/3` while no boundary existed and latch itself done.
`patches/recompone/0028` exposes `HookManager.IsCommitted`, `patches/HookAttach.cs`
holds the retry latch and the read-back, and **all eleven patches use it** — seven
of them had also been latching `attached = true` *before* calling `Attach()`, so
anything thrown inside was swallowed by `Event.Dispatch` into one stderr line and
never retried. Half a pair can no longer be re-enabled from the Video pane either
(`_paired`), and `LoopPacing`'s missing-marker case now stands the class down
instead of reading every frame as modal. See "A registration is not a hook" in
`docs/PATCHES_AND_MODS.md`. What is gated is stages **2**, 3, 4, 5, 6 and
stage 13's fade stepper `func_80033FBC` and animated-texture updater
`func_8002DC78`, and the test each had to pass is **can it draw**. **Stage 2
(`func_80037C0C`) is where doors, the drawbridge, the minecart and the crystals
move** — it walks the object table at `0x80177714` and dispatches on the type byte
at `rec+0x4` through a 224-entry jump table at `0x8001191C` — and it *does* reach
`DrawOTag`, through exactly one edge: `func_80037B5C`, the transition fade, which
renders its own frames by calling stage 13. That is an **extra** render inside the
stage, not the frame's own, which is the same recorded exception stage 3 carries,
so both are gated and `scripts/check_gate.py`'s `KNOWN` holds the reason. The cost
is that entering a fade or a cutscene can be deferred by up to one tick. What
survived the gate is `rec+0x40` on two slots — a per-object ambient-sound
retrigger stepped by **stage 13's own object pass** `func_800331B4`, which cannot
be gated because it draws the models in the same loop. `patches/FrameSmoothing.cs` is the other half rather
than an option beside it: one pre/post pair around **stage 8** (`func_80025A1C`),
the only copy of the camera between the player state and the renderer, carrying
yaw and pitch by the fraction of a tick the frame stands at. **Smoothing the camera
is not the whole picture, and `patches/ObjectSmoothing.cs` is the rest of it**:
most of the frame is architecture that never moves, so a moving camera smooths it
for free, but anything with a position of its own still arrives in tick-sized
steps — and against a world sliding smoothly past, that step is *more* obvious
than if nothing were smoothed. Same shape, one pre/post pair around **stage 13**
(`func_800342D8`), walking **all four tables the renderer draws from** — the
object table `0x80177714` (396 slots of `0x44`, `VECTOR` at `+0x14`), the entity
table `0x8016C544` (200 of `0x7C`, `+0x2C`), stage 5's effects table `0x8019CC6C`
(128 of `0x48`, `+0x14`) and the billboards `0x80195174`. It was first written
against the object table alone, on a `func_80032588` argument census taken in a
scene with props and no creatures near; the entity table is AI state stage 4
copies *from* the object record **and** what the first loop draws creatures from,
rotation included. **Each row's liveness test is the renderer's own, not the
owning stage's, and the two are not even the same way round**: an object is drawn
when `u16[+0x6] != 0xFF` (stage 2 steps it on `u8[+0x4] != 0xFF`) and a creature
when `u8[+0x9] == 1` (stage 4 and `AgentServer` use `u8[+0x0] != 0xFF`), so
`TableSpec` carries the polarity. Using the owning stage's for the entity row
carried records the renderer never draws — measured, a mean carried offset of
2600 u with 11,400-unit tick steps, against 10-60 u and ≤87 u once the row reads
what is drawn — and each of those refusals published a `_held` address
`AnimSmoothing` then obeyed. It
**interpolates — `lerp(prev, cur, phase)`, never past a position the game
produced — on the same clock and by the same fraction as the view.** It
interpolated, was switched to extrapolating, and interpolates again: interpolating
was always right on its own terms, and was abandoned only while the *camera*
extrapolated, since the two then sat a whole tick apart and a constant offset
between the world and the things in it reads as the objects moving slower than
everything else. `FrameSmoothing` interpolates too now, so they agree about what
time it is. It leaves a
slot whose step exceeds 1024 units on an axis exactly where the game put it,
because that is a placement rather than motion (measured: real motion 37 u a tick,
a placement 233,472). **That threshold was briefly raised to 8192 and made sticky,
and the raise was reverted**: it was chasing a boss whose parts appeared to tear
apart mid-attack, and this patch cannot cause that — a creature is *one record* in
the entity table, one position and one rotation, so it moves a creature as a whole
and a limb-relative defect is the MO pose. The boss was never confirmed to improve,
while the raise did make **fireballs stutter and jump**: the effects table recycles
slots, a slot freed and refilled inside one tick is never seen free, so `Prev` is
the dead projectile and `Cur` the new one, and 8192 carries that delta instead of
refusing it. The finding underneath is that the threshold does **two jobs** —
rejecting slot reuse, which wants it tight, and admitting fast motion, which wants
it loose — and the real fix is to key the sample on the slot's *identity* rather
than infer reuse from distance. **The threshold is therefore per table** (`TableSpec.Fast`): the raise applies to
the **entity table only**, so a boss gets it and the projectile tables keep 1024
whatever the mode is. That is the compromise, and it comes from play — on `strict`
the boss is calm and projectiles are perfect but a fast boss's *head snaps into the
next animation frame*, and on the raised modes the animation gains in-between
frames and projectiles break. Both halves are one cause: a creature is drawn from
**two** smoothers, its root here and its pose in `AnimSmoothing`, and a refused
root steps at the tick rate while the vertices morph at the frame rate. So
**`ObjectSmoothing` publishes the addresses it refused and `AnimSmoothing` holds
those poses for the same tick** — they need no knowledge of each other's tables,
since `func_80032588`'s `a2` *is* `base + slot*stride + PosOff` — and a creature
past even the raised cap degrades to a coherent tick-rate creature instead of a
smooth head on a stepping body. The mode is a setting (`KF2_SMOOTH_OBJECTS_GUARD=strict|sticky|continuous`,
`continuous` by default; its combo came out of Video ▸ Enhancements with the
merge below), and the carry decision is made once per tick rather than once per
frame because the hysteresis reads state it also writes.
**3D pose is `patches/AnimSmoothing.cs`**, which drives
the MO clip clock (`func_80032588`'s ninth stack word / `func_8003486C`) so the
blender writes the in-between mesh (`KF2_SMOOTH_ANIM=1`). **The clip time is a
point on a circle, not a number on a line**, and every version of this patch
before the current one did not know the circumference: it lerped the integer time
as a scalar and then repaired each way that fails — the end of a looping cycle
told from a re-seek by *where the time landed*, a turnover synthesised out of the
last playback step and believed only off a settled run, a clip the AI is fighting
over counted in direction reversals and held, a magnitude cutoff at 4096 for a
re-seek, and a default that gave up driving the time at all. **The missing fact
was the clip's length, and it is in the table `func_8003486C` already walks**:
clip table at `bank + u32[bank+0x10]`, record at `bank + u32[clipTable +
clip*4]`, `u16` segment count, `bank`-relative `u32` pointers to segments whose
`u16` at `+0x2` is the duration — so `D` is their sum, and measured it is 4096
for every clip reached, confirmed from the other side by a highest-time-seen of
4095 and wrap steps of `rate - 4096`. `Mode.Timeline` (the default) is one
predicate where there were five: unwrap the tick's step against the slot's
settled rate over the candidates `cur + kD - prev` — `k` computed, not searched —
plus the two ping-pong reflections `-cur - prev` and `2D - cur - prev`, and take
the nearest if it is within half the rate. That covers playback, the cycle wrap,
a reverse clip and an endpoint turn at once; anything else is a re-seek or a
fight and is **held at the game's own time**. A carried tick lerps along the path
it recognised, folds it back onto the clip, hands `floor(t)` to `func_8003486C`
and spends the leftover fraction on the 12.12 weight — the fraction is under one
clip unit, so it cannot leave the segment `floor(t)` landed in and the old
whole-tick overrun refusal has nothing to refuse. It carries from the **third**
sample of a clip, since a rate has to be confirmed once, except at a genuine clip
change where the first moving step *is* the rate and is taken on trust — but only
within a quarter of the clip, or a seek into the middle of one sweeps most of the
animation in a 50 ms tick; a zero step touches neither. **Shipped as the default
it made the teleport crystals shake up and down**, and the probe had already said
so: it counted 1-6 endpoint *turns* a second while never once counting reverse
playback, and a real turn is always followed by reverse playback, so every turn
was spurious — a spurious turn runs the pose to the end of the clip and back
inside one tick. Three causes: the rate was not reflected after a turn (so every
turn mispredicted the next tick and turned again), a turn was accepted merely for
scoring better than straight playback rather than only after it failed, and the
opening step was unbounded. **The lesson under them is that `Timeline` is the
only mode that can ask for a pose outside `[prev, cur]`** — deliberately, since
that is how a loop plays forward through its wrap — so a misclassification gives
a pose from elsewhere in the clip rather than a slightly wrong one, where
`Mode.Time`'s guessy classifiers sit on top of an interpolator that stays bounded
when they are wrong. The two designs differ in **where the risk sits**. Hence the
fourth fix: the acceptance window, which scales with the rate, is **capped** at a
twentieth of the clip, so a seek recorded as a rate cannot let the next tick
accept anything — being wrong costs a held tick rather than a pose out of
nowhere. **That cap replaced a worse first attempt**: refusing to *record* an
implausible step at all, which stranded any clip whose real rate exceeded a
quarter of its length — no rate, so the unsettled branch; too big, so neither
carried nor recorded; nothing changed, so the next tick reasoned identically,
for the whole animation. Play found it on a flying gecko's backflip looking like
it ran at a low frame rate. **Size buys a tick of latency; repetition buys
correctness** — a big step now waits one tick and is confirmed by a second
observation, and `FirstStepFrac` gates only the no-confirmation shortcut. The
probe gained `widest refused N` for it, which is the counter that makes a
stranded clip visible at all: a refused step of 1344 on a 4096-unit clip turned
up in the first run after adding it. Measured after, at 144 fps over areas 0, 2 and 7: 553 playback ticks,
7 wraps, **0 turns**, 15 holds, 0 without a clip length, and the
widest arc carried over the whole run is **290 units** — the top of the measured
playback range, so nothing walked round the back of the circle — against a widest
*refused* step of 1344, the two staying far apart being the shape to want. (That
run also read `0 settling`, and **that number said nothing**: every `Hold` exit
seeded a rate on the way out, so the census test could never be true and the
column was dead. It is recorded at the exit that knows it now, and reports.)
`Mode.Weight`
refuses 8-17 carries a second for leaving their segment in the same scenes.
**A tick is a frame identity, not a flag**: `TickedThisFrame` is stable for the
whole frame, so a second stage-13 walk inside one would step the tick twice and
re-sample both smoothers at the same instant, wiping the tick's prev/cur pair.
`FramePacing.FirstWalkOfTick` is that test — `Frames` plus the 500 ms boundary
watchdog, since the identity fails closed — and `AnimSmoothing` and
`ObjectSmoothing` (three sites: its sample and both hysteresis blocks) go through
it. Measured 0 such walks over the autostart load and five area warps, which the
code agrees with: `LoopPacing`'s redraws run only while `!TickedThisFrame`.
**`Mode.Timeline` is the default again** — the default moved to `Mode.Time` while
the shake was diagnosed, since that was the only mode with a positive report by
eye, and moved back once play reported the fixed one looking very good; the other
two are `KF2_SMOOTH_ANIM=weight|time`, switchable while a creature is on screen,
and the losers go once the picture is judged. **On the
invariant it is the correct approach and `Mode.Time` is not**: when the predicate
cannot explain a tick it holds, which *is* the invariant's second half, whereas
`Mode.Time` interpolates anyway and synthesises its turnover out of the last
step, so across a wrap it draws a pose the game never produced. Short of that it
is not proven: three tuned constants remain (`RateTolRel`, `RateTolAbs`,
`FirstStepFrac`, each against something measured rather than picked), "playback
is constant velocity" is an assumption about the game rather than a reading of
it, **the endpoint-turn and reverse-playback branches have never once been
exercised** in any run — that is the part with no evidence behind it and the part
that shipped the shake — and every clip measured is 4096 long, so a per-clip
length lookup would look identical to reading a constant. Still to look at: a
looping clip's turnover, a clip played in reverse, an attack the AI restarts.
Vertex-fetch lerp was tried and did
not change the picture. The
player's arm was recorded as a different bug and is not: it was called 2D, a
sprite index in the HUD builder `func_80031D5C`, on a packet-count difference
that was really the HP/MP gauges collapsing — it moved *down*, the wrong way for
an arm appearing. It is a **3D MO mesh drawn by `func_80032400`**, a fourth
drawing callee of stage 13 that draws nothing while the swing clock at
`0x801994A4` reads `-1`, which is why a census taken standing in an area credited
it nothing. So `AnimSmoothing` has a **second front-end** rather than a second
patch: `Observe()` is the shared body, a pair on `func_80032400` fences the scope
and resets the slot across the idle gap between swings (the clip byte is the
*kind* of attack, so two swings of one kind would otherwise read as one enormous
backwards step), and a pair on `func_80034DA8` opens the same window
`BeforeClock` already works inside. Keyed on `a0` = `0x8019949C`; nothing written
to game memory. Measured at 144 fps: clip 0, 300 a tick on a 4096-unit clip, 13
ticks a swing, **0 held**, 86 of 94 frames carried, world clock still 19.9
ticks/s. **All four
default to off, and they are now one checkbox** — *Video ▸ Enhancements ▸ Smooth
motion between game ticks* writes all four patches and all four keys together,
since four controls for one idea was the implementation's shape rather than the
player's, and the two expert combos came out with the checkboxes they hung under
(see "One switch for all of the smoothing" in `docs/PATCHES_AND_MODS.md`). **The
position half is inside that tick and carries a known shear**: two of stage 13's
callees read the raw player position after `FrameSmoothing.After` restores it, so
the arm, the torches and the creatures slide against the architecture on a
non-tick frame — which is why it used to be its own switch, off by default, and
the merged tick has never been looked at by eye. While the boundary was broken
the phase was pinned to 0 and the
smoothing never ran at all, so the first three's picture has never been seen.
The animation one's has: it was confirmed by eye once the clip-time guard stopped
discarding every real step. A 50 ms tick makes
it matter more than the 33 ms one did. **The stage gate cannot reach the in-game
menu, and `patches/MenuPacing.cs` is why that mattered**: the menu is a modal
sub-loop (`func_80029CBC` `jal`s `func_80018E80`, which blocks for the whole
session and renders its own frames), so no gated stage is being called while it
runs. Two things in there are counted in **vblanks** rather than in ticks, and so
ran at the render rate. The cursor does not edge-detect — holding a direction
steps once per menu-loop iteration, throttled only by `func_80022E90`, a spin on
six `VSync(0)` calls. Those were a vblank each on hardware (100 ms); here `VSync`
returns as fast as `FrameClock`'s deliberately permissive `max(60, fps*2)` ceiling
allows, which above 60 is not at all — **measured 36-37 cursor steps a second at
144 fps** against 7.5 at the 20 fps default. A pre/post pair around
`func_80022E90` and a pre on the `VSync` thunk hold those six calls to the 60 Hz
grid, so the six frames still present: 100.2-100.8 ms at 20, 60 and 144. The
residual 6.0-9.5 steps a second is the menu's own frame, which still lands at the
render rate. **The cursor's blink is the same bug one layer up** — an eight-step
ramp at `0x8006E5CC` stepped by the menu's frame head `func_80022530`, one wink
per accepted move rather than a continuous pulse, measured 73-77 steps a second at
144 fps against 15-19 at 20. The frame head swaps the buffer so it cannot be
skipped; a pre/post pair puts the two words back on a frame the grid did not
advance on, which **caps** the wink at 60 Hz without pacing the menu — nothing
sleeps, so a 144 fps menu is still a 144 fps menu. On by default;
`KF2_MENUPACING=0` is the comparison. **Neither number has been looked at by
eye**, and the 60 Hz is a choice rather than a reading (`MenuPacing.BlinkMs`): if
the console's menu held 60 fps it stepped the blink twice a vblank, since the
frame head runs twice an iteration. See "The menu's cursor repeat" in
`docs/PATCHES_AND_MODS.md`. **The menu is not the only loop of that shape, and
`patches/LoopPacing.cs` is the generalisation rather than a third instance of it**:
any *modal loop* — a function that takes the main loop over and presents its own
frames — is entered from a gated stage, so the gate decides only whether it is
entered and never cuts one in half, and inside it the loop iterates once per
**rendered** frame. That is the transition fade `func_80037B5C`, the cutscene and
message-box loops, the menu box open/close and the item-use and spell-cast
animations — a picked-up item spinning too fast is that bug, not an entity to be
found. The fix restores the identity the console had, that a modal loop's
iteration *was* a frame and a frame *was* a tick: **the loop's body runs once per
world tick, not once per rendered frame**, so every counter inside it is right
without being enumerated. Classifying a frame costs **one** hook — a pre on
**stage 9 `func_800140AC`**, whose only caller is the main loop `func_8001369C`
(stage 1 looks like the marker and is not: two modal loops and three area modules
call it too) — plus a flag set in `FramePacing.BeforeFrameGate`, which is already
hooked on `func_80017880`, stage 13's sole caller, and so says whether the frame
drew the world. **Holding the loop is only half**: pacing its frames to the tick
gives the right speed and a 20 fps *picture*, because the frame the loop draws
*is* the tick and `LogicPhase` is 0 on every one of them, so the smoothing patches
have nothing to carry. The gap between iterations is therefore filled with
**redraws** — stage 13 called again at the frame's phase, which is what
`func_80037B5C` already does inside a stage — so `ObjectSmoothing` and
`AnimSmoothing`, which bracket stage 13, carry the picture. That post on stage 13
must run after theirs, which is why `LoopPacing` is installed last in `Program.cs`.
**A redraw replays stage 13 with the two pointers the loop itself passed it**, and
that is load-bearing rather than tidy: stage 13 is
`func_800342D8(VECTOR *pos, SVECTOR *rot)` and builds the frame's whole view matrix
out of them unless both are zero, so a redraw that leaves the register file alone
projects the world through the tail of `func_8003549C` — measured, a pointer into
the sound table near `0x8018EAA4`. That draws next to nothing, and since this
game's `PutDrawEnv` has `isbg=0` there is no background clear, so the buffer keeps
what was in it two frames ago: the first version of the redraw shipped that way and
play reported black flicker and a stale frame alternating with the live one.
**Stage 8 is deliberately not replayed** — it *writes* through those same two
pointers, so it corrupted whatever they addressed, and re-running it would
overwrite a cutscene's scripted camera with the player's; the *player* camera
cannot move inside a modal loop anyway, since no gated stage runs there (measured:
`KF2_LOOPPACING_PROBE=2` reads `the loop's own view moved 0.0 u per iteration`
through a fade, a warp and the menu). **A camera the loop builds itself is the case
that does move**, and `func_8004831C` — the cutscene and message-box loop — is the
one play reported: it ramps a heading `0 -> 0x1000` by `0x200` an iteration and
hands stage 13 the `u16` it just wrote, a full turn in 32 steps, and elsewhere steps
`rec+0x26` by `0x40` while passing `a1 = 0x80199504`. Held to the tick that is
11.25° a step. So a redraw **carries it**, in `FrameSmoothing`'s own shape:
`lerp(prev, cur, phase)` over the three `u16` angles at `a1` and the three position
words at `a0`, applied in the pre and taken back in the post, re-primed whenever the
pointer pair changes or the main loop takes over, with a `CutUnits` guard so a
scene cut is left where the loop put it. The menu draws no world, so it is paced at
the vblank instead. **Both markers are GAME.EXE's and `DrawOTag` is not**, so
OPEN.EXE and END.EXE arrived with both flags clear and were read as modal
interface frames — measured, the title screen pinned to exactly 60.0 fps at
`KF2_FPS=144`; the class now tracks which *executable* is loaded from
`OverlayLoadedEvent` (fdat modules are GAME.EXE still running) and classifies a
frame outside GAME.EXE as the main loop's. The redraw cap is
`3 x max(TargetFps, Measured) / LogicHz` floored at 64 rather than a pinned 64,
since `KF2_TICKRATE` reaches 5 and 64 is then a third of a tick, and it announces
itself once when hit. The reprime that keeps the main loop's own camera out of
`Carry` runs in `MainLoopStage` rather than at the frame boundary, so a lost
boundary cannot latch it. Measured at `KF2_FPS=144` with
`rate_matrix.py modal-rate`: the fade's body 33.8 -> 19.9 iterations a second, its
picture 33.8 -> 144.0 frames a second, the menu 144.0 -> 60.1. It does nothing at
or below the tick rate and touches no game memory. What it cannot reach is a counter stepped inside a *drawing function's own
body* — stage 13's shake accumulator `0x8006E608` and `func_800331B4`'s ambient
retrigger, which want a hold/restore pair instead and which redraws step as
often as an ordinary frame already does — or a counter the modal loop steps in its
own body, whose *speed* is right but which is smooth only if its transform comes
from a table `ObjectSmoothing` carries or is the view the loop hands stage 13. Both
are in `docs/TODO.md`.
See "Loops that render their own frames" in `docs/PATCHES_AND_MODS.md`.
**A disc wait is neither a modal loop nor a frame, and `patches/LoadPacing.cs` is
that third case**: the loading screen's walking figure is stepped by
`func_8001883C`, which draws straight into VRAM with `ClearImage`/`MoveImage` and
so presents **no ordering table at all** — measured, zero `DrawOTag` calls between
entering the loader and the fade at the end of it — so no frame boundary exists
there and neither `FramePacing` nor `LoopPacing` can see it. It ends in
`DrawSync(0); VSync(0)`, one vblank a call on hardware; here `VSync` returns at
`FrameClock`'s permissive `max(60, fps*2)` ceiling, so the figure took its 84
steps in **352 ms at `KF2_FPS=144` and 1715 ms at the 20 fps default**. A pre/post
pair on the two disc waits `func_80017CA8` and `func_800181B0` (depth-counted, the
second calls the first) marks a window in which every `VSync(0)` is held to the
60 Hz grid — `MenuPacing`'s repeat shape — which reads **49.0 steps a second at
20, 60 and 144**, the console's own four-steps-per-five-vblanks. Holding the
animator's three counters instead was tried and is worse: the calls arrive in
bursts of four, so a cap refuses steps that were not early on average and drops
the *default* to 36-40. The cost is that above 60 fps a load now takes as long as
it already does at the default, 1.7 s rather than 0.35 s. On by default;
`KF2_LOADPACING=0` is the comparison. See "The loading screen's walking figure" in
`docs/PATCHES_AND_MODS.md`.
**The flames are a fourth case and `patches/SpriteAnim.cs` is it**: not the eight
scrolling texture slots at `0x80192D58` that `func_8002DC78` owns and the gate has
held since they were found, but the **billboard sprites** — table 4 of the four the
renderer walks, `0x80195174`, 128 records of `0x18`, free at the `u16` `+0x0`. Each
is a strip of authored cels: `+0x3` how many, `+0x4` the interval, `+0x5` the
current one, seeded at load to `(rand * cels) >> 15` so two torches do not flicker
in step. The object loop of `func_800331B4` draws slot `i` with `u8[rec+0x5] + 0x80`
and then steps that byte whenever a **single global counter at `0x80195170`**
divides by the slot's interval — and `func_800331B4`'s last instruction increments
that counter, so it counts *rendered frames* and every animated billboard in the
game burned at the render rate (measured **4488 cel changes a second at 144 fps
against 640 at 20**; reported from play as "these flames still run really fast at a
high framerate"). The stage gate cannot reach it — `func_800331B4` draws — so it is
a **hold/restore pair** around that function, putting the counter and the 128 cel
bytes back on a frame the world did not tick on. **The word is what makes it
fixable**: hold one global and the whole system holds, which is exactly what stage
13's shake accumulator and the `rec+0x40` ambient retrigger do *not* offer, and is
the question to ask of the next one. Keyed on `FramePacing.Frames` (a new
public *identity*, not a rate) as well as `TickedThisFrame`, so a `LoopPacing`
redraw or a fade's own render inside one frame is held rather than counted twice —
and with the same 500 ms boundary watchdog the stage gate has, **because a hold
fails closed**: the first measured run lost the frame boundary and every flame in
the game stood still for the session. Nothing to interpolate, so it is a rate and
not a picture: on by default, no settings page. Measured with `rate_matrix.py
sprite-anim`: 20.0/20.6/20.8 cel steps a second at 20/60/144 against
20.7/60.1/144.8 with `KF2_SPRITEANIM=0`, `KF2_TICKRATE=30` reading exactly 1.5x
the 20 Hz cel rate, and `walk` unchanged at 2844-2845 units. See "The flames run
at the render rate" in `docs/PATCHES_AND_MODS.md`.
`patches/FullRateLogic.cs` (`KF2_FPS_LOGIC=full`) is the comparison mode and
is not shippable — pitch, gravity and every per-tick counter do not scale. **The
default is 20 fps drawn against a 20 Hz world.** See "Any frame rate" in
`docs/PATCHES_AND_MODS.md`. Dithering is a patch for a
different reason — it is a picture the port should be able to offer without a
package having to load — and defaults to *off* (no crosshatch). **True color is a
patch for that same reason** and is the other answer to the same 15-bit banding the
dither hides: it renders the shaded gradient at 24 bits so it does not band, with
no crosshatch (`patches/recompone/0021`, switch in `patches/TrueColor.cs`). It
defaults to *off* too, but not for the sub-pixel reason — 24-bit shading is
deliberately not what the hardware did, so the default is the authentic look.
**Being one question they are asked once**: the two checkboxes are a single
three-entry `Shading` combo under Video ▸ Enhancements
(`patches/settings/ShadingPage.cs`) — `Dither (original)` / `None` /
`Smooth (24-bit)` — because two ticks cross into four states carrying three
meanings and the fourth is a crosshatch laid over a smooth gradient. `None` is
what both defaults already were, so no saved config changed meaning, and both
patches keep their key and their env var. See "Two shading checkboxes were one
question asked twice" in `docs/PATCHES_AND_MODS.md`.
**Perspective
correction is a patch for that same reason and is on by default**, beside it under
Video. Unlike the others its work is not in `patches/` at all: a texture
coordinate is decided far below anything `HookManager` can reach, so the mechanism
is `patches/recompone/0009` and `0012` (`GteVertexMap`, the rasterizer and the prim
shaders) and `patches/Perspective.cs` is only the switch and the probe. The depth is
tied to its vertex by **the address the screen coordinate is stored at**, followed
through the game's own copy into the primitive packet — not by the screen position,
which several vertices share and which `0009`-`0011` could only guess between.
**Sub-pixel vertex positioning is the other half of the same recovered number** and
is shaped the same way — mechanism in `patches/recompone/0010` and `0012`, switch and probe in
`patches/Subpixel.cs`, checkbox under Video — but defaults to *off*, because the
mechanism has been measured and the picture has not. **The Z-buffer is the same
depth used as occlusion** rather than as a texture denominator: the GPU has none,
so intersecting surfaces take turns in front of each other on the ordering table,
and `patches/recompone/0014` tests the recovered SZ per pixel instead. **It has
no control in the window** — `KF2_ZBUFFER` and `KF2_ZBUFFER_THRESHOLD`, the
threshold defaulting to off — because the cause the notes had left open was
found: `vDepth` is an
ordinary varying, so OpenGL interpolates it in `1/w`, and `HleTri` set the clip W
only for triangles whose *texture* was being corrected — so every untextured wall
arrived with `w = 1` and its interior depths came out linear in screen space,
which is the one thing a view depth is not. The software rasterizer never had it
(it interpolates the reciprocals and takes one back), so the two renderers had
disagreed about the interior of most of the architecture. `0036` gives a
depth-tested triangle a real clip W whichever mechanism recovered it; the cost is
that "depth buffer on, perspective correction off" now corrects textures too,
which is a comparison rather than a shipped picture. Still off by default and
still unjudged by eye. **Both of the new guards were picked by census rather than
by copying DuckStation, and one of them changed as a result.** The depth-clear
threshold looks for a scene the game restarted mid-frame; measured, the forward
steps between consecutive primitives are one smoothly decaying population with no
gap (39.4% under 10 units, 47.7% under 50, 9.2% under 150, 2.8% under 300, 0.9%
beyond, widest 318), because this game draws one world and a 2D HUD and 2D never
enters the mean — so there is no break to find, and DuckStation's 300 fired **2575
times a second**, twenty times a frame, taking 144 fps down to 34-76 as each clear
flushed the GL batch. It is 0 now. PGXP's tolerance censused the same way is one
population too: nothing past 2 px ever, widest 1.87, so 2 is inert and stays as a
tripwire — and the 2.6-7.7% sitting between 1 and 2 px is not a wrong vertex but
the **divider**, `PushPrecise`'s double-precision `H/w` against the GTE's own `Unr`
reciprocal table. **The second mechanism beside it is PGXP**
(`patches/recompone/0034`-`0036`, upstream RecompOne's own, backported from
`39fb337a`/`91c20fcf`/`95f0585b`/`6aae910a`): the same two numbers followed through
the CPU's registers by hooks the recompiler emits, rather than paired by value out
of `PSMemory`'s traffic. `patches/Pgxp.cs` is the switch and the probe and
`KF2_PGXP*` the console equivalents; both sources ship and **`KF2_PGXP` alone
chooses** — PGXP has no control in the settings window and its saved key is not
read, so a config that ticked it while upstream's block was drawn is not left
running a fifth slower with nothing to explain it. Upstream's own frame-rate
slider went with it: it writes `Interp`'s key, disables itself unless PGXP is on,
and this port never enters `PresentLoop`, so it changed nothing it claimed to and
was the second frame-rate slider in one pane. See "PGXP has no control in the
window" in `docs/RENDERING.md`.
**Measured in area 2 at 144 fps, PGXP bought no coverage in this game** — the
address map answers for 92.2-97.1% of vertices against PGXP's 93.4-96.7%, because
King's Field assembles its packets with whole-word `lw`/`sw` out of a transform
cache, the one shape a value ring follows perfectly — **and it costs a fifth of
the frame rate** (144.0 fps against 106.7-114.9). `KF2_PGXP_CPU=0` isolates that
cost to the emitted hooks — 144.0 fps again, but 79.2-85.7% coverage and **zero**
answers from the RAM shadow, since `PgxpMemory.Store` is only reached from
`PgxpCpu`: upstream's CPU and memory ticks are not independent, and without the
first PGXP is the screen-position guess the ring replaced. What it has that the ring cannot
is backface culling decided on precise positions, true float positions rather than
a recovered fraction, and coverage by construction instead of by luck of the copy.
The emitted hooks are free when it is off: 144.0 fps at 20.0 ticks/s with PGXP
disabled on the recompiled binary. `0035` is one of three patches that force a
recompile, with `0004` and `0037`.
See "Sub-pixel vertex positioning", "Z-buffer" and "PGXP" in `docs/RENDERING.md`. Auto reload is a
patch for the same kind of reason: a death costing four screens of menu is
something a player expects the port itself to have dealt with, so it is on by
default and its knobs — the switch and the slot — are under Gameplay; **the
delay is a fixed 2 s** and `KF2_AUTORELOAD_DELAY` is the comparison, both ends of
the slider it had being wrong (0 reloads inside the death animation, 10 leaves
time to reach the menu the patch exists to skip). Analog twin-stick control is the same
test applied to the pad — without it a modern controller's left stick is wired to
the D-pad and *turns* rather than walking — so it is on by default too, and its
knobs are under Input, below the button-binding table. It costs nothing when a
stick is centred: both hooks return before touching memory, so keyboard and D-pad
play are identical to having it off. **Mouse look is the other half of that
patch rather than a patch beside it** (`patches/Mouse.cs`): a mouse and a stick
both ask for the same per-frame turn and pitch step, so the mouse's number is
spent inside `Analog.BeforeLook` and one routine writes the velocity word. Its
buttons take the other route entirely — `PadReadEvent`, so they are pressed *as
pad buttons* at the moment the game reads the pad, which needs no address, follows
the game's own control-config screen and works in its menus. It is **off by
default**, though not for the sub-pixel reason: the path *is* measured end to end
— the angle asked for and the angle the game applied agree within a few percent
over four windows of real play — but a pointer that disappears into the game
unasked is worse than one switch to find. What no counter can answer is the feel
(0.15°/px) and whether the pitch runs the right way round. See "Mouse look" in
`docs/INPUT.md`.

**The menu pointer is the other thing a mouse can do here, and unlike mouse look
it is on by default** (`patches/MenuMouse.cs`, `KF2_MENUMOUSE=0` the comparison,
switch on the Mouse tab of Input): point at an in-game menu item and the game's
own cursor moves to it, left click confirms, right click backs out, and a left
click clear of the menu's own boxes backs out too. On by
default because it needs **no captured pointer** — a player who never locks the
pointer still has one, and pointing it at a menu is the one thing a mouse can do
in this game without being locked to the window first; opening a menu in fact
*releases* a captured pointer and retakes it on the way out, since
`CursorMode.Raw` reports an unbounded virtual position and there is no "over the
picture" while it is locked. **There are three menus, not one, and the first
version of this patch knew only the first** — reported from play as working on
the main tab menu and doing nothing in any submenu, which is exactly what it was.
The game builds a menu out of three unrelated widgets and the three cursors are
not the same kind of thing, so there are three mechanisms rather than one
generalisation: a **fixed option list** (`func_800208D8` draws,
`func_8001EA14` steps, the cursor is the caller's register), a **scrolling list**
(`func_800209E0` draws, `func_8001EB70` steps, the cursor is `u8[desc+0x21]` in
memory) and a **two-line yes/no prompt** (`func_80021478` draws,
`func_800206E0` steps it inline, the cursor is that function's `S1` and nowhere
else). What they share is the pointer, the hit test and the ownership rule.
**The fixed list is met at its return**, which is forced rather than chosen:
injected Up/Down is already recorded as not moving `func_8001EA14` at all ("The
wall is the title, not the Continue menu"), and the menu never reads stage 3's
pad word at `0x80199554` — `func_80022E58` calls `PadRead(1)` itself, so
`Analog`'s route cannot reach it either. A post-hook writes `V0`, and a click
writes the stepper's own out-parameters exactly as its Cross arm does,
**including that confirming the last entry is a cancel**. **The scrolling list
needs none of that**: `func_8001EB70` returns the *pad word* and steps the
cursor in the descriptor its caller passes, so hover writes `u8[+0x21]` and
`u8[+0x22]` and leaves `u8[+0x20]` alone — a pointer can only reach a row that
is on screen, so the three stay consistent by construction. A move also replays
the arm's `func_80022CAC(items[cursor])`, which is what loads the entry's
preview; without it the list moves and the picture beside it does not.
**The prompt is the one place that goes through the pad**, and it is safe only
because the loop tells the patch its state every iteration — `func_80021478` is
handed the flag as `a2`. A post on `func_80022E58` ORs in one synthetic Up while
the hovered box disagrees with the drawn flag and a Cross once they agree, so
the injection is **closed over the state it changes** and the next iteration
stops asking. That is what the no-edge-detection finding actually forbids: a
held synthetic Cross confirms on every iteration and runs away through the
submenus, where this cannot. It is scoped to `func_800206E0` being on the stack.
**The geometry is the game's own, not a calibration, for all three**:
`func_800208D8` reads item *i* at `0x80064CD4 + 0x134*group + 0x1C*(i+1)`,
`func_800209E0` lays its rows out itself at `(u8[desc+0x1C], u8[desc+0x1D] + 5 +
14*r)`, 236x14 packed, and the prompt's two records come off the drawer's
arguments — so a page this patch has never heard of is measured correctly the
first time it draws (measured: group 0, eight boxes at x 25 y 13..195, 124x24;
group 6, two at x 92 y 88/114; an inventory of 10 entries, 10 visible, rows at
x 42 y 44). **The hit test is the rectangle the game drew, in both axes**, and
that replaces a synthesised per-row band tested against Y alone which was wrong
three ways at once: six pixels low (`func_800218B4` insets the box's *origin* by
six, not its size — `docs/GAME_INTERNALS.md` had that backwards and is
corrected), running each row's band into the gutter so a click between two boxes
picked the one above, and leaving the whole width of the screen live so a click
far right of a 124-pixel box confirmed it. **X is exact rather than avoided**:
the presented picture is `GameW + 2*margin` game pixels wide with the game's own
column 0 at `margin` — what `GlCore.PresentDisplay` builds and what
`Display.WideMargin` computes — so the margin comes off after the scale and a
game X is a game X at every aspect (measured `of 320x240 +54` at 16:9). The
gutters are now dead rather than assigned to a neighbour: 2 pixels in 26, and
none at all in a scrolling list. **Hover only takes the cursor once the pointer
has moved and hands it back the moment the pad moves it**, or a mouse resting
over the picture would pin the selection and the D-pad would look broken; in the
prompt, where there is no cursor to compare, a drawn flag the patch did *not*
ask for is the pad. It writes `V0` and the confirm out-parameters plus `0x8006E5D0` for
the fixed list, two descriptor bytes for the scrolling one, and **nothing at all
in game memory** for the prompt or for any back-out; `0x8006E5C4` is untouched by
all three, so
`MenuPacing` is unaffected both ways. The pointer's position is
`ImGui.GetIO().MousePos` — same screen space as `OutputView`, a plain field read
— and `OutputView` is read **directly** rather than through
`MapRender.Picture`, whose viewport fallback is right for something that must be
drawn somewhere and wrong for a coordinate conversion. The one runtime change is
`0029` growing `GameW`/`GameH`, which is deliberately the game's own width, not
the presented one. **A wheel was written, taken out, and put back on the other
axis**: it stepped the *cursor*, relative to where the cursor was, while hover
puts it where the pointer is, and the two fought on the next iteration — but a
list longer than its window was unreachable by pointing at all (measured, `7
entries, 4 visible`), so three of its rows needed the D-pad. `u8[desc+0x20]`, the
page, is the one byte hover never writes, so the wheel owns that and hover keeps
the cursor and nothing is contested: the page changes which entries the rows show
and hover reads off the row the pointer is on. It is clamped to `count - visible`
rather than wrapped like the game's own arm, one notch to one row (the pad's Down
pages by exactly one), and it rewrites `+0x22` for a page that moved under a
cursor that did not, since `+0x22` is `+0x21` minus `+0x20`. **The scrolling list
alone**: a fixed list and a prompt draw every row they have, so a notch over one
is discarded rather than saved. The notch is **taken from the host, not listened
for** (`HostWindow.TakeMouseWheel`, `0038`, `TakeMouseMotion`'s own shape) —
ImGui's per-frame `MouseWheel` is a level and would lose a notch whenever two
frames passed between menu iterations, and a `MouseEvent` listener would have to
be scoped to a session that **not every scrolling list is inside** (the save-slot
menu comes off the object-use handler, a shop off an NPC). It is a float end to
end, `(int)wheel.Y` having rounded a trackpad's sub-notch scroll away entirely.
Nothing about the gesture is measured — the shell's `press` reaches Cross but not
the menu's Up/Down and cannot inject a scroll at all — so the arithmetic is read
off `func_8001EB70`'s own six branches rather than run. **Backing out is not one of the three and used to be**, and that is what
"sometimes right click works, other times you need Tab" was: it was written three
times, once per widget, against that widget's `*cancelled` out-parameter, and
**six** routines read the pad inside a menu — the three steppers plus
`func_8001BB7C`, `func_8001BE60` and `func_8001B0D0`. Two of six. All six call
`func_80022E58`, so the gesture now raises one flag and a post on that read ORs
the game's own cancel mask `0x8006E56C` into the word it returns; whichever
routine is reading gets it, its own arm blips and writes its own out-parameter,
and a screen with no cancel arm ignores it exactly as it ignores the pad's cancel
button (`func_8001BE60` is the one found). It cannot run away the way a held
synthetic Cross would: the flag is spent on the read that delivers it and one
iteration is one pad read. The second gesture is a left click **clear of the
widget's boxes by 8 px on every side** — clear of them rather than merely off a
row, or the fixed list's 2px gutters would each be a back-out — with the bounds
being the union of the boxes drawn and nothing else, so a click on the frame or
on the item picture beside a list reads as off the menu. The **button edges are
sampled at that pad read** as well, which is the other half of the same defect: a
press and release that both fell while no hooked stepper ran used to be seen by
nothing. Its 500 ms gap is also the session scope for the menus outside
`func_80018E80` that `BeforeMenu` cannot see. **What is still not covered is a
fourth shape**: `func_8001BB7C`
and `func_8001BE60` draw a fixed list and then read the pad themselves rather
than calling `func_8001EA14`, so hover and confirm do nothing on them. Nothing here has been judged by eye, including
whether the cursor lands on the item the pointer is actually over, and whether a
click on the frame or on the picture beside a list reads as "off the menu" the
way the bounds say it does. See "The menu pointer"
in `docs/INPUT.md` and "The menu's item positions are a table", "The scrolling
list is one descriptor" and "The two-line prompt keeps its cursor nowhere" in
`docs/GAME_INTERNALS.md`.

**Opening the full-screen map stops the world** (`Map.Pause`, `KF2_MAP_PAUSE=0`
the comparison; not a setting), and the mechanism is **the stage gate held
shut rather than a new one**: `FramePacing.PauseWhen(predicate)` makes
`BeforeStage` refuse on every frame instead of on three in four, and the six gated
stages already *are* the per-tick world — objects, the pad read and movement, the
entity table, effect lifetimes, the area module's logic, the fade and the texture
scroll. So nothing is written to game memory and nothing is restored, every
counter stops together with no list to remember, and stage 13 is not gated so the
picture keeps being drawn at the render rate under the map. A **predicate** rather
than a flag because the panel closes by three routes — M, the pad button, the menu
bar — and a latch one of them misses is a game that never resumes; it is latched
once a frame (the boundary, and `FallbackTick` when the boundary is lost) because
host input is polled from inside the game's own `VSync`, and gated on `Map.InGame`
so opening it at the title cannot freeze the intro. **A modal loop needs the other
half**: a fade or a message box is the main loop on the stack, so `LoopPacing`'s
redraw fill is entered when paused even at the tick rate and is **uncapped** while
paused — it cannot spin, since each redraw passes the frame boundary, is paced by
`Floor` and presents through the `VSync` where input is polled, which is what makes
the map closable from inside one. Pause redraws are counted apart from fill
redraws so the cap warning keeps its meaning (it fired once, spuriously, on the
frame a paused map was closed inside the fill). The pad is not read while paused,
which is what a pause *is* and also why the shell's new `map [on|off|toggle]` verb
is on the fast (vblank) queue rather than the heavy (stage 3) one. Measured at
`KF2_FPS=144`: 20.0 ticks/s → **0.0** for the whole time the map is up → 20.0 on
closing it, the picture holding 144.0 fps throughout, and the same through three
warps with the pause landing inside the transition fade, with no cap warning.
Never looked at by eye: whether a frozen frame under the scrim reads as paused
rather than as a stall. The minimap and the docked panel deliberately do not
pause. See "Opening the map stops the world" in `docs/PATCHES_AND_MODS.md`.

**The map is a patch for auto reload's reason** — King's Field is a maze, the
original shipped no automap, and everything else in the port that knows where you
are is a debug instrument — so it is on by default and its knobs are under
Gameplay. **The pad's touchpad button opens a full-screen map** and `M` does the
same from the keyboard; `N` toggles a corner minimap and `Shift+M` opens the
docked panel with the per-tile readout. **The Gameplay page is down to three
widgets** — a Map combo, and auto reload's checkbox and slot — since a picture
nobody has judged is a comparison rather than a feature and everything else there
was the port's question to answer rather than the player's; the pad binding moved
to Input, where a player looks for what a button does. The minimap is *off* and
its seven controls went with it, for the sub-pixel reason. **The map and fog of
war are one combo** — `Off` / `Whole area` / `Fill in as you go` — for the reason
the two shading checkboxes are one: two ticks cross into four states carrying
three meanings, and fog on with the map off is nobody's answer; both patches keep
their key and their env var, and the map is read as the master so any click
harmonises the pair. Auto reload's slot dims rather than vanishing, and its
*Simulate death* button and death census are gone, being instruments — the shell's
`kill`, the MCP `kf2_kill` and the attract demo are where dying on purpose lives,
and `AutoReload.Status` still prints. **Neither page names itself any more**: an
empty `IPatchPage.Title` declines the heading, because `SettingsPopup` already
draws one saying Gameplay and the section has no content of its own to separate
from. See "What the Map page is down to" in `docs/PATCHES_AND_MODS.md`. **There are three viewports over one reading, and
`patches/MapFullscreen.cs` is the one a player opens**: the whole area over the
dimmed game, no chrome, `NoInputs`, closing the minimap while it is up — the
docked `MapPanel` with its toolbar and its ten-byte hover readout is the
*instrument*, and a windowed debugger over a 320x240 picture is what "the minimap
contrasts hard with the game design" meant. **"Full screen" is the game picture,
not the window**, and it was the window: the picture is an `Image` inside the
runtime's Output panel, fitted to that panel at the display's aspect and centred
in it, so a map sized to the viewport lay over the menu bar, the dockspace border
and the bars either side of a 4:3 picture in a wider window — centred on the
window rather than on the game, with its edges nowhere near the picture's, which
is the "odd appearance" reported from play. `patches/recompone/0029` publishes
that rectangle as `OutputView` from the one place that computes it, before any
floating panel draws (`PanelManager` draws in registration order and `HostWindow`
registers the Output panel long before `Program.cs` adds these), and
`MapRender.Picture` is the accessor, falling back to the viewport's work area
when no picture was drawn that frame — a blank map being worse than the old
behaviour. The map's margin and header band became a share of that rectangle as
well as of the interface scale, since the picture is now the smaller of the two. **The touchpad is reachable because it
is not a PS1 button**: `ControllerEvent` is dispatched off SDL before `PadState`
maps anything onto the PSX pad, so the raw `SDL_GameControllerButton` 20 arrives
where nothing downstream has a slot for it and it cannot leak into the game.
Nothing here can ask a pad whether it *has* a touchpad — `InputManager` keeps the
`GameController*` to itself — so the binding is a setting (Touchpad by default,
L3, R3, Select, None) rather than a probe, and **no controller was connected to
any measured run**, so that button 20 arrives is read off SDL's mapping rather
than measured. It fits the area rather than following the player, from a new
occupied-extent pass in `Map.Copy` — which **measured as the whole 80x80 grid**
on both halves of areas 0 and 1, so below a floor of 6 px a tile the fit is
abandoned and the view centres on the player's *tile*. **The player is a dot in
the square they occupy, not an arrow**, and that is the default
(`MapRender.DrawPlayerDot`; `KF2_MAP_ARROW=1` is the comparison): an arrow gives a sub-tile position
and a heading to a twelfth of a degree, which is a satellite fix in a maze whose
difficulty is being lost in it, while a dot says only "you are in this square" —
what someone mapping it on graph paper would have known. The arrow is kept as the
other entry in the code, since what it records about `func_80028080`'s heading is
measured, but it is no longer a setting: how much the map gives away is a question
about the game rather than about the player's screen. Measured: 144.0 fps and 20.0 ticks/s with all three
viewports drawing. **The map is drawn the way the game's own map is, and that is
the only way it is drawn** (`Map.Style`, `MapRender.DrawNative`;
`KF2_MAP_STYLE=blueprint` is the comparison): the port's first map was accurate and belonged to a
different game — walkable tiles filled pale on near-black under a ruled grid,
which is the docked instrument's palette scaled up, and was reported from play as
not conforming to the game's styles. **The one difference that matters is that the
original does not fill**: its board is one flat slate green and the plan on it is
the *wall* between a walkable tile and a void one, so interiors and the
unexplored field are the same colour and a corridor is two lines rather than a
ribbon. Everything else is sampled off a capture of the map item — field
`#3A523A`, ink `#0E200E`, the other-floor wash `#2D3A2D`, a bevelled frame
highlighting at `#7B8C7F`, and a pale pointer with a `#F78272` boss for the player.
**That pointer turns to the cardinal direction you face**, which is the arrow's
bargain read the other way: the objection was the precision — a heading to a
twelfth of a degree on top of a sub-tile position — not the heading, and a
quarter turn is "north, roughly", what someone holding a paper map in a corridor
knows. The snap is in the game's own angle units (`q = round(yaw / (Turn/4)) mod
4`) off a four-entry cosine table, so the bars stay exactly axis-aligned — which
a marker built out of axis-aligned rectangles needs. **Its shape is traced off
the capture rather than invented**: a blade through the tile, a crossbar and a
salmon boss with a pale pip at the centre, and a third of the way forward a
two-step chevron, which is what a pixel triangle looks like at that size and the
only asymmetric thing in it. The first version was a plain cross, and a cross
rotated still reads as a cross rather than as a pointer. Four things follow from copying rather than
recolouring: the board is **square**, there is **no grid** at any scale, the scrim
drops 0.93 → 0.55 (the game's map is a board held up with the dungeon lit around
it, so a black-out is what would still read as an overlay) and it is black rather
than the map's own now-green ground, and the frame is drawn **inside** the rect it
is given, since both callers hand it the edge of something already clipped. An
edge is drawn by the tile that is *visible*, on each side whose neighbour is not —
asking the 80x80 grid, not the drawing window — so a shared wall is inked once and
a window's edge grows no border. Two parts are readings rather than measurements:
the original's mottled shapes are reproduced as the **other stacked half**, and
the height ramp is kept in the board's green at a fifth of the blueprint's
contrast — and is now **off**, along with the sight-blocking tint, the style
combo and the marker layer: five controls came off the Gameplay page because
none of them was a choice, none reads its saved key any more, and
`KF2_MAP_STYLE=blueprint`, `KF2_MAP_SHADE=1` and `KF2_MAP_WALLS=1` are what is
left of the first three (see "Five map controls that were not choices" in
`docs/PATCHES_AND_MODS.md`). The blueprint is kept in the code, being the picture
the fog, the extents and the marker layer were judged against.
**None of the native style has been
looked at by eye**, and it is now the only map anybody sees. **The minimap is a fully opaque square and can be a circle and semi-transparent,
though nothing in the window says so any more** (its seven controls came off the
Gameplay page with it; the fields keep the values that shipped): opacity
fades the ground and the tiles but never the player's marker, and the circle is
cut **per tile** — ImGui clip rects are rectangles and a draw list cannot erase,
so the usual mask ring would have to be painted opaque, which is the one thing
the opacity setting forbids — clamping each tile to the disc's chords at its far
edges, which scallops the edge by up to a cell and never spills past it. **It needs no hook and writes nothing**:
the area loader `func_8001689C` copies 64,000 bytes to `0x801C8484`, which is an
**80x80 grid of 10-byte tile records** — `tile = 0x801C8484 + 800·z + 10·x`, a
tile spanning 2048 world units, so `tileX = worldX >> 11` — and each record is two
stacked 5-byte halves (a lower floor at `+0`, an upper at `+5`) whose fields are
model index, height, collision flags, collision shape and a flag byte. The 24×24
grid at `0x80192EAC` that `CullCone`/`CullGrid` work on is only a visibility
*window* over it. **Every read happens inside the panels' own `Draw()` and that is
correct rather than lazy**: `LibEtc.VSync` → `PresentFrame` → `HostWindow.Present`
→ `PanelManager.DrawPanels` is one thread, so a panel draws *inside the game's own
VSync call*. The one check that proves the whole chain is that the player stands
on a half the renderer draws and that half's `-(height << 7)` **equals** their Y —
measured gap 0 over three save slots, two areas and both floors. The arrow's angle
is derived rather than guessed: `func_80028080` moves the player by
`(-sin yaw, cos yaw)·d` — confirmed against the attract demo to within 0.6% — and
the world's up is `-Y`, so yaw increasing turns you left. **A map is that plane
seen from above, which puts +Z at the top of the screen**: laying screen Y out
along +Z draws the area mirrored, which is the view from underneath, and it was
reported from play as the arrow swinging right when the player turned left. So
screen Y runs along `-Z` (`Map.RowF`, `Map.RowOf`; `MapRender.Draw` takes rows
rather than tiles) and the arrow's screen angle is `-(yaw + π/2)`, **decreasing**
with yaw. Negating the arrow alone would have matched the report and left it
pointing across the direction of travel, since the mirror was in the map — a
mirror is invisible to any measurement taken inside the mirrored frame. The
`KF2_MAP_PROBE=1` dump is not flipped, being a dump of the grid rather than of
the picture. **The tile grid is only half of a map, and `patches/MapMarkers.cs` is the other
half — which is now off, and not a setting** (`KF2_MAP_MARKERS=1` and the docked
panel's session tick are the comparisons): a live read of where every creature,
prop, spell and torch is standing, through walls, is an instrument rather than a
map, and King's Field's difficulty is not knowing what is round the corner. It
draws what is *standing* in the area, from the **four world tables
`func_800331B4` itself draws from** — creatures `0x8016C544` (200 x `0x7C`, drawn
when `u8[+0x9] == 1`, pos `+0x2C`), objects `0x80177714` (396 x `0x44`, `u16[+0x6]
!= 0xFF`, pos `+0x14`), effects `0x8019CC6C` (128 x `0x48`, pos `+0x14`) and
billboards `0x80195174` (128 x `0x18`, pos `+0x8`) — so it is a third reader of an
already-measured fact rather than a new address hunt. Same properties as the map:
no hook, nothing written, sampled every 50 ms (one sample a tick). **The liveness
test is the renderer's, not the owning stage's**, the distinction `ObjectSmoothing`
was fixed for. **The object table outlives its area and the map must not believe
it**: measured across an area change it reads *258 slots drawn, 0 stepped*, with
the previous area's positions verbatim, because the loader clears `+0x4` and
leaves `+0x6` and the `VECTOR` alone — so the sample is **held** while not one slot
passes `+0x4 != 0xFF`, `Map.Ready`'s shape applied to a second table. In a settled
area the two tests still disagree (area 0: 258 drawn, 139 stepped) and that is
*kept*, since a slot the renderer draws is on screen; the readout labels the rest
`static`. A marker's floor is derived from the drawn half whose `-(height << 7)`
is nearest its Y — the map's founding equality applied to everything else — and
fog gives what *moves* the stricter rule (lit now, not merely remembered).
**What a marker is called is deliberately not claimed**: the object type byte
dispatches through 224 jump-table entries and nothing here pairs an arm with a
noun, so the readout prints the raw type and `KF2_MAP_PROBE=1` censuses all four
tables with a type histogram, which is the instrument for closing that. The one
confirmed identity is the creature's `u8[+0x2]`, the descriptor index `HitGuard`
already uses. **The one exception is the save point, and the game names it
itself**: the use handler `func_800489FC` resolves each object's *definition* the
way stage 2 does — `0x80175914 + u16[rec+0x6] * 0x18`, the `0x18`-stride table of
kinds — and dispatches on `u8[def+0x0]`, not on the behaviour byte; its `0x0E`
arm at `0x80048FEC` packs the entity table into the save buffer and then opens the
slot menu `func_8001C624`, which is the only path in the handler that reaches
`func_80023764` and the memory card. So **kind `0x0E` is a save point** and it is
drawn as a white **S** in the middle of the *room* rather than on the object's own
tile, since a save point stands against a wall like any other prop and an S on it
marks the corner rather than the chamber. **A room is a definition, not a
reading** (`Map.RoomCentre`): the largest solid rectangle of drawn tiles
containing the object's tile, because a flood fill returns the whole floor —
every walkable tile connects to every other through the doorways — while a
rectangle is bounded on four sides at once and so stops at a doorway, degrading
to a long thin one in a corridor. Memoised per tile and cleared on an *area*
change rather than on a grid copy. **The letter is sized to the room too**, since
a cell is 6-22 px and one cell of letter is a speck on a large area's map:
`RoomCentre` returns the rectangle's short side and the S spans `0.55` of it
clamped to 2.0-3.4 tiles, with the halo's offset growing with it. Measured: the
letters sit a mean 2.2 tiles from their objects and draw at 26.4 px on a 12 px
cell, against 12.6 px before. It is the one marker that is a letter, and
**independent of the whole marker layer** rather than only of the object class in
it. That was reported from play as *no save points ever show up on the map*: the
first version was independent of `Objects` alone, and `MapMarkers.Refresh` opens
by clearing the sample when the layer is off, so a player who had turned it off
— which is most of the reason the switch exists, the object squares being the
clutter — got an empty sample no draw could recover (measured `live 0, markers
False`). `Shown` folds `Enabled` in and `Refresh` stands down only on
`!Enabled && !Saves`. The same report's other half was a **size gate that could
not fail**: `DrawSave` clamped the glyph up to nine pixels and then tested
whether it had reached nine, so the fallback was dead code; it gates on the cell
now, below five pixels a tile. Measured after: `4 of 4 lettered on the upper half
at 12.0 px a tile`, with the marker layer off. Never looked at by eye: whether an S lands where the game
actually lets you save. **The save points are the exception and stay on**, being
the one object a player wants a map to find and one the game names itself;
billboards and the creature-facing spoke default off. Measured:
144.0 fps and 20.0 ticks/s at `KF2_FPS=144` with both viewports drawing markers.
Never judged by eye: whether the markers read at minimap size, and whether the
facing spoke points the way the creature does. Two traps are
recorded: **a cleared grid reads as a *full* one** (a zeroed model index is 0,
which is below the drawn threshold, so all 12,800 halves pass — hence
`Map.Ready`), and **`func_8001689C` is not a usable "area changed" hook** despite
owning the copy, because the main loop calls it every frame (5,673 times in 40 s
at 144 fps). Costs nothing measurable: 144.0 fps and 20.0 ticks/s with both
viewports drawing. What has *not* been looked at is the picture — whether the
floor plan matches the area, whether the height shading reads, and whether bit
`0x80` of a half's `+4` is the wall it behaves like or the "see through" the
widescreen notes call it; the full map's hover readout prints all ten raw bytes
for that. **Fog of war is `patches/MapFog.cs`**, and it needs no hook either: the 24×24 grid
ORed into an 80×80 bitset per area per save slot, sampled on `VSyncEvent` — a
wall-clock 60 Hz whatever the render rate is — and kept in `carda.fog` beside the
memory card. Off by default (`KF2_MAP_FOG=1`), for the sub-pixel reason. **A cell byte is not a
boolean, and the note that stood here was wrong about it**: the scanline fill
writes the marker over the whole trapezoid and the flood only clears what it can
prove is occluded, so "nonzero" is the frustum's *footprint* — measured 190 cells
nonzero against 26 actually lit, on a trapezoid that cannot hold more than about
110 tiles. Visible is `byte & 3`, the two bits `func_80031B1C` draws the two
stacked halves on; `KF2_MAP_FOG_PROBE=2` prints the array that settles it. Three
guards keep a lie out of a store that never forgets: the camera must be within two
tiles of the player (the area byte moves before the grid does), the slot and area
bytes must be ones the game means (measured: a record for "area 99" written while
a reload unpacked `buf0`), and a sample whose cone drew nothing is not a view of
anywhere (a New Game before its area is placed reads HP up with everything at
0,0). The slot is the game's own byte, zero until a save or a load has run, so a
New Game accumulates into a scratch bucket that is merged the moment it becomes
1..3. The seam widened from `bool` to **0 unexplored / 1 remembered / 2 in view
now**, since the third state was free. Measured: separate records across a warp and
back, persistence across a kill, and 144.0 fps / 20.0 ticks/s with the minimap
open. **That grid is still not the set of tiles the player can see, and `byte & 3`
only narrowed the lie**: it is a *culling* test, and the flood lights a cell when
**either** of its two ring parents is lit — a 45° spread a ring, which paints an
expanding wedge behind the wall beside a doorway. Harmless as overdraw, permanent
in a store that never forgets, and the reason the map "reveals places disconnected
from the room I am in". So each lit cell is checked against a **recursive
symmetric shadowcast** out of the player's own tile over the 80×80 map — a tile
opaque when it carries no drawn model (`+0 >= 240`, the renderer's own test: the
gaps between rooms *are* the walls here) — and only the intersection is written.
Shadowcasting rather than a ray per cell because a centre-to-centre ray clips a
doorway's corners, and because it is symmetric. **Both stacked halves are cast and
a cell is answered by the one its own bit names**, because asking the game which
floor the player is on fails twice over: the selector at `0x801D9C8E` says upper
in area 5 with the player 4200 units above that floor, and a cast on the wrong
half refused 86 of 93 lit cells including the player's own tile; deriving the half
from the player's Y instead left four of the eight areas with no cast at all. A
window with **no** wall in it is an unloaded map rather than an open field — the
cleared-grid trap the other way up, since model 0 is a *drawn* tile — so that half
is passed: the gate **fails open**, back to the raw cone, never to a blank map.
Measured over a walk through all eight areas at 144 fps: 3993 of 8469 lit cells
refused, the player's own tile revealed on every sample, nothing outside the cast
window, and 144.0 fps / 20.0 ticks/s with the minimap open.
`KF2_MAP_FOG_PROBE=2` prints the grid, the gate's verdict and the walls it read
side by side, which is what makes a refusal arguable; `KF2_MAP_FOG_LOS=0` and the
docked panel's *Sight* tick are the comparison, the gate being a correctness
argument rather than a preference and so no longer a setting. Never looked at by
eye: whether the revealed shape matches where you walked.
See "A dynamic map" in `docs/PATCHES_AND_MODS.md`.

**The port ships its own keyboard layout** (`patches/KeyLayout.cs`), because
RecompOne's defaults are a console's spelled on a keyboard — face buttons on
Z X A S, D-pad on the arrows — and this game walks *and turns* on the D-pad, so
the arrows alone are a tank control. W/S walk, A/D strafe (the game strafes on
L1/R1), the arrows still walk and turn, Space attacks, F uses, Q casts, Tab opens
the menu, and pitch is the mouse's alone. **Changing a runtime default needs no patch to the checkout**:
`ConfigManager.Game.Keys` is settable and `Configure()` runs *before*
`ConfigManager.Load`, which saves the in-memory object when there is no
`settings.json` — so it is a default rather than an override. An existing config
is migrated once, only if every binding in it is still stock, and the fact is
recorded in `interface.ini` (`kf2.keys.layout`). Up and Down carry a **second**
key each — the arrows — which the runtime's one-string-per-button schema cannot
hold, so they are ORed in at `PAD_dr` like the mouse buttons; without them the
in-game menu would scroll on W and S. Changing the layout *after* it has shipped
costs one piece of bookkeeping — bump `Version` and record the old layout in
`Superseded`, or an existing config reads as customised and is never corrected;
that is how v1's swapped attack/use was fixed. See "The keyboard layout" in
`docs/INPUT.md`.

**Widescreen is a patch for the dither reason** — an aspect ratio is a picture the port should be able to offer without a
package having to load, and Video is where a player looks for it — but it is the
one patch that defaults to *doing nothing*, for the sub-pixel reason: the picture
has never been checked by eye. **The page is now one combo and nothing else**: the
three ticks under it were not choices, so the cull widening and the tint stretch
follow the aspect (on the moment one is chosen — the picture without either *was*
checked and was wrong both times), the HUD anchoring is **off**, and none of the
three reads its saved key any more, so nobody is stranded by a value they set when
it was still a tick. `KF2_WIDESCREEN_CULL=0`, `KF2_WIDESCREEN_EFFECTS=0` and
`KF2_WIDESCREEN_HUD=1` are the comparisons. See "Three checkboxes that were not
choices" in `docs/WIDESCREEN.md`. The measurement tools are the mods that are left
under `mods/` — **enable them in the game's Mods panel**, since mods default to
off and load silently when disabled. Prefer them to `KF2_LOG=sdk`, which is
gigabytes a minute.

Widescreen is `Display.WideAspect` and nothing else: the runtime renders a margin
either side of the display buffer and presents it, the projection is untouched, so
the sides show geometry the game submitted and the GPU used to clip — a quarter of
its primitives in an area. The one piece of machinery in `patches/Widescreen.cs`
is a **replacement of `DrawOTag`**, and it is there for the HUD rather than for
the picture: anchoring the HP/MP panel and the equipment icons to the new edges
needs to know which ordering-table entry a primitive came from, and the primitive
event cannot say. **With the anchoring off by default its two-pass walk no longer
runs** — the replacement is a straight call to the original at every aspect unless
`KF2_WIDESCREEN_HUD=1`, which is the path 4:3 always took. That replacement is the reason every other `DrawOTag` hook in
`patches/` is a pre or a post — `HookManager` allows one `Replace` owner per
function. It must also pass the **source address** to `WriteGp0`, or the recovered
GTE depth misses and perspective correction quietly turns itself off whenever the
HUD is anchored. Its other job is the **screen-space tints** — the death fade, the
damage flash, the wash on an area load — which the game draws as one 320-wide quad
and which therefore covered only the middle of a wide picture. All of them come out
of one drawer (`func_8003220C` fills a request block, `func_8003214C` submits
`func_80031EE8(0,0,320,240)`), but the fix is keyed on the *shape* — semi-transparent,
flat, full clip width, snapped out to the margin in the primitive listener — so that
OPEN.EXE's and END.EXE's own links of the same drawer need no addresses. Opaque
full-screen pictures (titles, menus) are left at their authored width on purpose.
See "Widescreen" in `docs/WIDESCREEN.md`.

**The cull the margin runs into is `patches/CullCone.cs`**, and it is the other
half of widescreen rather than an option beside it. The game gates every object
and every geometry block on a **24×24 byte grid of tile visibility** at
`0x80192EAC`, rebuilt each frame by `func_8002D3A8` as a top-down trapezoid — the
4:3 frustum flattened onto the map — whose corners are seven `s16` pairs in
GAME.EXE's data at `0x80068760`. Widening the picture without widening that leaves
the sides showing only what the game happened to overdraw. Two things about it are
load-bearing: the 24×24 window fits the shipped cone *exactly* (`0/60 frames reach
the grid edge` at stock, measured), and the game's scanline fill
(`func_8002CF0C`) scans in from both sides, so a row whose edge left the grid gets
**no fill at all** — widening the table alone loses whole ranks of tiles. Hence the
post-hook that re-rasterises the recorded edges and fills what the game dropped.
The 24-tile window is stride and bounds baked into nine routines, so growing it is
a reimplementation of the visibility system whose only correctness check is a
person looking at the screen; `KF2_WIDESCREEN_CULL_PROBE=2` measured what that
would buy and the answer was **3.5% of lit tiles, at the far corners** — binding,
barely, and not worth it. See "The cull the margin runs into" in
`docs/WIDESCREEN.md`.

**The attract demo is a free live session**: leave the port at the title and it
walks itself into an area about a minute later, with a character, an HP bar and
(eventually) a death. That is how in-game behaviour gets tested without a human
driving the menus — `AutoReload.Simulate()` kills on demand from there, and the
death clock at `0x8019951A` can be pinned to hold any frame of the death sequence
still.

**Getting an agent into the game — and driving it once there: `KF2_AUTOSTART`,
`KF2_AGENT`, `KF2_SHELL`.** An agent left
at the title waits forever — the boot menus take no input by the usual routes
(`KF2_AUTOPAD` only arms once an area has loaded, the very thing that has not
happened), and the screen must not be scraped. `KF2_AUTOSTART=<1..3>` drives the
pad itself through `PAD_dr`: Start through the intro, Cross to start a New Game
into `fdat02`, then loads the chosen slot over it through `AutoReload.LoadSlot`,
landing in the save's own area in a few seconds. `KF2_AGENT=1` prints a
machine-readable `[KF2-AGENT]` line on each overlay change and about once a second
(`{"overlay":…,"inGame":…,"hp":…,"area":…,"slot":…}`) — `inGame:false` is how a
program tells "stuck at the title" from "in an area" without a screenshot. See
"Auto start and the agent beacon" in `docs/PATCHES_AND_MODS.md`.

**`KF2_SHELL=1` is the acting half**: while the session runs, a line protocol on
TCP 127.0.0.1:27900 (`state`, `nearby`, `load <slot>`, `warp <area>`,
`press <button> [ms]`, `kill`, `ending [boss|kill]`, `map [on|off|toggle]`; one
request per line, one single-line JSON response back) steers the game
the beacon is only watching. **`ending` is there because the last ten minutes of
the game cannot be loaded into**: plain `ending` writes `GAME.EXE`'s own quit
word at `0x80199574` (1 = `END.EXE`, 9 = title), and `ending boss` runs the
post-final-boss sequence that normally writes it — `fdat23`'s `func_8019F474`
then `func_8019F688`, two modal loops that present their own frames, both
needed because the first fills the pointer at `0x801A0598` the second writes its
camera through. **`ending kill` is the form that reproduces the final-boss
crash**, because that crash is in the hit resolution *underneath* those loops,
which `ending boss` never puts on the stack: it replays `fdat23`'s damage hook
`func_8019FA2C` and then the death reaction `func_8003A490` that
`func_8003A9CC` makes next. The ending blanks six entity type bytes to `0xFF`
on its way out and its caller then indexes the descriptor table with one, which
is a game bug the console absorbed as an open-bus read; `HitGuard` fences the
walk `func_8003A448` so it answers "no reaction" instead of faulting. See "The
crash on the final boss's last hit" in `docs/TODO.md`. Reaching it needs `KF2_DEBUG_GODMODE=1`, or `warp 7` kills the
player on the way in. See "The command channel" in
`docs/PATCHES_AND_MODS.md`.

`KF2_AUTOPAD` reproduces an input-triggered bug without a human at the keyboard;
its clock starts when the first area module loads, which is the only point in the
boot sequence that reliably means "in game". `KF2_LOG=bios` is very expensive
during play — the game polls `PAD_dr` hundreds of thousands of times a second,
which is gigabytes of log per minute.

**For a hang, take the managed stack of the live process instead of adding
logging.** Recompiled functions carry their MIPS address in their name, so the
trace names the routine directly:

```bash
~/.dotnet/tools/dotnet-stack report -p $(pgrep -f net10.0/KingsField2)
```

Start the game from the same shell you run that in, or the diagnostic socket in
`TMPDIR` will not be found.

`Program.cs` is hand-owned (RecompOne would otherwise generate one into
`generated/`); add new env-var-driven diagnostics there. Note the docs mention
`KF2_TRACECALL` — that was an ad-hoc local edit to the dispatcher and is *not* in
any committed patch; re-add it by hand if you need indirect-call tracing.

### Regenerating function maps

Only needed if the sweep is wrong or a new executable is added:

```bash
RC="dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build --"
$RC --generate-function-file -linear-sweep -disc disc/KingsField2.cue \
    -file OPEN.EXE -base 80011000 -skip 800 -out config/funcmaps/open.json
```

An `unmapped call: 0x…` has three causes, so check which it is before reaching
for a script. **Is the address a function start the map is missing, an address
inside a function that already exists, or a few instructions past a mapped start
that has no prologue?**

- Missing start — `scripts/add_call_targets.py` recovers it by harvesting `jal`
  targets and splicing them into the map. Only sites already inside a known
  function are harvested; a `.data` word that decodes as `jal` is not a call.
- Interior address — the sweep split one real function in two, because it ends a
  function at any `jr`/`j` plus delay slot and PSY-Q emits both *inside* a
  function (a `jr` through a switch table, a `j` to a shared epilogue). A
  conditional branch that crosses a boundary proves it, since MIPS branches never
  leave their function. `scripts/merge_branch_spans.py` merges on that proof (and
  on jump-table entries), checks nothing `jal`s a start it swallowed, and is
  idempotent — run it after any sweep. See "The sweep splits a switch" in
  `docs/RECOMPILATION.md`.
- False split from data — `add_call_targets.py` once treated a table word as
  `jal` and cut a real function. The crash address sits just past a mapped start
  that has no prologue and that nothing in code `jal`s; the previous function
  falls through into it. `merge_branch_spans.py` cannot see a fallthrough, so
  rejoin by hand (the previous start's size should reach the next real function).
  See "add_call_targets can split a function" in `docs/RECOMPILATION.md`. The script no longer
  harvests sites outside a known function, so re-running it will not re-cut.

```bash
python3 scripts/merge_branch_spans.py --dry-run   # all overlays, writes nothing
python3 scripts/merge_branch_spans.py fdat17
```

The FDAT overlays deliberately use `"skip": 0` so the overlay covers the module
header: the modules' switch jump tables live in it, past the 32 dispatch slots,
and the recompiler reads a jump table out of the overlay's own bytes. Raising
`skip` past them makes every `jr` through a table dispatch to nothing at run time.

## Architecture

```
config/kf2.json          recompiler config: overlays, funcMaps, stubs[], patches[]
config/funcmaps/*.json   swept function maps (address/name/size; size is mandatory)
patches/                 hand-written C# replacing recompiled functions
mods/<id>/               runtime-loaded mods (mod.json + C#, Roslyn-compiled)
mcp/                     stdio MCP server exposing the KF2_SHELL command channel as tools to MCP hosts
Verdite2.Launcher/       the SHIPPED executable; builds with no disc, and makes the
                         game at first run from the player's own image. See docs/PACKAGING.md
packaging/               AppImage and Windows packaging, plus placeholder icons
patches/recompone/*.patch  local fixes to the RecompOne checkout itself
generated/               recompiler output (gitignored — derived from copyrighted disc data)
scripts/*.py             disc inspection, address-hunting, and the rate tooling:
                         merge_sdk_names (write the PSY-Q names a signature
                         match found into config/funcmaps/, refusing the ones
                         SdkPatches would bind -- see "Merging the SDK names" in
                         docs/RECOMPILATION.md),
                         rate_census (which words move at the render rate),
                         find_writers (which code moves them), rate_matrix (did
                         the fix work), check_gate (does the gate obey its rule).
                         kf2run/callgraph/kf2model are their shared halves.
                         See "Finding the rate defects" in docs/DEVELOPMENT.md
Program.cs               hand-owned entry point; calls Entry.Run(PSMemory, cuePath)
```

The disc holds a 4 KiB boot stub (`SLUS_001.58`, named by SYSTEM.CNF) plus three
real executables — `OPEN.EXE` (title/intro), `GAME.EXE`, `END.EXE` — which **all
load at `0x80011000`** and are mutually exclusive. They are declared as
**overlays** in `config/kf2.json`. Left alone the recompiler would only find the
2 KiB stub.

Because the three overlays share an address range, *every* address-based config
entry must name its overlay explicitly. Prefer a named overlay over `"*"`.

### The core problem: SDK functions must be mapped by address

The recompiler's `SdkPatches.cs` binds PSY-Q calls to the runtime's HLE by exact
**function name**. A linear sweep names everything `func_800xxxxx`, so it always
reports `applied 0 reimplementations`. Every SDK entry point therefore has to be
mapped by address in `patches[]`:

```json
{ "overlay": "open", "address": "0x80016078",
  "target": "RecompOne.Runtime.Sdk.LibGpu.DrawOTag", "mode": "replace" }
```

63 such patches exist today (`libetc`, `libcd`, four `libgpu` entry points, six
`libcdstream`, libapi's `DMACallback`). `libpad` is the notable gap — and a
signature match confirms it is not a gap at all: the game links no `libpad`, which
is consistent with it reading `PAD_dr` through `BiosB` instead.

**997 non-binding functions now carry their real PSY-Q names** (`rsin`, `rcos`,
`SsSetMVol`, `RotTransPers`...), from upstream's signature bank via
`scripts/merge_sdk_names.py`. That is legibility only: the script **refuses every
name `SdkPatches` binds**, reading that list out of the patched checkout rather
than copying it, so all 63 entries above still do the binding and the recompiler
still reports `applied 63 patches, 0 reimplementations`. The generated C# was
verified identical apart from identifiers. It needs no pin move —
`--autoconfigure` is a standalone command. See "Merging the SDK names" in
`docs/RECOMPILATION.md`. Only map a
function the runtime actually implements — check
`tools/RecompOne/RecompOne.Runtime/sdk/Lib*.cs` first; unmapped library routines
run fine as recompiled MIPS because `PSMemory` traps their register writes.

`mode` is `"replace"`, `"pre"` or `"post"`. **Use config patches for `replace`
only** — binding an SDK entry point, which has to happen before any mod could
load. For anything else, do not add a config entry: RecompOne's `HookManager`
detours a recompiled function by address at run time, so a hook needs neither a
config entry nor a recompile. See "Mods" in `docs/PATCHES_AND_MODS.md`; `patches/FramePacing.cs`
is the in-project example and `mods/` holds the loadable ones.

Watch the namespace — generated code is `Recompiled.KingsField2`, a *class* named
after the project, which shadows any namespace called `KingsField2`.

### Identifying an address: the techniques that work

In rough order of payoff (full reasoning and worked examples in
`docs/RECOMPILATION.md`):

1. **The overlay delta.** The three executables are three links of the same
   libraries, laid out at a constant offset *per translation unit* (not per
   library — `libcd`'s `cdio` and `stream` differ from each other). Identify once,
   derive the other two. `scripts/match_overlays.py` does this mechanically
   against a relocation-insensitive normal form; validate any new delta by
   re-deriving already-known addresses with it.
2. **Data-side search for hardware addresses.** PSY-Q reaches I/O through pointer
   tables in `.data`, never through literals in code — there are five
   `lui …, 0x1F80` instructions in all of `OPEN.EXE` and none is the GPU. Search
   the *data* for `0x1F801810` (GPU) or `0x1F801800` (CD) to find the table; the
   functions loading through it are the library.
3. **Diagnostic strings.** `func_80014C0C` is the `printf` thunk (BIOS A(3Fh), 69
   call sites). PSY-Q error text (`VSync: timeout`, `CdInit: Init failed`,
   `GPU timeout:QUE=…`) names the calling function for free.
4. **Struct offsets as evidence.** Two independent offsets agreeing (a `DR_ENV`
   packet at `env+0x1C` *and* a `0x5C`-byte copy) turns a guess into an ID.
5. **Indirect-call tracing.** Public entry points can have zero `jal` references
   because PSY-Q dispatches through driver tables filled in at init (`libgpu`'s
   15-slot table, libapi's `DMACallback`). Log the address in `Dispatcher.Call`
   rather than searching statically.

### Two traps

**Number bases differ between the config and the CLI.** In `config/kf2.json`,
`base` is a hex *string* while `size`/`skip`/`offset`/`lba` are decimal numbers;
on the `--generate-function-file` CLI, `-size`/`-skip`/`-offset` are hex.
`"skip": 2048` in the config is `-skip 800` on the command line.

**Overlays are read as raw bytes.** `ResolveOverlay` does not parse the PS-X EXE
header, so every `.EXE` overlay needs `"skip": 2048` to step past the 0x800-byte
header, and `base` must be the header's real text address (read it with
`scripts/extract_file.py --header-only`). The *boot* executable is different — it
goes through `Psx/Parser.cs`, which strips the header itself.

### csproj

`generated/` and `patches/` are picked up by the SDK's default globs — adding an
explicit `Compile Include` causes NETSDK1022. `tools/**` must stay explicitly
removed, or the build compiles RecompOne's own sources and its `obj/`
AssemblyInfo files (CS0579).

## The RecompOne checkout

**`tools/RecompOne/` is vendored: its sources are tracked here, so an edit made
inside it is a change to this repository like any other.** It used to be a
gitignored clone of an upstream pin with `patches/recompone/*.patch` replayed
over it on every run, and the patches are *kept* — they are no longer replayed.

**Why that changed, because the reason generalises.** `git apply` matches text
context and knows nothing about what upstream changed, so upstream's Rider
reformat (`410f0d4`) broke 28 of the 39 patches at once — and would have broken
them again on every future pin move, because a diff is permanently written
against context that has to still be there. A vendored fork has a **merge base**,
and a three-way merge reasons about changes rather than appearances: the reformat
is absorbed once, as a commit. Measured: taking one real upstream commit
(`67fc37c`, 23 files) costs 23 conflict hunks as a merge, against hand-authoring
a ~700-line patch carried for the life of the project — which is exactly what
`0034` (1,315 lines) and `0037` (2,479 lines) already were. **In the patch
workflow every gift from upstream becomes permanent debt.**

  - `tools/RecompOne/UPSTREAM` — the upstream commit this tree was merged from,
    and so the merge base for the next harvest. Currently `0409bc2`.
  - `tools/RecompOne.git/` — the fork's own history: the 39 patches as commits,
    the merge, and the upstream remote. Gitignored and rebuilt on demand, so a
    fresh clone needs none of it to build or play. Reach it with
    `git --git-dir=tools/RecompOne.git --work-tree=tools/RecompOne <cmd>`.
  - `bash scripts/setup_tools.sh --sync-upstream` — fetch upstream, list what is
    new, and leave a three-way merge in the tree to resolve. A single commit is
    `cherry-pick -n <sha>` through the same git-dir.

**What the merge to `0409bc2` decided is the model for the next one.** Upstream
wins on structure and on anything it has since implemented itself; the port wins
on behaviour, and nothing of the port's is dropped without evidence that upstream
carries the same code. Four patches collapsed into upstream's own: `0037` (all 14
CHD files byte-identical to `137a793`), `0034` (our `Runtime/Pgxp/` differed from
upstream's `Gpu/Pgxp/` only by the reformat — one directory now, with `0036`'s
`PgxpStats` beside it), `0033` (upstream's `FontSet` verbatim; the 16.5 MB CJK
face is simply not embedded, so the load is inert and the file will never
conflict again) and the GTE transform ring, `PushPrecise` and `Nclip`. Four were
kept because upstream converged differently and worse for this game — `0006`,
`0026`, `0035`, and the vblank timeline below. **Four were kept whole because
upstream deleted what the port needs**: it removed the software rasterizer and
does perspective correction with its own shader attribute, so `GpuRaster`,
`GlCore`, `GlShaders` and `GpuHleForward` stay one unit. Upstream's VRAM-dirty
tracking and its PGXP `ResolveAmbiguous` are left unharvested on purpose and are
the obvious next thing to take.

**The one that had to be put back by measurement is `0005`.** The merge took
upstream's rewritten `LibCd` whole, on the theory that its new IRQ and callback
pump subsumed it. It does not: upstream signals a CD interrupt only through the
sync/ready/data callbacks and has no `DeliverEvent` on `HwCdRom` at all, and
King's Field's loader is event-driven (`EvMdINTR`). Measured before the graft —
`GAME.EXE` loads, no `fdat` module ever does, the agent beacon reads `hp 0` at
`pos 0,0,0` forever, and `LoadPacing` reports a disc wait open for over 30 s.
After — `open → game → fdat02 → fdat05`, slot 2 restored at hp 46/86 in area 1,
144.0 fps drawn at 20.0 ticks/s, no exceptions and every hook attached. **That
run is the acceptance test for any future merge — and it is not sufficient on its
own, because every number in it was still true with a completely black window.**
The merge also moved presentation onto upstream's `Runtime.Run`/`PresentLoop`,
which this port never enters (`Program.cs` calls `Entry.Run` directly and presents
from inside the game's own `VSync`), and wrapped the GL backend in upstream's
`InterpBackend`, which records primitives into a `FrameGraph` that only
`PresentLoop` replays. Two independent ways for a frame to reach no screen, with
nothing thrown and the whole game running normally underneath. `PresentFrame`
calls `HostWindow.Present(Gpu)` again and the GL backend is used unwrapped, so
`Interp.Backend` stays null and frame interpolation stays uncarried. **The rate is
measured from inside the game — a `DrawOTag` after a `VSync`, neither of which
touches GL — so pair it with `KF2_PRESENT_PROBE=1`,** which reads `wide 288, plain
0, vram fallback 0` when `PresentDisplay` is reached and prints nothing at all
when it is not. See "The window went black" in `docs/RUNTIME.md`.

**The second thing the merge broke silently is `0012`'s address map.** Upstream
added RAM fast paths to `PSMemory.ReadU32`/`WriteU32` — an `Unsafe` access
straight into the array, taken by every `lw` and `sw` the game makes — which
return before the slow paths where `GteVertexMap.NoteRead`/`NoteWrite` live. This
mechanism *is* following a value through the game's `lw`/`sw`, so skipping the
hooks skips the mechanism: nothing bound to an address, every `TryGet` a miss, and
both halves quietly falling back to what a miss means — **affine textures and
whole-pixel vertex wobble, with `[KF2] perspective: on` still printed at boot**.
The hooks are offered from the fast paths now, on the same `GteVertexMap.Active`
gate. The counter that named it is `KF2_PERSPECTIVE_PROBE=1` reading `0 caught/s,
0 copied/s` beside a healthy `projected/s`; measured after, in area 2 at 144 fps,
93.0-93.7% hit, inside the band this was first measured at. See "The RAM fast path
went round both hooks" in `docs/RENDERING.md`.

**The third is `0022`-`0024`'s background clear, and it is the one that was
visible.** `LibGpu.PutDrawEnv`'s `isbg` rectangle is the *only* thing that paints
the widescreen margin every frame — `GlCore` writes back and re-syncs a target's
middle `W` columns only, so the margin columns live nowhere but in the render
target and are otherwise reached only by geometry that spills past the game's own
320-wide clip. Upstream has no margin, so its clear covers `clipW` where the
port's covered `clipW + 2*margin`, and the merge took upstream's. Without it the
margins accumulate every primitive that ever crossed the edge and never lose one:
reported from play as ghosting that **persists while standing still**, **only
gains content as you move**, and keeps a damage flash's red **permanently** —
`Widescreen.Stretch` widens that tint across the margin by design, so the flash
reaches out there and then nothing ever washes it off. No setting touches it
because it is not a setting; only going back to 4:3 removes the margin that is
accumulating. See "The margin's only clear is the game's own" in
`docs/WIDESCREEN.md`.

**`patches/recompone/` is still the record of what the port changed and why**,
and the numbering below is still how each change is referred to in the source.
Thirty-seven of the forty-one are load-bearing; `0002`, `0003` and `0015` are
diagnostics and `0013` is a settings-placement hook. **One patch has an asset
beside it**: `patches/recompone/assets/` holds the TTF `0033` embeds, which is
now simply a tracked file in the vendored tree.

**Upstream 0409bc2 emits one class per overlay** (`Recompiled.KingsField2_game`
rather than `Recompiled.KingsField2`), because CoreCLR caps a class at 65535
methods. The fourteen direct static call sites in `patches/AreaWarp.cs`,
`patches/AutoReload.cs`, `patches/CullGrid.cs`, `mods/kf2debug/Noclip.cs` and
`mods/kf2debug/Attributes.cs` carry a one-line `using KingsField2 =
Recompiled.KingsField2_game;` alias instead of being rewritten — every function
named in them is GAME.EXE's, so the alias names the overlay once. `MenuRegistry`
also lost its numeric ordering for anchor-by-name, which is the one thing that
broke `mods/kf2debug`.

**Historical, and kept because the finding outlives the mechanism.** What follows
describes the replay loop `setup_tools.sh` no longer has. It is the clearest
statement of why a diff stack cannot be maintained against a moving upstream,
which is the argument the vendoring rests on.

`setup_tools.sh` **does** rebuild the checkout on this branch, and that used to be
false: `0021-true-color-24bit-output.patch` was authored while
`lighting-experiments`' `0025`/`0026` were applied, so its hunks quoted
`_uCoplanarTol` / `_uLitCenter` context that exists only there and `git apply`
rejected them, leaving the tree at `0020`. The patch has been regenerated against
this branch's context. Verified by applying all thirty-three patches in glob order
to a pristine worktree of the pin: every one applies, and the result is
byte-identical to the tree in place. (`0032` and `0033` were added after that verification
and are checked the same way — two consecutive `setup_tools.sh` runs, the second
still reporting `applied` after a clean peel.)

`setup_tools.sh` **peels the stack off newest-first before applying it
oldest-first**, rather than asking each patch on its own whether it is already
applied. A per-patch reverse-check breaks the moment one patch edits lines another
added — `0010`, `0011`, `0012` and `0014` all edit the `GteDepth.cs` that `0009` creates — and the
symptom is `0009` being reported as "FAILED TO APPLY (upstream likely changed)" on
the second run of a script that is supposed to be idempotent. Undoing in the
opposite order to applying has no such problem. A patch that will not reverse stops
the peeling rather than being forced, so a fresh clone peels nothing and an
uncaptured edit inside the checkout is left where it is.

- `0001-bios-load-return-1.patch` — BIOS `Load` must return 1, not the header
  pointer. Without it the boot stub spins in the loader forever.
- `0004-libapi-dma-callbacks.patch` — adds `Sdk.LibApi` so DMA-completion
  callbacks run at all. A static recompilation has no exception path, so PSY-Q's
  IRQ-3 handler never runs and every DMA callback silently dies. Nothing errors;
  the work the game does *inside* the callback just disappears. Keep this in mind
  whenever something completes but produces no visible effect.
- `0005-libcd-interrupt-driven-reads.patch` — the polled read path and CD-ROM
  kernel events; without it the game hangs on the loading screen.
- `0006-irq-callback-table.patch` — the runtime otherwise derives the PSY-Q
  interrupt-callback table from the `HookEntryInt` argument, which for this game
  lands in game data and eventually calls a data word. `Program.cs` supplies the
  real per-overlay address; the patch also makes the derived path refuse a
  handler that is not a known function.
- `0007-pad-poll-outside-frame-loop.patch` — host input used to be polled only
  inside `PresentFrame`, so a game busy-waiting on the pad without vsyncing read
  a frozen snapshot forever. King's Field's screen transitions all begin with
  such a wait; this is what hung the in-game menu.
- `0008-unload-overlapping-overlays.patch` — `HandleRegionOverwrites` only
  dropped an overlay fully contained in the new one. `END.EXE` is smaller than
  `GAME.EXE` at the same base, so GAME's functions past `0x8003A000` stayed
  mapped after the ending loaded. Any overlap is now an overwrite.

- `0009-perspective-correct-textures.patch` — the GPU is handed polygons with no
  depth in them, so it can only interpolate U and V linearly and every texture
  swims. The depth still exists one step earlier: `Gte.Rtp` produces the screen
  position and the view depth in the same call, and that screen position is
  bit-for-bit what reaches the GP0 packet. `GteDepth` keys a small table on it, so
  the two halves are reunited without following a register or a store. A miss is
  the old affine behaviour, which is what makes it safe on by default. See
  "Perspective correction" in `docs/RENDERING.md`.

- `0010-subpixel-vertex-positions.patch` — the GTE projects to 16.16 and then keeps
  only the whole part, so a vertex drifting slowly holds still and then jumps a
  pixel and its polygon twitches. The fraction is the low sixteen bits of the same
  expression `0009` takes the depth from, so `GteDepth` carries both and serves them
  independently. The hardware backend needed nothing — its vertex position was
  always a float — and the software rasterizer now works in sixteenths of a pixel
  for any triangle that recovered a fraction, which scales its edge functions and
  its area by 256 and changes no ratio taken from them. See "Sub-pixel vertex
  positioning" in `docs/RENDERING.md`.

- `0011-gte-depth-collisions.patch` — screen position is not a unique key, and
  dropping saturated vertices made every large nearby polygon fall back to affine
  (the texture looking as if the camera jumped). The table keeps several samples
  per pixel, records the clamp for depth only, and picks per primitive; a leftover
  far Z is refused rather than applied. Positions stay on the packet — moving a
  clamped vertex to its true projection opened holes. See "The table is not unique"
  in `docs/RENDERING.md`.

- `0012-exact-gte-vertex-map.patch` — screen position was never an identity, so
  `0011`'s picking between samples was scoring a collision rather than avoiding one,
  and a wrong W throws a texture across the screen. `GteVertexMap` keys on the
  **address** the coordinate is stored at instead: a `swc2` publishes the depth and
  the fraction, a store binds them to its destination, a load of a bound address
  republishes them so they follow the game's whole-word `lw`/`sw` into the packet,
  and `DrawPolygon` asks by the address `DrawOTag` read the word from — verifying the
  word before answering. No codegen change, so **this one needs no recompile**. The
  old table stays behind `KF2_PERSPECTIVE_FALLBACK` for comparison only. See
  "Following the value through memory" in `docs/RENDERING.md`.

- `0013-settings-slot-in-section.patch` — `SettingsRegistry.Extend` only draws
  *after* a section's whole body, so a port option that belongs beside one of the
  runtime's own controls could only ever land in a block underneath the lot. This
  adds `SettingsRegistry.DrawSlot(slotId)` and one call to it in the display
  section, after the render scale, which is where the widescreen aspect goes.
  Register with `PatchSettings.RegisterSlot`, not `Register`. UI only — **no
  recompile**.

- `0014-gte-zbuffer.patch` — a depth buffer from the same recovered SZ3
  perspective correction already follows through memory. The GPU has none, so
  intersecting surfaces take turns in front of each other on the ordering table;
  both rasterizers now test the recovered view depth per pixel. Window depth is
  a fragment value, not clip-space Z, so OpenGL does not far-clip the already-
  projected triangle. A miss is painter's order, so the HUD is untouched. Off
  by default. See "Z-buffer" in `docs/RENDERING.md`. **No recompile** — the lookup is
  the one `0012` already does.

- `0015-zbuffer-occlusion-census.patch` — diagnostic behind `KF2_ZBUFFER_PROBE=2`.
  Every polygon's bbox, depth range, table position and flags for the window, the
  `DrawOTag` walk position (`GteDepth.OtEntry`, counted from the far end — so
  `Widescreen`'s replacement of `DrawOTag` has to publish it too), and a 32×16 map
  read back from the depth attachment. It reads the RT the depth batches went to,
  not the presented one (last frame's, under double buffering) and not the most
  recently drawn (a fill stamps `LastDrawFrame` too, so that can be a buffer just
  cleared). **No recompile.**

- `0016-zbuffer-clear-at-frame-head.patch` — `PresentDisplay` incremented `_frame`
  before its trailing `Flush`, so the depth clear (keyed on `LastDrawFrame !=
  _frame`) fired on the tail of the *outgoing* frame and was then skipped at the
  head of the next one, which inherited the last batch's depths. Nothing rescues
  it — `isbg=0` here, so no game-side fill reaches `FillRtFull`. Swapping the two
  statements took the depth-map readback from 67-of-91 empty to 11-of-11
  populated. Real and measured, but **it did not cure the sky showing through
  nearby walls** — a second cause remains. **No recompile.** See "The clear
  landed at the tail of the frame" in `docs/RENDERING.md`.

- `0018-imgui-fractional-framebuffer-scale.patch` — Silk's `ImGuiController`
  computes `io.DisplayFramebufferScale` by dividing two `int`s, so a compositor
  running a display at a *fractional* scale (KDE's 1.15) truncates to 1 and
  `RenderImDrawData` sizes its GL viewport and every scissor from the logical
  window instead of the framebuffer — the whole interface lands in the bottom-left
  corner, with dead margins top and right. Recomputed as a float between
  `Update()` and `Render()`, which is the only window where it is read: layout is
  already fixed and still logical, so **input is untouched**. An integer scale
  divides exactly, which is why a 1:1 monitor never shows it. Its sibling defect
  is ours and unfixed — `QueryDpiScale()` reads the *primary* monitor's content
  scale once at startup, and GLFW's Wayland path returns the integer `wl_output`
  scale, so a 1.15 monitor reports 2.0 and the chrome is oversized on both
  screens. **No recompile.** See "The interface only fits a monitor whose scale is
  a whole number" in `docs/RUNTIME.md`.

- `0017-mouse-capture-and-motion.patch` — `InputManager` owns the `IMouse` and is
  `internal`, so a port could not reach the cursor at all. Adds `MouseCaptured`
  (`CursorMode.Raw`, or `Disabled` where raw is unsupported — both make GLFW
  report an unbounded virtual position, which is what turns successive positions
  into motion), `TakeMouseMotion` and `IsMouseButtonDown`, forwarded from
  `HostWindow` beside the `IsKeyDown` that already plays that role for the
  keyboard, and gives the cursor back in `Shutdown`. Everything else about mouse
  look is `patches/Mouse.cs`. **No recompile.** See "Mouse look" in
  `docs/INPUT.md`.

- `0019-popups-cannot-leave-the-window.patch` — every popup is centred and pinned
  with `SetNextWindowPos`, which is the flag that suppresses ImGui's own clamp
  into the viewport, and its size (`Size * Theme.Scale`) is capped against
  nothing, with `NoResize`, `NoMove` and `NoScrollWithMouse` closing the ways
  back. The 780x500 settings popup therefore outgrows a 1280x720 window at a
  `Theme.Scale` of 1.44 and takes the UI-scale field — the one control that would
  undo it — off-screen with it, permanently, since the value is saved. Reachable
  from the slider alone (0.5-3), and reached at `UiScale` 1 on the monitor whose
  `DpiScale` misreads as 2.0. The size is now clamped to the viewport, so an
  oversized scale costs scrolling instead of the controls, and `Debug > Reset
  view` re-applies `FontGlobalScale` and `Theme` instead of leaving giant text
  behind small windows. **No recompile.** See "The scale can put the settings out
  of reach" in `docs/RUNTIME.md`; `patches/UiScale.cs` (`KF2_UISCALE`) is the
  port's own way back for a config already past that point.

- `0020-theme-apply-compounds-the-style.patch` — `Theme.Apply` ends in
  `ScaleAllSizes`, which multiplies *every* size field, but only resets some of
  them first, so each accent, background or scale change multiplies the rest
  again: measured, `WindowMinSize` 32 -> 44 -> 88 -> 528 -> 1056 over five calls.
  ImGui floors every non-child, non-`AlwaysAutoResize` window at `WindowMinSize`
  **after** applying a size constraint, so that overrides `0019`'s clamp and the
  popup grows off the bottom of the screen — a 1264x704 clamp measured coming out
  1264x1056 on a 1280x720 viewport. `Apply` now restores the style ImGui built
  before re-theming, which stays correct whatever upstream adds to
  `ScaleAllSizes`. Latent since long before `0019`; changing the *accent*
  compounds it too. **No recompile.** See "The scale can put the settings out of
  reach" in `docs/RUNTIME.md`.

- `0021-true-color-24bit-output.patch` — the console renders into 15-bit VRAM, so a
  smooth shaded fog gradient bands into 32 levels unless the ordered dither hides
  it with a crosshatch. Two things enforce the truncation: the `GlDisplayRt` colour
  attachment is `Rgb5A1`, and the fragment shader's `quant5` ends in
  `min(c8 >> 3, 31) / 31.0`. Under `GteDepth.TrueColor` the attachment becomes
  `Rgba8` and `quant5` keeps eight bits, so the gradient is smooth without the
  crosshatch. Textures stay 5-bit (they live in 15-bit VRAM), so only the shaded
  gradient gains precision; the writeback/present blits convert automatically.
  GL backend only — the software rasterizer is always 15-bit. Off by default (the
  authentic look). `patches/TrueColor.cs` (`KF2_TRUECOLOR`) is the switch and
  `patches/settings/ShadingPage.cs`'s combo is where it is chosen. **No recompile** —
  render-target format and shaders are runtime. See "True color" in
  `docs/RENDERING.md`.

- `0022-present-stale-wide-target.patch`, `0023-splash-margin-idle.patch`,
  `0024-margin-content-latch.patch` — the present gate. `PresentDisplay` picks a
  wide render target only when its margin columns have carried a world, so a scene
  that never draws out there (the MDEC boot splash) keeps its authored width
  instead of flapping between widths. Latching is per target, and it is cleared
  when an **executable** loads — from `patches/Widescreen.cs`, not from
  `Dispatcher.Load`, which fires for the `fdat` area modules too and put the black
  bars back for the length of every area transition. **No recompile.** See "The
  present gate" in `docs/WIDESCREEN.md`.

- `0025-frameclock-target-rate.patch` — `FrameClock.FrameMs` was a `const` 60 Hz,
  and it is the *host* rate: the emulated vblank grid (`LibEtc.VBlankMs`) and every
  game clock hanging off it are a different 60 that must not move, or the music
  speeds up. Now a settable `FrameClock.TargetFps` (0 = off), exposed as
  `Runtime.TargetFps` because `FrameClock` is `internal`. It still cannot be a
  frame pacer — it throttles per `VSync` *call* and a frame carries two — so the
  port hands it a permissive ceiling and keeps its own deadline at `DrawOTag`.
  **No recompile.** See "There were three fixed 60s" in `docs/RUNTIME.md`.

- `0026-str-pacing-without-a-latch.patch` — an STR movie is paced by the disc:
  sectors arrive at 150 a second, a frame is 9-14 of them, and the game's display
  loop blocks in `StGetNext` until one is complete, so the loop's own rate never
  enters into it. `StreamLoop` modelled that, but only once a latch tripped — two
  decoded frames sitting in the 32-slot ring at the same time — which for the third
  intro movie's 13-14-sector frames never happened, because the game drains a frame
  as soon as it is ready. Unthrottled, that movie played at the **render rate**
  instead: measured ~56 frames a second at `KF2_FPS=60` and ~93 at 144, against the
  10 the disc holds it to. Paced from the start of the stream now, since there is no
  free burst on hardware either. **No recompile.** See "The intro movie ran at the
  render rate" in `docs/RUNTIME.md`.

- `0027-commit-each-hook-independently.patch` — `HookManager.Commit`'s `foreach`
  installed each detour with no guard, so the first `new Hook` that threw abandoned
  every function after it in the dictionary. Silently: a patch that hooks from an
  `OverlayLoadedEvent` listener — which is all of them, since `SymbolRegistry` is
  only readable once the dispatcher's overlay tables are registered — has its
  exception swallowed by `Event.Dispatch` into one stderr line. Losing
  `FramePacing`'s frame boundary that way reads as *the whole game running fast*,
  because `Floor()` and `_tickThisFrame` both hang off it and `_tickThisFrame`
  fails open. Each function is committed on its own now and a failure names the
  method. **No recompile.** See "Everything hung off one hook" in
  `docs/PATCHES_AND_MODS.md`.

- `0028-hook-manager-commit-state.patch` — `AddPre`/`AddPost` only append a
  delegate and return `true`; the detour is created later in `Commit`, which since
  `0027` fails per function without throwing. A patch counting `Add*` returns
  therefore claimed what it had *queued* — `FramePacing` could print
  `boundary 3/3 DrawOTag + 3/3 VSync` with no boundary installed, latch itself
  done, and never retry, which is the uncapped-picture-and-8×-world failure.
  `IsRegistered` and `IsCommitted` let a caller read back what actually landed.
  **No recompile.** See "A registration is not a hook" in
  `docs/PATCHES_AND_MODS.md`.

- `0029-output-panel-image-rect.patch` — the game picture is not the window, and
  nothing outside `OutputPanel` knew where it was. It is an `Image` inside that
  panel, fitted to the panel's content region at the display's aspect and centred
  in it, so the menu bar, the dockspace border and any docked panel take their
  share off it and a 4:3 picture in a wider window has a bar either side. An
  overlay could therefore only anchor to the viewport, which is why the
  full-screen map covered the port's own chrome and lined up with neither. A
  public `OutputView` publishes the rectangle from the one place that computes it,
  once a frame, and is invalid when the panel drew no picture so a caller can fall
  back to the viewport. It also publishes **`GameW`/`GameH`**, the picture's size
  in the game's *own* pixels, off the `SetTexture` call that already receives
  both: `Min`/`Max` alone are a rectangle and not a scale, so nothing could turn
  a window pixel back into a game pixel — which is what the menu pointer needs to
  ask which item is under the cursor. UI only — **no recompile**. See "A dynamic
  map" in `docs/PATCHES_AND_MODS.md` and "The menu pointer" in `docs/INPUT.md`.

- `0030-expose-host-pump.patch` — the shipped launcher has to build the game
  before there is a game to run, and that blocks for seconds; a window that stops
  pumping for seconds is one the desktop offers to force-quit. `HostWindow.Pump`
  already does exactly the right thing and is `internal`, and `WaitForValidDisc`
  already runs that loop but only ever for its own condition. Exposed as
  `Runtime.Pump`. Everything else the progress UI needs was public already —
  `Popup` is abstract-public and `PopupManager.Register` takes any implementation
  — so `Verdite2.Launcher/BuildProgressPopup.cs` is not a patch. UI only, **no
  recompile**. See "The one patch this needed" in `docs/PACKAGING.md`.

- `0032-expose-pad-queries.patch` — `InputManager` is `internal`, so a port
  drawing its **own** binding table could not ask whether a pad is connected or
  what is held down on it. Both methods were already `public` on that class, so
  unlike `0017` nothing had to be added there — only the two forwards from
  `HostWindow`, beside `IsKeyDown` and the mouse block. Two lines against `0017`'s
  sixty. UI only — **no recompile**. See "The Input pane is the port's" in
  `docs/INPUT.md`.

- `0031-output-panel-fills-its-dock-node.patch` — the picture is the point of the
  Output panel, so it gets none of the chrome every other panel wants. The themed
  `WindowPadding` (12,10) and the 1px `WindowBorderSize` are read by `Begin` when
  it computes the inner rect, so a docked, tab-bar-less Output panel filling the
  dockspace still letterboxed the game behind a band of window background on all
  four sides — scaled by `Theme.Scale`, so widest exactly where the DPI is misread
  highest. Both are pushed around `Begin` only and popped straight after it, so
  the toasts drawn below still lay themselves out on the real style and no other
  panel is affected. UI only — **no recompile**. See "The picture is inset
  inside its own panel" in `docs/RUNTIME.md`.

- `0033-sans-serif-interface-font.patch` — ImGui's built-in face is ProggyClean,
  a 13 px bitmap: it is pixel art, it does not scale (every other size is a
  stretched bitmap), and it makes the port's own settings window read as a debug
  overlay laid over the game. This is upstream's own fix back-ported —
  RecompOne `aaf7be0`, which our pin `870c5ba` predates — so `Icons` becomes
  `FontSet`, Noto Sans is embedded and merged with the Font Awesome range, and
  the size goes 13 → 16 px. **Upstream's CJK face is deliberately not carried**:
  it is a second 16.5 MB resource, and every string in the runtime's three
  languages (en, pt-BR, es-419) is Latin, so it would cost 16 MB in every release
  artifact to render nothing anyone can select. Cyrillic, Greek and Vietnamese are
  kept, being Noto's own coverage and only atlas space — they are what a path or a
  mod name falls back to instead of boxes. A missing resource falls back to the
  bitmap font rather than to no text. The font is OFL 1.1
  (`patches/recompone/assets/NotoSans-OFL.txt`, which the packaging must ship).
  UI only — **no recompile**. This is the one patch that *wants* to stop applying:
  when the pin moves past `aaf7be0` it is upstream's, and the right response to
  `FAILED TO APPLY` here is to delete it. See "The interface's font" in
  `docs/RUNTIME.md`.

- `0034-pgxp-value-tracking.patch` — **upstream's PGXP, backported.** RecompOne
  grew a real PGXP after our pin (`39fb337a`, `91c20fcf`, `95f0585b`, `6aae910a`,
  2026-08-31 to 09-07): the GTE's own divide publishes a float screen position and
  view depth, and those follow the value through the CPU's registers and a
  `PgxpValue`-per-word RAM shadow to the GP0 packet. `RecompOne.Runtime/Pgxp/` is
  verbatim from `6aae910a` apart from living one directory up, beside `GteDepth`
  rather than under it; the edits are `Gte.Rtp` publishing the precise vertex,
  `Nclip` doing backface culling on precise positions, the transform-serial ring,
  `PSMemory`'s ctor sizing the shadow and `Runtime.Run` initialising the vertex
  cache. **Upstream's frame interpolation is deliberately not carried** — it is a
  separate experimental feature that arrived in the same commit. Inert until
  something turns it on. **No recompile.**

- `0035-pgxp-cpu-hooks.patch` — the recompiler half, and one of three patches that
  **force a recompile**, with `0004` and `0037`. `InstructionEmitter` emits
  `if (Pgxp.CpuTracking) PgxpCpu.X(...)` beside every load, store, move, shift,
  add, multiply and divide, which is what makes PGXP's coverage a fact rather
  than a rate. The gate is emitted rather than taken inside the hook, so with PGXP
  off the cost is a predictable branch. Loads and stores hold the address in a
  local rather than emitting the expression twice — upstream evaluates it again
  after the access, which hands the hook the wrong address for `lw $t0, 0($t0)`.
  Measured: 68,188 hook sites in `game.cs`, no change in generated line count
  (the hooks append to existing lines), build 15 s → 37 s.

- `0036-pgxp-vertex-and-depth-source.patch` — where the two mechanisms meet.
  `DrawPolygon` asks PGXP when it is on and `GteVertexMap` when it is not, filling
  the same `Vert` fields either way, so `HleVertex`, both rasterizers and the
  shaders are untouched by the choice. Three things are ours rather than
  upstream's: the **tolerance is actually spent** (upstream defines
  `pgxp.tolerance` and never reads it — here a recovered position more than that
  many pixels from the packet's is refused, because it is a different vertex
  rather than a better version of this one), **`PgxpStats`** counts where each
  answer came from, and **a depth-tested triangle is given a real clip W whether
  or not its texture is being corrected** — `vDepth` is an ordinary varying, so
  with `w = 1` an untextured wall's interior depths came out linear in screen
  space when it is `1/z` that is affine there. The software rasterizer had always
  interpolated the reciprocals; this makes the GL path agree. Also the
  **depth-clear threshold** (`GteDepth.DepthClearThreshold`, DuckStation's 300),
  which bumps the existing `Generation` rather than adding a clear path. **No
  recompile.**

- `0037-chd-disc-images.patch` — **upstream's CHD support, backported** (`137a793`,
  six commits past our pin). `CueFs` becomes `DiscFs` over a new `IDiscImage`, with
  `CueBinImage` and a from-scratch libchdr port (`Cdrom/Chd/`: header, hunk map,
  Huffman, LZMA, FLAC, CD-sector ECC) behind it; `DiscImage.Open` picks by
  extension and falls back to the CHD magic, so the recompiler, the runtime and the
  launcher only changed a type name. The commit's unrelated **RAM-size** change
  comes with it — `PSMemory(uint ramSize)` and `Runtime.RamWordMask` replacing the
  literal `0x1FFFFCu` — and nothing here passes a size, so the RAM is the same 2 MB
  and the mask the same value. Codecs: cdzl, cdlz, cdfl, zlib, lzma; **not zstd**,
  and a `cdzs` image is refused at the picker rather than crashing. **Forces a
  recompile** — `EntryWriter` emits `DiscFs.Open`. Measured: `generated/` from the
  CHD is byte-identical to `generated/` from the cue; the recompile costs
  1.35-1.39 s against 0.86-0.89 s; the autostart area load is 305.1 ms against
  305.8 ms, the same 84 steps over the same 105 blocking VSyncs; a CHD run walks
  `open` → `game` → `fdat02` → `fdat05` at 144.0 fps / 20.0 ticks/s, and the intro
  STR decodes. See "CHD disc images" in `docs/RUNTIME.md`.

- `0038-expose-the-mouse-wheel.patch` — `InputManager` owns the `IMouse` and is
  `internal`, so a port could not read the wheel at all; nothing in the runtime
  listened for the `MouseEvent` that carried it either. Adds `TakeMouseWheel` in
  the drained shape `0017`'s `TakeMouseMotion` already has — scroll arrives as
  discrete events, so a drained accumulator cannot miss a notch or spend one
  twice, where ImGui's per-frame `io.MouseWheel` is a level and a menu loop
  iterating at 30 a second against a 144 fps window would do both. The value is a
  **float** throughout: `OnScroll` had `Wheel = (int)wheel.Y`, exact for a
  discrete wheel and a total loss for a trackpad's two-finger scroll, which
  arrives in fractions of a notch. `patches/MenuMouse.cs` is the only caller.
  Input only — **no recompile**. See "The wheel owns the page, not the cursor" in
  `docs/INPUT.md`.

- `0039-render-scale-survives-a-menu.patch` — a modal sub-loop keeps the world
  behind it by reading the finished frame out of VRAM once (`StoreImage`) and
  blitting it back every iteration (`LoadImage`), and that roundtrip is 1x by
  construction: VRAM is the console's own resolution, so at any render scale the
  restore stamped a 1x picture over the display area on every frame of the menu,
  a shop or an NPC's message box. Only the *middle* of the picture, because
  `Writeback` copies a target's middle `W` columns and the widescreen margin
  lives nowhere but in the render target — which is the seam the report named.
  `ReadVram` now also takes a scaled copy on the GPU and `WriteVram` serves an
  upload of byte-identical pixels from it. Keyed on content and size rather than
  address, since the frame may be restored into either display buffer; anything
  the game actually built in RAM fails the compare and uploads as before.
  Measured in the menu at 144 fps, 16:9, scale 4: 120 restores per two seconds
  and **0** misses, present still `wide`, world still 20.0 ticks/s; `0, 0` in an
  area, so it costs nothing when nothing reads the frame back. `KF2_VRAMSNAP=0`
  is the comparison; no control in the window, a render scale surviving a menu
  not being a choice. GL backend only. **No recompile.** See "The render scale
  did not survive a menu" in `docs/RENDERING.md`.

`0007`, `0008` and `patches/EndingHold.cs` are the shape to keep in mind
generally: **anything the runtime refreshes only at `VSync` is invisible to a
game that stops calling `VSync`**, and that failure mode is always silent.
`END.EXE` ends in `while(1);` with no `VSync`; on hardware the last frame stays
on the CRT, here the window dies. **Holding it is faithful and still reads as a
crash**: the spin is real (`08004694 00000000` at `0x80011A50` in the disc image)
and `END.EXE` never writes the boot stub's next-executable byte, so on hardware
the ending is a hang you leave with the reset button — and a window has no reset
button, which is why "the game crashes after The End" was reported again after
the hold was already in. Measured holding, alive and pumping, at 20 fps through
`GAME.EXE`'s own hand-over and at 144 fps. So **any button now leaves the still
for the title** (`KF2_ENDINGEXIT=0` keeps the hang), through the stub's own
loader: `SLUS_001.58` holds three file names at `0x80010254`
(`0` = `OPEN.EXE`, `1` = `GAME.EXE`, `2` = `END.EXE`), an index at `0x80010268`
and a loop in `func_80010038` that `Load`s, `Exec`s *as a call* and re-reads the
index from `0x800102F0` on return — which is exactly the door `GAME.EXE`'s own
quit-to-title uses. `patches/BootExe.cs` (`KF2_BOOTEXE`) writes that same index
before the loop's first pass, **once**, so the ending is reachable in seconds
rather than by finishing the game. See "The ending screen" in `docs/RUNTIME.md`.

**Nothing goes upstream. Not a pull request, and not an issue either.** Upstream
rejects AI-authored pull requests outright, and this project does not file
issues against it: a defect found here is recorded in `docs/` and fixed in the
vendored tree, which is the whole point of vendoring it. If the user wants
something reported upstream they will write it themselves.

## Shipping it

**The port cannot ship a playable binary, and that is the whole shape of the
release.** `generated/` is a translation of FromSoftware's code and its compiled
form is no less derived, so the assembly that plays the game has to be built on
the machine of somebody who owns the disc. What *is* distributable is every
**input** to that build: `config/`'s addresses are metadata about the code rather
than the code, `patches/**` and `Program.cs` are original MIT work, and RecompOne
is MIT. So the release ships the inputs and makes the output at first run.

That is also a correctness win rather than only a legal one: the generated
dispatch tables bake **absolute LBAs from one mastering** (`fdat02.cs` reads
`LbaStart => 457`) and `Dispatcher` arms an overlay swap on a CD read hitting that
exact sector, so a prebuilt binary would silently fail to load area modules on a
differently mastered dump. A per-user recompile reads those LBAs off the player's
own image.

**`Verdite2.Launcher/` is the shipped executable and `KingsField2Recomp.csproj` is
unchanged.** They are opposites on purpose: the game project compiles `generated/`
and `patches/` through the SDK's default globs and so needs the disc, which is what
keeps developer iteration incremental; the launcher compiles **neither** — it
carries them as payload under `content/` and compiles them at first run. That is
what lets CI build a release at all, and `.github/workflows/ci.yml` asserts it on
every push, because it is easy to break by accident and invisible locally where
`generated/` exists. Anything added to `Verdite2.Launcher/` must keep that
property; the game csproj has a `Compile Remove` for the directory, the same
CS0579 trap `tools/**`, `mods/**` and `mcp/**` are removed for.

First run is: chdir into the data directory, register the disc validator, ask for
a disc, build if this one has not been built, hand over. **The chdir is the whole
packaging fix for file locations** — the runtime addresses `settings.json`,
`interface.ini`, `carda.sav`, `carda.fog` and `mods/.cache` with bare relative
paths, so they all follow it and none of them needed a patch
(`%LOCALAPPDATA%\Verdite2`, `~/.local/share/verdite2`, `VERDITE2_DATA` to
override). **`Runtime.DiscValidator` has existed since the runtime was written and
nothing ever filled it**, so until now any file at all was accepted;
`DiscCheck.Validate` fills it and names `SLUS-00255` explicitly, that being the
game most people will reach for and one that would otherwise build. The recompiler
runs **in process** through `Assembly.EntryPoint` — its `Program.cs` is top-level
statements, so its entry point is an ordinary invocable method, and a self-contained
publish has no `dotnet` to launch a second process with. Measured: **12.7 s** from
launching the AppImage to a running game, of which the recompile is 0.85 s.

**The recompiled output and the port's own sources are compiled in ONE Roslyn
pass**, because the port reaches into the recompiled code directly — `Program.cs`
calls `Recompiled.Entry.Run` and `AutoReload`, `AreaWarp` and `CullGrid` make
fifteen static calls to `Recompiled.KingsField2.func_XXXXXXXX`. Splitting them
would mean an interface boundary for each, or routing through `Dispatcher.Call`,
which goes through `HookManager` and so is *not the same call*. Two things about
that pass were nearly wrong and are worth carrying: **the reference set must come
from `TRUSTED_PLATFORM_ASSEMBLIES`, not from loaded assemblies** — the first
version copied `ModCompiler`, which is right for a *mod* (it compiles against what
the game has, and by then the game has loaded it) and wrong here, because the
launcher has loaded almost nothing; it failed on `AgentServer.cs` with six errors
about `System.Net.Sockets` purely because the launcher never opens a socket. And
**`ImplicitUsings` is an SDK feature, not a compiler one**, so `GameCompile`
supplies `GlobalUsings.g.cs` itself or the port's 22k lines lose `System` and
`System.Linq` in hundreds of places that read as the port being broken.
**`GameCompile`'s options and `KingsField2Recomp.csproj`'s properties are two
statements of one thing and must stay in step** — a difference between them is a
bug that exists only in the release, and losing the frame boundary that way is not
a crash but the whole game running fast, silently. The check is `KF2_FPS=144
KF2_FPS_PROBE=1` on the packaged binary: measured 144.0 fps drawn at 20.0 ticks/s.

**The version is one line in `VERSION` at the repository root and everything else
reads it** — the launcher's assembly version, the AppImage's name, the zip's, the
installer's (`VERDITE2_VERSION`, an error if unset), and `release.yml`, which
asserts the tag it fired on equals `v$(cat VERSION)`; a tag that disagrees with
the tree would otherwise publish the old artifacts under the new number. The
assembly also carries `0.1.0+<sha>` (`Ver.Full`, printed at startup, on a crash
and at the head of the build log), stamped by the csproj's `StampBuild` target
and *not* part of `BuildKey`, since hashing the commit would recompile the
player's game on every docs commit. `bash scripts/release.sh 0.2.0` is the bump;
it commits and tags and deliberately does not push. See "Versioning" in
`docs/PACKAGING.md`.

Packaging is `packaging/linux/build-appimage.sh` and
`packaging/windows/build-windows.ps1`, neither of which needs the disc; trimming is
off and must stay off (MonoMod detours, Roslyn, `AutoStart`'s reflection). The
icons under `packaging/shared/` are **placeholders**. Not packaged: macOS and
Flatpak. **`.chd` is supported**, as of `0037`. See `docs/PACKAGING.md`.

## Repository conventions

Never commit disc data or recompiler output — `disc/`, `generated/`, `*.sav` and
`settings.json` (written by the runtime at play time) are gitignored for
copyright and cleanliness reasons.

Commit messages in this repo state the *finding*, in the imperative, with the
observable consequence: "Map the PSY-Q CD library; boot now reaches the main
loop".
