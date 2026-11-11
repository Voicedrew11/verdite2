namespace Kf2.Japanese;

/// <summary>
/// The port's own words in Japanese: PORT SETTINGS (its rows, values, pages and its
/// question) and the gear comparison, which the Japanese disc has no text for. Written
/// in what the Japanese label font has (<see cref="JapaneseGlyphs"/>): katakana, most
/// hiragana (no small ゃゅょ), and only the kanji the game's own menus used, so 設定,
/// 画面 and the like are spelled in kana. The game's own words are used where it has
/// them: On and Off from the option page, 切る/打つ/刺す, 聖の魔法, 体力, 魔力, 攻撃力 and
/// 防御力 from the status pages.
///
/// A label is 8 Japanese cells at most (the 124-wide box), a page's name 6 (it shares
/// the header with "n/6"), a value 9. <see cref="Check"/> measures all of them in the
/// dry run.
/// </summary>
static class JapanesePortText
{
    /// <summary>The settings page's words, by the English the page draws.</summary>
    static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        // the menu item and the question on leaving
        ["PORT SETTINGS"] = "PCせってい",
        ["SAVE CHANGES"] = "へんこうをほぞん",
        ["DISCARD CHANGES"] = "へんこうを捨てる",
        ["RUN SETUP"] = "セットアップ",

        // pages
        ["DISPLAY"] = "ディスプレイ",
        ["GRAPHICS"] = "グラフィック",
        ["WORLD"] = "ワールド",
        ["GAMEPLAY"] = "ゲームプレイ",
        ["CONTROLS"] = "そうさ",
        ["SOUND"] = "サウンド",

        // DISPLAY
        ["WINDOWED"] = "ウィンドウ",
        ["FULLSCREEN"] = "フルスクリーン",
        ["BORDERLESS"] = "ボーダーレス",
        ["RESOLUTION"] = "かいぞうど",
        ["ASPECT"] = "アスペクト",
        ["HUD AT EDGES"] = "HUDをはしに",
        ["FRAME RATE"] = "フレームレート",
        ["UNCAPPED"] = "むげん",
        ["NO SMOOTH"] = "ほかんなし",

        // GRAPHICS
        ["TEXTURE FILTER"] = "フィルタリング",
        ["PER-PIXEL LIGHT"] = "ピクセルごとの光",
        ["TRILINEAR"] = "トライリニア",
        ["NO MIPMAPS"] = "ミップなし",
        ["AMB. OCCLUSION"] = "AOシェーディング",
        ["LOW"] = "ロー",
        ["MEDIUM"] = "ミドル",
        ["HIGH"] = "ハイ",
        ["SHADING"] = "シェーディング",
        ["DITHER"] = "ディザ",
        ["NONE"] = "なし",
        ["SMOOTH"] = "スムーズ",

        // WORLD
        ["RENDER DISTANCE"] = "表示はんい",
        ["FOG"] = "霧",
        ["CORNERS"] = "ポリゴン",
        ["PIXEL DEPTH"] = "ピクセルの深さ",
        ["DISTANCE"] = "カメラから",
        ["WATER"] = "水",
        ["ENHANCED"] = "かいぜん",
        ["FULL"] = "フル",
        ["CUSTOM"] = "カスタム",
        ["ORIGINAL"] = "オリジナル",

        // GAMEPLAY
        ["RELOAD ON DEATH"] = "しんだらロード",
        ["COMPARE GEAR"] = "装備のひかく",
        ["CURSOR MEMORY"] = "カーソルきおく",
        ["NPC DIALOGUE"] = "NPCかいわ",
        ["ENEMY TURN"] = "てきのふりむき",
        ["LANGUAGE"] = "げんご",
        ["ENGLISH"] = "えいご",
        ["RETRANSLATION"] = "しんやく",
        ["JAPANESE, RESTART"] = "にほんご",
        ["FONT"] = "フォント",
        ["CLASSIC"] = "クラシック",
        ["MODERN"] = "モダン",

        // CONTROLS
        ["2D ACCEL"] = "2D かそく",
        ["INVERTED CAMERA"] = "カメラはんてん",
        ["STICK SENS"] = "スティックかんど",
        ["MOUSE SENS"] = "マウスかんど",
        ["GYRO"] = "ジャイロ",
        ["RUMBLE"] = "しんどう",
        ["SHORTCUTS"] = "ショートカット",

        // SOUND
        ["3D AUDIO"] = "3Dサウンド",
        ["SPEAKERS"] = "スピーカー",
        ["HEADPHONES"] = "ヘッドホン",

        // the option page's own, in the disc's Latin letters
        ["ON"] = "On",
        ["OFF"] = "Off",
    };

    /// <summary>The gear comparison's words: the status pages' own where they have them.
    /// The shop's two-column panel has 2 cells before its numbers.</summary>
    static readonly Dictionary<string, (string Panel, string Compact)> Gear = new(StringComparer.Ordinal)
    {
        ["OFFENSE"] = ("攻撃力", "攻撃"),
        ["DEFENSE"] = ("防御力", "防御"),
        ["STR POWER"] = ("体力", "体力"),
        ["MAG POWER"] = ("魔力", "魔力"),
        ["SLASH"] = ("切る", "切る"),
        ["CHOP"] = ("打つ", "打つ"),
        ["STAB"] = ("刺す", "刺す"),
        ["HOLY"] = ("聖の魔法", "聖"),
        ["FIRE"] = ("炎の魔法", "炎"),
        ["EARTH"] = ("土の魔法", "土"),
        ["WIND"] = ("風の魔法", "風"),
        ["WATER"] = ("水の魔法", "水"),
        ["POISON"] = ("毒", "毒"),
        ["DARK"] = ("魔の魔法", "魔"),
        ["TOTAL"] = ("ごうけい", "けい"),
        ["NOW"] = ("いま", "いま"),
        ["NEW"] = ("あと", "あと"),
        [NoChange] = ("へんかなし", "へんかなし"),
    };

    const string NoChange = "NO CHANGE";

    /// <summary>The page's English in Japanese, or null. A value built from parts ("60 NO
    /// SMOOTH", "4X NO MIPMAPS", "X1.5") is put together a word at a time; a word with a
    /// digit in it (a number, 2D, 4X, 1080P) or a mark (16/9) stays in Latin letters.</summary>
    public static string? For(string english)
    {
        if (Words.TryGetValue(english, out var whole)) return whole;
        var tokens = english.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var parts = new List<string>();
        for (int i = 0; i < tokens.Length;)
        {
            int taken = 0;
            for (int n = tokens.Length - i; n >= 1 && taken == 0; n--)
                if (Words.TryGetValue(string.Join(' ', tokens, i, n), out var phrase)) { parts.Add(phrase); taken = n; }
            if (taken == 0)
            {
                if (!StaysLatin(tokens[i])) return null;
                parts.Add(tokens[i]);
                taken = 1;
            }
            i += taken;
        }
        return string.Join(' ', parts);
    }

    static bool StaysLatin(string token) => token.Any(ch => ch is >= '0' and <= '9') || token == "HD";

    /// <summary>A gear comparison word in Japanese, for the panel or the shop's short form.</summary>
    public static string? GearWord(string english, bool compact) =>
        Gear.TryGetValue(english, out var word) ? compact ? word.Compact : word.Panel : null;

    // ---- the dry run's check ----

    const int CellPixels = 14, LatinPixels = 7;
    const int LabelPixels = 8 * CellPixels, PagePixels = 6 * CellPixels, ValuePixels = 9 * CellPixels;

    /// <summary>
    /// One line per word the port draws: its Japanese, or what is wrong with it (none, a
    /// character the font lacks, too wide). Labels, values over every step and half step,
    /// the pages, the question and the gear comparison.
    /// </summary>
    public static IEnumerable<string> Check(
        IEnumerable<string> labels, IEnumerable<string> values, IEnumerable<string> pages, IEnumerable<string> gear)
    {
        foreach (var line in CheckAll(labels, LabelPixels, For)) yield return line;
        foreach (var line in CheckAll(values, ValuePixels, For)) yield return line;
        foreach (var line in CheckAll(pages, PagePixels, For)) yield return line;
        foreach (var line in CheckAll(gear, LabelPixels, w => GearWord(w, compact: false))) yield return line;
        // The shop's short forms sit before their numbers; NO CHANGE has the line to itself.
        foreach (var line in CheckAll(gear.Where(w => w != NoChange), 2 * CellPixels, w => GearWord(w, compact: true))) yield return line;
    }

    static IEnumerable<string> CheckAll(IEnumerable<string> english, int room, Func<string, string?> translate)
    {
        foreach (var word in english.Distinct())
        {
            if (translate(word) is not { } japanese) { yield return $"port text MISSING \"{word}\""; continue; }
            if (JapaneseGlyphs.Encode(japanese, out char missing) is null)
            {
                yield return $"port text \"{word}\" -> {japanese}: NOT IN THE FONT '{missing}'";
                continue;
            }
            int width = japanese.Sum(ch => ch is >= ' ' and <= '~' ? LatinPixels : CellPixels);
            string fits = width > room ? $" TOO WIDE ({width} px of {room})" : "";
            yield return $"port text \"{word}\" -> {japanese}{fits}";
        }
    }
}
