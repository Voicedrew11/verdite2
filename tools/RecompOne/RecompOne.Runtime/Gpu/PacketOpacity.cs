namespace RecompOne.Runtime;

/// <summary>
/// 0126. An opacity a port gives one of the packets it built, so a HUD can fade
/// smoothly: the PlayStation blends only in four fixed modes, and on a textured face
/// only the texels whose STP bit is set, so a face cannot be made 30% visible in the
/// packet itself. The port records the packet's address, its command word and its
/// first vertex word; the GPU takes the record when it draws that packet, checks both
/// words, and the GL backend draws the polygon as <c>src * a + dst * (1 - a)</c> on
/// every texel, whatever the packet's own blend. A record is taken once: a packet
/// redrawn later, or a stale record at an address reused for something else, draws
/// as the game made it. The software rasterizer ignores it.
///
/// Thread: <see cref="Set"/> and <see cref="Clear"/> from the game thread, which is
/// also where the GPU walks the ordering table; a lock keeps a host-thread caller safe.
/// </summary>
public static class PacketOpacity
{
    struct Rec { public uint Cmd, Xy0; public byte Alpha; }

    static readonly Dictionary<uint, Rec> _recs = new();
    static readonly object _sync = new();
    static volatile bool _any;

    /// <summary>Packets drawn at a recorded opacity since boot; a probe's.</summary>
    public static long Drawn;

    /// <summary>A record waits to be drawn.</summary>
    public static bool Any => _any;

    /// <summary>The packet at <paramref name="packet"/> (its tag word), whose command
    /// word and first vertex word are given, drawn at <paramref name="alpha"/>/255.
    /// An alpha of 255 or 0 drops any record there.</summary>
    public static void Set(uint packet, uint cmd, uint xy0, byte alpha)
    {
        uint key = Key(packet + 4);
        lock (_sync)
        {
            if (alpha is 0 or 255) _recs.Remove(key);
            else _recs[key] = new Rec { Cmd = cmd, Xy0 = xy0, Alpha = alpha };
            _any = _recs.Count > 0;
        }
    }

    /// <summary>Drops every record not yet drawn.</summary>
    public static void Clear()
    {
        lock (_sync)
        {
            _recs.Clear();
            _any = false;
        }
    }

    /// <summary>The opacity of the packet whose command word was read from
    /// <paramref name="cmdAddress"/>, or 0 (the game's own) when none is recorded or
    /// the words differ; the record is used up either way.</summary>
    internal static byte Take(uint cmdAddress, uint cmd, uint xy0)
    {
        lock (_sync)
        {
            if (!_recs.Remove(Key(cmdAddress), out var r)) return 0;
            _any = _recs.Count > 0;
            if (r.Cmd != cmd || r.Xy0 != xy0) return 0;
            Drawn++;
            return r.Alpha;
        }
    }

    static uint Key(uint address) => address & 0x1FFFFFFFu;
}
