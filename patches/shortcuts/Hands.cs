using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Shortcuts;

/// <summary>
/// The two hands of the Souls layout: the right holds two weapons, the left two attack
/// spells, and the d-pad swaps each between its two. What a hand has out is always the
/// game's own equipment -- the weapon byte 0x801994AF, set through the game's own equip
/// <c>func_80026210</c> (record, model, stats), and the attack spell 0x801994AB -- so the
/// swing, the cast, MP and the arm are the game's. The port keeps only what each hand
/// has put away, and which of its two slots is out. See "Shortcuts" in
/// docs/PATCHES_AND_MODS.md.
///
/// Threads: the swaps and assignment run on the game thread, in player control's
/// pre-hook and the Equipment page; the HUD reads on the game thread; the settings page
/// reads <see cref="Describe"/> under <see cref="Shortcuts.Sync"/>.
/// </summary>
static class Hands
{
    // ---- the game's ----
    internal const uint Weapon = 0x801994AF;                 // u8, 0xFF none
    internal const uint AttackSpell = 0x801994AB;            // u8, 0xFF none
    const uint SwingClock = 0x801994A4;                      // s16, -1 idle
    internal const uint Inventory = 0x8009B52C;              // u8 count per item id
    const uint FakeRa = 0x8001FFF4;
    const int WeaponIds = 18;                                // 0-17: swords, axes, blades, bow, arbalest
    const int SpellIds = 20;

    /// <summary>A hand: two slots, one of them out.</summary>
    internal struct Hand
    {
        /// <summary>Which slot (0 or 1) holds what the game has equipped.</summary>
        public int Out;
        /// <summary>What the other slot holds, 0xFF none.</summary>
        public byte Away;
    }

    static Hand _right = new() { Out = 0, Away = Shortcuts.None };
    static Hand _left = new() { Out = 0, Away = Shortcuts.None };

    /// <summary>A weapon swap asked for while a swing was running; done once it ends.</summary>
    static bool _weaponPending;

    /// <summary>The frame a swap last happened, for the HUD's name.</summary>
    internal static long SwappedAt { get; private set; } = -1;
    internal static bool SwappedWeapon { get; private set; }

    internal static Hand Right { get { lock (Shortcuts.Sync) return _right; } }
    internal static Hand Left { get { lock (Shortcuts.Sync) return _left; } }

    /// <summary>The id in slot <paramref name="slot"/> of a hand, given what is out.</summary>
    internal static byte InSlot(Hand hand, byte outId, int slot) => slot == hand.Out ? outId : hand.Away;

    internal static void Reset()
    {
        lock (Shortcuts.Sync)
        {
            _right = new Hand { Out = 0, Away = Shortcuts.None };
            _left = new Hand { Out = 0, Away = Shortcuts.None };
        }
        _weaponPending = false;
        SwappedAt = -1;
    }

    internal static void Restore(Hand right, Hand left)
    {
        lock (Shortcuts.Sync)
        {
            _right = right;
            _left = left;
        }
        _weaponPending = false;
    }

    internal static bool AnyAway()
    {
        lock (Shortcuts.Sync) return _right.Away != Shortcuts.None || _left.Away != Shortcuts.None || _right.Out != 0 || _left.Out != 0;
    }

    // ---- once a control tick; game thread ----

    /// <summary>Drops a weapon put away that is no longer held or is the one out, and
    /// finishes a swap a swing held back.</summary>
    internal static void Tick(CpuContext c, IMemory m, bool probe)
    {
        lock (Shortcuts.Sync)
        {
            byte away = _right.Away;
            if (away != Shortcuts.None && (away >= WeaponIds || m.ReadU8(Inventory + away) == 0 || away == m.ReadU8(Weapon)))
                _right.Away = Shortcuts.None;
            if (_left.Away != Shortcuts.None && (_left.Away >= SpellIds || _left.Away == m.ReadU8(AttackSpell)))
                _left.Away = Shortcuts.None;
        }
        if (_weaponPending && Idle(m)) SwapWeapon(c, m, probe);
    }

    static bool Idle(IMemory m) => (short)m.ReadU16(SwingClock) == -1;

    /// <summary>The right hand's other weapon out, through the game's own equip. A swing
    /// or a drawn bow holds it back until it ends.</summary>
    internal static void SwapWeapon(CpuContext c, IMemory m, bool probe)
    {
        byte away;
        lock (Shortcuts.Sync) away = _right.Away;
        _weaponPending = false;
        if (away == Shortcuts.None)
        {
            if (probe) Console.WriteLine("[KF2] shortcuts: weapon swap, nothing in the other slot");
            return;
        }
        if (!Idle(m))
        {
            _weaponPending = true;
            return;
        }

        byte was = m.ReadU8(Weapon);
        Equip(c, m, away);
        if (m.ReadU8(Weapon) != away)
        {
            Console.Error.WriteLine($"[KF2] shortcuts: equipping weapon 0x{away:X2} left 0x{m.ReadU8(Weapon):X2}");
            return;
        }
        lock (Shortcuts.Sync)
        {
            _right.Away = was;
            _right.Out = 1 - _right.Out;
        }
        Swapped(true);
        if (probe) Console.WriteLine($"[KF2] shortcuts: weapon 0x{was:X2} -> 0x{away:X2}, slot {_right.Out + 1} out");
    }

    /// <summary>The left hand's other spell out: the attack spell byte, as the game's
    /// attack magic page sets it (0x80025F70).</summary>
    internal static void SwapSpell(IMemory m, bool probe)
    {
        byte away, was = m.ReadU8(AttackSpell);
        lock (Shortcuts.Sync)
        {
            away = _left.Away;
            if (away == Shortcuts.None) { if (probe) Console.WriteLine("[KF2] shortcuts: spell swap, nothing in the other slot"); return; }
            _left.Away = was;
            _left.Out = 1 - _left.Out;
        }
        m.WriteU8(AttackSpell, away);
        Swapped(false);
        if (probe) Console.WriteLine($"[KF2] shortcuts: spell 0x{was:X2} -> 0x{away:X2}, slot {_left.Out + 1} out");
    }

    static void Swapped(bool weapon)
    {
        SwappedAt = Environment.TickCount64;
        SwappedWeapon = weapon;
    }

    /// <summary>func_80026210(id): the weapon byte, its record, its model in the arm and
    /// the stats, from a scratch return address.</summary>
    static void Equip(CpuContext c, IMemory m, byte id)
    {
        var snap = c.Snapshot();
        try
        {
            c.A0 = id;
            c.RA = FakeRa;
            KingsField2.func_80026210(c, m);
        }
        finally { c.Restore(snap); }
    }

    // ---- assigning, from the Equipment page; game thread ----

    /// <summary>A weapon (or 0xFF, none) picked for slot <paramref name="slot"/>. True when
    /// the game's own equip should run: the slot is the one out.</summary>
    internal static bool AssignWeapon(IMemory m, int slot, byte id) => Assign(ref _right, m.ReadU8(Weapon), slot, id);

    /// <summary>An attack spell (or 0xFF) for slot <paramref name="slot"/>; as <see cref="AssignWeapon"/>.</summary>
    internal static bool AssignSpell(IMemory m, int slot, byte id) => Assign(ref _left, m.ReadU8(AttackSpell), slot, id);

    static bool Assign(ref Hand hand, byte outId, int slot, byte id)
    {
        lock (Shortcuts.Sync)
        {
            if (slot == hand.Out)
            {
                if (id != Shortcuts.None && id == hand.Away) hand.Away = outId;   // the two trade places
                return true;
            }
            if (id != Shortcuts.None && id == outId)
            {
                // What is out moves to the slot asked for; the other slot keeps what was away.
                hand.Out = slot;
                return false;
            }
            hand.Away = id;
            return false;
        }
    }

    /// <summary>"weapon 0x05 out in slot 1, 0x0A away"; host thread.</summary>
    internal static string Describe(Hand hand, string what) =>
        $"{what} slot {hand.Out + 1} out, other slot {(hand.Away == Shortcuts.None ? "empty" : $"0x{hand.Away:X2}")}";
}
