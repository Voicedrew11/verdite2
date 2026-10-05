# Verdite3: complete native scene submission and GPU world rendering

Implementation plan prepared 2026-10-04. This is planned work, not a claim that
the renderer or lighting has been ported. The destination is Verdite3
(`SLUS-00255`), on its `main` development line. This document lives beside the
cross-game sharing plan in Verdite2; implementation findings belong in Verdite3's
own documents and shared changes in their respective subtree documentation.

## Objective and scope

Build the final world-rendering path once: hand-written C# owns the game's scene
enumeration and submission semantics; persistent GPU meshes and pose data supply
the geometry; the GPU handles projection, lighting, fog, depth, and world drawing.
Plan every drawing family before treating any family as finished. This avoids a
release built around recompiled drawing routines, followed by a separate enhanced
C# renderer, followed by another port to retained GPU geometry.

The existing packet path remains the reference and explicit fallback. New native
replacements need a literal, verifiable reference mode, but we do not first build
and ship a complete advanced-lighting implementation on packets. Scene extraction
and numerical checks feed the final GPU path directly.

In this document, a **native replacement** means a hand-written C# replacement for
a recompiled function. A native walk calling recompiled submitters is still
incomplete for this objective.

Required final coverage:

- Map halves, all supported mesh face types, opaque and blended geometry, and
  both the bulk and near paths.
- Creatures, objects, animated and rigid models, effects, billboards, forced
  blending, front-table submits, sky, the arm, and other view-space submits.
- Every reachable drawing context: gameplay, transitions, modal loops, menus,
  shops, dialogue, death, scripted scenes, demo, and the opening/ending overlays.
- Per-pixel lighting, Verdite3's own fog, optional neighbour blending, SSAO with
  geometry normals, texture filtering/mipmaps, and enhancement-distance control.
- The same geometry and material decisions for colour, depth, normals, and
  surface coverage. Additional cameras consume that scene, not new emitters.
- Runtime mutations, area/overlay changes, saved settings, existing smoothing,
  mouse lead, controls, and the game's fixed 15 Hz default world clock.

Packet-owned presentation is an explicit part of coverage: HUD icons/models,
text, menu chrome, message boxes, loading presentation, fades/flashes, and MDEC
movies retain their appropriate draw order and masks. Inventory and shop model
previews get an explicit renderer owner after their callers and draw state have
been read. They must not become an uncounted exception because they use a world
assembler. A world redrawn behind a menu uses the world renderer.

Water effects, planar reflections, and authored remaster lighting/shadows are
extensions of the same scene contract. Their required seams and numerical probes
are included here. Porting the complete remaster editor, pack authoring UI, map
UI, positional audio, and launcher is separate feature work. Renderer completion
must not be advertised as completion of those features.

## Evidence and current starting point

Read against local source: Verdite2 `ebd5412` and Verdite3 `130f870`. Verdite3 has
existing uncommitted pacing/settings/documentation changes; leave those intact.
These SHAs describe the inspection baseline, not an instruction to reset either
checkout. No game run or visual judgement was performed for this plan.

Verdite3 already supplies C# implementations for stage 15, its camera block,
the model walk, MO blending, the bulk map and lit-model assemblers, and the near
map/model assemblers with libgte subdivision. It also has camera/model/clip-time
smoothing, scrolling-texture interpolation, widened culling, depth records,
controls, and the agent harness. Preserve those mechanisms.

Confirmed source facts that determine the port:

- Stage 15 (`800422B8`) has 22 calls and an inline HUD block. The existing walk
  (`80040AE4`) still delegates submission to recompiled functions.
- The map is 80x80, two five-byte halves per cell, 2048 units per tile. The
  current map walk visits a 25x25 window and uses visibility bits differently
  from Verdite2's 24x24 walk.
- Verdite3's light records have a `0x6C` stride, back colour at `+0x64`, and a
  fog near/far pair at `+0x68/+0x6A`. Verdite2's corresponding layout differs.
- Bulk fog is computed from depth and that pair, rather than Verdite2's GTE
  knee/offset curves. Near map polygons go through libgte subdivision and
  have flat face colour; there is no Verdite2-style Clip3FTP/Clip4FTP path.
- The main table has 8192 entries and the front table eight. The front table
  is spliced at main slot 8190. Map packets have a `0xF0` slot bias.
- The model submitter contains both perspective and orthographic transforms.
  Submit flags choose near subdivision or forced blending. `0xF2` objects
  use the front submitter. The current object walk explicitly skips `0xE5`
  and `0xE9`; preserve those branches and inspect other contexts before
  declaring these kinds globally non-rendering.
- There are 28 configured area modules. Arrival in one area does not exercise
  all models, flags, animation segments, rendering contexts, or effects.

Some Verdite3 TODO and sharing-log entries predate completed work. Use source
and task-specific documents to refresh status rather than treating them as a
current implementation checklist.

## Where Verdite2 is practical to reuse

### Shared renderer: reuse first, extend where required

The runtime already contains the retained world backend: `RetainedScene`,
`GpuRetainedMain`, `GlMainView`, `GlRetained`, `GlModelMeshes`, `GlShadows`, GPU
pose storage, world/normal shaders, blending, texture decoding/mipmaps, SSAO,
and planar rendering. Inventory the exact APIs before adding new ones.

At inspection, `GteLightMap.cs`, `AoGeometry.cs`, and `GteDepth.cs` are
byte-identical between the games. These can be used directly. Most of the
rendering backend is already vendored in Verdite3; this is chiefly an integration
task, not a request to copy a second backend into its patches.

The shader/backend copies are not entirely identical. Observed differences
include later Verdite2 fixes for UI ink covering water and surface classification.
Compare subtree pins and their actual changes; take applicable fixes through a
coherent shared-fork update, not loose file copies. Re-run both games' acceptance
checks after shared changes.

Runtime extensions likely needed:

- A generic representation of linear-in-view-depth fog with near/far parameters,
  independent of the existing GTE depth-cue curve selectors.
- Explicit draw domains, table boundaries, sort parameters, and reference cull
  modes for Verdite3's bulk, near, front, sky, and view-space submissions.
- Support for Verdite3's per-corner-normal map face type and its subdivision
  colour/UV semantics wherever existing mesh formats cannot express them.
- Reliable dirty updates, ownership, and epoch invalidation for shared buffers.
- Texture-pack rendering in the retained path if the existing backend otherwise
  falls back to packets when a pack is loaded.

All game addresses, layouts, table sizes/biases, thresholds, env prefixes, and
settings names come from the game adapter. Audit existing runtime assumptions,
including `LibGpu.WaterCut`, table-entry handling, retained-map light packing,
and model shader culls, even where the two games happen to share a number.

### Port adapters from the final Verdite2 implementation

- `RetainedMap`: reuse face decoding, texture rectangles, map identity, dirty
  tracking ideas, light-record upload, and numerical corner checks. Adapt mesh
  selection, the light layout, face kinds, fog, and current tile mutations.
- `RetainedModels`: reuse mesh caches, rigid/animated instances, face metadata,
  stable submission identity, blended ordering, sky and arm handling, and pose
  checks. Replace Verdite2's scratchpad/layout assumptions and assembler tests.
- `MoPose`: reuse deferred materialisation, cached keyframe/delta storage, GPU
  weights, and the materialise-on-demand contract. Extend Verdite3's existing
  `ClipCarry` implementation rather than replacing its working clip timing.
- `GpuWorld`: reuse capability gates, per-domain toggles, diagnostics, and the
  division between extraction and backend drawing. Derive its eligibility
  tests from Verdite3's own drawing state.
- `ScenePass` and `PlanarWalk`/`PlanarCull`: reuse isolated pass state and the
  planar camera mechanism once their Verdite3 counterparts are identified.
- `GpuWorldCensus`, `DrawCensus`, profiler mechanisms, and retained checks:
  reuse measurement designs; seed them with Verdite3 functions and contexts.

Start from the final mesh/pose/instance path, rather than Verdite2's intermediate
per-frame capture of already-transformed vertices. The map's existing packed
world-space mesh is a practical initial backend format: placement at area-build
time is compatible with GPU frame rendering. Prefer dirty chunk/half updates;
evaluate shared per-mesh tile instancing if the existing format cannot meet
mutation and rebuild-cost gates. Record that choice before Phase 3, so it does
not become an unplanned second map implementation.

### Lighting: reuse the mechanism, adapt the inputs

Reuse unclamped light products/dots, BK/LCM generation tracking, shader lighting,
packet-reference validation, and numerical corner probes from `PolyAssemblerLight`
and `GteLightMap`. New GPU draws consume the same light description directly.

The neighbour-selection and bilinear weight logic in `PolyAssemblerFog` is
substantially applicable: the maps share tile dimensions and quarter turns.
Adapt record decoding and blend the inputs appropriate to Verdite3's near/far
fog. Blending a pair and blending evaluated weights are not assumed equivalent;
choose and document the enhancement's intended result.

`CurveWord` permits an early numerical prototype using supplied fog weights,
but its screen-affine interpolation is not proof of correct depth-based fog.
The final shader must evaluate the chosen Verdite3 formula at the fragment's
own depth, with the reference integer/truncation/clamp behaviour understood.

Do not import Verdite2's clipped-half-fog correction: its faulty emitter has
no direct counterpart here. Do not transplant its fog knee, visible-cell bits,
arm bias, water texture rectangles, or model-water classifications.

### Small feature ports

The settings/probe portions of `AmbientOcclusion`, `Anisotropic`, and
`EnhancementDistance` are straightforward adaptations to `KF3_` and `kf3.*`.
The filtering and SSAO mechanisms already exist in the shared backend. Tile
units currently match for enhancement distance, but that scale is still supplied
by the game. Optional `AoWorld` needs a new map/camera adapter and confirmation
of blocker semantics; it is not necessary for the first complete SSAO port.

## Native replacement inventory

Keep the exact current roles and corresponding addresses in Verdite3's
`GAME_INTERNALS.md`. Add a status per entry: recompiled, native reference,
scene-producing native, GPU-owned, exercised, and verified. A hook installed
successfully is a separate check from a path having been exercised.

### Existing native functions to extend

- `800422B8`, stage 15: pass lifetime, draw domains, ordered scene handoff,
  context reporting, and presentation integration; preserve every sound/HUD
  call and existing pacing hook.
- `800357E8`, camera block: immutable per-view camera/projection state; keep
  the existing camera carry and override semantics.
- `80040AE4`, model walk: authoritative source records, stable submit sequence,
  visibility decisions, and complete instance publication.
- `800431E8`, MO blender: pose-store publication and deferred decoding with
  materialisation whenever a guest-memory consumer needs posed vertices.
- `80039D50` and `80035CA4`, bulk map/lit assemblers: complete reference
  metadata, raw light inputs, fog inputs, face/cull rules, and scene identity.
- `8003AB04` and `800366A8`, near map/models: subdivision policies, references,
  and direct GPU submission eligibility.
- The four libgte division entries `80074D88`, `80075188`, `800756A8`,
  `80075B48` and their existing native bodies: exact subdivision and colour/UV
  rules plus reference instrumentation.

### New native replacements required for the complete target

- `8003BFD0`, map walk: preserve its visibility window, boundaries, two-half
  handling, current flags, ordering, and any side effects; publish visited halves.
- `8003BB04`, tile-half submitter: publish placement, light/fog record identity,
  rotation, near/bulk selection, and bank/mesh; skip the geometry work only after
  the GPU has accepted the full submit. Identify intermediate cell helpers
  from current generated code; replace those that own required work.
- `8003E34C`, main model submitter: own matrices, light selection, perspective/
  orthographic branches, pose choice, flags, bias, and assembler selection.
- `8003F304`, front-table submitter: separately preserve its table, transform,
  depth/facing rules, ordering, and all its callers, including kind `0xF2`.
- `800400AC`, sky submitter: capture its lighting, table order, object record
  copy, redraw-after-table branch, and fog exclusion.
- `8003DF50`, first-person arm: preserve animation, transform, light lookup,
  near handling, and painter-order runs; implement a GPU view-space instance.
- `80037BEC`, forced-blend assembler; `80038844`, front-table assembler; and
  `80039428`, sky assembler: literal reference replacements with all face kinds
  and exceptional branches represented in the scene.
- Any newly discovered world emitter or submitter outside this list: identify
  its overlay and context, then add a native replacement and a scene path before
  closing coverage. This includes direct packet construction in area modules.

Helpers concerned only with queries, sound, loading, or non-rendering game logic
can remain recompiled when their semantics are preserved. A complete GPU drawing
path does not require rewriting the game's simulation.

HUD and preview functions require domain hooks and caller audits. Rewrite one
only if it owns rendering work needed by the selected domain policy. Document
each permitted packet-domain caller; do not exempt all calls to a shared assembler.

## Final scene and rendering contracts

Use a compact, game-neutral runtime scene API with per-game producers. Keep the
CPU's scope to scene enumeration, simulation side effects, small instance/light
updates, culling decisions that are game semantics, and necessary order keys.
Mesh decoding and pose-stream decoding are cached; repeated per-frame vertex
uploads or GTE transforms are not the default rendering path.

The scene contract must retain:

- Asset identity: overlay/area epoch, bank generation, mesh content identity,
  face topology/type, original normals, UVs, CLUT/page, blend bits, texture rects.
- Instance identity: source table/slot/record, model, stable sequence, transforms,
  world/view space, submit family, flags, table, bias, light state, and pose key.
- Tile identity: area/cell/half, rotation, height, mesh, light record/neighbours,
  visibility, near-path selection, and game rewrites.
- Per-view state: immutable camera, projection H/centre/aspect, reference integer
  placement and saturation policy, display target, clipping, cull result, and
  drawing epoch. Store it for the target actually presented, not just the latest
  frame the CPU constructed.
- Draw policy: domain, opaque/blended/sky/arm category, complete console blend
  mode, intra-slot order, render-state barriers, depth/mask behaviour, colour,
  normal/surface eligibility, and texture-pack eligibility.
- Lighting: rotated light matrix, colour matrix, back/far colour, RGB input,
  per-face/per-corner normals, native fog description, optional neighbour blends,
  and material/light identifiers reserved for extensions.
- Pose ownership: clip/segment/weight, cached keys/deltas, interpolation policy,
  integer wrap/shift semantics, and explicit deferred/materialised state.

Two consumers use the same decoded descriptions: the literal native reference
for verification/fallback and the retained GPU renderer. Factor shared decoding
only after equivalence is established; keep guest register/memory operations in
the per-game reference bodies. Use existing runtime stores wherever they can
express the contract rather than adding a competing scene graph.

## Implementation phases and completion gates

### Phase 0: close the drawing inventory and establish the baseline

1. Audit current generated functions, native replacements, direct/indirect
   callers, SDK draw entry points, and all overlays. Trace world submission from
   the main/front tables as well as direct GP0/DMA drawing outside stage 15.
2. Extend the existing geometry probe with caller/overlay/domain attribution,
   transformed-vertex counts, packet bytes, face kinds, submit flags, table and
   instance counts, and whether a native replacement actually ran.
3. Enumerate the content of all 28 area modules and their available meshes,
   object kinds, face modes, and pose clips. Keep generated census data ignored;
   record the meaningful findings and unresolved entries in docs.
4. Establish gameplay, area transitions, save/load, boot/movie, paced/smoothed
   operation, controls, and reference performance on the current build.
5. Prepare reproducible scene fixtures and safe command driving. The current
   shell lacks load/warp/pause; implement only the needed game-thread verbs from
   Verdite3's known loader/debug-mod mechanisms, or use documented manual steps.
   Do not assume Verdite2's commands exist. Verify actual area/overlay and loaded
   state after every transition; use copied cards/settings and bounded runs.

Gate: every known emitter and draw context has an owner and verification route.
Unreachable cases are listed as open, not called covered. Inspection and runtime
exercise are reported separately. Establish counters with GPU off so a zero with
GPU on cannot mean the probe failed to attach.

### Phase 1: finish the shared-runtime contract

1. Compare actual shared-subtree revisions and applicable late fixes.
2. Define/pass game drawing parameters and the new fog description. Document
   console-exact and enhanced semantics, and supported GL/backend capabilities.
3. Check mesh/instance representations against all Verdite3 face types,
   perspective/orthographic branches, table families, and far-colour behaviour.
4. Define target/pass lifetime, resource disposal, content epochs, and dirty
   ownership. Retained state must not survive an incompatible overlay/area.
5. Choose the retained-map storage/update strategy once, with the existing
   packed/chunk format preferred where it meets the gates. Record any required
   tile-instance extension and its practical cost before implementing it.

Gate: contracts can express every Phase 0 drawing family and lighting state.
Shared changes build and pass Verdite2's acceptance checks; no game-specific
address or setting name is introduced into runtime code.

### Phase 2: native submission and numerical references

Implement the new native functions listed above and extend the existing ones.
Start with literal register, scratchpad, RAM, GTE, return-address, and call-order
behaviour. Bind calls through hooks as current code does. Establish equivalence
before factoring arithmetic or suppressing geometry work.

Instrument the reference at the original operations to record transforms,
normals, unclamped lighting, raw fog, facing decisions, table keys, source face,
subdivision output, and state barriers. This is verification infrastructure,
not a separate advanced renderer to ship.

Gate: references match recompiled behaviour for RAM, scratchpad, promised CPU
registers, GTE, packet/table writes, and important callee order. Stack scratch
bytes can only be excluded after demonstrating they are dead. Every new branch
has static review and an execution/numerical fixture. Verify modes suppress
enhancements and GPU substitution so deliberate improvements are not confused
with transcription mistakes.

### Phase 3: retained map, meshes, poses, and mutation handling

1. Implement Verdite3's retained map adapter with all face kinds, light records,
   rotated normals, half identity, and complete near/bulk rules.
2. Implement model mesh caching and direct instances for every submit family.
3. Extend the existing MO blender with GPU pose publication, deferral and
   materialisation. Preserve fractional clip carry, loops, segment transitions,
   morphs, and arm timing. Prove all guest-memory readers of posed buffers are
   accounted for before omitting their writes.
4. Publish the carried camera and object placement used by the presented frame.
   Resolve ticked-position culls at moving boundaries as an explicit policy;
   preserve the game's occlusion/flood semantics.
5. Separate dirty asset bytes/topology, tile records, lighting, poses, materials,
   texture/CLUT contents, and visibility. Light changes update light data;
   instance movement updates instances; neither rebuilds every mesh. Detect
   mid-area doors/lifts/rewrites, bank reuse and runtime-mod modifications.

Gate: stable assets upload once per epoch; changing only pose weight needs no
per-vertex frame upload. Dirty updates leave unrelated assets intact. GPU pose
results match native integer operations on representative vertices for every
available clip/segment, including negative deltas and wrap boundaries. Unknown
or unsupported data produces a counted fallback, never an invisible skip.

### Phase 4: complete main-view GPU geometry

Implement whole drawing families against the final scene contract:

1. Opaque map and models, then near subdivision geometry, with reference
   placement, clipping, face culls, table-range decisions, and texture seams.
2. All four console blend modes, STP/transparent-texel behaviour, forced blend,
   mixed-mode meshes, effects, and camera-facing billboards.
3. Main/front table integration, same-slot last-linked-first ordering, GPU face
   order relative to remaining packets, render-state barriers, and the sky copy/
   redraw branch.
4. Arm and other view-space instances, including near-camera saturated divide,
   intended painter order, and background-depth masking.
5. Packet-domain presentation and standalone model preview handling.

Read Verdite3's subdivision arithmetic before substituting unsplit GPU triangles.
Subdivision can change UV truncation, flat face colour and ordering even with
perspective correction. Cache compatible topology, represent it in the GPU
path, or implement a proven equivalent; an untested near-plane improvement is
not a substitute for complete near-path support.

Gate: every available face type and submit flag has a GPU path and exercised
checks. All known world emitters report GPU ownership. Residual world packet
emission is listed by caller and reason; opaque-only completion does not pass.

### Phase 5: lighting and all auxiliary buffers

1. Match native light products/dots, back/colour matrices, rotated tile light,
   source colour, far colour and clamp order, for flat and Gouraud faces.
2. Implement the game's bulk and near fog policies explicitly, then the intended
   per-pixel enhancement. Check formulae at corners and interior depths, including
   equal near/far, the 32000 cutoff, clamp boundaries, and changing light records.
3. Add neighbour fog/light blending as a reversible enhancement using source
   records each frame/update. Handle missing neighbours, both half levels,
   quarter turns and shared boundaries; neutral/off restores native inputs.
4. Draw the accepted geometry into colour/depth and normal/surface targets with
   identical projection, clipping, posing, texture holes, blend classification,
   domain masks, and target lifetime. Doors and other blended solids need their
   intended surface/depth treatment.
5. Wire SSAO quality, filtering/mipmaps and enhancement-distance controls.
   Verify minified world textures, clamped texture rectangles, scrolling textures,
   dynamic CLUT/VRAM updates, and unfiltered UI behaviour.
6. Support the existing texture-pack path and selectors, or leave pack support
   explicitly incomplete. A loaded pack quietly disabling all GPU drawing does
   not satisfy full renderer coverage.

Gate: numerical light checks are exact where the native arithmetic permits it;
any float tolerance is justified per operation and recorded. Interior fog/light
checks prove the enhancement runs where it matters. Surface/depth/normal counters
prove every eligible drawing family contributes. The user separately judges
lighting transitions, seams, near geometry, SSAO and transparency.

### Phase 6: additional views and extensions on the same scene

Design and validate pass isolation during the main renderer work. Add a second
camera execution to prove geometry/pose/light caches are view-independent, and
that drawing it cannot alter simulation, sound, cels, guest matrices or the
main frame's state.

For an actual planar-reflection port, use Verdite2's chosen `PlanarWalk` mechanism
and per-view cull, adapted to Verdite3. Implement all main-view families in the
mirror, including blended faces, water, sky and appropriate view-space exclusions.
Water classification must come from this game's scrolling slots/material rules
and geometry, not texture reuse alone. Full murk/wave/swell integration and its
tuning are a feature milestone of their own, not implied by importing shaders.

Authored lights/materials and shadow passes take geometry from this same scene.
Reserve identity and material bindings now; exercise a small diagnostic light
and shadow pass when that extension is implemented. The authoring pack/editor
port remains separate. Optional world AO also consumes an explicit grid adapter.
No new screen-space/temporal reflection system is needed.

Gate: second-view execution and numerical checks demonstrate pass isolation and
reuse without re-running world simulation. Reflection/water/remaster extensions
are marked complete only after their own content, buffer and user checks; their
absence must not conceal an inability to render a second view.

### Phase 7: whole-game coverage, performance, and adoption

Execute the coverage programme below. Close unsupported paths and unexpected
fallbacks before enabling the renderer by default. Retain the reference backend
and domain comparisons for later regressions; keep their diagnostics available
without imposing their per-vertex work on normal GPU frames.

Gate: all known reachable world contexts have exercised GPU paths; remaining
packet-owned presentation is specifically enumerated. Unreached content or an
unknown branch is a reported limitation and blocks an unqualified full-coverage
claim. Preserve a working reference for unsupported backends/capability settings.

## Verification and acceptance programme

### Numeric and behavioural checks

- Native transcription equivalence, including the 1 KB scratchpad. Use the
  current shared `Differential` mechanism where it already supplies the needed
  snapshots; extend it narrowly when it does not.
- GPU projection against the native GTE: integer matrix arithmetic, SZ/IR flags,
  saturated perspective divide, fractional positions, negative/near depth,
  winding, diagonal choice, near thresholds, and wide-aspect screen bounds.
- CPU/GPU light and pose formula agreement using buffer/counter probes. Shader
  checks must evaluate the actual shared shader code or a shared implementation,
  rather than independently copying a convenient formula into a test.
- Subdivision topology, UV rounding, flat/Gouraud colour policy, table keys,
  all blend modes, stable within-slot order, and draw-state barriers.
- Runtime mutation/invalidation, repeated area changes, slot reuse, save/load,
  context loss/resource recreation where supported, live setting changes,
  fallback toggles, and packs loading/unloading.
- Fixed-world timing at default 15 Hz and several drawn rates, including 60 and
  144; repeat with smoothing and mouse lead enabled/disabled. Preserve the
  current user's tick-rate comparison controls and pending pacing edits.

World ownership counters must include native GTE projections, CPU-produced world
packets, fallback reasons, mesh/pose uploads, GPU instances and draw domains.
Outside reference/diagnostic mode, ordinary GPU-owned world draws should produce
no legacy world packets or per-vertex native GTE projection. Small CPU order-key
calculations are counted separately; sorting blended faces is not hidden as a
successful removal of all CPU work. The packets permitted for presentation are
reported by caller/domain, not filtered away from the census.

### Content and context coverage

- Visit all 28 areas with actual area/overlay confirmation and representative
  headings, near views, upper/lower halves, busy views and mutable geometry.
- Exercise rigid and animated creatures/objects, available clips and segment
  changes, doors/lifts, mixed face modes, front-table objects, sky branches,
  effects, billboards, view-space instances, attacks and spells.
- Exercise title/intro/demo, new game, all relevant save/load routes, transitions,
  menus/submenus and their previews, shops, pickups/item use, signs/dialogue,
  fades/flash effects, death, scripted/boss sequences, credits and ending.
- Content discovery can use static bank enumeration and synthetic numerical
  fixtures for rare arithmetic branches, but neither proves that its actual
  gameplay caller/side effects have been exercised. Keep both statuses.
- Separate supported combinations: GPU/reference, pacing/smoothing, 4:3/wide,
  perspective/subpixel/depth switches, per-pixel/neighbor-blend/AO settings,
  filtering, texture packs, and enabled runtime mods. Test risk-based combinations
  rather than claiming exhaustive coverage from one configuration.

Use the safe game-thread command channel where sufficient and manual user steps
where it is not. All agent launches are bounded; one controlled game instance
owns the shell port. Do not change the user's normal cards/settings to build the
coverage corpus. Do not mistake a rejected or bounced warp for coverage of its
requested destination.

### Performance and visual acceptance

Record CPU extraction, simulation, sorting/submission, upload bytes, mesh/pose
rebuild counts, per-pass GPU time, draw calls and frame-time spikes. Compare the
same scenes/settings at uncapped or a high rate; a 144 fps cap conceals cost.
Include first-use/JIT, area changes, busy translucent scenes and additional views.
Establish budgets from this hardware's baseline rather than importing Verdite2
FPS claims. Any remaining expensive blended batching is visible in the report.

Visual judgement belongs to the user. Provide short scenario/check lists for
near texture seams, lighting/fog transitions, missing/popping faces, arm layering,
sky ordering, translucent doors/effects, water, HUD/text coverage, and menus.
Do not capture/screenshot/scrape the game window for agent visual inspection.
Numerical buffer probes and counters establish mechanisms; they do not establish
that the picture looks right. Record measured and user-judged separately.

Completion requires both mechanism checks and user acceptance for changes whose
result is visual. A missing observation is an open item, not a favourable result.

## Compatibility, configuration, and integration

- Keep implementation on Verdite3's appropriate development line; protect its
  existing dirty files. This planning task changes no implementation or pins.
- Preserve `KF3_*`, `kf3.*`, existing control/smoothing types, runtime-mod surface,
  save data and settings. Scene state stays host-owned; a render pass never writes
  into saves or advances gameplay.
- Introduce a master GPU-world comparison and focused domain/probe controls,
  documented in `ENV_VARS.md`. Use `Program.cs` for composition/diagnostics and
  declare hook order. Confirm committed attachment for every required target.
- Register useful user settings through the current settings framework, with
  English, pt-BR and es-419 strings. Keep diagnostic/reference selections in
  Testing. Effects remain reversible and defaults follow user visual acceptance.
- Define explicit fallback when the backend or required perspective/depth/native
  capabilities are unavailable. Report the reason. Development treats a fallback
  as incomplete coverage unless it is an intentional supported configuration.
- Shared-runtime and Verdite Core edits are game-neutral, isolated subtree
  commits, with corresponding documentation and pins. Extract only proven shared
  decoding/mechanisms; keep addresses and guest control flow in the games.
- Preserve Verdite2 behaviour and acceptance after every shared change; use its
  existing settings/env/save/mod names. Public pushes and enabling unjudged
  defaults are separate actions governed by the sharing workflow.
- Update Verdite3 `NOTES`, `GAME_INTERNALS`, `GEOMETRY`, picture/GPU documentation,
  `ENV_VARS`, `DEVELOPMENT`, and TODO as each gate closes. Keep cross-game progress
  in `SHARING.md`. Never replace a measured status with a historical summary.

## Work breakdown and practical priorities

The largest work is the Verdite3 native submitters, precise near/subdivision
semantics, complete blended/front/view-space coverage, and whole-game verification.
Reusing Verdite2 removes much of the backend and caching work; it does not remove
the need to understand those game-specific paths.

The comparatively small pieces are feature switches/settings, SSAO wiring,
filtering controls, enhancement distance, and basic shared pose-store integration.
Neighbour blending and mutable-map handling sit between these groups because
the layouts are close while the fog and lifetime rules require fresh checks.

Implement in reviewable units using the phase dependencies above. Each unit
delivers its final scene/GPU contract and its reference evidence. Early opaque
draws are development checkpoints; completion includes the initially planned
near, blended, front, sky, view-space and non-stage-15 paths. Do not postpone
these as a second renderer project.

Before implementation, Phase 0 must resolve the unknown callers, available face/
flag variants, standalone preview policy, sky redraw behaviour, and mutations.
Those findings can refine individual units without reopening the agreed final
GPU-renderer target. No reliable calendar estimate is available until that audit.

## Source guide

Verdite3:

- `docs/GAME_INTERNALS.md`, “The geometry path”, especially stage 15, map,
  models, tables and scratchpad; `docs/GEOMETRY.md` and `docs/PICTURE.md`.
- `patches/Stage15.cs`, `CameraBlock.cs`, `ModelWalk.cs`, `MoPose.cs`,
  `PolyAssembler*.cs`, `NearPath.cs`, `NearPathDivide.cs`, `CullCone.cs`,
  `NearScreen.cs`, `ModelSmoothing.cs`, `ViewSmoothing.cs` and `TextureScroll.cs`.
- `patches/GeometryProbe.cs`, `MapCoverage.cs`, `AgentServer.cs`, and
  `mods/kf3debug/Warp.cs` for existing coverage/driving mechanisms.

Verdite2:

- `docs/GPU_RENDERER.md`, especially the final map light-record path, mesh/pose/
  arm/blended/sky/near/mirror slices and the fallback census; `docs/RENDERING.md`.
- `patches/RetainedMap.cs`, `RetainedModels.cs`, `GpuWorld.cs`, `MoPose.cs`,
  `TileWalk.cs`, `ModelWalk.cs`, `ScenePass.cs`, `PlanarWalk.cs`, `PlanarCull.cs`,
  `PolyAssemblerLight.cs`, `PolyAssemblerFog.cs`, `GpuWorldCensus.cs`,
  `AmbientOcclusion.cs`, `Anisotropic.cs`, and `EnhancementDistance.cs`.
- Both games' `tools/RecompOne/RecompOne.Runtime/Gpu/` and backend sources;
  shared `LibGpu.WalkOTag` and the actual world/normal/fragment shaders.
- `docs/SHARING_PLAN.md`, `SHARING.md` and `SHARING_INVENTORY.md` for subtree
  ownership and cross-game compatibility requirements.
