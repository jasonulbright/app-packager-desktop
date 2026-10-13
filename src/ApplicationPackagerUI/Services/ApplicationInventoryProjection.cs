using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ApplicationPackagerUI.Services;

/// <summary>Projects persisted inventory facts into the current UI row and One Click view.</summary>
public sealed class ApplicationInventoryProjection(ApplicationInventoryService inventory)
{
    public InventoryVendorState? Vendor(PackagerInfo row) => inventory.GetVendorState(row.Uid);

    public InventoryEnvironmentState? Environment(PackagerInfo row, AppSettings settings) =>
        inventory.GetEnvironmentState(row.Uid, InventoryEnvironmentIdentity.ConfigMgr(settings.SiteCode, settings.ProviderMachine));

    public OneClickHistoryEntry Entry(PackagerInfo row, AppSettings settings,
        PackagerExecutionService.OneClickBuildPlan? plan = null)
    {
        var vendor = Vendor(row);
        var identity = IdentityFor(row, plan);
        var package = identity.ProfileId is { } profileId
            ? inventory.GetPackageState(identity.ApplicationId, profileId)
            : null;
        var published = new Dictionary<string, OneClickPublishRecord>(StringComparer.OrdinalIgnoreCase);
        var refused = new Dictionary<string, OneClickRefusalRecord>(StringComparer.OrdinalIgnoreCase);
        var publishedBuild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (destination, environment) in Environments(settings))
        {
            // An unknown legacy destination is retained for audit, but is not attributed
            // to a current tenant or server whose identity was never recorded.
            if (environment.Key == "unknown") continue;
            var state = inventory.GetDestinationState(row.Uid, environment, "Publish");
            if (!string.IsNullOrWhiteSpace(state?.LastSuccessVersion))
            {
                published[destination] = new OneClickPublishRecord
                {
                    Version = state.LastSuccessVersion,
                    Id = state.LastSuccessObjectId ?? "",
                    At = OneClickHistory.Stamp(state.LastSuccessAt ?? DateTimeOffset.MinValue)
                };
                if (plan is not null && PublishedBuildKey(plan, state.LastSuccessBuildId) is { } buildKey)
                    publishedBuild[destination] = buildKey;
            }
            var refusal = inventory.GetLatestRefusal(row.Uid, environment, "Publish");
            if (refusal is not null)
                refused[destination] = new OneClickRefusalRecord
                {
                    Version = refusal.Version,
                    Reason = refusal.Detail ?? "",
                    At = OneClickHistory.Stamp(refusal.At ?? DateTimeOffset.MinValue)
                };
        }

        return new OneClickHistoryEntry
        {
            LastChecked = vendor?.LastSucceededAt?.LocalDateTime,
            LastKnownVersion = vendor?.LatestVersion ?? "",
            LastStaged = package?.StagedAt.LocalDateTime,
            Published = published,
            NotSupported = refused,
            PublishedBuild = publishedBuild
        };
    }

    /// <summary>
    /// The build key of a published build, read from its sealed build record; null when
    /// the publish names no build or the record is gone, which leaves the build unknown.
    /// </summary>
    private static string? PublishedBuildKey(PackagerExecutionService.OneClickBuildPlan plan, string? buildId)
    {
        if (string.IsNullOrWhiteSpace(buildId)) return null;
        try
        {
            var record = BuildRecords.List(plan.DataRoot, plan.ApplicationId)
                .FirstOrDefault(r => string.Equals(WorkbenchJson.Get(r, "BuildId").Text, buildId, StringComparison.OrdinalIgnoreCase));
            if (record is null) return null;
            return OneClickHistory.BuildKey(WorkbenchJson.Get(record, "ProfileId").Text,
                WorkbenchJson.AsInt(record["ProfileRevision"]) ?? 0, WorkbenchJson.Get(record, "PolicyDigest").Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public IReadOnlyDictionary<string, OneClickHistoryEntry> History(IEnumerable<PackagerInfo> rows, AppSettings settings,
        Func<PackagerInfo, PackagerExecutionService.OneClickBuildPlan?>? resolvePlan = null) =>
        rows.ToDictionary(row => row.Uid, row => Entry(row, settings, resolvePlan?.Invoke(row)), StringComparer.OrdinalIgnoreCase);

    public static InventoryApplicationIdentity IdentityFor(IPackagerExecutionRequest row,
        PackagerExecutionService.OneClickBuildPlan? plan = null)
    {
        if (plan is not null)
            return new InventoryApplicationIdentity(row.Uid, plan.ApplicationId, plan.ProfileId, plan.ProfileRevision);

        return ReferenceFor(row) with { ProfileId = ApplicationIds.DefaultProfileId };
    }

    public static InventoryApplicationIdentity ReferenceFor(IPackagerExecutionRequest row)
    {
        var applicationId = TryByoApplicationId(row.Uid);
        if (applicationId is null)
        {
            try { applicationId = WorkbenchApplicationCatalog.ApplicationIdFor(row); }
            catch (ArgumentException)
            {
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Uid)))[..16].ToLowerInvariant();
                applicationId = "custom:packager-" + key;
            }
        }
        return new InventoryApplicationIdentity(row.Uid, applicationId, null);
    }

    public static IReadOnlyList<(string Destination, InventoryEnvironmentIdentity Environment)> Environments(AppSettings settings)
    {
        var wsus = InventoryEnvironmentIdentity.Unknown("WSUS");
        try { wsus = InventoryEnvironmentIdentity.Wsus(settings.Wsus); }
        catch (WsusSettingsException) { }
        return
        [
            (OneClickDestinations.ConfigMgr, InventoryEnvironmentIdentity.ConfigMgr(settings.SiteCode, settings.ProviderMachine)),
            (OneClickDestinations.Intune, InventoryEnvironmentIdentity.Intune(settings.Intune.TenantId)),
            (OneClickDestinations.Wsus, wsus)
        ];
    }

    private static string? TryByoApplicationId(string uid)
    {
        const string prefix = "byo-";
        if (!uid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var key = uid[prefix.Length..];
        try
        {
            var id = ApplicationIds.FromKey(key);
            return ApplicationIds.IsByo(id) ? id : null;
        }
        catch (ArgumentException) { return null; }
    }
}
