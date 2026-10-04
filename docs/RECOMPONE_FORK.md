# The RecompOne fork

How `tools/RecompOne/` is kept, why it is a subtree of a fork rather than
patched, and what the two merges so far decided — `0409bc2` set the model and
`d81dec8` followed it. The individual changes the port carries are catalogued in
`tools/RecompOne/docs/RECOMPONE_PATCHES.md`.

**`tools/RecompOne/` is a git subtree of `Voicedrew11/verdite-recompone`: its
sources are tracked here, so a fresh clone already has a working recompiler and
an edit made inside it is a change to this repository like any other.** It used to
be a gitignored clone of an upstream pin with `tools/RecompOne/patches/*.patch`
replayed over it on every run, and the patches are *kept* — they are no longer
replayed. It used to be a tracked copy with a throwaway local fork repo
(`tools/RecompOne.git/`) beside it; that repo is gone.

**The fork is standalone, not a GitHub network fork.** Its home is
`Voicedrew11/verdite-recompone`
(`https://github.com/Voicedrew11/verdite-recompone.git`, branch `main`). It is
upstream-derived, so it still shares ancestry with `BlackLabelHQ/RecompOne`
(`master`) and upstream can be merged three-way: the fork's history is this
repository's own history of `tools/RecompOne/` from `git subtree split`, with the
vendoring import (Verdite2 `7c198b5`) grafted onto upstream `0409bc2` and the
`d81dec8` harvest (Verdite2 `1138329`) grafted as a real merge of upstream
`d81dec8`. So `git merge-base main upstream/master` is the sha in
`tools/RecompOne/UPSTREAM` — currently `d81dec8`.

**Why that changed, because the reason generalises.** `git apply` matches text
context and knows nothing about what upstream changed, so upstream's Rider
reformat (`410f0d4`) broke 28 of the 39 patches at once — and would have broken
them again on every future pin move, because a diff is permanently written
against context that has to still be there. A fork with a **merge base** lets a
three-way merge reason about changes rather than appearances: the reformat is
absorbed once, as a commit. Measured: taking one real upstream commit
(`67fc37c`, 23 files) costs 23 conflict hunks as a merge, against hand-authoring
a ~700-line patch carried for the life of the project — which is exactly what
`0034` (1,315 lines) and `0037` (2,479 lines) already were. **In the patch
workflow every gift from upstream becomes permanent debt.**

  - `tools/RecompOne/UPSTREAM` — the upstream commit the fork was last merged
    from, and so the merge base for the next harvest. Currently `d81dec8`.
  - **Harvesting happens in a working clone of the fork**, not in this
    repository. The suggested location is a sibling checkout, e.g.
    `~/Desktop/verdite-recompone`. There it is a plain
    `git fetch upstream && git merge upstream/master` (or `git cherry-pick <sha>`
    for one commit). The fork carries `harvest_upstream.sh` at its root: it
    prints what is new and starts the merge, and it prints the acceptance check
    word for word — *then run the game and check: 144 fps drawn at 20 ticks/s,
    the agent beacon reaching an fdat overlay with a real position*. Then each
    game takes the result with `bash scripts/setup_tools.sh --pull-fork`.
  - `bash scripts/setup_tools.sh --pull-fork [ref]` is
    `git subtree pull --prefix=tools/RecompOne --squash <FORK_URL> <ref>`
    (`main` by default); `--push-fork` is the matching `git subtree push`. The
    fork URL and branch are defined in one place at the top of `setup_tools.sh`,
    overridable with `VERDITE_FORK_URL` and `VERDITE_FORK_BRANCH` (a local path,
    for testing). `git subtree` is a separate package on some distros, so the
    flags check for it and say so.
  - `--signatures` fetches
    `AutoConfigure/signatures/psyq.json` from upstream at the pinned `UPSTREAM`
    sha with curl from `raw.githubusercontent.com`; the file stays gitignored in
    both this repository and the fork. `--sync-upstream` is **removed** — it
    prints that harvesting moved to the fork clone and how, then exits 2.
  - The fork has its own `.gitignore` (the 15.7 MB `psyq.json`, `bin/`, `obj/`)
    and a README that says what the fork is and states the no-AI-PR policy;
    upstream's `LICENSE` and copyright are untouched.

**Nothing goes upstream. Not a pull request, and not an issue either.** Upstream
rejects AI-authored pull requests, and this project does not file issues against
it: a defect found here is fixed in the fork, which is the whole point of having
one.

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

## Editing the fork from a game

A change to the runtime is made inside `tools/RecompOne/` in whichever game
needs it, exactly like any other source edit — but because the tree is a subtree
of the fork, it is kept to itself. **A commit that touches `tools/RecompOne/`
touches nothing else**, and it is pushed back to the fork with
`bash scripts/setup_tools.sh --push-fork` soon after, so the game's copy is
always equal to some commit of the fork. If it is not, the next `--pull-fork`
merges into a tree the fork has never seen. Mixing a fork edit with game code
would make `git subtree push` split the wrong history.

The other games do not move automatically: a change reaches one when the user
runs `--pull-fork` there, on that game's schedule. Every fork change has to keep
Verdite2's acceptance test passing — one game is never allowed to break another
because it needed something. That is Phase 2 of `docs/SHARING_PLAN.md`.

`patches/*.patch` and `docs/RECOMPONE_PATCHES.md` moved into the fork on
2026-10-02 (Phase 2 of `docs/SHARING_PLAN.md`), so every game has them at
`tools/RecompOne/patches/` and `tools/RecompOne/docs/RECOMPONE_PATCHES.md`; the
`0001`-`0085` numbering is unchanged. They are the record of what the fork
changed and why, kept beside the code that cites them, and nothing applies them. The fork's history
starts at the vendoring import, which carries `0001`-`0039` folded into one
commit; everything after it is a commit of its own.

## Why `--squash`

The fork is pulled with `--squash`, one commit per pull, not its full history.
Full history would bring all of upstream's history into every game repo — and the
blobs of the 15.7 MB PSY-Q signature bank among it. A squash commit carries only
the tree at the fork commit being taken, so a game carries exactly what it
builds, and a fresh clone still builds with nothing fetched. The cost is that
`git log` in a game shows one commit per pull rather than the fork's individual
commits; the fork's own history is where those live, and where the reason for a
change is.

That trade was not decided on argument alone. It is the round-trip test: on a
scratch branch of the fork, a trivial edit is made in Verdite2's
`tools/RecompOne` as its own commit, `git subtree push`ed to the fork, confirmed
to arrive clean, then `git subtree pull`ed back. Run on 2026-10-02 with
`--squash`: the push arrived as one commit whose parent was the fork commit the
subtree was added from; a fork-side commit on top pulled back as one squash
commit, the subtree's tree equal to the fork's; a second edit pushed after that
pull arrived as one commit on the fork-side one. Linear every time. The fork
repository's pack is 16 MiB, almost all of it upstream's history; that is what
full history would have added to every game. The scratch branches were deleted
and the fork's objects pruned.

### The add commit says where the split starts

The first push in that test carried **this repository's whole history** into
the fork. The switch-over was a `git rm -r tools/RecompOne` commit followed by
`git subtree add`, and `git subtree split` maps a commit that lacks the prefix,
but whose parents had it, to *itself* — so the removal commit, and everything
behind it, became ancestors of the pushed commit. The add commit therefore
carries the trailers a `--rejoin` writes:

```
git-subtree-dir: tools/RecompOne
git-subtree-mainline: <the removal commit>
git-subtree-split: <the fork commit added>
```

which tell `split` that the subtree at the removal commit *is* that fork commit,
and to walk no further back. With them, the push was the one commit above. Any
game that is switched over the same way needs the same trailers on its add
commit, and the first `--push-fork` from it should be checked for exactly that:
`git rev-list --count <fork-branch> ^<fork-commit-added>` is the number of
commits being sent, and none of them may be a game commit.
