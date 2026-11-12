using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Shortcuts;

/// <summary>
/// The HUD boxes' icons: each item's own model, the one the menu's preview spins, kept
/// turning in its box. The game has no 2D icons; its HUD (the compass, the HP digits) is
/// small models drawn without perspective, and these are drawn the same way, through the
/// HUD builder's own steps (func_80031D5C): its light record, the orthographic transform
/// func_8002E910 and the lit assembler func_8002F214 onto the front of the table.
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
    const uint LightRecords = 0x801930F0;                    // 0x68 bytes each
    const uint FakeRa = 0x8001FFF4;
    const int ItemFile = 6, ItemCount = 120;
    const int Slots = 3;
    const uint SlotBytes = PrimBuffer.IconBytes / Slots;
    const int TmdHeader = 0xC, ObjectBytes = 28;

    /// <summary>One slot: the item it holds (0xFF none), whether that loaded, and the
    /// model's extent, for its scale.</summary>
    struct Slot
    {
        public byte Item;
        public bool Ready;
        public int CentreX, CentreY, CentreZ, Radius;
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

        if (!Measure(m, dst, out var s))
        {
            _failed.Add(item);
            Console.Error.WriteLine($"[KF2] shortcuts: item 0x{item:X2}'s model did not read as a TMD; no icon");
            return;
        }
        s.Item = item;
        s.Ready = true;
        _slots[slot] = s;
        if (probe) Console.WriteLine($"[KF2] shortcuts: icon {slot} item 0x{item:X2}, radius {s.Radius}");
    }

    /// <summary>The model's first object's box, read off its vertices; false if the
    /// bytes do not look like a model.</summary>
    static bool Measure(IMemory m, uint tmd, out Slot s)
    {
        s = default;
        uint objects = m.ReadU32(tmd + 8);
        if (objects == 0 || objects > 64) return false;
        uint obj = tmd + TmdHeader;
        uint verts = tmd + TmdHeader + m.ReadU32(obj);
        uint count = m.ReadU32(obj + 4);
        if (count == 0 || count > 4096 || verts + count * 8 > tmd + SlotBytes) return false;
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        for (uint i = 0; i < count; i++)
        {
            short x = (short)m.ReadU16(verts + i * 8), y = (short)m.ReadU16(verts + i * 8 + 2), z = (short)m.ReadU16(verts + i * 8 + 4);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);
        }
        s.CentreX = (minX + maxX) / 2;
        s.CentreY = (minY + maxY) / 2;
        s.CentreZ = (minZ + maxZ) / 2;
        int rx = (maxX - minX) / 2, ry = (maxY - minY) / 2, rz = (maxZ - minZ) / 2;
        s.Radius = Math.Max(1, (int)Math.Sqrt((double)rx * rx + (double)ry * ry + (double)rz * rz));
        return true;
    }

    /// <summary>True if <paramref name="slot"/> has a model ready for <paramref name="item"/>.</summary>
    internal static bool Ready(int slot, byte item) => _slots[slot].Ready && _slots[slot].Item == item;

    /// <summary>Draws slot <paramref name="slot"/>'s model turning about its middle at
    /// (x, y), at most <paramref name="half"/> pixels from it, lit by light record
    /// <paramref name="light"/> at depth <paramref name="z"/>, as the HUD's own models are.
    /// Inside the HUD builder's post-hook.</summary>
    internal static void Draw(CpuContext c, IMemory m, int slot, int x, int y, int half, int light, int z)
    {
        var s = _slots[slot];
        if (!s.Ready) return;
        uint tmd = Base(slot);
        uint savedModel = m.ReadU32(CurrentModel), savedVerts = m.ReadU32(VertexBase);
        var snap = c.Snapshot();
        try
        {
            c.SP = (c.SP - 0x200) & ~0xFu;
            uint sp = c.SP + 0x40;
            uint angles = sp, mat = sp + 0x10, lit = sp + 0x40, scale = sp + 0x70;

            // A slow turn, the preview's (4 a frame at 60) on the clock, tipped toward the eye.
            int turn = (int)(Environment.TickCount64 * 240 / 1000 & 0xFFF);
            m.WriteU16(angles, 0x100);
            m.WriteU16(angles + 2, (ushort)turn);
            m.WriteU16(angles + 4, 0);
            Call(c, m, KingsField2.RotMatrix, angles, mat);

            uint rec = LightRecords + (uint)light * 0x68;
            Call(c, m, KingsField2.func_8002DDDC, (uint)(short)m.ReadU16(rec + 0x66));
            Call(c, m, KingsField2.SetBackColor_game, m.ReadU8(rec + 0x62), m.ReadU8(rec + 0x63), m.ReadU8(rec + 0x64));
            Call(c, m, KingsField2.SetColorMatrix, rec + 0x50);
            Call(c, m, KingsField2.MulMatrix0, rec, mat, lit);
            Call(c, m, KingsField2.SetLightMatrix, lit);

            int k = Math.Clamp(half * 0x1000 / s.Radius, 1, 0x7FFF);
            m.WriteU32(scale, (uint)k);
            m.WriteU32(scale + 4, (uint)k);
            m.WriteU32(scale + 8, (uint)k);
            Call(c, m, KingsField2.ScaleMatrix, mat, scale);

            // Turn about the model's middle: the translation less where the matrix puts it.
            var (ox, oy, oz) = Apply(m, mat, s.CentreX, s.CentreY, s.CentreZ);
            m.WriteU32(mat + 0x14, (uint)(x - ox));
            m.WriteU32(mat + 0x18, (uint)(y - oy));
            m.WriteU32(mat + 0x1C, (uint)(z - oz));
            Call(c, m, KingsField2.SetRotMatrix, mat);
            Call(c, m, KingsField2.SetTransMatrix, mat);

            m.WriteU32(CurrentModel, tmd);
            Call(c, m, KingsField2.func_8002E1F0, 0);
            uint count = m.ReadU32(tmd + TmdHeader + 4);
            Call(c, m, KingsField2.func_8002E910, count);
            Call(c, m, KingsField2.func_8002F214, 0, 0);
        }
        finally
        {
            c.Restore(snap);
            m.WriteU32(CurrentModel, savedModel);
            m.WriteU32(VertexBase, savedVerts);
        }
    }

    /// <summary>A MATRIX's 3x3 (s16, 4.12) times a vector.</summary>
    static (int X, int Y, int Z) Apply(IMemory m, uint mat, int x, int y, int z)
    {
        int E(int r, int col) => (short)m.ReadU16(mat + (uint)(r * 6 + col * 2));
        return ((E(0, 0) * x + E(0, 1) * y + E(0, 2) * z) >> 12,
                (E(1, 0) * x + E(1, 1) * y + E(1, 2) * z) >> 12,
                (E(2, 0) * x + E(2, 1) * y + E(2, 2) * z) >> 12);
    }

    static uint Call(CpuContext c, IMemory m, Action<CpuContext, IMemory> f, uint a0 = 0, uint a1 = 0, uint a2 = 0, uint a3 = 0)
    {
        c.A0 = a0; c.A1 = a1; c.A2 = a2; c.A3 = a3;
        c.RA = FakeRa;
        f(c, m);
        return c.V0;
    }
}
