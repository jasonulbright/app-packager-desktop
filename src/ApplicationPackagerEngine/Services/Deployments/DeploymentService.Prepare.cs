using ApplicationPackagerEngine.Services.Logging;
using System.Globalization;

namespace ApplicationPackagerEngine.Services;

public sealed partial class DeploymentService
{
    internal const string UndoNote = "Undo removes the deployment. Software that clients already installed stays installed.";
    internal const string NoDistributionUndo = "Content distribution has no Undo: the content stays on the DP groups until it is removed in the console.";
    internal const string CreatedHere = "CreatedHere";
    internal const string Unchanged = "DeploymentUnchanged";

    private static PreflightCheck Check(string name, string result, string message) => new(name, result, message);

    public async Task<DeploymentChange> PrepareAsync(DeploymentChange request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = request.Kind switch
        {
            DeploymentChangeKind.Create => await PrepareCreateAsync(request, ct).ConfigureAwait(false),
            DeploymentChangeKind.Distribute => await PrepareDistributeAsync(request, ct).ConfigureAwait(false),
            DeploymentChangeKind.RemoveDeployment => await PrepareRemoveAsync(request, ct).ConfigureAwait(false),
            _ => throw new DeploymentServiceException("The change kind is not known.")
        };
        _log?.Info(LogArea, $"Prepared {request.Kind}: {prepared.Checks.Count(c => c.Result == DeploymentCheckResults.Pass)} checks passed, " +
            $"{prepared.Checks.Count(c => c.Result is DeploymentCheckResults.Fail or DeploymentCheckResults.Unknown)} block.");
        return prepared;
    }

    internal DateTime NowIn(bool utcBasis) => DeploymentScheduleRules.NowIn(utcBasis, _utcNow(), _local);

    // ---- Create -------------------------------------------------------------------------------

    private async Task<DeploymentChange> PrepareCreateAsync(DeploymentChange request, CancellationToken ct)
    {
        var obj = request.Object ?? throw new DeploymentServiceException("Select the object to deploy.");
        var collection = request.Collection ?? throw new DeploymentServiceException("Select the device collection.");
        var options = Seconds(request.Options ?? throw new DeploymentServiceException("Enter the deployment settings."));
        var collectionId = (collection.CollectionId ?? "").Trim().ToUpperInvariant();
        var checks = new List<PreflightCheck>();

        StateRead? state = null;
        string? readProblem = null;
        try
        {
            if (!IsObjectId(obj.Type, obj.Id)) throw new DeploymentServiceException($"The ID of the {Noun(obj.Type).ToLowerInvariant()} is not valid.");
            state = await ReadStateAsync(obj, PackageIdRegex().IsMatch(collectionId) ? collectionId : "", options.Purpose, ct).ConfigureAwait(false);
        }
        catch (DeploymentServiceException ex)
        {
            readProblem = ex.Message;
        }

        var current = state?.Object is { } o
            ? obj with { Id = o.Id ?? obj.Id, Name = o.Name ?? obj.Name, Version = o.Version ?? obj.Version, ModelId = o.ModelId ?? obj.ModelId, UpdateCount = o.UpdateCount,
                Program = obj.Type == DeploymentObjectType.Package ? o.Program ?? "" : "" }
            : obj;
        checks.Add(TargetCheck(obj, state, readProblem));
        checks.Add(ContentCheck(request, current, state, readProblem));
        var (validCheck, safeCheck) = CollectionChecks(collectionId, state, readProblem);
        checks.Add(validCheck);
        checks.Add(safeCheck);
        checks.Add(DuplicateCheck(current, collectionId, state, readProblem));

        var verdict = DeploymentScheduleRules.Check(obj.Type, options, NowIn(options.UtcBasis));
        var optionProblem = DeploymentScheduleRules.OptionProblem(obj.Type, options);
        checks.Add(verdict.Error is { } scheduleError ? Check(DeploymentCheckNames.Schedule, DeploymentCheckResults.Fail, scheduleError)
            : optionProblem is not null ? Check(DeploymentCheckNames.Schedule, DeploymentCheckResults.Fail, optionProblem)
            : verdict.Warnings.Count > 0 ? Check(DeploymentCheckNames.Schedule, DeploymentCheckResults.Warn, string.Join(" ", verdict.Warnings))
            : Check(DeploymentCheckNames.Schedule, DeploymentCheckResults.Pass, "The schedule and the options are valid."));

        var probe = _audit.Probe();
        checks.Add(Check(DeploymentCheckNames.AuditLog, probe.Writable ? DeploymentCheckResults.Pass : DeploymentCheckResults.Fail, probe.Message));
        var (signingCheck, packaged) = SigningCheck(current, options, state?.NameCount);
        checks.Add(signingCheck);
        current = current with { PackagedByProduct = packaged ?? current.PackagedByProduct };

        var refreshed = state?.Collection is { } c ? collection with { CollectionId = c.CollectionId ?? collectionId, Name = c.Name ?? collection.Name, MemberCount = c.MemberCount } : collection;
        return request with
        {
            Object = current,
            Collection = refreshed,
            Options = options,
            DpGroups = request.DpGroups ?? [],
            Checks = checks,
            Impact = CreateImpact(request, current, refreshed, options, state),
            NeedsBackdatedAcknowledgement = verdict.Backdated,
            Prepared = PreparedState(state)
        };
    }

    /// <summary>
    /// The options with the times cut to whole seconds and no deadline for Available: ConfigMgr
    /// stores seconds, so a finer planned time would never equal the stored one.
    /// </summary>
    internal static DeploymentOptions Seconds(DeploymentOptions options) => options with
    {
        AvailableTime = Second(options.AvailableTime),
        Deadline = options.Purpose == DeploymentPurposes.Required && options.Deadline is { } d ? Second(d) : null
    };

    private static DateTime Second(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    private static PreflightCheck TargetCheck(DeployableObject obj, StateRead? state, string? readProblem)
    {
        const string name = DeploymentCheckNames.TargetExists;
        if (state is null) return Check(name, DeploymentCheckResults.Unknown, readProblem ?? "The object could not be read.");
        if (state.Object is not { } o) return Check(name, DeploymentCheckResults.Fail, $"{Noun(obj.Type)} {Shown(obj.Name, obj.Id)} does not exist any more.");
        if (o.Retired) return Check(name, DeploymentCheckResults.Fail, $"Application {Shown(o.Name, o.Id)} is retired. ConfigMgr does not deploy a retired application.");
        if (obj.Type == DeploymentObjectType.Package && string.IsNullOrEmpty(o.Program))
            return Check(name, DeploymentCheckResults.Fail, $"Package {Shown(o.Name, o.Id)} has no program {(string.IsNullOrEmpty(obj.Program) ? "selected" : obj.Program)}.");
        var detail = obj.Type switch
        {
            DeploymentObjectType.Application => $"version {o.Version}",
            DeploymentObjectType.Package => $"program {o.Program}",
            DeploymentObjectType.SoftwareUpdateGroup => Count(o.UpdateCount, "update"),
            _ => "found"
        };
        return Check(name, DeploymentCheckResults.Pass, $"{Noun(obj.Type)} {Shown(o.Name, o.Id)}: {detail}.");
    }

    private static PreflightCheck ContentCheck(DeploymentChange request, DeployableObject obj, StateRead? state, string? readProblem)
    {
        const string name = DeploymentCheckNames.ContentDistributed;
        if (obj.Type == DeploymentObjectType.SoftwareUpdateGroup)
            return Check(name, DeploymentCheckResults.Warn, "Not checked: the content of software updates is in deployment packages. Make sure the deployment packages of the updates are distributed.");
        if (state is null) return Check(name, DeploymentCheckResults.Unknown, readProblem ?? "The content status could not be read.");
        if (state.Object is null) return Check(name, DeploymentCheckResults.Unknown, "Not checked: the object does not exist.");
        if (!state.ContentRead) return Check(name, DeploymentCheckResults.Unknown, "The content status could not be read, so the deployment is blocked. Try again.");
        var missing = state.Content.Where(c => !c.Distributed).ToList();
        var noContent = obj.Type != DeploymentObjectType.TaskSequence && state.Content.Count == 0;
        if (!noContent && missing.Count == 0)
            return Check(name, DeploymentCheckResults.Pass, obj.Type == DeploymentObjectType.TaskSequence && state.Content.Count == 0
                ? "The task sequence references no content."
                : string.Join(" ", state.Content.Select(c => $"{c.Name} ({c.PackageId}): {c.Success} of {c.Targeted} distribution points.")));
        var status = noContent
            ? "The content is not on any distribution point."
            : string.Join(" ", missing.Select(c => $"{c.Name} ({c.PackageId}): {c.Success} of {c.Targeted} distribution points, {c.InProgress} in progress, {c.Errors} with errors."));
        if (request.DpGroups is { Count: > 0 })
            return Check(name, DeploymentCheckResults.Warn, request.CreateNowContentDistributing
                ? $"Content distribution in progress: this plan requests it first, and the deployment is created while the content is still distributing (selected). {status}"
                : $"Content distribution in progress: this plan requests it first, and the deployment is created only when the content check passes. {status}");
        return Check(name, DeploymentCheckResults.Fail, $"The content is not fully distributed. {status}");
    }

    private static (PreflightCheck Valid, PreflightCheck Safe) CollectionChecks(string collectionId, StateRead? state, string? readProblem)
    {
        var safe = !PackageIdRegex().IsMatch(collectionId)
            ? Check(DeploymentCheckNames.CollectionSafe, DeploymentCheckResults.Fail, "Select a device collection.")
            : IsBuiltInCollection(collectionId)
                ? Check(DeploymentCheckNames.CollectionSafe, DeploymentCheckResults.Fail, $"Collection {collectionId} is a built-in collection. Deployments to built-in collections are blocked.")
                : Check(DeploymentCheckNames.CollectionSafe, DeploymentCheckResults.Pass, $"Collection {collectionId} is not a built-in collection.");
        const string name = DeploymentCheckNames.CollectionValid;
        if (!PackageIdRegex().IsMatch(collectionId)) return (Check(name, DeploymentCheckResults.Fail, "Select a device collection."), safe);
        if (state is null) return (Check(name, DeploymentCheckResults.Unknown, readProblem ?? "The collection could not be read."), safe);
        if (state.Collection is not { } c) return (Check(name, DeploymentCheckResults.Fail, $"Collection {collectionId} does not exist."), safe);
        if (c.CollectionType != 2) return (Check(name, DeploymentCheckResults.Fail, $"Collection {Shown(c.Name, c.CollectionId)} is not a device collection."), safe);
        return (Check(name, DeploymentCheckResults.Pass, $"Device collection {Shown(c.Name, c.CollectionId)}: {Count(c.MemberCount, "member")} now."), safe);
    }

    private static PreflightCheck DuplicateCheck(DeployableObject obj, string collectionId, StateRead? state, string? readProblem)
    {
        const string name = DeploymentCheckNames.NoDuplicate;
        if (state is null) return Check(name, DeploymentCheckResults.Unknown, readProblem ?? "The existing deployments could not be read.");
        if (state.Object is null || state.Collection is null || state.Snapshot is null)
            return Check(name, DeploymentCheckResults.Unknown, "Not checked: the object or the collection does not exist.");
        if (!state.Snapshot.Read)
            return Check(name, DeploymentCheckResults.Unknown, "The existing deployments could not be read, so the duplicate check failed and the deployment is blocked. Try again.");
        var duplicates = state.Snapshot.Duplicates ?? [];
        return duplicates.Length > 0
            ? Check(name, DeploymentCheckResults.Fail, $"{Noun(obj.Type)} {Shown(obj.Name, obj.Id)}{(obj.Type == DeploymentObjectType.Package ? $", program {obj.Program}," : "")} is already deployed to collection {collectionId} (deployment ID {string.Join(", ", duplicates)}).")
            : Check(name, DeploymentCheckResults.Pass, $"No deployment of this object to collection {collectionId} exists.");
    }

    private (PreflightCheck Check, bool? Packaged) SigningCheck(DeployableObject obj, DeploymentOptions options, int? nameCount)
    {
        const string name = DeploymentCheckNames.Signing;
        if (obj.Type != DeploymentObjectType.Application || _signing is null)
            return (Check(name, DeploymentCheckResults.Pass, "The script-signing rule applies only to applications this product packaged."), null);
        try
        {
            var packaged = _signing.PackagedApplications().Contains(obj.Name);
            if (options.Purpose != DeploymentPurposes.Required)
                return packaged
                    ? (Check(name, DeploymentCheckResults.Pass, "This product packaged the application. The script-signing rule applies to Required deployments only."), true)
                    : (Check(name, DeploymentCheckResults.Pass, "This product did not package the application, so the script-signing rule does not apply."), false);
            if (SigningRefusal(obj.Name, nameCount) is { } refusal) return (Check(name, DeploymentCheckResults.Fail, refusal), packaged);
            return packaged
                ? (Check(name, DeploymentCheckResults.Pass, "This product packaged the application, and the script-signing policy allows a Required deployment of it."), true)
                : (Check(name, DeploymentCheckResults.Pass, "This product did not package the application, so the script-signing rule does not apply."), false);
        }
        catch (DeploymentServiceException ex)
        {
            return (Check(name, DeploymentCheckResults.Unknown, ex.Message), null);
        }
    }

    /// <summary>
    /// The refusal of the signing rule for a Required deployment of an application with these
    /// names, or null. The rule matches by name, so a name that is not unique on the site, or a
    /// count that was not read, is refused. Throws <see cref="DeploymentServiceException"/> when
    /// the packaged applications cannot be read.
    /// </summary>
    private string? SigningRefusal(string siteName, int? nameCount, string? plannedName = null)
    {
        if (_signing is null) return null;
        if (nameCount != 1)
            return nameCount is null
                ? "the number of applications with this name could not be read, so the script-signing rule cannot be applied to a Required deployment. Try again."
                : $"{nameCount.Value.ToString(CultureInfo.InvariantCulture)} applications on the site are named {siteName}, so the script-signing rule cannot tell which one this product packaged. A Required deployment is refused. Rename one of them in the console.";
        return _signing.RequiredDeploymentRefusal(siteName)
            ?? (plannedName is { Length: > 0 } && !string.Equals(plannedName, siteName, StringComparison.OrdinalIgnoreCase) ? _signing.RequiredDeploymentRefusal(plannedName) : null);
    }

    private static List<string> CreateImpact(DeploymentChange request, DeployableObject obj, DeploymentCollection collection, DeploymentOptions o, StateRead? state)
    {
        var lines = new List<string>();
        var required = o.Purpose == DeploymentPurposes.Required;
        var members = state?.Collection?.MemberCount ?? collection.MemberCount;
        if (request.DpGroups is { Count: > 0 } groups)
        {
            lines.Add($"First requests content distribution of {ObjectText(obj)} to {Count(groups.Count, "DP group")}: {string.Join(", ", groups)}.");
            lines.Add(NoDistributionUndo);
            lines.Add(request.CreateNowContentDistributing
                ? "Then creates the deployment while the content is still distributing (selected)."
                : "Then creates the deployment only when the content check passes; otherwise the create is skipped and can run again later.");
        }
        lines.Add($"Creates a {o.Purpose} deployment of {ObjectText(obj)} to collection {Shown(collection.Name, collection.CollectionId)} ({Count(members, "member")} now).");
        lines.Add($"Available: {When(o.AvailableTime, o.UtcBasis)}.");
        if (required && o.Deadline is { } deadline)
            lines.Add($"Required: at the deadline {When(deadline, o.UtcBasis)} it installs on every device of the collection ({Count(members, "device")} now).");
        if (DeploymentScheduleRules.HasNotification(obj.Type)) lines.Add($"User notification: {NotificationText(o.UserNotification)}.");
        if (required)
        {
            lines.Add(o.OverrideServiceWindow ? "Installs outside maintenance windows at the deadline." : "Installs only in maintenance windows.");
            lines.Add(o.RebootOutsideServiceWindow ? "Restarts outside maintenance windows when needed." : "Restarts only in maintenance windows.");
            if (o.AllowMetered) lines.Add("Clients can download over a metered connection after the deadline.");
        }
        switch (obj.Type)
        {
            case DeploymentObjectType.Package:
                lines.Add($"Fast network: {o.NetworkBehavior}. Slow network: {o.SlowNetworkBehavior}. Fallback to the default site boundary group: {YesNo(o.AllowFallback)}.");
                if (required) lines.Add($"Rerun: {o.RerunBehavior}.");
                break;
            case DeploymentObjectType.TaskSequence:
                lines.Add($"Available to: {o.TaskSequenceAvailability}. Content: {o.NetworkBehavior}. Show progress: {YesNo(o.ShowProgress)}. Fallback: {YesNo(o.AllowFallback)}.");
                if (required) lines.Add($"Rerun: {o.RerunBehavior}.");
                break;
            case DeploymentObjectType.SoftwareUpdateGroup:
                lines.Add($"Full scan after restart: {YesNo(o.PostRebootScan)}.");
                if (required)
                    lines.Add($"Fallback to the default site boundary group: {YesNo(o.FallbackToDp)}. Microsoft Update fallback: {YesNo(o.AllowMicrosoftUpdate)}. Suppress restarts on servers and workstations: {YesNo(o.SuppressRestart)}.");
                break;
        }
        lines.Add(UndoNote);
        return lines;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    internal static string NotificationText(string value) => value switch
    {
        "DisplayAll" => "show in Software Center and show all notifications",
        "DisplaySoftwareCenterOnly" => "show in Software Center and only the notifications of restarts",
        "HideAll" => "hide in Software Center and hide all notifications",
        _ => value
    };

    // ---- Distribute ---------------------------------------------------------------------------

    private async Task<DeploymentChange> PrepareDistributeAsync(DeploymentChange request, CancellationToken ct)
    {
        var obj = request.Object ?? throw new DeploymentServiceException("Select the object whose content to distribute.");
        if (obj.Type == DeploymentObjectType.SoftwareUpdateGroup)
            throw new DeploymentServiceException("The content of a software update group is in deployment packages. Distribute the deployment packages in the console.");
        if (request.DpGroups is not { Count: > 0 }) throw new DeploymentServiceException("Select at least one DP group.");
        var checks = new List<PreflightCheck>();
        var lines = new List<string>();
        IReadOnlyList<DpGroupInfo>? groups = null;
        try
        {
            groups = await ListDpGroupsAsync(obj, ct).ConfigureAwait(false);
            checks.Add(Check(DeploymentCheckNames.TargetExists, DeploymentCheckResults.Pass, $"{Noun(obj.Type)} {Shown(obj.Name, obj.Id)} exists."));
        }
        catch (DeploymentServiceException ex)
        {
            checks.Add(Check(DeploymentCheckNames.TargetExists, DeploymentCheckResults.Unknown, ex.Message));
        }
        var resolved = new List<string>();
        foreach (var requested in request.DpGroups)
        {
            var group = groups?.FirstOrDefault(g => string.Equals(g.Id, requested, StringComparison.OrdinalIgnoreCase))
                ?? groups?.FirstOrDefault(g => string.Equals(g.Name, requested, StringComparison.OrdinalIgnoreCase));
            if (groups is null) continue;
            if (group is null)
            {
                checks.Add(Check("DpGroup", DeploymentCheckResults.Fail, $"DP group {requested} does not exist."));
                continue;
            }
            if (group.Name.IndexOfAny(['*', '?', '[', ']']) >= 0)
            {
                checks.Add(Check("DpGroup", DeploymentCheckResults.Fail, $"DP group {group.Name} has a wildcard character in its name, which the distribution cmdlet would read as a pattern. Rename the group in the console."));
                continue;
            }
            resolved.Add(group.Id);
            lines.Add($"Distributes the content of {ObjectText(obj)} to DP group {group.Name}: now {StatusText(group)}.");
        }
        var probe = _audit.Probe();
        checks.Add(Check(DeploymentCheckNames.AuditLog, probe.Writable ? DeploymentCheckResults.Pass : DeploymentCheckResults.Fail, probe.Message));
        lines.Add(NoDistributionUndo);
        return request with { DpGroups = resolved.Count == request.DpGroups.Count ? resolved : request.DpGroups, Checks = checks, Impact = lines };
    }

    internal static string StatusText(DpGroupInfo g) => g.Status switch
    {
        "Distributed" => $"distributed to {g.Success} of {g.Targeted} distribution points",
        "InProgress" => $"in progress ({g.Success} of {g.Targeted} distribution points done)",
        "Failed" => $"failed on {g.Error} of {g.Targeted} distribution points",
        "NotTargeted" => "not distributed to this group",
        _ => "status not known"
    };

    // ---- Remove -------------------------------------------------------------------------------

    private async Task<DeploymentChange> PrepareRemoveAsync(DeploymentChange request, CancellationToken ct)
    {
        var existing = request.Existing ?? throw new DeploymentServiceException("Select the deployment to remove.");
        var checks = new List<PreflightCheck>();
        var (read, row) = IsDeploymentId(existing.Type, existing.DeploymentId)
            ? await ReadDeploymentOrNullAsync(existing.Type, existing.DeploymentId).ConfigureAwait(false)
            : (true, null);
        if (!read) checks.Add(Check(DeploymentCheckNames.TargetExists, DeploymentCheckResults.Unknown, "The deployment could not be read from ConfigMgr. Try again."));
        else if (row is null) checks.Add(Check(DeploymentCheckNames.TargetExists, DeploymentCheckResults.Fail, $"Deployment {existing.DeploymentId} does not exist any more."));
        else
        {
            checks.Add(Check(DeploymentCheckNames.TargetExists, DeploymentCheckResults.Pass, $"Deployment {existing.DeploymentId} exists."));
            var differences = Differences(existing, row);
            checks.Add(differences.Count == 0
                ? Check(Unchanged, DeploymentCheckResults.Pass, "The deployment holds the captured object, collection, purpose and times.")
                : Check(Unchanged, DeploymentCheckResults.Fail, $"The deployment changed since it was captured ({string.Join(", ", differences)}), so it is not removed."));
        }
        var ownership = Ownership(existing.DeploymentId);
        checks.Add(ownership switch
        {
            true => Check(CreatedHere, DeploymentCheckResults.Pass, "This product created the deployment (the audit log has its create)."),
            false => Check(CreatedHere, DeploymentCheckResults.Fail, "This product did not create the deployment, so it does not remove it."),
            null => Check(CreatedHere, DeploymentCheckResults.Unknown, "The audit log could not be read, so it is not known whether this product created the deployment. It is not removed.")
        });
        var probe = _audit.Probe();
        checks.Add(Check(DeploymentCheckNames.AuditLog, probe.Writable ? DeploymentCheckResults.Pass : DeploymentCheckResults.Fail, probe.Message));
        var lines = new List<string>
        {
            $"Removes the {existing.Purpose} deployment {existing.DeploymentId} of {Noun(existing.Type).ToLowerInvariant()} {Shown(existing.ObjectName, existing.ObjectId)} from collection {Shown(existing.CollectionName, existing.CollectionId)}.",
            "Software that clients already installed stays installed. Devices that have not installed it yet do not get it."
        };
        return request with { Checks = checks, Impact = lines };
    }

    /// <summary>The captured values a stored deployment no longer holds.</summary>
    internal static List<string> Differences(DeploymentRecord captured, DeploymentRow row)
    {
        var diff = new List<string>();
        if (!string.Equals(row.CollectionId, captured.CollectionId, StringComparison.OrdinalIgnoreCase)) diff.Add("collection");
        if (captured.Type == DeploymentObjectType.Application && captured.ModelId is { Length: > 0 })
        {
            if (!string.Equals(row.ModelId, captured.ModelId, StringComparison.Ordinal)) diff.Add("object");
        }
        else if (!string.Equals(row.ObjectId, captured.ObjectId, StringComparison.OrdinalIgnoreCase)
            && !(captured.Type == DeploymentObjectType.Application && string.Equals(row.ModelId, captured.ObjectId, StringComparison.Ordinal))) diff.Add("object");
        if (captured.Type == DeploymentObjectType.Package && !string.Equals(row.Program, captured.Program, StringComparison.OrdinalIgnoreCase)) diff.Add("program");
        if (!string.Equals(row.Purpose, captured.Purpose, StringComparison.Ordinal)) diff.Add("purpose");
        if (ParseTime(row.Available) != captured.AvailableTime) diff.Add("available time");
        if (ParseTime(row.Deadline) != captured.Deadline) diff.Add("deadline");
        if (row.Utc != captured.UtcBasis) diff.Add("time basis");
        return diff;
    }

    /// <summary>True when this product created the deployment; null when the audit log cannot be read.</summary>
    private bool? Ownership(string deploymentId)
    {
        if (_created.ContainsKey(deploymentId)) return true;
        try
        {
            var records = _audit.Read(OwnershipRecords);
            return records.Any(r => r.Event == AuditEvents.Create
                && string.Equals(r.DeploymentUniqueID, deploymentId, StringComparison.OrdinalIgnoreCase)
                && r.Result is AuditResults.Success or AuditResults.Partial);
        }
        catch (DeploymentServiceException)
        {
            return null;
        }
    }
}

/// <summary>The Event values of the audit records this product writes.</summary>
public static class AuditEvents
{
    public const string Create = "Create";
    public const string Distribute = "Distribute";
    public const string Undo = "Undo";
}

/// <summary>The Result values of the audit records this product writes; the suite tool writes Success or 'Failed: text'.</summary>
public static class AuditResults
{
    public const string Success = "Success";

    /// <summary>ConfigMgr made the deployment and reported a problem or stored other values.</summary>
    public const string Partial = "Partial";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";
}