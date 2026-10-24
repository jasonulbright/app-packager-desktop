using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the DP Content Manager page reads and writes; its logic depends on nothing else.</summary>
public sealed record DpContentPageServices(
    IDpContentService Content,
    IDpContentAuditLog AuditLog,
    IDpContentReportWriter Reports,
    IDpContentDialogs Dialogs,
    IChangePlanDialogs Plans,
    DpContentWriter Writer,
    Func<AppSettings> LoadSettings,
    Func<DpContentSettings?> LoadPreferences,
    Func<DpContentSettings, bool> SavePreferences,
    IAppLog Log)
{
    /// <summary>The time a text filter waits after the last key before it filters the rows.</summary>
    public TimeSpan FilterDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// The DP Content Manager page: the tabs DPs, Content, Status Issues and History over one snapshot
/// of the site. The snapshot is read on the first visit of a site tab with one bulk read; every grid,
/// the DP detail and the per-DP status of a content come from it. Nothing is read in the
/// constructor; leaving the page releases the data, and a read that ends later is discarded.
/// </summary>
public sealed partial class DpContentManagerViewModel : ObservableObject
{
    public const int DpsTab = 0;
    public const int ContentTab = 1;
    public const int IssuesTab = 2;
    public const int HistoryTab = 3;

    public const string LogArea = DpContentWriter.LogArea;
    public const string AllText = "All";
    public const string HealthyOnly = "Healthy only";
    public const string HasFailures = "Has failures";
    public const string HasInProgress = "Has in-progress";
    public const string FailedOnly = "Failed only";
    public const string InProgressOnly = "In progress only";
    public const string ReadCancelled = "The read was cancelled.";
    public const string LoadFailed = "The distribution point content could not be read. The log has the details.";
    public const string AccessHint = " The ConfigMgr console role needs read rights on distribution points and their content.";

    public const string DpsGrid = "dps";
    public const string DpPairsGrid = "dpPairs";
    public const string ContentGrid = "content";
    public const string ContentPairsGrid = "contentPairs";
    public const string IssuesGrid = "issues";
    public const string HistoryGrid = "history";

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _debounce = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Column, bool Ascending)> _sorts = new(StringComparer.Ordinal);
    private bool _preferencesLoaded;
    private bool _applying;
    private bool _restoring;
    private Task _preferencesLoad = Task.CompletedTask;
    private DpContentSettings? _stored;
    private bool _active;
    private CancellationTokenSource _visit = new();
    private CancellationTokenSource? _loadCts;
    private Task? _loading;
    private int _loadGeneration;

    private DpContentIndex? _index;
    private IReadOnlyList<DpGridRow> _allDps = [];
    private IReadOnlyList<ContentGridRow> _allContent = [];
    private IReadOnlyList<PairGridRow> _allIssues = [];
    private string? _selectedDpKey;
    private string? _selectedContentKey;
    private HashSet<string> _contentPairKeys = new(StringComparer.Ordinal);
    private HashSet<string> _issueKeys = new(StringComparer.Ordinal);

    public DpContentManagerViewModel(DpContentPageServices services) => Services = services;

    public DpContentPageServices Services { get; }

    /// <summary>Changes when the operator leaves the page; a result that belongs to an older value is discarded.</summary>
    public int Generation { get; private set; }

    public CancellationToken VisitToken => _visit.Token;

    public IReadOnlyList<string> DpStatuses { get; } = [AllText, HealthyOnly, HasFailures, HasInProgress];
    public IReadOnlyList<string> ContentTypes { get; } = [AllText, .. DpContentLabels.ContentTypes.Select(DpContentLabels.TypeLabel)];
    public IReadOnlyList<string> IssueStates { get; } = [AllText, FailedOnly, InProgressOnly];

    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CancelLoadCommand))]
    private bool _isLoading;

    /// <summary>The step the running read reports; empty when no read runs.</summary>
    [ObservableProperty] private string _loadProgress = "";

    /// <summary>The counts of the loaded snapshot, with the truncation note when the service cut the rows.</summary>
    [ObservableProperty] private string _loadSummary = "";

    [ObservableProperty] private bool _isTruncated;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionMessage))]
    private string _connectionMessage = "";

    public bool HasConnectionMessage => ConnectionMessage.Length > 0;

    [ObservableProperty] private string _statusMessage = "";

    /// <summary>True once a read of this visit succeeded.</summary>
    public bool IsLoaded => _index is not null;

    /// <summary>The running read, or a completed task.</summary>
    public Task LoadTask => _loading ?? Task.CompletedTask;

    [ObservableProperty] private string _dpText = "";
    [ObservableProperty] private string _dpStatus = AllText;
    [ObservableProperty] private IReadOnlyList<DpGridRow> _dpRows = [];
    [ObservableProperty] private string _dpCount = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDp))]
    private DpGridRow? _selectedDp;

    public bool HasSelectedDp => SelectedDp is not null;
    [ObservableProperty] private IReadOnlyList<PairGridRow> _dpPairRows = [];
    [ObservableProperty] private string _dpDetail = "Select a DP to see its content.";

    [ObservableProperty] private string _contentText = "";
    [ObservableProperty] private string _contentType = AllText;
    [ObservableProperty] private IReadOnlyList<ContentGridRow> _contentRows = [];
    [ObservableProperty] private string _contentCount = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedistributeFailedCommand), nameof(RemoveFromDpCommand), nameof(ValidateCommand))]
    private ContentGridRow? _selectedContent;

    [ObservableProperty] private IReadOnlyList<PairGridRow> _contentPairRows = [];
    [ObservableProperty] private string _contentDetail = "Select a content to see its status on each DP.";

    /// <summary>The per-DP rows the operator selected; the view selects them in the grid.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedistributeSelectedDpsCommand))]
    private IReadOnlyList<PairGridRow> _selectedContentPairs = [];

    [ObservableProperty] private string _issueText = "";
    [ObservableProperty] private string _issueState = AllText;
    [ObservableProperty] private IReadOnlyList<PairGridRow> _issueRows = [];
    [ObservableProperty] private string _issueCount = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedistributeIssuesCommand), nameof(RemoveFailuresCommand))]
    private IReadOnlyList<PairGridRow> _selectedIssues = [];

    partial void OnSelectedTabIndexChanged(int value)
    {
        SaveLater();
        if (_active && !_applying) _ = EnsureTabLoadedAsync();
    }

    /// <summary>Reads the saved choices on the first visit, then the data of the current tab.</summary>
    public async Task OnNavigatedToAsync()
    {
        _active = true;
        ConnectionProblem();
        if (!_preferencesLoaded)
        {
            _preferencesLoaded = true;
            _preferencesLoad = ApplyPreferencesAsync();
        }
        await _preferencesLoad;
        if (_active) await EnsureTabLoadedAsync();
    }

    /// <summary>Releases the data of every tab and cancels its reads; the Undo of the last change stays.</summary>
    public async Task OnNavigatedFromAsync()
    {
        _active = false;
        Release();
        await SaveAsync();
    }

    public Task EnsureTabLoadedAsync() => SelectedTabIndex == HistoryTab
        ? (IsHistoryLoaded ? Task.CompletedTask : LoadHistoryAsync())
        : (IsLoaded ? Task.CompletedTask : LoadAsync());

    /// <summary>The setting to change before the page can reach ConfigMgr, or null.</summary>
    public string? ConnectionProblem()
    {
        var problem = CollectionComplianceViewModel.ConnectionProblem(Services.LoadSettings());
        ConnectionMessage = problem ?? "";
        return problem;
    }

    /// <summary>A message for the operator: the session's plain text with the setting to check, or the fallback.</summary>
    public static string PlainMessage(Exception ex, string fallback) => ex switch
    {
        ConfigMgrSessionException { Failure: ConfigMgrFailure.InvalidSiteCode or ConfigMgrFailure.DriveNotConnected or ConfigMgrFailure.ModuleNotFound } s
            => SecretMask.Apply(s.Message) + CollectionComplianceViewModel.SiteHint,
        ConfigMgrSessionException { Failure: ConfigMgrFailure.AccessDenied } s => SecretMask.Apply(s.Message) + AccessHint,
        ConfigMgrSessionException { Failure: ConfigMgrFailure.Unexpected } => fallback,
        ConfigMgrSessionException s => SecretMask.Apply(s.Message),
        _ => fallback
    };

    public static string FailureName(Exception ex) => ex is ConfigMgrSessionException s ? $"{nameof(ConfigMgrSessionException)} {s.Failure}" : ex.GetType().Name;

    private void Release()
    {
        Generation++;
        _loadGeneration++;
        var visit = _visit;
        _visit = new CancellationTokenSource();
        try { visit.Cancel(); } catch (AggregateException ex) { Services.Log.Warning(LogArea, $"A read did not stop cleanly ({ex.InnerException?.GetType().Name})."); }
        try { _loadCts?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var pending in _debounce.Values) pending.Cancel();
        _debounce.Clear();
        _loadCts = null;
        _loading = null;
        IsLoading = false;
        LoadProgress = "";
        _index = null;
        _allDps = [];
        _allContent = [];
        _allIssues = [];
        _restoring = true;
        try
        {
            DpRows = [];
            ContentRows = [];
            IssueRows = [];
            DpPairRows = [];
            ContentPairRows = [];
            SelectedDp = null;
            SelectedContent = null;
            SelectedContentPairs = [];
            SelectedIssues = [];
        }
        finally { _restoring = false; }
        DpCount = ContentCount = IssueCount = "";
        LoadSummary = "";
        IsTruncated = false;
        StatusMessage = "";
        ConnectionMessage = "";
        ReleaseHistory();
        ReleaseResults();
        OnPropertyChanged(nameof(IsLoaded));
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => LoadAsync();

    private bool CanCancelLoad() => IsLoading;

    [RelayCommand(CanExecute = nameof(CanCancelLoad))]
    private void CancelLoad()
    {
        try { _loadCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Reads the snapshot; a call while a read runs waits for that read.</summary>
    public Task LoadAsync()
    {
        if (_loading is { IsCompleted: false } pending) return pending;
        var task = RunLoadAsync();
        _loading = task.IsCompleted ? null : task;
        return task;
    }

    private async Task RunLoadAsync()
    {
        var generation = Generation;
        var mine = ++_loadGeneration;
        bool Current() => generation == Generation && mine == _loadGeneration;
        if (ConnectionProblem() is not null) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(VisitToken);
        _loadCts = cts;
        IsLoading = true;
        LoadProgress = "Reading the distribution points and their content.";
        var progress = new Progress<string>(text =>
        {
            if (Current() && IsLoading) LoadProgress = SecretMask.Apply(text);
        });
        try
        {
            var snapshot = await Task.Run(() => Services.Content.LoadAsync(progress, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!Current()) return;
            var index = await Task.Run(() => new DpContentIndex(snapshot), cts.Token);
            if (!Current()) return;
            Show(index);
            Services.Log.Info(LogArea, $"Read {snapshot.Dps.Count} DPs, {snapshot.Content.Count} contents and {snapshot.Pairs.Count} pairs; truncated={snapshot.Truncated}.");
        }
        catch (OperationCanceledException)
        {
            if (Current()) StatusMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (!Current()) return;
            StatusMessage = PlainMessage(ex, LoadFailed);
            Services.Log.Warning(LogArea, $"The DP content read failed ({FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) _loadCts = null;
            if (mine == _loadGeneration)
            {
                IsLoading = false;
                LoadProgress = "";
            }
        }
    }

    /// <summary>The truncation sentence of the loaded snapshot; null when the service read every row.</summary>
    public string? TruncationNote => _index is { Snapshot.Truncated: true } index
        ? $"Truncated: showing {DpContentLabels.Count(index.Snapshot.Pairs.Count, "pair")}." + (index.Snapshot.Note is { Length: > 0 } note ? " " + SecretMask.Apply(note) : "")
        : null;

    private void Show(DpContentIndex index)
    {
        _index = index;
        var snapshot = index.Snapshot;
        _allDps = snapshot.Dps.Select(d => new DpGridRow(d)).ToList();
        _allContent = snapshot.Content.Select(c => new ContentGridRow(c)).ToList();
        _allIssues = index.Issues.Select(p => new PairGridRow(p)).ToList();
        IsTruncated = snapshot.Truncated;
        LoadSummary = $"{DpContentLabels.Count(snapshot.Dps.Count, "DP")}, {DpContentLabels.Count(snapshot.Content.Count, "content")}, "
            + $"{DpContentLabels.Count(snapshot.Pairs.Count, "pair")}, {DpContentLabels.Count(index.Issues.Count, "status issue")}."
            + (TruncationNote is { } truncated ? " " + truncated : snapshot.Note is { Length: > 0 } note ? " " + SecretMask.Apply(note) : "");
        OnPropertyChanged(nameof(IsLoaded));
        ApplyDpFilter();
        ApplyContentFilter();
        ApplyIssueFilter();
    }

    /// <summary>Reads the snapshot again after a write, after the read that runs now, and the audit log when the History tab holds it.</summary>
    private async Task RefreshAfterWriteAsync()
    {
        while (_loading is { IsCompleted: false } pending) await pending;
        if (!_active) return;
        if (IsHistoryLoaded) await LoadHistoryAsync();
        if (_active) await LoadAsync();
    }

    partial void OnDpTextChanged(string value) => Debounce(DpsGrid, ApplyDpFilter);
    partial void OnDpStatusChanged(string value) => Filtered(ApplyDpFilter);
    partial void OnContentTextChanged(string value) => Debounce(ContentGrid, ApplyContentFilter);
    partial void OnContentTypeChanged(string value) => Filtered(ApplyContentFilter);
    partial void OnIssueTextChanged(string value) => Debounce(IssuesGrid, ApplyIssueFilter);
    partial void OnIssueStateChanged(string value) => Filtered(ApplyIssueFilter);

    private void Filtered(Action apply)
    {
        apply();
        SaveLater();
    }

    private void Debounce(string key, Action apply)
    {
        if (_debounce.Remove(key, out var old)) old.Cancel();
        SaveLater();
        if (_applying || Services.FilterDelay <= TimeSpan.Zero)
        {
            apply();
            return;
        }
        var cts = new CancellationTokenSource();
        _debounce[key] = cts;
        _ = ApplyLaterAsync(key, cts, apply);
    }

    private async Task ApplyLaterAsync(string key, CancellationTokenSource cts, Action apply)
    {
        try { await Task.Delay(Services.FilterDelay, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (_debounce.TryGetValue(key, out var current) && ReferenceEquals(current, cts)) _debounce.Remove(key);
        if (!cts.IsCancellationRequested) apply();
    }

    /// <summary>Applies the text filters that still wait for their delay, as a test or an export needs the shown rows now.</summary>
    public void FlushFilters()
    {
        foreach (var key in _debounce.Keys.ToList())
        {
            _debounce.Remove(key, out var cts);
            cts?.Cancel();
            (key switch { DpsGrid => (Action)ApplyDpFilter, ContentGrid => ApplyContentFilter, _ => ApplyIssueFilter })();
        }
    }

    private static bool Has(string? value, string text) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private void ApplyDpFilter()
    {
        var text = DpText.Trim();
        var rows = _allDps.Where(r => text.Length == 0 || Has(r.DpName, text) || Has(r.SiteCode, text) || Has(r.Dp.NalPath, text))
            .Where(r => DpStatus switch
            {
                HealthyOnly => r.Dp.CountsKnown && r.Dp.Failed == 0 && r.Dp.InProgress == 0,
                HasFailures => r.Dp.Failed > 0,
                HasInProgress => r.Dp.InProgress > 0,
                _ => true
            });
        var list = Sorted(DpsGrid, rows).ToList();
        _restoring = true;
        try
        {
            DpRows = list;
            SelectedDp = _selectedDpKey is null ? null : list.FirstOrDefault(r => r.Key == _selectedDpKey);
        }
        finally { _restoring = false; }
        ShowDpDetail();
        DpCount = Counted(list.Count, _allDps.Count, "DP");
    }

    private void ApplyContentFilter()
    {
        var text = ContentText.Trim();
        var rows = _allContent.Where(r => text.Length == 0 || Has(r.Name, text) || Has(r.PackageId, text))
            .Where(r => ContentType == AllText || string.Equals(r.Type, ContentType, StringComparison.OrdinalIgnoreCase));
        var list = Sorted(ContentGrid, rows).ToList();
        _restoring = true;
        try
        {
            ContentRows = list;
            SelectedContent = _selectedContentKey is null ? null : list.FirstOrDefault(r => r.Key == _selectedContentKey);
        }
        finally { _restoring = false; }
        ShowContentPairs();
        ContentCount = Counted(list.Count, _allContent.Count, "content");
    }

    private void ApplyIssueFilter()
    {
        var text = IssueText.Trim();
        var rows = _allIssues.Where(r => text.Length == 0 || Has(r.ContentName, text) || Has(r.DpName, text) || Has(r.PackageId, text) || Has(r.StateName, text))
            .Where(r => IssueState switch
            {
                FailedOnly => r.Pair.IsFailed,
                InProgressOnly => r.Pair.IsInProgress,
                _ => true
            });
        var list = Sorted(IssuesGrid, rows).ToList();
        _restoring = true;
        try
        {
            IssueRows = list;
            SelectedIssues = list.Where(r => _issueKeys.Contains(r.Key)).ToList();
        }
        finally { _restoring = false; }
        IssueCount = Counted(list.Count, _allIssues.Count, "status issue");
    }

    private static string Counted(int shown, int all, string what) =>
        shown == all ? DpContentLabels.Count(all, what) : $"{DpContentLabels.Number(shown)} of {DpContentLabels.Count(all, what)}";

    partial void OnSelectedDpChanged(DpGridRow? value)
    {
        if (_restoring) return;
        _selectedDpKey = value?.Key;
        ShowDpDetail();
    }

    private void ShowDpDetail()
    {
        if (SelectedDp is not { } row || _index is not { } index)
        {
            DpPairRows = [];
            DpDetail = "Select a DP to see its content.";
            return;
        }
        var pairs = index.PairsOn(row.Dp);
        DpPairRows = Sorted(DpPairsGrid, pairs.Select(p => new PairGridRow(p))).ToList();
        var failed = pairs.Count(p => p.IsFailed);
        var inProgress = pairs.Count(p => p.IsInProgress);
        DpDetail = $"{row.DpName}: {DpContentLabels.Count(pairs.Count, "content")}, total size {row.SizeGb} GB, "
            + $"{DpContentLabels.Number(failed)} failed, {DpContentLabels.Number(inProgress)} in progress."
            + (row.Dp.IsPullDp ? " The DP is a pull DP." : "");
    }

    partial void OnSelectedContentChanged(ContentGridRow? value)
    {
        if (_restoring) return;
        if (!string.Equals(_selectedContentKey, value?.Key, StringComparison.Ordinal)) _contentPairKeys = new HashSet<string>(StringComparer.Ordinal);
        _selectedContentKey = value?.Key;
        ShowContentPairs();
    }

    private void ShowContentPairs()
    {
        if (SelectedContent is not { } row || _index is not { } index)
        {
            _restoring = true;
            try
            {
                ContentPairRows = [];
                SelectedContentPairs = [];
            }
            finally { _restoring = false; }
            ContentDetail = "Select a content to see its status on each DP.";
            return;
        }
        var list = Sorted(ContentPairsGrid, index.PairsOf(row.Content).Select(p => new PairGridRow(p))).ToList();
        _restoring = true;
        try
        {
            ContentPairRows = list;
            SelectedContentPairs = list.Where(r => _contentPairKeys.Contains(r.Key)).ToList();
        }
        finally { _restoring = false; }
        ContentDetail = $"{row.Name} ({row.PackageId}): {DpContentLabels.Count(list.Count, "DP")}, {row.Installed} installed, {row.Failed} failed, {row.InProgress} in progress. "
            + $"Deployments: {row.Deployments}.";
    }

    /// <summary>The view reports the rows the operator selected in the per-DP grid of the Content tab.</summary>
    public void SetSelectedContentPairs(IEnumerable<PairGridRow> rows)
    {
        if (_restoring) return;
        var list = rows.ToList();
        _contentPairKeys = new HashSet<string>(list.Select(r => r.Key), StringComparer.Ordinal);
        _restoring = true;
        try { SelectedContentPairs = list; }
        finally { _restoring = false; }
    }

    /// <summary>The view reports the rows the operator selected in the Status Issues grid.</summary>
    public void SetSelectedIssues(IEnumerable<PairGridRow> rows)
    {
        if (_restoring) return;
        var list = rows.ToList();
        _issueKeys = new HashSet<string>(list.Select(r => r.Key), StringComparer.Ordinal);
        _restoring = true;
        try { SelectedIssues = list; }
        finally { _restoring = false; }
    }

    /// <summary>True while the page sets a list or a selection; the view ignores the selection events it causes.</summary>
    public bool IsRestoring => _restoring;

    // The rows are sorted here, not by the grid view, because a filter change and a refresh replace the list and its view.

    /// <summary>The sorted column of a grid and its direction; null when the grid is not sorted.</summary>
    public (string Column, bool Ascending)? SortOf(string grid) => _sorts.TryGetValue(grid, out var sort) ? sort : null;

    /// <summary>Sorts a grid by a column: a first click sorts ascending, the next descending.</summary>
    public void Sort(string grid, string column)
    {
        var ascending = !(_sorts.TryGetValue(grid, out var now) && now.Column == column && now.Ascending);
        _sorts[grid] = (column, ascending);
        Resort(grid);
        SaveLater();
    }

    private void Resort(string grid)
    {
        switch (grid)
        {
            case DpsGrid: ApplyDpFilter(); break;
            case ContentGrid: ApplyContentFilter(); break;
            case IssuesGrid: ApplyIssueFilter(); break;
            case DpPairsGrid: ShowDpDetail(); break;
            case ContentPairsGrid: ShowContentPairs(); break;
            case HistoryGrid: ApplyHistoryFilter(); break;
        }
    }

    private IEnumerable<T> Sorted<T>(string grid, IEnumerable<T> rows)
    {
        if (!_sorts.TryGetValue(grid, out var sort)) return rows;
        Func<T, IComparable> key = item => SortKey(item!, sort.Column);
        return sort.Ascending ? rows.OrderBy(key) : rows.OrderByDescending(key);
    }

    /// <summary>The value a column sorts by: the number behind a count, the text otherwise.</summary>
    public static IComparable SortKey(object row, string column) => (row, column) switch
    {
        (DpGridRow r, nameof(DpGridRow.Total)) => r.Dp.Total,
        (DpGridRow r, nameof(DpGridRow.Installed)) => r.Dp.Installed,
        (DpGridRow r, nameof(DpGridRow.InProgress)) => r.Dp.InProgress,
        (DpGridRow r, nameof(DpGridRow.Failed)) => r.Dp.Failed,
        (DpGridRow r, nameof(DpGridRow.PercentOk)) => r.Dp.PercentOk,
        (DpGridRow r, nameof(DpGridRow.SizeGb)) => r.Dp.TotalSizeGb,
        (ContentGridRow r, nameof(ContentGridRow.Size)) => r.Content.SizeMb,
        (ContentGridRow r, nameof(ContentGridRow.Dps)) => r.Content.DpsTargeted,
        (ContentGridRow r, nameof(ContentGridRow.Installed)) => r.Content.Installed,
        (ContentGridRow r, nameof(ContentGridRow.InProgress)) => r.Content.InProgress,
        (ContentGridRow r, nameof(ContentGridRow.Failed)) => r.Content.Failed,
        (ContentGridRow r, nameof(ContentGridRow.PercentOk)) => r.Content.PercentOk,
        (ContentGridRow r, nameof(ContentGridRow.Deployments)) => r.Content.DeploymentCount,
        (PairGridRow r, nameof(PairGridRow.State)) => r.State,
        (PairGridRow r, nameof(PairGridRow.LastStatus)) => r.Pair.SummaryDate ?? DateTime.MinValue,
        (DpContentHistoryRow r, nameof(DpContentHistoryRow.Time)) => (r.Record.Timestamp, r.Index),
        (DpContentHistoryRow r, nameof(DpContentHistoryRow.Pairs)) => r.Record.PairCount,
        _ => row.GetType().GetProperty(column)?.GetValue(row) as string ?? ""
    };

    private async Task ApplyPreferencesAsync()
    {
        DpContentSettings? saved;
        try { saved = await Task.Run(Services.LoadPreferences); }
        catch (Exception ex)
        {
            saved = null;
            Services.Log.Warning(LogArea, $"The page choices could not be read ({ex.GetType().Name}).");
        }
        if (saved is not null)
        {
            _applying = true;
            try
            {
                SelectedTabIndex = Math.Clamp(saved.SelectedTab, DpsTab, HistoryTab);
                DpText = saved.DpText ?? "";
                DpStatus = DpStatuses.Contains(saved.DpStatus ?? "") ? saved.DpStatus! : AllText;
                ContentText = saved.ContentText ?? "";
                ContentType = ContentTypes.Contains(saved.ContentType ?? "") ? saved.ContentType! : AllText;
                IssueText = saved.IssueText ?? "";
                IssueState = IssueStates.Contains(saved.IssueState ?? "") ? saved.IssueState! : AllText;
                HistoryText = saved.HistoryText ?? "";
                HistoryAction = HistoryActions.Contains(saved.HistoryAction ?? "") ? saved.HistoryAction! : AllText;
                foreach (var (grid, value) in saved.Sorts ?? [])
                {
                    var parts = (value ?? "").Split('|');
                    if (parts.Length == 2 && parts[0].Length > 0 && parts[0].Length <= 64 && parts[0].All(char.IsLetter))
                        _sorts[grid] = (parts[0], !string.Equals(parts[1], "Descending", StringComparison.Ordinal));
                }
            }
            finally { _applying = false; }
        }
        _stored = Current();
    }

    private DpContentSettings Current() => new()
    {
        SelectedTab = SelectedTabIndex,
        DpText = DpText,
        DpStatus = DpStatus,
        ContentText = ContentText,
        ContentType = ContentType,
        IssueText = IssueText,
        IssueState = IssueState,
        HistoryText = HistoryText,
        HistoryAction = HistoryAction,
        Sorts = _sorts.Count == 0 ? null : _sorts.ToDictionary(s => s.Key, s => $"{s.Value.Column}|{(s.Value.Ascending ? "Ascending" : "Descending")}", StringComparer.Ordinal)
    };

    private static bool Same(DpContentSettings? a, DpContentSettings b) =>
        a is not null && a.SelectedTab == b.SelectedTab && a.DpText == b.DpText && a.DpStatus == b.DpStatus && a.ContentText == b.ContentText
        && a.ContentType == b.ContentType && a.IssueText == b.IssueText && a.IssueState == b.IssueState && a.HistoryText == b.HistoryText
        && a.HistoryAction == b.HistoryAction
        && (a.Sorts ?? []).OrderBy(s => s.Key, StringComparer.Ordinal).SequenceEqual((b.Sorts ?? []).OrderBy(s => s.Key, StringComparer.Ordinal));

    private void SaveLater()
    {
        if (_applying || !_preferencesLoaded) return;
        _ = SaveAsync();
    }

    /// <summary>Saves run one at a time and each reads the choices when its turn comes, so the last write holds the newest. False when nothing was written.</summary>
    public async Task<bool> SaveAsync()
    {
        await _preferencesLoad;
        await _saveLock.WaitAsync();
        try
        {
            var choices = Current();
            if (Same(_stored, choices)) return true;
            bool saved;
            try { saved = await Task.Run(() => Services.SavePreferences(choices)); }
            catch (Exception ex)
            {
                saved = false;
                Services.Log.Warning(LogArea, $"The page choices could not be saved ({ex.GetType().Name}).");
            }
            if (saved) _stored = choices;
            else Services.Log.Warning(LogArea, "The page choices were not saved.");
            return saved;
        }
        finally { _saveLock.Release(); }
    }
}
