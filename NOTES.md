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

`libetc` (VSync), `libcd`, `libgpu`, `libcdstream` and libapi's `DMACallback`
are mapped to the runtime's HLE — 63 patches. A steady-state second of
`KF2_LOG=sdk` looks like this, and is what "working" should look like:

```
[SDK] StFreeRing
[SDK] DrawSync(0)
[SDK] PutDrawEnv env=0x800A8044 clip=(0,240)-320x240 ofs=(0,240) isbg=0
[SDK] PutDispEnv env=0x800A80A0 disp=(0,0)-320x240
[SDK] DrawOTag   ot=0x800A80B4
```

— one frame off the ring, one buffer flip, one ordering table, repeating.

Not yet done: the movie has no sound (XA audio is routed but unverified), only
the first area has actually been played, and `END.EXE` has never run.

## Layout

```
config/kf2.json          recompiler config (schema: RecompOne.Recompiler/Config/ConfigLoader.cs)
config/funcmaps/         generated function maps (address/name/size)
patches/                 hand-written C# replacing recompiled functions
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

They are declared as overlays like any other. `base` is the address of the byte
at `offset + skip`, so it is the module base plus its pointer table, and
`ResolveOverlay` derives the LBA as the archive's LBA plus the entry's start
sector. **That LBA is what arms the swap**: the CD read of the module's first
sector marks the overlay pending (`Dispatcher.LoadByLba`) and the write that
lands it in RAM activates it (`NotifyWrite`) — which only works because the HLE
`CdRead`/`CdGetSector` call `LoadByLba` per sector and write through `PSMemory`.

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

Still open: the archive has 70 entries and only these nine are code — the group
pattern breaks down after entry 23, so more module families may be hiding in the
later entries, and `END.EXE`'s equivalents have not been looked for at all.

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

## Next steps

1. **Cross an area boundary.** Only `fdat02` has run. The swap from one area
   module to the next is the untested half of the overlay mechanism, and it is
   where a wrong `base` or a missed entry point in the other eight maps will
   show up — as `unmapped call`, or as a module running against the wrong map.
2. **Look for the module families the group-of-three pattern misses.** It holds
   for `FDAT.T` entries 0–23 and breaks down after; entry 32 is already a second
   family at its own base. The rest of the archive has not been classified, and
   `END.EXE` has not been checked for the same trick at all.
3. Check the movie's audio. XA sectors are routed to `XaRouter` and the runtime
   has an XA thread, but nothing has been verified by ear.
4. Correct linear-sweep damage as it surfaces. Coverage is only ~56% of `open`
   and ~86% of `game`, so expect more gaps; `scripts/add_call_targets.py`
   re-derives starts from the code and can be re-run at any time.
5. Work out the rest of the `CD/COM/*.T` archive formats when asset work starts.
   [IvanDSM/KingsFieldRE](https://github.com/IvanDSM/KingsFieldRE) has KFModTool
   and format notes covering this game across its regional variants (no symbols,
   `.map` or ELF, so it does not help the function maps).

## Upstream contribution policy

RecompOne's maintainer rejects AI-authored pull requests outright ("AI PRs will
be rejected, no exceptions"). That governs contributions to RecompOne itself; the
tool is MIT and using it here is unaffected. It does mean any fix to the
*recompiler* should go upstream as an issue rather than a PR, unless you write
the patch yourself.
