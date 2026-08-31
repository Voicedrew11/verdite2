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
| `docs/TODO.md` | next steps and open, undiagnosed questions |

Update the right document when you learn something — that is where findings
belong, not in commit messages. Source comments still say `See "X" in NOTES.md`,
and **the text they name is not in `NOTES.md` any more** — the titles are
unchanged, so the index resolves X to a document, but it is a hop rather than a
direct hit. Grep `docs/` for the title, not `NOTES.md`.

## Build and run

Nothing here builds without the disc (gitignored, `disc/KingsField2.cue`) and
without `tools/RecompOne` (a gitignored checkout, not a submodule).

```bash
bash scripts/setup_tools.sh          # clone RecompOne, apply patches/recompone/*, build recompiler

# recompile MIPS -> C# into generated/ (~2099 functions, ~163k lines)
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build -- config/kf2.json

dotnet build KingsField2Recomp.csproj -c Release
dotnet run --project KingsField2Recomp.csproj -- disc/KingsField2.cue
```

`setup_tools.sh` is idempotent and is also how you re-apply the local patches
after pulling upstream. The cue path is needed at *play* time as well as at
recompile time.

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
KF2_TICKRATE=30                        # ticks a second the world runs at (20 by default)
KF2_FPS_GATE=80037C0C+8002A550+80040348+80046A60+8004910C+80033FBC+8002DC78  # what is ticked
KF2_FPS_LOGIC=full                     # no gating; scale the movement deltas instead
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
KF2_DRAWCENSUS=1                       # which renderer routine drew how much of the frame; =2 names the models
KF2_WIDESCREEN=16:9 KF2_WIDESCREEN_PROBE=1  # aspect (4:3 by default), and the margin census
KF2_WIDESCREEN_PROBE=2                   # the census plus every wide primitive, once per shape
KF2_WIDESCREEN_EFFECTS=0                 # leave the death fade and damage flash 320 wide
KF2_WIDESCREEN_CULL=0                    # leave the game's view cone at its 4:3 shape
KF2_WIDESCREEN_CULL=1.5                  # pin a widening factor instead of the aspect's
KF2_WIDESCREEN_CULL_PROBE=1              # tiles lit, and what the 24x24 grid clipped
KF2_WIDESCREEN_CULL_PROBE=2              # also lit-per-ring after the occlusion flood
KF2_PRIMBUF_PROBE=1                      # the frame's primitive budget: peak, capacity, overflows
KF2_VIEWCLIP=0 KF2_VIEWCLIP_PROBE=1      # the game's view-space clip volume, and where it cuts
KF2_NODITHER_PROBE=1                   # where the dither bit comes from, and GPUSTAT bit 9
KF2_TRUECOLOR=1                        # 24-bit shaded output, no 15-bit banding (off by default; GL backend only)
KF2_PERSPECTIVE=0                      # affine textures again (correction is on by default)
KF2_PERSPECTIVE_PROBE=1                # the GTE vertex map's hit rate
KF2_PERSPECTIVE_FALLBACK=1             # also guess by screen position on a miss (the old mechanism)
KF2_SUBPIXEL=1                         # sub-pixel vertex positions (off by default)
KF2_SUBPIXEL_PROBE=1                   # how far vertices actually move, in pixels
KF2_ZBUFFER=1                          # per-pixel occlusion from GTE depth (off by default)
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
KF2_AUTORELOAD=1 KF2_AUTORELOAD_DELAY=2.0 KF2_AUTORELOAD_SLOT=0  # reload the last save on death
KF2_AUTOSTART=2                          # boot straight into save slot 1..3, past the title menus
KF2_BOOTEXE=end                          # boot straight into OPEN.EXE, GAME.EXE or END.EXE
KF2_ENDINGEXIT=0                         # leave "The End" hanging, as the original does (a button exits by default)
KF2_AGENT=1                              # [KF2-AGENT] state lines on stdout: overlay, inGame, HP/MP/area/slot
KF2_SHELL=1                              # TCP 127.0.0.1:27900 line protocol: state|nearby|load|warp|press|kill
KF2_UISCALE=1                            # force the interface scale, and save it
```

Patch settings live in `patches/settings/`. A patch registers an `IPatchPage`
against one of the runtime's own settings sections —
`PatchSettings.Register("display", new FramePacingPage())` — and is drawn inside
it, so the frame rate and the dither switch sit in System ▸ Settings ▸ Video
beside vsync rather than in a panel of their own. That is where a mod's
`DrawSettings` body goes when the mod becomes a patch. Pages that give the same
`Title` share one heading, so single checkboxes group under "Enhancements"
instead of each getting a rule of its own. That section is the runtime's
`display` — still that id everywhere in code; the port renames only its *label*,
through `Localization.Merge`, which needs no patch to the checkout. See "Patch
settings" in `docs/PATCHES_AND_MODS.md`.

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
literal 2 is what the code asks for; the console missed that deadline under load
and landed in the three-vblank band, and since King's Field's speed *is* its frame
rate, 20 is the speed it was played at. The port's HLE GPU makes the 2-vblank
deadline every frame and never bands down, so it has to be told. No counter here
can settle it — the port cannot observe hardware, and the 30-minute vblank
histogram that looks like it can is a measurement *of the port* — so it is a
**setting** (`KF2_TICKRATE`, and a combo under Video), and 30 is one entry away.
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
smooth head on a stepping body. The mode is a setting (Video ▸ Enhancements ▸
Placement guard, `KF2_SMOOTH_OBJECTS_GUARD=strict|sticky|continuous`, `continuous`
by default), and the carry decision is made once per tick rather than once per
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
two are a combo under Video ▸ Enhancements ▸ Pose interpolation, switchable while
a creature is on screen, and the losers go once the picture is judged. **On the
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
default to off** — while the boundary was broken the phase was
pinned to 0 and the
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
the vblank instead. Measured at `KF2_FPS=144` with
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
and `patches/recompone/0014` tests the recovered SZ per pixel instead. It has
**no user-facing switch** — the Video checkbox was removed because the picture is
effectively unbridgeable (DuckStation's PGXP depth buffer fails on the same
per-polygon OTZ averages), so the mechanism is kept for diagnosis only, driven
from the console by `KF2_ZBUFFER` / `KF2_ZBUFFER_PROBE`.
See "Sub-pixel vertex positioning" and "Z-buffer" in `docs/RENDERING.md`. Auto reload is a
patch for the same kind of reason: a death costing four screens of menu is
something a player expects the port itself to have dealt with, so it is on by
default and its knobs are under Gameplay. Analog twin-stick control is the same
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
has never been checked by eye. (Its two sub-options are on by default, since they
only do anything once an aspect has been chosen; the tint stretch is on because
the picture without it *was* checked and was wrong.) The measurement tools are the mods that are left
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
event cannot say. That replacement is the reason every other `DrawOTag` hook in
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
`press <button> [ms]`, `kill`, `ending [boss]`; one request per line, one
single-line JSON response back) steers the game
the beacon is only watching. **`ending` is there because the last ten minutes of
the game cannot be loaded into**: plain `ending` writes `GAME.EXE`'s own quit
word at `0x80199574` (1 = `END.EXE`, 9 = title), and `ending boss` runs the
post-final-boss sequence that normally writes it — `fdat23`'s `func_8019F474`
then `func_8019F688`, two modal loops that present their own frames, both
needed because the first fills the pointer at `0x801A0598` the second writes its
camera through. Reaching it needs `KF2_DEBUG_GODMODE=1`, or `warp 7` kills the
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
patches/recompone/*.patch  local fixes to the RecompOne checkout itself
generated/               recompiler output (gitignored — derived from copyrighted disc data)
scripts/*.py             disc inspection, address-hunting, and the rate tooling:
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
`libcdstream`, libapi's `DMACallback`). `libpad` is the notable gap. Only map a
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

`tools/RecompOne/` is gitignored, so **any edit made inside it is lost on a fresh
clone**. Changes to the recompiler or runtime must be captured as a patch in
`patches/recompone/` (numbered, applied in order by `setup_tools.sh`). Twenty-six
of the thirty are load-bearing; `0002`, `0003` and `0015` are diagnostics and
`0013` is a settings-placement hook. The numbering has doubled up twice
(`0014b`, and `0021` naming both true-color and the vblank clock), so the count is
of files, and the glob's sort is the apply order.

`setup_tools.sh` **does** rebuild the checkout on this branch, and that used to be
false: `0021-true-color-24bit-output.patch` was authored while
`lighting-experiments`' `0025`/`0026` were applied, so its hunks quoted
`_uCoplanarTol` / `_uLitCenter` context that exists only there and `git apply`
rejected them, leaving the tree at `0020`. The patch has been regenerated against
this branch's context. Verified by applying all thirty patches in glob order
to a pristine worktree of the pin: every one applies, and the result is
byte-identical to the tree in place.

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
  `patches/settings/TrueColorPage.cs` the checkbox under Video. **No recompile** —
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

Upstream **rejects AI-authored pull requests outright**. Recompiler fixes go
upstream as issues, never as PRs, unless the user writes the patch themselves.

## Repository conventions

Never commit disc data or recompiler output — `disc/`, `generated/`, `*.sav` and
`settings.json` (written by the runtime at play time) are gitignored for
copyright and cleanliness reasons.

Commit messages in this repo state the *finding*, in the imperative, with the
observable consequence: "Map the PSY-Q CD library; boot now reaches the main
loop".
