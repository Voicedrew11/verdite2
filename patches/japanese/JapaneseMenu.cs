using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Japanese;

/// <summary>
/// The menu's labels in Japanese when the Japanese layer is on: the words and the places
/// the Japanese program puts them, read from the player's own Japanese disc. A label with a
/// Japanese version is drawn here, with the primitives the Japanese label routine builds;
/// every other label, and every English run, goes on to the port's lettering or the game.
///
///     KF2_JP_PROBE=1    log each label with no Japanese version the first time it is drawn,
///                       what the build left out, and a count of Japanese draws every 5 s
///
/// The label routine is func_80021E10(template, record): an s16 X, an s16 Y and the string
/// at +4, a cell every 7 px. The Japanese one (0x8002108C in the Japanese GAME.EXE) draws a
/// 15 x 15 cell per code every 14 px, glyph g at u = 15 * (g &amp; 15), v = 15 * (g &gt;&gt; 4),
/// and for a code with 0x1000 or 0x2000 set a dakuten (glyph 0x2E) or handakuten (0x2F) in
/// the same place. This copies it with the US primitives, the glyph's cell taken from the
/// atlas (<see cref="JapaneseFontAtlas"/>); <see cref="Text.Lettering"/>'s replace on the
/// label routine asks <see cref="TryDraw"/> first.
///
/// Which Japanese words a label gets depends on where it is drawn from, because the same US
/// word can mean different things:
///
///     a record of a card message     the Japanese builder's own, in the port's RAM
///     a record of an option group    that record's Japanese one, by address
///     one of a choice's two records  the Japanese pair, by call and argument
///     a call in the site table       the records the Japanese call builds, by call
///     anything else                  by its US text: names, words, then the groups' words
///
/// The card messages take the Japanese window's line count and size too, because the
/// Japanese program splits its sentences over more lines.
///
/// The Japanese font's own Latin letters and digits (<see cref="Latin"/>: HP, MP, On,
/// Off, the file numbers) are full-width cells; they are drawn instead from the US label
/// font, which is on the same page, in the US routine's 8 x 15 cells, 7 px apart or as
/// the port's lettering spaces them, in the letters the FONT row chose: the port's
/// (lower case and all) on MODERN, the disc's capitals on CLASSIC. See docs/JAPANESE.md.
/// </summary>
public static class JapaneseMenu
{
    const uint LabelRoutine = 0x80021E10;
    const uint ChoiceRows = 0x800205C0, ChoicePair = 0x80021478;

    /// <summary>func_800229D8 takes the next POLY_FT4 from the cursor at <see cref="PrimCursor"/>;
    /// func_80022A28(slot) links it into the menu's ordering table.</summary>
    const uint PrimCursor = 0x8006E914;
    const uint LabelSlot = 10;

    const int RecordText = 4;
    const int MaxTextBytes = 24;
    const int MaxMessageCodes = 12;
    const ushort JpEnd = 0xFFFF;
    const byte TextEnd = 0xFF;

    const int Cell = 15, PenStep = 14;
    const int DakutenBit = 0x1000, HandakutenBit = 0x2000, GlyphMask = 0xFFF;
    const int DakutenGlyph = 0x2E, HandakutenGlyph = 0x2F;
    const int BlankGlyph = 0xFF;

    /// <summary>The Japanese label font's Latin cells (FDAT.T entry 47, page (832, 256)):
    /// row 0xE0 N E W H M P 1-8, row 0xF0 "On" "Off", and C D at 0x10D. Read off the page
    /// of all three Japanese discs, which is the same on each.</summary>
    static readonly Dictionary<int, char> Latin = new()
    {
        [0xE0] = 'N', [0xE1] = 'E', [0xE2] = 'W', [0xE3] = 'H', [0xE4] = 'M', [0xE5] = 'P',
        [0xE6] = '1', [0xE7] = '2', [0xE8] = '3', [0xE9] = '4', [0xEA] = '5', [0xEB] = '6', [0xEC] = '7', [0xED] = '8',
        [0xF0] = 'O', [0xF1] = 'n', [0xF2] = 'O', [0xF3] = 'f', [0xF4] = 'f',
        [0x10D] = 'C', [0x10E] = 'D',
    };

    /// <summary>The US label routine's step and cell (func_80021E10): u = (g &amp; 15) * 8, v = (g &gt;&gt; 4) * 15.</summary>
    const int UsAdvance = 7, UsCellU = 8, UsCellV = 15;

    /// <summary>Between a run of Latin letters and the Japanese cell after it.</summary>
    const int LatinGap = 1;

    /// <summary>The card messages' records, in a slot each of the port's region (PrimBuffer.TextScratch).</summary>
    const int MessageSlotBytes = JapaneseMenuText.RecordBytes * 6;
    const int MessageSlots = (int)PrimBuffer.TextBytes / MessageSlotBytes;

    const int ReportEveryMs = 5000;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.japanesemenu",
        Name = "Japanese menu text",
        Version = "1.0",
        Description = "Draws the menu's labels in Japanese when the Japanese layer is on (KF2_LANGUAGE=ja).",
    };

    /// <summary>A card message: the US builder's buffer (and its first record, to know it is
    /// still the same message), and where its Japanese records are, with the window's arguments.</summary>
    sealed record Message(uint UsBuffer, byte[] UsFirstRecord, uint Records, int Length, JapaneseMenuText.CallArgs Window);

    static bool _queued, _built, _ownsLabel;
    static JapaneseMenuText.Map? _map;
    static ExeInterpreter? _program;
    static JapaneseFontAtlas? _atlas;
    static readonly List<Message> _messages = [];
    static int _nextSlot;
    static (uint Record0, uint Record1, uint Call)? _choice;
    static (uint Call, uint A0, uint A1, uint A2, uint A3)? _builder;
    static long _vblank;
    static int _japaneseDraws, _englishDraws, _reportedDraws;
    static double _nextReportMs;
    static readonly HashSet<string> _untranslated = new(StringComparer.Ordinal);
    static readonly Stopwatch _clock = Stopwatch.StartNew();

    public static void Install()
    {
        JapaneseDisc.Mixed += () =>
        {
            if (!JapaneseDisc.DryRun) return;
            JapaneseDisc.EndDryRun(Ready());
        };
        HookAttach.OnOverlayLoad("japanese menu", Attach);
    }

    static bool Attach()
    {
        // The disc is opened (and the layer laid, or not) before the first overlay loads.
        if (!JapaneseDisc.Active) return true;

        SymbolRegistry.Build();
        var pre = new List<(uint Address, string Impl)>
        {
            (ChoiceRows, nameof(BeforeChoice)), (ChoicePair, nameof(BeforeChoice)),
            (JapaneseMenuText.UsWindowA, nameof(BeforeWindowA)), (JapaneseMenuText.UsWindowB, nameof(BeforeWindowB)),
            (JapaneseMenuText.UsBox, nameof(BeforeBox)),
        };
        var post = new List<(uint Address, string Impl)>
        {
            (ChoiceRows, nameof(AfterChoice)), (ChoicePair, nameof(AfterChoice)),
        };
        foreach (var (us, _) in JapaneseMenuText.MessageBuilders)
        {
            pre.Add((us, nameof(BeforeBuilder)));
            post.Add((us, nameof(AfterBuilder)));
        }

        // Lettering owns the one replace on the label routine and asks TryDraw first; with it
        // off (KF2_TEXT=0) this takes the replace itself.
        _ownsLabel = !Text.Lettering.Enabled;
        var targets = new List<MethodInfo>();
        foreach (var address in pre.Concat(post).Select(h => h.Address).Append(LabelRoutine).Distinct())
        {
            if (SymbolRegistry.Resolve("game", null, address) is not { } target)
            {
                Console.Error.WriteLine($"[KF2] japanese menu: 0x{address:X8} did not resolve; labels stay English");
                return false;
            }
            if (address != LabelRoutine || _ownsLabel) targets.Add(target);
        }

        if (!_queued)
        {
            _queued = true;
            MethodInfo M(string name) => typeof(JapaneseMenu).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            foreach (var (address, impl) in pre) HookManager.AddPre(_self, SymbolRegistry.Resolve("game", null, address)!, M(impl));
            foreach (var (address, impl) in post) HookManager.AddPost(_self, SymbolRegistry.Resolve("game", null, address)!, M(impl));
            if (_ownsLabel) HookManager.AddReplace(_self, SymbolRegistry.Resolve("game", null, LabelRoutine)!, M(nameof(Label)));
        }
        HookManager.Commit();
        bool ok = targets.All(HookAttach.Installed);
        if (!ok) Console.Error.WriteLine("[KF2] japanese menu: a hook did not commit; retrying on the next overlay load");
        return ok;
    }

    /// <summary>func_80021E10 when this takes the replace itself (the port's lettering off).</summary>
    public static void Label(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (!TryDraw(c, m)) orig(c, m);
    }

    /// <summary>
    /// Draws the label at (a0 template, a1 record) in Japanese when it has a version, and
    /// says so; false leaves it to the caller (the port's lettering, or the game's routine).
    /// </summary>
    public static bool TryDraw(CpuContext c, IMemory m)
    {
        if (!JapaneseDisc.Active || !Ready()) return false;

        JapaneseMenuText.Label[]? labels;
        string text = "";
        if (MessageRecord(m, c.A1) is { } message) labels = [message];
        else
        {
            text = ReadText(m, c.A1 + RecordText);
            labels = text.Length == 0 ? null : Lookup(c, text);
        }

        if (labels is null)
        {
            NoteUntranslated(c, text);
            return false;
        }

        uint template = c.A0, record = c.A1;
        short recordX = (short)m.ReadU16(record), recordY = (short)m.ReadU16(record + 2);
        try
        {
            foreach (var label in labels) Draw(c, m, template, label.X ?? recordX, label.Y ?? recordY, label.Codes);
        }
        finally
        {
            c.A0 = template;
            c.A1 = record;
        }
        _japaneseDraws++;
        ReportEvery();
        return true;
    }

    // ---- the choice routines: their two records, and who called them ----

    public static void BeforeChoice(CpuContext c, IMemory m) => _choice = (c.A0, c.A1, c.RA - 8);

    public static void AfterChoice(CpuContext c, IMemory m) => _choice = null;

    // ---- the card messages ----

    public static void BeforeBuilder(CpuContext c, IMemory m) => _builder = (c.RA - 8, c.A0, c.A1, c.A2, c.A3);

    /// <summary>After a US builder filled its buffer: the Japanese builder's records, in a slot of
    /// the port's region, registered against the US buffer for the window that shows it.</summary>
    public static void AfterBuilder(CpuContext c, IMemory m)
    {
        var call = _builder;
        _builder = null;
        if (call is not { } b || !JapaneseDisc.Active || !Ready() || !PrimBuffer.HasTextScratch) return;
        if (!_map!.BuilderCalls.TryGetValue(b.Call, out uint builder)) return;
        try
        {
            if (JapaneseMenuText.BuildMessage(_program!, builder, b.A1, b.A2, b.A3) is not { } bytes) return;
            int length = Math.Min(bytes.Length, MessageSlotBytes);
            uint slot = PrimBuffer.TextScratch + (uint)(_nextSlot * MessageSlotBytes);
            _nextSlot = (_nextSlot + 1) % MessageSlots;
            for (int i = 0; i < length; i++) m.WriteU8(slot + (uint)i, bytes[i]);

            var first = new byte[JapaneseMenuText.RecordBytes];
            for (int i = 0; i < first.Length; i++) first[i] = m.ReadU8(b.A0 + (uint)i);
            _messages.RemoveAll(message => message.UsBuffer == b.A0 || message.Records == slot);
            _messages.Add(new Message(b.A0, first, slot, length, _map.BuilderWindows[b.Call]));
            if (JapaneseDisc.Probe) Console.WriteLine($"[KF2] japanese menu: card message from call 0x{b.Call:X8}, {length / JapaneseMenuText.RecordBytes} record(s) at 0x{slot:X8}");
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException)
        {
            Console.WriteLine($"[KF2] japanese menu: card message from call 0x{b.Call:X8} stays English: {e.Message}");
        }
    }

    /// <summary>func_8001B8AC: the card window with its records in a1 and their count in a2.</summary>
    public static void BeforeWindowA(CpuContext c, IMemory m)
    {
        if (!JapaneseDisc.Active || MessageAt(m, c.A1) is not { } message) return;
        c.A1 = message.Records;
        Apply(c, m, message.Window);
    }

    /// <summary>func_8001D43C: the card window with its records in a0 and their count in a1.</summary>
    public static void BeforeWindowB(CpuContext c, IMemory m)
    {
        if (!JapaneseDisc.Active || MessageAt(m, c.A0) is not { } message) return;
        c.A0 = message.Records;
        Apply(c, m, message.Window);
    }

    /// <summary>func_800222B8, the box: the format question's is sized by its caller, so it takes the Japanese size.</summary>
    public static void BeforeBox(CpuContext c, IMemory m)
    {
        if (!JapaneseDisc.Active || c.RA - 8 != JapaneseMenuText.UsFormatBoxCall) return;
        if (!Ready() || _map!.FormatBox is not { } box) return;
        Apply(c, m, box);
    }

    static void Apply(CpuContext c, IMemory m, JapaneseMenuText.CallArgs args)
    {
        if (args.A0 is { } a0) c.A0 = a0;
        if (args.A1 is { } a1) c.A1 = a1;
        if (args.A2 is { } a2) c.A2 = a2;
        if (args.A3 is { } a3) c.A3 = a3;
        if (args.Stack10 is { } s10) m.WriteU32(c.SP + 0x10, s10);
        if (args.Stack14 is { } s14) m.WriteU32(c.SP + 0x14, s14);
        if (args.Stack18 is { } s18) m.WriteU32(c.SP + 0x18, s18);
    }

    /// <summary>The card message whose US buffer is at this address, if it still holds what the builder wrote.</summary>
    static Message? MessageAt(IMemory m, uint usBuffer)
    {
        foreach (var message in _messages)
        {
            if (message.UsBuffer != usBuffer) continue;
            for (int i = 0; i < message.UsFirstRecord.Length; i++)
                if (m.ReadU8(usBuffer + (uint)i) != message.UsFirstRecord[i]) return null;
            return message;
        }
        return null;
    }

    /// <summary>A record inside a card message's Japanese records, read as Japanese, or null.</summary>
    static JapaneseMenuText.Label? MessageRecord(IMemory m, uint record)
    {
        if (!PrimBuffer.HasTextScratch) return null;
        if (record < PrimBuffer.TextScratch || record >= PrimBuffer.TextScratch + PrimBuffer.TextBytes) return null;
        var codes = new List<ushort>();
        for (uint i = 0; i < MaxMessageCodes; i++)
        {
            ushort code = m.ReadU16(record + RecordText + i * 2);
            if (code == JpEnd) break;
            codes.Add(code);
        }
        return codes.Count == 0 ? null : new JapaneseMenuText.Label(null, null, codes.ToArray());
    }

    // ---- the lookup ----

    /// <summary>The Japanese labels for this draw, or null when it has none. The first holds the label's own place.</summary>
    static JapaneseMenuText.Label[]? Lookup(CpuContext c, string text)
    {
        var map = _map!;
        if (map.Records.TryGetValue(c.A1, out var record)) return [record];
        if (ChoiceLabel(c) is { } choice) return [choice];

        uint call = c.RA - 8;
        if (map.Sites.TryGetValue(call, out var sited)) return sited;
        if (map.Seeded.TryGetValue(call, out var seeded) && seeded.TryGetValue(text, out var picked)) return [picked];
        if (map.Names.TryGetValue(text, out var name)) return [Words(name)];
        if (map.Words.TryGetValue(text, out var word)) return [Words(word)];
        return map.Titles.TryGetValue(text, out var title) ? [Words(title)] : null;
    }

    static JapaneseMenuText.Label Words(ushort[] codes) => new(null, null, codes);

    static JapaneseMenuText.Label? ChoiceLabel(CpuContext c)
    {
        if (_choice is not { } choice) return null;
        int argument = c.A1 == choice.Record0 ? 0 : c.A1 == choice.Record1 ? 1 : -1;
        if (argument < 0) return null;
        return _map!.Choices.TryGetValue((choice.Call, argument), out var label) ? label : null;
    }

    /// <summary>Builds the map and the atlas the first time a label is drawn in Japanese (in a
    /// dry run, when the layer is in place). True when both are ready; a failure is logged once
    /// and the labels stay English.</summary>
    static bool Ready()
    {
        if (_built) return _atlas is not null;
        _built = true;
        try
        {
            var usExe = Required(JapaneseDisc.ReadUsFile, "GAME.EXE", "US");
            var jpExe = Required(JapaneseDisc.ReadJapaneseFile, "GAME.EXE", "Japanese");
            var usFdat = Required(JapaneseDisc.ReadUsFile, JapaneseMix.FdatPath, "US");
            var jpFdat = Required(JapaneseDisc.ReadJapaneseFile, JapaneseMix.FdatPath, "Japanese");

            var program = JapaneseMenuText.RequireMeasuredExes(usExe, jpExe);
            _map = JapaneseMenuText.Build(usExe, jpExe, program);
            _program = new ExeInterpreter(jpExe);
            _atlas = JapaneseFontAtlas.Plan(usFdat, jpFdat);
            Event.AddListener<VSyncEvent>(vsync => _vblank = vsync.Frame);
            Console.WriteLine($"[KF2] japanese menu: the {program.Name} program; {_map.LabelCount} label(s) in Japanese, {_map.Skipped.Count} left out; {_atlas.FreeCount} free atlas cell(s)" +
                              (PrimBuffer.HasTextScratch ? "" : "; card messages stay English (KF2_PRIMBUF=1 leaves no RAM for them)"));
            if (JapaneseDisc.Probe || JapaneseDisc.DryRun)
            {
                foreach (var skipped in _map.Skipped) Console.WriteLine($"[KF2] japanese menu: left out: {skipped}");
                foreach (var line in JapaneseMenuText.Describe(_map)) Console.WriteLine($"[KF2] japanese menu: {line}");
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            _map = null;
            _atlas = null;
            Console.WriteLine($"[KF2] japanese: the menus stay English: {e.Message}");
        }
        return _atlas is not null;
    }

    static byte[] Required(Func<string, byte[]?> read, string path, string disc) =>
        read(path) ?? throw new InvalidDataException($"the {disc} disc's {path} cannot be read");

    // ---- drawing ----

    /// <summary>The Japanese label routine's primitives for a string of codes: a cell per code,
    /// and an accent's cell over it; a run of Latin letters in the US font's cells. A label
    /// right-aligned by leading blanks still ends where the Japanese routine's would.</summary>
    static void Draw(CpuContext c, IMemory m, uint template, int x, int y, ushort[] codes)
    {
        var cells = Layout(codes, out int width);
        if (codes.Length > 0 && (codes[0] & GlyphMask) == BlankGlyph) x += codes.Length * PenStep - width;
        foreach (var (code, us, at) in cells)
        {
            int pen = x + at;
            if (us >= 0) { DrawUsCell(c, m, template, us, pen, y); continue; }
            int glyph = code & GlyphMask;
            if (glyph != BlankGlyph) DrawCell(c, m, template, glyph, pen, y);
            if ((code & DakutenBit) != 0) DrawCell(c, m, template, DakutenGlyph, pen, y);
            if ((code & HandakutenBit) != 0) DrawCell(c, m, template, HandakutenGlyph, pen, y);
        }
    }

    /// <summary>Each code's place from the label's origin, and the US cell it draws (-1 for a
    /// Japanese cell); <paramref name="width"/> is where the next label cell would go.</summary>
    static List<(int Code, int UsCell, int X)> Layout(ushort[] codes, out int width)
    {
        var cells = new List<(int, int, int)>(codes.Length);
        int pen = 0;
        for (int i = 0; i < codes.Length;)
        {
            if (!Latin.ContainsKey(codes[i]))
            {
                cells.Add((codes[i], -1, pen));
                pen += PenStep;
                i++;
                continue;
            }
            var run = new List<byte>();
            for (; i < codes.Length && Latin.TryGetValue(codes[i], out char ch); i++) run.Add(UsCell(ch));
            var at = Text.Lettering.Drawing ? Text.MenuLabels.Place(run, out int runWidth) : Classic(run.Count, out runWidth);
            for (int k = 0; k < run.Count; k++) cells.Add((-1, run[k], pen + at[k]));
            pen += runWidth + (i < codes.Length ? LatinGap : 0);
        }
        width = pen;
        return cells;
    }

    static int[] Classic(int count, out int width)
    {
        var at = new int[count];
        for (int k = 0; k < count; k++) at[k] = k * UsAdvance;
        width = count * UsAdvance;
        return at;
    }

    /// <summary>The US label font's cell for a Latin letter: the port's letters on MODERN
    /// (lower case once they are in VRAM), the disc's capitals on CLASSIC.</summary>
    static byte UsCell(char ch)
    {
        if (Text.Lettering.Drawing && Text.MenuCells.Index(ch) is >= 0 and var cell) return (byte)cell;
        return (byte)Text.MenuCells.DiscCells[char.ToUpperInvariant(ch)];
    }

    /// <summary>One cell of the US font, as func_80021E10 builds it: the template's tpage,
    /// CLUT and cell size (+8, +0xA).</summary>
    static void DrawUsCell(CpuContext c, IMemory m, uint template, int cell, int x, int y)
    {
        int w = m.ReadU8(template + 8), h = m.ReadU8(template + 0xA);
        int u = (cell & 15) * UsCellU, v = (cell >> 4) * UsCellV;

        KingsField2.func_800229D8(c, m);
        uint prim = m.ReadU32(PrimCursor);
        m.WriteU16(prim + 0x16, m.ReadU16(template));
        m.WriteU16(prim + 0x0E, m.ReadU16(template + 2));
        Corner(m, prim + 0x08, x, y, u, v);
        Corner(m, prim + 0x10, x + w, y, u + w, v);
        Corner(m, prim + 0x18, x, y + h, u, v + h);
        Corner(m, prim + 0x20, x + w, y + h, u + w, v + h);
        c.A0 = LabelSlot;
        KingsField2.func_80022A28(c, m);
    }

    /// <summary>One cell, as func_80021E10 builds it: a POLY_FT4 from the cursor, the template's
    /// tpage and CLUT, the corners and the cell's u, v, linked into the label slot.</summary>
    static void DrawCell(CpuContext c, IMemory m, uint template, int glyph, int x, int y)
    {
        var gpu = RecompOne.Runtime.Runtime.Gpu;
        if (gpu is null || _atlas is null) return;
        var (u, v) = _atlas.CellOf(glyph, _vblank, gpu.Vram, gpu.WriteGp0);

        KingsField2.func_800229D8(c, m);
        uint prim = m.ReadU32(PrimCursor);
        m.WriteU16(prim + 0x16, m.ReadU16(template));
        m.WriteU16(prim + 0x0E, m.ReadU16(template + 2));
        Corner(m, prim + 0x08, x, y, u, v);
        Corner(m, prim + 0x10, x + Cell, y, u + Cell, v);
        Corner(m, prim + 0x18, x, y + Cell, u, v + Cell);
        Corner(m, prim + 0x20, x + Cell, y + Cell, u + Cell, v + Cell);
        c.A0 = LabelSlot;
        KingsField2.func_80022A28(c, m);
    }

    /// <summary>A POLY_FT4 corner: s16 x, s16 y, then u8 u, u8 v.</summary>
    static void Corner(IMemory m, uint at, int x, int y, int u, int v)
    {
        m.WriteU16(at, unchecked((ushort)x));
        m.WriteU16(at + 2, unchecked((ushort)y));
        m.WriteU8(at + 4, (byte)Math.Min(u, 255));
        m.WriteU8(at + 5, (byte)Math.Min(v, 255));
    }

    static string ReadText(IMemory m, uint at)
    {
        Span<byte> bytes = stackalloc byte[MaxTextBytes];
        int length = 0;
        while (length < MaxTextBytes)
        {
            byte b = m.ReadU8(at + (uint)length);
            if (b == TextEnd) break;
            bytes[length++] = b;
        }
        return Encoding.Latin1.GetString(bytes[..length]);
    }

    static void NoteUntranslated(CpuContext c, string text)
    {
        if (!JapaneseDisc.Active) return;
        _englishDraws++;
        if (!JapaneseDisc.Probe || text.Length == 0 || !_untranslated.Add(text)) return;
        Console.WriteLine($"[KF2] japanese menu: no Japanese for \"{JapaneseMenuText.LabelText(text)}\" (first draw, return 0x{c.RA:X8}, record 0x{c.A1:X8})");
    }

    static void ReportEvery()
    {
        if (!JapaneseDisc.Probe) return;
        double now = _clock.Elapsed.TotalMilliseconds;
        if (now < _nextReportMs || _japaneseDraws == _reportedDraws) return;
        _nextReportMs = now + ReportEveryMs;
        _reportedDraws = _japaneseDraws;
        Console.WriteLine($"[KF2] japanese menu: {_japaneseDraws} label(s) drawn in Japanese, {_englishDraws} left to the English, " +
                          $"{_atlas?.Writes} atlas cell write(s), {_atlas?.ForcedEvictions} forced, {_untranslated.Count} distinct without Japanese");
    }
}
