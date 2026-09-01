using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Boot straight into one of the three executables.
///
///     KF2_BOOTEXE=end|game|open
///
/// The ending is otherwise reachable only by finishing the game, which makes
/// anything wrong with <c>END.EXE</c> -- see <see cref="EndingHold"/> -- a bug
/// that cannot be reproduced in a diagnostic session at all. This is the
/// equivalent of <c>KF2_AUTOSTART</c> for the far end of the disc.
///
/// The mechanism is the boot stub's own. <c>SLUS_001.58</c> is a three-entry
/// loader: a table of file names at <c>0x80010254</c>
/// (<c>0</c> = <c>OPEN.EXE</c>, <c>1</c> = <c>GAME.EXE</c>, <c>2</c> =
/// <c>END.EXE</c>), an index at <c>0x80010268</c>, and a loop in
/// <c>func_80010038</c> that <c>Load</c>s the named file, <c>Exec</c>s it, and
/// then re-reads the index from the byte at <c>0x800102F0</c> -- which is how a
/// finished <c>GAME.EXE</c> asks for the ending. The index word ships as 0, so
/// writing it before the loop's first pass is the whole switch.
///
/// <c>GAME.EXE</c> and <c>END.EXE</c> booted this way get none of the state the
/// executable before them would have left, so this is a diagnostic and not a
/// way to play: it is honest for <c>END.EXE</c>, which is a movie player that
/// initialises everything it uses, and it is not for <c>GAME.EXE</c>.
/// </summary>
public static class BootExe
{
    const uint StubMain = 0x80010038;   // the loader loop
    const uint Selector = 0x80010268;   // its index into the file-name table

    static readonly ModInfo _self = new()
    {
        Id = "kf2.bootexe",
        Name = "Boot executable",
        Version = "1.0",
        Description = "Boots straight into OPEN.EXE, GAME.EXE or END.EXE.",
    };

    static int _index = -1;
    static bool _spent;

    public static void Configure(string? which)
    {
        if (string.IsNullOrWhiteSpace(which)) return;
        _index = which.Trim().ToLowerInvariant() switch
        {
            "open" => 0,
            "game" => 1,
            "end"  => 2,
            _ => -1,
        };
        if (_index < 0)
            Console.Error.WriteLine($"[KF2] boot exe: '{which}' is not open, game or end -- ignored");
    }

    public static void Install()
    {
        if (_index < 0) return;

        bool attached = false;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (attached) return;
            attached = true;
            Attach();
        });
    }

    static void Attach()
    {
        SymbolRegistry.Build();
        var target = SymbolRegistry.Resolve("main", null, StubMain);
        if (target == null)
        {
            Console.Error.WriteLine($"[KF2] boot exe: no boot-stub function at 0x{StubMain:X8}");
            return;
        }

        var impl = typeof(BootExe).GetMethod(nameof(BeforeLoaderLoop), BindingFlags.Public | BindingFlags.Static)!;
        if (!HookManager.AddPre(_self, target, impl)) return;
        HookManager.Commit();
    }

    /// <summary>
    /// The stub is about to read its index for the first time.
    ///
    /// Once, and only the first time: the loop is re-entered when an executable
    /// asks for another one — <see cref="EndingHold"/> returns to the title
    /// through exactly that door — and writing the index again there would send
    /// every such request back to whichever file this switch names. Measured:
    /// leaving the ending re-loaded <c>END.EXE</c> instead of the title.
    /// </summary>
    public static void BeforeLoaderLoop(CpuContext c, IMemory m)
    {
        if (_spent) return;
        _spent = true;
        m.WriteU32(Selector, (uint)_index);
        Console.WriteLine($"[KF2] boot exe: index {_index}");
    }
}
