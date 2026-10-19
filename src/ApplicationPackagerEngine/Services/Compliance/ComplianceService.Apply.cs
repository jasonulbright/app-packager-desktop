using System.Globalization;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;

namespace ApplicationPackagerEngine.Services;

public sealed partial class ComplianceService
{
    private const string NotPreparedMessage = "The change has no captured state, so it was not applied. Refresh the list and try again.";
    private const string RemediationNotConfirmedMessage = "Remediation changes client settings and needs its own confirmation, so nothing was changed.";

    private Task<ComplianceOutcome> ApplyOneAsync(ComplianceChange change, IProgress<ComplianceOutcome> progress, CancellationToken ct) => change.Kind switch
    {
        ComplianceChangeKind.CatalogDeploy => ApplyCatalogAsync(change, progress, ct),
        ComplianceChangeKind.SetRemediation => ApplySetRemediationAsync(change),
        ComplianceChangeKind.Redeploy or ComplianceChangeKind.Reassign => ApplyMoveAsync(change),
        ComplianceChangeKind.DeleteDeployment => ApplyDeleteAsync(change),
        ComplianceChangeKind.CreateDeployment => ApplyCreateAsync(change),
        ComplianceChangeKind.AddSetting or ComplianceChangeKind.EditSetting or ComplianceChangeKind.RemoveSetting => ApplySettingAsync(change),
        ComplianceChangeKind.RemoveCatalogObjects => ApplyRemoveObjectsAsync(change),
        _ => Task.FromResult(Fail(change, "The change kind is not known."))
    };

    // ---- Deployments ----------------------------------------------------------------------------

    private static ConfigMgrParameters DeploymentCheck(string operation, BaselineDeploymentRow before)
    {
        var settings = before.Settings ?? new BaselineDeploymentSettings("", false, false, 0, null, false);
        return new()
        {
            { "Operation", operation },
            { "AssignmentId", long.Parse(before.DeploymentId, CultureInfo.InvariantCulture) },
            { "BaselineId", long.Parse(before.BaselineId, CultureInfo.InvariantCulture) },
            { "CollectionId", before.CollectionId.ToUpperInvariant() },
            { "ExpectedEnforcement", before.RemediateNoncompliant },
            { "ExpectedSchedule", settings.ScheduleToken ?? "" },
            { "ExpectedOverride", settings.OverrideServiceWindow },
            { "ExpectedAlert", settings.GenerateAlert },
            { "ExpectedThreshold", settings.GenerateAlert ? Math.Clamp(settings.AlertThresholdPercent, 0, 100) : 0 },
            { "ExpectedScom", settings.GenerateAlert && settings.MonitoredByScom },
            { "ExpectedAlertAfter", settings.GenerateAlert ? UtcText(settings.AlertAfter) : "" }
        };
    }

    private static void AddCreateValues(ConfigMgrParameters parameters, BaselineDeploymentRow row)
    {
        var settings = row.Settings ?? new BaselineDeploymentSettings("", false, false, 0, null, false);
        parameters["Enforcement"] = row.RemediateNoncompliant;
        parameters["ScheduleToken"] = settings.ScheduleToken ?? "";
        parameters["OverrideServiceWindow"] = settings.OverrideServiceWindow;
        parameters["GenerateAlert"] = settings.GenerateAlert;
        parameters["AlertThreshold"] = Math.Clamp(settings.AlertThresholdPercent, 0, 100);
        parameters["MonitoredByScom"] = settings.MonitoredByScom;
        parameters["AlertAfter"] = settings.GenerateAlert ? UtcText(settings.AlertAfter) : "";
    }

    private static string UtcText(DateTime? value) =>
        value is { } v ? v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "";

    /// <summary>
    /// The Values key of a Reassign that moves a captured deployment back: an Undo may restore a
    /// deployment to the built-in collection it was taken from.
    /// </summary>
    public const string RestoreKey = "RestoresCapture";

    private static bool Restores(ComplianceChange change) =>
        change.Values is { } v && v.TryGetValue(RestoreKey, out var r) && r == "True";

    private static bool IsCaptured(BaselineDeploymentRow? row) =>
        row is { Settings: not null } && NumberRegex().IsMatch(row.DeploymentId ?? "") && NumberRegex().IsMatch(row.BaselineId ?? "") && CollectionIdRegex().IsMatch(row.CollectionId ?? "");

    private async Task<ComplianceOutcome> ApplySetRemediationAsync(ComplianceChange change)
    {
        if (!IsCaptured(change.DeploymentBefore) || change.RemediateAfter is not { } after) return Refused(change, NotPreparedMessage);
        var before = change.DeploymentBefore!;
        if (after && !change.RemediationConfirmed) return Refused(change, RemediationNotConfirmedMessage);
        var parameters = DeploymentCheck("SetRemediation", before);
        parameters["Enforcement"] = after;
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        var now = before with { RemediateNoncompliant = after };
        var undo = change with
        {
            DeploymentBefore = now, RemediateAfter = before.RemediateNoncompliant, RemediationConfirmed = false,
            Impact = [$"Sets {Deployment(before)} back to {Mode(before.RemediateNoncompliant)}."], CreatedIds = []
        };
        switch (run.Result?.Status)
        {
            case "ok":
                return Done(change, $"Set {Deployment(before)} to {Mode(after)}.", undo, run.Steps);
            case "partial":
                return Partial(change, $"ConfigMgr set {Deployment(before)} to {Mode(after)} and then reported an error. Undo sets it back.", undo, run.Steps);
            case "stored-differs":
                return Fail(change, $"ConfigMgr did not keep {Mode(after)} for {Deployment(before)}. Refresh the list to see the stored mode.");
        }
        if (run.Uncertain)
        {
            var read = await ReadDeploymentOrNullAsync(before.DeploymentId).ConfigureAwait(false);
            if (read.Read && read.Row is { } row && row.RemediateNoncompliant == after)
                return Partial(change, $"ConfigMgr set {Deployment(before)} to {Mode(after)} and then reported an error. Undo sets it back.", undo, run.Steps);
        }
        return run.Failure ?? Fail(change, WriteFailedMessage(change));
    }

    private async Task<ComplianceOutcome> ApplyMoveAsync(ComplianceChange change)
    {
        if (!IsCaptured(change.DeploymentBefore)) return Refused(change, NotPreparedMessage);
        var before = change.DeploymentBefore!;
        if (before.RemediateNoncompliant && !change.RemediationConfirmed) return Refused(change, RemediationNotConfirmedMessage);
        var reassign = change.Kind == ComplianceChangeKind.Reassign;
        var to = reassign ? (change.ToCollectionId ?? "").Trim().ToUpperInvariant() : before.CollectionId.ToUpperInvariant();
        if (!CollectionIdRegex().IsMatch(to)) return Refused(change, NotPreparedMessage);
        if (IsBuiltInCollection(to) && !(reassign && Restores(change)))
            return Refused(change, $"Collection {to} is a built-in collection. Select a collection that holds the devices to evaluate.");
        var parameters = DeploymentCheck(reassign ? "Reassign" : "Redeploy", before);
        if (reassign) parameters["ToCollectionId"] = to;
        AddCreateValues(parameters, before);
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        var status = run.Result?.Status;
        (bool Read, BaselineDeploymentRow? Row) made = status is "ok" or "old-remains" or "replacement-differs" ? await ReadDeploymentOrNullAsync(run.Result!.CreatedId).ConfigureAwait(false) : (false, null);
        var target = reassign ? $"collection {to}" : $"collection {Shown(before.CollectionName, before.CollectionId)}";
        switch (status)
        {
            case "ok" when made.Row is { } row:
                return reassign
                    ? Done(change, $"Moved the deployment of baseline {before.BaselineName} to {Shown(row.CollectionName, row.CollectionId)} (new deployment ID {row.DeploymentId}) and removed the old one.",
                        change with
                        {
                            DeploymentBefore = row, ToCollectionId = before.CollectionId, RemediationConfirmed = false, CreatedIds = [],
                            Values = new Dictionary<string, string> { [RestoreKey] = "True" },
                            Impact = [$"Moves the deployment of baseline {before.BaselineName} back to collection {Shown(before.CollectionName, before.CollectionId)}. The deployments have new IDs."]
                        }, run.Steps)
                    : Done(change, $"Created {Deployment(before)} again (new deployment ID {row.DeploymentId}) and removed the old one. Undo is not applicable: the deployment holds the same settings with a new ID.", null, run.Steps);
            case "ok":
                return Fail(change, $"ConfigMgr made the new deployment on {target} and confirmed its settings, then removed the old deployment, but the new deployment could not be read back here. Refresh the list. The captured values: {Mode(before.RemediateNoncompliant)}, {before.Schedule}.");
            case "replacement-differs":
                // The new deployment is the run's own: its ID is the one deployment that was not there before the create.
                return made.Row is { } differs
                    ? Partial(change, $"ConfigMgr created the deployment on {target} (ID {differs.DeploymentId}) but reported an error or stored other settings than captured, so the old deployment ({before.DeploymentId}) was kept. Both exist now. Undo removes the new one.", DeleteUndo(change, differs), run.Steps)
                    : Fail(change, $"ConfigMgr created a deployment on {target} that does not hold the captured settings, and it could not be read back. The old deployment ({before.DeploymentId}) was kept. Refresh the list and remove the new deployment in the console.");
            case "old-remains":
                return made.Row is { } both
                    ? Partial(change, $"ConfigMgr created the deployment on {target} (ID {both.DeploymentId}) but did not remove the old one ({before.DeploymentId}). Both exist now. Undo removes the new one.", DeleteUndo(change, both), run.Steps)
                    : Fail(change, $"ConfigMgr created a deployment on {target} but did not remove the old one. Refresh the list.");
            case "deployment-exists":
                return Refused(change, $"Baseline {before.BaselineName} is already deployed to {target}.");
            case "create-failed":
            case "create-unclear":
            case "baseline-missing":
                return Fail(change, $"ConfigMgr did not create the deployment on {target}, so {Deployment(before)} was not changed. The log has the details.");
        }
        if (run.Uncertain)
            return Fail(change, $"The result of the change to {Deployment(before)} is not known: ConfigMgr reported an error during the change. Refresh the list to see which deployments exist. Undo is not available because the new deployment cannot be shown to come from this change.");
        return run.Failure ?? Fail(change, WriteFailedMessage(change));
    }

    private ComplianceChange DeleteUndo(ComplianceChange change, BaselineDeploymentRow made) =>
        new(ComplianceChangeKind.DeleteDeployment, null, new Dictionary<string, string>(), [], made, null, null, null, null, null,
            [$"Removes {Deployment(made)}, which the change made."], []);

    private async Task<ComplianceOutcome> ApplyDeleteAsync(ComplianceChange change)
    {
        if (!IsCaptured(change.DeploymentBefore)) return Refused(change, NotPreparedMessage);
        var before = change.DeploymentBefore!;
        var run = await RunWriteAsync(change, DeploymentCheck("RemoveDeployment", before)).ConfigureAwait(false);
        var undo = new ComplianceChange(ComplianceChangeKind.CreateDeployment, null, new Dictionary<string, string>(), [], before, null, null, null, null, null,
            [$"Creates {Deployment(before)} again with the same settings. The new deployment has a new ID."], []);
        if (run.Result?.Status == "ok")
            return Done(change, $"Removed {Deployment(before)}. Undo creates it again with a new ID.", undo, run.Steps);
        if (run.Result?.Status == "remove-failed") return Fail(change, $"ConfigMgr did not remove {Deployment(before)}. The log has the details.");
        if (run.Uncertain)
        {
            var read = await ReadDeploymentOrNullAsync(before.DeploymentId).ConfigureAwait(false);
            if (read.Read && read.Row is null)
                return Partial(change, $"ConfigMgr removed {Deployment(before)} and then reported an error. Undo creates it again with a new ID.", undo, run.Steps);
        }
        return run.Failure ?? Fail(change, WriteFailedMessage(change));
    }

    private async Task<ComplianceOutcome> ApplyCreateAsync(ComplianceChange change)
    {
        var row = change.DeploymentBefore;
        if (row is null || !NumberRegex().IsMatch(row.BaselineId ?? "") || !CollectionIdRegex().IsMatch(row.CollectionId ?? "")) return Refused(change, NotPreparedMessage);
        if (IsBuiltInCollection(row.CollectionId) && !IsCaptured(row))
            return Refused(change, $"Collection {row.CollectionId} is a built-in collection. Select a collection that holds the devices to evaluate.");
        if (row.RemediateNoncompliant && !change.RemediationConfirmed) return Refused(change, RemediationNotConfirmedMessage);
        var parameters = new ConfigMgrParameters
        {
            { "Operation", "CreateDeployment" }, { "BaselineId", long.Parse(row.BaselineId!, CultureInfo.InvariantCulture) }, { "CollectionId", row.CollectionId!.ToUpperInvariant() }
        };
        AddCreateValues(parameters, row);
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        var status = run.Result?.Status;
        if (status is "ok" or "created-with-error" or "stored-differs")
        {
            var made = await ReadDeploymentOrNullAsync(run.Result!.CreatedId).ConfigureAwait(false);
            if (made.Row is { } stored)
                return status == "ok"
                    ? Done(change, $"Created {Deployment(stored)} (deployment ID {stored.DeploymentId}).", DeleteUndo(change, stored), run.Steps)
                    : Partial(change, $"ConfigMgr created {Deployment(stored)} (ID {stored.DeploymentId}) but reported an error or stored other settings. Undo removes it.", DeleteUndo(change, stored), run.Steps);
            return Fail(change, $"ConfigMgr reported {Deployment(row)} as made, but it could not be read back. Refresh the list.");
        }
        return status switch
        {
            "deployment-exists" => Refused(change, $"Baseline {row.BaselineName} is already deployed to collection {Shown(row.CollectionName, row.CollectionId)}."),
            "baseline-missing" => Refused(change, $"Baseline {Shown(row.BaselineName, row.BaselineId)} does not exist any more."),
            "collection-missing" => Refused(change, $"Collection {Shown(row.CollectionName, row.CollectionId)} does not exist any more."),
            "not-device" => Refused(change, $"Collection {Shown(row.CollectionName, row.CollectionId)} is not a device collection."),
            "create-failed" or "create-unclear" => Fail(change, $"ConfigMgr did not create {Deployment(row)}. The log has the details."),
            _ when run.Uncertain => Fail(change, $"The result of creating {Deployment(row)} is not known. Refresh the list to see whether it exists. Undo is not available because a deployment found later cannot be shown to come from this change."),
            _ => run.Failure ?? Fail(change, WriteFailedMessage(change))
        };
    }

    private async Task<(bool Read, BaselineDeploymentRow? Row)> ReadDeploymentOrNullAsync(string? deploymentId)
    {
        if (deploymentId is null || !NumberRegex().IsMatch(deploymentId)) return (false, null);
        try
        {
            var rows = await ReadDeploymentsAsync(new ConfigMgrParameters { { "Operation", "Deployments" }, { "AssignmentId", long.Parse(deploymentId, CultureInfo.InvariantCulture) } }, CancellationToken.None).ConfigureAwait(false);
            return (true, rows.FirstOrDefault());
        }
        catch (ComplianceServiceException)
        {
            return (false, null);
        }
    }

    // ---- Settings -------------------------------------------------------------------------------

    private static void AddSettingValues(ConfigMgrParameters p, CiSetting s)
    {
        p["SettingKind"] = s.Kind;
        p["Name"] = s.Name;
        p["Description"] = s.Description ?? "";
        p["DataType"] = s.DataType;
        p["RuleName"] = s.RuleName;
        p["Operator"] = s.Operator;
        p["ExpectedValues"] = SettingRules.Values(s).ToArray();
        p["Remediate"] = s.Remediate;
        p["Report"] = s.ReportNoncompliance;
        p["Severity"] = s.Severity;
        p["Is64Bit"] = s.Is64Bit;
        if (s.Kind == "Registry")
        {
            p["Hive"] = s.Hive;
            p["KeyName"] = s.KeyName;
            p["ValueName"] = s.ValueName ?? "";
            p["RemediateDword"] = s.RemediateDword;
        }
        else
        {
            p["DiscoveryLanguage"] = s.DiscoveryLanguage;
            p["DiscoveryScript"] = s.DiscoveryScript;
            p["RemediationLanguage"] = s.RemediationScript.Length > 0 ? s.RemediationLanguage : "";
            p["RemediationScript"] = s.RemediationScript ?? "";
        }
    }

    private async Task<ComplianceOutcome> ApplySettingAsync(ComplianceChange change)
    {
        if (!NumberRegex().IsMatch(change.CiId ?? "")) return Refused(change, NotPreparedMessage);
        var revision = change.Values is { } v && v.TryGetValue(RevisionKey, out var r) && int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var ci = Shown(change.CiName, change.CiId);
        var before = change.SettingBefore;
        var after = change.SettingAfter;
        if (change.Kind != ComplianceChangeKind.AddSetting && (before is null || !SettingIdRegex().IsMatch(before.SettingId ?? ""))) return Refused(change, NotPreparedMessage);
        if (change.Kind != ComplianceChangeKind.RemoveSetting && SettingRules.Check(after) is { } invalid) return Refused(change, invalid);
        if (RemediatingSetting(change.Kind, after) && !change.RemediationConfirmed)
        {
            switch (await InRemediatingDeploymentAsync(change).ConfigureAwait(false))
            {
                case true:
                    return Refused(change, $"Configuration item {ci} is in a baseline deployment that remediates, and this setting remediates. That needs the second confirmation of the plan, so nothing was changed.");
                case null:
                    return Refused(change, $"The baseline deployments of configuration item {ci} could not be read, so it is not known whether the setting needs the second confirmation of the plan. Nothing was changed. Try again.");
            }
        }
        var parameters = new ConfigMgrParameters
        {
            { "Operation", change.Kind.ToString() }, { "CiId", long.Parse(change.CiId!, CultureInfo.InvariantCulture) }, { "ExpectedRevision", revision }
        };
        var temp = "";
        if (before is not null)
        {
            parameters["SettingId"] = before.SettingId;
            parameters["OldName"] = before.Name;
        }
        if (after is not null) AddSettingValues(parameters, after);
        if (change.Kind == ComplianceChangeKind.EditSetting)
        {
            var mode = change.Values is { } values && values.TryGetValue(EditModeKey, out var m) && m is "InPlace" or "Replace"
                ? m
                : SettingRules.CanChangeInPlace(before!, after!) ? "InPlace" : "Replace";
            parameters["EditMode"] = mode;
            var suffix = $" (edit {_tempSuffix()})";
            temp = (after!.Name.Length + suffix.Length > SettingRules.MaxNameLength ? after.Name[..(SettingRules.MaxNameLength - suffix.Length)] : after.Name) + suffix;
            parameters["TempName"] = temp;
        }
        var run = await RunWriteAsync(change, parameters).ConfigureAwait(false);
        var result = run.Result;
        var latest = result?.CiId is { Length: > 0 } id ? id : change.CiId!;
        var latestRevision = result?.Revision ?? 0;
        var stored = result?.CreatedId is { Length: > 0 } createdId ? await ReadSettingOrNullAsync(latest, createdId).ConfigureAwait(false) : null;

        ComplianceChange Undo(ComplianceChangeKind kind, CiSetting? undoBefore, CiSetting? undoAfter, string line) =>
            new(kind, null, new Dictionary<string, string> { [RevisionKey] = latestRevision.ToString(CultureInfo.InvariantCulture) }, [], null, null, null, latest,
                undoBefore, undoAfter, [line], [], CiName: change.CiName);

        switch (change.Kind, result?.Status)
        {
            case (ComplianceChangeKind.AddSetting, "ok" or "partial"):
                if (stored is null) return Fail(change, $"ConfigMgr reported setting {after!.Name} as added to {ci}, but it could not be read back. Refresh the list.");
                var addUndo = Undo(ComplianceChangeKind.RemoveSetting, stored, null, $"Removes setting {stored.Name}, which the change added.");
                if (!SettingRules.Same(stored, after! with { SettingId = stored.SettingId }))
                    return Partial(change, $"ConfigMgr added setting {stored.Name} to {ci} but stored other values than sent. Undo removes it.", addUndo, run.Steps);
                return result!.Status == "ok"
                    ? Done(change, $"Added setting {stored.Name} to {ci}.", addUndo, run.Steps)
                    : Partial(change, $"ConfigMgr added setting {stored.Name} to {ci} and then reported an error. Undo removes it.", addUndo, run.Steps);
            case (ComplianceChangeKind.EditSetting, "ok"):
            {
                if (stored is null) return Fail(change, $"ConfigMgr reported setting {after!.Name} on {ci} as changed, but it could not be read back. Refresh the list.");
                var editUndo = Undo(ComplianceChangeKind.EditSetting, stored, before! with { SettingId = "" }, $"Sets the old values of setting {before!.Name} back.");
                if (!SettingRules.Same(stored, after! with { SettingId = stored.SettingId }))
                    return Partial(change, $"ConfigMgr changed setting {stored.Name} on {ci} but stored other values than sent. Undo sets the old values back.", editUndo, run.Steps);
                var newId = stored.SettingId != before!.SettingId ? " The setting has a new ID." : "";
                return Done(change, $"Changed setting {stored.Name} on {ci}.{newId}", editUndo, run.Steps);
            }
            case (ComplianceChangeKind.EditSetting, "set-uncertain"):
            {
                var editUndo = Undo(ComplianceChangeKind.EditSetting, stored, before! with { SettingId = "" }, $"Sets the old values of setting {before!.Name} back.");
                return stored is not null && SettingRules.Same(stored, after! with { SettingId = stored.SettingId })
                    ? Partial(change, $"ConfigMgr changed setting {stored.Name} on {ci} and then reported an error. The new values are stored. Undo sets the old values back.", editUndo, run.Steps)
                    : Fail(change, $"The result of the change to setting {before.Name} on {ci} is not known: ConfigMgr reported an error after it stored a new revision. Check the setting in ConfigMgr. Undo is not available.");
            }
            case (ComplianceChangeKind.EditSetting, "set-unknown"):
                return Fail(change, $"The result of the change to setting {before!.Name} on {ci} is not known: ConfigMgr reported an error after it stored a new revision. Check the setting in ConfigMgr. Undo is not available.");
            case (ComplianceChangeKind.EditSetting, "replace-conflict"):
                return stored is null
                    ? Fail(change, $"ConfigMgr added the new values as setting {temp} on {ci}, but the configuration item changed during the change, so setting {before!.Name} was not removed. Both may exist now. Refresh the list; remove {temp} in the console if it exists.")
                    : Partial(change, $"ConfigMgr added the new values as setting {temp} on {ci}, but setting {before!.Name} or the configuration item changed during the change, so {before.Name} was not removed. Both exist now: {before.Name} and {temp}. Undo removes {temp}.",
                        Undo(ComplianceChangeKind.RemoveSetting, stored, null, $"Removes setting {temp}, which the change added."), run.Steps);
            case (ComplianceChangeKind.EditSetting, "replace-added"):
                return stored is null
                    ? Fail(change, $"ConfigMgr added the new setting {temp} to {ci} and did not finish the change. Refresh the list; remove {temp} in the console if it exists.")
                    : Partial(change, $"ConfigMgr added the new values as setting {temp} on {ci} but did not remove setting {before!.Name}. Both exist now. Undo removes {temp}.",
                        Undo(ComplianceChangeKind.RemoveSetting, stored, null, $"Removes setting {temp}, which the change added."), run.Steps);
            case (ComplianceChangeKind.EditSetting, "replace-unnamed"):
                return stored is null
                    ? Fail(change, $"ConfigMgr removed setting {before!.Name} from {ci}, but the new setting {temp} could not be read back. Refresh the list.")
                    : Partial(change, $"ConfigMgr replaced setting {before!.Name} on {ci}, but the new setting kept the temporary name {temp}. Undo puts the old setting back.",
                        Undo(ComplianceChangeKind.EditSetting, stored, before with { SettingId = "" }, $"Puts setting {before.Name} back with its old values."), run.Steps);
            case (ComplianceChangeKind.RemoveSetting, "ok" or "partial"):
            {
                var removeUndo = Undo(ComplianceChangeKind.AddSetting, null, before! with { SettingId = "" }, $"Adds setting {before!.Name} again with its old values. It gets a new ID.");
                return result!.Status == "ok"
                    ? Done(change, $"Removed setting {before.Name} from {ci}. Undo adds it again with a new ID.", removeUndo, run.Steps)
                    : Partial(change, $"ConfigMgr removed setting {before.Name} from {ci} and then reported an error. Undo adds it again.", removeUndo, run.Steps);
            }
            case (_, "not-stored"):
                return Fail(change, $"ConfigMgr reported the change to {ci} as done but did not store it, so nothing changed. Check the data type and the values against what ConfigMgr accepts.");
            case (_, "item-missing"):
                return Refused(change, $"Configuration item {ci} does not exist any more. Refresh the list.");
            case (_, "changed"):
                return Refused(change, $"Configuration item {ci} changed since the plan was shown (another revision exists), so it was not changed. Refresh the list and try again.");
            case (_, "setting-missing"):
                return Refused(change, $"Setting {before?.Name} does not exist on {ci} any more. Refresh the list.");
            case (_, "setting-duplicate"):
                return Refused(change, $"More than one setting on {ci} is named {before?.Name}. The ConfigMgr cmdlet would change or remove all of them, so nothing was changed. Rename them in the console first.");
            case (_, "setting-name-taken"):
                return Refused(change, $"Configuration item {ci} already has a setting named {after?.Name}.");
            case (_, "failed"):
                return Fail(change, $"ConfigMgr did not make the change to {ci}. The log has the details.");
        }
        if (run.Uncertain)
            return Fail(change, $"The result of the change to {ci} is not known: ConfigMgr reported an error during the change. Refresh the settings to see the current state.");
        return run.Failure ?? Fail(change, WriteFailedMessage(change));
    }

    /// <summary>The prepared value, or the live state when the plan saw no remediating deployment; null when the live read failed.</summary>
    private async Task<bool?> InRemediatingDeploymentAsync(ComplianceChange change)
    {
        if (change.InRemediatingDeployment) return true;
        try
        {
            var (_, _, remediating) = await ReadItemAsync(change.CiId!, CancellationToken.None).ConfigureAwait(false);
            return remediating.Count > 0;
        }
        catch (ComplianceServiceException)
        {
            return null;
        }
    }

    private async Task<CiSetting?> ReadSettingOrNullAsync(string ciId, string settingId)
    {
        try
        {
            var (_, settings) = await ReadSettingsAsync(ciId, CancellationToken.None).ConfigureAwait(false);
            return settings.FirstOrDefault(s => s.SettingId == settingId);
        }
        catch (ComplianceServiceException)
        {
            return null;
        }
    }

    // ---- Remove catalog objects -----------------------------------------------------------------

    private async Task<ComplianceOutcome> ApplyRemoveObjectsAsync(ComplianceChange change)
    {
        var (deployments, baselines, items) = SplitCreated(change.CreatedIds);
        if (deployments.Count + baselines.Count + items.Count == 0) return Refused(change, NotPreparedMessage);
        var run = await RunWriteAsync(change, new ConfigMgrParameters
        {
            { "Operation", "RemoveObjects" }, { "DeploymentIds", deployments.ToArray() }, { "BaselineIds", baselines.ToArray() }, { "ItemIds", items.ToArray() }
        }).ConfigureAwait(false);
        var steps = (run.Result?.Steps ?? []).Select(StepLine).ToList();
        var kept = KeptText(run.Result?.Kept);
        switch (run.Result?.Status)
        {
            case "ok":
                return Done(change, $"Removed the objects the catalog deploy created: {Count(deployments.Count, "deployment")}, {Count(baselines.Count, "baseline")}, {Count(items.Count, "configuration item")}.", null, steps);
            case "kept":
                return Fail(change, $"{kept} The other objects were removed.") with { Steps = steps };
            case "some-remain":
                var left = string.Join(", ", (run.Result.References ?? []).Select(RefText));
                return Fail(change, $"ConfigMgr did not remove {left}. Something else may use them now; check them in the console.{(kept.Length > 0 ? " " + kept : "")} The other objects were removed.") with { Steps = steps };
        }
        if (run.Uncertain)
            return Fail(change, "The result of the removal is not known: ConfigMgr reported an error during the change. Refresh the lists to see which objects still exist.");
        return run.Failure ?? Fail(change, WriteFailedMessage(change));
    }

    /// <summary>One sentence per kept object: its name and ID, and what uses it.</summary>
    private static string KeptText(IReadOnlyList<KeptRow>? kept) =>
        string.Join(" ", (kept ?? []).Select(k =>
        {
            var parts = (k.Ref ?? "").Split(':', 2);
            var noun = parts[0] == "ConfigurationItem" ? "Configuration item" : parts[0];
            var uses = string.Join(", ", k.UsedBy ?? []);
            return $"{noun} {Shown(k.Name, parts.Length == 2 ? parts[1] : "")} was kept because it is used by something this run did not create{(uses.Length > 0 ? ": " + uses : "")}.";
        }));

    private static string RefText(string reference)
    {
        var parts = reference.Split(':', 2);
        return parts.Length == 2 ? $"{(parts[0] == "ConfigurationItem" ? "configuration item" : parts[0].ToLowerInvariant())} {parts[1]}" : reference;
    }

    private static string StepLine(string step)
    {
        var parts = step.Split(':', 2);
        return parts[0] switch
        {
            "removed" => $"Removed {RefText(parts.Length > 1 ? parts[1] : "")}.",
            "gone" => $"Not found in ConfigMgr, so not removed: {RefText(parts.Length > 1 ? parts[1] : "")}.",
            "kept" => $"Kept {RefText(parts.Length > 1 ? parts[1] : "")}: something this run did not create uses it.",
            "created" => "Created the deployment.",
            "set" => "Set the new values.",
            "added" => "Added the setting.",
            "renamed" => "Gave the new setting its name.",
            "tagged" => "Wrote the drift tag.",
            _ => step
        };
    }

    // ---- Write runs ---------------------------------------------------------------------------

    /// <summary>
    /// One write script run. <c>Uncertain</c> marks a failure that can follow a change ConfigMgr
    /// already made: the session failed during the write.
    /// </summary>
    private sealed record WriteRun(WriteResult? Result, ComplianceOutcome? Failure, bool Uncertain, IReadOnlyList<string> Steps);

    private async Task<WriteRun> RunWriteAsync(ComplianceChange change, ConfigMgrParameters parameters)
    {
        WriteResult? result;
        try
        {
            var output = await _session.InvokeAsync(WriteScript, Masked(parameters), CancellationToken.None).ConfigureAwait(false);
            result = Rows<WriteResult>(output, "Result").LastOrDefault();
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
            if (message is null) return new WriteRun(null, Refused(change, $"The change was not made. {SessionProblem(ex.Failure)}"), false, []);
            var uncertain = ex.Failure is ConfigMgrFailure.Timeout or ConfigMgrFailure.ProcessDied or ConfigMgrFailure.Unexpected or ConfigMgrFailure.ScriptFailed;
            return new WriteRun(null, Fail(change, message), uncertain, []);
        }
        catch (ComplianceServiceException)
        {
            return new WriteRun(null, Fail(change, WriteFailedMessage(change)), true, []);
        }
        if (result is null)
            return new WriteRun(null, Fail(change, WriteFailedMessage(change)), true, []);
        if (!string.IsNullOrWhiteSpace(result.Detail))
            _log?.Warning(LogArea, $"{change.Kind} returned {result.Status}: {CollectionService.FailureKinds(result.Detail)}.");
        var steps = (result.Steps ?? []).Select(StepLine).ToList();
        return result.Status switch
        {
            "changed" => new WriteRun(result, Refused(change, "The object changed since the plan was shown, so it was not changed. Refresh the list and try again."), false, steps),
            "missing" => new WriteRun(result, Refused(change, "The deployment does not exist any more. Refresh the list."), false, steps),
            "collection-missing" => new WriteRun(result, Refused(change, "The target collection does not exist any more. Refresh the list."), false, steps),
            "not-device" => new WriteRun(result, Refused(change, "The target collection is not a device collection."), false, steps),
            "failed" when change.Kind is ComplianceChangeKind.SetRemediation => new WriteRun(result, Fail(change, WriteFailedMessage(change)), false, steps),
            _ => new WriteRun(result, null, false, steps)
        };
    }

    private static string WriteFailedMessage(ComplianceChange change) => $"ConfigMgr did not make the change ({KindText(change.Kind)}). The log has the details.";

    private static string KindText(ComplianceChangeKind kind) => kind switch
    {
        ComplianceChangeKind.CatalogDeploy => "catalog deploy",
        ComplianceChangeKind.SetRemediation => "remediation",
        ComplianceChangeKind.Redeploy => "redeploy",
        ComplianceChangeKind.Reassign => "reassign",
        ComplianceChangeKind.DeleteDeployment => "delete deployment",
        ComplianceChangeKind.CreateDeployment => "create deployment",
        ComplianceChangeKind.AddSetting => "add setting",
        ComplianceChangeKind.EditSetting => "edit setting",
        ComplianceChangeKind.RemoveSetting => "remove setting",
        ComplianceChangeKind.RemoveCatalogObjects => "remove catalog objects",
        _ => "change"
    };

    private ComplianceOutcome Partial(ComplianceChange change, string message, ComplianceChange? undo, IReadOnlyList<string> steps)
    {
        _log?.Warning(LogArea, $"{change.Kind} did not finish after ConfigMgr made a change; the outcome carries its undo.");
        return new ComplianceOutcome(change, false, message, undo, steps);
    }

    private static ComplianceOutcome Fail(ComplianceChange change, string message) => new(change, false, message, null, []);

    /// <summary>An outcome for a change that was not attempted: refused, stale or not prepared.</summary>
    private static ComplianceOutcome Refused(ComplianceChange change, string message) =>
        new(change, false, message.StartsWith(SkippedPrefix, StringComparison.Ordinal) ? message : SkippedPrefix + message, null, []);

    private static ComplianceOutcome Done(ComplianceChange change, string message, ComplianceChange? undo, IReadOnlyList<string> steps) =>
        new(change, true, message, undo, steps);
}
