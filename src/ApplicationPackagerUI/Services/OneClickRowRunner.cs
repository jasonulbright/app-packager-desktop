using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>The steps one One Click row runs: check, build plan, run snapshot, stage and each publish.</summary>
public interface IOneClickExecution
{
    Task<string?> GetLatestVersionAsync(PackagerInfo row, CancellationToken ct);
    PackagerExecutionService.OneClickBuildPlan? ResolveBuildPlan(PackagerInfo row);
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

    public PackagerExecutionService.OneClickBuildPlan? ResolveBuildPlan(PackagerInfo row) => execution.ResolveBuildPlan(row);

    public RunSnapshot? CreateRowSnapshot(PackagerInfo row, PackagerExecutionService.OneClickBuildPlan plan, string target, out string? refusal) =>
        execution.CreateRowSnapshot(row, plan, target, out refusal);

    public Task<PackagerExecutionResult> StageAsync(PackagerInfo row, Action<string> log, RunSnapshot snapshot, CancellationToken ct) =>
        execution.StageAsync(row, log, ct, null, snapshot);

    public Task<PackagerExecutionResult> PackageAsync(PackagerInfo row, string comment, Action<string> log, OnExistingBehavior onExisting,
        RunSnapshot snapshot, string target, CancellationToken ct) =>
        execution.PackageAsync(row, comment, log, ct, onExisting, snapshot, target);

    public Task<WsusStepResult> PublishWsusAsync(PackagerInfo row, PackagerExecutionResult stage, RunSnapshot snapshot, Action<string> log, CancellationToken ct) =>
        execution.PublishWsusAsync(row, stage, snapshot, wsusPublisher, log, ct);
}

/// <summary>
/// One One Click row: checked, decided against the per-destination history and the plan's
/// readiness, staged with one run snapshot, and published to each remaining destination
/// from that same snapshot, ConfigMgr first. A destination's result and its history are
/// recorded before a cancellation stops the row.
/// </summary>
public sealed class OneClickRowRunner(IOneClickExecution execution, SettingsService settingsService, Action<string> log)
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
        var action = OneClickActionNames.Normalize(options.Action);
        var stepLog = (string line) => log($"  {line}");
        var settings = settingsService.Load();
        var entry = settings.AppStates.TryGetValue(p.Uid, out var state) && state is not null ? OneClickSettingsMap.Entry(state) : null;
        var lastKnown = entry?.LastKnownVersion ?? "";

        // 1. Check latest
        item.Step = "checking " + p.Application;
        p.Status = "Checking latest...";
        log($"One Click: {p.Application} ({p.Script})");
        string? latest;
        try { latest = await execution.GetLatestVersionAsync(p, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            latest = null;
            log($"Latest check failed: {ex.Message}");
        }
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(latest))
        {
            p.Status = "Check error";
            Finish(item, OneClickOutcomes.Failed, "check failed");
            return;
        }
        p.LatestVersion = latest;
        p.LastChecked = DateTime.Now;
        item.Version = latest;
        log("Latest: " + latest);
        VersionChecked?.Invoke();
        var versionChanged = lastKnown.Length == 0 || !string.Equals(lastKnown, latest, StringComparison.OrdinalIgnoreCase);

        if (action == OneClickActionNames.Report)
        {
            p.Status = versionChanged ? "Update available" : "Up to date";
            Finish(item, OneClickOutcomes.Checked, versionChanged ? "update available" : "up to date");
            return;
        }

        // 2. Which destinations still need this version, and whether the build is stale.
        var plan = execution.ResolveBuildPlan(p);
        var buildStale = plan is not null && BuildRecords.IsCurrent(plan.DataRoot, plan.ApplicationId, plan.ProfileId, latest,
            plan.ProfileRevision, settings.ScriptSigning.PolicyDigest) == false;
        var decision = OneClickPlanner.Decide(action, item.Destinations.Names, entry, latest, p.CurrentVersion, options.Force, versionChanged, buildStale,
            staged: entry?.LastStaged is not null, item.NotReady);
        foreach (var (destination, cell) in decision.GuardResults) item.SetResult(destination, cell);
        if (!decision.Stage)
        {
            p.Status = "Up to date";
            log($"No change - skipping: {p.Application}");
            Finish(item, OneClickOutcomes.Skipped, decision.SkipReason);
            return;
        }
        if (buildStale && !versionChanged && decision.PublishTo.Count == 0)
            log($"One Click: {p.Application} {latest} restages: the last build differs in version, profile revision or signing policy. Its destinations carry this version, so it is not published.");

        // 3. One run snapshot for the stage and every publish of this row.
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

        // 4. Stage, with the saved install parameters and no review dialog.
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
        UpdateAppState(p.Uid, s => s.LastStaged = DateTime.Now);

        if (action == OneClickActionNames.Stage || decision.PublishTo.Count == 0)
        {
            Finish(item, OneClickOutcomes.Staged, string.Join("; ", decision.SkipReasons));
            return;
        }

        // 5. Publish per destination, ConfigMgr first: it is the only one that reads the network copy.
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
                            cell = $"skipped: application {conflict.ApplicationName} already exists";
                            reasons.Add($"ConfigMgr: application exists, policy {OneClickOnExisting.Normalize(options.OnExisting)}");
                        }
                        else if (result.ExitCode != 0)
                        {
                            StdErr?.Invoke(result.StdErr);
                            cell = $"failed: exit code {result.ExitCode}";
                            detail = result.StdErr.Trim();
                        }
                        else
                        {
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
                        if (present) reasons.Add($"WSUS: {latest} already on the server");
                        if (step.Refused)
                        {
                            notSupported++;
                            reasons.Add("WSUS: not supported");
                            UpdateAppState(p.Uid, s => OneClickHistory.SetNotSupported(s.NotSupported, OneClickDestinations.Wsus, latest, mapped.Cell, DateTimeOffset.Now));
                        }
                        break;
                    }
                    default:
                    {
                        var result = await execution.PackageAsync(p, options.Comment, stepLog, OnExistingBehavior.Skip, snapshot, PublishRunTargets.IntuneOnly, ct);
                        IntuneNotes?.Invoke(p, result);
                        if (result.ExitCode == 0)
                        {
                            ok = true;
                            id = IntuneAppId(result.IntunePublishNote);
                            cell = "published " + id;
                        }
                        else
                        {
                            cell = string.IsNullOrWhiteSpace(result.IntunePublishNote) ? $"failed: exit code {result.ExitCode}" : result.IntunePublishNote;
                            detail = result.StdErr.Trim();
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                StopCanceled(item, p, publishedTo);
                throw;
            }

            item.SetResult(destination, cell, id, detail);
            if (ok)
            {
                if (!present) published++;
                publishedTo.Add(destination);
                UpdateAppState(p.Uid, s => OneClickHistory.SetPublished(s.Published, s.NotSupported, destination, latest, id, DateTimeOffset.Now));
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

    /// <summary>Writes one application's stored check state, creating it when absent.</summary>
    private void UpdateAppState(string uid, Action<AppCheckState> change)
    {
        var s = settingsService.Load();
        if (!s.AppStates.TryGetValue(uid, out var state) || state is null)
            s.AppStates[uid] = state = new AppCheckState();
        state.Published ??= [];
        state.NotSupported ??= [];
        change(state);
        settingsService.Save(s);
    }
}
