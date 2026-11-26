namespace ApplicationPackagerEngine.Models;

/// <summary>The settings the publish buttons depend on.</summary>
public sealed record PublishReadiness(
    SystemsInUse Systems,
    bool ConsoleFound,
    string? SiteCode,
    string? FileShareRoot,
    string? IntuneTenantId,
    string? IntuneClientId,
    bool HasIntuneClientSecret,
    string? WsusServerName)
{
    public bool IntuneCredentialsSet =>
        !string.IsNullOrWhiteSpace(IntuneTenantId) && !string.IsNullOrWhiteSpace(IntuneClientId) && HasIntuneClientSecret;

    public bool WsusServerSet => !string.IsNullOrWhiteSpace(WsusServerName);
}

/// <summary>
/// Enablement and tooltip of every publish action. A button whose system is
/// not ready is disabled, not hidden, and its tooltip names the missing
/// setting. The decision touches no WPF type, so it is exercised headlessly.
/// </summary>
public sealed record PublishActionState
{
    public const string NotInUse = "not in use. Select it under Systems in Use in Settings.";

    public required bool CheckMecmEnabled { get; init; }
    public required string CheckMecmToolTip { get; init; }
    public required bool MecmEnabled { get; init; }
    public required string MecmToolTip { get; init; }
    public required bool IntuneEnabled { get; init; }
    public required string IntuneToolTip { get; init; }
    public required bool WsusEnabled { get; init; }
    public required string WsusToolTip { get; init; }
    public required bool WsusUpdatesEnabled { get; init; }
    public required string WsusUpdatesToolTip { get; init; }

    /// <summary>True when a launch must not open a provider connection for a version query.</summary>
    public required bool SkipMecmPreflight { get; init; }

    public static PublishActionState For(PublishReadiness readiness)
    {
        var systems = readiness.Systems;
        var mecmReason =
            !systems.ConfigMgr ? "ConfigMgr is " + NotInUse
            : !readiness.ConsoleFound ? "Needs the ConfigMgr console on this computer."
            : string.IsNullOrWhiteSpace(readiness.SiteCode) ? "Set the site code in Settings, ConfigMgr."
            : "";

        var intuneReady = systems.Intune && readiness.IntuneCredentialsSet;
        var intuneReason = systems.Intune
            ? "set Tenant ID, Client ID and Client Secret in Settings, Intune."
            : "Intune is " + NotInUse;

        var wsusReady = systems.Wsus && readiness.WsusServerSet;
        var wsusReason = systems.Wsus
            ? "Set the WSUS server in Settings, WSUS."
            : "WSUS is " + NotInUse;

        var mecmReady = mecmReason.Length == 0;
        return new PublishActionState
        {
            CheckMecmEnabled = mecmReady,
            CheckMecmToolTip = mecmReady ? "Query ConfigMgr for the currently deployed version of each checked app" : "Check ConfigMgr: " + mecmReason,
            MecmEnabled = mecmReady,
            MecmToolTip = mecmReady ? "Build a ConfigMgr Application and Deployment Type for every checked app" : "Publish to ConfigMgr: " + mecmReason,
            IntuneEnabled = intuneReady,
            IntuneToolTip = intuneReady ? "Stage every checked app, build its .intunewin, and publish it to Intune" : "Publish to Intune: " + intuneReason,
            WsusEnabled = wsusReady,
            WsusToolTip = wsusReady
                ? "Stage every checked app and publish its installer to WSUS as an update for computers that have an older version"
                : "Publish to WSUS: " + wsusReason,
            WsusUpdatesEnabled = wsusReady,
            WsusUpdatesToolTip = wsusReady
                ? "List the updates AppPackager published to WSUS; approve, decline, expire or remove them, or import from the Microsoft Update Catalog"
                : "WSUS Updates: " + wsusReason,
            SkipMecmPreflight = !systems.ConfigMgr
        };
    }

    /// <summary>
    /// The reason one destination cannot take a publish, or null when it is ready. The site
    /// gates apply to ConfigMgr; Intune needs its credentials and WSUS its server.
    /// </summary>
    public static string? DestinationGate(string destination, PublishReadiness readiness)
    {
        switch (OneClickDestinations.Resolve(destination))
        {
            case OneClickDestinations.Intune:
                return readiness.IntuneCredentialsSet
                    ? null
                    : "Publish requires Intune credentials. Open Settings, Intune, to configure Tenant ID, Client ID, and Client Secret.";
            case OneClickDestinations.Wsus:
                return readiness.WsusServerSet
                    ? null
                    : "Publish requires a WSUS server. Open Settings, WSUS, to configure it.";
            default:
                if (!readiness.ConsoleFound)
                    return "The ConfigMgr Console is required. It is not detected on this workstation.";
                if (string.IsNullOrWhiteSpace(readiness.SiteCode))
                    return "SiteCode is required. Open Settings to configure.";
                if (string.IsNullOrWhiteSpace(readiness.FileShareRoot))
                    return "File Share Root is required. Open Settings to configure.";
                return null;
        }
    }

    /// <summary>The destinations of a target that are not ready, each with its reason, in the order ConfigMgr, Intune, WSUS.</summary>
    public static IReadOnlyList<(string Destination, string Reason)> BlockedDestinations(string target, PublishReadiness readiness)
    {
        // An unknown or empty target keeps the earlier reading: the ConfigMgr gates.
        var set = DestinationSet.TryFromTarget(target) is { Any: true } known ? known : new DestinationSet(true, false, false);
        var blocked = new List<(string, string)>();
        foreach (var destination in new[] { OneClickDestinations.ConfigMgr, OneClickDestinations.Intune, OneClickDestinations.Wsus })
            if (set.Has(destination) && DestinationGate(destination, readiness) is { } reason)
                blocked.Add((destination, reason));
        return blocked;
    }

    /// <summary>
    /// The reason a publish run to a target cannot start, or null: the first destination of
    /// the target that is not ready. A target with several destinations is checked for each.
    /// </summary>
    public static string? RunGate(string target, PublishReadiness readiness) =>
        BlockedDestinations(target, readiness).Select(b => b.Reason).FirstOrDefault();
}
