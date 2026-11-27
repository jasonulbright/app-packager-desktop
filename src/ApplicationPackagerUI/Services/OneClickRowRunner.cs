using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using Supersedence = ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;
using System.IO;
using System.Text.Json;

namespace ApplicationPackagerUI.Services;

/// <summary>The steps one One Click row runs: check, build plan, run snapshot, stage and each publish.</summary>
public interface IOneClickExecution
{
    Task<string?> GetLatestVersionAsync(PackagerInfo row, CancellationToken ct);
    string VersionSource(PackagerInfo row);
    /// <summary>The version of the local installer that the stage will take; null when the stage downloads.</summary>
    string? LocalStageVersion(PackagerInfo row);
    PackagerExecutionService.OneClickBuildPlan? ResolveBuildPlan(PackagerInfo row);
    string? StagedManifestPath(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot? snapshot);
    RunSnapshot? CreateRowSnapshot(PackagerInfo row, PackagerExecutionService.OneClickBuildPlan plan, string target, out string? refusal);
    Task<PackagerExecutionResult> StageAsync(PackagerInfo row, Action<string> log, RunSnapshot snapshot, CancellationToken ct);
    Task<PackagerExecutionResult> PackageAsync(PackagerInfo row, string comment, Action<string> log, OnExistingBehavior onExisting,
        RunSnapshot snapshot, string target, CancellationToken ct);
    Task<WsusStepResult> PublishWsusAsync(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot snapshot, Action<string> log, CancellationToken ct);

}

/// <summary>The execution service and the WSUS publisher as one One Click row uses them.</summary>
public sealed class OneClickExecution(PackagerExecutionService execution, IWsusPublisher wsusPublisher) : IOneClickExecution
{
    public Task<string?> GetLatestVersionAsync(PackagerInfo row, CancellationToken ct) => execution.GetLatestVersionAsync(row, ct);

    public string VersionSource(PackagerInfo row) => execution.VersionSource(row);

    public string? LocalStageVersion(PackagerInfo row) => execution.LocalStageVersion(row);

    public PackagerExecutionService.OneClickBuildPlan? ResolveBuildPlan(PackagerInfo row) => execution.ResolveBuildPlan(row);

    public string? StagedManifestPath(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot? snapshot) =>
        execution.StagedManifestPath(row, stage, snapshot);

    public RunSnapshot? CreateRowSnapshot(PackagerInfo row, PackagerExecutionService.OneClickBuildPlan plan, string target, out string? refusal) =>
        execution.CreateRowSnapshot(row, plan, target, out refusal);

    public Task<PackagerExecutionResult> StageAsync(PackagerInfo row, Action<string> log, RunSnapshot snapshot, CancellationToken ct) =>
        execution.StageAsync(row, log, ct, null, snapshot, recordInventory: false);

    public Task<PackagerExecutionResult> PackageAsync(PackagerInfo row, string comment, Action<string> log, OnExistingBehavior onExisting,
        RunSnapshot snapshot, string target, CancellationToken ct) =>
        execution.PackageAsync(row, comment, log, ct, onExisting, snapshot, target, recordInventory: false);

    public Task<WsusStepResult> PublishWsusAsync(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot snapshot, Action<string> log, CancellationToken ct) =>
        execution.PublishWsusAsync(row, stage, snapshot, wsusPublisher, log, ct, recordInventory: false);

}

/// <summary>
/// One One Click row: checked, decided against the per-destination history and the plan's
/// readiness, staged with one run snapshot, and published to each remaining destination
/// from that same snapshot, ConfigMgr first. A destination's result and its history are
/// recorded before a cancellation stops the row.
/// </summary>
public sealed class OneClickRowRunner(IOneClickExecution execution, ISettingsReader<AppSettings> settingsService,
    ApplicationInventoryService inventory, ApplicationInventoryProjection projection, Action<string> log)
{
    /// <summary>Called after the check wrote the row's latest version.</summary>
    public Action? VersionChecked { get; init; }

    /// <summary>Writes the Intune build and publish notes of a package result.</summary>
    public Action<PackagerInfo, PackagerExecutionResult>? IntuneNotes { get; init; }

    /// <summary>Writes the standard error of a failed step.</summary>
    public Action<string>? StdErr { get; init; }

    public async Task RunAsync(OneClickPlanItem item, OneClickRunOptions options, CancellationToken ct)
    {
        var p = item.Row;
        if (p is null) { Finish(item, OneClickOutcomes.Skipped, "not in the grid"); return; }
        var settings = settingsService.Load();
        var plan = execution.ResolveBuildPlan(p);
        var identity = ApplicationInventoryProjection.IdentityFor(p, plan);
        var entry = projection.Entry(p, settings, plan);
        var lastKnown = item.PriorKnownVersion ?? entry?.LastKnownVersion ?? "";
        var operationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        inventory.BeginOperation(new InventoryOperation(operationId, "OneClickRow", p.Uid, identity,
            Target(item.Destinations.Names), startedAt));

        try
        {
            var action = OneClickActionNames.Normalize(options.Action);
            var stepLog = (string line) => log($"  {line}");

            // The version that the plan window showed. The run publishes that version or nothing.
            var planned = item.Version;
            item.Step = "checking " + p.Application;
            p.Status = "Checking latest...";
            log($"One Click: {p.Application} ({p.Script})");
            var source = execution.VersionSource(p);
            string? latest;
            try { latest = await execution.GetLatestVersionAsync(p, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                inventory.RecordVendorCheck(new InventoryVendorCheck(p.Uid, p.Application, null, source,
                    InventoryOutcome.Cancelled, DateTimeOffset.UtcNow, operationId, "Check cancelled."));
                Finish(item, OneClickOutcomes.Skipped, "check cancelled");
                throw;
            }
            catch (Exception ex)
            {
                latest = null;
                log($"Latest check failed: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(latest))
            {
                inventory.RecordVendorCheck(new InventoryVendorCheck(p.Uid, p.Application, null, source,
                    InventoryOutcome.Failed, DateTimeOffset.UtcNow, operationId, "Check failed."));
                p.Status = "Check error";
                Finish(item, OneClickOutcomes.Failed, "check failed");
                return;
            }
            var checkedAt = DateTimeOffset.UtcNow;

            // A vendor feed can step back to an older release. The check is recorded as
            // skipped, which leaves the newer version on record, and the row does nothing.
            if (!options.Force && lastKnown.Length > 0 && VersionCompare.CompareStrict(lastKnown, latest) > 0)
            {
                var older = $"vendor reports {latest}, older than {lastKnown} seen before";
                inventory.RecordVendorCheck(new InventoryVendorCheck(p.Uid, p.Application, latest, source,
                    InventoryOutcome.Skipped, checkedAt, operationId, older));
                p.LatestVersion = latest;
                item.Version = latest;
                p.Status = "Vendor older than last seen";
                log($"{p.Application}: the vendor reports {latest}, older than {lastKnown} seen before - skipping");
                Finish(item, OneClickOutcomes.Skipped, older);
                return;
            }

            inventory.RecordVendorCheck(new InventoryVendorCheck(p.Uid, p.Application, latest, source,
                InventoryOutcome.Succeeded, checkedAt, operationId));
            p.LatestVersion = latest;
            p.LastChecked = checkedAt.LocalDateTime;
            // The version that the stage will hold: the local installer's when the stage takes one, else the vendor's. It decides what
            // each destination takes. The vendor version only decides whether there is an update.
            var local = execution.LocalStageVersion(p);
            var toPublish = local ?? latest;
            item.Version = toPublish;
            log("Latest: " + latest);
            VersionChecked?.Invoke();
            var versionChanged = lastKnown.Length == 0 || !string.Equals(lastKnown, latest, StringComparison.OrdinalIgnoreCase);

            if (action == OneClickActionNames.Report)
            {
                p.Status = versionChanged ? CveRowState.UpdateStatus(p) : "Up to date";
                Finish(item, OneClickOutcomes.Checked, versionChanged ? "update available" : "up to date");
                return;
            }

        var buildStale = plan is not null && BuildRecords.IsCurrent(plan.DataRoot, plan.ApplicationId, plan.ProfileId, toPublish,
            plan.ProfileRevision, settings.ScriptSigning.PolicyDigest) == false;
        var buildKey = plan is null ? "" : OneClickHistory.BuildKey(plan.ProfileId, plan.ProfileRevision, settings.ScriptSigning.PolicyDigest);
        var decision = OneClickPlanner.Decide(action, item.Destinations.Names, entry, toPublish, p.CurrentVersion, options.Force, versionChanged, buildStale,
            staged: entry?.LastStaged is not null, item.NotReady, buildKey);
        foreach (var (destination, cell) in decision.GuardResults)
        {
            item.SetResult(destination, cell);
            item.Cells.Add(new(destination, cell.StartsWith("not supported", StringComparison.OrdinalIgnoreCase)
                ? PublishDestinationKind.NotSupported : PublishDestinationKind.Skipped, "", cell));
        }

        var reasons = decision.SkipReasons.ToList();

        if (!decision.Stage)
        {
            p.Status = "Up to date";
            log($"No change - skipping: {p.Application}");
            Finish(item, OneClickOutcomes.Skipped, decision.SkipReason);
            return;
        }
        if (buildStale && !versionChanged && decision.PublishTo.Count == 0)
            log($"One Click: {p.Application} {toPublish} restages: the last build differs in version, profile revision or signing policy. No destination takes a publish of it.");

        var publishes = action == OneClickActionNames.StageAndPackage && decision.PublishTo.Count > 0;
        if (publishes && ReviewedVersion.Refusal(planned, toPublish) is not null)
        {
            var found = local is null ? $"the check now reads {latest}" : $"the installer source folder now holds {local}";
            var changed = $"the plan showed {(planned.Length > 0 ? planned : "no version")} and {found}; open One Click again and review the plan";
            p.Status = CveRowState.UpdateStatus(p);
            log($"One Click: {p.Application} is not published: {changed}.");
            Finish(item, OneClickOutcomes.Skipped, changed);
            return;
        }

        // One run snapshot serves the stage and every publish of this row.
        if (plan is null)
        {
            p.Status = "Stage error";
            log($"Run snapshot not created for {p.Application}: the workbench could not resolve its application or profile.");
            Finish(item, OneClickOutcomes.Failed, "run snapshot not created");
            return;
        }
        var snapshot = execution.CreateRowSnapshot(p, plan, Target(decision.PublishTo), out var refusal);
        if (snapshot is null)
        {
            p.Status = "Stage error";
            log($"Run snapshot refused for {p.Application}: {refusal}");
            Finish(item, OneClickOutcomes.Failed, "run snapshot refused: " + refusal);
            return;
        }
        log($"  Workbench profile {snapshot.ProfileId} (revision {snapshot.ProfileRevision}, build {snapshot.BuildId})");

        // The stage uses the saved install parameters and shows no review dialog.
        item.Step = "staging " + p.Application;
        p.Status = "Staging...";
        log("Stage: " + p.Application);
        var stage = await execution.StageAsync(p, stepLog, snapshot, ct);
        ct.ThrowIfCancellationRequested();
        if (stage.ExitCode != 0)
        {
            p.Status = "Stage error";
            log($"Stage FAILED (exit {stage.ExitCode})");
            StdErr?.Invoke(stage.StdErr);
            Finish(item, OneClickOutcomes.Failed, "stage failed");
            return;
        }
        if (publishes && StagedVersionRefusal(p, stage, snapshot, toPublish) is { } otherVersion)
        {
            p.Status = "Publish error";
            log($"One Click: {p.Application}: {otherVersion}");
            Finish(item, OneClickOutcomes.Failed, otherVersion);
            return;
        }
        p.Status = "Staged";
        PersistStagedBuild(p, stage, snapshot, identity, operationId);

        if (action == OneClickActionNames.Stage || decision.PublishTo.Count == 0)
        {
            Finish(item, OneClickOutcomes.Staged, string.Join("; ", decision.SkipReasons));
            return;
        }

        // ConfigMgr publishes first: it is the only destination that reads the network copy.
        var publishedTo = new List<string>();
        int published = 0, failed = 0, notSupported = 0;
        foreach (var destination in decision.PublishTo)
        {
            if (ct.IsCancellationRequested) StopCanceled(item, p, publishedTo);
            item.Step = $"publishing {p.Application} to {destination}";
            p.Status = $"Publishing to {destination}...";
            string cell, id = "", detail = "";
            string? ciId = null, modelName = null;
            var ok = false;
            var present = false;
            var destinationOutcome = InventoryOutcome.Failed;
            // The cell of what the destination answered, set as soon as it answers: a later error must not hide a publish that happened.
            PublishDestinationResult? answered = null;
            var intune = false;
            try
            {
                switch (destination)
                {
                    case OneClickDestinations.ConfigMgr:
                    {
                        var onExisting = OneClickOnExisting.Normalize(options.OnExisting) == OneClickOnExisting.Overwrite
                            ? OnExistingBehavior.Overwrite : OnExistingBehavior.Skip;
                        var result = await execution.PackageAsync(p, options.Comment, stepLog, onExisting, snapshot, PublishRunTargets.ConfigMgr, ct);
                        answered = PublishDestinationResult.FromConfigMgr(result);
                        id = ApplicationName(result.StdOut);
                        if (result.Conflict is { } conflict)
                        {
                            destinationOutcome = InventoryOutcome.Skipped;
                            cell = $"skipped: application {conflict.ApplicationName} already exists";
                            reasons.Add($"ConfigMgr: application exists, policy {OneClickOnExisting.Normalize(options.OnExisting)}");
                        }
                        else if (result.ExitCode != 0)
                        {
                            // A content step failed after the application was created: the run failed, and the application exists.
                            var applicationExists = result.ConfigMgrOutcome == InventoryOutcome.Partial;
                            destinationOutcome = applicationExists ? InventoryOutcome.Partial : InventoryOutcome.Failed;
                            if (applicationExists) (ciId, modelName) = (result.ConfigMgrCiId, result.ConfigMgrModelName);
                            StdErr?.Invoke(result.StdErr);
                            cell = $"failed: exit code {result.ExitCode}";
                            detail = result.StdErr.Trim();
                        }
                        else
                        {
                            destinationOutcome = result.ConfigMgrOutcome == InventoryOutcome.PublishedSupersedenceNotApplied
                                ? InventoryOutcome.PublishedSupersedenceNotApplied : InventoryOutcome.Succeeded;
                            ok = true;
                            cell = "published " + id;
                            if (destinationOutcome == InventoryOutcome.PublishedSupersedenceNotApplied)
                            {
                                // The application is complete and the run did not fail: the row is published with a warning, and the
                                // version stays pending for the supersedence-only pass of the next run.
                                cell = $"published {id}, supersedence not applied";
                                reasons.Add("ConfigMgr: supersedence not applied, the next run applies it");
                            }
                            (ciId, modelName) = (result.ConfigMgrCiId, result.ConfigMgrModelName);
                            // A warning of a run that succeeded (an icon that was not applied) goes to the history and the report.
                            detail = result.ConfigMgrDetail ?? "";
                        }
                        IntuneNotes?.Invoke(p, result);
                        break;
                    }
                    case OneClickDestinations.Wsus:
                    {
                        var step = await execution.PublishWsusAsync(p, stage, snapshot, stepLog, ct);
                        answered = PublishDestinationResult.FromWsus(step);
                        log("WSUS publish: " + step.Message);
                        var mapped = OneClickPlanner.WsusResult(step);
                        (cell, detail, id, ok, present) = (mapped.Cell, mapped.Detail, mapped.Id, mapped.Ok, mapped.AlreadyPresent);
                        destinationOutcome = step.Refused ? InventoryOutcome.Refused
                            : mapped.Ok && mapped.AlreadyPresent ? InventoryOutcome.AlreadyPresent
                            : mapped.Ok ? InventoryOutcome.Succeeded : InventoryOutcome.Failed;
                        if (present) reasons.Add($"WSUS: {toPublish} already on the server");
                        if (step.Refused)
                        {
                            notSupported++;
                            reasons.Add("WSUS: not supported");
                        }
                        break;
                    }
                    default:
                    {
                        var result = await execution.PackageAsync(p, options.Comment, stepLog, OnExistingBehavior.Skip, snapshot, PublishRunTargets.IntuneOnly, ct);
                        // The Intune cell keeps what the publish said: a supersedence that was not applied, or a destination that cannot take it.
                        answered = PublishDestinationResult.FromIntune(result);
                        intune = true;
                        IntuneNotes?.Invoke(p, result);
                        if (result.ExitCode == 0)
                        {
                            destinationOutcome = InventoryOutcome.Succeeded;
                            ok = true;
                            id = IntuneAppId(result.IntunePublishNote);
                            cell = "published " + id;
                        }
                        else
                        {
                            destinationOutcome = InventoryOutcome.Failed;
                            cell = string.IsNullOrWhiteSpace(result.IntunePublishNote) ? $"failed: exit code {result.ExitCode}" : result.IntunePublishNote;
                            detail = result.StdErr.Trim();
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                item.Cells.Add(answered ?? new(destination, PublishDestinationKind.Failed, "cancel", "The run stopped during this publish. Check the system."));
                RecordDestination(p, identity, settings, destination, toPublish, id, InventoryOutcome.Cancelled,
                    detail.Length == 0 ? "Publish cancelled." : detail, snapshot.BuildId, operationId);
                StopCanceled(item, p, publishedTo);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                item.Cells.Add(answered ?? new(destination, PublishDestinationKind.Failed, "error", "The publish ended with an error. Check the system. " + ex.Message));
                throw;
            }

            // The cell is added before the history write, which can fail on its own.
            item.Cells.Add(intune && answered is not null ? answered
                : ResultCell(destination, destinationOutcome, detail.Length > 0 ? detail : cell, id));
            RecordDestination(p, identity, settings, destination, toPublish, id, destinationOutcome, detail, snapshot.BuildId, operationId, ciId, modelName);
            item.SetResult(destination, cell, id, detail);
            if (ok)
            {
                if (!present) published++;
                publishedTo.Add(destination);
            }
            else if (!OneClickPlanner.IsSkipOrRefusal(cell))
            {
                failed++;
            }
        }
        var (outcome, status) = OneClickPlanner.Conclude(published, failed, notSupported);
        p.Status = status;
        Finish(item, outcome, string.Join("; ", reasons));
        }
        finally
        {
            var outcome = InventoryOperationOutcome(item, ct.IsCancellationRequested);
            inventory.CompleteOperation(operationId, outcome, DateTimeOffset.UtcNow, item.Reason);
        }
    }

    /// <summary>Why the stage is not the version that the run decided to publish; null when it is, or when no manifest exists (the publish then refuses).</summary>
    private string? StagedVersionRefusal(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot snapshot, string version)
    {
        if (execution.StagedManifestPath(row, stage, snapshot) is not { } path || !File.Exists(path)) return null;
        try { return ReviewedVersion.Refusal(version, new ManifestService().Read(path).SoftwareVersion); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return $"The stage manifest {path} cannot be read, so its version cannot be compared: {ex.Message} Nothing was published.";
        }
    }

    /// <summary>The results cell of one destination, from the outcome that the history records.</summary>
    public static PublishDestinationResult ResultCell(string destination, InventoryOutcome outcome, string message, string id) => outcome switch
    {
        InventoryOutcome.Succeeded or InventoryOutcome.AlreadyPresent => new(destination, PublishDestinationKind.Published, "", message, id),
        InventoryOutcome.PublishedSupersedenceNotApplied => new(destination, PublishDestinationKind.Published, "", message, id) { Warning = true },
        InventoryOutcome.Partial => new(destination, PublishDestinationKind.Failed, "content", message, id),
        InventoryOutcome.Skipped or InventoryOutcome.Cancelled => new(destination, PublishDestinationKind.Skipped, "", message, id),
        InventoryOutcome.Refused => new(destination, PublishDestinationKind.NotSupported, "", message, id),
        _ => new(destination, PublishDestinationKind.Failed, "", message, id)
    };

    /// <summary>Ends a canceled row with what it published; throws the cancellation.</summary>
    private static void StopCanceled(OneClickPlanItem item, PackagerInfo row, IReadOnlyList<string> publishedTo)
    {
        row.Status = "Cancelled";
        Finish(item, publishedTo.Count > 0 ? OneClickOutcomes.Published : OneClickOutcomes.Skipped,
            publishedTo.Count > 0
                ? "run canceled after publishing to " + string.Join(", ", publishedTo)
                : "run canceled before any publish");
        throw new OperationCanceledException();
    }

    private static void Finish(OneClickPlanItem item, string outcome, string reason)
    {
        item.Outcome = outcome;
        if (reason.Length > 0) item.Reason = reason;
        item.Step = outcome;
    }

    private static InventoryOutcome InventoryOperationOutcome(OneClickPlanItem item, bool cancelled)
    {
        var results = item.Results.Values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var hasSuccess = results.Any(value => value.StartsWith("published ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("already on the server ", StringComparison.OrdinalIgnoreCase));
        var hasFailureOrRefusal = results.Any(value => value.StartsWith("failed", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("not supported", StringComparison.OrdinalIgnoreCase));
        if (hasSuccess && (hasFailureOrRefusal || cancelled)) return InventoryOutcome.Partial;
        if (cancelled) return InventoryOutcome.Cancelled;
        return item.Outcome switch
        {
            OneClickOutcomes.Published or OneClickOutcomes.Staged or OneClickOutcomes.Checked or OneClickOutcomes.Superseded => InventoryOutcome.Succeeded,
            OneClickOutcomes.NotSupported => InventoryOutcome.Refused,
            OneClickOutcomes.Skipped => InventoryOutcome.Skipped,
            _ => InventoryOutcome.Failed
        };
    }

    /// <summary>
    /// The run target the snapshot records for the destinations a row publishes to: the name of
    /// that combination, as for the stored target. Each publish still runs with its own destination target.
    /// </summary>
    public static string Target(IReadOnlyList<string> publishTo) =>
        new DestinationSet(publishTo.Contains(OneClickDestinations.ConfigMgr), publishTo.Contains(OneClickDestinations.Wsus),
            publishTo.Contains(OneClickDestinations.Intune)) is { Any: true } set ? set.ToTargetName() : RunTargets.ContentOnly;

    /// <summary>The ConfigMgr application a package run named in "Packaged: &lt;name&gt;".</summary>
    private static string ApplicationName(string stdOut)
    {
        foreach (var line in stdOut.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Packaged: ", StringComparison.Ordinal)) return trimmed["Packaged: ".Length..];
        }
        return "";
    }

    /// <summary>The app id an Intune publish note names in "(app id &lt;id&gt;)".</summary>
    private static string IntuneAppId(string note)
    {
        var start = note.IndexOf("app id ", StringComparison.Ordinal);
        if (start < 0) return "";
        var id = note[(start + "app id ".Length)..];
        var end = id.IndexOf(')');
        return end < 0 ? id.Trim() : id[..end].Trim();
    }

    private void PersistStagedBuild(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot snapshot,
        InventoryApplicationIdentity identity, string operationId)
    {
        try
        {
            var manifestPath = execution.StagedManifestPath(row, stage, snapshot);
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                log("  Inventory: the stage did not expose its manifest; no sealed build reference was recorded.");
                return;
            }

            var dataRoot = string.IsNullOrWhiteSpace(snapshot.DataRoot) ? WorkbenchPaths.ResolveDataRoot() : snapshot.DataRoot;
            var buildRecord = BuildRecords.List(dataRoot, snapshot.ApplicationId, snapshot.ProfileId)
                .FirstOrDefault(record => string.Equals(WorkbenchJson.Get(record, "BuildId").Text, snapshot.BuildId, StringComparison.OrdinalIgnoreCase));
            if (buildRecord is null)
            {
                log("  Inventory: the stage has no matching sealed build record; no package reference was recorded.");
                return;
            }

            var manifest = JsonSerializer.Deserialize<StageManifest>(File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null || !string.Equals(manifest.BuildId, snapshot.BuildId, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(manifest.SoftwareVersion))
            {
                log("  Inventory: the manifest does not match this run snapshot; no package reference was recorded.");
                return;
            }

            var buildRecordPath = WorkbenchJson.Get(buildRecord, "Path").Text
                ?? Path.Combine(BuildRecords.GetBuildFolder(dataRoot, snapshot.ApplicationId, snapshot.ProfileId, snapshot.BuildId), BuildRecords.FileName);
            inventory.RecordStagedBuild(new InventoryStagedBuild(
                identity with { ApplicationId = snapshot.ApplicationId, ProfileId = snapshot.ProfileId, ProfileRevision = snapshot.ProfileRevision },
                manifest.SoftwareVersion, snapshot.BuildId, buildRecordPath, manifestPath, DateTimeOffset.UtcNow, operationId));
        }
        catch (Exception ex)
        {
            log($"  Inventory: could not record the sealed build reference: {ex.Message}");
        }
    }

    private void RecordDestination(PackagerInfo row, InventoryApplicationIdentity identity, AppSettings settings,
        string destination, string version, string? objectId, InventoryOutcome outcome, string? detail,
        string? buildId, string operationId, string? configMgrCiId = null, string? configMgrModelName = null)
    {
        var environment = ApplicationInventoryProjection.Environments(settings)
            .First(item => string.Equals(item.Destination, destination, StringComparison.OrdinalIgnoreCase)).Environment;
        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome(row.Uid, identity, environment, "Publish",
            version, outcome, objectId, DateTimeOffset.UtcNow, buildId, operationId, detail, configMgrCiId, configMgrModelName));
    }
}
