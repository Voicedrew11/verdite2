# The runtime: what a static recompilation loses, and the patches for it

`tools/RecompOne/` is gitignored, so **any edit made inside it is lost on a fresh
clone**. Changes to the recompiler or the runtime must be captured as a numbered
patch in `patches/recompone/`, applied in order by `scripts/setup_tools.sh` (see
"Setting up the tools" in [DEVELOPMENT.md](DEVELOPMENT.md) for why it peels the
stack newest-first before applying it oldest-first).

**One root cause is behind half of this file.** On hardware, PSY-Q reaches the
outside world through interrupts: a finished DMA raises IRQ 3, a CD sector raises
IRQ 2, the BIOS refills the pad buffer from VBlank. A recompiled build has **no
exception path**, so none of those entry points ever runs. Nothing errors — the
transfers themselves are emulated and complete fine — and only the work the game
does *inside* the handler silently disappears.

Its sibling failure is just as quiet: **anything the runtime refreshes only at
`VSync` is invisible to a game that stops calling `VSync`.** The three CD hangs,
the menu deadlock and the dead ending screen below are all that shape, in
different libraries. When something completes but produces no visible effect, or
a wait loop never satisfies, check which of the two it is before looking anywhere
else.

## DMA callbacks: the thing that was actually missing

With libcdstream mapped, the movie ran end to end and the screen stayed black.
Everything looked right: `StFreeRing`/`DrawSync`/`PutDrawEnv`/`DrawOTag` cycling
once per frame, and `KF2_LOG=mdec` showing real decodes —

```
[MDEC] decode depth=3 signed=False bit15=True mbs=300 wordsOut=38400
```

300 macroblocks, 38400 words = 320×240×2 bytes, a full frame, 312 of them. The
decoded frames were landing in RAM and never reaching VRAM.

The reason is a general problem with static recompilation, not a King's Field
one. **On hardware a finished DMA raises IRQ 3 and PSY-Q's interrupt entry reads
DICR and calls the channel's callback. A recompiled build has no exception path,
so that entry never runs and every DMA callback is dead.** Nothing errors; the
transfers themselves are emulated and complete fine. Only the work the game does
*inside* the callback silently disappears — and here that work is the whole
picture:

```
DMACallback(1, 0x800142A0)        <- MDEC-out DMA completion callback
0x800142A0:  LoadImage(rect, buf) <- uploads one 16x240 strip
             rect.x += rect.w     <- 20 strips = one 320x240 frame
             DecDCTout(buf, n)    <- decode the next strip
```

`DMACallback` is `0x8001EAF0` / `0x8005FC30` / `0x8001B0BC`, reached indirectly
through a per-channel wrapper and libapi's own driver table, so it has no direct
`jal` anywhere — `KF2_TRACECALL` on the dispatcher is what caught it. It is
identified by its DICR arithmetic: it indexes a callback table by channel,
returns the previous entry, and on install ORs `0x00800000 | (0x01010000 << ch)`
into DICR, clearing those bits again when passed null.

`patches/recompone/0004-libapi-dma-callbacks.patch` adds
`RecompOne.Runtime.Sdk.LibApi`, which records the table and runs the callback
from `Dma.Complete`. It is deliberately **not** gated on DICR: the routine that
would have set those bits is the one being replaced, and a registered callback
already encodes the same intent.

Two things to know if this misbehaves later. The transfer completes inside the
store that starts it, so the callback runs re-entrantly on top of the caller —
`LibApi.Complete` snapshots and restores the CPU context the way
`Interrupts.Deliver` does, and the strip loop above therefore recurses about 20
deep per frame instead of iterating. And `Interrupts.Deliver` now reports a
dropped IRQ once per line; a run where *every* IRQ misses means the
interrupt-environment offset `BiosB` derives from `HookEntryInt` (`A0 - 0x36`)
does not fit this game's PSY-Q version. For KF2 the real interrupt-callback
table is at `0x8003DD44 + irq*4` and the DMA callback table at
`0x8003DD74 + ch*4`, neither of which the runtime's offset finds — which is why
routing `DMACallback` to the runtime beats trying to fix the offset.

Verified by dumping the VRAM shadow mid-playback: the From Software logo and the
title sequence, in both framebuffers. (Window capture is not available in this
session — the compositor screenshots the lock screen — so the check was on VRAM
rather than on the presented frame.)

## The three ways a CD read can hang

Every one of these is the same root cause as the DMA callbacks — **a static
recompilation has no interrupt path** — and each one presents completely
differently. The game uses all three, so all three had to be fixed, and each was
hidden behind the one before it.

**1. Polled `ReadN`.** `OPEN.EXE`'s title-screen loader issues
`CdControl(CdlSetmode, 0x80)` and `CdControl(CdlReadN, &loc)`, then per sector
runs `while (CdReady(0,0) != CdlDataReady) ;` followed by
`CdGetSector(buf, 0x200)`. The raw `CdReady` spins on a global only libcd's
interrupt handler writes. Symptom: **total silence** — the log stops dead, no
VSync, nothing, because the CPU never leaves the loop.

Fixed by mapping `CdReady` and `CdGetSector` and teaching the HLE that a read
with no callback registered always has its next sector ready; `CdGetSector`
advances the drive one sector, which is what makes the loop walk the file.

**2. `CdReadSync`.** libcd's own file reader is
`CdRead(...); while (CdReadSync(1, 0) > 0) ;`, and `CdReadSync` returns a
sectors-remaining counter that only the interrupt decrements. Same silent spin.
The instructive part: `OPEN.EXE` runs this identical routine and *survived*,
because the counter happened to read 0 and the loop fell straight through.
`GAME.EXE` read it as garbage and hung. **An unmapped SDK wait can look fine
purely by accident** — do not treat "the intro works" as evidence the library is
mapped.

**3. Kernel events.** `GAME.EXE`'s loader is event-driven. Its init opens, in
`EvMdINTR` (callback) mode:

| class | spec | handler | what it does |
|---|---|---|---|
| `HwCdRom` `0xF0000003` | `EvSpCOMP` `0x20` | `0x8001794C` | job state machine; issues each `CdRead` |
| `HwCdRom` | `EvSpDR` `0x40` | `0x80017A98` | `CdGetSector(job->buf, 0x200)` per sector |
| `HwCdRom` | `EvSpERROR` `0x8000` | `0x80017B14` | |
| `RCntCNT3` `0xF2000003` | `EvSpINT` `0x02` | `0x80017850` | frame counter the CD timeout rides on |

On hardware libcd's interrupt handler turns each CD interrupt into a
`DeliverEvent`. The runtime already has the whole event manager — `OpenEvent`,
`EnableEvent`, `DeliverEventIntr` with `EvMdINTR` dispatch, used by the memory
card — but **nothing ever delivered `HwCdRom` or root-counter events**, so the
job queue never took a step.

This one does *not* present as a freeze. The main loop keeps running and the
loading screen animates happily forever, which reads like a slow load rather
than a bug. Symptom: **everything alive, nothing progressing.**

`0005` delivers them at VSync rather than inside the command, so a handler that
issues the next command does not recurse on top of the one that called it; new
events queued by a handler are drained in the same tick, up to a bound. `EvSpACK`
goes out for every command and `EvSpCOMP` only for the commands with a second
response (`Init`/`Stop`/`Pause`/`SeekL`/`SeekP`/`Standby`), plus one at the end
of an HLE `CdRead` — modelling the pause real libcd issues once the last sector
is in, which is the completion the caller actually waits for.

| function | `open` | `game` | `end` |
|---|---|---|---|
| `CdReady` | `0x8001AF5C` | `0x8004AD18` | `0x80017528` |
| `CdReadSync` | `0x8001AF7C` | `0x8004AD38` | `0x80017548` |
| `CdGetSector` | `0x8001B224` | `0x8004AFE0` | `0x800177F0` |

These are 8-instruction thunks that match in several places, so `game` and `end`
come from the delta the unambiguous rows of the same object agree on
(`+0x2FDBC` / `-0x3A34`), confirmed by the internal routine each thunk forwards
to. **Note that is not the delta in the table below**: `CdInit` sits at
`+0x315B0`, so libcd is split over more than one object and its thunk block and
`cdinit` move independently.

## The interrupt-callback table cannot be guessed

**Symptom:** `unmapped call: 0x0BFF0FFE` from `Runtime.PresentFrame` →
`Interrupts.Deliver`, arriving on the loading screen after minutes of correct
play. The address is not code; it is not even aligned.

`Interrupts.Deliver(irq)` finds the handler to call by reading
`BiosB.IntrEnvInInterruptAddr + 2 + irq*4`, where that base comes from the
argument the game passed to BIOS `B(19h) HookEntryInt`, minus `0x36`. That
argument is a **jmp_buf**, not a callback table — in `GAME.EXE` it is
`0x8007437C`, and the word above it (`jb[1]`, the interrupt stack pointer) is
`0x8007535C`, 4 KB higher. Where the callback table sits relative to that buffer
is a property of one link of one PSY-Q version, so here the derived slot,
`0x80074348`, is an ordinary game variable. For most of the run it happens to
read zero and the delivery is dropped silently; the moment the game stores
something there, the runtime calls it.

The real table is the one libapi's `InterruptCallback(irq, func)` indexes. That
function is easy to recognise and gives the address directly: it computes
`table + irq*4`, returns the previous entry, ORs `1 << irq` into `I_MASK` through
the pointer in its `.data`, and clears the slot when passed null.
`ResetCallback` next to it zeroes 11 consecutive slots — `irq` 0 to 10 — which is
what fixes the base rather than leaving it one word ambiguous.

| overlay | `InterruptCallback` | callback table |
|---|---|---|
| `open` | `0x8001E75C` | `0x8003DD48` |
| `game` | `0x8005F8CC` | `0x8006E3D4` |
| `end` | `0x8001AD28` | `0x80038D90` |

Two independent checks. **Statically**, `table + 11*4` lands exactly on the DMA
callback table the DMA interrupt dispatcher walks (`0x8003DD74` in `open`,
`0x8006E400` in `game`) — the two tables are adjacent, as the 11-slot layout
predicts. **At run time**, the slots read back as the functions they should be:
`game` slot 3 is `0x8005FAE0`, which is that same DMA dispatcher (it masks DICR
with `0x7F000000`, walks seven channels, clears each flag and calls the
channel's callback), and slot 0 is `0x8005F45C`, the vblank handler that bumps
the frame counter and runs the registered `VSyncCallback`.

`patches/recompone/0006-irq-callback-table.patch` adds
`Interrupts.CallbackTable` for a game to set, and — for the case where nothing
has — makes the derived path refuse a handler that is not a word-aligned
function the dispatcher knows, reporting it once instead of calling it. The
addresses themselves are game knowledge, so they live in `Program.cs`, rebound
per overlay from `OverlayLoadedEvent`.

Note what this fixes beyond the crash: those two handlers had **never run**. The
vblank callback the game registers now fires once a frame, which is what
`VSyncCallback` users expect.

## The vblank fired when the game asked

**Symptom:** the title-menu music plays at roughly half speed, and locking the
frame rate below 30 (`KF2_FPS=15/20`) slows music everywhere the same way. The
title screen renders at ~15 fps, not 30.

**Mechanism:** the port advanced the emulated vblank once per HLE `VSync` call
and nowhere else. `LibEtc.VSync` incremented `_vcount`, delivered the RCNT3
vblank BIOS event (`BiosB.DeliverEventIntr`, class `0xF2000003` spec `0x0002`)
and dispatched `VSyncEvent`; `Runtime.PresentFrame` additionally ran the PSY-Q
IRQ 0 chain — all four at VSync-call frequency, and nothing else in the checkout
advanced any of them. On hardware the vblank interrupt fires every 16.7 ms
whether or not the main loop is ready: a loop blocked on a CD read misses
pictures, not vblanks. In-game the loop issues two VSync calls per rendered
frame (85.7% of frames charge two vblanks), so at 30 fps the domain ticks 60/s
and the sound sequencer — fed by root-counter interrupt events opened beside the
SPU flush (`OpenEvent(0xF2000000|n, …, EvMdINTR, …)`) — keeps time. The title
loop presents through DrawSync, one `VSync(0)`, PutDrawEnv, PutDispEnv,
DrawOTag — one VSync call per picture, and the streamed-picture rate is paced at
real drive speed by `LibCdStream.StreamLoop`, which stays. So on the title the
whole vblank domain ran at ~15 Hz and the music played half speed; at
`KF2_FPS=15` it ran at half that again.

**Fix:** `patches/recompone/0021-vblank-wall-clock.patch` advances the vblank on
a wall-clock 60 Hz grid. Each `VSync(mode >= 0)` call catches up every boundary
missed since the last one — counter increment, RCNT3 event, `VSyncEvent`, IRQ 0
— as a burst, which is what the hardware's interrupt effectively did across the
same gap. IRQ 0 moved with it, out of `PresentFrame`. Presentation cadence
(`PresentFrame`, `FrameClock.Throttle`, the FramePacing floor) is untouched. A
host stall longer than 120 vblanks (~2 s — window drag, breakpoint) discards the
stale backlog and processes only the current vblank, rather than fast-forwarding
the game through the gap. Mode semantics are
unchanged (`<0` returns the count, `==1` returns immediately); the game passes 0
at every call site in all three overlays.

**Measurement:** with the framestats mod, title windows go from
`vblanks/frame 1:100%` to a spread worth ~60 vblanks/s; in-area windows stay
`2:100%` at 30.0 fps. Expected behaviour change: title-screen waits counted in
vblanks (the attract-demo timeout among them) now elapse at hardware's rate
rather than at picture rate.

One loose end recorded for the next audio timing mystery: the sound driver also
opens RCNT1/RCNT2 interrupt events, which the runtime never delivers. Nothing
visibly misses them today, but they are the first suspect.


## The menu deadlock: input only moved when the game drew

**Symptom:** press the button that opens the in-game menu and everything stops —
last frame still on screen, no error, no log output on any channel, process
alive and burning CPU. Exactly the "total silence" signature of the polled CD
read, and just as misleading.

`dotnet-stack` named it immediately:

```
BiosB.PadRead → func_8005FE64 → func_8005F564 → func_80022EFC → func_80018E80
```

`func_8005FE64` is the `B(16h) PAD_dr` thunk and `func_8005F564` is libetc's
`PadRead(id)` — `PAD_dr`, then `~*(u_long*)0x8006EAE4`, the buffer the game
registered with `PAD_init2`. `func_80022EFC` is the caller that matters:

```c
do { } while (PadRead(1) != 0);      /* wait for every button to come up */
```

It draws nothing and it never calls `VSync`. **In the runtime, host input is
polled only inside `PresentFrame`, which only runs from `VSync`** — so the pad
word this loop reads is a snapshot frozen at the last frame drawn, taken while
the button that opened the menu was still down. The release can never arrive.
The game had walked into a wait that, in this port, nothing could ever satisfy.

On hardware the BIOS fills that buffer from its own VBlank interrupt, so
`PAD_dr` is fresh whether or not the game vsyncs — the loop is perfectly
reasonable code.

`patches/recompone/0007-pad-poll-outside-frame-loop.patch` makes `PAD_dr` pump
the host itself, rate-limited to 4 ms (a game in this loop calls it ~200,000
times a second; a VBlank is 16 ms, so 4 ms is still fresher than hardware).
`HostWindow.PumpInput` takes in events and re-polls input, and redraws at most
every 16 ms so the window stays live while the game is stuck outside its frame
loop — `Present` stamps the same clock, so a normally running game never renders
twice in a frame.

This is a general RecompOne bug, not a King's Field one: any game that waits on
the pad without vsyncing deadlocks the same way. Worth an upstream issue.

Two things to take from it. First, **a wait loop that reads state the runtime
only refreshes elsewhere is the recurring shape of this port's bugs** — the three
CD hangs were the same mistake in a different library, and `VSync`-driven
delivery is load-bearing for far more than frames. Second, the phantom is
readable: when the game hangs, the last pad state in the log is the button that
was held at the last drawn frame, which points straight at the input path.

## The ending screen

`END.EXE` is a movie player. `func_800119A4` inits the GPU, then calls
`func_80011CC4` twice: `\OP\ED0.S` (1700 frames, timer `0x06A2` = 1698) and
`\OP\ED1.S` (2673 frames, timer `0x0A6D` = 2669). The last displayed frame of
the second file is the still with "The End" and the two copyright lines. After
that it waits 150 × 6 `VSync`s, `CdControl(CdlStop)`, and falls into
`while(1);` at `0x80011A50`.

On hardware that spin is the ending: the GPU keeps scanning out the last
framebuffer and the picture stays. Here a frame reaches the window only from
`PresentFrame`, which only runs from `VSync`, so the same loop leaves the
image up but the window dead — no events, no close, 100% of a core. That is
what "it crashed on The End" was. `patches/EndingHold.cs` hooks
`func_80011CC4` and, after the second movie returns, `VSync(0)`s forever
instead of reaching the spin.

Two dispatcher leftovers ride in with the executable swap and are the same
shape of bug, even though they were not what froze the window:

- `HandleRegionOverwrites` only dropped an overlay that was **fully contained**
  in the new one. `GAME.EXE` is `0x5E000` bytes at `0x80011000`, `END.EXE` is
  `0x29000` at the same base, so GAME was not contained and stayed mapped —
  every `GAME.EXE` function past `0x8003A000` remained callable. The same hole
  hits a smaller FDAT module loading over a larger one, and quit-to-title
  (`OPEN.EXE` over `GAME.EXE`). `patches/recompone/0008` unloads on any
  overlap.
- The FDAT modules sit at `0x8019F07C`, which does not overlap any executable,
  so even with that fix they would survive into `END.EXE`. Its heap starts at
  `0x80100000` and covers that RAM. `Program.cs` unloads every `fdat*` overlay
  when `open` or `end` loads.


## The patches to the checkout, one by one

Seventeen of the twenty-one are load-bearing; `0002`, `0003` and `0015` are
diagnostics and `0013` is a settings-placement hook. Several need **no recompile**
— they change runtime behaviour only — and that is noted where it applies.

**`patches/recompone/0001-bios-load-return-1.patch` is required to boot.** The
runtime's BIOS `Load` (A(42h)) returned the header pointer, but the real BIOS
returns 1 on success. King's Field's boot stub compares the result against 1
exactly and retries forever otherwise, so unpatched it spins in the loader
(~12,900 `Load` calls in 30 seconds) and never reaches `Exec`.

**`patches/recompone/0004-libapi-dma-callbacks.patch` is required for the intro
movie.** It adds `RecompOne.Runtime.Sdk.LibApi` so DMA-completion callbacks are
delivered at all; see "DMA callbacks" above for why nothing works without it.

**`patches/recompone/0005-libcd-interrupt-driven-reads.patch` is required to get
past the title screen.** It gives `LibCd` a polled read path and makes it deliver
CD-ROM kernel events, and gives `LibEtc.VSync` the vblank root-counter event. See
"The three ways a CD read can hang" for what each one unblocks.

**`patches/recompone/0006-irq-callback-table.patch` and
`0007-pad-poll-outside-frame-loop.patch` are required to open the menu.** The
first lets a game point the runtime at PSY-Q's real interrupt-callback table
instead of deriving one that lands in game data; the second lets `PAD_dr` poll
the host, so a game waiting on the pad without vsyncing is not waiting forever.
See "The interrupt-callback table cannot be guessed" and "The menu deadlock"
above.

**`patches/recompone/0009-perspective-correct-textures.patch` is what stops the
textures swimming.** It adds `GteDepth`, a screen-position-keyed table the GTE
fills as it projects and the GPU reads as it decodes a vertex word, and teaches
both renderers to use the depth it recovers. Nothing depends on it to run — every
vertex it misses is drawn exactly as before — but it is the largest single change
to the picture in the port. See "Perspective correction" in
[RENDERING.md](RENDERING.md).

**`patches/recompone/0010-subpixel-vertex-positions.patch` is the other half of
that same recovered number** — the fraction of a pixel the GTE truncates off a
projected vertex, which is what makes geometry twitch as it moves. It extends
`GteDepth`'s slots rather than adding a table, and it is the reason `setup_tools.sh`
peels the stack before applying it: two patches now edit the same file. Off by
default. See "Sub-pixel vertex positioning" in [RENDERING.md](RENDERING.md).

**`patches/recompone/0011-gte-depth-collisions.patch` is the rest of that table.**
Screen position is not a unique key, and dropping saturated vertices made every
large nearby polygon fall back to affine. The table now keeps several samples per
pixel, records the clamp, and picks per primitive; a leftover far Z is refused
rather than applied. See "The table is not unique" in
[RENDERING.md](RENDERING.md).

**`patches/recompone/0014-gte-zbuffer.patch` is a depth buffer from that same
number.** The GPU walks the ordering table back to front with no per-pixel test,
so two surfaces that actually interpenetrate take turns in front of each other.
The recovered SZ is interpolated per pixel and tested; a miss is painter's order,
so the HUD is untouched. Window depth is a fragment value rather than clip-space
Z, because putting SZ into `gl_Position.z` far-clipped the already-projected
triangle. Off by default. See "Z-buffer" in [RENDERING.md](RENDERING.md).

The other two are diagnostics and safe to skip: `0002-cdtrace-diagnostic.patch`
names the function behind a CD register access (`KF2_CDTRACE=1`), and
`0003-libgpu-sdk-trace.patch` gives `LibGpu` the `Log.Sdk` tracing the other SDK
libraries already had, plus a `Log.Gpu` line for every GP1 write. GP1 is
display/control only — a handful of writes per mode change — so tracing all of it
is cheap, and it is the only way to see whether the game ever enabled the display.

**`0008-unload-overlapping-overlays.patch`** is the executable-swap fix written up
under "The ending screen" above: `HandleRegionOverwrites` only dropped an overlay
*fully contained* in the new one, so `GAME.EXE` stayed mapped under the smaller
`END.EXE` at the same base. Any overlap is now an overwrite. The same hole hits a
smaller FDAT module loading over a larger one, and quit-to-title.

**`0012-exact-gte-vertex-map.patch`** replaces the screen-position key of `0009`
and `0011` with the **RAM address** the coordinate is stored at, following the
value through the game's own copies into the GP0 packet. **No recompile.** See
"Following the value through memory" in [RENDERING.md](RENDERING.md).

**`0013-settings-slot-in-section.patch`** adds `SettingsRegistry.DrawSlot(slotId)`
and one call to it in the display section, so a port option can sit *inside* a
runtime section rather than in a block underneath it. UI only — **no recompile**.
See "Patch settings" in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

**`0015-zbuffer-occlusion-census.patch`** is the diagnostic behind
`KF2_ZBUFFER_PROBE=2` — per-polygon bbox, depth range, table position and flags,
plus a 32×16 map read back from the depth attachment. **No recompile.** See
"Z-buffer" in [RENDERING.md](RENDERING.md).

**`0016-zbuffer-clear-at-frame-head.patch`** swaps two statements in
`GlCore.PresentDisplay` so the depth clear lands at the head of a frame rather
than the tail of the outgoing one. Real and measured, but it did **not** cure the
reported symptom. **No recompile.** See "The clear landed at the tail of the
frame" in [RENDERING.md](RENDERING.md).

**`0017-mouse-capture-and-motion.patch`** exposes the cursor: `MouseCaptured`,
`TakeMouseMotion` and `IsMouseButtonDown` on `InputManager`, forwarded from
`HostWindow`. **No recompile.** See "What the runtime had to grow" in
[INPUT.md](INPUT.md).

**`0018-imgui-fractional-framebuffer-scale.patch`** recomputes
`io.DisplayFramebufferScale` as a float in `HostWindow.OnRender`, because Silk's
divides two ints. **No recompile.** See "The interface only fits a monitor whose
scale is a whole number" below.

**`0019-popups-cannot-leave-the-window.patch`** clamps a popup's size to the
viewport and finishes what `Debug ▸ Reset view` starts, so no interface scale can
put the controls out of reach. **No recompile.** See "The scale can put the
settings out of reach" below.

**`0020-theme-apply-compounds-the-style.patch`** restores the style ImGui built
before `Theme.Apply` re-themes it, because `ScaleAllSizes` multiplies every size
field and `Apply` only resets some — leaving a `WindowMinSize` that grows on each
call and eventually floors a window past the screen, over `0019`'s clamp. **No
recompile.** See "The scale can put the settings out of reach" below.

## The interface only fits a monitor whose scale is a whole number

Reported as "the UI does not display correctly on my 1440p monitor, but correctly
on my 1080p one". It is neither the resolution nor the monitor: it is the
**compositor's scale factor being fractional**, and the port draws its whole
interface into the bottom-left corner of the window whenever it is.

`Silk.NET.OpenGL.Extensions.ImGui` 2.22.0, `ImGuiController.SetPerFrameImGuiData`:

```csharp
io.DisplayFramebufferScale = new Vector2(_view.FramebufferSize.X / _windowWidth,
    _view.FramebufferSize.Y / _windowHeight);
```

Both sides of each division are `int`, so the ratio truncates before it is ever a
float. `RenderImDrawData` then sizes **both** its GL viewport and every scissor
rectangle from it:

```csharp
int framebufferWidth = (int) (drawDataPtr.DisplaySize.X * drawDataPtr.FramebufferScale.X);
SetupRenderState(drawDataPtr, framebufferWidth, framebufferHeight);   // -> gl.Viewport(0, 0, fbW, fbH)
```

A GL viewport is anchored bottom-left, so a viewport smaller than the framebuffer
leaves dead margins along the **top and right** and clips every panel that
crosses them. An integer scale divides exactly and nothing is lost, which is the
whole of why one monitor is fine and the other is not — 2.0 is as safe as 1.0.

Measured, with the window fullscreen on the reporter's 2560×1440 monitor, which
KDE runs at `"scale": 1.15` (`~/.config/kwinoutputconfig.json`; the 1920×1080 one
is at `1`):

```
window.Size=2226x1252  FramebufferSize=2559x1439  ratio=1.1496
io.DisplayFramebufferScale = 1x1                      <- 1.1496 truncated
drawData -> ImGui GL viewport 2226x1252  (framebuffer is 2559x1439) => SHORT BY 333x187 px
```

GLFW 3.4 honours a fractional scale for the window's framebuffer (`wp_fractional_
scale_v1`), so the framebuffer really is 1.15× the logical window; it is only the
arithmetic that loses it. The reported screenshot agrees to a few pixels: a
1467×824 client area with the interface 1276 px wide (= 1467 / 1.15), a 188 px
dead strip on the right and a 131 px band at the top, both showing the GL clear
colour `(27,27,29)` — `Theme.Background`, i.e. nothing drew there.

The fix recomputes the ratio in floating point between `Update()` and `Render()`.
That window is the right one: `Update()` has already run `NewFrame`, so the
frame's layout is fixed and still in logical units — **input is untouched**, since
`io.MousePosition` and `io.DisplaySize` are both logical and stay that way — and
`ImGui::Render()` reads `io.DisplayFramebufferScale` when it fills the draw data,
so a value set anywhere between the two is what the backend sees. It cannot be
set *before* `Update()`, which overwrites it, nor after `Render()`, which has
already copied it. With it, the viewport goes to 2559×1439 — exactly the
framebuffer.

**The chrome is separately too large, and that one is ours.**
`HostWindow.QueryDpiScale()` reads `glfwGetMonitorContentScale` of the *primary*
monitor, once, at startup, and GLFW's Wayland path returns the **integer**
`wl_output` scale — so the 1.15 monitor reports **2.0**, and `Theme.Scale =
2.0 × UiScale` with the icon font baked at 26 px applies on *both* monitors no
matter which one the window is on. The reporter had already compensated by hand
with `UiScale=0.6999999` in `interface.ini`. Two things would fix it: prefer the
framebuffer/window ratio (1.15 here) over the monitor's content scale, and
re-evaluate it per frame, rebuilding the font atlas and re-applying `Theme` when
it changes — `Theme.Apply()` sets absolute sizes before `ScaleAllSizes`, so it is
safe to call again. Both are unwritten: the numbers above are measured, but how
large the interface *should* look is a judgement by eye.

One loose end, measured and not chased: a **programmatic** `IWindow.Size` set does
not raise Silk's `Resize` event under GLFW-on-Wayland, so `io.DisplaySize` goes
stale. Compositor-driven resizes do raise it — the reported screenshot has the
correct logical width — so nothing in the port depends on it.

## The scale can put the settings out of reach

The other half of the same report, and the worse half: at a large enough
interface scale the settings popup grows past the window, and the parts that left
cannot be reached at all — including the UI-scale field itself, which is the one
control that would undo it. Nothing recovers on its own, because the value is
saved to `interface.ini` and read back at the next start.

Two things have to be true at once for a window to be unrecoverable, and both
were:

```csharp
// Popups/Popup.cs
ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
ImGui.SetNextWindowSize(Size * Theme.Scale, ImGuiCond.Always);
// flags: NoTitleBar | NoResize | NoMove | NoDocking | NoSavedSettings
//      | NoScrollbar | NoScrollWithMouse
```

The **size is never capped** against anything, and `SetNextWindowPos` is what sets
ImGui's `window_pos_set_by_api` — which is precisely the flag that suppresses
ImGui's own clamp of a window into its viewport (`Begin` only calls
`ClampWindowPos` when the position was *not* set by the API). So the popup is
re-centred every frame and overflows symmetrically off all four edges, with
`NoResize`, `NoMove` and `NoScrollWithMouse` closing the three ways back.

The arithmetic, for the settings popup at its declared 780×500 logical against
the default 1280×720 window:

```
Theme.Scale = HostWindow.DpiScale * ConfigManager.View.UiScale
780 * Scale > 1280  =>  Scale > 1.64      horizontally
500 * Scale >  720  =>  Scale > 1.44      vertically      <- binds first
```

`UiScale` alone ranges 0.5–3 (`ViewConfig.cs`), so the slider reaches it on any
monitor. On the reporter's it is reached at a `UiScale` of **1**, because
`QueryDpiScale` returns 2.0 there — measured, `[Host] display scale: 2x` — for a
display KDE runs at 1.15, which is the defect the section above is about. The
compensating `UiScale=0.6999999` in that `interface.ini` puts `Theme.Scale` at
1.40: the value was found by hand and sits just under the threshold, which is why
the interface was cramped rather than gone.

**Clamping the size to the viewport is what makes this impossible rather than
unlikely.** An oversized scale should cost scrolling, not cost the controls:

```csharp
var room = viewport.WorkSize - style.WindowPadding * 2f;
var size = Vector2.Min(Size * Theme.Scale, room);
if (Size.Y <= 0f) size.Y = 0f;                 // auto-height: ImGui picks it
```

The `##body` child scrolls by default — the `NoScrollbar` flags belong to the
popup window, not to it — so a fixed-height popup that had to be cut short stays
whole for free. An **auto-height** popup (`Size.Y == 0`, which is `Popup`'s
default and what the disc picker, the mods list and the notices use) needs the
ceiling stated twice: once on the window and once as a max size constraint on the
child, because an `AutoResizeY` child sizes itself to its content and would
otherwise grow straight past the clamped window and be clipped silently. A
constraint is how ImGui caps an auto-resizing child, and at the cap it scrolls.

The **escape hatch was also half-built**. `Debug ▸ Reset view` puts a default
`ViewConfig` back, but nothing reads the scale or the colours *out of* the config
after startup: the scale lives on in `io.FontGlobalScale` and in the sizes
`ScaleAllSizes` baked into the style, and the accent and background live on in
`Theme`'s own fields. Popups take `Theme.Scale` live and shrink the same frame, so
the reset landed half-done — small windows, giant text — and the one thing a
person reaches for that item to undo was the one thing left behind. It now
re-applies both (`Theme.Apply` sets absolute sizes before `ScaleAllSizes`, so
calling it again does not compound). That item is reachable at any scale: the menu
bar is anchored at the top-left corner and is the last thing to leave the window.

The third way out is the port's, `patches/UiScale.cs`:

```
KF2_UISCALE=1     force the interface scale for this run, and save it
```

It **writes** the value as well as applying it, so one run repairs a settings file
that is already past the point of being editable in the interface, rather than
having to be kept around forever. It applies on `RuntimeReadyEvent` rather than in
`Configure`: `ConfigManager.Load` runs inside `HostWindow.Initialize`, after
`Program.cs`, so anything set earlier is read straight back off disk. By that
event the ImGui context exists and the window loop has not started, and both are
on the same thread — the game and the interface share one here. Measured:

```
$ KF2_UISCALE=1.0 dotnet run …
[Host] display scale: 2x
[KF2] ui scale: 0.85x -> 1x (saved)
```

### The clamp is not the last word on a window's size

The clamp above holds until the scale is changed a few times, and then a popup
grows off the bottom of the screen — reported as "make the UI scale smaller and
then larger". The clamp is not what fails; it is overridden.

`Theme.Apply()` is called again on every accent, background and UI-scale change,
and ends in `style.ScaleAllSizes(Scale)`, which multiplies **every** size field in
the style. The block above it resets only some of them — `WindowPadding`,
`FramePadding`, `ItemSpacing`, `IndentSpacing`, `ScrollbarSize`, `GrabMinSize`,
the roundings. Everything else it does not name is multiplied again from the value
the last call left, and compounds. Measured, replaying `Apply` at 1.4, 2, 1, 6, 2
against ImGui.NET 1.90 (Silk 2.22.0):

```
default   WindowMinSize=<32, 32>   WindowPadding=<8, 8>
Apply(1.4)  WindowMinSize=<44, 44>   WindowPadding=<16, 14>   SafeArea=<4, 4>
Apply(  2)  WindowMinSize=<88, 88>   WindowPadding=<24, 20>   SafeArea=<8, 8>
Apply(  1)  WindowMinSize=<88, 88>   WindowPadding=<12, 10>   SafeArea=<8, 8>
Apply(  6)  WindowMinSize=<528,528>  WindowPadding=<72, 60>   SafeArea=<48,48>
Apply(  2)  WindowMinSize=<1056,…>   WindowPadding=<24, 20>   SafeArea=<96,96>
```

`WindowPadding` is right at every step, which is why nothing about the *spacing*
ever looked wrong. `WindowMinSize` is not, and it is the one that matters: ImGui
floors every window that is neither a child nor `AlwaysAutoResize` at it, in
`CalcWindowSizeAfterConstraint`, and does it **after** applying a size constraint:

```cpp
if (g.NextWindowData.HasFlags & ImGuiNextWindowDataFlags_HasSizeConstraint) { ...clamp... }
// Minimum size
if (!(flags & (ImGuiWindowFlags_ChildWindow | ImGuiWindowFlags_AlwaysAutoResize)))
    new_size = ImMax(new_size, g.Style.WindowMinSize);
```

So a compounded minimum beats both `SetNextWindowSize` and
`SetNextWindowSizeConstraints` — the two things `0019` clamps a popup with.
Measured on a 1280×720 viewport, asking for the clamped 1264×704:

```
WindowMinSize=  32  -> window 1264x704      with max-constraint 1264x704
WindowMinSize=  88  -> window 1264x704      with max-constraint 1264x704
WindowMinSize= 528  -> window 1264x704      with max-constraint 1264x704
WindowMinSize=1056  -> window 1264x1056     with max-constraint 1264x1056
```

Vertically only, because 1056 is past 720 but not past 1280 — which is exactly the
shape of the report. Each scale change is one more multiplication, so it takes a
few before the minimum passes the viewport height; going *down* and back up is
simply the quickest way to make several.

The fix is `0020`: snapshot the style ImGui built, once, before the first
`ScaleAllSizes`, and restore it at the top of `Apply` before re-theming. Listing
the fields `Apply` forgets would work only until upstream adds one to
`ScaleAllSizes`. Verified idempotent — replaying 1.4, 2, 1, 6, 2, 6, 6 gives
`WindowMinSize` 44, 64, 32, 192, 64, 192, 192: a function of the scale alone, with
the themed colours surviving the restore.

This was always latent — `Apply` has never been idempotent, and changing the
*accent* compounds it just as well as changing the scale. It only became visible
when the popups stopped being unbounded, since a window that already overflowed
had nothing left to reveal.

### What is measured and what is not

The geometry: the 1.44 threshold, the scale the runtime reports, the compounding,
that a compounded `WindowMinSize` beats the clamp, that the restore is idempotent,
and that the clamp and all three escape hatches fire. **How the clamped popup
reads at a large scale has not been checked by eye**, and neither has the
underlying question of what the chrome *should* look like on this monitor, which
is the unwritten fix in the section above.

## Two general shapes worth keeping

`0007`, `0008` and `patches/EndingHold.cs` are the pattern to keep in mind:
**anything the runtime refreshes only at `VSync` is invisible to a game that stops
calling `VSync`**, and that failure mode is always silent.

`0004`, `0005` and `0006` are the other one: **a handler that never runs loses
only the work inside it**, so the symptom is never an error — it is a picture that
stays black, a job queue that never advances, or a callback table that reads as an
ordinary game variable until the game happens to store something in it.

## Upstream contribution policy

RecompOne's maintainer rejects AI-authored pull requests outright ("AI PRs will
be rejected, no exceptions"). That governs contributions to RecompOne itself; the
tool is MIT and using it here is unaffected. It does mean any fix to the
*recompiler* should go upstream as an issue rather than a PR, unless you write
the patch yourself.
