using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Site Hygiene page reads and writes; its logic depends on nothing else.</summary>
public sealed record SiteHygienePageServices(
    ISiteHygieneService Hygiene,
    IHygieneSuppressionStore Suppressions,
    IHygieneScanStore ScanStore,
    IHygieneReportWriter Reports,
    ISiteHygieneDialogs Dialogs,
    IChangePlanDialogs Plans,
    SiteHygieneWriter Writer,
    Func<AppSettings> LoadSettings,
    Func<SiteHygieneSettings?> LoadPreferences,
    Func<SiteHygieneSettings, bool> SavePreferences,
    Func<Task<SuiteToolLaunchResult>> OpenLiveViews,
    IAppLog Log)
{
    /// <summary>The time a text filter waits after the last key before it filters the rows.</summary>
    public TimeSpan FilterDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>One scan area with its check box.</summary>
public sealed partial class HygieneAreaChoice(HygieneArea area, bool isChecked) : ObservableObject
{
    public string Id => area.Id;
    public string Title => area.Title;

    [ObservableProperty] private bool _isChecked = isChecked;
}

/// <summary>
/// The Site Hygiene page: Scan over the selected areas, then the tabs Findings, Relationships,
/// Summary and Options over the result. Nothing is read in the constructor, and no site call runs
/// before the first Scan: the first visit shows the stored last scan from disk. The result stays when
/// the operator leaves the page; a scan that ends after a newer one started or after Cancel is discarded.
/// </summary>
public sealed partial class SiteHygieneViewModel : ObservableObject
{
    public const int FindingsTab = 0;
    public const int RelationsTab = 1;
    public const int SummaryTab = 2;
    public const int OptionsTab = 3;
    public const int InventoryTab = 0;
    public const int TreeTab = 1;

    public const string LogArea = SiteHygieneWriter.LogArea;
    public const string AllText = "All";
    public const string FindingsGrid = "findings";
    public const string EdgesGrid = "edges";
    public const string ChecksGrid = "checks";

    public const string SelectArea = "Select at least one scan area on the Options tab.";
    public const string ScanCancelling = "Cancelling the scan. The scan stops after the current step.";
    public const string ScanCancelled = "The scan was cancelled. The earlier result stays.";
    public const string ScanFailed = "The scan could not read the site. The earlier result stays. The log has the details.";
    public const string SiteChanged = "The site settings changed during the scan, so its result was discarded. Scan again.";
    public const string AccessHint = " The ConfigMgr console role needs read rights on the objects of the selected areas.";
    public const string NotScanned = "Nothing was scanned yet. Select Scan first.";
    public const string NoFindingSelected = "Select a finding to see its details.";

    public static readonly IReadOnlyList<int> PauseChoices = [0, 50, 100, 250, 500, 1000];

    /// <summary>The categories of the source checks; a category the scan returns beyond these is added after them.</summary>
    public static readonly IReadOnlyList<string> KnownCategories =
    [
        "Applications", "Packages", "Content", "Relationships", "Collections", "Deployments", "Devices", "Boundaries", "Task Sequences", "Updates",
        "Distribution Points", "Compliance", "Drivers", "Security", "Site"
    ];

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _debounce = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Column, bool Ascending)> _sorts = new(StringComparer.Ordinal);
    private bool _preferencesLoaded;
    private bool _applying;
    private bool _restoring;
    private bool _storedRequested;
    private Task _preferencesLoad = Task.CompletedTask;
    private Task _storedLoad = Task.CompletedTask;
    private SiteHygieneSettings? _stored;
    private bool _active;

    private CancellationTokenSource? _scanCts;
    private Task? _scanning;
    private int _scanGeneration;

    /// <summary>Changes each time a new result is shown; a fix prepared on an older result is discarded.</summary>
    private int _resultGeneration;

    private HygieneScanResult? _result;
    private bool _isStored;
    private Dictionary<string, Suppression> _suppressions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fixedKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<FindingGridRow> _allFindings = [];
    private HashSet<string> _findingKeys = new(StringComparer.Ordinal);
    private string? _detailKey;

    public SiteHygieneViewModel(SiteHygienePageServices services)
    {
        Services = services;
        AreaChoices = SiteHygieneLabels.Areas.Select(a => new HygieneAreaChoice(a, true)).ToList();
        foreach (var choice in AreaChoices) choice.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(HygieneAreaChoice.IsChecked)) return;
            ShowAreas();
            SaveLater();
        };
        Categories = [AllText, .. KnownCategories];
        ShowAreas();
    }

    public SiteHygienePageServices Services { get; }

    public IReadOnlyList<HygieneAreaChoice> AreaChoices { get; }

    public IReadOnlyList<string> Severities { get; } = [AllText, .. SiteHygieneLabels.Severities.Select(SiteHygieneLabels.SeverityName)];

    public IReadOnlyList<int> Pauses => PauseChoices;

    [ObservableProperty] private IReadOnlyList<string> _categories;

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _relationsTabIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CancelScanCommand))]
    private bool _isScanning;

    /// <summary>The step the running scan reports; empty when no scan runs.</summary>
    [ObservableProperty] private string _scanProgress = "";

    /// <summary>The counts by severity, the suppressed count, the time of the result and the checks that did not run.</summary>
    [ObservableProperty] private string _summaryLine = "No scan result yet. Select Scan to read the site.";

    [ObservableProperty] private string _areasText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionMessage))]
    private string _connectionMessage = "";

    public bool HasConnectionMessage => ConnectionMessage.Length > 0;

    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty] private int _pauseMilliseconds;
    [ObservableProperty] private bool _probeContentSources;

    /// <summary>True once a result shows, scanned in this session or stored.</summary>
    public bool HasResult => _result is not null;

    /// <summary>True when the result shown is the stored last scan and not a scan of this session.</summary>
    public bool IsStoredResult => _result is not null && _isStored;

    public HygieneScanResult? Result => _result;

    /// <summary>The running scan, or a completed task.</summary>
    public Task ScanTask => _scanning ?? Task.CompletedTask;

    /// <summary>The read of the stored last scan on the first visit, or a completed task.</summary>
    public Task StoredLoadTask => _storedLoad;

    public string ReportFolder => Services.Reports.Folder;

    [ObservableProperty] private string _findingText = "";
    [ObservableProperty] private string _categoryFilter = AllText;
    [ObservableProperty] private string _severityFilter = AllText;
    [ObservableProperty] private bool _showSuppressed;
    [ObservableProperty] private IReadOnlyList<FindingGridRow> _findingRows = [];
    [ObservableProperty] private string _findingCount = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunFixCommand), nameof(SuppressCommand), nameof(UnsuppressCommand))]
    private IReadOnlyList<FindingGridRow> _selectedFindings = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail), nameof(DetailTitle), nameof(DetailObject), nameof(DetailEvidence), nameof(DetailRecommendation), nameof(DetailFixText), nameof(HasDetailFixText),
        nameof(DetailFixNote), nameof(HasDetailFixNote))]
    [NotifyCanExecuteChangedFor(nameof(CopyFixTextCommand))]
    private FindingGridRow? _detailFinding;

    public bool HasDetail => DetailFinding is not null;

    public string DetailTitle => DetailFinding is { } d
        ? $"{SiteHygieneLabels.SeverityText(d.Finding.Severity)}, {d.CheckId}, {d.Category}{(d.Finding.IsNew ? ", new since the last scan" : "")}{(d.IsSuppressed ? ", suppressed" + SuppressionText(d) : "")}"
        : NoFindingSelected;

    public string DetailObject => DetailFinding is { } d ? $"Object: {d.ObjectText}" : "";
    public string DetailEvidence => DetailFinding is { } d ? $"Evidence: {d.Finding.Evidence}" : "";
    public string DetailRecommendation => DetailFinding is { } d ? $"Recommended action: {d.Finding.Recommendation}" : "";

    public string DetailFixText => DetailFinding is { } d
        ? d.Finding.FixText is { Length: > 0 } text ? text : SiteHygieneLabels.ReportOnlyReason(d.Finding.FixKind) is { Length: > 0 } reason ? reason : "The service gave no PowerShell text for this fix."
        : "";

    public bool HasDetailFixText => DetailFinding?.Finding.FixText is { Length: > 0 };

    /// <summary>Why Run fix does not run this finding, and the console guidance of a report-only finding; empty for a finding with a fix.</summary>
    public string DetailFixNote
    {
        get
        {
            if (DetailFinding is not { } d) return "";
            var parts = new List<string>();
            if (!SiteHygieneLabels.IsFixable(d.Finding) && d.Finding.FixText is { Length: > 0 })
                parts.Add(SiteHygieneLabels.ReportOnlyReason(d.Finding.FixKind) + " Run fix is not available; the PowerShell text can be copied.");
            if (d.Finding.Guidance is { Length: > 0 } guidance) parts.Add("Guidance: " + guidance);
            return string.Join(" ", parts);
        }
    }

    public bool HasDetailFixNote => DetailFixNote.Length > 0;

    private string SuppressionText(FindingGridRow row) =>
        _suppressions.TryGetValue(row.Key, out var s) ? $" on {SiteHygieneLabels.Time(s.CreatedUtc)}: {s.Comment}" : "";

    /// <summary>The checks that did not run, with the reason; empty when every check ran.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotRun))]
    private string _notRunText = "";

    public bool HasNotRun => NotRunText.Length > 0;

    [ObservableProperty] private IReadOnlyList<CheckStatusRow> _checkRows = [];
    [ObservableProperty] private IReadOnlyList<SummaryCategoryRow> _categoryRows = [];

    partial void OnSelectedTabIndexChanged(int value) => SaveLater();
    partial void OnRelationsTabIndexChanged(int value) => SaveLater();
    partial void OnPauseMillisecondsChanged(int value) => SaveLater();
    partial void OnProbeContentSourcesChanged(bool value) => SaveLater();

    /// <summary>Reads the saved choices and the stored last scan on the first visit; later visits read nothing.</summary>
    public async Task OnNavigatedToAsync()
    {
        _active = true;
        ConnectionProblem();
        OnPropertyChanged(nameof(ReportFolder));
        if (!_preferencesLoaded)
        {
            _preferencesLoaded = true;
            _preferencesLoad = ApplyPreferencesAsync();
        }
        await _preferencesLoad;
        if (!_storedRequested)
        {
            _storedRequested = true;
            _storedLoad = LoadStoredAsync();
        }
        await _storedLoad;
        if (_active) RefreshSummary();
    }

    /// <summary>The scan and a fix keep running; the result stays for the next visit.</summary>
    public async Task OnNavigatedFromAsync()
    {
        _active = false;
        FlushFilters();
        await SaveAsync();
    }

    public bool IsActive => _active;

    /// <summary>The setting to change before the page can reach ConfigMgr, or null.</summary>
    public string? ConnectionProblem()
    {
        var problem = CollectionComplianceViewModel.ConnectionProblem(Services.LoadSettings());
        ConnectionMessage = problem ?? "";
        return problem;
    }

    private string SiteKey()
    {
        var settings = Services.LoadSettings();
        return (settings.SiteCode ?? "").Trim().ToUpperInvariant() + "|" + (settings.ProviderMachine ?? "").Trim().ToUpperInvariant();
    }

    /// <summary>A message for the operator: the service's plain text with the setting to check, or the fallback.</summary>
    public static string PlainMessage(Exception ex, string fallback) => ex switch
    {
        HygieneServiceException h => SecretMask.Apply(h.Message),
        ConfigMgrSessionException { Failure: ConfigMgrFailure.InvalidSiteCode or ConfigMgrFailure.DriveNotConnected or ConfigMgrFailure.ModuleNotFound } s
            => SecretMask.Apply(s.Message) + CollectionComplianceViewModel.SiteHint,
        ConfigMgrSessionException { Failure: ConfigMgrFailure.AccessDenied } s => SecretMask.Apply(s.Message) + AccessHint,
        ConfigMgrSessionException { Failure: ConfigMgrFailure.Unexpected } => fallback,
        ConfigMgrSessionException s => SecretMask.Apply(s.Message),
        _ => fallback
    };

    public static string FailureName(Exception ex) => ex is ConfigMgrSessionException s ? $"{nameof(ConfigMgrSessionException)} {s.Failure}" : ex.GetType().Name;

    private void ShowAreas()
    {
        var count = AreaChoices.Count(a => a.IsChecked);
        AreasText = count == AreaChoices.Count
            ? $"Scan areas: all {SiteHygieneLabels.Number(count)}. Change them on the Options tab."
            : count == 0
                ? "Scan areas: none. Select the areas on the Options tab."
                : $"Scan areas: {SiteHygieneLabels.Number(count)} of {SiteHygieneLabels.Number(AreaChoices.Count)}. Change them on the Options tab.";
    }

    [RelayCommand]
    private void SelectAllAreas()
    {
        foreach (var area in AreaChoices) area.IsChecked = true;
    }

    [RelayCommand]
    private void ClearAreas()
    {
        foreach (var area in AreaChoices) area.IsChecked = false;
    }

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task Scan() => ScanAsync();

    private bool CanCancelScan() => IsScanning;

    /// <summary>True once the running scan showed its result; a Cancel after that changes nothing.</summary>
    private bool _scanShown;

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        if (_scanCts is not { } cts || _scanShown) return;
        _scanGeneration++;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        StatusMessage = ScanCancelling;
    }

    /// <summary>The site code of the settings, upper case; empty when it is not set.</summary>
    private string CurrentSite() => HygieneSite.Code(Services.LoadSettings().SiteCode);

    /// <summary>The SMS Provider of the settings, trimmed; empty for the local computer.</summary>
    private string CurrentProvider() => (Services.LoadSettings().ProviderMachine ?? "").Trim();

    /// <summary>True when a result or suppression of <paramref name="resultSite"/> belongs to the site <paramref name="site"/>; a result without a site code belongs to none.</summary>
    public static bool SameSite(string? resultSite, string site) =>
        !string.IsNullOrWhiteSpace(resultSite) && string.Equals(resultSite.Trim(), site.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string OtherSite(string resultSite, string site) =>
        $"The result shown is of site {(resultSite.Length > 0 ? resultSite : "(not known)")}, and the settings name site {site}. Nothing was changed. Select Scan to read site {site}.";

    /// <summary>The refusal of a fix whose result is of another site or SMS Provider than the settings name.</summary>
    public static string OtherConnection(HygieneScanResult result, string site, string provider) =>
        $"The result shown is of site {HygieneSite.Text(result.SiteCode, result.Provider ?? "(not known)")}, and the settings name site {HygieneSite.Text(site, provider)}. Nothing was changed. Select Scan to read the site of the settings.";

    /// <summary>
    /// Why a scan result must not replace the stored last scan: a truncated read, or a check that did not
    /// run because a read failed, was cut or the check threw. The New column of the next scan compares
    /// with the stored scan, and a baseline without those findings would mark them all new. Null when the
    /// result may replace it.
    /// </summary>
    public static string? BaselineProblem(HygieneScanResult result)
    {
        if (result.Truncated) return "The read was truncated, so the result was not stored as the last scan.";
        var notRun = result.Checks.Count(c => c.State is HygieneCheckStates.NotRun or HygieneCheckStates.Failed);
        return notRun > 0
            ? $"{SiteHygieneLabels.Count(notRun, "check")} did not run, so the result was not stored as the last scan."
            : null;
    }

    /// <summary>The reason the result shown was not stored as the last scan; empty when it was stored or is the stored one.</summary>
    private string _notStored = "";

    /// <summary>Scans the selected areas; a call while a scan runs waits for that scan.</summary>
    public Task ScanAsync()
    {
        if (_scanning is { IsCompleted: false } pending) return pending;
        var task = RunScanAsync();
        _scanning = task.IsCompleted ? null : task;
        return task;
    }

    private async Task RunScanAsync()
    {
        if (ConnectionProblem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }
        var areas = AreaChoices.Where(a => a.IsChecked).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        if (areas.Count == 0)
        {
            StatusMessage = SelectArea;
            return;
        }
        var full = areas.Count == AreaChoices.Count;
        var options = new HygieneScanOptions(Areas: areas, PauseBetweenCalls: TimeSpan.FromMilliseconds(Math.Max(0, PauseMilliseconds)), ProbeContentSources: ProbeContentSources);
        var mine = ++_scanGeneration;
        var site = SiteKey();
        var siteCode = CurrentSite();
        var provider = CurrentProvider();
        using var cts = new CancellationTokenSource();
        _scanCts = cts;
        _scanShown = false;
        IsScanning = true;
        ScanProgress = "Starting the scan.";
        StatusMessage = "";
        var progress = new Progress<string>(text =>
        {
            if (mine == _scanGeneration && IsScanning) ScanProgress = SecretMask.Apply(text);
        });
        try
        {
            var result = await Task.Run(() => Services.Hygiene.ScanAsync(options, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || mine != _scanGeneration)
            {
                StatusMessage = ScanCancelled;
                return;
            }
            if (!string.Equals(SiteKey(), site, StringComparison.Ordinal))
            {
                StatusMessage = SiteChanged;
                return;
            }
            if (result.SiteCode.Length == 0) result = result with { SiteCode = siteCode };
            if (result.Provider is null) result = result with { Provider = provider };
            var suppressions = await Task.Run(() => ReadSuppressions(siteCode, provider));
            var last = await Task.Run(() => ReadLastScan(siteCode, provider));
            if (mine != _scanGeneration)
            {
                StatusMessage = ScanCancelled;
                return;
            }
            result = MarkNew(result, last);
            var baseline = full ? BaselineProblem(result) : "The scan covered part of the areas, so it is not stored as the last scan.";
            _notStored = baseline is null ? "" : baseline + (last is not null ? $" The New column compares with the stored scan of {SiteHygieneLabels.Time(last.ScannedUtc)}." : "");
            _scanShown = true;
            Show(result, stored: false, suppressions);
            var notRun = result.Checks.Count(SiteHygieneLabels.IsNotRun);
            StatusMessage = $"The scan ended: {SiteHygieneLabels.Count(result.Findings.Count, "finding")}, {SiteHygieneLabels.Count(result.Checks.Count, "check")}"
                + (notRun > 0 ? $", {SiteHygieneLabels.Number(notRun)} did not run." : ".")
                + (_notStored.Length > 0 ? " " + _notStored : "");
            Services.Log.Info(LogArea, $"Scan: areas={areas.Count} findings={result.Findings.Count} checks={result.Checks.Count} notRun={notRun} truncated={result.Truncated} stored={baseline is null}.");
            if (baseline is null) await SaveScanAsync(result);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = ScanCancelled;
        }
        catch (Exception ex)
        {
            if (mine != _scanGeneration)
            {
                StatusMessage = ScanCancelled;
                return;
            }
            StatusMessage = PlainMessage(ex, ScanFailed);
            Services.Log.Warning(LogArea, $"The scan failed ({FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            IsScanning = false;
            ScanProgress = "";
        }
    }

    private async Task SaveScanAsync(HygieneScanResult result)
    {
        try { await Task.Run(() => Services.ScanStore.Save(result)); }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The scan result could not be stored ({ex.GetType().Name}).");
            StatusMessage += " The result could not be stored as the last scan, so the next start shows no stored result.";
        }
    }

    /// <summary>The stored last scan of the site; null when there is none, it cannot be read, or it is of another site or provider.</summary>
    private HygieneScanResult? ReadLastScan(string siteCode, string provider)
    {
        try { return Services.ScanStore.Load() is { } last && last.IsFor(siteCode, provider) ? last : null; }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The last scan could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>
    /// Sets the New flag of each finding from the last scan of the same site: new when the check ran in
    /// the last scan and the last scan did not hold its key. Without a last scan, or for a check that did
    /// not run in it, no finding is new, as the page cannot tell.
    /// </summary>
    public static HygieneScanResult MarkNew(HygieneScanResult result, HygieneScanResult? last)
    {
        var keys = last?.Findings.Select(SiteHygieneLabels.Key).ToHashSet(StringComparer.Ordinal);
        var ran = last?.Checks.Where(SiteHygieneLabels.DidRun).Select(c => c.CheckId.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        return result with
        {
            Findings = result.Findings.Select(f => f with { IsNew = keys is not null && ran!.Contains(f.CheckId.ToUpperInvariant()) && !keys.Contains(SiteHygieneLabels.Key(f)) }).ToList()
        };
    }

    /// <summary>
    /// The suppressions that hold on the site and provider: their own, those of the site without a recorded
    /// provider and those of every site; the most specific wins for the same key.
    /// </summary>
    private Dictionary<string, Suppression> ReadSuppressions(string siteCode, string provider)
    {
        var map = new Dictionary<string, Suppression>(StringComparer.Ordinal);
        try
        {
            foreach (var s in Services.Suppressions.Load().Where(s => s is { CheckId.Length: > 0, ObjectKey.Length: > 0 } && s.AppliesTo(siteCode, provider))
                         .OrderBy(s => s.SiteCode.Length > 0).ThenBy(s => s.Provider is not null))
                map[SiteHygieneLabels.Key(s.CheckId, s.ObjectKey)] = s;
        }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The suppressions could not be read ({ex.GetType().Name}).");
        }
        return map;
    }

    /// <summary>The site code of the result shown, or of the settings when no result shows.</summary>
    private string ResultSite() => _result is { SiteCode.Length: > 0 } r ? r.SiteCode.Trim().ToUpperInvariant() : CurrentSite();

    /// <summary>The SMS Provider of the result shown, or of the settings when no result shows or the result has none recorded.</summary>
    private string ResultProvider() => _result is { Provider: { } p } ? p : CurrentProvider();

    /// <summary>Shows the stored last scan of the site when no scan of this session shows yet; the read touches the disk only.</summary>
    private async Task LoadStoredAsync()
    {
        HygieneScanResult? stored;
        Dictionary<string, Suppression> suppressions;
        var siteCode = CurrentSite();
        var provider = CurrentProvider();
        try
        {
            stored = await Task.Run(Services.ScanStore.Load);
            suppressions = await Task.Run(() => ReadSuppressions(siteCode, provider));
        }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The stored scan could not be read ({ex.GetType().Name}).");
            return;
        }
        // A scan of this session that ended or still runs is newer than the stored result.
        if (stored is null || _result is not null || IsScanning) return;
        if (!stored.IsFor(siteCode, provider))
        {
            if (siteCode.Length > 0)
                StatusMessage = SameSite(stored.SiteCode, siteCode)
                    ? $"The stored last scan is of site {HygieneSite.Text(stored.SiteCode, stored.Provider ?? "(not known)")}, not of site {HygieneSite.Text(siteCode, provider)}, so it is not shown. Select Scan to read site {siteCode}."
                    : $"The stored last scan is of site {(stored.SiteCode.Length > 0 ? stored.SiteCode : "(not known)")}, not of site {siteCode}, so it is not shown. Select Scan to read site {siteCode}.";
            return;
        }
        _notStored = "";
        Show(stored, stored: true, suppressions);
    }

    private void Show(HygieneScanResult result, bool stored, Dictionary<string, Suppression> suppressions)
    {
        _result = result;
        _isStored = stored;
        _resultGeneration++;
        _fixedKeys.Clear();
        _suppressions = suppressions;
        BuildRows();
        var extra = result.Findings.Select(f => f.Category).Concat(result.Checks.Select(c => c.Category))
            .Where(c => !string.IsNullOrWhiteSpace(c) && !KnownCategories.Contains(c, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        Categories = [AllText, .. KnownCategories, .. extra];
        if (!Categories.Contains(CategoryFilter)) CategoryFilter = AllText;
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(IsStoredResult));
        OnPropertyChanged(nameof(Result));
        ApplyFindingFilter();
        RefreshSummary();
    }

    private void BuildRows() =>
        _allFindings = (_result?.Findings ?? []).Select(f => new FindingGridRow(f, _suppressions.ContainsKey(SiteHygieneLabels.Key(f)))).ToList();

    /// <summary>The rows the summary counts: the findings of the result that a fix of this session did not resolve.</summary>
    private IEnumerable<FindingGridRow> LiveRows => _allFindings.Where(r => !_fixedKeys.Contains(r.Key));

    /// <summary>The summary line and the Summary tab, from the current result.</summary>
    public void RefreshSummary()
    {
        if (_result is not { } result)
        {
            SummaryLine = "No scan result yet. Select Scan to read the site.";
            NotRunText = "";
            CheckRows = [];
            CategoryRows = [];
            return;
        }
        var live = LiveRows.ToList();
        var shown = live.Where(r => !r.IsSuppressed).ToList();
        var suppressed = live.Count - shown.Count;
        var notRun = result.Checks.Where(SiteHygieneLabels.IsNotRun).ToList();
        var notSelected = result.Checks.Count(SiteHygieneLabels.WasNotSelected);
        var noneRan = result.Checks.Count > 0 && result.Checks.All(c => !SiteHygieneLabels.DidRun(c));
        var parts = new List<string>();
        if (result.Truncated) parts.Add("The read was truncated, so the counts are not known.");
        else if (noneRan) parts.Add("No check ran, so the counts are not known.");
        else parts.Add(string.Join(", ", SiteHygieneLabels.Severities.Select(s => SiteHygieneLabels.SeverityCount(s, shown.Count(r => r.Finding.Severity == s)))) + ".");
        parts.Add($"{SiteHygieneLabels.Number(suppressed)} suppressed.");
        parts.Add($"Last scan {SiteHygieneLabels.Time(result.ScannedUtc)}{(_isStored ? " (stored result, not read from the site in this session)" : "")}.");
        if (notRun.Count > 0) parts.Add($"{SiteHygieneLabels.Count(notRun.Count, "check")} did not run.");
        if (notSelected > 0) parts.Add($"{SiteHygieneLabels.Count(notSelected, "check")} {(notSelected == 1 ? "was" : "were")} not in the scanned areas or options.");
        if (_fixedKeys.Count > 0) parts.Add($"{SiteHygieneLabels.Count(_fixedKeys.Count, "finding")} fixed since the scan.");
        if (!_isStored && _notStored.Length > 0) parts.Add(_notStored);
        if (result.Note is { Length: > 0 } note) parts.Add(SecretMask.Apply(note));
        SummaryLine = string.Join(" ", parts);

        const int listed = 6;
        NotRunText = notRun.Count == 0 ? ""
            : $"{SiteHygieneLabels.Count(notRun.Count, "check")} did not run, so {(notRun.Count == 1 ? "its" : "their")} findings are not known: "
              + string.Join("; ", notRun.Take(listed).Select(c => $"{c.CheckId} ({SiteHygieneLabels.StateText(c.State).ToLowerInvariant()}{(c.Reason is { Length: > 0 } r ? ": " + SecretMask.Apply(r) : "")})"))
              + (notRun.Count > listed ? $"; and {SiteHygieneLabels.Number(notRun.Count - listed)} more. The Summary tab lists every check." : ".");

        CheckRows = Sorted(ChecksGrid, result.Checks.Select(c => new CheckStatusRow(c))).ToList();
        CategoryRows = BuildCategories(result, shown);
    }

    private static List<SummaryCategoryRow> BuildCategories(HygieneScanResult result, List<FindingGridRow> shown)
    {
        var names = KnownCategories.Concat(result.Checks.Select(c => c.Category)).Concat(shown.Select(r => r.Category))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<SummaryCategoryRow>();
        foreach (var name in names)
        {
            var checks = result.Checks.Where(c => string.Equals(c.Category, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var mine = shown.Where(r => string.Equals(r.Category, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (checks.Count == 0 && mine.Count == 0) continue;
            var unknown = result.Truncated || (checks.Count > 0 && checks.All(c => !SiteHygieneLabels.DidRun(c)));
            rows.Add(Row(name, mine, unknown, checks.Where(SiteHygieneLabels.IsNotRun).ToList()));
        }
        rows.Add(Row("All categories", shown, result.Truncated || (result.Checks.Count > 0 && result.Checks.All(c => !SiteHygieneLabels.DidRun(c))), result.Checks.Where(SiteHygieneLabels.IsNotRun).ToList()));
        return rows;
    }

    /// <summary>
    /// One summary row. A cell whose severity has a check that did not run shows "At least N", or "Not
    /// known" for zero, as the findings of that check are missing; a zero there never means none.
    /// </summary>
    private static SummaryCategoryRow Row(string name, List<FindingGridRow> rows, bool unknown, List<HygieneCheckStatus> notRun)
    {
        string Cell(Func<FindingGridRow, bool> match, bool partial)
        {
            if (unknown) return "Not known";
            var n = rows.Count(match);
            return !partial ? SiteHygieneLabels.Number(n) : n == 0 ? "Not known" : "At least " + SiteHygieneLabels.Number(n);
        }
        bool Missing(HygieneSeverity s) => notRun.Any(c => c.Severity == s);
        return new SummaryCategoryRow(Category: name, Error: Cell(r => r.Finding.Severity == HygieneSeverity.Error, Missing(HygieneSeverity.Error)),
            Warning: Cell(r => r.Finding.Severity == HygieneSeverity.Warning, Missing(HygieneSeverity.Warning)),
            Info: Cell(r => r.Finding.Severity == HygieneSeverity.Info, Missing(HygieneSeverity.Info)), Total: Cell(_ => true, notRun.Count > 0),
            NotRun: SiteHygieneLabels.Number(notRun.Count));
    }

    partial void OnFindingTextChanged(string value) => Debounce(FindingsGrid, ApplyFindingFilter);
    partial void OnCategoryFilterChanged(string value) => Filtered(ApplyFindingFilter);
    partial void OnSeverityFilterChanged(string value) => Filtered(ApplyFindingFilter);
    partial void OnShowSuppressedChanged(bool value) => Filtered(ApplyFindingFilter);

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
            (key == EdgesGrid ? (Action)ApplyEdgeFilter : ApplyFindingFilter)();
        }
    }

    private static bool Has(string? value, string text) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private void ApplyFindingFilter()
    {
        var text = FindingText.Trim();
        var rows = LiveRows
            .Where(r => ShowSuppressed || !r.IsSuppressed)
            .Where(r => CategoryFilter == AllText || string.Equals(r.Category, CategoryFilter, StringComparison.OrdinalIgnoreCase))
            .Where(r => SeverityFilter == AllText || string.Equals(SiteHygieneLabels.SeverityName(r.Finding.Severity), SeverityFilter, StringComparison.Ordinal))
            .Where(r => text.Length == 0 || Has(r.CheckId, text) || Has(r.Finding.ObjectName, text) || Has(r.Finding.ObjectKey, text) || Has(r.Finding.ObjectType, text)
                || Has(r.Finding.Evidence, text) || Has(r.Finding.Recommendation, text) || Has(r.Category, text));
        var list = Sorted(FindingsGrid, rows).ToList();
        _restoring = true;
        try
        {
            FindingRows = list;
            SelectedFindings = list.Where(r => _findingKeys.Contains(r.Key)).ToList();
            DetailFinding = _detailKey is null ? null : list.FirstOrDefault(r => r.Key == _detailKey);
        }
        finally { _restoring = false; }
        var all = LiveRows.Count(r => ShowSuppressed || !r.IsSuppressed);
        FindingCount = list.Count == all ? SiteHygieneLabels.Count(all, "finding") : $"{SiteHygieneLabels.Number(list.Count)} of {SiteHygieneLabels.Count(all, "finding")}";
        if (!ShowSuppressed && _result is not null)
        {
            var hidden = LiveRows.Count(r => r.IsSuppressed);
            if (hidden > 0) FindingCount += $", {SiteHygieneLabels.Number(hidden)} suppressed and hidden";
        }
    }

    /// <summary>The view reports the rows the operator selected; the last one is the row the detail pane shows.</summary>
    public void SetSelectedFindings(IEnumerable<FindingGridRow> rows)
    {
        if (_restoring) return;
        var list = rows.ToList();
        _findingKeys = new HashSet<string>(list.Select(r => r.Key), StringComparer.Ordinal);
        _detailKey = list.Count == 0 ? null : list[^1].Key;
        _restoring = true;
        try
        {
            SelectedFindings = list;
            DetailFinding = list.Count == 0 ? null : list[^1];
        }
        finally { _restoring = false; }
    }

    /// <summary>True while the page sets a list or a selection; the view ignores the selection events it causes.</summary>
    public bool IsRestoring => _restoring;

    // The rows are sorted here, not by the grid view, because a filter change and a new result replace the list and its view.

    public (string Column, bool Ascending)? SortOf(string grid) => _sorts.TryGetValue(grid, out var sort) ? sort : null;

    /// <summary>Sorts a grid by a column: a first click sorts ascending, the next descending.</summary>
    public void Sort(string grid, string column)
    {
        var ascending = !(_sorts.TryGetValue(grid, out var now) && now.Column == column && now.Ascending);
        _sorts[grid] = (column, ascending);
        switch (grid)
        {
            case FindingsGrid: ApplyFindingFilter(); break;
            case EdgesGrid: ApplyEdgeFilter(); break;
            case ChecksGrid: RefreshSummary(); break;
        }
        SaveLater();
    }

    private IEnumerable<T> Sorted<T>(string grid, IEnumerable<T> rows)
    {
        if (!_sorts.TryGetValue(grid, out var sort))
            return grid == FindingsGrid ? rows.OfType<FindingGridRow>().OrderByDescending(r => r.Finding.Severity).ThenBy(r => r.CheckId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ObjectName, StringComparer.OrdinalIgnoreCase).Cast<T>() : rows;
        Func<T, IComparable> key = item => SortKey(item!, sort.Column);
        return sort.Ascending ? rows.OrderBy(key) : rows.OrderByDescending(key);
    }

    /// <summary>The value a column sorts by: the severity rank, the number behind a count, the text otherwise.</summary>
    public static IComparable SortKey(object row, string column) => (row, column) switch
    {
        (FindingGridRow r, nameof(FindingGridRow.Severity)) => (int)r.Finding.Severity,
        (FindingGridRow r, nameof(FindingGridRow.New)) => r.Finding.IsNew,
        (CheckStatusRow r, nameof(CheckStatusRow.Findings)) => SiteHygieneLabels.DidRun(r.Check) ? r.Check.Findings : -1,
        _ => row.GetType().GetProperty(column)?.GetValue(row) as string ?? ""
    };

    private async Task ApplyPreferencesAsync()
    {
        SiteHygieneSettings? saved;
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
                SelectedTabIndex = Math.Clamp(saved.SelectedTab, FindingsTab, OptionsTab);
                RelationsTabIndex = Math.Clamp(saved.RelationsTab, InventoryTab, TreeTab);
                FindingText = saved.FindingText ?? "";
                CategoryFilter = Categories.Contains(saved.Category ?? "") ? saved.Category! : AllText;
                SeverityFilter = Severities.Contains(saved.Severity ?? "") ? saved.Severity! : AllText;
                ShowSuppressed = saved.ShowSuppressed;
                EdgeText = saved.EdgeText ?? "";
                EdgeKind = EdgeKinds.Contains(saved.EdgeKind ?? "") ? saved.EdgeKind! : AllText;
                PauseMilliseconds = PauseChoices.Contains(saved.PauseMilliseconds) ? saved.PauseMilliseconds : 0;
                ProbeContentSources = saved.ProbeContentSources;
                if (saved.Areas is { Count: > 0 } areas)
                {
                    var wanted = areas.Where(a => a is not null).ToHashSet(StringComparer.Ordinal);
                    // An area list that names no known area is from another version; every area is scanned then.
                    if (AreaChoices.Any(a => wanted.Contains(a.Id)))
                        foreach (var area in AreaChoices) area.IsChecked = wanted.Contains(area.Id);
                }
                foreach (var (grid, value) in saved.Sorts ?? [])
                {
                    var parts = (value ?? "").Split('|');
                    if (parts.Length == 2 && parts[0].Length > 0 && parts[0].Length <= 64 && parts[0].All(char.IsLetter))
                        _sorts[grid] = (parts[0], !string.Equals(parts[1], "Descending", StringComparison.Ordinal));
                }
            }
            finally { _applying = false; }
            ShowAreas();
        }
        _stored = Current();
    }

    private SiteHygieneSettings Current() => new()
    {
        SelectedTab = SelectedTabIndex,
        RelationsTab = RelationsTabIndex,
        FindingText = FindingText,
        Category = CategoryFilter,
        Severity = SeverityFilter,
        ShowSuppressed = ShowSuppressed,
        EdgeText = EdgeText,
        EdgeKind = EdgeKind,
        Areas = AreaChoices.All(a => a.IsChecked) ? null : AreaChoices.Where(a => a.IsChecked).Select(a => a.Id).ToList(),
        PauseMilliseconds = PauseMilliseconds,
        ProbeContentSources = ProbeContentSources,
        Sorts = _sorts.Count == 0 ? null : _sorts.ToDictionary(s => s.Key, s => $"{s.Value.Column}|{(s.Value.Ascending ? "Ascending" : "Descending")}", StringComparer.Ordinal)
    };

    private static bool Same(SiteHygieneSettings? a, SiteHygieneSettings b) =>
        a is not null && a.SelectedTab == b.SelectedTab && a.RelationsTab == b.RelationsTab && a.FindingText == b.FindingText && a.Category == b.Category
        && a.Severity == b.Severity && a.ShowSuppressed == b.ShowSuppressed && a.EdgeText == b.EdgeText && a.EdgeKind == b.EdgeKind
        && a.PauseMilliseconds == b.PauseMilliseconds && a.ProbeContentSources == b.ProbeContentSources
        && (a.Areas ?? []).SequenceEqual(b.Areas ?? [])
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
