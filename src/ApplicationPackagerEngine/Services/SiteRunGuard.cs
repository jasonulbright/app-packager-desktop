using System.Runtime.CompilerServices;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Tells whether a run is active, so that a switch of site is refused while one is (Q-112d). A view model reports its busy state
/// with <see cref="Set"/> (an owner and a reason, or null when it is idle). The owners are held weakly: a closed window never keeps a site locked.
/// </summary>
public static class SiteRunGuard
{
    private static readonly ConditionalWeakTable<object, string> Active = new();

    /// <summary>Reports that <paramref name="owner"/> is running (a reason) or idle (null).</summary>
    public static void Set(object owner, string? reason)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (Active)
        {
            Active.Remove(owner);
            if (reason is not null) Active.Add(owner, reason);
        }
    }

    /// <summary>The reason of one active run, or null when none is active.</summary>
    public static string? ActiveRun()
    {
        lock (Active)
        {
            foreach (var pair in Active) return pair.Value;
        }
        return null;
    }
}
