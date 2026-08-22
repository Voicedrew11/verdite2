# King's Field II — RecompOne port

Static recompilation of **King's Field** (NTSC-U, `SLUS-00158`) using
[RecompOne](https://github.com/BlackLabelHQ/RecompOne) (MIT).

**This file is the index.** It carries what the project is and where it stands;
everything else lives in `docs/`, split by what you would be doing when you need
it.

**Source comments still say `See "X" in NOTES.md`, and the text they mean is no
longer in this file.** The section titles are unchanged, so the map below resolves
X to a document — but grepping this file for one will only find its bullet in that
map, and the passage itself is one hop away. Four workflow headings were renamed
in the move (`1. Set up the tools` → "Setting up the tools", and its three
siblings); nothing in the source cites those.

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
different ways — see "The three ways a CD read can hang" in `docs/RUNTIME.md` —
and it turned up the fact that **`GAME.EXE` is not the whole game**: per-area
logic is MIPS code loaded off the disc at run time (see "GAME.EXE loads code" in
`docs/RECOMPILATION.md`). The area modules are confirmed by play, not just by
static analysis: walking around is `fdat02` executing.

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
state (see "Saving and loading" in `docs/GAME_INTERNALS.md`).

The picture, all four in `docs/RENDERING.md`: **textures are
perspective-correct** — the depth the GPU never receives is recovered from the GTE
and matched back by screen position, which changes 76.6% of a frame's pixels (see
"Perspective correction"). The same table now also recovers the **sub-pixel
position** the GTE truncates, so vertices need not snap to whole pixels; that one
is off by default until its picture has been measured the way the textures were
(see "Sub-pixel vertex positioning"). **A Z-buffer is available from the same
recovered depth** — per-pixel occlusion instead of the ordering table — and is off
by default for the same reason (see "Z-buffer"). Its one known picture defect —
the sky drawing over nearby walls outdoors — has since been found (the skybox
projects near and the game parks it at the far end of the ordering table) and
fixed in `patches/recompone/0025`; the re-check by eye is still owed. Nearby walls and floors no longer
pop back to affine the moment one vertex clamps off-screen, and a pixel that two
vertices share no longer hands one polygon the other's depth (see "The table is
not unique").

**The frame rate is pinned to 30 fps**, NTSC's fastest band — the port used to
burst past it (see "Frame pacing" in `docs/PATCHES_AND_MODS.md`). **The ending
runs**: `END.EXE` plays the two STR movies and holds "The End" — see "The ending
screen" in `docs/RUNTIME.md`.

**Widescreen and sub-pixel positioning ship switched off for the same reason** —
mechanism measured, picture never checked by eye. The Z-buffer's picture *was*
checked, came back wrong outdoors, and its second cause is now fixed (see
"Z-buffer"); it ships off anyway until a longer look replaces that single clean
one. Mouse look is
off for a different one: its path *is* measured end to end, but a pointer that
disappears into the game unasked is worse than one switch to find. What is open
and undiagnosed is in `docs/TODO.md`.

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

## The documents

A section name in quotes anywhere in these files is an exact heading, in the file
named beside it here.

### [DEVELOPMENT.md](docs/DEVELOPMENT.md)

Build it, run it, and measure what it did.

- Prerequisites
- Disc structure
- Setting up the tools
- Recompile
- Build and run
- Diagnostics
- Compile a mod without launching the game
- Getting pixels out without a screenshot
- What counts as verification

### [RECOMPILATION.md](docs/RECOMPILATION.md)

Making the recompiler produce correct code: config, overlays, function maps, SDK addresses.

- Two traps worth knowing
- Generating function maps
- Fixing bad output
- GAME.EXE loads code
- fdat32 is a cut area, and nothing can load it
- The SDK naming problem
- The overlay delta: identify once, get all three
- libgpu: found and mapped
- libcdstream: found and mapped
- VSync: found and mapped
- Where the rest of the mapped addresses are written up

### [RUNTIME.md](docs/RUNTIME.md)

What a static recompilation loses (interrupts, VSync-driven work) and the twenty patches to the checkout.

- DMA callbacks: the thing that was actually missing
- The three ways a CD read can hang
- The interrupt-callback table cannot be guessed
- The menu deadlock: input only moved when the game drew
- The ending screen
- The patches to the checkout, one by one
- The interface only fits a monitor whose scale is a whole number
- The scale can put the settings out of reach
- Two general shapes worth keeping
- Upstream contribution policy

### [RENDERING.md](docs/RENDERING.md)

Recovering the depth and the sub-pixel fraction the GP0 packet threw away: perspective correction, sub-pixel positions, Z-buffer, dither.

- Perspective correction: the depth is one step upstream, and the screen position is the key
- Sub-pixel vertex positioning: the same number's other half
- The table is not unique: remaining wobble and the "far away" pop
- Following the value through memory: the address is the vertex
- Z-buffer: the same depth, used as occlusion
- Dithering: one flag, and it lives in the draw environment

### [WIDESCREEN.md](docs/WIDESCREEN.md)

Aspect ratio, the HUD and screen-space effects authored 320 wide, and the three culls the margin runs into.

- Widescreen: the runtime renders the margin, the game fills a quarter of it
- The HUD does not widen with the world, and finding it is the problem
- The screen-space effects are 320 wide too, and one drawer makes all of them
- Widescreen became a patch, and the default stayed 4:3
- The cull the margin runs into: a 24×24 tile grid, and a trapezoid drawn on it
- The second cull: a view-space clipper, and it is set to twice the screen
- Is the 24-tile window worth lifting? Measured: binding, and barely
- There is a third cull and it is none of the obvious ones

### [GAME_INTERNALS.md](docs/GAME_INTERNALS.md)

The reverse-engineered game: main loop, player state, stats, death, movement, areas, saves, the boot stub.

- The main game loop, stage by stage
- Player state: found, and it was in stage 3 all along
- Saving and loading
- Debug tools

### [PATCHES_AND_MODS.md](docs/PATCHES_AND_MODS.md)

How the port's own code attaches, where its settings go, plus frame pacing and auto reload.

- Where each patch is written up
- Mods
- Patch settings: a patch's knobs go in the runtime's own sections
- Frame pacing: the port is pinned to the fastest band
- Auto reload

### [INPUT.md](docs/INPUT.md)

Pad, analog sticks, keyboard layout and mouse look.

- Analog twin-stick control
- The keyboard layout, and changing a default RecompOne provides
- Mouse look

### [TODO.md](docs/TODO.md)

Next steps, and an index of what is reported but not diagnosed.

- Open questions — reported, not diagnosed
- Next steps

## Where to write a new finding

Same rule as before, one level down: **the finding goes in the document, not in
the commit message.** Pick by what a reader would be doing when they need it, not
by what the finding is about — a GTE fact learned while chasing a cull belongs
with the culls if that is where it will be looked for. If it fits nowhere, put it
in the general-purpose part of the nearest file rather than starting a tenth
document.

Three markers are used where a passage's standing is not obvious from its prose:
**Confirmed** (measured, or read straight out of the emitted code), **Inferred**
(a reading that fits the evidence and has not been tested), and **Open**
(reported or suspected, not diagnosed). Keep the two states apart when you write
it down, the way these files already do:
**a mechanism that has been measured** and **a picture that has been looked at**
are different kinds of evidence, and most of the port's defaults turn on which
one a feature has. Mark what is inferred as inferred; a plausible story about a
branch has twice been wrong here (the pitch sign, and attack/use), and both times
the write-up is what made the correction cheap.
