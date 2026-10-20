using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Creates, distributes and removes single deployments of applications, packages, task sequences
/// and software update groups through the ConfigMgr session. Every write re-reads the state it
/// depends on, the create script checks for a duplicate again right before the create, every
/// create is read back by the deployment ID, and every write is recorded in the audit log, which
/// must be writable before a create or a remove starts. A write runs to its end once started:
/// cancellation takes effect between changes.
/// </summary>
public sealed partial class DeploymentService : IDeploymentService
{
    public const string LogArea = "Deployments";

    /// <summary>Starts the message of an outcome that changed nothing on purpose.</summary>
    public const string SkippedPrefix = "Skipped: ";

    internal const string ScriptResource = "ApplicationPackagerEngine.Services.Deployments.Scripts.Deployments.ps1";

    internal const string AccessHint =
        "The account needs a ConfigMgr security role with deployment rights for the object type, distribute content rights and read rights on collections.";

    private static readonly ConfigMgrScript Script = ConfigMgrScript.FromResource(ScriptResource);
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IConfigMgrSession _session;
    private readonly ICollectionService _collections;
    private readonly IDeploymentAuditLog _audit;
    private readonly IDeploymentSigningCheck? _signing;
    private readonly IAppLog? _log;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeZoneInfo _local;
    private readonly IDeploymentTargetLock _targetLock;

    /// <summary>The deployment IDs this service created in this process; an Undo of one of them is owned without the log.</summary>
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.OrdinalIgnoreCase);

    public DeploymentService(
        IConfigMgrSession session,
        ICollectionService collections,
        IDeploymentAuditLog audit,
        IDeploymentSigningCheck? signing = null,
        IAppLog? log = null,
        Func<DateTime>? utcNow = null,
        TimeZoneInfo? local = null,
        IDeploymentTargetLock? targetLock = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _signing = signing;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _local = local ?? TimeZoneInfo.Local;
        _targetLock = targetLock ?? new DeploymentTargetLock(() => _audit.Path);
    }

    /// <summary>The most audit records read to decide whether this product created a deployment.</summary>
    public int OwnershipRecords { get; init; } = 100_000;

    /// <summary>The most rows a list of objects or of the deployments of an object holds.</summary>
    public int MaxListRows { get; init; } = 1000;

    [GeneratedRegex("^[0-9]{1,10}$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex("^[A-Za-z0-9]{8}$")]
    private static partial Regex PackageIdRegex();

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex GuidRegex();

    public static bool IsBuiltInCollection(string? id) => id is not null && id.Trim().StartsWith("SMS", StringComparison.OrdinalIgnoreCase);

    internal static bool IsObjectId(DeploymentObjectType type, string? id) =>
        type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup
            ? NumberRegex().IsMatch(id ?? "") && long.Parse(id!, CultureInfo.InvariantCulture) <= int.MaxValue
            : PackageIdRegex().IsMatch(id ?? "");

    internal static bool IsDeploymentId(DeploymentObjectType type, string? id) =>
        type is DeploymentObjectType.Application or DeploymentObjectType.SoftwareUpdateGroup ? GuidRegex().IsMatch(id ?? "") : PackageIdRegex().IsMatch(id ?? "");

    // ---- Script output shapes ----------------------------------------------------------------

    internal sealed record ObjectRow(string? Kind, string? Id, string? Name, string? Version, string? ModelId, int UpdateCount, string? Program = null, bool Retired = false, string? Revision = null,
        string? ModelName = null);

    internal sealed record NameCountRow(string? Kind, int Count);

    /// <summary>Application: the CI_IDs of every revision of the application's model.</summary>
    internal sealed record RevisionsRow(string? Kind, string[]? CiIds);

    internal sealed record TruncatedRow(string? Kind, int Shown, int Total);

    internal sealed record ProofRow(string? Kind, string? DeploymentId, string? Created, string? CreatedBy, bool Owned, string? Account = null);

    internal sealed record ContentRow(string? Kind, string? PackageId, string? Name, int Targeted, int Success, int InProgress, int Errors, bool Distributed);

    internal sealed record ReadFlag(string? Kind, bool Read);

    internal sealed record CollectionReadRow(string? Kind, string? CollectionId, string? Name, int CollectionType, int MemberCount);

    internal sealed record SnapshotRow(string? Kind, bool Read, string[]? Ids, string[]? Duplicates);

    internal sealed record DeploymentRow(
        string? Kind, string? DeploymentId, string? AssignmentId, string? ObjectId, string? ModelId, string? ObjectName, string? Program, string? CollectionId,
        string? CollectionName, string? Purpose, string? Available, string? Deadline, bool Utc, string? Created, string? CreatedBy, string? Notification,
        bool Override, bool Reboot, bool Metered, bool FallbackToDp, bool AllowMicrosoftUpdate, bool SuppressRestart, bool PostRebootScan,
        string? Network, string? Slow, string? Rerun, bool ShowProgress, bool AllowFallback, string? Availability, bool ExpirationEnabled);

    internal sealed record DpGroupRow(string? Kind, string? GroupId, string? Name, string? Status, int Targeted, int Success, int InProgress, int Error);

    internal sealed record GroupResultRow(string? Kind, string? GroupId, string? Status, string? Detail, string[]? Missing);

    /// <param name="Account">The account of the ConfigMgr session, as ConfigMgr records a creator.</param>
    internal sealed record ResultRow(string? Kind, string? Status, string? Detail, string[]? Steps, string? CreatedId, string[]? Differences, string[]? Ids, string? Account = null);

    /// <summary>The state a Prepare and a create read: the object, its content, the collection and the deployments of the object to it.</summary>
    /// <param name="NameCount">Required application: the number of latest applications with the object's name; null when not read.</param>
    /// <param name="RevisionIds">Application: the CI_IDs of every revision of its model; null when not read.</param>
    internal sealed record StateRead(ObjectRow? Object, IReadOnlyList<ContentRow> Content, bool ContentRead, CollectionReadRow? Collection, SnapshotRow? Snapshot, int? NameCount = null,
        IReadOnlyList<string>? RevisionIds = null);

    // ---- Reads --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeployableObject>> ListObjectsAsync(DeploymentObjectType type, CancellationToken ct)
    {
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Objects" }, { "Type", type.ToString() }, { "MaxRows", MaxListRows } }, ct).ConfigureAwait(false);
        var objects = Rows<ObjectRow>(output, "Object");
        var packaged = type == DeploymentObjectType.Application ? PackagedFlags(objects) : null;
        var rows = objects
            .Select((r, i) => new DeployableObject(type, r.Id ?? "", r.Name ?? "", r.Version ?? "", "", packaged?[i] == true, r.ModelId ?? "", r.UpdateCount))
            .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _log?.Info(LogArea, $"Listed {rows.Count} objects of type {type}.");
        return Limited(rows, output, $"{Noun(type)} list");
    }

    private DeploymentList<T> Limited<T>(List<T> rows, ConfigMgrCommandResult output, string what)
    {
        if (Rows<TruncatedRow>(output, "Truncated").FirstOrDefault() is not { } cut) return new DeploymentList<T>(rows);
        _log?.Warning(LogArea, $"A list was cut at {cut.Shown} of {cut.Total} rows.");
        return new DeploymentList<T>(rows, true,
            $"The {what.ToLowerInvariant()} shows the first {cut.Shown.ToString(CultureInfo.InvariantCulture)} of {cut.Total.ToString(CultureInfo.InvariantCulture)} rows. Use the console for the rest.");
    }

    /// <summary>
    /// Whether this product packaged each listed application, after linking the inventory records
    /// that hold no ConfigMgr identity yet to the listed applications; null when not known.
    /// </summary>
    private bool[]? PackagedFlags(IReadOnlyList<ObjectRow> rows)
    {
        if (_signing is null) return null;
        var applications = rows.Select(r => new SiteApplication(r.Name ?? "", r.Id ?? "", r.ModelName ?? "", r.Id is { Length: > 0 } id ? [id] : [])).ToList();
        try
        {
            _signing.Link(applications);
            return applications.Select(_signing.IsPackaged).ToArray();
        }
        catch (DeploymentServiceException)
        {
            return null;
        }
    }

    /// <summary>The application a state read found, as the signing rule matches it; null for another type or a missing object.</summary>
    internal static SiteApplication? SiteApplicationOf(StateRead? state) =>
        state?.Object is { } o
            ? new SiteApplication(o.Name ?? "", o.Id ?? "", o.ModelName ?? "", state.RevisionIds ?? [])
            : null;

    public async Task<IReadOnlyList<string>> ListProgramsAsync(string packageId, CancellationToken ct)
    {
        var id = (packageId ?? "").Trim().ToUpperInvariant();
        if (!PackageIdRegex().IsMatch(id)) throw new DeploymentServiceException("Select a package.");
        var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Programs" }, { "ObjectId", id } }, ct).ConfigureAwait(false);
        return Rows<ProgramRow>(output, "Program").Select(p => p.Name ?? "").Where(n => n.Length > 0).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal sealed record ProgramRow(string? Kind, string? Name);

    public async Task<IReadOnlyList<DeploymentCollection>> ListCollectionsAsync(CancellationToken ct)
    {
        IReadOnlyList<CollectionRow> rows;
        try
        {
            rows = await _collections.ListCollectionsAsync(ct).ConfigureAwait(false);
        }
        catch (CollectionServiceException ex)
        {
            throw new DeploymentServiceException(ex.Message);
        }
        var result = rows.Where(c => !c.IsBuiltIn && !IsBuiltInCollection(c.CollectionId))
            .Select(c => new DeploymentCollection(c.CollectionId, c.Name, c.FolderPath, c.MemberCount))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _log?.Info(LogArea, $"Listed {result.Count} device collections that are not built in.");
        return result;
    }

    public async Task<IReadOnlyList<DpGroupInfo>> ListDpGroupsAsync(DeployableObject obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (obj.Type == DeploymentObjectType.SoftwareUpdateGroup) return [];
        var output = await ReadAsync(ObjectParameters("DpGroups", obj), ct).ConfigureAwait(false);
        if (Rows<ResultRow>(output, "Result").FirstOrDefault()?.Status == "object-missing")
            throw new DeploymentServiceException($"{Noun(obj.Type)} {Shown(obj.Name, obj.Id)} does not exist any more. Refresh the list.");
        var groups = Rows<DpGroupRow>(output, "DpGroup")
            .Select(g => new DpGroupInfo(g.Name ?? "", g.GroupId ?? "", g.Status ?? "Unknown", g.Targeted, g.Success, g.InProgress, g.Error))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _log?.Info(LogArea, $"Read the status of {groups.Count} DP groups.");
        return groups;
    }

    public async Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(DeployableObject obj, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var parameters = ObjectParameters("Deployments", obj);
        parameters["MaxRows"] = MaxListRows;
        var output = await ReadAsync(parameters, ct).ConfigureAwait(false);
        if (Rows<ResultRow>(output, "Result").FirstOrDefault()?.Status == "object-missing")
            throw new DeploymentServiceException($"{Noun(obj.Type)} {Shown(obj.Name, obj.Id)} does not exist any more. Refresh the list.");
        var rows = Rows<DeploymentRow>(output, "Deployment").Select(r => ToRecord(obj.Type, r)).OrderBy(r => r.CollectionName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _log?.Info(LogArea, $"Listed {rows.Count} deployments of one {obj.Type}.");
        return Limited(rows, output, "deployment list");
    }

    internal static DeploymentRecord ToRecord(DeploymentObjectType type, DeploymentRow r) => new(
        r.DeploymentId ?? "", type, r.ObjectId ?? "", r.ObjectName ?? "", r.CollectionId ?? "", r.CollectionName ?? "", r.Purpose ?? "",
        ParseTime(r.Available) ?? DateTime.MinValue, ParseTime(r.Deadline),
        ParseUtc(r.Created) ?? DateTime.MinValue,
        r.CreatedBy ?? "", r.Utc, r.Program ?? "", r.AssignmentId ?? "", type == DeploymentObjectType.Application ? r.ModelId ?? "" : "");

    internal static DateTime? ParseUtc(string? text) =>
        DateTime.TryParseExact(text ?? "", "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    internal static DateTime? ParseTime(string? text) =>
        DateTime.TryParseExact(text ?? "", TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    internal const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss";

    internal static string TimeText(DateTime value) => value.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private async Task<StateRead> ReadStateAsync(DeployableObject obj, string collectionId, string purpose, CancellationToken ct)
    {
        var parameters = ObjectParameters("State", obj);
        parameters["CollectionId"] = collectionId;
        if (purpose is DeploymentPurposes.Available or DeploymentPurposes.Required) parameters["Purpose"] = purpose;
        var output = await ReadAsync(parameters, ct).ConfigureAwait(false);
        return new StateRead(
            Rows<ObjectRow>(output, "Object").FirstOrDefault(),
            Rows<ContentRow>(output, "Content"),
            Rows<ReadFlag>(output, "ContentRead").FirstOrDefault()?.Read ?? false,
            Rows<CollectionReadRow>(output, "Collection").FirstOrDefault(),
            Rows<SnapshotRow>(output, "Snapshot").FirstOrDefault(),
            Rows<NameCountRow>(output, "NameCount").FirstOrDefault()?.Count,
            Rows<RevisionsRow>(output, "Revisions").FirstOrDefault()?.CiIds);
    }

    /// <summary>The state values a create is checked against: the object revision, its content and the collection size.</summary>
    internal static DeploymentPreparedState? PreparedState(StateRead? state) =>
        state?.Object is { } o && state.Collection is { } c
            ? new DeploymentPreparedState(o.Id ?? "", o.Revision ?? "",
                state.Content.Select(x => (x.PackageId ?? "").ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToList(), c.MemberCount)
            : null;

    private async Task<(bool Read, DeploymentRow? Row)> ReadDeploymentOrNullAsync(DeploymentObjectType type, string? deploymentId)
    {
        if (!IsDeploymentId(type, deploymentId)) return (false, null);
        try
        {
            var output = await ReadAsync(new ConfigMgrParameters { { "Operation", "Deployment" }, { "Type", type.ToString() }, { "DeploymentId", deploymentId } }, CancellationToken.None).ConfigureAwait(false);
            return (true, Rows<DeploymentRow>(output, "Deployment").FirstOrDefault());
        }
        catch (DeploymentServiceException)
        {
            return (false, null);
        }
    }

    private static ConfigMgrParameters ObjectParameters(string operation, DeployableObject obj)
    {
        var id = (obj.Id ?? "").Trim().ToUpperInvariant();
        if (!IsObjectId(obj.Type, id)) throw new DeploymentServiceException($"Select {Article(obj.Type)} {Noun(obj.Type).ToLowerInvariant()}.");
        var parameters = new ConfigMgrParameters { { "Operation", operation }, { "Type", obj.Type.ToString() }, { "ObjectId", id } };
        if (obj.Type == DeploymentObjectType.Application && NumberRegex().IsMatch(obj.ModelId ?? "")) parameters["ModelId"] = obj.ModelId;
        if (obj.Type == DeploymentObjectType.Package) parameters.AddSecret("Program", obj.Program ?? "");
        return parameters;
    }

    // ---- Apply --------------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeploymentOutcome>> ApplyAsync(IReadOnlyList<DeploymentChange> changes, IProgress<DeploymentOutcome> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(progress);
        var outcomes = new List<DeploymentOutcome>(changes.Count);
        var skipped = 0;
        foreach (var change in changes)
        {
            DeploymentOutcome outcome;
            if (ct.IsCancellationRequested)
            {
                outcome = new DeploymentOutcome(change, false, SkippedPrefix + "the run was cancelled before this change.", null, []);
            }
            else
            {
                try
                {
                    outcome = change.Kind switch
                    {
                        DeploymentChangeKind.Create => await ApplyCreateAsync(change).ConfigureAwait(false),
                        DeploymentChangeKind.Distribute => await ApplyDistributeAsync(change).ConfigureAwait(false),
                        DeploymentChangeKind.RemoveDeployment => await ApplyRemoveAsync(change).ConfigureAwait(false),
                        _ => Refused(change, "The change kind is not known.")
                    };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log?.Warning(LogArea, $"{change.Kind} stopped with an unexpected error: {ex.GetType().Name}.");
                    outcome = Fail(change, "The change stopped with an unexpected error. Refresh the lists to see the current state. The log has the details.");
                }
                _log?.Info(LogArea, $"{change.Kind}: {(outcome.Succeeded ? "succeeded" : "not applied")}{(outcome.UndoChange is null ? "" : ", with undo")}.");
            }
            if (outcome.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)) skipped++;
            outcomes.Add(outcome);
            progress.Report(outcome);
        }
        var succeeded = outcomes.Count(o => o.Succeeded);
        _log?.Info(LogArea, $"Applied {changes.Count} changes: {succeeded} succeeded, {outcomes.Count - succeeded - skipped} failed, {skipped} skipped.");
        return outcomes;
    }

    // ---- Session helpers ----------------------------------------------------------------------

    private async Task<ConfigMgrCommandResult> ReadAsync(ConfigMgrParameters parameters, CancellationToken ct)
    {
        try
        {
            return await _session.InvokeAsync(Script, parameters, ct).ConfigureAwait(false);
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"Reading deployment data failed in the ConfigMgr session: {ex.Failure}, {CollectionService.FailureKinds(ex.Message)}.");
            throw new DeploymentServiceException(ex.Failure == ConfigMgrFailure.ScriptFailed
                ? "The deployment data could not be read from ConfigMgr. The log has the details."
                : ComplianceService.SessionProblem(ex.Failure).Replace(ComplianceService.AccessHint, AccessHint, StringComparison.Ordinal));
        }
    }

    private IReadOnlyList<T> Rows<T>(ConfigMgrCommandResult result, string kind)
    {
        try
        {
            return result.Output
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == kind)
                .Select(e => e.Deserialize<T>(ReadOptions)!)
                .ToList();
        }
        catch (JsonException ex)
        {
            _log?.Warning(LogArea, $"ConfigMgr returned output that could not be read ({ex.GetType().Name}).");
            throw new DeploymentServiceException("ConfigMgr returned data that could not be read. The log has the details.");
        }
    }

    internal static string Noun(DeploymentObjectType type) => type switch
    {
        DeploymentObjectType.Application => "Application",
        DeploymentObjectType.Package => "Package",
        DeploymentObjectType.TaskSequence => "Task sequence",
        _ => "Software update group"
    };

    private static string Article(DeploymentObjectType type) => type == DeploymentObjectType.Application ? "an" : "a";

    internal static string Shown(string? name, string? id) =>
        string.IsNullOrWhiteSpace(name) ? id ?? "" : string.IsNullOrWhiteSpace(id) ? name : $"{name} ({id})";

    internal static string ObjectText(DeployableObject o) =>
        o.Type == DeploymentObjectType.Package && !string.IsNullOrEmpty(o.Program)
            ? $"package {Shown(o.Name, o.Id)}, program {o.Program}"
            : $"{Noun(o.Type).ToLowerInvariant()} {Shown(o.Name, o.Id)}";

    internal static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n.ToString(CultureInfo.InvariantCulture)} {noun}s";

    internal static string When(DateTime value, bool utc) => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + (utc ? " UTC" : " (client local time)");
}
