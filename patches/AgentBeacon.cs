using RecompOne.Runtime.Events;

namespace Kf2;

/// <summary>
/// A machine-readable state line on stdout, so a program driving the port from
/// outside can tell where the game is without a screenshot (which project policy
/// rules out anyway):
///
///     KF2_AGENT=1    emit [KF2-AGENT] lines (off unless set)
///
/// Two kinds of line, both single-line and prefixed [KF2-AGENT] for grepping:
///
///   * a transition on every overlay load — open, game, end, fdatNN — so the boot
///     walk is legible;
///   * a JSON snapshot about once a second carrying the fields an agent needs to
///     answer "am I in the game yet": inGame (the same MaxHp != 0 test the rest of
///     the port uses), the overlay, HP/MP/level/exp/area/slot, dead, and position.
///
/// The point is the failure the port kept hitting with automated testers: an agent
/// that cannot see the screen cannot tell "stuck at the title" from "in an area",
/// so it waits on nothing and burns its budget. `inGame:false`, printed once a
/// second, is that answer.
///
/// Read-only. It reads memory on the game thread — the VSync event fires there, so
/// no cross-thread access — and writes stdout. A patch rather than a mod for the
/// KF2_AUTOPAD reason: it must work from an environment variable with no package to
/// enable.
/// </summary>
public static class AgentBeacon
{
    // buf2 stats and the state/position bytes, carried here because patches/ cannot
    // reach the mod's GameState.cs (the mod loader compiles it into a separate
    // assembly). This is the same map -- see mods/kf2debug/GameState.cs.
    const uint Exp         = 0x80199414;   // u32
    const uint Level       = 0x8019941C;   // u8
    const uint MaxHp       = 0x80199426;   // u16
    const uint Hp          = 0x80199428;   // u16
    const uint MaxMp       = 0x8019942A;   // u16
    const uint Mp          = 0x8019942C;   // u16
    const uint State       = 0x801994E1;   // u8
    const byte StateDead   = 0x11;
    const uint DeathFrames = 0x8019951A;   // u16
    const uint Area        = 0x8017E060;   // u8
    const uint CurrentSlot = 0x8006E5D4;   // u8
    const uint PosX        = 0x801994EC;   // s32
    const uint PosY        = 0x801994F0;   // s32
    const uint PosZ        = 0x801994F4;   // s32

    const long PeriodMs = 1000;

    static bool _on;
    static string _overlay = "boot";
    static long _lastEmit;

    public static void Configure(string? on)
    {
        _on = !string.IsNullOrWhiteSpace(on)
              && on.Trim().ToLowerInvariant() is "1" or "on" or "true" or "yes";
    }

    public static void Install()
    {
        if (!_on) return;

        Event.AddListener<OverlayLoadedEvent>(e =>
        {
            _overlay = e.Name;
            Console.WriteLine($"[KF2-AGENT] overlay {e.Name}");
        });

        Event.AddListener<VSyncEvent>(_ =>
        {
            long now = Environment.TickCount64;
            if (now - _lastEmit < PeriodMs) return;
            _lastEmit = now;
            Emit();
        });

        Console.WriteLine("[KF2-AGENT] beacon on");
    }

    static void Emit()
    {
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return;

        // Not in an area: the one field an agent needs to stop waiting.
        if (m.ReadU16(MaxHp) == 0)
        {
            Console.WriteLine($"[KF2-AGENT] {{\"overlay\":\"{_overlay}\",\"inGame\":false}}");
            return;
        }

        bool dead = m.ReadU8(State) == StateDead;
        int x = (int)m.ReadU32(PosX), y = (int)m.ReadU32(PosY), z = (int)m.ReadU32(PosZ);
        Console.WriteLine(
            $"[KF2-AGENT] {{\"overlay\":\"{_overlay}\",\"inGame\":true," +
            $"\"dead\":{(dead ? "true" : "false")}," +
            $"\"hp\":{m.ReadU16(Hp)},\"maxHp\":{m.ReadU16(MaxHp)}," +
            $"\"mp\":{m.ReadU16(Mp)},\"maxMp\":{m.ReadU16(MaxMp)}," +
            $"\"level\":{m.ReadU8(Level)},\"exp\":{m.ReadU32(Exp)}," +
            $"\"area\":{m.ReadU8(Area)},\"slot\":{m.ReadU8(CurrentSlot)}," +
            $"\"deathFrames\":{m.ReadU16(DeathFrames)}," +
            $"\"pos\":[{x},{y},{z}]}}");
    }
}
