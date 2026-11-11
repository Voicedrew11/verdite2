using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Kf2.Japanese;

/// <summary>
/// The menu's words and where they go, from the two GAME.EXE files. Only addresses,
/// strides and counts are in the code; every word and position is read from the player's
/// own discs when the map is built, partly from tables and partly by reading what the
/// Japanese menu code would build (Verdite Core's ExeInterpreter).
///
/// A US string is bytes ending in 0xFF; a Japanese one is u16 codes ending in 0xFFFF, each
/// code a cell of the Japanese label font (<see cref="JapaneseFontAtlas"/>), 0x1000 adding a
/// dakuten and 0x2000 a handakuten. A record holds its s16 X and Y first, so its text is at
/// +4; an item, spell or word is at +0.
///
/// The US and Japanese programs have the same menu routines in the same order, so a call in
/// one has a counterpart in the other. Those pairs are the tables below, measured
/// 2026-11-11 against Japanese Rev 1 (docs/JAPANESE.md, "The menu's text"). Their Japanese
/// addresses are the Best program's; <see cref="JapaneseProgram.Original"/> carries each one
/// to where the 1995 program has it.
/// </summary>
static class JapaneseMenuText
{
    const byte UsEnd = 0xFF;
    const ushort JpEnd = 0xFFFF;
    const int GlyphMask = 0xFFF;

    /// <summary>SHA-256 of the US GAME.EXE the tables were measured in.</summary>
    internal const string UsExeSha256 = "3ca696397ace8b062bce71d390a79f5b5ca6e941ed23c0cc7adb8b261553b10a";

    /// <summary>One table: where its US and Japanese records start, their strides, how many, and the text's offset.</summary>
    internal readonly record struct Table(string Name, uint UsAddress, int UsStride, uint JpAddress, int JpStride, int Count, int TextAt);

    /// <summary>The option lists (func_800208D8): seven groups of eleven 0x1C-byte records, a header
    /// and ten rows, 0x134 apart. The card windows' headers (0x80064E24 on) are records of group 1.
    /// The Japanese address is the first group's; each program places the others the same distance on.</summary>
    const int GroupCount = 7, GroupStride = 0x134;
    static readonly Table[] Groups = Enumerable.Range(0, GroupCount)
        .Select(g => new Table($"option group {g}", 0x80064CD4u + (uint)(GroupStride * g), 0x1C, 0x80064C6Cu, 0x1C, 11, 4))
        .ToArray();

    /// <summary>The item names (ids 0-119) and then the spells (120-139), one table.</summary>
    static readonly Table Names = new("item and spell names", 0x80065B24, 24, 0x8006592C, 24, 140, 0);

    /// <summary>Equipment slots, TAKE OFF, the status page's words, the conditions and GOLD. The US
    /// field pads a condition with spaces and the Japanese one with code 0xFF, so the US spaces are part of the key.</summary>
    static readonly Table Words = new("words", 0x80065674, 24, 0x8006560C, 20, 24, 0);

    /// <summary>A label call of the US program and the Japanese calls that draw the same words: the
    /// first in its place, the others (where Japan splits a line) beside it.</summary>
    internal readonly record struct Site(uint UsCall, uint[] JpCalls);

    /// <summary>The label calls of one menu routine, with the Japanese routine holding their counterparts.</summary>
    internal readonly record struct SiteGroup(uint JpFunction, Site[] Sites);

    /// <summary>Every label whose record the code builds on its own stack. Not here: the status page's
    /// condition (0x8001FA2C), copied from the words by branch, which the words map serves.</summary>
    internal static readonly SiteGroup[] SiteGroups =
    [
        // the format question
        new(0x8001C3D8,
        [
            new(0x8001CCE8, [0x8001C758, 0x8001C764, 0x8001C770]),
            new(0x8001CCF8, [0x8001C77C, 0x8001C788]),
        ]),
        // status page 1
        new(0x8001EB20,
        [
            new(0x8001F2D0, [0x8001EB84]), new(0x8001F3A0, [0x8001EC10]), new(0x8001F40C, [0x8001EC74]),
            new(0x8001F4D4, [0x8001ED2C]), new(0x8001F5D4, [0x8001EDEC]), new(0x8001F640, [0x8001EE48]),
            new(0x8001F708, [0x8001EEC8]), new(0x8001FAB4, [0x8001F11C]),
        ]),
        // status page 2: offense and defense
        new(0x8001F1B4,
        [
            new(0x8001FBF8, [0x8001F224]), new(0x8001FC44, [0x8001F25C]), new(0x8001FCCC, [0x8001F2CC]),
            new(0x8001FD38, [0x8001F324]), new(0x8001FDD0, [0x8001F390]), new(0x8001FE58, [0x8001F3E0]),
            new(0x8001FEE8, [0x8001F430]), new(0x8001FF70, [0x8001F480]), new(0x80020000, [0x8001F4D0]),
            new(0x800200BC, [0x8001F544]), new(0x800200FC, [0x8001F578]), new(0x80020178, [0x8001F5D4]),
            new(0x800201E4, [0x8001F628]), new(0x8002025C, [0x8001F67C]), new(0x800202E4, [0x8001F6E0]),
            new(0x8002036C, [0x8001F730]), new(0x800203FC, [0x8001F780]), new(0x80020484, [0x8001F7D0]),
            new(0x80020514, [0x8001F820]),
        ]),
    ];

    /// <summary>A label call whose words depend on a0 at its routine's entry: each seed is run through
    /// both programs, and the pair keyed by the US words it picks (the shop's GOLD or CRYSTAL heading).</summary>
    internal readonly record struct SeededSite(uint UsFunction, uint UsCall, uint JpFunction, uint JpCall, uint[] Seeds);

    internal static readonly SeededSite[] SeededSites =
    [
        new(0x800216C0, 0x8002176C, 0x80020A00, 0x80020A9C, [0, 3]),
        new(0x800216C0, 0x80021854, 0x80020A00, 0x80020B50, [0, 3]),
    ];

    /// <summary>A call of one of the two-choice routines (func_800205C0 and func_80021478, records in
    /// a0 and a1): the US caller and call, and the Japanese caller and call.</summary>
    internal readonly record struct ChoiceCall(uint UsFunction, uint UsCall, uint JpFunction, uint JpCall);

    internal static readonly ChoiceCall[] ChoiceCalls =
    [
        new(0x8001BB7C, 0x8001BDD4, 0x8001B574, 0x8001B758),   // ON / OFF, the option page
        new(0x8001B9C8, 0x8001BB34, 0x8001B3EC, 0x8001B528),   // QUIT / RETURN
        new(0x8001CA70, 0x8001CC9C, 0x8001C3D8, 0x8001C730),   // YES / NO, the format question
        new(0x800206E0, 0x8002088C, 0x8001F9E0, 0x8001FC64),   // YES / NO, the shops
    ];

    /// <summary>The routines that fill a card message's records, each US one with its Japanese counterpart.</summary>
    internal static readonly (uint Us, uint Jp)[] MessageBuilders =
    [
        (0x8001CD60, 0x8001C804), (0x8001C3BC, 0x8001BD28), (0x8001C4B4, 0x8001BE3C),
        (0x8001D148, 0x8001CD88), (0x8001D1C8, 0x8001CDF8), (0x8001CDE0, 0x8001C8E0),
        (0x8001CED8, 0x8001CA24), (0x8001D0C8, 0x8001CCA8), (0x8001CFD0, 0x8001CB64),
    ];

    /// <summary>Each call of a message builder: the US caller and call, the Japanese caller and call.
    /// The window that shows it is the next window call in the Japanese caller.</summary>
    internal readonly record struct BuilderCall(uint UsCall, uint JpFunction, uint JpCall);

    internal static readonly BuilderCall[] BuilderCalls =
    [
        new(0x8001BEA8, 0x8001B7E8, 0x8001B82C), new(0x8001BF0C, 0x8001B7E8, 0x8001B890), new(0x8001BF84, 0x8001B7E8, 0x8001B90C),
        new(0x8001C2F4, 0x8001BAC8, 0x8001BC54), new(0x8001C348, 0x8001BAC8, 0x8001BCA8),
        new(0x8001C654, 0x8001BFA4, 0x8001BFD4),
        new(0x8001C8C0, 0x8001C210, 0x8001C240), new(0x8001C934, 0x8001C210, 0x8001C2B4), new(0x8001C988, 0x8001C210, 0x8001C308),
        new(0x8001C9DC, 0x8001C210, 0x8001C35C), new(0x8001CA04, 0x8001C210, 0x8001C36C),
    ];

    /// <summary>The two card windows in each program: records in a1 (count a2), and records in a0 (count a1).</summary>
    internal const uint UsWindowA = 0x8001B8AC, UsWindowB = 0x8001D43C, JpWindowA = 0x8001B2CC, JpWindowB = 0x8001D064;

    /// <summary>The box routine (x, y, w, h in a0-a3), and the one call of it whose box the caller
    /// sizes itself (the format question), with its Japanese counterpart.</summary>
    internal const uint UsBox = 0x800222B8;
    internal const uint UsFormatBoxCall = 0x8001CD18, JpFormatFunction = 0x8001C3D8, JpFormatBoxCall = 0x8001C7A8;

    const int RecordTextAt = 4;
    const int RegisterSp = 29, RegisterA0 = 4, RegisterA1 = 5, RegisterA2 = 6, RegisterA3 = 7;
    const int MaxRecordCodes = 12, MaxUsText = 24;
    internal const int RecordBytes = 0x1C;
    const int MaxMessageRecords = 6;

    /// <summary>A label's Japanese codes and position. A null position keeps the record's own.</summary>
    internal readonly record struct Label(short? X, short? Y, ushort[] Codes);

    /// <summary>A call's arguments as the Japanese call sets them; null keeps the US value.</summary>
    internal readonly record struct CallArgs(uint? A0, uint? A1, uint? A2, uint? A3, uint? Stack10, uint? Stack14, uint? Stack18);

    /// <summary>Everything the menu hooks look up.</summary>
    internal sealed class Map
    {
        public Dictionary<uint, Label> Records { get; } = new();

        /// <summary>A list page's title, by its US text: the list routine draws the descriptor's copy of a group record.</summary>
        public Dictionary<string, ushort[]> Titles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ushort[]> Names { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ushort[]> Words { get; } = new(StringComparer.Ordinal);
        public Dictionary<uint, Label[]> Sites { get; } = new();
        public Dictionary<uint, Dictionary<string, Label>> Seeded { get; } = new();
        public Dictionary<(uint Call, int Argument), Label> Choices { get; } = new();
        public Dictionary<uint, uint> BuilderCalls { get; } = new();
        public Dictionary<uint, CallArgs> BuilderWindows { get; } = new();
        public CallArgs? FormatBox { get; set; }
        public List<string> Skipped { get; } = [];
        public int LabelCount => Records.Count + Names.Count + Words.Count + Sites.Count + Seeded.Count + Choices.Count + BuilderCalls.Count;
    }

    /// <summary>The Japanese program the player's disc carries, or InvalidDataException when either
    /// file is not one the tables know.</summary>
    internal static JapaneseProgram RequireMeasuredExes(byte[] us, byte[] jp)
    {
        if (Hash(us) != UsExeSha256)
            throw new InvalidDataException("the US GAME.EXE is not the one the menu tables were measured in");
        string hash = Hash(jp);
        return JapaneseProgram.Known.FirstOrDefault(program => program.Sha256 == hash)
            ?? throw new InvalidDataException($"the Japanese GAME.EXE ({hash[..8]}) is none of the programs the menu tables were measured in");
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static Map Build(byte[] us, byte[] jp, JapaneseProgram program)
    {
        var map = new Map();
        var usExe = new ExeInterpreter(us);
        var jpExe = new ExeInterpreter(jp);
        var groups = Groups.Select((table, g) => table with { JpAddress = program.At(table.JpAddress) + (uint)(GroupStride * g) }).ToArray();
        foreach (var table in groups) AddRecords(table, us, jp, usExe.ImageBase, jpExe.ImageBase, map);
        foreach (var table in groups) AddTitles(table, us, jp, usExe.ImageBase, jpExe.ImageBase, map);
        AddNames(Names with { JpAddress = program.At(Names.JpAddress) }, us, jp, usExe.ImageBase, jpExe.ImageBase, map.Names);
        AddNames(Words with { JpAddress = program.At(Words.JpAddress) }, us, jp, usExe.ImageBase, jpExe.ImageBase, map.Words);

        foreach (var group in SiteGroups) AddSites(group, jpExe, program, map);
        foreach (var site in SeededSites) AddSeeded(site, usExe, jpExe, program, map);
        foreach (var choice in ChoiceCalls) AddChoice(choice, jpExe, program, map);
        AddMessages(jpExe, program, map);
        return map;
    }

    /// <summary>A group's records, by the address of each US record: two groups hold the same US word.
    /// The position is the record's own at draw time (a choice routine moves its records as it draws).</summary>
    static void AddRecords(Table table, byte[] us, byte[] jp, uint usBase, uint jpBase, Map map)
    {
        for (int i = 0; i < table.Count; i++)
        {
            int usAt = Offset(table.UsAddress, usBase) + i * table.UsStride;
            int jpAt = Offset(table.JpAddress, jpBase) + i * table.JpStride;
            if (UsText(us, usAt + table.TextAt, table.UsStride - table.TextAt) is null) continue;
            if (JpCodes(jp, jpAt + table.TextAt, table.JpStride - table.TextAt) is not { } codes) continue;
            map.Records[table.UsAddress + (uint)(i * table.UsStride)] = new Label(null, null, codes);
        }
    }

    /// <summary>Each group's words by US text, the header first, so the header's words win a clash.</summary>
    static void AddTitles(Table table, byte[] us, byte[] jp, uint usBase, uint jpBase, Map map)
    {
        for (int i = 0; i < table.Count; i++)
        {
            string? key = UsText(us, Offset(table.UsAddress, usBase) + i * table.UsStride + table.TextAt, table.UsStride - table.TextAt);
            if (key is null) continue;
            if (JpCodes(jp, Offset(table.JpAddress, jpBase) + i * table.JpStride + table.TextAt, table.JpStride - table.TextAt) is { } codes)
                map.Titles.TryAdd(key, codes);
        }
    }

    static void AddNames(Table table, byte[] us, byte[] jp, uint usBase, uint jpBase, Dictionary<string, ushort[]> names)
    {
        for (int i = 0; i < table.Count; i++)
        {
            string? key = UsText(us, Offset(table.UsAddress, usBase) + i * table.UsStride + table.TextAt, table.UsStride - table.TextAt);
            if (key is null) continue;
            if (JpCodes(jp, Offset(table.JpAddress, jpBase) + i * table.JpStride + table.TextAt, table.JpStride - table.TextAt) is { } codes)
                names.TryAdd(key, codes);
        }
    }

    static void AddSites(SiteGroup group, ExeInterpreter japanese, JapaneseProgram program, Map map)
    {
        foreach (var site in group.Sites)
        {
            var labels = site.JpCalls.Select(call => RecordAtCall(japanese, program.At(group.JpFunction), program.At(call))).ToList();
            if (labels.Any(label => label is null))
            {
                map.Skipped.Add($"label call 0x{site.UsCall:X8}: the Japanese record is not constant");
                continue;
            }
            map.Sites[site.UsCall] = labels.Select(label => label!.Value).ToArray();
        }
    }

    static void AddSeeded(SeededSite site, ExeInterpreter american, ExeInterpreter japanese, JapaneseProgram program, Map map)
    {
        var words = new Dictionary<string, Label>(StringComparer.Ordinal);
        uint jpFunction = program.At(site.JpFunction), jpCall = program.At(site.JpCall);
        foreach (uint seed in site.Seeds)
        {
            var us = american.Run(site.UsFunction, (pc, _) => pc == site.UsCall, straight: false, (RegisterA0, seed));
            var jp = japanese.Run(jpFunction, (pc, _) => pc == jpCall, straight: false, (RegisterA0, seed));
            if (!us.Reached || !jp.Reached) continue;
            if (us.Get(RegisterA1) is not { } usAt || jp.Get(RegisterA1) is not { } jpAt) continue;
            if (ReadUsText(us, usAt) is { } key && ReadJapaneseRecord(jp, jpAt) is { } label) words.TryAdd(key, label);
        }
        if (words.Count == 0) map.Skipped.Add($"label call 0x{site.UsCall:X8}: no seed gave both programs' words");
        else map.Seeded[site.UsCall] = words;
    }

    /// <summary>The record a call's a1 points at, read straight through the Japanese routine.</summary>
    static Label? RecordAtCall(ExeInterpreter program, uint function, uint call)
    {
        var trace = program.Run(function, (pc, _) => pc == call, straight: true);
        return trace.Reached && trace.Get(RegisterA1) is { } address ? ReadJapaneseRecord(trace, address) : null;
    }

    /// <summary>A Japanese record: s16 X and Y, then u16 codes to 0xFFFF.</summary>
    static Label? ReadJapaneseRecord(ExeInterpreter.Trace trace, uint address)
    {
        var codes = new List<ushort>();
        for (uint i = 0; i < MaxRecordCodes; i++)
        {
            if (trace.HalfAt(address + RecordTextAt + i * 2) is not { } code) return null;
            if (code == JpEnd) break;
            if ((code & GlyphMask) > JapaneseFontAtlas.MaxGlyph) return null;
            codes.Add(code);
        }
        if (codes.Count == 0) return null;
        return new Label((short?)trace.HalfAt(address), (short?)trace.HalfAt(address + 2), codes.ToArray());
    }

    static string? ReadUsText(ExeInterpreter.Trace trace, uint address)
    {
        var text = new StringBuilder();
        for (uint i = 0; i < MaxUsText; i++)
        {
            if (trace.ByteAt(address + RecordTextAt + i) is not { } b) return null;
            if (b == UsEnd) return text.ToString();
            text.Append((char)b);
        }
        return null;
    }

    /// <summary>The two records of a choice, read at the Japanese call. Their positions are the
    /// records' own at draw time, since func_800205C0 moves them down as it draws its rows.</summary>
    static void AddChoice(ChoiceCall choice, ExeInterpreter japanese, JapaneseProgram program, Map map)
    {
        uint jpCall = program.At(choice.JpCall);
        var trace = japanese.Run(program.At(choice.JpFunction), (pc, _) => pc == jpCall, straight: true);
        if (!trace.Reached)
        {
            map.Skipped.Add($"choice 0x{choice.UsCall:X8}: the Japanese call was not reached");
            return;
        }
        if (trace.Get(RegisterA0) is { } a0 && ReadJapaneseRecord(trace, a0) is { } first)
            map.Choices[(choice.UsCall, 0)] = first with { X = null, Y = null };
        if (trace.Get(RegisterA1) is { } a1 && ReadJapaneseRecord(trace, a1) is { } second)
            map.Choices[(choice.UsCall, 1)] = second with { X = null, Y = null };
    }

    static void AddMessages(ExeInterpreter japanese, JapaneseProgram program, Map map)
    {
        var builders = MessageBuilders.Select(pair => program.At(pair.Jp)).ToHashSet();
        uint windowA = program.At(JpWindowA), windowB = program.At(JpWindowB);
        foreach (var call in BuilderCalls)
        {
            uint target = 0;
            uint jpFunction = program.At(call.JpFunction), jpCall = program.At(call.JpCall);
            var atCall = japanese.Run(jpFunction, (pc, t) => { if (pc != jpCall) return false; target = t; return true; }, straight: true);
            if (!atCall.Reached || !builders.Contains(target))
            {
                map.Skipped.Add($"message builder call 0x{call.UsCall:X8}: 0x{jpCall:X8} does not call a builder");
                continue;
            }
            bool past = false;
            var atWindow = japanese.Run(jpFunction, (pc, t) =>
            {
                if (pc == jpCall) past = true;
                return past && (t == windowA || t == windowB);
            }, straight: true);
            if (!atWindow.Reached)
            {
                map.Skipped.Add($"message builder call 0x{call.UsCall:X8}: no window after 0x{jpCall:X8}");
                continue;
            }
            map.BuilderCalls[call.UsCall] = target;
            map.BuilderWindows[call.UsCall] = Arguments(atWindow);
        }

        uint boxCall = program.At(JpFormatBoxCall);
        var box = japanese.Run(program.At(JpFormatFunction), (pc, _) => pc == boxCall, straight: true);
        if (box.Reached) map.FormatBox = Arguments(box);
        else map.Skipped.Add("the format question's box was not reached");
    }

    /// <summary>A call's arguments from a run stopped at it; only small constants count, not addresses.</summary>
    static CallArgs Arguments(ExeInterpreter.Trace trace)
    {
        // The outgoing arguments are above the stack pointer at the call, the routine's own frame below the entry's.
        uint? sp = trace.Get(RegisterSp);
        uint? Stack(uint offset) => sp is { } at ? Small(WordAt(trace, at + offset)) : null;
        return new CallArgs(Small(trace.Get(RegisterA0)), Small(trace.Get(RegisterA1)), Small(trace.Get(RegisterA2)), Small(trace.Get(RegisterA3)),
            Stack(0x10), Stack(0x14), Stack(0x18));
    }

    static uint? WordAt(ExeInterpreter.Trace trace, uint address)
    {
        return trace.HalfAt(address) is { } low && trace.HalfAt(address + 2) is { } high ? (uint)(low | high << 16) : null;
    }

    static uint? Small(uint? value) => value is < 0x10000 ? value : null;

    /// <summary>Runs a Japanese message builder against <see cref="ExeInterpreter.DestinationAddress"/> with
    /// the US call's a1-a3: its records, at most six, as bytes, or null when it wrote none.</summary>
    internal static byte[]? BuildMessage(ExeInterpreter program, uint builder, uint a1, uint a2, uint a3)
    {
        var trace = program.Run(builder, null, straight: false,
            (RegisterA0, ExeInterpreter.DestinationAddress), (RegisterA1, a1), (RegisterA2, a2), (RegisterA3, a3));
        int length = 0;
        foreach (var address in trace.Written.Keys)
            if (address >= ExeInterpreter.DestinationAddress && address < ExeInterpreter.DestinationAddress + RecordBytes * MaxMessageRecords)
                length = Math.Max(length, (int)(address - ExeInterpreter.DestinationAddress) + 1);
        if (length == 0) return null;
        length = (length + RecordBytes - 1) / RecordBytes * RecordBytes;
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = trace.ByteAt(ExeInterpreter.DestinationAddress + (uint)i) ?? 0xFF;
        return bytes;
    }

    /// <summary>What the map holds, for the probe's log: each resolved place with its codes.</summary>
    internal static IEnumerable<string> Describe(Map map)
    {
        static string Show(Label label) => $"({label.X},{label.Y}) {string.Join(' ', label.Codes.Select(code => code.ToString("x")))}";

        yield return $"{map.Records.Count} list record(s), {map.Titles.Count} title(s), {map.Names.Count} name(s), {map.Words.Count} word(s), " +
                     $"{map.Sites.Count} site(s), {map.Seeded.Count} seeded site(s), {map.Choices.Count} choice record(s), {map.BuilderCalls.Count} message call(s)";
        foreach (var (call, labels) in map.Sites.OrderBy(pair => pair.Key))
            yield return $"site 0x{call:X8}: {string.Join(" + ", labels.Select(Show))}";
        foreach (var (call, words) in map.Seeded.OrderBy(pair => pair.Key))
            foreach (var (text, label) in words)
                yield return $"seeded 0x{call:X8} \"{LabelText(text)}\": {Show(label)}";
        foreach (var (key, label) in map.Choices.OrderBy(pair => pair.Key))
            yield return $"choice 0x{key.Call:X8}/{key.Argument}: {Show(label)}";
        foreach (var (call, window) in map.BuilderWindows.OrderBy(pair => pair.Key))
            yield return $"message call 0x{call:X8}: builder 0x{map.BuilderCalls[call]:X8}, window {window}";
        if (map.FormatBox is { } box) yield return $"format question box: {box}";
    }

    /// <summary>The US string at a field, or null when the field is empty, has no 0xFF in it, or is
    /// a lone zero byte. A lone zero is the letter A to the font, but in these tables it is an
    /// unused record, so it is not a name.</summary>
    static string? UsText(byte[] file, int at, int field)
    {
        var bytes = Field(file, at, field);
        int end = bytes.IndexOf(UsEnd);
        if (end <= 0 || (end == 1 && bytes[0] == 0)) return null;
        return Encoding.Latin1.GetString(bytes[..end]);
    }

    /// <summary>The Japanese codes at a field, or null when it is empty, has no 0xFFFF, or names a
    /// cell past the font page.</summary>
    static ushort[]? JpCodes(byte[] file, int at, int field)
    {
        var bytes = Field(file, at, field);
        var codes = new List<ushort>();
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            ushort code = (ushort)(bytes[i] | bytes[i + 1] << 8);
            if (code == JpEnd) return codes.Count == 0 ? null : codes.ToArray();
            if ((code & GlyphMask) > JapaneseFontAtlas.MaxGlyph) return null;
            codes.Add(code);
        }
        return null;
    }

    static ReadOnlySpan<byte> Field(byte[] file, int at, int length)
    {
        if (at < 0 || length <= 0 || at + length > file.Length)
            throw new InvalidDataException($"a menu text field at file offset 0x{at:X} is outside GAME.EXE");
        return file.AsSpan(at, length);
    }

    static int Offset(uint address, uint imageBase)
    {
        if (address < imageBase)
            throw new InvalidDataException($"menu text at 0x{address:X} is below GAME.EXE");
        return checked((int)(address - imageBase));
    }

    /// <summary>The label font's letters for a US string, for the probe's log.</summary>
    internal static string LabelText(string key)
    {
        var text = new StringBuilder(key.Length);
        foreach (char code in key)
            text.Append(code switch
            {
                >= '\x00' and <= '\x19' => ((char)('A' + code)).ToString(),
                >= '\x20' and <= '\x29' => ((char)('0' + code - 0x20)).ToString(),
                '\x30' => ".", '\x31' => ",", '\x32' => "'", '\x33' => "-", '\x34' => "=", '\x35' => "/",
                '\x36' => "*", '\x37' => "#", '\x38' => "!", '\x39' => "•", '\x3A' => "?", '\x7F' => " ",
                _ => $"[{(int)code:X2}]",
            });
        return text.ToString();
    }
}
