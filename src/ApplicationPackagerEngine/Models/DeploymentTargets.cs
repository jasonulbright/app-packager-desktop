namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The stored deployment target: the nearest single target name for the One Click
/// default destinations, which a grid Stage records in its run snapshot and hands to a
/// PS1 packager. The publish buttons name their own destination and never read it.
/// </summary>
public static class DeploymentTargets
{
    public const string Mecm = "MECM";
    public const string MecmAndIntune = "MECMAndIntune";
    public const string IntuneOnly = "IntuneOnly";
    public const string MecmAndWsus = "MECMAndWSUS";
    public const string WsusOnly = "WSUSOnly";

    /// <summary>Keeps every known target name; an unknown or absent stored value is MECM only.</summary>
    public static string Normalize(string? value) => value switch
    {
        MecmAndIntune => MecmAndIntune,
        IntuneOnly => IntuneOnly,
        MecmAndWsus => MecmAndWsus,
        WsusOnly => WsusOnly,
        _ => Mecm
    };

    public static bool PublishesToIntune(string? value) => Normalize(value) is MecmAndIntune or IntuneOnly;

    public static bool PublishesToWsus(string? value) => Normalize(value) is MecmAndWsus or WsusOnly;

    /// <summary>True for the site-free targets: no site connection, no share copy, no ConfigMgr application.</summary>
    public static bool SkipsMecm(string? value) => Normalize(value) is IntuneOnly or WsusOnly;
}

/// <summary>
/// Label, tooltip, and enablement the main action bar takes from the deployment target.
/// </summary>
public sealed record DeploymentTargetActionState
{
    public required bool CheckMecmEnabled { get; init; }
    public required string CheckMecmToolTip { get; init; }
    public required string PackageLabel { get; init; }
    public required string PackageToolTip { get; init; }

    /// <summary>True when a bulk flow must not open a provider connection for a version query.</summary>
    public required bool SkipMecmPreflight { get; init; }

    /// <summary>
    /// Single source for the action-bar state. WPF-free so the mapping is exercised headlessly;
    /// the view model applies it and is the only writer of those bound properties.
    /// </summary>
    public static DeploymentTargetActionState For(string? deploymentTarget)
    {
        if (DeploymentTargets.SkipsMecm(deploymentTarget))
        {
            return new DeploymentTargetActionState
            {
                CheckMecmEnabled = false,
                CheckMecmToolTip = "Check ConfigMgr needs a ConfigMgr site. The Deployment Target is Intune only — change it in Settings, Intune, to use this.",
                PackageLabel = "Publish",
                PackageToolTip = "Stage each selected app, build the .intunewin, and publish it to Intune",
                SkipMecmPreflight = true
            };
        }

        return new DeploymentTargetActionState
        {
            CheckMecmEnabled = true,
            CheckMecmToolTip = "Query ConfigMgr for the currently deployed version of each selected app",
            PackageLabel = "Package",
            PackageToolTip = "Build ConfigMgr Application + Deployment Type for every selected app",
            SkipMecmPreflight = false
        };
    }
}

/// <summary>
/// Pre-run configuration gate. MECM targets need the site fields; an Intune-only run needs
/// Intune credentials instead, so a console-less workstation can still publish.
/// </summary>
public static class DeploymentTargetGate
{
    /// <summary>Returns the blocking reason, or null when the run may start.</summary>
    public static string? Validate(
        string? deploymentTarget,
        string? siteCode,
        string? fileShareRoot,
        string? tenantId,
        string? clientId,
        bool hasClientSecret)
    {
        var target = DeploymentTargets.Normalize(deploymentTarget);

        if (!DeploymentTargets.SkipsMecm(target))
        {
            if (string.IsNullOrWhiteSpace(siteCode))
                return "Site Code is required. Open Settings.";
            if (string.IsNullOrWhiteSpace(fileShareRoot))
                return "File Share Root is required. Open Settings.";
        }

        if (DeploymentTargets.PublishesToIntune(target))
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || !hasClientSecret)
                return "Intune credentials are required. Open Settings and set Tenant ID, Client ID, and Client Secret.";
        }

        return null;
    }
}
