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

   **What is still left is the part no counter answers**, and it is the user's: is
   20 actually right, is a 20 fps default acceptable or should the picture be drawn
   faster than the world runs, does the extrapolated camera swim or lag, and does
   `KF2_SMOOTH_POS=1` jitter against a wall? Frame smoothing still defaults to
   **off** as well as position — while the boundary was broken the logic phase was
   pinned to 0, so it had never run at any rate and its picture is new; a 50 ms tick
   makes it matter more than the 33 ms one did. Two things a counter *can* still be pointed at:
   stage 2's per-tick counters (it holds them but cannot be gated wholesale, since
   `DrawOTag` is in its subtree), and stage 13's jitter accumulator at
   `0x8006E608`, which no hook can reach because it is in stage 13's own body.
6. Work out the rest of the `CD/COM/*.T` archive formats when asset work starts.
   [IvanDSM/KingsFieldRE](https://github.com/IvanDSM/KingsFieldRE) has KFModTool
   and format notes covering this game across its regional variants (no symbols,
   `.map` or ELF, so it does not help the function maps).
7. **Report the runtime bugs upstream as issues** (not PRs — see "Upstream
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
8. **Take the twice-drawn pair for sub-pixel vertex positioning, and flip its
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
9. ~~**Look at the Z-buffer in the cave.**~~ **Closed.** Retired as a user option:
   the game's visibility is OT draw order, and the skybox (near-projecting, drawn
   first) plus coplanar decals contradict any single per-pixel depth, so no z-test
   can be globally correct on this content. Mechanism kept for diagnosis only,
   behind `KF2_ZBUFFER`/`KF2_ZBUFFER_PROBE`. See "Z-buffer" in
   [RENDERING.md](RENDERING.md).
10. **Decide what the interface should be scaled by, and by eye.**
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
11. Play further in. Now that the menu opens, the parts of the game it reaches —
   inventory, equipment, magic, the map — have never run, and each is a screen
   with its own code path. `mods/kf2debug` is the instrument for this: its state
   readout is how the rest of `buf2` gets named, and its area warp reaches an
   area without walking there. Inventory, equipment, magic and the entity table
   are all still unmapped.

