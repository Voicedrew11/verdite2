using RecompOne.Runtime;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace Kf2;

/// <summary>
/// A drawing pass of the port's own: the game's drawing routines pointed at an
/// ordering table and a primitive arena the port owns, for as long as the pass is
/// open, and everything they move put back when it closes.
///
///     pass.Begin(c, mem, descriptor, table, arena, arenaEnd);
///     try { ... Stage13.DrawScene, CameraBlock.Build, a walk ... }
///     finally { pass.End(c, mem); }
///
/// What is borrowed is the union of what any pass moves, so a pass leaves the frame
/// exactly as it found it whatever it draws: the registers, the GTE, the active
/// primitive descriptor and ordering-table pointer, the model table and vertex base
/// the submitter selects, the fog word the tile walk keeps, and the camera block.
/// <see cref="MenuWorld"/> draws the world behind a menu this way and
/// <see cref="PlanarWalk"/> the world mirrored in water; an editor camera or a
/// shadow pass is the same four lines. See "A pass of the port's own" in
/// docs/PATCHES_AND_MODS.md.
///
/// One instance per caller, reused every frame: it holds the saved state, so a pass
/// cannot be opened inside itself.
/// </summary>
public sealed class ScenePass
{
    /// <summary>Entries in a pass's table: the game's own size.</summary>
    public const uint OtEntries = 0x2000;
    public const uint OtBytes = OtEntries * 4u;

    /// <summary>A descriptor is three words: start, end, the next free byte.</summary>
    public const uint DescriptorBytes = 0xC;

    /// <summary>The frame's primitive descriptor and ordering table, as the drawing
    /// routines find them.</summary>
    public const uint ActiveDescriptor = 0x8017E0A4, OtPointer = 0x8018E0A8;

    /// <summary>The model table and vertex base the submitter selects, and the fog
    /// word <c>func_8002DDDC</c> keeps; all three are read again later in the frame.</summary>
    public const uint ModelTable = 0x8018E19C, VertexBase = 0x8018EAA0, FogWord = 0x80192EA8;

    readonly Gte.State _gte = new();
    readonly byte[] _camera = new byte[CameraBlock.Bytes];
    CpuSnapshot _regs;
    uint _desc, _ot, _models, _verts, _fog;
    bool _open;

    /// <summary>The pass's descriptor, table and arena, as last opened.</summary>
    public uint Descriptor { get; private set; }
    public uint Table { get; private set; }
    public uint Arena { get; private set; }
    public uint ArenaEnd { get; private set; }

    /// <summary>The entry a walk of the table starts at: the last, for a
    /// <c>ClearOTagR</c> table.</summary>
    public uint Head => Table + (OtEntries - 1u) * 4u;

    /// <summary>Bytes of the arena the last pass used, read when it closed.</summary>
    public uint Used { get; private set; }

    /// <summary>Whether the last pass wrote past its arena's end.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>
    /// Save what a pass may move, clear <paramref name="table"/>, and point the frame
    /// at it and at an arena of <c>[arena, arenaEnd)</c> described at
    /// <paramref name="descriptor"/>. The registers and the GTE are saved as they
    /// stand, so a caller that loads a GTE state of its own does so after this.
    /// </summary>
    public void Begin(CpuContext c, PSMemory mem, uint descriptor, uint table, uint arena, uint arenaEnd)
    {
        if (_open) throw new InvalidOperationException("a scene pass is already open");
        _open = true;

        _regs = c.Snapshot();
        Gte.Save(_gte);
        _desc = mem.ReadU32(ActiveDescriptor);
        _ot = mem.ReadU32(OtPointer);
        _models = mem.ReadU32(ModelTable);
        _verts = mem.ReadU32(VertexBase);
        _fog = mem.ReadU32(FogWord);
        for (uint i = 0; i < CameraBlock.Bytes; i++) _camera[i] = mem.ReadU8(CameraBlock.Start + i);

        Descriptor = descriptor;
        Table = table;
        Arena = arena;
        ArenaEnd = arenaEnd;
        ClearTable(mem, table);
        mem.WriteU32(descriptor, arena);
        mem.WriteU32(descriptor + 4u, arenaEnd);
        mem.WriteU32(descriptor + 8u, arena);
        mem.WriteU32(ActiveDescriptor, descriptor);
        mem.WriteU32(OtPointer, table);
    }

    /// <summary>Measure the arena, then put back everything <see cref="Begin"/> saved.</summary>
    public void End(CpuContext c, PSMemory mem)
    {
        if (!_open) return;
        _open = false;

        uint cur = mem.ReadU32(Descriptor + 8u);
        Used = cur - Arena;
        Overflowed = cur > ArenaEnd;

        mem.WriteU32(ActiveDescriptor, _desc);
        mem.WriteU32(OtPointer, _ot);
        mem.WriteU32(ModelTable, _models);
        mem.WriteU32(VertexBase, _verts);
        mem.WriteU32(FogWord, _fog);
        for (uint i = 0; i < CameraBlock.Bytes; i++) mem.WriteU8(CameraBlock.Start + i, _camera[i]);
        Gte.Load(_gte);
        c.Restore(_regs);
    }

    /// <summary>
    /// Link the pass's table in front of another, so one <c>DrawOTag</c> from
    /// <see cref="Head"/> walks this table and then that one: the terminator, entry 0,
    /// is pointed at <paramref name="next"/>. Returns <see cref="Head"/>.
    /// </summary>
    public uint LinkBefore(PSMemory mem, uint next)
    {
        mem.WriteU32(Table, next & 0x00FFFFFFu);
        return Head;
    }

    /// <summary><c>ClearOTagR</c>: each entry links to the one before it, and entry 0
    /// is the terminator, so a walk starts at the last and ends at the first.</summary>
    public static void ClearTable(PSMemory mem, uint table)
    {
        mem.WriteU32(table, 0x00FFFFFFu);
        for (uint i = 1; i < OtEntries; i++)
            mem.WriteU32(table + i * 4u, (table + (i - 1u) * 4u) & 0x00FFFFFFu);
    }
}
