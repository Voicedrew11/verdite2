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

// Mods: behaviour changes that can be turned on and off without a recompile.
// They hang off the hook points in mods/Hooks.cs, which are what config/kf2.json
// names; adding or removing a mod that uses an existing hook costs nothing.
//
//     KF2_MODS=fps=60,framestats=15,loopprobe
//
// `fps` is on by default at 30, which is the frame-pacing floor -- the game's
// speed is its frame rate and the port would otherwise burst past NTSC's fastest
// band. See "Frame pacing" in NOTES.md.
Kf2.Mods.Hooks.Install();
Kf2.Mods.ModHost.Register(new Kf2.Mods.FpsMod());
Kf2.Mods.ModHost.Register(new Kf2.Mods.FrameStatsMod());
Kf2.Mods.ModHost.Register(new Kf2.Mods.LoopProbeMod());

Kf2.Mods.ModHost.Load(Environment.GetEnvironmentVariable("KF2_MODS"));

// The env vars that predate the mod list, kept working: they are what NOTES.md
// documents and what any saved command line uses.
if (int.TryParse(Environment.GetEnvironmentVariable("KF2_MINVBLANKS"), out var minVBlanks))
    Kf2.Mods.ModHost.Load(minVBlanks <= 1 ? "fps=60" : $"fps={60 / minVBlanks}");
if (Environment.GetEnvironmentVariable("KF2_FRAMESTATS") is { Length: > 0 } stats)
    Kf2.Mods.ModHost.Load($"framestats={stats}");

Kf2.Mods.ModHost.PrintStatus();
Kf2.Mods.ModHost.Get<Kf2.Mods.FpsMod>().Validate();

var memory = new PSMemory();
Entry.Run(memory, args.Length > 0 ? args[0] : null);
return 0;
