using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The publish run per destination (ConfigMgr, Intune, WSUS), the WSUS Updates
/// entry point and the One Click plan and run.
/// </summary>
public partial class PackagesViewModel : IOneClickRunner, IPublishActionHost
{
    public static string PublishLabel(string target) => target switch
    {
        PublishRunTargets.IntuneOnly => "Publish to Intune",
        PublishRunTargets.WsusOnly => "Publish to WSUS",
        _ => "Publish to ConfigMgr"
    };

    /// <summary>The grid status of a ConfigMgr or Intune run row.</summary>
    private static string RowStatus(string target, int exitCode) => PublishRunService.RowStatus(target, exitCode);

    /// <summary>
    /// "Apply supersedence": the lookup and the supersedence step for each checked application, with its stage and without a new publish.
    /// Needs a ConfigMgr site; it is available where the ConfigMgr publish is.
    /// </summary>
    private async Task ApplySupersedenceAsync()
    {
        if (IsOperationRunning) { AddLogLine("Apply supersedence skipped: another operation is running."); return; }
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Apply supersedence"); return; }
        if (PublishRunService.RunGate(PublishRunTargets.ConfigMgr, _settingsService.Load()) is { } blocked)
        {
            AddLogLine($"Apply supersedence blocked: {blocked}");
            return;
        }
        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = selected.Count;
        _operationCts = new CancellationTokenSource();
        try
        {
            foreach (var p in selected)
            {
                if (_operationCts.IsCancellationRequested) break;
                ProgressCurrent++;
                StatusText = $"Apply supersedence: {ProgressCurrent} of {ProgressTotal} — {p.Application}";
                AddLogLine($"Apply supersedence: {p.Application}");
                try
                {
                    var result = await _executionService.ApplySupersedenceAsync(p, null, line => AddLogLine($"  {line}"), _operationCts.Token);
                    AddLogLine($"  {(result.Ok ? "Supersedence" : "Supersedence not applied")}: {result.Message}");
                    p.Status = !result.Ok ? "Supersedence not applied"
                        : result.Status == ApplicationPackagerEngine.Services.Supersedence.SupersedenceStepStatus.Applied ? "Supersedence applied" : "No older version";
                    p.StatusDetail = result.Ok ? null : result.Message;
                }
                catch (OperationCanceledException) when (_operationCts.IsCancellationRequested)
                {
                    AddLogLine("  Cancelled.");
                    break;
                }
                catch (Exception ex)
                {
                    AddLogLine($"  Supersedence not applied: {ex.Message}");
                    p.Status = "Supersedence not applied";
                    p.StatusDetail = ex.Message;
                }
            }
        }
        finally
        {
            ShowProgress = false;
            StatusText = "Apply supersedence finished.";
            _operationCts?.Dispose();
            _operationCts = null;
            IsOperationRunning = false;
        }
    }

    /// <summary>
    /// "Rewrite old versions": the plan of the clauses that the rewrite would change for the checked application, in the change plan
    /// window with its confirmation and Undo (Restore). When nothing is left to rewrite and an earlier rewrite is pending in the log,
    /// the window offers that Restore. Works on one application at a time.
    /// </summary>
    private async Task RewriteOldVersionsAsync()
    {
        if (IsOperationRunning) { AddLogLine("Rewrite old versions skipped: another operation is running."); return; }
        var selected = SelectedPackagers.ToList();
        if (selected.Count != 1) { AddLogLine("Rewrite old versions works on one checked application at a time."); StatusText = "Check one application."; return; }
        if (PublishRunService.RunGate(PublishRunTargets.ConfigMgr, _settingsService.Load()) is { } blocked)
        {
            AddLogLine($"Rewrite old versions blocked: {blocked}");
            return;
        }
        var p = selected[0];
        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = 1;
        _operationCts = new CancellationTokenSource();
        try
        {
            var (scope, error) = _executionService.RewriteScopeFor(p, null);
            if (scope is null) { AddLogLine($"Rewrite old versions: {error}"); StatusText = "Rewrite old versions not possible."; return; }
            StatusText = $"Reading the line of {p.Application} from the site...";
            AddLogLine($"Rewrite old versions: reading the line of {p.Application}");
            var plan = await new ApplicationPackagerEngine.Services.Supersedence.OldVersionRewriteRunner().PlanAsync(scope, _operationCts.Token);
            var restorable = RewriteOldVersionsPlans.Pending().Count;
            foreach (var item in plan.Items.Where(i => !i.Rewrites)) AddLogLine($"  left: {item.Application} / {item.DeploymentType}: {item.Skip}");
            foreach (var item in plan.Blocked) AddLogLine($"  blocked: {item}");
            ApplicationPackagerUI.Services.ChangePlanOutcome outcome;
            if (RewriteOldVersionsPlans.ForRewrite(plan, scope, restorable) is { } rewrite)
                outcome = _publishDialogs.ShowChangePlan(rewrite.Plan, rewrite.Run, rewrite.Undo);
            else if (RewriteOldVersionsPlans.ForRestore(scope) is { } restore)
                outcome = _publishDialogs.ShowChangePlan(restore.Plan, restore.Run);
            else
            {
                AddLogLine("Rewrite old versions: nothing to rewrite and nothing to restore.");
                StatusText = "Nothing to rewrite.";
                _publishDialogs.ShowMessage("Rewrite old versions", "No old version of this line has a clause that the rewrite can change, and no earlier rewrite is pending. The log lists each clause and why it is left.");
                return;
            }
            if (outcome.Result is { } result) AddLogLine($"Rewrite old versions: {result.Summary}");
            if (outcome.UndoResult is { } undo) AddLogLine($"Restore: {undo.Summary}");
            if (!outcome.Confirmed) AddLogLine("Rewrite old versions: not confirmed; nothing was changed.");
            StatusText = !outcome.Confirmed ? "Rewrite old versions not confirmed."
                : outcome.Result?.Stop == ChangePlanStop.Completed ? "Rewrite old versions finished." : "Rewrite old versions stopped; see the log.";
        }
        catch (OperationCanceledException)
        {
            AddLogLine("Rewrite old versions: cancelled while reading the site. Nothing was changed.");
            StatusText = "Rewrite old versions cancelled.";
        }
        catch (Exception ex)
        {
            AddLogLine($"Rewrite old versions failed: {ex.Message}");
            StatusText = "Rewrite old versions failed.";
        }
        finally
        {
            ShowProgress = false;
            _operationCts?.Dispose();
            _operationCts = null;
            IsOperationRunning = false;
        }
    }

    /// <summary>One publish run for the checked rows with the button's own destination; the saved deployment target plays no part.</summary>
    private Task StartPublishRunAsync(string target) =>
        target == PublishRunTargets.WsusOnly ? PublishWsusRunAsync() : PackageAsync(null, target, null);

    /// <summary>
    /// A WSUS-bound action needs a server and the administration API. Checked on the UI
    /// thread before anything stages, so a missing API never fails every application
    /// after its stage completes.
    /// </summary>
    private bool ConfirmWsusPrerequisites(AppSettings settings, string action)
    {
        if (!settings.Wsus.IsConfigured)
        {
            AddLogLine($"{action} requires a WSUS server. Open Settings, WSUS Publishing, to configure it.");
            StatusText = "WSUS server required.";
            return false;
        }
        var api = WsusApiProbe.Probe();
        if (api.Available) return true;
        AddLogLine($"WSUS publishing is unavailable: {api.Reason}");
        StatusText = "WSUS administration API not installed.";
        _publishDialogs.ShowMessage("WSUS API Required", api.Reason);
        return false;
    }

    /// <summary>
    /// Opens WSUS Updates for the configured server. The window opens without the API as
    /// well: its listing then shows the adapter's API-not-installed text, and Catalog
    /// Import shows it per id.
    /// </summary>
    private void OpenWsusUpdates()
    {
        var settings = _settingsService.Load();
        if (!settings.Wsus.IsConfigured)
        {
            AddLogLine("WSUS Updates needs a WSUS server. Open Settings, WSUS Publishing, to configure it.");
            StatusText = "WSUS server required.";
            return;
        }
        var api = WsusApiProbe.Probe();
        if (!api.Available) AddLogLine($"WSUS Updates is unavailable: {api.Reason}");
        _publishDialogs.ShowWsusUpdates(settings.Wsus);
    }

    /// <summary>
    /// Publish to WSUS: each checked row is staged when it has no stage of its latest
    /// version, then published through the WSUS publish service. A refusal leaves the
    /// row at "not supported: &lt;codes&gt;"; the log and the status tooltip carry the full text.
    /// </summary>
    private async Task PublishWsusRunAsync(IReadOnlyList<PackagerInfo>? rows = null, RunSnapshot? workbenchSnapshot = null)
    {
        var settings = _settingsService.Load();
        const string label = "Publish to WSUS";
        if (PublishRunService.RunGate(PublishRunTargets.WsusOnly, settings) is { } blocked)
        {
            AddLogLine($"{label} blocked: {blocked}");
            StatusText = "WSUS server required.";
            return;
        }
        if (!ConfirmWsusPrerequisites(settings, "Publish")) return;

        var selected = rows?.ToList() ?? SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected(label); return; }
        selected = ConfirmLocalSourceFolders(selected);
        if (selected.Count == 0) return;
        foreach (var sentence in ApplicationPackagerEngine.Services.PublishPreflight.Lines(DeploymentTargets.WsusOnly))
            AddLogLine("Before publish: " + sentence);

        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = selected.Count;
        _operationCts = new CancellationTokenSource();
        StatusText = "Publishing selected applications to WSUS...";
        var tally = new PackagingRunTally();

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }
                var p = selected[i];
                ProgressCurrent = i + 1;
                StatusText = $"Publishing to WSUS: {ProgressCurrent} of {ProgressTotal} — {p.Application}";
                AddLogLine($"Publish to WSUS: {p.Application} ({p.Script})");

                try
                {
                    var run = await _publishRuns.PublishWsusRowAsync(p, workbenchSnapshot, AddLogLine, _operationCts.Token);
                    if (run.StageFailed)
                    {
                        if (tally.RecordInterruptedOrFailed(_operationCts.Token)) { p.Status = "Cancelled"; break; }
                        p.Status = "Publish error";
                        AddLogLine($"Stage FAILED (exit {run.Stage.ExitCode})");
                        LogStdErr(run.Stage.StdErr);
                        continue;
                    }

                    var outcome = run.Outcome!;
                    if (outcome.Kind == PublishRowOutcomeKind.Success) { tally.RecordSuccess(); p.IsSelected = false; }
                    else if (outcome.Kind == PublishRowOutcomeKind.NotSupported) tally.RecordSkip();
                    else tally.RecordFailure();
                }
                catch (OperationCanceledException ex)
                {
                    if (tally.RecordInterruptedOrFailed(_operationCts.Token)) { p.Status = "Cancelled"; break; }
                    p.Status = "Publish error";
                    AddLogLine($"ERROR: {ex.Message}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or ArgumentException or System.Text.Json.JsonException)
                {
                    p.Status = "Publish error";
                    tally.RecordFailure();
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            tally.Finish(_operationCts.Token);
            StatusText = tally.Summarize("Publish to WSUS", ProgressTotal, "published");
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>The folder of the One Click run reports.</summary>
    public static string OneClickReportFolder => OneClickReportWriter.Folder(LogDir);

    /// <summary>Opens the plan window for the tracked applications the grid shows.</summary>
    private Task OneClickAsync()
    {
        var settings = _settingsService.Load();
        var action = OneClickActions.Normalize(settings.OneClick.Action);
        var tracked = new HashSet<string>(settings.OneClick.TrackedApplications, StringComparer.OrdinalIgnoreCase);
        if (tracked.Count == 0)
        {
            AddLogLine("No apps are tracked for One Click. Open Settings, One Click Settings, to configure.");
            StatusText = "No apps tracked.";
            _publishDialogs.ShowMessage("One Click Not Configured",
                "No apps are tracked yet.\n\nOpen Settings, One Click Settings, choose which applications to include, pick an action (Report only, Stage, or Stage and Publish) and the destinations, and save.");
            return Task.CompletedTask;
        }
        if (action != OneClickActions.Report && string.IsNullOrWhiteSpace(settings.DownloadRoot))
        {
            AddLogLine($"Download Root is required for action '{action}'. Open Settings.");
            StatusText = "Download Root is required.";
            return Task.CompletedTask;
        }
        var rows = Packagers.Where(p => p.IsRunnable && tracked.Contains(p.Uid)).ToList();
        if (rows.Count == 0)
        {
            AddLogLine("No tracked apps are visible in the grid.");
            StatusText = "No visible tracked apps.";
            return Task.CompletedTask;
        }

        var consoleFound = ConfigMgrConsole.IsInstalled();
        var context = new OneClickPlanContext
        {
            Apps = rows.Select(p => (new OneClickApp(p.Uid, p.Application, p.Vendor, p.LatestVersion, p.CurrentVersion)
                { WsusNotSupported = p.WsusNotSupported }, (PackagerInfo?)p)).ToList(),
            Settings = () => OneClickSettingsMap.PlanSettings(_settingsService.Load(), consoleFound),
            History = _inventoryProjection.History(rows, settings, _executionService.ResolveBuildPlan),
            Action = action,
            Force = settings.OneClick.ForceOnLaunch,
            OnExisting = settings.OneClick.OnExisting,
            Comment = Comment.Trim(),
            ReportFolder = OneClickReportFolder,
            ConfirmLocalSources = selected => ConfirmLocalSourceFolders(selected.ToList()),
            Log = AddLogLine
        };
        _publishDialogs.ShowOneClickPlan(new OneClickPlanViewModel(context, this));
        return Task.CompletedTask;
    }

    /// <summary>
    /// The One Click run over the rows the plan window includes. Each row is checked,
    /// decided against the per-destination history, staged with one run snapshot, and
    /// published to each remaining destination from that same snapshot, ConfigMgr first.
    /// Nothing prompts: an existing ConfigMgr application follows the saved policy.
    /// </summary>
    async Task<bool> IOneClickRunner.RunAsync(IReadOnlyList<OneClickPlanItem> items, OneClickRunOptions options, CancellationToken ct)
    {
        if (IsOperationRunning)
        {
            AddLogLine("One Click skipped: another operation is running.");
            foreach (var item in items) { item.Outcome = OneClickOutcomes.Skipped; item.Reason = "another operation was running"; item.Step = item.Outcome; }
            return false;
        }

        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = items.Count;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _operationCts = linked;
        var token = linked.Token;
        var canceled = false;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (token.IsCancellationRequested) { canceled = true; break; }
                ProgressCurrent = i + 1;
                var item = items[i];
                StatusText = $"One Click {i + 1}/{items.Count}: {item.Application}";
                try
                {
                    await RunOneClickRowAsync(item, options, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    canceled = true;
                    if (item.Outcome.Length == 0) FinishOneClickRow(item, OneClickOutcomes.Skipped, "run canceled before any publish");
                    item.Step = "Canceled";
                    if (item.Row is not null) item.Row.Status = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    // One application's unexpected failure ends that row, not the run.
                    AddLogLine($"One Click: {item.Application} failed: {ex.Message}");
                    if (item.Row is not null) item.Row.Status = "Publish error";
                    FinishOneClickRow(item, OneClickOutcomes.Failed, "failed: " + ex.Message);
                }
            }
            StatusText = canceled ? "One Click: canceled." : "One Click: run complete.";
            return canceled;
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts = null;
        }
    }

    private void FinishOneClickRow(OneClickPlanItem item, string outcome, string reason)
    {
        item.Outcome = outcome;
        if (reason.Length > 0) item.Reason = reason;
        item.Step = outcome;
    }

    private Task RunOneClickRowAsync(OneClickPlanItem item, OneClickRunOptions options, CancellationToken ct) =>
        new OneClickRowRunner(new OneClickExecution(_executionService, _wsusPublisher), _settingsService,
            _inventory, _inventoryProjection, AddLogLine)
        {
            IntuneNotes = LogIntuneNotes,
            StdErr = LogStdErr
        }.RunAsync(item, options, ct);
}
