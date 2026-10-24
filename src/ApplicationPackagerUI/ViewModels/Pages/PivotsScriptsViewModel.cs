using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Pivots and Scripts page reads and writes; its logic depends on nothing else.</summary>
public sealed record PivotsScriptsPageServices(
    IPivotsScriptsService Service,
    IPivotsSessionStore Sessions,
    IPivotsAuditLog AuditLog,
    IPivotsReportWriter Reports,
    IPivotsLibrary Library,
    IPivotsDialogs Dialogs,
    IChangePlanDialogs Plans,
    PivotsScriptsWriter Writer,
    Func<AppSettings> LoadSettings,
    Func<PivotsScriptsSettings?> LoadPreferences,
    Func<PivotsScriptsSettings, bool> SavePreferences,
    IAppLog Log)
{
    /// <summary>The time a text filter waits after the last key before it filters the rows.</summary>
    public TimeSpan FilterDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// The Pivots and Scripts page: CMPivot queries and approved site scripts on a resolved set of
/// devices, the site scripts, the saved sessions and the audit log. Nothing is read in the
/// constructor and no site call runs before the first visit. Leaving the page releases the site
/// data and discards a read that ends later; a run that waits for results keeps waiting and its
/// result shows on the next visit.
/// </summary>
public sealed partial class PivotsScriptsViewModel : ObservableObject
{
    public const int PivotTab = 0;
    public const int ScriptTab = 1;
    public const int SiteScriptsTab = 2;
    public const int HistoryTab = 3;
    public const int OptionsTab = 4;

    public const string LogArea = PivotsScriptsWriter.LogArea;
    public const string ReadCancelled = "The read was cancelled.";
    public const string AccessHint = " The ConfigMgr role needs the right the site names, such as Run CMPivot on the collection or Approve on SMS Scripts.";
    public const string ProviderMessage = "Set the SMS Provider in Settings, ConfigMgr Site Settings. The page reaches the AdminService on the SMS Provider.";

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _debounce = new(StringComparer.Ordinal);
    private bool _preferencesLoaded;
    private bool _applying;
    private Task _preferencesLoad = Task.CompletedTask;
    private PivotsScriptsSettings? _stored;
    private bool _active;
    private CancellationTokenSource _visit = new();

    public PivotsScriptsViewModel(PivotsScriptsPageServices services) => Services = services;

    public PivotsScriptsPageServices Services { get; }

    /// <summary>Changes when the operator leaves the page; a result that belongs to an older value is discarded.</summary>
    public int Generation { get; private set; }

    public CancellationToken VisitToken => _visit.Token;

    public bool IsActive => _active;

    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionMessage))]
    private string _connectionMessage = "";

    public bool HasConnectionMessage => ConnectionMessage.Length > 0;

    [ObservableProperty] private string _statusMessage = "";

    /// <summary>The line above the tabs: the site, the resolved targets and the last run. Every tab shows it.</summary>
    [ObservableProperty] private string _pageSummary = "";

    partial void OnSelectedTabIndexChanged(int value)
    {
        SaveLater();
        if (_active && !_applying) _ = EnsureTabLoadedAsync();
    }

    /// <summary>Reads the saved choices on the first visit, shows the last result again, then reads what the current tab needs.</summary>
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
        if (!_active) return;
        ShowKeptResult();
        UpdateSummary();
        await EnsureTabLoadedAsync();
    }

    /// <summary>Releases the site data and cancels its reads; a run that waits for results keeps waiting.</summary>
    public async Task OnNavigatedFromAsync()
    {
        _active = false;
        Release();
        await SaveAsync();
    }

    /// <summary>Reads what the current tab shows: the library on the Pivot and Site scripts tabs, the scripts on the Script and Site scripts tabs, the history on the History tab.</summary>
    public Task EnsureTabLoadedAsync()
    {
        var site = ConnectionProblem() is null;
        return SelectedTabIndex switch
        {
            PivotTab => Task.WhenAll(EnsureLibraryAsync(PivotsLibraryKind.Query)),
            ScriptTab => site && !IsScriptsLoaded ? LoadScriptsAsync() : Task.CompletedTask,
            SiteScriptsTab => Task.WhenAll(EnsureLibraryAsync(PivotsLibraryKind.Script), site && !IsScriptsLoaded ? LoadScriptsAsync() : Task.CompletedTask),
            HistoryTab => IsHistoryLoaded ? Task.CompletedTask : LoadHistoryAsync(),
            _ => Task.CompletedTask
        };
    }

    /// <summary>The setting to change before the page can reach the site, or null.</summary>
    public string? ConnectionProblem()
    {
        var settings = Services.LoadSettings();
        var problem = CollectionComplianceViewModel.ConnectionProblem(settings)
            ?? (string.IsNullOrWhiteSpace(settings.ProviderMachine) ? ProviderMessage : null);
        ConnectionMessage = problem ?? "";
        return problem;
    }

    /// <summary>A message for the operator: the plain text of the service or the session with the setting to check, or the fallback.</summary>
    public static string PlainMessage(Exception ex, string fallback) => ex switch
    {
        PivotsScriptsServiceException p => SecretMask.Apply(p.Message),
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
        var visit = _visit;
        _visit = new CancellationTokenSource();
        try { visit.Cancel(); } catch (AggregateException ex) { Services.Log.Warning(LogArea, $"A read did not stop cleanly ({ex.InnerException?.GetType().Name})."); }
        foreach (var pending in _debounce.Values) pending.Cancel();
        _debounce.Clear();
        StatusMessage = "";
        ConnectionMessage = "";
        ReleaseTargets();
        ReleaseScripts();
        ReleaseLibraries();
        ReleaseHistory();
        ReleaseResultRows();
    }

    /// <summary>Builds the summary line from the current state.</summary>
    public void UpdateSummary()
    {
        var settings = Services.LoadSettings();
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.SiteCode))
            parts.Add(string.IsNullOrWhiteSpace(settings.ProviderMachine) ? $"Site {settings.SiteCode.Trim()}." : $"Site {settings.SiteCode.Trim()} on {settings.ProviderMachine!.Trim()}.");
        if (Resolved is { } targets) parts.Add($"Targets: {PivotsLabels.Count(targets.Devices.Count, "device")}.");
        if (IsWaiting) parts.Add(WaitProgress);
        else if (LastRunHeadline.Length > 0) parts.Add(LastRunHeadline);
        PageSummary = string.Join(" ", parts);
    }

    private void Debounce(string key, Action apply)
    {
        if (_debounce.Remove(key, out var old)) old.Cancel();
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
            (key switch { ResultFilterKey => (Action)ApplyResultFilter, SiteScriptFilterKey => ApplySiteScriptFilter, _ => ApplyHistoryFilter })();
        }
    }

    private static bool Has(string? value, string text) => value is not null && value.Contains(text, StringComparison.OrdinalIgnoreCase);

    private async Task ApplyPreferencesAsync()
    {
        PivotsScriptsSettings? saved;
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
                SelectedTabIndex = Math.Clamp(saved.SelectedTab, PivotTab, OptionsTab);
                ResultTabIndex = Math.Clamp(saved.ResultTab, ResultsView, PipelineView);
                LibraryFolder = saved.LibraryFolder ?? "";
                WaitMinutes = WaitChoices.Contains(saved.WaitMinutes) ? saved.WaitMinutes : DefaultWaitMinutes;
                if (Enum.TryParse<RunTargetKind>(saved.TargetKind, ignoreCase: false, out var kind)) TargetKind = kind;
            }
            finally { _applying = false; }
        }
        _stored = Current();
    }

    private PivotsScriptsSettings Current() => new()
    {
        SelectedTab = SelectedTabIndex,
        ResultTab = ResultTabIndex,
        LibraryFolder = string.IsNullOrWhiteSpace(LibraryFolder) ? null : LibraryFolder.Trim(),
        WaitMinutes = WaitMinutes,
        TargetKind = TargetKind.ToString()
    };

    private static bool Same(PivotsScriptsSettings? a, PivotsScriptsSettings b) =>
        a is not null && a.SelectedTab == b.SelectedTab && a.ResultTab == b.ResultTab && a.LibraryFolder == b.LibraryFolder && a.WaitMinutes == b.WaitMinutes
        && a.TargetKind == b.TargetKind;

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
