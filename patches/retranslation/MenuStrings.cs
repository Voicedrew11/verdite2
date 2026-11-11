using System;
using System.Collections.Generic;
using Kf2.Text;
using RecompOne.Runtime.Memory;

namespace Kf2.Retranslation;

/// <summary>
/// Menu-font text in GAME.EXE: menu records (s16 x, s16 y, text), and the 24-byte
/// label and name slots (labels, item, equipment and magic names). It is font
/// cells in RAM while GAME.EXE is resident, so it is rewritten in place, encoded
/// by the port's lettering (lower case once the menu font has it). Each slot keeps its
/// size (23 letters at most) and the disc's bytes are kept to put back. A fresh
/// GAME.EXE load brings the disc's text back, so every call checks one slot and
/// rewrites everything when it has reverted or the case has changed.
///
/// Labels the game builds in code on the stack (SLASH, YES, ...) are changed as
/// they are drawn instead (BeforeDraw), up to 11 letters, and put back as soon as
/// the draw returns (AfterDraw): the game builds some labels by
/// writing over part of the last one ("STR POWER" becomes "MAG POWER" by its
/// first three letters), so a rewrite left in place shows through ("MAGength").
/// Every other label in the disc's capitals is left to GameText, which draws
/// it in its normal case.
/// </summary>
static class MenuStrings
{
    const int Slot = 24;
    static readonly Dictionary<uint, byte[]> _original = new();
    static readonly HashSet<string> _ours = new();   // every encoding we have written, as hex
    static bool _lower;
    static volatile bool _dirty;     // the text changed (a workbook import): write it all again
    public static bool Applied { get; private set; }
    public static int Written { get; private set; }

    // Code-built labels, by their US text, are Script.Coded. They may be longer than
    // the original, up to CodedMax: the draw puts the bytes back after.
    const int CodedMax = 11;

    // What BeforeDraw changed, to put back once the draw returns.
    static readonly Stack<(uint Address, byte[] Bytes)?> _drawn = new();

    static string Hex(IMemory m, uint address)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Slot; i++)
        {
            byte b = m.ReadU8(address + (uint)i);
            sb.Append(b.ToString("X2"));
            if (b == 0xFF) break;
        }
        return sb.ToString();
    }

    static string Hex(byte[] b) => Convert.ToHexString(b);

    // The status page's Condition values (PARALYZE .. GOOD): the US text pads each
    // with leading spaces to ten cells, which right-aligns it in the game's fixed
    // 7-pixel spacing, and the page draws it from a fixed x. Ours are padded the
    // same way. MenuLabels recognises these slots and, with proportional menus,
    // ends each word flush with the numbers above it.
    const uint ConditionSlots = 0x8006580C, ConditionEnd = 0x8006589C;
    const int ConditionCells = 10;

    static string Padded(uint address, string text) =>
        address >= ConditionSlots && address < ConditionEnd && text.Length < ConditionCells
            ? text.PadLeft(ConditionCells) : text;

    /// <summary>The text has changed: the next Ensure writes every slot again,
    /// keeping the disc's bytes it already has.</summary>
    public static void Refresh() => _dirty = true;

    /// <summary>Make sure the translation is in RAM (cheap when it already is).</summary>
    public static void Ensure(IMemory m)
    {
        lock (Script.Sync) EnsureLocked(m);
    }

    static void EnsureLocked(IMemory m)
    {
        if (Script.Strings.Length == 0) return;
        bool lower = MenuCells.Ready;
        var (a0, t0) = Script.Strings[0];
        string now = Hex(m, a0);
        var want = MenuCells.Encode(t0, Slot - 1);
        if (!_dirty && Applied && _lower == lower && now == Hex(want)) return;
        _dirty = false;
        // A fresh GAME.EXE has the disc's text: keep it. Text we wrote ourselves is not the disc's.
        bool ours = Applied && _ours.Contains(now);
        if (!ours) _original.Clear();
        foreach (var (address, text) in Script.Strings)
        {
            if (!ours)
            {
                var old = new byte[Slot];
                for (int i = 0; i < Slot; i++) old[i] = m.ReadU8(address + (uint)i);
                _original[address] = old;
            }
            var b = MenuCells.Encode(Padded(address, text), Slot - 1);
            for (int i = 0; i < b.Length; i++) m.WriteU8(address + (uint)i, b[i]);
        }
        _ours.Add(Hex(want));
        Applied = true;
        _lower = lower;
        Written = Script.Strings.Length;
        if (Lettering.Probe) Console.WriteLine($"[KF2] retranslation: {Written} menu strings written, {(lower ? "lower case" : "capitals")}");
    }

    /// <summary>Put the disc's text back.</summary>
    public static void Restore(IMemory m)
    {
        if (!Applied) return;
        var (a0, _) = Script.Strings[0];
        if (_ours.Contains(Hex(m, a0)))
            foreach (var (address, old) in _original)
                for (int i = 0; i < Slot; i++) m.WriteU8(address + (uint)i, old[i]);
        Applied = false;
        if (Lettering.Probe) Console.WriteLine("[KF2] retranslation: menu strings put back");
    }

    /// <summary>Pre on func_80021E10(template, rec): a code-built label at rec+4,
    /// rewritten for the draw. Every call is paired with AfterDraw.</summary>
    public static void BeforeDraw(IMemory m, uint rec)
    {
        if (_drawn.Count > 8) _drawn.Clear();        // a draw that never returned (a load): start again
        (uint, byte[])? done = null;
        try { lock (Script.Sync) done = Rewrite(m, rec + 4); }
        finally { _drawn.Push(done); }           // pushed whatever happens, so the post pairs with it
    }

    /// <summary>A draw with nothing changed, so the post still pairs with it.</summary>
    public static void Skip() => _drawn.Push(null);

    /// <summary>Post on func_80021E10: the label's bytes as they were.</summary>
    public static void AfterDraw(IMemory m)
    {
        if (_drawn.Count == 0) return;
        if (_drawn.Pop() is not { } was) return;
        for (int i = 0; i < was.Bytes.Length; i++) m.WriteU8(was.Address + (uint)i, was.Bytes[i]);
    }

    static (uint, byte[])? Rewrite(IMemory m, uint text)
    {
        if (!MenuCells.Ready) return null;
        var chars = new char[Slot];
        int n = 0;
        for (; n < Slot; n++)
        {
            byte b = m.ReadU8(text + (uint)n);
            if (b == 0xFF) break;
            if (b >= 0x40 && b != 0x7F) return null;     // has an added cell: not the disc's text
            char c = MenuCells.Char(b);
            if (c == '\0') return null;
            chars[n] = c;
        }
        if (n == 0 || n == Slot) return null;
        int lead = 0;
        while (lead < n && chars[lead] == ' ') lead++;
        var core = new string(chars, lead, n - lead);
        if (!Script.Coded.TryGetValue(core, out var word) || word == core) return null;
        int max = Math.Max(core.Length, CodedMax);
        if (word.Length > max) return null;
        var e = MenuCells.Encode(word, max);
        var old = new byte[lead + e.Length];
        for (int i = 0; i < old.Length; i++) old[i] = m.ReadU8(text + (uint)i);
        for (int i = 0; i < e.Length; i++) m.WriteU8(text + (uint)(lead + i), e[i]);
        return (text, old);
    }
}
