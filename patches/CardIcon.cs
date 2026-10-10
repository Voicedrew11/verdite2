using RecompOne.Runtime.Cdrom;

namespace Kf2;

/// <summary>
/// The window icon is the game's own memory-card icon, read off the disc.
///
///     KF2_ICON=orb      the shipped verdite mark instead
///     KF2_ICON=off      no icon at all
///     KF2_ICON=1        a frame of the card icon's three (0 by default)
///     KF2_ICON_INSTALL=0   do not write the icon into the desktop's icon theme
///
/// Where the icon is on this disc is this file's; decoding it, scaling it by
/// whole multiples for every size a desktop asks for, and writing it into the
/// icon theme for Wayland are Verdite Core's WindowIcon and DesktopEntry. Nothing
/// is shipped: the bytes come from the player's own image at boot, and a disc
/// that does not answer leaves whatever Program.cs already set. See "The icon
/// comes off the disc" and "Wayland takes the icon from the desktop entry" in
/// docs/PACKAGING.md.
/// </summary>
public static class CardIcon
{
    /// <summary>The CLUT in CD/COM/FDAT.T; the three frames are 0x30 past it.</summary>
    const int IconOffset = 0x14D210;

    /// <summary>The same sixteen colours in GAME.EXE, which is what names the icon.</summary>
    const int PaletteOffset = 0x56DB4;

    const int Frames = 3, FrameBytes = WindowIcon.Side * WindowIcon.Side / 2;

    public static void Install(string? discPath)
    {
        if (WindowIcon.Frame(Frames, byDefault: 0) is not { } frame) return;

        var path = string.IsNullOrWhiteSpace(discPath) ? RecompOne.Runtime.Runtime.CdPath : discPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            using var disc = DiscFs.Open(path);
            if (Read(disc, out byte[] clut, out byte[] pixels) is { } why)
            {
                Console.Error.WriteLine($"[KF2] icon: {why}; keeping the shipped mark");
                return;
            }

            // The three frames are stored a row at a time -- frame 0's row y, then
            // frame 1's, then frame 2's -- which is the order a loop filling all
            // three of a card header's frames reads them in: a row stride of three.
            WindowIcon.Apply(WindowIcon.Decode(clut, pixels, Frames * WindowIcon.Side / 2, frame));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] icon: {e.Message}; keeping the shipped mark");
        }
    }

    /// <summary>
    /// The palette is in GAME.EXE as well as in FDAT.T, so it is the signature the
    /// pixels are found by: the fixed offset is checked against it, and a scan is
    /// what answers if some other mastering moved the archive's contents.
    /// </summary>
    static string? Read(DiscFs disc, out byte[] clut, out byte[] pixels)
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
