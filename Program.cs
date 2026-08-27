using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Hardware;
using Recompiled;

// Entry point for the King's Field (SLUS-00158) port.
//
// RecompOne generates its own Program.cs into generated/ if one is missing; this
// file takes that role instead so startup stays hand-editable. Custom init and
// patching hooks go here, before Entry.Run.

// Diagnostics. The runtime's log channels are plain static bools with no CLI of
// their own, so expose them through an env var:
//     KF2_LOG=bios,cd,gpu,dma,sdk,spu,mdec   (or KF2_LOG=all)
var channels = (Environment.GetEnvironmentVariable("KF2_LOG") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => s.ToLowerInvariant())
    .ToHashSet();

if (channels.Count > 0)
{
    bool all = channels.Contains("all");
    RecompOne.Runtime.Log.BiosOn = all || channels.Contains("bios");
    RecompOne.Runtime.Log.CdOn = all || channels.Contains("cd");
    RecompOne.Runtime.Log.GpuOn = all || channels.Contains("gpu");
    RecompOne.Runtime.Log.DmaOn = all || channels.Contains("dma");
    RecompOne.Runtime.Log.SdkOn = all || channels.Contains("sdk");
    RecompOne.Runtime.Log.SpuOn = all || channels.Contains("spu");
    RecompOne.Runtime.Log.MdecOn = all || channels.Contains("mdec");
    Console.WriteLine($"[KF2] log channels: {string.Join(",", channels)}");
}

// PSY-Q's interrupt-callback table, per overlay.
//
// libapi's InterruptCallback(irq, func) writes the handler into a table of 11
// slots, and the runtime reads that table to deliver an IRQ. It cannot find the
// table on its own: it derives an address from the pointer the game hands to
// HookEntryInt, but that pointer is a jmp_buf (jb[1] is the interrupt stack this
// game puts 4 KB above it), and where the table sits relative to it differs per
// link. Here the derived address lands in game data -- 0x80074348 in GAME.EXE,
// which is a live variable, not a callback -- so IRQ 0 was calling whatever word
// happened to be sitting there once a frame.
//
// The real table is the one InterruptCallback indexes with irq*4, identified in
// each overlay from that function's body (it also ORs 1<<irq into I_MASK, and
// ResetCallback clears the 11 slots below it):
//
//     overlay  InterruptCallback  table         checks out as
//     open     0x8001E75C         0x8003DD48    table + 11*4 == 0x8003DD74, the
//     game     0x8005F8CC         0x8006E3D4    DMA callback table the DMA IRQ
//     end      0x8001AD28         0x80038D90    dispatcher reads, in all three
//
// The three executables are mutually exclusive, so the address is rebound as
// each one loads. What comes out of the table confirms it: slot 3 in `game`
// holds 0x8005FAE0, which is the DMA interrupt dispatcher -- the routine that
// walks DICR's seven channel flags and calls each channel's callback -- and slot
// 0 holds 0x8005F45C, its vblank counterpart. Those are the handlers the library
// registers, arriving at the slots the identification predicts.
RecompOne.Runtime.Events.Event.AddListener<RecompOne.Runtime.Events.OverlayLoadedEvent>(e =>
{
    uint table = e.Name switch
    {
        "open" => 0x8003DD48u,
        "game" => 0x8006E3D4u,
        "end"  => 0x80038D90u,
        _ => 0u,   // the FDAT.T code modules share the resident executable's libapi
    };

    // FDAT.T modules sit at 0x8019F07C, which does not overlap OPEN/GAME/END, so
    // the dispatcher's region-overwrite path will not drop them on an executable
    // swap. END.EXE's heap starts at 0x80100000 and covers that RAM; leaving the
    // area module mapped would keep its functions callable from a table the
    // ending never owns.
    if (e.Name is "open" or "end")
    {
        foreach (var n in RecompOne.Runtime.Dispatch.Dispatcher.ActiveNames)
            if (n.StartsWith("fdat", StringComparison.Ordinal))
                RecompOne.Runtime.Dispatch.Dispatcher.Unload(n);
    }

    if (table == 0) return;
    RecompOne.Runtime.Interrupts.CallbackTable = table;
    Console.WriteLine($"[KF2] irq callback table: {e.Name} 0x{table:X8}");
});

// Scripted pad input, for reproducing a bug that needs a button press without a
// human at the keyboard:
//
//     KF2_AUTOPAD=5:Start:1000,8:Circle:200      seconds:button:holdMs
//
// The clock starts when the first area module loads -- that is the only moment
// in the boot sequence that means "in game", and it drifts with disc timing, so
// counting from process start would need recalibrating every run. Buttons are
// the Controller field names (Start, Select, Cross, Circle, Square, Triangle,
// L1, R1, L2, R2, Up, Down, Left, Right).
//
// It writes Controller.State only while a button is held, so an idle script
// never fights the keyboard. InputManager.Poll rewrites that field once a frame
// from the real keyboard; the game reads the pad far more often than that, so a
// hold of a few hundred ms lands regardless of who wrote last.
var autopad = Environment.GetEnvironmentVariable("KF2_AUTOPAD");
if (!string.IsNullOrWhiteSpace(autopad))
{
    var buttons = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["Select"] = Controller.Select, ["Start"] = Controller.Start,
        ["Cross"] = Controller.Cross, ["Circle"] = Controller.Circle,
        ["Square"] = Controller.Square, ["Triangle"] = Controller.Triangle,
        ["L1"] = Controller.L1, ["R1"] = Controller.R1,
        ["L2"] = Controller.L2, ["R2"] = Controller.R2,
        ["L3"] = Controller.L3, ["R3"] = Controller.R3,
        ["Up"] = Controller.Up, ["Down"] = Controller.Down,
        ["Left"] = Controller.Left, ["Right"] = Controller.Right,
    };

    var press = new List<(double At, double Until, ushort Bit)>();
    foreach (var step in autopad.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var f = step.Split(':');
        if (f.Length != 3 || !buttons.TryGetValue(f[1].Trim(), out var bit))
            throw new ArgumentException($"KF2_AUTOPAD: bad step '{step}'");
        double at = double.Parse(f[0]), hold = double.Parse(f[2]) / 1000.0;
        press.Add((at, at + hold, bit));
    }

    var inGame = new ManualResetEventSlim(false);
    RecompOne.Runtime.Events.Event.AddListener<RecompOne.Runtime.Events.OverlayLoadedEvent>(e =>
    {
        if (e.Name.StartsWith("fdat", StringComparison.Ordinal)) inGame.Set();
    });

    new Thread(() =>
    {
        inGame.Wait();
        Console.WriteLine($"[KF2] autopad: {press.Count} step(s) armed");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = 0xFFFF;
        while (true)
        {
            double t = clock.Elapsed.TotalSeconds;
            int mask = 0xFFFF;
            foreach (var (at, until, bit) in press)
                if (t >= at && t < until) mask &= ~bit;

            if (mask != 0xFFFF) Controller.State = (ushort)mask;
            if (mask != last)
            {
                Console.WriteLine($"[KF2] autopad t={t:F1}s state=0x{mask:X4}");
                last = mask;
            }
            Thread.Sleep(1);
        }
    }) { IsBackground = true, Name = "kf2-autopad" }.Start();
}

// The way back from an interface scaled too large to use:
//
//     KF2_UISCALE=1     force the interface scale for this run, and save it
//
// Theme.Scale is the runtime's DpiScale times the saved UiScale, and every popup is
// sized from it, so the 780x500 settings popup stops fitting a 1280x720 window at
// 1.44 -- inside UiScale's own range, and reachable at a UiScale of 1 by itself,
// since QueryDpiScale reads the primary monitor's GLFW content scale and GLFW's
// Wayland path reports the integer wl_output scale (a 1.15 display arrives as 2).
// patches/recompone/0019 clamps a popup to the viewport so the controls can no
// longer leave the window; this repairs a settings file that is already past that
// point, by writing the value as well as applying it.
Kf2.UiScale.Configure(Environment.GetEnvironmentVariable("KF2_UISCALE"));
Kf2.UiScale.Install();

// The keyboard layout the port ships. RecompOne's defaults are a console's
// defaults spelled on a keyboard -- face buttons on Z X A S, D-pad on the arrows
// -- and this game walks *and turns* on the D-pad, so the arrows alone are a tank
// control and a mouse in the other hand has nothing to do. W and S walk, A and D
// strafe (the game strafes on L1/R1), the arrows turn and look:
//
//     KF2_KEYS=stock    leave RecompOne's own bindings alone
//
// Configure must run before ConfigManager.Load, which is inside HostWindow's
// Initialize: Load overwrites these from settings.json when there is one and saves
// them when there is not, which is exactly what a default should do. Install then
// migrates an existing settings.json once, and only if every binding in it is
// still stock.
Kf2.KeyLayout.Configure();
Kf2.KeyLayout.Install();

// Frame pacing. The game's speed is its frame rate, so the rate the port draws at
// and the rate the game's own clock runs at are two different numbers here:
//
//     KF2_FPS=20                  frames a second to draw (default); any number,
//                                 or off for no pacing at all
//     KF2_TICKRATE=20             ticks a second the world runs at (default); 30
//                                 is the rate the game's own code asks for
//     KF2_FPS_LOGIC=full          do not tick the world on its own clock; scale
//                                 the movement deltas instead -- a comparison mode
//     KF2_FPS_GATE=80037C0C+8002A550+80040348+80046A60+8004910C+80033FBC+8002DC78   what is ticked
//
// The game's own frame gate (func_80017880, which spins on the vblank credit at
// 0x801B6CA8 until it reaches two) is skipped at every rate, because it decides
// the render rate and the world rate together and only knows one answer for both:
// 30. The port's own deadline paces the picture, and everything holding per-tick
// state that cannot draw -- stages 3, 4, 5, 6 and stage 13's fade stepper -- runs
// on the wall-clock tick rate instead of once per frame. 20 rather than 30 is a
// judgement about what the console achieved under load rather than what the code
// asked for; see FramePacing.LogicHz. A frame ends at the DrawOTag that
// follows a VSync *call*, not the one that follows an emulated vblank: the vblank
// is a fixed 60 Hz grid, so keying on it silently stopped pacing and stopped
// ticking above 60 fps, and the world ran at double speed.
//
// This is a correctness fix rather than an optional extra, so it lives in the
// project rather than in mods/ -- see the class comment. It attaches through
// RecompOne's HookManager, which detours a recompiled function by address at run
// time, so it needs no entry in config/kf2.json. Optional extras *are* real
// mods: mods/kf2debug, loaded by ModLoader and toggled in the game's own Mods
// panel.
Kf2.FramePacing.Configure(Environment.GetEnvironmentVariable("KF2_FPS"),
                          Environment.GetEnvironmentVariable("KF2_FPS_GATE"),
                          Environment.GetEnvironmentVariable("KF2_FPS_LOGIC"),
                          Environment.GetEnvironmentVariable("KF2_TICKRATE"));
Kf2.FramePacing.Install();

// The one thing frame pacing cannot reach: the in-game menu is a modal sub-loop
// inside stage 3 (func_80029CBC jal's func_80018E80, which blocks for the whole
// session and renders its own frames), so no gated stage is being called while it
// runs and nothing in it is on the tick clock. Two things in there are counted in
// vblanks and so ran at the render rate instead. The cursor's auto-repeat is a
// spin on six VSync(0) calls in func_80022E90 -- a vblank each on hardware, 1.2 ms
// at 144 fps here -- and those calls are held to the 60 Hz grid. The cursor's
// blink steps once per menu frame in func_80022530, the frame head, which cannot
// be skipped because it swaps the buffer, so a pre/post pair puts its counter
// back on a frame the grid did not advance on. Nothing sleeps for the blink: the
// menu still draws at the render rate, the wink is only capped at 60 Hz.
//
//     KF2_MENUPACING=0        leave both on the frame clock -- comparison only
//     KF2_MENUPACING_PROBE=1  what each repeat cost, and the blink's step rate
Kf2.MenuPacing.Configure(Environment.GetEnvironmentVariable("KF2_MENUPACING"),
                         Environment.GetEnvironmentVariable("KF2_MENUPACING_PROBE"));
Kf2.MenuPacing.Install();

// Which words of the game's memory change at the render rate rather than at the
// tick rate -- the instrument that turns "something looks too fast" into an
// address. Samples on the emulated vblank, which is a wall-clock 60 Hz grid since
// patches/recompone/0021, so two runs at different render rates are directly
// comparable. Off by default; it costs a compare over the game's data region
// sixty times a second.
//
//     KF2_RATECENSUS=1                       on
//     KF2_RATECENSUS_RANGE=80060000:801C0000 the window to watch (the default)
//     KF2_RATECENSUS_OUT=path                where to write (default ratecensus.txt)
//     KF2_RATECENSUS_PERIOD=5                seconds between dumps
Kf2.RateCensus.Configure(Environment.GetEnvironmentVariable("KF2_RATECENSUS"),
                         Environment.GetEnvironmentVariable("KF2_RATECENSUS_RANGE"),
                         Environment.GetEnvironmentVariable("KF2_RATECENSUS_OUT"),
                         Environment.GetEnvironmentVariable("KF2_RATECENSUS_PERIOD"));
Kf2.RateCensus.Install();

// The other half of drawing above 30: the world advances 30 times a second, so
// without this the camera does too and the extra frames show the same view twice.
// One pre/post pair around stage 8 (func_80025A1C), which is the whole of "build
// the render camera from the player state" and the only thing between that state
// and the picture -- so the extrapolation lives for one function call and reaches
// nothing else.
//
//     KF2_SMOOTH=1        on; off by default, so the view steps at the logic rate
//     KF2_SMOOTH_POS=1    carry the position too (off by default)
//     KF2_SMOOTH_PROBE=1  what is being carried, per second
Kf2.FrameSmoothing.Configure(Environment.GetEnvironmentVariable("KF2_SMOOTH"),
                             Environment.GetEnvironmentVariable("KF2_SMOOTH_POS"),
                             Environment.GetEnvironmentVariable("KF2_SMOOTH_PROBE"));
Kf2.FrameSmoothing.Install();

// The other half of the same problem: the camera moves every frame now, but an
// object whose position advances on the tick still arrives in 50 ms steps, and
// against a smoothly sliding world that reads worse than not smoothing at all.
// One pre/post pair around stage 13, interpolating the object table at 0x80177714
// -- the table the renderer is actually handed, not the entity records, which are
// a copy stage 4 makes of it.
//
//     KF2_SMOOTH_OBJECTS=1        on; off by default
//     KF2_SMOOTH_OBJECTS_PROBE=1  how much is being carried, per second
Kf2.ObjectSmoothing.Configure(Environment.GetEnvironmentVariable("KF2_SMOOTH_OBJECTS"),
                              Environment.GetEnvironmentVariable("KF2_SMOOTH_OBJECTS_PROBE"));
Kf2.ObjectSmoothing.Install();
Kf2.EndingHold.Install();

// Which routine in the renderer drew how much of the frame. Stage 13 is the only
// filler of the display list, but nothing records which of its callees draws the
// map, the objects, or the first-person weapon -- and "the arm steps at the tick
// rate" has two different fixes depending on which. Measured off the primitive
// arena's bump pointer across each routine, so it costs two reads per routine per
// frame and nothing per polygon.
//
//     KF2_DRAWCENSUS=1    the per-routine primitive census, every two seconds
Kf2.DrawCensus.Configure(Environment.GetEnvironmentVariable("KF2_DRAWCENSUS"));
Kf2.DrawCensus.Install();

// Dithering. Clearing the GPU's dither bit is one pre/post hook pair on each of
// PutDrawEnv and DrawOTag, and it is a patch rather than a mod because it is a
// picture the port should be able to offer without a package having to load: the
// switch itself lives in Display beside vsync.
//
//     KF2_NODITHER_PROBE=1   report the two draw-mode sources, and GPUSTAT bit 9
Kf2.NoDither.Configure(Environment.GetEnvironmentVariable("KF2_NODITHER_PROBE"));
Kf2.NoDither.Install();

// Perspective-correct textures. The GPU is handed polygons with no depth in them,
// so it interpolates U and V linearly and the texture swims; the depth still exists
// one step earlier, in the GTE, and the address the coordinate is stored at is what
// ties the two back together -- the same word is copied into the packet and read
// back out of it by DrawOTag. All of the work is in the runtime (GteVertexMap, the
// rasterizer and the prim shaders) because a texture coordinate is decided far below
// anything HookManager can reach -- this is the switch and the report:
//
//     KF2_PERSPECTIVE=0           off, for the console's own affine mapping
//     KF2_PERSPECTIVE_PROBE=1     report the vertex map's hit rate
//     KF2_PERSPECTIVE_FALLBACK=1  also guess by screen position on a miss, which is
//                                 the mechanism the map replaced, kept for comparison
//
// A patch rather than a mod for the same reason the dither switch is one: it is a
// picture the port should be able to offer without a package having to load. Its
// switch is under Video beside that one.
Kf2.Perspective.Configure(Environment.GetEnvironmentVariable("KF2_PERSPECTIVE"),
                          Environment.GetEnvironmentVariable("KF2_PERSPECTIVE_PROBE"),
                          Environment.GetEnvironmentVariable("KF2_PERSPECTIVE_FALLBACK"));
Kf2.Perspective.Install();

// Sub-pixel vertex positioning, which is the other half of the same discarded
// number: the GTE projects to 16.16 and keeps only the whole part, so a vertex
// drifting slowly sits still and then jumps a pixel, and its polygon twitches. The
// fraction is recovered through the same map and the same address, one shift earlier
// in the same expression, and served independently of the depth:
//
//     KF2_SUBPIXEL=1        on; 0 or unset leaves vertices on whole pixels
//     KF2_SUBPIXEL_PROBE=1  report how far vertices are actually moving
//
// Off by default where perspective correction is on -- not because it is riskier,
// the same "a miss is the old behaviour" argument covers both, but because that one
// was measured before it became a default and this one has not been. Its switch is
// under Video with the others.
Kf2.Subpixel.Configure(Environment.GetEnvironmentVariable("KF2_SUBPIXEL"),
                       Environment.GetEnvironmentVariable("KF2_SUBPIXEL_PROBE"));
Kf2.Subpixel.Install();

// Z-buffer. The GPU is handed polygons with no depth in them, so occlusion is
// whatever order the game stuffed the ordering table in — one OTZ per polygon,
// back to front. Interpenetrating surfaces can only take turns in front of each
// other. The depth still exists one step earlier, in the GTE, and the same map
// that feeds perspective correction hands it to both rasterizers as a per-pixel
// test:
//
//     KF2_ZBUFFER=1        on; 0 or unset leaves the ordering table in charge
//     KF2_ZBUFFER_PROBE=1  report how many triangles actually depth-tested
//     KF2_ZBUFFER_PROBE=2  also the frame's occlusion census: which large primitive
//                          is standing in front of which, and how much of the
//                          picture that costs
//
// Off by default where perspective correction is on -- the recovered number is
// the same one, but the picture has not been checked by eye. Its switch is under
// Video with the others.
Kf2.ZBuffer.Configure(Environment.GetEnvironmentVariable("KF2_ZBUFFER"),
                      Environment.GetEnvironmentVariable("KF2_ZBUFFER_PROBE"));
Kf2.ZBuffer.Install();

// True color (24-bit) output for the GL backend. The console renders into 15-bit
// VRAM, so a shaded fog gradient bands into steps unless the dither hides it with
// a crosshatch; this keeps eight bits per channel and removes the banding without
// the crosshatch. Off by default -- the default picture is the console's:
//
//     KF2_TRUECOLOR=1  on; 0 or unset keeps the 15-bit output
//
// The mechanism is patches/recompone/0021 (the render-target format and the
// fragment shader). Its switch is under Video with the others.
Kf2.TrueColor.Configure(Environment.GetEnvironmentVariable("KF2_TRUECOLOR"));
Kf2.TrueColor.Install();

// Auto reload. One post-hook on the end of main-loop stage 3 watches for the
// player's death and reloads the last save through the game's own loader, so a
// death costs a couple of seconds instead of four screens of menu:
//
//     KF2_AUTORELOAD=1        on (the default); 0 leaves the death alone
//     KF2_AUTORELOAD_DELAY=2  seconds of the game's death sequence first
//     KF2_AUTORELOAD_SLOT=0   0 = the game's own "last used" slot, 1..3 pins one
//
// A patch rather than a mod because it answers a design of the original that a
// package should not have to be present to fix; its settings are under Gameplay.
Kf2.AutoReload.Configure(Environment.GetEnvironmentVariable("KF2_AUTORELOAD"),
                         Environment.GetEnvironmentVariable("KF2_AUTORELOAD_DELAY"),
                         Environment.GetEnvironmentVariable("KF2_AUTORELOAD_SLOT"));
Kf2.AutoReload.Install();

// Auto start, and the agent beacon -- the pair that lets an automated tester get
// into the game and know it got there. Scripted input cannot drive the boot menus
// (KF2_AUTOPAD's clock only starts once an area has loaded), and screenshots are
// off the table, so an agent left at the title waits on nothing:
//
//     KF2_AUTOSTART=2   load slot 1..3 through the game's own loader at boot,
//                       skipping the title and Continue menus (off unless set)
//     KF2_AGENT=1       emit [KF2-AGENT] stdout lines -- an overlay transition on
//                       each load and a JSON state snapshot ~1/s, whose inGame
//                       field is how a program tells "stuck at title" from "in an
//                       area" (off unless set)
//
// Patches for the KF2_AUTOPAD reason: they must work from an environment variable
// with no package to enable.
Kf2.AutoStart.Configure(Environment.GetEnvironmentVariable("KF2_AUTOSTART"));
Kf2.AutoStart.Install();
Kf2.AgentBeacon.Configure(Environment.GetEnvironmentVariable("KF2_AGENT"));
Kf2.AgentBeacon.Install();

// The command channel -- the half that lets an agent act instead of only watch.
// A small TCP server on loopback, off unless KF2_SHELL is set:
//
//     KF2_SHELL=1   line protocol on 127.0.0.1:27900 (or =<port>): one request
//                   per line, one single-line JSON response back --
//                   state | load <slot> | warp <area> | press <button> [ms] | kill
//
// Commands arrive on socket threads and are marshalled onto the game thread
// through two drainers -- a VSync listener for the cheap ones and a post hook on
// main-loop stage 3 for load/warp, whose loader waits on the CD by looping VSync,
// so running it inside the VSync event would nest VSync inside itself and swap
// overlays under a live frame. Patches for the KF2_AUTOPAD reason; see "The
// command channel" in docs/PATCHES_AND_MODS.md.
Kf2.AgentServer.Configure(Environment.GetEnvironmentVariable("KF2_SHELL"));
Kf2.AgentServer.Install();

// Analog twin-stick control. Two pre-hooks on the game's own turn/look and
// walk/strafe routines pre-load the velocities those routines are about to
// accumulate, so the sticks pick the amount and the game's own movement code
// still applies it:
//
//     KF2_ANALOG=1                on (the default); 0 hands the sticks back
//     KF2_ANALOG_TURN/PITCH/MOVE  sensitivities; DEADZONE, CURVE, ACCEL* the feel
//     KF2_ANALOG_INVERTY=1        look-Y inversion, and INVERTTURN/STRAFE/FWD
//     KF2_ANALOG_PROBE=1          the control-state report
//
// A patch rather than a mod because a pad without it has its left stick wired to
// the D-pad, which in this game turns instead of walking — working sticks are not
// a taste a player should have to find a package to fix. Configure reads its own
// environment here rather than being handed the strings: there are eighteen of
// them and they are listed above its own class. Its settings are under Input.
Kf2.Analog.Configure();
Kf2.Analog.Install();

// Mouse look, and the mouse buttons. The look half is not a hook of its own: a
// mouse is another way of choosing the same per-frame turn and pitch step, so it
// is spent inside Analog's look hook and runs even with the sticks handed back.
// The buttons take the other route, through PadReadEvent, so they are pressed as
// pad buttons at the moment the game reads the pad -- which is what makes them
// follow the game's own control-config screen and work in its menus:
//
//     KF2_MOUSE=1                              on; off by default
//     KF2_MOUSE_TURN/LOOK=1.0                  sensitivities; INVERTY=1 flips look Y
//     KF2_MOUSE_BUTTONS=Square,Triangle,Cross  left, right, middle, as pad buttons
//     KF2_MOUSE_KEY=Escape                     the key that captures and releases
//
// Off by default, unlike the sticks: nothing is broken about playing this with the
// keyboard, and a pointer that vanishes into the game unasked is worse than one
// switch to find. Its settings sit under Input beside the stick ones.
Kf2.Mouse.Configure();
Kf2.Mouse.Install();

// Widescreen. The runtime already renders a margin either side of the display
// buffer and presents the whole thing at Display.WideAspect, so setting that one
// number is the entire hookup; the replacement of DrawOTag here is only for the
// HUD, which is drawn in screen space and would otherwise sit inset from the new
// edges:
//
//     KF2_WIDESCREEN=16:9       aspect for the run; "1.777" and "off" also parse
//     KF2_WIDESCREEN_PROBE=1    the margin census, on the console
//     KF2_WIDESCREEN_EFFECTS=0  leave the death fade and the damage flash 320 wide
//
// A patch rather than a mod because an aspect ratio is a picture the port should
// be able to offer without a package having to load, and Video is where a player
// looks for it. It still defaults to 4:3: the census says a quarter of every frame
// in an area crosses the screen edge and is there to recover, but the two ways it
// can go wrong -- a 2D screen the game draws 320 wide, and per-object culling
// against the game's own 4:3 frustum -- are invisible to a primitive counter and
// have never been checked by eye.
Kf2.Widescreen.Configure(Environment.GetEnvironmentVariable("KF2_WIDESCREEN"),
                         Environment.GetEnvironmentVariable("KF2_WIDESCREEN_PROBE"),
                         Environment.GetEnvironmentVariable("KF2_WIDESCREEN_EFFECTS"));
Kf2.Widescreen.Install();

// Present-path census. Counts, per two-second window, what PresentDisplay
// picked: the widened render target, a plain one, or a fallback to raw VRAM.
// The fallback presents at 4:3, so a high fallback rate is the flashing margin
// -- see "Widescreen" in docs/WIDESCREEN.md and patches/recompone/0022:
//
//     KF2_PRESENT_PROBE=1    the census, on the console
if (Environment.GetEnvironmentVariable("KF2_PRESENT_PROBE") == "1")
    RecompOne.Runtime.Hle.GpuHle.PresentProbe = true;

// The cull the margin runs into. King's Field decides what to draw from a 24x24
// grid of tile visibility, rebuilt each frame as a top-down trapezoid whose corners
// are seven s16 pairs in GAME.EXE's data -- the 4:3 frustum, flattened onto the
// map. Widening the picture without widening that leaves the sides showing only
// what the game happened to overdraw, so this scales the trapezoid's four lateral
// corners by the same ratio the margin widens by, and fixes the game's scanline
// fill so a cone that now leaves the 24x24 window does not lose whole ranks of
// tiles:
//
//     KF2_WIDESCREEN_CULL=0        leave the cone at its 4:3 shape
//     KF2_WIDESCREEN_CULL=1.5      pin a widening factor instead of the aspect's
//     KF2_WIDESCREEN_CULL_PROBE=1  the cone's shape and what the grid clipped
//     KF2_CULL_RESCUE_RADIUS=N     Chebyshev radius of the force-lit disc around
//                                  the camera (default 3, 0 disables) — the
//                                  occlusion flood's eye sits five tiles ahead
//                                  of the camera, so near-camera tiles can be
//                                  shadowed by walls that do not block the eye
Kf2.CullCone.Configure(Environment.GetEnvironmentVariable("KF2_WIDESCREEN_CULL"),
                       Environment.GetEnvironmentVariable("KF2_WIDESCREEN_CULL_PROBE"),
                       Environment.GetEnvironmentVariable("KF2_CULL_RESCUE_RADIUS"));
Kf2.CullCone.Install();

// The rest of the third cull: the 24x24 window itself, which fits the shipped
// cone exactly and so truncates the widened one at some yaw -- a straight line
// cut in world space at the screen edges. This rebuilds the whole visibility
// pipeline at 32x32 (build, point query, box query; the legacy array gets our
// central 24x24 crop), transcribed cell for cell from the generated code:
//
//     KF2_CULLGRID=shadow        run ours after the stock build, change nothing
//     KF2_CULLGRID=on            replace the build and both queries with ours
//     KF2_CULLGRID_COMPARE=1     in shadow, diff our grid against the stock one;
//                                at factor 1 the acceptance gate is zero
//                                mismatches over a whole attract session
Kf2.CullGrid.Configure(Environment.GetEnvironmentVariable("KF2_CULLGRID"),
                       Environment.GetEnvironmentVariable("KF2_CULLGRID_COMPARE"));
Kf2.CullGrid.Install();

// How close the frame's primitive buffer comes to running out. The game hands out
// 0x19000 bytes a frame -- 1969 POLY_GT4 packets -- and func_80030540 abandons the
// rest of the call when the bump passes the end, so a busier frame silently loses
// whatever it had not drawn yet. Widening the cull cone spends that budget, which
// makes this the thing to check when geometry goes missing in a wide picture:
//
//     KF2_PRIMBUF_PROBE=1     peak usage, capacity and overflows, on the console
Kf2.PrimBuffer.Configure(Environment.GetEnvironmentVariable("KF2_PRIMBUF_PROBE"));
Kf2.PrimBuffer.Install();

// The game's other cull: a six-plane view-space clipper (func_8005CAC8) that only
// the near floor and ceiling are big enough to reach, set to twice the screen
// frustum as a guard band against the GPU's 1023-pixel limit. Twice the frustum is
// 320 px either side of centre, which the picture passes at about 8:3 -- and what a
// plane removes has a straight edge, which is how this was told apart from the
// per-tile cull:
//
//     KF2_VIEWCLIP=0          leave the clip volume at its 4:3 shape
//     KF2_VIEWCLIP=1.5        force a widening factor instead of the aspect's
//     KF2_VIEWCLIP_PROBE=1    what it wrote, and where the cut lands on screen
Kf2.ViewClip.Configure(Environment.GetEnvironmentVariable("KF2_VIEWCLIP"),
                       Environment.GetEnvironmentVariable("KF2_VIEWCLIP_PROBE"));
Kf2.ViewClip.Install();

// Where the patches' own settings live. A patch registers a page against one of
// the runtime's settings sections and is drawn inside it, so the frame rate is in
// System > Settings > Video beside vsync rather than in a box of its own; the one
// section the port adds itself is Gameplay, for patches that change how the game
// plays rather than how the machine behaves.
Kf2.Settings.PatchSettings.Install();

var memory = new PSMemory();
Entry.Run(memory, args.Length > 0 ? args[0] : null);
return 0;
