using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using RecompOne.Runtime.Sdk;

namespace Kf2;

/// <summary>
/// Keep presenting after the ending movie, so "The End" stays on screen.
///
/// <c>END.EXE</c>'s main plays <c>\OP\ED0.S</c> then <c>\OP\ED1.S</c> and
/// finishes in <c>func_800119A4</c> at <c>0x80011A50</c> with <c>while(1);</c>
/// and no <c>VSync</c>. On hardware the GPU keeps scanning out the last
/// framebuffer, so that still is the ending. Here a frame reaches the window
/// only from <c>PresentFrame</c>, which only runs from <c>VSync</c>, so the
/// same spin leaves the last image up but the window dead — no events, no
/// close, 100% of a core. That is what "it crashed on The End" is.
///
/// <c>func_80011CC4</c> is the movie player, called once per file. After the
/// second return the original code is about to hit the spin; this hook never
/// returns and <c>VSync(0)</c>s instead, which is the port equivalent of
/// leaving the picture on the CRT.
///
/// <b>Holding the picture is only half of it, and the other half is a port
/// question rather than a faithfulness one.</b> The spin at <c>0x80011A50</c> is
/// real — <c>08004694 00000000</c> in the disc image, <c>j 0x80011A50</c> and its
/// delay slot — and <c>END.EXE</c> never writes the boot stub's next-executable
/// byte, so on hardware the ending is a hang you leave with the reset button. A
/// window has no reset button, so the authentic behaviour is indistinguishable
/// from the port having died, and that is what it gets reported as. Any button
/// therefore returns to the title (<c>KF2_ENDINGEXIT=0</c> keeps the pure hold),
/// which is the same test auto reload is on by default under: a player expects
/// the port itself to have dealt with it.
///
/// The way back is the stub's own. <c>SLUS_001.58</c> holds three file names at
/// <c>0x80010254</c> (<c>0</c> = <c>OPEN.EXE</c>, <c>1</c> = <c>GAME.EXE</c>,
/// <c>2</c> = <c>END.EXE</c>), an index at <c>0x80010268</c>, and a loop in
/// <c>func_80010038</c> that <c>Load</c>s the named file, <c>Exec</c>s it as a
/// call and re-reads the index from the byte at <c>0x800102F0</c> when it
/// returns. Writing index 0 and re-entering that loop is exactly what
/// <c>GAME.EXE</c>'s own quit-to-title does one frame later, so no new loading
/// path is invented here; <see cref="BootExe"/> is the other user of the same
/// three addresses. <c>Exec</c> leaves <c>SP</c> alone for this stub (its header
/// carries a zero <c>s_addr</c>), so re-entering from inside the ending costs a
/// few words of stack and nothing else.
/// </summary>
public static class EndingHold
{
    const uint MoviePlayer = 0x80011CC4;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.endinghold",
        Name = "Ending hold",
        Version = "1.0",
        Description = "Keeps presenting after END.EXE's last frame.",
    };

    // The stub's loader loop, and the two words that tell it what to load next.
    const uint StubMain = 0x80010038;
    const uint StubIndex = 0x80010268;   // u32, index into the file-name table
    const uint StubNext = 0x800102F0;    // u8, what a returning executable asks for
    const int TitleIndex = 0;            // OPEN.EXE

    static int _played;

    /// <summary>Whether a button leaves the held frame for the title screen.
    /// <c>KF2_ENDINGEXIT=0</c> keeps the hang the original has.</summary>
    public static bool ExitToTitle { get; private set; } = true;

    public static void Configure(string? exit)
    {
        if (string.IsNullOrWhiteSpace(exit)) return;
        ExitToTitle = exit.Trim() is not ("0" or "off" or "false" or "no");
    }

    public static void Install()
    {
        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            if (e.Name == "end") _played = 0;
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    static void Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("end", null, MoviePlayer);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] ending hold: no function at end/0x{MoviePlayer:X8}");
            return;
        }

        var impl = typeof(EndingHold).GetMethod(nameof(AfterMovie), BindingFlags.Public | BindingFlags.Static)!;
        if (!HookManager.AddPost(_self, target, impl)) return;
        HookManager.Commit();
        Console.WriteLine("[KF2] ending hold: hooked");
    }

    public static void AfterMovie(CpuContext c, IMemory m)
    {
        if (++_played < 2) return;

        Console.WriteLine("[KF2] ending: holding the last frame" +
                          (ExitToTitle ? " — any button returns to the title" : ""));

        // A button has to be seen going down, not merely found down: the movie
        // player accepts a skip, so whatever ended the credits can still be held
        // when the still comes up, and that press must not also spend the still.
        bool released = !ExitToTitle;
        for (;;)
        {
            c.A0 = 0;
            LibEtc.VSync(c, m);
            if (!ExitToTitle) continue;

            bool down = Controller.State != 0xFFFF;
            if (!down) released = true;
            else if (released) break;
        }

        m.WriteU32(StubIndex, (uint)TitleIndex);
        m.WriteU8(StubNext, (byte)TitleIndex);
        Console.WriteLine("[KF2] ending: returning to the title");
        Dispatcher.Call(c, m, StubMain);
    }
}
