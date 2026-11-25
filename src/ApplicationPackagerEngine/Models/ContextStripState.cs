namespace ApplicationPackagerEngine.Models;

/// <summary>The state of one system in the context strip at the top of the window.</summary>
public enum SystemState
{
    /// <summary>Not in Settings, Systems in Use.</summary>
    Off,
    /// <summary>In use, and a setting it needs is missing. The reason names it.</summary>
    NotConfigured,
    /// <summary>In use and configured. Nothing has tested the connection.</summary>
    Ready,
    /// <summary>The last connection test of ConfigMgr succeeded. Only ConfigMgr has a test.</summary>
    Connected,
    /// <summary>The last connection test of ConfigMgr failed.</summary>
    Failed,
    /// <summary>A connection test is running.</summary>
    Testing
}

/// <summary>The result of the last ConfigMgr connection test. Intune and WSUS have none.</summary>
public enum ConfigMgrConnectionState
{
    Unknown,
    Testing,
    Connected,
    Failed
}

/// <summary>One system of the strip: its state, the word that states it and the reason when it is not configured.</summary>
public sealed record SystemStatus(string Name, SystemState State, string Reason)
{
    /// <summary>The word of the state. Intune and WSUS never read "connected": nothing tests them.</summary>
    public string Word => State switch
    {
        SystemState.Off => "off",
        SystemState.NotConfigured => "not configured",
        SystemState.Ready => "enabled",
        SystemState.Connected => "connected",
        SystemState.Failed => "connection failed",
        _ => "testing"
    };

    /// <summary>"ConfigMgr connected", "Intune off", "WSUS not configured".</summary>
    public string Text => $"{Name} {Word}";
}

/// <summary>
/// What the context strip shows (overhaul V1, phase 2): the site, the SMS Provider host and one state for each of ConfigMgr,
/// Intune and WSUS. A pure function of the settings and the last ConfigMgr connection test, so the text is tested without a window.
/// </summary>
public sealed record ContextStripState(string SiteCode, string Host, IReadOnlyList<SystemStatus> Systems)
{
    /// <summary>"Site MCM" or "Site not set".</summary>
    public string SiteText => SiteCode.Length == 0 ? "Site not set" : "Site " + SiteCode;

    /// <summary>The host, or "server not set" when the site has no SMS Provider host.</summary>
    public string HostText => Host.Length == 0 ? "server not set" : Host;

    /// <summary>The name read by a screen reader: "Site MCM, cm01.contoso.com, ConfigMgr connected, Intune off, WSUS off".</summary>
    public string AutomationName => string.Join(", ", new[] { SiteText, HostText }.Concat(Systems.Select(s => s.Text)));

    public SystemStatus For(string system) =>
        Systems.First(s => string.Equals(s.Name, system, StringComparison.OrdinalIgnoreCase));

    public static ContextStripState From(PublishReadiness readiness, ConfigMgrConnectionState connection, string? siteCode, string? host)
    {
        SystemStatus Of(string name, bool inUse)
        {
            if (!inUse) return new(name, SystemState.Off, "");
            if (PublishActionState.DestinationGate(name, readiness) is { } reason) return new(name, SystemState.NotConfigured, reason);
            if (name != OneClickDestinations.ConfigMgr) return new(name, SystemState.Ready, "");
            return connection switch
            {
                ConfigMgrConnectionState.Connected => new(name, SystemState.Connected, ""),
                ConfigMgrConnectionState.Failed => new(name, SystemState.Failed, ""),
                ConfigMgrConnectionState.Testing => new(name, SystemState.Testing, ""),
                _ => new(name, SystemState.Ready, "")
            };
        }

        var systems = readiness.Systems;
        return new ContextStripState(siteCode?.Trim() ?? "", host?.Trim() ?? "",
        [
            Of(OneClickDestinations.ConfigMgr, systems.ConfigMgr),
            Of(OneClickDestinations.Intune, systems.Intune),
            Of(OneClickDestinations.Wsus, systems.Wsus)
        ]);
    }
}
