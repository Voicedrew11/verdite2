using RecompOne.Runtime.Memory;

namespace Kf2.Shortcuts;

/// <summary>
/// Shortcuts 2-5 beside each card save: shortcuts_slotN.txt in the working directory,
/// which is the data directory the cards are in (the launcher sets it). One line per
/// shortcut 2..5, "spell,item", -1 for none: the mod's own format and names, so a
/// player's shortcuts from the mod carry over. Game thread.
/// </summary>
static class CardFiles
{
    const uint SaveSlot = 0x8006E5D4;                        // u8 1..3
    const int SpellCount = 20, ItemCount = 120;

    /// <summary>Where the shortcuts last came from, for the settings page.</summary>
    public static volatile string LastFile = "";

    static string PathOf(int slot) => Path.GetFullPath($"shortcuts_slot{slot}.txt");

    static string HandsPathOf(int slot) => Path.GetFullPath($"hands_slot{slot}.txt");

    /// <summary>What the hands keep and the current hotkey, beside card slot
    /// <paramref name="slot"/>: one line, "rightOut,rightAway,leftOut,leftAway,hotkey",
    /// -1 for none. A file of the port's own, so the mod's files keep the mod's format.
    /// Both hands as new, hotkey 1, when there is no file.</summary>
    public static (Hands.Hand Right, Hands.Hand Left, int Hotkey) ReadHands(int slot)
    {
        var right = new Hands.Hand { Out = 0, Away = Shortcuts.None };
        var left = new Hands.Hand { Out = 0, Away = Shortcuts.None };
        int hotkey = 0;
        string path = HandsPathOf(slot);
        try
        {
            if (!File.Exists(path)) return (right, left, hotkey);
            var f = File.ReadAllText(path).Trim().Split(',');
            if (f.Length < 5) return (right, left, hotkey);
            var n = new int[5];
            for (int i = 0; i < 5; i++)
                if (!int.TryParse(f[i], out n[i])) return (right, left, hotkey);
            right = new Hands.Hand { Out = n[0] == 1 ? 1 : 0, Away = n[1] >= 0 && n[1] < ItemCount ? (byte)n[1] : Shortcuts.None };
            left = new Hands.Hand { Out = n[2] == 1 ? 1 : 0, Away = n[3] >= 0 && n[3] < SpellCount ? (byte)n[3] : Shortcuts.None };
            hotkey = n[4] >= 0 && n[4] < Shortcuts.Count ? n[4] : 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] shortcuts: reading {path}: {e.Message}");
        }
        return (right, left, hotkey);
    }

    /// <summary>Writes the hands for the card slot just saved to.</summary>
    public static void WriteHands(IMemory m, Hands.Hand right, Hands.Hand left, int hotkey)
    {
        int slot = m.ReadU8(SaveSlot);
        if (slot < 1 || slot > 3) return;
        string path = HandsPathOf(slot);
        try
        {
            File.WriteAllText(path, $"{right.Out},{Number(right.Away)},{left.Out},{Number(left.Away)},{hotkey}\n");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] shortcuts: writing {path}: {e.Message}");
        }
    }

    /// <summary>The shortcuts kept with card slot <paramref name="slot"/>; all empty when
    /// there is no file.</summary>
    public static Shortcuts.Slot[] Read(int slot)
    {
        var slots = new Shortcuts.Slot[Shortcuts.Count];
        for (int i = 0; i < slots.Length; i++) slots[i] = new Shortcuts.Slot { Spell = Shortcuts.None, Item = Shortcuts.None };

        string path = PathOf(slot);
        try
        {
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                for (int i = 1; i < Shortcuts.Count && i - 1 < lines.Length; i++)
                {
                    var f = lines[i - 1].Split(',');
                    if (f.Length < 2 || !int.TryParse(f[0], out int spell) || !int.TryParse(f[1], out int item)) continue;
                    slots[i] = new Shortcuts.Slot
                    {
                        Spell = spell >= 0 && spell < SpellCount ? (byte)spell : Shortcuts.None,
                        Item = item >= 0 && item < ItemCount ? (byte)item : Shortcuts.None,
                    };
                }
            }
            LastFile = $"slot {slot}";
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] shortcuts: reading {path}: {e.Message}");
        }
        return slots;
    }

    /// <summary>Writes shortcuts 2..5 for the card slot just saved to.</summary>
    public static void Write(IMemory m, Shortcuts.Slot[] slots)
    {
        int slot = m.ReadU8(SaveSlot);
        if (slot < 1 || slot > 3) return;
        string path = PathOf(slot);
        try
        {
            var lines = new string[Shortcuts.Count - 1];
            for (int i = 1; i < Shortcuts.Count; i++)
                lines[i - 1] = $"{Number(slots[i].Spell)},{Number(slots[i].Item)}";
            File.WriteAllLines(path, lines);
            LastFile = $"slot {slot}";
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[KF2] shortcuts: writing {path}: {e.Message}");
        }
    }

    static int Number(byte id) => id == Shortcuts.None ? -1 : id;
}
