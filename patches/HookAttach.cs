using System.Reflection;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// The two things every patch in this directory has to get right about attaching,
/// in one place: *retry a pass that threw*, and *claim only what actually
/// installed*.
///
/// ## Why a latch set before the work is a session-long outage
///
/// Every patch here attaches from an <see cref="OverlayLoadedEvent"/> listener,
/// because <see cref="SymbolRegistry"/> is only readable once the dispatcher's
/// overlay tables are registered. <c>Event.Dispatch</c> wraps every listener in a
/// try/catch that writes **one line to stderr and continues**, so anything thrown
/// inside an attach pass -- a renamed method behind a null-forgiving
/// <c>GetMethod(...)!</c>, a <c>SymbolRegistry</c> failure, a detour MonoMod will
/// not install -- disappears. Seven copies of this shape latched
/// <c>attached = true</c> *before* calling <c>Attach()</c>, so the throw left the
/// patch permanently half-installed and never retried, with no summary line
/// printed either way to say so. <see cref="OnOverlayLoad"/> latches on the pass's
/// own success instead, and a session loads an area module every time the player
/// walks through a door, so the retry costs nothing.
///
/// ## Why a registration is not a hook
///
/// <c>HookManager.AddPre</c>/<c>AddPost</c> only append a delegate to a dictionary
/// and return <c>true</c> unless the *signature* is wrong. The detour is created
/// later, in <c>HookManager.Commit()</c>, and since
/// <c>patches/recompone/0027</c> that catches per function and prints
/// <c>[Mods] could not hook ...</c> rather than throwing -- which is what makes a
/// partial install possible at all. So counting <c>Add*</c> return values counts
/// what was *queued*: a patch could print <c>boundary 3/3 DrawOTag</c> while no
/// boundary existed, latch itself done, and never try again.
///
/// <see cref="Installed"/> reads the answer back from <c>HookManager</c> after the
/// commit (<c>patches/recompone/0028</c>), so a claim is a claim about a detour
/// that exists. **A pre and a post on the same function share one detour**, so
/// those two land or fail together; the case that needs the read-back is a patch
/// whose sites are different functions -- <see cref="AnimSmoothing"/>'s five,
/// <see cref="FramePacing"/>'s per-overlay roles -- plus every summary line.
/// </summary>
static class HookAttach
{
    /// <summary>Overlay loads a failed or partial attach is retried on. A miss is
    /// nearly always a miss for good -- every overlay is registered before the
    /// first load, so a role that will not resolve now will not resolve later --
    /// so this only has to cover a transient, and has to stop the console filling
    /// up. Same number <see cref="FramePacing"/> arrived at.</summary>
    public const int MaxTries = 3;

    /// <summary>
    /// Run <paramref name="attach"/> on overlay loads until it reports success or
    /// <see cref="MaxTries"/> passes have been spent. The pass returns true only
    /// when it has everything it wants; anything less is retried, so a pass must
    /// be safe to re-enter and must add only what it is still missing rather than
    /// a second copy of the lot. <paramref name="hint"/> is appended to the
    /// give-up line, for a patch that has somewhere to point at.
    /// </summary>
    public static void OnOverlayLoad(string label, Func<bool> attach, string? hint = null, int maxTries = MaxTries)
    {
        bool done = false;
        int tries = 0;
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            if (done || tries >= maxTries) return;
            tries++;
            try
            {
                done = attach();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[KF2] {label}: attach failed -- {e}");
            }
            if (!done && tries >= maxTries)
                Console.Error.WriteLine($"[KF2] {label}: giving up on the rest; what is hooked is hooked." +
                                        (hint == null ? "" : $" {hint}"));
        });
    }

    /// <summary>
    /// Did a detour actually get installed on this function? Only meaningful after
    /// <c>HookManager.Commit()</c>; before it, everything reads false.
    /// </summary>
    public static bool Installed(MethodInfo? target)
        => target != null && HookManager.IsCommitted(target);
}
