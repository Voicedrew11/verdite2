# The remaster: authoring tools first, effects second

**A design document, and from Phase 1 on a record of the work done against it.**
The first slice of Phase 1 is in (see "Phase 1, the first slice" under the
roadmap); everything else is still design. The
stretch goal is a full visual remaster the user authors themselves: placing lights,
assigning materials, tuning reflections, editing levels. **An effect is worth
nothing to that goal until it can be placed, tuned, saved and shared**, so this
document plans the authoring tools and the data they write first, and treats each
rendering feature as something those tools drive.

Every claim about the existing port is marked the way the rest of `docs/` marks
them: **Confirmed** (measured, or read from the code), **Inferred** (fits the
evidence, not tested) and **Open** (not known). Every planned feature keeps
"mechanism measured" apart from "picture judged". Features ship **off**, and off
must be bit-identical to the picture the port draws today.

The request this answers arrived with some lines cut off. It was read as asking
for:
- each phase's measurement that proves it works;
- the cost of each feature, on and off;
- the remaster being off by default;
- the seams between the layers;
- versioning, and how a pack layers over the base game and over other packs;
- in-game or external editing;
- driving the game with `KF2_SHELL`, `goto`, a free camera and pause;
- forward or deferred lighting.

If one of those readings is wrong, the section built on it is the one to revisit.

## What this is, and what it is not

**"Full visual remaster" needs a target before it needs a renderer.** Two
remasters could come out of this port. One keeps the game's look and adds light,
material and surface detail to it. The other replaces the art. The tools are the
same for both, but the order of the phases is not, and neither is what "done"
means. **Recommendation:** take one area from start to finish before spreading
across all of them. `fdat02` is the obvious one:
- it is where `KF2_AUTOSTART=new` stands, so the spawn is fixed;
- it has water, so the reflection passes were measured there;
- the SSR and planar readbacks already have numbers for it.

Area 1 with save slot 2 (`KF2_AUTOSTART=2`) is the second test scene, as it is
for everything else.

**What this document deliberately does not do:**
- It does not design a new renderer. Everything here extends the passes the port
  already has.
- It does not replace creature meshes.
- It does not add walkable geometry beyond what the tile grid can already express.

Each of those gets a paragraph below saying why.

## What exists, and what each piece gives the remaster

**Most of the machinery a remaster needs is already in the tree, because the
port learned to name its geometry before it had a reason to.** The C# assemblers
know which routine built each packet. The two walks know which tile and which
model. The surface buffer already stores a material per pixel. What is missing
is anything authored, and anything that remembers it.

| piece | what it is | what it gives the remaster |
|---|---|---|
| `Gpu/SurfaceMaterial.cs` (`0067`) | a per-pixel material id stored in the surface buffer's alpha, 8 slots, with a `Reflectivity`/`F0` table the port fills | **The material channel.** The id already reaches the reflection pass, and "What a surface is made of" in `RENDERING.md` names lighting as its next reader. Eight ids is too few to author with, so widening it is an amendment to `0067` |
| `GtePacketDepth` (`0050`) | per-packet corner depths, `Solid` and **`Material`**, written by the C# assemblers | **Where authored per-tile and per-model materials are written.** `SurfaceMaterial.Classify` already prefers the packet's material. **Confirmed**: `PolyAssemblerDepth.SealDepth` is the one place a record is sealed, and it never sets `Material` |
| `GteLightMap` (`0048`) | per-corner lit colour or light dots and the raw depth cue; `BK`/`LCM` by generation | **The chain authored lights join.** `shade8` rebuilds the game's colour per pixel from these inputs; an authored light adds a term to it |
| `GteTexRect` (`0060`) | the texture rectangle of a face, for clipped fans | **A stable rectangle for a texture key**, where a clipped fan's own UVs would give a different rectangle every frame |
| `patches/TileWalk.cs` | the 24×24 cell sweep, a cell's two halves, one half set up and assembled | **Tiles, by coordinate, as they are drawn.** It knows `(x, z, half)`, the world position and the assembler. It does not *publish* the current tile yet, the way `ModelWalk.SetSubmit` publishes the current model |
| `ModelWalk.Scene` | a `ModelDraw` per submit: kind, slot, record, model id, world position, assembler, light record | **Models and instances, by table and slot, with a world position.** Used for picking, per-model materials and light receivers. `SubmitKind`/`SubmitRecord`/`SubmitSlot` are live while a packet is being built |
| `patches/PlanarWalk.cs` (`0068`) | the tile walk run again, and the model submits replayed, from a camera mirrored in the water, with every piece of state saved and restored | **Proof that the scene can be drawn from a second camera without disturbing the first.** It is the template for the editor's free camera, and for any later pass drawn from a light's point of view |
| `patches/AoWorld.cs` (`0059`) | the area's 80×80 tile grid as a GPU texture, and the camera's rotation and world position as uniforms | **World space inside a shader.** It turns a fragment's view position into a world one, and it is a grid a shader can march, which gives coarse shadows for authored lights |
| `patches/Map*.cs` | the full-screen plan, the docked panel with a readout of all ten tile bytes under the cursor, fog of war, markers | **The editor's top-down view and tile picker, mostly written.** It also holds the one invariant that proves the tile addressing: the player stands on a drawn half with a floor-height gap of 0 |
| upstream `Assets/` | texture and XA replacement packs: `pack.json`, content-hashed texture keys, `TextureDumper`, a Texture Inspector panel | **Texture identity and replacement images, already built.** See below |
| `FrameCapture` (`0046`) | one run of stage 13, scrubbed GP0 command by command, with an owner routine for each command | **Picking a texture on demand**: which command drew this pixel, and so which texture page, CLUT and UVs |
| `OutputView` (`0029`) | where the game picture sits in the window, in window pixels and in game pixels | **Where the editor draws its gizmos** |
| `KF2_SHELL` and `mcp/` | `goto`, `state`, `nearby`, `load`, `warp`, `press` over TCP | **How an agent drives every measurement below without seeing the window** |
| `scripts/shader_probe.c`, `scripts/light_probe.c` | the real fragment shaders run headless over known inputs, read back | **The bit-identical proof for a shader branch that is switched off** |

### Upstream already has a texture pack system, and the port's docs never mention it

**Confirmed, read from the vendored tree.** `tools/RecompOne/RecompOne.Runtime/Assets/`
holds:
- `AssetReplacerManager`, which loads `./packs/` as folders or zips at startup;
- `AssetPack` and `PackManifest`: `pack.json` with `id`, `name`, `author`,
  `version`, `priority`, a `game` id (strict by default), and lists of textures,
  CLUTs, texture rules and XA replacements;
- `TextureTile.Hash`: an FNV-64 hash of the texel indices plus a separate hash
  of only the CLUT entries those indices use;
- `TextureResolver`, `TextureDumper` and `TextureRegistry`;
- a Texture Inspector panel with tile and page dumping.

`GlCore.ResolveReplacement` binds the replacement as `uRepTex`/`uRepClut` in
`PrimFs`.

That is the right identity for a texture and the right home for replacement
images, and the remaster should use it rather than build a second one. **What has
not been measured is how it behaves on the port's own texture path.** That path
has three pieces:
- the anisotropic kernel (`0041`) in `decode()`;
- the texture-rectangle clamp and the mip atlas (`0060`);
- the fluid scroll (`0053`).

The replacement branch in `PrimFs` sits beside all three. **Inferred**: a
replaced texture skips the mip atlas and the rectangle clamp. That is the first
thing Phase 4 measures.

## Identity: what authored data attaches to

**Everything else in this document depends on this section, so it comes first.**
Authored data has to stay attached to the same thing across frames, sessions,
save files, the port's own settings, and differently mastered discs. Packet
addresses change every frame. LBAs change between masterings. Screen positions
change with the render scale and the aspect.

### The rule for what an authored file may hold

**Identifiers the game defines, and the author's own values. Never the game's
payload.** Allowed:
- area numbers, tile coordinates, table indices and model ids;
- content hashes;
- colours, positions, intensities and file names the author chose.

Never allowed: bytes, texels, vertices, text or any other copy of disc content.
A hash is a fingerprint, not a copy: it cannot be turned back into the texture
or the tile block it was taken from. This rule is what lets a pack sit in the
repository and be shared between players who each own the disc.

### The keys, and what breaks each one

| key | names | what breaks it | how the design survives it |
|---|---|---|---|
| **Game**: the disc serial `SLUS-00158`, as upstream's strict `PackGame.Id` | the whole pack | a different game, above all the US-boxed *King's Field II* (`SLUS-00255`) | already refused twice: by `DiscCheck.Validate` and by a strict pack game id |
| **Area**: the area byte at `0x8017E060` **plus an area fingerprint**, an FNV-64 of the 64,000-byte tile block at `0x801C8484` **less each half's `+2`** and the `0x600`-byte collision-shape block at `0x801D8484`, taken once the area has settled and before any edit is applied. `+2` is out because the game writes a moving footprint into it (**Confirmed**, see "Phase 1, the first slice") | everything authored in that area | a different revision or region of the same game; anything that rewrites the tile block before the fingerprint is taken | the fingerprint is taken before the remaster's own edits. **On a mismatch the area's layers are switched off whole, and the editor says why**; they are never half-applied |
| **Tile half**: `(area, x, z, lower \| upper)` | one floor or ceiling surface; the mesh instance the tile draws; its collision cell; its light record (`+4 & 0x3F`) | the game rewriting tiles at run time: the drawbridge and the minecart are tiles, not models (see "The map is an 80x80 tile grid" in `GAME_INTERNALS.md`) | the key stays valid, but what it names can change under it. An entry may carry a condition on the tile's current model index, which is an index and not payload |
| **Tile mesh**: `(area, model index at half +0)` | every instance of that mesh in the area | nothing known. **Open**: where the area's model bank lives, and so whether two areas share a mesh | once the bank is found, a content hash of the mesh gives an identity across areas |
| **Model**: `ModelDraw.Model` (the model id), and for objects `(area, definition index at rec +0x6)` | "every creature of this kind", "every torch" | an MO morph changes a model's vertices, not its identity | a kind is the natural key for materials and for lights attached to a model |
| **Instance**: `(area, table, spawn ordinal)`, where the ordinal is the slot the area loader filled | one placed prop or creature | dynamic slots (drops, projectiles, respawns); saved state (a killed creature, a picked-up item) | **Inferred**, not measured, that static props land in the same slot on every load. Fallback: `(area, definition index, spawn position rounded to 64 units)`, matched to the nearest live record at load |
| **Texture**: upstream's `(index hash, CLUT hash, bpp, w, h)` | a piece of art, wherever VRAM puts it | (a) the key hashes the **polygon's UV bounds**, so one piece of art can have several keys; (b) the scrolling textures are re-uploaded every tick, so their content, and their hash, changes every tick; (c) a CLUT that cycles; (d) a region the GPU drew into is rejected as dirty; (e) a port patch that changes a palette (`MessageText` zeroes one) | (a) key a face on its `GteTexRect` rectangle, and measure whether the game uploads per texture or per page (**Open**); (b) key the fluid slots as `(area, fluid slot)`, the same slots `Reflections` already publishes as water; (c, e) a material keys on the **index hash alone**, with the CLUT hash as an optional narrowing |
| **Face**: `(mesh key, primitive index)` | one polygon of one mesh | nothing, provided the assembler can expose the index | only needed for per-face materials, late in the roadmap |

### What the keys survive by construction

- **Render scale and widescreen.** Every key is in world space or VRAM space, and
  VRAM is always the console's 1× (`0054`). Nothing is keyed on a screen position.
- **A mod or patch being toggled.** The area fingerprint is taken before edits.
  Materials key on the texel indices, not the palette, so a patch that zeroes a
  palette leaves the key alone. `kf2debug`'s noclip moves the player, not the map.
- **A different mastering.** No LBA, file offset or `fdat` sector appears in any
  key. The generated dispatch tables bake LBAs (see `PACKAGING.md`); the remaster
  must never read them.
- **A save file.** Tile, model, texture and area keys are all re-resolved when an
  area settles. Instance keys are the weak ones, and their edits are the ones that
  can leak into a save (see "Level editing").

**Forbidden as keys:** a packet address, an ordering-table position, a screen
position, a VRAM position on its own, an LBA, and a slot number for anything that
can be spawned.

### When an area has settled

**Confirmed in Phase 1, with two additions the design did not have.** A New Game
passed the floor test once *before* `fdat02` loaded, on the block the last area
left, so an area can settle only after an `fdat` module has loaded since the last
executable did. And the fingerprint must read the same twice 200 ms apart, so a
block still being copied in cannot settle.

**The loader is not the signal.** `func_8001689C` is called every frame with the
load inside a branch (see "The area loader looks like the right hook and is not"
in `PATCHES_AND_MODS.md`), and `OverlayLoadedEvent` fires before the tile block
is copied. The map patches wait for the invariant instead: the player stands on a
drawn half whose floor height matches their own Y exactly. The identity layer
uses the same test, plus the area byte changing. It takes the fingerprint on the
first frame both hold, and only then applies the area's layers.

### What has to be measured before the keys are trusted

1. **Instance stability.** Load an area twice, reload a save, and compare
   `slot → (definition index, position)` for the object and creature tables.
2. **The model bank's address**, for mesh identity across areas.
3. **Texture upload granularity.** Do `LoadImage` calls carry one texture each,
   or whole pages? That decides whether key (a) needs normalising.
4. **Which tile bytes the game rewrites at run time, and when.** The map already
   copies the block four times a second; the probe needs a diff.

## Architecture: five layers and the seams between them

**A new feature must be a new file, not an edit to five.** The port's patches have
so far each been a vertical slice: hooks, state, settings page, probe, shader.
That works for one feature and does not work for twenty, because each one edits
`GlCore`, `GlShaders`, `Program.cs` and a settings page. The remaster splits the
work into layers, and each layer has one owner.

```
  editor UI (patches/editor/)     picks, gizmos, inspectors, undo
        |  edits documents only
  data model (patches/remaster/)  packs, documents, layering, versions, file watch
        |  change events, on the game thread
  application (one IRemasterFeature per layer)
        |  writes side tables and uniforms, hooks through HookAttach
  render features (vendored runtime, 0069+)   one uniform block, shader terms
        |
  identity (patches/remaster/Identity.cs)     area, fingerprint, keys, resolvers
```

1. **Identity** (`patches/remaster/Identity.cs`). This layer:
   - detects when an area has settled and takes the fingerprint;
   - defines the key types;
   - resolves a world position to a tile half, a `ModelDraw` to an instance or a
     model, and a texture to its key through upstream's `TextureTile`.

   It reads guest memory and writes nothing.
2. **Data model** (`patches/remaster/Pack*.cs`). This layer:
   - reads and writes documents as JSON;
   - migrates versions and layers packs;
   - watches the pack directory. A change on disk is parsed on a worker thread
     and swapped in at the next `VSyncEvent`, the same marshal point `KF2_SHELL`
     uses for its light commands.
3. **Application**. One `IRemasterFeature` per layer:

   ```csharp
   interface IRemasterFeature
   {
       string Id { get; }                       // "lights", "surfaces", "level", ...
       bool Enabled { get; set; }               // KF2_REMASTER_<ID>, and a setting
       void Attach();                           // through HookAttach; read back IsCommitted
       void Detach();                           // puts back everything it changed
       void OnAreaSettled(AreaContext area);    // resolve keys, apply edits
       void OnDocumentChanged(DocumentChange c);
       void OnTick();  void OnFrame();
       void DrawInspector(Selection s);         // the editor's panel for this layer
       string Probe();                          // the KF2_REMASTER_PROBE line
   }
   ```

   The features are registered in one list in `patches/remaster/Remaster.cs`,
   which `Program.cs` installs **before `LoopPacing`**. `LoopPacing` has to stay
   last, because its post on stage 13 must run after the smoothers'. `Detach` is
   what makes off bit-identical: a feature that cannot put something back does not
   change it.
4. **Render features** (the vendored runtime). One new runtime file owns a single
   `RemasterUniforms` block: the material table, the light list, the fog colour
   and the sky. `GlCore` uploads it by generation, and the shaders read it. Once
   that block exists, a later render feature adds fields to it and a term to a
   shader, without editing `GlCore` again. Numbering follows the rule in
   `RECOMPONE_PATCHES.md`, where a new number is for a new mechanism:
   - widening the material ids and moving their table into the block **amends
     `0067`**;
   - authored lights are `0069`;
   - fog colour and the sky fill are `0070`;
   - normal and roughness maps are `0071`;
   - a GPU id buffer for picking would be `0072`, and only if it turns out to be
     needed.

   All of it is **GL core only**; the 2.1 path and the software rasterizer ignore
   it, as they do `0048` and `0067`.
5. **Editor UI** (`patches/editor/`). ImGui panels, with gizmos drawn over
   `OutputView`. It reads the data model, the identity resolvers and the scene
   the walks publish. **It writes only documents**, never a side table or a
   uniform. That keeps undo, saving and live reload the same operation.

**The seam that matters most is between the editor and the application.** An
edit is a document change. The feature hears about it on the game thread and
re-applies. Nothing in the editor can put the game into a state the saved file
cannot reproduce.

## Data model and file format

### A remaster pack is an upstream asset pack with a `remaster/` directory

**Recommendation: do not invent a second pack format.** A remaster pack is a
folder or zip under `packs/`, with upstream's `pack.json` at its root. That gives
it for free:
- a game-id check;
- a priority order;
- enable and disable;
- loading from a zip;
- replacement textures and XA.

The port's own data sits next to `pack.json`, in `remaster/`, as one JSON file
**per layer per area**, so that a diff is small and two people editing different
areas never touch the same file:

```
packs/verdite-stone/
  pack.json                        upstream's manifest: id, name, author, version, game, priority
  textures/...                     upstream replacement images (the author's own art)
  remaster/
    pack.remaster.json             formatVersion, the features the pack uses, a gameplay-changing flag
    materials.json                 the material library, referred to by name
    textures.json                  per-texture: material, normal/roughness map, emissive
    areas/1/lights.json
    areas/1/surfaces.json          tile, mesh and model -> material
    areas/1/atmosphere.json        fog colour and curve, sky, light-record overrides
    areas/1/level.json             tile byte edits
    areas/1/props.json             port-drawn decorations (late)
```

Every area file carries its gate:

```json
{ "formatVersion": 1, "area": 1, "fingerprint": "9f3c1a0be4d27765" }
```

### The documents, by example

A material is named, and packs refer to it by name, so ids are allocated at load
time and two packs cannot collide on a number:

```json
// materials.json
{ "formatVersion": 1,
  "materials": {
    "polished-stone": { "reflectivity": 0.35, "f0": 0.04, "roughness": 0.3 },
    "wet-rock":       { "reflectivity": 0.2,  "f0": 0.03, "roughness": 0.6 },
    "brazier-coal":   { "emissive": [1.0, 0.45, 0.1], "emissiveStrength": 0.8 } } }
```

Surfaces assign materials, from the most specific key to the least:

```json
// areas/1/surfaces.json
{ "formatVersion": 1, "area": 1, "fingerprint": "9f3c1a0be4d27765",
  "tiles":  [ { "x": 35, "z": 36, "half": "upper", "material": "polished-stone" } ],
  "meshes": [ { "tileModel": 12, "material": "wet-rock" } ],
  "models": [ { "model": 41, "material": "brazier-coal" } ] }
```

Lights are world positions in the game's own units: a tile is 2048, a height step
is 128, and up is **−Y**:

```json
// areas/1/lights.json
{ "formatVersion": 1, "area": 1, "fingerprint": "9f3c1a0be4d27765",
  "lights": [
    { "name": "hall brazier", "type": "point",
      "position": [72704, -15360, 73728], "colour": [1.0, 0.62, 0.3],
      "intensity": 1.2, "radius": 4096, "falloff": "smooth",
      "affects": "both", "shadow": "none",
      "flicker": { "amount": 0.15, "hz": 7 } },
    { "name": "shaft", "type": "spot", "position": [70000, -18000, 70000],
      "direction": [0, 1, 0], "cone": [20, 35], "colour": [0.7, 0.8, 1.0],
      "intensity": 0.6, "radius": 6144 } ] }
```

Textures use upstream's key string for the art, so a key read off
`TextureDumper` or the Inspector can be pasted in as it is:

```json
// textures.json
{ "formatVersion": 1,
  "textures": [
    { "index": "5b1e0c2a9d7f3e41", "material": "polished-stone",
      "normal": "maps/floor01_n.png", "roughness": "maps/floor01_r.png" } ] }
```

Atmosphere and level edits are covered in their own sections below.

### Versions, layers and removal

- **A version per file.** `formatVersion` is an integer, bumped when a field
  changes meaning. `PackMigrations.cs` holds one function per step. **Unknown
  fields are kept on a round trip**, so a newer pack opened in an older editor
  loses nothing. The editor always writes the current version.
- **Layering.** From bottom to top:
  1. the base game (no packs);
  2. packs, in upstream's priority order;
  3. the user's working pack, always on top.

  Each layer overrides the ones below it **per key**, not per file. An explicit
  `null` for a key removes it, which is how one pack turns off a light another
  pack placed.
- **The fingerprint gate applies per pack.** A pack whose area fingerprint does
  not match is off for that area only, and the Remaster packs page lists it with
  the reason.
- **Sharing is a zip of the working pack.** By the rule in "Identity" it contains
  no disc data, so it can be committed to this repository and handed to another
  player. Replacement images are the author's own art.

### Settings

- One **Remaster packs** page lists the packs found, whether each is enabled,
  which features each uses, and every load or fingerprint error.
- Each render feature gets one switch under Video ▸ Enhancements, beside the
  existing ones.
- Nothing is enabled on a first run, and the repository ships no pack switched on.
- A pack that changes collision or placement says so on that page, in plain
  words; see "Level editing".

## The editor

**Recommendation: in the game, in ImGui, not an external tool.** The scene exists
only in the running process:
- which tile the cursor is over;
- which model is where this tick;
- which texture a pixel came from;
- the camera block.

A tool outside the process would need all of that sent over a socket every frame,
and could still never show the result as the renderer draws it. In the game,
live reload comes free, and so do `PanelManager`, the map panels, the frame
viewer and `OutputView`.

**External tools keep the jobs they are good at.** The JSON is meant to be edited
by hand and is watched, so a text editor is a first-class tool. Images are drawn
in an image editor. A mesh importer for port-drawn props, if one is ever built,
is a command-line converter.

### Modes

- **Play.** The editor is closed and costs one bool a frame.
- **Edit.** The world is paused through `FramePacing.PauseWhen`, the mechanism the
  full-screen map already uses. Stage gating holds and the renderer keeps
  drawing, so lights and materials update live on a frozen scene. The editor
  camera takes over, and the panels open.

  **Shift+E** is proposed as the hotkey. It has to be checked against
  `KeyLayout` and the existing Shift+P, Shift+F and Shift+M, M and N.

### Picking

- **Tiles:** a ray cast on the CPU from the cursor, through the camera block at
  `0x80192E18` and the GTE's H, OFX and OFY, against the 80×80 grid's drawn
  halves, whose floors sit at `-(height << 7)`. Exact, cheap, and it returns a
  tile key directly.
- **Models:** the same ray against `ModelWalk.Scene`'s positions with a bounding
  radius per model id. It returns a `ModelDraw`, and through that an instance or
  a model key.
- **Textures:** on a click, a one-frame `FrameCapture`, which already answers
  which GP0 command and owner routine drew a pixel. The command gives the page,
  the CLUT and the UVs, and from those upstream's key. It is only needed on a
  click, so its cost does not matter.
- **The map panel** doubles as a top-down tile picker: it already reads all ten
  bytes under the cursor.
- **No GPU id buffer at first.** It would be a render-target attachment and a
  per-triangle id, which is a new runtime mechanism (`0072`). The CPU paths
  answer everything the first phases need.

### Gizmos

Gizmos are a move handle on each of the three axes, a radius sphere and a
direction arrow for a spot light. They are drawn in the ImGui draw list over
`OutputView`'s rectangle and projected the same way the AO and SSR passes
reconstruct positions: the view matrix at `0x80192E18`, H, and OFX/OFY. Snapping
is to 128 units in Y, the height step, and to tile centres in X and Z.

### The editor camera

**The `PlanarWalk` pattern:**
1. save the camera block;
2. rebuild it through `func_8002E22C` for a free eye;
3. let stage 13 draw;
4. put it back.

**The catch is the visibility grid.** `CullGrid` builds the 24×24 window around
the camera the game has, so an eye far from the player would see its surroundings
through the player's window, with tiles missing. `CullGrid.Build` reads the camera
from memory. **Open**: whether running it again after the camera block has been
swapped gives a correct window for the free eye.

**Until then, the fallback is `goto` plus the game's own camera**, which puts the
player where the author wants to look. That costs nothing new and is enough for
Phases 1–6.

### Undo, save, autosave

- **Undo** is a stack of document diffs, each able to apply and revert, kept per
  session. An edit made through a gizmo drag is one entry, not one per frame.
- **Save** is explicit, and writes the working pack's files.
- **Autosave** writes a `.autosave` file next to each changed document every 30
  seconds. On startup, the editor offers to restore it.

### Driving it without a window

New `KF2_SHELL` verbs, also exposed through `mcp/`, so an agent can run every
measurement in the roadmap:

```
edit on|off                        enter or leave Edit mode (pause, editor camera)
select <key>                       select by key: tile:1:35:36:upper, model:41, tex:5b1e..., light:1:"hall brazier"
set <key> <field> <value>          change one field through the same undo stack the panels use
pack save|reload|list              the working pack
snap <path> [hash]                 read the presented target back; write a PNG, or print its hash
```

**`snap` is the tool the whole "off is bit-identical" rule depends on.** It is
built from what "Getting pixels out without a screenshot" in `DEVELOPMENT.md`
already does: the presented target read back from a `DrawOTag` post and written
with `PngWriter`. With the world paused and the view pinned by `goto x y z yaw
pitch`, two runs give comparable frames.

## Lighting: authored lights beside the game's own

**The game's lighting is a known chain, and the port already rebuilds it per
pixel.** A packet's colour is:

    RGBC * (BK + LCM * max(0, LLM · N))

followed by the depth cue, a darkening towards the far colour, which is 0 here.
Map tiles take one `NormalColorCol` per face, so **the depth cue is all of a
tile's shading**. `0048` records the chain's inputs per corner and `shade8`
evaluates it per pixel; `light_probe.c` measured the shader against the formula
with a worst difference of 0. See "Per-pixel lighting" in `RENDERING.md`.

### Recommendation: forward, inside `shade8`

**An authored light is one more term in the lit colour, added before the depth
cue and before the texture is modulated.** So:
- it is fogged by the game's own curve;
- it is textured exactly as the game's own light is;
- it saturates where the game's light saturates.

Every other placement gets at least one of those three wrong.

- **Position per fragment:** the view position rebuilt from the recovered depth,
  exactly as `NormalFs` rebuilds it, then turned into world space with the
  camera rotation and position `AoWorld` already uploads. No new vertex
  attribute.
- **Normal per fragment:** the plane from the view position's screen
  derivatives, as `0058` takes it. It is exact for the flat faces the tiles are.
  Gouraud normals for models are a later refinement: the light-dot record would
  also carry the normal.
- **The light list** is culled on the CPU against the camera and the 24×24 window
  every frame, capped at 16, and sent in `RemasterUniforms` by generation, so an
  unchanged list is not re-sent.
- **Receivers:** tiles and models. The first-person arm and anything placed in
  view space (a matrix of 0 in the submitter) are left out unless a light is
  flagged to reach them, because their positions are not in world space.
- **Off is a branch on a count of zero**, so the shader's output with no lights
  is the output it has today. That is proved headless: `light_probe.c` run with
  and without the uniform block, worst difference 0.

### Why not deferred

The surface buffer (`0067`) already holds a normal, a depth and a material at
every pixel, so deferred looks free. **It is not, because the colour it would
relight is final.** The texture has already been multiplied by the lit colour,
clamped and fogged, and water and the other translucents are drawn over it. A
deferred light would have to divide the game's lit term back out per pixel, and
that term saturates. Deferred stays the right place for terms that apply to the
finished picture: a specular highlight along the SSR path, light shafts, bloom.

### Shadows

- **None in the first version.** An authored light lights everything inside its
  radius, which is how the game's own light records behave.
- **Then a march through the tile grid.** `AoWorld` already marches the 80×80 grid
  in a shader for occlusion. A march from the fragment towards the light through
  the same texture gives whole-tile walls, with creatures and props casting
  nothing. It is coarse, but it is a known cost.
- **Shadow maps are pushed back.** They need the scene drawn from each light. The
  mirrored walk alone costs 1.09–1.12 ms of CPU a frame, measured under
  "Planar reflections", and the port is CPU-bound, so even one shadowed light
  would cost a planar reflection's worth of frame time. If a static shadow map
  per area is ever wanted, it should be taken once when the area settles, never
  per frame.

### The game's own light and fog, authored through the game

**The cheapest and most faithful remaster edit is to the game's own lighting
data.** Stage 1 copies the area's 80 light records from `0x800679A0` into
`0x801930F0` every frame; `TintHold`'s C# stage 1 does the copy (see "The tints
strobed between ticks" in `PATCHES_AND_MODS.md`). Each 104-byte record carries
the light matrix, the colour matrix, the back colour and the depth cue, and
`func_80031950` picks one per tile by `+4 & 0x3F`.

An override applied right after that copy runs through the game's own code, and
through `0048` unchanged, so it is exact whether per-pixel lighting is on or off.
**Inferred**: nothing but rendering reads those records. That has to be confirmed
with `scripts/find_writers.py` and a read census before an override ships.

Fog colour is the GTE's far colour, measured at 0, which is why the fog darkens
to black rather than fading to a colour. A fog colour becomes one term in
`shade8`: `mix` towards a colour instead of a multiply towards black.

## Level editing: what can be edited and what can only be decorated

**The split is whether the game's own logic reads it.** If the game reads it, an
edit changes gameplay, and it is a level edit. If only the renderer reads it, it
is decoration.

### Edited: the tile bytes, because the game reads them

The 80×80 block at `0x801C8484`, applied when the area settles, after the
fingerprint is taken, and applied again whenever the area is entered.

| byte of a half | what | read by |
|---|---|---|
| `+0` | model index; `0xFF` empty, drawn below 240 | `func_80031B1C` (the renderer), `CullGrid` |
| `+1` | height; the floor is `-(h) << 7` | `func_80031B1C`, the floor queries `func_8002B6B4` and `func_8002C3A8` |
| `+2` | collision flags, `& 0xFC` | `func_8002C700` |
| `+3` | collision-shape index into `0x801D8484` | `func_8002B7D0` |
| `+4` | flags: bit `0x80` stops the visibility flood; the low six bits pick the light record | `CullGrid`, `func_80031950` |

Limits that follow from the format and cannot be designed away:
- **Heights move in steps of 128 units.**
- **A tile can only show a mesh the area already has.**
- **Collision shapes come from a 0x600-byte block**, so a new shape is a slot
  in that block, if one is free.
- **The game's own tile rewrites win.** A door, the drawbridge or the minecart
  rewrites its tiles at run time, so an edit to one of those tiles is undone by
  the game. The editor flags such tiles from the rewrite census.
- **Open**: whether any of the tile block is written into a save, or whether it
  is always re-read from the disc on an area load. Until that is measured, level
  edits are marked gameplay-changing.

The test for an edit is the invariant the map already prints. Stand on the edited
tile with `goto`; `state` must give a floor gap of 0 at the new height. Walk into
an edited wall with `press`; the position must not cross it.

### Edited with a warning: placement of objects and creatures

Objects at `rec+0x14` in the table at `0x80177714`; creatures at `rec+0x2C` in the
table at `0x8016C544`. **`func_800492B8` packs the 200-slot creature table into
the save buffer** when the player saves. A moved creature is therefore written
into the player's save, where it outlives the pack and stays after the pack is
switched off. That is the one kind of edit that can change a player's game
permanently. It is last in the roadmap, opt-in per pack, and labelled on the
Remaster packs page.

### Decorated: everything only the renderer reads

- materials, lights, fog, sky, and the light-record overrides;
- **port-drawn props**: meshes the author supplies, submitted through
  `PolyAssembler` into the frame's ordering table, so they get depth, fog,
  per-pixel lighting and reflections the same way the game's own models do.
  They have no collision, and are said to have none. This is the only way to
  add geometry, and it is late in the roadmap.

### Pushed back

- **Replacing creature meshes.** A creature is an MO morph, a base TMD plus
  packed vertex deltas per clip (see "The model pipeline has no skeleton" in
  `GAME_INTERNALS.md`). A replacement would have to carry matching morph targets
  for every clip. That is a modelling pipeline, not an authoring tool.
- **New walkable geometry** beyond what tiles and collision shapes can express.
- **Lifting the 24-tile draw window.** Already measured as binding and barely
  worth it; see "Is the 24-tile window worth lifting?" in `WIDESCREEN.md`.

## Rules every phase keeps

- **Off is bit-identical, and it is proved three ways**, depending on what the
  feature touches:
  1. `snap … hash`, feature off, against a build without the feature, at a pinned
     view: the world paused, `goto x y z yaw pitch`, at `KF2_AUTOSTART=new` and
     at `KF2_AUTOSTART=2`;
  2. `shader_probe.c` or `light_probe.c` for any shader branch;
  3. `KF2_*=verify` for any C# routine a feature adds to (`KF2_POLYASM`,
     `KF2_TILEWALK`, `KF2_MODELWALK`), with 0 RAM, register and GTE mismatches.
- **144.0 fps drawn at 20.0 ticks/s**, measured with
  `KF2_FPS=144 KF2_FPS_PROBE=1 KF2_PRESENT_PROBE=1`. Each feature's cost is taken
  from the frame profiler, on and off. GPU time comes from a frame capture's
  `GL_TIME_ELAPSED` queries, since frame rate cannot see GPU cost on a CPU-bound
  port.
- **Features ship off; no pack is enabled by default; the repository ships none
  switched on.** The remaster never breaks faithfulness by default.
- **Every feature has a `KF2_REMASTER_<ID>` switch and a probe line**, both listed
  in `ENV_VARS.md`.
- **Hooks attach through `HookAttach`** and read back `IsCommitted`, as every patch
  does (see "A registration is not a hook" in `PATCHES_AND_MODS.md`).

## The phased roadmap

Each phase is usable on its own. For each one this lists what ships, what it
depends on, the risks, the counter that proves the mechanism, and what the user
has to look at, because nobody else can.

### Phase 1: an authored material on a floor, end to end

**The smallest slice that exercises every layer:** one material placed in the
editor, saved, reloaded and rendered. A material, not a light, because the
reflection pass already reads `SurfaceMaterial` and `GtePacketDepth.Rec.Material`
already exists, so **no new runtime patch is needed**. The whole slice is
port-side. (Wrong by one amendment to `0067`; see "Phase 1, the first slice".)

**Status:** [x] done and measured, [~] done in part, [ ] not started.

- **Ships:**
  - [x] `Identity` with area, fingerprint and tile half only;
  - [x] `TileWalk` publishing the half it is assembling (`x, z, half`), the way
    `ModelWalk.SetSubmit` publishes a model;
  - [x] `PolyAssemblerDepth.SealDepth` writing `Rec.Material` from a table the
    surfaces feature fills;
  - [x] `SurfaceMaterial.Reflectivity`/`F0` filled from `materials.json`;
  - [x] the pack loader (the working pack only);
  - [~] a minimal editor: tile pick by ray and from the map panel, a material
    inspector, save, reload, live reload, undo -- all in, driven over the shell
    and used by hand to place the first mirror floor;
  - [~] the `edit`, `select`, `set`, `pack` and `snap` shell verbs -- all but
    `snap`, which has to read the presented target;
  - [ ] the Remaster packs settings page, and the switch under Video ▸
    Enhancements (the editor's checkbox saves `kf2.remaster.on` for now).
- **Depends on:** reflections being switched on (`KF2_SSR=1`), since that is the
  only reader of a material today.
- **Risks:**
  - the eight material ids. The slice needs one; if it needs more, the `0067`
    amendment moves into this phase;
  - SSR's water assumptions (the fog curve, the sky fallback) may look wrong on
    a floor. That is for the eye to judge, and it is useful to learn early.
- **Mechanism measured by:**
  - [x] `SurfaceMaterial.FromPacket` and `ByMaterial[id]` rising for the authored id;
  - [~] the SSR readback's reflective share with the tile in view -- the id shows
    in the readback's map, but the only tile tried was the water surface, so the
    share could not move;
  - [x] the same tile key resolving after a `warp` out and back, after `load 2`, and
    after a restart;
  - [x] the fingerprint identical across those -- once `+2` was left out of it;
  - [ ] `snap` identical to baseline with the pack disabled;
  - [x] `KF2_POLYASM=verify` and `KF2_TILEWALK=verify` clean;
  - [x] 144.0 at 20.0.
- **You look at** (the first judged: a dry tile at reflectivity 1 reads as a
  mirror, placed correctly):
  - whether the floor reads as polished stone or as a mirror;
  - whether the reflection's fog and sky fallback look wrong on something that
    is not water;
  - whether the editor is usable: picking, the inspector, the save round trip.
- **Cost:** one table read per sealed tile packet while on, one bool while off.

### Phase 1, the first slice

**What is in.** Everything in the Ships list above except `snap`, the Remaster
packs settings page and the camera-free parts of the editor's gizmos:

- `patches/remaster/Identity.cs` — area, fingerprint, the settle test, `TileKey`
  (`tile:A:X:Z:lower|upper`), record address to key and back.
- `TileWalk.CurrentRecord` — the half `func_80031950` is assembling, set around the
  assembler call. `PolyAssembler.TileMaterial` is what `Surfaces` resolved for it,
  and `SealDepth` writes it into `GtePacketDepth.Rec.Material` for every packet it
  seals, clipped fans included. A material therefore needs the C# half
  (`KF2_TILEWALK_TILE=0` authors nothing).
- `patches/remaster/Pack.cs` — the working pack (`packs/working`, or
  `KF2_REMASTER_PACK`), `materials.json` and `areas/<n>/surfaces.json` kept as JSON
  trees so unknown fields survive, upstream's `pack.json` written once, a
  `FileSystemWatcher` whose parse is swapped in at the next VSync, and one undo stack.
- `patches/remaster/Surfaces.cs` — the one `IRemasterFeature` so far: names to ids
  from `SurfaceMaterial.FirstAuthored`, the fingerprint gate, an 80×80×2 id table.
- `patches/remaster/Pick.cs` — the game's projection both ways: the view matrix at
  `0x80192E18` less the camera (whose 16-bit X and Z are unwrapped against the
  player's), and `GteDepth.ProjH`/`ProjCx`/`ProjCy`. A floor ray is walked cell by
  cell through the grid; walls are not read, so a ray can pass through one.
- `patches/remaster/Editor.cs` — Shift+E; pauses the world; picks by a click on the
  picture, by right-click in the docked map, or as the player's tile; outlines the
  selection; a material library with reflectivity and F0 sliders (one undo entry per
  drag); Save, Reload, Undo, Redo.
- `patches/remaster/Shell.cs` — `edit`, `select`, `set`, `pack`, `remaster` on
  `KF2_SHELL`, through the same calls the panel makes.

**The runtime needed one change after all, an amendment to `0067`.** `NormalFs`
decided opacity from the id (`vM < 1.5`), so any authored id on an opaque floor
would have taken that floor out of the occlusion pass's normals. Opacity now
travels with the triangle; every existing id is drawn as before. The design's claim
that Phase 1 needs no runtime patch was wrong by exactly this.

**Measured** (`KF2_AUTOSTART=new`, `KF2_SSR=1`, area 0 in `fdat02`):
- The authored id reaches the classifier and the surface buffer:
  `SurfaceMaterial.ByMaterial[4]` and `FromPacket` rise together (4,032 in one
  window), about 4,600 authored packets a second for two tiles in view.
- The pick and the projection agree with the picture: `select pick 160 200` chose
  tile 37,45, and the SSR readback's material map then showed the new id in the
  cells covering game pixels 115-169 by 195-210. Picks follow the player's heading.
- Tile 37,45 is **the water surface itself** — the water is a tile, drawn blended by
  the tile assembler — so a packet material there replaces `Water` and the pixels
  stay reflective (the readback's reflective share did not move, 36.3%). It is also
  the amendment's test: a blended tile with an authored id stays translucent.
- **The first fingerprint was too strict.** Area 0 read `75069e…` from a New Game
  and `fab65f…` entered from save slot 2. The probe's diff: 32 empty lower halves
  (model `FF`) in two 4×4 blocks, `+2` bit `0x04` set in one and cleared in the
  other, nothing else different. Something four tiles square moves between the two
  saves and stamps its footprint into the collision flags; the ship by the shore is
  the obvious candidate (**Inferred**). With `+2` left out, area 0 reads
  `3f7d7b45edcbda59` from both, and area 1 `49930d41f830e0a3` across two loads and
  an auto-reload.
- A pack authored against the old fingerprint was refused whole, with the reason on
  the panel and in `remaster`; editing the file's fingerprint by hand applied it
  through the watcher with no restart.
- A hand edit adding a material with an unknown field (`roughness`) and a root field
  reloaded live and survived the next save.
- `KF2_TILEWALK=verify KF2_POLYASM=verify` with materials applied: 1,297 reports,
  all 0 RAM, register and GTE mismatches.
- 144.0 fps drawn at 20.0 ticks/s, `[present] wide 288`, vertex map 97.1% hit, with
  the remaster on. `set remaster off` in the same run: sealed packets stop, the
  authored id's count stops growing.
- **Off costs one test a frame** (`Host.Frame` returns before reading anything) and
  one byte store per sealed packet, which writes the `None` the record always held.

**Not measured, and why.** `snap` is not built: `GpuHle.Backend.ReadVram` reads 1×
VRAM, and the reflection pass composites at present, after VRAM, so a VRAM hash
cannot see what a material changes. `snap` has to read the presented target, which
is a runtime hook (`GlCore.PresentDisplay`) — next. Until then "off is
bit-identical" rests on the record holding `None` with the switch off and on the
amendment keeping every existing id's opacity, both read from the code.

**Looked at.** One dry floor tile in area 0, set to reflectivity 1 through the
editor, judged good as a first step: it reads as a mirror of the doorway and the
wall above it, in the right place and the right way up. The editor was used end to
end to get there (pick, material, sliders). Still to judge: a floor at a *partial*
reflectivity (does it read as polished stone), a larger area of floor, and the
speckled fringe along the reflected wall edge in the same screenshot, which is
where the march's thickness test decides hit or miss and has not been looked into.

**Next.** `snap` from the presented target; the Remaster packs settings page and
the saved switch under Video ▸ Enhancements (the editor's checkbox saves
`kf2.remaster.on` today); picking that stops at walls; the texture-key census
(Phase 4) can start independently.

### Phase 2: authored point and spot lights (`0069`)

- **Ships:**
  - `RemasterUniforms` and the light list;
  - the light term in `shade8` (core `PrimFs`);
  - the light gizmo and inspector;
  - `lights.json`;
  - flicker, evaluated on the world tick so it holds with the world;
  - a probe counting lights culled, uploaded and lit fragments.
- **Depends on:** Phase 1's pack, editor and `snap`; per-pixel lighting on
  (`0048`), since the term lives in `shade8`.
- **Risks:**
  - fragment cost at high render scales. `PrimFs` runs for overdraw too, under
    painter's order;
  - the derivative normal at silhouettes;
  - light on the first-person arm.
- **Mechanism measured by:**
  - `light_probe.c` extended with a light: the term against a C reference, and 0
    lights bit-identical to today;
  - the probe's light and receiver counts;
  - frame profiler and GPU timers at 0, 1, 4 and 16 lights, at render scale 1
    and 5.
- **You look at:** falloff and colour; how a light reads through the fog; a
  creature walking through a light; the look at the edge of the radius.

### Phase 3: the material system proper

- **Ships:**
  - the `0067` amendment: ids widened to 256, which fits exactly in the
    `RGBA16F` alpha, with the table moved into `RemasterUniforms`;
  - materials keyed by tile mesh, model and texture index hash, resolved in that
    order after the tile half;
  - emissive, which adds to the lit term the way a light does, and roughness,
    which only SSR reads, as a blur of its hit.
- **Mechanism measured by:** the `ByMaterial` census for every source; the
  reflection census identical with no pack; `shader_probe.c` for emissive.
- **You look at:** emissive surfaces in the dark areas; how roughness looks on
  SSR.

### Phase 4: textures

- **Ships:**
  - upstream replacement packs brought into the port's path, first measured:
    - does a replaced texture keep the `0041` kernel, the `0060` rectangle clamp
      and the mip atlas;
    - do the fluid slots scroll a replaced water texture through `0053`;
    - fixes by amendment to those patches;
  - normal and roughness maps, **only paired with a replacement texture** (`0071`),
    read in `PrimFs` for the light term and passed to the surface buffer for SSR;
  - a texture-key census that tells a pack author which textures of an area the
    pack covers.
- **Risks:**
  - the UV-rectangle problem in the texture key (see "Identity");
  - the atlas's 2048×2048 budget with high-resolution replacements.
- **You look at:** replaced textures under filtering, at a distance, and in
  motion; normal maps under an authored light.

### Phase 5: atmosphere

- **Ships:**
  - light-record overrides after stage 1's copy;
  - fog colour and curve (`0070`);
  - a sky fill at the far plane that respects `Overlay`, so the HUD is never
    painted over;
  - `atmosphere.json`.
- **Depends on:** the read census confirming that only rendering reads the light
  records.
- **Mechanism measured by:** `KF2_PERPIXEL_PROBE=2`, the formula against the GTE's
  colour, still exact with an override on; the SSR fog-curve readback moving
  with the new curve; `snap` for the no-override case.
- **You look at:** the whole area's mood; the sky against the void past the draw
  distance.

### Phase 6: level edits

- **Ships:**
  - `level.json` with tile byte edits, behind the fingerprint gate;
  - the rewrite census, so a door's tiles are flagged;
  - the gameplay-changing label.
- **Depends on:** measuring whether the tile block reaches a save.
- **Mechanism measured by:**
  - `goto` onto an edited height, with `state` reading a floor gap of 0;
  - `press` into an edited wall, with the position not crossing it;
  - the `KF2_MAP_PROBE=1` dump showing the edit;
  - a save made after the edit loading with the pack off and giving the
    original tile.
- **You look at:** whether edited heights meet their neighbours; lighting on the
  edited tiles, which take their light record from `+4`.

### Phase 7: the editor camera, and sharing

- **Ships:**
  - the free camera: the `PlanarWalk` pattern, plus `CullGrid` run from the eye;
  - export of the working pack as a zip;
  - a compatibility report per pack: fingerprints matched, keys resolved and
    keys missing, per area.
- **Risk:** `CullGrid` from a free eye. If it cannot be made correct, the free
  camera is limited to within a few tiles of the player.
- **You look at:** flying the camera through an area with nothing missing.

### Phase 8: later

- shadows by marching the tile grid;
- port-drawn props;
- opt-in object and creature placement;
- a GPU id buffer (`0072`), if picking is ever too slow.

### Dependencies, in one list

- Phase 1 comes before everything.
- Phase 2 needs 1.
- Phase 3 needs 1; it helps 2, since emissive shares the light term.
- Phase 4 needs 3, because maps attach to materials.
- Phase 5 needs 2's uniform block.
- Phase 6 needs 1 and the save measurement.
- Phase 7 needs 1.
- Phase 8 needs 2, 6 and 7.

### Risks, in one list

1. **The texture key is unstable across UV rectangles.** It is measured in
   Phase 4 and normalised through `GteTexRect` or the upload rectangle.
2. **Instance slots may not be stable.** They are measured before any instance
   key is used, and there is a fallback key.
3. **`CullGrid` from a free eye** may not work; `goto` is the fallback.
4. **Shader cost at a high render scale**, since the port is CPU-bound and frame
   rate hides it. GPU timers per feature are the answer.
5. **Save contamination** from placement edits. Opt-in, labelled, and last.
6. **An upstream merge that touches `Assets/`.** The remaster depends on
   upstream's pack code, so the next merge (see `RECOMPONE_FORK.md`) has to
   treat `Assets/` as something the port relies on.

## Open decisions

Each has a recommendation. None of them blocks writing Phase 1 except the first
two.

1. **The first target area.** *Recommend `fdat02`*: a fixed spawn, water, and
   SSR and planar numbers already taken there.
2. **Material or light for the first slice.** *Recommend a material*: it needs no
   runtime patch and exercises every layer.
3. **In-game or external editor.** *Recommend in-game*, with hand-editable,
   watched JSON as the external path.
4. **Where packs live.** *Recommend upstream's `packs/`*: a remaster pack is an
   asset pack plus a `remaster/` directory, so replacement textures and the
   remaster ship as one thing.
5. **Forward or deferred authored lights.** *Recommend forward, inside `shade8`*,
   and deferred only for terms that apply to the finished picture.
6. **Whether object and creature edits may reach a save.** *Recommend no by
   default*: opt-in per pack, labelled, and last in the roadmap.
7. **Shadows.** *Recommend none at first, then the tile-grid march*; no per-frame
   shadow maps on a CPU-bound port.
8. **Normal maps only with replacement textures.** *Recommend yes.* A normal map
   drawn over a 64×64 four-bit texture fights it.
9. **Whether a remaster pack may change collision.** *Recommend tile collision
   only, behind a gameplay-changing label.* It keeps the rule against gameplay
   changes a player did not ask for.
10. **The material id budget.** *Recommend 256*: exact in the surface buffer's
    alpha, and allocated by name at load time.
11. **Where the remaster's code lives.** *Recommend `patches/`, not `mods/`*: the
    remaster is something the port offers, and a mod is a package that can be
    absent (see "What belongs in a mod" in `PATCHES_AND_MODS.md`). The *packs*
    are the optional part.
