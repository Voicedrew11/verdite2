using System.Text;

namespace Kf2.Japanese;

/// <summary>
/// The Japanese label font as text: which code draws which character. The font is FDAT.T
/// entry 47's page at (832, 256) on all three Japanese discs, a 15 x 15 cell per glyph,
/// sixteen to a row; read off the page by eye (2026-11-11) and checked by decoding the
/// Japanese program's own labels with it (the status pages, the shop, the card messages
/// all read as Japanese). A voiced kana is its plain glyph with 0x1000 (dakuten) or 0x2000
/// (handakuten) set, as the Japanese label routine draws it.
///
/// The font has every katakana, the small katakana ァィゥェォッャュョ, every hiragana but
/// the small ones except っ, and about 190 kanji: what the game's own menus needed. The
/// port's words are written in what it has (<see cref="JapanesePortText"/>).
///
/// Latin letters, digits, marks and spaces are not the font's: a code with
/// <see cref="LatinBit"/> set holds the ASCII character, drawn from the US label font on
/// the same page, as the disc's own full-width Latin cells are (<see cref="JapaneseMenu"/>).
/// </summary>
static class JapaneseGlyphs
{
    public const ushort LatinBit = 0x8000;
    const ushort DakutenBit = 0x1000, HandakutenBit = 0x2000;
    const char Dakuten = '゙', Handakuten = '゚';

    /// <summary>Row r holds glyphs 16r..16r+15. '\0' is a cell this table does not name: an
    /// empty cell, the accents, or one of the disc's full-width Latin cells.</summary>
    static readonly string[] Rows =
    [
        "アイウエオカキクケコサシスセソタ",
        "チツテトナニヌネノハヒフヘホマミ",
        "ムメモヤユヨワンラリルレロー\0\0",
        "ァィゥェォッャュョ・▶っ\0\0\0\0",
        "あいうえおかきくけこさしすせそた",
        "ちつてとなにぬねのはひふへほまみ",
        "むめもやゆよわんらりるれろを士笛",
        "装備使売買捨武器魔法防具盾頭胴腕",
        "足剣経験値状態矢毒攻撃力体水晶薬",
        "草消回復竜王兜実月星太陽輪鎧指握",
        "紫電雷撃鍵海賊鐘地図炎幻杖瓶真理",
        "鏡涙牢坑夫教僧兵神殿石土風光火羽",
        "導師髑髏像正常重針国個数敏霊屋作",
        "御金銀深淵効果音楽表示方向所持戦",
        "\0\0\0\0\0\0\0\0\0\0\0\0\0\0歩行",
        "\0\0\0\0\0\0一日巨人首森霧除波\0",
        "複製鑑定聞交換切打刺聖容量\0\0源",
    ];

    static readonly Dictionary<char, ushort> _codes = Index();

    static Dictionary<char, ushort> Index()
    {
        var codes = new Dictionary<char, ushort>();
        for (int row = 0; row < Rows.Length; row++)
            for (int column = 0; column < Rows[row].Length; column++)
                if (Rows[row][column] != '\0') codes.TryAdd(Rows[row][column], (ushort)(row * 16 + column));
        return codes;
    }

    /// <summary>The codes for <paramref name="text"/>, or null with the first character the
    /// font cannot draw in <paramref name="missing"/>.</summary>
    public static ushort[]? Encode(string text, out char missing)
    {
        missing = '\0';
        var codes = new List<ushort>(text.Length);
        foreach (char ch in text.Normalize(NormalizationForm.FormD))
        {
            if (ch is Dakuten or Handakuten)
            {
                if (codes.Count == 0 || (codes[^1] & LatinBit) != 0) { missing = ch; return null; }
                codes[^1] |= ch == Dakuten ? DakutenBit : HandakutenBit;
            }
            else if (ch is >= ' ' and <= '~') codes.Add((ushort)(LatinBit | ch));
            else if (_codes.TryGetValue(ch, out ushort code)) codes.Add(code);
            else { missing = ch; return null; }
        }
        return codes.ToArray();
    }

    /// <summary>The text a string of codes draws, for logs.</summary>
    public static string Decode(IEnumerable<ushort> codes)
    {
        var text = new StringBuilder();
        foreach (ushort code in codes)
        {
            if ((code & LatinBit) != 0) { text.Append((char)(code & 0x7F)); continue; }
            int glyph = code & 0xFFF;
            char ch = glyph / 16 < Rows.Length && Rows[glyph / 16][glyph % 16] is var c && c != '\0' ? c : '□';
            text.Append(ch);
            if ((code & DakutenBit) != 0) text.Append(Dakuten);
            if ((code & HandakutenBit) != 0) text.Append(Handakuten);
        }
        return text.ToString().Normalize(NormalizationForm.FormC);
    }
}
