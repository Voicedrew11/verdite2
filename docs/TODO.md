# Outstanding work and open questions

Two lists. **Open questions** come first — what is known to be wrong or
unexplained, each with a pointer to the full write-up rather than a copy of it.
**Next steps** follow, which is the prioritised work list; anything already
answered there is struck through in place, because the answer is usually more
useful than the question was.

## Open questions — reported, not diagnosed

| question | what is known | where |
|---|---|---|
| **A third cull at the sides of a wide picture.** Corners still drop, floor and ceiling, occasionally; the edge was reported as straight, which says a clip plane or rectangle rather than a per-object test. | The tile cone, the view-space clipper, the primitive buffer and the backend scissor are each **ruled out with a number**. `KF2_VIEWCLIP=1.5` forces the clip volume open at any aspect and is the test that would implicate it regardless of the arithmetic. | "There is a third cull" in [WIDESCREEN.md](WIDESCREEN.md) |
| ~~**The Z-buffer still shows sky through nearby walls.**~~ **Closed as unbridgeable.** The game encodes visibility as OT draw order, not depth, and some of that order *contradicts* any per-pixel depth: the skybox is a near-projecting box deliberately drawn first, so its true SZ makes it win against distant walls; coplanar decals rely on order at equal depth. One global z-test cannot serve both "cave rocks interpenetrate" and "near sky stays behind everything." The user-facing switch was **removed**; the mechanism stays for diagnosis behind `KF2_ZBUFFER`/`KF2_ZBUFFER_PROBE`. (DuckStation's PGXP depth buffer hits the same wall.) | "Z-buffer" in [RENDERING.md](RENDERING.md) |
| **The camera does not feel consistent in every direction.** | Four candidates, three of them certainly *real* effects whether or not they are the one being felt (the game turns you slower while walking; yaw and pitch scales differ; diagonals saturate sooner; the ramp is radial). The measurement to take is written out. | "Open: the camera does not feel consistent" in [INPUT.md](INPUT.md) |
| **The twin-stick controls broke after the dragon stone went into the fountain.** Not reproduced. | Three candidates, each one memory read apart: the rate guards at `0x8019955C`/`0x80199558`, the action-mask table being rewritten, or the player action state parking in an arm that never calls the control routines. "Did the D-pad still work?" settles the third. | "Open: the twin-stick controls broke" in [INPUT.md](INPUT.md) |
| **`KF2_WIDESCREEN` was overridden mid-session by the saved aspect.** | Env-forced aspects are not as forced as `_forced ?? saved` in `Widescreen.Install` reads. Anything A/B-ing two aspects should pin the *saved* setting instead. Worth tracking down. | "There is a third cull", trap at the end, in [WIDESCREEN.md](WIDESCREEN.md) |
| **A wide target destroyed mid-load would show the bars again.** Mechanism only — never seen. | `PresentDisplay` bumps `_frame` per *present*, and destroys any target idle past 300 of them: about five seconds at 60 Hz, two at 144. A disc read that long leaves a fresh target with black margins and no latch. The guard would be to exempt the target holding the display area, as `0022` reasoned. `KF2_PRESENT_PROBE=1` across the longest area load is the test. | "The present gate" in [WIDESCREEN.md](WIDESCREEN.md) |
| ~~**`scripts/setup_tools.sh` cannot rebuild the checkout.**~~ **Closed.** `0021-true-color-24bit-output.patch` quoted `_uCoplanarTol` / `_uLitCenter` context from `lighting-experiments`' `0025`/`0026`, so four of its hunks were rejected on this branch. Regenerated against a scratch worktree carrying only `0001`–`0020`, so the added lines are unchanged and the context is this branch's. The whole 27-patch stack now peels and re-applies; verified by running the script twice from a pinned tree. | this table |
| ~~**The death clock stops short above the tick rate.**~~ **Closed — not a defect.** It was `patches/AutoReload.cs` being measured. Auto reload is on by default and pins this very counter at 31 (`AutoReload.HoldAt`) on every tick for the whole of its 2 s delay, so the death animation finishes while the fade (32..64) and the game's own respawn-to-area-0 (65) never come due — that is the patch working, and it is why a player dying repeatedly sees nothing wrong. With it on, the numerator is a clamp and the denominator is `AutoReload.Delay`, so the ratio the scenario printed was never a rate. Measured at `KF2_FPS=165`: **31 frames in 2.06 s with auto reload on, 65 in 3.25 s — 20.02 ticks/s — with `KF2_AUTORELOAD=0`**, which is the documented reference exactly. `rate_matrix.py`'s `death-clock` now asks for `KF2_AUTORELOAD=0` the way `menu-scroll` asks for its probe. A second and independent flakiness in the same scenario was found while confirming this: the `kill` does not always take, because `wait_in_game`'s fixed settle can end before the area is up — at `KF2_FPS=20` that read as a flat `0 death frames` until `--settle` was raised to 25. It now confirms the counter started and retries, and 20 fps reports 65 in 3.25 s at the default settle. Full matrix after both fixes: **20.0, 20.0 and 19.8 ticks/s at 20, 60 and 165 fps.** The lesson is the general one: **a scenario that reads a game counter has to name every patch that writes it**, and this one is written by a patch that is on by default. | this table |
| **Two of `Mode.Timeline`'s four cases have never been exercised.** | The probe reports `0 in reverse` and `0 turned` in every window measured across areas 0, 2 and 7, before and after the endpoint-turn fix. The drawbridge lever is the reverse case the old sign bug was found on; the ping-pong branch has no evidence behind it at all and is the part that shipped the crystal shake, so it is where to look first if anything shakes again. Drive to a lever and read the probe. | "Is it *correct*, though" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md) |
| **Every MO clip measured is 4096 units long.** | The modulus is confirmed twice over (highest clip time seen 4095; wrap steps are exactly `rate - 4096`), but a uniform length means `AnimSmoothing.Duration`'s per-clip walk would look identical if it were reading a constant. Finding one clip with a different `D` is the test. | "The model pipeline has no skeleton" in [GAME_INTERNALS.md](GAME_INTERNALS.md) |
| **The analog settings page is under the fold.** Not a bug; a placement problem with three weighed options and one chosen. | `SettingsRegistry.Extend` has no ordering argument, so an extension can only land at the bottom of a pane. Upstream gap, worth an issue. | "Open: the page is under the fold" in [INPUT.md](INPUT.md) |
| **The smoothing is sometimes dead for a whole session, and the title screen's speed predicts it.** Reported from play; not reproduced. | Fourteen cold starts on the same machine, config and build are *byte-identical*: `165 fps, boundary 3/3 DrawOTag + 3/3 VSync, 7/7 stage(s)`, `smoothing: on`, `objects: on`, and a measured 165.0 fps against 20.0 ticks/s in an area. So the boot path is not where the variance is. The healthy title is **15.0 fps and is not paced by `KF2_FPS` at all** — it is OPEN.EXE's own four-vblank wait on a wall-clock grid, and it reads 15.0 at `KF2_FPS` 20, 60, 165 and 300 alike — so "the title is going at the wrong speed" cannot come from the render rate the settings hold. `KF2_FPS_PROBE=1` was added to close this: it reports from the first second of the boot, and one line from a session that is going wrong distinguishes every remaining candidate (rate not loaded, boundary lost, world ticking at the render rate, smoothing inert). **Needs a measurement from a broken run.** | "The rate a session is actually running at" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md) |
| **A stall at mod load, seen once, never reproduced.** | In `HookManager.Commit`, worker thread absent from the stack. Not a standing trap — recorded only so it is recognised if it recurs. | "Seen once, not reproduced", under "Four things that will bite" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md) |

## Next steps

1. ~~**Cross an area boundary.**~~ ~~**Load a save back.**~~ Both done — a
   30-minute session loaded `fdat05` over `fdat02` with no `unmapped call`, no
   VRAM collision and no exception, so the swap mechanism and the derived base
   hold for a second module; three files were written to `carda.sav`; and slot 2
   has since been loaded back from the title screen into the right area with the
   right character state. See "Saving and loading" in
   [GAME_INTERNALS.md](GAME_INTERNALS.md). The memory card is closed as far as
   playing goes — what has *not* been exercised is creating a save on an
   empty card, deleting one, or a card the game considers corrupt.
2. ~~**Look for the module families the group-of-three pattern misses.**~~ Done —
   all 70 `FDAT.T` entries and every other file on the disc were tested for the
   module signature; the nine declared modules are the only code, and every
   static indirect-dispatch target is already a known function start. See
   "GAME.EXE loads code" in [RECOMPILATION.md](RECOMPILATION.md). A later,
   narrower static source was not exhausted: `add_call_targets.py` treating a
   `.data` word as `jal` and splitting a GAME.EXE function. Area 7 died on that;
   see "add_call_targets can split a function", in the same file. What remains
   after that class is an address computed at run time.
3. ~~Check the audio.~~ Verified by ear during play — it sounds right. Note the
   two paths are independent: in-game music and effects come from the SPU, while
   the intro and ending movies are **XA** sectors routed to `XaRouter` on the
   runtime's own thread. `END.EXE` has now run (see "The ending screen" in
   [RUNTIME.md](RUNTIME.md)); XA on those two files still wants a listen.
4. ~~**Fix the frame pacing.**~~ Step one done — the `fps` mod holds every
   rendered frame to two vblanks, so the port is a constant 30 fps and no longer
   bursts past NTSC's top band. See "Frame pacing" in
   [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md); the measurement was a frame-stats
   mod, since removed.
5. ~~**Confirm the buf5 view block**, which is all 60 fps is now blocked on.~~
   ~~**Halve the deltas.**~~ Both overtaken. The port now draws at **any** rate
   and holds the game's own clock at 30 Hz instead of scaling anything: the thing
   that actually pinned it to 30 was the *game's* frame gate (`func_80017880`,
   spinning on the vblank credit at `0x801B6CA8` until it reaches two), not a
   vblank floor, and the stages to gate are three, not one — stage 5's effect
   lifetimes were the miss. The view is carried between ticks by a pre/post pair
   around stage 8, which is the only copy of the camera. See "Any frame rate" in
   [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

   ~~**What is left is the part no counter answers.**~~ Not quite — a counter
   answered first. Every rate above 30 was running the game fast (measured double
   speed at `KF2_FPS=60`: 120 fps drawn, the 65-tick death clock at `0x8019951A`
   finishing in 1.10 s instead of 2.17). The frame boundary was a `DrawOTag`
   following an emulated *vblank*, which stops being once-a-frame the moment the
   port draws faster than 60; it is now a `DrawOTag` following a `VSync` **call**.
   With that fixed, 30/60/90/120/144 all measure 30.0–30.3 world ticks a second and
   a rendered rate equal to the number asked for. The gate set also grew from three
   stages to five — stage 6 and stage 13's fade stepper, both audited for whether
   they can draw before being added. See "Any frame rate" in
   [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

   **The tick rate has since moved from 30 to 20.** The literal `2` in the game's
   frame gate is what the code *asks* for; the console missed that deadline under
   load and landed in the three-vblank band, and since the game's speed is its
   frame rate that band is the speed it was played at. The gate is now skipped at
   every rate rather than only above 30 — it decides the render rate and the world
   rate together and knows one answer for both — and the world runs on
   `FramePacing.LogicHz`, a setting under Video with `KF2_TICKRATE` beside it.
   Measured 20.00 ticks/s at 20, 30, 60, 120, 144 and uncapped, and `KF2_TICKRATE=30`
   reproduces the old 2.14 s death clock exactly. See "The reference band is 3
   vblanks, not 2" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

   **A second half of the smoothing has since been written.** Carrying the camera
   turned out not to be the whole picture: reported from play at 60/20, "the
   enemies move at the correct speed, but they are animated at a visibly lower
   framerate, the HUD renders at 60, and the player's arm renders at 20 as well."
   The static world and the 2D HUD are smooth for free; anything with a position of
   its own still stepped, and against a smoothly sliding world that reads worse
   than not smoothing at all. `patches/ObjectSmoothing.cs`
   (`KF2_SMOOTH_OBJECTS=1`) carries the object table at `0x80177714` across a
   pre/post pair on stage 13 — measured `121/121 frames carried`, no leak, and the
   death clock still 65 ticks in 3219 ms. **The arm was written up as a different
   bug and is not**: it was called 2D, a sprite index in the HUD builder
   `func_80031D5C`, on a packet-count difference that was measuring the HP/MP
   gauges collapsing. It is a 3D MO mesh drawn by `func_80032400` and it is
   carried now. Both established with a new probe,
   `patches/DrawCensus.cs` (`KF2_DRAWCENSUS`), which attributes the frame's
   primitives to the routine that drew them. See "The camera is not the only thing
   that moves" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md) and "What in the
   renderer draws what" in [GAME_INTERNALS.md](GAME_INTERNALS.md).

   **That first version smoothed the wrong table for enemies.** It carried only the
   object table `0x80177714`, but `func_800331B4` draws creatures from a *separate*
   loop over the **entity table** `0x8016C544` (200 records of `0x7C`, position
   `+0x2C`, rotation `+0x40`) — the `KF2_DRAWCENSUS=2` reading that said "renderer
   reads the object table, not the entity record" was taken with no creatures near
   and saw only the second loop. So enemies stepped in position *and* facing even
   with objects on. `ObjectSmoothing` now carries both tables, and interpolates the
   entity **rotation** the shortest way round a 4096-unit turn — measured
   `241/241 frames carried, 3.0 creature(s) each`, no leak. **Pose is the MO
   clip clock.** Vertex-fetch lerp was tried and did not change the picture.
   `patches/AnimSmoothing.cs` now drives `func_8003486C`'s time
   (`KF2_SMOOTH_ANIM=1`, off by default) so the blender writes the in-between
   mesh. **The arm turned out to be the same bug and is fixed too**:
   `func_80032400` draws it, `func_80034DA8` poses it from the swing clock at
   `0x801994A4` — 300 a tick on a 4096-unit clip — and `AnimSmoothing` grew a
   second front-end for it rather than a second patch. Measured 0 held, 86 of a
   swing's 94 frames carried at 144 fps, world clock untouched. See "The player's
   arm is the same bug after all" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

   **What is still left is the part no counter answers**, and it is the user's: is
   20 actually right, is a 20 fps default acceptable or should the picture be drawn
   faster than the world runs, and does the full-rate mode feel better than a
   smoothed 20 despite its broken timers? The smoothing was checked by eye at a high
   rate and reported *"incredible"* once it **interpolated** — `FrameSmoothing` and
   `ObjectSmoothing` both extrapolated at first, which bounced the camera back to
   the tick position whenever a turn eased off or motion met a wall; they now draw
   `lerp(prev, cur, phase)`, which cannot overshoot. Smoothing still defaults to
   **off** (position and objects included) as a house rule until that judgement is
   settled for shipping, not because it has never run. **Stage 2 is done** — it turned out to be
   gateable after all (its only edge to the renderer is the transition fade
   `func_80037B5C`, an extra render rather than the frame's own), so doors, the
   drawbridge, the minecart and the crystals now step at the tick rate. What is
   still open, in rough order of how reachable it is:

   * **`rec+0x40` on two object slots, ratio 3.69.** Not stage 2 — gating
     `func_800331B4` as a probe dropped it from 53.4/s to 17.6/s, so the writer is
     **stage 13's own object pass**. It is a per-object ambient-sound retrigger
     deadline in vblank units (`vbl + 6 * (u16 at rec+0x3E)`, against `0x801B6CAC`),
     which on expiry computes a distance-attenuated volume and calls
     `func_80014158`. With a small interval it retriggers once per rendered frame
     up to the 60 Hz ceiling: 20/s at the default, 60/s at 144. No whole-function
     hook can reach it — `func_800331B4` steps the timer and draws the models in
     one loop — so this needs either a sub-function hook or a hold/restore pair
     around the field. **Never listened to**; only the counter has spoken.
   * ~~**The billboard sprites' cel index.**~~ **Closed** — this was the third
     member of the class and the one play actually reported ("these flames still
     run really fast at a high framerate"). Every animated billboard in the game
     divides one global counter at `0x80195170`, and `func_800331B4` increments it
     as its last instruction, so it counted rendered frames: 4488 cel changes a
     second at 144 fps against 640 at 20. `patches/SpriteAnim.cs` is a
     hold/restore pair on that word and the 128 cel bytes; 20.0/20.6/20.8 steps a
     second at 20/60/144 with it on. **The lesson generalises to the two below**:
     the question is not "can it be gated" but "is there one word upstream of all
     of it". See "The flames run at the render rate" in
     [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).
   * **Stage 13's jitter accumulator at `0x8006E608`**, which no hook can reach
     because it is in stage 13's own body. With the modal loops closed and the
     sprite cels fixed, this and the `rec+0x40` retrigger above are the **only**
     rate defects left, and they are the same shape as each other: a counter
     stepped inside a drawing function's own body, which needs a hold/restore pair
     on the field rather than a deadline on the frame. Neither has been reported
     from play, and neither has the sprite counter's single-word escape hatch —
     the shake accumulator is summed from `func_80015374()` in place, and the
     retrigger is per object. **A modal loop's redraws make both of them fire
     inside it as often as they already do in the main loop** — no worse than an
     ordinary frame, but no better either.

   * **A counter a modal loop steps in its own body** — a picked-up item's spin, if
     its transform does not come from a table `ObjectSmoothing` carries. Its
     *speed* is right; it steps once a tick, which is the console's own rate for
     it. Making it smooth is a different mechanism from any in the port so far:
     interpolating the **arguments** of a model submit (`func_80032588`), keyed by
     model pointer and submit ordinal, rather than a table entry at a fixed
     address. The identity problem that killed display-list matching does not
     obviously apply — a submit is per object, not per back-face-culled polygon —
     but that is an argument, not a measurement. Not started.

   * ~~**A modal loop's own camera.**~~ **Closed.** `func_8004831C`, the cutscene and
     message-box loop, ramps a heading `0 -> 0x1000` by `0x200` an iteration and
     hands stage 13 the `u16` it just wrote, and elsewhere steps `rec+0x26` by `0x40`
     while passing `a1 = 0x80199504`; held to the tick that is a full turn in 32
     steps, which play reported as "the camera is visibly moving at a lower framerate
     in the modals". `LoopPacing` now carries the block the loop passes —
     `lerp(prev, cur, phase)` over the three angles at `a1` and the three position
     words at `a0`, applied in stage 13's pre and taken back in its post, with the
     same placement guard the other two smoothing patches use.
     `KF2_LOOPPACING_PROBE=2` is what found it, and reads `0.0 u per iteration`
     everywhere the player camera is the one being drawn. `KF2_LOOPPACING=nocarry`
     is the A/B. **The picture has not been checked by eye** — the loop needs an NPC
     or a cutscene, which the shell cannot reach.
   * **Four ungated stages that submit nothing at all** and so are free under the
     existing rule, but hold globals nobody has looked at: stage 1 `func_8002C944`
     (8), stage 9 `func_800140AC` (8, the 3D sound listener), stage 11
     `func_80016FC8` (1), stage 12 `func_80014534` (14). `check_gate.py --stages`
     lists them as candidates. Measure before gating — the point of doing them
     separately is that a regression stays attributable to one cause.
   * ~~**`func_80037B5C` itself.**~~ **Closed, and so is the whole class it
     belonged to.** Every loop that renders its own frames — the transition fade,
     the cutscene and message-box loops, the spell-cast and item-use animations,
     the menu — stepped once per *rendered* frame, and the answer turned out to be
     structural rather than one fix per loop: the loop's **body** is held to one
     run per world tick, and the gap between its iterations is filled with
     **redraws** — stage 13 called again at the phase the frame stands at, with the
     two view pointers the loop itself passed it — so `ObjectSmoothing` and
     `AnimSmoothing` carry the picture the way they do everywhere else. Pacing the
     loop alone was the first version and was only half: it gave the right speed and
     a 20 fps picture, since the frame the loop drew *was* the tick and `LogicPhase`
     was 0 on all of them. The second version redrew without passing stage 13 its
     arguments, which built the view matrix out of the leftover register file and
     put a black flicker and a stale buffer on screen; the pointers are recorded by
     a pre-hook now. One pre-hook on stage 9, whose only caller is the main loop,
     plus a pre and a post on stage 13. `patches/LoopPacing.cs`; measured with the
     new `rate_matrix.py modal-rate` scenario at `KF2_FPS=144`, the fade's body went
     33.8 -> 19.9 iterations a second while its picture went 33.8 -> 144.0 frames a
     second, and the menu 144.0 -> 60.1; the 20 fps rows are identical either way.
     See "Loops that render their own frames" in
     [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

   **Two `rate_matrix` scenarios are not measuring what they claim, and it predates
   the stage 2 work** (confirmed by re-running them with `KF2_FPS_GATE` set to the
   old six addresses — identical numbers). `death-clock` reports 31 death frames
   and ~2.05 s in every configuration, including `KF2_TICKRATE=30` against `20`,
   where the recorded baseline is 65 ticks and 3.20 s against 2.14 s; the loop
   probably exits on the agent's `dead` flag before the counter finishes.
   `walk` reports exactly 2844 units/2 s in all four combinations, which is the
   signature of the player walking into a wall from the autostart position rather
   than a distance that scales with the tick rate. Until both are fixed the
   trustworthy tick-rate check is the census itself: the global frame counter at
   `0x80199488` and the animated-texture phase both read ~16-18/s at 20 and at
   144 fps.
6. **Finish the smoothing sweep: enumerate every table, do the map, stop finding
   them by bug report.** This is the live work item and it has a specific history
   worth reading before touching it, because the same mistake was made three times.

   **What the pattern is.** Every smoothing fix in the port is one operation: *a
   per-entity number, at a stable address, in a table of fixed stride, which the
   game updates once a tick and the renderer reads — snapshot it on the tick, write
   `lerp(prev, cur, phase)` before the read, put it back after.* Object position,
   object rotation, entity position, entity rotation and (not yet done) tile height
   are five instances of that one operation, not five features. Model pose is
   the same lerp on a different site: the MO clip clock (`func_80032588`'s
   ninth stack word / `func_8003486C`), not the vertex fetch.

   **What was done.** Stage 2 gated so props step at the tick rate at all; the
   object table's rotation lane at `+0x24` found and carried; the renderer's *four*
   model tables carried instead of two; the object table's emptiness test corrected
   from stage 2's (`byte +0x4`) to the renderer's (`u16 +0x6`); vertex-fetch pose
   lerp tried and discarded (rigid majority, vertex-0 probe); **clip clock
   driven instead** (`patches/AnimSmoothing.cs`, `KF2_SMOOTH_ANIM=1`). See
   "The model pipeline has no skeleton" in `docs/GAME_INTERNALS.md`.

   **What is left, in order.**

   1. ~~**Confirm which byte animates a moving tile.**~~ Closed. `KF2_PACKETMATCH`
      measured map tile primitives at 100% match with **0.00 px displacement** across
      30 windows while the player stood still; model primitives moved in the same
      windows. The drawbridge reported as architecture did not show up in the tile
      pass. A prior `KF2_DRAWCENSUS=2` sample that listed 11 byte-identical models
      over two seconds apart likely sampled between animation cycles. Not chased
      further — the goal is model pose animation, not the drawbridge.
   2. **If it is the height: write the tile smoother.** *(Deprioritised — item 1
      closed the drawbridge question.)* Sample the 80x80 grid's two
      height bytes each tick (12.8 KB), pre-hook `func_80031B1C` to note `(tileX,
      tileZ)`, and pre-hook `func_80031950` to write
      `-(lerp(prev, cur, phase) * 128) - camY` into the Y at `a1+2`. **No restore
      pair is needed** — that vector is a stack temp, not game state, which makes
      this the cleanest of the five sites rather than the hardest. One hook covers
      every moving tile in the game.
   3. ~~**If it is the model index: stop.**~~ Closed. It is not: the vertex
      fetch rewrites the same mesh. Count mismatches (5% of one window) are skipped
      per slot, which is the discrete-swap boundary applied where it actually
      happens.
   4. **Enumerate the remaining tables from the code, not from reports.** The set is
      finite and derivable: stage 13 has three drawing callees, and every position,
      height or angle any of them reads is a table entry at a known address. Four
      model tables and the map are now known. Walk all three routines end to end and
      write the complete list down, because "that's all of them" has been asserted
      and been wrong twice, and each correction cost a round trip through a person
      playing the game.

   **The generic alternative was tried and it does not work at the packet layer.**
   The idea was to stop enumerating tables entirely: at `DrawOTag` the frame is a
   list of finished primitives, so recognise each one in the previous tick's list
   and carry its screen position by the phase. It fails on **identity**. Back-face
   culling submits only the faces pointing at the eye, so one dropped polygon shifts
   every ordinal after it and the key then names a different triangle while still
   counting as a match — measured, models, **92-100% of ordinals matched and only
   14-40% of those were the same face** as soon as anything moved. Applying it
   garbled every object on screen. The full write-up, including the two probe
   defects found on the way and the trap of measuring a hit rate instead of an
   accuracy, is "The display list cannot name a face" in `docs/RENDERING.md`.
   `patches/PacketMatch.cs` (`KF2_PACKETMATCH=1`) is kept as the probe.

   **What that experiment did establish.** With the camera **completely** frozen
   and two enemies attacking, over twenty-five consecutive one-second windows, the
   objects' own translation was 0.1-0.8 px a tick while the motion of their
   primitives *relative to each other* was **3.4-13 px a tick on 9-13% of contexts,
   peaking at 36**. That is pose animation, it is large, and no table smoother can
   reach it. The clip clock is what reaches it.

   **The vertex fetch inside `func_80032588` was the wrong site.** It interpolated
   a rigid majority; the picture did not change. `patches/AnimSmoothing.cs` now
   drives `func_8003486C` instead. **Checked by eye and it works** — off by
   default all the same, until someone decides that is the wrong default.

   **The fast-world regression the vertex-fetch version caused is gone**, and that
   was measured rather than assumed: 65 death frames on the counter at `0x8019951A`
   took 3.25 s with the switch off and 3.25-3.28 s with it on, at 20, 60 and 144
   fps — 19.8-20.1 ticks a second either way, and `check_gate.py` reports 0
   violations. The clip clock writes no game state (one register on one call, and
   the caller's own stack temp), which is why it cannot.

   **No counter here proved the mechanism; a person did.** Across every scene an
   agent could reach — the save's own area, warps to 1/2/3/4/5/8, an attack, a
   death — the probe read 0-61 morph submits a second against 60-122 rigid ones,
   and **every one of those morph submits carried a clip time of 0**: props posed
   through the MO path, not clips being played. One of `func_800331B4`'s five call
   sites passes a literal `0` for the time, which is what those are. The probe
   counts "with a running clip" separately so that this reads as "no subject"
   rather than as "no effect". Getting an agent in front of a creature whose clip
   is running is still unsolved and is the reason the eye had to settle it.

   One transient scene (immediately after a death, warped into area 3) did show
   **30 morph submits with a running clip, stepping a mean of 511 clip units a
   tick** — and that is what caught the real defect in the first version: its
   `MaxTimeStep` guard was `32`, so 24 of those 30 were discarded as a
   discontinuity and nothing was ever carried. The guard is now `4096`, and the
   reasoning is written down at the constant: a clip *restarting* runs its time
   backwards and a clip being *swapped* changes the clip byte, so the two real
   discontinuities are caught by their own tests and the magnitude is only a
   backstop. Picking it near a plausible-looking number is what broke it.

   **The sign of that step was the same mistake one level down, and the
   drawbridge lever found it.** The guard also read `step <= 0`, on the reasoning
   that a clip restarting runs its time backwards — but so does a clip **played
   in reverse**, and the lever going back up is exactly that. Down was smooth and
   up stepped at 20 Hz, on the same object and the same clip. The test is on
   `Math.Abs(step)` now: the sign is a direction, only the size separates playback
   from a re-seek, and a restart from a high time is a large negative jump that
   the size still catches. A negative step used to exit silently and was counted
   nowhere, which is the reporting gap that hid it; the probe prints
   `N playing backwards` beside the skipped count now. Confirmed by eye.

   **What is left is the default, which is a judgement and not a measurement.**
   The picture is confirmed, so the sub-pixel reason for shipping this off no
   longer applies to it; whether smooth poses are the *authentic* picture is the
   same question `LogicHz` is, and it is the user's. If an agent ever needs to
   re-check this itself, the blocker to solve first is getting in front of a
   creature whose clip is running: the entity table has 159 records in area 1 but
   never more than one within 8192 units of the spawn, and that one sits 9,000
   units above the floor.

   **Two tooling defects found on the way, both unfixed.**

   * **`KF2_RATECENSUS_RANGE` defaults to `80060000:801C0000` and the map tile table
     is at `0x801C8484` — outside it.** Every rate census ever run in this repo has
     been blind to the entire map. Widen the default, or at minimum pass
     `KF2_RATECENSUS_RANGE=801C0000:801D8000` for step 1 above.
   * **`rate_matrix`'s `death-clock` and `walk` scenarios are misreporting**, and it
     predates the stage 2 work (confirmed by re-running both with `KF2_FPS_GATE` set
     to the old six addresses — identical numbers). `death-clock` gives 31 frames and
     ~2.05 s in every configuration including `KF2_TICKRATE=30` against `20`, where
     the recorded baseline is 65 ticks, 3.20 s against 2.14 s; the loop probably
     exits on the agent's `dead` flag before the counter finishes. `walk` gives
     exactly 2844 units/2 s in all four combinations, which is the signature of
     walking into a wall from the autostart position. Until they are fixed the
     trustworthy tick-rate check is the census itself — `0x80199488` and the
     animated-texture phase both read ~16-18/s at 20 and at 144 fps.

   **One rate defect is still open and is not smoothing.** `rec+0x40` on two object
   slots runs at ratio 3.69; gating `func_800331B4` as a probe dropped it to 17.6/s,
   naming stage 13's own object pass as the writer, where `rec+0x40` is an ambient
   sound retrigger deadline in vblank units. It steps the timer and draws the models
   in one loop, so no whole-function hook separates them. Never listened to.

7. Work out the rest of the `CD/COM/*.T` archive formats when asset work starts.
   [IvanDSM/KingsFieldRE](https://github.com/IvanDSM/KingsFieldRE) has KFModTool
   and format notes covering this game across its regional variants (no symbols,
   `.map` or ELF, so it does not help the function maps).
8. **Report the runtime bugs upstream as issues** (not PRs — see "Upstream
   contribution policy" in [RUNTIME.md](RUNTIME.md)):
   input polled only from `PresentFrame`, which deadlocks any game that waits on
   the pad without vsyncing, and `Interrupts.Deliver` deriving a callback-table
   address from the `HookEntryInt` jmp_buf, which calls whatever the resulting
   game variable holds. Both are in `patches/recompone/0006` and `0007` with the
   reasoning; the second at minimum should refuse a handler that is not a known
   function. A third is `QueryDpiScale` taking the *primary* monitor's content
   scale once at startup, so the interface is scaled for a monitor the window may
   not be on and never follows it across; and a fourth is not RecompOne's at all
   but Silk.NET's — the integer division in
   `ImGuiController.SetPerFrameImGuiData` that `patches/recompone/0018` works
   around, which breaks every fractionally scaled display and belongs in
   `dotnet/Silk.NET`.
9. **Take the twice-drawn pair for sub-pixel vertex positioning, and flip its
   default if it holds up.** The mechanism is measured — 47k vertices a second
   recovered, offsets uniform across the pixel, no frame-rate cost — but the
   picture is not, and that is the only thing keeping it off by default while its
   sibling is on. `GteDepth.Subpixel` is read at vertex-decode time, so it can be
   flipped between two `DrawOTag` passes exactly as the dither bit was. Expect a
   much smaller pixel count than the dither or the textures got: this moves
   polygon edges, not interiors, and the artefact is really about motion, so a
   still pair may undersell it. See "Sub-pixel vertex positioning" in
   [RENDERING.md](RENDERING.md). **Walk a wall after 0011 with both switches on**
   — that patch is what should have killed the remaining crawl and the far-away
   pop, and the picture is the test.
10. ~~**Look at the Z-buffer in the cave.**~~ **Closed.** Retired as a user option:
   the game's visibility is OT draw order, and the skybox (near-projecting, drawn
   first) plus coplanar decals contradict any single per-pixel depth, so no z-test
   can be globally correct on this content. Mechanism kept for diagnosis only,
   behind `KF2_ZBUFFER`/`KF2_ZBUFFER_PROBE`. See "Z-buffer" in
   [RENDERING.md](RENDERING.md).
11. **Decide what the interface should be scaled by, and by eye.**
   `patches/recompone/0018` fixed *where* the interface is drawn; how large it is
   is a second defect and still ours. `QueryDpiScale` returns the primary
   monitor's integer `wl_output` scale — 2.0 for a monitor KDE runs at 1.15 — so
   `Theme.Scale` and the 26 px icon font are wrong on both screens at once, and
   the only reason the port looks usable is a hand-set `UiScale=0.6999999`. The
   mechanism to write is known: prefer the framebuffer/window ratio, re-evaluate
   per frame, rebuild the font atlas and re-apply `Theme` on a change. What no
   counter answers is whether the result *looks* right, or whether the atlas
   rebuild is cheap enough to do on a monitor change. See "The interface only
   fits a monitor whose scale is a whole number" in [RUNTIME.md](RUNTIME.md).
12. Play further in. Now that the menu opens, the parts of the game it reaches —
   inventory, equipment, magic, the map — have never run, and each is a screen
   with its own code path. `mods/kf2debug` is the instrument for this: its state
   readout is how the rest of `buf2` gets named, and its area warp reaches an
   area without walking there. **`buf2` itself is now mapped whole** — the status
   screen labels every word it draws, and the font-index string table decodes,
   so `EXPERIENCE` through `WATER MAGIC` are named by the game rather than
   guessed; see "The status screen names the rest of buf2" in
   [GAME_INTERNALS.md](GAME_INTERNALS.md). Inventory, equipment and the entity
   table are still unmapped, and the same route should reach them: the item and
   spell names are all in that table too, from `0x80065B20` (`DAGGER`) to
   `0x800663F0` (`LIGHT CRYSTAL`), so whichever routine indexes it with a slot
   number is the inventory.
13. **Check the Attributes tab by eye.** `mods/kf2debug`'s character editor is
   written and compiles; nothing in it has been seen running. Three things a
   person has to confirm: that the status screen shows what the panel shows, that
   "Level up" — which calls `func_80024CAC` rather than imitating it — lands on
   the level, maxima and base attributes the game would have given, and that a
   *held* combat rating is felt in combat rather than merely displayed (the hold
   is a post hook on `func_800244CC`, so it wins the display; whether the damage
   arithmetic reads those same words is a separate reading). See "Editing the
   character: the split is the design" in [GAME_INTERNALS.md](GAME_INTERNALS.md).

14. ~~**The crash on the final boss's last hit.**~~ **Answered, reproduced and
   fixed, and it is not a pacing defect.** The state is the game's own, made
   deliberately, and the port's only fault is that `PSMemory` traps a read the
   console absorbed.

   **The chain, all of it read from the recompiled code and then confirmed
   live.** `fdat23`'s module header holds the 32 dispatch slots every area module
   has; **slot 18** — `module+0x48` — is `func_8019FA2C`, the area's damage hook.
   `func_8003A9CC` calls that hook through `u32[u32[0x8017E068] + 0x48]` **part
   of the way through resolving a hit**, and then carries on using the record:

   ```
   func_800271D0                 stage 3's reach scan picks the boss
    +- func_8003A9CC             S4 = the record, entity 0
       +- func_8019FA2C          the damage hook (fdat23 slot 0x48)
       |   +- func_8019F474      the ending's setup; fills 0x801A0598
       |   +- func_8019F688      the ending. At 0x8019F908 it writes
       |                         u8[+0x2] = 0xFF into entity 0 and entities
       |                         6..10, runs its fade, and sets the quit word
       +- func_8003A490          HP - damage <= 0, so: the death reaction --
          |                      and it re-reads u8[S4+0x2], which is now 0xFF
          +- func_8003A448       desc = 0x80172624 + 255*120 = 0x80179DAC
                                 -> ReadU8 through 0x80179DE4 -> unmapped
   ```

   The six records `CrashDump` named — entity 0 and entities 6, 7, 8, 9, 10 —
   are **exactly** the six that loop writes (`S2 = 0x8016C544`, then
   `S1 = 0x8016C82C` stepping by `0x7C` five times), and it is the only write of
   `+0x2 = 0xFF` anywhere in the module. They were never "uninitialised" and
   nothing raced to make them: the ending blanks them on its way to `END.EXE`
   and simply does not expect a hit resolution to still be on the stack beneath
   it. That also explains why there is **no `fdat23` frame on the trace** — the
   hook had already returned.

   **Why it looked like a frame-rate bug.** `desc` lands at `0x80179DAC`, past
   the descriptor block, so `desc+0x38 .. desc+0x74` is inside the **object
   table** at `0x80177714` — slot 146 onward, from field `+0x8`. The "pointers"
   the walk dereferences are therefore live object fields, and `0x0FFF0000` is a
   pair of `u16`s whose top half is `0x0FFF`, the game's own clamped-angle
   constant. Whether the first non-zero one happens to look like a RAM address is
   luck, and the render rate changes the luck. Nothing in `LoopPacing`,
   `ObjectSmoothing` or the stage gate is involved, and no switch among them
   could have fixed it — which is why five commits of bisecting them found
   nothing.

   **`patches/HitGuard.cs`'s entry guard could never have caught this, and
   saying it did was wrong.** It validates the record when `func_8003A9CC` is
   *entered*, and at that moment the type byte is still the boss's real type; the
   state it would have to refuse is created by a call it has already approved.
   The guard that works is `HitGuard.BeforeDescLookup`, a pre on **`func_8003A448`
   itself**: it replays the fifteen-pointer walk, and when the pointer the walk is
   about to dereference is neither zero nor RAM it sets `V0 = 0` and skips the
   body. Zero is the routine's own "no reaction found" answer and every caller
   already handles it — `func_8003A490` passes it to `func_80039E08`, which clears
   the reaction state, and `func_8003A9CC` branches on it — so this is what the
   console's open-bus read almost certainly produced anyway.

   **The entry guard is now report-only, and that is part of the fix rather than
   tidying.** Returning false there skipped `func_8003A9CC` outright, which is
   where the damage, the experience, the knockback and the reaction are applied —
   a swing that connects and does nothing, with nothing on screen to say why. It
   was the right trade while it was the only thing between the player and a hard
   crash; with the read fenced downstream it is the one thing in the port that
   could silently make a creature unkillable. An area loads only 14–30 descriptors
   and leaves the rest as `0xFFFFFFFF` filler, so any type between the loaded count
   and ~107 reached the pointer test and would have been refused on filler.
   Nothing is known to have been dropped — 0 refusals over 40 swings, and none in
   an area with a high creature type — which is exactly why it went before
   something was.

   **Reproduced both ways** with the new `ending kill` (see "`ending` exists
   because the last ten minutes of the game are otherwise untestable" in
   [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md)), at `KF2_FPS=165` in area 7:

   ```
   [KF2] ending kill: entity 0 type is now 255, descriptor 0x80179DAC,
         pointers: 0FFF0000 FF380320 00000000 00001180 FFFFC7C0 ...
   [KF2] hit guard: descriptor 0x80179DAC pointer 0 at 0x80179DE4 reads
         0x0FFF0000, which is not RAM -- reaction lookup answered 0 instead of
         faulting. kind=0x03, called from 0x8003A4C4
   [KF2] ending kill: the death reaction returned without faulting.
   ```

   With `KF2_HITGUARD=0` the last line never appears and the call faults;
   `0x8003A4C4` is `func_8003A490`'s call site, which is the reported stack.

   **What is still open.** The load-bearing assumption is unchanged and still
   unverified: that a PS1 load from unmapped KUSEG returns open bus rather than
   trapping, so the console absorbed this. It is reasoned from the game having
   shipped and people having finished it, not read off hardware. And what the
   *console* found at `desc+0x38` was a live object field too, so "the walk found
   no match" is overwhelmingly likely but not certain — if some run of the
   original did match, the reaction it picked is one this port now declines. The
   remaining check is by eye: that the ending plays through to `END.EXE` from a
   real final-boss kill.

15. ~~**"The game crashes after The End."**~~ Answered, and it was not a crash:
   `END.EXE` really does end in `j 0x80011A50` on itself and never asks the boot
   stub for another executable, so the ending is a hang you leave with the reset
   button. `EndingHold` was reproducing that faithfully — measured holding, alive
   and pumping, at 20 fps through `GAME.EXE`'s own hand-over and at 144 fps, and
   from `KF2_BOOTEXE=end`. A window has no reset button, so any button now returns
   to the title through the stub's own loader loop. See "Holding the frame is
   faithful, and it still reads as a crash" in [RUNTIME.md](RUNTIME.md). What is
   left is by eye: that the title screen that comes back is the real one and not a
   half-initialised one, since nothing clears RAM between the two.
