# Sharing Verdite2's work across Verdite1, Verdite2 and Verdite3

What in this repository could serve the other two games (King's Field and
King's Field III in the Japanese numbering), what is tied to this one, and the
running log of the work to share it. The plan, with its phases, gates and rules,
is `docs/SHARING_PLAN.md`. Read that first, then the progress log at the bottom of
this file.

**The classification below is a best guess, made before any second game exists.**
It comes from reading the code: an inventory of every file under `patches/`,
`scripts/`, the launcher, packaging, CI, `mcp/`, `mods/`, `config/` and
`tools/RecompOne`, spot-checked against grep. The real bucket of a patch is known
only once Phase 3 has ported it to a second game, and each entry should move when
that happens. **Inferred** is the standing of everything here unless it says
**Confirmed**.

The per-file detail (hook targets, addresses, struct layouts, env vars, settings
keys, mod-visible members, and where the seam is in each file) is in
`docs/SHARING_INVENTORY.md`. This file is the summary and the decisions.

## The buckets

- **A. RecompOne fork**: `tools/RecompOne`, the generic PS1 runtime and
  recompiler. Shared in Phase 1.
- **B. Game-agnostic infrastructure**: no knowledge of the game's RAM, code or
  data layouts. It may still carry trivial game strings (an env prefix, an
  assembly name, a path), which are listed.
- **C. A patch whose mechanism might generalize**: the idea is not
  King's-Field-specific, but the implementation reads this game's RAM or hooks
  its code.
- **D. KF2-only**: only makes sense for this game.

For bucket C the couplings are the five kinds in the plan: **hook targets**,
**data addresses**, **struct layouts**, **control-flow assumptions** and
**overlay names and bases**.

## The finding that shapes everything else: one per-game table removes most of the coupling

Most C patches carry the same few facts about this game, restated in each file.
Gathered once, as a per-game data structure each game supplies, they would turn
a large share of bucket C into bucket B. This is **Inferred** from reading: no
second game exists to confirm the counterparts are as regular.

| fact | KF2 value | restated in |
|---|---|---|
| libgpu `DrawOTag` per overlay (the frame boundary) | `open 0x80016078`, `game 0x80060818`, `end 0x80013D80` | `FramePacing`, `Perspective`, `Pgxp`, `Subpixel`, `ZBuffer`, `NoDither`, `Widescreen` (the one `Replace`), `FluidSmoothing`, `remaster/Lights`, others |
| libetc `VSync` thunk per overlay | `open 0x8001EB88`, `game 0x8005FCC8`, `end 0x8001B154` | `FramePacing` |
| libgpu `PutDrawEnv` | `game 0x80060870` (and per overlay in `NoDither`) | `NoDither`, `remaster/Atmosphere` |
| PSY-Q interrupt-callback table per overlay | `open 0x8003DD48`, `game 0x8006E3D4`, `end 0x80038D90` | root `Program.cs` (`0006`) |
| the game's own frame gate and its credit | `func_80017880`, `0x801B6CA8` | `FramePacing` |
| the main loop's stages | 13 stages, gated set `0x80037C0C`, `0x8002A550`, `0x80040348`, `0x80046A60`, `0x8004910C`, `0x80033FBC`, `0x8002DC78` | `FramePacing`, `LoopPacing`, `FullRateLogic`, `FrameProfiler`, `check_gate.py`, `kf2model.py` |
| the renderer (stage 13) | `func_800342D8`, 19 calls, camera block `func_8002E22C` | `Stage13`, `CameraBlock`, `MenuWorld`, `ObjectSmoothing`, `LoopPacing`, the walks |
| the four world tables | creatures `0x8016C544` 200 x `0x7C`, live `u8[+0x9]==1`; objects `0x80177714` 396 x `0x44`, free `u16[+0x6]==0xFF`; effects `0x8019CC6C` 128 x `0x48`, free `u8[+0x0]==0xFF`; sprites `0x80195174` 128 x `0x18`, free `u16[+0x0]==0xFFFF` | `ModelWalk`, `ObjectSmoothing`, `MapMarkers`, `CrashDump`, `SpriteAnim`, `mods/kf2debug` |
| the map | `0x801C8484`, 80 x 80 cells of 10 bytes, two halves; banks `0x8018E18C`, model table `0x8018E19C` (28-byte headers), vertex base `0x8018EAA0`, vertex cache `0x8018EB94` (8 bytes a vertex) | `TileWalk`, `RetainedMap`, `WaterSwell`, `RenderDistance`, `PlanarCull`, `ReflectionReach`, `Map`, `remaster/Identity` |
| light records | `0x801930F0`, 64 used (80 addressed by the remaster) x `0x68` | `PolyAssemblerFog`, `PolyAssemblerLight`, `RetainedMap`, `GpuWorld`, `remaster/Atmosphere` |
| primitive arena and ordering table | descriptor `0x8017E0A4` (`{start,end,current}`, two `0x19000` buffers swapped by `func_8002E064`), OT `0x8018E0A8` | `PrimBuffer`, `DrawCensus`, `FrameCapture`, `ScenePass`, `PlanarWalk` |
| scrolling-texture slots | `0x80192D58`, 8 x `0x18` | `FluidSmoothing`, `Waves`, `Reflections`, `RetainedPlanes`, `remaster/TextureKeys` |
| player and camera | player block from `0x80199414` (position `0x801994EC/F0/F4`, yaw `0x80199506`), camera block `0x80192E18`-`0x80192EA4`, area byte `0x8017E060`, slot `0x8006E5D4` | `Analog`, `Mouse`, `FrameSmoothing`, `AgentBeacon`, `AgentServer`, `AutoReload`, `Map`, `AoWorld` |
| angle convention | 12-bit, `0x1000` a turn, yaw `+0x800` bias in the renderer, pitch clamped `±0x2BC`; strafe along `yaw-0x400`; walk speed `0xC8` | `Analog`, `Mouse`, `ObjectSmoothing`, `ModelWalk` |

Two more facts every C patch already shares and that would go with the table:
how a hook is attached (`HookAttach.OnOverlayLoad`, resolved through
`SymbolRegistry` per overlay), and how a setting is read (`KF2_<FEATURE>` wins
over the saved `kf2.<feature>.*` key).

**The picture switches are the clearest case.** `Perspective`, `Pgxp`,
`Subpixel`, `ZBuffer`, `NoDither`, `TrueColor`, `Anisotropic`,
`AmbientOcclusion` and `EnhancementDistance` hold a switch and a probe; the
mechanism is entirely in the runtime (`0009`-`0014`, `0021`, `0034`-`0036`,
`0040`, `0041`, `0060`, `0083`). With the SDK address table above supplied by
the game, they have no other game coupling. `ZBuffer` is the exception: it also
brackets the arm and model submitters (`func_80032400`, `func_80032588`).

## Bucket A: the RecompOne fork

`tools/RecompOne`, as it stands (upstream `d81dec8` plus the port's `0001`-`0085`).
Phase 1 moves it, unchanged, into a fork repo consumed by subtree.

**It is not yet free of this game.** See "What the fork knows about this game"
below: the port's additions to the runtime read `KF2_*` environment variables by
name and carry some of this renderer's assumptions. None of it needs to change
for Phase 1, which is a move and not a change; it is a list for Phase 2, when a
second game first runs on the fork.

### What the fork knows about this game

The audit is the last section of `docs/SHARING_INVENTORY.md`, with file and line
for each finding. In short:

- **No game code, no game addresses.** Nothing in `0x80010000`-`0x801FFFFF` is
  hardcoded in runtime code, and no executable line names King's Field,
  `KingsField2`, `Verdite` or a serial; those appear only in comments. The
  constants at `0x8000F800`-`0x8000F830` in `LibCd` and `LibDs` are upstream's HLE
  scratch slots in kernel RAM, not this game's (**Confirmed**: the same
  constants are in upstream `d81dec8`).
- **Seven `KF2_*` environment variables are read by name**, every one a
  diagnostic or a comparison: `KF2_CDTRACE` (`PSMemory`), `KF2_GLDEBUG`
  (`GlDebug`), `KF2_GTE_FAST` and `KF2_GTE_LIGHTCACHE` (`Gte`), `KF2_RAM_PROBE`
  (`RamProbe`), `KF2_SWAP` (`HostWindow`) and `KF2_VRAMCHECK` (`VramCheck`). All
  seven are static fields set at type initialisation (five `static readonly`), so a
  prefix the game supplies has to be set before those types are first touched: first thing in the
  game's `Program.cs`, or through an environment variable of the runtime's own.
  Two diagnostic lines print `[KF2]` (`GlTexCache`, `ScreenReflections`).
- **The port's renderer additions carry this game's layout as constants.** The
  mechanism is generic in every case, but these hold only for King's Field:
  - `LibGpu.WalkOTag` draws the retained map when the walk reaches slot 1 and
    treats slot 0 as the sky; `WaterCut` is `4 * (0x1FFF - slot - 0xF0)`, which is
    a 0x2000-entry table with a tile linked at its mean SZ over four plus `0xF0`.
    `GlMainView` repeats it (`TileBias = 0xF0`, `SortBuckets = 0x2000`,
    `z * 0.25f`), as do `RetainedScene.ArmSlot` and `GlModelMeshes`.
  - `RetainedScene`: the 80 x 80 grid of two halves (`HalvesW/H = 160/80`), 8-tile
    chunks, the 2048-unit tile, and 64 light records of 52 ints.
  - `GteDepth`: `AoHeightSpan = 80`, `AoTileUnits = 2048`, `FluidSlots = 8`, and a
    `DepthClearThreshold` of 0 chosen because this game draws one world and a 2D HUD.
    `WaterWaves.MaxRects = 8` has to match `FluidSlots`.
  - `SurfaceMaterial` takes a translucent rect in blend mode 0 or 3 to be water.
  - Tuning in this game's world units: `PlanarReflections.Tolerance` and `Ripple`,
    the AO radius and depth, the wave defaults. These are defaults, not layout,
    and can stay.
- **Settings**: the only keys the port added to the runtime's own sections are
  generic (`ViewConfig.Borderless`, the `DrawSlot` and `AddRightItem` extension
  points, the display-mode strings). The KF2 pages are registered from `patches/`.

None of this blocks Phase 1, which moves the tree unchanged. For Phase 2 it is one
env prefix and one bundle of renderer layout constants (table length, map and sky
slots, tile bias and OTZ divisor, grid, tile size, record shape, fluid slot count,
the water blend modes), supplied by the game. Until the GPU world renderer is
ported to a second game, those constants only need to stay correct for this one.

## Bucket B: game-agnostic infrastructure

Grouped by what each is for. The last column is every game-specific string the
file carries; none of them reads the game's RAM.

### Build, launch, ship

| item | game-specific strings |
|---|---|
| `Verdite2.Launcher/Program.cs`, `Ver.cs`, `Console.cs`, `BuildProgressPopup.cs` | app id `verdite2`, window title `Verdite2 {ver}`, `KingsField2.dll`, `[Verdite2]` console tag, `verdite2.build.*` localisation keys |
| `Verdite2.Launcher/UpdateCheck.cs`, `UpdateBadge.cs` | repo `Voicedrew11/verdite2`, setting `Verdite2.UpdateCheck`, `verdite2.update.*` localisation keys |
| `Verdite2.Launcher/UpdatePopup.cs` | **one piece of game logic**: it waits for overlay `open` or `game` to load before offering an update. The overlay names should come from the game. |
| `Verdite2.Launcher/Build/BuildKey.cs` | the literal `verdite2`; the disc file list (`SYSTEM.CNF`, `SLUS_001.58`, `OPEN.EXE`, `GAME.EXE`, `END.EXE`, `CD/COM/FDAT.T`) |
| `Verdite2.Launcher/Build/DiscCheck.cs` | serial `SLUS-00158`, boot file `SLUS_001.58`, required files and their size floors, the `SLUS-00255` message, `config/kf2.json` (read for the `FDAT.T` floor) |
| `Verdite2.Launcher/Build/GameCompile.cs` | assembly name `KingsField2`; skips `Verdite2` among references |
| `Verdite2.Launcher/Build/Recompile.cs` | `kf2.json`, `kf2.build.json` |
| `Verdite2.Launcher/Build/Paths.cs` | data folder `Verdite2` / `verdite2` |
| `Verdite2.Launcher/Verdite2.Launcher.csproj` | assembly `Verdite2`, icons, the payload globs |
| `packaging/linux/build-appimage.sh`, `packaging/windows/build-windows.ps1`, `verdite2.iss`, `Stub/` | `Verdite2-*` artefact names, `verdite2` icons, the Inno AppId GUID |
| `packaging/shared/` | `verdite2.desktop` (Name, "King's Field", SLUS-00158), the icon art |
| `.github/workflows/ci.yml`, `release.yml` | names; the release body's `SLUS-00255` warning |
| `scripts/release.sh`, `VERSION` | none |
| `scripts/setup_tools.sh` | none beyond paths; rewritten in Phase 1 regardless |
| `mcp/Program.cs`, `KingsField2Mcp.csproj` | **B/C**: the JSON-RPC framing is generic; the tool set is this game's shell vocabulary (`warp <area>`, save slots 1-3, 8192 units ≈ four tiles), `KF2_MCP_ENDPOINT`, `kf2-mcp`, `Kf2.Mcp` |

**The launcher is a shell plus about twenty per-game strings.** Lifted from the
inventory, the record a shared launcher would take from the game:

| datum | KF2 value |
|---|---|
| disc serial; boot file in `SYSTEM.CNF` | `SLUS-00158`; `SLUS_001.58` |
| disc files the recompile reads (and `BuildKey` hashes) | `SYSTEM.CNF`, `SLUS_001.58`, `OPEN.EXE`, `GAME.EXE`, `END.EXE`, `CD/COM/FDAT.T` |
| required-file size floors | each `.EXE` `0x800`; `FDAT.T` from the config's last slice |
| known-wrong discs and their message | `SLUS-00255` |
| recompiler config | `config/kf2.json` |
| game assembly; built DLL | `KingsField2`; `KingsField2.dll` |
| launcher assembly, executable, root namespace | `Verdite2`, `Verdite2.exe`, `Verdite2.Launcher` |
| app id; per-user data folder | `verdite2`; `Verdite2` / `verdite2` |
| env prefixes | `VERDITE2_` (launcher), `KF2_` (game) |
| update repository | `Voicedrew11/verdite2` |
| window title; icons; desktop entry | `Verdite2 {ver}`; `verdite2.png`, `verdite2.ico`; `verdite2.desktop` |
| package names; Inno AppId | `Verdite2-$VERSION-x86_64.AppImage`, `Verdite2-$v-win-x64.zip`, `-setup`; `{9F1F0C1E-…}` |
| overlays that mean "at the title" | `open`, `game` (`UpdatePopup`) |

What `BuildKey` covers, since Phase 2 has to extend it to shared files: the
literal `verdite2`, the launcher's assembly version, the six disc files by
SHA-256, and every `content/src/**/*.cs` and `content/config/**/*.json` by
relative path and SHA-256. Not the LBAs, the commit or the informational
version. **A shared subtree compiled into the game has to land under
`content/src`, or a change to it will not trigger a rebuild.**

What `GameCompile` assumes, which `KingsField2Recomp.csproj` must keep matching:
assembly `KingsField2`, a console application, unsafe on, nullable on, Release
optimisation, AnyCPU, every warning suppressed but CS5001 (an error), references
from `TRUSTED_PLATFORM_ASSEMBLIES` plus loaded assemblies, and a generated
`GlobalUsings.g.cs` of seven usings.

### Patch infrastructure

| item | game-specific strings | note |
|---|---|---|
| `patches/HookAttach.cs` | `[KF2]` log prefix | Every patch depends on it. |
| `patches/Differential.cs` | none | The verify harness behind every `=verify` mode. |
| `patches/settings/PatchSettings.cs` | `kf2.*` keys, the page list | **B with a seam**: `Install` and `SectionNames` hard-code this game's pages. The framework (`IPatchPage`, `Register`, `RegisterSlot`, `Get`/`Set`, `Note`) is generic. |
| `patches/settings/GameplaySection.cs`, `InputSection.cs` | section ids, localisation | `InputSection` replaces the runtime's `input` pane by id. |
| `patches/settings/BindingTable.cs` | none | A copy of an internal RecompOne type, with its SDL `PadLabel` indices duplicated: it will drift silently when the fork changes. A candidate to expose from the fork instead. |
| `patches/HotkeyGate.cs` | references `Remaster.Editor.Open` | Generic policy, one remaster reference. |
| `patches/Prejit.cs` | ranks overlay names `fdat*`, `game`, `main` | |
| `patches/CrashDump.cs` | **C**, not the B the plan guessed | The dump-on-fault mechanism is generic; every table it dumps is this game's. Splits along that line. |
| `patches/DesktopEntry.cs`, `patches/UiScale.cs`, `patches/MouseIndicator.cs` | app id, `KF2_` prefix, `kf2mouseind` panel id | |

### Diagnostics and measurement

| item | game-specific strings |
|---|---|
| `patches/FrameViewerPanel.cs`, `patches/ProfilerPanel.cs`, `patches/GpuFrames.cs`, `patches/TexProbe.cs`, `patches/AudioProbe.cs` | `KF2_` prefix; section names. `FrameViewerPanel` names `FrameCapture`'s slots, so it moves with `FrameCapture`. |
| `patches/RateCensus.cs` | the default RAM window `0x80060000:0x801C0000` |
| `patches/remaster/Snap.cs` | none |
| `patches/FrameProfiler.cs`, `patches/FrameCapture.cs` | **C**: the profiler, capture and replay are generic, but each is seeded with a table of this game's routine addresses (`Known[]`, the slot list). The table is the seam. |

### Picture and sound switches

`AudioQuality`, `Anisotropic`, `AmbientOcclusion`, `TrueColor`,
`EnhancementDistance`, `Perspective`, `Pgxp`: switches and probes over runtime
mechanisms. Their only game coupling is the `DrawOTag` table, where a probe
needs a frame boundary.

### Scripts

| script | bucket | what it hardcodes |
|---|---|---|
| `inspect_disc.py`, `extract_file.py`, `add_call_targets.py` | B | docstring examples only |
| `merge_branch_spans.py` | B | `disc/KingsField2.cue` and `config/kf2.json` as defaults: should be flags |
| `merge_sdk_names.py` | B | `OVERLAYS = ("open", "game", "end")` |
| `callgraph.py` | B | the generated class prefix `KingsField2(_\w+)?`, `generated/`, `config/funcmaps/` |
| `kf2run.py` | B | `KingsField2Recomp.csproj`, `bin/Release/net10.0/KingsField2`, `disc/KingsField2.cue` |
| `profile_report.py`, `audio_spectrum.py` | B | none of substance |
| `match_overlays.py` | C | a 17-entry libgpu seed table in `open`, the overlay names; the delta algorithm is generic |
| `find_writers.py`, `check_gate.py`, `rate_census.py`, `rate_matrix.py` | C | the main-loop model through `kf2model.py`; `rate_*` add RAM addresses (object table, death counter, needle) and scenario drivers |
| `light_probe.c`, `shader_probe.c` | C | the shader's uniform and material-table contract, which is the fork's (`0048`, `0067`, `0071`), not the game's |
| `kf2model.py` | D | the keystone: main loop address, stage labels, gate source, generated class. **A per-game JSON for it turns `check_gate.py`, `find_writers.py` and `rate_census.py` into B.** |
| `msg_glyphs.py` | D | the message archive, its ids, the glyph grid, the output class |

## Bucket C: patches whose mechanism might generalize

Clusters, because the patches share data and depend on each other: sharing goes
along this graph, not file by file. The table under "The finding that shapes
everything else" holds the addresses; this section is what each cluster
assumes beyond them.

**Frame pacing and the tick** (`FramePacing`, `LoopPacing`, `LoadPacing`,
`MenuPacing`, `SpriteAnim`, `TintHold`, `FullRateLogic`).
The mechanism (a fixed-step logic clock, a per-stage gate, a frame boundary at
a `DrawOTag` that follows a `VSync`, an absolute pacing deadline, the 500 ms
watchdog, pause as the gate held shut) is generic. The **control-flow model is
not**: KF2 is a loop of thirteen stage functions, stage 13 draws, stages 2-6 and
two of stage 13's sub-steps are world state, and stages 2 and 3 reach stage 13
only through their own modal loops (the two recorded exceptions in
`check_gate.py`). The world runs at 20 ticks a second because the game's speed is its
frame rate and it landed in the three-vblank band on the console (see "The
reference band is 3 vblanks, not 2" in `docs/PATCHES_AND_MODS.md`); its own gate
only forbids faster than two vblanks. AI runs one entity in four. A second game's loop has
to be drawn out stage by stage before any of this transfers, and it may not be
a stage loop at all. `FramePacing`'s full model is in its inventory entry. The
plan already expects this cluster to stay per-game; that is the likely outcome.

**Smoothing** (`FrameSmoothing`, `ObjectSmoothing`, `AnimSmoothing`,
`FluidSmoothing`). Interpolating between two ticks is generic; what is carried is
this game's camera block, its four tables, its MO clip format and its texture
slots. `AnimSmoothing` is the deepest coupling in the repository: it parses the
MO clip table (`bank+0x10`, segment durations) and reads the caller's stack
words (`SP+0x1C/0x20/0x10`) of `func_80034DA8`.

**The renderer taken over in C#** (`Stage13`, `CameraBlock`, `TileWalk`,
`ModelWalk`, `MoPose`, `PolyAssembler*`, `CullGrid`, `CullCone`, `ViewClip`,
`ScenePass`, `PrimBuffer`). Each is a C# transcription of a KF2 routine with a
`verify` mode against the recompiled one. The technique (replace hook, verify
by `Differential`, direct-RAM fast path, GTE save and restore) is generic; the
routines are not, and a sibling game's renderer is a fresh transcription. The
clean extraction boundary is `PolyAssemblerDepth`, `PolyAssemblerLight` and
`PolyAssemblerTexRect`: they fill the runtime's `GtePacketDepth`, `GteLightMap`
and `GteTexRect` records, and those record structs are the interface any game's
assemblers would fill.

**The GPU world renderer and the reflections** (`GpuWorld`, `GpuWorldCensus`,
`RetainedMap`, `RetainedModels`, `RetainedPlanes`, `PlanarWalk`, `PlanarCull`,
`ReflectionReach`, `Reflections`, `RenderDistance`, `Murk`, `Waves`,
`WaterSwell`, `EvenFog`, `PerPixelLighting`). The engine is in the fork
(`0067`, `0068`, `0072`, `0078`, `0085`); these files decode this game's map,
banks, model table and light records into it. `PlanarWalk`, `PlanarCull` and
`ReflectionReach` move as a unit or not at all; `PrimBuffer` is their
prerequisite. "Which VRAM rects are water" is a KF2 fact (the fluid slots) that
several of them assume.

**Input** (`Analog`, `Mouse`, `KeyLayout`, `MenuMouse`). `Analog` writes
velocities into the player block and folds the pad mask through this game's
table; `Mouse` rides `Analog`'s hook and speaks the 12-bit angle. `KeyLayout`'s
machinery (defaults before `ConfigManager.Load`, versioned migration,
`Superseded`) is generic, and only its key map is KF2's. `MenuMouse` needs this
game's menu routines and layout tables.

**Menus and messages** (`MenuWorld`, `MenuDraw`, `MenuPacing`, `MenuMouse`,
`GearCompare`, `MessageText`). One menu API with six routines (`func_80022754`
enter, `func_80022530` frame head, `func_800226A8` presenter, `func_800228C8`
leave, `func_80018E80` modal loop, `func_80022E58` pad read), each patch hooking
a different part. A sibling game re-identifies the API; nothing here is generic
but the ideas.

**The map** (`Map`, `MapFog`, `MapRender`, `MapPanel`, `MapFullscreen`,
`MapOverlay`; `MapMarkers` is D). The three ImGui viewports are generic shells
over one model; `Map` is the only reader of the grid and `MapMarkers` the only
reader of the tables. `Map` and `MapMarkers` never hook and never write: they
read during the game's own `VSync`, the most portable shape in the repository.
The `.fog` file is `"KF2FOG\0"`, version 1, a u16 count, then records of
`(slot u8, area u8, 800-byte bitset)`, written beside the memory card as
`carda.fog` through a `.tmp` and a rename. The container is generic; its record
size and keys are this game's 80 x 80 grid, its areas and its save slots.

**Driving and observing the game** (`AgentServer`, `AgentBeacon`, `AutoStart`,
`AutoReload`, `BlackProbe`, `CrossProbe`, `DrawCensus`, `AnalogProbe`,
`PacketMatch`). `AgentServer`'s transport and two-queue marshal onto the game
thread are generic; almost every verb's body is KF2 state.

**Sound** (`PositionalAudio`). The spatial mix is in the fork (`0044`); the
listener (`0x80198584`), the sound routine (`func_80013D08`) and stage 9 are
KF2's.

**The remaster** (`patches/remaster/`). The pack system (upstream's asset pack
plus a `remaster/` directory: `materials.json`, `surfaces.json`, `lights.json`,
`atmosphere.json`, `level.json`, `props.json`, `layers.json`), the editor, the
shell verbs and the measurement tools are generic in shape. **`Identity` and
`TileField` are the seam**: every key is a KF2 coordinate (`TileKey(area, x, z,
half)`, `ModelKey(area, kind, model)`, light records 0-79) and the area
fingerprint is an FNV-1a 64 over this game's 64,000-byte tile block, taken at
`func_80017244`. Only `Identity` (`0x80017244`), `Lights` (the `DrawOTag` table)
and `Atmosphere` (stage 1, `PutDrawEnv`) hook code. `Host`'s feature list
(`Surfaces`, `Lights`, `Atmosphere`, `Level`, `Props`) is the extension point.

## Bucket D: KF2-only

`AreaWarp`, `BootExe`, `EndingHold` (`END.EXE`'s spin and the boot stub's loader
at `0x80010254`/`0x80010268`), `CardIcon` (this disc's card-icon offsets),
`HitGuard` (`fdat23`'s final-boss fault), `MessageGlyphs` (a generated font hash;
a sibling regenerates its own), `MapMarkers`, `remaster/TileField`,
`remaster/Level`, `remaster/Props` (an object record above 2 MB through
`ModelWalk`), `remaster/SaveCheck` (`0x8006E98C`, `func_80049A88`),
`config/kf2.json` (63 SDK bindings by address, per overlay), `config/funcmaps/`
(13 maps, 2,225 functions over the boot stub, the three executables and the
`fdat` modules), `docs/GAME_INTERNALS.md`, `mods/kf2debug` (about 40 RAM
addresses and 25 routines), `scripts/kf2model.py`, `scripts/msg_glyphs.py`, and
the root `Program.cs`.

**`Program.cs` is the composition root and stays per-game.** It holds the 74
`Install()` calls, the IRQ table per overlay and the diagnostics, and reads 223
distinct `KF2_*` names itself (the patches and mods read another 80; the two
sets overlap). Each game writes its own.

`AreaWarp` is borderline: warping by the area loader is a general idea, but
nothing in it survives the change of game.

## What is game-specific in the infrastructure

- **The `KF2_` prefix.** `docs/ENV_VARS.md` names 244 distinct `KF2_*` variables
  (the plan's 216 was an earlier count); `Program.cs` reads 223 and
  `patches/` plus `mods/` 80. Shared code must take the prefix from the game, and
  every name must survive unchanged in Verdite2.
- **The `KingsField2` names.** Assembly `KingsField2`, project
  `KingsField2Recomp.csproj`, the generated classes `Recompiled.KingsField2_game`,
  `_open`, `_end`, `_fdat`, `_main`, the process name `KingsField2` (which
  `pkill` and `dotnet-stack` find), `KingsField2Mcp`.
- **The namespace `Kf2`** (patches), `Kf2.Mcp`, `Kf2.Mods.*`, and the
  `Verdite2.Launcher` namespace.
- **Settings keys** are `kf2.<feature>.<name>` in `interface.ini`, plus the
  launcher's `Verdite2.UpdateCheck`. Localisation keys: `verdite2.build.*`,
  `verdite2.update.*`, `settings.gameplay`, `settings.display`, and `kf2.*` keys
  in the distance, even-fog, fast-geometry, reflections, remaster and Z-buffer
  pages, each in en, pt-BR and es-419.
- **Disc identity**: `SLUS-00158`, `SLUS_001.58`, the `SLUS-00255` refusal
  (`DiscCheck`, `release.yml`), `disc/KingsField2.cue` (scripts, docs, the MCP
  instructions).
- **The command channel**: port `27900`, the shell's verbs.
- **The overlay names** `open`, `game`, `end`, `fdat*`, `main`, in `Prejit`,
  `UpdatePopup`, `merge_sdk_names.py`, every hook table.

### What a new repo's `.gitignore` must copy

`disc/*` (with `!disc/README.md`), `generated/`, `bin/` and `obj/`,
`tools/RecompOne/**/bin/` and `**/obj/`, the PSY-Q signature bank
(`…/AutoConfigure/signatures/psyq.json`), `*.sav`, `settings.json`,
`interface.ini`, `*.fog` and `*.fog.tmp`, `packs/`, `exports/`, `dump/`,
`mods/.cache/`, `scratch/`, `*.log`, `ratecensus.txt`, `dist/`, `__pycache__/`
and `*.pyc`. The ones that hold copyrighted or derived data are `disc/`,
`generated/`, `dump/` (the texture dumper writes the disc's art), `packs/` and
`exports/` (they can carry it), and the saves. `tools/RecompOne.git/` goes away
in Phase 1.

### What mods can see

The two mods in the tree reach the game through `Recompiled.KingsField2_game`
directly (`func_80048178`, `func_8002C3A8`, `func_80024CAC`, `func_800244CC`),
`Kf2.AreaWarp` (`Areas`, `CutArea`, `TryRun`), `Kf2.MouseIndicator.Suppressed`
and `Kf2.Settings.PatchSettings`; everything else they use is RecompOne's.
Third-party mods can reach any public member, and nearly every patch exposes
`Enabled`, `Configure`, `Install` and `SetEnabled`, so the safe assumption is that
**every public type under `Kf2` is mod-visible**. Moving one needs a forwarding
type or a wrapper under the old name. The inventory lists each file's public
surface.

## Corrections to the plan's guesses

- `CrashDump` is C, not B: its tables are this game's.
- `FrameProfiler` and `FrameCapture` (with `FrameViewerPanel`) are C: each is
  seeded with a table of this game's routines.
- `mcp/` is B/C: the transport is generic, the tool vocabulary is this game's.
- `UpdatePopup` carries one piece of game logic (the overlay names that mean "at
  the title").
- `Perspective` and `Pgxp` are nearly B: the mechanism is all in the fork.
- `BindingTable` copies an internal fork type and will drift; exposing it from
  the fork would be cleaner than sharing the copy.
- The env var count is 244 names in `docs/ENV_VARS.md`, not 216.

## Progress log

### 2026-10-02: Phase 0, the inventory

**Done.** `docs/SHARING_PLAN.md` (the brief) and this file, with the per-file
inventory in `docs/SHARING_INVENTORY.md`. No code changed. The inventory was made
by reading each file and checked against grep for addresses, env vars and
overlay names. The table entries for `ModelWalk`'s four tables, `Perspective`'s
hooks and the scripts' hardcodes were read against the source.

**Shared repos and pins.** None exist yet. Verdite2 vendors `tools/RecompOne` at
upstream `d81dec8` plus `0001`-`0085`.

**Measured.** Nothing: Phase 0 makes no change to measure.

**Needs your eyes.** This document, which is Phase 0's gate.

**Open, for Phase 1's gate.** The release that has shipped and is tagged is
`v0.3.3` (`VERSION` 0.3.3). `v0.4.0-staging` carries 54 commits not on `main`,
and this branch (`vendor-veditecore`) starts from it. The plan's gate needs the
current release shipped and no unreleased staged work. **Decided (2026-10-02):**
staging is settled enough to build on, so Phase 1 starts from `v0.4.0-staging`
without waiting for `v0.4.0` to ship; anything that lands on staging later is
rebased in.

**Decided (2026-10-02), for Phase 1.** The fork is a **standalone** repo (not a
network-linked GitHub fork of `BlackLabelHQ/RecompOne`, so nothing offers a
one-click PR upstream) named **`Voicedrew11/verdite-recompone`**. It does not
exist yet; Phase 1 creates it, with your go-ahead. **Verdite Core is
`Voicedrew11/verdite_core`** (underscore), which already exists and is empty; it
stays for Phase 2, apart from the fork, as the plan says.

**Seen on GitHub, to confirm in Phase 2.** The repo descriptions name the discs:
`verdite1` is King's Field `SLPS-00017`, `verdite3` is King's Field II
`SLUS-00255`, and there is a `verdite4` for King's Field: The Ancient City
(`SLUS-20318`, a PS2 game, so not on RecompOne). `verditeST` exists and is empty.
Phase 2 still asks before taking any of these as the target.

**Decided (2026-10-02), for Phase 2.** Only one game is brought up at the start:
King's Field III in the Japanese numbering, **Verdite3**. Verdite1 waits. Which
disc Verdite3 targets (the US `SLUS-00255`, sold as "King's Field II", or the
Japanese release) is still to be asked when Phase 2 starts.

**Open, for Phase 2.** The fork reads seven `KF2_*` names itself and carries this
game's ordering-table and grid layout in its renderer additions (see "What the
fork knows about this game"). Phase 1 moves it unchanged; the first game after
this one will need the prefix, and (once the GPU world renderer is ported) the
layout, to come from the game.

**Corrected on review.** Three things the inventory's first draft got wrong, fixed
in `docs/SHARING_INVENTORY.md`: the `LibCd`/`LibDs` scratch addresses are
upstream's and not this game's link; `FramePacing`'s tick contract is a patch
(C), not runtime infrastructure; and the settings pane `InputSection` replaces
lives in the vendored tree, not a gitignored checkout.
