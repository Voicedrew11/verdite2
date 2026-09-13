# The RecompOne fork

How `tools/RecompOne/` is kept, why it is vendored rather than patched, and what
the two merges so far decided — `0409bc2` set the model and `d81dec8` followed
it. The individual changes the port carries are catalogued in
`docs/RECOMPONE_PATCHES.md`.

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
    and so the merge base for the next harvest. Currently `d81dec8`.
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

## The merge to `d81dec8`

One upstream commit, 35 files, 1,582 insertions: a `jr ra` codegen fix, real
`ChangeTh` threading in `BiosB`, hardware timers polled from `Interrupts`, a
`LibPress` MDEC HLE, presentation decoupled from the interface, and `FrameClock`
rewritten. Seven conflicts. The acceptance test passes on both timelines —
`open → game → fdat02 → fdat05`, slot 2 at hp 46/86 in area 1, 144.0 fps drawn at
20.0 ticks/s with `[present] wide 288, plain 0, vram fallback 0`, and 60.0 fps at
19.7-20.0 ticks/s under `KF2_VSYNC=block`. The vertex map reads 91.6-94.7% hit
while moving, and `scripts/check_gate.py` reports 0 violations.

**The one that broke the game is `FrameClock`, and it broke it two rooms away
from where it was edited.** Upstream repurposed the class: it used to be the
*host* throttle and nothing else, and it is now the **guest vblank clock** —
`Interrupts.VBlankCount` is `FrameClock.Count` and `Interrupts.ClockMs` is
`FrameClock.Now`, both advanced only when `FrameClock.Catch()` is called from
`TickVBlank`. The port gates `TickVBlank` off, because on its timeline
`LibEtc.AdvanceVBlanks` delivers IRQ 0 on its own wall-clock grid and raising it
here too would deliver every vblank twice. Gating the *call* therefore froze the
*count* at 0 — and `BiosB`'s memory-card pump is `if (VBlankCount ==
_cardEventFrame) return;`, so the card never pumped, the save never loaded, and
the run sat in `GAME.EXE` at `hp 0`, `area 0`, `slot 0` forever with no error and
no CD read. Only the IRQ is gated now; the count always advances. **The lesson is
the general one: upstream moving a clock's ownership silently changes what a gate
on it means.**

**`FrameClock` therefore holds two clocks that must not touch.** `FrameMs` is the
guest 60/50 Hz and is upstream's; the host ceiling — `TargetFps`, `Throttle`,
`LastWaitMs`, all that is left of `0025` — is a separate block below it with its
own grid. Upstream now throttles in `PresentLoop`, which this port never enters,
so `Runtime.PresentFrame` calls `Throttle()` beside upstream's `MarkFrame()`.

**Three upstream defaults were refused, each for the same reason: they are
behaviour, not structure.**

- **`ScanCrossImage` became unconditional.** `open`, `game` and `end` share one
  address range, so a `jal` from an `fdat` module is added as an entry point to
  *all three* — splitting a real function in the two overlays the call cannot
  have meant. Measured: 2234 functions to 2370, and `0x80025D38` colliding across
  all three, which renames it `func_80025D38_game` and breaks `AreaWarp` and
  `AutoReload`. Kept behind `config.PointerScan`, where it was.
- **`LibPress` binds names the funcmaps already carry.** Upstream added
  `DecDCTin`, `DecDCTout`, `DecDCTinSync`, `DecDCToutSync` and `DecDCToutCallback`
  to `SdkPatches`, and `merge_sdk_names.py` had merged those names into
  `open`/`game`/`end` as legibility only — so the recompiler reported `applied 63
  patches, 11 reimplementations` and the intro's MDEC path silently moved to an
  HLE nobody has watched. The five names are back to `func_`, the count is `63, 0`
  again, and they are in the script's `HLE_NAMES` fallback. Binding them is a
  deliberate experiment for later, not a merge artifact.
- **Upstream's `Classify` cache** keys on the clip rect and `GpuHle.ViewVersion`
  and is invalidated from the one eviction site upstream has. The port's
  `GetOrCreateRt` is not that site — it also destroys a target when the aspect
  moves the margin, and `PresentDisplay` destroys idle ones — so the cache would
  hand back a destroyed target. Left uncached, as the port's `GlCore` already was.

**What was taken.** `GpuHle.Hold`/`Release` and `ViewVersion`; `TakeExceptionStack`
on all three delivery paths; `Runtime.Timers?.Poll`; `_inDataCb` and the one-shot
`_dataIntr` in `LibCd` (the `0005` graft keeps its `DiskError` guard and now sets
both); `Log.VSyncOn`; `LibEtc.LastWaitMs`, which feeds upstream's new
`LibGpu.AutoPresent` — a present forced from `PutDispEnv` when the display rect
moves and the game has not `VSync`ed for 100 ms. That grace means it never fires
in play, and the port measures exactly 144.0 fps with it in.
