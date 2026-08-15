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

Boots into the game's own code and runs PSY-Q startup. Nothing renders yet, and
the cause is understood: **no SDK function is HLE'd**, because the recompiler
matches them by name and a linear sweep produces no names. See "The SDK naming
problem" below — that is the main remaining work.

Boot currently reaches:

```
open_entry -> func_80011AE0 (main) -> func_8001AD90 -> func_8001C044 -> func_8001BD08
```

and spins in `func_8001BD08`, a CD state machine polling a status word at
`0x8003E92C` for 2 or 5. It never advances because the PSY-Q CD library is
running as raw recompiled MIPS instead of being routed to the runtime's `LibCd`.

## Layout

```
config/kf2.json          recompiler config (schema: RecompOne.Recompiler/Config/ConfigLoader.cs)
config/funcmaps/         generated function maps (address/name/size)
patches/                 hand-written C# replacing recompiled functions
scripts/inspect_disc.py  SYSTEM.CNF + ISO9660 listing from a .cue/.bin
scripts/extract_file.py  extract a disc file and dump its PS-X EXE header
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

## The SDK naming problem (main open issue)

`SdkPatches.cs` routes PSY-Q library calls to the runtime's HLE implementations
by matching **exact function names** — `VSync`, `DrawOTag`, `DrawSync`,
`PutDrawEnv`, `PutDispEnv`, `CdInit`, `CdRead`, `PadInitDirect` and so on. A
linear sweep names everything `func_800xxxxx`, so nothing matches and the
recompiler reports:

```
[Recompiler] it was applied 0 reimplementations
```

Consequences, in order of severity:

1. **Nothing can ever be presented.** `Runtime.PresentFrame()` has exactly one
   caller in the entire runtime: the HLE `LibEtc.VSync`. If `VSync` is not
   reimplemented, no frame is ever pushed to the window regardless of what the
   game draws.
2. **Interrupts are never delivered.** `DispatchIrq` is called from
   `PresentFrame`, so anything waiting on an IRQ-updated counter waits forever.
   This is circular with (1).
3. **CD loading stalls**, which is where boot currently stops.

Raw GPU register writes are *not* a problem — `PSMemory.cs:163-164` traps
`0x1F801810`/`0x1F801814` and forwards to the emulated GPU, and DMA is emulated
too. So drawing works at the hardware level; presentation and the CD/pad
libraries are what need HLE.

Upstream's guidance for this case is manual: "you need to map each SDK address to
its runtime counterpart yourself, using `patches` with a `replace` that targets
the runtime function." So each identified function gets a `patches[]` entry, e.g.

```json
{ "overlay": "open", "address": "0x8001BD08",
  "target": "RecompOne.Runtime.Sdk.LibCd.CdRead", "mode": "replace" }
```

Identifying them is ordinary PS1 RE work. Options, roughly in order of payoff:

- **PSY-Q signature matching.** The libraries are statically linked and identical
  across games, so hash/FLIRT-style matching against the official SDK objects
  identifies them wholesale. This is what `ghidra_psx_ldr` and hash-based
  matchers do, and it is the only approach that scales.
- **Behavioural fingerprinting.** Each library function touches characteristic
  hardware: `DrawOTag` programs DMA channel 2 in linked-list mode, `DrawSync`
  polls GPUSTAT, the CD functions hit `0x1F801800`-`0x1F801803`. Note the
  generated C# builds MMIO addresses at runtime from a `0x1F800000` base rather
  than emitting literals, so grep for the base plus offset, not the full address.
- **Follow the call graph from the stall.** `func_8001BD08` and its helpers
  (`func_8001B5AC`, `func_8001B5CC`, `func_8001B664`) are the CD library.

## Next steps

1. Identify the PSY-Q SDK functions and add `patches[]` entries for them, VSync
   first — without it nothing can ever appear on screen.
2. Correct linear-sweep damage as it surfaces. Coverage is only ~56% of `open`
   and ~86% of `game`, so expect more gaps; `scripts/add_call_targets.py`
   re-derives starts from the code and can be re-run at any time.
3. Work out the `CD/COM/*.T` archive formats when asset work starts.
   [IvanDSM/KingsFieldRE](https://github.com/IvanDSM/KingsFieldRE) has KFModTool
   and format notes covering this game across its regional variants (no symbols,
   `.map` or ELF, so it does not help the function maps).

## Upstream contribution policy

RecompOne's maintainer rejects AI-authored pull requests outright ("AI PRs will
be rejected, no exceptions"). That governs contributions to RecompOne itself; the
tool is MIT and using it here is unaffected. It does mean any fix to the
*recompiler* should go upstream as an issue rather than a PR, unless you write
the patch yourself.
