namespace Kf2;

/// <summary>
/// The addresses this port knows, with the names the frame profiler shows for them.
/// Verdite Core's <c>FrameProfiler</c> takes this table in <c>Configure</c>; a row
/// whose label starts "stage " is one of the main-loop stages the "stages" probe times.
/// </summary>
static class ProfilerKnown
{
    public static readonly (string Overlay, uint Addr, string Label)[] Table =
    [
        ("game", 0x8002C944, "stage 1"),
        ("game", 0x80037C0C, "stage 2: object state machine"),
        ("game", 0x8002A550, "stage 3: player, pad, menu"),
        ("game", 0x80040348, "stage 4: entity table"),
        ("game", 0x80046A60, "stage 5: effects and projectiles"),
        ("game", 0x8004910C, "stage 6: area module per-frame"),
        ("game", 0x8001689C, "stage 7: area loader"),
        ("game", 0x80025A1C, "stage 8"),
        ("game", 0x800140AC, "stage 9"),
        ("game", 0x8002CA74, "stage 10"),
        ("game", 0x80016FC8, "stage 11"),
        ("game", 0x80014534, "stage 12"),
        ("game", 0x800342D8, "stage 13: renderer"),
        ("game", 0x80033FBC, "fade stepper"),
        ("game", 0x8002DC78, "animated textures"),
        ("game", 0x80017880, "frame gate"),
        ("game", 0x80018E80, "in-game menu loop"),
        ("game", 0x80037B5C, "transition fade loop"),
        ("game", 0x80060818, "DrawOTag"),
        ("game", 0x8005FCC8, "VSync thunk"),
        ("open", 0x80016078, "DrawOTag"),
        ("open", 0x8001EB88, "VSync thunk"),
        ("end", 0x80013D80, "DrawOTag"),
        ("end", 0x8001B154, "VSync thunk"),
    ];
}
