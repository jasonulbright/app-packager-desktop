using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>The One Click planner's view of the stored settings and per-application history.</summary>
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
            s.Wsus.ApprovalGroup)
    };

    public static OneClickHistoryEntry Entry(AppCheckState state) => new()
    {
        LastChecked = state.LastChecked,
        LastKnownVersion = state.LatestVersion ?? "",
        LastStaged = state.LastStaged,
        Published = new Dictionary<string, OneClickPublishRecord>(state.Published ?? [], StringComparer.OrdinalIgnoreCase),
        NotSupported = new Dictionary<string, OneClickRefusalRecord>(state.NotSupported ?? [], StringComparer.OrdinalIgnoreCase)
    };

    public static IReadOnlyDictionary<string, OneClickHistoryEntry> History(AppSettings s) =>
        s.AppStates.Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => Entry(kv.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The destinations a settings save stores per application: only the rows that
    /// differ from the default, so a row that matches it follows a later change.
    /// </summary>
    public static Dictionary<string, DestinationSet> RowsThatDiffer(IEnumerable<(string Key, DestinationSet Set)> rows, DestinationSet defaults) =>
        rows.Where(r => !r.Set.Equals(defaults))
            .ToDictionary(r => r.Key, r => r.Set.Copy(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The log line after Setup saves: every default destination, or none.</summary>
    public static string SetupCompleteLine(DestinationSet defaults) =>
        $"Setup complete. One Click publishes to {defaults.Describe()}; change it in Settings, One Click Settings.";
}
