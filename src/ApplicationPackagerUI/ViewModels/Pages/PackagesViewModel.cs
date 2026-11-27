using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Controls;
using ApplicationPackagerUI.Helpers;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

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
    private readonly SiteStateService _siteState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSourceColumn))]
    private ObservableCollection<PackagerInfo> _packagers = [];

    /// <summary>Refreshes the icon of every row (the icon pack was installed or updated in the background).</summary>
    public void RefreshIcons()
    {
        foreach (var row in Packagers) row.RefreshIcon();
    }

    /// <summary>The Source column of the grid shows only while a row is an imported definition; it is empty for every other row.</summary>
    public bool ShowSourceColumn => Packagers is { } rows && rows.Any(p => p.SourceLabel.Length > 0);

    [ObservableProperty] private ObservableCollection<PackagerInfo> _filteredPackagers = [];
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _comment = "";
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty] private bool _showDebugColumns;
    /// <summary>The bulk selection cycle behind the header check box (same rules as the PowerShell app).</summary>
    private readonly SelectionCycle _selection = new();

    /// <summary>Header check box: checked = all visible rows, indeterminate = updates only, unchecked = none.</summary>
    public bool? HeaderSelection => _selection.HeaderCheck;
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
    [ObservableProperty] private bool _isPublishAllAvailable;
    [ObservableProperty] private string _publishAllToolTip = "";
    private string _publishAllTarget = "";
    [ObservableProperty] private bool _isWsusUpdatesAvailable;
    [ObservableProperty] private string _wsusUpdatesToolTip = "";
    /// <summary>The Intune note column shows only while Intune is a system in use.</summary>
    [ObservableProperty] private bool _showIntuneColumn;
    /// <summary>The connection mark before the status line shows only while ConfigMgr is a system in use.</summary>
    [ObservableProperty] private bool _showConfigMgrIndicator = true;
    private bool _skipMecmPreflight;

    public IAsyncRelayCommand LoadPackagersCommand { get; }
    public IAsyncRelayCommand CheckLatestCommand { get; }
    public IAsyncRelayCommand CheckMecmCommand { get; }
    public IAsyncRelayCommand StageCommand { get; }
    public IAsyncRelayCommand PublishAllCommand { get; }
    public IRelayCommand WsusUpdatesCommand { get; }
    public IAsyncRelayCommand OneClickCommand { get; }

    /// <summary>Opens the window that imports vendor definitions from a URL; the page shows it and reloads the grid after an import.</summary>
    public IRelayCommand ImportFromUrlCommand { get; }

    public event EventHandler? ImportFromUrlRequested;

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
        IAddFileRoute addFileRoute,
        SiteStateService siteState)
    {
        // Made here, so the log belongs to the UI thread.
        _logLines = new OperationLog();
        _siteState = siteState;
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
        PublishAllCommand = new AsyncRelayCommand(PublishAllAsync, () => CanRunOperation() && IsPublishAllAvailable);
        WsusUpdatesCommand = new RelayCommand(OpenWsusUpdates, () => CanRunOperation() && IsWsusUpdatesAvailable);
        OneClickCommand = new AsyncRelayCommand(OneClickAsync, CanRunOperation);
        ImportFromUrlCommand = new RelayCommand(() => ImportFromUrlRequested?.Invoke(this, EventArgs.Empty), CanRunOperation);
        // Before the checklist stores anything: the choice of columns depends on whether this is a new install.
        MigrateColumns();
        _isDetailPaneOpen = _settingsService.Load().PackagesDetailPaneOpen;
        _detailPaneWidth = _settingsService.Load().PackagesDetailPaneWidth;
        InitializeDiscovery(discovery, discoveryPrompt);
        Checklist = new SetupChecklistViewModel(_settingsService, _scanStore, () => _siteState.ConfigMgr, () => IsOperationRunning,
            RunChecklistDiscoveryAsync, TestMecmConnectionAsync);
        _siteState.Changed += () => OnUiThread(() => Checklist?.Refresh());

        RefreshPublishActionState();

        _ = InitializeAsync();
    }

    /// <summary>The first-start checklist of this page. Null only in a view model that a test builds without its constructor.</summary>
    public SetupChecklistViewModel? Checklist { get; set; }

    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// The Setup window closed. A saved setup can name another site, so the connection is tested again. Skip with "Don't show this again"
    /// also stops the checklist.
    /// </summary>
    public void OnSetupClosed(bool saved, bool dontShowAgain)
    {
        if (dontShowAgain) Checklist?.Dismiss();
        else Checklist?.Refresh();
        if (saved) _ = TestMecmConnectionAsync();
    }

    /// <summary>The settings the publish buttons depend on, as stored now.</summary>
    public static PublishReadiness ReadinessOf(AppSettings settings) => PublishRunService.ReadinessOf(settings);

    /// <summary>
    /// Applies Systems in Use and the stored settings to the publish buttons: a button
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
        ShowConfigMgrIndicator = readiness.Systems.ConfigMgr;
        IsCheckMecmAvailable = state.CheckMecmEnabled;
        CheckMecmToolTip = state.CheckMecmToolTip;
        IsPublishConfigMgrAvailable = state.MecmEnabled;
        PublishConfigMgrToolTip = state.MecmToolTip;
        IsPublishIntuneAvailable = state.IntuneEnabled;
        PublishIntuneToolTip = state.IntuneToolTip;
        IsPublishWsusAvailable = state.WsusEnabled;
        PublishWsusToolTip = state.WsusToolTip;
        (IsPublishAllAvailable, PublishAllToolTip, _publishAllTarget) = PublishAllState.For(readiness);
        DetailPublishingText = PackagesDetail.PublishingText(readiness.Systems, readiness.SiteCode);
        PublishScopeText = "Publish All: " + ScopeText.For(ScopeText.SetOf(readiness.Systems), ScopeSource.SystemsInUse, readiness.SiteCode);
        IsWsusUpdatesAvailable = state.WsusUpdatesEnabled;
        WsusUpdatesToolTip = state.WsusUpdatesToolTip;
        _skipMecmPreflight = state.SkipMecmPreflight;

        NotifyActionCommands();
        Checklist?.Refresh();
    }

    /// <summary>
    /// The active site changed, or its settings changed on disk. Everything this page holds about the site is dropped, and every text,
    /// button and tooltip that depends on the site is read again, before the list loads again.
    /// </summary>
    public void FollowSiteChange()
    {
        ClearSiteResults(Packagers ?? []);
        StatusText = "";
        MecmConnectionStatus = "Unknown";
        RefreshPublishActionState();
    }

    private void NotifyActionCommands()
    {
        CancelOperationCommand.NotifyCanExecuteChanged();
        CheckLatestCommand.NotifyCanExecuteChanged();
        CheckMecmCommand.NotifyCanExecuteChanged();
        ExportCheckResultsCommand.NotifyCanExecuteChanged();
        StageCommand.NotifyCanExecuteChanged();
        PublishAllCommand.NotifyCanExecuteChanged();
        WsusUpdatesCommand.NotifyCanExecuteChanged();
        OneClickCommand.NotifyCanExecuteChanged();
        ImportFromUrlCommand.NotifyCanExecuteChanged();
        DiscoverCommand.NotifyCanExecuteChanged();
        DiscoverAllCommand.NotifyCanExecuteChanged();
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

    partial void OnIsOperationRunningChanged(bool value)
    {
        if (value) _cancelRequested = false;
        ApplicationPackagerEngine.Services.SiteRunGuard.Set(this, value ? "a check, Stage or publish run is active" : null);
        NotifyActionCommands();
        Checklist?.RefreshBusy();
    }

    /// <summary>The connection test of this page goes to the shared state, so the strip on every page shows it.</summary>
    partial void OnMecmConnectionStatusChanged(string value) => _siteState.SetConfigMgrConnection(value switch
    {
        "Connected" => ConfigMgrConnectionState.Connected,
        "Failed" => ConfigMgrConnectionState.Failed,
        "Testing..." => ConfigMgrConnectionState.Testing,
        _ => ConfigMgrConnectionState.Unknown
    });

    private IEnumerable<PackagerInfo> SelectedPackagers =>
        Packagers.Where(p => p.IsSelected && p.IsRunnable);

    /// <summary>The checked rows. A run acts on all of them, hidden by the search or the tab or not.</summary>
    public int SelectedCount => SelectionCounts.Of(Packagers ?? [], FilteredPackagers ?? []).Selected;

    /// <summary>The checked rows that the current search or tab hides.</summary>
    public int HiddenSelectedCount => SelectionCounts.Of(Packagers ?? [], FilteredPackagers ?? []).Hidden;

    /// <summary>The row counts of the status tabs, over every row.</summary>
    public PackagesTabCounts TabCounts => PackagesTab.Count(Packagers ?? []);

    public string TabAllText => $"All {TabCounts.All}";
    public string TabUpdateText => $"Updates available {TabCounts.Update}";
    public string TabNotFoundText => $"Not found in ConfigMgr {TabCounts.NotFound}";
    public string TabNotCheckedText => $"Not checked {TabCounts.NotChecked}";

    /// <summary>"12 selected" and, when the view hides some of them, "12 selected, 4 outside this view". A run acts on all of them.</summary>
    public string SelectionText
    {
        get
        {
            var (selected, hidden) = SelectionCounts.Of(Packagers ?? [], FilteredPackagers ?? []);
            return hidden > 0 ? $"{selected} selected, {hidden} outside this view" : $"{selected} selected";
        }
    }

    /// <summary>"Publish All scope: ConfigMgr and WSUS (Settings, Systems in Use)".</summary>
    [ObservableProperty] private string _publishScopeText = "";

    /// <summary>The Publishing line of the detail pane: the systems that are on and off.</summary>
    [ObservableProperty] private string _detailPublishingText = "";

    /// <summary>The columns that the Columns menu shows, as saved.</summary>
    public IReadOnlyList<string> ShownColumns => PackagesColumns.Shown(_settingsService?.Load().PackagesShownColumns);

    /// <summary>Shows or hides one column and saves the choice.</summary>
    public void SetColumnVisible(string header, bool visible)
    {
        var settings = _settingsService.Load();
        PackagesColumns.Store(settings, PackagesColumns.With(settings.PackagesShownColumns, header, visible));
        _settingsService.Save(settings);
    }

    /// <summary>The Compact view entry: the columns of a new install. The order, the widths and the sort stay.</summary>
    public void ShowCompactColumns()
    {
        var settings = _settingsService.Load();
        PackagesColumns.Store(settings, PackagesColumns.Compact);
        _settingsService.Save(settings);
    }

    /// <summary>The first start with this setting stores which columns this install shows.</summary>
    private void MigrateColumns()
    {
        var settings = _settingsService.Load();
        // An unreadable file gives defaults, which look like a new install; nothing is stored over it.
        if (_settingsService.IsFileUnreadable) return;
        if (!PackagesColumns.Migrate(settings, newInstall: !_settingsService.FileExistedAtStart)) return;
        try { _settingsService.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The saved order, widths and sort of the grid; null when the operator changed none.</summary>
    public PackagesColumnLayout? ColumnLayout => PackagesColumns.ReadLayout(_settingsService?.Load().PackagesGridLayout);

    public void SaveColumnLayout(PackagesColumnLayout layout)
    {
        if (_settingsService is null) return;
        var settings = _settingsService.Load();
        settings.PackagesGridLayout = PackagesColumns.StoredLayout(layout);
        _settingsService.Save(settings);
    }

    /// <summary>The default of a new install: the compact columns, in the order and with the widths of the page, with no sort.</summary>
    public void ResetColumns()
    {
        var settings = _settingsService.Load();
        PackagesColumns.Store(settings, PackagesColumns.Compact);
        settings.PackagesGridLayout = null;
        _settingsService.Save(settings);
    }

    public bool HasHiddenSelection => HiddenSelectedCount > 0;
    public bool HasSelection => SelectedCount > 0;

    /// <summary>The tab of the grid. A change filters the grid in place: the sort and the scroll position stay.</summary>
    [ObservableProperty] private PackagesTabChoice _statusTab = PackagesTabChoice.All;

    partial void OnStatusTabChanged(PackagesTabChoice value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private bool _countsPending;

    /// <summary>
    /// A row changed its check, status or ConfigMgr version. The counts and the tab are refreshed once for a burst of changes (a Check run
    /// changes every row), on the next dispatcher turn.
    /// </summary>
    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PackagerInfo.Status) or nameof(PackagerInfo.CurrentVersion) or nameof(PackagerInfo.IsSelected))) return;
        if (_countsPending) return;
        _countsPending = true;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) RefreshCounts();
        else dispatcher.BeginInvoke(RefreshCounts, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Raises the change of every count and keeps the grid on its tab: a row that left the tab leaves the grid without a reload.</summary>
    public void RefreshCounts()
    {
        _countsPending = false;
        if (StatusTab != PackagesTabChoice.All) ApplyFilter(announce: false);
        else RaiseCountChanges();
    }

    private void RaiseCountChanges()
    {
        OnPropertyChanged(nameof(TabCounts));
        OnPropertyChanged(nameof(TabAllText));
        OnPropertyChanged(nameof(TabUpdateText));
        OnPropertyChanged(nameof(TabNotFoundText));
        OnPropertyChanged(nameof(TabNotCheckedText));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HiddenSelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasHiddenSelection));
        OnPropertyChanged(nameof(HasSelection));
    }

    private ObservableCollection<PackagerInfo>? _watchedRows;
    private readonly HashSet<PackagerInfo> _followedRows = [];

    /// <summary>Follows the current list and its rows for the counts, rows added later too; the old list and its rows are let go.</summary>
    private void WatchRows()
    {
        if (_watchedRows is not null) _watchedRows.CollectionChanged -= OnRowsChanged;
        _watchedRows = Packagers;
        _watchedRows.CollectionChanged += OnRowsChanged;
        FollowRows();
    }

    private void OnRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        FollowRows();
        RefreshCounts();
    }

    private void FollowRows()
    {
        var now = new HashSet<PackagerInfo>(_watchedRows!);
        foreach (var row in _followedRows.Where(r => !now.Contains(r)).ToList())
        {
            row.PropertyChanged -= OnRowChanged;
            _followedRows.Remove(row);
        }
        foreach (var row in now.Where(r => _followedRows.Add(r))) row.PropertyChanged += OnRowChanged;
    }

    /// <summary>Show only the checked rows (the "Show selected" link of the selection line).</summary>
    [RelayCommand]
    private void ShowSelected()
    {
        // The search would hide some of the checked rows again, so it is cleared.
        SearchText = "";
        StatusTab = PackagesTabChoice.Selected;
    }

    /// <summary>Uncheck every row, hidden ones included.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Packagers) row.IsSelected = false;
        if (StatusTab == PackagesTabChoice.Selected) StatusTab = PackagesTabChoice.All;
        OnPropertyChanged(nameof(HeaderSelection));
    }

    /// <param name="replace">True after a load: the grid gets a new list. Otherwise the list changes in place, so the sort and the scroll stay.</param>
    /// <param name="announce">
    /// True when the operator changed the search or the tab, or after a load: the status line says what the grid shows. A refresh after a row
    /// changed does not write the status line, and during a run the line stays the progress of the run.
    /// </param>
    private void ApplyFilter(bool replace = false, bool announce = true)
    {
        var term = SearchText;
        var tab = StatusTab;
        var wanted = Packagers
            .Where(p => (string.IsNullOrWhiteSpace(term) || PackagerSearch.Matches(p, term)) && PackagesTab.Matches(tab, p))
            .ToList();
        if (replace || FilteredPackagers is null) FilteredPackagers = new ObservableCollection<PackagerInfo>(wanted);
        else ListSync.Sync(FilteredPackagers, wanted);
        if (announce && !IsOperationRunning)
            StatusText = string.IsNullOrWhiteSpace(term) && tab == PackagesTabChoice.All
            ? $"Loaded {Packagers.Count} packager(s). Ready."
            : $"Showing {FilteredPackagers.Count} of {Packagers.Count} packager(s).";
        RaiseCountChanges();
    }

    /// <summary>One click on the header check box: none, every visible row, updates only, none.</summary>
    [RelayCommand]
    private void CycleSelection()
    {
        _selection.Advance(FilteredPackagers, AddLogLine);
        OnPropertyChanged(nameof(HeaderSelection));
    }

    /// <summary>Ctrl+A: every visible row, or clear when every visible row is already the bulk selection.</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        var visible = FilteredPackagers.Where(p => p.IsRunnable).ToList();
        if (visible.Count > 0 && visible.All(p => p.IsSelected)) _selection.Clear(visible, AddLogLine);
        else _selection.SelectAllVisible(FilteredPackagers, AddLogLine);
        OnPropertyChanged(nameof(HeaderSelection));
    }

    [RelayCommand]
    private void SelectUpdateAvailable()
    {
        _selection.SelectUpdates(FilteredPackagers, AddLogLine);
        OnPropertyChanged(nameof(HeaderSelection));
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

    /// <summary>The rows of the last Check ConfigMgr run, as the export file carries them.</summary>
    private IReadOnlyList<CheckConfigMgrResultRow> _lastCheckResult = [];

    private IReadOnlyList<CheckConfigMgrResultRow> BuildCheckResultRows(IEnumerable<PackagerInfo> rows, DateTime checkedAtUtc)
    {
        var result = new List<CheckConfigMgrResultRow>();
        foreach (var p in rows)
        {
            var status = CheckConfigMgrResultExport.MapStatus(p.Status);
            if (status is null) continue;
            var definition = LoadDefinitionOrNull(p);
            var arp = string.Join(" | ", ArpWildcard.Split(definition?.ArpName));
            var architecture = string.IsNullOrWhiteSpace(definition?.Architecture) ? "x64" : definition!.Architecture.Trim().ToLowerInvariant();
            result.Add(new CheckConfigMgrResultRow(p.Application, p.Vendor, p.CMName, arp, architecture,
                p.CurrentVersion ?? "", p.LatestVersion ?? "", status, CheckConfigMgrResultExport.SourceName, checkedAtUtc));
        }
        return result;
    }

    /// <summary>Writes the fixed result file after a run. A write failure goes to the log and never fails the check.</summary>
    private void WriteCheckResultFile(IEnumerable<PackagerInfo> checkedRows, DateTime checkedAtUtc)
    {
        _lastCheckResult = BuildCheckResultRows(checkedRows, checkedAtUtc);
        ExportCheckResultsCommand.NotifyCanExecuteChanged();
        try
        {
            var path = CheckConfigMgrResultExport.WriteDefault(_lastCheckResult);
            AddLogLine($"Check ConfigMgr result written to: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLogLine($"Check ConfigMgr result file not written: {ex.Message}");
        }
    }

    private bool CanExportCheckResults() => _lastCheckResult is { Count: > 0 } && !IsOperationRunning;

    /// <summary>Saves the result of the last Check ConfigMgr run; the file type in the dialog chooses the format and the filter.</summary>
    [RelayCommand(CanExecute = nameof(CanExportCheckResults))]
    private void ExportCheckResults()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Check ConfigMgr Results",
            Filter = "CSV, all checked applications (*.csv)|*.csv|CSV, only updates available (*.csv)|*.csv|JSON, all checked applications (*.json)|*.json|JSON, only updates available (*.json)|*.json",
            FileName = "CheckConfigMgrResult"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var rows = CheckConfigMgrResultExport.Filter(_lastCheckResult, onlyUpdatesAvailable: dialog.FilterIndex is 2 or 4);
            CheckConfigMgrResultExport.Write(dialog.FileName, rows);
            AddLogLine($"Exported {rows.Count} Check ConfigMgr result rows to: {dialog.FileName}");
            _snackbarService.Show("Export", $"Exported {rows.Count} rows.",
                Wpf.Ui.Controls.ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLogLine($"Export failed: {ex.Message}");
        }
    }

    private bool _cancelRequested;

    private bool CanCancelOperation() => IsOperationRunning && !_cancelRequested;

    /// <summary>
    /// Asks the run to stop: after the product in flight, whose download or child process the
    /// token ends. A second click finds the token already cancelled and does nothing.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation()
    {
        var cts = _operationCts;
        if (_cancelRequested || cts is null || cts.IsCancellationRequested) return;
        _cancelRequested = true;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return; }
        AddLogLine("Cancel requested: stopping the product in flight.");
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The window is closing: stops the run and ends the child processes it started, by process id,
    /// so none is left writing after the app is gone.
    /// </summary>
    public void StopRun()
    {
        try { _operationCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        ApplicationPackagerEngine.Services.RunChildProcesses.KillAll();
    }

    /// <summary>The end-of-run status line; a cancelled run also writes it to the log.</summary>
    private string RunEndLine(PackagingRunTally tally, string label, int total, string successVerb, string? checkName = null)
    {
        // A cancelled check names itself ("Check ConfigMgr cancelled: N of M products checked."); any other run says "Cancelled: N of M products done."
        var line = tally.Cancelled && checkName is not null ? tally.CheckCancelledLine(checkName, total) : tally.Summarize(label, total, successVerb);
        if (tally.Cancelled) AddLogLine(line);
        return line;
    }

    private static readonly string LogDir = Path.Combine(ApplicationPackagerEngine.Services.AppDataPaths.LegacyRoot, "Logs");

    private static readonly string SessionLogPath = Path.Combine(
        LogDir, $"packr-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    // Every session also appends to this one rolling log beside the per-session files.
    private static readonly string PersistentLogPath = Path.Combine(ApplicationPackagerEngine.Services.AppDataPaths.LegacyRoot, "packr.log");

    private static bool _logInitialized;

    private OperationLog? _logLines;

    /// <summary>The newest log lines, for the Logs window.</summary>
    public OperationLog LogLines => _logLines ??= new OperationLog();

    /// <summary>The newest log line, which the page shows under the grid.</summary>
    public string LastLogLine => LogLines.Last;

    /// <summary>The kept lines as one text. A set replaces them.</summary>
    public string LogText
    {
        get => LogLines.Text;
        set
        {
            LogLines.Clear();
            foreach (var line in value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)) LogLines.Append(line);
            OnPropertyChanged(nameof(LastLogLine));
        }
    }

    /// <summary>The file that holds every line of this session.</summary>
    public static string SessionLogFile => SessionLogPath;

    /// <summary>Writes one line to the operation log for work that runs outside this page.</summary>
    public void AppendLog(string message) => AddLogLine(message);

    private void AddLogLine(string message)
    {
        // A download reports its progress on a pool thread. The bound log and the file writes stay on the thread of the log.
        if (LogLines.OnAnotherThread)
        {
            LogLines.Owner!.BeginInvoke(() => AddLogLine(message));
            return;
        }
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        LogLines.Append(line);
        OnPropertyChanged(nameof(LastLogLine));

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
        var gone = SelectionKeeper.Restore(Packagers ?? [], results);
        if (results.Count == 0)
        {
            Packagers = [];
            WatchRows();
            SearchText = "";
            ApplyFilter(replace: true);
            StatusText = load.StatusText ?? "";
            if (load.LogLine is not null) AddLogLine(load.LogLine);
            if (gone.Count > 0) AddLogLine(SelectionKeeper.GoneLine(gone));
            return;
        }

        var settings = _settingsService.Load();
        foreach (var p in results)
        {
            var vendor = _inventoryProjection.Vendor(p);
            p.LastChecked = vendor?.LastSucceededAt?.LocalDateTime;
            p.LatestVersion = vendor?.LatestVersion ?? "";
            var observed = _inventoryProjection.Environment(p, settings);
            p.CurrentVersion = observed?.LatestObservedVersion ?? "";
            if (!string.IsNullOrEmpty(vendor?.LatestVersion)) p.Status = "Cached";
            p.SiteCheckedAt = observed?.LatestObservedAt;
            // The last completed ConfigMgr check found no application: the tab count survives a restart.
            if (CveRowState.IsStoredNotFound(observed)) p.Status = PackagesTab.NotFoundStatus;
            ApplyStoredCveState(p, settings);
        }

        Packagers = new ObservableCollection<PackagerInfo>(results);
        WatchRows();
        SearchText = "";
        ApplyFilter(replace: true);
        if (gone.Count > 0)
        {
            AddLogLine(SelectionKeeper.GoneLine(gone));
            StatusText = SelectionKeeper.GoneLine(gone);
        }

        Checklist?.Refresh();
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
            AddLogLine("ConfigMgr: pre-flight version query skipped; ConfigMgr is not in use (Settings, Systems in Use).");
            return;
        }

        // No test of an example site code, so a new user gets no error for a site that was never entered.
        if (string.IsNullOrWhiteSpace(settings.SiteCode) || SetupChecklistRules.IsExampleSite(settings))
        {
            MecmConnectionStatus = "Not configured";
            AddLogLine("ConfigMgr: Site code not configured. Open Settings.");
            return;
        }

        MecmConnectionStatus = "Testing...";
        AddLogLine($"ConfigMgr: Testing connection to site {settings.SiteCode}...");
        var asked = SiteKeyOf(settings);
        bool Switched() => SiteKeyOf(_settingsService.Load()) != asked;

        try
        {
            // A query for an application that does not exist proves the connection when it returns, NotFound included.
            var result = await _mecmQueryService.QueryVersionAsync("__CONNECTION_TEST__", CancellationToken.None);
            if (Switched()) return;
            MecmConnectionStatus = "Connected";
            AddLogLine($"ConfigMgr: Connected to site {settings.SiteCode}");
        }
        catch (Exception ex)
        {
            if (Switched()) return;
            MecmConnectionStatus = "Failed";
            AddLogLine($"ConfigMgr: Connection failed — {ex.Message}");
            _snackbarService.Show("ConfigMgr Connection",
                $"Could not connect to site {settings.SiteCode}. Check Settings and ensure the Configuration Manager console is available.",
                Wpf.Ui.Controls.ControlAppearance.Danger,
                new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(5));
        }
    }

    private async Task CheckLatestAsync()
    {
        var selected = SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Check Latest"); return; }

        var tally = await RunCheckLatestOnList(selected, "Checking latest versions...", "Latest check");
        // One line with the count of the products that need an update. Check Latest selects nothing, so the line has no selection count.
        if (!tally.Cancelled) AddLogLine(CheckResultSummary.Line("Check Latest", selected.Select(p => p.Status).ToList(), null));
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
                StatusText = ApplicationPackagerEngine.Models.RunProgress.Running("Check Latest", ProgressCurrent, ProgressTotal, p.Application);
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
            StatusText = RunEndLine(tally, logPrefix, ProgressTotal, "checked", "Check Latest");
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
        _ = await RunCheckMecmAsync(selected, lookupCvesAfter: true);
    }

    /// <summary>Queries ConfigMgr for the rows. Check CVEs follows when the setting asks for it and <paramref name="lookupCvesAfter"/> is true.</summary>
    private async Task<bool> RunCheckMecmAsync(List<PackagerInfo> selected, bool lookupCvesAfter)
    {
        var settings = _settingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.SiteCode))
        {
            _snackbarService.Show("Check ConfigMgr", "SiteCode is required. Open Settings.",
                Wpf.Ui.Controls.ControlAppearance.Danger, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return true;
        }
        var environment = InventoryEnvironmentIdentity.ConfigMgr(settings.SiteCode, settings.ProviderMachine);

        IsOperationRunning = true;
        _operationCts = new CancellationTokenSource();
        StatusText = ApplicationPackagerEngine.Models.RunProgress.Starting("Check ConfigMgr", selected.Count);
        var tally = new PackagingRunTally();

        try
        {
            foreach (var p in selected)
            {
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }

                StatusText = ApplicationPackagerEngine.Models.RunProgress.Running("Check ConfigMgr", selected.IndexOf(p) + 1, selected.Count, p.Application);
                p.Status = "Querying ConfigMgr...";
                AddLogLine($"ConfigMgr: {p.Application}");
                var operationId = Guid.NewGuid().ToString("N");
                var startedAt = DateTimeOffset.UtcNow;

                try
                {
                    _inventory.BeginOperation(new InventoryOperation(operationId, "CheckEnvironment", p.Uid,
                        ApplicationInventoryProjection.ReferenceFor(p), environment.Key, startedAt));
                    var result = await _mecmQueryService.QueryVersionAsync(p.CMName, _operationCts.Token, p.ArpName);

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
                        if (!string.IsNullOrEmpty(result.MatchNote)) AddLogLine($"  Match: {result.MatchNote}");
                    }
                    else
                    {
                        p.CurrentVersion = "";
                        p.SiteCheckedAt = DateTimeOffset.Now;
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
            StatusText = RunEndLine(tally, "ConfigMgr query", selected.Count, "checked", "Check ConfigMgr");
            if (!tally.Cancelled) AddLogLine(CheckResultSummary.Line("Check ConfigMgr", selected.Select(p => p.Status).ToList(), Packagers.Count(p => p.IsSelected)));
            WriteCheckResultFile(selected, DateTime.UtcNow);
        }
        finally
        {
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }

        if (lookupCvesAfter && settings.Vulnerability is { LookupAfterCheckCm: true } && !tally.Cancelled)
            await RunCveCheckAsync(selected, force: false, "Checking CVEs for the queried applications...");
        return !tally.Cancelled;
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
        if (package) await PackageAsync([row], snapshot);
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
        string? stagingBlock = null;

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }

                var p = selected[i];
                ProgressCurrent = i + 1;
                StatusText = ApplicationPackagerEngine.Models.RunProgress.Running("Staging", ProgressCurrent, ProgressTotal, p.Application);
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
                        _stageFailures[p] = result.StdErr;
                        if (StagingRootGuard.IsBlockedText(result.StdErr)) stagingBlock = result.StdErr;
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
                    _stageFailures[p] = ex.Message;
                    AddLogLine($"ERROR: {ex.Message}");
                }
                catch (Exception ex)
                {
                    p.Status = "Stage error";
                    _stageFailures[p] = ex.Message;
                    if (StagingRootGuard.IsBlockedText(ex.Message)) stagingBlock = ex.Message;
                    tally.RecordFailure();
                    AddLogLine($"ERROR: {ex.Message}");
                }
            }

            tally.Finish(_operationCts.Token);
            StatusText = RunEndLine(tally, "Stage", ProgressTotal, "staged");
            // A staging folder that other accounts can write to blocks the stage; the run status says why, in the words of Settings.
            if (stagingBlock is not null) StatusText += " " + stagingBlock;
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
    /// Publishes the selection, or the given rows, to the systems in use: the publish review first, then the run.
    /// </summary>
    private async Task PackageAsync(IReadOnlyList<PackagerInfo>? rows, RunSnapshot? snapshot)
    {
        // The review and the prompts await the operator; the flag is raised before them
        // so no other operation starts while they are open.
        if (IsOperationRunning)
        {
            AddLogLine("Package skipped: another operation is running.");
            return;
        }
        PublishStarting?.Invoke(this, EventArgs.Empty);
        IsOperationRunning = true;
        try
        {
            // The Workbench starts this run while it closes. The run yields first, so the review window opens after the Workbench is gone.
            if (rows is not null) await Task.Yield();
            await PackageCoreAsync(rows, snapshot);
        }
        finally
        {
            IsOperationRunning = false;
            var review = _reviewAfterPublish;
            _reviewAfterPublish = null;
            PublishEnded?.Invoke(this, new PublishEndedEventArgs(review));
        }
    }

    /// <summary>A publish is about to open its review; the page notes the focused row and the scroll position.</summary>
    public event EventHandler? PublishStarting;

    /// <summary>The review closed with Back, or the run and its results ended; the page restores its place and the focus.</summary>
    public event EventHandler<PublishEndedEventArgs>? PublishEnded;

    private static string SiteKeyOf(AppSettings settings) =>
        ApplicationPackagerEngine.Services.SiteIdentity.Key(settings.SiteCode, settings.ProviderMachine);

    private async Task PackageCoreAsync(IReadOnlyList<PackagerInfo>? rows, RunSnapshot? snapshot)
    {
        // From the first read to the end of the results the site cannot change: the reads before the review leave the window usable.
        var hold = new object();
        ApplicationPackagerEngine.Services.SiteRunGuard.Set(hold, "a publish is being reviewed or run");
        try { await PublishHeldAsync(rows, snapshot); }
        finally { ApplicationPackagerEngine.Services.SiteRunGuard.Set(hold, null); }
    }

    private async Task PublishHeldAsync(IReadOnlyList<PackagerInfo>? rows, RunSnapshot? snapshot)
    {
        var settings = _settingsService.Load();

        var selected = rows?.ToList() ?? SelectedPackagers.ToList();
        if (selected.Count == 0) { ShowNoneSelected("Package"); return; }

        // A Workbench run publishes one build: the sealed build of its snapshot, or the stage at the manifest path of a bring-your-own row.
        // The review and the results name the version of that build, and the build is read again before each write.
        string? sealedVersion = null;
        string? pinnedBuildId = null;
        Func<(string Version, string? Failure)>? readBuild = null;
        // True when the build of the run is gone; the log then says why, and the run publishes nothing more.
        bool BuildIsGone()
        {
            if (readBuild?.Invoke().Failure is not { } failure) return false;
            AddLogLine("Publish cancelled. " + failure);
            StatusText = "Publish cancelled. Nothing was published.";
            return true;
        }
        void ThrowIfBuildIsGone()
        {
            if (readBuild?.Invoke().Failure is { } failure) throw new InvalidOperationException(failure);
        }
        if (rows is not null && selected is [var workbenchRow])
        {
            if (workbenchRow.StagedManifestPath is { } stagedPath)
            {
                pinnedBuildId = WorkbenchRunSnapshots.StageAt(stagedPath, snapshot?.BuildId).BuildId;
                var expected = snapshot?.BuildId ?? pinnedBuildId;
                readBuild = () =>
                {
                    var stage = WorkbenchRunSnapshots.StageAt(stagedPath, expected);
                    return (stage.Version, stage.Failure);
                };
            }
            else if (snapshot is not null)
            {
                readBuild = () => WorkbenchRunSnapshots.SealedVersion(snapshot);
            }
            if (BuildIsGone()) return;
            sealedVersion = readBuild?.Invoke().Version;
        }

        // The review lists every checked application, the ones that the view hides too, and takes the comment of the run. It starts with the
        // comment of the last confirmed review.
        // The rows of a Workbench run are named by the caller, so none of them is hidden.
        var shown = new HashSet<PackagerInfo>(rows ?? FilteredPackagers);
        var plan = BuildPlan(selected.Select(p => PlanApp(p, settings, !shown.Contains(p),
                string.IsNullOrEmpty(p.CurrentVersion) ? ConfigMgrFact.None : ConfigMgrFact.Found(p.CurrentVersion), sealedVersion)).ToList(), settings);
        // A plan with nothing to write cannot be confirmed, so no read would follow it. A skip that rests on the last ConfigMgr check is
        // read from the site before such a plan shows.
        if (PublishPlanner.NeedsSiteRead(plan))
        {
            if (await RereadPlanAsync(plan, selected, readBuild) is not { } read)
            {
                StatusText = "Publish cancelled. Nothing was published.";
                return;
            }
            plan = read;
        }
        var confirmed = await PublishReviewGate.ConfirmAsync(plan, Comment, _publishDialogs.ShowPublishReview,
            shownPlan => RereadPlanAsync(shownPlan, selected, readBuild), AddLogLine,
            comment => Comment = comment);
        if (confirmed is null)
        {
            StatusText = "Publish cancelled. Nothing was published.";
            return;
        }
        var confirmedPlan = confirmed.Plan;
        // The run writes to the systems that the confirmed plan lists, with the settings that the last read used.
        settings = _settingsService.Load();
        var target = new DestinationSet(
            confirmedPlan.Systems.Contains(OneClickDestinations.ConfigMgr),
            confirmedPlan.Systems.Contains(OneClickDestinations.Wsus),
            confirmedPlan.Systems.Contains(OneClickDestinations.Intune)).ToTargetName();
        var wsusOnly = target == PublishRunTargets.WsusOnly;
        if (DeploymentTargets.PublishesToWsus(target) && !ConfirmWsusPrerequisites(settings, "Publish")) return;

        selected = ConfirmLocalSourceFolders(selected);
        if (selected.Count == 0)
        {
            StatusText = "Publish cancelled. Nothing was published.";
            return;
        }
        // What this publish does and does not do, before anything starts.
        foreach (var sentence in ApplicationPackagerEngine.Services.PublishPreflight.Lines(target))
            AddLogLine("Before publish: " + sentence);
        // The Intune review covers the applications that this run publishes to Intune.
        var toIntune = selected.Where(p => confirmedPlan.Rows.First(r => r.App.Uid == p.Uid).Cells
            .Any(c => c.Destination == OneClickDestinations.Intune && c.Kind != PublishCellKind.Skip)).ToList();
        if (toIntune.Count > 0 && !await ReviewIntuneCompatibilityAsync(toIntune, target, snapshot, pinnedBuildId))
        {
            AddLogLine("Intune review: run cancelled.");
            StatusText = "Publish cancelled. Nothing was published.";
            return;
        }
        // The dialogs above wait for the operator, so the build is read once more before the first write.
        if (BuildIsGone()) return;

        IsOperationRunning = true;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = selected.Count;
        _operationCts = new CancellationTokenSource();
        // One confirmation for the run: "Remember" lasts until this run ends.
        _executionService.SupersedenceConfirmation = new ApplicationPackagerEngine.Services.Supersedence.SupersedenceConfirmer(
            Views.Windows.SupersedenceConfirmDialog.AskAsync, () => _operationCts?.Cancel());
        StatusText = wsusOnly ? "Publishing selected applications to WSUS..." : "Packaging selected applications...";

        // Per-run only: an apply-to-all answer dies with the loop and is never persisted.
        ConflictAnswer? runConflictAnswer = null;


        var tally = new ApplicationPackagerEngine.Models.PackagingRunTally();
        var rowResults = new List<(string Uid, string Application, PublishRowResult Result)>();

        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (_operationCts.IsCancellationRequested) { tally.MarkCancelled(); break; }

                var p = selected[i];
                ProgressCurrent = i + 1;
                StatusText = wsusOnly
                    ? ApplicationPackagerEngine.Models.RunProgress.Running("Publishing to WSUS", ProgressCurrent, ProgressTotal, p.Application)
                    : ApplicationPackagerEngine.Models.RunProgress.Running("Packaging", ProgressCurrent, ProgressTotal, p.Application);
                p.Status = wsusOnly ? "Publishing to WSUS..." : "Packaging...";
                AddLogLine($"{(wsusOnly ? "Publish to WSUS" : "Package")}: {p.Application} ({p.Script})");

                try
                {
                    var token = _operationCts.Token;
                    // The version that the confirmed review showed for this application. No other version is published.
                    var reviewed = confirmedPlan.Rows.First(r => r.App.Uid == p.Uid).App.Version;
                    var conflictStop = false;
                    var conflictKept = false;

                    // ConfigMgr and Intune run through one package call. An existing application asks here.
                    async Task<PackagerExecutionResult> PackageRowAsync(string packageTarget)
                    {
                        ThrowIfBuildIsGone();
                        var packaged = await _executionService.PackageAsync(
                            p, Comment, line => AddLogLine($"  {line}"), token,
                            targetOverride: packageTarget, snapshot: snapshot, reviewedVersion: reviewed);
                        if (packaged.Conflict is null) return packaged;

                        var answer = runConflictAnswer ?? await AskExistingConflictAsync(packaged.Conflict);
                        if (answer.ApplyToAll && answer.Behavior != ConflictAnswer.CancelRemaining)
                            runConflictAnswer = answer;

                        if (answer.Behavior == ConflictAnswer.CancelRemaining)
                        {
                            conflictStop = true;
                            AddLogLine($"Existing application kept: {packaged.Conflict.ApplicationName} v{packaged.Conflict.Version}. Remaining applications cancelled.");
                            return packaged;
                        }

                        if (answer.Behavior == ConflictAnswer.Overwrite)
                        {
                            // The conflict question waits for the operator. The first call may already have published to Intune, so its
                            // result stays: a throw here would report that publish as failed.
                            if (readBuild?.Invoke().Failure is { } gone)
                            {
                                conflictKept = true;
                                AddLogLine($"Overwrite skipped: {gone} The existing application is kept: {packaged.Conflict.ApplicationName} v{packaged.Conflict.Version}");
                                return packaged;
                            }
                            AddLogLine($"Overwriting existing: {packaged.Conflict.ApplicationName} v{packaged.Conflict.Version}");
                            return await _executionService.PackageAsync(
                                p, Comment, line => AddLogLine($"  {line}"), token,
                                ApplicationPackagerEngine.Services.OnExistingBehavior.Overwrite,
                                snapshot, packageTarget, reviewedVersion: reviewed);
                        }

                        conflictKept = true;
                        AddLogLine($"Existing application kept: {packaged.Conflict.ApplicationName} v{packaged.Conflict.Version}");
                        return packaged;
                    }

                    // Each destination of the target runs once; one that fails does not stop the next. A system that the confirmed review
                    // lists as Skip is not written: its cell says why.
                    var version = reviewed;
                    var cells = new List<PublishDestinationResult>();
                    var planRow = confirmedPlan.Rows.First(r => r.App.Uid == p.Uid);
                    var held = planRow.Cells.Where(c => c.Kind == PublishCellKind.Skip)
                        .Select(c => PublishDestinationResult.NotAttempted(c.Destination, PublishDestinationKind.Skipped, c.Text)).ToList();
                    var written = DestinationSet.FromTarget(target);
                    written = new DestinationSet(written.ConfigMgr && held.All(h => h.Destination != OneClickDestinations.ConfigMgr),
                        written.Wsus && held.All(h => h.Destination != OneClickDestinations.Wsus),
                        written.Intune && held.All(h => h.Destination != OneClickDestinations.Intune));
                    if (!written.Any)
                    {
                        rowResults.Add((p.Uid, p.Application, new PublishRowResult(p.Uid, version, held)));
                        p.Status = "Skipped";
                        p.IsSelected = false;
                        tally.RecordSkip();
                        AddLogLine("  Skipped: " + string.Join("; ", held.Select(h => h.Destination + ", " + h.Message)));
                        continue;
                    }
                    foreach (var skip in held) AddLogLine($"  {skip.Destination}: {skip.Message}");
                    var rowTarget = written.ToTargetName();
                    var rowWsusOnly = rowTarget == PublishRunTargets.WsusOnly;
                    PublishRowRun run;
                    try
                    {
                        run = await PublishRowRunner.RunAsync(p.Uid, version, rowTarget, PackageRowAsync,
                            () =>
                            {
                                ThrowIfBuildIsGone();
                                return _publishRuns.PublishWsusRowAsync(p, snapshot, reviewed, AddLogLine, token);
                            },
                            AddLogLine, token, () => conflictStop, cells);
                    }
                    finally
                    {
                        rowResults.Add((p.Uid, p.Application, new PublishRowResult(p.Uid, version, [.. cells, .. held])));
                    }
                    var result = run.Package;
                    var wsusStep = run.WsusStep;
                    if (run.Wsus is { StageFailed: true } unstaged)
                    {
                        AddLogLine($"Stage FAILED (exit {unstaged.Stage.ExitCode})");
                        LogStdErr(unstaged.Stage.StdErr);
                    }

                    if (conflictStop)
                    {
                        p.Status = "Skipped";
                        tally.RecordSkip();
                        _operationCts.Cancel();
                        break;
                    }

                    if (conflictKept && run.Wsus is null && !run.WsusFailed)
                    {
                        p.Status = "Skipped";
                        p.IsSelected = false;
                        tally.RecordSkip();
                        continue;
                    }

                    // A package call that threw has no result; its cells carry the error and the row failed.
                    var packageThrew = result is null && PublishRowRunner.PackageTarget(rowTarget) is not null;
                    var packageExit = packageThrew ? 1 : result?.ExitCode ?? 0;
                    var wsusFailed = run.WsusFailed;
                    var rowStatus = run.Wsus is null
                        ? RowStatus(rowTarget, wsusFailed ? 1 : packageExit)
                        : PublishRowOutcome.For(rowTarget, packageExit, conflictKept ? PackageRunEnd.Skipped : PackageRunEnd.Completed, wsusStep).Status;

                    // WSUS alone cannot take this application: nothing reached a server, so the row is a skip and stays checked.
                    if (rowWsusOnly && wsusStep?.Refused == true)
                    {
                        p.Status = rowStatus;
                        tally.RecordSkip();
                        continue;
                    }

                    if (packageExit == 0 && !wsusFailed)
                    {
                        p.Status = rowStatus;
                        p.IsSelected = false;
                        // The ConfigMgr application the operator kept is a skip, whatever WSUS did: nothing was packaged for it.
                        if (conflictKept) tally.RecordSkip();
                        else
                        {
                            tally.RecordSuccess();
                            if (result is not null) AddLogLine(PublishRunService.PackagedLine(result));
                        }
                        if (wsusStep is not null) AddLogLine("WSUS publish: " + wsusStep.Message);
                    }
                    else
                    {
                        // The target catches its own exceptions, so a run cancelled mid-flight
                        // comes back as an ordinary failed result. The caller's token is what
                        // tells the two apart.
                        var stopped = tally.RecordInterruptedOrFailed(_operationCts.Token);
                        p.Status = stopped ? "Cancelled" : rowStatus;
                        // A failed row stays checked, so the next run takes it again.
                        if (!stopped)
                        {
                            if (packageExit != 0)
                            {
                                AddLogLine($"Package FAILED (exit {packageExit})");
                                if (result is not null) LogStdErr(result.StdErr);
                            }
                            if (wsusFailed)
                                AddLogLine("WSUS publish FAILED: " + (wsusStep?.Message ?? run.Result.For(OneClickDestinations.Wsus)?.Message));
                        }

                        // Notes stand either way: a partially completed target may already
                        // have created the application or written content.
                        if (result is not null) LogIntuneNotes(p, result);
                        if (stopped) break;
                        continue;
                    }

                    if (result is not null) LogIntuneNotes(p, result);
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
                ? RunEndLine(tally, wsusOnly ? "Publish to WSUS" : "Publish", ProgressTotal, "published")
                : RunEndLine(tally, "Package", ProgressTotal, "packaged");
        }
        finally
        {
            ShowProgress = false;
            IsOperationRunning = false;
            _operationCts?.Dispose();
            _operationCts = null;
        }

        // The results window after the run: one cell for each application and system, from the facts of the run.
        if (rowResults.Count > 0)
        {
            // Every application of the confirmed plan has a row; one that the run did not reach reads "Not run".
            var all = confirmedPlan.Rows.Select(r =>
            {
                var done = rowResults.FindIndex(x => x.Uid == r.App.Uid);
                return done >= 0
                    ? (rowResults[done].Application, rowResults[done].Result)
                    : (r.App.Application, new PublishRowResult(r.App.Uid, r.App.Version, []));
            }).ToList();
            // Only a definition run says what it asked for: a PS1 packager can deploy by its own script.
            var withoutDeployment = confirmedPlan.Rows.Where(r => r.App.IntuneNotSupported.Length == 0
                    && !r.App.ConfigMgrSteps.Contains("deploy to ", StringComparison.Ordinal))
                .Select(r => r.App.Uid).ToHashSet(StringComparer.Ordinal);
            await ShowResultsAsync(all, tally.Cancelled, confirmedPlan.Systems, new PublishRunBuild(selected, snapshot, sealedVersion, readBuild),
                withoutDeployment);
        }
    }

    /// <param name="retry">The one destination of a retry; empty for a publish to every system in use.</param>
    private static PublishPlan BuildPlan(IReadOnlyList<PublishPlanApp> apps, AppSettings settings, string retry = "")
    {
        var systems = settings.Systems;
        if (retry.Length > 0)
            systems = new SystemsInUse
            {
                ConfigMgr = retry == OneClickDestinations.ConfigMgr && settings.Systems is { ConfigMgr: true },
                Intune = retry == OneClickDestinations.Intune && settings.Systems is { Intune: true },
                Wsus = retry == OneClickDestinations.Wsus && settings.Systems is { Wsus: true }
            };
        var plan = PublishPlanner.Plan(apps, systems, ReadinessOf(settings), skips: retry.Length == 0) with
        {
            Site = (settings.SiteCode ?? "").Trim() + "|" + (settings.ProviderMachine ?? "").Trim(),
            RetryDestination = retry
        };
        return plan with { Cards = PublishCards.For(plan, settings) };
    }

    /// <summary>One application of the publish plan: the version the run publishes, what ConfigMgr does afterward, and what each system cannot take.</summary>
    private PublishPlanApp PlanApp(PackagerInfo p, AppSettings settings, bool hidden, ConfigMgrFact site, string? sealedVersion = null)
    {
        var script = !p.IsJsonDefined && p.StagedManifestPath is null;
        var steps = new List<string>();
        if (!script)
        {
            var applicationId = _executionService.SupersedenceApplicationId(p);
            var distribution = ContentDistributionResolver.For(settings, applicationId);
            if (distribution.AutoDistribute && !string.IsNullOrWhiteSpace(distribution.DpGroupName))
            {
                steps.Add("distribute content to " + distribution.DpGroupName.Trim());
                if (distribution.DeployToTestCollection && !string.IsNullOrWhiteSpace(distribution.TestCollectionName))
                    steps.Add("deploy to " + distribution.TestCollectionName.Trim()
                        + (distribution.CreateTestCollectionIfMissing ? " (the run creates the collection if it is missing)" : ""));
            }
            if (ApplicationPackagerEngine.Services.Supersedence.SupersedenceSettingsEditor.EffectiveMode(settings.Supersedence, applicationId)
                != ApplicationPackagerEngine.Services.Supersedence.SupersedenceMode.Off)
                steps.Add("apply supersedence");
        }
        var version = sealedVersion ?? VersionOf(p);
        // A view model that a test builds without its constructor has no history.
        var build = _inventoryProjection is null ? null : _executionService.ResolveBuildPlan(p);
        var history = _inventoryProjection?.Entry(p, settings, build, _executionService.LocalStageVersion(p) is not null);
        var stale = build is not null && ApplicationPackagerEngine.Services.Workbench.BuildRecords.IsCurrent(build.DataRoot, build.ApplicationId,
            build.ProfileId, version, build.ProfileRevision, settings.ScriptSigning.PolicyDigest) == false;
        return new PublishPlanApp(p.Uid, p.Application, version, hidden)
        {
            History = history,
            ConfigMgrIncomplete = _inventoryProjection?.ConfigMgrIncomplete(p, settings, version, build) == true,
            BuildStale = stale,
            BuildKey = build is null ? "" : ApplicationPackagerEngine.Services.OneClick.OneClickHistory.BuildKey(build.ProfileId, build.ProfileRevision,
                settings.ScriptSigning.PolicyDigest),
            Detection = PublishCards.DetectionOf(p),
            Site = site,
            CheckedAt = p.LastChecked,
            ConfigMgrSteps = string.Join(", ", steps),
            IntuneNotSupported = script ? "a PS1 packager publishes to ConfigMgr only" : "",
            WsusNotSupported = p.WsusNotSupported ?? ""
        };
    }

    /// <summary>The version that a publish of the row uses: of the local installer when the stage takes one.</summary>
    private string VersionOf(PackagerInfo p) =>
        p.IsJsonDefined ? _executionService.RunVersion(p) : PackagerExecutionService.PublishVersion(p);

    /// <summary>
    /// The read before the first write (overhaul V1, phase 5b): the systems and their readiness from the stored settings, the version of each
    /// application, and the ConfigMgr application of each one. One site query for each application when ConfigMgr is in the plan. Null when cancelled.
    /// </summary>
    private async Task<PublishPlan?> RereadPlanAsync(PublishPlan shown, IReadOnlyList<PackagerInfo> selected,
        Func<(string Version, string? Failure)>? readBuild)
    {
        // A later stage into the same folder replaces a build. The run does not start for a build that is gone.
        string? sealedVersion = null;
        if (readBuild is not null)
        {
            var (version, failure) = readBuild();
            if (failure is not null)
            {
                AddLogLine("Publish cancelled. " + failure);
                return null;
            }
            sealedVersion = version;
        }

        var settings = _settingsService.Load();
        var byUid = selected.ToDictionary(p => p.Uid, StringComparer.Ordinal);
        var apps = shown.Rows.Select(r => PlanApp(byUid[r.App.Uid], settings, r.App.Hidden, r.App.Site, sealedVersion)).ToList();
        var unread = BuildPlan(apps, settings, shown.RetryDestination);
        // A blocked plan, or one with no ConfigMgr, keeps the ConfigMgr facts it was shown with.
        if (unread.Blocked.Count > 0 || !unread.Systems.Contains(OneClickDestinations.ConfigMgr)) return unread;

        _operationCts = new CancellationTokenSource();
        var token = _operationCts.Token;
        ShowProgress = true;
        ProgressCurrent = 0;
        ProgressTotal = apps.Count;
        try
        {
            for (var i = 0; i < apps.Count; i++)
            {
                if (token.IsCancellationRequested) return null;
                var p = byUid[apps[i].Uid];
                ProgressCurrent = i + 1;
                StatusText = ApplicationPackagerEngine.Models.RunProgress.Running("Reading ConfigMgr before publish", i + 1, apps.Count, p.Application);
                if (await ReadConfigMgrFactAsync(p, token) is not { } fact) return null;
                apps[i] = apps[i] with { Site = fact };
            }
            // The queries take time, and a Settings save during them must show. So the plan is built from the settings after the last query,
            // with the facts that were read. A change of the site makes the plan differ, and the next confirm reads the new site.
            var after = _settingsService.Load();
            return BuildPlan(apps.Select(a => PlanApp(byUid[a.Uid], after, a.Hidden, a.Site, sealedVersion)).ToList(), after, shown.RetryDestination);
        }
        finally
        {
            ShowProgress = false;
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
            PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            SecondaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
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

    /// <summary>The row of the detail pane: the row that has the keyboard focus or the click in the grid.</summary>
    [ObservableProperty] private PackagerInfo? _detailPackager;

    /// <summary>"Staged 26.06" or "Not staged", read from the stage folder off the UI thread when the row changes.</summary>
    [ObservableProperty] private string _detailStageText = PackagesDetail.NotStaged;

    /// <summary>The lines of the detail pane.</summary>
    public IReadOnlyList<DetailLine> DetailLines => DetailPackager is null ? [] : PackagesDetail.Lines(DetailPackager, DetailStageText);

    /// <summary>The pane is open. The choice is saved.</summary>
    [ObservableProperty] private bool _isDetailPaneOpen = true;

    partial void OnIsDetailPaneOpenChanged(bool value)
    {
        if (_settingsService is null) return;
        var settings = _settingsService.Load();
        if (settings.PackagesDetailPaneOpen == value) return;
        settings.PackagesDetailPaneOpen = value;
        _settingsService.Save(settings);
    }

    /// <summary>The width of the open pane. The choice is saved.</summary>
    [ObservableProperty] private double _detailPaneWidth = 320;

    partial void OnDetailPaneWidthChanged(double value)
    {
        if (_settingsService is null) return;
        var settings = _settingsService.Load();
        if (Math.Abs(settings.PackagesDetailPaneWidth - value) < 0.5) return;
        settings.PackagesDetailPaneWidth = value;
        _settingsService.Save(settings);
    }

    private int _stageRead;

    partial void OnDetailPackagerChanged(PackagerInfo? oldValue, PackagerInfo? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnDetailRowChanged;
        if (newValue is not null) newValue.PropertyChanged += OnDetailRowChanged;
        // The stage text of the row that was shown before must not stand under the new row.
        DetailStageText = newValue is null ? PackagesDetail.NotStaged : PackagesDetail.CheckingStage;
        OnPropertyChanged(nameof(DetailLines));
        ReadDetailStage();
    }

    /// <summary>The pane follows its row: a check, a stage or a publish changes the lines while the focus stays.</summary>
    private void OnDetailRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PackagerInfo.IsSelected)) return;
        OnPropertyChanged(nameof(DetailLines));
        if (e.PropertyName is nameof(PackagerInfo.Status) or nameof(PackagerInfo.StagedVersion) or nameof(PackagerInfo.LatestVersion)) ReadDetailStage();
    }

    /// <summary>Reads the stage folder of the shown row off the UI thread; only the answer of the newest read is shown.</summary>
    private void ReadDetailStage()
    {
        var read = ++_stageRead;
        if (DetailPackager is not { } row) return;
        _ = Task.Run(() =>
        {
            try
            {
                var manifest = _executionService?.ExistingStageManifest(row);
                // A dropped installer keeps its manifest in a folder that is not named by the version.
                var version = row.StagedManifestPath is null ? PublishVersionOf(manifest) : row.StagedVersion ?? row.LatestVersion;
                return PackagesDetail.StageText(manifest is not null, version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PackagesDetail.NotStaged; }
        }).ContinueWith(t =>
        {
            void Show()
            {
                if (read != _stageRead) return;
                DetailStageText = t.IsCompletedSuccessfully ? t.Result : PackagesDetail.NotStaged;
                OnPropertyChanged(nameof(DetailLines));
            }
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) Show();
            else dispatcher.BeginInvoke(Show);
        });
    }

    /// <summary>The version folder name of a stage manifest path: the manifest sits in the folder of its version.</summary>
    private static string? PublishVersionOf(string? manifestPath) =>
        manifestPath is null ? null : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(manifestPath));

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
        _stageFailures.Remove(row);
        await StageCommand.ExecuteAsync(null);
        return StageNowStatus(StatusText, _stageFailures.GetValueOrDefault(row));
    }

    /// <summary>The reason of the last failed stage of each row in this session.</summary>
    private readonly Dictionary<PackagerInfo, string> _stageFailures = new(ReferenceEqualityComparer.Instance);

    /// <summary>The status line of Stage now: the run status, then the first line of the failure reason when the stage failed.</summary>
    public static string StageNowStatus(string status, string? failure)
    {
        var reason = (failure ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(reason) || status.Contains(reason, StringComparison.Ordinal) ? status : $"{status} Reason: {reason}";
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
        _operationCts = new CancellationTokenSource();
        StatusText = $"Staging dropped installer: {manifest.AppName}";
        AddLogLine($"Stage: {manifest.AppName} {manifest.SoftwareVersion} (dropped installer)");

        try
        {
            var outcome = await CancellableStageRun.RunAsync(ct => _stagingService.StageDroppedInstallerAsync(
                manifest, analysis.Path, settings.DownloadRoot, line => AddLogLine($"  {line}"), ct), _operationCts.Token);
            if (outcome.Cancelled)
            {
                StatusText = RunEndLine(outcome.Tally, "Stage", 1, "staged");
                return;
            }
            var result = outcome.Result;
            if (result is null)
            {
                AddLogLine($"Stage ERROR: {outcome.ErrorMessage}");
                return;
            }
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
            _operationCts?.Dispose();
            _operationCts = null;
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
        ApplyFilter(replace: true);
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
    private async Task<bool> ReviewIntuneCompatibilityAsync(List<PackagerInfo> selected, string target,
        ApplicationPackagerEngine.Models.Workbench.RunSnapshot? snapshot, string? expectedBuildId)
    {
        var lines = new List<string>();
        var acceptable = new List<string>();
        foreach (var p in selected)
        {
            var stage = _executionService.ReviewIntuneCompatibility(p, snapshot, expectedBuildId);
            if (stage.Failure is not null)
            {
                AddLogLine($"Intune review: the build of {p.Application} cannot be read. {stage.Failure}");
                return false;
            }
            if (stage.Findings is null) continue;
            var reviewed = ReviewedStageName(p.Application, stage.Version, stage.BuildId);
            foreach (var f in stage.Findings)
            {
                if (f.CanAcceptDrop) acceptable.Add(f.DropKey!);
                AddLogLine($"{reviewed}: {ApplicationPackagerEngine.Services.IntuneCompatibilityFindings.LogLine(f)}");
                if (f.Severity != ApplicationPackagerEngine.Services.IntuneCompatibilityFindings.Info)
                    lines.Add($"{reviewed}: [{f.Severity}] {f.Code} - {f.Message}");
            }
        }
        if (lines.Count == 0) return true;

        var dialog = new ContentDialog
        {
            Title = "Intune review",
            Content = new TextBlock
            {
                Text = string.Join("\n\n", ApplicationPackagerEngine.Services.PublishPreflight.Lines(target).Concat(lines)),
                TextWrapping = System.Windows.TextWrapping.Wrap
            },
            PrimaryButtonText = "Continue",
            // A requirement that has no Intune rule blocks its publish. This button accepts the drop for those requirements.
            SecondaryButtonText = acceptable.Count > 0 ? "Accept dropped requirements and continue" : "",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        var answer = await _contentDialogService.ShowAsync(dialog, CancellationToken.None);
        if (answer == ContentDialogResult.Secondary && acceptable.Count > 0)
        {
            if (_settingsService.IsFileUnreadable)
            {
                AddLogLine("Intune review: the settings file cannot be read, so the accepted drops were not stored. The run is cancelled.");
                return false;
            }
            try
            {
                var settings = _settingsService.Load();
                settings.Intune.AcceptedRequirementDrops = (settings.Intune.AcceptedRequirementDrops ?? [])
                    .Concat(acceptable).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                _settingsService.Save(settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddLogLine($"Intune review: the accepted drops were not stored ({ex.Message}). The run is cancelled.");
                return false;
            }
            foreach (var key in acceptable.Distinct(StringComparer.OrdinalIgnoreCase))
                AddLogLine($"Intune review: the drop of the requirement is accepted ({key}).");
            return true;
        }
        return answer == ContentDialogResult.Primary;
    }

    /// <summary>The name of a reviewed stage in the Intune review: the application, the version and the build id.</summary>
    public static string ReviewedStageName(string application, string version, string buildId) =>
        application + (version.Length > 0 ? " " + version : "") + (buildId.Length > 0 ? " (build " + buildId + ")" : "");

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
            // A check box was set against one site: a publish must not take it to another one.
            p.IsSelected = false;
            p.SiteCheckedAt = null;
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
