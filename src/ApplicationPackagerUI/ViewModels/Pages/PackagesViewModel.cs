using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
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
    private readonly ApplicationPackagerEngine.Services.ApplicationInventoryService _inventory;
    private readonly ApplicationInventoryProjection _inventoryProjection;
    private readonly ISnackbarService _snackbarService;
    private readonly IContentDialogService _contentDialogService;
    private readonly ApplicationPackagerEngine.Services.DroppedInstallerAnalysisService _dropAnalysisService;
    private readonly ApplicationPackagerEngine.Services.StagingService _stagingService;
    private readonly ApplicationPackagerEngine.Services.Wsus.IWsusPublisher _wsusPublisher;
    private readonly IPublishDialogs _publishDialogs;
    private readonly PublishRunService _publishRuns;

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

    // Publish buttons per destination. Written only by RefreshPublishActionState.
    [ObservableProperty] private bool _isCheckMecmAvailable = true;
    [ObservableProperty] private string _checkMecmToolTip = "";
    [ObservableProperty] private bool _isPublishConfigMgrAvailable;
    [ObservableProperty] private string _publishConfigMgrToolTip = "";
    [ObservableProperty] private bool _isPublishIntuneAvailable;
    [ObservableProperty] private string _publishIntuneToolTip = "";
    [ObservableProperty] private bool _isPublishWsusAvailable;
    [ObservableProperty] private string _publishWsusToolTip = "";
    [ObservableProperty] private bool _isWsusUpdatesAvailable;
    [ObservableProperty] private string _wsusUpdatesToolTip = "";
    /// <summary>The Intune note column shows only while Intune is a system in use.</summary>
    [ObservableProperty] private bool _showIntuneColumn;
    private bool _skipMecmPreflight;

    public IAsyncRelayCommand LoadPackagersCommand { get; }
    public IAsyncRelayCommand CheckLatestCommand { get; }
    public IAsyncRelayCommand CheckMecmCommand { get; }
    public IAsyncRelayCommand StageCommand { get; }
    public IAsyncRelayCommand PublishConfigMgrCommand { get; }
    public IAsyncRelayCommand PublishIntuneCommand { get; }
    public IAsyncRelayCommand PublishWsusCommand { get; }
    public IRelayCommand WsusUpdatesCommand { get; }
    public IAsyncRelayCommand OneClickCommand { get; }

    private CancellationTokenSource? _operationCts;

    public PackagesViewModel(
        PackagerDiscoveryService discoveryService,
        PackagerExecutionService executionService,
        MecmQueryService mecmQueryService,
        SettingsService settingsService,
        ISnackbarService snackbarService,
        IContentDialogService contentDialogService,
        ApplicationPackagerEngine.Services.DroppedInstallerAnalysisService dropAnalysisService,
        ApplicationPackagerEngine.Services.StagingService stagingService,
        ApplicationPackagerEngine.Services.Wsus.IWsusPublisher wsusPublisher,
        IPublishDialogs publishDialogs,
        PublishRunService publishRuns,
        ApplicationPackagerEngine.Services.ApplicationInventoryService inventory,
        ApplicationInventoryProjection inventoryProjection,
        ApplicationPackagerEngine.Services.IDiscoveryService discovery,
        IDiscoveryPrompt discoveryPrompt,
        IAddFileRoute addFileRoute)
    {
        AddFileRoute = addFileRoute;
        _publishRuns = publishRuns;
        _dropAnalysisService = dropAnalysisService;
        _stagingService = stagingService;
        _discoveryService = discoveryService;
        _executionService = executionService;
        _mecmQueryService = mecmQueryService;
        _settingsService = settingsService;
        _inventory = inventory;
        _inventoryProjection = inventoryProjection;
        _snackbarService = snackbarService;
        _contentDialogService = contentDialogService;
        _wsusPublisher = wsusPublisher;
        _publishDialogs = publishDialogs;

        LoadPackagersCommand = new AsyncRelayCommand(LoadPackagersAsync);
        CheckLatestCommand = new AsyncRelayCommand(CheckLatestAsync, CanRunOperation);
        CheckMecmCommand = new AsyncRelayCommand(CheckMecmAsync, CanRunCheckMecm);
        StageCommand = new AsyncRelayCommand(StageAsync, CanRunOperation);
        PublishConfigMgrCommand = new AsyncRelayCommand(() => StartPublishRunAsync(PublishRunTargets.ConfigMgr), () => CanRunOperation() && IsPublishConfigMgrAvailable);
        PublishIntuneCommand = new AsyncRelayCommand(() => StartPublishRunAsync(PublishRunTargets.IntuneOnly), () => CanRunOperation() && IsPublishIntuneAvailable);
        PublishWsusCommand = new AsyncRelayCommand(() => StartPublishRunAsync(PublishRunTargets.WsusOnly), () => CanRunOperation() && IsPublishWsusAvailable);
        WsusUpdatesCommand = new RelayCommand(OpenWsusUpdates, () => CanRunOperation() && IsWsusUpdatesAvailable);
        OneClickCommand = new AsyncRelayCommand(OneClickAsync, CanRunOperation);
        InitializeDiscovery(discovery, discoveryPrompt);

        RefreshPublishActionState();

        _ = InitializeAsync();
    }

    /// <summary>The settings the publish buttons depend on, as stored now.</summary>
    public static PublishReadiness ReadinessOf(AppSettings settings) => PublishRunService.ReadinessOf(settings);

    /// <summary>
    /// Applies Systems in use and the stored settings to the publish buttons: a button
    /// whose system is not ready is disabled with a tooltip naming the missing setting,
    /// and one whose setting was saved since is enabled again. The only writer of these
    /// bound properties; called at launch, after a Settings save, and after Setup saves.
    /// </summary>
    public void RefreshPublishActionState()
    {
        var settings = _settingsService.Load();
        var readiness = ReadinessOf(settings);
        var state = PublishActionState.For(readiness);
        RefreshDiscoverState(settings);

        ShowIntuneColumn = readiness.Systems.Intune;
        IsCheckMecmAvailable = state.CheckMecmEnabled;
        CheckMecmToolTip = state.CheckMecmToolTip;
        IsPublishConfigMgrAvailable = state.MecmEnabled;
        PublishConfigMgrToolTip = state.MecmToolTip;
        IsPublishIntuneAvailable = state.IntuneEnabled;
        PublishIntuneToolTip = state.IntuneToolTip;
        IsPublishWsusAvailable = state.WsusEnabled;
        PublishWsusToolTip = state.WsusToolTip;
        IsWsusUpdatesAvailable = state.WsusUpdatesEnabled;
        WsusUpdatesToolTip = state.WsusUpdatesToolTip;
        _skipMecmPreflight = state.SkipMecmPreflight;

        NotifyActionCommands();
    }

    private void NotifyActionCommands()
    {
        CheckLatestCommand.NotifyCanExecuteChanged();
        CheckMecmCommand.NotifyCanExecuteChanged();
        StageCommand.NotifyCanExecuteChanged();
        PublishConfigMgrCommand.NotifyCanExecuteChanged();
        PublishIntuneCommand.NotifyCanExecuteChanged();
        PublishWsusCommand.NotifyCanExecuteChanged();
        WsusUpdatesCommand.NotifyCanExecuteChanged();
        OneClickCommand.NotifyCanExecuteChanged();
        DiscoverCommand.NotifyCanExecuteChanged();
        NotifyCveCommands();
    }

    private async Task InitializeAsync()
    {
        await LoadPackagersCommand.ExecuteAsync(null);

        var pendingDrop = ReadStartupDropPath();
        if (pendingDrop is null) return;

        AddLogLine($"Startup: analysing installer from --analyze-drop: {pendingDrop}");
        await AnalyzeDroppedInstallerCommand.ExecuteAsync(pendingDrop);
    }

    private bool CanRunOperation() => !IsOperationRunning;

    /// <summary>Disabled rather than hidden when ConfigMgr is not ready: the tooltip states why.</summary>
    private bool CanRunCheckMecm() => CanRunOperation() && IsCheckMecmAvailable;

    partial void OnIsOperationRunningChanged(bool value) => NotifyActionCommands();

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
            p.IsSelected = p.IsRunnable && CveRowState.IsUpdateAvailable(p.Status);
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export the App Packager grid to CSV",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"AppPackager-Export-{DateTime.Now:yyyyMMdd}.csv"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = new System.Collections.Generic.List<string>
            {
                "Application,Vendor,CMName,Current (ConfigMgr),Latest,Status,Last Checked,Script"
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

    private static readonly string LogDir = Path.Combine(ApplicationPackagerEngine.Services.AppDataPaths.LegacyRoot, "Logs");

    private static readonly string SessionLogPath = Path.Combine(
        LogDir, $"packr-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    // Every session also appends to this one rolling log beside the per-session files.
    private static readonly string PersistentLogPath = Path.Combine(ApplicationPackagerEngine.Services.AppDataPaths.LegacyRoot, "packr.log");

    private static bool _logInitialized;

    /// <summary>Writes one line to the operation log for work that runs outside this page.</summary>
    public void AppendLog(string message) => AddLogLine(message);

    private void AddLogLine(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        LogText = LogText.Length > 0 ? LogText + Environment.NewLine + line : line;

        try
        {
            if (!_logInitialized)
            {
                Directory.CreateDirectory(LogDir);
                var header = $"--- App Packager Desktop session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}";
                File.WriteAllText(SessionLogPath, header);
                File.AppendAllText(PersistentLogPath, Environment.NewLine + header);
                _logInitialized = true;

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

    private async Task LoadPackagersAsync()
    {
        var root = _discoveryService.ResolvePackagersRoot();
        AddLogLine($"Session log: {SessionLogPath}");
        _ = _settingsService.Load();
        if (_settingsService.TakeLoadWarning() is { } settingsWarning) AddLogLine("WARNING: " + settingsWarning);
        AddLogLine(root is null
            ? "Loading the shipped definitions (no PS1 packagers folder configured)."
            : $"Loading packagers from: {root}");
        var load = await ReadGridAsync(() => _discoveryService.DiscoverAsync(), () => _discoveryService.DiscoverAllAsync());
        var results = load.Rows;
        if (results.Count == 0)
        {
            Packagers = [];
            SearchText = "";
            ApplyFilter();
            StatusText = load.StatusText ?? "";
            if (load.LogLine is not null) AddLogLine(load.LogLine);
            return;
        }

        var settings = _settingsService.Load();
        foreach (var p in results)
        {
            var vendor = _inventoryProjection.Vendor(p);
            p.LastChecked = vendor?.LastSucceededAt?.LocalDateTime;
            p.LatestVersion = vendor?.LatestVersion ?? "";
            p.CurrentVersion = _inventoryProjection.Environment(p, settings)?.LatestObservedVersion ?? "";
            if (!string.IsNullOrEmpty(vendor?.LatestVersion)) p.Status = "Cached";
            ApplyStoredCveState(p, settings);
        }

        Packagers = new ObservableCollection<PackagerInfo>(results);
        SearchText = "";
        ApplyFilter();

        _ = TestMecmConnectionAsync();
    }

    /// <summary>The rows the grid shows and, when it shows none, the status and the log line that say why.</summary>
    public sealed record GridLoad(IReadOnlyList<PackagerInfo> Rows, string? StatusText, string? LogLine);

    public const string AllHiddenMessage = "All applications are hidden. Select Catalog to show applications.";

    /// <summary>Reads every application, hidden ones included, only when no visible one exists.</summary>
    public static async Task<GridLoad> ReadGridAsync(Func<Task<List<PackagerInfo>>> visible, Func<Task<List<PackagerInfo>>> all)
    {
        var rows = await visible();
        if (rows.Count > 0) return new GridLoad(rows, null, null);
        if ((await all()).Count > 0)
            return new GridLoad([], AllHiddenMessage, AllHiddenMessage);
        return new GridLoad([],
            "No applications found. Check the Definitions folder beside the executable or set a Packagers folder in Settings.",
            "ERROR: no definitions beside the executable and no packagers folder.");
    }

    /// <summary>
    /// Reads a --analyze-drop &lt;path&gt; startup argument. Drives the drop review flow without
    /// a real drag-drop, which no automation harness can synthesise across processes.
    /// </summary>
    private static string? ReadStartupDropPath()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--analyze-drop", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private async Task TestMecmConnectionAsync()
    {
        var settings = _settingsService.Load();

        // Skipped before it can open a provider connection: a workstation without ConfigMgr has no site.
        if (_skipMecmPreflight)
        {
            MecmConnectionStatus = "Not used";
            AddLogLine("ConfigMgr: pre-flight version query skipped; ConfigMgr is not in use (Settings, Systems in use).");
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.SiteCode))
        {
            MecmConnectionStatus = "Not configured";
            AddLogLine("ConfigMgr: Site code not configured. Open Settings.");
            return;
        }

        MecmConnectionStatus = "Testing...";
        AddLogLine($"ConfigMgr: Testing connection to site {settings.SiteCode}...");

        try
        {
            // A query for an application that does not exist proves the connection when it returns, NotFound included.
            var result = await _mecmQueryService.QueryVersionAsync("__CONNECTION_TEST__", CancellationToken.None);
            MecmConnectionStatus = "Connected";
            AddLogLine($"ConfigMgr: Connected to site {settings.SiteCode}");
        }
        catch (Exception ex)
        {
            MecmConnectionStatus = "Failed";
            AddLogLine($"ConfigMgr: Connection failed — {ex.Message}");
            _snackbarService.Show("ConfigMgr Connection",
                $"Could not connect to site {settings.SiteCode}. Check Settings and ensure the CM console is available.",
                Wpf.Ui.Controls.ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(5));
        }
    }

    private async Task CheckLatestAsync()
    {
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Check Latest"); return; }

        await RunCheckLatestOnList(selected, "Checking latest versions...", "Latest check");
    }

    private async Task<PackagingRunTally> RunCheckLatestOnList(
        List<PackagerInfo> targets, string startMessage, string logPrefix)
    {
        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = targets.Count;
        _operationCts = new CancellationTokenSource();
        StatusText = startMessage;
        var settings = _settingsService.Load();
        var tally = new PackagingRunTally();

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                // Cancellation seen between items interrupts nothing; the items after this
                // point were never attempted.
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }

                var p = targets[i];
                ProgressCurrent = i + 1;
                StatusText = $"{logPrefix}: {ProgressCurrent} of {ProgressTotal} — {p.Application}";
                p.Status = "Checking latest...";
                AddLogLine($"Latest: {p.Application} ({p.Script})");
                var operationId = Guid.NewGuid().ToString("N");
                var source = _executionService.VersionSource(p);
                var at = DateTimeOffset.UtcNow;

                try
                {
                    _inventory.BeginOperation(new InventoryOperation(operationId, "CheckLatest", p.Uid, null, source, at));
                    var version = await _executionService.GetLatestVersionAsync(p, _operationCts.Token);

                    if (version is not null)
                    {
                        PersistVendorCheck(p, operationId, source, version, InventoryOutcome.Succeeded, null);
                        p.LatestVersion = version;
                        p.LastChecked = DateTime.Now;
                        p.Status = "Latest retrieved";
                        tally.RecordSuccess();

                        var suffix = p.Script.Contains("m365", StringComparison.OrdinalIgnoreCase)
                            ? $" [{ChannelTag(settings.M365Channel)}]" : "";
                        AddLogLine($"Latest version: {version}{suffix}");
                    }
                    else
                    {
                        // A null lookup after the operator cancelled is an interrupted item,
                        // not a version that could not be found.
                        if (tally.RecordInterruptedOrFailed(_operationCts.Token))
                        {
                            PersistVendorCheck(p, operationId, source, null, InventoryOutcome.Cancelled, "Check cancelled.");
                            p.Status = "Cancelled";
                            break;
                        }
                        PersistVendorCheck(p, operationId, source, null, InventoryOutcome.Failed, "Could not determine the latest version.");
                        p.Status = "Error";
                        p.IsSelected = false;
                        AddLogLine($"ERROR: Could not determine latest version for {p.Application}");
                    }
                }
                catch (OperationCanceledException ex)
                {
                    // The row that was in flight has no verdict and is not "not attempted".
                    if (tally.RecordInterruptedOrFailed(_operationCts.Token))
                    {
                        PersistVendorCheck(p, operationId, source, null, InventoryOutcome.Cancelled, ex.Message);
                        p.Status = "Cancelled";
                        break;
                    }
                    // A timeout inside the service throws the same type; that is this item
                    // failing, not the operator stopping the batch.
                    p.Status = "Error";
                    PersistVendorCheck(p, operationId, source, null, InventoryOutcome.Failed, ex.Message);
                    AddLogLine($"ERROR: {ex.Message}");
                }
                catch (Exception ex)
                {
                    p.Status = "Error";
                    tally.RecordFailure();
                    PersistVendorCheck(p, operationId, source, null, InventoryOutcome.Failed, ex.Message);
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            // A lookup can finish successfully after the operator cancelled, so the run's
            // state is settled here rather than only when an item was interrupted.
            tally.Finish(_operationCts.Token);
            StatusText = tally.Summarize(logPrefix, ProgressTotal, "checked");
            return tally;
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    private async Task CheckMecmAsync()
    {
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Check ConfigMgr"); return; }

        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.SiteCode))
        {
            _snackbarService.Show("Check ConfigMgr", "SiteCode is required. Open Settings.",
                Wpf.Ui.Controls.ControlAppearance.Danger, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return;
        }
        var environment = InventoryEnvironmentIdentity.ConfigMgr(settings.SiteCode, settings.ProviderMachine);

        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        StatusText = "Querying ConfigMgr...";
        var tally = new PackagingRunTally();

        try
        {
            foreach (var p in selected)
            {
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }

                p.Status = "Querying ConfigMgr...";
                AddLogLine($"ConfigMgr: {p.Application}");
                var operationId = Guid.NewGuid().ToString("N");
                var startedAt = DateTimeOffset.UtcNow;

                try
                {
                    _inventory.BeginOperation(new InventoryOperation(operationId, "CheckEnvironment", p.Uid,
                        ApplicationInventoryProjection.ReferenceFor(p), environment.Key, startedAt));
                    var result = await _mecmQueryService.QueryVersionAsync(p.CMName, _operationCts.Token);

                    // A query that never reached an answer establishes nothing, so the last
                    // known version stays put rather than being cleared as an absence.
                    if (result.Failed)
                    {
                        // A failed query after the operator cancelled is an interrupted
                        // item; the site was never given a chance to answer.
                        if (tally.RecordInterruptedOrFailed(_operationCts.Token))
                        {
                            PersistEnvironmentObservation(p, environment, operationId, null, InventoryOutcome.Cancelled, result.FailureReason);
                            p.Status = "Cancelled";
                            break;
                        }
                        PersistEnvironmentObservation(p, environment, operationId, null, InventoryOutcome.Failed, result.FailureReason);
                        p.Status = "Error";
                        AddLogLine($"ERROR: {result.FailureReason}");
                        continue;
                    }

                    // The site answered either way. An application it does not carry is a
                    // completed check, not a failed one.
                    tally.RecordSuccess();
                    PersistEnvironmentObservation(p, environment, operationId,
                        result.Found ? result.SoftwareVersion : null,
                        result.Found ? InventoryOutcome.Succeeded : InventoryOutcome.NotFound, null);

                    if (result.Found)
                    {
                        p.CurrentVersion = result.SoftwareVersion ?? "";

                        if (!string.IsNullOrEmpty(p.LatestVersion))
                        {
                            var cmp = CompareVersions(p.CurrentVersion, p.LatestVersion);
                            p.Status = cmp < 0 ? CveRowState.UpdateStatus(p)
                                     : cmp == 0 ? "Up to date"
                                     : "Current newer";
                            if (cmp >= 0)
                                p.IsSelected = false;
                        }
                        else
                        {
                            p.Status = "ConfigMgr version retrieved";
                        }

                        AddLogLine($"  {result.MatchCount} match(es), version: {result.SoftwareVersion}");
                    }
                    else
                    {
                        p.CurrentVersion = "";
                        p.Status = "Not found in ConfigMgr";
                        AddLogLine($"  Not found in ConfigMgr");
                    }
                }
                catch (OperationCanceledException ex)
                {
                    if (tally.RecordInterruptedOrFailed(_operationCts.Token))
                    {
                        PersistEnvironmentObservation(p, environment, operationId, null, InventoryOutcome.Cancelled, ex.Message);
                        p.Status = "Cancelled";
                        break;
                    }
                    PersistEnvironmentObservation(p, environment, operationId, null, InventoryOutcome.Failed, ex.Message);
                    p.Status = "Error";
                    AddLogLine($"ERROR: {ex.Message}");
                }
                catch (Exception ex)
                {
                    p.Status = "Error";
                    tally.RecordFailure();
                    PersistEnvironmentObservation(p, environment, operationId, null, InventoryOutcome.Failed, ex.Message);
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            tally.Finish(_operationCts.Token);
            StatusText = tally.Summarize("ConfigMgr query", selected.Count, "checked");
        }
        finally
        {
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }

        if (settings.Vulnerability is { LookupAfterCheckCm: true } && !tally.Cancelled)
            await RunCveCheckAsync(selected, force: false, "Checking CVEs for the queried applications...");
    }

    private async Task StageAsync() => await RunStageAsync();

    /// <summary>
    /// Stages or packages one row for the Application Workbench through the same per-row
    /// flow the buttons use, with the workbench's run snapshot and, for a publish, its target.
    /// The grid selection is left as it is.
    /// </summary>
    public async Task RunWorkbenchBuildAsync(PackagerInfo row, bool package, string? target, RunSnapshot snapshot)
    {
        if (IsOperationRunning)
        {
            AddLogLine($"Workbench run skipped: another operation is running. {row.Application} was not {(package ? "packaged" : "staged")}.");
            return;
        }
        var profileName = ApplicationPackagerEngine.Services.Workbench.WorkbenchJson.AsText(snapshot.Profile["Name"]) ?? snapshot.ProfileId;
        AddLogLine($"Workbench {(package ? "Package" : "Stage")}: {row.Application} (profile {profileName}, revision {snapshot.ProfileRevision}, build {snapshot.BuildId})");
        if (package && target == PublishRunTargets.WsusOnly) await PublishWsusRunAsync([row], snapshot);
        else if (package) await PackageAsync([row], target, snapshot);
        else await RunStageAsync([row], snapshot);
    }

    /// <summary>Stages the selection, or the given rows, and reports what actually happened to each item.</summary>
    private async Task<PackagingRunTally> RunStageAsync(IReadOnlyList<PackagerInfo>? rows = null, RunSnapshot? workbenchSnapshot = null)
    {
        var selected = rows?.ToList() ?? SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Stage"); return new PackagingRunTally(); }
        selected = ConfirmLocalSourceFolders(selected);
        if (selected.Count == 0) return new PackagingRunTally();

        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = selected.Count;
        _operationCts = new CancellationTokenSource();
        StatusText = "Staging selected packages...";
        var tally = new PackagingRunTally();

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }

                var p = selected[i];
                ProgressCurrent = i + 1;
                StatusText = $"Staging: {ProgressCurrent} of {ProgressTotal} — {p.Application}";
                p.Status = "Staging...";
                AddLogLine($"Stage: {p.Application} ({p.Script})");

                try
                {
                    Dictionary<string, string>? settingsValues = null;

                    // Config review dialog — show if app has InstallerSettings and review is required
                    if (p.IsJsonDefined && !string.IsNullOrEmpty(p.DefinitionPath) && File.Exists(p.DefinitionPath))
                    {
                        var defJson = await File.ReadAllTextAsync(p.DefinitionPath, _operationCts.Token);
                        var def = System.Text.Json.JsonSerializer.Deserialize<ApplicationPackagerEngine.Models.AppDefinition>(defJson,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        var settings = _settingsService.Load();
                        if (def?.InstallerSettings?.Length > 0 && (def.ReviewBeforeStage || !settings.SkipConfigReview))
                        {
                            // The dialog starts from the values saved under Settings; a skipped
                            // dialog applies those same values through the execution service.
                            var settingsPanel = new Controls.InstallerSettingsPanel
                            {
                                Store = settings.InstallerSettingValues.TryGetValue(def.Id, out var saved)
                                    ? new Dictionary<string, string>(saved)
                                    : new Dictionary<string, string>(),
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
                            // A dialog dismissed because the operator cancelled the run is
                            // a cancellation, not a decision to skip this application.
                            _operationCts.Token.ThrowIfCancellationRequested();
                            if (dialogResult != ContentDialogResult.Primary)
                            {
                                p.Status = "Skipped";
                                tally.RecordSkip();
                                AddLogLine($"  Skipped (config review cancelled)");
                                continue;
                            }
                            settingsValues = new Dictionary<string, string>(settingsPanel.Values);
                        }
                    }

                    var result = await _executionService.StageAsync(
                        p, line => AddLogLine($"  {line}"), _operationCts.Token, settingsValues, workbenchSnapshot);

                    if (result.ExitCode == 0)
                    {
                        p.Status = "Staged";
                        tally.RecordSuccess();
                        AddLogLine($"Staged: {result.StdOut}");
                    }
                    else
                    {
                        if (tally.RecordInterruptedOrFailed(_operationCts.Token))
                        {
                            p.Status = "Cancelled";
                            break;
                        }
                        p.Status = "Stage error";
                        p.IsSelected = false;
                        AddLogLine($"Stage FAILED (exit {result.ExitCode})");
                        LogStdErr(result.StdErr);
                    }
                }
                catch (OperationCanceledException ex)
                {
                    if (tally.RecordInterruptedOrFailed(_operationCts.Token))
                    {
                        p.Status = "Cancelled";
                        break;
                    }
                    p.Status = "Stage error";
                    AddLogLine($"ERROR: {ex.Message}");
                }
                catch (Exception ex)
                {
                    p.Status = "Stage error";
                    tally.RecordFailure();
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            tally.Finish(_operationCts.Token);
            StatusText = tally.Summarize("Stage", ProgressTotal, "staged");
            return tally;
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>
    /// Packages the selection, or the given rows, for one publish target: ConfigMgr or Intune only.
    /// </summary>
    private async Task PackageAsync(IReadOnlyList<PackagerInfo>? rows, string? targetOverride, RunSnapshot? snapshot)
    {
        // The comment and review dialogs await the operator; the flag is raised before them
        // so no other operation starts while they are open.
        if (IsOperationRunning)
        {
            AddLogLine("Package skipped: another operation is running.");
            return;
        }
        IsOperationRunning = true;
        try { await PackageCoreAsync(rows, targetOverride, snapshot); }
        finally { IsOperationRunning = false; }
    }

    private async Task PackageCoreAsync(IReadOnlyList<PackagerInfo>? rows, string? targetOverride, RunSnapshot? snapshot)
    {
        var settings = _settingsService.Load();
        var target = targetOverride ?? PublishRunTargets.ConfigMgr;
        var label = PublishLabel(target);

        // The site gates apply only to a ConfigMgr run; an Intune-only run needs its
        // credentials instead, so a console-less workstation can still publish.
        var blocked = PublishRunService.RunGate(target, settings);

        if (blocked is not null)
        {
            _snackbarService.Show(label, blocked,
                Wpf.Ui.Controls.ControlAppearance.Danger, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
            AddLogLine($"{label} blocked: {blocked}");
            return;
        }

        if (string.IsNullOrWhiteSpace(Comment))
        {
            var dialog = new ContentDialog
            {
                Title = "No Comment Provided",
                Content = "Package without a work order / comment? The comment field will be empty in the ConfigMgr Application description.",
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };
            var result = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);
            if (result != ContentDialogResult.Primary)
                return;
        }

        var selected = rows?.ToList() ?? SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Package"); return; }
        selected = ConfirmLocalSourceFolders(selected);
        if (selected.Count == 0) return;
        if (DeploymentTargets.PublishesToIntune(target) && !await ReviewIntuneCompatibilityAsync(selected))
        {
            AddLogLine("Intune review: run cancelled.");
            return;
        }

        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = selected.Count;
        _operationCts = new CancellationTokenSource();
        StatusText = "Packaging selected applications...";

        // Per-run only: an apply-to-all answer dies with the loop and is never persisted.
        ConflictAnswer? runConflictAnswer = null;


        var tally = new ApplicationPackagerEngine.Models.PackagingRunTally();

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }

                var p = selected[i];
                ProgressCurrent = i + 1;
                StatusText = $"Packaging: {ProgressCurrent} of {ProgressTotal} — {p.Application}";
                p.Status = "Packaging...";
                AddLogLine($"Package: {p.Application} ({p.Script})");

                try
                {
                    var result = await _executionService.PackageAsync(
                        p, Comment, line => AddLogLine($"  {line}"), _operationCts.Token,
                        targetOverride: target, snapshot: snapshot);

                    if (result.Conflict is not null)
                    {
                        var answer = runConflictAnswer ?? await AskExistingConflictAsync(result.Conflict);
                        if (answer.ApplyToAll && answer.Behavior != ConflictAnswer.CancelRemaining)
                            runConflictAnswer = answer;

                        if (answer.Behavior == ConflictAnswer.CancelRemaining)
                        {
                            p.Status = "Skipped";
                            tally.RecordSkip();
                            AddLogLine($"Existing application kept: {result.Conflict.ApplicationName} v{result.Conflict.Version}. Remaining applications cancelled.");
                            _operationCts.Cancel();
                            break;
                        }

                        if (answer.Behavior == ConflictAnswer.Overwrite)
                        {
                            AddLogLine($"Overwriting existing: {result.Conflict.ApplicationName} v{result.Conflict.Version}");
                            result = await _executionService.PackageAsync(
                                p, Comment, line => AddLogLine($"  {line}"), _operationCts.Token,
                                ApplicationPackagerEngine.Services.OnExistingBehavior.Overwrite,
                                snapshot, target);
                        }
                        else
                        {
                            p.Status = "Skipped";
                            p.IsSelected = false;
                            tally.RecordSkip();
                            AddLogLine($"Existing application kept: {result.Conflict.ApplicationName} v{result.Conflict.Version}");
                            continue;
                        }
                    }

                    if (result.ExitCode == 0)
                    {
                        p.Status = RowStatus(target, result.ExitCode);
                        p.IsSelected = false;
                        tally.RecordSuccess();
                        AddLogLine(PublishRunService.PackagedLine(result));
                    }
                    else
                    {
                        // The target catches its own exceptions, so a run cancelled mid-flight
                        // comes back as an ordinary failed result. The caller's token is what
                        // tells the two apart.
                        var stopped = tally.RecordInterruptedOrFailed(_operationCts.Token);
                        p.Status = stopped ? "Cancelled" : RowStatus(target, result.ExitCode);
                        if (!stopped)
                        {
                            p.IsSelected = false;
                            AddLogLine($"Package FAILED (exit {result.ExitCode})");
                            LogStdErr(result.StdErr);
                        }

                        // Notes stand either way: a partially completed target may already
                        // have created the application or written content.
                        LogIntuneNotes(p, result);
                        if (stopped) break;
                        continue;
                    }

                    LogIntuneNotes(p, result);
                }
                catch (OperationCanceledException ex)
                {
                    if (tally.RecordInterruptedOrFailed(_operationCts.Token))
                    {
                        p.Status = "Cancelled";
                        break;
                    }
                    p.Status = RowStatus(target, 1);
                    AddLogLine($"ERROR: {ex.Message}");
                }
                catch (Exception ex)
                {
                    p.Status = RowStatus(target, 1);
                    tally.RecordFailure();
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            // Keeping an existing application with Cancel remaining cancels the token and
            // leaves the loop normally, so completion is not what happened.
            tally.Finish(_operationCts.Token);
            StatusText = PublishRunTargets.SkipsSite(target)
                ? tally.Summarize("Publish", ProgressTotal, "published")
                : tally.Summarize("Package", ProgressTotal, "packaged");
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>Operator answer to one existing-application conflict.</summary>
    private sealed record ConflictAnswer(string Behavior, bool ApplyToAll)
    {
        public const string Skip = "Skip";
        public const string Overwrite = "Overwrite";
        public const string CancelRemaining = "CancelRemaining";
    }

    /// <summary>
    /// Asks what to do about an application the site already holds under this
    /// exact title, at any version. Overwrite replaces its deployment types
    /// and keeps the application object and its deployments.
    /// </summary>
    private async Task<ConflictAnswer> AskExistingConflictAsync(ExistingApplicationConflict conflict)
    {
        var applyToAll = new System.Windows.Controls.CheckBox
        {
            Content = "Do this for all remaining conflicts in this run",
            Margin = new System.Windows.Thickness(0, 12, 0, 0)
        };

        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"'{conflict.ApplicationName}' already exists at version {conflict.Version}. "
                 + $"The staged version is {conflict.IncomingVersion}.\n\n"
                 + "Overwrite updates its content and deployment types while keeping the application and its deployments. "
                 + "Skip leaves it unchanged; Cancel remaining stops the rest of the run.",
            TextWrapping = System.Windows.TextWrapping.Wrap
        });
        panel.Children.Add(applyToAll);

        var dialog = new ContentDialog
        {
            Title = "Application Already Exists",
            Content = panel,
            PrimaryButtonText = "Overwrite",
            SecondaryButtonText = "Skip",
            CloseButtonText = "Cancel remaining",
            DefaultButton = ContentDialogButton.Secondary
        };

        var result = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);
        var behavior = result switch
        {
            ContentDialogResult.Primary => ConflictAnswer.Overwrite,
            ContentDialogResult.Secondary => ConflictAnswer.Skip,
            _ => ConflictAnswer.CancelRemaining
        };
        return new ConflictAnswer(behavior, applyToAll.IsChecked == true);
    }

    /// <summary>The route of a file drop on the grid to the Add from file page.</summary>
    public IAddFileRoute? AddFileRoute { get; set; }

    /// <summary>
    /// Entry point for a file drop: the files go to the Add from file page, which checks the file
    /// type and runs the same analysis and review as its picker.
    /// </summary>
    [RelayCommand]
    private Task HandleDroppedFilesAsync(string[]? paths)
    {
        if (paths is null || paths.Length == 0 || AddFileRoute is null) return Task.CompletedTask;
        AddFileRoute.Route(paths);
        return Task.CompletedTask;
    }

    /// <summary>The grid row the result panel of an add flow selects.</summary>
    [ObservableProperty] private PackagerInfo? _focusedPackager;

    /// <summary>Loads the grid again, clears the search and selects the row of the definition.</summary>
    public async Task SelectDefinitionAsync(string definitionId)
    {
        await LoadPackagersCommand.ExecuteAsync(null);
        SearchText = "";
        FocusedPackager = Packagers.FirstOrDefault(p => string.Equals(p.Uid, definitionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs the Stage command on the row of the definition only; returns the status line.</summary>
    public async Task<string> StageDefinitionAsync(string definitionId)
    {
        var row = Packagers.FirstOrDefault(p => string.Equals(p.Uid, definitionId, StringComparison.OrdinalIgnoreCase));
        if (row is null) return "The new row is not in the App Packager. Press F5 on the App Packager to load it.";
        if (!StageCommand.CanExecute(null)) return "Another operation is running. Select Stage now again when it ends.";
        foreach (var p in Packagers) p.IsSelected = ReferenceEquals(p, row);
        FocusedPackager = row;
        await StageCommand.ExecuteAsync(null);
        return StatusText;
    }

    /// <summary>
    /// Analyses one dropped installer and opens the review dialog. Exposed as a command so the
    /// flow can be driven without a real drag-drop.
    /// </summary>
    [RelayCommand]
    private async Task AnalyzeDroppedInstallerAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        AddLogLine($"Drop: analysing {Path.GetFileName(path)}...");

        ApplicationPackagerEngine.Services.DroppedInstallerAnalysis analysis;
        try
        {
            analysis = await _dropAnalysisService.AnalyzeAsync(path, deepInspect: true, ct: CancellationToken.None);
        }
        catch (ApplicationPackagerEngine.Services.AddFromInstaller.AddFromInstallerRefusedException ex)
        {
            AddLogLine($"Drop refused: {ex.Message}");
            return;
        }
        catch (Exception ex)
        {
            AddLogLine($"Drop analysis failed: {ex.Message}");
            return;
        }

        AddLogLine($"  Engine: {analysis.Engine} ({analysis.InstallerType}), confidence: {analysis.Confidence}");
        if (analysis.RequiresConfirmation)
            AddLogLine("  Predicted values present. Confirm them in the review dialog before staging.");

        var panel = new DropReviewPanel { FitToWindow = true };
        panel.Load(analysis);

        var dialog = new ContentDialog
        {
            Title = "Installer drop",
            Content = panel,
            PrimaryButtonText = "Stage",
            SecondaryButtonText = "Save as definition",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = panel.IsConfirmed,
            IsSecondaryButtonEnabled = panel.IsConfirmed
        };
        panel.ConfirmationChanged += (_, _) =>
        {
            dialog.IsPrimaryButtonEnabled = panel.IsConfirmed;
            dialog.IsSecondaryButtonEnabled = panel.IsConfirmed;
        };
        var saveAsApplication = false;
        panel.SaveAsApplicationRequested += (_, _) =>
        {
            saveAsApplication = true;
            dialog.Hide(ContentDialogResult.None);
        };

        var choice = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);
        if (saveAsApplication)
        {
            await SaveDroppedInstallerAsApplicationAsync(analysis, panel);
            return;
        }
        if (choice == ContentDialogResult.None)
        {
            AddLogLine("Drop cancelled.");
            return;
        }

        var problem = panel.Validate();
        if (problem is not null)
        {
            AddLogLine($"Drop rejected: {problem}");
            _snackbarService.Show("Installer drop", problem,
                Wpf.Ui.Controls.ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return;
        }

        var overrides = panel.ReadOverrides();
        if (choice == ContentDialogResult.Secondary)
            SaveDroppedInstallerAsDefinition(analysis, overrides, panel.IsConfirmed);
        else
            await StageDroppedInstallerAsync(analysis, overrides, panel.IsConfirmed);
    }

    private async Task StageDroppedInstallerAsync(
        ApplicationPackagerEngine.Services.DroppedInstallerAnalysis analysis,
        ApplicationPackagerEngine.Services.DroppedInstallerOverrides overrides,
        bool confirmed)
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.DownloadRoot))
        {
            AddLogLine("Stage: DownloadRoot is not configured. Open Settings.");
            return;
        }

        ApplicationPackagerEngine.Models.StageManifest manifest;
        try
        {
            manifest = _dropAnalysisService.BuildStageManifest(analysis, overrides, confirmed);
        }
        catch (Exception ex)
        {
            AddLogLine($"Stage refused: {ex.Message}");
            return;
        }

        manifest.DetectedEngine = analysis.Engine;
        manifest.AdHocSource = analysis.Path;

        IsOperationRunning = true;
        StatusText = $"Staging dropped installer: {manifest.AppName}";
        AddLogLine($"Stage: {manifest.AppName} {manifest.SoftwareVersion} (dropped installer)");

        try
        {
            var result = await _stagingService.StageDroppedInstallerAsync(
                manifest, analysis.Path, settings.DownloadRoot, line => AddLogLine($"  {line}"));

            if (!result.Success)
            {
                AddLogLine($"Stage FAILED: {result.ErrorMessage}");
                StatusText = "Stage failed.";
                return;
            }

            AddLogLine($"Staged: {result.ContentPath}");
            AddDroppedPackagerRow(manifest, analysis, result.ManifestPath!);
            StatusText = $"Staged {manifest.AppName}. Select the row and Package to continue.";
        }
        catch (Exception ex)
        {
            AddLogLine($"Stage ERROR: {ex.Message}");
        }
        finally
        {
            IsOperationRunning = false;
        }
    }

    /// <summary>
    /// Adds the staged drop to the grid as a normal run row so Package uses the same flow.
    /// </summary>
    private void AddDroppedPackagerRow(
        ApplicationPackagerEngine.Models.StageManifest manifest,
        ApplicationPackagerEngine.Services.DroppedInstallerAnalysis analysis,
        string manifestPath)
    {
        var row = new PackagerInfo
        {
            Uid = $"drop-{Guid.NewGuid():N}"[..20],
            Vendor = manifest.Publisher,
            Application = manifest.AppName,
            CMName = manifest.AppName,
            Script = analysis.FileName,
            FullPath = analysis.Path,
            Description = $"{manifest.InstallerType} ({analysis.Engine})",
            StagedManifestPath = manifestPath,
            LatestVersion = manifest.SoftwareVersion,
            Status = "Staged",
            IsSelected = true
        };

        Packagers.Add(row);
        ApplyFilter();
    }

    private void SaveDroppedInstallerAsDefinition(
        ApplicationPackagerEngine.Services.DroppedInstallerAnalysis analysis,
        ApplicationPackagerEngine.Services.DroppedInstallerOverrides overrides,
        bool confirmed)
    {
        ApplicationPackagerEngine.Models.AppDefinition definition;
        try
        {
            definition = _dropAnalysisService.BuildDefinition(analysis, overrides, confirmed);
        }
        catch (Exception ex)
        {
            AddLogLine($"Save as definition refused: {ex.Message}");
            return;
        }

        var definitionsDir = ApplicationPackagerEngine.Services.AppDataPaths.UserDefinitionsRoot;
        string target;
        try
        {
            target = ApplicationPackagerEngine.Services.UserDefinitionFiles.GetPath(definitionsDir, definition.Application);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            AddLogLine($"Save as definition failed: {ex.Message}");
            return;
        }

        if (File.Exists(target))
        {
            AddLogLine($"Save as definition refused: {Path.GetFileName(target)} already exists. Existing definitions are never overwritten.");
            _snackbarService.Show("Save as definition", $"{Path.GetFileName(target)} already exists.",
                Wpf.Ui.Controls.ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
            return;
        }

        try
        {
            target = ApplicationPackagerEngine.Services.UserDefinitionFiles.SaveNew(
                definitionsDir, definition.Application, definition);
            AddLogLine($"Saved definition: {target}");
            AddLogLine("  Acquisition is unset — a dropped file carries no origin URL. Fill in the download URL and version scraper before Check Latest works.");
            _ = LoadPackagersCommand.ExecuteAsync(null);
        }
        catch (IOException) when (File.Exists(target))
        {
            AddLogLine($"Save as definition refused: {Path.GetFileName(target)} already exists. Existing definitions are never overwritten.");
        }
        catch (Exception ex)
        {
            AddLogLine($"Save as definition failed: {ex.Message}");
        }
    }

    /// <summary>Prompts for missing local installer folders; returns the rows the run keeps.</summary>
    private List<PackagerInfo> ConfirmLocalSourceFolders(List<PackagerInfo> selected)
    {
        var settings = _settingsService.Load();
        var kept = LocalSourceFolderPrompt.Confirm(selected, LoadDefinitionOrNull, settings, description =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = description };
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }, AddLogLine, out var changed);
        if (changed) _settingsService.Save(settings);
        foreach (var p in selected.Except(kept)) p.Status = "Skipped";
        return kept;
    }

    private static AppDefinition? LoadDefinitionOrNull(PackagerInfo p)
    {
        if (!p.IsJsonDefined || string.IsNullOrEmpty(p.DefinitionPath) || !File.Exists(p.DefinitionPath)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(p.DefinitionPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The Intune review step: the compatibility findings of each selected stage, in the log
    /// and, when any finding blocks or needs a decision, in a dialog. A blocked application is
    /// refused by its own publish; Cancel stops the whole run. One Click logs and continues.
    /// </summary>
    private async Task<bool> ReviewIntuneCompatibilityAsync(List<PackagerInfo> selected)
    {
        var lines = new List<string>();
        foreach (var p in selected)
        {
            var findings = _executionService.ReviewIntuneCompatibility(p);
            if (findings is null) continue;
            foreach (var f in findings)
            {
                AddLogLine($"{p.Application}: {ApplicationPackagerEngine.Services.IntuneCompatibilityFindings.LogLine(f)}");
                if (f.Severity != ApplicationPackagerEngine.Services.IntuneCompatibilityFindings.Info)
                    lines.Add($"{p.Application}: [{f.Severity}] {f.Code} - {f.Message}");
            }
        }
        if (lines.Count == 0) return true;

        var dialog = new ContentDialog
        {
            Title = "Intune review",
            Content = new TextBlock { Text = string.Join("\n\n", lines), TextWrapping = System.Windows.TextWrapping.Wrap },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await _contentDialogService.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    private void ShowNoneSelected(string operation)
    {
        _snackbarService.Show(operation, "No packagers selected.",
            Wpf.Ui.Controls.ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(2));
    }

    /// <summary>Mirrors the Intune post-step outcome into the run log and the grid's note column.</summary>
    private void LogIntuneNotes(PackagerInfo packager, PackagerExecutionResult result) =>
        PublishRunService.LogIntuneNotes(packager, result, AddLogLine);

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

    /// <summary>
    /// Clears the ConfigMgr version and the compare status of every row after a site
    /// change. The vendor version, the last check and the selection describe the vendor
    /// and the operator, not the site, and stay.
    /// </summary>
    public static void ClearSiteResults(IEnumerable<PackagerInfo> rows)
    {
        foreach (var p in rows)
        {
            p.CurrentVersion = "";
            p.Status = "Ready";
            CveRowState.Clear(p);
        }
    }

    public static int CompareVersions(string a, string b) =>
        ApplicationPackagerEngine.Services.VersionCompare.Compare(a, b);

    private void PersistVendorCheck(PackagerInfo row, string operationId, string source, string? version,
        InventoryOutcome outcome, string? detail)
    {
        var at = DateTimeOffset.UtcNow;
        _inventory.RecordVendorCheck(new InventoryVendorCheck(row.Uid, row.Application, version, source, outcome, at,
            operationId, detail));
        _inventory.CompleteOperation(operationId, outcome, at, detail);
    }

    private void PersistEnvironmentObservation(PackagerInfo row, InventoryEnvironmentIdentity environment,
        string operationId, string? version, InventoryOutcome outcome, string? detail)
    {
        var at = DateTimeOffset.UtcNow;
        _inventory.RecordEnvironmentObservation(new InventoryEnvironmentObservation(row.Uid,
            ApplicationInventoryProjection.ReferenceFor(row), environment, version, "ConfigMgrQuery", outcome, at,
            operationId, detail));
        _inventory.CompleteOperation(operationId, outcome, at, detail);
    }

}
