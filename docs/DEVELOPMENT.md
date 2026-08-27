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


## Finding the rate defects

Every rate defect in this port is one defect wearing different clothes. King's
Field has no clock: it counts time in main-loop iterations and in `VSync` calls,
and on hardware those were the same clock, because the loop ran at a whole number
of vblanks. Drawing faster prises that one number into three, and every place the
game reads one and means another is a site.

`FramePacing` handles the sites a whole-function hook can reach. The rest were
being found by noticing them in play, one at a time — which is slow, and biased
toward whatever is *annoying* rather than whatever is *wrong*. These four tools
exist to replace that.

| tool | question |
|---|---|
| `patches/RateCensus.cs` + `scripts/rate_census.py` | which words move at the render rate? |
| `scripts/find_writers.py` | which code moves them, and what should be holding it back? |
| `scripts/rate_matrix.py` | did the fix work, at every rate? |
| `scripts/check_gate.py` | does the gate still obey its own rule? |

`scripts/kf2run.py` is the shared harness (launch, drive through `KF2_SHELL`,
harvest stdout) and `scripts/callgraph.py` + `scripts/kf2model.py` are the shared
static model. Nothing here needs a recompile.

### The census: which words move at the render rate

    python3 scripts/rate_census.py --run --seconds 30
    python3 scripts/rate_census.py --compare census-20.txt census-144.txt

Runs the same scene at two render rates and ranks every word of the game's memory
by how much more often it changed at the higher one. A word on the tick clock
changes at the same rate in both runs; a word on the render clock does not.

**The sampling clock is the emulated vblank, and that is the whole trick.**
Sampling per rendered frame would move the ruler with the thing being measured;
since `0021` the vblank is a wall-clock 60 Hz grid, so both runs sample at 60/s
and are directly comparable. Two consequences follow and are worth having in mind
before reading a number:

* **The measurement ceilings at 60 changes a second.** A word stepping at 144 Hz
  reports 60, so the ratio between a 20 fps run and a 144 fps run tops out near
  **3.0**, not 7.2. Read >1.5 as render-clocked and ~1.0 as tick-clocked.
* **Below 60 fps the vblanks arrive in bursts**, several per `VSync` call, with
  nothing running between them — so a burst reads as one change, which is what
  keeps a 20 Hz stepper reporting 20 rather than 60.

The census switches `KF2_SMOOTH`, `KF2_SMOOTH_OBJECTS` and `KF2_SMOOTH_ANIM` off
for its runs, because they write interpolated values every rendered frame *on
purpose* and would otherwise be the loudest rows in the report. That is the general caveat in one
instance: **the output is a candidate list, not a defect list.** A frame counter
and a primitive-buffer cursor are legitimately per-frame too.

Records of a known structure are folded, so a field stepping in eighty object
slots is one finding rather than eighty rows. Measured standing still in area 1
at 20 against 144 fps:

    object table +0x18   4 record(s)   ratio 3.84   13.0/s -> 49.8/s
    object table +0x24   4 record(s)   ratio 4.05   13.0/s -> 52.4/s
    object table +0x40   2 record(s)   ratio 3.84   13.8/s -> 52.8/s

That is the spinning-crystal and opening-door complaint, named without anyone
knowing what a crystal is. These fields are written through an index rather than a
literal address, so `find_writers` cannot attribute them — which is itself the
expected answer for anything inside a table.

**How the attribution was got anyway, since `find_writers` could not.** The step
the tooling does not automate is reading the emitted C# for the *stage* rather
than for the address. `func_80037C0C` (stage 2) sets a base register to
`0x80177714` and advances it by `0x44` at the loop tail, so every write it makes
is `base + k` — invisible to a `lui`/`addiu` scan and obvious to a regex over one
function's body. Tallying those gave `+0x18` at 7 sites, `+0x24` at 6 and `+0x40`
at 20, which is the census's three rows and nothing else. **When `find_writers`
answers "reached through a pointer or an index", the census's own structure
folding has already told you which structure — go and read the function that walks
it.**

Two things came out of finishing it that are worth keeping:

* **The first two rows were stage 2, and it is now gated** — see "Any frame rate"
  in `docs/PATCHES_AND_MODS.md`. `+0x18` and `+0x24` fell to ratio 1.29.
* **The third row was not.** `+0x40` stayed at 3.69. Adding `800331B4` to
  `KF2_FPS_GATE` as a one-off experiment dropped it to 17.6/s, which named the
  writer as stage 13's object pass. **That is the technique worth reusing: an
  address in `KF2_FPS_GATE` is a free attribution probe.** It does not have to be
  a change you would ship — gating a drawing function ruins the picture and
  answers the question anyway, in one run.

### find_writers: which code, and at what rate

    python3 scripts/find_writers.py 8006E5CC
    python3 scripts/find_writers.py --stage 2
    python3 scripts/find_writers.py --modal
    python3 scripts/find_writers.py --audit

Four verdicts, and the fourth is the one that matters:

    tick rate: gated directly           FramePacing skips the call on a non-tick frame
    tick rate: only under gated stages  ... and everything below it
    render rate: under ungated <stage>  stage 2 and stage 13 present, so cannot be skipped
    render rate: inside modal loop      a loop that renders its own frames

**"Which stage contains this" is not enough on its own.** The in-game menu lives
inside stage 3, which *is* gated, and every counter in it still stepped per
rendered frame — because `FramePacing` decides whether the loop is *entered* and
cannot cut one in half. A modal loop is computed, not listed: a function with a
backward branch whose subtree reaches a drawing entry point. There are 53.

`--audit` classifies every global on the per-frame path. As of writing: **594
globals, 73 held to the tick rate, 157 inside a modal loop, 364 under a stage that
presents.** A writer is any function that stores to the address — an initialiser
and a reset count too — so a render-rate row is where to look, not a verdict.

Addresses are recovered from `lui`/`addiu` pairs, which is how PSY-Q reaches
statics. Anything reached through a pointer, an index or a struct base in a
register does not appear, so an empty answer means "not written through a literal
address", never "not written".

### rate_matrix: did it work

    python3 scripts/rate_matrix.py menu-scroll --fps 20 60 144
    python3 scripts/rate_matrix.py death-clock --fps 20 144 --tickrate 20 30
    python3 scripts/rate_matrix.py menu-scroll --fps 144 --env KF2_MENUPACING=0
    python3 scripts/rate_matrix.py --list

Every empirical claim in these documents should be reproducible by one of these.
The output is a markdown table ready to paste; `--json` keeps the raw numbers so a
later run can be diffed against an earlier one.

**Read the rate a scenario reports, not the duration.** Some of these numbers are
stable and some are not: the menu repeat's spin measured 1.2 ms on one run and
16.6 ms on the next at identical settings, while the repeat *rate* it produced was
37.5 and 36.0. A scenario says which of its numbers is load-bearing.

Four things in `kf2run.py` are scar tissue and are why the harness is worth having
rather than re-glued each session:

* `state.inGame` goes true as the save loads, *before* the area transition
  finishes, so a press in that window is swallowed — hence the settle delay.
* Probes report per-second windows; the first is short and the last is truncated,
  so a scenario must say which windows it means.
* Never `pkill -f` on a pattern that also matches the calling shell's own command
  line. The harness kills itself and the symptom is a run that reports nothing
  rather than an error.
* These runs open a real window. They are not headless.

### check_gate: does the gate still obey its own rule

    python3 scripts/check_gate.py          # non-zero exit on a violation
    python3 scripts/check_gate.py --stages # what every stage reaches and writes

`FramePacing` states that **"can it draw" is the test each gate entry had to
pass**. That test was applied by reading the emitted C# by hand, once, and nothing
re-applied it since — so adding an address to `DefaultGate` was an assertion. This
makes it a check: the subtree must reach no `DrawOTag`, `PutDrawEnv` or
`PutDispEnv`, and anything that does must be a recorded exception with its reason.

Running it corrected a claim in these documents. Stage 3 was described as reaching
the renderer *only* as `func_8002A550 -> func_80037B5C -> func_800342D8`; it also
reaches it as `func_8002A550 -> func_80029CBC -> func_80018E80 -> func_800226A8 ->
DrawOTag`, and it reaches `VSync` outside any modal loop entirely, through the
menu blip `func_80022DC4`. The *reasoning* survives — those are extra renders
inside the stage, not the frame's own, and the main loop still runs stage 13
afterwards — but the single-path claim was wrong, which is the kind of thing a
hand-applied rule stops catching the moment it is written down.

`--stages` prints the inverse, which is the part that goes stale in prose: what
each stage reaches and how many globals it writes, so stage 2's deliberate
exclusion stays a recorded consequence rather than a remembered decision. It also
lists **four ungated stages that could be gated and are not** — `func_8002C944`,
`func_800140AC`, `func_80016FC8` and `func_80014534` all submit nothing and write
between 1 and 14 globals each. Nobody has looked at whether those globals are
per-tick state; the tool only says they are reachable and unheld.
