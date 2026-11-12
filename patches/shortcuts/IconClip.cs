using RecompOne.Runtime.Memory;

namespace Kf2.Shortcuts;

/// <summary>
/// Keeps an icon's model inside its card. The icons are drawn larger than the card
/// (<see cref="HandsIcons"/>), so the polygons the lit assembler links for one model are
/// bracketed in the ordering table by two GPU draw-area packets (GP0 E3/E4): one setting
/// the card's inside, one putting back the frame's own clip, read from the draw
/// environment this frame is drawn under (the buffer index the frame head flips,
/// 0x8017E084, into the two DRAWENVs at 0x8018E0AC). The renderer's scissor and the
/// software rasteriser both honour the draw area.
///
/// The assembler links each polygon at the head of its slot (AddPrim), so after the call
/// a slot whose head moved begins with the model's packets and ends at the old head; the
/// bracket goes round exactly those, slot by slot.
///
/// Game thread, inside the HUD builder's post-hook.
/// </summary>
static class IconClip
{
    const uint BufferIndex = 0x8017E084;                     // u8, flipped by the frame head func_80022530
    const uint DrawEnvs = 0x8018E0AC, DrawEnvStride = 0x5C;  // DRAWENV: clip RECT at +0, ofs at +8
    const int Entries = (int)ScenePass.OtEntries;
    const uint AreaBytes = 12;                               // tag, E3, E4
    const uint AddressBits = 0x00FFFFFFu;

    /// <summary>A draw area as its two GP0 words.</summary>
    internal readonly record struct Area(uint TopLeft, uint BottomRight);

    /// <summary>The clip for a model: the card's inside, and the frame's to put back.</summary>
    internal readonly record struct Bracket(Area Inside, Area Frame);

    static readonly uint[] _heads = new uint[Entries];
    static uint _table;
    static long _wrapped, _slots, _dropped, _unclipped;

    /// <summary>For the shell's <c>hands</c> verb: models bracketed, slots bracketed,
    /// models dropped for want of room, and cards drawn whole (no clip possible).</summary>
    internal static string Json =>
        $"{{\"wrapped\":{_wrapped},\"slots\":{_slots},\"dropped\":{_dropped},\"whole\":{_unclipped}}}";

    /// <summary>The bracket for the screen rectangle (x, y, w, h), or null when it does
    /// not lie inside the frame's own clip (a card anchored into the widescreen margin),
    /// where a draw area cannot express it.</summary>
    internal static Bracket? For(IMemory m, int x, int y, int w, int h)
    {
        uint env = DrawEnvs + m.ReadU8(BufferIndex) % 2u * DrawEnvStride;
        int cx = (short)m.ReadU16(env), cy = (short)m.ReadU16(env + 2);
        int cw = (short)m.ReadU16(env + 4), ch = (short)m.ReadU16(env + 6);
        int ox = (short)m.ReadU16(env + 8), oy = (short)m.ReadU16(env + 0xA);
        if (cw <= 0 || ch <= 0 || w <= 0 || h <= 0) return null;

        int left = ox + x, top = oy + y, right = left + w - 1, bottom = top + h - 1;
        if (left < cx || top < cy || right > cx + cw - 1 || bottom > cy + ch - 1)
        {
            _unclipped++;
            return null;
        }
        return new Bracket(new Area(TopLeft(left, top), BottomRight(right, bottom)),
                           new Area(TopLeft(cx, cy), BottomRight(cx + cw - 1, cy + ch - 1)));
    }

    /// <summary>Records every slot's head of the current ordering table, before the model
    /// is assembled. False if the table cannot be read whole.</summary>
    internal static bool Mark(IMemory m)
    {
        if (m is not PSMemory ps) return false;
        _table = m.ReadU32(MenuDraw.OrderingTable);
        if (!ps.TryWords(_table, Entries, out var words)) return false;
        words.CopyTo(_heads);
        return true;
    }

    /// <summary>Brackets the packets linked since <see cref="Mark"/> with
    /// <paramref name="bracket"/>, out of the frame's primitive buffer. With no room for
    /// the packets, unlinks the model instead and returns false: an unclipped icon would
    /// cover its neighbours.</summary>
    internal static bool Wrap(IMemory m, Bracket bracket)
    {
        if (m is not PSMemory ps || !ps.TryWords(_table, Entries, out var now)) return false;

        int moved = 0;
        for (int i = 0; i < Entries; i++)
            if (now[i] != _heads[i]) moved++;
        if (moved == 0) return true;

        uint d = m.ReadU32(MenuDraw.ActiveDescriptor);
        uint at = m.ReadU32(d + 8), end = m.ReadU32(d + 4);
        if (at == 0 || end <= at || end - at < (uint)moved * 2 * AreaBytes)
        {
            for (int i = 0; i < Entries; i++)
                if (m.ReadU32(_table + (uint)i * 4) != _heads[i]) m.WriteU32(_table + (uint)i * 4, _heads[i]);
            _dropped++;
            return false;
        }

        for (int i = 0; i < Entries; i++)
        {
            uint entry = _table + (uint)i * 4;
            uint head = m.ReadU32(entry);
            if (head == _heads[i]) continue;
            uint last = LastNew(m, head, _heads[i]);
            if (last == 0) continue;                         // not a chain we recognise: left alone

            uint set = at, restore = at + AreaBytes;
            at += 2 * AreaBytes;
            WriteArea(m, set, bracket.Inside, head);
            WriteArea(m, restore, bracket.Frame, _heads[i]);
            uint lastTag = m.ReadU32(last);
            m.WriteU32(last, (lastTag & ~AddressBits) | (restore & AddressBits));
            m.WriteU32(entry, (head & ~AddressBits) | (set & AddressBits));
            _slots++;
        }
        _wrapped++;
        m.WriteU32(d + 8, at);
        m.WriteU32(MenuDraw.Cursor, at);
        return true;
    }

    /// <summary>The last packet of the chain from <paramref name="head"/> before it reaches
    /// <paramref name="old"/>, or 0 if it does not reach it within reason.</summary>
    static uint LastNew(IMemory m, uint head, uint old)
    {
        const int MaxPackets = 4096;
        uint p = 0x80000000u | (head & AddressBits);
        for (int n = 0; n < MaxPackets; n++)
        {
            uint next = m.ReadU32(p) & AddressBits;
            if (next == (old & AddressBits)) return p;
            if (next == AddressBits) return 0;
            p = 0x80000000u | next;
        }
        return 0;
    }

    static void WriteArea(IMemory m, uint p, Area a, uint next)
    {
        m.WriteU32(p, (2u << 24) | (next & AddressBits));
        m.WriteU32(p + 4, a.TopLeft);
        m.WriteU32(p + 8, a.BottomRight);
    }

    static uint TopLeft(int x, int y) => 0xE3000000u | Pack(x, y);
    static uint BottomRight(int x, int y) => 0xE4000000u | Pack(x, y);
    static uint Pack(int x, int y) => ((uint)Math.Clamp(y, 0, 511) << 10) | (uint)Math.Clamp(x, 0, 1023);
}
