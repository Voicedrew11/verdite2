using RecompOne.Runtime.Memory;

namespace Kf2.Shortcuts;

/// <summary>
/// A spell drawn as a crystal and a level: the game has no model for a spell, so its box
/// shows the crystal of its element (the items FIRE, WATER, EARTH, WIND, LIGHT and DARK
/// CRYSTAL, 90-95) with a Roman numeral. The element is the port's reading of the name
/// (the game keeps none: a spell's 26-byte record at 0x8019C5EC holds a damage per kind
/// and the MP cost at +0x16); the level is the spell's rank by MP cost among its
/// element's, cheapest I. So WIND CUTTER (3 MP) is WIND I and MISSILE SHIELD (5) WIND II.
/// </summary>
static class SpellCrystals
{
    const uint Records = 0x8019C5EC, RecordBytes = 26, Cost = 0x16;
    const byte Fire = 90, Water = 91, Earth = 92, Wind = 93, Light = 94, Dark = 95;

    /// <summary>The crystal of each spell, by id.</summary>
    static readonly byte[] Element =
    [
        Water,  // 0  WATER FALL
        Earth,  // 1  STONE
        Earth,  // 2  EARTH WAVE
        Earth,  // 3  METEOR
        Wind,   // 4  WIND CUTTER
        Water,  // 5  ICE STORM
        Water,  // 6  FREEZE
        Fire,   // 7  FIRE BALL
        Fire,   // 8  FIRE WALL
        Fire,   // 9  FIRE STORM
        Fire,   // 10 FLAME
        Light,  // 11 LIGHTNING VOLT
        Light,  // 12 FLASH
        Dark,   // 13 SEATH
        Water,  // 14 DISPOISON
        Fire,   // 15 RESIST FIRE
        Earth,  // 16 EARTH HEAL
        Wind,   // 17 MISSILE SHIELD
        Light,  // 18 LIGHT
        Wind,   // 19 BREATH
    ];

    /// <summary>The crystal item id for <paramref name="spell"/>, 0xFF for none.</summary>
    internal static byte Crystal(byte spell) => spell < Element.Length ? Element[spell] : Shortcuts.None;

    /// <summary>1 for the cheapest spell of its element, and so on; ties go by id.</summary>
    internal static int Level(IMemory m, byte spell)
    {
        if (spell >= Element.Length) return 1;
        int mine = MpCost(m, spell), level = 1;
        for (int s = 0; s < Element.Length; s++)
        {
            if (s == spell || Element[s] != Element[spell]) continue;
            int other = MpCost(m, s);
            if (other < mine || (other == mine && s < spell)) level++;
        }
        return level;
    }

    static int MpCost(IMemory m, int spell) => m.ReadU16(Records + (uint)spell * RecordBytes + Cost);

    internal static string Roman(int n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", _ => "VII",
    };
}
