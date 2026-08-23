# Development: building, running and measuring

Everything needed to get the port compiling, running and reporting on itself.
The recompiler's own quirks are in [RECOMPILATION.md](RECOMPILATION.md); the
patches to the RecompOne checkout are in [RUNTIME.md](RUNTIME.md).

**Nothing here builds without the disc** (gitignored, `disc/KingsField2.cue`) or
without `tools/RecompOne` (a gitignored checkout, not a submodule).

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

## Setting up the tools

```bash
bash scripts/setup_tools.sh
```

Clones RecompOne, applies everything in `patches/recompone/`, and builds the
recompiler. Idempotent, so it is also the way to re-apply local fixes after
pulling upstream.

It gets that idempotency by **peeling the stack off newest-first and then applying
it oldest-first**. Asking each patch on its own "are you already applied?" — which
is what it used to do — only works while no patch touches lines another one added,
and `0010` edits the `GteDepth.cs` that `0009` creates. The symptom was `0009`
reverse-checking against text `0010` had since changed, failing, and being reported
as upstream having moved. `0011`, `0012` and `0014` edit that same file again. Undoing in the opposite
order to applying cannot hit that. A patch that will not reverse stops the peeling
instead of being forced, so a fresh clone peels nothing, and an uncaptured edit
inside the checkout — the normal way a new patch gets written — stops it rather
than being rolled over.

The seventeen patches themselves, and what each one is required for, are in
[RUNTIME.md](RUNTIME.md). Function-map generation — step 2 of the old workflow —
is in [RECOMPILATION.md](RECOMPILATION.md).

## Recompile

```bash
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build -- \
  config/kf2.json
```

2099 functions into `generated/` (~163k lines of C#).

## Build and run

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


## Diagnostics

Log channels and the port's own probes are all environment variables read in
`Program.cs`; `CLAUDE.md` carries the complete current list. The ones with
something to say about *how* to use them:

- **`KF2_LOG=bios` is very expensive during play.** The game polls `PAD_dr`
  hundreds of thousands of times a second, which is gigabytes of log a minute.
  `KF2_LOG=sdk` is the same order once the frame loop is running — prefer the
  patches' own probes, which report a summary per window instead of a line per
  call.
- **`KF2_CDTRACE=1`** puts a stack trace on the first CD register access
  (`patches/recompone/0002`).
- **`KF2_AUTOPAD=8:Start:400,20:Circle:200`** replays scripted input, for
  reproducing an input-triggered bug with nobody at the keyboard. Its clock
  starts when the first area module loads, which is the only point in the boot
  sequence that reliably means "in game".
- **`KF2_SHELL=1`** opens a command channel for agents on TCP 127.0.0.1:27900 —
  `state | load <slot> | warp <area> | press <button> [ms] | kill`, one request
  per line, one single-line JSON response back. See "The command channel" in
  [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).
- **`mcp/` is the same channel as an MCP server.** `KingsField2Mcp.csproj` is a
  standalone stdio server exposing those six verbs as typed tools to any MCP
  host (`KF2_MCP_ENDPOINT` overrides the endpoint). See "The MCP layer" in
  [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).
- **The attract demo is a free live session.** Leave the port at the title and it
  walks itself into an area about a minute later, with a character, an HP bar and
  eventually a death — which is how in-game behaviour gets tested without a human
  driving the menus. `AutoReload.Simulate()` kills on demand from there, and the
  death clock at `0x8019951A` can be pinned to hold any frame of the death
  sequence still. See "Auto reload" in
  [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md).

### For a hang, take the managed stack of the live process

A recompiled function keeps its
MIPS address in its name, so a stack trace of the hung game names the MIPS routine
it is spinning in — no logging, no rebuild, no reproduction in a debugger:

```bash
dotnet tool install -g dotnet-stack        # once
~/.dotnet/tools/dotnet-stack report -p $(pgrep -f net10.0/KingsField2)
```

This is the fastest tool in the box for a silent hang. It is what identified
the menu deadlock (see [RUNTIME.md](RUNTIME.md)) in one shot, after a static hunt had gone nowhere:
`func_80022EFC → func_8005F564 → func_8005FE64 → BiosB.PadRead` is the whole
diagnosis, read off the top four frames. Note the process must be started from
the same shell environment you run `dotnet-stack` in, or the diagnostic socket
in `TMPDIR` will not be found.

**`KF2_TRACECALL` does not exist in the tree.** It was an ad-hoc local edit to the
dispatcher, is *not* in any committed patch, and is mentioned here and in
`CLAUDE.md` only so that a reference to it is not mistaken for a missing feature.
Re-add it by hand if you need indirect-call tracing.

## Compile a mod without launching the game

Roslyn compiles mods at load, so a
typo costs a whole boot to find. A throwaway csproj that compiles the mod source
against `bin/Release/net10.0/RecompOne.Runtime.dll` and `ImGui.NET.dll`, with
`ImplicitUsings` disabled to match `ModCompiler`, catches it in three seconds.
(Learned on the widescreen mod, and moot for that one now that it is a patch and
the project build type-checks it — but it still applies to everything under
`mods/`.)

## Getting pixels out without a screenshot

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

That "one ordering table, twice" trick is the port's standard instrument for a
picture change: it is what licensed perspective correction's 76.6% and what the
dither pair above measures. It cannot be used for the Z-buffer — the second pass
would fail every depth test against the first — see "Z-buffer" in
[RENDERING.md](RENDERING.md).

**Running headless has its own trap.** With the desktop session locked, KWin
stops sending frame callbacks and the port blocks in `SwapBuffers` forever with
`VSync=True` — 0% CPU, no log output, and it looks exactly like a hang. Set
`VSync=False` in `interface.ini` to run it without a visible window, and read the
caveat about the widened render target under "Widescreen" in
[WIDESCREEN.md](WIDESCREEN.md) before trusting a headless picture measurement.

## What counts as verification

There are no tests. Verification is empirical, and the useful distinction — kept
deliberately all through these documents — is between **a mechanism that has been
measured** and **a picture that has been looked at**. A counter can say that 92%
of vertices recovered a depth; only a person can say whether the cave looks right.
Where a feature's mechanism is measured and its picture is not, that feature ships
switched off, and the reason is recorded with it.
