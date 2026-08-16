# King's Field II — RecompOne port

Static recompilation of **King's Field** (NTSC-U, `SLUS-00158`) using
[RecompOne](https://github.com/BlackLabelHQ/RecompOne) (MIT).

## Which game this is

The series was renumbered for the West, so the name is ambiguous:

| Chronological | Japan | North America |
|---|---|---|
| 1st (1994) | King's Field, SLPS-00017 | *not released* |
| 2nd (1995) | King's Field II, SLPS-00069 | **King's Field, SLUS-00158** |
| 3rd (1996) | King's Field III, SLPS-00377 | King's Field II, SLUS-00255 |

This project targets the **second game**. The disc in use is the North American
release, which is the English localization of the Japanese *King's Field II* —
same game, so the "KFII" project name is accurate even though the disc says
`SLUS-00158`. If you ever swap in the US-boxed "King's Field II" (`SLUS-00255`),
that is a *different game* and every address and function map here is wrong for it.

## Status

**The game is playable.** `OPEN.EXE` streams the intro movies off the disc
through the MDEC, the title screen loads and runs, the boot stub swaps in
`GAME.EXE`, the main game gets through its data load and memory-card check, and
the first area comes up and can be walked around in.

Reaching that took three CD paths that a static recompilation breaks in three
different ways — see "The three ways a CD read can hang" — and it turned up the
fact that **`GAME.EXE` is not the whole game**: per-area logic is MIPS code
loaded off the disc at run time (see "GAME.EXE loads code"). The area modules
are confirmed by play, not just by static analysis: walking around is `fdat02`
executing.

The in-game menu opens. It used to hang the port dead, and the two bugs behind
that are worth reading before touching anything interrupt- or input-related:
the runtime was **guessing the address of PSY-Q's interrupt-callback table** and
eventually called a game data word as a function, and **host input was only
polled from `VSync`**, so the menu's wait-for-button-release loop — which draws
nothing and never vsyncs — could never see the release.

`libetc` (VSync), `libcd`, `libgpu`, `libcdstream` and libapi's `DMACallback`
are mapped to the runtime's HLE — 66 patches. A steady-state second of
`KF2_LOG=sdk` looks like this, and is what "working" should look like:

```
[SDK] StFreeRing
[SDK] DrawSync(0)
[SDK] PutDrawEnv env=0x800A8044 clip=(0,240)-320x240 ofs=(0,240) isbg=0
[SDK] PutDispEnv env=0x800A80A0 disp=(0,0)-320x240
[SDK] DrawOTag   ot=0x800A80B4
```

— one frame off the ring, one buffer flip, one ordering table, repeating.

Two areas have been played, half an hour in one sitting, across an area-module
swap and a memory-card save, and the audio sounds right. **A save has now been
loaded back** — slot 2, from the title screen, correct area and correct character
state (see "Saving and loading"). **The frame rate is pinned to 30 fps**, NTSC's
fastest band — the port used to burst past it (see "Frame pacing"). Not yet done:
`END.EXE` has never run — see "Next steps".

## Layout

```
config/kf2.json          recompiler config (schema: RecompOne.Recompiler/Config/ConfigLoader.cs)
config/funcmaps/         generated function maps (address/name/size)
patches/                 hand-written C# replacing recompiled functions
mods/<id>/               runtime-loaded mods (mod.json + C#), toggled in-game
scripts/inspect_disc.py  SYSTEM.CNF + ISO9660 listing from a .cue/.bin
scripts/extract_file.py  extract a disc file and dump its PS-X EXE header
scripts/match_overlays.py  carry a function identified in one overlay to the other two
disc/                    your own dump (gitignored)
generated/               recompiler output (gitignored, derived from the disc)
tools/RecompOne/         upstream tool checkout (gitignored)
Program.cs               hand-owned entry point
KingsField2Recomp.csproj
```

## Prerequisites

- .NET SDK 10 — `sudo dnf install dotnet-sdk-10.0`
- `chdman` for CHD conversion — `sudo dnf install mame-tools`

The runtime pulls Silk.NET, SDL, OpenGL and OpenAL via NuGet, all cross-platform
with no Windows-only P/Invoke. Verified working on Fedora 43 / Wayland / Mesa.

## Disc structure

Converted from CHD with:

```bash
chdman extractcd -i "King's Field (USA).chd" -o KingsField2.cue -ob KingsField2.bin
```

Result is a single MODE2/2352 track. `python3 scripts/inspect_disc.py` gives the
full listing; the part that matters:

| File | Size | Role |
|---|---|---|
| `SLUS_001.58` | 4 KiB | boot stub named by SYSTEM.CNF |
| `OPEN.EXE` | 186 KiB | title / intro |
| `GAME.EXE` | 378 KiB | the main game |
| `END.EXE` | 166 KiB | ending |
| `CD/COM/*.T` | ~40 MiB | data archives (models, textures, audio, text) |
| `OP/*.S` | ~180 MiB | streamed media |

**The boot executable is a 2 KiB loader stub, not the game.** RecompOne
auto-discovers it from SYSTEM.CNF, so left alone it would recompile 2 KiB and
nothing else. The three real executables **all load at `0x80011000`** and are
mutually exclusive — only one is resident at a time. They are therefore declared
as overlays, which is exactly what the overlay system is for.

Header values come from `scripts/extract_file.py <cue> GAME.EXE --header-only`;
`-base` must be the header's text address rather than an assumed `0x80010000`.

`CD/COM/*.T` and `OP/*.S` are data and streamed media, not code, so they stay out
of the overlay list and are read by the runtime through the CD interface.

## Two traps worth knowing

**1. Number bases are inconsistent between the JSON config and the CLI.**

| | `base` | `size` / `skip` / `offset` | `lba` |
|---|---|---|---|
| `config/kf2.json` | hex **string** `"0x80011000"` | decimal JSON numbers | decimal |
| `--generate-function-file` | hex | **hex** | decimal |

So the same value is `"skip": 2048` in the config but `-skip 800` on the command
line. `inspect_disc.py` prints both decimal and hex columns for this reason.

**2. Overlay files are read as raw bytes.** `ResolveOverlay` does *not* parse the
PS-X EXE header — it slices the file from `offset + skip`. Every overlay pointing
at a `.EXE` therefore needs `"skip": 2048` to step over the 0x800-byte header.
(The *boot* executable is different: it goes through `Psx/Parser.cs`, which
strips the header itself.)

## Workflow

### 1. Set up the tools

```bash
bash scripts/setup_tools.sh
```

Clones RecompOne, applies everything in `patches/recompone/`, and builds the
recompiler. Idempotent, so it is also the way to re-apply local fixes after
pulling upstream.

**`patches/recompone/0001-bios-load-return-1.patch` is required to boot.** The
runtime's BIOS `Load` (A(42h)) returned the header pointer, but the real BIOS
returns 1 on success. King's Field's boot stub compares the result against 1
exactly and retries forever otherwise, so unpatched it spins in the loader
(~12,900 `Load` calls in 30 seconds) and never reaches `Exec`.

**`patches/recompone/0004-libapi-dma-callbacks.patch` is required for the intro
movie.** It adds `RecompOne.Runtime.Sdk.LibApi` so DMA-completion callbacks are
delivered at all; see "DMA callbacks" below for why nothing works without it.

**`patches/recompone/0005-libcd-interrupt-driven-reads.patch` is required to get
past the title screen.** It gives `LibCd` a polled read path and makes it deliver
CD-ROM kernel events, and gives `LibEtc.VSync` the vblank root-counter event. See
"The three ways a CD read can hang" for what each one unblocks.

**`patches/recompone/0006-irq-callback-table.patch` and
`0007-pad-poll-outside-frame-loop.patch` are required to open the menu.** The
first lets a game point the runtime at PSY-Q's real interrupt-callback table
instead of deriving one that lands in game data; the second lets `PAD_dr` poll
the host, so a game waiting on the pad without vsyncing is not waiting forever.
See "The interrupt-callback table cannot be guessed" and "The menu deadlock".

The other two are diagnostics and safe to skip: `0002-cdtrace-diagnostic.patch`
names the function behind a CD register access (`KF2_CDTRACE=1`), and
`0003-libgpu-sdk-trace.patch` gives `LibGpu` the `Log.Sdk` tracing the other SDK
libraries already had, plus a `Log.Gpu` line for every GP1 write. GP1 is
display/control only — a handful of writes per mode change — so tracing all of it
is cheap, and it is the only way to see whether the game ever enabled the display.

### 2. Generate function maps

There is no King's Field decompilation producing an ELF + `.map`, so the
`-elf`/`-map` path is unavailable and we use a linear sweep. Upstream is blunt
about the tradeoff — a sweep "can and WILL get some data as code"
(`ConfigLoader.cs`) — so expect to correct these by hand as bad functions surface.

```bash
RC="dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build --"

$RC --generate-function-file -linear-sweep -disc disc/KingsField2.cue \
    -file SLUS_001.58 -base 80010000 -skip 800 -out config/funcmaps/main.json
$RC --generate-function-file -linear-sweep -disc disc/KingsField2.cue \
    -file OPEN.EXE     -base 80011000 -skip 800 -out config/funcmaps/open.json
$RC --generate-function-file -linear-sweep -disc disc/KingsField2.cue \
    -file GAME.EXE     -base 80011000 -skip 800 -out config/funcmaps/game.json
$RC --generate-function-file -linear-sweep -disc disc/KingsField2.cue \
    -file END.EXE      -base 80011000 -skip 800 -out config/funcmaps/end.json
```

Current yield: main 9, open 515, game 1103, end 471. The sweep also resolves jump
tables (1937 entries in `game` alone). Every function map entry requires a `size`
or the loader throws (`FunctionMapLoader.cs`).

### 3. Recompile

```bash
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build -- \
  config/kf2.json
```

2099 functions into `generated/` (~163k lines of C#).

### 4. Build and run

```bash
dotnet build KingsField2Recomp.csproj -c Release
dotnet run --project KingsField2Recomp.csproj -- disc/KingsField2.cue
```

The runtime needs the cue at play time as well as at recompile time; pass it as
the first argument or the runtime will prompt for a disc.

**csproj gotcha:** the RecompOne checkout is nested inside this project
directory, so the SDK's default item globs would compile RecompOne's own sources
and its `obj/` AssemblyInfo files (CS0579 duplicate-attribute errors). Hence the
`<Compile Remove="tools/**" />`. Conversely, `generated/` and `patches/` need *no*
explicit include — adding one causes NETSDK1022.

## Fixing bad output

When a function misbehaves, turn on `debug`, `addressComments` and
`disasmComments` in the config to annotate the generated C# with source addresses
and disassembly. They inflate the output a lot, so keep them off otherwise.

Two escape hatches, both per-function and both overlay-aware:

- `stubs[]` — replace a function with a no-op. Good for hardware pokes and
  copy-protection paths with no native meaning.
- `patches[]` — point a function at hand-written C#. Takes `function` or
  `address`, a `target`, and `mode` (default `replace`). The `overlay` field
  accepts a single name, a list, or `"*"`. Since `open`/`game`/`end` overlap in
  address space, prefer naming the overlay explicitly over `"*"`.

Hand-written replacements live in `patches/`.

## VSync: found and mapped

| Overlay | `VSync` |
|---|---|
| `open` | `0x8001EB88` |
| `game` | `0x8005FCC8` |
| `end`  | `0x8001B154` |

Found by fingerprinting the function's *contract* rather than its name, since a
linear sweep provides no names. `VSync(mode)` returns a counter when `mode < 0`,
returns early when `mode == 1`, and otherwise waits — which shows up in the
generated C# as a register copied from `A0`, sign-tested, then compared against 1.
Exactly one function per overlay matches, each 79 lines with identical structure:
the same statically linked routine, linked at a different address in each
executable.

**Confirmed, not guessed:** the helper immediately after it counts down and, on
expiry, calls the `printf` thunk with the string at `0x800117B4`, which reads
`VSync: timeout` — PSY-Q's own timeout message.

Mapped via `patches[]` in `config/kf2.json`; the recompiler reports `applied 3
patches` and emits the body as a forwarding call:

```csharp
public static void func_8001EB88(CpuContext c, IMemory m) => RecompOne.Runtime.Sdk.LibEtc.VSync(c, m);
```

Useful technique for the remaining SDK functions: `func_80014C0C` is the `printf`
thunk (BIOS A(3Fh), 69 call sites). Library routines pass diagnostic strings to
it, so following its arguments names the surrounding function for free — that is
how `VSync` was confirmed.

## The overlay delta: identify once, get all three

The three executables are three separate links of the *same* PSY-Q libraries, so
every library routine exists in all three at a different address. They are laid
out at a **constant offset per object file**:

| object | `game` − `open` | `end` − `open` |
|---|---|---|
| `libgpu` (both layers) | `+0x4A7A0` | `-0x22F8` |
| `libcd` (`cdinit`) | `+0x315B0` | `-0x3A34` |
| `libcd` (`cdio`, the thunk block) | `+0x2FDBC` | `-0x3A34` |
| `libcd` (`stream`) | `+0x30AB4` | `-0x3A34` |
| `libetc` (`vsync`) | `+0x41140` | — |

The granularity is the translation unit, not the library: `libcd`'s `cdio` and
`stream` modules are at different offsets from each other. So one identification
plus one subtraction gives the other two, but only for functions in the *same*
object — do not extrapolate a delta across a module boundary.

`scripts/match_overlays.py` does this mechanically. It matches on a
relocation-insensitive normal form — `j`/`jal` targets, `lui` immediates and the
16-bit displacement of loads, stores and `addiu` masked out, leaving opcodes and
register numbers. Absolute addresses are exactly what differs between the links,
so masking them is the point:

```bash
# where is open's DrawOTag in the other two?
python3 scripts/match_overlays.py disc/KingsField2.cue 0x80016078

# re-derive the whole libgpu map and emit the config patches
python3 scripts/match_overlays.py disc/KingsField2.cue --libgpu
```

Short functions (a 12-instruction table dispatch, say) match in several places;
the tool reports that rather than guessing, and settles them against the delta
the unambiguous rows agree on. Run against the already-known `libcd`/`libetc`
addresses it reproduces every one of them, which is what makes it trustworthy
for the ones that are not known yet.

## libgpu: found and mapped

libgpu is **two layers**, and only the outer one is worth patching.

The public API never touches hardware. It dispatches through a 15-slot driver
table (`0x8003D610` in `open`, reached via a pointer at `0x8003E790`) whose
entries are the routines that do: `_ctl` writes GP1 and shadows the value,
`_cwc` pushes words to GP0, `_dma` programs DMA channel 2 in linked-list mode,
`_otc` uses channel 6, `_load`/`_store` issue GP0 `0xA0`/`0xC0`. Patching a
public function replaces the whole path below it, and the runtime already
emulates every register the driver layer would have written.

This indirection is also why the library is invisible to the obvious searches:
**nothing in the image ever forms a GPU register address**. There are five
`lui …, 0x1F80` instructions in all of `OPEN.EXE` and none of them is the GPU.
The register addresses live in a data table (`0x8003E7A8`: GP0, GP1, D2_MADR,
D2_BCR, D2_CHCR, D6_MADR, D6_BCR, D6_CHCR, DPCR) that the driver layer loads
through. Finding that table by searching the *data* for the dword `0x1F801810`
is what unpicked the whole library, and the same trick found `libcd` earlier.

| function | `open` | `game` | `end` | HLE |
|---|---|---|---|---|
| `ResetGraph` | `0x80015A8C` | `0x8006022C` | `0x80013794` | |
| `SetGraphDebug` | `0x80015D28` | `0x800604C8` | `0x80013A30` | |
| `GetGraphDebug` | `0x80015D9C` | `0x8006053C` | `0x80013AA4` | |
| `SetDispMask` | `0x80015DC4` | `0x80060564` | `0x80013ACC` | |
| `DrawSync` | `0x80015E04` | `0x800605A4` | `0x80013B0C` | ✔ |
| `ClearImage` | `0x80015E34` | `0x800605D4` | `0x80013B3C` | |
| `LoadImage` | `0x80015E84` | `0x80060624` | `0x80013B8C` | |
| `StoreImage` | `0x80015EC0` | `0x80060660` | `0x80013BC8` | |
| `MoveImage` | `0x80015EFC` | `0x8006069C` | `0x80013C04` | |
| `ClearOTag` | `0x80015F68` | `0x80060708` | `0x80013C70` | |
| `ClearOTagR` | `0x80015FBC` | `0x8006075C` | `0x80013CC4` | |
| `DrawPrim` | `0x80015FF4` | `0x80060794` | `0x80013CFC` | |
| `DrawOTag` | `0x80016078` | `0x80060818` | `0x80013D80` | ✔ |
| `PutDrawEnv` | `0x800160D0` | `0x80060870` | `0x80013DD8` | ✔ |
| `GetDrawEnv` | `0x80016190` | `0x80060930` | `0x80013E98` | |
| `PutDispEnv` | `0x800161F0` | `0x80060990` | `0x80013EF8` | ✔ |

Only the four marked `HLE` are patched, because those are the only four
`RecompOne.Runtime.Sdk.LibGpu` implements. The rest run as recompiled MIPS and
work: their register writes are trapped by `PSMemory` and their DMA (channels 2
and 6, including OTC) is emulated.

How each of the four was pinned down:

- **`PutDispEnv`** computes `hStart = scrX*10 + 0x260` and clamps it to
  `[0x1F4, 0xCDA]` — the same two magic numbers as the runtime's own
  `LibGpu.PutDispEnv` (`Math.Clamp(hStart, 500, 3290)`). It finishes with
  `GP1(0x08)` and a 20-byte copy of the DISPENV to the current-env global.
- **`PutDrawEnv`** builds the `DR_ENV` packet at `env+0x1C`, which is precisely
  where that packet sits inside a 92-byte `DRAWENV`; it then sends the packet
  through the driver's `_dma` slot, copies `0x5C` bytes to the current draw env
  and returns its argument. Two independent struct offsets agreeing is what
  makes this one certain rather than plausible.
- **`DrawOTag`** calls the `_dma` slot — the routine that writes `D2_MADR`,
  `D2_BCR = 0` and `D2_CHCR = 0x01000401` — with the ordering table.
- **`DrawSync`** tail-calls the slot that polls `D2_CHCR` and GPUSTAT and, on
  expiry, prints `GPU timeout:QUE=(%2d,%2d),CODE=(%d,%d,%08X)`.

`SetGraphDebug` is confirmed the same way: it is the only caller of
`SetGraphDebug:level:%d,type:%d reverse:%d`, and `DrawPrim` passes the literal
string `"DrawPrim"` as the `%s` of `%s: bad prim:addr=%08X,type=%s,len=%d`.

`GetDrawEnv` and `GetDispEnv` have **zero** call sites in all three overlays, so
the current-env copies that the HLE versions skip are never read back.

Verification is in the log rather than in the reasoning: with the patches in
place `KF2_LOG=gpu,sdk` shows `PutDrawEnv`/`PutDispEnv` reporting a coherent
640×240 double buffer (garbage struct offsets would give nonsense), the
`[DMA] ch2` linked-list transfers disappear (the HLE `DrawOTag` writes GP0
directly instead of programming the DMA controller), and `GP1(03) 0x000000`
confirms the display is switched on.

## libcdstream: found and mapped

The intro and ending are STR movies streamed off the disc. KF2 drives them
through `CdRead2(mode | 0x100)`, which is `stream.c`'s own entry point: it sends
`CdlSetmode`, installs the stream module's DMA-3 and ready callbacks, and issues
`CdlReadS`. Six public functions matter.

| function | `open` | `game` | `end` | call sites in `open` |
|---|---|---|---|---|
| `StSetRing` | `0x8001C584` | `0x8004D038` | `0x80018B50` | 1 |
| `StClearRing` | `0x8001C5DC` | `0x8004D090` | `0x80018BA8` | 0 |
| `StUnSetRing` | `0x8001C62C` | `0x8004D0E0` | `0x80018BF8` | 0 |
| `StSetStream` | `0x8001C6CC` | `0x8004D180` | `0x80018C98` | 1 |
| `StFreeRing` | `0x8001C7A0` | `0x8004D254` | `0x80018D6C` | 2 |
| `StGetNext` | `0x8001C8A8` | `0x8004D35C` | `0x80018E74` | 1 |

The ring layout identifies them, and it is exactly the layout the runtime's
`LibCdStream` already assumed: `slots` 32-byte headers first, then `slots`
2016-byte data blocks. `StGetNext` computes its data pointer as
`ring + slots*32 + idx*2016` (in the disassembly, `idx*63 << 5`) and its header
pointer as `ring + idx*32`; `StFreeRing` divides back by `0x1F8` words to
recover the index, requires the slot's status word to be 4, and clears the `n`
headers whose count it reads from `header+6` — the STR frame header's sector
count. The call sites settle the argument shapes: `StSetRing(0x800927F0, 0x20)`
(a 64 KiB ring, 32 slots) and `StGetNext(&addr, &header)` polled until it
returns 0.

Mapping these was necessary but **not sufficient** — see the next section.

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

## GAME.EXE loads code

`GAME.EXE` is not the whole game. Per-area logic is **MIPS code linked to run at
`0x8019F07C` and loaded off the disc into RAM at run time**. The port died on it
with `unmapped call: 0x8019F1C8` the moment the first area came up.

`CD/COM/FDAT.T` is an archive: `u16` count, then a `u16` table of start sectors
in 2048-byte units. Entries run in groups of three per area — ~66 KB of data,
~28 KB of data, then a 4–8 KB code module — so the code lives at entries `3n+2`.
Each module is a table of function pointers (32 slots and up) followed by the
functions. `GAME.EXE` holds the current module at `0x8017E068` and dispatches
through it; the crash was slot 8, `(*mod)[8]`. With no module loaded the same
pointer holds `0x80064B64`, a static table of 32 identical pointers to a bare
`jr ra` — which is how the shape of the object was confirmed before any of the
loaded ones were read.

Two independent things pin the load address:

- The pointer table holds **absolute** addresses inside the module, so the file
  is linked for one fixed address. Searching the whole disc image for the crash
  address `0x8019F1C8` returns exactly one hit, inside `FDAT.T`, `0x20` bytes
  into entry 2 — the table slot the crash dispatched through.
- Scoring every candidate base by how many of a module's own `jal` targets land
  on a plausible function start (preceded by `jr ra` + delay slot, or opening
  `addiu sp,sp,-N`) peaks **unanimously** at `0x8019F07C` for all eight area
  modules — the address `GAME.EXE` itself writes into `0x8017E068` — and at
  `0x80193B38` for entry 32, which is a different module family.

| entry | file offset | size | base | LBA |
|---|---|---|---|---|
| 2 | `0x018000` | 4096 | `0x8019F07C` | 457 |
| 5 | `0x031000` | 8192 | `0x8019F07C` | 507 |
| 8 | `0x04B000` | 4096 | `0x8019F07C` | 559 |
| 11 | `0x064000` | 6144 | `0x8019F07C` | 609 |
| 14 | `0x07D000` | 8192 | `0x8019F07C` | 659 |
| 17 | `0x096800` | 6144 | `0x8019F07C` | 710 |
| 20 | `0x0B0000` | 6144 | `0x8019F07C` | 761 |
| 23 | `0x0C8800` | 8192 | `0x8019F07C` | 810 |
| 32 | `0x0E2000` | 6144 | `0x80193B38` | 861 |

Entry 32 is the odd one out and is **dead content** — a cut area the loader cannot
reach, since every code module this game loads goes to `0x8019F07C` and entry 32
is linked for `0x80193B38`. See "fdat32 is a cut area" below before spending any
time on it.

They are declared as overlays like any other. `base` is the address of the byte
at `offset + skip`, and `ResolveOverlay` derives the LBA as the archive's LBA
plus the entry's start sector. **That LBA is what arms the swap**: the CD read of
the module's first sector marks the overlay pending (`Dispatcher.LoadByLba`) and
the write that lands it in RAM activates it (`NotifyWrite`) — which only works
because the HLE `CdRead`/`CdGetSector` call `LoadByLba` per sector and write
through `PSMemory`.

Every module now takes `skip: 0` and its own load address as `base`, so the
overlay covers the header table as well as the code. The reason is in "The sweep
splits a switch" below: the words past the header's 32 dispatch slots are the
modules' **switch jump tables**, and the recompiler reads a jump table out of the
overlay's own bytes. Skipping the header hid them. Nothing disassembles the
header — only addresses named in the function map are emitted — so covering it
costs nothing.

Function maps are a linear sweep of each module with the pointer-table targets
and every internal `jal` merged in. Do not skip the merge: the sweep alone
missed real entry points in **five of the nine** modules, and each of those is a
future `unmapped call`. `scripts/add_call_targets.py`'s `merge()` does the
splicing, and re-running the sweep is:

```bash
$RC --generate-function-file -linear-sweep -disc disc/KingsField2.cue \
    -file "CD/COM/FDAT.T" -base 8019F0FC -offset 18000 -skip 80 -size F80 \
    -out config/funcmaps/fdat02.json
```

**Nine is all of them, and the disc has no other code.** Every one of `FDAT.T`'s
70 entries was tested for the shape that identifies a module — a table of
absolute pointers into itself — and exactly the nine above match, at 16 words out
of 16. The group pattern does not break down after entry 23 as previously
suspected; entries 24–29 and 33–46 are simply **empty** (zero length), and 30/31/32
is one more group of the same 66 KB / 28 KB / 6 KB shape. From 47 on the entries
are large asset blobs with no pointer table.

The same test run over every other file on the disc — `RTIM.T`, `RTMD.T`, `MO.T`,
`VAB.T`, `ITEM.T`, `TALK.T`, `FN.D`, `OP.D`, `OPU.D` — finds **zero** module
tables at any sector boundary. `FDAT.T` is the only file that carries code.

The other static source of `unmapped call` is closed too. Scanning all three
executables for words that point into their own text at a plausible function
start (preceded by `jr ra` + delay slot, or opening `addiu sp,sp,-N`) and are not
already a known start returns **0 in all three** — so every indirect dispatch
target that exists statically is mapped, on top of every `jal` target. What can
still surface is an address computed at run time, which nothing static can
predict — and, as the next section covers, an address that is not a function
start at all.

### The sweep splits a switch, and the split crashes

A room off the main hall died with `unmapped call: 0x8019F578`, six frames deep
in `fdat17`. The address is not missing from the map: it is *inside*
`func_8019F564`, 20 bytes past its start. Nothing calls it. `func_8019F374`
**branches** to it — `beq s0, a1, 0x8019F578` — and the recompiler turns a branch
that leaves its own function into a `Dispatcher.Call`, which only knows entry
points.

The real function is `0x8019F374`–`0x8019F5A4`: prologue `addiu sp,sp,-48`,
epilogue `jr ra` + `addiu sp,sp,48`, and in between a 14-case switch whose case
bodies and shared epilogue the sweep chopped into twelve "functions". **The sweep
ends a function at any `jr` or `j` plus its delay slot.** That is right for
`jr ra` and for a tail call, and wrong for the two forms PSY-Q emits *inside* a
function — a `jr` through a jump table, and a `j` to a shared epilogue — so every
switch in the game is a candidate.

The proof that a split is wrong is cheap and total: **a MIPS conditional branch
never leaves the function that issues it.** There is no conditional tail call and
the range is only ±128 KB. So a conditional branch crossing a boundary in the map
means the map is wrong, and the two functions plus everything laid out between
them are one function. `scripts/merge_branch_spans.py` applies exactly that rule,
and jump-table entries as a second source of the same proof:

```bash
python3 scripts/merge_branch_spans.py --dry-run    # every overlay
python3 scripts/merge_branch_spans.py fdat17
```

It found the bug in **five of the nine modules** — `fdat05`, `fdat11`, `fdat14`,
`fdat17`, `fdat20`, `fdat32` — i.e. four more areas were carrying the identical
crash. The three executables are clean, which is why this never showed up before
the area modules went in. Merging drops fdat17 from 29 functions to 13.

Two things have to hold for a merge to be safe, and the script checks both:

- Nothing may `jal` a start the merge swallowed. A label is not callable, so that
  would trade one unmapped call for another. (Nothing does; every swallowed start
  is a switch case or an epilogue.)
- The `jr` has to resolve, or the switch dispatches to nothing at run time.
  `JumpTableAnalyzer` reads the table out of the overlay's bytes and only accepts
  entries inside the function — which is why **the merge and the `skip: 0` change
  above are one fix, not two.** The tables live in the module header, past the 32
  dispatch slots, so with the header skipped the recompiler could not read a
  single one.

Finding the table needs the same dataflow the recompiler does, not a peephole:
`fdat14` hoists its `lui`/`addiu` pair to the top of the function, **33
instructions** above the `jr` that uses it.

After the fix every switch in every module resolves — `fdat17` alone goes from
zero resolved tables to two, 19 entries — and the only `Dispatcher.Call` left on
a register in any module is the unreachable `default:` arm of a resolved switch.

`fdat32` was the exception, and it turned out not to be a bug at all. It is
covered in "fdat32 is a cut area" below.

## fdat32 is a cut area, and nothing can load it

Scoring each module's *external* `jal` targets the way the load address was
originally pinned gives 17/17, 19/19, 22/22, 38/38 … for the eight area modules
against `GAME.EXE` — and **0/15 for `fdat32` against all three executables**. Its
fifteen outbound calls land mid-function on words like `bltz v0`, `sw s1,0x6C(sp)`
and `lw v0,72(sp)`. No constant delta fixes them either: the best offset over
±32 KB puts 4 of 15 on a function start, which is what chance gives.

The base is not the problem. `fdat32`'s **internal** calls are 4/4 on a prologue,
its header pointer table holds `0x80193BCC`–`0x80194954`, and `0x80193BD4`
disassembles as a clean function. `0x80193B38` is right. The module is real,
correctly based code whose calls into the host resolve to nothing.

**The loader settles it.** `CD/COM/FDAT.T` is registered as archive 5 by
`func_800185A0` at `0x80015E7C`, and the area loader at `0x8001689C` reads a
group of three with the area index taken from the byte at `0x8017E06C`:

| entry | destination | site |
|---|---|---|
| `area*3` | `0x801B6FA8` | `0x80016968` |
| `area*3 + 1` | `0x801B6FA8` | `0x80016A88` |
| `area*3 + 2` (the code module) | **`0x8019F07C`** | `0x800169C0` |

The code module's destination is a **literal argument**, built by
`lui a2,0x801A` / `addiu a2,a2,-3972` right before the call. So every code module
this game can load, loads at `0x8019F07C` — and `fdat32` is linked for
`0x80193B38`. Reading entry 32 through this path would drop it 46 KB from where
its own pointer table says it is, and `(*mod)[8]` would dispatch to `0x80194624`
with nothing there. It would break on hardware exactly as it breaks here.

And nothing names it. Scanning all three executables for `lui`/`addiu`/`ori`
pairs and for raw data words:

| address | OPEN.EXE | GAME.EXE | END.EXE |
|---|---|---|---|
| `0x8019F07C` (live module base) | 0 | **2 code refs** | 0 |
| `0x80193B38` (fdat32 base) | 0 | **0** | 0 |
| `0x80193BCC` (fdat32 first function) | 0 | **0** | 0 |

`OPEN.EXE` and `END.EXE` do not contain the string `COM\FDAT.T` at all, so
`GAME.EXE` is the only thing that ever reads the archive.

So `fdat32` is **leftover content**: an area whose data (entries 30 and 31, 66 KB
and 28 KB, the normal group shape) and code were left on the disc after the host
it was linked against moved on. The two empty groups before it — entries 24–29,
zero length — are two more areas cut without leaving anything behind. `fdat32`
only survives because a cut area's files were not stripped.

Consequences for the port, all of them "nothing to do":

- **The fifteen mid-function calls can never fire.** Nothing reads LBA 861 and
  nothing writes `0x80193B38`, so the overlay is never armed and never activated.
- Entry 31 ends at LBA 860, one sector short of 861, so no neighbouring read
  overruns into it.
- The overlay stays declared. It is correctly based, it costs 13 dead functions,
  and it is the evidence for this entry — removing it would only make the next
  person redo the work.

Worth remembering as a general point: **a module's internal `jal` targets confirm
its base, and its external ones confirm its host.** `fdat32` passes the first and
fails the second, and only the second test distinguishes live content from
abandoned content.

## Saving and loading

Both halves of the memory card now work. Saving was confirmed first — three files
written to `carda.sav` across a 30-minute session — and **loading one back has now
been confirmed too**: slot 2, entered from the title screen, came up in the right
area with the right character state, and the process ran on past it without an
`unmapped call`, a VRAM collision or an exception.

Loading is worth calling out separately because it is *not* the same path as
saving. It reaches an area module from the **title screen** rather than from an
area already running, so it drives the module load described in "GAME.EXE loads
code" from a cold start rather than as a swap over a resident module.

The card image is a stock PS1 memory card and can be read without the game
running, which makes it the cheap way to check what is on it:

```
$ python3 - <<'EOF'
d = open('carda.sav','rb').read()            # 131072 bytes, block 0 = directory
for i in range(1, 16):                        # 15 dir entries, 128 bytes each
    e = d[i*128:(i+1)*128]
    if int.from_bytes(e[0:4],'little') == 0xA0: continue      # free
    print(i, e[10:30].split(b'\x00')[0].decode())             # filename
for blk in (1, 3, 5):                         # each save's first block
    print(d[blk*8192+4:blk*8192+68].split(b'\x00')[0].decode('shift_jis'))
EOF
BASLUS-001581 / BASLUS-001582 / BASLUS-001583
ＫＩＮＧ’Ｓ　ＦＩＥＬＤ　２−１　ＥＸＰ　　　３１３　ＬＶ　５
ＫＩＮＧ’Ｓ　ＦＩＥＬＤ　２−２　ＥＸＰ　　　２６１　ＬＶ　４
ＫＩＮＧ’Ｓ　ＦＩＥＬＤ　２−３　ＥＸＰ　　　２９１　ＬＶ　５
```

Three saves, each two blocks (16 KB), `state=0x51` first-block-in-use, `0x53`
continuation — all well-formed. The trailing `2−N` in the title is the **slot
number, not the area**; the level and EXP are the only per-save state visible from
outside. Titles are full-width Shift-JIS even in the NTSC-U release, which is what
a straight localization of the Japanese KFII would look like.

**`carda.sav`'s mtime is useless as evidence.** It is rewritten at process start,
not only when the game writes a save, so a fresh timestamp means the port booted
— nothing more. Check the directory entries or the titles instead.

### The card code is all in GAME.EXE, and loading is one call

`OPEN.EXE` has **no card code at all** — no `BASLUS`, no `bu00:`, no `cdrom:`
string anywhere in it. Everything works off the filename template `BASLUS-00158`
at `0x80067564` and the title template at `0x80067574`, both in `GAME.EXE`:

| function | role |
|---|---|
| `func_80023638(slot)` | **load** |
| `func_80023764(...)` | save — builds the `SC` header, the title, the filename |
| `func_80023CC0(hdr, slot)` | stamps the slot, EXP and level into the title |
| `func_8001B4F4` | the load-slot menu; `func_8001B35C` is the start menu above it |
| `func_80023DD0(buf)` | the checksum |
| `func_8004A040(buf)` | unpacks a loaded block into game state |

`func_80023638` is short enough to state whole: build
`bu00:BASLUS-00158<slot+'0'>`, `open` it (retrying up to three times), read
`0x4000` bytes into `*(u32*)0x8006E98C`, checksum `buf+0x400` and compare against
`buf+0x200`, unpack with `func_8004A040`, record the slot, return **0 / 1 (no
file) / 2 (bad checksum)**. On a non-zero return the unpack never ran, so a
failed load leaves game state untouched.

**`0x8006E5D4` (u8) is "the current save slot".** Both the load and the save
write it, and it is zero in the executable image, so zero means neither has run.
Slots are 1–3; the `2−N` in a save's title is this number.

### The game can load a save without leaving the area

Worth knowing before building anything that reloads: `func_80029CBC` handles the
in-game menu's result, and its `-3` arm — "the menu loaded a save" — is twelve
instructions at `0x80029E0C`:

```c
func_800240B8();                    /* post-load fixup: music, equipment, and
                                       func_80023FCC to re-zero the deltas    */
area = *(u8*)0x8017E060;            /* the loaded save's area, in buf0        */
func_80024154(area, area, area, area, /*sp+0x10*/ area, /*sp+0x14*/ 0xFF);
func_80025D38();
```

So a complete reload from anywhere is `func_80023638(slot)` followed by that,
with **no overlay swap and no title screen**. That is what the `autoreload` mod
does; see "Auto reload".

`func_80029CBC` itself is dispatched only from the state machine's arms for
states 1 and 2, so it stops being called the moment the player dies — anything
that wants to run at that point has to hook stage 3 instead.

### The boot stub's handoff, and the other way back to the title

Found while looking for a return-to-title path, and worth having written down
even though the mod does not use it.

`SLUS_001.58` picks its next executable from `*(u32*)0x80010268`, an index into
the filename table at `0x80010254` — **0 = `OPEN.EXE`, 1 = `GAME.EXE`,
2 = `END.EXE`**. After each `Exec` returns it refills that index from
`*(u8*)0x800102F0` (reached through the pointer at `0x8001026C`).

`GAME.EXE` is the only executable that writes it, from the tail of the main loop
at `0x800139CC`–`0x800139EC`, off an **exit-reason global at `0x80199574`**:

| `*(u32*)0x80199574` | effect |
|---|---|
| `0` | keep looping — this is the normal state |
| `1` | `*(u8*)0x800102F0 = 2` → `END.EXE` |
| `9` | `*(u8*)0x800102F0 = 0` → `OPEN.EXE`, and `*(u8*)0x800102F8 = 1` |
| anything else | no write; the index stays `1`, so `GAME.EXE` re-execs |

`*(u8*)0x800102F8` is read by `OPEN.EXE` at `0x80011BCC` and **skips the three
intro movies** when it is 1 — the only thing `OPEN.EXE` ever reads from the stub.
The in-game menu's "quit" is what writes 9, via `func_80018E80` returning −2.

## The SDK naming problem

`SdkPatches.cs` routes PSY-Q library calls to the runtime's HLE implementations
by matching **exact function names** — `VSync`, `DrawOTag`, `DrawSync`,
`PutDrawEnv`, `PutDispEnv`, `CdInit`, `CdRead`, `PadInitDirect` and so on. A
linear sweep names everything `func_800xxxxx`, so nothing matches and the
recompiler always reports `applied 0 reimplementations`. Every SDK function has
to be mapped **by address** in `patches[]` instead, which is upstream's own
advice for this case: "you need to map each SDK address to its runtime
counterpart yourself, using `patches` with a `replace` that targets the runtime
function."

```json
{ "overlay": "open", "address": "0x80016078",
  "target": "RecompOne.Runtime.Sdk.LibGpu.DrawOTag", "mode": "replace" }
```

Done so far, across all three overlays — 63 patches: `libetc` (VSync), `libcd`
(CdInit, CdControl/F/B, CdSync, CdRead, CdReady, CdReadSync, CdGetSector), all
four HLE'd `libgpu` entry points, six of `libcdstream`, and libapi's
`DMACallback`. Still unmapped: `libpad` — and it may never be needed, since the
game reads the pad through the BIOS (`B(16) PAD_dr` in the trace, not
`PadInitDirect`) — and `libcdstream`'s `StSetMask`/`StGetBackloc` (never called
by this game, so not identified either).

What worked for identifying them, roughly in order of payoff:

- **The overlay delta.** Identify once, get the other two for free — see above.
  This is worth doing first because it makes every other technique 3× cheaper.
- **Data-side search for hardware addresses.** PSY-Q reaches I/O through pointer
  tables in `.data`, not through literals in code — there are only five
  `lui …, 0x1F80` instructions in all of `OPEN.EXE`. Searching the *data* for a
  register address (`0x1F801810` for the GPU, `0x1F801800` for the CD) finds the
  table, and the functions that load through it are the library.
- **Diagnostic strings.** `func_80014C0C` is the `printf` thunk (BIOS A(3Fh), 69
  call sites) and the PSY-Q libraries are full of `$Id:` tags and error text —
  `VSync: timeout`, `CdInit: Init failed`, `GPU timeout:QUE=…`,
  `SetGraphDebug:level:%d,…`. Following an argument names the caller for free.
- **Struct offsets as evidence.** A function that writes a packet at `env+0x1C`
  and copies `0x5C` bytes is handling a `DRAWENV`; one that clamps to
  `[0x1F4, 0xCDA]` is doing PSY-Q's horizontal-range arithmetic. Two independent
  offsets agreeing is what turns a guess into an identification.
- **Indirect-call tracing.** PSY-Q reaches whole modules through driver tables
  filled in at init, so a public entry point can have *zero* `jal` references in
  the image. `libgpu`'s 15-slot table and libapi's `DMACallback` are both like
  this. When a function you expect to be called has no callers, log the address
  in `Dispatcher.Call` and watch for it instead of searching statically.
- **PSY-Q signature matching** against the official SDK objects would identify
  everything wholesale (`ghidra_psx_ldr` and FLIRT-style matchers do this) and is
  the only approach that really scales — still worth setting up if the remaining
  libraries fight back.
- **The managed stack of the running process.** A recompiled function keeps its
  address in its name, so a stack trace of the hung game names the MIPS routine
  it is spinning in — no logging, no rebuild, no reproduction in a debugger:

  ```bash
  dotnet tool install -g dotnet-stack        # once
  ~/.dotnet/tools/dotnet-stack report -p $(pgrep -f net10.0/KingsField2)
  ```

  This is the fastest tool in the box for a silent hang. It is what identified
  the menu deadlock below in one shot, after a static hunt had gone nowhere:
  `func_80022EFC → func_8005F564 → func_8005FE64 → BiosB.PadRead` is the whole
  diagnosis, read off the top four frames. Note the process must be started from
  the same shell environment you run `dotnet-stack` in, or the diagnostic socket
  in `TMPDIR` will not be found.

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

## Mods

**RecompOne has a modding system; use it.** `RecompOne.Runtime/Modding/` is 839
lines of `ModLoader`, `HookManager`, `SymbolRegistry`, hook attributes and a
`ModsPopup` UI, and the generated `Entry.cs` already calls `ModLoader.LoadAll()`.
It was missed on the first pass here and a parallel one was built by hand; that
was wasted work and two wrong beliefs, both recorded below so they are not
re-derived.

A mod is a folder (or zip) under `mods/` with a `mod.json` and C# sources,
**compiled at run time by Roslyn** and hooked by address:

```
mods/framestats/mod.json + FrameStats.cs     fps and the vblank-per-frame histogram
mods/loopprobe/mod.json  + LoopProbe.cs      per-frame writes, attributed to loop stages
mods/widescreen/mod.json + Widescreen.cs     wider picture, and the census that justifies it
mods/nodither/mod.json   + NoDither.cs       clears the GPU dither bit; see "Dithering"
mods/analog/mod.json     + Analog.cs,
                           AnalogProbe.cs    analog twin-stick control; see "Analog twin-stick control"
mods/autoreload/mod.json + AutoReload.cs     reload the last save on death; see "Auto reload"
```

```csharp
[PostHook("game", Address = 0x80060818)]
static void AfterDrawOTag(CpuContext c, IMemory m) { ... }

[PreHook("game", Address = 0x80037C0C)]        // return false to skip the original
static bool BeforeStage(CpuContext c, IMemory m) => ...;
```

`HookManager` detours the emitted method through MonoMod, so **a hook site costs
no config entry and no recompile** — `SymbolRegistry` resolves `overlay + address`
(or `overlay + function name`) against the dispatcher's tables, which are all
registered up front in `Entry.Run`. `[Replace]` also exists, and can take a
leading `orig` parameter to call through to the original.

Mods are toggled in the game's own Mods panel, with reload, and `IMod.DrawSettings`
puts an ImGui panel under each one. State persists to `interface.ini` as
`mods.<id>.enabled`.

### What belongs in a mod, and what does not

`patches/FramePacing.cs` is deliberately **not** a mod. It is a correctness fix
that has to be on: as a loaded package it could be absent, disabled, or fail to
compile, and the failure mode would just be the game running too fast. It still
attaches through `HookManager` — the same runtime detour, no config entry — just
from `Program.cs` with its own `ModInfo`, so it cannot be turned off by accident.
Measurement tools, which are genuinely optional and expensive, are real mods.

Config `patches[]` entries are still the right tool for **`replace`**, which is
how the whole SDK is bound: those are needed before any mod could load, and there
are 63 of them.

### Four things that will bite

1. **`ModCompiler` does not enable implicit usings.** Every mod file must name
   `System`, `System.Collections.Generic`, `System.Linq` itself. This is the real
   cost of runtime compilation: it is a *runtime* error, printed as
   `[Mods] <id>: ... error CS0103` and then the mod silently does not load.
2. **`mods/**` must be removed from the csproj `Compile` glob**, exactly like
   `tools/**`. Otherwise the SDK compiles every mod into the main assembly *as
   well as* Roslyn compiling it at run time, giving two copies that can drift.
3. **Mods default to disabled** (`IsEnabled` falls back to `false`), and
   `LoadAll` returns silently when nothing is enabled — no log line at all. A mod
   that appears to do nothing is probably just off.
4. **`mods/.cache/`** holds the compiled assemblies and is written into the repo;
   it is gitignored.

## The main game loop, stage by stage

`GAME.EXE`'s per-frame loop is the tail of `func_8001369C` — everything before it
in that function is area setup, starting with nine `memset`s that are the game's
own declaration of where its per-area state lives. The loop itself is a **flat
list of thirteen calls with a backward branch at `0x80013918`, and the renderer
is last**:

| # | stage | instrs | subtree | reaches | words/entry | into |
|---|---|---|---|---|---|---|
| 1 | `func_8002C944` | 76 | 1 | — | 0.0 | — |
| 2 | `func_80037C0C` | 1917 | 224 | VSync DrawOTag CdControl | 4.0 | buf5 (5 words) |
| 3 | `func_8002A550` | 1113 | 452 | VSync DrawOTag CdControl | 0.4 | buf2 buf3 |
| 4 | `func_80040348` | 119 | 118 | — | 12–16 | buf6 buf7 |
| 5 | `func_80046A60` | 63 | 110 | — | 0–7 | buf7 |
| 6 | `func_8004910C` | 12 | 1 | — | 0.0 | — |
| 7 | `func_8001689C` | 429 | 77 | VSync DrawSync CdControl | 0.0 | — (66 while loading) |
| 8 | `func_80025A1C` | 30 | 1 | — | 0.0 | — |
| 9 | `func_800140AC` | 43 | 2 | — | 3.0 | buf8 (5 words) |
| 10 | `func_8002CA74` | 32 | 2 | — | 0.0 | — (81 while loading) |
| 11 | `func_80016FC8` | 159 | 13 | DrawSync CdControl | 0.0 | — |
| 12 | `func_80014534` | 79 | 35 | CdControl | 0.0 | — |
| 13 | `func_800342D8` | 253 | 159 | VSync DrawOTag … | 310–470 | buf1 |

**Take the write counts from a steady window, not the first one.** Stages 7 and
10 look like the busiest state writers in the window where the area loads (66 and
81 words a frame) and write *nothing at all* once it has — that is loading work,
and reading it as per-frame behaviour is the easy mistake here.

"subtree" is how many distinct functions the stage can reach, "reaches" is which
mapped SDK entry points are in that subtree — both from the emitted C#, so they
are static facts, not guesses. The renderer is confirmed dynamically as well: a
managed stack taken mid-frame reads
`func_8001369C → func_800342D8 → func_8002E0FC → func_80060818 (DrawOTag)`.

The nine buffers, straight out of the `memset` arguments:

```
buf0 0x8017E05C 0x0007    buf3 0x801B3084 0x0E46    buf6 0x8016C544 0x24F3
buf1 0x8017E084 0x5F3C    buf4 0x801C8484 0x4611    buf7 0x8019C5EC 0x0AA3
buf2 0x80199414 0x0058    buf5 0x80175914 0x21D1    buf8 0x80198574 0x03A7
```

The write counts come from the `loopprobe` mod, which snapshots those 66 KB at
every stage boundary and attributes each changed word to the stage that ran
before it. What a steady in-area window says:

- **buf1 is the display list**, and stage 13 is the only thing that fills it —
  310 to 470 words a frame, varying with what is on screen, which is also how you
  can tell the camera is moving while the demo plays.
- **Almost nothing else moves.** Outside the display list the entire per-frame
  churn across all nine buffers is about **twenty words**: five in buf5 from stage
  2, five in buf8 from stage 9, a dozen in buf6/buf7 from stages 4 and 5. Those
  are counters, not state.
- **So the player's position is not in these buffers.** The demo is visibly
  walking — the display list changes every frame — and the game's own per-area
  clears do not cover whatever holds the camera. Two places to look next: the
  gaps between the nine buffers (`0x8017E068`, the area-module pointer, falls in
  one), and the area module's own data at `0x8019F07C`.
- **Stage 2 is the biggest function in the loop by far** — 1,917 instructions,
  224 functions reachable, `VSync` and `DrawOTag` in its subtree — and writes four
  words. Whatever it does, it does somewhere else.

The probe is not free — 220k memory reads a frame drops the port to ~26 fps and
shifts the band histogram — so it is a diagnostic to run deliberately, not to
leave on.

## Player state: found, and it was in stage 3 all along

The probe above could not find the player because it was watching the wrong 66 KB
— the per-area buffers do not contain it. Reading the *emitted C#* found it in
an afternoon, and the route in was the pad: `func_8005F564` is libetc's
`PadRead(id)`, it has 17 call sites in `GAME.EXE`, and exactly one of them is a
main-loop stage. That stage is **3, `func_8002A550`**, and everything the player
does hangs off it. **Stage 2 was the wrong guess**; the four busy `buf5` words it
writes are not the view block.

The pad word is read once a frame and stored to **`0x80199554`**; every consumer
reads that global, and the `fdat` area modules never touch it, so all player
control is `GAME.EXE` code. The word is active *high* — libetc returns
`~*(u_long*)buf` — and its two button bytes are in the opposite order to the
runtime's `Controller` bit layout, so a mask decoded straight comes out as
Triangle/Cross/Square/Circle where the game means Up/Down/Left/Right.

Stage 3's own body, in order:

```
func_80023FCC        zero the three delta vectors, copy base angles to composed
PadRead(1)        -> 0x80199554
func_8002957C        player control: attacks, items, magic (reads pad + masks)
func_80028DB8        turn and look
func_800290D4        walk and strafe
func_80029EE0        base angles += delta vector A, then zero A
0x8002B330           composed = base + A + B + C
```

### The state, by address

| address | width | what |
|---|---|---|
| `0x801994EC` / `F0` / `F4` | u32 ×3 | position X / height Y / Z — the triple the collision queries (`func_8002C330`, `func_8002C700`) take with radius `0x320` |
| `0x8019950C` / `0E` / `10` | s16 ×3 | base view angles: pitch / **yaw** / roll |
| `0x8019951C` / `1E` / `20` | s16 ×3 | delta vector A — zeroed each frame, folded into the base by `func_80029EE0` |
| `0x80199514` / `16` / `18` | s16 ×3 | delta vector B |
| `0x80199524` / `26` / `28` | s16 ×3 | delta vector C |
| `0x80199504` / `06` / `08` | s16 ×3 | composed view = base + A + B + C, what the renderer reads |
| `0x8019953E` / `0x80199540` | s16 | strafe velocity / forward velocity |
| `0x80199542` | s16 | resulting walk magnitude, `sqrt(vx² + vz²)` via `func_8005B890` |
| `0x80199544` / `0x80199546` | s16 | turn velocity / pitch velocity |
| `0x80199554` / `0x80199556` | u16 | this frame's pad word / its companion for edge detection |
| `0x80199558` / `0x8019955C` | u32 | this frame's walk speed (`0xC8`) / turn rate (`0x1C`, `0x23` standing still) |

Yaw is not a guess: `func_800290D4` turns the two velocities into a heading by
calling `func_80028080` with `yaw`, `(yaw + 0x800) & 0xFFF`, or `yaw ∓ 0x400` —
180° for backwards and 90° either side for strafing, which only makes sense for a
facing angle. A debug warp at `0x8002AFBC` writes the same three words as
`0, 0x0C00, 0` alongside a literal position, which confirms the triple's order.

**You turn faster standing still than walking** — 35 units a frame against 28 —
and that is the game's own rule, set in stage 3 from whether a movement button is
down. Worth knowing before blaming a mod for it.

### The action-mask table, and why nothing should hardcode a pad bit

The control code never tests a pad bit directly. It tests `pad & mask[i]` against
a table of 24 words at **`0x8006E568`–`0x8006E5D0`**, which is what makes the
game's own control-config screen work. Dumped at run time (the `analog` mod does
this) it reads:

| mask word | button | action |
|---|---|---|
| `0x8006E59C` / `0x8006E598` | Left / Right | turn: yaw += / −= `rate>>2` |
| `0x8006E590` / `0x8006E594` | Up / Down | walk forward / back |
| `0x8006E580` / `0x8006E588` | R1 / L1 | strafe right / left |
| `0x8006E584` / `0x8006E58C` | R2 / L2 | pitch += / −= 3 (R2 looks **down**) |

So **yaw increases when you turn left** and **pitch increases when you look
down**, which is the sort of sign a mod gets backwards until it reads this table.
The table settles the buttons but not which way the view tips: that half of the
pitch sign came from playing it, after the first build had the look axis
inverted.

### Every control axis has the same three branches

Turn, pitch, forward and strafe are all velocity based and all written the same
way:

```c
if      (pad & maskInc)  vel += rate >> 2;   /* clamped to +rate */
else if (pad & maskDec)  vel -= rate >> 2;   /* clamped to -rate */
else                     vel decays toward 0;
angle_or_position += vel;
```

(pitch steps by a flat 3 to a limit of 32, and the angle itself is held inside
±`0x2BC`; forward decays at `speed>>3` and everything else at `>>2`.)

That shape is the whole reason analog control is cheap here — see "Analog
twin-stick control".

### The character's stats are buf2, and the memory card is what found them

The position-and-angles block above is the *camera*. HP, MP, EXP and level are
somewhere else entirely, and the route in was the **save title**. Every save's
64-byte Shift-JIS name carries the decimal EXP and level — `ＥＸＰ　３１３　ＬＶ　５`
— so the routine that stamps those digits has to read both from RAM.
`func_80023CC0` is it, and it reads `0x80199414` and `0x8019941C`. That address
is **`buf2`**, the 0x58-byte buffer already in the `memset` list above.

| address | width | what |
|---|---|---|
| `0x80199414` | u32 | EXP |
| `0x8019941C` | u8 | level |
| `0x80199426` / `0x80199428` | u16 | max HP / **current HP** |
| `0x8019942A` / `0x8019942C` | u16 | max MP / current MP |
| `0x801994E1` | u8 | player action state; `0x11` is **dead** |

Which pair is HP and which is MP is not a guess — three things agree:

1. **`func_80024F90(delta)` is "add `delta` to HP".** It reads `0x80199428`,
   and when the sum is `<= 0` it clamps to zero **and calls `func_8002A264(0)`**.
   It touches no other stat, and nothing else in the block has a death branch.
2. **State `0x11`'s handler opens by forcing `*(u16*)0x80199428 = 0`.**
3. **The HUD reads the four in bar order** — current HP, max HP, current MP, max
   MP — which is the order the two bars are drawn in.

Two more shapes in the block confirm the pairing generally: a full heal is
`cur = max` for both pairs written back to back, and a level-up writes the new
maxima and then refills from them.

`func_8002A264` is the death **latch**: it returns immediately if the state byte
is already `0x11`, otherwise sets it, plays sound `(0x0B, 0x6E)` and zeroes two
timers. `func_80024FE0`, the take-damage routine, tests the same byte on entry
and returns early — once you are dead you take no more damage, which is also why
the byte is a safe thing for a mod to key off.

The state byte drives a **jump table at `0x80011300`**, `0x80011300 + state*4`,
states `0x00`–`0x12`, dispatched from stage 3. `0x11` lands at `0x8002ADAC`.
`func_80029E5C` is its inverse: it clears the byte to 0 along with eleven timers,
which is the game's own "back to normal" reset.

## Frame pacing: the port is pinned to the fastest band

King's Field's game speed **is** its frame rate — everything advances a fixed
amount per loop iteration — and the loop always waits an integer number of
vblanks, so the achievable rates are quantised to 60/n. That quantisation is the
"banding" the game is known for: on NTSC hardware a frame costs 2, 3 or 4 vblanks
depending on scene load, so the game runs at 30, 20 or 15 fps and *plays* at
correspondingly different speeds. (PAL bands off 50 Hz instead — 25, 16.7, 12.5 —
which is where its ~17 fps ceiling comes from: more consistent, slower, and the
reason the PAL release feels different.)

Counting `VSync(0)` calls between consecutive `DrawOTag`s over a 30-minute
session, 49,570 rendered frames, is a direct measurement of which band each frame
landed in:

| vblanks charged | rate | frames | share |
|---|---|---|---|
| 1 | 60 fps | 6,960 | 14.0% |
| 2 | 30 fps | 42,460 | 85.7% |
| 3 | 20 fps | 30 | 0.1% |
| 4+ | ≤15 fps | ~120 | 0.2% |

**The port sits in the top band essentially always.** In an area it is 87% at
30 fps and 13% at 60 — the intro and title screens are ~99% at 60, because there
the loop only asks for one vblank. Nothing here is a throttle doing its job;
`DrawOTag` returns immediately on an HLE GPU and the MIPS is native code, so no
frame ever costs enough to fall into a slower band the way a real PlayStation
would under load.

So the earlier framing of "twice as fast as the console" was too blunt. Precisely:
**in light scenes the port matches hardware's best case exactly, in heavy scenes
it is up to 2× faster because it never bands down, and for one frame in eight it
runs at 60 fps — twice the NTSC ceiling, which is faster than the game can go on
any console.** That last group is the part that is unambiguously wrong.

That reading makes the work much smaller than it first looked, because the
reference speed is not some variable hardware average — it is **the top band,
30 fps**, which is both the design ceiling and where the port already spends 87%
of its frames.

**Step one is a floor, not a scale factor.** Enforce a minimum of two vblanks
(33.3 ms) per rendered frame and the port is a constant 30 fps: exactly NTSC's
fastest band, never above it, and without the banding that made the original's
speed wander. No game knowledge, no constants touched — and it is strictly more
consistent than hardware ever was. **Done**; see below.

### The floor, as built

`patches/FramePacing.cs`, attached as a post hook on `DrawOTag` in all three
overlays through `HookManager` at run time. Three things about the shape of it
are worth keeping:

**It is not a change to `FrameClock`.** The runtime's throttle paces per *`VSync`
call*, and from inside it a one-vblank frame is indistinguishable from the first
half of a two-vblank one — the information it needs (where the frame ends)
arrives at `DrawOTag`, which `FrameClock` never sees. The floor therefore keeps
its own deadline and lets the two clocks coexist: for a frame the floor extends,
`FrameClock`'s deadline simply falls behind real time and its wait collapses to
nothing (it resyncs on the `wait < -100` path every few frames), so the floor
ends up the sole pacer for exactly the frames that need one, and a no-op for the
frames that were already two vblanks long.

**It needed no RecompOne patch and no config entry.** `SymbolRegistry.Resolve`
turns `("game", 0x80060818)` into the emitted method and `HookManager.AddPost`
detours it, so the hook is installed at run time. It composes with the `replace`
that binds `LibGpu.DrawOTag`: `HookManager.Invoke` runs pres, then the
replacement, then posts.

The attach is deferred to the first `OverlayLoadedEvent`, because the dispatcher
tables `SymbolRegistry` reads are registered inside `Entry.Run` — after
`Program.cs` has run, but before anything is loaded, so the first load event is
the earliest moment every overlay resolves.

Note the namespace trap: generated code is `Recompiled.KingsField2`, a *class*
named after the project, which shadows any namespace called `KingsField2` — hence
`Kf2`.

**The vblank defines the frame boundary, not the `DrawOTag` call.** A second
ordering table with no `VSync` between it and the first belongs to the frame
already in flight; charging the floor per call would halve the rate of any screen
that draws more than one OT. King's Field draws exactly one (no zero-vblank gap
appears in any measurement), but the guard is free — the hook counts vblanks off
a `VSyncEvent` listener and returns early when the count is zero.

The deadline is absolute rather than `now + 33.3`, so a frame that overruns is
paid for out of the next one instead of the rate drifting down by the accumulated
jitter, with one frame of debt as the limit: past that the game has *stopped*
drawing (a disc read, a module swap) rather than run late, and the cadence
restarts instead of running flat out to catch up.

Two env vars, both read in `Program.cs`:

```bash
KF2_FPS=30          # 30 fps, the default; 60, or off for no floor
KF2_FPS_GATE=80040348+8002A550   # at 60, stages to run every other frame
```

The `framestats` mod is the measurement above, made cheap — it is the same count of
`VSync(0)`s between `DrawOTag`s that produced the band table, without the
gigabytes of `KF2_LOG=sdk`. Use it to check any pacing change; bands are per
report window, so consecutive lines separate the title screen from an area.

### What the floor actually changed — and a correction

Measuring with `KF2_FPS=off` makes the deviation look different
from the aggregate above, and more specific. In an area the port is **already at
exactly 30 fps, asking for two vblanks on every single frame**, for minutes at a
time — then it flips into a burst where it asks for one:

```
floor off                                        floor on
30.0 fps  450 frames  2:100.0%                   30.0 fps  300 frames  2:100.0%
28.3 fps  424 frames  1:8.5%  2:91.5%            30.0 fps  300 frames  2:100.0%
30.0 fps  450 frames  2:100.0%                   30.0 fps  301 frames  2:100.0%
39.1 fps  587 frames  1:83.8% 2:14.0%  ...       30.0 fps  301 frames  2:100.0%
51.6 fps  775 frames  1:87.7% 2:11.0%  ...       30.0 fps  300 frames  2:100.0%
30.0 fps  451 frames  2:100.0%                   30.0 fps  300 frames  2:100.0%
```

So the one-vblank frames are **not spread evenly through play** the way "14% of
frames" implies — they cluster, and while a cluster lasts the whole game runs at
about 1.7× speed for half a minute at a stretch. That is a worse bug than a
uniform overspeed and an easier one to feel: the world lurches, then settles.

The floor removes them. Over 3,200 frames of the floor-on run **no report window
came out above 30.0 fps**, and eight consecutive in-area windows were 30.0 fps at
`2:100.0%` exactly. Windows *below* 30 stay below it — the title screen and the
intro are CD-bound at 7–15 fps with the floor on or off, which is right: a floor
is a ceiling on speed, not a promise of one.

Two things the measurement says that are worth keeping in mind for step two:

- **The game asks for two vblanks on its own almost all the time.** Whatever
  decides that count is not measuring host time; it is the game's own pacing
  logic, and the port's job is only to stop it going faster than the top band.
- **The band histogram is unchanged by the floor** in steady state (`2:100%` in
  both columns), which is the evidence that the floor is not fighting the game's
  loop or the runtime's `FrameClock` — it just absorbs the slack.

**Step two, if 60 fps rendering is wanted**, is then a clean factor of two rather
than four: run at one vblank per frame, halve every per-tick movement delta, and
*double* the thresholds of per-tick counters (spell duration, torch burn,
i-frames). The asymmetry is the trap — dividing a counter's step instead of
multiplying its threshold makes it expire early. The cheaper variant, which gets
most of the feel in a first-person game, is to run the player's movement and view
at 60 with halved deltas and gate the world update to every other tick: smooth
camera, enemy timing untouched, and a 2:1 gate is far safer than 4:1.

Full decoupling — logic at 30, rendering interpolated at 60 — still needs
decomp-level knowledge of which state is positional, and is still not worth it.

### 60 fps: the mechanism exists, the map is half drawn

**There is room.** Sampling the game thread 200 times in an area puts 161 samples
in the pacing sleep, 23 in `Present`, and six in game code — so a frame is about a
millisecond of MIPS and thirty-two of waiting. Rendering twice as often costs
nothing this port has not already got.

**The gate works.** A `pre` hook that returns `false` skips the original —
`HookManager.Invoke` honours it — and hooks attach by address at run time, so any
subset of the loop can be gated with no recompile and no config entry:

```bash
KF2_FPS=60 KF2_FPS_GATE=80040348+8002A550
```

Nothing is hooked when no gate list is given, so the mechanism costs nothing
while it is unused.

**What is missing is which stages to name**, and the probe has narrowed it to a
hypothesis rather than settled it.

*Established.* Over ten consecutive windows in an area, exactly four words outside
the display list change on **every single frame**, and one stage writes all four:

```
8017783C (buf5)  changed 656x/656 frames  mean -0.18   now -13348   by 80037C0C
80177848 (buf5)  changed 656x             0xNN000000               by 80037C0C
801778F8 (buf5)  changed 656x             0xNN800001               by 80037C0C
8017793C (buf5)  changed 656x             0xNN800001               by 80037C0C
```

Three are packed `u16:u16` pairs whose high halves range `0x0000`–`0x0E80` across
samples; the fourth is a signed scalar that sits between −13,226 and −13,352 in
every window, jitters by tens, and has a mean signed change of about zero — it
does not drift over four minutes. Separately, stage 4 (`func_80040348`) writes a
*marching* set of buf6 addresses — `8016C654`, `8016CBC0`, `8016CFA4`, `8016D1F4`,
`8016CCA0` in successive windows — rather than a fixed set.

*Inferred, not confirmed.* A 0–4096 range is the PSY-Q angle scale, so the buf5
four look like a **view/camera block** and stage 2 like the stage that maintains
it; a set of addresses that walks through a 9 KB buffer looks like **iteration
over an entity table**, making stage 4 a world updater. That would give the split
the cheap variant needs: gate stage 4, leave stage 2 alone, and the camera runs at
60 while the world stays at 30.

Note this reverses the reading of the write-count table above, where stage 2 looks
inert because it writes four words. It writes four words and they are *the* four.

*The experiment that settles it* is scripted input, which has not successfully run
yet — the port exited before `KF2_AUTOPAD`'s first press each time it was tried:

```bash
KF2_LOOPPROBE=5 KF2_AUTOPAD=20:Up:5000,40:Right:5000
```

If `0x8017783C` steps monotonically under held Up, it is positional; if the three
packed words swing under held Right and not under Up, they are the view angles.
Either result names the stage, and the gate follows immediately. Until then
`fps=60` with no gate list runs everything at double speed — the mod says so at
startup rather than pretending otherwise.

**No clock exists in these buffers.** Every busy word has a mean signed change of
about zero, so there is no frame counter to watch, and the verification idea of
"gate a stage and see a counter's rate halve" needs a counter found somewhere else
first. The area module's data at `0x8019F07C` is the next place to point the probe.

The delta-halving is still owed regardless — a view stage running at 60 with
unhalved deltas moves twice as fast, and no amount of gating fixes that from outside.
That is the one part of 60 fps that needs a constant identified in the game's own
code rather than a stage gated from outside it.

## Widescreen: the runtime renders the margin, the game fills a quarter of it

**The runtime already implements widescreen; nothing in the game had to be
touched.** `GpuHle.WideMargin` sizes a margin of extra columns either side of the
display buffer, `GlCore` builds the display render target that wide
(`GlDisplayRt.Wide1x`), and the pieces that would otherwise fight it are already
handled: only the original columns are blitted back to VRAM (`Writeback` starts at
`rt.Margin`), the GPU clip is widened to the whole target when the game clips to
the whole framebuffer, `PutDrawEnv` extends an `isbg` background clear across the
margin, and `PresentDisplay` returns `WideAspect` instead of `SourceAspect`
whenever the margin is non-zero. So `mods/widescreen` sets one number:

```csharp
Display.WideAspect = 16f / 9f;      // 320 -> 428, 54 columns a side
Display.WideAspect = 21f / 9f;      // 120 columns a side
```

**This is not a stretch, and the difference is the whole point.** The projection
is untouched — no GTE control register is written, `H` and the rotation matrix are
the game's own — so pixels keep their aspect and the HUD keeps its authored size.
Which means the extra picture is not synthesised: it is only there if the game
submits geometry past the screen edge and expects the GPU to clip it. A game that
culls per polygon against its own screen rectangle would gain black bars and
nothing else.

So the mod counts. It listens on `RenderPrimEvent` and classifies every primitive
by whether a vertex falls outside the game's own clip rectangle
(`KF2_WIDESCREEN_PROBE=1`, or the checkbox in its settings panel):

```
[widescreen] 25.4% of 480 prims reach the margin (239/s)      title screen (open)
[widescreen] 0.0% of 600 prims reach the margin (300/s)       GAME.EXE menus
[widescreen] 25.4% of 58064 prims reach the margin (28787/s)  fdat02, in an area
[widescreen] 25.1% of 70500 prims reach the margin (35145/s)  fdat02, next window
```

**A quarter of everything King's Field draws in an area was being thrown away at
`x=0` and `x=319`** — about 1,000 primitives a frame, of which ~250 crossed the
edge. It culls per object and by depth and leaves the screen edge to the GPU,
which is exactly the property the margin needs. The 0.0% on the GAME.EXE menus is
the same measurement giving the other answer: those are 2D, authored 320 wide, and
no aspect ratio widens them.

**What has not been done is looking at it.** The measurement above ran with the
desktop session locked, so this is a primitive census, not a visual check. Two
things to check by eye first:

1. **2D screens and fades.** The margin is cleared only by the `isbg` path. A
   full-screen rectangle the game draws itself is 320 wide and will leave the
   sides showing the previous frame.
2. **Pop-in at the edges.** Per-object culling still uses the game's 4:3 frustum,
   so an object can be dropped while its polygons would have been visible in the
   margin. The counter cannot see this — it only counts what was submitted.

If either turns out to be bad enough to matter, the alternative is the classic
hor+ hack: scale the X row of the GTE rotation matrix by `source/target` so the
projection itself narrows, and present stretched. That needs the address of
whatever loads the matrix, costs correct HUD proportions, and — given a quarter of
the frame already crosses the edge — buys much less here than it does on a game
that clips its own polygons.

**Trap, learned while measuring:** with the session locked, KWin stops sending
frame callbacks and the port blocks in `SwapBuffers` forever with `VSync=True` —
0% CPU, no log output, and it looks exactly like a hang. Set `VSync=False` in
`interface.ini` to run it without a visible window. Note the runtime only presents
through the widened render target if that target was drawn into within the last
**4** presented frames (`GlCore.PresentDisplay`); with `VSync=False` the host
presents far faster than the game draws, so most frames fall back to the plain
VRAM texture at 4:3. That is an artefact of running headless rather than a bug in
the mod — but it is also what a real stall would look like on screen.

### The HUD does not widen with the world, and finding it is the problem

The world gets wider; the HUD is drawn in screen space, so it keeps the 4:3 box it
was authored in and sits visibly inset from the new edges. Moving it means telling
a HUD primitive from a world one, and `RenderPrimEvent` carries no such flag. Two
whole-frame dumps in `fdat02` (599 and 522 primitives, before and after turning on
the spot) locate it:

* It is **two fixed clusters** — x 5..91 for the HP/MP panel, x 269..310 for the
  equipment icons, both y 11..60 — and 42 of its 66 primitives are pixel-identical
  across the two frames; the rest are digits and an icon that animate in place.
* It is always in the **last ordering-table entries**, from about 65 from the end
  of a ~9,400-entry table. That is the front of the OT, where a painter's
  algorithm has to put whatever goes on top. A frame is a single `DrawOTag` call,
  the game alternates two tables (`0x801860A4`, `0x8018E0A4`) one per frame, and
  the HUD has no table of its own.

**The rule that looked right and was not: the palette.** In the first dump every
HUD primitive used a CLUT in VRAM **column 0** (rows 493-495) and no world
primitive did — world palettes sat at (8,492), (16,506), (20,496), (36,258..260).
Anchoring on "the first column-0 palette opens the HUD tail" worked in the
starting corridor and wrecked the frame elsewhere: further into the area, world
geometry uses column-0 palettes all the way back through the table, so the tail
opened early and half the world moved sideways. **The artefact named the cause —
the missing wedges of floor and ceiling were 54 pixels across, which is the
margin.** Holes exactly one margin wide mean geometry is being shifted that should
not be.

What replaced it is structural and positional: a primitive is HUD if it is in the
last 128 OT entries *and* its box falls in one of the two clusters. The OT gate is
why the mod replaces `DrawOTag` and walks the table itself — one pass to count the
entries, one to emit them, mirroring `LibGpu.DrawOTag` including its
custom-primitive branch. `HookManager.Invoke` runs pre-hooks, then the replacement,
then post-hooks, so the frame pacing's `DrawOTag` post-hook still runs. Being
wrong now costs one small triangle in a corner instead of the whole frame after it,
and the anchoring has its own toggle.

**Compile a mod without launching the game.** Roslyn compiles mods at load, so a
typo costs a whole boot to find. A throwaway csproj that compiles the mod source
against `bin/Release/net10.0/RecompOne.Runtime.dll` and `ImGui.NET.dll`, with
`ImplicitUsings` disabled to match `ModCompiler`, catches it in three seconds.

## Dithering: one flag, and it lives in the draw environment

The 4x4 crosshatch over every shaded surface is the GPU's ordered dither, and the
port reproduces it faithfully on both render paths — the software rasterizer adds
the table entry in `GpuRaster.Plot`, the hardware backend packs it into the vertex
texpage word and applies it in `quant5` in the fragment shader. Both read the same
single bit of GPU state, `Gpu._dither`, and `SetDrawMode` sets that from bit 9 of a
GP0(E1) word and from nowhere else.

So removing the dithering is not a rendering change at all. It is one question:
**can an E1 word with bit 9 set still reach the GPU?** There are three routes, and
they are worth separating because only two of them are hookable:

1. **`PutDrawEnv`**, from `DRAWENV.dtd` at `env+0x16` — `LibGpu.GetMode` turns that
   byte straight into bit 9. **This is the only route this game uses**: exactly one
   dithered draw env per frame, 30 a second at 30 fps, on the title screen and in
   `fdat02` alike.
2. **A `DR_MODE` or `DR_TPAGE` packet linked into the ordering table.** Measured
   over the same two, **zero** — the game sets the mode once a frame and never
   mid-frame. (`SetDrawTPage` would build one with the bit already clear;
   `SetDrawMode` is the one that could set it.)
3. **Recompiled MIPS writing GP0 through the trapped register**, from the parts of
   libgpu that are not mapped to the runtime's HLE. **No mod can hook this**, so it
   is checked instead of intercepted: `KF2_NODITHER_PROBE=1` samples GPUSTAT bit 9
   after every frame, and it read 0 for whole sessions of title screen and play.
   That register read is what closes the loop — without it, "the two hooks fire"
   would only be evidence about the two routes that were already known.

`mods/nodither` covers routes 1 and 2 and reports on all three. It **restores what
it clears** — the `dtd` byte and any E1 word are cleared in the pre-hook and put
back in the post-hook — so game memory is identical either side of the call and
nothing survives unloading the mod. That matters most for the packet buffer: some
of it is built once and re-sent for the rest of the run, so a bit cleared in place
would stay cleared long after the mod was switched off.

The ordering-table scan steps **command by command** using the GP0 command lengths
rather than searching for bytes that look like E1: a colour or a vertex word can
perfectly well carry 0xE1 in its top byte, and clearing bit 9 of a coordinate moves
geometry. It stops at the two commands whose length depends on data that follows (a
polyline, and an image load); neither occurs in this game's tables.

**It is a pre/post hook, not a replacement, and that is deliberate.**
`HookManager` allows exactly one `Replace` owner per function — a second mod's
replacement is refused with `replace conflict on …` — and the widescreen mod owns
`DrawOTag`. Pre- and post-hooks compose with a replacement and with each other, so
both mods load together (`loaded 2/2 mod(s), 6 function(s) hooked`) and the frame
pacing's own post-hook still runs.

### Getting pixels out without a screenshot

The window cannot be captured from a headless shell, but the frame buffer can:
`GpuHle.Backend.ReadVram` reads the back buffer straight out of the hardware
backend from a `DrawOTag` post-hook, and `Assets.PngWriter.WriteRgba` writes it
out. The rectangle to read is the clip rect from the last `PutDrawEnv` — which is
also how you can see the game alternating buffers, `(0,0)` and `(0,240)`.

**Getting a like-for-like pair is the hard part, and two obvious ways don't work.**
Consecutive frames of one run are different views — the attract demo is walking —
and it also leaves open which frame a given draw env applies to. The same frame
*number* in two runs is not the same view either: the demo drives itself into
`fdat02` every time but not on the same schedule, so frame 175 was a wall in one
run and a staircase in the next. Both comparisons still showed the effect
(12% of adjacent pixels one 5-bit step apart against 49%), but scene and setting
were confounded.

What works is drawing **one ordering table twice**: replace `DrawOTag`, write an
E1 built from GPUSTAT with bit 9 clear, call the original, dump; then write the
same E1 with bit 9 set, call the original again, dump. Identical geometry,
lighting and textures, one bit apart. The only caveat is that the second pass
draws over the first, so semi-transparent primitives blend twice; opaque geometry
simply overwrites.

That pair, in `fdat02`:

| | dither off | dither on |
| --- | --- | --- |
| adjacent pixels one 5-bit step apart | 11.7% | 41.6% |
| mean step between adjacent pixels | 0.264 | 0.608 |
| PNG of that frame | 19.7 KB | 30.2 KB |

Two fifths of all neighbouring pixels landing exactly one quantization step apart
is the dither pattern stated numerically, and the same picture compressing 53%
larger is the same fact seen by a compressor. Zoomed, the pair is unambiguous:
identical rock and identical lighting, one smooth and one carrying the crosshatch.
`mods/dithershot` was the throwaway that produced these; it is not kept.

**Seen once, not reproduced:** the very first run with the mod hung after both mods
reported their hooks and before `ModLoader.LoadAll` printed `loaded N/N` — i.e. in
`HookManager.Commit`, with the worker thread absent from `dotnet-stack` output
entirely. Five later runs of the same pair of mods committed instantly. Worth
recognising rather than chasing: if a build stalls at mod load, the stack will show
the main thread in `ModLoader.LoadAll → HostWindow.Pump`.

## Analog twin-stick control

`mods/analog` gives the game continuous analog turning, looking and walking on a
modern layout — left stick walks and strafes, right stick turns and looks — and
it does it **without replacing any of the game's own movement code**.

The sticks were always there: `InputManager` fills
`Controller.LeftX/LeftY/RightX/RightY` from SDL every poll. Nothing consumed
them, because the game reads `PAD_dr` and gets a digital word, and the runtime's
default binding just wires the left stick to the D-pad
(`GamepadBindings.Up = [11, 104]`) — which in this game means the left stick
*turns*, at the fixed rate, like the D-pad does.

**The trick is the three-branch velocity shape** documented under "Player state".
Because each axis is `vel += rate>>2` on a held button and then
`angle_or_position += vel`, a mod can pre-load the velocity word with
`target - accel` and assert the matching button in the game's pad global: the
game's own next instruction adds `accel`, lands exactly on `target`, and then
applies it through its own path. Collision, the pitch limit, the walk
normalisation, footsteps and animation all run untouched, on an amount the stick
chose. Nothing is replaced, nothing is `[Replace]`d — two `[PreHook]`s, on
`func_80028DB8` (turn/look) and `func_800290D4` (walk/strafe), plus one
`[PostHook]` on stage 3 for the probe.

The clamps are never fought: with `|target| ≤ rate` and the button asserted in
the direction of `target`'s sign, the pre-loaded value is always inside `±rate`
after the game's own accumulate, so the clamp branch is not taken.

Four things worth keeping:

* **The camera accelerates while the stick is held out.** A d-pad is down or it
  is not, so the game has no notion of a ramp; a stick held at the edge for half
  a second should be sweeping faster than one just pushed. The mod ramps a
  look-speed multiplier to 2.2× over half a second past 80% deflection and drops
  it three times faster, which — with the cap lifted above — is what took the
  camera from "stiff" to something like a modern shooter's. Fine aim near centre
  is untouched, because the ramp never starts there.
* **The fractional carry is not optional.** At 30 fps a small deflection rounds
  to a zero step every frame; without carrying the remainder the player simply
  does not move below about a third of stick.
* **Buttons come from the mask table, never hardcoded**, so the mod follows the
  game's own control-config screen — and gets the byte order right by
  construction, since it ORs the game's own mask words back into the game's own
  pad word.
* **The left stick leaks into turning** unless the turn masks are taken away from
  it, because the runtime binds the left stick to the D-pad and the D-pad *is*
  the turn control. Measured before the fix: 168 yaw steps in 300 frames with
  the right stick idle. The mod therefore owns the turn bits with a zero step
  whenever the left stick is deflected, and leaves them alone when both sticks
  are centred — so the D-pad still plays exactly as it did.
* **The per-frame speed limit only exists in the two button branches.** The
  branch that runs with *neither* button down decays the velocity by the same
  step and then applies whatever is left, unclamped — so writing `target + accel`
  and asserting nothing lands on `target` however large it is, while writing
  `target - accel` and asserting the button is capped at the game's own rate.
  That is the whole difference between a camera that tops out at the d-pad's
  74°/s and one that does not, and it costs one branch in `Drive`. Confirmed in
  play: mean yaw steps of 39 against a frame rate limit of 28, and a `turnVel` of
  37 sitting in memory, which no button can produce. Nothing else gameplay-side
  reads the turn masks — the only other readers are the control-config screen —
  so dropping the button for a frame is free.
* **A released velocity ramps down, and on a stick that reads as inertia.** The
  game drops nothing: pitch decays by 3 a frame from a limit of 32, so releasing
  the stick keeps the view moving for about eleven frames — a third of a second,
  some 16°. That is reasonable for a button, which cannot be released halfway,
  and wrong for a stick, and it showed up *only* on pitch because the leak fix
  above was already zeroing turn whenever the left stick moved. The mod therefore
  drives a released camera axis to zero for one frame and then hands it back, so
  L2/R2 and the D-pad still work. Movement is deliberately left alone: its
  ramp-down is the walking momentum the game has always had.
* **Sticks idle means mod idle.** Both hooks return before touching memory, which
  is what keeps D-pad and keyboard play identical to an unloaded mod.

`KF2_ANALOG_PROBE=1` reports the velocities, the yaw and pitch steps, the walk
speed and turn rate next to the stick deflection that produced them, and dumps
the mask table once. That dump is the evidence for the sign conventions in the
table above — with one gap it cannot close: it names the button behind an action
but not which way the view moves. That cost the first build an inverted look
axis, fixed by playing it; **increasing pitch looks down**. The "Invert look Y"
toggle is now a preference rather than a guess.

### Open: the camera does not feel consistent in every direction

Reported from play and **not yet diagnosed** — the camera's speed does not feel
even across directions, and not all the time. Nothing below is confirmed; these
are the candidates worth measuring first, and three of them are certain to be
*real* effects whether or not they are the one being felt:

1. **The game turns you slower while you walk.** Stage 3 sets the turn rate to
   `0x1C` (28) when a movement button is down and `0x23` (35) when none is, so
   horizontal speed drops by a fifth the moment you move. The mod inherits this
   because it reads the rate out of `0x8019955C` each frame. This is the best fit
   for "not all the time" and it is the game's own rule, so overriding it is a
   decision, not a fix.
2. **Vertical and horizontal are not the same scale, and the ratio moves.** Yaw
   steps by up to `rate` (28 or 35) and pitch by a fixed 32, so the vertical
   speed is constant while the horizontal one changes with what you are doing.
   Standing still the camera is relatively faster sideways than it is walking.
3. **Diagonals reach full deflection sooner than cardinals.** `AxisToByte` gains
   the axis by 1.3 and clamps *per axis*, so a stick pushed to a corner reads
   (1, 1) — radial magnitude 1.41, not 1. `Shape` renormalises the direction
   correctly, but the deadzone and curve are applied to a magnitude that saturates
   at a different point depending on the angle, which makes mid-deflection
   diagonals proportionally quicker than mid-deflection cardinals.
4. **The acceleration ramp is radial and applies to both axes.** Sweeping hard
   sideways speeds the vertical axis up too, so a diagonal flick during a sweep
   is faster than the same flick from rest.

The measurement to take is the mod's own: hold a fixed deflection at eight
directions in turn with `KF2_ANALOG_PROBE=1` and compare mean |yaw step| and the
pitch total, once standing still and once walking. Candidate 1 will show up as
two clean bands and would settle it immediately.

Measured with the mod loaded alongside `widescreen` and `nodither`:
`loaded 3/3 mod(s), 9 function(s) hooked`, no replace conflict, 300 frames per
10-second window — the pacing floor is untouched.

### Open: the twin-stick controls broke after the dragon stone went into the fountain

Reported from play and **not yet reproduced or diagnosed**. Putting the dragon
stone into the fountain — a scripted world event — and the sticks stopped
working afterwards. Unknown so far: whether it was both sticks or one, whether
the D-pad still worked, and whether it survived leaving the area or a reload.
Those three answers narrow it to one of the candidates below on their own, so
they are worth capturing next time before anything else.

What makes this worth writing down rather than guessing at: the mod has **two
silent bail-outs that a scripted event is exactly the thing to trigger**.

1. **The rate guards.** `BeforeLook` returns when `*(u32*)0x8019955C <= 0` and
   `BeforeMove` when `*(u32*)0x80199558 <= 0` (`Analog.cs:247,291`). They exist so
   the mod never divides a scale by a zero the game is holding, and the game
   plausibly zeroes exactly these while a scripted sequence has the camera. If
   the event leaves either at zero, that axis is dead until something sets it
   back — and the mod would be *reporting* the game's state faithfully rather
   than having a bug of its own. **This is the first thing to check** and it is
   one read each.
2. **The action-mask table.** `Drive` reads the button masks out of
   `0x8006E568`–`0x8006E5D0` every frame rather than hardcoding them, which is
   what makes the mod follow the control-config screen. If the event rewrites or
   clears that table, `inc`/`dec` come back zero, no button is asserted, and the
   game takes its neither-button branch — which *decays* the velocity instead of
   accumulating it. That reads as controls that are alive but weak and wrong,
   not as controls that are dead, so it is distinguishable from candidate 1 by
   feel alone.
3. **The player action state.** `0x801994E1` drives the jump table at
   `0x80011300`; states other than the ordinary ones take arms that need not call
   `func_80028DB8` and `func_800290D4` at all. If the event parks the player in
   such a state, the mod's hooks simply never run — and neither does the game's
   own control code, so the D-pad would be dead too. **That is the question the
   "did the D-pad still work?" answer settles**, and it is the one case where
   nothing is wrong with the mod.

Cheap next step: reproduce with `KF2_ANALOG_PROBE=1`, which already reports the
control state, and read `0x8019955C` / `0x80199558` / `0x801994E1` at the moment
it breaks. All three candidates are one memory read apart.

## Auto reload

`mods/autoreload` reloads the last save when the player dies. King's Field has no
retry; without it a death costs the menu, then the load screen, then the slot.

The mod is small because it adds **no loading path of its own**. Both halves were
already in the game and both are written up above: the death latch under "The
character's stats are buf2", and the in-game load sequence under "The game can
load a save without leaving the area". The mod is the wiring between them.

**One hook, `[PostHook("game", Address = 0x8002A550)]`** — the end of main-loop
stage 3. Not `func_80029CBC`, which is where the game does its own load from:
that is dispatched only from the state machine's arms for states 1 and 2, so it
stops being called the moment the state byte latches to `0x11`. Stage 3 itself
runs every frame, dead or alive, because the death sequence *is* one of its arms.

Per frame it reads one byte, `0x801994E1`. Not `0x11` and it clears its arming
and returns, so a live player pays a single read for the whole mod. On the
edge into `0x11` it additionally requires `HP == 0`, which is what separates a
death from any other reason that byte could hold `0x11`, then starts a clock. The
reload fires once, after a configurable delay (default 2 s) during which the
game's own death sequence plays.

### The game is already reloading, and it wins the race

The thing that nearly sank this, and the reason the delay is not just a sleep.
**Death is a timeline, not a state**, clocked by a `u16` frame counter at
`0x8019951A`. `func_8002A264` zeroes it, state `0x11`'s handler increments it,
and those three sites are its only uses in `GAME.EXE`:

| counter | what the handler does |
|---|---|
| 1–31 | the death animation |
| 32–64 | fade to black, amount `(n − 32) << 7` |
| **65** | `func_80024154(0, 0, 0, 0, 0, 0xFF)` |

That last call is **the same area-entry routine the mod calls, with area 0** —
the game's own respawn, and what "you died, start again" actually is. (The branch
above it, at `0x8002AFBC`, is the resurrection-item path: a literal position and
yaw `0x0C00` into area 1. It is the debug-looking warp already noted at that
address.)

65 frames is **2.17 s at 30 fps** — and the mod's default delay was 2.0 s. Five
frames of margin. The first run reloaded correctly and the second went back to
the beginning of the game, from identical code and an identical log line, which
is exactly what a race that tight looks like. At `KF2_FPS=60` the same 65 frames
is 1.08 s and the mod would always lose.

So the mod **holds the counter at 31** while it waits. The animation finishes,
the fade never starts, the respawn never comes due, and the delay becomes ours
rather than a bet against the game's clock. The reload logs the counter it fired
at for exactly this reason: `held at frame 31` is the assertion, and anything
near 65 would mean the hold had failed.

The reload is `func_80023638(slot)` and then `0x80029E0C`'s arm transcribed, with
a `c.SP -= 0x20` window for `func_80024154`'s fifth and sixth arguments — MIPS
passes those on the caller's frame at `sp+0x10` and `sp+0x14`. `c.Snapshot()` /
`c.Restore()` bracket the whole thing so the hooked function's caller sees the
registers it left.

Two details that are the mod's and not the game's:

- **`func_80029E5C` afterwards, but only if the state byte survived.** The game
  reaches that arm from a live state and so never has to clear the death latch;
  coming from `0x11`, something must.
- **The slot.** `0x8006E5D4` is the game's own record and is what "last used"
  means, but it is zero until a save or a load has run. Dying on a fresh New Game
  therefore has nothing to reload, and the mod logs once and leaves the death
  alone rather than inventing a slot. A fixed slot can be pinned in the panel.

`KF2_AUTORELOAD`, `KF2_AUTORELOAD_DELAY` and `KF2_AUTORELOAD_SLOT` mirror the
three settings, which persist to `interface.ini` under `kf2.autoreload.*`.

**Dying on demand is the hard part of testing this**, so the panel has a
*Simulate death* button: it zeroes HP and calls `func_8002A264(0)`, which is
exactly what the damage path does. It refuses when max HP is zero, since buf2 is
clear until an area is up.

**Status: working on the attract demo, which dies on its own and is therefore a
free test rig** — leave the port running and it will kill the character for you.
Three consecutive deaths in one session each reloaded slot 2 into area 1 at
`HP 46/86, LV 6`, `held at frame 31` every time, with no `unmapped call` and no
exception.

`fdat02` in the overlay log is the tell for the failure: the game's own respawn
loads it, so a run that reloads correctly never does. With the mod off it appears
right after the death, and with the mod on it does not appear at all.

Still wanted from a real session: a death in a *different* area from the save,
which is the case that makes `func_80024154` re-enter a `fdat` module other than
the resident one, and a save written mid-session to confirm `0x8006E5D4` tracks
saves as well as loads.

## Next steps

1. ~~**Cross an area boundary.**~~ ~~**Load a save back.**~~ Both done — a
   30-minute session loaded `fdat05` over `fdat02` with no `unmapped call`, no
   VRAM collision and no exception, so the swap mechanism and the derived base
   hold for a second module; three files were written to `carda.sav`; and slot 2
   has since been loaded back from the title screen into the right area with the
   right character state. See "Saving and loading". The memory card is closed as
   far as playing goes — what has *not* been exercised is creating a save on an
   empty card, deleting one, or a card the game considers corrupt.
2. ~~**Look for the module families the group-of-three pattern misses.**~~ Done —
   all 70 `FDAT.T` entries and every other file on the disc were tested for the
   module signature; the nine declared modules are the only code, and every
   static indirect-dispatch target is already a known function start. See
   "GAME.EXE loads code". **The statically findable `unmapped call` sources are
   exhausted**; what remains can only come from an address computed at run time.
3. ~~Check the audio.~~ Verified by ear during play — it sounds right. Note the
   two paths are independent: in-game music and effects come from the SPU, while
   the intro and ending movies are **XA** sectors routed to `XaRouter` on the
   runtime's own thread. Good SPU output says nothing about XA, so if the movies
   have not been listened to specifically, that half is still open.
4. ~~**Fix the frame pacing.**~~ Step one done — the `fps` mod holds every
   rendered frame to two vblanks, so the port is a constant 30 fps and no longer
   bursts past NTSC's top band. See "Frame pacing"; the measurement is the
   `framestats` mod.
5. ~~**Confirm the buf5 view block**, which is all 60 fps is now blocked on.~~
   Overtaken: the player state was found by reading the emitted C# instead, and
   it is not in `buf5` at all — see "Player state". The stage to gate is **3**
   (`func_8002A550`), which holds the pad read, the turn, the walk and the angle
   fold, and every per-tick delta it produces is now a named address. What 60 fps
   still needs is the delta halving, and that is now a small edit rather than a
   hunt: halve the turn rate at `0x8019955C` and the walk speed at `0x80199558`
   on alternate frames, or scale them the way `mods/analog` already scales the
   velocities they feed. The buf5 four are presumably the *rendered* camera and
   can be left alone.
6. Work out the rest of the `CD/COM/*.T` archive formats when asset work starts.
   [IvanDSM/KingsFieldRE](https://github.com/IvanDSM/KingsFieldRE) has KFModTool
   and format notes covering this game across its regional variants (no symbols,
   `.map` or ELF, so it does not help the function maps).
7. **Report the two runtime bugs upstream as issues** (not PRs — see below):
   input polled only from `PresentFrame`, which deadlocks any game that waits on
   the pad without vsyncing, and `Interrupts.Deliver` deriving a callback-table
   address from the `HookEntryInt` jmp_buf, which calls whatever the resulting
   game variable holds. Both are in `patches/recompone/0006` and `0007` with the
   reasoning; the second at minimum should refuse a handler that is not a known
   function.
8. Play further in. Now that the menu opens, the parts of the game it reaches —
   inventory, equipment, magic, the map — have never run, and each is a screen
   with its own code path.

## Upstream contribution policy

RecompOne's maintainer rejects AI-authored pull requests outright ("AI PRs will
be rejected, no exceptions"). That governs contributions to RecompOne itself; the
tool is MIT and using it here is unaffected. It does mean any fix to the
*recompiler* should go upstream as an issue rather than a PR, unless you write
the patch yourself.
