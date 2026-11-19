using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Services.Vulnerability;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class PackagesViewModel
{
    private IAsyncRelayCommand? _checkCvesCommand;
    private IAsyncRelayCommand? _cveReportCommand;
    private CveRowChecker? _cveChecker;

    public IAsyncRelayCommand CheckCvesCommand => _checkCvesCommand ??= new AsyncRelayCommand(CheckCvesAsync, CanRunOperation);
    public IAsyncRelayCommand CveReportCommand => _cveReportCommand ??= new AsyncRelayCommand(CveReportAsync, CanRunOperation);

    private CveRowChecker CveChecker => _cveChecker ??= new CveRowChecker(
        new BackgroundVulnerabilityLookup(new VulnerabilityLookupService(log: AddLogLineFromAnyThread)), _inventory);

    private void NotifyCveCommands()
    {
        _checkCvesCommand?.NotifyCanExecuteChanged();
        _cveReportCommand?.NotifyCanExecuteChanged();
    }

    /// <summary>The lookup service logs from the thread its request completes on.</summary>
    private void AddLogLineFromAnyThread(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) AddLogLine(message);
        else dispatcher.BeginInvoke(() => AddLogLine(message));
    }

    private static InventoryEnvironmentIdentity CveEnvironment(AppSettings settings) =>
        InventoryEnvironmentIdentity.ConfigMgr(settings.SiteCode, settings.ProviderMachine);

    /// <summary>The CVE cells and the compare result of a row from the versions and the summary stored for this site.</summary>
    private void ApplyStoredCveState(PackagerInfo row, AppSettings settings)
    {
        VulnerabilitySummary? summary = null;
        if (row.Cpe.Length > 0 && row.CurrentVersion.Length > 0 && row.LatestVersion.Length > 0)
        {
            try { summary = _inventory.GetVulnerabilitySummary(row.Uid, CveEnvironment(settings), row.Cpe); }
            catch (Exception ex) when (ex is System.Data.Common.DbException or System.IO.IOException) { }
        }
        CveRowState.Preload(row, summary);
    }

    private async Task CheckCvesAsync()
    {
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Check CVEs"); return; }
        await RunCveCheckAsync(selected, force: false, "Checking CVEs for selected applications...");
    }

    /// <summary>
    /// Looks up the CVEs each row's update fixes. Returns the run context with the result
    /// of each row that reached a lookup, or null when another operation is running.
    /// </summary>
    public async Task<CveRunContext?> RunCveCheckAsync(IReadOnlyList<PackagerInfo> rows, bool force, string? startMessage = null)
    {
        if (IsOperationRunning) return null;
        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = rows.Count;
        _operationCts = new CancellationTokenSource();
        StatusText = startMessage ?? "Checking CVEs...";
        var settings = _settingsService.Load();
        var context = new CveRunContext
        {
            Environment = CveEnvironment(settings),
            ApiKey = NvdApiKeyStore.Resolve(settings),
            CacheTtlMinutes = NvdApiKeyStore.CacheTtlMinutes(settings),
            Force = force
        };
        var tally = new PackagingRunTally();
        // While the pacer waits for the NVD request limit the status line says so and counts
        // the lookups left. The wait runs on a pool thread; the text goes through the dispatcher.
        var stepText = StatusText;
        var lookupsLeft = rows.Count;
        var waiting = true;
        var pacer = NvdRequestPacer.Shared;
        pacer.WaitCallback = seconds =>
        {
            var text = seconds > 0 ? NvdLimits.WaitStatusText(Volatile.Read(ref lookupsLeft), seconds) : Volatile.Read(ref stepText);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) StatusText = text;
            // A text queued before the run ended must not replace the final status.
            else dispatcher.BeginInvoke(() => { if (Volatile.Read(ref waiting)) StatusText = text; });
        };

        try
        {
            for (var i = 0; i < rows.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }
                var row = rows[i];
                ProgressCurrent = i + 1;
                Volatile.Write(ref lookupsLeft, rows.Count - i);
                StatusText = $"CVEs: {ProgressCurrent} of {ProgressTotal} — {row.Application}";
                Volatile.Write(ref stepText, StatusText);
                AddLogLine($"CVEs: {row.Application} ({row.Script})");
                try
                {
                    switch (await CveChecker.CheckAsync(row, context, line => AddLogLine($"  {line}"), _operationCts.Token))
                    {
                        case CveRowOutcome.Checked: tally.RecordSuccess(); break;
                        case CveRowOutcome.Skipped: tally.RecordSkip(); break;
                        default: tally.RecordFailure(); break;
                    }
                }
                catch (OperationCanceledException)
                {
                    tally.RecordInterruptedOrFailed(_operationCts.Token);
                    break;
                }
            }

            Volatile.Write(ref waiting, false);
            tally.Finish(_operationCts.Token);
            StatusText = RunEndLine(tally, "CVE check", ProgressTotal, "checked", "Check CVEs");
            // Some rows may be left unchecked (NVD limit or timeout): one line says so.
            var notice = NvdLimits.BatchNotice(tally.Succeeded + tally.Failed, tally.Failed);
            if (notice.Length > 0)
            {
                AddLogLine(notice);
                if (!tally.Cancelled) StatusText = notice;
            }
            return context;
        }
        finally
        {
            Volatile.Write(ref waiting, false);
            pacer.WaitCallback = null;
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>The cached lookup for the row's CPE and version pair, or null.</summary>
    public static VulnerabilityResult? CachedCveResult(PackagerInfo row) =>
        VulnerabilityCache.Find(row.Cpe, row.CurrentVersion, row.LatestVersion);

    private async Task CveReportAsync()
    {
        var rows = SelectedPackagers.ToList();
        if (rows.Count == 0) rows = Packagers.ToList();
        if (rows.Count == 0) { AddLogLine("No rows to report."); return; }
        var paths = await WriteCveReportAsync(rows);
        if (paths is not null) StatusText = $"CVE report written for {rows.Count} application(s).";
    }

    /// <summary>Writes every report format for the rows and opens the HTML page. Null when the write failed.</summary>
    public async Task<VulnerabilityReportPaths?> WriteCveReportAsync(IReadOnlyList<PackagerInfo> rows)
    {
        StatusText = "Writing the CVE report...";
        try
        {
            var sources = rows.Select(CveRowState.ReportSource).ToList();
            var folder = VulnerabilityPaths.ReportFolder;
            var paths = await Task.Run(() =>
                VulnerabilityReportWriter.Export(VulnerabilityReportWriter.BuildRows(sources), folder));
            foreach (var path in paths.All()) AddLogLine($"CVE report: {path}");
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.Html) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                AddLogLine($"CVE report not opened: {ex.Message}");
            }
            return paths;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            AddLogLine($"CVE report failed: {ex.Message}");
            StatusText = "CVE report failed.";
            return null;
        }
    }

    /// <summary>The site-independent links of a row: the six CVE sites and the vendor advisory page.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> CveSourceLinks(PackagerInfo row) =>
        VulnerabilityLinks.Product(row.Cpe, row.CurrentVersion, row.AdvisoryUrl).ToList();

    /// <summary>Writes one line for a link that was refused or failed.</summary>
    public void OpenExternalLink(string? url) => ExternalLinks.OpenHttps(url, AddLogLine);
}
