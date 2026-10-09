using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>Maps saved One Click configuration into planner inputs.</summary>
public static class OneClickSettingsMap
{
    public static PublishReadiness Readiness(AppSettings s, bool consoleFound) => new(
        s.Systems ?? new SystemsInUse(), consoleFound, s.SiteCode, s.FileShareRoot,
        s.Intune.TenantId, s.Intune.ClientId, !string.IsNullOrWhiteSpace(s.Intune.ClientSecretProtected), s.Wsus.ServerName);

    public static OneClickPlanSettings PlanSettings(AppSettings s, bool consoleFound) => new()
    {
        Tracked = s.OneClick.TrackedApplications,
        Destinations = new Dictionary<string, DestinationSet>(s.OneClick.Destinations, StringComparer.OrdinalIgnoreCase),
        DefaultDestinations = s.OneClick.DefaultDestinations,
        LegacyTarget = s.Intune.DeploymentTarget,
        DefaultCadenceDays = s.OneClick.DefaultCadenceDays,
        CadenceOverrides = new Dictionary<string, int>(s.OneClick.CadenceOverrides, StringComparer.OrdinalIgnoreCase),
        Readiness = OneClickPlanner.Readiness(Readiness(s, consoleFound)),
        Scope = new OneClickScopeSettings(
            s.ContentDistribution.AutoDistribute, s.ContentDistribution.DpGroupName,
            s.ContentDistribution.DeployToTestCollection, s.ContentDistribution.TestCollectionName,
            s.Wsus.ApprovalGroup, s.ContentDistribution.TestCollectionIsUser)
    };

    /// <summary>
    /// The destinations a settings save stores per application: only the rows that
    /// differ from the default, so a row that matches it follows a later change.
    /// </summary>
    public static Dictionary<string, DestinationSet> RowsThatDiffer(IEnumerable<(string Key, DestinationSet Set)> rows, DestinationSet defaults) =>
        rows.Where(r => !r.Set.Equals(defaults))
            .ToDictionary(r => r.Key, r => r.Set.Copy(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tracked list and cadence overrides a settings save stores: the rows the One
    /// Click grid shows, plus every stored entry of an application the grid does not list,
    /// so a save while the Packages page shows fewer applications loses no entry.
    /// </summary>
    public static (string[] Tracked, Dictionary<string, int> CadenceOverrides) TrackedAndCadence(
        IReadOnlyCollection<(string Uid, bool Tracked, int? Cadence)> rows, int defaultCadence, OneClickSettings stored)
    {
        var listed = new HashSet<string>(rows.Select(r => r.Uid), StringComparer.OrdinalIgnoreCase);
        var tracked = rows.Where(r => r.Tracked).Select(r => r.Uid)
            .Concat(stored.TrackedApplications.Where(uid => !listed.Contains(uid)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var cadence = rows.Where(r => r.Cadence is { } days && days != defaultCadence)
            .ToDictionary(r => r.Uid, r => r.Cadence!.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (uid, days) in stored.CadenceOverrides)
            if (!listed.Contains(uid)) cadence.TryAdd(uid, days);
        return (tracked, cadence);
    }

    /// <summary>The log line after Setup saves: every default destination, or none.</summary>
    public static string SetupCompleteLine(DestinationSet defaults) =>
        $"Setup complete. One Click publishes to {defaults.Describe()}; change it in Settings, One Click Settings.";
}
