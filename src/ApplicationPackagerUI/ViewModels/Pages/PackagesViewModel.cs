using System.IO;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class PackagesViewModel : ObservableObject
{
    private readonly PackagerDiscoveryService _discoveryService;
    private readonly PackagerExecutionService _executionService;
    private readonly MecmQueryService _mecmQueryService;
    private readonly SettingsService _settingsService;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;

    [ObservableProperty] private ObservableCollection<PackagerInfo> _packagers = [];
    [ObservableProperty] private ObservableCollection<PackagerInfo> _filteredPackagers = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _comment = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty] private bool _showDebugColumns;
    [ObservableProperty] private bool _isAllSelected;
    [ObservableProperty] private int _progressCurrent;
    [ObservableProperty] private int _progressTotal;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private string _mecmConnectionStatus = "Unknown";

    public IAsyncRelayCommand LoadPackagersCommand { get; }
    public IAsyncRelayCommand CheckLatestCommand { get; }
    public IAsyncRelayCommand CheckMecmCommand { get; }
    public IAsyncRelayCommand StageCommand { get; }
    public IAsyncRelayCommand PackageCommand { get; }

    private CancellationTokenSource? _operationCts;

    public PackagesViewModel(
        PackagerDiscoveryService discoveryService,
        PackagerExecutionService executionService,
        MecmQueryService mecmQueryService,
        SettingsService settingsService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService)
    {
        _discoveryService = discoveryService;
        _executionService = executionService;
        _mecmQueryService = mecmQueryService;
        _settingsService = settingsService;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;

        LoadPackagersCommand = new AsyncRelayCommand(LoadPackagersAsync);
        CheckLatestCommand = new AsyncRelayCommand(CheckLatestAsync, CanRunOperation);
        CheckMecmCommand = new AsyncRelayCommand(CheckMecmAsync, CanRunOperation);
        StageCommand = new AsyncRelayCommand(StageAsync, CanRunOperation);
        PackageCommand = new AsyncRelayCommand(PackageAsync, CanRunOperation);

        _ = LoadPackagersCommand.ExecuteAsync(null);
    }

    private bool CanRunOperation() => !IsOperationRunning;

    partial void OnIsOperationRunningChanged(bool value)
    {
        CheckLatestCommand.NotifyCanExecuteChanged();
        CheckMecmCommand.NotifyCanExecuteChanged();
        StageCommand.NotifyCanExecuteChanged();
        PackageCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<PackagerInfo> SelectedPackagers =>
        Packagers.Where(p => p.IsSelected && p.IsRunnable);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            FilteredPackagers = new ObservableCollection<PackagerInfo>(Packagers);
        }
        else
        {
            var term = SearchText;
            FilteredPackagers = new ObservableCollection<PackagerInfo>(
                Packagers.Where(p =>
                    p.Application.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    p.Vendor.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    p.CMName.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }
        StatusText = string.IsNullOrWhiteSpace(SearchText)
            ? $"Loaded {Packagers.Count} packager(s). Ready."
            : $"Showing {FilteredPackagers.Count} of {Packagers.Count} packager(s).";
    }

    // ── Selection helpers ────────────────────────────────────────

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var newState = !IsAllSelected;
        foreach (var p in Packagers)
            if (p.IsRunnable) p.IsSelected = newState;
        IsAllSelected = newState;
    }

    [RelayCommand]
    private void SelectUpdateAvailable()
    {
        foreach (var p in Packagers)
            p.IsSelected = p.IsRunnable && p.Status == "Update available";
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Packages to CSV",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"AppPackager-Export-{DateTime.Now:yyyyMMdd}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = new System.Collections.Generic.List<string>
            {
                "Application,Vendor,CMName,Current (MECM),Latest,Status,Last Checked,Script"
            };
            foreach (var p in FilteredPackagers)
            {
                var lastChecked = p.LastChecked?.ToString("yyyy-MM-dd HH:mm") ?? "";
                lines.Add($"\"{p.Application}\",\"{p.Vendor}\",\"{p.CMName}\",\"{p.CurrentVersion}\",\"{p.LatestVersion}\",\"{p.Status}\",\"{lastChecked}\",\"{p.Script}\"");
            }
            System.IO.File.WriteAllLines(dialog.FileName, lines);
            AddLogLine($"Exported {FilteredPackagers.Count} rows to: {dialog.FileName}");
            _snackbarService.Show("Export", $"Exported {FilteredPackagers.Count} rows.",
                Wpf.Ui.Controls.ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            AddLogLine($"Export failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _operationCts?.Cancel();
        AddLogLine("Operation cancelled by user.");
    }

    // ── Log helper ───────────────────────────────────────────────

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SRLPackr", "Logs");

    private static readonly string SessionLogPath = Path.Combine(
        LogDir, $"packr-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    // Legacy path kept for backward compat — new sessions write to timestamped files
    private static readonly string PersistentLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SRLPackr", "packr.log");

    private static bool _logInitialized;

    private void AddLogLine(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        LogText = LogText.Length > 0 ? LogText + Environment.NewLine + line : line;

        // Write to both session log and rolling log
        try
        {
            if (!_logInitialized)
            {
                Directory.CreateDirectory(LogDir);
                var header = $"--- SRL Packr session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}";
                File.WriteAllText(SessionLogPath, header);
                File.AppendAllText(PersistentLogPath, Environment.NewLine + header);
                _logInitialized = true;

                // Prune old session logs (keep last 30)
                try
                {
                    var oldLogs = Directory.GetFiles(LogDir, "packr-*.log")
                        .OrderByDescending(f => f)
                        .Skip(30);
                    foreach (var old in oldLogs)
                        File.Delete(old);
                }
                catch { }
            }

            var entry = line + Environment.NewLine;
            File.AppendAllText(SessionLogPath, entry);
            File.AppendAllText(PersistentLogPath, entry);
        }
        catch { }
    }

    // ── Load ─────────────────────────────────────────────────────

    private async Task LoadPackagersAsync()
    {
        var root = _discoveryService.ResolvePackagersRoot();
        if (root is null)
        {
            StatusText = "Packagers folder not found. Configure in Settings.";
            AddLogLine("ERROR: Packagers folder not found. Open Settings to configure.");
            return;
        }

        AddLogLine($"Session log: {SessionLogPath}");
        AddLogLine($"Loading packagers from: {root}");
        var results = await _discoveryService.DiscoverAsync();

        // Restore persisted per-app state (LastChecked, versions)
        var settings = _settingsService.Load();
        foreach (var p in results)
        {
            if (settings.AppStates.TryGetValue(p.Uid, out var state))
            {
                p.LastChecked = state.LastChecked;
                p.LatestVersion = state.LatestVersion;
                p.CurrentVersion = state.CurrentVersion;
                if (!string.IsNullOrEmpty(state.LatestVersion))
                    p.Status = "Cached";
            }
        }

        Packagers = new ObservableCollection<PackagerInfo>(results);
        SearchText = "";
        ApplyFilter();

        // Test MECM connection on launch
        _ = TestMecmConnectionAsync();

        // Auto-check on launch (background, priority-ordered)
        // TODO: re-enable after testing. Disabled to allow manual JSON validation.
        if (false && settings.AutoCheckLatestOnLaunch)
            _ = AutoCheckLatestAsync();
    }

    private async Task TestMecmConnectionAsync()
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.SiteCode))
        {
            MecmConnectionStatus = "Not configured";
            AddLogLine("MECM: Site code not configured. Open Settings.");
            return;
        }

        MecmConnectionStatus = "Testing...";
        AddLogLine($"MECM: Testing connection to site {settings.SiteCode}...");

        try
        {
            // Test by querying a known non-existent app — if it doesn't throw, connection works
            var result = await _mecmQueryService.QueryVersionAsync("__CONNECTION_TEST__", CancellationToken.None);
            // If we get here (even NotFound), the connection works
            MecmConnectionStatus = "Connected";
            AddLogLine($"MECM: Connected to site {settings.SiteCode}");
        }
        catch (Exception ex)
        {
            MecmConnectionStatus = "Failed";
            AddLogLine($"MECM: Connection failed — {ex.Message}");
            _snackbarService.Show("MECM Connection",
                $"Could not connect to site {settings.SiteCode}. Check Settings and ensure the CM console is available.",
                Wpf.Ui.Controls.ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(5));
        }
    }

    // ── Check Latest ─────────────────────────────────────────────

    private async Task CheckLatestAsync()
    {
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Check Latest"); return; }

        await RunCheckLatestOnList(selected, "Checking latest versions...", "Latest check");
    }

    /// <summary>
    /// Auto-check on launch: checks ALL packagers in priority order (browsers first, then oldest).
    /// Runs in background without requiring selection.
    /// </summary>
    private async Task AutoCheckLatestAsync()
    {
        // Small delay to let UI finish rendering
        await Task.Delay(500);

        var ordered = Packagers.OrderBy(p => p.CheckPriority).ToList();
        if (ordered.Count == 0) return;

        AddLogLine("Auto-check: starting background version check (priority order)...");
        await RunCheckLatestOnList(ordered, "Auto-checking latest versions...", "Auto-check");
    }

    private async Task RunCheckLatestOnList(List<PackagerInfo> targets, string startMessage, string logPrefix)
    {
        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = targets.Count;
        _operationCts = new CancellationTokenSource();
        StatusText = startMessage;
        var settings = _settingsService.Load();

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) break;

                var p = targets[i];
                ProgressCurrent = i + 1;
                StatusText = $"{logPrefix}: {ProgressCurrent} of {ProgressTotal} — {p.Application}";
                p.Status = "Checking latest...";
                AddLogLine($"Latest: {p.Application} ({p.Script})");

                try
                {
                    var version = await _executionService.GetLatestVersionAsync(p, _operationCts.Token);

                    if (version is not null)
                    {
                        p.LatestVersion = version;
                        p.LastChecked = DateTime.Now;
                        p.Status = "Latest retrieved";

                        var suffix = p.Script.Contains("m365", StringComparison.OrdinalIgnoreCase)
                            ? $" [{ChannelTag(settings.M365Channel)}]" : "";
                        AddLogLine($"Latest version: {version}{suffix}");
                    }
                    else
                    {
                        p.Status = "Error";
                        p.IsSelected = false;
                        AddLogLine($"ERROR: Could not determine latest version for {p.Application}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    p.Status = "Error";
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            SaveAppStates();
            StatusText = $"{logPrefix} complete. {ProgressCurrent} of {ProgressTotal} checked.";
        }
        catch (OperationCanceledException)
        {
            SaveAppStates();
            StatusText = $"{logPrefix} cancelled at {ProgressCurrent} of {ProgressTotal}.";
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    // ── Check MECM ───────────────────────────────────────────────

    private async Task CheckMecmAsync()
    {
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Check MECM"); return; }

        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.SiteCode))
        {
            _snackbarService.Show("Check MECM", "SiteCode is required. Open Settings.",
                Wpf.Ui.Controls.ControlAppearance.Danger, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return;
        }

        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        StatusText = "Querying MECM...";

        try
        {
            foreach (var p in selected)
            {
                if (_operationCts.IsCancellationRequested) break;

                p.Status = "Querying MECM...";
                AddLogLine($"MECM: {p.Application}");

                try
                {
                    var result = await _mecmQueryService.QueryVersionAsync(p.CMName, _operationCts.Token);

                    if (result.Found)
                    {
                        p.CurrentVersion = result.SoftwareVersion ?? "";

                        if (!string.IsNullOrEmpty(p.LatestVersion))
                        {
                            var cmp = CompareVersions(p.CurrentVersion, p.LatestVersion);
                            p.Status = cmp < 0 ? "Update available"
                                     : cmp == 0 ? "Up to date"
                                     : "Current newer";
                            if (cmp >= 0)
                                p.IsSelected = false;
                        }
                        else
                        {
                            p.Status = "MECM version retrieved";
                        }

                        AddLogLine($"  {result.MatchCount} match(es), version: {result.SoftwareVersion}");
                    }
                    else
                    {
                        p.CurrentVersion = "";
                        p.Status = "Not in MECM";
                        AddLogLine($"  Not in MECM");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    p.Status = "Error";
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            StatusText = "MECM query complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "MECM query cancelled.";
        }
        finally
        {
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    // ── Stage ────────────────────────────────────────────────────

    private async Task StageAsync()
    {
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Stage"); return; }

        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = selected.Count;
        _operationCts = new CancellationTokenSource();
        StatusText = "Staging selected packages...";

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) break;

                var p = selected[i];
                ProgressCurrent = i + 1;
                StatusText = $"Staging: {ProgressCurrent} of {ProgressTotal} — {p.Application}";
                p.Status = "Staging...";
                AddLogLine($"Stage: {p.Application} ({p.Script})");

                try
                {
                    // Config review dialog — show if app has InstallerSettings and review is required
                    if (p.IsJsonDefined && !string.IsNullOrEmpty(p.DefinitionPath) && File.Exists(p.DefinitionPath))
                    {
                        var defJson = await File.ReadAllTextAsync(p.DefinitionPath, _operationCts.Token);
                        var def = System.Text.Json.JsonSerializer.Deserialize<ApplicationPackagerEngine.Models.AppDefinition>(defJson,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        var settings = _settingsService.Load();
                        if (def?.InstallerSettings?.Length > 0 && (def.ReviewBeforeStage || !settings.SkipConfigReview))
                        {
                            var settingsPanel = new Controls.InstallerSettingsPanel
                            {
                                Parameters = def.InstallerSettings,
                                MinWidth = 400
                            };
                            var dialog = new ContentDialog
                            {
                                Title = $"Configure: {p.Application}",
                                Content = settingsPanel,
                                PrimaryButtonText = "Continue",
                                CloseButtonText = "Cancel",
                                DefaultButton = ContentDialogButton.Primary
                            };
                            var dialogResult = await _contentDialogService.ShowAsync(dialog, _operationCts.Token);
                            if (dialogResult != ContentDialogResult.Primary)
                            {
                                p.Status = "Skipped";
                                AddLogLine($"  Skipped (config review cancelled)");
                                continue;
                            }
                            // TODO: pass settingsPanel.Values to StageAsync for ResolveInstallerArgs
                        }
                    }

                    var result = await _executionService.StageAsync(
                        p, line => AddLogLine($"  {line}"), _operationCts.Token);

                    if (result.ExitCode == 0)
                    {
                        p.Status = "Staged";
                        AddLogLine($"Staged: {result.StdOut}");
                    }
                    else
                    {
                        p.Status = "Stage error";
                        p.IsSelected = false;
                        AddLogLine($"Stage FAILED (exit {result.ExitCode})");
                        LogStdErr(result.StdErr);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    p.Status = "Stage error";
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            StatusText = $"Stage complete. {ProgressCurrent} of {ProgressTotal} staged.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Stage cancelled at {ProgressCurrent} of {ProgressTotal}.";
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    // ── Package ──────────────────────────────────────────────────

    private async Task PackageAsync()
    {
        var settings = _settingsService.Load();

        if (string.IsNullOrWhiteSpace(settings.SiteCode))
        {
            _snackbarService.Show("Package", "SiteCode is required. Open Settings.",
                Wpf.Ui.Controls.ControlAppearance.Danger, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return;
        }
        if (string.IsNullOrWhiteSpace(Comment))
        {
            var dialog = new ContentDialog
            {
                Title = "No Comment Provided",
                Content = "Package without a work order / comment? The comment field will be empty in the MECM Application description.",
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            var result = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);
            if (result != ContentDialogResult.Primary)
                return;
        }

        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Package"); return; }

        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = selected.Count;
        _operationCts = new CancellationTokenSource();
        StatusText = "Packaging selected applications...";

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) break;

                var p = selected[i];
                ProgressCurrent = i + 1;
                StatusText = $"Packaging: {ProgressCurrent} of {ProgressTotal} — {p.Application}";
                p.Status = "Packaging...";
                AddLogLine($"Package: {p.Application} ({p.Script})");

                try
                {
                    var result = await _executionService.PackageAsync(
                        p, Comment, line => AddLogLine($"  {line}"), _operationCts.Token);

                    if (result.ExitCode == 0)
                    {
                        p.Status = "Packaged";
                        p.IsSelected = false;
                        var pkgRef = string.IsNullOrEmpty(result.OutLogPath) ? result.StdOut : $"Log: {System.IO.Path.GetFileName(result.OutLogPath)}";
                        AddLogLine($"Packaged. {pkgRef}");
                    }
                    else
                    {
                        p.Status = "Package error";
                        p.IsSelected = false;
                        AddLogLine($"Package FAILED (exit {result.ExitCode})");
                        LogStdErr(result.StdErr);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    p.Status = "Package error";
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            StatusText = $"Package complete. {ProgressCurrent} of {ProgressTotal} packaged.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Package cancelled at {ProgressCurrent} of {ProgressTotal}.";
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private void ShowNoneSelected(string operation)
    {
        _snackbarService.Show(operation, "No packagers selected.",
            Wpf.Ui.Controls.ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(2));
    }

    private void LogStdErr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return;
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var limit = Math.Min(lines.Length, 10);
        for (var i = 0; i < limit; i++)
            AddLogLine($"  STDERR: {lines[i].Trim()}");
        if (lines.Length > 10)
            AddLogLine($"  ... ({lines.Length - 10} more stderr lines)");
    }

    private static string ChannelTag(string channel) => channel switch
    {
        "MonthlyEnterprise" => "MEC",
        "Current" => "CC",
        "SemiAnnual" => "SAEC",
        _ => channel
    };

    private static int CompareVersions(string a, string b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
            return va.CompareTo(vb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveAppStates()
    {
        var s = _settingsService.Load();
        foreach (var p in Packagers)
        {
            s.AppStates[p.Uid] = new AppCheckState
            {
                LastChecked = p.LastChecked,
                LatestVersion = p.LatestVersion,
                CurrentVersion = p.CurrentVersion
            };
        }
        _settingsService.Save(s);
    }
}
