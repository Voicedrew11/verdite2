# Environment variables

Every `KF2_*` switch the port reads, in one list. Most are wired up in
`Program.cs` or read by the patch they belong to (`patches/*.cs`); the document
named for each feature in `NOTES.md` explains what the comparison is for. A
variable marked *no longer a setting* is the only way left to reach that
behaviour — its control came out of the settings window and its saved key is not
read.

Prefer the mods under `mods/` (enable them in the game's Mods panel) to
`KF2_LOG=sdk`, which is gigabytes a minute. `KF2_LOG=bios` is worse during play:
the game polls `PAD_dr` hundreds of thousands of times a second.

```bash
KF2_LOG=bios,cd,gpu,dma,sdk,spu,mdec  # or KF2_LOG=all; wired up in Program.cs
KF2_CDTRACE=1                          # stack trace on first CD register access (patch 0002)
KF2_AUTOPAD=8:Start:400,20:Circle:200  # scripted pad input: seconds:button:holdMs
KF2_FPS=120                            # 20 (default), any number, or off; see "Any frame rate"
KF2_TICKRATE=30                        # ticks a second the world runs at (20, and no longer a setting)
KF2_FPS_GATE=80037C0C+8002A550+80040348+80046A60+8004910C+80033FBC+8002DC78  # what is ticked
KF2_FPS_LOGIC=full                     # no gating; scale the movement deltas instead
KF2_FPS_PROBE=1                        # a line a second: fps drawn, presents, ticks taken, what each smoother is doing; a `no boundary` line while the watchdog paces
KF2_MENUPACING=0                       # menu cursor repeat and blink back on the frame clock (on by default)
KF2_MENUPACING_PROBE=1                 # what each repeat cost, and the blink's step rate
KF2_LOOPPACING=0                       # loops that render their own frames back on the render rate (on by default)
KF2_LOOPPACING=pace                    # hold such a loop but do not redraw: right speed, tick-rate picture
KF2_LOOPPACING=nocarry                 # redraw, but do not carry a view the loop pans itself
KF2_LOOPPACING_PROBE=1                 # modal frames a second, world and interface, against the main loop's
KF2_LOOPPACING_PROBE=2                 # also how far the loop's own view moves per iteration
KF2_LOADPACING=0                       # the loading screen's walking figure back on the host ceiling (paced by default)
KF2_LOADPACING_PROBE=1                 # its steps a second, and what one load cost
KF2_SPRITEANIM=0                       # billboard sprite animation back on the render rate (paced by default)
KF2_SPRITEANIM_PROBE=1                 # cel changes a second, live slots, and how many walks stepped
KF2_RATECENSUS=1                       # rank memory by whether it moves at the render rate
KF2_RATECENSUS_RANGE=80060000:801C0000 # the window to watch (this is the default)
KF2_RATECENSUS_OUT=path KF2_RATECENSUS_PERIOD=5   # where to dump, and how often
KF2_SMOOTH=1 KF2_SMOOTH_POS=1          # carry the view between ticks (off by default); carry position too
KF2_SMOOTH_PROBE=1                     # how far the view is being carried, per second
KF2_SMOOTH_OBJECTS=1                   # carry enemies, doors and everything else that moves (off by default)
KF2_SMOOTH_OBJECTS_PROBE=1             # how much is being carried, per second
KF2_SMOOTH_OBJECTS_GUARD=continuous    # strict|sticky|continuous: what counts as a placement (creatures)
KF2_SMOOTH_ANIM=1                      # carry MO pose between ticks (off by default)
KF2_SMOOTH_ANIM=time                   # lerp the clip time between the two ticks (the default mode)
KF2_SMOOTH_ANIM=timeline               # comparison: interpolate on the clip's own timeline
KF2_SMOOTH_ANIM=weight                 # comparison: the blend weight only, inside the game's segment
KF2_SMOOTH_ANIM_PROBE=1                # morph vs rigid submits, the verdict census, carries
KF2_HITGUARD=0                         # let the hit path's reaction lookup fault (it is fenced by default; docs/TODO.md #14)
KF2_CRASHDUMP=0                        # no game-state dump on an unhandled exception (it dumps by default)
KF2_HITPROBE=1                         # census what the hit check saw; =2 every call
KF2_DRAWCENSUS=1                       # which renderer routine drew how much of the frame; =2 names the models
KF2_TEXPROBE=1                         # textured vs flat prims a second, and a per-page VRAM census, into texprobe.log
KF2_WIDESCREEN=16:9 KF2_WIDESCREEN_PROBE=1  # aspect (4:3 by default), and the margin census
KF2_WIDESCREEN_PROBE=2                   # the census plus every wide primitive, once per shape
KF2_WIDESCREEN_EFFECTS=0                 # leave the death fade and damage flash 320 wide (stretched by default)
KF2_WIDESCREEN_HUD=1                     # anchor the HP/MP panel and icons to the new edges (off; no longer a setting)
KF2_WIDESCREEN_CULL=0                    # leave the game's view cone at its 4:3 shape (widened by default)
KF2_WIDESCREEN_CULL=1.5                  # pin a widening factor instead of the aspect's
KF2_WIDESCREEN_CULL_PROBE=1              # tiles lit, and what the 24x24 grid clipped
KF2_WIDESCREEN_CULL_PROBE=2              # also lit-per-ring after the occlusion flood
KF2_PRIMBUF_PROBE=1                      # the frame's primitive budget: peak, capacity, overflows
KF2_VIEWCLIP=0 KF2_VIEWCLIP_PROBE=1      # the game's view-space clip volume, and where it cuts
KF2_NODITHER_PROBE=1                   # where the dither bit comes from, and GPUSTAT bit 9
KF2_TRUECOLOR=1                        # 24-bit shaded output, no 15-bit banding (off by default; GL backend only)
KF2_VRAMSNAP=0                         # a menu restores the frozen frame at 1x again (the scaled copy is kept by default)
KF2_VRAMSNAP_PROBE=1                   # frame restores served from that copy, against uploads that missed
KF2_VSYNC=block                        # upstream's blocking vblank timeline instead of the port's grid (caps the picture at 60)
KF2_PERSPECTIVE=0                      # affine textures again (correction is on by default)
KF2_PERSPECTIVE_PROBE=1                # the GTE vertex map's hit rate
KF2_PERSPECTIVE_FALLBACK=1             # also guess by screen position on a miss (the old mechanism)
KF2_ANISO=8                            # anisotropic filtering: taps along the footprint's long axis (1, off)
KF2_ANISO_PROBE=1                      # the level, and whether the uniform reaches the shader
KF2_SUBPIXEL=1                         # sub-pixel vertex positions (off by default)
KF2_SUBPIXEL_PROBE=1                   # how far vertices actually move, in pixels
KF2_PGXP=1                             # upstream's PGXP as the vertex source (off; the address map answers)
KF2_PGXP_TEXTURE=0                     # its share of perspective correction off
KF2_PGXP_CULLING=0                     # leave backface culling on truncated positions
KF2_PGXP_CPU=0                         # no per-instruction register tracking (and so no RAM shadow)
KF2_PGXP_MEMORY=0                      # no RAM shadow
KF2_PGXP_VERTEXCACHE=0                 # no screen-position fallback
KF2_PGXP_CACHEW=0                      # let that fallback answer positions but not depths
KF2_PGXP_TOLERANCE=2                   # how far a recovered position may sit from the packet's; -1 off
KF2_PGXP_PROBE=1                       # its coverage, and where each answer came from
KF2_ZBUFFER=1                          # per-pixel occlusion from GTE depth (off by default)
KF2_ZBUFFER_THRESHOLD=300              # restart the depth buffer when the scene jumps forward (0, off)
KF2_ZBUFFER_PROBE=1                    # how many triangles actually depth-tested
KF2_ZBUFFER_PROBE=2                    # the frame's polygon census, and a map of the depth buffer
KF2_AO=1                               # ambient occlusion (off by default; GL backend only)
KF2_AO_RADIUS=512 KF2_AO_STRENGTH=0.8  # how far it reaches, in world units, and how dark it goes
KF2_AO_BIAS=0.08 KF2_AO_SAMPLES=16 KF2_AO_MAXDEPTH=24000
KF2_AO_PROBE=1                         # coverage, the projection read off the GTE, passes run
KF2_AO_PROBE=2                         # also read the occlusion back: how dark, how much, and where
KF2_ANALOG=0                             # twin-stick control off (it is on by default)
KF2_ANALOG_TURN=1.0 KF2_ANALOG_MOVE=1.0 KF2_ANALOG_DEADZONE=0.15  # its sensitivities
KF2_ANALOG_INVERTY=1 KF2_ANALOG_PROBE=1  # look-Y inversion, and the control-state report
KF2_KEYS=stock                           # RecompOne's own key bindings; the port ships WASD
KF2_MOUSE=1                              # mouse look (off by default; Escape captures the pointer)
KF2_MOUSE_TURN=1.0 KF2_MOUSE_LOOK=1.0 KF2_MOUSE_INVERTY=1   # its sensitivities and look-Y
KF2_MOUSE_BUTTONS=Square,Triangle,Cross  # left, right, middle, as pad buttons
KF2_MOUSE_KEY=Escape                     # the key that captures and releases
KF2_MENUMOUSE=0                          # the menu pointer off (on by default)
KF2_MENUMOUSE_PROBE=1                    # the layout table, the pointer's row, and what it did
KF2_AUTORELOAD=1 KF2_AUTORELOAD_SLOT=0   # reload the last save on death
KF2_AUTORELOAD_DELAY=2.0                 # seconds of the death first (2.0; no longer a setting)
KF2_AUTOSTART=2                          # boot straight into save slot 1..3, past the title menus
KF2_BOOTEXE=end                          # boot straight into OPEN.EXE, GAME.EXE or END.EXE
KF2_ENDINGEXIT=0                         # leave "The End" hanging, as the original does (a button exits by default)
KF2_AGENT=1                              # [KF2-AGENT] state lines on stdout: overlay, inGame, HP/MP/area/slot
KF2_SHELL=1                              # TCP 127.0.0.1:27900 line protocol: state|nearby|load|warp|press|kill
KF2_UISCALE=1                            # force the interface scale, and save it
KF2_MAP=0                                # the map off entirely (on by default); M opens it
KF2_MAP_MINIMAP=1                        # the corner minimap on (off; no longer a setting); N toggles it
KF2_MAP_MARKERS=1                        # creatures, objects, effects and sprites on (off; no longer a setting)
KF2_MAP_STYLE=blueprint                  # the port's original blueprint plan (the game's own map; no longer a setting)
KF2_MAP_SHADE=1                          # colour each tile by its height byte (off; no longer a setting)
KF2_MAP_WALLS=1                          # tint the tiles whose +4 bit 0x80 is set (off; no longer a setting)
KF2_MAP_PAUSE=0                          # leave the world running while the full map is up (it pauses; no longer a setting)
KF2_MAP_FLOOR=lower                      # pin a stacked half instead of following the player (no longer a setting)
KF2_MAP_ARROW=1                          # the player as an arrow with a heading, not a dot (no longer a setting)
KF2_MAP_PROBE=1                          # dump the 80x80 tile grid as ASCII, its occupied extent and a marker census, and open both maps
KF2_MAP_FOG=1                            # fog of war: only the tiles you have seen (off by default)
KF2_MAP_FOG_LOS=0                        # its line-of-sight gate off (on; no longer a setting)
KF2_MAP_FOG_PROBE=1                      # tiles seen, tiles lit now, tiles refused, records, flushes
KF2_MAP_FOG_PROBE=2                      # also the raw 24x24 grid, the gate's verdict and its walls
```

