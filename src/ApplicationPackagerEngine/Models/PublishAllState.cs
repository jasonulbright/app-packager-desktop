namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The Publish All button: it publishes to every system that Settings, Systems in Use turns on, in one run. It is off when no system is on.
/// </summary>
public static class PublishAllState
{
    /// <summary>The state of the button, its tooltip, and the publish target that one run uses.</summary>
    public static (bool Enabled, string ToolTip, string Target) For(SystemsInUse? systems)
    {
        var set = new DestinationSet(systems?.ConfigMgr ?? false, systems?.Wsus ?? false, systems?.Intune ?? false);
        if (!set.ConfigMgr && !set.Wsus && !set.Intune)
            return (false, "Publish All: no system is in use. Turn on ConfigMgr, Intune or WSUS in Settings, Systems in Use.", "");
        return (true, "Publish the checked applications to " + set.Describe() + " in one run. The systems are the ones in Settings, Systems in Use.", set.ToTargetName());
    }

    /// <summary>
    /// The state with readiness: a system that is in use and not ready blocks the button, and the tooltip names
    /// each blocked system with its reason. The run never publishes to a smaller set than Settings names.
    /// </summary>
    public static (bool Enabled, string ToolTip, string Target) For(PublishReadiness readiness)
    {
        var state = For(readiness.Systems);
        if (!state.Enabled) return state;
        var blocked = PublishActionState.BlockedDestinations(state.Target, readiness);
        if (blocked.Count == 0) return state;
        return (false, "Publish All: " + string.Join(" ", blocked.Select(b => b.Destination + ": " + b.Reason)), state.Target);
    }

    /// <summary>
    /// The scope line of the Packages page: the systems that Publish All reaches and where that comes from. One function for the page and, later,
    /// the review window, so the two never disagree.
    /// </summary>
    public static string ScopeText(SystemsInUse? systems)
    {
        var set = new DestinationSet(systems?.ConfigMgr ?? false, systems?.Wsus ?? false, systems?.Intune ?? false);
        return set.Any
            ? "Publish All scope: " + set.Describe() + " (Settings, Systems in Use)"
            : "Publish All scope: no system in use (Settings, Systems in Use)";
    }
}
