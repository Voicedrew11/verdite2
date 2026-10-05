# Changelog

## v0.4.0 (2026-10-03) — changes since v0.3.3

86 commits, 2026-09-25 to 2026-10-03. Mechanisms are measured; where noted, the picture has not been judged by eye.

### Added

**Remaster packs and editor** (off unless a pack is loaded)
- In-game editor (Shift+E), reworked: docked at the picture's right edge under a pinned header with six tabs, light gizmos with axis arrows, and Material and Atmosphere tabs that show what they assign. The editor has its own free camera.
- Materials per tile face, with 256 ids: roughness, metalness (a tinted mirror), specular, occlusion strength, glow, pulse, and an emissive light with its own colour. They can be keyed to a tile half, a mesh face, a model kind and id, or a texture, which applies in every area.
- Authored point and spot lights. The map, creatures and objects cast shadows from them, and a door blocks light until it opens.
- Per-area **Darkness** slider and light-record overrides. Darkness scales the game's own light.
- Fog colour and curve, plus a sky. Surfaces fade into the fog colour instead of black.
- Props: place an area's own object models from `props.json`.
- Pack layers: stack any pack in `packs/` under the working pack, per key, each with its own switch.
- Pack export as a zip, and a per-area compatibility report.
- Tile edits from `level.json`, off by default (`KF2_REMASTER_LEVEL=1`). A raised floor becomes the floor, a raised wall stops the player, and the block never reaches a save.
- Texture replacements keyed on the image the game uploaded, so scrolling water can be retextured and still scroll.
- Object and creature placement was dropped from the remaster scope.

**Water**
- Waves: an interior swell plus per-pixel ripples, so the repeated 64×64 water tile no longer reads as a grid.
- Murky water: water darkens by the view ray's run to the floor.
- Planar (mirror) reflections are now the reflection. They sit at the water's rest height and have their own cull. Authored lights, even fog and mip detail appear in them. Reflection strength slider.
- Each of murk, screen-space, planar and world reflections has its own switch.

**Other**
- Render distance past the game's 24×24 window, reflection reach, and an enhancement distance that restores the game's own look beyond a set depth. All three are off by default and not yet judged by eye.
- **Unlimited** option on the frame-rate slider (882–938 fps at 20.0 ticks/s).
- Gear comparison is built into the port, on by default (Gameplay ▸ Compare gear). The equip and buy prompts show every stat the item would change.
- `snap` and `aspect` shell verbs.

### Changed
- **GPU geometry, planar reflections, murky water and water waves now default on.** The Z-buffer tolerance and water sliders are removed, and the water checkboxes moved into Enhancements.
- The GPU geometry checkbox moved under Fast geometry and dims without it.
- The compass needle and HP/MP gauges animate at the render rate. Stage 13's hooks run in a declared order, and the needle's spring steps once per tick, so it no longer stiffens as the frame rate rises.
- The reflection march, retained-scene reflections and reflection reach are no longer user settings.

### Fixed
- Fish, bones and sea floor under water no longer paint over it.
- Murky water: tile cracks closed, and the first-person arm, pillars and see-through boxes now draw correctly. Area 7's dark hexagon and area 4's crystal mismatch are gone. Murk applies only to level water.
- Slimes and other skins in the water's texture are no longer treated as water, so they are not murked or reflected.
- Dialogue text is preserved over water murk.
- The planar mirror no longer drifts or jitters with the swell, and nothing pops in lit at the cull edge or screen corner.
- Retained reflections no longer show floors from underneath or dropped chunks.
- Creatures no longer show through walls in ambient occlusion.
- A floor clipped at the camera's feet no longer fogs to black.
- Wall panels overlapping in one plane no longer fight pixel by pixel.
- Changing the aspect ratio in play no longer drops to the 4:3 VRAM fallback.
- A crash is fixed: the mirror replay could draw a model the area loader had evicted mid-walk.
- Models are no longer darkened by a wrapped light-constant ring.
- A doubled vblank event (`0x801B6CAC` ran 120×/s, now 60×/s), via the RecompOne fork fix.
- An effect in front of a crystal is no longer drawn behind it (area 4).

### Performance
- A GPU world renderer now draws the map, its water, opaque and lit models, the sky, the first-person arm, blended faces and the planar mirror. Pose blending also moved to the GPU.
  - Area 7 frame work: 5.19 → 3.26 ms.
  - fdat02 pool with mirror: 3.77 → 1.82 ms.
  - Mirrored walk: 1.72 → 0.40 ms.
  - MO pose blender: 0.055 → 0.003 ms.
  - Stage 13's object walk submits 0 polygons per frame at arrival in every area.
- Area 6 no longer rebuilds its map 20×/s (665 → 900 fps). Area 7's light-record fade is a 0.013 ms upload instead of a 13 ms rebuild.
- Shadows redraw far less: turning in place redraws nothing where it once redrew 357 of 475 frames.
- Swinging the first-person arm runs about 3% faster.

### Internal
- RecompOne and Verdite Core are now `git subtree`s (`setup_tools.sh --pull-fork/--push-fork/--pull-core/--push-core`).
- Sharing plan and handoff logs with Verdite1 and Verdite3.
- Stage-13 draw census, GPU profiler timing, and a light census.
- `callgraph.py` and `check_gate.py` fixed so the gate cannot pass vacuously.
- Windows package licence path fixed after the subtree move.
