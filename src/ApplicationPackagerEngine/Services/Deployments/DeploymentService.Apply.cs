using ApplicationPackagerEngine.Services.Logging;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Services;

public sealed partial class DeploymentService
{
    internal const string NotPreparedMessage = "The change has no prepared checks, so it was not applied. Validate again.";
    internal const string RequiredNotConfirmedMessage =
        "A Required deployment installs on every device of the collection and needs its own confirmation, so nothing was created.";
    internal const string BackdatedNotConfirmedMessage =
        "The available time is in the past and needs its own confirmation, so nothing was created.";

    /// <summary>A create that ends in an error may still finish in the provider; a deployment made this long before the run is not the run's.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

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

    // ---- Create -------------------------------------------------------------------------------

    private async Task<DeploymentOutcome> ApplyCreateAsync(DeploymentChange change)
    {
        if (change.Object is not { } obj || change.Collection is not { } collection || change.Options is not { } options || change.Checks.Count == 0)
            return Refused(change, NotPreparedMessage);
        if (change.Blocked)
            return Refused(change, $"the plan has a check that did not pass ({string.Join(", ", change.Checks.Where(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown).Select(c => c.Name))}).");
        var collectionId = (collection.CollectionId ?? "").Trim().ToUpperInvariant();
        if (!IsObjectId(obj.Type, obj.Id) || !PackageIdRegex().IsMatch(collectionId)) return Refused(change, NotPreparedMessage);
        if (IsBuiltInCollection(collectionId)) return Refused(change, $"Collection {collectionId} is a built-in collection. Deployments to built-in collections are blocked.");
        if (options.Purpose != DeploymentPurposes.Required) options = options with { Deadline = null };
        if (options.Purpose == DeploymentPurposes.Required && !change.RequiredConfirmed) return Refused(change, RequiredNotConfirmedMessage);
        var verdict = DeploymentScheduleRules.Check(obj.Type, options, NowIn(options.UtcBasis));
        if (verdict.Error is { } scheduleError) return Refused(change, scheduleError);
        if (DeploymentScheduleRules.OptionProblem(obj.Type, options) is { } optionProblem) return Refused(change, optionProblem);
        if (verdict.Backdated && !change.BackdatedAcknowledged) return Refused(change, BackdatedNotConfirmedMessage);
        if (obj.Type == DeploymentObjectType.Application && options.Purpose == DeploymentPurposes.Required && _signing is not null)
        {
            try
            {
                if (_signing.RequiredDeploymentRefusal(obj.Name) is { } refusal) return Refused(change, refusal);
            }
            catch (DeploymentServiceException ex)
            {
                return Refused(change, ex.Message);
            }
        }

        using var reservation = _audit.Reserve();
        if (!reservation.Probe.Writable)
            return Refused(change, $"the audit log cannot be written, so the deployment was not created. {reservation.Probe.Message}");

        var steps = new List<string>();
        if (change.DpGroups is { Count: > 0 })
        {
            var distribution = await DistributeAsync(obj, change.DpGroups).ConfigureAwait(false);
            steps.AddRange(distribution.Steps);
            var written = reservation.Append(DistributionRecord(obj, change.DpGroups, distribution));
            if (!written.Succeeded) steps.Add($"The audit record of the distribution could not be written: {written.Message}");
            if (!distribution.Requested)
                return Fail(change, $"The content distribution failed, so the deployment was not created. {distribution.Message}") with { Steps = steps };
        }

        StateRead state;
        try
        {
            state = await ReadStateAsync(obj, collectionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DeploymentServiceException ex)
        {
            return Refused(change, $"the current state could not be read, so nothing was created. {ex.Message}") with { Steps = steps };
        }
        if (Stale(obj, collectionId, state) is { } stale) return Refused(change, stale) with { Steps = steps };
        if (state.Snapshot is not { Read: true } snapshot)
            return Refused(change, "the existing deployments could not be read, so the duplicate check failed and nothing was created.") with { Steps = steps };
        if ((snapshot.Duplicates ?? []).Length > 0)
            return Refused(change, $"{ObjectText(obj)} is now deployed to collection {collectionId} (deployment ID {string.Join(", ", snapshot.Duplicates!)}), so no duplicate was created.") with { Steps = steps };
        var current = obj with { Id = state.Object!.Id ?? obj.Id, ModelId = state.Object.ModelId ?? obj.ModelId, Program = obj.Type == DeploymentObjectType.Package ? state.Object.Program ?? obj.Program : obj.Program };

        var runStart = _utcNow();
        var parameters = CreateParameters(current, collectionId, options, !change.CreateNowContentDistributing);
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        steps.AddRange(run.Steps);
        var outcome = await CreateOutcomeAsync(change with { Object = current, Options = options }, current, collectionId, options, run, snapshot, runStart, steps).ConfigureAwait(false);

        var record = CreateRecord(change, current, collectionId, options, outcome);
        var audit = reservation.Append(record);
        if (!audit.Succeeded)
        {
            var id = outcome.UndoChange?.CreatedId;
            outcome = outcome with
            {
                Message = outcome.Message + (id is null
                    ? $" The audit record could not be written: {audit.Message}"
                    : $" The audit record could not be written: {audit.Message} Write down the deployment ID {id} and restore the audit log before the next deployment.")
            };
        }
        return outcome;
    }

    private string? Stale(DeployableObject obj, string collectionId, StateRead state)
    {
        if (state.Object is not { } o) return $"{ObjectText(obj)} does not exist any more. Nothing was created.";
        if (o.Retired) return $"application {Shown(o.Name, o.Id)} was retired after the plan was shown. Nothing was created.";
        if (obj.Type == DeploymentObjectType.Package && string.IsNullOrEmpty(o.Program)) return $"package {Shown(o.Name, o.Id)} has no program {obj.Program} any more. Nothing was created.";
        if (state.Collection is not { } c) return $"collection {collectionId} does not exist any more. Nothing was created.";
        if (c.CollectionType != 2) return $"collection {Shown(c.Name, c.CollectionId)} is not a device collection. Nothing was created.";
        return null;
    }

    private static ConfigMgrParameters CreateParameters(DeployableObject obj, string collectionId, DeploymentOptions o, bool requireContent)
    {
        var p = new ConfigMgrParameters
        {
            { "Operation", "Create" }, { "Type", obj.Type.ToString() }, { "ObjectId", obj.Id.Trim().ToUpperInvariant() }, { "CollectionId", collectionId },
            { "Purpose", o.Purpose }, { "Available", TimeText(o.AvailableTime) }, { "Deadline", o.Deadline is { } d ? TimeText(d) : "" }, { "Utc", o.UtcBasis },
            { "Override", o.OverrideServiceWindow }, { "Reboot", o.RebootOutsideServiceWindow }, { "Metered", o.AllowMetered }, { "RequireContent", requireContent }
        };
        if (obj.Type == DeploymentObjectType.Application && NumberRegex().IsMatch(obj.ModelId ?? "")) p["ModelId"] = obj.ModelId;
        if (DeploymentScheduleRules.HasNotification(obj.Type)) p["Notification"] = o.UserNotification;
        switch (obj.Type)
        {
            case DeploymentObjectType.Package:
                p.AddSecret("Program", obj.Program ?? "");
                p["Network"] = o.NetworkBehavior; p["Slow"] = o.SlowNetworkBehavior; p["Rerun"] = o.RerunBehavior; p["AllowFallback"] = o.AllowFallback;
                break;
            case DeploymentObjectType.TaskSequence:
                p["Network"] = o.NetworkBehavior; p["Rerun"] = o.RerunBehavior; p["AllowFallback"] = o.AllowFallback; p["ShowProgress"] = o.ShowProgress;
                p["Availability"] = o.TaskSequenceAvailability;
                break;
            case DeploymentObjectType.SoftwareUpdateGroup:
                p["FallbackToDp"] = o.FallbackToDp; p["PostRebootScan"] = o.PostRebootScan; p["SuppressRestart"] = o.SuppressRestart; p["AllowMicrosoftUpdate"] = o.AllowMicrosoftUpdate;
                break;
        }
        return p;
    }

    private async Task<DeploymentOutcome> CreateOutcomeAsync(
        DeploymentChange change, DeployableObject obj, string collectionId, DeploymentOptions options, WriteRun run, SnapshotRow snapshot, DateTime runStart, List<string> steps)
    {
        var target = $"{ObjectText(obj)} to collection {collectionId}";
        var result = run.Result;
        var stored = run.Rows.FirstOrDefault();
        switch (result?.Status)
        {
            case "ok" when stored is not null:
                return Done(change, $"Created the {options.Purpose} deployment of {target} (deployment ID {stored.DeploymentId}). The stored values equal the plan.", Undo(change, obj, stored), steps);
            case "created-with-error" when stored is not null:
                return Partial(change, $"ConfigMgr created the deployment of {target} (deployment ID {stored.DeploymentId}) and then reported an error. The stored values equal the plan. Undo removes it.", Undo(change, obj, stored), steps);
            case "stored-differs" when stored is not null:
                return Partial(change, $"ConfigMgr created the deployment of {target} (deployment ID {stored.DeploymentId}) but stored other values than the plan: {string.Join(", ", (result.Differences ?? []).Select(SettingText))}. Check it in the console or remove it with Undo.", Undo(change, obj, stored), steps);
            case "readback-failed":
                return Partial(change, $"ConfigMgr created the deployment of {target} (deployment ID {result.CreatedId}), but it could not be read back, so its stored values are not known. Undo removes it when it still holds the planned values.",
                    UndoFromPlan(change, obj, collectionId, options, result.CreatedId ?? ""), steps);
            case "create-unclear":
                return Fail(change, $"The result of the create of {target} is not known: {((result.Ids ?? []).Length > 1 ? $"{(result.Ids ?? []).Length} new deployments appeared ({string.Join(", ", result.Ids!)})" : "the deployments could not be read after the create")}. Check the deployments in the console. Undo is not available because a deployment found now cannot be shown to come from this change.") with { Steps = steps };
            case "create-failed":
                return Fail(change, $"ConfigMgr did not create the deployment of {target}. The log has the details.") with { Steps = steps };
            case "duplicate":
                return Refused(change, $"{target} is now deployed (deployment ID {string.Join(", ", result.Ids ?? [])}), so no duplicate was created.") with { Steps = steps };
            case "content-missing":
                return Refused(change, $"the content of {ObjectText(obj)} is not fully distributed yet, so the deployment was not created. Create it again when the content check passes, or select 'create now, content still distributing'.") with { Steps = steps };
            case "object-missing" or "program-missing" or "object-retired" or "collection-missing" or "not-device" or "built-in":
                return Refused(change, $"{StatusText(result.Status)} Nothing was created.") with { Steps = steps };
            case "check-failed":
                return Refused(change, "the checks right before the create could not read ConfigMgr, so nothing was created. Try again.") with { Steps = steps };
            case "invalid":
                return Refused(change, NotPreparedMessage) with { Steps = steps };
        }
        if (!run.Uncertain) return (run.Failure ?? Fail(change, $"ConfigMgr did not create the deployment of {target}. The log has the details.")) with { Steps = steps };
        return await ReconcileAsync(change, obj, collectionId, options, snapshot, runStart, steps).ConfigureAwait(false);
    }

    /// <summary>
    /// After a failure during the create: a deployment of the object to the collection that was
    /// not in the snapshot taken before the create, made no earlier than the run, and holding
    /// the planned values is the run's own. Anything else is reported as not known, without Undo.
    /// </summary>
    private async Task<DeploymentOutcome> ReconcileAsync(DeploymentChange change, DeployableObject obj, string collectionId, DeploymentOptions options, SnapshotRow snapshot, DateTime runStart, List<string> steps)
    {
        var target = $"{ObjectText(obj)} to collection {collectionId}";
        var unknown = $"The result of the create of {target} is not known: ConfigMgr reported an error during the create. Check the deployments in the console. Undo is not available because a deployment found now cannot be shown to come from this change.";
        IReadOnlyList<DeploymentRow> rows;
        try
        {
            rows = Rows<DeploymentRow>(await ReadAsync(ObjectParameters("Deployments", obj), CancellationToken.None).ConfigureAwait(false), "Deployment");
        }
        catch (DeploymentServiceException)
        {
            return Fail(change, unknown) with { Steps = steps };
        }
        var before = (snapshot.Ids ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = rows.Where(d => string.Equals(d.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase) && !before.Contains(d.DeploymentId ?? "")
            && (obj.Type != DeploymentObjectType.Package || string.Equals(d.Program, obj.Program, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count != 1) return Fail(change, unknown) with { Steps = steps };
        var found = ToRecord(obj.Type, candidates[0]) with { ObjectName = obj.Name };
        var recent = found.Created == DateTime.MinValue || found.Created >= runStart - ClockSkew;
        if (StoredDifferences(obj.Type, candidates[0], options).Count > 0 || !recent) return Fail(change, unknown) with { Steps = steps };
        _created[found.DeploymentId] = 0;
        var undo = new DeploymentChange(DeploymentChangeKind.RemoveDeployment, obj, change.Collection, null, [], false, false, false, found with { ObjectName = obj.Name },
            [], [$"Removes deployment {found.DeploymentId}, which the create made.", UndoNote], found.DeploymentId);
        return Partial(change, $"ConfigMgr reported an error during the create of {target}, but deployment {found.DeploymentId} appeared with the planned purpose and times and was not there before the create. Check its other settings in the console. Undo removes it.", undo, steps);
    }

    /// <summary>The planned settings a stored deployment does not hold; the same rules as the read-back in the script.</summary>
    internal static List<string> StoredDifferences(DeploymentObjectType type, DeploymentRow r, DeploymentOptions o)
    {
        var diff = new List<string>();
        var required = o.Purpose == DeploymentPurposes.Required;
        void Check(bool same, string name) { if (!same) diff.Add(name); }
        Check(r.Purpose == o.Purpose, "Purpose");
        Check(ParseTime(r.Available) == o.AvailableTime, "AvailableTime");
        Check(!required || ParseTime(r.Deadline) == o.Deadline, "Deadline");
        Check(r.Utc == o.UtcBasis, "TimeBasis");
        if (DeploymentScheduleRules.HasNotification(type)) Check(r.Notification == o.UserNotification, "UserNotification");
        if (required)
        {
            Check(r.Override == o.OverrideServiceWindow, "OverrideServiceWindow");
            Check(r.Reboot == o.RebootOutsideServiceWindow, "RebootOutsideServiceWindow");
            Check(r.Metered == o.AllowMetered, "AllowMetered");
        }
        switch (type)
        {
            case DeploymentObjectType.Package:
                Check(r.Network == o.NetworkBehavior, "NetworkBehavior");
                Check(r.Slow == o.SlowNetworkBehavior, "SlowNetworkBehavior");
                Check(r.AllowFallback == o.AllowFallback, "AllowFallback");
                Check(!required || r.Rerun == o.RerunBehavior, "RerunBehavior");
                Check(!r.ExpirationEnabled, "Expiration");
                break;
            case DeploymentObjectType.TaskSequence:
                Check(r.Network == o.NetworkBehavior, "NetworkBehavior");
                Check(r.Availability == o.TaskSequenceAvailability, "TaskSequenceAvailability");
                Check(r.ShowProgress == o.ShowProgress, "ShowProgress");
                Check(r.AllowFallback == o.AllowFallback, "AllowFallback");
                Check(!required || r.Rerun == o.RerunBehavior, "RerunBehavior");
                Check(!r.ExpirationEnabled, "Expiration");
                break;
            case DeploymentObjectType.SoftwareUpdateGroup:
                Check(r.PostRebootScan == o.PostRebootScan, "PostRebootScan");
                if (required)
                {
                    Check(r.SuppressRestart == o.SuppressRestart, "SuppressRestart");
                    Check(r.FallbackToDp == o.FallbackToDp, "FallbackToDp");
                    Check(r.AllowMicrosoftUpdate == o.AllowMicrosoftUpdate, "AllowMicrosoftUpdate");
                }
                break;
        }
        return diff;
    }

    private DeploymentChange Undo(DeploymentChange change, DeployableObject obj, DeploymentRow stored)
    {
        var record = ToRecord(obj.Type, stored) with { ObjectName = obj.Name };
        _created[record.DeploymentId] = 0;
        return new DeploymentChange(DeploymentChangeKind.RemoveDeployment, obj, change.Collection, null, [], false, false, false, record, [],
            [$"Removes deployment {record.DeploymentId} of {ObjectText(obj)} from collection {Shown(record.CollectionName, record.CollectionId)}.", UndoNote], record.DeploymentId);
    }

    private DeploymentChange? UndoFromPlan(DeploymentChange change, DeployableObject obj, string collectionId, DeploymentOptions options, string deploymentId)
    {
        if (!IsDeploymentId(obj.Type, deploymentId)) return null;
        var record = PlannedRecord(obj, collectionId, options, deploymentId) with { CollectionName = change.Collection?.Name ?? "" };
        _created[deploymentId] = 0;
        return new DeploymentChange(DeploymentChangeKind.RemoveDeployment, obj, change.Collection, null, [], false, false, false, record, [],
            [$"Removes deployment {deploymentId} of {ObjectText(obj)} from collection {collectionId}.", UndoNote], deploymentId);
    }

    private static DeploymentRecord PlannedRecord(DeployableObject obj, string collectionId, DeploymentOptions options, string deploymentId) =>
        new(deploymentId, obj.Type, obj.Id, obj.Name, collectionId, "", options.Purpose,
            options.AvailableTime, options.Purpose == DeploymentPurposes.Required ? options.Deadline : null, DateTime.MinValue, "", options.UtcBasis, obj.Program ?? "");

    private static string StatusText(string? status) => status switch
    {
        "object-missing" => "The object does not exist any more.",
        "program-missing" => "The program does not exist in the package any more.",
        "object-retired" => "The application was retired.",
        "collection-missing" => "The collection does not exist any more.",
        "not-device" => "The collection is not a device collection.",
        "built-in" => "The collection is a built-in collection.",
        _ => "The state changed."
    };

    internal static string SettingText(string name) => name switch
    {
        "AvailableTime" => "available time",
        "Deadline" => "deadline",
        "TimeBasis" => "time basis",
        "UserNotification" => "user notification",
        "OverrideServiceWindow" => "install outside maintenance windows",
        "RebootOutsideServiceWindow" => "restart outside maintenance windows",
        "AllowMetered" => "metered connection",
        "NetworkBehavior" => "network or content option",
        "SlowNetworkBehavior" => "slow network option",
        "AllowFallback" => "fallback",
        "RerunBehavior" => "rerun behavior",
        "Expiration" => "expiration (ConfigMgr enabled an expiration time)",
        "TaskSequenceAvailability" => "availability",
        "ShowProgress" => "show progress",
        "PostRebootScan" => "full scan after restart",
        "SuppressRestart" => "restart suppression",
        "FallbackToDp" => "fallback to the default site boundary group",
        "AllowMicrosoftUpdate" => "Microsoft Update fallback",
        _ => name.ToLowerInvariant()
    };

    private static DeploymentAuditRecord CreateRecord(DeploymentChange change, DeployableObject obj, string collectionId, DeploymentOptions options, DeploymentOutcome outcome)
    {
        var id = outcome.UndoChange?.CreatedId ?? "";
        var assignment = outcome.UndoChange?.Existing?.AssignmentId;
        return BaseRecord(obj, change.Collection, collectionId, options.Purpose, options.Deadline,
            assignment is { Length: > 0 } ? assignment : id,
            outcome.Succeeded ? AuditResults.Success
            : id.Length > 0 ? AuditResults.Partial
            : outcome.Message.StartsWith("The result of the create", StringComparison.Ordinal) ? AuditResults.Unknown
            : AuditResults.Failed) with
        {
            Event = AuditEvents.Create,
            AvailableDateTime = options.AvailableTime,
            TimeBasedOn = options.UtcBasis ? "Utc" : "LocalTime",
            DeploymentUniqueID = id,
            Message = outcome.Message
        };
    }

    private static DeploymentAuditRecord BaseRecord(DeployableObject obj, DeploymentCollection? collection, string collectionId, string purpose, DateTime? deadline, string deploymentId, string result) =>
        new(default, "", obj.Type == DeploymentObjectType.SoftwareUpdateGroup ? "SUG" : obj.Type.ToString(), obj.Name,
            obj.Type switch
            {
                DeploymentObjectType.Package => obj.Program,
                DeploymentObjectType.SoftwareUpdateGroup => $"({obj.UpdateCount.ToString(CultureInfo.InvariantCulture)} updates)",
                _ => obj.Version
            },
            collection?.Name ?? "", collectionId, collection?.MemberCount, purpose, "Install", deadline, deploymentId, result)
        {
            ObjectId = obj.Id,
            ProgramName = obj.Type == DeploymentObjectType.Package ? obj.Program : ""
        };
    // ---- Distribute ---------------------------------------------------------------------------

    private sealed record DistributionRun(bool Requested, string Message, IReadOnlyList<string> Steps, IReadOnlyList<string> GroupNames);

    private async Task<DistributionRun> DistributeAsync(DeployableObject obj, IReadOnlyList<string> requested)
    {
        IReadOnlyList<DpGroupInfo> groups;
        try
        {
            groups = await ListDpGroupsAsync(obj, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DeploymentServiceException ex)
        {
            return new DistributionRun(false, ex.Message, [], []);
        }
        var resolved = requested.Select(r => groups.FirstOrDefault(g => string.Equals(g.Id, r, StringComparison.OrdinalIgnoreCase))
            ?? groups.FirstOrDefault(g => string.Equals(g.Name, r, StringComparison.OrdinalIgnoreCase))).ToList();
        if (resolved.Any(g => g is null)) return new DistributionRun(false, "A DP group of the plan does not exist any more.", [], []);
        var parameters = ObjectParameters("Distribute", obj);
        parameters["GroupIds"] = resolved.Select(g => g!.Id).ToArray();
        var run = await RunWriteAsync(null, parameters).ConfigureAwait(false);
        if (run.Result is null) return new DistributionRun(false, "The result of the distribution request is not known. Check the content status in the console.", [], resolved.Select(g => g!.Name).ToList());
        var steps = new List<string>();
        var ok = true;
        foreach (var group in resolved)
        {
            var row = run.Groups.FirstOrDefault(r => string.Equals(r.GroupId, group!.Id, StringComparison.OrdinalIgnoreCase));
            var line = row?.Status switch
            {
                "requested" => $"Requested distribution to DP group {group!.Name}.",
                "already" => $"DP group {group!.Name} already has the content.",
                "group-name-wildcard" => $"DP group {group!.Name} has a wildcard character in its name; distribution was not requested.",
                "group-missing" => $"DP group {group!.Name} does not exist any more.",
                "not-targeted" => $"ConfigMgr accepted the request for DP group {group!.Name}, but the group does not list the content.",
                _ => $"Distribution to DP group {group!.Name} failed. The log has the details."
            };
            if (row?.Status is not ("requested" or "already")) ok = false;
            if (!string.IsNullOrEmpty(row?.Detail)) _log?.Warning(LogArea, $"Distribution to one group returned {row.Status}: {CollectionService.FailureKinds(row.Detail)}.");
            steps.Add(line);
        }
        return new DistributionRun(ok, ok ? "The distribution was requested." : "Distribution to at least one DP group failed.", steps, resolved.Select(g => g!.Name).ToList());
    }

    private static DeploymentAuditRecord DistributionRecord(DeployableObject obj, IReadOnlyList<string> requested, DistributionRun run) =>
        BaseRecord(obj, null, "", "", null, "", run.Requested ? AuditResults.Success : AuditResults.Failed) with
        {
            Event = AuditEvents.Distribute,
            DistributionPointGroups = string.Join("; ", run.GroupNames.Count > 0 ? run.GroupNames : requested),
            Message = run.Message
        };
    private async Task<DeploymentOutcome> ApplyDistributeAsync(DeploymentChange change)
    {
        if (change.Object is not { } obj || change.Checks.Count == 0 || change.DpGroups is not { Count: > 0 }) return Refused(change, NotPreparedMessage);
        if (obj.Type == DeploymentObjectType.SoftwareUpdateGroup) return Refused(change, "the content of a software update group is in deployment packages.");
        if (change.Blocked) return Refused(change, "the plan has a check that did not pass.");
        using var reservation = _audit.Reserve();
        if (!reservation.Probe.Writable) return Refused(change, $"the audit log cannot be written, so nothing was distributed. {reservation.Probe.Message}");
        var run = await DistributeAsync(obj, change.DpGroups).ConfigureAwait(false);
        var written = reservation.Append(DistributionRecord(obj, change.DpGroups, run));
        var message = run.Requested
            ? $"Requested the distribution of {ObjectText(obj)} to {Count(change.DpGroups.Count, "DP group")}. {NoDistributionUndo}"
            : $"The distribution of {ObjectText(obj)} did not finish: {run.Message} {NoDistributionUndo}";
        if (!written.Succeeded) message += $" The audit record could not be written: {written.Message}";
        return new DeploymentOutcome(change, run.Requested, message, null, run.Steps);
    }

    // ---- Remove -------------------------------------------------------------------------------

    private async Task<DeploymentOutcome> ApplyRemoveAsync(DeploymentChange change)
    {
        if (change.Existing is not { } existing || !IsDeploymentId(existing.Type, existing.DeploymentId) || !IsObjectId(existing.Type, existing.ObjectId))
            return Refused(change, NotPreparedMessage);
        if (!PackageIdRegex().IsMatch(existing.CollectionId ?? "")) return Refused(change, NotPreparedMessage);
        switch (Ownership(existing.DeploymentId))
        {
            case false: return Refused(change, $"deployment {existing.DeploymentId} was not created by this product, so it is not removed.");
            case null: return Refused(change, $"the audit log could not be read, so it is not known whether this product created deployment {existing.DeploymentId}. It is not removed.");
        }
        using var reservation = _audit.Reserve();
        if (!reservation.Probe.Writable) return Refused(change, $"the audit log cannot be written, so nothing was removed. {reservation.Probe.Message}");

        var obj = change.Object ?? new DeployableObject(existing.Type, existing.ObjectId, existing.ObjectName, "", existing.Program, false);
        var parameters = new ConfigMgrParameters
        {
            { "Operation", "Remove" }, { "Type", existing.Type.ToString() }, { "DeploymentId", existing.DeploymentId }, { "ObjectId", existing.ObjectId }, { "CollectionId", existing.CollectionId!.ToUpperInvariant() },
            { "Purpose", existing.Purpose }, { "Available", TimeText(existing.AvailableTime) }, { "Deadline", existing.Deadline is { } d ? TimeText(d) : "" }, { "Utc", existing.UtcBasis }
        };
        if (existing.Type == DeploymentObjectType.Application && NumberRegex().IsMatch(obj.ModelId ?? "")) parameters["ModelId"] = obj.ModelId;
        if (existing.Type == DeploymentObjectType.Package) parameters.AddSecret("Program", existing.Program ?? "");
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        var what = $"deployment {existing.DeploymentId} of {Noun(existing.Type).ToLowerInvariant()} {Shown(existing.ObjectName, existing.ObjectId)} from collection {Shown(existing.CollectionName, existing.CollectionId)}";
        DeploymentOutcome outcome = run.Result?.Status switch
        {
            "ok" => Done(change, $"Removed {what}. Software that clients already installed stays installed.", null, run.Steps),
            "removed-with-error" => Done(change, $"Removed {what}; ConfigMgr reported an error, and the read-back shows it is gone. Software that clients already installed stays installed.", null, run.Steps),
            "missing" => Refused(change, $"{what} does not exist any more."),
            "changed" => Refused(change, $"{what} changed since it was captured ({string.Join(", ", (run.Result.Differences ?? []).Select(SettingText))}), so it was not removed."),
            "remove-failed" => Fail(change, $"ConfigMgr did not remove {what}. The log has the details."),
            "removed-others" => Fail(change, $"ConfigMgr removed {what} and also other deployments of the object to the collection: {string.Join(", ", run.Result.Ids ?? [])}. Check them in the console."),
            "invalid" => Refused(change, NotPreparedMessage),
            _ => null!
        };
        if (outcome is null)
        {
            if (run.Uncertain)
            {
                var (read, row) = await ReadDeploymentOrNullAsync(existing.Type, existing.DeploymentId).ConfigureAwait(false);
                outcome = read && row is null
                    ? Done(change, $"Removed {what}; ConfigMgr reported an error, and the read-back shows it is gone. Software that clients already installed stays installed.", null, [])
                    : Fail(change, $"The result of the removal of {what} is not known. Check it in the console.");
            }
            else outcome = run.Failure ?? Fail(change, $"ConfigMgr did not remove {what}. The log has the details.");
        }
        var record = BaseRecord(obj with { Name = existing.ObjectName }, new DeploymentCollection(existing.CollectionId!, existing.CollectionName, "", 0) { }, existing.CollectionId!,
            existing.Purpose, existing.Deadline, existing.AssignmentId is { Length: > 0 } a ? a : existing.DeploymentId, outcome.Succeeded ? AuditResults.Success : AuditResults.Failed) with
        {
            MemberCount = null,
            DeployAction = "",
            Event = AuditEvents.Undo,
            AvailableDateTime = existing.AvailableTime,
            TimeBasedOn = existing.UtcBasis ? "Utc" : "LocalTime",
            DeploymentUniqueID = existing.DeploymentId,
            Undoes = existing.DeploymentId,
            Message = outcome.Message
        };
        var written = reservation.Append(record);
        if (!written.Succeeded) outcome = outcome with { Message = outcome.Message + $" The audit record could not be written: {written.Message}" };
        if (outcome.Succeeded) _created.TryRemove(existing.DeploymentId, out _);
        return outcome;
    }

    // ---- Write runs ---------------------------------------------------------------------------

    /// <summary>
    /// One write script run. <c>Uncertain</c> marks a failure that can follow a change ConfigMgr
    /// already made: the session failed during the write.
    /// </summary>
    private sealed record WriteRun(ResultRow? Result, IReadOnlyList<DeploymentRow> Rows, IReadOnlyList<GroupResultRow> Groups, DeploymentOutcome? Failure, bool Uncertain, IReadOnlyList<string> Steps);

    private async Task<WriteRun> RunWriteAsync(DeploymentChange? change, ConfigMgrParameters parameters)
    {
        ResultRow? result;
        ConfigMgrCommandResult output;
        try
        {
            output = await _session.InvokeAsync(Script, parameters, CancellationToken.None).ConfigureAwait(false);
            result = Rows<ResultRow>(output, "Result").LastOrDefault();
        }
        catch (ConfigMgrSessionException ex)
        {
            _log?.Warning(LogArea, $"A change failed in the ConfigMgr session: {ex.Failure}, {CollectionService.FailureKinds(ex.Message)}.");
            var message = ex.Failure switch
            {
                ConfigMgrFailure.AccessDenied => $"ConfigMgr denied the change. {AccessHint}",
                ConfigMgrFailure.Timeout => "The result of the change is not known: ConfigMgr did not answer in time. Refresh the lists to see the current state.",
                ConfigMgrFailure.ProcessDied => "The result of the change is not known: the ConfigMgr session ended during the change. Refresh the lists to see the current state.",
                ConfigMgrFailure.Unexpected => "The result of the change is not known: the ConfigMgr session failed. Refresh the lists to see the current state.",
                ConfigMgrFailure.ScriptFailed => "ConfigMgr reported an error for the change. The log has the details.",
                _ => null
            };
            if (message is null)
                return new WriteRun(null, [], [], change is null ? null : Refused(change, $"The change was not made. {ComplianceService.SessionProblem(ex.Failure).Replace(ComplianceService.AccessHint, AccessHint, StringComparison.Ordinal)}"), false, []);
            var uncertain = ex.Failure is ConfigMgrFailure.Timeout or ConfigMgrFailure.ProcessDied or ConfigMgrFailure.Unexpected or ConfigMgrFailure.ScriptFailed;
            return new WriteRun(null, [], [], change is null ? null : Fail(change, message), uncertain, []);
        }
        catch (DeploymentServiceException)
        {
            return new WriteRun(null, [], [], change is null ? null : Fail(change, "ConfigMgr returned data that could not be read. Refresh the lists to see the current state."), true, []);
        }
        if (result is null)
            return new WriteRun(null, [], [], change is null ? null : Fail(change, "ConfigMgr returned no result for the change. Refresh the lists to see the current state."), true, []);
        if (!string.IsNullOrWhiteSpace(result.Detail))
            _log?.Warning(LogArea, $"{parameters["Operation"]} returned {result.Status}: {CollectionService.FailureKinds(result.Detail)}.");
        var steps = (result.Steps ?? []).Select(StepLine).ToList();
        return new WriteRun(result, Rows<DeploymentRow>(output, "Deployment"), Rows<GroupResultRow>(output, "Group"), null, false, steps);
    }

    private static string StepLine(string step) => step switch
    {
        "created" => "Created the deployment.",
        "create-error" => "ConfigMgr reported an error during the create.",
        "removed" => "Removed the deployment.",
        "remove-error" => "ConfigMgr reported an error during the removal.",
        _ => step
    };

    private DeploymentOutcome Partial(DeploymentChange change, string message, DeploymentChange? undo, IReadOnlyList<string> steps)
    {
        _log?.Warning(LogArea, $"{change.Kind} did not finish after ConfigMgr made a change; the outcome carries its undo.");
        return new DeploymentOutcome(change, false, message, undo, steps);
    }

    private static DeploymentOutcome Fail(DeploymentChange change, string message) => new(change, false, message, null, []);

    /// <summary>An outcome for a change that was not attempted: refused, stale or not prepared.</summary>
    private static DeploymentOutcome Refused(DeploymentChange change, string message) =>
        new(change, false, message.StartsWith(SkippedPrefix, StringComparison.Ordinal) ? message : SkippedPrefix + message, null, []);

    private static DeploymentOutcome Done(DeploymentChange change, string message, DeploymentChange? undo, IReadOnlyList<string> steps) =>
        new(change, true, message, undo, steps);
}
