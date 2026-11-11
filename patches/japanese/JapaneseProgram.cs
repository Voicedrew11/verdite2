using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Kf2.Japanese;

/// <summary>
/// One Japanese GAME.EXE the menu tables know, and where it keeps what the tables name.
/// <see cref="JapaneseMenuText"/>'s Japanese addresses are the Best program's (PlayStation the
/// Best and its Rev 1 carry the same file); the 1995 release (SLPS-00069) is an earlier
/// build of the same program, with the same menu routines in the same order, moved.
///
/// The 1995 table was measured 2026-11-11 (docs/JAPANESE.md, "The 1995 program"): the two
/// programs' functions paired by order and by how many calls each makes, then each call
/// site carried by its place among its routine's calls, and each data table by an alignment
/// of the two images. It holds addresses only; every word is read from the player's disc.
/// </summary>
sealed class JapaneseProgram
{
    public string Name { get; }
    public string Sha256 { get; }

    /// <summary>Best address to this program's; null when the program is the Best one.</summary>
    readonly IReadOnlyDictionary<uint, uint>? _moved;

    JapaneseProgram(string name, string sha256, IReadOnlyDictionary<uint, uint>? moved)
    {
        Name = name;
        Sha256 = sha256;
        _moved = moved;
    }

    /// <summary>Where this program has what the Best program has at <paramref name="best"/>.
    /// Throws InvalidDataException for an address the table was not measured for.</summary>
    public uint At(uint best)
    {
        if (_moved is null) return best;
        return _moved.TryGetValue(best, out uint moved)
            ? moved
            : throw new InvalidDataException($"0x{best:X8} has no measured place in the {Name} program");
    }

    public static readonly JapaneseProgram Best = new(
        "PlayStation the Best", "9e300cd26a20a54b01bcef84702e528bdcd34537183c11bea18baf087cba9821", null);

    public static readonly JapaneseProgram Original = new(
        "1995", "27cc1603adae25db0200831e6442bb5be3b9a60f242bdb7f12f63b8bee18b263", new (uint Best, uint Original)[]
    {
        (0x8001B2CC, 0x8001B030), (0x8001B3EC, 0x8001B14C), (0x8001B528, 0x8001B294), (0x8001B574, 0x8001B2DC),
        (0x8001B758, 0x8001B4C8), (0x8001B7E8, 0x8001B554), (0x8001B82C, 0x8001B598), (0x8001B890, 0x8001B5FC),
        (0x8001B90C, 0x8001B678), (0x8001BAC8, 0x8001B834), (0x8001BC54, 0x8001B9B8), (0x8001BCA8, 0x8001BA0C),
        (0x8001BD28, 0x8001BA80), (0x8001BE3C, 0x8001BB94), (0x8001BFA4, 0x8001BCFC), (0x8001BFD4, 0x8001BD2C),
        (0x8001C210, 0x8001BF68), (0x8001C240, 0x8001BF98), (0x8001C2B4, 0x8001C00C), (0x8001C308, 0x8001C060),
        (0x8001C35C, 0x8001C0B4), (0x8001C36C, 0x8001C0C4), (0x8001C3D8, 0x8001C12C), (0x8001C730, 0x8001C45C),
        (0x8001C758, 0x8001C4A8), (0x8001C764, 0x8001C4B8), (0x8001C770, 0x8001C4C8), (0x8001C77C, 0x8001C4D8),
        (0x8001C788, 0x8001C4E8), (0x8001C7A8, 0x8001C508), (0x8001C804, 0x8001C550), (0x8001C8E0, 0x8001C62C),
        (0x8001CA24, 0x8001C770), (0x8001CB64, 0x8001C8B0), (0x8001CCA8, 0x8001C9F4), (0x8001CD88, 0x8001CAD4),
        (0x8001CDF8, 0x8001CB44), (0x8001D064, 0x8001CDB0), (0x8001EB20, 0x8001E94C), (0x8001EB84, 0x8001E9A4),
        (0x8001EC10, 0x8001EA34), (0x8001EC74, 0x8001EAA0), (0x8001ED2C, 0x8001EB68), (0x8001EDEC, 0x8001EC38),
        (0x8001EE48, 0x8001EC9C), (0x8001EEC8, 0x8001ED24), (0x8001F11C, 0x8001EF78), (0x8001F1B4, 0x8001F008),
        (0x8001F224, 0x8001F074), (0x8001F25C, 0x8001F0B0), (0x8001F2CC, 0x8001F124), (0x8001F324, 0x8001F184),
        (0x8001F390, 0x8001F1F8), (0x8001F3E0, 0x8001F250), (0x8001F430, 0x8001F2A8), (0x8001F480, 0x8001F300),
        (0x8001F4D0, 0x8001F358), (0x8001F544, 0x8001F3D4), (0x8001F578, 0x8001F408), (0x8001F5D4, 0x8001F46C),
        (0x8001F628, 0x8001F4C4), (0x8001F67C, 0x8001F520), (0x8001F6E0, 0x8001F58C), (0x8001F730, 0x8001F5E4),
        (0x8001F780, 0x8001F63C), (0x8001F7D0, 0x8001F694), (0x8001F820, 0x8001F6EC), (0x8001F8CC, 0x8001F798),
        (0x8001F9E0, 0x8001F8B8), (0x8001FC64, 0x8001FB40), (0x800207C8, 0x80020748), (0x80020A00, 0x80020990),
        (0x80020A9C, 0x80020A28), (0x80020B50, 0x80020AF0), (0x8002108C, 0x800210AC), (0x8002173C, 0x800217F0),
        (0x80064C6C, 0x80063F70), (0x8006560C, 0x80064910), (0x8006592C, 0x80064C30),
    }.ToDictionary(pair => pair.Best, pair => pair.Original));

    public static readonly IReadOnlyList<JapaneseProgram> Known = [Best, Original];
}
