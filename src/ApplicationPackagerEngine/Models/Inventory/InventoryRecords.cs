namespace ApplicationPackagerEngine.Models.Inventory;

/// <summary>The stable catalog identity plus the Workbench identity for one profile.</summary>
public sealed record InventoryApplicationIdentity(
    string PackagerUid,
    string ApplicationId,
    string? ProfileId,
    int? ProfileRevision = null);

/// <summary>A destination and the specific server or tenant whose state is being recorded.</summary>
public sealed record InventoryEnvironmentIdentity(string Destination, string Key)
{
    public static InventoryEnvironmentIdentity Unknown(string destination) => new(destination, "unknown");

    public static InventoryEnvironmentIdentity ConfigMgr(string? siteCode, string? providerMachine) =>
        string.IsNullOrWhiteSpace(siteCode)
            ? Unknown("ConfigMgr")
            : new("ConfigMgr", ApplicationPackagerEngine.Services.SiteIdentity.Key(siteCode, providerMachine));

    public static InventoryEnvironmentIdentity Intune(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId)
            ? Unknown("Intune")
            : new("Intune", tenantId.Trim().ToUpperInvariant());

    public static InventoryEnvironmentIdentity Wsus(ApplicationPackagerEngine.Models.WsusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        return string.IsNullOrWhiteSpace(normalized.ServerName)
            ? Unknown("WSUS")
            : new("WSUS", $"{normalized.ServerName.ToUpperInvariant()}|{normalized.PortNumber}|{(normalized.UseSsl ? "SSL" : "HTTP")}");
    }
}

public enum InventoryOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Skipped,
    NotFound,
    Refused,
    AlreadyPresent,
    Partial,
    LegacyImported,
    Unknown
}

public sealed record InventoryVendorCheck(
    string PackagerUid,
    string AppName,
    string? Version,
    string Source,
    InventoryOutcome Outcome,
    DateTimeOffset At,
    string? OperationId = null,
    string? Detail = null);

public sealed record InventoryVendorState(
    string PackagerUid,
    string? LatestVersion,
    string? LatestSource,
    DateTimeOffset? LastSucceededAt,
    DateTimeOffset? LastAttemptAt,
    InventoryOutcome LastAttemptOutcome,
    string LastAttemptSource,
    string? LastAttemptDetail);

public sealed record InventoryEnvironmentObservation(
    string PackagerUid,
    InventoryApplicationIdentity? Application,
    InventoryEnvironmentIdentity Environment,
    string? Version,
    string Source,
    InventoryOutcome Outcome,
    DateTimeOffset At,
    string? OperationId = null,
    string? Detail = null);

public sealed record InventoryEnvironmentState(
    string PackagerUid,
    string Destination,
    string EnvironmentKey,
    string? ApplicationId,
    string? ProfileId,
    string? LatestObservedVersion,
    string? LatestObservationSource,
    DateTimeOffset? LatestObservedAt,
    DateTimeOffset? LastAttemptAt,
    InventoryOutcome LastAttemptOutcome,
    string LastAttemptSource,
    string? LastAttemptDetail);

public sealed record InventoryStagedBuild(
    InventoryApplicationIdentity Application,
    string Version,
    string BuildId,
    string BuildRecordPath,
    string ManifestPath,
    DateTimeOffset StagedAt,
    string? OperationId = null);

public sealed record InventoryPackageState(
    string ApplicationId,
    string ProfileId,
    string PackagerUid,
    int? ProfileRevision,
    string Version,
    string BuildId,
    string BuildRecordPath,
    string ManifestPath,
    DateTimeOffset StagedAt);

/// <param name="ConfigMgrCiId">ConfigMgr publish: the CI_ID the packaging run read back for the application (<c>PackagingResult.CiId</c>); null when not known.</param>
public sealed record InventoryDestinationOutcome(
    string PackagerUid,
    InventoryApplicationIdentity? Application,
    InventoryEnvironmentIdentity Environment,
    string Action,
    string Version,
    InventoryOutcome Outcome,
    string? ObjectId,
    DateTimeOffset At,
    string? BuildId = null,
    string? OperationId = null,
    string? Detail = null,
    string? ConfigMgrCiId = null);

/// <param name="ConfigMgrCiId">ConfigMgr publish: the CI_ID of the application revision the publish left, or one a lookup linked; null when not known.</param>
/// <param name="ConfigMgrModelName">ConfigMgr publish: the ModelName of the application, the same for all its revisions and kept on a rename; null until a lookup links it.</param>
public sealed record InventoryDestinationState(
    string PackagerUid,
    string Destination,
    string EnvironmentKey,
    string Action,
    string? ApplicationId,
    string? ProfileId,
    string? LastAttemptVersion,
    InventoryOutcome LastAttemptOutcome,
    string? LastAttemptObjectId,
    DateTimeOffset? LastAttemptAt,
    string? LastSuccessVersion,
    string? LastSuccessObjectId,
    DateTimeOffset? LastSuccessAt,
    string? LastSuccessBuildId,
    string? LastAttemptDetail,
    string? ConfigMgrCiId = null,
    string? ConfigMgrModelName = null);

public sealed record InventoryRefusalState(string Version, string? Detail, DateTimeOffset? At);

public sealed record InventoryOperation(
    string OperationId,
    string Kind,
    string PackagerUid,
    InventoryApplicationIdentity? Application,
    string? Target,
    DateTimeOffset StartedAt);

public sealed record InventoryOperationState(
    string OperationId,
    string Kind,
    string PackagerUid,
    string? ApplicationId,
    string? ProfileId,
    string? Target,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    InventoryOutcome Outcome,
    string? Detail);

public sealed record InventoryEventRecord(
    long Id,
    string EventId,
    string Kind,
    string PackagerUid,
    string? AppName,
    string? ApplicationId,
    string? ProfileId,
    string? Destination,
    string? EnvironmentKey,
    string? Action,
    string? Version,
    string? Source,
    InventoryOutcome Outcome,
    string? ObjectId,
    string? BuildId,
    string? BuildRecordPath,
    string? ManifestPath,
    DateTimeOffset? OccurredAt,
    string? OperationId,
    string? Detail);

/// <summary>One legacy destination fact whose original environment identity was not saved.</summary>
public sealed record LegacyInventoryDestinationFact(
    string Destination,
    string Version,
    string? ObjectId,
    DateTimeOffset? At,
    InventoryOutcome Outcome,
    string? Detail = null);

/// <summary>Settings.json state imported once while older releases stored operational data there.</summary>
public sealed record LegacyInventoryAppState(
    string PackagerUid,
    string LatestVersion,
    DateTimeOffset? LastChecked,
    string CurrentEnvironmentVersion,
    DateTimeOffset? LastStaged,
    IReadOnlyList<LegacyInventoryDestinationFact> Destinations);
