# The port's changes to RecompOne, one by one

`patches/recompone/*.patch` are no longer replayed (the tree is vendored — see
`docs/RECOMPONE_FORK.md`), but they are still the record of what the port changed
in the runtime and the recompiler and why, and the numbering is still how each
change is referred to in the source. `docs/RUNTIME.md`'s "The patches to the
checkout, one by one" covers the early ones at more length; this list is the
complete one.

Forty-one of the forty-five are load-bearing; `0002`, `0003` and `0015` are
diagnostics and `0013` is a settings-placement hook. **Three force a recompile** —
`0004`, `0035` and `0037`; every other one changes runtime behaviour only. **One
patch has an asset beside it**: `patches/recompone/assets/` holds the TTF `0033`
embeds, which is now simply a tracked file in the vendored tree.

Four files in the directory have no entry below:

- `0002-cdtrace-diagnostic.patch` — names the function behind a CD register
  access (`KF2_CDTRACE=1`).
- `0003-libgpu-sdk-trace.patch` — `Log.Sdk` tracing for `LibGpu`, plus a `Log.Gpu`
  line for every GP1 write.
- `0014b-zbuffer-depthmap-comment.patch` — restores four comment lines whose
  presence `0015`'s context assumed.
- `0021-vblank-wall-clock.patch` — advances the emulated vblank on a wall-clock
  60 Hz grid rather than when the game asks; `KF2_VSYNC=block` is upstream's
  blocking timeline. See "The vblank fired when the game asked" in
  `docs/RUNTIME.md`.

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

- `0021-true-color-24bit-output.patch` — the console renders into 15-bit VRAM, so a
  smooth shaded fog gradient bands into 32 levels unless the ordered dither hides
  it with a crosshatch. Two things enforce the truncation: the `GlDisplayRt` colour
  attachment is `Rgb5A1`, and the fragment shader's `quant5` ends in
  `min(c8 >> 3, 31) / 31.0`. Under `GteDepth.TrueColor` the attachment becomes
  `Rgba8` and `quant5` keeps eight bits, so the gradient is smooth without the
  crosshatch. Textures stay 5-bit (they live in 15-bit VRAM), so only the shaded
  gradient gains precision; the writeback/present blits convert automatically.
  GL backend only — the software rasterizer is always 15-bit. Off by default (the
  authentic look). `patches/TrueColor.cs` (`KF2_TRUECOLOR`) is the switch and
  `patches/settings/ShadingPage.cs`'s combo is where it is chosen. **No recompile** —
  render-target format and shaders are runtime. See "True color" in
  `docs/RENDERING.md`.

- `0022-present-stale-wide-target.patch`, `0023-splash-margin-idle.patch`,
  `0024-margin-content-latch.patch` — the present gate. `PresentDisplay` picks a
  wide render target only when its margin columns have carried a world, so a scene
  that never draws out there (the MDEC boot splash) keeps its authored width
  instead of flapping between widths. Latching is per target, and it is cleared
  when an **executable** loads — from `patches/Widescreen.cs`, not from
  `Dispatcher.Load`, which fires for the `fdat` area modules too and put the black
  bars back for the length of every area transition. **No recompile.** See "The
  present gate" in `docs/WIDESCREEN.md`.

- `0025-frameclock-target-rate.patch` — `FrameClock.FrameMs` was a `const` 60 Hz,
  and it is the *host* rate: the emulated vblank grid (`LibEtc.VBlankMs`) and every
  game clock hanging off it are a different 60 that must not move, or the music
  speeds up. Now a settable `FrameClock.TargetFps` (0 = off), exposed as
  `Runtime.TargetFps` because `FrameClock` is `internal`. It still cannot be a
  frame pacer — it throttles per `VSync` *call* and a frame carries two — so the
  port hands it a permissive ceiling and keeps its own deadline at `DrawOTag`.
  **No recompile.** See "There were three fixed 60s" in `docs/RUNTIME.md`.

- `0026-str-pacing-without-a-latch.patch` — an STR movie is paced by the disc:
  sectors arrive at 150 a second, a frame is 9-14 of them, and the game's display
  loop blocks in `StGetNext` until one is complete, so the loop's own rate never
  enters into it. `StreamLoop` modelled that, but only once a latch tripped — two
  decoded frames sitting in the 32-slot ring at the same time — which for the third
  intro movie's 13-14-sector frames never happened, because the game drains a frame
  as soon as it is ready. Unthrottled, that movie played at the **render rate**
  instead: measured ~56 frames a second at `KF2_FPS=60` and ~93 at 144, against the
  10 the disc holds it to. Paced from the start of the stream now, since there is no
  free burst on hardware either. **No recompile.** See "The intro movie ran at the
  render rate" in `docs/RUNTIME.md`.

- `0027-commit-each-hook-independently.patch` — `HookManager.Commit`'s `foreach`
  installed each detour with no guard, so the first `new Hook` that threw abandoned
  every function after it in the dictionary. Silently: a patch that hooks from an
  `OverlayLoadedEvent` listener — which is all of them, since `SymbolRegistry` is
  only readable once the dispatcher's overlay tables are registered — has its
  exception swallowed by `Event.Dispatch` into one stderr line. Losing
  `FramePacing`'s frame boundary that way reads as *the whole game running fast*,
  because `Floor()` and `_tickThisFrame` both hang off it and `_tickThisFrame`
  fails open. Each function is committed on its own now and a failure names the
  method. **No recompile.** See "Everything hung off one hook" in
  `docs/PATCHES_AND_MODS.md`.

- `0028-hook-manager-commit-state.patch` — `AddPre`/`AddPost` only append a
  delegate and return `true`; the detour is created later in `Commit`, which since
  `0027` fails per function without throwing. A patch counting `Add*` returns
  therefore claimed what it had *queued* — `FramePacing` could print
  `boundary 3/3 DrawOTag + 3/3 VSync` with no boundary installed, latch itself
  done, and never retry, which is the uncapped-picture-and-8×-world failure.
  `IsRegistered` and `IsCommitted` let a caller read back what actually landed.
  **No recompile.** See "A registration is not a hook" in
  `docs/PATCHES_AND_MODS.md`.

- `0029-output-panel-image-rect.patch` — the game picture is not the window, and
  nothing outside `OutputPanel` knew where it was. It is an `Image` inside that
  panel, fitted to the panel's content region at the display's aspect and centred
  in it, so the menu bar, the dockspace border and any docked panel take their
  share off it and a 4:3 picture in a wider window has a bar either side. An
  overlay could therefore only anchor to the viewport, which is why the
  full-screen map covered the port's own chrome and lined up with neither. A
  public `OutputView` publishes the rectangle from the one place that computes it,
  once a frame, and is invalid when the panel drew no picture so a caller can fall
  back to the viewport. It also publishes **`GameW`/`GameH`**, the picture's size
  in the game's *own* pixels, off the `SetTexture` call that already receives
  both: `Min`/`Max` alone are a rectangle and not a scale, so nothing could turn
  a window pixel back into a game pixel — which is what the menu pointer needs to
  ask which item is under the cursor. UI only — **no recompile**. See "A dynamic
  map" in `docs/PATCHES_AND_MODS.md` and "The menu pointer" in `docs/INPUT.md`.

- `0030-expose-host-pump.patch` — the shipped launcher has to build the game
  before there is a game to run, and that blocks for seconds; a window that stops
  pumping for seconds is one the desktop offers to force-quit. `HostWindow.Pump`
  already does exactly the right thing and is `internal`, and `WaitForValidDisc`
  already runs that loop but only ever for its own condition. Exposed as
  `Runtime.Pump`. Everything else the progress UI needs was public already —
  `Popup` is abstract-public and `PopupManager.Register` takes any implementation
  — so `Verdite2.Launcher/BuildProgressPopup.cs` is not a patch. UI only, **no
  recompile**. See "The one patch this needed" in `docs/PACKAGING.md`.

- `0032-expose-pad-queries.patch` — `InputManager` is `internal`, so a port
  drawing its **own** binding table could not ask whether a pad is connected or
  what is held down on it. Both methods were already `public` on that class, so
  unlike `0017` nothing had to be added there — only the two forwards from
  `HostWindow`, beside `IsKeyDown` and the mouse block. Two lines against `0017`'s
  sixty. UI only — **no recompile**. See "The Input pane is the port's" in
  `docs/INPUT.md`.

- `0031-output-panel-fills-its-dock-node.patch` — the picture is the point of the
  Output panel, so it gets none of the chrome every other panel wants. The themed
  `WindowPadding` (12,10) and the 1px `WindowBorderSize` are read by `Begin` when
  it computes the inner rect, so a docked, tab-bar-less Output panel filling the
  dockspace still letterboxed the game behind a band of window background on all
  four sides — scaled by `Theme.Scale`, so widest exactly where the DPI is misread
  highest. Both are pushed around `Begin` only and popped straight after it, so
  the toasts drawn below still lay themselves out on the real style and no other
  panel is affected. UI only — **no recompile**. See "The picture is inset
  inside its own panel" in `docs/RUNTIME.md`.

- `0033-sans-serif-interface-font.patch` — ImGui's built-in face is ProggyClean,
  a 13 px bitmap: it is pixel art, it does not scale (every other size is a
  stretched bitmap), and it makes the port's own settings window read as a debug
  overlay laid over the game. This is upstream's own fix back-ported —
  RecompOne `aaf7be0`, which our pin `870c5ba` predates — so `Icons` becomes
  `FontSet`, Noto Sans is embedded and merged with the Font Awesome range, and
  the size goes 13 → 16 px. **Upstream's CJK face is deliberately not carried**:
  it is a second 16.5 MB resource, and every string in the runtime's three
  languages (en, pt-BR, es-419) is Latin, so it would cost 16 MB in every release
  artifact to render nothing anyone can select. Cyrillic, Greek and Vietnamese are
  kept, being Noto's own coverage and only atlas space — they are what a path or a
  mod name falls back to instead of boxes. A missing resource falls back to the
  bitmap font rather than to no text. The font is OFL 1.1
  (`patches/recompone/assets/NotoSans-OFL.txt`, which the packaging must ship).
  UI only — **no recompile**. This is the one patch that *wants* to stop applying:
  when the pin moves past `aaf7be0` it is upstream's, and the right response to
  `FAILED TO APPLY` here is to delete it. See "The interface's font" in
  `docs/RUNTIME.md`.

- `0034-pgxp-value-tracking.patch` — **upstream's PGXP, backported.** RecompOne
  grew a real PGXP after our pin (`39fb337a`, `91c20fcf`, `95f0585b`, `6aae910a`,
  2026-08-31 to 09-07): the GTE's own divide publishes a float screen position and
  view depth, and those follow the value through the CPU's registers and a
  `PgxpValue`-per-word RAM shadow to the GP0 packet. `RecompOne.Runtime/Pgxp/` is
  verbatim from `6aae910a` apart from living one directory up, beside `GteDepth`
  rather than under it; the edits are `Gte.Rtp` publishing the precise vertex,
  `Nclip` doing backface culling on precise positions, the transform-serial ring,
  `PSMemory`'s ctor sizing the shadow and `Runtime.Run` initialising the vertex
  cache. **Upstream's frame interpolation is deliberately not carried** — it is a
  separate experimental feature that arrived in the same commit. Inert until
  something turns it on. **No recompile.**

- `0035-pgxp-cpu-hooks.patch` — the recompiler half, and one of three patches that
  **force a recompile**, with `0004` and `0037`. `InstructionEmitter` emits
  `if (Pgxp.CpuTracking) PgxpCpu.X(...)` beside every load, store, move, shift,
  add, multiply and divide, which is what makes PGXP's coverage a fact rather
  than a rate. The gate is emitted rather than taken inside the hook, so with PGXP
  off the cost is a predictable branch. Loads and stores hold the address in a
  local rather than emitting the expression twice — upstream evaluates it again
  after the access, which hands the hook the wrong address for `lw $t0, 0($t0)`.
  Measured: 68,188 hook sites in `game.cs`, no change in generated line count
  (the hooks append to existing lines), build 15 s → 37 s.

- `0036-pgxp-vertex-and-depth-source.patch` — where the two mechanisms meet.
  `DrawPolygon` asks PGXP when it is on and `GteVertexMap` when it is not, filling
  the same `Vert` fields either way, so `HleVertex`, both rasterizers and the
  shaders are untouched by the choice. Three things are ours rather than
  upstream's: the **tolerance is actually spent** (upstream defines
  `pgxp.tolerance` and never reads it — here a recovered position more than that
  many pixels from the packet's is refused, because it is a different vertex
  rather than a better version of this one), **`PgxpStats`** counts where each
  answer came from, and **a depth-tested triangle is given a real clip W whether
  or not its texture is being corrected** — `vDepth` is an ordinary varying, so
  with `w = 1` an untextured wall's interior depths came out linear in screen
  space when it is `1/z` that is affine there. The software rasterizer had always
  interpolated the reciprocals; this makes the GL path agree. Also the
  **depth-clear threshold** (`GteDepth.DepthClearThreshold`, DuckStation's 300),
  which bumps the existing `Generation` rather than adding a clear path. **No
  recompile.**

- `0037-chd-disc-images.patch` — **upstream's CHD support, backported** (`137a793`,
  six commits past our pin). `CueFs` becomes `DiscFs` over a new `IDiscImage`, with
  `CueBinImage` and a from-scratch libchdr port (`Cdrom/Chd/`: header, hunk map,
  Huffman, LZMA, FLAC, CD-sector ECC) behind it; `DiscImage.Open` picks by
  extension and falls back to the CHD magic, so the recompiler, the runtime and the
  launcher only changed a type name. The commit's unrelated **RAM-size** change
  comes with it — `PSMemory(uint ramSize)` and `Runtime.RamWordMask` replacing the
  literal `0x1FFFFCu` — and nothing here passes a size, so the RAM is the same 2 MB
  and the mask the same value. Codecs: cdzl, cdlz, cdfl, zlib, lzma; **not zstd**,
  and a `cdzs` image is refused at the picker rather than crashing. **Forces a
  recompile** — `EntryWriter` emits `DiscFs.Open`. Measured: `generated/` from the
  CHD is byte-identical to `generated/` from the cue; the recompile costs
  1.35-1.39 s against 0.86-0.89 s; the autostart area load is 305.1 ms against
  305.8 ms, the same 84 steps over the same 105 blocking VSyncs; a CHD run walks
  `open` → `game` → `fdat02` → `fdat05` at 144.0 fps / 20.0 ticks/s, and the intro
  STR decodes. See "CHD disc images" in `docs/RUNTIME.md`.

- `0038-expose-the-mouse-wheel.patch` — `InputManager` owns the `IMouse` and is
  `internal`, so a port could not read the wheel at all; nothing in the runtime
  listened for the `MouseEvent` that carried it either. Adds `TakeMouseWheel` in
  the drained shape `0017`'s `TakeMouseMotion` already has — scroll arrives as
  discrete events, so a drained accumulator cannot miss a notch or spend one
  twice, where ImGui's per-frame `io.MouseWheel` is a level and a menu loop
  iterating at 30 a second against a 144 fps window would do both. The value is a
  **float** throughout: `OnScroll` had `Wheel = (int)wheel.Y`, exact for a
  discrete wheel and a total loss for a trackpad's two-finger scroll, which
  arrives in fractions of a notch. `patches/MenuMouse.cs` is the only caller.
  Input only — **no recompile**. See "The wheel owns the page, not the cursor" in
  `docs/INPUT.md`.

- `0039-render-scale-survives-a-menu.patch` — a modal sub-loop keeps the world
  behind it by reading the finished frame out of VRAM once (`StoreImage`) and
  blitting it back every iteration (`LoadImage`), and that roundtrip is 1x by
  construction: VRAM is the console's own resolution, so at any render scale the
  restore stamped a 1x picture over the display area on every frame of the menu,
  a shop or an NPC's message box. Only the *middle* of the picture, because
  `Writeback` copies a target's middle `W` columns and the widescreen margin
  lives nowhere but in the render target — which is the seam the report named.
  `ReadVram` now also takes a scaled copy on the GPU and `WriteVram` serves an
  upload of byte-identical pixels from it. Keyed on content and size rather than
  address, since the frame may be restored into either display buffer; anything
  the game actually built in RAM fails the compare and uploads as before.
  Measured in the menu at 144 fps, 16:9, scale 4: 120 restores per two seconds
  and **0** misses, present still `wide`, world still 20.0 ticks/s; `0, 0` in an
  area, so it costs nothing when nothing reads the frame back. `KF2_VRAMSNAP=0`
  is the comparison; no control in the window, a render scale surviving a menu
  not being a choice. GL backend only. **No recompile.** See "The render scale
  did not survive a menu" in `docs/RENDERING.md`.

- `0040-ambient-occlusion.patch` — contact shading in the corners, from the same
  recovered SZ. **The interesting part is not the SSAO, it is where the G-buffer
  comes from.** A screen-space pass needs the nearest visible surface at every
  pixel before any shading happens, and this port cannot build one the usual way:
  the geometry arrives incrementally through GP0, nothing holds it, and nothing
  knows the frame is finished until it is, so there is no moment at which a depth
  prepass could run. Painter's order supplies it instead — `DrawOTag` walks the
  ordering table back to front, so give every 3D triangle a depth *write* with the
  test left at `GL_ALWAYS` and the attachment ends the frame holding exactly the
  visible-surface depth, with nothing rejected and the ordering table still in
  sole charge of what is visible. That one `DepthFunc` is the whole difference
  from the Z-buffer, which is why `GteDepth.DepthWanted` replaced
  `GteDepth.ZBuffer` at every site that decides whether a depth is recovered —
  including `GpuRaster`'s `wantZ` and the `tex || Subpixel || ZBuffer` gate above
  it, without which the buffer holds only the *textured* geometry and most of this
  game's architecture is flat-shaded. **The far plane is the HUD mask and it is
  free**: everything with no recovered depth writes `1.0` instead of the clip Z it
  used to, so the HUD, the menus and any triangle the vertex map missed are
  neither shaded nor allowed to occlude, and semi-transparent primitives write
  nothing at all, so a death fade or a damage flash does not switch the shading
  off for the frames it covers. Two full-screen draws at present (spiral SSAO with
  a 4x4 interleaved rotation, then the 4x4 depth-aware box that cancels it
  exactly) into the pass's own RG8 texture, which the present shader multiplies —
  so nothing the game can read back carries the shading, not VRAM, not either
  display buffer, and not the frame a modal loop restores. The pass undoes the
  game's own projection to get a view position out of a depth texel, with `H` and
  the `OFX`/`OFY` centre read off `Gte.Rtp` — measured `H 200`, not the 320 a guess
  would have used. **The one error worth recording was silent and intermittent**:
  the first version added the drawing offset of the last depth-writing triangle,
  which belongs to the buffer being *drawn* while the pass runs against the buffer
  being *presented*, so with two display buffers half the frames put the
  projection centre a whole screen out; a display target's offset already **is**
  its own origin, or `uPosBias` would be misplacing every polygon. The probe
  prints the reconstructed centre for that reason and it should read `0.500,0.500`.
  `KF2_AO_PROBE=2` is the counter that matters — every other number stays
  identical if the shader returns white on every pixel — and it reads the
  occlusion back as a darkest value, a mean, a shaded share, a surface share and a
  32x16 map. Off by default, for the sub-pixel reason and because it is
  deliberately not authentic; one checkbox under Video ▸ Enhancements and the
  tuning on the console. GL backend only. **No recompile.** See "Ambient
  occlusion" in `docs/RENDERING.md`.

- `0041-anisotropic-filtering.patch` — a screen pixel covers an *area* of the
  texture, and the shape of it is the parallelogram spanned by the two screen
  derivatives of the texture coordinate: about a square square-on to a wall, and a
  long thin sliver on a floor running away to the horizon. The console read one
  texel out of that sliver, and which texel changes completely for a sub-pixel
  movement of the camera — the crawling, sparkling floor. **The interesting part is
  where the filter had to go.** `GL_TEXTURE_MAX_ANISOTROPY` on the VRAM sampler does
  nothing at all: the game's textures are never sampled by a GL sampler, the shader
  `texelFetch`es a sheet holding every page and every CLUT at once (so no filter may
  run across it), and in the 4- and 8-bit modes the value read is a CLUT *index*
  whose average with its neighbour is an unrelated colour. So `decode(raw)` holds
  the whole per-texel job — texture window, page wrap, nibble extract, CLUT lookup —
  and the kernel calls it once per texel along the long axis, up to `uAniso`, and
  averages. The single-sample path calls the same function, so off is bit-identical
  to before. There is **no mip chain and there cannot be one** for that same first
  reason, so this is supersampling rather than mipmapped anisotropy, and a footprint
  large on both axes is still averaged along one only. Two things the encoding
  forces: a transparent texel is stored as black, so taps are weighed by solidity
  and renormalised, discarding below half coverage; and the semi-transparency bit
  picks a blend equation rather than being a colour, so it comes whole from the
  centre tap. **No "is this 3D" test is needed** — a 2D primitive is axis-aligned
  and unminified, so the tap count is 1 and it takes the unfiltered path by
  construction, which is why this needs no varying and does not care whether
  perspective correction is on. Both prim shaders, core profile and GLSL 120 (whose
  loop is a constant bound with a `break`, 1.20 not promising dynamic bounds); GL
  backend only, native VRAM paths only. `GteDepth.AnisotropyLive` is read back from
  the one place that uploads the uniform. Off by default. **No recompile** — a plain
  uniform the next batch reads. See "Anisotropic filtering" in `docs/RENDERING.md`.

- `0042-present-counters.patch` — `FramePacing` paces from hooks on `VSync` and
  `DrawOTag`, so it cannot use those hooks to notice that they have stopped
  running. Some boots present at exactly twice the asked-for rate for the whole
  session, which is the host ceiling `FramePacing` hands `FrameClock` with nothing
  of the port holding the picture. `LibEtc.VSyncCalls` and `LibGpu.AutoPresents`
  count presents in the bodies themselves, and `LibEtc.CaptureNextStack` returns
  the managed stack of one call, which shows whether it still came through
  `HookManager.Invoke`. Read by `FramePacing`'s sentinel and its probe line.
  **No recompile.** See "The smoothing is sometimes dead for a whole session" in
  `docs/TODO.md`.

- `0043-spu-audio-quality.patch` — the port's first change to sound. The reverb
  network ran on every other raw sample and held its output, where the hardware
  decimates and interpolates through a 39-tap half-band FIR; `Spu.ReverbMode`
  `Hardware` adds both (measured: wet level unchanged, 24 dB less energy above
  11 kHz, 37 dB less above 16 kHz). `Enhanced` swaps the network for an 8-line FDN
  (`Hardware/SpuReverb.cs`) sized from the game's own reverb registers, keeping
  every send and return level the game's. `Spu.Interpolation` adds Catmull-Rom and
  a pitch-band-limited 8-tap sinc (`Hardware/SincKernel.cs`) beside the Gaussian,
  which needed `Voice.Buf` widened to seven samples of history; `XaAudio` follows
  the same setting. `Host/Audio.cs` asks OpenAL Soft for its best resampler and
  counts underruns and stalls in `AudioStats`; `Spu.Stats` counts clamps, voices
  and mixer time, and `Spu.Mixed` hands the mix and the wet return to a listener.
  Defaults are the old behaviour: Gaussian, `Legacy`. **No recompile.** See
  `docs/AUDIO.md`.

`0007`, `0008` and `patches/EndingHold.cs` are the shape to keep in mind
generally: **anything the runtime refreshes only at `VSync` is invisible to a
game that stops calling `VSync`**, and that failure mode is always silent.
`END.EXE` ends in `while(1);` with no `VSync`; on hardware the last frame stays
on the CRT, here the window dies. **Holding it is faithful and still reads as a
crash**: the spin is real (`08004694 00000000` at `0x80011A50` in the disc image)
and `END.EXE` never writes the boot stub's next-executable byte, so on hardware
the ending is a hang you leave with the reset button — and a window has no reset
button, which is why "the game crashes after The End" was reported again after
the hold was already in. Measured holding, alive and pumping, at 20 fps through
`GAME.EXE`'s own hand-over and at 144 fps. So **any button now leaves the still
for the title** (`KF2_ENDINGEXIT=0` keeps the hang), through the stub's own
loader: `SLUS_001.58` holds three file names at `0x80010254`
(`0` = `OPEN.EXE`, `1` = `GAME.EXE`, `2` = `END.EXE`), an index at `0x80010268`
and a loop in `func_80010038` that `Load`s, `Exec`s *as a call* and re-reads the
index from `0x800102F0` on return — which is exactly the door `GAME.EXE`'s own
quit-to-title uses. `patches/BootExe.cs` (`KF2_BOOTEXE`) writes that same index
before the loop's first pass, **once**, so the ending is reachable in seconds
rather than by finishing the game. See "The ending screen" in `docs/RUNTIME.md`.
