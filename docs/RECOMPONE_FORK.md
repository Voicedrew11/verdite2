# The RecompOne fork

How `tools/RecompOne/` is kept, why it is vendored rather than patched, and what
the merge to upstream `0409bc2` decided — the model for the next one. The
individual changes the port carries are catalogued in `docs/RECOMPONE_PATCHES.md`.

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
