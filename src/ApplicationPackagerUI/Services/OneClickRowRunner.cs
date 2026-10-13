using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
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
        var lastKnown = entry?.LastKnownVersion ?? "";
        var operationId = Guid.NewGuid().ToString("N");
        var startedAt = DateTimeOffset.UtcNow;
        inventory.BeginOperation(new InventoryOperation(operationId, "OneClickRow", p.Uid, identity,
            Target(item.Destinations.Names), startedAt));

        try
        {
            var action = OneClickActionNames.Normalize(options.Action);
            var stepLog = (string line) => log($"  {line}");

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
            item.Version = latest;
            log("Latest: " + latest);
            VersionChecked?.Invoke();
            var versionChanged = lastKnown.Length == 0 || !string.Equals(lastKnown, latest, StringComparison.OrdinalIgnoreCase);

            if (action == OneClickActionNames.Report)
            {
                p.Status = versionChanged ? CveRowState.UpdateStatus(p) : "Up to date";
                Finish(item, OneClickOutcomes.Checked, versionChanged ? "update available" : "up to date");
                return;
            }

        var buildStale = plan is not null && BuildRecords.IsCurrent(plan.DataRoot, plan.ApplicationId, plan.ProfileId, latest,
            plan.ProfileRevision, settings.ScriptSigning.PolicyDigest) == false;
        var buildKey = plan is null ? "" : OneClickHistory.BuildKey(plan.ProfileId, plan.ProfileRevision, settings.ScriptSigning.PolicyDigest);
        var decision = OneClickPlanner.Decide(action, item.Destinations.Names, entry, latest, p.CurrentVersion, options.Force, versionChanged, buildStale,
            staged: entry?.LastStaged is not null, item.NotReady, buildKey);
        foreach (var (destination, cell) in decision.GuardResults) item.SetResult(destination, cell);
        if (!decision.Stage)
        {
            p.Status = "Up to date";
            log($"No change - skipping: {p.Application}");
            Finish(item, OneClickOutcomes.Skipped, decision.SkipReason);
            return;
        }
        if (buildStale && !versionChanged && decision.PublishTo.Count == 0)
            log($"One Click: {p.Application} {latest} restages: the last build differs in version, profile revision or signing policy. No destination takes a publish of it.");

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
        p.Status = "Staged";
        PersistStagedBuild(p, stage, snapshot, identity, operationId);

        if (action == OneClickActionNames.Stage || decision.PublishTo.Count == 0)
        {
            Finish(item, OneClickOutcomes.Staged, string.Join("; ", decision.SkipReasons));
            return;
        }

        // ConfigMgr publishes first: it is the only destination that reads the network copy.
        var reasons = decision.SkipReasons.ToList();
        var publishedTo = new List<string>();
        int published = 0, failed = 0, notSupported = 0;
        foreach (var destination in decision.PublishTo)
        {
            if (ct.IsCancellationRequested) StopCanceled(item, p, publishedTo);
            item.Step = $"publishing {p.Application} to {destination}";
            p.Status = $"Publishing to {destination}...";
            string cell, id = "", detail = "";
            var ok = false;
            var present = false;
            var destinationOutcome = InventoryOutcome.Failed;
            try
            {
                switch (destination)
                {
                    case OneClickDestinations.ConfigMgr:
                    {
                        var onExisting = OneClickOnExisting.Normalize(options.OnExisting) == OneClickOnExisting.Overwrite
                            ? OnExistingBehavior.Overwrite : OnExistingBehavior.Skip;
                        var result = await execution.PackageAsync(p, options.Comment, stepLog, onExisting, snapshot, PublishRunTargets.ConfigMgr, ct);
                        id = ApplicationName(result.StdOut);
                        if (result.Conflict is { } conflict)
                        {
                            destinationOutcome = InventoryOutcome.Skipped;
                            cell = $"skipped: application {conflict.ApplicationName} already exists";
                            reasons.Add($"ConfigMgr: application exists, policy {OneClickOnExisting.Normalize(options.OnExisting)}");
                        }
                        else if (result.ExitCode != 0)
                        {
                            destinationOutcome = InventoryOutcome.Failed;
                            StdErr?.Invoke(result.StdErr);
                            cell = $"failed: exit code {result.ExitCode}";
                            detail = result.StdErr.Trim();
                        }
                        else
                        {
                            destinationOutcome = InventoryOutcome.Succeeded;
                            ok = true;
                            cell = "published " + id;
                        }
                        IntuneNotes?.Invoke(p, result);
                        break;
                    }
                    case OneClickDestinations.Wsus:
                    {
                        var step = await execution.PublishWsusAsync(p, stage, snapshot, stepLog, ct);
                        log("WSUS publish: " + step.Message);
                        var mapped = OneClickPlanner.WsusResult(step);
                        (cell, detail, id, ok, present) = (mapped.Cell, mapped.Detail, mapped.Id, mapped.Ok, mapped.AlreadyPresent);
                        destinationOutcome = step.Refused ? InventoryOutcome.Refused
                            : mapped.Ok && mapped.AlreadyPresent ? InventoryOutcome.AlreadyPresent
                            : mapped.Ok ? InventoryOutcome.Succeeded : InventoryOutcome.Failed;
                        if (present) reasons.Add($"WSUS: {latest} already on the server");
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
                RecordDestination(p, identity, settings, destination, latest, id, InventoryOutcome.Cancelled,
                    detail.Length == 0 ? "Publish cancelled." : detail, snapshot.BuildId, operationId);
                StopCanceled(item, p, publishedTo);
                throw;
            }

            RecordDestination(p, identity, settings, destination, latest, id, destinationOutcome, detail, snapshot.BuildId, operationId);
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
            OneClickOutcomes.Published or OneClickOutcomes.Staged or OneClickOutcomes.Checked => InventoryOutcome.Succeeded,
            OneClickOutcomes.NotSupported => InventoryOutcome.Refused,
            OneClickOutcomes.Skipped => InventoryOutcome.Skipped,
            _ => InventoryOutcome.Failed
        };
    }

    /// <summary>
    /// The run target the snapshot records for the destinations a row publishes to: the
    /// nearest single name, as for the stored target. WSUS with Intune has no name and
    /// records WSUSOnly; the Intune publish still runs, with its own IntuneOnly target.
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
        string buildId, string operationId)
    {
        var environment = ApplicationInventoryProjection.Environments(settings)
            .First(item => string.Equals(item.Destination, destination, StringComparison.OrdinalIgnoreCase)).Environment;
        inventory.RecordDestinationOutcome(new InventoryDestinationOutcome(row.Uid, identity, environment, "Publish",
            version, outcome, objectId, DateTimeOffset.UtcNow, buildId, operationId, detail));
    }
}
