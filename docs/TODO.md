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
| **The analog settings page is under the fold.** Not a bug; a placement problem with three weighed options and one chosen. | `SettingsRegistry.Extend` has no ordering argument, so an extension can only land at the bottom of a pane. Upstream gap, worth an issue. | "Open: the page is under the fold" in [INPUT.md](INPUT.md) |
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
   death clock still 65 ticks in 3219 ms. **The arm is not the same bug**: it is
   2D, drawn by the HUD builder `func_80031D5C`, so what steps is its sprite index
   and the console stepped it too. Both established with a new probe,
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
   `241/241 frames carried, 3.0 creature(s) each`, no leak. **The open part is
   animation poses**: an enemy's pose is a frame index in the entity record advanced
   once a tick, the same class as the arm's sprite index. Whether it can be smoothed
   needs a spike — read `func_80032588` and its callees (`func_8005C780`,
   `func_8005C810`, `func_8005D968`) to decide if a pose is interpolatable per-limb
   transforms or a keyframe vertex swap with no in-between — and the console stepped
   these at 20 fps regardless, so it may be left as authentic.

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
   * **Stage 13's jitter accumulator at `0x8006E608`**, which no hook can reach
     because it is in stage 13's own body.
   * **Four ungated stages that submit nothing at all** and so are free under the
     existing rule, but hold globals nobody has looked at: stage 1 `func_8002C944`
     (8), stage 9 `func_800140AC` (8, the 3D sound listener), stage 11
     `func_80016FC8` (1), stage 12 `func_80014534` (14). `check_gate.py --stages`
     lists them as candidates. Measure before gating — the point of doing them
     separately is that a regression stays attributable to one cause.
   * **`func_80037B5C` itself.** The transition fade steps once per *rendered*
     frame inside its own loop, so an area transition is still quicker at 144 than
     at 20. Same family as the menu cursor, so the fix is `MenuPacing`'s shape and
     not a gate.

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
   are five instances of that one operation, not five features.

   **What was done.** Stage 2 gated so props step at the tick rate at all; the
   object table's rotation lane at `+0x24` found and carried; the renderer's *four*
   model tables carried instead of two; the object table's emptiness test corrected
   from stage 2's (`byte +0x4`) to the renderer's (`u16 +0x6`). See "Any frame rate"
   in `docs/PATCHES_AND_MODS.md` and "What in the renderer draws what" in
   `docs/GAME_INTERNALS.md`.

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
   3. **If it is the model index: stop.** That is a discrete swap with no in-between,
      which is the boundary below.
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

   **What that experiment did establish, and it is the case for continuing.** With
   the camera **completely** frozen and two enemies attacking, over twenty-five
   consecutive one-second windows, the objects' own translation was 0.1-0.8 px a
   tick while the motion of their primitives *relative to each other* was **3.4-13
   px a tick on 9-13% of contexts, peaking at 36**. That is pose animation, it is
   large, and no table smoother can reach it.

   **The boundary, restated where it actually falls.** The table technique works on
   a *continuous quantity in an addressable slot*, and a pose is not one: models are
   rigid, there is no per-part matrix anywhere in the subtree, so "interpolate the
   matrices" is not available (see "The model pipeline has no skeleton" in
   `docs/GAME_INTERNALS.md`). What is **not** true is that this makes pose
   interpolation impossible — only that it cannot be done from a table, and cannot
   be done from the display list either.

   **The open door is the vertex fetch inside `func_80032588`.** Before projection
   and before culling, a face is identified by its index in the mesh — exact rather
   than inferred — and a morph applied there comes out through the game's own
   transform, so there is no camera to subtract and no per-object mean to take out.
   Both problems that killed the packet layer are absent by construction. What it
   needs is **where the loop reads vertices and how many**, which is a runtime
   observation (log the reads) rather than a decode of the model format. The
   remaining question it must answer is whether a pose change is a *swapped model
   index* — in which case morphing needs a correspondence between two meshes — or a
   rewrite of the same mesh's vertex data, in which case it is a straight lerp.

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
   area without walking there. Inventory, equipment, magic and the entity table
   are all still unmapped.

