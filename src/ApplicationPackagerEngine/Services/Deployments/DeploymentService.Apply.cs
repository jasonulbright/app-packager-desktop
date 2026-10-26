using ApplicationPackagerEngine.Services.Logging;
using System.Globalization;
using System.Text.Json;
using ApplicationPackagerEngine.Services.ConfigMgrSession;

namespace ApplicationPackagerEngine.Services;

public sealed partial class DeploymentService
{
    /// <summary>Starts the message of a create that ConfigMgr did not make: the deployments were read after the call and none was new.</summary>
    internal const string NotCreatedPrefix = "ConfigMgr did not create the deployment of ";

    internal const string NotPreparedMessage = "The change has no prepared checks, so it was not applied. Validate again.";
    internal const string RequiredNotConfirmedMessage =
        "A Required deployment installs on every device of the collection and needs its own confirmation, so nothing was created.";
    internal const string BackdatedNotConfirmedMessage =
        "The available time is in the past and needs its own confirmation, so nothing was created.";

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
        if (change.Prepared is not { } prepared) return Refused(change, NotPreparedMessage);
        options = Seconds(options);
        if (options.Purpose == DeploymentPurposes.Required && !change.RequiredConfirmed) return Refused(change, RequiredNotConfirmedMessage);
        var verdict = DeploymentScheduleRules.Check(obj.Type, options, NowIn(options.UtcBasis));
        if (verdict.Error is { } scheduleError) return Refused(change, scheduleError);
        if (DeploymentScheduleRules.OptionProblem(obj.Type, options) is { } optionProblem) return Refused(change, optionProblem);
        if (verdict.Backdated && !change.BackdatedAcknowledged) return Refused(change, BackdatedNotConfirmedMessage);
        if (change.CreateNowContentDistributing && (change.DpGroups is not { Count: > 0 } || !ContentInProgress(change)))
            return Refused(change, CreateNowWithoutDistributionMessage);
        var requiredApplication = obj.Type == DeploymentObjectType.Application && options.Purpose == DeploymentPurposes.Required && _signing is not null;

        // The target lock comes first: a second session that waits for the same object and
        // collection then reads the lock as the cause, not the audit log the first session holds.
        using var targetLock = _targetLock.TryAcquire(LockKey(obj), collectionId);
        if (targetLock is null) return Refused(change, TargetBusyMessage);
        using var reservation = _audit.Reserve();
        if (!reservation.Probe.Writable)
            return Refused(change, $"the audit log cannot be written, so the deployment was not created. {reservation.Probe.Message}");

        var steps = new List<string>();
        DeploymentOutcome? distributed = null;
        if (change.DpGroups is { Count: > 0 })
        {
            var distribution = await DistributeAsync(obj, change.DpGroups).ConfigureAwait(false);
            steps.AddRange(distribution.Steps);
            var written = SafeAppend(reservation, () => DistributionRecord(obj, change.DpGroups, distribution).WithRing(change.Ring));
            distributed = DistributionOutcome(change, obj, distribution, written);
            if (!written.Succeeded)
                return Fail(change, $"The content distribution may have been requested but could not be logged, so the deployment was not created. {written.Message}") with { Steps = steps, Distribution = distributed, NotCreated = true };
            if (!distribution.Requested)
                return Fail(change, $"The content distribution failed, so the deployment was not created. {distribution.Message}") with { Steps = steps, Distribution = distributed, NotCreated = true };
        }
        var outcome = await CreateAfterDistributionAsync(change, obj, collectionId, options, prepared, requiredApplication, reservation, steps).ConfigureAwait(false);
        if (distributed is null) return outcome;
        return outcome.Message.StartsWith(SkippedPrefix, StringComparison.Ordinal)
            ? outcome with { Message = SkippedPrefix + "content distribution was requested; the deployment was not created because " + outcome.Message[SkippedPrefix.Length..], Distribution = distributed }
            : outcome with { Distribution = distributed };
    }

    /// <summary>The Distribute outcome of the distribution a combined create requested first.</summary>
    private static DeploymentOutcome DistributionOutcome(DeploymentChange change, DeployableObject obj, DistributionRun run, AuditWrite written)
    {
        var distribute = new DeploymentChange(DeploymentChangeKind.Distribute, obj, null, null, change.DpGroups, false, false, false, null, change.Checks, [], null);
        var message = run.Requested
            ? $"Requested the distribution of {ObjectText(obj)} to {Count(change.DpGroups.Count, "DP group")}. {NoDistributionUndo}"
            : $"The distribution of {ObjectText(obj)} did not finish: {run.Message} {NoDistributionUndo}";
        if (!written.Succeeded) message += $" The audit record could not be written: {written.Message}";
        return new DeploymentOutcome(distribute, run.Requested, message, null, run.Steps);
    }

    private async Task<DeploymentOutcome> CreateAfterDistributionAsync(DeploymentChange change, DeployableObject obj, string collectionId, DeploymentOptions options,
        DeploymentPreparedState prepared, bool requiredApplication, IDeploymentAuditReservation reservation, List<string> steps)
    {
        StateRead state;
        try
        {
            state = await ReadStateAsync(obj, collectionId, options.Purpose, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DeploymentServiceException ex)
        {
            return Refused(change, $"the current state could not be read, so nothing was created. {ex.Message}") with { Steps = steps };
        }
        if (Stale(obj, collectionId, state) is { } stale) return Refused(change, stale) with { Steps = steps };
        if (StalePlan(obj, options, prepared, state) is { } changed) return Refused(change, changed) with { Steps = steps };
        if (requiredApplication)
        {
            try
            {
                if (SigningRefusal(SiteApplicationOf(state), state.NameCount, obj.Name) is { } refusal) return Refused(change, refusal) with { Steps = steps };
            }
            catch (DeploymentServiceException ex)
            {
                return Refused(change, ex.Message) with { Steps = steps };
            }
        }
        if (state.Snapshot is not { Read: true } snapshot)
            return Refused(change, "the existing deployments could not be read, so the duplicate check failed and nothing was created.") with { Steps = steps };
        if ((snapshot.Duplicates ?? []).Length > 0)
            return Refused(change, $"{ObjectText(obj)} is now deployed to collection {collectionId} (deployment ID {string.Join(", ", snapshot.Duplicates!)}), so no duplicate was created.") with { Steps = steps };
        var current = obj with
        {
            ModelId = state.Object!.ModelId ?? obj.ModelId,
            Program = obj.Type == DeploymentObjectType.Package ? state.Object.Program ?? obj.Program : obj.Program,
            UpdateCount = obj.Type == DeploymentObjectType.SoftwareUpdateGroup ? state.Object.UpdateCount : obj.UpdateCount
        };

        var runStart = _utcNow();
        var parameters = CreateParameters(current, collectionId, options, !change.CreateNowContentDistributing);
        parameters["RunStart"] = TimeText(runStart);
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        steps.AddRange(run.Steps);
        var planned = change with { Object = current, Options = options };
        DeploymentOutcome outcome;
        try
        {
            outcome = await CreateOutcomeAsync(planned, current, collectionId, options, run, snapshot, runStart, steps).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = AfterCreateFailure(planned, current, collectionId, options, run, steps, ex);
        }
        if (UnclearIds(current.Type, run.Result) is { Count: > 0 } unclear) outcome = outcome with { UnclearDeploymentIds = unclear };

        AuditWrite audit;
        try
        {
            audit = SafeAppend(reservation, () => CreateRecord(change, current, collectionId, options, outcome, runStart, run.Result).WithRing(change.Ring));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            audit = new AuditWrite(false, $"The audit record could not be built ({ex.GetType().Name}).");
        }
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

    internal const string CreateNowWithoutDistributionMessage =
        "'Create now, content still distributing' applies only to a plan that requests the content distribution first and shows it in progress. Nothing was created.";

    internal const string TargetBusyMessage =
        "another session of this product is creating a deployment of the same object to this collection now, or the lock file next to the audit log cannot be made. Nothing was created. Try again when that session finishes.";

    private static bool ContentInProgress(DeploymentChange change) =>
        change.Checks.Any(c => c.Name == DeploymentCheckNames.ContentDistributed && c.Result == DeploymentCheckResults.Warn
            && c.Message.StartsWith("Content distribution in progress", StringComparison.Ordinal));

    /// <summary>The application lock key is the ModelID: it stays the same across revisions.</summary>
    private static string LockKey(DeployableObject obj) =>
        obj.Type == DeploymentObjectType.Application && NumberRegex().IsMatch(obj.ModelId ?? "") ? "M" + obj.ModelId : obj.Id.Trim().ToUpperInvariant();

    /// <summary>An append that reports every failure, also one the log does not expect, as a failed write.</summary>
    private AuditWrite SafeAppend(IDeploymentAuditReservation reservation, Func<DeploymentAuditRecord> record)
    {
        try
        {
            return reservation.Append(record());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryWarn($"An audit record could not be written ({ex.GetType().Name}).");
            return new AuditWrite(false, "the append failed. The log has the details.");
        }
    }

    /// <summary>
    /// The outcome when building the result of a create failed: a deployment the script read back
    /// keeps its Undo, which removes it only while it holds the planned values.
    /// </summary>
    private DeploymentOutcome AfterCreateFailure(DeploymentChange change, DeployableObject obj, string collectionId, DeploymentOptions options, WriteRun run, List<string> steps, Exception ex)
    {
        TryWarn($"The result of a create could not be processed ({ex.GetType().Name}).");
        var id = run.Result?.CreatedId ?? "";
        var target = $"{ObjectText(obj)} to collection {collectionId}";
        if (run.Result?.Status is "ok" or "created-with-error" or "stored-differs" or "readback-failed" or "concurrent" && UndoFromPlan(change, obj, collectionId, options, id) is { } undo)
            return new DeploymentOutcome(change, false,
                $"ConfigMgr created the deployment of {target} (deployment ID {id}), but its result could not be processed. Check it in the console. Undo removes it when it still holds the planned values.", undo, steps);
        return new DeploymentOutcome(change, false,
            $"The result of the create of {target} is not known: its result could not be processed. Check the deployments in the console. Undo is not available because a deployment found now cannot be shown to come from this change.", null, steps);
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

    internal const string StalePlanPrefix = "the plan is stale, refresh the plan: ";

    private static string RevisionText(string revision) => revision.Length > 0 ? revision : "none";

    /// <summary>A warning that cannot replace the outcome it describes, also when the log fails.</summary>
    private void TryWarn(string message)
    {
        try
        {
            _log?.Warning(LogArea, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <summary>
    /// The difference between the state Prepare read and the state now: another object ID or
    /// revision, other content, or for Required more collection members. Fewer members only
    /// narrow a deployment, and an Available deployment installs nothing by itself.
    /// </summary>
    private static string? StalePlan(DeployableObject obj, DeploymentOptions options, DeploymentPreparedState prepared, StateRead state)
    {
        var now = PreparedState(state)!;
        if (!string.Equals(obj.Id.Trim(), prepared.ObjectId, StringComparison.OrdinalIgnoreCase) || !string.Equals(now.ObjectId, prepared.ObjectId, StringComparison.OrdinalIgnoreCase))
            return StalePlanPrefix + $"{ObjectText(obj)} is now {Noun(obj.Type).ToLowerInvariant()} ID {now.ObjectId} (a new revision or another object). Nothing was created.";
        if (!string.Equals(now.Revision, prepared.Revision, StringComparison.Ordinal))
            return StalePlanPrefix + $"{ObjectText(obj)} changed since the plan was shown (revision {RevisionText(prepared.Revision)} is now {RevisionText(now.Revision)}). Nothing was created.";
        if (!now.ContentIds.SequenceEqual(prepared.ContentIds, StringComparer.OrdinalIgnoreCase))
            return StalePlanPrefix + $"the content that {ObjectText(obj)} references changed since the plan was shown ({string.Join(", ", prepared.ContentIds)} is now {string.Join(", ", now.ContentIds)}). Nothing was created.";
        if (options.Purpose == DeploymentPurposes.Required && now.MemberCount > prepared.MemberCount)
            return StalePlanPrefix + $"the collection has {Count(now.MemberCount, "member")} now and had {prepared.MemberCount.ToString(CultureInfo.InvariantCulture)} when the plan was shown. A Required deployment installs on every member. Nothing was created.";
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
                return Partial(change, $"ConfigMgr created the deployment of {target} (deployment ID {stored.DeploymentId}) but stored other values than the plan: {string.Join(", ", (result.Differences ?? []).Select(SettingText))}. Check it in the console or remove it with Undo.{OthersText(result, stored.DeploymentId)}", Undo(change, obj, stored), steps);
            case "concurrent" when stored is not null:
                return Partial(change, $"ConfigMgr created the deployment of {target} (deployment ID {stored.DeploymentId}). The stored values equal the plan.{OthersText(result, stored.DeploymentId)}", Undo(change, obj, stored), steps);
            case "readback-failed":
                return Partial(change, $"ConfigMgr created the deployment of {target} (deployment ID {result.CreatedId}), but it could not be read back, so its stored values are not known. Undo removes it when it still holds the planned values.",
                    UndoFromPlan(change, obj, collectionId, options, result.CreatedId ?? ""), steps);
            case "create-unclear":
                var ids = result.Ids ?? [];
                if (ids.Length > 1)
                    return Fail(change, $"The result of the create of {target} is not known: {ids.Length} new deployments appeared: {string.Join("; ", ids.Select(i => CreationText(i, run.Proofs)))}. They have the same object, collection and account, so this product cannot tell which one it made. Check them in the console and remove the one that must not stay there. Undo is not available because neither deployment can be shown to come from this change.") with { Steps = steps };
                return Fail(change, $"The result of the create of {target} is not known: {(ids.Length == 1 ? $"ConfigMgr reported an error, and new deployment {ids[0]} cannot be shown to be made by this account during this run" : "the deployments could not be read after the create")}. Check the deployments in the console. Undo is not available because a deployment found now cannot be shown to come from this change.") with { Steps = steps };
            case "create-failed":
                return Fail(change, $"{NotCreatedPrefix}{target}. The log has the details.") with { Steps = steps, NotCreated = true };
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
        if (!run.Uncertain) return (run.Failure ?? Fail(change, $"{NotCreatedPrefix}{target}. The log has the details.")) with { Steps = steps, NotCreated = true };
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
        ProofRow? proof;
        try
        {
            var parameters = ObjectParameters("Deployments", obj);
            parameters["CollectionId"] = collectionId;
            rows = Rows<DeploymentRow>(await ReadAsync(parameters, CancellationToken.None).ConfigureAwait(false), "Deployment");
            var before = (snapshot.Ids ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            rows = rows.Where(d => string.Equals(d.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase) && !before.Contains(d.DeploymentId ?? "")
                && (obj.Type != DeploymentObjectType.Package || string.Equals(d.Program, obj.Program, StringComparison.OrdinalIgnoreCase))).ToList();
            if (rows.Count != 1 || !IsDeploymentId(obj.Type, rows[0].DeploymentId)) return Fail(change, unknown) with { Steps = steps };
            var proofParameters = ObjectParameters("Proof", obj);
            proofParameters["DeploymentId"] = rows[0].DeploymentId;
            proofParameters["RunStart"] = TimeText(runStart);
            proof = Rows<ProofRow>(await ReadAsync(proofParameters, CancellationToken.None).ConfigureAwait(false), "Proof").FirstOrDefault();
        }
        catch (DeploymentServiceException)
        {
            return Fail(change, unknown) with { Steps = steps };
        }
        var found = ToRecord(obj.Type, rows[0]) with { ObjectName = obj.Name };
        if (StoredDifferences(obj.Type, rows[0], options).Count > 0 || proof is not { Owned: true } || !string.Equals(proof.DeploymentId, found.DeploymentId, StringComparison.OrdinalIgnoreCase))
            return Fail(change, unknown) with { Steps = steps };
        _created[found.DeploymentId] = 0;
        var undo = new DeploymentChange(DeploymentChangeKind.RemoveDeployment, obj, change.Collection, null, [], false, false, false, found with { ObjectName = obj.Name },
            [], [$"Removes deployment {found.DeploymentId}, which the create made.", UndoNote], found.DeploymentId);
        return Partial(change, $"ConfigMgr reported an error during the create of {target}, but deployment {found.DeploymentId} appeared with the planned values, was not there before the create, and was made by this account during this run. Undo removes it.", undo, steps);
    }

    /// <summary>A deployment ID with the creator and creation time ConfigMgr holds for it, when the script read them.</summary>
    private static string CreationText(string id, IReadOnlyList<ProofRow> proofs) =>
        proofs.FirstOrDefault(p => string.Equals(p.DeploymentId, id, StringComparison.OrdinalIgnoreCase)) is { } proof && ParseUtc(proof.Created) is { } created
            ? $"{id} (made by {(string.IsNullOrWhiteSpace(proof.CreatedBy) ? "an account not known" : proof.CreatedBy)} at {created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC)"
            : $"{id} (creation time not known)";

    private static string OthersText(ResultRow result, string? ours) =>
        (result.Ids ?? []).Length == 0 ? ""
            : $" A second deployment of the same object to this collection exists (made by someone else at the same time): {string.Join(", ", result.Ids!)}. The one made here is {ours}; Undo removes only that one.";

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

    private static DeploymentRecord PlannedRecord(DeployableObject obj, string collectionId, DeploymentOptions options, string deploymentId)
    {
        var planned = Seconds(options);
        return new(deploymentId, obj.Type, obj.Id, obj.Name, collectionId, "", planned.Purpose, planned.AvailableTime, planned.Deadline, DateTime.MinValue, "", planned.UtcBasis,
            obj.Type == DeploymentObjectType.Package ? obj.Program ?? "" : "", ModelId: obj.Type == DeploymentObjectType.Application ? obj.ModelId ?? "" : "");
    }

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

    /// <param name="runStart">The UTC time the create started; the History Undo compares it with the creation time ConfigMgr holds.</param>
    /// <param name="result">The result of the create script: its account becomes the record's user, as ConfigMgr names the creator.</param>
    private static DeploymentAuditRecord CreateRecord(DeploymentChange change, DeployableObject obj, string collectionId, DeploymentOptions options, DeploymentOutcome outcome,
        DateTime runStart, ResultRow? result)
    {
        var id = outcome.UndoChange?.CreatedId ?? "";
        var stored = outcome.UndoChange?.Existing;
        var assignment = stored?.AssignmentId;
        var unclear = string.Join("; ", UnclearIds(obj.Type, result));
        return BaseRecord(obj, change.Collection, collectionId, stored?.Purpose is { Length: > 0 } purpose ? purpose : options.Purpose, stored is null ? options.Deadline : stored.Deadline,
            assignment is { Length: > 0 } ? assignment : id,
            outcome.Succeeded ? AuditResults.Success
            : id.Length > 0 ? AuditResults.Partial
            : outcome.Message.StartsWith("The result of the create", StringComparison.Ordinal) ? AuditResults.Unknown
            : AuditResults.Failed) with
        {
            Event = AuditEvents.Create,
            AvailableDateTime = stored?.AvailableTime ?? options.AvailableTime,
            TimeBasedOn = (stored?.UtcBasis ?? options.UtcBasis) ? "Utc" : "LocalTime",
            DeploymentUniqueID = id,
            ProgramName = obj.Type == DeploymentObjectType.Package ? stored?.Program is { Length: > 0 } program ? program : obj.Program : "",
            ModelId = obj.Type == DeploymentObjectType.Application ? stored?.ModelId is { Length: > 0 } model ? model : obj.ModelId ?? "" : "",
            Message = outcome.Message,
            User = result?.Account is { Length: > 0 } account ? account : "",
            CreatedUtc = DateTime.SpecifyKind(runStart, DateTimeKind.Utc),
            UnclearDeploymentIds = unclear
        };
    }

    /// <summary>The new deployments a create whose result is not known reported; empty for every other result.</summary>
    private static IReadOnlyList<string> UnclearIds(DeploymentObjectType type, ResultRow? result) =>
        result?.Status == "create-unclear" ? (result.Ids ?? []).Where(i => IsDeploymentId(type, i)).ToList() : [];

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
        var written = reservation.Append(DistributionRecord(obj, change.DpGroups, run).WithRing(change.Ring));
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
        var ownership = await OwnershipAsync(existing, null).ConfigureAwait(false);
        if (ownership.Result != DeploymentCheckResults.Pass) return Refused(change, ownership.Reason);
        using var reservation = _audit.Reserve();
        if (!reservation.Probe.Writable) return Refused(change, $"the audit log cannot be written, so nothing was removed. {reservation.Probe.Message}");

        var held = ownership.Site is { } site ? ToRecord(existing.Type, site.Row) : existing;
        var obj = (change.Object ?? new DeployableObject(existing.Type, existing.ObjectId, existing.ObjectName, "", existing.Program, false)) with { Name = held.ObjectName.Length > 0 ? held.ObjectName : existing.ObjectName };
        var parameters = new ConfigMgrParameters
        {
            { "Operation", "Remove" }, { "Type", existing.Type.ToString() }, { "DeploymentId", existing.DeploymentId }, { "ObjectId", existing.ObjectId }, { "CollectionId", existing.CollectionId!.ToUpperInvariant() },
            { "Purpose", existing.Purpose }, { "Available", TimeText(existing.AvailableTime) }, { "Deadline", existing.Deadline is { } d ? TimeText(d) : "" }, { "Utc", existing.UtcBasis }
        };
        var modelId = existing.ModelId is { Length: > 0 } captured ? captured : obj.ModelId ?? "";
        if (existing.Type == DeploymentObjectType.Application && NumberRegex().IsMatch(modelId)) parameters["ModelId"] = modelId;
        if (existing.Type == DeploymentObjectType.Package) parameters.AddSecret("Program", existing.Program ?? "");
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        var what = $"deployment {existing.DeploymentId} of {Noun(existing.Type).ToLowerInvariant()} {Shown(held.ObjectName, held.ObjectId)} from collection {Shown(held.CollectionName, held.CollectionId)}";
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
                var (read, after) = await ReadDeploymentOrNullAsync(existing.Type, existing.DeploymentId).ConfigureAwait(false);
                outcome = read && after is null
                    ? Done(change, $"Removed {what}; ConfigMgr reported an error, and the read-back shows it is gone. Software that clients already installed stays installed.", null, [])
                    : Fail(change, $"The result of the removal of {what} is not known. Check it in the console.");
            }
            else outcome = run.Failure ?? Fail(change, $"ConfigMgr did not remove {what}. The log has the details.");
        }
        var record = BaseRecord(obj, new DeploymentCollection(existing.CollectionId!, held.CollectionName, "", 0) { }, existing.CollectionId!,
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
        var written = reservation.Append(record.WithRing(change.Ring));
        if (!written.Succeeded) outcome = outcome with { Message = outcome.Message + $" The audit record could not be written: {written.Message}" };
        if (outcome.Succeeded) _created.TryRemove(existing.DeploymentId, out _);
        return outcome;
    }

    // ---- Write runs ---------------------------------------------------------------------------

    /// <summary>
    /// One write script run. <c>Uncertain</c> marks a failure that can follow a change ConfigMgr
    /// already made: the session failed during the write.
    /// </summary>
    private sealed record WriteRun(ResultRow? Result, IReadOnlyList<DeploymentRow> Rows, IReadOnlyList<GroupResultRow> Groups, DeploymentOutcome? Failure, bool Uncertain, IReadOnlyList<string> Steps)
    {
        /// <summary>The creation proofs the create script read for new deployments.</summary>
        public IReadOnlyList<ProofRow> Proofs { get; init; } = [];
    }

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
        return new WriteRun(result, Rows<DeploymentRow>(output, "Deployment"), Rows<GroupResultRow>(output, "Group"), null, false, steps) { Proofs = Rows<ProofRow>(output, "Proof") };
    }

    private static string StepLine(string step) => step switch
    {
        "created" => "Created the deployment.",
        "relist-failed" => "The deployments of the object to the collection could not be read again after the create, so a deployment someone else made at the same time is not known.",
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
        new(change, false, message.StartsWith(SkippedPrefix, StringComparison.Ordinal) ? message : SkippedPrefix + message, null, []) { NotCreated = true };

    private static DeploymentOutcome Done(DeploymentChange change, string message, DeploymentChange? undo, IReadOnlyList<string> steps) =>
        new(change, true, message, undo, steps);
}
