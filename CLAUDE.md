# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A static recompilation of **King's Field (NTSC-U, `SLUS-00158`)** — the North
American release of the Japanese *King's Field II* (`SLPS-00069`) — using
[RecompOne](https://github.com/BlackLabelHQ/RecompOne). The series was renumbered
for the West: the US-boxed "King's Field II" (`SLUS-00255`) is a *different game*
and every address here is wrong for it.

There is no decompilation, no ELF and no `.map`. Function boundaries come from a
linear sweep and PSY-Q library functions are identified by hand, so most work in
this repo is *reverse engineering*, not application coding: find an SDK function's
address in the disc image, map it to the runtime's HLE implementation, re-run the
recompiler, run the game, read the logs.

**`NOTES.md` is the working log and the primary reference.** It carries the
current status, every identified address, the reasoning behind each
identification, and the next steps. Read it before starting anything and update it
when you learn something — that is where findings belong, not in commit messages.

## Build and run

Nothing here builds without the disc (gitignored, `disc/KingsField2.cue`) and
without `tools/RecompOne` (a gitignored checkout, not a submodule).

```bash
bash scripts/setup_tools.sh          # clone RecompOne, apply patches/recompone/*, build recompiler

# recompile MIPS -> C# into generated/ (~2099 functions, ~163k lines)
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build -- config/kf2.json

dotnet build KingsField2Recomp.csproj -c Release
dotnet run --project KingsField2Recomp.csproj -- disc/KingsField2.cue
```

`setup_tools.sh` is idempotent and is also how you re-apply the local patches
after pulling upstream. The cue path is needed at *play* time as well as at
recompile time.

There are no tests. Verification is empirical: run the game with log channels on
and check the trace against what the SDK sequence should look like (see the
steady-state `KF2_LOG=sdk` excerpt in `NOTES.md`).

### Diagnostics

```bash
KF2_LOG=bios,cd,gpu,dma,sdk,spu,mdec  # or KF2_LOG=all; wired up in Program.cs
KF2_CDTRACE=1                          # stack trace on first CD register access (patch 0002)
KF2_AUTOPAD=8:Start:400,20:Circle:200  # scripted pad input: seconds:button:holdMs
KF2_FPS=60                             # 30 (default), 60, or off; see "Frame pacing"
KF2_FPS_GATE=80040348                  # at 60, loop stages to run every other frame
KF2_FRAMESTATS=15 KF2_LOOPPROBE=20     # report intervals for the two mods
KF2_WIDESCREEN=16:9 KF2_WIDESCREEN_PROBE=1  # aspect override, and the margin census
KF2_NODITHER_PROBE=1                   # where the dither bit comes from, and GPUSTAT bit 9
```

Frame pacing is load-bearing: without it the port runs faster than the game can on
hardware, so it lives in `patches/` and is always on. The measurement tools and
the widescreen support are real mods under `mods/` — **enable them in the game's
Mods panel**, since mods default to off and load silently when disabled. Prefer
them to `KF2_LOG=sdk`, which is gigabytes a minute.

Widescreen is `Display.WideAspect` and nothing else: the runtime renders a margin
either side of the display buffer and presents it, the projection is untouched, so
the sides show geometry the game submitted and the GPU used to clip — a quarter of
its primitives in an area. See "Widescreen" in `NOTES.md`.

`KF2_AUTOPAD` reproduces an input-triggered bug without a human at the keyboard;
its clock starts when the first area module loads, which is the only point in the
boot sequence that reliably means "in game". `KF2_LOG=bios` is very expensive
during play — the game polls `PAD_dr` hundreds of thousands of times a second,
which is gigabytes of log per minute.

**For a hang, take the managed stack of the live process instead of adding
logging.** Recompiled functions carry their MIPS address in their name, so the
trace names the routine directly:

```bash
~/.dotnet/tools/dotnet-stack report -p $(pgrep -f net10.0/KingsField2)
```

Start the game from the same shell you run that in, or the diagnostic socket in
`TMPDIR` will not be found.

`Program.cs` is hand-owned (RecompOne would otherwise generate one into
`generated/`); add new env-var-driven diagnostics there. Note `NOTES.md` mentions
`KF2_TRACECALL` — that was an ad-hoc local edit to the dispatcher and is *not* in
any committed patch; re-add it by hand if you need indirect-call tracing.

### Regenerating function maps

Only needed if the sweep is wrong or a new executable is added:

```bash
RC="dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build --"
$RC --generate-function-file -linear-sweep -disc disc/KingsField2.cue \
    -file OPEN.EXE -base 80011000 -skip 800 -out config/funcmaps/open.json
```

`scripts/add_call_targets.py` recovers function starts the sweep missed by
harvesting `jal` targets; run it when a call dies with "unmapped call: 0x…".

## Architecture

```
config/kf2.json          recompiler config: overlays, funcMaps, stubs[], patches[]
config/funcmaps/*.json   swept function maps (address/name/size; size is mandatory)
patches/                 hand-written C# replacing recompiled functions
mods/<id>/               runtime-loaded mods (mod.json + C#, Roslyn-compiled)
patches/recompone/*.patch  local fixes to the RecompOne checkout itself
generated/               recompiler output (gitignored — derived from copyrighted disc data)
scripts/*.py             disc inspection and address-hunting tooling
Program.cs               hand-owned entry point; calls Entry.Run(PSMemory, cuePath)
```

The disc holds a 4 KiB boot stub (`SLUS_001.58`, named by SYSTEM.CNF) plus three
real executables — `OPEN.EXE` (title/intro), `GAME.EXE`, `END.EXE` — which **all
load at `0x80011000`** and are mutually exclusive. They are declared as
**overlays** in `config/kf2.json`. Left alone the recompiler would only find the
2 KiB stub.

Because the three overlays share an address range, *every* address-based config
entry must name its overlay explicitly. Prefer a named overlay over `"*"`.

### The core problem: SDK functions must be mapped by address

The recompiler's `SdkPatches.cs` binds PSY-Q calls to the runtime's HLE by exact
**function name**. A linear sweep names everything `func_800xxxxx`, so it always
reports `applied 0 reimplementations`. Every SDK entry point therefore has to be
mapped by address in `patches[]`:

```json
{ "overlay": "open", "address": "0x80016078",
  "target": "RecompOne.Runtime.Sdk.LibGpu.DrawOTag", "mode": "replace" }
```

63 such patches exist today (`libetc`, `libcd`, four `libgpu` entry points, six
`libcdstream`, libapi's `DMACallback`). `libpad` is the notable gap. Only map a
function the runtime actually implements — check
`tools/RecompOne/RecompOne.Runtime/sdk/Lib*.cs` first; unmapped library routines
run fine as recompiled MIPS because `PSMemory` traps their register writes.

`mode` is `"replace"`, `"pre"` or `"post"`. **Use config patches for `replace`
only** — binding an SDK entry point, which has to happen before any mod could
load. For anything else, do not add a config entry: RecompOne's `HookManager`
detours a recompiled function by address at run time, so a hook needs neither a
config entry nor a recompile. See "Mods" in `NOTES.md`; `patches/FramePacing.cs`
is the in-project example and `mods/` holds the loadable ones.

Watch the namespace — generated code is `Recompiled.KingsField2`, a *class* named
after the project, which shadows any namespace called `KingsField2`.

### Identifying an address: the techniques that work

In rough order of payoff (full reasoning and worked examples in `NOTES.md`):

1. **The overlay delta.** The three executables are three links of the same
   libraries, laid out at a constant offset *per translation unit* (not per
   library — `libcd`'s `cdio` and `stream` differ from each other). Identify once,
   derive the other two. `scripts/match_overlays.py` does this mechanically
   against a relocation-insensitive normal form; validate any new delta by
   re-deriving already-known addresses with it.
2. **Data-side search for hardware addresses.** PSY-Q reaches I/O through pointer
   tables in `.data`, never through literals in code — there are five
   `lui …, 0x1F80` instructions in all of `OPEN.EXE` and none is the GPU. Search
   the *data* for `0x1F801810` (GPU) or `0x1F801800` (CD) to find the table; the
   functions loading through it are the library.
3. **Diagnostic strings.** `func_80014C0C` is the `printf` thunk (BIOS A(3Fh), 69
   call sites). PSY-Q error text (`VSync: timeout`, `CdInit: Init failed`,
   `GPU timeout:QUE=…`) names the calling function for free.
4. **Struct offsets as evidence.** Two independent offsets agreeing (a `DR_ENV`
   packet at `env+0x1C` *and* a `0x5C`-byte copy) turns a guess into an ID.
5. **Indirect-call tracing.** Public entry points can have zero `jal` references
   because PSY-Q dispatches through driver tables filled in at init (`libgpu`'s
   15-slot table, libapi's `DMACallback`). Log the address in `Dispatcher.Call`
   rather than searching statically.

### Two traps

**Number bases differ between the config and the CLI.** In `config/kf2.json`,
`base` is a hex *string* while `size`/`skip`/`offset`/`lba` are decimal numbers;
on the `--generate-function-file` CLI, `-size`/`-skip`/`-offset` are hex.
`"skip": 2048` in the config is `-skip 800` on the command line.

**Overlays are read as raw bytes.** `ResolveOverlay` does not parse the PS-X EXE
header, so every `.EXE` overlay needs `"skip": 2048` to step past the 0x800-byte
header, and `base` must be the header's real text address (read it with
`scripts/extract_file.py --header-only`). The *boot* executable is different — it
goes through `Psx/Parser.cs`, which strips the header itself.

### csproj

`generated/` and `patches/` are picked up by the SDK's default globs — adding an
explicit `Compile Include` causes NETSDK1022. `tools/**` must stay explicitly
removed, or the build compiles RecompOne's own sources and its `obj/`
AssemblyInfo files (CS0579).

## The RecompOne checkout

`tools/RecompOne/` is gitignored, so **any edit made inside it is lost on a fresh
clone**. Changes to the recompiler or runtime must be captured as a patch in
`patches/recompone/` (numbered, applied in order by `setup_tools.sh`). Five of
the seven are load-bearing; `0002` and `0003` are diagnostics:

- `0001-bios-load-return-1.patch` — BIOS `Load` must return 1, not the header
  pointer. Without it the boot stub spins in the loader forever.
- `0004-libapi-dma-callbacks.patch` — adds `Sdk.LibApi` so DMA-completion
  callbacks run at all. A static recompilation has no exception path, so PSY-Q's
  IRQ-3 handler never runs and every DMA callback silently dies. Nothing errors;
  the work the game does *inside* the callback just disappears. Keep this in mind
  whenever something completes but produces no visible effect.
- `0005-libcd-interrupt-driven-reads.patch` — the polled read path and CD-ROM
  kernel events; without it the game hangs on the loading screen.
- `0006-irq-callback-table.patch` — the runtime otherwise derives the PSY-Q
  interrupt-callback table from the `HookEntryInt` argument, which for this game
  lands in game data and eventually calls a data word. `Program.cs` supplies the
  real per-overlay address; the patch also makes the derived path refuse a
  handler that is not a known function.
- `0007-pad-poll-outside-frame-loop.patch` — host input used to be polled only
  inside `PresentFrame`, so a game busy-waiting on the pad without vsyncing read
  a frozen snapshot forever. King's Field's screen transitions all begin with
  such a wait; this is what hung the in-game menu.

The last two are the shape to keep in mind generally: **anything the runtime
refreshes only at `VSync` is invisible to a game that stops calling `VSync`**,
and that failure mode is always silent.

Upstream **rejects AI-authored pull requests outright**. Recompiler fixes go
upstream as issues, never as PRs, unless the user writes the patch themselves.

## Repository conventions

Never commit disc data or recompiler output — `disc/`, `generated/`, `*.sav` and
`settings.json` (written by the runtime at play time) are gitignored for
copyright and cleanliness reasons.

Commit messages in this repo state the *finding*, in the imperative, with the
observable consequence: "Map the PSY-Q CD library; boot now reaches the main
loop".
