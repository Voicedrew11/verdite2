using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// The status screen's numbers, before and after, on the equip prompt and the
/// shops' buy prompt, drawn in the game's own font and window.
///
///     KF2_GEARCOMPARE=0       off (on by default; Gameplay ▸ Compare gear)
///     KF2_GEARCOMPARE=verify  draw each panel through the recompiled routines
///                             as well and compare every byte
///
/// "After" is <c>func_800244CC</c> run with the candidate in its slot byte, and
/// the slot and the nineteen words it rebuilds put back; the real equip calls are
/// never made. The panel is drawn after the prompt's own boxes
/// (<c>func_80021478</c>) by <see cref="MenuDraw"/>. See "Comparing gear on the
/// equip prompt" in docs/PATCHES_AND_MODS.md.
/// </summary>
public static class GearCompare
{
    public const string OnKey = "kf2.gearcompare.enabled";

    public static bool Enabled { get; set; } = true;

    static bool _fromEnv, _verify;

    const uint EquipPage = 0x8001A6E8;
    const uint PromptLoop = 0x800206E0;
    const uint PromptDraw = 0x80021478;
    static readonly uint[] ShopPages = [0x8001D6BC, 0x8001DF5C, 0x8001E45C];

    const uint StatsStart = 0x8019943C;   // STR POWER .. the last defense word
    const uint StatsEnd   = 0x80199468;

    static readonly (string Label, uint Addr, int Group)[] Stats =
    [
        ("STR POWER", 0x8019943C, 0),
        ("MAG POWER", 0x8019943E, 0),
        ("SLASH", 0x80199444, 1), ("CHOP", 0x80199446, 1), ("STAB", 0x80199448, 1),
        ("HOLY", 0x8019944A, 1), ("FIRE", 0x8019944C, 1), ("EARTH", 0x8019944E, 1),
        ("WIND", 0x80199450, 1), ("WATER", 0x80199452, 1),
        ("SLASH", 0x80199456, 2), ("CHOP", 0x80199458, 2), ("STAB", 0x8019945A, 2),
        ("POISON", 0x8019945C, 2), ("DARK", 0x8019945E, 2), ("FIRE", 0x80199460, 2),
        ("EARTH", 0x80199462, 2), ("WIND", 0x80199464, 2), ("WATER", 0x80199466, 2),
    ];

    static readonly string[] GroupHeads = ["", "OFFENSE", "DEFENSE"];

    static int _kind = -1;        // the equipment page's kind while it is open
    static int _shop;             // buy pages open (they do not nest, but count)
    static bool _active;          // a panel is to be drawn on the open prompt
    static bool _fromShop;        // the open prompt is a shop's
    static readonly List<(string Text, int Now, int New, bool Head)> _rows = [];

    static readonly ModInfo _self = new()
    {
        Id = "kf2.gearcompare",
        Name = "Gear compare",
        Version = "1.0",
        Description = "Shows the stats an equip or a purchase would change, beside the prompt.",
    };

    public static void Configure(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        _fromEnv = true;
        _verify = mode.Equals("verify", StringComparison.OrdinalIgnoreCase);
        Enabled = mode != "0";
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            if (!_fromEnv) Enabled = RecompOne.Runtime.Runtime.View.GetBool(OnKey, Enabled);
        });
        HookAttach.OnOverlayLoad("gear compare", Attach);
    }

    static readonly HashSet<uint> _hooked = [];

    static bool Attach()
    {
        SymbolRegistry.Build();
        var targets = new List<(uint Addr, string Pre, string Post)>
        {
            (EquipPage, nameof(EnterEquipPage), nameof(LeaveEquipPage)),
            (PromptLoop, nameof(PromptOpens), nameof(PromptCloses)),
            (PromptDraw, "", nameof(AfterPromptDrawn)),
        };
        foreach (uint a in ShopPages) targets.Add((a, nameof(EnterShop), nameof(LeaveShop)));

        var queued = new List<(uint Addr, MethodInfo Fn)>();
        foreach (var (addr, pre, post) in targets)
        {
            if (_hooked.Contains(addr)) continue;
            var fn = SymbolRegistry.Resolve("game", null, addr);
            if (fn == null)
            {
                Console.Error.WriteLine($"[KF2] gear compare: no game function at 0x{addr:X8}");
                continue;
            }
            if (pre != "") HookManager.AddPre(_self, fn, Own(pre));
            HookManager.AddPost(_self, fn, Own(post));
            queued.Add((addr, fn));
        }

        HookManager.Commit();
        foreach (var (addr, fn) in queued)
            if (HookAttach.Installed(fn)) _hooked.Add(addr);

        bool all = _hooked.Count == targets.Count;
        Console.WriteLine($"[KF2] gear compare: {(Enabled ? "on" : "off")}{(_verify ? ", verify" : "")}, " +
                          $"{_hooked.Count}/{targets.Count} hooked");
        return all;
    }

    static MethodInfo Own(string name) =>
        typeof(GearCompare).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;

    // ---- where the candidate comes from ----
    //
    // func_8001A6E8(kind) is the equipment page; kind picks the slot the "Yes"
    // arm writes. It opens the prompt as func_800206E0(desc, 5, 5, id), the
    // highlighted item's id in A3 (0xFF on the last row, "take it off").

    static uint? SlotFor(int kind) => kind switch
    {
        0 => 0x801994AF,
        3 => 0x801994D4,
        4 => 0x801994D5,
        2 => 0x801994D6,
        5 => 0x801994D7,
        6 => 0x801994D8,
        7 => 0x801994D9,
        8 => 0x801994DA,
        _ => null,
    };

    // A shop's buy page opens the same prompt with the item id in A3; the slot
    // follows from the id's range, and a ring takes an empty ring slot first.
    static uint? SlotForItem(IMemory m, int id) => id switch
    {
        <= 0x14 => 0x801994AF,
        <= 0x1B => 0x801994D4,
        <= 0x21 => 0x801994D5,
        <= 0x28 => 0x801994D6,
        <= 0x2E => 0x801994D7,
        <= 0x34 => 0x801994D8,
        <= 0x3B => m.ReadU8(0x801994D9) == 0xFF ? 0x801994D9
                 : m.ReadU8(0x801994DA) == 0xFF ? 0x801994DA : 0x801994D9,
        _ => null,
    };

    public static void EnterEquipPage(CpuContext c, IMemory m) => _kind = (int)c.A0;

    public static void LeaveEquipPage(CpuContext c, IMemory m)
    {
        _kind = -1;
        _active = false;
    }

    public static void EnterShop(CpuContext c, IMemory m) => _shop++;

    public static void LeaveShop(CpuContext c, IMemory m)
    {
        if (_shop > 0) _shop--;
        _active = false;
    }

    public static void PromptOpens(CpuContext c, IMemory m)
    {
        _active = false;
        if (!Enabled) return;

        uint slot;
        if (_kind >= 0 && SlotFor(_kind) is uint s) slot = s;
        else if (_shop > 0 && SlotForItem(m, (int)(c.A3 & 0xFF)) is uint t) slot = t;
        else return;

        byte candidate = (byte)(c.A3 & 0xFF);
        var saved = c.Snapshot();
        try
        {
            Compute(c, m, slot, candidate);
            _active = true;
            _fromShop = _kind < 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] gear compare: {e.Message}");
        }
        finally
        {
            c.Restore(saved);
        }
    }

    public static void PromptCloses(CpuContext c, IMemory m) => _active = false;

    static void Compute(CpuContext c, IMemory m, uint slot, byte candidate)
    {
        var now = new int[Stats.Length];
        for (int i = 0; i < Stats.Length; i++) now[i] = m.ReadU16(Stats[i].Addr);

        byte oldSlot = m.ReadU8(slot);
        var block = new byte[StatsEnd - StatsStart];
        for (uint i = 0; i < block.Length; i++) block[i] = m.ReadU8(StatsStart + i);

        var after = new int[Stats.Length];
        try
        {
            m.WriteU8(slot, candidate);
            KingsField2.func_800244CC(c, m);
            for (int i = 0; i < Stats.Length; i++) after[i] = m.ReadU16(Stats[i].Addr);
        }
        finally
        {
            m.WriteU8(slot, oldSlot);
            for (uint i = 0; i < block.Length; i++) m.WriteU8(StatsStart + i, block[i]);
        }

        _rows.Clear();
        int group = -1;
        for (int i = 0; i < Stats.Length; i++)
        {
            if (now[i] == after[i]) continue;
            if (Stats[i].Group != group)
            {
                if (group > 0) AddTotal(group, now, after);
                group = Stats[i].Group;
                if (GroupHeads[group].Length > 0) _rows.Add((GroupHeads[group], 0, 0, true));
            }
            _rows.Add((Stats[i].Label, now[i], after[i], false));
        }
        if (group > 0) AddTotal(group, now, after);
    }

    // The sum of the whole group, not only the rows shown: a rough guide, since
    // a hit is scored per damage type (func_8003A94C, summed in func_8003A9CC).
    static void AddTotal(int group, int[] now, int[] after)
    {
        int n = 0, a = 0;
        for (int i = 0; i < Stats.Length; i++)
            if (Stats[i].Group == group) { n += now[i]; a += after[i]; }
        _rows.Add(("TOTAL", n, a, false));
    }

    // ---- drawing ----

    /// <summary>The three draws a layout makes: <see cref="MenuDraw"/>, or the
    /// recompiled routines for the verify comparison.</summary>
    interface IPen
    {
        void Text(int x, int y, string s);
        void Number(int x, int y, int value);
        void Box(int x, int y, int w, int h);
    }

    sealed class NativePen(IMemory m) : IPen
    {
        public void Text(int x, int y, string s) => MenuDraw.DrawText(m, x, y, s);
        public void Number(int x, int y, int value) => MenuDraw.DrawNumber(m, x, y, value);
        public void Box(int x, int y, int w, int h) => MenuDraw.DrawWindow(m, x, y, w, h);
    }

    sealed class ReferencePen(CpuContext c, IMemory m) : IPen
    {
        public void Text(int x, int y, string s) => MenuDraw.Reference.Text(c, m, x, y, s);
        public void Number(int x, int y, int value) => MenuDraw.Reference.Number(c, m, x, y, value);
        public void Box(int x, int y, int w, int h) => MenuDraw.Reference.Box(c, m, x, y, w, h);
    }

    // The equipment page's layout, in the PS1's 320x240. The EQUIPMENT title ends
    // at about y 36 and the item list's window starts at about y 160, so a panel
    // that would reach the list slides up toward the title first.
    const int PanelX = 99, PanelY = 40, RowHeight = 13;
    const int LabelX = 8, NowX = 92, NewX = 128, Width = 170, Pad = 6;
    const int TopLimit = 38, BottomLimit = 158;

    public static void AfterPromptDrawn(CpuContext c, IMemory m)
    {
        if (!_active) return;
        try
        {
            if (_verify) Verify(c, m);
            else Draw(new NativePen(m));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] gear compare draw: {e.Message}");
            _active = false;
        }
    }

    static void Draw(IPen pen)
    {
        if (_fromShop) DrawCompact(pen);
        else DrawPanel(pen);
    }

    static void DrawPanel(IPen pen)
    {
        int x = PanelX;

        // The column heads share the first line with the first group's heading
        // when there is one, which saves a row.
        bool headFirst = _rows.Count > 0 && _rows[0].Head;
        int lines = 1 + (_rows.Count == 0 ? 1 : _rows.Count - (headFirst ? 1 : 0));
        int height = Pad + lines * RowHeight + Pad;

        int top = PanelY;
        if (top + height > BottomLimit) top = Math.Max(TopLimit, BottomLimit - height);
        int y = top + Pad;

        if (headFirst) pen.Text(x + LabelX, y, _rows[0].Text);
        pen.Text(x + NowX - 4, y, "NOW");
        pen.Text(x + NewX - 4, y, "NEW");
        y += RowHeight;

        if (_rows.Count == 0)
        {
            pen.Text(x + LabelX, y, "NO CHANGE");
            y += RowHeight;
        }
        for (int i = headFirst ? 1 : 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (r.Head)
            {
                pen.Text(x + LabelX, y, r.Text);
            }
            else
            {
                pen.Text(x + LabelX + 8, y, r.Text);
                pen.Number(x + NowX, y, r.Now);
                pen.Number(x + NewX, y, r.New);
            }
            y += RowHeight;
        }

        // The window last, so the ordering table puts it underneath, as the
        // status screen does.
        pen.Box(x, top, Width, y - top + Pad);
    }

    // A shop has about 80 lines free between GOLD/NUMBER and the stock list, and
    // an armour's nine defense rows need twice that: the rows flow into two
    // columns with three-letter names, and a lone group's heading is dropped.
    const int ShopX = 92, ShopY = 78;
    const int CellW = 84, CNow = 30, CNew = 56, CRow = 12;

    static string Short(string label) => label switch
    {
        "STR POWER" => "STR", "MAG POWER" => "MAG", "SLASH" => "SLA", "CHOP" => "CHP",
        "STAB" => "STB", "HOLY" => "HLY", "FIRE" => "FIR", "EARTH" => "ERT",
        "WIND" => "WND", "WATER" => "WTR", "POISON" => "PSN", "DARK" => "DRK",
        "TOTAL" => "TOT", "OFFENSE" => "OFF", "DEFENSE" => "DEF",
        _ => label.Length > 3 ? label[..3] : label,
    };

    static void DrawCompact(IPen pen)
    {
        var cells = new List<(string Text, int Now, int New, bool Head)>();
        int heads = 0;
        foreach (var r in _rows) if (r.Head) heads++;
        foreach (var r in _rows)
            if (!(r.Head && heads == 1)) cells.Add(r);

        int perCol = Math.Max(1, (cells.Count + 1) / 2);
        int cols = cells.Count > perCol ? 2 : 1;
        int x = ShopX, top = ShopY;
        int y0 = top + Pad;

        for (int k = 0; k < cols; k++)
        {
            pen.Text(x + LabelX + k * CellW + CNow - 4, y0, "NOW");
            pen.Text(x + LabelX + k * CellW + CNew - 4, y0, "NEW");
        }

        if (cells.Count == 0)
            pen.Text(x + LabelX, y0 + CRow, "NO CHANGE");

        for (int i = 0; i < cells.Count; i++)
        {
            var r = cells[i];
            int cx = x + LabelX + (i / perCol) * CellW;
            int cy = y0 + CRow * (1 + i % perCol);
            pen.Text(cx, cy, Short(r.Text));
            if (!r.Head)
            {
                pen.Number(cx + CNow, cy, r.Now);
                pen.Number(cx + CNew, cy, r.New);
            }
        }

        int lines = 1 + Math.Max(1, cells.Count == 0 ? 1 : perCol);
        pen.Box(x, top, LabelX * 2 + cols * CellW - 6, Pad * 2 + lines * CRow);
    }

    // ---- KF2_GEARCOMPARE=verify ----
    //
    // Draw the panel through the recompiled routines, rewind the cursor, its
    // mirror and the two ordering-table slots, draw it through MenuDraw, and
    // compare every byte. MenuDraw's output is the one kept.

    static long _panels, _mismatches;
    static DateTime _lastVerifyLine = DateTime.MinValue;

    static void Verify(CpuContext c, IMemory m)
    {
        uint o0 = m.ReadU32(MenuDraw.Cursor);
        uint desc = m.ReadU32(MenuDraw.ActiveDescriptor);
        uint cur0 = m.ReadU32(desc + 8);
        uint ot = m.ReadU32(MenuDraw.OrderingTable);
        uint s10 = m.ReadU32(ot + 10 * 4), s20 = m.ReadU32(ot + 20 * 4);

        var saved = c.Snapshot();
        try
        {
            c.SP -= 0x80u;
            Draw(new ReferencePen(c, m));
        }
        finally
        {
            c.Restore(saved);
        }
        uint o1 = m.ReadU32(MenuDraw.Cursor);
        var refBytes = new byte[o1 - o0];
        for (uint i = 0; i < refBytes.Length; i++) refBytes[i] = m.ReadU8(o0 + i);
        uint cur1 = m.ReadU32(desc + 8);
        uint s10a = m.ReadU32(ot + 10 * 4), s20a = m.ReadU32(ot + 20 * 4);

        m.WriteU32(MenuDraw.Cursor, o0);
        m.WriteU32(desc + 8, cur0);
        m.WriteU32(ot + 10 * 4, s10);
        m.WriteU32(ot + 20 * 4, s20);

        Draw(new NativePen(m));
        uint o2 = m.ReadU32(MenuDraw.Cursor);
        uint cur2 = m.ReadU32(desc + 8);
        uint s10b = m.ReadU32(ot + 10 * 4), s20b = m.ReadU32(ot + 20 * 4);

        _panels++;
        int bad = 0;
        string first = "";
        if (o2 != o1) { bad++; first = $"cursor ref {o1:X} vs new {o2:X}"; }
        if (cur2 != cur1) { bad++; if (first == "") first = $"current ref {cur1:X} vs new {cur2:X}"; }
        if (s10b != s10a) { bad++; if (first == "") first = $"slot 10 ref {s10a:X} vs new {s10b:X}"; }
        if (s20b != s20a) { bad++; if (first == "") first = $"slot 20 ref {s20a:X} vs new {s20b:X}"; }
        if (o2 - o0 == refBytes.Length)
        {
            for (uint i = 0; i < refBytes.Length; i++)
            {
                byte b = m.ReadU8(o0 + i);
                if (b != refBytes[i])
                {
                    bad++;
                    if (first == "") first = $"packet {i / 0x28} byte {i % 0x28}: ref {refBytes[i]:X2} vs new {b:X2}";
                    break;
                }
            }
        }
        else if (bad == 0) { bad++; first = $"length ref {refBytes.Length} vs new {o2 - o0}"; }
        if (bad > 0) _mismatches++;

        var now = DateTime.UtcNow;
        if (now - _lastVerifyLine >= TimeSpan.FromSeconds(1))
        {
            _lastVerifyLine = now;
            Console.WriteLine($"[KF2] gear compare verify: {_panels} panels, {_mismatches} mismatches" +
                              (first != "" ? $"; first: {first}" : ""));
        }
    }
}
