namespace RecompOne.Runtime.Diagnostics;

/// <summary>Which recompiled functions ran: one byte per function, indexed by the
/// number the recompiler gave it under "coverage": true (its coverage.tsv names them).</summary>
public static class Coverage
{
    public const int Capacity = 65536;
    public static bool On;
    public static readonly byte[] Hits = new byte[Capacity];

    /// <summary>The indices hit so far, ascending.</summary>
    public static List<int> HitIndices()
    {
        var hit = new List<int>();
        for (var i = 0; i < Capacity; i++)
            if (Hits[i] != 0) hit.Add(i);
        return hit;
    }
}
