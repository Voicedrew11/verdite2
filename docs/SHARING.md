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
| `inspect_disc.py`, `extract_file.py`, `add_call_targets.py` | B | docstring examples only; **moved to Verdite Core in Phase 2** |
| `merge_branch_spans.py` | B | `disc/KingsField2.cue` and `config/kf2.json` as defaults: now from `config/verdite.json`; **moved to Verdite Core in Phase 2** |
| `merge_sdk_names.py` | B | `OVERLAYS = ("open", "game", "end")`, now `sdkOverlays` in `config/verdite.json`; **moved to Verdite Core in Phase 2** |
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
`Voicedrew11/verdite-core`** (renamed from `verdite_core` on 2026-10-02), which
already exists and is empty; it stays for Phase 2, apart from the fork, as the
plan says.

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

### 2026-10-02: Phase 1, the fork extracted

**Done, locally.** The fork's history is built and Verdite2 takes `tools/RecompOne`
from it as a `git subtree --squash`. The working clone of the fork is
`~/Desktop/verdite-recompone` (branch `main`, remote `upstream` =
`BlackLabelHQ/RecompOne`, `origin` = `Voicedrew11/verdite-recompone`).

**How the history was built: the preferred approach, and it worked.** `git subtree
split --prefix=tools/RecompOne` of this branch gives 89 commits with one root (the
vendoring import, `7c198b5` here) and one merge of our own. Two grafts, baked in
with `git replace --graft` and `git filter-branch` over the split range only (a
whole-history rewrite re-hashed upstream's signed merge commits too and moved the
merge base, so the range is `main ^upstream/master`): the import onto upstream
`0409bc2` (the `UPSTREAM` it was vendored at), and the `d81dec8` harvest
(`1138329` here) as a real merge with upstream `d81dec8` as its second parent.
That was the only other commit that changed `UPSTREAM`. Checked:
`git merge-base main upstream/master` is `d81dec8`, the `UPSTREAM` sha; the
fork's `07f6537` has the tree of this branch's `tools/RecompOne` at `ea10fb6`
(`dd9c56b`), exactly.

**What upstream tracks that the fork does not.** Two files, both deliberate:
`RecompOne.Recompiler/AutoConfigure/signatures/psyq.json` (15.7 MB; gitignored
here, now gitignored in the fork too) and
`RecompOne.Runtime/Host/Window/Assets/NotoSansCJK-Regular.otf` (16.5 MB; left out
by `0033`, see `FontSet.cs`). The fork tracks 33 files upstream lacks: `UPSTREAM`
and the port's `.cs` additions.

**The fork's commits.** `07f6537` is the move (the tree above). `bbbf56b` adds what
is the fork's own: a fork section at the top of `README.md` (what it is, no
pull requests or issues upstream, the two files left out; upstream's `LICENSE`
untouched), `psyq.json` in its `.gitignore` (its `bin/`/`obj/` lines were already
there), and `harvest_upstream.sh`, which replaces `--sync-upstream` and carries
its acceptance-check text word for word.

**Verdite2's commits on `vendor-verditecore`.** `f4ee759` removes the tracked
tree; `8698a4d` adds it back as a subtree of `07f6537` (squash `b64c50f`).
`git diff ea10fb6 8698a4d` is **empty**, the whole repository and not only
`tools/RecompOne`. `50ac62e` is the game side: `setup_tools.sh`, `.gitignore`,
the docs, the CI and packaging wording. `8c000de` pulls `bbbf56b` through the new
`--pull-fork` (squash `6819790`); after it the subtree's tree equals the fork's
`main`. `tools/RecompOne.git/` is out of the repository: moved to
`~/Desktop/RecompOne.git.old`, not deleted, because its `kf2` branches (the
39-patch stack as commits) exist nowhere else. Delete it when you like.

**Pins.** Verdite2 is at fork `bbbf56b`. No other game exists yet.

**`--squash`, from the round-trip test.** With squash, push, pull and a second
push each came out as one linear commit (see "Why `--squash`" in
`RECOMPONE_FORK.md`). Full history would put the fork's 16 MiB pack, nearly all
of it upstream's, into every game. **The first push carried this repository's
whole history into the fork**: `git subtree split` maps the `git rm` commit to
itself. The add commit now carries `git-subtree-mainline`/`git-subtree-split`
trailers, which fixed it; the test branches were deleted and the fork's objects
pruned (`f4ee759` is not in it). See "The add commit says where the split
starts" in `RECOMPONE_FORK.md`. `--push-fork` also refuses a commit that touches
`tools/RecompOne` and anything else.

**Measured.**
- **Hashes.** As built, all four assemblies differ before and after, and **only
  because the commit is stamped into them**: the SDK writes `1.0.0+<HEAD sha>`
  into the informational version and SourceLink writes it into the PDB, whose
  id and checksum the DLL records (72 bytes differ, same size); the launcher
  also writes `git rev-parse` into its own version on purpose (`VERDITE2_BUILD`).
  Built with that held fixed
  (`-p:IncludeSourceRevisionInInformationalVersion=false
  -p:EnableSourceControlManagerQueries=false -p:VERDITE2_BUILD=pinned`), `ea10fb6`
  and the final HEAD give the same bytes: `RecompOne.Runtime.dll` `11acf1a5…`,
  `recompone.dll` `0bb5abaa…`, `KingsField2.dll` `8e83a91a…`, `Verdite2.dll`
  `a53e5769…`. Plain builds before, for the record: `91fe4123…`, `83581ff6…`,
  `3720d153…`, `a7fb546d…`.
- **Fresh clone.** Cloned with `origin` removed and `VERDITE_FORK_URL` pointed at
  an unresolvable host: `setup_tools.sh` and the launcher build, 0 errors.
- **Packaging.** `packaging/linux/build-appimage.sh` makes
  `Verdite2-0.3.3-x86_64.AppImage` (44 MB).
- **Acceptance.** `open → game → fdat02 → fdat05`, slot 2 at hp 46/86 in area 1,
  144.0 fps drawn at 19.9-20.8 ticks/s, `[present] wide 288, plain 0, vram
  fallback 0`, the vertex map at 18720 caught/s and 100.0% hit (standing, not
  moving: the scripted walk did not move the player), pacing at 15 hooks with the
  boundary 3/3 and 7/7 stages, no exceptions; `scripts/check_gate.py` 0
  violations.

**Needs your eyes.** The picture in a normal run, the widescreen margin's clear
in particular. The binaries are the same bytes, so this is only the plan's
belt-and-braces check.

**Pushed (2026-10-02, with your go-ahead).** The fork's `main` (`bbbf56b`) to
`Voicedrew11/verdite-recompone`, a standalone repo; then `bash
scripts/setup_tools.sh --pull-fork` here, from GitHub, reported the subtree
already at `bbbf56b`. This branch, `vendor-verditecore`, to Verdite2's origin. Not
merged to `main`.

**Open.**
- `patches/recompone/*.patch` and `docs/RECOMPONE_PATCHES.md` stayed here. They
  belong in the fork, in Phase 2: the fork's own source names these patch
  numbers, and points at this repo's `docs/` in 9 places, and a second game's
  copy would cite a record it does not have. Phase 1 is a move, so they did not
  go now.
- `git subtree` is a separate package on Fedora (`git-subtree`), so a new machine
  needs it before `--pull-fork`/`--push-fork`. The build does not need it.

### 2026-10-02: Phase 2 begun, Verdite3 laid out

**Decided (2026-10-02).** Phase 1 lands in `v0.4.0-staging`, which is
fast-forwarded to `vendor-verditecore` (locally; not pushed). Verdite3 targets
**`SLUS-00255`** (the US "King's Field II", the Japanese *King's Field III*).
Its names: assembly `KingsField3`, env prefix `KF3_`, MCP project
`KingsField3Mcp`, disc `disc/KingsField3.cue`. Its working clone is
`~/Desktop/verdite3`. Verdite Core is subtree'd at **`tools/verdite-core`** in
every game, beside `tools/RecompOne`. The patch record moves into the fork.

**The patch record is the fork's.** Fork `a617cf8` adds `patches/` (the 85
diffs and `assets/`) and `docs/RECOMPONE_PATCHES.md`, with a note that the other
`docs/` it names are Verdite2's. Here: `fd1d200` pulls it, `2852ed5` deletes
`patches/recompone/` and `docs/RECOMPONE_PATCHES.md` and repoints 43 files at
`tools/RecompOne/patches/` and `tools/RecompOne/docs/RECOMPONE_PATCHES.md`
(source comments, docs, and the AppImage and Windows scripts, which ship
`NotoSans-OFL.txt` from there). Sentences that describe the old replay keep the
old path, since they describe history.

**Verdite Core started.** `Voicedrew11/verdite-core` `536167a` (local clone
`~/Desktop/verdite-core`): `LICENSE` (Verdite2's MIT), a README, and the five
scripts a bring-up uses, `inspect_disc`, `extract_file`, `add_call_targets`,
`merge_branch_spans` and `merge_sdk_names`, plus `verdite_game.py`, which finds
the game's root (`VERDITE_GAME_ROOT`, else the first directory up holding
`config/verdite.json`) and reads it. What became game input: the disc path,
the recompiler config (`merge_branch_spans` now resolves each `funcMap`
relative to that config's directory, as the recompiler does, not `REPO/config`),
the funcmap directory, and `merge_sdk_names`' overlay list (`sdkOverlays`).
`inspect_disc` and `extract_file` need no config, so they run in a game that has
none yet. Here: `08447b9` adds the subtree (squash `2254b03`); `f803010` makes
`scripts/<name>.py` a wrapper of each that runs the shared one with
`VERDITE_GAME_ROOT` set, adds `config/verdite.json`, and points
`match_overlays.py` and `msg_glyphs.py` at the shared modules. `setup_tools.sh`
gained `--pull-core`/`--push-core` (`9931313`), the URL defined beside the
fork's.

**Verdite3 laid out** (`~/Desktop/verdite3`, branch `main`, local only):
`4f809d6` adds `tools/RecompOne` at fork `a617cf8` (its tree equals Verdite2's
copy exactly), `bb1290a` the skeleton (`.gitignore` with every protection
Verdite2's has, `README.md`, `AGENTS.md`, `NOTES.md`, `disc/README.md`, and
`docs/` with `DEVELOPMENT`, `RECOMPILATION`, `GAME_INTERNALS`, `ENV_VARS` and
`TODO`, which holds the bring-up order), `4a3f4af` adds `tools/verdite-core` at
`536167a`, and `d9c34bf` its `config/verdite.json` (`sdkOverlays` empty until
the disc says which executables link PSY-Q). Neither game ever had these
prefixes, so the subtree adds needed no `mainline` trailers: `git subtree split`
in Verdite3 gives exactly `a617cf8` and `536167a`, and in Verdite2 `536167a`.

**Pins.** Verdite2 and Verdite3: fork `a617cf8`, Verdite Core `536167a`.

**Pushed (2026-10-02, with your go-ahead).** The fork's `main` (`a617cf8`), Verdite
Core's first `main` (`536167a`), Verdite3's `main`, and here `vendor-verditecore`
and `v0.4.0-staging` (both `d03ac2c`, fast-forwards).

**Measured.**
- Old against new script, on this disc, from the repo root: `inspect_disc` (45
  lines), `extract_file --header-only GAME.EXE`, a full `OPEN.EXE` extract
  (`cmp`), `merge_branch_spans --dry-run` (all overlays, and `fdat17`; the same
  again from `docs/`), `add_call_targets` on a copy of `open.json` (the map
  written is identical; the one line differing is the scratch path it names),
  `merge_sdk_names` against copies of the maps with a synthetic autoconfigure
  directory (162 renames, the same maps), and `match_overlays --libgpu` (60
  lines): **identical**. `add_call_targets --help` differs only in its
  docstring's example disc name. `msg_glyphs` stops at the missing `tesseract`
  binary in both versions, so only its imports are shown to resolve. Nothing
  wrote to `config/funcmaps`.
- `--push-fork` against a scratch bare copy of the fork: "Everything up-to-date",
  so the split from this branch is `a617cf8` and carries no game commit.
- `dotnet build KingsField2Recomp.csproj -c Release`: 0 errors;
  `packaging/linux/build-appimage.sh`: exit 0. The game was not run: the only
  runtime change is comments and the patch record's location.

**Needs your eyes.** Nothing on screen. Verdite3's `README.md`, `AGENTS.md`
and `NOTES.md` are new prose.

**Open.**
- **Verdite3's disc is in place and read** (`disc/KingsField3.cue`, volume
  `SLUS-00255`; Verdite3 `6797f9f`, "What is on the disc" in its
  `docs/RECOMPILATION.md`). It has Verdite2's shape: a 4 KiB boot stub and
  `OPEN`/`GAME`/`END.EXE`, all at `0x80011000`; `GAME.EXE` is 0x8B800 bytes of
  text against 0x5E000 here. Next, in Verdite3: `config/kf3.json`, the sweep,
  the signature bank, recompile, boot. The launcher, packaging and CI move into
  Verdite Core only once Verdite3 needs them.
- `~/Desktop/verdite1`'s `kf1-port` branch was an experiment, not Verdite1's
  start; ignore it. Verdite1 still waits.
- `docs/RUNTIME.md` still opens by saying `tools/RecompOne/` is gitignored, which
  has been untrue since the vendoring. It predates this phase.

### 2026-10-02: Verdite3 boots, plays, saves and loads

**Done when, met** (the picture is yours to confirm). Verdite3 (`~/Desktop/verdite3`,
`main`, local) recompiles `SLUS-00255` and runs it from boot through
`OPEN.EXE`'s intro and title, the memory card screen, into `GAME.EXE` and an
area, between areas, and saves to and loads from card A, in game and from the
title. Its acceptance test is "The acceptance test" in its `docs/DEVELOPMENT.md`.
Commits there: `acf4873` (Verdite Core, below), `9b137e8` (the recompile:
`config/kf3.json`, the maps, `KingsField3Recomp.csproj`, `Program.cs`,
`scripts/setup_tools.sh` ported from here), `a17c0fa` (docs).

**Pins.** Verdite3: fork `a617cf8` (unchanged, no fork commit was needed),
Verdite Core `acf4873`, which is `536167a` plus one commit, **not yet pushed**.
Verdite2: fork `a617cf8`, Verdite Core `536167a`; nothing here changed but this
entry.

**What carried over from Verdite2, and what did not.**
- The disc has this disc's shape: three executables at `0x80011000` as overlays,
  per-area code in `CD/COM/FDAT.T` at entries `3n+2` (28 modules, all linked for
  `0x801E8308`, a module pointer at `0x8018FAE0` dispatched through slot 8),
  empty entry groups where areas were cut. The techniques (scoring a base by its
  slot targets, the overlay delta, data-side search, constants as evidence)
  all worked unchanged.
- **The PSY-Q libraries are a newer build.** Not one of the routines identified
  here matches there by `match_overlays`' normal form, not even the libcd thunks,
  so every address was found again: 516 names from the signature bank, the
  rest by hand. `CdControl`/`F`/`B` are full functions; `CdRead` is a retry
  wrapper; libapi is the 4.x interrupt manager, whose callback table is
  `intrEnv + 4` (the layout upstream's fallback assumes; `Program.cs` sets it per
  executable anyway). The same 21 entry points are bound, 63 in all, and the
  recompiler reports `applied 63 patches, 0 reimplementations`.
- **The executables' text runs on into data, and the sweep makes functions of
  it.** In `GAME.EXE` the data "functions" branch back into real code. The maps
  are cut at the end of code before the `jal` harvest.

**Verdite Core `acf4873`: three fixes to `merge_branch_spans`**, all found on
Verdite3's `GAME.EXE`, where the old script would have merged half the
executable into one function: a switch table is bounded by the `sltiu` guarding
its index (a 17-entry table read on into a data pointer); `jal`s are counted
only from inside known functions (the rule `add_call_targets` already had); an
absorbed start reached by fallthrough is not reported as lost. **Measured on
this repo:** the old and new script, run from Verdite2's pre-merge maps
(`e41f53d^`) with `e41f53d`'s config, print the same output and write the same
maps, which equal the ones `e41f53d` committed; `--dry-run` on today's maps is
identical, all overlays and `fdat17` from `docs/`.

**Measured** (Verdite3): the overlay sequence `open` → `game` → `fdat02` →
`fdat14` in the log, each executable's callback table set as it loads, the title
load as `game` → `open` → `game`; `carda.sav` holding `BASLUS-002551`, 3 blocks,
titled `KING'S FIELD 2-1 EXP 0 LV 1`; no `unmapped call`.

**Needs your eyes.** You played it: intro, title, the memory card screen, the
short video, the first area and the next, a save and two loads. Nothing more is
outstanding for the done condition. The movies were seen only in passing, and
`END.EXE` has not been reached.

**Open.**
- **Push** Verdite Core `acf4873` (from Verdite3, `setup_tools.sh --push-core`)
  and Verdite3's `main`. Waiting on your go-ahead. Verdite2 pulls the new Verdite
  Core only when you decide; it changes no output here.
- **The world runs at 60**, once per drawn frame. A world clock like
  `FramePacing` needs this game's frame gate and stages found first.
- **The fork reads seven `KF2_*` switches by name.** Verdite3 inherits them under
  those names. Taking the prefix from the game is the first fork change Verdite3
  will want; it must keep this repo's acceptance test passing.
- Verdite3's acceptance test still needs a person at every step: it has no
  scripted pad, auto start or state beacon yet. Those (`KF2_AUTOPAD`,
  `KF2_AUTOSTART`, `KF2_AGENT` here) are the Phase 3 candidates that would make
  it a program.
- Launcher, packaging and CI stay out of Verdite Core: Verdite3 does not need
  them yet.
