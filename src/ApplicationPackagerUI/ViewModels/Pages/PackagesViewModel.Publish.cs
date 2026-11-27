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
    /// <summary>The grid status of a ConfigMgr or Intune run row.</summary>
    private static string RowStatus(string target, int exitCode) => PublishRunService.RowStatus(target, exitCode);

    /// <summary>Publish All: the review window first, then the run.</summary>
    private Task PublishAllAsync() => PackageAsync(null, null);

    /// <summary>
    /// A WSUS-bound action needs a server and the administration API. Checked on the UI
    /// thread before anything stages, so a missing API never fails every application
    /// after its stage completes.
    /// </summary>
    private bool ConfirmWsusPrerequisites(AppSettings settings, string action)
    {
        if (!settings.Wsus.IsConfigured)
        {
            AddLogLine($"{action} requires a WSUS server. Open Settings, WSUS, to configure it.");
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
    public void OpenWsusUpdates()
    {
        var settings = _settingsService.Load();
        if (!settings.Wsus.IsConfigured)
        {
            AddLogLine("WSUS Updates needs a WSUS server. Open Settings, WSUS, to configure it.");
            StatusText = "WSUS server required.";
            return;
        }
        var api = WsusApiProbe.Probe();
        if (!api.Available) AddLogLine($"WSUS Updates is unavailable: {api.Reason}");
        _publishDialogs.ShowWsusUpdates(settings.Wsus);
    }

    /// <summary>The folder of the One Click run reports.</summary>
    public static string OneClickReportFolder => OneClickReportWriter.Folder(LogDir);

    /// <summary>The checks of the One Click chain, run over the tracked rows before the plan opens.</summary>
    private sealed class OneClickChecks(PackagesViewModel vm) : IOneClickChecks
    {
        public async Task<bool> CheckLatestAsync(IReadOnlyList<PackagerInfo> rows) =>
            !(await vm.RunCheckLatestOnList(rows.ToList(), "One Click: checking latest versions...", "Latest check")).Cancelled;

        public Task<bool> CheckConfigMgrAsync(IReadOnlyList<PackagerInfo> rows) => vm.RunCheckMecmAsync(rows.ToList(), lookupCvesAfter: false);

        public async Task<bool> CheckCvesAsync(IReadOnlyList<PackagerInfo> rows)
        {
            await vm.RunCveCheckAsync(rows, force: false, "One Click: checking CVEs...");
            return true;
        }
    }

    /// <summary>
    /// Runs the whole chain, Check Latest, Check ConfigMgr and Check CVEs, over the tracked
    /// applications the grid shows, then opens the plan window (D-47).
    /// </summary>
    private async Task OneClickAsync()
    {
        var settings = _settingsService.Load();
        var action = OneClickActions.Normalize(settings.OneClick.Action);
        var tracked = new HashSet<string>(settings.OneClick.TrackedApplications, StringComparer.OrdinalIgnoreCase);
        if (tracked.Count == 0)
        {
            AddLogLine("No apps are tracked for One Click. Open Settings, One Click, to configure.");
            StatusText = "No apps tracked.";
            _publishDialogs.ShowMessage("One Click Not Configured",
                "No apps are tracked yet.\n\nOpen Settings, One Click, choose which applications to include, pick an action (Report only, Stage, or Stage and Publish) and the destinations, and save.");
            return;
        }
        if (action != OneClickActions.Report && string.IsNullOrWhiteSpace(settings.DownloadRoot))
        {
            AddLogLine($"Download Root is required for action '{action}'. Open Settings.");
            StatusText = "Download Root is required.";
            return;
        }
        var rows = Packagers.Where(p => p.IsRunnable && tracked.Contains(p.Uid)).ToList();
        if (rows.Count == 0)
        {
            AddLogLine("No tracked apps are visible in the grid.");
            StatusText = "No visible tracked apps.";
            return;
        }

        // The history before the checks: the cadence, the last known version and the last publish of the plan are read from it, because the checks record a new check time and version.
        var history = _inventoryProjection.History(rows, settings, _executionService.ResolveBuildPlan,
            p => _executionService.LocalStageVersion(p) is not null);
        if (!await OneClickPrechecks.RunAsync(rows, new OneClickChecks(this), AddLogLine))
        {
            AddLogLine("One Click: a check was cancelled, so no plan was made.");
            StatusText = "One Click cancelled.";
            return;
        }

        var consoleFound = ConfigMgrConsole.IsInstalled();
        var context = new OneClickPlanContext
        {
            Apps = rows.Select(p => (new OneClickApp(p.Uid, p.Application, p.Vendor, p.LatestVersion, p.CurrentVersion)
                { WsusNotSupported = p.WsusNotSupported, StageVersion = _executionService.LocalStageVersion(p) }, (PackagerInfo?)p)).ToList(),
            Settings = () => OneClickSettingsMap.PlanSettings(_settingsService.Load(), consoleFound),
            History = history,
            Action = action,
            Force = settings.OneClick.ForceOnLaunch,
            OnExisting = settings.OneClick.OnExisting,
            Comment = Comment.Trim(),
            SiteText = string.IsNullOrWhiteSpace(settings.SiteCode) ? "" : "ConfigMgr site " + settings.SiteCode.Trim(),
            PlannedSite = SiteKeyOf(settings),
            CurrentSite = () => SiteKeyOf(_settingsService.Load()),
            SystemsText = SystemsLine(settings.Systems),
            ReportFolder = OneClickReportFolder,
            ConfirmLocalSources = selected => ConfirmLocalSourceFolders(selected.ToList()),
            Log = AddLogLine
        };
        AddLogLine($"One Click: the checks are done, the plan opens for {rows.Count} application(s).");
        _publishDialogs.ShowOneClickPlan(new OneClickPlanViewModel(context, this));
        return;
    }

    /// <summary>"In use: ConfigMgr. Not in use: Intune, WSUS." from Settings, Systems in Use.</summary>
    public static string SystemsLine(SystemsInUse? systems)
    {
        (string Name, bool On)[] all = [("ConfigMgr", systems?.ConfigMgr ?? false), ("Intune", systems?.Intune ?? false), ("WSUS", systems?.Wsus ?? false)];
        var on = string.Join(", ", all.Where(s => s.On).Select(s => s.Name));
        var off = string.Join(", ", all.Where(s => !s.On).Select(s => s.Name));
        return "In use: " + (on.Length > 0 ? on : "none") + "." + (off.Length > 0 ? " Not in use: " + off + "." : "");
    }

    /// <summary>
    /// The One Click run over the rows the plan window includes. Each row is checked,
    /// decided against the per-destination history, staged with one run snapshot, and
    /// published to each remaining destination from that same snapshot, ConfigMgr first.
    /// An existing ConfigMgr application follows the saved policy and prompts nothing. With supersedence on for an application, the
    /// confirmation window (Yes, Skip, No, Remember) can open for it; the window belongs to this attended run.
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
        _executionService.SupersedenceConfirmation = new ApplicationPackagerEngine.Services.Supersedence.SupersedenceConfirmer(
            Views.Windows.SupersedenceConfirmDialog.AskAsync, () => _operationCts?.Cancel());
        var canceled = false;
        var done = 0;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                done = i;
                if (token.IsCancellationRequested) { canceled = true; break; }
                ProgressCurrent = i + 1;
                var item = items[i];
                StatusText = ApplicationPackagerEngine.Models.RunProgress.Running("One Click", i + 1, items.Count, item.Application);
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
            if (!canceled && token.IsCancellationRequested) { canceled = true; done = items.Count; }
            StatusText = canceled ? ApplicationPackagerEngine.Models.RunProgress.Cancelled("One Click", done, items.Count, "product", "done") : $"One Click: {items.Count} of {items.Count} {(items.Count == 1 ? "product" : "products")} done.";
            if (canceled) AddLogLine(StatusText);
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
