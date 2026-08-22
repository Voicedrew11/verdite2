# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

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

**`NOTES.md` is the index, and `docs/` is the working log.** `NOTES.md` carries
what the project is and where it stands, plus a map of the nine documents under
`docs/` and the exact section titles in each. Read the index before starting
anything, then the one or two documents your task touches — they are split by what
you would be doing when you need them:

| file | when |
|---|---|
| `docs/DEVELOPMENT.md` | build, run, diagnose, measure |
| `docs/RECOMPILATION.md` | config, overlays, function maps, SDK addresses |
| `docs/RUNTIME.md` | interrupts, HLE, the `patches/recompone/` stack |
| `docs/RENDERING.md` | perspective correction, sub-pixel, Z-buffer, dither |
| `docs/WIDESCREEN.md` | aspect ratio, the HUD, the three culls |
| `docs/GAME_INTERNALS.md` | the game's own addresses and routines |
| `docs/PATCHES_AND_MODS.md` | hooking, settings UI, frame pacing, auto reload |
| `docs/INPUT.md` | pad, sticks, keyboard, mouse |
| `docs/TODO.md` | next steps and open, undiagnosed questions |

Update the right document when you learn something — that is where findings
belong, not in commit messages. Source comments still say `See "X" in NOTES.md`,
and **the text they name is not in `NOTES.md` any more** — the titles are
unchanged, so the index resolves X to a document, but it is a hop rather than a
direct hit. Grep `docs/` for the title, not `NOTES.md`.

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
steady-state `KF2_LOG=sdk` excerpt under "Status" in `NOTES.md`).

**Anything that has to be judged by eye is the user's job, not yours.** Do not
try to capture, screenshot or otherwise scrape the game window — it burns a lot
of context and produces nothing a person could not tell you in one sentence.
Measure what a counter can measure, then say plainly what still needs looking at
and ask. That distinction is already all over `docs/`, which repeatedly
separates "mechanism measured" from "picture never checked"; keep writing it down
that way.

### Diagnostics

```bash
KF2_LOG=bios,cd,gpu,dma,sdk,spu,mdec  # or KF2_LOG=all; wired up in Program.cs
KF2_CDTRACE=1                          # stack trace on first CD register access (patch 0002)
KF2_AUTOPAD=8:Start:400,20:Circle:200  # scripted pad input: seconds:button:holdMs
KF2_FPS=60                             # 30 (default), 60, or off; see "Frame pacing"
KF2_FPS_GATE=80040348                  # at 60, loop stages to run every other frame
KF2_FRAMESTATS=15 KF2_LOOPPROBE=20     # report intervals for the two mods
KF2_WIDESCREEN=16:9 KF2_WIDESCREEN_PROBE=1  # aspect (4:3 by default), and the margin census
KF2_WIDESCREEN_PROBE=2                   # the census plus every wide primitive, once per shape
KF2_WIDESCREEN_EFFECTS=0                 # leave the death fade and damage flash 320 wide
KF2_WIDESCREEN_CULL=0                    # leave the game's view cone at its 4:3 shape
KF2_WIDESCREEN_CULL=1.5                  # pin a widening factor instead of the aspect's
KF2_WIDESCREEN_CULL_PROBE=1              # tiles lit, and what the 24x24 grid clipped
KF2_WIDESCREEN_CULL_PROBE=2              # also lit-per-ring after the occlusion flood
KF2_PRIMBUF_PROBE=1                      # the frame's primitive budget: peak, capacity, overflows
KF2_VIEWCLIP=0 KF2_VIEWCLIP_PROBE=1      # the game's view-space clip volume, and where it cuts
KF2_NODITHER_PROBE=1                   # where the dither bit comes from, and GPUSTAT bit 9
KF2_PERSPECTIVE=0                      # affine textures again (correction is on by default)
KF2_PERSPECTIVE_PROBE=1                # the GTE vertex map's hit rate
KF2_PERSPECTIVE_FALLBACK=1             # also guess by screen position on a miss (the old mechanism)
KF2_SUBPIXEL=1                         # sub-pixel vertex positions (off by default)
KF2_SUBPIXEL_PROBE=1                   # how far vertices actually move, in pixels
KF2_ZBUFFER=1                          # per-pixel occlusion from GTE depth (off by default)
KF2_ZBUFFER_PROBE=1                    # how many triangles actually depth-tested
KF2_ZBUFFER_PROBE=2                    # the frame's polygon census, and a map of the depth buffer
KF2_ANALOG=0                             # twin-stick control off (it is on by default)
KF2_ANALOG_TURN=1.0 KF2_ANALOG_MOVE=1.0 KF2_ANALOG_DEADZONE=0.15  # its sensitivities
KF2_ANALOG_INVERTY=1 KF2_ANALOG_PROBE=1  # look-Y inversion, and the control-state report
KF2_KEYS=stock                           # RecompOne's own key bindings; the port ships WASD
KF2_MOUSE=1                              # mouse look (off by default; Escape captures the pointer)
KF2_MOUSE_TURN=1.0 KF2_MOUSE_LOOK=1.0 KF2_MOUSE_INVERTY=1   # its sensitivities and look-Y
KF2_MOUSE_BUTTONS=Square,Triangle,Cross  # left, right, middle, as pad buttons
KF2_MOUSE_KEY=Escape                     # the key that captures and releases
KF2_AUTORELOAD=1 KF2_AUTORELOAD_DELAY=2.0 KF2_AUTORELOAD_SLOT=0  # reload the last save on death
KF2_UISCALE=1                            # force the interface scale, and save it
```

Patch settings live in `patches/settings/`. A patch registers an `IPatchPage`
against one of the runtime's own settings sections —
`PatchSettings.Register("display", new FramePacingPage())` — and is drawn inside
it, so the frame rate and the dither switch sit in System ▸ Settings ▸ Video
beside vsync rather than in a panel of their own. That is where a mod's
`DrawSettings` body goes when the mod becomes a patch. Pages that give the same
`Title` share one heading, so single checkboxes group under "Enhancements"
instead of each getting a rule of its own. That section is the runtime's
`display` — still that id everywhere in code; the port renames only its *label*,
through `Localization.Merge`, which needs no patch to the checkout. See "Patch
settings" in `docs/PATCHES_AND_MODS.md`.

**`gameplay` is the one section the port adds itself**, for patches that change
how the *game* behaves rather than how the machine does — auto reload is not a
video option and not an input option. `ISettingsSection` is public and
`SettingsRegistry.Register` takes any implementation, so it needs no patch to the
checkout either; `patches/settings/GameplaySection.cs` is an empty shell and
everything in the pane is a page registered against `"gameplay"`. A new key has to
supply all three of the runtime's languages, unlike an override of an existing
one.

Frame pacing is load-bearing: without it the port runs faster than the game can on
hardware, so it lives in `patches/` and is always on. Dithering is a patch for a
different reason — it is a picture the port should be able to offer without a
package having to load — and defaults to *off* (no crosshatch). **Perspective
correction is a patch for that same reason and is on by default**, beside it under
Video. Unlike the others its work is not in `patches/` at all: a texture
coordinate is decided far below anything `HookManager` can reach, so the mechanism
is `patches/recompone/0009` and `0012` (`GteVertexMap`, the rasterizer and the prim
shaders) and `patches/Perspective.cs` is only the switch and the probe. The depth is
tied to its vertex by **the address the screen coordinate is stored at**, followed
through the game's own copy into the primitive packet — not by the screen position,
which several vertices share and which `0009`-`0011` could only guess between.
**Sub-pixel vertex positioning is the other half of the same recovered number** and
is shaped the same way — mechanism in `patches/recompone/0010` and `0012`, switch and probe in
`patches/Subpixel.cs`, checkbox under Video — but defaults to *off*, because the
mechanism has been measured and the picture has not. **The Z-buffer is the same
depth used as occlusion** rather than as a texture denominator: the GPU has none,
so intersecting surfaces take turns in front of each other on the ordering table,
and `patches/recompone/0014` tests the recovered SZ per pixel instead. Off by
default for the sub-pixel reason; its switch sits with the others under Video.
See "Sub-pixel vertex positioning" and "Z-buffer" in `docs/RENDERING.md`. Auto reload is a
patch for the same kind of reason: a death costing four screens of menu is
something a player expects the port itself to have dealt with, so it is on by
default and its knobs are under Gameplay. Analog twin-stick control is the same
test applied to the pad — without it a modern controller's left stick is wired to
the D-pad and *turns* rather than walking — so it is on by default too, and its
knobs are under Input, below the button-binding table. It costs nothing when a
stick is centred: both hooks return before touching memory, so keyboard and D-pad
play are identical to having it off. **Mouse look is the other half of that
patch rather than a patch beside it** (`patches/Mouse.cs`): a mouse and a stick
both ask for the same per-frame turn and pitch step, so the mouse's number is
spent inside `Analog.BeforeLook` and one routine writes the velocity word. Its
buttons take the other route entirely — `PadReadEvent`, so they are pressed *as
pad buttons* at the moment the game reads the pad, which needs no address, follows
the game's own control-config screen and works in its menus. It is **off by
default**, though not for the sub-pixel reason: the path *is* measured end to end
— the angle asked for and the angle the game applied agree within a few percent
over four windows of real play — but a pointer that disappears into the game
unasked is worse than one switch to find. What no counter can answer is the feel
(0.15°/px) and whether the pitch runs the right way round. See "Mouse look" in
`docs/INPUT.md`.

**The port ships its own keyboard layout** (`patches/KeyLayout.cs`), because
RecompOne's defaults are a console's spelled on a keyboard — face buttons on
Z X A S, D-pad on the arrows — and this game walks *and turns* on the D-pad, so
the arrows alone are a tank control. W/S walk, A/D strafe (the game strafes on
L1/R1), the arrows still walk and turn, Space attacks, F uses, Q casts, Tab opens
the menu, and pitch is the mouse's alone. **Changing a runtime default needs no patch to the checkout**:
`ConfigManager.Game.Keys` is settable and `Configure()` runs *before*
`ConfigManager.Load`, which saves the in-memory object when there is no
`settings.json` — so it is a default rather than an override. An existing config
is migrated once, only if every binding in it is still stock, and the fact is
recorded in `interface.ini` (`kf2.keys.layout`). Up and Down carry a **second**
key each — the arrows — which the runtime's one-string-per-button schema cannot
hold, so they are ORed in at `PAD_dr` like the mouse buttons; without them the
in-game menu would scroll on W and S. Changing the layout *after* it has shipped
costs one piece of bookkeeping — bump `Version` and record the old layout in
`Superseded`, or an existing config reads as customised and is never corrected;
that is how v1's swapped attack/use was fixed. See "The keyboard layout" in
`docs/INPUT.md`.

**Widescreen is a patch for the dither reason** — an aspect ratio is a picture the port should be able to offer without a
package having to load, and Video is where a player looks for it — but it is the
one patch that defaults to *doing nothing*, for the sub-pixel reason: the picture
has never been checked by eye. (Its two sub-options are on by default, since they
only do anything once an aspect has been chosen; the tint stretch is on because
the picture without it *was* checked and was wrong.) The measurement tools are the mods that are left
under `mods/` — **enable them in the game's Mods panel**, since mods default to
off and load silently when disabled. Prefer them to `KF2_LOG=sdk`, which is
gigabytes a minute.

Widescreen is `Display.WideAspect` and nothing else: the runtime renders a margin
either side of the display buffer and presents it, the projection is untouched, so
the sides show geometry the game submitted and the GPU used to clip — a quarter of
its primitives in an area. The HUD is anchored at its **source**, not from the
primitive stream: `func_80031D5C` walks its fourteen entries at `0x80067774`, and
its pre/post hooks rewrite the projected XY words in the primitive-buffer interval
it just allocated, before it returns. `DrawOTag` is not asked to recognise those
packets — a rendezvous at playback is what made the panel flash in the margin.
The in-game menu never rebuilds the HUD and never walks those packets either
(it ClearOTags); leftover picture in the extra columns is pixels in the last two
display targets, and each menu `DrawOTag` fills those columns so the list sits
on pillarboxes rather than last frame's HUD. Rules keyed on how a primitive
*looks* were tried twice and failed twice: the CLUT
column moved half the world sideways, and "the last 128 OT entries inside the two
measured boxes" tore the in-game menu apart, because a menu is screen space in
front of the world too. `DrawOTag` remains a replacement only for the
**screen-space tints** — the death fade, the damage flash, the wash on an area
load — which the game draws as one 320-wide quad and which therefore covered only
the middle of a wide picture. All of them come out of one drawer
(`func_8003220C` fills a request block, `func_8003214C` submits
`func_80031EE8(0,0,320,240)`). A pre-hook on the generic quad builder identifies
the tint by `func_8003214C`'s return address, records the just-about-to-be-allocated
packet, and the replacement consumes that source marker before snapping it out to
the margin. The menu's black input overlay can have the same packet format but
reaches the builder from another call site, so it is never stretched. Opaque
full-screen pictures (titles, menus) are left at their authored width on purpose.
See "Widescreen" in `docs/WIDESCREEN.md`.

**The cull the margin runs into is `patches/CullCone.cs`**, and it is the other
half of widescreen rather than an option beside it. The game gates every object
and every geometry block on a **24×24 byte grid of tile visibility** at
`0x80192EAC`, rebuilt each frame by `func_8002D3A8` as a top-down trapezoid — the
4:3 frustum flattened onto the map — whose corners are seven `s16` pairs in
GAME.EXE's data at `0x80068760`. Widening the picture without widening that leaves
the sides showing only what the game happened to overdraw. Two things about it are
load-bearing: the 24×24 window fits the shipped cone *exactly* (`0/60 frames reach
the grid edge` at stock, measured), and the game's scanline fill
(`func_8002CF0C`) scans in from both sides, so a row whose edge left the grid gets
**no fill at all** — widening the table alone loses whole ranks of tiles. Hence the
post-hook that re-rasterises the recorded edges and fills what the game dropped.
The 24-tile window is stride and bounds baked into nine routines, so growing it is
a reimplementation of the visibility system whose only correctness check is a
person looking at the screen; `KF2_WIDESCREEN_CULL_PROBE=2` measured what that
would buy and the answer was **3.5% of lit tiles, at the far corners** — binding,
barely, and not worth it. See "The cull the margin runs into" in
`docs/WIDESCREEN.md`.

**The attract demo is a free live session**: leave the port at the title and it
walks itself into an area about a minute later, with a character, an HP bar and
(eventually) a death. That is how in-game behaviour gets tested without a human
driving the menus — `AutoReload.Simulate()` kills on demand from there, and the
death clock at `0x8019951A` can be pinned to hold any frame of the death sequence
still.

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
`generated/`); add new env-var-driven diagnostics there. Note the docs mention
`KF2_TRACECALL` — that was an ad-hoc local edit to the dispatcher and is *not* in
any committed patch; re-add it by hand if you need indirect-call tracing.

### Regenerating function maps

Only needed if the sweep is wrong or a new executable is added:

```bash
RC="dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build --"
$RC --generate-function-file -linear-sweep -disc disc/KingsField2.cue \
    -file OPEN.EXE -base 80011000 -skip 800 -out config/funcmaps/open.json
```

An `unmapped call: 0x…` has three causes, so check which it is before reaching
for a script. **Is the address a function start the map is missing, an address
inside a function that already exists, or a few instructions past a mapped start
that has no prologue?**

- Missing start — `scripts/add_call_targets.py` recovers it by harvesting `jal`
  targets and splicing them into the map. Only sites already inside a known
  function are harvested; a `.data` word that decodes as `jal` is not a call.
- Interior address — the sweep split one real function in two, because it ends a
  function at any `jr`/`j` plus delay slot and PSY-Q emits both *inside* a
  function (a `jr` through a switch table, a `j` to a shared epilogue). A
  conditional branch that crosses a boundary proves it, since MIPS branches never
  leave their function. `scripts/merge_branch_spans.py` merges on that proof (and
  on jump-table entries), checks nothing `jal`s a start it swallowed, and is
  idempotent — run it after any sweep. See "The sweep splits a switch" in
  `docs/RECOMPILATION.md`.
- False split from data — `add_call_targets.py` once treated a table word as
  `jal` and cut a real function. The crash address sits just past a mapped start
  that has no prologue and that nothing in code `jal`s; the previous function
  falls through into it. `merge_branch_spans.py` cannot see a fallthrough, so
  rejoin by hand (the previous start's size should reach the next real function).
  See "add_call_targets can split a function" in `docs/RECOMPILATION.md`. The script no longer
  harvests sites outside a known function, so re-running it will not re-cut.

```bash
python3 scripts/merge_branch_spans.py --dry-run   # all overlays, writes nothing
python3 scripts/merge_branch_spans.py fdat17
```

The FDAT overlays deliberately use `"skip": 0` so the overlay covers the module
header: the modules' switch jump tables live in it, past the 32 dispatch slots,
and the recompiler reads a jump table out of the overlay's own bytes. Raising
`skip` past them makes every `jr` through a table dispatch to nothing at run time.

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
config entry nor a recompile. See "Mods" in `docs/PATCHES_AND_MODS.md`; `patches/FramePacing.cs`
is the in-project example and `mods/` holds the loadable ones.

Watch the namespace — generated code is `Recompiled.KingsField2`, a *class* named
after the project, which shadows any namespace called `KingsField2`.

### Identifying an address: the techniques that work

In rough order of payoff (full reasoning and worked examples in
`docs/RECOMPILATION.md`):

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
`patches/recompone/` (numbered, applied in order by `setup_tools.sh`). Twenty-one of
the twenty-six are load-bearing; `0002`, `0003` and `0015` are diagnostics,
`0013` is a settings-placement hook, and `0014b` only restores four comment lines
whose presence patch `0015`'s context assumes.

`setup_tools.sh` **peels the stack off newest-first before applying it
oldest-first**, rather than asking each patch on its own whether it is already
applied. A per-patch reverse-check breaks the moment one patch edits lines another
added — `0010`, `0011`, `0012` and `0014` all edit the `GteDepth.cs` that `0009` creates — and the
symptom is `0009` being reported as "FAILED TO APPLY (upstream likely changed)" on
the second run of a script that is supposed to be idempotent. Undoing in the
opposite order to applying has no such problem. A patch that will not reverse stops
the peeling rather than being forced, so a fresh clone peels nothing and an
uncaptured edit inside the checkout is left where it is.

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
- `0008-unload-overlapping-overlays.patch` — `HandleRegionOverwrites` only
  dropped an overlay fully contained in the new one. `END.EXE` is smaller than
  `GAME.EXE` at the same base, so GAME's functions past `0x8003A000` stayed
  mapped after the ending loaded. Any overlap is now an overwrite.

- `0009-perspective-correct-textures.patch` — the GPU is handed polygons with no
  depth in them, so it can only interpolate U and V linearly and every texture
  swims. The depth still exists one step earlier: `Gte.Rtp` produces the screen
  position and the view depth in the same call, and that screen position is
  bit-for-bit what reaches the GP0 packet. `GteDepth` keys a small table on it, so
  the two halves are reunited without following a register or a store. A miss is
  the old affine behaviour, which is what makes it safe on by default. See
  "Perspective correction" in `docs/RENDERING.md`.

- `0010-subpixel-vertex-positions.patch` — the GTE projects to 16.16 and then keeps
  only the whole part, so a vertex drifting slowly holds still and then jumps a
  pixel and its polygon twitches. The fraction is the low sixteen bits of the same
  expression `0009` takes the depth from, so `GteDepth` carries both and serves them
  independently. The hardware backend needed nothing — its vertex position was
  always a float — and the software rasterizer now works in sixteenths of a pixel
  for any triangle that recovered a fraction, which scales its edge functions and
  its area by 256 and changes no ratio taken from them. See "Sub-pixel vertex
  positioning" in `docs/RENDERING.md`.

- `0011-gte-depth-collisions.patch` — screen position is not a unique key, and
  dropping saturated vertices made every large nearby polygon fall back to affine
  (the texture looking as if the camera jumped). The table keeps several samples
  per pixel, records the clamp for depth only, and picks per primitive; a leftover
  far Z is refused rather than applied. Positions stay on the packet — moving a
  clamped vertex to its true projection opened holes. See "The table is not unique"
  in `docs/RENDERING.md`.

- `0012-exact-gte-vertex-map.patch` — screen position was never an identity, so
  `0011`'s picking between samples was scoring a collision rather than avoiding one,
  and a wrong W throws a texture across the screen. `GteVertexMap` keys on the
  **address** the coordinate is stored at instead: a `swc2` publishes the depth and
  the fraction, a store binds them to its destination, a load of a bound address
  republishes them so they follow the game's whole-word `lw`/`sw` into the packet,
  and `DrawPolygon` asks by the address `DrawOTag` read the word from — verifying the
  word before answering. No codegen change, so **this one needs no recompile**. The
  old table stays behind `KF2_PERSPECTIVE_FALLBACK` for comparison only. See
  "Following the value through memory" in `docs/RENDERING.md`.

- `0013-settings-slot-in-section.patch` — `SettingsRegistry.Extend` only draws
  *after* a section's whole body, so a port option that belongs beside one of the
  runtime's own controls could only ever land in a block underneath the lot. This
  adds `SettingsRegistry.DrawSlot(slotId)` and one call to it in the display
  section, after the render scale, which is where the widescreen aspect goes.
  Register with `PatchSettings.RegisterSlot`, not `Register`. UI only — **no
  recompile**.

- `0014-gte-zbuffer.patch` — a depth buffer from the same recovered SZ3
  perspective correction already follows through memory. The GPU has none, so
  intersecting surfaces take turns in front of each other on the ordering table;
  both rasterizers now test the recovered view depth per pixel. Window depth is
  a fragment value, not clip-space Z, so OpenGL does not far-clip the already-
  projected triangle. A miss is painter's order, so the HUD is untouched. Off
  by default. See "Z-buffer" in `docs/RENDERING.md`. **No recompile** — the lookup is
  the one `0012` already does.

- `0015-zbuffer-occlusion-census.patch` — diagnostic behind `KF2_ZBUFFER_PROBE=2`.
  Every polygon's bbox, depth range, table position and flags for the window, the
  `DrawOTag` walk position (`GteDepth.OtEntry`, counted from the far end — so
  `Widescreen`'s replacement of `DrawOTag` has to publish it too), and a 32×16 map
  read back from the depth attachment. It reads the RT the depth batches went to,
  not the presented one (last frame's, under double buffering) and not the most
  recently drawn (a fill stamps `LastDrawFrame` too, so that can be a buffer just
  cleared). **No recompile.**

- `0016-zbuffer-clear-at-frame-head.patch` — `PresentDisplay` incremented `_frame`
  before its trailing `Flush`, so the depth clear (keyed on `LastDrawFrame !=
  _frame`) fired on the tail of the *outgoing* frame and was then skipped at the
  head of the next one, which inherited the last batch's depths. Nothing rescues
  it — `isbg=0` here, so no game-side fill reaches `FillRtFull`. Swapping the two
  statements took the depth-map readback from 67-of-91 empty to 11-of-11
  populated. Real and measured, but **it did not cure the sky showing through
  nearby walls** — a second cause remains. **No recompile.** See "The clear
  landed at the tail of the frame" in `docs/RENDERING.md`.

- `0018-imgui-fractional-framebuffer-scale.patch` — Silk's `ImGuiController`
  computes `io.DisplayFramebufferScale` by dividing two `int`s, so a compositor
  running a display at a *fractional* scale (KDE's 1.15) truncates to 1 and
  `RenderImDrawData` sizes its GL viewport and every scissor from the logical
  window instead of the framebuffer — the whole interface lands in the bottom-left
  corner, with dead margins top and right. Recomputed as a float between
  `Update()` and `Render()`, which is the only window where it is read: layout is
  already fixed and still logical, so **input is untouched**. An integer scale
  divides exactly, which is why a 1:1 monitor never shows it. Its sibling defect
  is ours and unfixed — `QueryDpiScale()` reads the *primary* monitor's content
  scale once at startup, and GLFW's Wayland path returns the integer `wl_output`
  scale, so a 1.15 monitor reports 2.0 and the chrome is oversized on both
  screens. **No recompile.** See "The interface only fits a monitor whose scale is
  a whole number" in `docs/RUNTIME.md`.

- `0017-mouse-capture-and-motion.patch` — `InputManager` owns the `IMouse` and is
  `internal`, so a port could not reach the cursor at all. Adds `MouseCaptured`
  (`CursorMode.Raw`, or `Disabled` where raw is unsupported — both make GLFW
  report an unbounded virtual position, which is what turns successive positions
  into motion), `TakeMouseMotion` and `IsMouseButtonDown`, forwarded from
  `HostWindow` beside the `IsKeyDown` that already plays that role for the
  keyboard, and gives the cursor back in `Shutdown`. Everything else about mouse
  look is `patches/Mouse.cs`. **No recompile.** See "Mouse look" in
  `docs/INPUT.md`.

- `0019-popups-cannot-leave-the-window.patch` — every popup is centred and pinned
  with `SetNextWindowPos`, which is the flag that suppresses ImGui's own clamp
  into the viewport, and its size (`Size * Theme.Scale`) is capped against
  nothing, with `NoResize`, `NoMove` and `NoScrollWithMouse` closing the ways
  back. The 780x500 settings popup therefore outgrows a 1280x720 window at a
  `Theme.Scale` of 1.44 and takes the UI-scale field — the one control that would
  undo it — off-screen with it, permanently, since the value is saved. Reachable
  from the slider alone (0.5-3), and reached at `UiScale` 1 on the monitor whose
  `DpiScale` misreads as 2.0. The size is now clamped to the viewport, so an
  oversized scale costs scrolling instead of the controls, and `Debug > Reset
  view` re-applies `FontGlobalScale` and `Theme` instead of leaving giant text
  behind small windows. **No recompile.** See "The scale can put the settings out
  of reach" in `docs/RUNTIME.md`; `patches/UiScale.cs` (`KF2_UISCALE`) is the
  port's own way back for a config already past that point.

- `0020-theme-apply-compounds-the-style.patch` — `Theme.Apply` ends in
  `ScaleAllSizes`, which multiplies *every* size field, but only resets some of
  them first, so each accent, background or scale change multiplies the rest
  again: measured, `WindowMinSize` 32 -> 44 -> 88 -> 528 -> 1056 over five calls.
  ImGui floors every non-child, non-`AlwaysAutoResize` window at `WindowMinSize`
  **after** applying a size constraint, so that overrides `0019`'s clamp and the
  popup grows off the bottom of the screen — a 1264x704 clamp measured coming out
  1264x1056 on a 1280x720 viewport. `Apply` now restores the style ImGui built
  before re-theming, which stays correct whatever upstream adds to
  `ScaleAllSizes`. Latent since long before `0019`; changing the *accent*
  compounds it too. **No recompile.** See "The scale can put the settings out of
  reach" in `docs/RUNTIME.md`.
- `0021-vblank-wall-clock.patch` — the vblank advanced once per HLE `VSync` call, so
  every game clock hung on it ran at the rendered frame rate: the title screen's music
  played half speed because the CD-bound title loop issues one VSync call per ~15 fps
  picture. The vblank now advances on a wall-clock 60 Hz grid and missed vblanks are
  delivered as a burst at the next call; IRQ 0 moved with it, out of `PresentFrame`.
  **No recompile.** See "The vblank fired when the game asked" in `docs/RUNTIME.md`.
- `0022-present-stale-wide-target.patch` — `PresentDisplay` presented through the
  widened render target only if it was drawn into within the last 4 *presented*
  frames; with `VSync=False` (or any monitor faster than ~5× the game's 30 fps)
  both targets aged out between game frames and the picture fell back to the
  plain VRAM texture at 4:3 — the margins flashing black all session. The gate
  is gone: writeback keeps VRAM's middle columns identical to the targets every
  present, so a containing target is never staler than the fallback. See the
  trap paragraph in `docs/WIDESCREEN.md`; `KF2_PRESENT_PROBE=1` counts the
  picks. **No recompile.**
- `0023-splash-margin-idle.patch` — a wide render target may serve a present only
  while the scene is producing margin content (a primitive past the game's own
  edge, or a fill covering the target). STR playback draws one ordering table and
  MDECs the other, so only one flip buffer had a target and the present flapped
  between 16:9 and the 4:3 fallback -- the boot splash breathing horizontally.
  Idle-margin targets are demoted to the VRAM fallback, which presents at authored
  width. See "The present gate" in `docs/WIDESCREEN.md` (0024 replaces this
  idle window with a per-target latch). **No recompile.**
- `0024-margin-content-latch.patch` — replaces 0023's two-flip idle window with a
  per-target latch: one display flip delivering 32 game vertices past the game's
  own draw edge (or a fill covering the target) latches it for the overlay
  session, and an overlay load clears every latch. Menus, dialogs, shops and
  signs keep the wide picture instead of collapsing to the 320-wide 4:3 fallback
  the moment the world render stops -- which is what 0023's decaying stamp did,
  since such scenes produce no margin content at all. Splash and title still
  present at authored width: their oversized clear rects cross the edge two
  vertices a flip, under the threshold, and primitives the widescreen patch
  itself widened never count (`GpuHle.PortWidenedPrim`). **No recompile.** See
  "The present gate" in `docs/WIDESCREEN.md`.
- `0025-background-band-painter-order.patch` — outdoors the game links its
  backdrop at the extreme far end of the ordering table, but (census-measured) it
  projects *mid-depth*, not near — its SZ overlaps real geometry while the terrain
  behind it is genuinely farther. The Z-buffer believed that SZ and let the
  backdrop reject the distant terrain it should sit behind. `GteDepth.IsBackgroundPark`
  parks a primitive on that **disagreement**: it predicts the depth a table
  position would carry if position and SZ agreed (`FarSz × (1 − OtEntry/OtLength)`)
  and parks anything projecting below `SkyParkMargin` (0.7) of that — the backdrop,
  linked farthest, projects a third to a half of it; real geometry sits at or above
  its prediction and keeps testing. `FarSz` is the frame's far depth published one
  walk late beside `OtLength` in both `DrawOTag` sites. Two earlier tries missed and
  are recorded: a fixed node band (`OtEntry < 64`) over-captured filler and real far
  geometry, and a near-SZ cut (`SZ < FarSz × 0.3`) assumed the backdrop was near and
  never parked it. Parked prims go back to painter's order — no depth test, no depth
  write, counted as `GteDepth.ZBand`. **No recompile.** See "The second cause" in
  `docs/RENDERING.md`.

`0007`, `0008` and `patches/EndingHold.cs` are the shape to keep in mind
generally: **anything the runtime refreshes only at `VSync` is invisible to a
game that stops calling `VSync`**, and that failure mode is always silent.
`END.EXE` ends in `while(1);` with no `VSync`; on hardware the last frame stays
on the CRT, here the window dies. See "The ending screen" in `docs/RUNTIME.md`.

Upstream **rejects AI-authored pull requests outright**. Recompiler fixes go
upstream as issues, never as PRs, unless the user writes the patch themselves.

## Repository conventions

Never commit disc data or recompiler output — `disc/`, `generated/`, `*.sav` and
`settings.json` (written by the runtime at play time) are gitignored for
copyright and cleanliness reasons.

Commit messages in this repo state the *finding*, in the imperative, with the
observable consequence: "Map the PSY-Q CD library; boot now reaches the main
loop".
