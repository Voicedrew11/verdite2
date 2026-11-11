using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using RecompOne.Runtime.Cdrom;

namespace Kf2.Japanese;

/// <summary>
/// The Japanese King's Field II's pictures, laid over the US King's Field's sectors: the
/// pure half of the Japanese layer. <see cref="JapaneseDisc"/> finds the discs and
/// installs it; Verdite Core's MixedDiscImage serves what this builds. Bytes in, bytes
/// out: it keeps no state and logs nothing, and reads the two discs only through the
/// DiscFs it is given.
///
/// What is replaced, measured on all three Japanese discs (docs/JAPANESE.md):
///
///     CD/COM/TALK.T   every entry whose Japanese copy is a TIM picture and differs:
///                     the dialogue and the signs (379 of 755, all its non-empty ones)
///     CD/COM/ITEM.T   the same, TIMs only: the item descriptions (276 of 548). The
///                     two other entries that differ (74, 79) are 3D models and stay US
///     CD/COM/FDAT.T   entry 47's block at VRAM (704, 256): the messages in play
///                     (LEVEL UP, LOCKED...). Its other 499 blocks stay US, the label
///                     font among them, which the Japanese labels share
///                     (<see cref="JapaneseFontAtlas"/>), and the checksum is rewritten
///
/// Every file keeps the US disc's table: the Japanese archive must have the same
/// table, or the file is left US. Everything else stays US: the program, the area
/// modules, the area data (three entries differ, each by a creature statistic) and
/// the models.
/// </summary>
static class JapaneseMix
{
    const int SectorBytes = DiscArchive.SectorBytes;
    public const string TalkPath = "CD/COM/TALK.T";
    public const string ItemPath = "CD/COM/ITEM.T";
    public const string FdatPath = "CD/COM/FDAT.T";

    /// <summary>A TIM image starts with this little-endian word, 0x10.</summary>
    const uint TimMagic = 0x10;

    /// <summary>FDAT.T's entry of VRAM blocks: the fonts, the HUD's sprites and the messages in play.</summary>
    public const int PageEntry = 47;

    /// <summary>The messages in play: a 4-bit page at VRAM (704, 256), 64 words by 256 rows.</summary>
    const int MessagesX = 704, MessagesY = 256, MessagesWords = 64, MessagesRows = 256;

    const int BlockHeaderBytes = 16;     // x, y, w, h (u16, w in VRAM words), then the same again

    /// <summary>One file's plan: the US file on the disc, and its edited bytes when anything changes.</summary>
    public sealed record FileEdit(string Path, int Lba, byte[] NewBytes, int Entries);

    /// <summary>The US data sectors the layer replaces, by LBA.</summary>
    /// <exception cref="InvalidDataException">A file is missing or its archive is malformed.</exception>
    public static Dictionary<int, byte[]> BuildSectorOverrides(DiscFs us, DiscFs jp, List<string> notes)
    {
        var overrides = new Dictionary<int, byte[]>();
        foreach (var edit in PlanFiles(us, jp, notes))
        {
            var usBytes = us.ReadSectors(edit.Lba, edit.NewBytes.Length);
            for (int k = 0; k < edit.NewBytes.Length / SectorBytes; k++)
            {
                int at = k * SectorBytes;
                if (edit.NewBytes.AsSpan(at, SectorBytes).SequenceEqual(usBytes.AsSpan(at, SectorBytes))) continue;
                overrides[edit.Lba + k] = edit.NewBytes.AsSpan(at, SectorBytes).ToArray();
            }
        }
        return overrides;
    }

    static List<FileEdit> PlanFiles(DiscFs us, DiscFs jp, List<string> notes)
    {
        var edits = new List<FileEdit>();
        foreach (var path in new[] { TalkPath, ItemPath })
            if (PictureEdit(us, jp, path, notes) is { } edit) edits.Add(edit);
        if (MessagesEdit(us, jp, notes) is { } fdat) edits.Add(fdat);
        return edits;
    }

    /// <summary>Each entry whose Japanese copy is a TIM and differs, copied over the US entry.</summary>
    static FileEdit? PictureEdit(DiscFs us, DiscFs jp, string path, List<string> notes)
    {
        var (usFile, usArchive) = Open(us, path);
        var (jpFile, jpArchive) = Open(jp, path);
        if (!usArchive.SameTable(jpArchive))
        {
            notes.Add($"{path}: the Japanese table differs from the US one; left US");
            return null;
        }

        var bytes = Read(us, usFile);
        int replaced = 0, kept = 0;
        for (int i = 0; i < usArchive.Count; i++)
        {
            var jpEntry = jpArchive.Entry(i);
            if (jpEntry.IsEmpty || jpEntry.SequenceEqual(usArchive.Entry(i))) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(jpEntry) != TimMagic) { kept++; continue; }
            jpEntry.CopyTo(bytes.AsSpan(usArchive.ByteStart(i)));
            replaced++;
        }
        notes.Add($"{path}: {replaced} picture(s) replaced, {kept} other entr{(kept == 1 ? "y" : "ies")} that differ left US");
        return new FileEdit(path, usFile.Lba, bytes, replaced);
    }

    /// <summary>FDAT.T entry 47 with the Japanese messages-in-play block in place of the US one.</summary>
    static FileEdit? MessagesEdit(DiscFs us, DiscFs jp, List<string> notes)
    {
        var (usFile, usArchive) = Open(us, FdatPath);
        var (_, jpArchive) = Open(jp, FdatPath);
        if (PageEntry >= usArchive.Count || PageEntry >= jpArchive.Count)
        {
            notes.Add($"{FdatPath}: no entry {PageEntry}; the messages in play stay US");
            return null;
        }

        var usEntry = usArchive.Entry(PageEntry);
        var jpEntry = jpArchive.Entry(PageEntry);
        int usAt = MessagesBlock(usEntry), jpAt = MessagesBlock(jpEntry);
        if (usAt < 0 || jpAt < 0)
        {
            notes.Add($"{FdatPath} entry {PageEntry}: no block at ({MessagesX}, {MessagesY}) on both discs; the messages in play stay US");
            return null;
        }

        var bytes = Read(us, usFile);
        var entry = bytes.AsSpan(usArchive.ByteStart(PageEntry), usEntry.Length);
        int pixels = MessagesWords * MessagesRows * 2;
        jpEntry.Slice(jpAt + BlockHeaderBytes, pixels).CopyTo(entry[(usAt + BlockHeaderBytes)..]);
        DiscArchive.WriteChecksum(entry);
        notes.Add($"{FdatPath} entry {PageEntry}: the messages in play replaced");
        return new FileEdit(FdatPath, usFile.Lba, bytes, 1);
    }

    /// <summary>The messages block's header offset in an entry of VRAM blocks, or -1.</summary>
    static int MessagesBlock(ReadOnlySpan<byte> entry)
    {
        int at = 0;
        while (at + BlockHeaderBytes <= entry.Length)
        {
            int x = U16(entry, at), y = U16(entry, at + 2), w = U16(entry, at + 4), h = U16(entry, at + 6);
            if (w == 0 || h == 0 || w == 0xFFFF) return -1;
            if (!entry.Slice(at, 8).SequenceEqual(entry.Slice(at + 8, 8))) return -1;
            int bytes = w * h * 2;
            if (at + BlockHeaderBytes + bytes > entry.Length) return -1;
            if (x == MessagesX && y == MessagesY && w == MessagesWords && h == MessagesRows) return at;
            at += BlockHeaderBytes + bytes;
        }
        return -1;
    }

    static int U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);

    /// <summary>A file on a disc: where it is and how long.</summary>
    public readonly record struct DiscFile(string Path, int Lba, int Bytes);

    static (DiscFile File, DiscArchive Archive) Open(DiscFs disc, string path)
    {
        var file = Find(disc, path);
        return (file, DiscArchive.Read(path, Read(disc, file)));
    }

    public static DiscFile Find(DiscFs disc, string path)
    {
        if (!disc.Locate(path, out int lba, out uint size))
            throw new InvalidDataException($"{path} is not on the disc");
        return new DiscFile(path, lba, (int)size);
    }

    static byte[] Read(DiscFs disc, DiscFile file)
    {
        int sectors = (file.Bytes + SectorBytes - 1) / SectorBytes;
        return disc.ReadSectors(file.Lba, sectors * SectorBytes);
    }
}
