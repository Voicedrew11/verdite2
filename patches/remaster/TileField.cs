namespace Kf2.Remaster;

/// <summary>
/// One editable part of a tile half: the byte it lives in and the bits of that byte it
/// owns. A level edit names fields, never bytes, so an author cannot write the bits the
/// game uses at run time: <c>+2</c>'s <c>0x04</c> is a moving thing's footprint and its
/// low two bits are never tested, so <see cref="Collision"/> owns only <c>0xF8</c>.
/// A number is the masked bits shifted down (the light record, 0..63); a set of flags
/// is the bits where the byte holds them (collision, <c>0x40</c>), since what each bit
/// means is not known.
/// See "Level editing: what can be edited and what can only be decorated" and "Phase 6,
/// the first slice" in docs/REMASTER.md.
/// </summary>
public sealed class TileField
{
    public string Name { get; }
    public int Offset { get; }
    public byte Mask { get; }
    public string Description { get; }

    /// <summary>Whether the document writes it as true/false rather than a number.</summary>
    public bool IsFlag => Max == 1;

    /// <summary>Whether a value is the byte's own bits rather than a number.</summary>
    public bool IsBits { get; }

    int Shift => IsBits ? 0 : System.Numerics.BitOperations.TrailingZeroCount(Mask);
    public int Max => Mask >> Shift;

    TileField(string name, int offset, byte mask, string description, bool bits = false)
    {
        Name = name;
        Offset = offset;
        Mask = mask;
        Description = description;
        IsBits = bits;
    }

    public static readonly TileField Mesh = new("mesh", 0, 0xFF, "the model index the half draws; 255 draws nothing");
    public static readonly TileField Height = new("height", 1, 0xFF, "the floor is at Y = -(height << 7)");
    public static readonly TileField Collision = new("collision", 2, 0xF8, "the collision flags func_8002C700 tests, less the footprint bit", bits: true);
    public static readonly TileField Shape = new("shape", 3, 0xFF, "the collision-shape index into 0x801D8484");
    public static readonly TileField Light = new("light", 4, 0x3F, "the light record the half is lit and fogged by");
    public static readonly TileField StopsFlood = new("stopsFlood", 4, 0x80, "stops the visibility flood at this cell");

    /// <summary>Every field, in the order a half's bytes hold them.</summary>
    public static readonly IReadOnlyList<TileField> All = [Mesh, Height, Collision, Shape, Light, StopsFlood];

    public static TileField? Find(string name)
        => All.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool Valid(int value) => value >= 0 && value <= Max && ((value << Shift) & ~Mask) == 0;

    /// <summary>What <see cref="Valid"/> accepts, for a message.</summary>
    public string Takes => IsFlag ? "true or false" : IsBits ? $"only the bits 0x{Mask:X2}" : $"0..{Max}";

    /// <summary>The field's value in a byte of the half.</summary>
    public int Read(byte b) => (b & Mask) >> Shift;

    /// <summary>The field's bits of <paramref name="value"/>, in place in the byte.</summary>
    public byte Bits(int value) => (byte)((value << Shift) & Mask);

    public override string ToString() => Name;
}
