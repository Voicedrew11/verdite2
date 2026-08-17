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

// Frame pacing. The game's speed is its frame rate and the port would otherwise
// burst past NTSC's fastest band, so a rendered frame is held to two vblanks:
//
//     KF2_FPS=30                       30 fps (default); 60, or off for no floor
//     KF2_FPS_GATE=80040348+8002A550   at 60, stages to run every other frame
//
// This is a correctness fix rather than an optional extra, so it lives in the
// project rather than in mods/ -- see the class comment. It attaches through
// RecompOne's HookManager, which detours a recompiled function by address at run
// time, so it needs no entry in config/kf2.json. The measurement tools *are*
// optional and are real mods: mods/framestats and mods/loopprobe, loaded by
// ModLoader and toggled in the game's own Mods panel.
Kf2.FramePacing.Configure(Environment.GetEnvironmentVariable("KF2_FPS"),
                          Environment.GetEnvironmentVariable("KF2_FPS_GATE"));
Kf2.FramePacing.Install();
Kf2.EndingHold.Install();

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

// Where the patches' own settings live. A patch registers a page against one of
// the runtime's settings sections and is drawn inside it, so the frame rate is in
// System > Settings > Video beside vsync rather than in a box of its own; the one
// section the port adds itself is Gameplay, for patches that change how the game
// plays rather than how the machine behaves.
Kf2.Settings.PatchSettings.Install();

var memory = new PSMemory();
Entry.Run(memory, args.Length > 0 ? args[0] : null);
return 0;
