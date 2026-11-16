using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Shortcuts;

/// <summary>
/// How the hands cross (<see cref="HandsHud"/>) answers the player:
///
/// - **It fades when idle.** <see cref="IdleMs"/> after the last press or change, the
///   cross dims over <see cref="FadeMs"/> to <see cref="DimFloor"/> and then goes
///   half-transparent: the PS1's blend mode 0, the only see-through the hardware has
///   (there is no alpha to step smoothly), which on a textured face blends only the
///   texels whose STP bit is set and leaves the rest dimmed. Any press, any change to
///   what a card shows, and the HUD coming back after <see cref="AbsentMs"/> away (a
///   menu, a load, a message) bring it back solid at once.
/// - **A card that changes pulses**: brighter and a pixel larger, easing back over
///   <see cref="PulseMs"/>. Changes are noticed by what the card shows, so a swap, a
///   cycle, an item used up and an equip in the menu all pulse the same way.
/// - **A press that does nothing flashes its card's background** red, fading over
///   <see cref="FlashMs"/>, and plays the menu's cancel sound; a swap or a hotkey step plays the menu's cursor sound
///   (the game's weapon equip func_80026210 plays none: measured, the voice table at
///   0x801989B0 unchanged across it).
///
/// The look is applied to the packets the cross has just put in the frame's buffer
/// (<see cref="Tint"/>), so the frames, the models and the digits are all treated alike
/// whichever assembler built them. Timed in real time, so it is as smooth as the frame
/// rate. Game thread: everything here runs in player control's pre-hook or the HUD
/// builder's post-hook.
/// </summary>
static class CrossFeedback
{
    public enum Card { Magic, Hotkey, Weapon }
    const int Cards = 3;

    const long IdleMs = 4000, FadeMs = 500, AbsentMs = 1000;
    const long PulseMs = 150, FlashMs = 250;
    const float DimFloor = 0.8f, PulseGain = 0.5f;
    const float FlashRed = 0.8f, FlashOthers = 0.5f;   // red up by this much at the flash, green and blue down

    /// <summary>Gameplay ▸ Shortcuts ▸ <i>Fade when idle</i>; <c>KF2_SHORTCUTS_FADE</c>.</summary>
    public static bool FadeWhenIdle { get; set; } = true;

    /// <summary><c>KF2_SHORTCUTS_PROBE</c>: a line when the cross fades or comes back,
    /// for each pulse, refusal and sound.</summary>
    internal static bool Probe { get; set; }

    static bool _wasFaded;

    static long _activeMs, _drawnMs = long.MinValue / 2;
    static readonly long[] _pulseMs = [long.MinValue / 2, long.MinValue / 2, long.MinValue / 2];
    static readonly long[] _flashMs = [long.MinValue / 2, long.MinValue / 2, long.MinValue / 2];

    const ulong Unseen = ulong.MaxValue;
    static readonly ulong[] _shows = [Unseen, Unseen, Unseen];

    static long Now => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    // ---- what happened; player control's pre-hook ----

    /// <summary>A press, or anything else that means the player is looking.</summary>
    internal static void Wake() => _activeMs = Now;

    /// <summary>What <paramref name="card"/> shows this tick; a change from what it last
    /// showed pulses it. The first sight after <see cref="Forget"/> does not.</summary>
    internal static void Notice(Card card, ulong shows)
    {
        ulong was = _shows[(int)card];
        _shows[(int)card] = shows;
        if (was == Unseen || was == shows) return;
        _pulseMs[(int)card] = Now;
        Wake();
        if (Probe) Console.WriteLine($"[KF2] shortcuts: {card} card pulses ({was:X} -> {shows:X})");
    }

    /// <summary>A new game or a loaded save: what the cards showed before is no news.</summary>
    internal static void Forget() => Array.Fill(_shows, Unseen);

    /// <summary>A press for <paramref name="card"/> that did nothing.</summary>
    internal static void Refused(CpuContext c, IMemory m, Card card)
    {
        _flashMs[(int)card] = Now;
        Wake();
        Play(c, m, RefuseSound.Se, RefuseSound.Volume);
        if (Probe) Console.WriteLine($"[KF2] shortcuts: {card} refused, flash and sound 0x{RefuseSound.Se:X2}");
    }

    /// <summary>A hand swapped or a hotkey stepped: the menu's cursor sound.</summary>
    internal static void Stepped(CpuContext c, IMemory m)
    {
        Wake();
        Play(c, m, CursorSound, MenuVolume);
        if (Probe) Console.WriteLine($"[KF2] shortcuts: stepped, sound 0x{CursorSound:X2}");
    }

    // ---- the sounds: the menu's own, at its own volume ----

    // func_80022DC4(id), the menu's sound: func_800142F4(id, 0x30, 0x30, 0) for 0x10-0x12.
    // func_8001EB70, the list cursor, plays 0x10 on a step and 0x12 on a back out.
    const uint CursorSound = 0x10, MenuVolume = 0x30;

    /// <summary>The refusal's sound: the menu's cancel, 0x12, unless
    /// <c>KF2_SHORTCUTS_REFUSE=&lt;hex&gt;[,&lt;hex volume&gt;]</c> names another of the
    /// game's sound ids (the shell's <c>hands sound:ID</c> plays one to try).</summary>
    internal static (uint Se, uint Volume) RefuseSound { get; set; } = (0x12, MenuVolume);

    /// <summary>A sound id and an optional volume in hex, <c>12</c> or <c>0C,60</c>.</summary>
    internal static bool TryParseSound(string text, out (uint Se, uint Volume) sound)
    {
        sound = default;
        var parts = text.Split(',');
        if (parts.Length > 2 || !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out uint se) || se > 0xFF)
            return false;
        uint volume = MenuVolume;
        if (parts.Length == 2 && (!uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out volume) || volume > 0x7F))
            return false;
        sound = (se, volume);
        return true;
    }

    /// <summary>One of the game's sounds, centred; the shell's audition.</summary>
    internal static void Audition(CpuContext c, IMemory m, (uint Se, uint Volume) sound)
    {
        Play(c, m, sound.Se, sound.Volume);
        Console.WriteLine($"[KF2] shortcuts: played sound 0x{sound.Se:X2} at volume 0x{sound.Volume:X2}");
    }
    const uint FakeRa = 0x8001FFF4;

    static void Play(CpuContext c, IMemory m, uint se, uint volume)
    {
        var snap = c.Snapshot();
        try
        {
            c.A0 = se;
            c.A1 = volume;
            c.A2 = volume;
            c.A3 = 0;
            c.RA = FakeRa;
            KingsField2.func_800142F4(c, m);
        }
        finally { c.Restore(snap); }
    }

    // ---- the look; the HUD builder's post-hook ----

    /// <summary>One card's look this frame.</summary>
    /// <summary>One card's look this frame; <see cref="Red"/> is the refusal's flash, 0 to 1,
    /// and only its background (<see cref="FrameLookOf"/>) carries it.</summary>
    internal readonly record struct Look(float Brightness, int Grow, bool Translucent, float Red = 0f);

    /// <summary>Called once before the cross is drawn: a HUD away for a while is coming
    /// back, and the player will want to see it.</summary>
    internal static void BeginFrame()
    {
        long now = Now;
        if (now - _drawnMs > AbsentMs) _activeMs = now;
        _drawnMs = now;

        bool faded = LookOf(Card.Hotkey).Translucent;
        if (Probe && faded != _wasFaded) Console.WriteLine($"[KF2] shortcuts: cross {(faded ? "faded" : "solid")}");
        _wasFaded = faded;
    }

    /// <summary>The look of <paramref name="card"/>'s background, its frame: its
    /// <see cref="LookOf"/>, with the refusal's flash.</summary>
    internal static Look FrameLookOf(Card card)
    {
        long t = Now - _flashMs[(int)card];
        float red = t >= 0 && t < FlashMs ? 1f - (float)t / FlashMs : 0f;
        return LookOf(card) with { Red = red };
    }

    internal static Look LookOf(Card card)
    {
        long now = Now;
        float brightness = 1f;
        bool translucent = false;
        long idle = now - _activeMs - IdleMs;
        if (FadeWhenIdle && idle > 0)
        {
            float f = Math.Min(1f, (float)idle / FadeMs);
            brightness = 1f + (DimFloor - 1f) * f;
            translucent = f >= 1f;
        }

        long p = now - _pulseMs[(int)card];
        int grow = 0;
        if (p >= 0 && p < PulseMs)
        {
            float k = 1f - (float)p / PulseMs;
            brightness *= 1f + PulseGain * k;
            grow = k > 0.5f ? 1 : 0;
        }
        return new Look(brightness, grow, translucent);
    }

    /// <summary>Applies <paramref name="look"/> to the GPU packets in [<paramref name="from"/>,
    /// <paramref name="to"/>) of the frame's buffer: every colour scaled, and with
    /// <see cref="Look.Translucent"/> the semi-transparent bit and blend mode 0. Polygons
    /// and sprites only; anything else (the icons' draw-area packets) is stepped over.
    /// Stops at a length that is not a packet's, so a buffer it misreads is left alone.</summary>
    internal static void Tint(IMemory m, uint from, uint to, Look look)
    {
        if (look.Brightness == 1f && !look.Translucent && look.Red == 0f) return;
        for (uint p = from; p < to;)
        {
            int words = m.ReadU8(p + 3);
            if (words == 0 || words > MaxWords || p + 4 + words * 4u > to) return;
            TintPacket(m, p, words, look);
            p += 4 + (uint)words * 4;
        }
    }

    const int MaxWords = 12;                                  // a POLY_GT4

    static void TintPacket(IMemory m, uint p, int words, Look look)
    {
        byte code = m.ReadU8(p + 7);
        bool polygon = code is >= 0x20 and < 0x40, sprite = code is >= 0x60 and < 0x80;
        if (!polygon && !sprite) return;
        bool textured = (code & 0x04) != 0;
        // A face the game already blends (the card frames are the HP panel's, 0x3E) keeps
        // its own blend mode: only its colour is dimmed.
        bool translucent = look.Translucent && (code & 0x02) == 0;

        if (sprite)
        {
            Scale(m, p + 4, look);
            if (translucent) m.WriteU8(p + 7, (byte)(code | 0x02));
            return;
        }

        bool gouraud = (code & 0x10) != 0;
        int vertices = (code & 0x08) != 0 ? 4 : 3;
        int perVertex = 1 + (textured ? 1 : 0) + (gouraud ? 1 : 0);
        int need = vertices * perVertex + (gouraud ? 0 : 1);
        if (words != need) return;

        uint at = p + 4;
        for (int v = 0; v < vertices; v++)
        {
            if (v == 0 || gouraud)
            {
                Scale(m, at, look);
                at += 4;
            }
            at += 4;                                           // x, y
            if (textured)
            {
                // vertex 1's UV word carries the texture page; blend mode 0 is bits 5-6 clear.
                if (v == 1 && translucent) m.WriteU16(at + 2, (ushort)(m.ReadU16(at + 2) & ~0x60));
                at += 4;
            }
        }
        if (translucent) m.WriteU8(p + 7, (byte)(code | 0x02));
    }

    /// <summary>One packet colour, scaled by the look's brightness and pushed toward red
    /// by its flash. A textured face's 0x80 is the texture as it is, so it may go up to
    /// 0xFF (twice as bright).</summary>
    static void Scale(IMemory m, uint colour, Look look)
    {
        float red = look.Brightness * (1f + FlashRed * look.Red);
        float other = look.Brightness * (1f - FlashOthers * look.Red);
        Channel(m, colour, red);
        Channel(m, colour + 1, other);
        Channel(m, colour + 2, other);
    }

    static void Channel(IMemory m, uint at, float k) =>
        m.WriteU8(at, (byte)Math.Clamp((int)MathF.Round(m.ReadU8(at) * k), 0, 255));
}
