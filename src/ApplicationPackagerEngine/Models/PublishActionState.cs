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
    public const string NotInUse = "not in use. Select it under Systems in use in Settings.";

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
            : string.IsNullOrWhiteSpace(readiness.SiteCode) ? "Set the site code in Settings, ConfigMgr Site Settings."
            : "";

        var intuneReady = systems.Intune && readiness.IntuneCredentialsSet;
        var intuneReason = systems.Intune
            ? "set Tenant ID, Client ID and Client Secret in Settings, Intune."
            : "Intune is " + NotInUse;

        var wsusReady = systems.Wsus && readiness.WsusServerSet;
        var wsusReason = systems.Wsus
            ? "Set the WSUS server in Settings, WSUS Publishing."
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
    /// The reason a publish run to a target cannot start, or null. The site
    /// gates apply only to a ConfigMgr run; Intune needs its credentials and
    /// WSUS its server.
    /// </summary>
    public static string? RunGate(string target, PublishReadiness readiness)
    {
        if (target == PublishRunTargets.IntuneOnly)
            return readiness.IntuneCredentialsSet
                ? null
                : "Publish requires Intune credentials. Open Settings, Intune, to configure Tenant ID, Client ID, and Client Secret.";
        if (target == PublishRunTargets.WsusOnly)
            return readiness.WsusServerSet
                ? null
                : "Publish requires a WSUS server. Open Settings, WSUS Publishing, to configure it.";
        if (!readiness.ConsoleFound)
            return "Publish to ConfigMgr requires the ConfigMgr Console. Not detected on this workstation.";
        if (string.IsNullOrWhiteSpace(readiness.SiteCode))
            return "SiteCode is required. Open Settings to configure.";
        if (string.IsNullOrWhiteSpace(readiness.FileShareRoot))
            return "File Share Root is required. Open Settings to configure.";
        return null;
    }
}
