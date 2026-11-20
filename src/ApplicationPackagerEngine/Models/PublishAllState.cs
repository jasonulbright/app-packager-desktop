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
}
