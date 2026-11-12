using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Shortcuts;

/// <summary>
/// The HUD boxes' icons: each item's own model, the one the menu's preview shows, posed
/// once and framed to fill its box. The game has no 2D icons; its HUD (the compass, the
/// HP digits) is small models drawn without perspective, and these are drawn the same
/// way: the orthographic transform func_8002E910 and the lit assembler func_8002F214 onto
/// the front of the table, lit as the preview lights its model (func_8002156C: back
/// colour 60, a white colour matrix, its fixed light matrix turned with the model).
///
/// The pose, worked out once when a model is read: a weapon lies on the diagonal, its
/// narrow end (the point) up and right, as an inventory icon shows a sword; anything
/// else stands as it is, tipped a little toward the eye. Either way the turn about the
/// vertical is the one that shows the most of it. The matrix is built here, so the box
/// it is measured into is the box it is drawn into: the model's projected corners fill
/// the box, centred.
///
/// The models: the menu's preview loads one at a time into bank 3 off the model heap
/// (func_80022CAC: disc file 6, entry = the item id). Three are needed at once, so each
/// is read here through the same two routines (func_80017F1C, its size; func_800183A0,
/// the read) into a slot of the port's own above 2 MB (<see cref="PrimBuffer.IconScratch"/>),
/// fixed up once by func_8002E314 as func_8002E5E8 does, and never touches the bank
/// table or the heap. A slot is read again only when what it shows changes, at a
/// control tick (a disc read, as the game's own weapon equip makes there).
///
/// Game thread throughout.
/// </summary>
static class HandsIcons
{
    const uint CurrentModel = 0x8018E19C;                    // the model func_8002E1BC indexes
    const uint VertexBase = 0x8018EAA0;                      // what func_8002E910 reads
    const uint FakeRa = 0x8001FFF4;
    const int ItemFile = 6, ItemCount = 120;
    const int WeaponIds = 18;                                // swords, axes, blades, the bow, the arbalest
    const int Slots = 3;
    const uint SlotBytes = PrimBuffer.IconBytes / Slots;
    const int TmdHeader = 0xC;
    const int MaxVertices = 4096;

    // The preview's light (func_8002156C): back colour 60, every colour 1.0, and this
    // matrix times the model's turn.
    const int Back = 60, PreviewFog = 0x59D8;
    static readonly int[] PreviewLight = [0x800, 0x800, -0x800, 0, 0, -0x800, 0x1000, 0, -0x800];

    const int Diagonal = 0x200;                              // 45 degrees, in 4096ths of a turn
    const int Tip = 0x100;                                   // what a standing item is tipped toward the eye

    /// <summary>One slot: the item it holds (0xFF none), whether that loaded, and the pose
    /// worked out for it: its turn, the scale that fills a unit box, and the middle of its
    /// projected corners at that scale.</summary>
    struct Slot
    {
        public byte Item;
        public bool Ready;
        public int[] Turn;                                   // 3x3, 4.12
        public double Fill;                                  // scale per pixel of box
        public double MidX, MidY;                            // projected middle per unit of scale
    }

    static readonly Slot[] _slots = [new() { Item = 0xFF }, new() { Item = 0xFF }, new() { Item = 0xFF }];
    static readonly HashSet<byte> _failed = [];

    static uint Base(int slot) => PrimBuffer.IconScratch + (uint)slot * SlotBytes;

    internal static bool Available => PrimBuffer.HasIconScratch;

    /// <summary>Reads the model of <paramref name="item"/> into <paramref name="slot"/> if
    /// it is not there already. Called from player control's pre-hook.</summary>
    internal static void Want(CpuContext c, IMemory m, int slot, byte item, bool probe)
    {
        if (!Available || _slots[slot].Item == item) return;
        _slots[slot] = new Slot { Item = item };
        if (item >= ItemCount || _failed.Contains(item)) return;

        uint dst = Base(slot);
        var snap = c.Snapshot();
        try
        {
            c.SP -= 0x100;
            uint size = Call(c, m, KingsField2.func_80017F1C, ItemFile, item, 0);
            if (size == 0 || size > SlotBytes)
            {
                _failed.Add(item);
                Console.Error.WriteLine($"[KF2] shortcuts: item 0x{item:X2}'s model is {size} bytes; no icon");
                return;
            }
            Call(c, m, KingsField2.func_800183A0, ItemFile, item, dst);
            Call(c, m, KingsField2.func_8002E314, dst);
        }
        finally { c.Restore(snap); }

        var verts = Vertices(m, dst);
        if (verts == null)
        {
            _failed.Add(item);
            Console.Error.WriteLine($"[KF2] shortcuts: item 0x{item:X2}'s model did not read as a TMD; no icon");
            return;
        }
        var s = Pose(verts, item < WeaponIds);
        s.Item = item;
        s.Ready = true;
        _slots[slot] = s;
        if (probe) Console.WriteLine($"[KF2] shortcuts: icon {slot} item 0x{item:X2}, {verts.Length} vertices, fill {s.Fill:F3}");
    }

    /// <summary>The first object's vertices, or null if the bytes do not look like a model.</summary>
    static (int X, int Y, int Z)[]? Vertices(IMemory m, uint tmd)
    {
        uint objects = m.ReadU32(tmd + 8);
        if (objects == 0 || objects > 64) return null;
        uint obj = tmd + TmdHeader;
        uint at = tmd + TmdHeader + m.ReadU32(obj);
        uint count = m.ReadU32(obj + 4);
        if (count == 0 || count > MaxVertices || at < tmd || at + count * 8 > tmd + SlotBytes) return null;
        var v = new (int, int, int)[count];
        for (uint i = 0; i < count; i++)
            v[i] = ((short)m.ReadU16(at + i * 8), (short)m.ReadU16(at + i * 8 + 2), (short)m.ReadU16(at + i * 8 + 4));
        return v;
    }

    // ---- the pose ----

    static Slot Pose((int X, int Y, int Z)[] v, bool weapon)
    {
        // A weapon is turned so its long axis is the model's vertical first.
        int[] upright = weapon ? LongAxisUp(v) : Identity();
        int[] best = upright;
        double bestArea = -1;
        for (int yaw = 0; yaw < 0x1000; yaw += 0x80)
        {
            int[] stood = Mul(Ry(yaw), upright);
            int[] t = weapon ? Mul(Rz(PointUpRight(v, stood)), stood) : Mul(Rx(Tip), stood);
            // How much of it shows. A blade's box hardly changes with its thickness, so a
            // weapon is judged by how far its vertices spread across the blade, which is
            // widest with the flat to the eye; anything else by its box.
            double area;
            if (weapon) area = SpreadX(v, stood);
            else
            {
                var (w, h, _, _) = Extent(v, t);
                area = w * h;
            }
            if (area > bestArea + 1e-6) { bestArea = area; best = t; }
        }
        var (bw, bh, mx, my) = Extent(v, best);
        double size = Math.Max(1, Math.Max(bw, bh));
        return new Slot { Turn = best, Fill = 1.0 / size, MidX = mx / size, MidY = my / size };
    }

    /// <summary>Turns the model so its longest bounding-box axis is vertical.</summary>
    static int[] LongAxisUp((int X, int Y, int Z)[] v)
    {
        int Span(Func<(int X, int Y, int Z), int> f) => v.Max(f) - v.Min(f);
        int sx = Span(p => p.X), sy = Span(p => p.Y), sz = Span(p => p.Z);
        if (sy >= sx && sy >= sz) return Identity();
        return sx >= sz ? Rz(0x400) : Rx(0x400);
    }

    /// <summary>The roll that puts the vertical axis on the diagonal with the narrow end
    /// (a blade's point; the hilt's guard is the wide end) up and right.</summary>
    static int PointUpRight((int X, int Y, int Z)[] v, int[] t)
    {
        var p = v.Select(q => Apply(t, q)).ToArray();
        double minY = p.Min(q => q.Y), maxY = p.Max(q => q.Y), band = (maxY - minY) * 0.2;
        double Width(Func<double, bool> inBand)
        {
            var xs = p.Where(q => inBand(q.Y)).Select(q => q.X).ToArray();
            return xs.Length == 0 ? 0 : xs.Max() - xs.Min();
        }
        double top = Width(y => y <= minY + band), bottom = Width(y => y >= maxY - band);
        // Screen Y grows down, and Rz takes (0, -1) to (sin, -cos): with the narrow end at
        // the top, +45 degrees leans it up and right; with it at the bottom, 180 + 45.
        return top <= bottom ? Diagonal : 0x800 + Diagonal;
    }

    /// <summary>The variance of the vertices' X after <paramref name="t"/>: with the long
    /// axis vertical, how wide the model looks across it.</summary>
    static double SpreadX((int X, int Y, int Z)[] v, int[] t)
    {
        double sum = 0, sq = 0;
        foreach (var q in v)
        {
            double x = Apply(t, q).X;
            sum += x;
            sq += x * x;
        }
        double mean = sum / v.Length;
        return sq / v.Length - mean * mean;
    }

    static (double W, double H, double MidX, double MidY) Extent((int X, int Y, int Z)[] v, int[] t)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var q in v)
        {
            var (x, y, _) = Apply(t, q);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return (maxX - minX, maxY - minY, (minX + maxX) / 2, (minY + maxY) / 2);
    }

    static (double X, double Y, double Z) Apply(int[] t, (int X, int Y, int Z) q) =>
        ((t[0] * q.X + t[1] * q.Y + t[2] * q.Z) / 4096.0,
         (t[3] * q.X + t[4] * q.Y + t[5] * q.Z) / 4096.0,
         (t[6] * q.X + t[7] * q.Y + t[8] * q.Z) / 4096.0);

    static int[] Identity() => [0x1000, 0, 0, 0, 0x1000, 0, 0, 0, 0x1000];

    static (int C, int S) CosSin(int a)
    {
        double r = a * Math.PI * 2 / 4096;
        return ((int)Math.Round(Math.Cos(r) * 4096), (int)Math.Round(Math.Sin(r) * 4096));
    }

    static int[] Rx(int a) { var (c, s) = CosSin(a); return [0x1000, 0, 0, 0, c, -s, 0, s, c]; }
    static int[] Ry(int a) { var (c, s) = CosSin(a); return [c, 0, s, 0, 0x1000, 0, -s, 0, c]; }
    static int[] Rz(int a) { var (c, s) = CosSin(a); return [c, -s, 0, s, c, 0, 0, 0, 0x1000]; }

    static int[] Mul(int[] a, int[] b)
    {
        var r = new int[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = (a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j]) >> 12;
        return r;
    }

    // ---- drawing ----

    /// <summary>True if <paramref name="slot"/> has a model ready for <paramref name="item"/>.</summary>
    internal static bool Ready(int slot, byte item) => _slots[slot].Ready && _slots[slot].Item == item;

    /// <summary>Draws slot <paramref name="slot"/>'s model in its pose, filling a
    /// <paramref name="size"/>-pixel square centred on (x, y), at depth <paramref name="z"/>.
    /// Inside the HUD builder's post-hook.</summary>
    internal static void Draw(CpuContext c, IMemory m, int slot, int x, int y, int size, int z)
    {
        var s = _slots[slot];
        if (!s.Ready) return;
        double k = s.Fill * size;                            // model units to pixels
        var draw = new int[9];
        for (int i = 0; i < 9; i++) draw[i] = Math.Clamp((int)Math.Round(s.Turn[i] * k), short.MinValue, short.MaxValue);
        var light = Mul(PreviewLight, s.Turn);
        int tx = x - (int)Math.Round(s.MidX * size), ty = y - (int)Math.Round(s.MidY * size);

        uint tmd = Base(slot);
        uint savedModel = m.ReadU32(CurrentModel), savedVerts = m.ReadU32(VertexBase);
        var snap = c.Snapshot();
        try
        {
            c.SP = (c.SP - 0x200) & ~0xFu;
            uint mat = c.SP + 0x40, lit = c.SP + 0x80, white = c.SP + 0xC0;
            WriteMatrix(m, mat, draw, tx, ty, z);
            WriteMatrix(m, lit, light, 0, 0, 0);
            WriteMatrix(m, white, [0x1000, 0x1000, 0x1000, 0x1000, 0x1000, 0x1000, 0x1000, 0x1000, 0x1000], 0, 0, 0);

            Call(c, m, KingsField2.func_8002DDDC, PreviewFog);
            Call(c, m, KingsField2.SetBackColor_game, Back, Back, Back);
            Call(c, m, KingsField2.SetColorMatrix, white);
            Call(c, m, KingsField2.SetLightMatrix, lit);
            Call(c, m, KingsField2.SetRotMatrix, mat);
            Call(c, m, KingsField2.SetTransMatrix, mat);

            m.WriteU32(CurrentModel, tmd);
            Call(c, m, KingsField2.func_8002E1F0, 0);
            Call(c, m, KingsField2.func_8002E910, m.ReadU32(tmd + TmdHeader + 4));
            Call(c, m, KingsField2.func_8002F214, 0, 0);
        }
        finally
        {
            c.Restore(snap);
            m.WriteU32(CurrentModel, savedModel);
            m.WriteU32(VertexBase, savedVerts);
        }
    }

    /// <summary>A PSY-Q MATRIX: nine s16 (4.12), two bytes of pad, three s32 translations.</summary>
    static void WriteMatrix(IMemory m, uint at, int[] r, int tx, int ty, int tz)
    {
        for (uint i = 0; i < 9; i++) m.WriteU16(at + i * 2, (ushort)(short)r[i]);
        m.WriteU16(at + 18, 0);
        m.WriteU32(at + 0x14, (uint)tx);
        m.WriteU32(at + 0x18, (uint)ty);
        m.WriteU32(at + 0x1C, (uint)tz);
    }

    static uint Call(CpuContext c, IMemory m, Action<CpuContext, IMemory> f, uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        c.A0 = a0; c.A1 = a1; c.A2 = a2; c.A3 = a3;
        c.RA = FakeRa;
        f(c, m);
        return c.V0;
    }

    static uint Call(CpuContext c, IMemory m, Action<CpuContext, IMemory> f, int a0, int a1 = 0, int a2 = 0) =>
        Call(c, m, f, (uint)a0, (uint)a1, (uint)a2);
}
