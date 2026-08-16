using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
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

    static int _played;

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

        Console.WriteLine("[KF2] ending: holding the last frame");
        for (;;)
        {
            c.A0 = 0;
            LibEtc.VSync(c, m);
        }
    }
}
