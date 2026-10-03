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
    private static string RowStatus(string target, int exitCode) =>
        PublishRowOutcome.For(target, exitCode, intune: exitCode == 0 ? IntuneRunOutcome.Published("", "") : null).Status;

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
    /// row at "WSUS: not supported" with its code; the log carries the full text.
    /// </summary>
    private async Task PublishWsusRunAsync(IReadOnlyList<PackagerInfo>? rows = null, RunSnapshot? workbenchSnapshot = null)
    {
        var settings = _settingsService.Load();
        const string label = "Publish to WSUS";
        if (PublishActionState.RunGate(PublishRunTargets.WsusOnly, ReadinessOf(settings)) is { } blocked)
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
                    PackagerExecutionResult stage;
                    if (workbenchSnapshot is not null)
                    {
                        // A Workbench publish consumes the sealed build its snapshot names.
                        stage = new PackagerExecutionResult { ExitCode = 0 };
                    }
                    else if (_executionService.ExistingStageManifest(p) is { } existing)
                    {
                        AddLogLine($"  Stage manifest found: {existing}");
                        stage = new PackagerExecutionResult { ExitCode = 0, OutLogPath = existing };
                    }
                    else
                    {
                        p.Status = "Staging...";
                        stage = await _executionService.StageAsync(p, line => AddLogLine($"  {line}"), _operationCts.Token, null, workbenchSnapshot);
                        if (stage.ExitCode != 0)
                        {
                            if (tally.RecordInterruptedOrFailed(_operationCts.Token)) { p.Status = "Cancelled"; break; }
                            p.Status = "Publish error";
                            AddLogLine($"Stage FAILED (exit {stage.ExitCode})");
                            LogStdErr(stage.StdErr);
                            continue;
                        }
                    }

                    p.Status = "Publishing to WSUS...";
                    var step = await _executionService.PublishWsusAsync(p, stage, workbenchSnapshot, _wsusPublisher, line => AddLogLine($"  {line}"), _operationCts.Token);
                    var outcome = PublishRowOutcome.For(PublishRunTargets.WsusOnly, 0, wsus: step);
                    p.Status = outcome.Status;
                    AddLogLine("WSUS publish: " + step.Message);
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

    // ── One Click ────────────────────────────────────────────────

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
            Apps = rows.Select(p => (new OneClickApp(p.Uid, p.Application, p.Vendor, p.LatestVersion, p.CurrentVersion), (PackagerInfo?)p)).ToList(),
            Settings = () => OneClickSettingsMap.PlanSettings(_settingsService.Load(), consoleFound),
            History = OneClickSettingsMap.History(settings),
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
            SaveAppStates();
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

    private async Task RunOneClickRowAsync(OneClickPlanItem item, OneClickRunOptions options, CancellationToken ct)
    {
        var p = item.Row;
        if (p is null) { FinishOneClickRow(item, OneClickOutcomes.Skipped, "not in the grid"); return; }
        var action = OneClickActionNames.Normalize(options.Action);
        var log = (string line) => AddLogLine($"  {line}");
        var settings = _settingsService.Load();
        var entry = settings.AppStates.TryGetValue(p.Uid, out var state) && state is not null ? OneClickSettingsMap.Entry(state) : null;
        var lastKnown = entry?.LastKnownVersion ?? "";

        // 1. Check latest
        item.Step = "checking " + p.Application;
        p.Status = "Checking latest...";
        AddLogLine($"One Click: {p.Application} ({p.Script})");
        string? latest;
        try { latest = await _executionService.GetLatestVersionAsync(p, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            latest = null;
            AddLogLine($"Latest check failed: {ex.Message}");
        }
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(latest))
        {
            p.Status = "Check error";
            FinishOneClickRow(item, OneClickOutcomes.Failed, "check failed");
            return;
        }
        p.LatestVersion = latest;
        p.LastChecked = DateTime.Now;
        item.Version = latest;
        AddLogLine("Latest: " + latest);
        SaveAppStates();
        var versionChanged = lastKnown.Length == 0 || !string.Equals(lastKnown, latest, StringComparison.OrdinalIgnoreCase);

        if (action == OneClickActionNames.Report)
        {
            p.Status = versionChanged ? "Update available" : "Up to date";
            FinishOneClickRow(item, OneClickOutcomes.Checked, versionChanged ? "update available" : "up to date");
            return;
        }

        // 2. Which destinations still need this version, and whether the build is stale.
        var plan = _executionService.ResolveBuildPlan(p);
        var buildStale = plan is not null && BuildRecords.IsCurrent(plan.DataRoot, plan.ApplicationId, plan.ProfileId, latest,
            plan.ProfileRevision, settings.ScriptSigning.PolicyDigest) == false;
        var wanted = item.Destinations.Names;
        var decision = OneClickPlanner.Decide(action, wanted, entry, latest, p.CurrentVersion, options.Force, versionChanged, buildStale,
            staged: entry?.LastStaged is not null);
        foreach (var (destination, cell) in decision.GuardResults) item.SetResult(destination, cell);
        if (!decision.Stage)
        {
            p.Status = "Up to date";
            AddLogLine($"No change - skipping: {p.Application}");
            FinishOneClickRow(item, OneClickOutcomes.Skipped, decision.SkipReason);
            return;
        }
        if (buildStale && !versionChanged && decision.PublishTo.Count == 0)
            AddLogLine($"One Click: {p.Application} {latest} restages: the last build differs in version, profile revision or signing policy. Its destinations carry this version, so it is not published.");

        // 3. One run snapshot for the stage and every publish of this row.
        RunSnapshot? snapshot = null;
        if (plan is null)
        {
            p.Status = "Stage error";
            AddLogLine($"Run snapshot not created for {p.Application}: the workbench could not resolve its application or profile.");
            FinishOneClickRow(item, OneClickOutcomes.Failed, "run snapshot not created");
            return;
        }
        snapshot = _executionService.CreateRowSnapshot(p, plan, OneClickTarget(decision.PublishTo), out var refusal);
        if (snapshot is null)
        {
            p.Status = "Stage error";
            AddLogLine($"Run snapshot refused for {p.Application}: {refusal}");
            FinishOneClickRow(item, OneClickOutcomes.Failed, "run snapshot refused: " + refusal);
            return;
        }
        AddLogLine($"  Workbench profile {snapshot.ProfileId} (revision {snapshot.ProfileRevision}, build {snapshot.BuildId})");

        // 4. Stage, with the saved install parameters and no review dialog.
        item.Step = "staging " + p.Application;
        p.Status = "Staging...";
        AddLogLine("Stage: " + p.Application);
        var stage = await _executionService.StageAsync(p, log, ct, null, snapshot);
        ct.ThrowIfCancellationRequested();
        if (stage.ExitCode != 0)
        {
            p.Status = "Stage error";
            AddLogLine($"Stage FAILED (exit {stage.ExitCode})");
            LogStdErr(stage.StdErr);
            FinishOneClickRow(item, OneClickOutcomes.Failed, "stage failed");
            return;
        }
        p.Status = "Staged";
        UpdateAppState(p.Uid, s => s.LastStaged = DateTime.Now);

        if (action == OneClickActionNames.Stage || decision.PublishTo.Count == 0)
        {
            FinishOneClickRow(item, OneClickOutcomes.Staged, string.Join("; ", decision.SkipReasons));
            return;
        }

        // 5. Publish per destination, ConfigMgr first: it is the only one that reads the network copy.
        var reasons = decision.SkipReasons.ToList();
        int published = 0, failed = 0, notSupported = 0;
        foreach (var destination in decision.PublishTo)
        {
            item.Step = $"publishing {p.Application} to {destination}";
            p.Status = $"Publishing to {destination}...";
            string cell, id = "", detail = "";
            var ok = false;
            var present = false;
            switch (destination)
            {
                case OneClickDestinations.ConfigMgr:
                {
                    var onExisting = OneClickOnExisting.Normalize(options.OnExisting) == OneClickOnExisting.Overwrite
                        ? OnExistingBehavior.Overwrite : OnExistingBehavior.Skip;
                    var result = await _executionService.PackageAsync(p, options.Comment, log, ct, onExisting, snapshot, PublishRunTargets.ConfigMgr);
                    id = ApplicationName(result.StdOut);
                    if (result.Conflict is { } conflict)
                    {
                        cell = $"skipped: application {conflict.ApplicationName} already exists";
                        reasons.Add($"ConfigMgr: application exists, policy {OneClickOnExisting.Normalize(options.OnExisting)}");
                    }
                    else if (result.ExitCode != 0)
                    {
                        LogStdErr(result.StdErr);
                        cell = $"failed: exit code {result.ExitCode}";
                        detail = result.StdErr.Trim();
                    }
                    else
                    {
                        ok = true;
                        cell = "published " + id;
                    }
                    LogIntuneNotes(p, result);
                    break;
                }
                case OneClickDestinations.Wsus:
                {
                    var step = await _executionService.PublishWsusAsync(p, stage, snapshot, _wsusPublisher, log, ct);
                    AddLogLine("WSUS publish: " + step.Message);
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
                    var result = await _executionService.PackageAsync(p, options.Comment, log, ct, OnExistingBehavior.Skip, snapshot, PublishRunTargets.IntuneOnly);
                    LogIntuneNotes(p, result);
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
            ct.ThrowIfCancellationRequested();

            item.SetResult(destination, cell, id, detail);
            if (ok)
            {
                if (!present) published++;
                UpdateAppState(p.Uid, s => OneClickHistory.SetPublished(s.Published, s.NotSupported, destination, latest, id, DateTimeOffset.Now));
            }
            else if (!OneClickPlanner.IsSkipOrRefusal(cell))
            {
                failed++;
            }
        }

        var (outcome, status) = OneClickPlanner.Conclude(published, failed, notSupported);
        p.Status = status;
        FinishOneClickRow(item, outcome, string.Join("; ", reasons));
    }

    /// <summary>The run target the snapshot records for the destinations a row publishes to.</summary>
    private static string OneClickTarget(IReadOnlyList<string> publishTo) =>
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
        var s = _settingsService.Load();
        if (!s.AppStates.TryGetValue(uid, out var state) || state is null)
            s.AppStates[uid] = state = new AppCheckState();
        state.Published ??= [];
        state.NotSupported ??= [];
        change(state);
        _settingsService.Save(s);
    }
}
