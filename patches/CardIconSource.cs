using RecompOne.Runtime.Cdrom;

namespace Kf2;

/// <summary>
/// Where the card icon is on this disc, for Verdite Core's CardIcon to read.
///
/// The icon's CLUT is in CD/COM/FDAT.T at <see cref="IconOffset"/>, and its three
/// frames are 0x30 past it. Its palette is in GAME.EXE as well, so that palette is
/// the signature the pixels are found by: the fixed offset is checked against it,
/// and a scan is what answers if some other mastering moved the archive's contents.
/// </summary>
public static class CardIconSource
{
    /// <summary>The CLUT in CD/COM/FDAT.T; the three frames are 0x30 past it.</summary>
    const int IconOffset = 0x14D210;

    /// <summary>The same sixteen colours in GAME.EXE, which is what names the icon.</summary>
    const int PaletteOffset = 0x56DB4;

    const int Frames = Verdite.Core.CardIcon.Frames, FrameBytes = Verdite.Core.WindowIcon.Side * Verdite.Core.WindowIcon.Side / 2;

    public static string? Read(DiscFs disc, out byte[] clut, out byte[] pixels)
    {
        clut = [];
        pixels = [];

        if (!disc.Locate("GAME.EXE", out int exeLba, out _)) return "no GAME.EXE";
        if (!disc.Locate("CD/COM/FDAT.T", out int fdatLba, out uint fdatSize)) return "no CD/COM/FDAT.T";

        var exe = disc.ReadSector(exeLba + PaletteOffset / 2048);
        // Entry 0 is transparent in the card's header and opaque in the archive's
        // copy, so the fifteen that are the icon's colours are what is compared.
        var signature = exe[(PaletteOffset % 2048 + 2)..(PaletteOffset % 2048 + 32)];

        int offset = IconOffset;
        var sector = disc.ReadSector(fdatLba + offset / 2048);
        if (!Same(sector, offset % 2048 + 2, signature))
        {
            var whole = disc.ReadSectors(fdatLba, (int)fdatSize);
            offset = IndexOf(whole, signature) - 2;
            if (offset < 0 || offset + 0x30 + Frames * FrameBytes > whole.Length)
                return "the icon is not in this image";
            clut = whole[offset..(offset + 32)];
            pixels = whole[(offset + 0x30)..(offset + 0x30 + Frames * FrameBytes)];
            return null;
        }

        int at = offset % 2048;
        clut = sector[at..(at + 32)];
        pixels = sector[(at + 0x30)..(at + 0x30 + Frames * FrameBytes)];
        return null;
    }

    static bool Same(byte[] data, int at, byte[] want)
    {
        if (at < 0 || at + want.Length > data.Length) return false;
        for (int i = 0; i < want.Length; i++)
            if (data[at + i] != want[i])
                return false;
        return true;
    }

    static int IndexOf(byte[] data, byte[] want)
    {
        for (int i = 0; i + want.Length <= data.Length; i++)
            if (Same(data, i, want))
                return i;
        return -1;
    }
}
