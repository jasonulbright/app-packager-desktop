namespace ApplicationPackagerEngine.Models;

/// <summary>
/// The stored deployment target: the name of a destination combination for the One Click
/// default destinations, which a grid Stage records in its run snapshot and hands to a
/// PS1 packager. The publish buttons name their own destination and never read it.
/// The three destinations, ConfigMgr, Intune and WSUS, are independent and each optional:
/// every combination has a name, and a name never implies a destination it does not list.
/// ConfigMgr is the default of the stored setting only; it is not the base of the others.
/// </summary>
public static class DeploymentTargets
{
    public const string Mecm = "ConfigMgr";
    public const string MecmAndIntune = "ConfigMgrAndIntune";
    public const string IntuneOnly = "IntuneOnly";
    public const string MecmAndWsus = "ConfigMgrAndWSUS";
    public const string WsusOnly = "WSUSOnly";
    public const string IntuneAndWsus = "IntuneAndWSUS";
    public const string MecmAndIntuneAndWsus = "ConfigMgrAndIntuneAndWSUS";

    /// <summary>A run that stages and publishes nowhere. It is also what an unknown or empty stored value reads as.</summary>
    public const string ContentOnly = "ContentOnly";

    /// <summary>The seven publish names: every non-empty combination of the three destinations.</summary>
    public static readonly IReadOnlyList<string> Names =
        [Mecm, MecmAndIntune, IntuneOnly, MecmAndWsus, WsusOnly, IntuneAndWsus, MecmAndIntuneAndWsus];

    /// <summary>The valid names as one text, for a message that rejects another name.</summary>
    public static string NamesText => string.Join(", ", Names);

    /// <summary>
    /// The earlier spellings of the ConfigMgr targets. Old settings, definitions, command lines
    /// and saved plans still carry them, so every place that reads a stored target maps them first.
    /// A known name in another letter case maps to its own spelling.
    /// </summary>
    public static string? Canonical(string? value)
    {
        var trimmed = value?.Trim();
        switch (trimmed?.ToUpperInvariant())
        {
            case "MECM": return Mecm;
            case "MECMANDINTUNE": return MecmAndIntune;
            case "MECMANDWSUS": return MecmAndWsus;
        }
        if (trimmed is null) return value;
        foreach (var name in Names.Append(ContentOnly))
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)) return name;
        return value;
    }

    /// <summary>The label of a target as a destination list, for example "Intune and WSUS". An unknown name has no label.</summary>
    public static string? Label(string? value) => DestinationSet.TryFromTarget(value)?.Describe();

    /// <summary>True when the value is a publish name or ContentOnly (an earlier MECM spelling counts).</summary>
    public static bool IsKnown(string? value) => DestinationSet.TryFromTarget(value) is not null;

    /// <summary>
    /// Keeps every known target name. An unknown or empty value is never ConfigMgr: it reads as
    /// ContentOnly, which publishes nowhere. The earlier MECM spellings still load.
    /// </summary>
    public static string Normalize(string? value) => IsKnown(value) ? Canonical(value)! : ContentOnly;

    public static bool PublishesToIntune(string? value) => DestinationSet.TryFromTarget(value)?.Intune ?? false;

    public static bool PublishesToWsus(string? value) => DestinationSet.TryFromTarget(value)?.Wsus ?? false;

    /// <summary>True when the target has no ConfigMgr destination: no site connection, no share copy, no ConfigMgr application.</summary>
    public static bool SkipsMecm(string? value) => !(DestinationSet.TryFromTarget(value)?.ConfigMgr ?? false);
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
            // The grid Package action publishes to ConfigMgr or Intune; WSUS has its own button, Publish to WSUS.
            var set = DestinationSet.TryFromTarget(deploymentTarget) ?? new DestinationSet();
            var published = (set.Intune, set.Wsus) switch
            {
                (true, true) => "Stage each selected app and publish it to Intune. Use Publish to WSUS to publish it to WSUS.",
                (true, false) => "Stage each selected app and publish it to Intune.",
                (false, true) => "Stage each selected app. Use Publish to WSUS to publish it to WSUS.",
                _ => "Stage each selected app. The Deployment Target has no destination, so nothing is published."
            };
            return new DeploymentTargetActionState
            {
                CheckMecmEnabled = false,
                CheckMecmToolTip = "To use Check ConfigMgr, select ConfigMgr under Default destinations in Settings, One Click.",
                PackageLabel = "Publish",
                PackageToolTip = published,
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
/// Pre-run configuration gate. Each destination asks for its own settings and no other: ConfigMgr the site
/// fields, Intune its credentials. A target without ConfigMgr never asks for a site, so a console-less workstation can still publish.
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
