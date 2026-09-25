namespace RecompOne.Runtime.Hle;

/// <summary>Temporary: prim shader experiments for a driver this port cannot be run
/// on here (the NVIDIA texture loss). Read by GlCore every batch, set from
/// KF2_GLDIAG at boot and from the port's Driver diagnostics page.</summary>
public static class GlDiag
{
    /// <summary>0 normal, 1 clip W forced to 1, 2 W over 65536, 3 a clip Z that is not 0.</summary>
    public static int WMode;

    /// <summary>0 normal, 1 texture coordinates as colour, 2 the plain texel (no filter, no fluid blend).</summary>
    public static int View;

    static GlDiag()
    {
        var env = Environment.GetEnvironmentVariable("KF2_GLDIAG");
        if (string.IsNullOrWhiteSpace(env)) return;
        foreach (var m in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            switch (m.ToLowerInvariant())
            {
                case "wone": WMode = 1; break;
                case "wscale": WMode = 2; break;
                case "wz": WMode = 3; break;
                case "uv": View = 1; break;
                case "point": View = 2; break;
                default: Console.WriteLine($"[GL] diag: unknown mode '{m}' (wone, wscale, wz, uv, point)"); break;
            }
        Console.WriteLine($"[GL] diag: w mode {WMode}, view {View}");
    }
}
