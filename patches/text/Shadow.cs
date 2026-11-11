using System;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2.Text;

/// <summary>
/// A drop shadow a pixel down and right of the letters, as the Japanese Disc
/// mod letters the movies. Two switches, both off by default:
///
///  - Pictures (signs, dialogue, descriptions, cards; the disc's own and the
///    ones this redraws): func_800356F4 fades a message in and out a frame
///    at a time, drawing the picture twice at brightness b, subtractive (ordering
///    slot 1) then additive (slot 0); the letters' colours are semi-transparent
///    and the background colour is transparent. The shadow is one more copy of
///    the subtractive quad, a pixel down and right, at brightness 2b: it takes
///    the letters' shape out of the scene under them, so it is as dark as the
///    letters are bright and fades with them. It is added before each frame is
///    presented (a pre on stage 13's presenter func_8002E0FC), found in slot 1
///    of the table that frame's head (func_8002E064) set up: the game's own fade
///    and the port's live-world one (patches/MenuWorld.cs) both draw through
///    those two. Nothing in the picture changes: a shadow drawn into the picture
///    itself shows nothing where it is see-through, and cannot fade if it is opaque.
///
///  - Menus (labels and numbers): each POLY_FT4 the game links with
///    func_80022A28 from the label or number template's page gets a copy one
///    pixel down and right, coloured near black, linked onto the same ordering
///    slot after it. AddPrim puts it at the head of the slot's chain, so the GPU
///    draws it first, under the letter. The copy comes out of the same
///    primitive buffer; if the buffer has no room for it, no shadow. The
///    windows (semi-transparent, their own templates) are left alone. A mod that
///    links its own letters (the Encyclopedia) asks for the same through the
///    published "MenuShadow".
/// </summary>
static class Shadow
{
    public const uint Link = 0x80022A28;              // func_80022A28(slot): AddPrim the quad at the cursor
    const uint Cursor = 0x8006E914, ActiveDescriptor = 0x8017E0A4, OrderingTable = 0x8018E0A8;
    const uint TextTemplate = 0x80064BF0, NumberTemplate = 0x80064BE4;
    const int QuadSize = 0x28;
    public const uint KeepFree = 0x1000;              // a mod's shadows leave this much of the buffer
    const byte MenuShade = 0x10;                      // texture x 0x10/0x80: near black

    public static bool Pictures, Menus;

    // ---- menus ----

    static uint _quad, _slot;

    public static void BeforeLink(CpuContext c, IMemory m)
    {
        _quad = 0;
        if (!Menus) return;
        _quad = m.ReadU32(Cursor);
        _slot = c.A0;
    }

    public static void AfterLink(IMemory m)
    {
        uint p = _quad;
        _quad = 0;
        Under(m, p, _slot);
    }

    /// <summary>The shadow under a letter quad just linked onto a slot, as
    /// func_80022A28 links it: for a mod that builds its own menu letters (the
    /// Encyclopedia's lists), published as "MenuShadow". Those leave KeepFree
    /// bytes of the buffer for what the game draws after them.</summary>
    public static void Under(IMemory m, uint p, uint slot, uint keep = 0)
    {
        if (!Menus || p < 0x80000000 || slot >= 0x2000) return;
        byte code = m.ReadU8(p + 7);
        if ((code & 0xFC) != 0x2C || (code & 2) != 0) return;            // POLY_FT4, opaque
        ushort tpage = m.ReadU16(p + 0x16), clut = m.ReadU16(p + 0x0E);
        bool text = tpage == m.ReadU16(TextTemplate) && clut == m.ReadU16(TextTemplate + 2);
        bool number = tpage == m.ReadU16(NumberTemplate) && clut == m.ReadU16(NumberTemplate + 2);
        if (!text && !number) return;

        uint entry = m.ReadU32(OrderingTable) + slot * 4;
        uint head = m.ReadU32(entry);
        if ((head & 0x00FFFFFF) != (p & 0x00FFFFFF)) return;             // not linked where expected
        uint q = m.ReadU32(Cursor);
        uint desc = m.ReadU32(ActiveDescriptor);
        if (q != p + QuadSize || desc < 0x80000000) return;
        uint end = m.ReadU32(desc + 4);
        if (end >= 0x80000000 && q + QuadSize + keep > end) return;     // no room this frame

        for (uint k = 0; k < QuadSize; k += 4) m.WriteU32(q + k, m.ReadU32(p + k));
        m.WriteU8(q + 4, MenuShade);
        m.WriteU8(q + 5, MenuShade);
        m.WriteU8(q + 6, MenuShade);
        for (uint v = 0; v < 4; v++)
        {
            uint at = q + 8 + v * 8;
            m.WriteU16(at, (ushort)(m.ReadU16(at) + 1));
            m.WriteU16(at + 2, (ushort)(m.ReadU16(at + 2) + 1));
        }
        uint tag = m.ReadU32(q);
        m.WriteU32(q, (tag & 0xFF000000) | (head & 0x00FFFFFF));
        m.WriteU32(entry, (head & 0xFF000000) | (q & 0x00FFFFFF));
        uint next = q + QuadSize;
        m.WriteU32(Cursor, next);
        m.WriteU32(desc + 8, next);
    }

    /// <summary>Is the quad at <paramref name="p"/> one of the menu shadows? A
    /// caller that recolours the quads a label drew (the PORT SETTINGS page's dimmed
    /// rows) leaves these alone.</summary>
    public static bool IsMenuShadow(IMemory m, uint p) =>
        (m.ReadU8(p + 7) & 0xFC) == 0x2C && m.ReadU8(p + 4) == MenuShade
        && m.ReadU8(p + 5) == MenuShade && m.ReadU8(p + 6) == MenuShade;

    // ---- the short messages in play (36 GOLD, LEVEL UP, ...) ----

    /// <summary>func_80033D24(rec, blend, rgb): one sprite of a message or of the
    /// amount beside GOLD, a semi-transparent POLY_FT4 from the frame's buffer
    /// (descriptor +8) linked onto ordering slot 1; blend 0x40 is the subtractive
    /// pass, 0x20 the additive one. Under the subtractive quad goes one more copy,
    /// a pixel down and right at twice its brightness, as under a picture: it
    /// fades with the message. Under the menu switch, since these are lettered in
    /// the menu font.</summary>
    public const uint HudSprite = 0x80033D24;
    const uint Subtractive = 0x40;
    static uint _hud;

    public static void BeforeHudSprite(CpuContext c, IMemory m)
    {
        _hud = 0;
        if (!Menus || c.A1 != Subtractive) return;
        uint desc = m.ReadU32(ActiveDescriptor);
        if (desc < 0x80000000) return;
        _hud = m.ReadU32(desc + 8);
    }

    public static void AfterHudSprite(IMemory m)
    {
        uint p = _hud;
        _hud = 0;
        if (!Menus || p < 0x80000000) return;
        uint desc = m.ReadU32(ActiveDescriptor);
        if (desc < 0x80000000 || m.ReadU32(desc + 8) != p + QuadSize) return;
        uint entry = m.ReadU32(OrderingTable) + 4, head = m.ReadU32(entry);
        if ((head & 0x00FFFFFF) != (p & 0x00FFFFFF) || (m.ReadU8(p + 7) & 0xFE) != 0x2E) return;   // not drawn
        uint q = p + QuadSize, end = m.ReadU32(desc + 4);
        if (end >= 0x80000000 && q + QuadSize > end) return;            // no room this frame

        for (uint k = 0; k < QuadSize; k += 4) m.WriteU32(q + k, m.ReadU32(p + k));
        for (uint k = 4; k < 7; k++) m.WriteU8(q + k, (byte)Math.Min(255, m.ReadU8(p + k) * 2));
        for (uint v = 0; v < 4; v++)
        {
            uint at = q + 8 + v * 8;
            m.WriteU16(at, (ushort)(m.ReadU16(at) + 1));
            m.WriteU16(at + 2, (ushort)(m.ReadU16(at + 2) + 1));
        }
        uint qt = m.ReadU32(q);
        m.WriteU32(q, (qt & 0xFF000000) | (head & 0x00FFFFFF));
        m.WriteU32(entry, (head & 0xFF000000) | (q & 0x00FFFFFF));
        m.WriteU32(desc + 8, q + QuadSize);
    }

    // ---- pictures ----

    public const uint FrameHead = 0x8002E064, Presenter = 0x8002E0FC;
    const uint MessageClut = (511u << 6) | (576u >> 4);
    const ushort MessageX = 0x20, MessageY = 0x0A;
    const int MostWalked = 256;

    static uint _ot;

    /// <summary>Post on stage 13's head: the table this frame links into.</summary>
    public static void AfterFrameHead(IMemory m) => _ot = Pictures ? m.ReadU32(OrderingTable) : 0;

    /// <summary>Pre on stage 13's presenter: a shadow under a message's letters.</summary>
    public static void BeforePresent(IMemory m)
    {
        uint ot = _ot;
        _ot = 0;
        if (!Pictures || ot < 0x80000000) return;
        uint entry = ot + 4, head = m.ReadU32(entry);
        uint p = head & 0x00FFFFFF;
        uint quad = 0;
        for (int k = 0; k < MostWalked && p != 0x00FFFFFF; k++)
        {
            uint at = 0x80000000 | p;
            uint tag = m.ReadU32(at);
            if ((tag >> 24) == 9 && (m.ReadU8(at + 7) & 0xFE) == 0x2E
                && m.ReadU16(at + 0x0E) == MessageClut
                && ((m.ReadU16(at + 0x16) >> 5) & 3) == 2                  // subtractive
                && m.ReadU16(at + 8) == MessageX && m.ReadU16(at + 0x0A) == MessageY)
            {
                quad = at;
                break;
            }
            p = tag & 0x00FFFFFF;
        }
        if (quad == 0) return;

        uint desc = m.ReadU32(ActiveDescriptor);
        if (desc < 0x80000000) return;
        uint q = m.ReadU32(desc + 8), end = m.ReadU32(desc + 4);
        if (q < 0x80000000 || q + QuadSize > end) return;
        for (uint k = 0; k < QuadSize; k += 4) m.WriteU32(q + k, m.ReadU32(quad + k));
        byte b = (byte)Math.Min(255, m.ReadU8(quad + 4) * 2);
        m.WriteU8(q + 4, b);
        m.WriteU8(q + 5, b);
        m.WriteU8(q + 6, b);
        for (uint v = 0; v < 4; v++)
        {
            uint at = q + 8 + v * 8;
            m.WriteU16(at, (ushort)(m.ReadU16(at) + 1));
            m.WriteU16(at + 2, (ushort)(m.ReadU16(at + 2) + 1));
        }
        uint qt = m.ReadU32(q);
        m.WriteU32(q, (qt & 0xFF000000) | (head & 0x00FFFFFF));
        m.WriteU32(entry, (head & 0xFF000000) | (q & 0x00FFFFFF));
        m.WriteU32(desc + 8, q + QuadSize);
    }
}
