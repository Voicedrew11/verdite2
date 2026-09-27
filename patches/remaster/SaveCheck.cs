using System.Text.Json.Nodes;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Remaster;

/// <summary>
/// Whether anything of the tile block reaches a save: the measurement Phase 6 depends
/// on (the <c>savecheck</c> shell verb).
///
/// The save is <c>func_80023764</c> writing the card buffer (<c>*(u32*)0x8006E98C</c>,
/// 0x4000 bytes) after <c>func_80049A88(buf + 0x400)</c> packs the game state into it.
/// The check runs that packer three times on the same buffer: as the game stands, with
/// every byte of the tile block and the collision shapes inverted, and with only the
/// player's X inverted, which is the control -- the packer does carry the position, so
/// a method that cannot see a change there proves nothing. Everything it touched is put
/// back: the buffer, both blocks, the position and the CPU. Nothing is written to a card.
///
/// <c>func_800492B8</c>, the per-area state the save carries through the heap at
/// <c>0x801B3188</c>, is not run, since it allocates; read off the code, it packs the
/// creature, descriptor and object tables and nothing of the tile block. See "Phase 6,
/// the first slice" in docs/REMASTER.md.
/// </summary>
public static class SaveCheck
{
    const uint CardBuffer = 0x8006E98C, CardBytes = 0x4000, Packed = 0x400;
    const uint PlayerX = 0x801994EC;

    public static string Usage => "savecheck - does the save carry any of the tile block? runs the game's save packer with the block inverted, and with the position as the control";

    public static JsonObject Run(CpuContext c, IMemory m)
    {
        if (Identity.Area < 0) throw new InvalidOperationException("not in an area");
        uint buf = m.ReadU32(CardBuffer);
        if ((buf & 0xFF000000u) != 0x80000000u || (buf & 0x00FFFFFFu) + CardBytes > 0x00200000u)
            throw new InvalidOperationException($"the card buffer pointer reads 0x{buf:X8}");

        var keep = Read(m, buf, CardBytes);
        var saved = c.Snapshot();
        byte[] asIs, blockInverted, controlInverted;
        try
        {
            asIs = PackOnce(c, m, buf, keep);
            Invert(m, Identity.TileBase, Identity.TileBytes);
            Invert(m, Identity.ShapeBase, Identity.ShapeBytes);
            blockInverted = PackOnce(c, m, buf, keep);
            Invert(m, Identity.TileBase, Identity.TileBytes);
            Invert(m, Identity.ShapeBase, Identity.ShapeBytes);
            Invert(m, PlayerX, 4);
            controlInverted = PackOnce(c, m, buf, keep);
            Invert(m, PlayerX, 4);
        }
        finally
        {
            Write(m, buf, keep);
            c.Restore(saved);
        }

        int block = Differ(asIs, blockInverted), control = Differ(asIs, controlInverted);
        return new JsonObject
        {
            ["area"] = Identity.Area,
            ["packedBytes"] = asIs.Length,
            ["tileBlockMovedBytes"] = block,
            ["controlMovedBytes"] = control,
            ["verdict"] = control == 0 ? "inconclusive: the control moved nothing"
                        : block == 0 ? "the save carries nothing of the tile block"
                        : "the save carries part of the tile block",
        };
    }

    static byte[] PackOnce(CpuContext c, IMemory m, uint buf, byte[] keep)
    {
        Write(m, buf, keep);
        c.A0 = buf + Packed;
        KingsField2.func_80049A88(c, m);
        return Read(m, buf + Packed, CardBytes - Packed);
    }

    static byte[] Read(IMemory m, uint at, uint bytes)
    {
        var b = new byte[bytes];
        for (uint i = 0; i < bytes; i++) b[i] = m.ReadU8(at + i);
        return b;
    }

    static void Write(IMemory m, uint at, byte[] b)
    {
        for (uint i = 0; i < b.Length; i++) m.WriteU8(at + i, b[i]);
    }

    static void Invert(IMemory m, uint at, uint bytes)
    {
        for (uint i = 0; i < bytes; i++) m.WriteU8(at + i, (byte)~m.ReadU8(at + i));
    }

    static int Differ(byte[] a, byte[] b)
    {
        int n = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) n++;
        return n;
    }
}
