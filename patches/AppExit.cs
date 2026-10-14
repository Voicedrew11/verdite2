namespace Kf2;

/// <summary>
/// Runs what a mod must do before the process leaves, whether the port's main
/// returned or threw. A mod subscribes to <see cref="Leaving"/>; the port calls
/// <see cref="Run"/> from the end of Program.cs's main, in a finally.
///
/// <c>AppDomain.ProcessExit</c> is not enough for this: a run that ends by throwing
/// is not guaranteed it, and the state a mod keeps (the map's fog of war, saved
/// between sessions) is worth more than the few lines this costs.
/// </summary>
public static class AppExit
{
    public static event Action? Leaving;

    public static void Run()
    {
        var handlers = Leaving;
        Leaving = null;
        try
        {
            handlers?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[KF2] leaving: a mod's exit handler threw: {ex.Message}");
        }
    }
}
