using System.Globalization;
using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Maintenance Windows page reads and writes; the page logic depends on nothing else.</summary>
public sealed record MaintenanceWindowsPageServices(
    IMaintenanceWindowService Windows,
    IScheduleProjector Projector,
    IMaintenanceTemplateStore Templates,
    IMaintenanceReportWriter Reports,
    IMaintenanceWindowsPrompts Prompts,
    IChangePlanDialogs Plans,
    MaintenanceWindowWriter Writer,
    Func<AppSettings> LoadSettings,
    Func<MaintenanceWindowsSettings?> LoadPreferences,
    Action<MaintenanceWindowsSettings> SavePreferences,
    IAppLog Log,
    Func<DateTime> Now);

/// <summary>One row of the Windows grid: a window, or a collection without one.</summary>
public sealed record MaintenanceWindowRow(CollectionInfo Collection, MaintenanceWindowInfo? Window)
{
    public bool HasWindow => Window is not null;
    public bool IsEnabled => Window?.Enabled == true;

    /// <summary>False for a window whose schedule the model cannot hold: the service reads it with a zero duration.</summary>
    public bool IsScheduleHeld => Window is null || Window.Schedule.Duration > TimeSpan.Zero;

    public string Status => Window is null ? "No window" : Window.Enabled ? "Enabled" : "Disabled";
    public string CollectionName => Collection.Name;
    public string CollectionId => Collection.CollectionId;
    public string FolderPath => Collection.FolderPath;
    public string WindowName => Window?.WindowName ?? "";
    public string Type => Window is null ? "" : MaintenanceWindowsText.WindowType(Window.WindowType);
    public string Recurrence => Window is null ? "" : IsScheduleHeld ? MaintenanceWindowsText.Kind(Window.Schedule.Kind) : MaintenanceWindowsText.OtherSchedule;
    public string ScheduleText => Window?.ScheduleText ?? "";
    public string Duration => Window is null || !IsScheduleHeld ? "" : MaintenanceWindowsText.Duration(Window.Schedule.Duration);
    public string Next => Window is null ? "" : MaintenanceWindowsText.Time(Window.NextOccurrence);

    public override string ToString() => Window is null ? $"{CollectionName}, no window" : $"{WindowName}, {CollectionName}, {ScheduleText}";
}

/// <summary>One row of the Coverage grid.</summary>
public sealed record CoverageRow(CollectionInfo Collection, int WindowCount)
{
    public bool HasWindow => WindowCount > 0;
    public string Coverage => HasWindow ? "Has a window" : "No window";
    public string Name => Collection.Name;
    public string CollectionId => Collection.CollectionId;
    public string FolderPath => Collection.FolderPath;
    public int MemberCount => Collection.MemberCount;
    public string BuiltIn => Collection.IsBuiltIn ? "Yes" : "No";

    public override string ToString() => $"{Name}, {Coverage}";
}

/// <summary>One row of the Templates grid.</summary>
public sealed record TemplateRow(MaintenanceTemplate Template, string ScheduleText)
{
    public string Name => Template.Name;
    public string Recurrence => MaintenanceWindowsText.Kind(Template.Schedule.Kind);
    public string Type => MaintenanceWindowsText.WindowType(Template.WindowType);
    public string Duration => MaintenanceWindowsText.Duration(Template.Schedule.Duration);
    public string Source => Template.IsShipped ? "Shipped" : "User";
    public string Description => Template.Description;

    public override string ToString() => $"{Name}, {Recurrence}";
}

public sealed record PropertyRow(string Name, string Value);

public sealed partial class MaintenanceWindowsViewModel : ObservableObject
{
    public const string LogArea = MaintenanceWindowWriter.LogArea;
    public const string All = "All";
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";
    public const string NotInUseMessage = "ConfigMgr is not in use. Select it under Systems in use in Settings.";
    public const string SiteCodeMessage = "Set the site code in Settings, ConfigMgr Site Settings.";
    public const string SiteHint = " Check the site code and the SMS Provider in Settings, ConfigMgr Site Settings.";
    public const string AccessHint = " The ConfigMgr console role needs modify rights on collections for maintenance windows.";
    public const string LoadFailed = "The maintenance windows could not be read. The log has the details.";
    public const string PrepareFailed = "The current state could not be read. The log has the details.";
    public const string NotConfirmed = "Nothing was changed.";
    public const string RefreshFirst = "Refresh the list first.";
    public const string NothingToExport = "The list has no windows to export.";
    public const string NothingToUndo = "There is no change to undo.";
    public const string ScheduleNotHeld =
        "ConfigMgr stores a schedule for this window that this page cannot show, for example an hourly interval. Here it can only be enabled or disabled. Use the ConfigMgr console to change it.";
    public const string NoOccurrencesShown = "This page cannot calculate the occurrences of this schedule.";
    public const string TemplateSaveFailed = "The template could not be saved. The log has the details.";
    public const string TemplateDeleteFailed = "The template could not be deleted. The log has the details.";
    public const string KeepsDescription = "Keeps the description.";

    public static string ReportFailed(string format) => $"The {format} report could not be written. The log has the details.";

    public static string WindowGone(string collection, string window) =>
        $"The window {window} on {collection} no longer exists. Refresh the list.";

    public static string BothWindowsExist(string collection, string oldName, string newName) =>
        $"Both windows exist on {collection}: {newName} was created, {oldName} was not removed. Select {oldName} and press Remove to remove the old window.";

    public static string RenameNotMade(string collection, string oldName, string newName) =>
        $"{newName} was not created on {collection} and {oldName} is unchanged. {NotConfirmed}";

    public static string AllSkipped(string name) => $"Every selected collection already has a window named {name}. Nothing to change.";

    private readonly MaintenanceWindowsPageServices _services;
    private bool _preferencesLoaded;
    private bool _applyingPreferences;
    private Task _preferencesLoad = Task.CompletedTask;
    private MaintenanceWindowsSettings? _storedChoices;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private double _editorWidth;
    private double _editorHeight;

    private IReadOnlyList<CollectionInfo>? _collections;
    private IReadOnlyList<MaintenanceWindowInfo>? _windows;
    private IReadOnlyList<MaintenanceTemplate> _templates = [];
    private CancellationTokenSource? _load;
    private Task? _loading;
    private int _generation;
    private bool _writing;
    private bool _background;
    private MaintenancePlan? _lastRunPlan;
    private IReadOnlyList<MaintenanceWindowRow> _selectedWindows = [];
    private IReadOnlyList<CoverageRow> _selectedCoverage = [];

    public MaintenanceWindowsViewModel(IMaintenanceWindowService windows, IScheduleProjector projector, IMaintenanceTemplateStore templates,
        IMaintenanceReportWriter reports, IMaintenanceWindowsPrompts prompts, IChangePlanDialogs plans, MaintenanceWindowWriter writer,
        SettingsService settings, IAppLog log)
        : this(new MaintenanceWindowsPageServices(windows, projector, templates, reports, prompts, plans, writer,
            settings.Load,
            () => MaintenanceWindowsPreferences.Load(settings),
            choices => MaintenanceWindowsPreferences.Save(settings, choices),
            log,
            () => DateTime.Now))
    {
    }

    public MaintenanceWindowsViewModel(MaintenanceWindowsPageServices services)
    {
        _services = services;
    }

    public ObservableCollection<MaintenanceWindowRow> Rows { get; } = [];
    public ObservableCollection<CoverageRow> Coverage { get; } = [];
    public ObservableCollection<TemplateRow> Templates { get; } = [];
    public ObservableCollection<PropertyRow> Properties { get; } = [];
    public ObservableCollection<string> NextOccurrences { get; } = [];
    public ObservableCollection<ChangePlanItemResult> LastResults { get; } = [];
    public ObservableCollection<Choice<string>> Folders { get; } = [new("", "All folders")];

    public IReadOnlyList<Choice<string>> TypeFilters { get; } = [new("", "All types"), .. MaintenanceWindowsText.WindowTypes];
    public IReadOnlyList<string> EnabledFilters { get; } = [All, Enabled, Disabled];

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _detailTabIndex;
    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private string _typeFilter = "";
    [ObservableProperty] private string _enabledFilter = All;
    [ObservableProperty] private bool _onlyCollectionsWithWindows = true;
    [ObservableProperty] private string _folderFilter = "";
    [ObservableProperty] private bool _coverageWithoutWindowOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedWindow), nameof(EditTooltip))]
    [NotifyCanExecuteChangedFor(nameof(EditWindowCommand))]
    private MaintenanceWindowRow? _selectedRow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditTemplateCommand), nameof(DeleteTemplateCommand), nameof(ApplySelectedTemplateCommand))]
    private TemplateRow? _selectedTemplate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CancelLoadCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _connectionMessage = "";
    [ObservableProperty] private string _rowCount = "";
    [ObservableProperty] private string _coverageCount = "";
    [ObservableProperty] private string _resultSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand))]
    private MaintenancePlan? _lastPlan;

    public bool HasSelectedWindow => SelectedRow?.HasWindow == true;

    /// <summary>Why Edit is disabled for the selected window, or null.</summary>
    public string? EditTooltip => SelectedRow is { HasWindow: true, IsScheduleHeld: false } ? ScheduleNotHeld : null;

    /// <summary>Why Remove is disabled for the selection, or null.</summary>
    public string? RemoveTooltip => _selectedWindows.Any(r => r.HasWindow && !r.IsScheduleHeld) ? ScheduleNotHeld : null;
    public bool HasConnectionMessage => ConnectionMessage.Length > 0;
    public bool HasResults => LastResults.Count > 0;
    public bool HasUndo => LastPlan is not null;
    public bool IsDataLoaded => _collections is not null && _windows is not null;

    /// <summary>The window rows selected in the grid; the page sets them from the grid selection.</summary>
    public IReadOnlyList<MaintenanceWindowRow> SelectedWindows => _selectedWindows;

    public IReadOnlyList<CoverageRow> SelectedCoverage => _selectedCoverage;

    partial void OnConnectionMessageChanged(string value) => OnPropertyChanged(nameof(HasConnectionMessage));

    public void SetSelectedWindows(IEnumerable<MaintenanceWindowRow> rows)
    {
        _selectedWindows = rows.ToList();
        EditWindowCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        RemoveWindowsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RemoveTooltip));
    }

    public void SetSelectedCoverage(IEnumerable<CoverageRow> rows) => _selectedCoverage = rows.ToList();

    /// <summary>Reads the saved choices on the first visit, then the collections, windows and templates.</summary>
    public async Task OnNavigatedToAsync()
    {
        if (!_preferencesLoaded)
        {
            _preferencesLoaded = true;
            _preferencesLoad = ApplyPreferencesAsync();
            await _preferencesLoad;
        }
        await LoadTemplatesAsync();
        if (!IsDataLoaded) await LoadAsync();
    }

    /// <summary>Releases the ConfigMgr data; the page reads it again on the next visit.</summary>
    public async Task OnNavigatedFromAsync()
    {
        // A load still running belongs to the visit that started it; its result is discarded.
        _generation++;
        try { _load?.Cancel(); } catch (ObjectDisposedException) { }
        _load = null;
        _loading = null;
        IsLoading = false;
        _collections = null;
        _windows = null;
        SelectedRow = null;
        SetSelectedWindows([]);
        SetSelectedCoverage([]);
        Rows.Clear();
        Coverage.Clear();
        Properties.Clear();
        NextOccurrences.Clear();
        RowCount = "";
        CoverageCount = "";
        await SavePreferencesAsync();
    }

    partial void OnSelectedTabIndexChanged(int value) => SaveChoicesLater();
    partial void OnDetailTabIndexChanged(int value) => SaveChoicesLater();
    partial void OnTextFilterChanged(string value) => ApplyWindowFilter();

    partial void OnTypeFilterChanged(string value)
    {
        ApplyWindowFilter();
        SaveChoicesLater();
    }

    partial void OnEnabledFilterChanged(string value)
    {
        ApplyWindowFilter();
        SaveChoicesLater();
    }

    partial void OnOnlyCollectionsWithWindowsChanged(bool value)
    {
        ApplyWindowFilter();
        SaveChoicesLater();
    }

    partial void OnFolderFilterChanged(string value)
    {
        ApplyWindowFilter();
        ApplyCoverageFilter();
        SaveChoicesLater();
    }

    partial void OnCoverageWithoutWindowOnlyChanged(bool value)
    {
        ApplyCoverageFilter();
        SaveChoicesLater();
    }

    partial void OnSelectedRowChanged(MaintenanceWindowRow? value) => ShowDetails(value);

    private void SaveChoicesLater()
    {
        if (_applyingPreferences) return;
        _ = SavePreferencesAsync();
    }

    private async Task ApplyPreferencesAsync()
    {
        MaintenanceWindowsSettings? saved;
        try { saved = await Task.Run(_services.LoadPreferences); }
        catch (Exception ex)
        {
            saved = null;
            _services.Log.Warning(LogArea, $"The page choices could not be read ({ex.GetType().Name}).");
        }
        if (saved is null) return;
        _applyingPreferences = true;
        try
        {
            SelectedTabIndex = saved.SelectedTab is >= 0 and <= 2 ? saved.SelectedTab : 0;
            DetailTabIndex = saved.DetailTab is 0 or 1 ? saved.DetailTab : 0;
            TextFilter = saved.TextFilter ?? "";
            TypeFilter = TypeFilters.Any(t => t.Value == saved.TypeFilter) ? saved.TypeFilter : "";
            EnabledFilter = EnabledFilters.Contains(saved.EnabledFilter) ? saved.EnabledFilter : All;
            OnlyCollectionsWithWindows = saved.OnlyCollectionsWithWindows;
            FolderFilter = saved.FolderFilter ?? "";
            CoverageWithoutWindowOnly = saved.CoverageWithoutWindowOnly;
            _editorWidth = saved.EditorWidth;
            _editorHeight = saved.EditorHeight;
        }
        finally { _applyingPreferences = false; }
        _storedChoices = CurrentChoices();
    }

    private MaintenanceWindowsSettings CurrentChoices() => new()
    {
        SelectedTab = SelectedTabIndex,
        DetailTab = DetailTabIndex,
        TextFilter = TextFilter,
        TypeFilter = TypeFilter,
        EnabledFilter = EnabledFilter,
        OnlyCollectionsWithWindows = OnlyCollectionsWithWindows,
        FolderFilter = FolderFilter,
        CoverageWithoutWindowOnly = CoverageWithoutWindowOnly,
        EditorWidth = _editorWidth,
        EditorHeight = _editorHeight
    };

    private static bool SameChoices(MaintenanceWindowsSettings? a, MaintenanceWindowsSettings b) =>
        a is not null && a.SelectedTab == b.SelectedTab && a.DetailTab == b.DetailTab && a.TextFilter == b.TextFilter
        && a.TypeFilter == b.TypeFilter && a.EnabledFilter == b.EnabledFilter && a.OnlyCollectionsWithWindows == b.OnlyCollectionsWithWindows
        && a.FolderFilter == b.FolderFilter && a.CoverageWithoutWindowOnly == b.CoverageWithoutWindowOnly
        && a.EditorWidth.Equals(b.EditorWidth) && a.EditorHeight.Equals(b.EditorHeight);

    /// <summary>
    /// Writes the choices on a pool thread when they differ from the stored ones; a failure is
    /// logged and tried again at the next change. Saves run one at a time and each reads the
    /// choices when its turn comes, so the last write holds the newest choices.
    /// </summary>
    private async Task SavePreferencesAsync()
    {
        // A save before the stored choices are known would replace them with the defaults.
        await _preferencesLoad;
        await _saveLock.WaitAsync();
        try
        {
            var choices = CurrentChoices();
            if (SameChoices(_storedChoices, choices)) return;
            _storedChoices = choices;
            try
            {
                await Task.Run(() => _services.SavePreferences(choices));
            }
            catch (Exception ex)
            {
                _storedChoices = null;
                _services.Log.Warning(LogArea, $"The page choices could not be saved ({ex.GetType().Name}).");
            }
        }
        finally { _saveLock.Release(); }
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => LoadAsync();

    private bool CanCancelLoad() => IsLoading;

    [RelayCommand(CanExecute = nameof(CanCancelLoad))]
    private void CancelLoad()
    {
        try { _load?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Reads the collections and windows; a second call while a load runs waits for that load.</summary>
    public async Task LoadAsync()
    {
        if (_loading is { IsCompleted: false } pending)
        {
            await pending;
            return;
        }
        var load = LoadCoreAsync(_generation);
        _loading = load;
        try { await load; }
        finally
        {
            if (ReferenceEquals(_loading, load)) _loading = null;
        }
    }

    private async Task LoadCoreAsync(int generation)
    {
        using var cts = new CancellationTokenSource();
        _load = cts;
        IsLoading = true;
        StatusMessage = "Reading the collections and maintenance windows.";
        IReadOnlyList<CollectionInfo>? collections = null;
        IReadOnlyList<MaintenanceWindowInfo>? windows = null;
        string? connection = null;
        string status = "";
        try
        {
            (connection, collections, windows) = await Task.Run(async () =>
            {
                var problem = ConnectionProblem(_services.LoadSettings());
                if (problem is not null) return (problem, (IReadOnlyList<CollectionInfo>?)null, (IReadOnlyList<MaintenanceWindowInfo>?)null);
                var c = await _services.Windows.ListCollectionsAsync(cts.Token);
                var w = await _services.Windows.ListWindowsAsync(cts.Token);
                return ((string?)null, (IReadOnlyList<CollectionInfo>?)c, (IReadOnlyList<MaintenanceWindowInfo>?)w);
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            status = "The read was cancelled.";
        }
        catch (Exception ex)
        {
            connection = PlainMessage(ex, LoadFailed);
            if (generation == _generation) _services.Log.Warning(LogArea, $"The maintenance windows could not be read ({FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_load, cts)) _load = null;
        }
        if (generation != _generation) return;
        IsLoading = false;
        ConnectionMessage = connection ?? "";
        if (collections is null || windows is null)
        {
            StatusMessage = status;
            return;
        }
        _collections = collections;
        _windows = windows;
        _services.Log.Info(LogArea, $"Read {windows.Count} maintenance windows on {collections.Count} collections.");
        StatusMessage = "";
        Rebuild();
    }

    /// <summary>The setting to change before the page can reach ConfigMgr, or null.</summary>
    public static string? ConnectionProblem(AppSettings settings)
    {
        if (settings.Systems is { ConfigMgr: false }) return NotInUseMessage;
        if (string.IsNullOrWhiteSpace(settings.SiteCode)) return SiteCodeMessage;
        return null;
    }

    /// <summary>A message for the operator: the session's own plain text with the setting to check, or the fallback.</summary>
    public static string PlainMessage(Exception ex, string fallback) => ex switch
    {
        MaintenanceWindowException m => SecretMask.Apply(m.Message),
        ConfigMgrSessionException { Failure: ConfigMgrFailure.InvalidSiteCode or ConfigMgrFailure.DriveNotConnected or ConfigMgrFailure.ModuleNotFound } s
            => SecretMask.Apply(s.Message) + SiteHint,
        ConfigMgrSessionException { Failure: ConfigMgrFailure.AccessDenied } s => SecretMask.Apply(s.Message) + AccessHint,
        ConfigMgrSessionException { Failure: ConfigMgrFailure.Unexpected } => fallback,
        ConfigMgrSessionException s => SecretMask.Apply(s.Message),
        _ => fallback
    };

    private static string FailureName(Exception ex) =>
        ex is ConfigMgrSessionException s ? $"{nameof(ConfigMgrSessionException)} {s.Failure}" : ex.GetType().Name;

    private void Rebuild()
    {
        if (_collections is null || _windows is null) return;
        CollectionFolderFilter.Fill(Folders, _collections.Select(c => c.FolderPath), FolderFilter);
        ApplyWindowFilter();
        ApplyCoverageFilter();
    }

    private Dictionary<string, CollectionInfo> CollectionsById() =>
        (_collections ?? []).GroupBy(c => c.CollectionId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, int> WindowCounts() =>
        (_windows ?? []).GroupBy(w => w.CollectionId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    private IEnumerable<MaintenanceWindowRow> AllRows()
    {
        if (_collections is null || _windows is null) yield break;
        var byId = CollectionsById();
        foreach (var window in _windows)
            yield return new MaintenanceWindowRow(byId.TryGetValue(window.CollectionId, out var c) ? c : new CollectionInfo(window.CollectionId, window.CollectionName, "", 0, false), window);
        var counts = WindowCounts();
        foreach (var collection in _collections.Where(c => !counts.ContainsKey(c.CollectionId)))
            yield return new MaintenanceWindowRow(collection, null);
    }

    private void ApplyWindowFilter()
    {
        if (!IsDataLoaded) return;
        var text = TextFilter.Trim();
        var selected = SelectedRow;
        var all = AllRows().ToList();
        var shownRows = new List<MaintenanceWindowRow>();
        foreach (var row in all.OrderBy(r => r.CollectionName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.WindowName, StringComparer.OrdinalIgnoreCase))
        {
            if (OnlyCollectionsWithWindows && !row.HasWindow) continue;
            if (TypeFilter.Length > 0 && !string.Equals(row.Window?.WindowType, TypeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (EnabledFilter == Enabled && !(row.Window?.Enabled ?? false)) continue;
            if (EnabledFilter == Disabled && (row.Window?.Enabled ?? true)) continue;
            if (!CollectionFolderFilter.Matches(FolderFilter, row.FolderPath)) continue;
            if (text.Length > 0 && !row.CollectionName.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !row.WindowName.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !row.ScheduleText.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !row.CollectionId.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            shownRows.Add(row);
        }
        ReplaceRows(shownRows);
        SelectedRow = selected is null ? null : Rows.FirstOrDefault(r => SameRow(r, selected));
        var stillShown = _selectedWindows.Select(s => Rows.FirstOrDefault(r => SameRow(r, s))).OfType<MaintenanceWindowRow>().ToList();
        if (!stillShown.SequenceEqual(_selectedWindows, ReferenceEqualityComparer.Instance)) SetSelectedWindows(stillShown);
        var windows = all.Count(r => r.HasWindow);
        var shown = Rows.Count(r => r.HasWindow);
        RowCount = shown == windows ? Count(windows, "window") : $"{shown} of {Count(windows, "window")}";
    }

    /// <summary>
    /// Changes <see cref="Rows"/> to <paramref name="wanted"/> with the fewest changes: a row that
    /// stays shown keeps its object, so the grid keeps its selection and scroll position.
    /// </summary>
    private void ReplaceRows(IReadOnlyList<MaintenanceWindowRow> wanted)
    {
        var keep = new HashSet<MaintenanceWindowRow>(wanted);
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= Rows.Count || !Equals(Rows[i], wanted[i])) Rows.Insert(i, wanted[i]);
        // Rows that kept their object but moved in the order end up after the wanted rows.
        while (Rows.Count > wanted.Count) Rows.RemoveAt(Rows.Count - 1);
    }

    private static bool SameRow(MaintenanceWindowRow a, MaintenanceWindowRow b) =>
        string.Equals(a.CollectionId, b.CollectionId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.WindowName, b.WindowName, StringComparison.OrdinalIgnoreCase);

    private void ApplyCoverageFilter()
    {
        if (!IsDataLoaded) return;
        var counts = WindowCounts();
        Coverage.Clear();
        var all = _collections!.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CoverageRow(c, counts.TryGetValue(c.CollectionId, out var n) ? n : 0)).ToList();
        foreach (var row in all)
        {
            if (CoverageWithoutWindowOnly && row.HasWindow) continue;
            if (!CollectionFolderFilter.Matches(FolderFilter, row.FolderPath)) continue;
            Coverage.Add(row);
        }
        var inFolder = all.Where(r => CollectionFolderFilter.Matches(FolderFilter, r.FolderPath)).ToList();
        var without = inFolder.Count(r => !r.HasWindow);
        CoverageCount = $"{Count(inFolder.Count, "collection")}, {without} without a window";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private void ShowDetails(MaintenanceWindowRow? row)
    {
        Properties.Clear();
        NextOccurrences.Clear();
        if (row?.Window is not { } w) return;
        var s = w.Schedule;
        Properties.Add(new PropertyRow("Collection", w.CollectionName));
        Properties.Add(new PropertyRow("Collection ID", w.CollectionId));
        Properties.Add(new PropertyRow("Folder", string.IsNullOrEmpty(row.FolderPath) ? "(root)" : row.FolderPath));
        Properties.Add(new PropertyRow("Window", w.WindowName));
        Properties.Add(new PropertyRow("Window ID", w.ServiceWindowId));
        Properties.Add(new PropertyRow("Type", MaintenanceWindowsText.WindowType(w.WindowType)));
        Properties.Add(new PropertyRow("Status", w.Enabled ? Enabled : Disabled));
        Properties.Add(new PropertyRow("Recurrence", row.Recurrence));
        Properties.Add(new PropertyRow("Schedule", w.ScheduleText));
        if (!row.IsScheduleHeld)
        {
            Properties.Add(new PropertyRow("Note", ScheduleNotHeld));
            NextOccurrences.Add(NoOccurrencesShown);
            return;
        }
        Properties.Add(new PropertyRow("Start", MaintenanceWindowsText.Time(s.Start)));
        Properties.Add(new PropertyRow("Duration", MaintenanceWindowsText.Duration(s.Duration)));
        Properties.Add(new PropertyRow("Time", s.IsUtc ? "UTC" : "Local time of the client"));
        if (OwnDescription(w) is { } description) Properties.Add(new PropertyRow("Description", description));
        foreach (var line in Project(s, MaintenanceWindowEditorViewModel.PreviewCount)) NextOccurrences.Add(line);
    }

    private string? OwnDescription(MaintenanceWindowInfo? window)
    {
        try { return MaintenanceWindowService.OwnDescription(window, _services.Projector); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    /// <summary>The plan text for a change that keeps the own description of <paramref name="window"/>; empty when it has none.</summary>
    private string KeepsDescriptionText(MaintenanceWindowInfo? window) => OwnDescription(window) is null ? "" : " " + KeepsDescription;

    private IReadOnlyList<string> Project(MaintenanceSchedule schedule, int count)
    {
        try
        {
            var next = _services.Projector.NextOccurrences(schedule, _services.Now(), count);
            return next.Count == 0 ? [MaintenanceWindowEditorViewModel.NotProjected] : next.Select(n => MaintenanceWindowsText.Occurrence(n, schedule.Duration)).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ["The schedule cannot be projected."];
        }
    }

    private async Task LoadTemplatesAsync()
    {
        try
        {
            var templates = await Task.Run(_services.Templates.All);
            _templates = templates;
            var selected = SelectedTemplate?.Template.Id;
            Templates.Clear();
            foreach (var t in templates.OrderBy(t => t.IsShipped ? 0 : 1).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                Templates.Add(new TemplateRow(t, TemplateScheduleText(t.Schedule)));
            SelectedTemplate = Templates.FirstOrDefault(t => t.Template.Id == selected);
        }
        catch (Exception ex)
        {
            StatusMessage = "The templates could not be read. The log has the details.";
            _services.Log.Warning(LogArea, $"The templates could not be read ({ex.GetType().Name}).");
        }
    }

    private string Describe(MaintenanceSchedule schedule)
    {
        try { return _services.Projector.Describe(schedule); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
    }

    /// <summary>
    /// A template's start date is a placeholder; the text describes the schedule as applying
    /// it today would create it, without the effective date, and adds its first occurrence.
    /// </summary>
    private string TemplateScheduleText(MaintenanceSchedule schedule)
    {
        var today = schedule.EffectiveOn(_services.Now().Date);
        var text = Describe(today);
        var cut = text.IndexOf(" effective ", StringComparison.Ordinal);
        if (cut > 0) text = text[..cut];
        if (text.Length == 0) return text;
        // Project returns either occurrences or one sentence that ends with a period.
        var next = Project(today, 1)[0];
        return next.EndsWith('.') ? $"{text}. {next}" : $"{text}. Next if applied today: {next}.";
    }

    /// <summary>
    /// Reads the current state of each request, builds the plan from it and shows the plan.
    /// The writer applies only the changes of the plan the operator confirms. Null when no
    /// plan was shown; the reason is in <see cref="StatusMessage"/> and passed to <paramref name="report"/>.
    /// With more than one request, a request whose state cannot be read becomes a Skip line with
    /// the plain reason. <paramref name="checkState"/> false sends the requests as they are: the
    /// service checks the state of each change again when it writes it.
    /// </summary>
    private async Task<(MaintenancePlan Plan, ChangePlanOutcome Outcome)?> RunPlanAsync(
        IReadOnlyList<MaintenanceChange> requests,
        Func<IReadOnlyList<MaintenanceChange>, (MaintenancePlan? Plan, string? Problem)> build,
        Action<string>? report = null,
        bool checkState = true)
    {
        if (_writing)
        {
            // A refused start says why when a run still works in the background.
            if (_background) { StatusMessage = BackgroundRunText; report?.Invoke(BackgroundRunText); }
            return null;
        }
        _writing = true;
        _lastRunPlan = null;
        NotifyWriteCommands();
        var detached = false;
        try
        {
            var generation = _generation;
            void Refuse(string message)
            {
                StatusMessage = message;
                report?.Invoke(message);
            }

            var prepared = new List<MaintenanceChange>(requests.Count);
            var skips = new List<MaintenancePlanEntry>();
            if (!checkState) prepared.AddRange(requests);
            else
            {
                var checks = await Task.Run(async () =>
                {
                    var list = new List<(MaintenanceChange Request, MaintenanceChange? Prepared, Exception? Failure)>(requests.Count);
                    foreach (var request in requests)
                    {
                        try { list.Add((request, await _services.Windows.PrepareAsync(request, CancellationToken.None), null)); }
                        catch (Exception ex) { list.Add((request, null, ex)); }
                    }
                    return list;
                });
                foreach (var (request, ready, failure) in checks)
                {
                    if (failure is null)
                    {
                        prepared.Add(ready!);
                        continue;
                    }
                    _services.Log.Warning(LogArea, $"The state before a write could not be read ({FailureName(failure)}).");
                    var reason = PlainMessage(failure, PrepareFailed);
                    skips.Add(MaintenancePlanEntry.Skip(new ChangePlanItem("Skip", MaintenanceWindowsText.Target(request.CollectionName, request.WindowName), reason), reason));
                }
                if (skips.Count > 0 && (requests.Count == 1 || prepared.Count == 0))
                {
                    Refuse(skips[0].SkipReason!);
                    return null;
                }
            }
            if (generation != _generation) return null;

            var (built, problem) = build(prepared);
            if (built is null)
            {
                if (problem is not null) Refuse(problem);
                return null;
            }
            var plan = skips.Count == 0 ? built
                : new MaintenancePlan(built.Action, built.Plan.Title, built.Plan.ActionName, [.. skips, .. built.Entries], built.Plan.IsDestructive, built.Plan.HasUndo);

            var outcome = _services.Plans.Show(plan.Plan, _services.Writer.RunFor(plan), plan.Plan.HasUndo ? _services.Writer.UndoFor(plan) : null);
            // A window force closed while the run works: the run still changes ConfigMgr, so every
            // write stays blocked and no list is read until the run ends.
            if (outcome.Result?.Stop == ChangePlanStop.Detached || outcome.UndoResult?.Stop == ChangePlanStop.Detached)
            {
                detached = true;
                _background = true;
                _ = WaitForBackgroundRunAsync(plan, outcome.UndoResult is null);
                Refuse(BackgroundRunText);
                return null;
            }
            if (outcome.Confirmed) _lastRunPlan = plan;
            ShowOutcome(plan, outcome);
            return (plan, outcome);
        }
        finally
        {
            if (!detached)
            {
                _writing = false;
                NotifyWriteCommands();
            }
        }
    }

    public const string BackgroundRunText =
        "The plan window was closed while the run was still working. The run continues in the background and can still change ConfigMgr. Wait until it ends before you start another change.";
    public const string BackgroundRunEndedText = "The run that continued in the background has ended. Refresh the lists to see the result.";

    /// <summary>Keeps every write blocked until the run that a closed plan window left working ends. No list is read meanwhile; the Undo of the run is offered when it ends.</summary>
    private async Task WaitForBackgroundRunAsync(MaintenancePlan plan, bool offerUndo)
    {
        try { await _services.Writer.Running; }
        catch (Exception ex)
        {
            _services.Log.Warning(LogArea, $"A run that continued in the background failed ({FailureName(ex)}).");
        }
        finally
        {
            LastPlan = offerUndo && plan.UndoChanges().Count > 0 ? plan : null;
            StatusMessage = BackgroundRunEndedText;
            _background = false;
            _writing = false;
            NotifyWriteCommands();
        }
    }

    private void NotifyWriteCommands()
    {
        EditWindowCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        RemoveWindowsCommand.NotifyCanExecuteChanged();
        UndoLastCommand.NotifyCanExecuteChanged();
    }

    private void ShowOutcome(MaintenancePlan plan, ChangePlanOutcome outcome)
    {
        if (!outcome.Confirmed || outcome.Result is not { } result)
        {
            StatusMessage = NotConfirmed;
            return;
        }
        LastResults.Clear();
        foreach (var row in result.Items) LastResults.Add(row);
        var summary = result.Summary;
        if (outcome.UndoResult is { } undo)
        {
            foreach (var row in undo.Items) LastResults.Add(row with { Message = "Undo: " + row.Message });
            summary += " Undo: " + undo.Summary;
        }
        OnPropertyChanged(nameof(HasResults));
        ResultSummary = $"{plan.Plan.Title}: {summary}";
        StatusMessage = ResultSummary;
        LastPlan = outcome.UndoResult is null && plan.UndoChanges().Count > 0 ? plan : null;
    }

    /// <summary>
    /// Reads the list again after a write and points at a rename that left both windows. Only
    /// a rename whose outcome carries an undo created the new window; a failed rename without
    /// one changed nothing, even when another window already has the new name.
    /// </summary>
    private async Task AfterWriteAsync(MaintenancePlan? plan)
    {
        // A run left working in the background still writes: nothing is read until it ends.
        if (_writing) return;
        var status = StatusMessage;
        // A load that started before the write can return the state before it; the list is
        // read again once that load ends.
        while (_loading is { IsCompleted: false } pending) await pending;
        await LoadAsync();
        if (StatusMessage.Length == 0) StatusMessage = status;
        if (plan is null || _windows is null) return;
        var outcomes = plan.Outcomes;
        foreach (var entry in plan.Entries.Where(e => e.Change is { Kind: ChangeKind.Rename, NewWindowName: not null }))
        {
            var change = entry.Change!;
            var outcome = outcomes.FirstOrDefault(o => ReferenceEquals(plan.EntryFor(o.Change), entry));
            if (outcome is null || outcome.Succeeded) continue;
            if (outcome.UndoChange is null)
            {
                StatusMessage = RenameNotMade(change.CollectionName, change.WindowName, change.NewWindowName!);
                break;
            }
            bool Exists(string name) => _windows.Any(w => string.Equals(w.CollectionId, change.CollectionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(w.WindowName, name, StringComparison.OrdinalIgnoreCase));
            if (!Exists(change.WindowName) || !Exists(change.NewWindowName!)) continue;
            StatusMessage = BothWindowsExist(change.CollectionName, change.WindowName, change.NewWindowName!);
            SelectedRow = Rows.FirstOrDefault(r => string.Equals(r.CollectionId, change.CollectionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.WindowName, change.WindowName, StringComparison.OrdinalIgnoreCase));
            if (SelectedRow is { } old) SetSelectedWindows([old]);
            break;
        }
    }

    private static string NextText(MaintenanceWindowInfo window, IReadOnlyList<string> next) =>
        next.Count == 0 ? "" : " Next: " + string.Join("; ", next) + ".";

    private ChangePlanItem CreateItem(MaintenanceWindowInfo after, string? prefix = null) =>
        new("Create", MaintenanceWindowsText.Target(after.CollectionName, after.WindowName),
            $"{prefix}{MaintenanceWindowsText.WindowType(after.WindowType)}. {after.ScheduleText}.{NextText(after, Project(after.Schedule, MaintenanceWindowEditorViewModel.PreviewCount))}");

    private (MaintenancePlan?, string?) BuildEditorPlan(IReadOnlyList<MaintenanceChange> prepared)
    {
        var change = prepared[0];
        var after = change.After!;
        // The editor's values carry the enabled state of the list read; the write sets the
        // enabled state too, so a toggle made since the read would be undone without this.
        if (change.Kind is ChangeKind.Update or ChangeKind.Rename && change.Before is { } current)
        {
            after = after with { Enabled = current.Enabled };
            change = change with { After = after };
        }
        switch (change.Kind)
        {
            case ChangeKind.Create:
                if (change.Before is not null) return (null, MaintenanceWindowEditorViewModel.NameTaken(change.CollectionName, change.WindowName));
                return (new MaintenancePlan("Create", "New maintenance window", "Create window",
                    [MaintenancePlanEntry.Write(CreateItem(after), change)]), null);
            case ChangeKind.Update:
                if (change.Before is not { } before) return (null, WindowGone(change.CollectionName, change.WindowName));
                return (new MaintenancePlan("Update", "Edit maintenance window", "Save changes",
                [
                    MaintenancePlanEntry.Write(new ChangePlanItem("Update", MaintenanceWindowsText.Target(change.CollectionName, change.WindowName),
                        $"Old: {MaintenanceWindowsText.WindowType(before.WindowType)}, {before.ScheduleText}, {DurationText(before)}. New: {MaintenanceWindowsText.WindowType(after.WindowType)}, {after.ScheduleText}, {DurationText(after)}.{KeepsDescriptionText(before)}"), change)
                ]), null);
            default:
                if (change.Before is not { } old) return (null, WindowGone(change.CollectionName, change.WindowName));
                return (new MaintenancePlan("Rename", "Rename maintenance window", "Rename window",
                [
                    MaintenancePlanEntry.Write(new ChangePlanItem("Rename", MaintenanceWindowsText.Target(change.CollectionName, change.WindowName),
                        $"New name: {change.NewWindowName}. {change.NewWindowName} is created first; {change.WindowName} is removed only after that. Old: {old.ScheduleText}, {DurationText(old)}. New: {after.ScheduleText}, {DurationText(after)}.{KeepsDescriptionText(old)}"), change)
                ]), null);
        }
    }

    /// <summary>ConfigMgr's schedule text has no duration, so a duration-only edit needs it stated.</summary>
    private static string DurationText(MaintenanceWindowInfo window) => "duration " + MaintenanceWindowsText.Duration(window.Schedule.Duration);

    /// <summary>The editor's Save: one change, its plan, and true when the write changed ConfigMgr.</summary>
    private async Task<bool> SaveWindowAsync(MaintenanceWindowEditorViewModel editor)
    {
        if (editor.BuildWindow() is not { } after) return false;
        MaintenanceChange request;
        if (editor.Original is not { } original)
            request = new MaintenanceChange(ChangeKind.Create, after.CollectionId, after.CollectionName, after.WindowName, null, null, after);
        else if (string.Equals(original.WindowName, after.WindowName, StringComparison.OrdinalIgnoreCase))
            request = new MaintenanceChange(ChangeKind.Update, original.CollectionId, original.CollectionName, original.WindowName, null, original, after);
        else
            request = new MaintenanceChange(ChangeKind.Rename, original.CollectionId, original.CollectionName, original.WindowName, after.WindowName, original, after);

        var run = await RunPlanAsync([request], BuildEditorPlan, editor.ShowMessage);
        if (run is not { } done) return false;
        if (done.Outcome.Result is not { } result)
        {
            editor.ShowMessage(NotConfirmed);
            return false;
        }
        if (result.SucceededCount > 0) return true;
        editor.ShowMessage(result.Items.FirstOrDefault(i => !i.Succeeded)?.Message ?? NotConfirmed);
        return false;
    }

    private MaintenanceWindowEditorViewModel NewEditor(MaintenanceEditorMode mode, Func<MaintenanceWindowEditorViewModel, Task<bool>> save)
    {
        var editor = new MaintenanceWindowEditorViewModel(mode, _services.Projector, save, _services.Now, _collections, _windows);
        if (double.IsFinite(_editorWidth) && _editorWidth >= 400) editor.Width = _editorWidth;
        if (double.IsFinite(_editorHeight) && _editorHeight >= 400) editor.Height = _editorHeight;
        return editor;
    }

    private void KeepEditorSize(MaintenanceWindowEditorViewModel editor)
    {
        _editorWidth = editor.Width;
        _editorHeight = editor.Height;
        _ = SavePreferencesAsync();
    }

    [RelayCommand]
    private async Task NewWindow()
    {
        if (!IsDataLoaded)
        {
            StatusMessage = ConnectionMessage.Length > 0 ? ConnectionMessage : RefreshFirst;
            return;
        }
        var editor = NewEditor(MaintenanceEditorMode.NewWindow, SaveWindowAsync);
        if (SelectedRow is { } row && !row.Collection.IsBuiltIn)
            editor.SelectedCollection = editor.Collections.FirstOrDefault(c => c.Collection.CollectionId == row.CollectionId);
        _services.Prompts.ShowEditor(editor);
        KeepEditorSize(editor);
        if (editor.Saved || _lastRunPlan is not null) await AfterWriteAsync(_lastRunPlan);
    }

    private bool CanEdit() => !_writing && SelectedRow is { HasWindow: true, IsScheduleHeld: true };

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditWindow()
    {
        if (!CanEdit() || SelectedRow?.Window is not { } window) return;
        var editor = NewEditor(MaintenanceEditorMode.EditWindow, SaveWindowAsync);
        editor.Load(window);
        _services.Prompts.ShowEditor(editor);
        KeepEditorSize(editor);
        if (editor.Saved || _lastRunPlan is not null) await AfterWriteAsync(_lastRunPlan);
    }

    private IReadOnlyList<MaintenanceWindowInfo> SelectedWindowInfos() =>
        _selectedWindows.Where(r => r.Window is not null).Select(r => r.Window!).ToList();

    private bool CanChangeSelection() => !_writing && _selectedWindows.Any(r => r.HasWindow);

    private bool CanRemoveSelection() => CanChangeSelection() && RemoveTooltip is null;

    [RelayCommand(CanExecute = nameof(CanChangeSelection))]
    private async Task ToggleEnabled()
    {
        var windows = SelectedWindowInfos();
        if (windows.Count == 0) return;
        var requests = windows.Select(w => new MaintenanceChange(ChangeKind.SetEnabled, w.CollectionId, w.CollectionName, w.WindowName, null, w,
            w with { Enabled = !w.Enabled })).ToList();
        var run = await RunPlanAsync(requests, prepared =>
        {
            var entries = new List<MaintenancePlanEntry>();
            foreach (var change in prepared)
            {
                if (change.Before is not { } before) return (null, WindowGone(change.CollectionName, change.WindowName));
                var enable = !before.Enabled;
                var after = before with { Enabled = enable };
                entries.Add(MaintenancePlanEntry.Write(new ChangePlanItem(enable ? "Enable" : "Disable",
                    MaintenanceWindowsText.Target(change.CollectionName, change.WindowName), before.ScheduleText), change with { After = after }));
            }
            var enables = entries.Count(e => e.Item.Action == "Enable");
            var action = enables == entries.Count ? $"Enable {Count(entries.Count, "window")}"
                : enables == 0 ? $"Disable {Count(entries.Count, "window")}"
                : $"Change {Count(entries.Count, "window")}";
            return (new MaintenancePlan("Toggle", "Enable or disable maintenance windows", action, entries), null);
        });
        if (run is { Outcome.Confirmed: true } done) await AfterWriteAsync(done.Plan);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelection))]
    private async Task RemoveWindows()
    {
        var windows = SelectedWindowInfos();
        if (windows.Count == 0 || RemoveTooltip is not null) return;
        var requests = windows.Select(w => new MaintenanceChange(ChangeKind.Remove, w.CollectionId, w.CollectionName, w.WindowName, null, w, null)).ToList();
        var run = await RunPlanAsync(requests, prepared =>
        {
            var entries = new List<MaintenancePlanEntry>();
            foreach (var change in prepared)
            {
                if (change.Before is not { } before) return (null, WindowGone(change.CollectionName, change.WindowName));
                entries.Add(MaintenancePlanEntry.Write(new ChangePlanItem("Remove", MaintenanceWindowsText.Target(change.CollectionName, change.WindowName),
                    $"{MaintenanceWindowsText.WindowType(before.WindowType)}, {before.ScheduleText}, {MaintenanceWindowsText.Duration(before.Schedule.Duration)}. "
                    + "Undo recreates it from these values. " + MaintenanceWindowWriter.RecreatedNote), change));
            }
            return (new MaintenancePlan("Remove", "Remove maintenance windows", $"Remove {Count(entries.Count, "window")}", entries, isDestructive: true), null);
        });
        if (run is { Outcome.Confirmed: true } done) await AfterWriteAsync(done.Plan);
    }

    private bool CanUndoLast() => !_writing && LastPlan is not null;

    /// <summary>Undoes the last write set through a new plan: the undo changes the last run reported.</summary>
    [RelayCommand(CanExecute = nameof(CanUndoLast))]
    private async Task UndoLast()
    {
        if (LastPlan is not { } last) return;
        var undo = last.UndoChanges();
        if (undo.Count == 0)
        {
            StatusMessage = NothingToUndo;
            LastPlan = null;
            return;
        }
        var run = await RunPlanAsync(undo.Select(u => u.Undo).ToList(), prepared =>
        {
            var entries = prepared.Select((change, i) => MaintenancePlanEntry.Write(new ChangePlanItem("Undo " + undo[i].Entry.Item.Action,
                undo[i].Entry.Item.Target, UndoDetail(change)), change)).ToList();
            var destructive = prepared.Any(c => c.Kind is ChangeKind.Remove or ChangeKind.Rename);
            return (new MaintenancePlan("Undo " + last.Action, "Undo: " + last.Plan.Title, $"Undo {Count(entries.Count, "change")}", entries, destructive), null);
        }, checkState: false);
        if (run is { Outcome.Confirmed: true } done) await AfterWriteAsync(done.Plan);
    }

    private string UndoDetail(MaintenanceChange change) => change.Kind switch
    {
        ChangeKind.Remove => $"Removes {change.WindowName}.",
        ChangeKind.Create or ChangeKind.ApplyTemplate => $"Recreates {change.WindowName}: {change.After?.ScheduleText}. {MaintenanceWindowWriter.RecreatedNote}{KeepsDescriptionText(change.After)}",
        ChangeKind.SetEnabled => change.After?.Enabled == true ? $"Enables {change.WindowName}." : $"Disables {change.WindowName}.",
        ChangeKind.Rename => $"Renames {change.WindowName} to {change.NewWindowName}.",
        _ => $"Sets the old values back: {change.After?.ScheduleText}."
    };

    [RelayCommand]
    private Task ApplyTemplateToSelected() => ShowApplyTemplateAsync(null, _selectedCoverage.Where(r => !r.Collection.IsBuiltIn).Select(r => r.CollectionId));

    private bool HasSelectedTemplate() => SelectedTemplate is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedTemplate))]
    private Task ApplySelectedTemplate() => ShowApplyTemplateAsync(SelectedTemplate?.Template, []);

    private async Task ShowApplyTemplateAsync(MaintenanceTemplate? template, IEnumerable<string> checkedIds)
    {
        if (!IsDataLoaded)
        {
            StatusMessage = ConnectionMessage.Length > 0 ? ConnectionMessage : RefreshFirst;
            return;
        }
        if (_templates.Count == 0)
        {
            StatusMessage = "There is no template to apply.";
            return;
        }
        var dialog = new MaintenanceApplyTemplateViewModel(_templates, _collections!, WindowCounts(), ApplyTemplateAsync, template, checkedIds);
        _services.Prompts.ShowApplyTemplate(dialog);
        if (dialog.Applied || _lastRunPlan is not null) await AfterWriteAsync(_lastRunPlan);
    }

    /// <summary>The first free name on the collection: the name, then the name with (2), (3) and so on.</summary>
    public static string FreeName(string name, ICollection<string> taken)
    {
        if (!taken.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{name} ({i})");
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    private async Task<bool> ApplyTemplateAsync(MaintenanceApplyTemplateViewModel dialog)
    {
        if (dialog.SelectedTemplate is not { } template) return false;
        var now = _services.Now();
        var schedule = template.Schedule.EffectiveOn(now.Date);
        var text = Describe(schedule);
        var skips = new List<(CollectionInfo Collection, ChangePlanItem Item)>();
        var requests = new List<MaintenanceChange>();
        foreach (var collection in dialog.CheckedCollections)
        {
            var taken = new HashSet<string>((_windows ?? []).Where(w => string.Equals(w.CollectionId, collection.CollectionId, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.WindowName), StringComparer.OrdinalIgnoreCase);
            if (taken.Contains(template.Name) && !dialog.RenameExisting)
            {
                skips.Add((collection, SkipItem(collection, template.Name)));
                continue;
            }
            var name = FreeName(template.Name, taken);
            var after = new MaintenanceWindowInfo(collection.CollectionId, collection.Name, name, "", template.WindowType, true, schedule, text, Next(schedule, now), "");
            requests.Add(new MaintenanceChange(ChangeKind.ApplyTemplate, collection.CollectionId, collection.Name, name, null, null, after));
        }
        if (requests.Count == 0)
        {
            var message = AllSkipped(template.Name);
            StatusMessage = message;
            dialog.ShowMessage(message);
            return false;
        }

        var run = await RunPlanAsync(requests, prepared =>
        {
            var entries = new List<MaintenancePlanEntry>();
            foreach (var (collection, item) in skips) entries.Add(MaintenancePlanEntry.Skip(item, SkipReason(template.Name)));
            foreach (var change in prepared)
            {
                if (change.Before is not null)
                {
                    entries.Add(MaintenancePlanEntry.Skip(SkipItem(new CollectionInfo(change.CollectionId, change.CollectionName, "", 0, false), change.WindowName),
                        SkipReason(change.WindowName)));
                    continue;
                }
                var renamed = !string.Equals(change.WindowName, template.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"Named {change.WindowName} because {template.Name} exists. " : "";
                entries.Add(MaintenancePlanEntry.Write(CreateItem(change.After!, $"From template {template.Name}. {renamed}"), change));
            }
            var writes = entries.Count(e => e.Change is not null);
            if (writes == 0) return (null, AllSkipped(template.Name));
            return (new MaintenancePlan("Apply template", $"Apply template {template.Name}", $"Create {Count(writes, "window")}", entries), null);
        }, dialog.ShowMessage);
        if (run is not { } done) return false;
        if (done.Outcome.Result is null) dialog.ShowMessage(NotConfirmed);
        return done.Outcome.Confirmed && done.Outcome.Result is { SucceededCount: > 0 };
    }

    private static ChangePlanItem SkipItem(CollectionInfo collection, string name) =>
        new("Skip", MaintenanceWindowsText.Target(collection.Name, name), SkipReason(name));

    private static string SkipReason(string name) => $"The collection already has a window named {name}. Not changed.";

    private DateTime? Next(MaintenanceSchedule schedule, DateTime now)
    {
        try { return _services.Projector.NextOccurrences(schedule, now, 1).Cast<DateTime?>().FirstOrDefault(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }

    [RelayCommand]
    private void NewTemplate()
    {
        var editor = NewEditor(MaintenanceEditorMode.NewTemplate, SaveTemplateAsync);
        _services.Prompts.ShowEditor(editor);
        KeepEditorSize(editor);
    }

    private bool CanEditTemplate() => SelectedTemplate is { Template.IsShipped: false };

    [RelayCommand(CanExecute = nameof(CanEditTemplate))]
    private void EditTemplate()
    {
        if (SelectedTemplate is not { Template.IsShipped: false } row) return;
        var editor = NewEditor(MaintenanceEditorMode.EditTemplate, SaveTemplateAsync);
        editor.Load(row.Template);
        _services.Prompts.ShowEditor(editor);
        KeepEditorSize(editor);
    }

    private async Task<bool> SaveTemplateAsync(MaintenanceWindowEditorViewModel editor)
    {
        if (editor.BuildTemplate() is not { } template) return false;
        try
        {
            await Task.Run(() => _services.Templates.Save(template));
        }
        catch (Exception ex)
        {
            var message = ex is MaintenanceWindowException m ? SecretMask.Apply(m.Message) : TemplateSaveFailed;
            editor.ShowMessage(message);
            _services.Log.Warning(LogArea, $"A template could not be saved ({ex.GetType().Name}).");
            return false;
        }
        _services.Log.Info(LogArea, "Saved a user template.");
        await LoadTemplatesAsync();
        SelectedTemplate = Templates.FirstOrDefault(t => t.Template.Id == template.Id);
        StatusMessage = $"Saved the template {template.Name}.";
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanEditTemplate))]
    private async Task DeleteTemplate()
    {
        if (SelectedTemplate is not { Template.IsShipped: false } row) return;
        if (!await _services.Prompts.ConfirmDeleteTemplateAsync(row.Name)) return;
        try
        {
            await Task.Run(() => _services.Templates.Delete(row.Template.Id));
        }
        catch (Exception ex)
        {
            StatusMessage = ex is MaintenanceWindowException m ? SecretMask.Apply(m.Message) : TemplateDeleteFailed;
            _services.Log.Warning(LogArea, $"A template could not be deleted ({ex.GetType().Name}).");
            return;
        }
        _services.Log.Info(LogArea, "Deleted a user template.");
        await LoadTemplatesAsync();
        StatusMessage = $"Deleted the template {row.Name}.";
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync("CSV", _services.Reports.WriteCsv);

    [RelayCommand]
    private Task ExportHtml() => ExportAsync("HTML", _services.Reports.WriteHtml);

    private async Task ExportAsync(string format, Func<IReadOnlyList<MaintenanceWindowInfo>, string> write)
    {
        var rows = Rows.Where(r => r.Window is not null).Select(r => r.Window!).ToList();
        if (rows.Count == 0)
        {
            StatusMessage = NothingToExport;
            return;
        }
        string path;
        try
        {
            path = await Task.Run(() => write(rows));
        }
        catch (Exception ex)
        {
            StatusMessage = ex is MaintenanceWindowException m ? SecretMask.Apply(m.Message) : ReportFailed(format);
            _services.Log.Warning(LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        StatusMessage = $"Saved the {format} report to {path}.";
        _services.Log.Info(LogArea, $"Saved a {format} report with {rows.Count} windows.");
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) _services.Prompts.OpenFolder(folder);
    }
}
