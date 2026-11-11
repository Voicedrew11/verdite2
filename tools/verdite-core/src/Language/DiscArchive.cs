using System.Buffers.Binary;

namespace Verdite.Core;

/// <summary>
/// An archive of the King's Field games' u16-table format (FDAT, ITEM, TALK and the
/// rest), read from a whole file's bytes. u16 count, then count+1 u16 start sectors,
/// counted from the file's first sector, which holds the table. Entry i is the
/// sectors from start[i] up to start[i+1]; an entry of length zero is empty. Each
/// non-empty entry ends with a checksum word: the seed 0x12345678 plus every word
/// before it, wrapping, is the last word.
/// </summary>
public sealed class DiscArchive
{
    public const int SectorBytes = 2048;
    const uint ChecksumSeed = 0x12345678;

    readonly byte[] _file;
    readonly int[] _starts;

    DiscArchive(byte[] file, int[] starts)
    {
        _file = file;
        _starts = starts;
    }

    public int Count => _starts.Length - 1;

    /// <summary>The entry's start sector, counted from the file's first sector.</summary>
    public int StartSector(int i) => _starts[i];

    public int Sectors(int i) => _starts[i + 1] - _starts[i];

    public int ByteStart(int i) => _starts[i] * SectorBytes;

    public ReadOnlySpan<byte> Entry(int i) => _file.AsSpan(ByteStart(i), Sectors(i) * SectorBytes);

    /// <summary>True when the two archives' tables are the same: same count, same start sectors.</summary>
    public bool SameTable(DiscArchive other) => _starts.AsSpan().SequenceEqual(other._starts);

    /// <exception cref="InvalidDataException">The table, an entry's order, or an entry's checksum is wrong.</exception>
    public static DiscArchive Read(string path, byte[] file)
    {
        if (file.Length < SectorBytes)
            throw new InvalidDataException($"{path} is {file.Length} bytes, shorter than its table");

        int count = BinaryPrimitives.ReadUInt16LittleEndian(file);
        var starts = new int[count + 1];
        if (2 + 2 * starts.Length > SectorBytes)
            throw new InvalidDataException($"{path}: the table of {count} entries is longer than its first sector");
        for (int i = 0; i < starts.Length; i++)
            starts[i] = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2 + 2 * i));
        for (int i = 0; i < count; i++)
            if (starts[i + 1] < starts[i])
                throw new InvalidDataException($"{path}: entry {i} ends before it starts");
        if (starts[count] * SectorBytes > file.Length)
            throw new InvalidDataException($"{path}: the table ends at sector {starts[count]}, past the file");

        var archive = new DiscArchive(file, starts);
        for (int i = 0; i < count; i++)
            if (archive.Entry(i).Length > 0 && !ChecksumOk(archive.Entry(i)))
                throw new InvalidDataException($"{path} entry {i}: checksum fails");
        return archive;
    }

    /// <summary>True when an entry's last word is the seed plus every word before it.</summary>
    public static bool ChecksumOk(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 4 || entry.Length % 4 != 0) return false;
        return ChecksumOf(entry[..^4]) == BinaryPrimitives.ReadUInt32LittleEndian(entry[^4..]);
    }

    /// <summary>Writes an edited entry's last word so the game's check passes again.</summary>
    public static void WriteChecksum(Span<byte> entry)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(entry[^4..], ChecksumOf(entry[..^4]));
    }

    static uint ChecksumOf(ReadOnlySpan<byte> words)
    {
        uint sum = ChecksumSeed;
        for (int at = 0; at + 4 <= words.Length; at += 4)
            sum += BinaryPrimitives.ReadUInt32LittleEndian(words[at..]);
        return sum;
    }
}
