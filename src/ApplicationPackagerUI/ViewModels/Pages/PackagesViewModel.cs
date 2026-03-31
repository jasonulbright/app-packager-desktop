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

    [ObservableProperty] private ObservableCollection<PackagerInfo> _packagers = [];
    [ObservableProperty] private ObservableCollection<PackagerInfo> _filteredPackagers = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _comment = "";
    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty] private bool _showDebugColumns;
    [ObservableProperty] private bool _isAllSelected;

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
        ISnackbarService snackbarService)
    {
        _discoveryService = discoveryService;
        _executionService = executionService;
        _mecmQueryService = mecmQueryService;
        _settingsService = settingsService;
        _snackbarService = snackbarService;

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
    private void CancelOperation()
    {
        _operationCts?.Cancel();
        AddLogLine("Operation cancelled by user.");
    }

    // ── Log helper ───────────────────────────────────────────────

    private void AddLogLine(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        LogText = LogText.Length > 0 ? LogText + Environment.NewLine + line : line;
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

        AddLogLine($"Loading packagers from: {root}");
        var results = await _discoveryService.DiscoverAsync();
        Packagers = new ObservableCollection<PackagerInfo>(results);
        SearchText = "";
        ApplyFilter();
    }

    // ── Check Latest ─────────────────────────────────────────────

    private async Task CheckLatestAsync()
    {
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Check Latest"); return; }

        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        StatusText = "Checking latest versions...";
        var settings = _settingsService.Load();

        try
        {
            foreach (var p in selected)
            {
                if (_operationCts.IsCancellationRequested) break;

                p.Status = "Checking latest...";
                AddLogLine($"Latest: {p.Application} ({p.Script})");

                try
                {
                    var version = await _executionService.GetLatestVersionAsync(p, _operationCts.Token);

                    if (version is not null)
                    {
                        p.LatestVersion = version;

                        // Compare with current if available
                        if (!string.IsNullOrEmpty(p.CurrentVersion))
                        {
                            var cmp = CompareVersions(p.CurrentVersion, version);
                            p.Status = cmp < 0 ? "Update available"
                                     : cmp == 0 ? "Up to date"
                                     : "Current newer";
                        }
                        else
                        {
                            p.Status = "Latest retrieved";
                        }

                        // M365 channel tag in log
                        var suffix = p.Script.Contains("m365", StringComparison.OrdinalIgnoreCase)
                            ? $" [{ChannelTag(settings.M365Channel)}]" : "";
                        AddLogLine($"Latest version: {version}{suffix}");
                    }
                    else
                    {
                        p.Status = "Error";
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

            StatusText = "Latest check complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Latest check cancelled.";
        }
        finally
        {
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
                        p.Status = "Not found in MECM";
                        AddLogLine($"  Not found in MECM");
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
        _operationCts = new CancellationTokenSource();
        StatusText = "Staging selected packages...";

        try
        {
            foreach (var p in selected)
            {
                if (_operationCts.IsCancellationRequested) break;

                p.Status = "Staging...";
                AddLogLine($"Stage: {p.Application} ({p.Script})");

                try
                {
                    var result = await _executionService.StageAsync(
                        p, line => AddLogLine($"  {line}"), _operationCts.Token);

                    if (result.ExitCode == 0)
                    {
                        p.Status = "Staged";
                        AddLogLine($"Staged. Log: {System.IO.Path.GetFileName(result.OutLogPath)}");
                    }
                    else
                    {
                        p.Status = "Stage error";
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

            StatusText = "Stage complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stage cancelled.";
        }
        finally
        {
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
            _snackbarService.Show("Package", "Work Order / Comment is required.",
                Wpf.Ui.Controls.ControlAppearance.Danger, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return;
        }

        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Package"); return; }

        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        StatusText = "Packaging selected applications...";

        try
        {
            foreach (var p in selected)
            {
                if (_operationCts.IsCancellationRequested) break;

                p.Status = "Packaging...";
                AddLogLine($"Package: {p.Application} ({p.Script})");

                try
                {
                    var result = await _executionService.PackageAsync(
                        p, Comment, line => AddLogLine($"  {line}"), _operationCts.Token);

                    if (result.ExitCode == 0)
                    {
                        p.Status = "Packaged";
                        AddLogLine($"Packaged. Log: {System.IO.Path.GetFileName(result.OutLogPath)}");
                    }
                    else
                    {
                        p.Status = "Package error";
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

            StatusText = "Package complete.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Package cancelled.";
        }
        finally
        {
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
        // Try System.Version first, fall back to string comparison
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb))
            return va.CompareTo(vb);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
