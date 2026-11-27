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

    /// <summary>True when the last ConfigMgr publish of this version of the row did not finish its content.</summary>
    public bool ConfigMgrIncomplete(PackagerInfo row, AppSettings settings, string version, PackagerExecutionService.OneClickBuildPlan? plan)
    {
        var environment = InventoryEnvironmentIdentity.ConfigMgr(settings.SiteCode, settings.ProviderMachine);
        return version.Length > 0 && environment.Key != "unknown"
            && inventory.LastObjectIsPartial(row.Uid, environment, "Publish", version,
                (applicationId, buildId) => SealedVersion(plan, applicationId, buildId));
    }

    /// <summary>
    /// The records of one row. A record's version is the one that its sealed build holds: an earlier version of the app stored the website
    /// version for a publish of a local installer.
    /// </summary>
    /// <param name="plan">The workbench identity of the row, which locates the build records; null when it cannot be resolved.</param>
    /// <param name="localInstaller">True when a stage of the row takes a local installer.</param>
    public OneClickHistoryEntry Entry(PackagerInfo row, AppSettings settings,
        PackagerExecutionService.OneClickBuildPlan? plan, bool localInstaller)
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
                var sealedVersion = SealedVersion(plan, state.ApplicationId, state.LastSuccessBuildId);
                published[destination] = new OneClickPublishRecord
                {
                    Version = sealedVersion ?? state.LastSuccessVersion,
                    // ConfigMgr keeps the stored version: the site answer is what a ConfigMgr skip mainly rests on.
                    Unverified = sealedVersion is null && localInstaller && destination != OneClickDestinations.ConfigMgr,
                    Id = state.LastSuccessObjectId ?? "",
                    At = OneClickHistory.Stamp(state.LastSuccessAt ?? DateTimeOffset.MinValue)
                };
                // A publish whose supersedence step failed stays pending for the pass of a later run. Only ConfigMgr has the step.
                if (destination == OneClickDestinations.ConfigMgr
                    && OneClickHistory.IsSupersedencePending(state.LastAttemptOutcome, state.LastAttemptVersion, state.LastSuccessVersion))
                {
                    published[destination].SupersedencePending = true;
                    published[destination].SupersedenceReason = OneClickHistory.SupersedenceReasonOf(state.LastAttemptDetail);
                }
                if (plan is not null && PublishedBuildKey(plan, state.LastSuccessBuildId) is { } buildKey)
                    publishedBuild[destination] = buildKey;
            }
            var refusal = inventory.GetLatestRefusal(row.Uid, environment, "Publish");
            if (refusal is not null)
                refused[destination] = new OneClickRefusalRecord
                {
                    Version = SealedVersion(plan, refusal.ApplicationId, refusal.BuildId) ?? refusal.Version,
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

    /// <summary>The version in the sealed build record of a build; null when no build is named or its record is gone.</summary>
    private static string? SealedVersion(PackagerExecutionService.OneClickBuildPlan? plan, string? applicationId, string? buildId)
    {
        if (plan is null || string.IsNullOrWhiteSpace(buildId)) return null;
        try
        {
            var record = BuildRecords.Find(plan.DataRoot, string.IsNullOrWhiteSpace(applicationId) ? plan.ApplicationId : applicationId, buildId);
            var version = record is null ? null : WorkbenchJson.Get(record, "SoftwareVersion").Text;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or InvalidOperationException or System.Text.Json.JsonException)
        {
            return null;
        }
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
            var record = BuildRecords.Find(plan.DataRoot, plan.ApplicationId, buildId);
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
        Func<PackagerInfo, PackagerExecutionService.OneClickBuildPlan?> resolvePlan, Func<PackagerInfo, bool> localInstaller) =>
        rows.ToDictionary(row => row.Uid, row => Entry(row, settings, resolvePlan(row), localInstaller(row)), StringComparer.OrdinalIgnoreCase);

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
