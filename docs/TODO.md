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
| ~~**The Z-buffer still shows sky through nearby walls.**~~ Answered — the skybox projects *near* and the game links it at the far end of the ordering table on purpose; believing its recovered SZ put the sky in front of the world. `patches/recompone/0025` hands the far band back to painter's order. | The clear-at-tail defect (`0016`) was real, fixed and measured, but not the cause. Picture re-check pending. | "The clear landed at the tail of the frame" and "The second cause" in [RENDERING.md](RENDERING.md) |
| **The camera does not feel consistent in every direction.** | Four candidates, three of them certainly *real* effects whether or not they are the one being felt (the game turns you slower while walking; yaw and pitch scales differ; diagonals saturate sooner; the ramp is radial). The measurement to take is written out. | "Open: the camera does not feel consistent" in [INPUT.md](INPUT.md) |
| **The twin-stick controls broke after the dragon stone went into the fountain.** Not reproduced. | Three candidates, each one memory read apart: the rate guards at `0x8019955C`/`0x80199558`, the action-mask table being rewritten, or the player action state parking in an arm that never calls the control routines. "Did the D-pad still work?" settles the third. | "Open: the twin-stick controls broke" in [INPUT.md](INPUT.md) |
| **`KF2_WIDESCREEN` was overridden mid-session by the saved aspect.** | Env-forced aspects are not as forced as `_forced ?? saved` in `Widescreen.Install` reads. Anything A/B-ing two aspects should pin the *saved* setting instead. Worth tracking down. | "There is a third cull", trap at the end, in [WIDESCREEN.md](WIDESCREEN.md) |
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
   [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md); the measurement is the
   `framestats` mod.
5. ~~**Confirm the buf5 view block**, which is all 60 fps is now blocked on.~~
   Overtaken: the player state was found by reading the emitted C# instead, and
   it is not in `buf5` at all — see "Player state" in
   [GAME_INTERNALS.md](GAME_INTERNALS.md). The stage to gate is **3**
   (`func_8002A550`), which holds the pad read, the turn, the walk and the angle
   fold, and every per-tick delta it produces is now a named address. What 60 fps
   still needs is the delta halving, and that is now a small edit rather than a
   hunt: halve the turn rate at `0x8019955C` and the walk speed at `0x80199558`
   on alternate frames, or scale them the way `patches/Analog.cs` already scales
   the velocities they feed. The buf5 four are presumably the *rendered* camera and
   can be left alone.
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
9. **Look at the Z-buffer in the cave.** The mechanism is the recovered SZ
   perspective correction already measures, but a twice-drawn OT cannot take this
   pair (the second pass would fail every test against the first), so the picture
   is the only test, and it is what is keeping the default off. Interpenetrating
   rocks should sit still; coplanar floors should not z-fight; the HUD should be
   identical. See "Z-buffer" in [RENDERING.md](RENDERING.md).
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

