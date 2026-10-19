using System.IO;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.ConfigMgrSession;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Collection and Compliance page reads and writes; the page logic depends on nothing else.</summary>
/// <param name="OpenComplianceTool">Starts the suite tool for compliance baselines; returns a message when it could not start.</param>
public sealed record CollectionCompliancePageServices(
    ICollectionService Collections,
    IQueryTemplateStore Templates,
    ICollectionReportWriter Reports,
    ICollectionCompliancePrompts Prompts,
    IChangePlanDialogs Plans,
    CollectionWriter Writer,
    Func<AppSettings> LoadSettings,
    Func<CollectionComplianceSettings?> LoadPreferences,
    Action<CollectionComplianceSettings> SavePreferences,
    Func<Task<string?>> OpenComplianceTool,
    IAppLog Log);

/// <summary>
/// The Collection and Compliance page: Collections, WQL Editor, Templates and Site Queries.
/// Nothing reaches ConfigMgr before the first visit; every write goes through a change plan the
/// operator confirms, and <see cref="CollectionWriter"/> is the only caller of the service write.
/// </summary>
public sealed partial class CollectionComplianceViewModel : ObservableObject
{
    public const string LogArea = CollectionWriter.LogArea;
    public const int CollectionsTab = 0;
    public const int WqlTab = 1;
    public const int TemplatesTab = 2;
    public const int SiteQueriesTab = 3;
    public const int MaxPreviewRows = 50;
    public const int MaxDeviceNames = 200;

    public const string BuiltInFilter = "BuiltIn";
    public const string CustomFilter = "Custom";
    public const string AllCategories = "All categories";

    public const string NotInUseMessage = "ConfigMgr is not in use. Select it under Systems in use in Settings.";
    public const string SiteCodeMessage = "Set the site code in Settings, ConfigMgr Site Settings.";
    public const string SiteHint = " Check the site code and the SMS Provider in Settings, ConfigMgr Site Settings.";
    public const string AccessHint = " The ConfigMgr console role needs collection and query rights.";
    public const string LoadFailed = "The collections could not be read. The log has the details.";
    public const string DetailFailed = "The details of the collection could not be read. The log has the details.";
    public const string QueriesFailed = "The saved queries could not be read. The log has the details.";
    public const string ConnectFailed = "The site could not be reached. The log has the details.";
    public const string WqlFailed = "The query could not be checked. The log has the details.";
    public const string PrepareFailed = "The current state could not be read. The log has the details.";
    public const string LookupFailed = "The device names could not be looked up. The log has the details.";
    public const string NotConfirmed = "Nothing was changed.";
    public const string NothingToChange = "Nothing to change.";
    public const string RefreshFirst = "Refresh the list first.";
    public const string NothingToExport = "The list has no collections to export.";
    public const string NothingToUndo = "There is no change to undo.";
    public const string InvalidCollectionId = "Enter a collection ID of eight characters, for example LAB00012.";
    public const string OpenedById = "1 collection, opened by ID. Refresh reads the whole list.";
    public const string BuiltInRefused = "A built-in collection cannot be changed or removed here.";
    public const string EvaluateNote = "The update starts membership evaluation work on the site server.";
    public const string NoUndoNote = "Undo: not applicable.";
    public const string MembershipNote = "An include or exclude rule changes the membership evaluation of the collection.";
    public const string DeviceNotFound = "No device with this name was found. Not added.";
    public const string NoDeviceFound = "No device with these names was found.";
    public const string NoDirectMembers = "Select one or more direct members.";
    public const string PickCollection = "Select a collection in the tree first.";
    public const string RuleNameRequired = "Enter a rule name.";
    public const string QueryRequired = "Enter a WQL query.";
    public const string PickRule = "Select a query rule of the collection.";
    public const string UpdateOrderNote = "The new rule is added under a temporary name first and the old rule is removed only after that, so the collection never has fewer rules.";
    public const string NoPreviewNote = "Preview was not run for this query.";
    public const string PreviewCancelled = "The preview was cancelled.";
    public const string ComplianceStarted = "Collection and Compliance Manager starts for the compliance baselines.";
    public const string RemoveUndoNote = "Undo recreates the collection with its properties and rules.";

    public static string CollectionNotFound(string id) => $"No collection with the ID {id} exists.";
    public static string Connected(string site) => $"Connected to site {site}.";
    public static string RuleNameTaken(string name) => $"The collection already has a query rule named {name}.";
    public static string RuleNameExists(string name) => $"The collection already has a rule named {name}. Not changed.";
    public static string PreviewNote(int count) => $"The last preview found {CollectionsText.Count(count, "match")}.";
    public static string ReportFailed(string format) => $"The {format} report could not be written. The log has the details.";
    public static string QueryNameTaken(string name) => $"A saved query named {name} already exists.";

    public static string PreviewText(int count, int shown) =>
        count == shown ? $"{CollectionsText.Count(count, "match")}." : $"{CollectionsText.Count(count, "match")}. The first {shown} are shown.";

    private static readonly Regex CollectionIdPattern = new("^[A-Za-z0-9]{3}[0-9A-Fa-f]{5}$", RegexOptions.CultureInvariant);

    private readonly CollectionCompliancePageServices _services;
    private bool _active;
    private bool _preferencesLoaded;
    private bool _applyingPreferences;
    private Task _preferencesLoad = Task.CompletedTask;
    private CollectionComplianceSettings? _storedChoices;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private double _editorWidth;
    private double _editorHeight;

    private IReadOnlyList<CollectionRow>? _collections;
    private bool _partialList;
    private CancellationTokenSource? _load;
    private Task? _loading;
    private int _generation;
    private bool _writing;
    private CollectionPlan? _lastRunPlan;
    private IReadOnlyList<CollectionGridRow> _selectedRows = [];

    public CollectionComplianceViewModel(CollectionCompliancePageServices services)
    {
        _services = services;
    }

    public ObservableCollection<CollectionGridRow> Rows { get; } = [];
    public ObservableCollection<Choice<string>> Folders { get; } = [new("", "All folders")];
    public IReadOnlyList<Choice<string>> TypeFilters { get; } = [new("", "All collections"), new(BuiltInFilter, "Built-in"), new(CustomFilter, "Custom")];
    public ObservableCollection<ChangePlanItemResult> LastResults { get; } = [];

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private int _detailTabIndex;
    [ObservableProperty] private string _textFilter = "";
    [ObservableProperty] private string _typeFilter = "";
    [ObservableProperty] private bool _onlyEmpty;
    [ObservableProperty] private bool _onlyWithDirectMembers;
    [ObservableProperty] private bool _onlyWithQueryRules;
    [ObservableProperty] private string _folderFilter = "";
    [ObservableProperty] private string _openByIdText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow), nameof(BuiltInTooltip))]
    [NotifyCanExecuteChangedFor(nameof(EditCollectionCommand), nameof(CopyCollectionCommand), nameof(RemoveCollectionCommand),
        nameof(AddDirectMembersCommand), nameof(AddIncludeRuleCommand), nameof(AddExcludeRuleCommand))]
    private CollectionGridRow? _selectedRow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(CancelLoadCommand))]
    private bool _isLoading;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _connectionMessage = "";
    [ObservableProperty] private string _rowCount = "";
    [ObservableProperty] private string _resultSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoLastCommand))]
    private CollectionPlan? _lastPlan;

    public bool HasSelectedRow => SelectedRow is not null;
    public bool HasConnectionMessage => ConnectionMessage.Length > 0;
    public bool HasResults => LastResults.Count > 0;
    public bool HasUndo => LastPlan is not null;
    public bool IsDataLoaded => _collections is not null;

    /// <summary>Why Edit and Remove are disabled for the selected collection, or null.</summary>
    public string? BuiltInTooltip => SelectedRow?.IsBuiltIn == true ? BuiltInRefused : null;

    public IReadOnlyList<CollectionGridRow> SelectedRows => _selectedRows;

    partial void OnConnectionMessageChanged(string value) => OnPropertyChanged(nameof(HasConnectionMessage));

    public void SetSelectedRows(IEnumerable<CollectionGridRow> rows)
    {
        _selectedRows = rows.ToList();
        EvaluateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the saved choices on the first visit, then the templates and the collections.</summary>
    public async Task OnNavigatedToAsync()
    {
        _active = true;
        if (!_preferencesLoaded)
        {
            _preferencesLoaded = true;
            _preferencesLoad = ApplyPreferencesAsync();
            await _preferencesLoad;
        }
        await LoadTemplatesAsync();
        if (!IsDataLoaded) await LoadAsync();
        if (_active && SelectedTabIndex == SiteQueriesTab && !_queriesLoaded) await StartQueriesLoad();
    }

    /// <summary>Releases the ConfigMgr data; the page reads it again on the next visit.</summary>
    public async Task OnNavigatedFromAsync()
    {
        _active = false;
        // A load still running belongs to the visit that started it; its result is discarded.
        _generation++;
        try { _load?.Cancel(); } catch (ObjectDisposedException) { }
        _load = null;
        _loading = null;
        IsLoading = false;
        _collections = null;
        _partialList = false;
        SelectedRow = null;
        SetSelectedRows([]);
        Rows.Clear();
        RowCount = "";
        ReleaseDetail();
        ReleaseWql();
        ReleaseQueries();
        await SavePreferencesAsync();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        SaveChoicesLater();
        if (_active && value == SiteQueriesTab && !_queriesLoaded && !_applyingPreferences) _ = StartQueriesLoad();
    }

    partial void OnDetailTabIndexChanged(int value) => SaveChoicesLater();
    partial void OnTextFilterChanged(string value) => FilterChanged();
    partial void OnTypeFilterChanged(string value) => FilterChanged();
    partial void OnOnlyEmptyChanged(bool value) => FilterChanged();
    partial void OnOnlyWithDirectMembersChanged(bool value) => FilterChanged();
    partial void OnOnlyWithQueryRulesChanged(bool value) => FilterChanged();
    partial void OnFolderFilterChanged(string value) => FilterChanged();
    partial void OnSelectedRowChanged(CollectionGridRow? value)
    {
        if (!_quietSelection) StartDetail(value, 0);
    }

    private void FilterChanged()
    {
        ApplyFilter();
        SaveChoicesLater();
    }

    private void SaveChoicesLater()
    {
        if (_applyingPreferences) return;
        _ = SavePreferencesAsync();
    }

    private async Task ApplyPreferencesAsync()
    {
        CollectionComplianceSettings? saved;
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
            SelectedTabIndex = saved.SelectedTab is >= 0 and <= SiteQueriesTab ? saved.SelectedTab : 0;
            DetailTabIndex = saved.DetailTab is >= 0 and <= 4 ? saved.DetailTab : 0;
            TextFilter = saved.TextFilter ?? "";
            TypeFilter = saved.TypeFilter is BuiltInFilter or CustomFilter ? saved.TypeFilter : "";
            OnlyEmpty = saved.OnlyEmpty;
            OnlyWithDirectMembers = saved.OnlyWithDirectMembers;
            OnlyWithQueryRules = saved.OnlyWithQueryRules;
            FolderFilter = saved.FolderFilter ?? "";
            CategoryFilter = string.IsNullOrEmpty(saved.TemplateCategory) ? AllCategories : saved.TemplateCategory;
            _editorWidth = saved.EditorWidth;
            _editorHeight = saved.EditorHeight;
        }
        finally { _applyingPreferences = false; }
        _storedChoices = CurrentChoices();
    }

    private CollectionComplianceSettings CurrentChoices() => new()
    {
        SelectedTab = SelectedTabIndex,
        DetailTab = DetailTabIndex,
        TextFilter = TextFilter,
        TypeFilter = TypeFilter,
        OnlyEmpty = OnlyEmpty,
        OnlyWithDirectMembers = OnlyWithDirectMembers,
        OnlyWithQueryRules = OnlyWithQueryRules,
        FolderFilter = FolderFilter,
        TemplateCategory = CategoryFilter == AllCategories ? "" : CategoryFilter,
        EditorWidth = _editorWidth,
        EditorHeight = _editorHeight
    };

    private static bool SameChoices(CollectionComplianceSettings? a, CollectionComplianceSettings b) =>
        a is not null && a.SelectedTab == b.SelectedTab && a.DetailTab == b.DetailTab && a.TextFilter == b.TextFilter && a.TypeFilter == b.TypeFilter
        && a.OnlyEmpty == b.OnlyEmpty && a.OnlyWithDirectMembers == b.OnlyWithDirectMembers && a.OnlyWithQueryRules == b.OnlyWithQueryRules
        && a.FolderFilter == b.FolderFilter && a.TemplateCategory == b.TemplateCategory
        && a.EditorWidth.Equals(b.EditorWidth) && a.EditorHeight.Equals(b.EditorHeight);

    /// <summary>
    /// Writes the choices on a pool thread when they differ from the stored ones. Saves run one at
    /// a time and each reads the choices when its turn comes, so the last write holds the newest.
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

    /// <summary>Reads the collections; a second call while a load runs waits for that load.</summary>
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
        StatusMessage = "Reading the collections.";
        IReadOnlyList<CollectionRow>? collections = null;
        string? connection = null;
        var status = "";
        try
        {
            (connection, collections) = await Task.Run(async () =>
            {
                var problem = ConnectionProblem(_services.LoadSettings());
                if (problem is not null) return (problem, (IReadOnlyList<CollectionRow>?)null);
                return ((string?)null, (IReadOnlyList<CollectionRow>?)await _services.Collections.ListCollectionsAsync(cts.Token));
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            status = "The read was cancelled.";
        }
        catch (Exception ex)
        {
            connection = PlainMessage(ex, LoadFailed);
            if (generation == _generation) _services.Log.Warning(LogArea, $"The collections could not be read ({FailureName(ex)}).");
        }
        finally
        {
            if (ReferenceEquals(_load, cts)) _load = null;
        }
        if (generation != _generation) return;
        IsLoading = false;
        ConnectionMessage = connection ?? "";
        if (collections is null)
        {
            StatusMessage = status;
            return;
        }
        _collections = collections;
        _partialList = false;
        _services.Log.Info(LogArea, $"Read {collections.Count} collections.");
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

    /// <summary>A message for the operator: the service's own plain text with the setting to check, or the fallback.</summary>
    public static string PlainMessage(Exception ex, string fallback) => ex switch
    {
        CollectionServiceException c => SecretMask.Apply(c.Message),
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
        if (_collections is null) return;
        var folders = _collections.Select(c => c.FolderPath).Where(f => !string.IsNullOrEmpty(f))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var folder = FolderFilter;
        while (Folders.Count > 1) Folders.RemoveAt(1);
        foreach (var f in folders) Folders.Add(new Choice<string>(f, f));
        if (folder.Length > 0 && !folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Folders.Add(new Choice<string>(folder, folder));
        ApplyFilter();
        RebuildTree();
    }

    private bool Matches(CollectionRow row)
    {
        if (TypeFilter == BuiltInFilter && !row.IsBuiltIn) return false;
        if (TypeFilter == CustomFilter && row.IsBuiltIn) return false;
        if (OnlyEmpty && row.MemberCount > 0) return false;
        if (OnlyWithDirectMembers && row.DirectRuleCount == 0) return false;
        if (OnlyWithQueryRules && row.QueryRuleCount == 0) return false;
        if (FolderFilter.Length > 0 && !string.Equals(row.FolderPath, FolderFilter, StringComparison.OrdinalIgnoreCase)) return false;
        var text = TextFilter.Trim();
        return text.Length == 0 || row.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || row.CollectionId.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.Comment.Contains(text, StringComparison.OrdinalIgnoreCase) || row.LimitingCollectionName.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        if (_collections is null) return;
        var selectedId = SelectedRow?.CollectionId;
        var wanted = _partialList ? _collections.Select(c => new CollectionGridRow(c)).ToList()
            : _collections.Where(Matches).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new CollectionGridRow(c)).ToList();
        ReplaceRows(wanted);
        var selected = selectedId is null ? null : Rows.FirstOrDefault(r => string.Equals(r.CollectionId, selectedId, StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(selected, SelectedRow))
        {
            // The same collection with new counts keeps its detail; a load reads it again when needed.
            if (selected is not null && SelectedRow is not null && selected.CollectionId == SelectedRow.CollectionId) SetSelectedRowQuietly(selected);
            else SelectedRow = selected;
        }
        var shownSelected = _selectedRows.Select(s => Rows.FirstOrDefault(r => string.Equals(r.CollectionId, s.CollectionId, StringComparison.OrdinalIgnoreCase))).OfType<CollectionGridRow>().ToList();
        SetSelectedRows(shownSelected);
        RowCount = _partialList ? OpenedById
            : Rows.Count == _collections.Count ? CollectionsText.Count(_collections.Count, "collection")
            : $"{Rows.Count} of {CollectionsText.Count(_collections.Count, "collection")}";
    }

    private bool _quietSelection;

    private void SetSelectedRowQuietly(CollectionGridRow row)
    {
        _quietSelection = true;
        try { SelectedRow = row; }
        finally { _quietSelection = false; }
    }

    /// <summary>Changes <see cref="Rows"/> with the fewest changes, so the grid keeps its selection and scroll position.</summary>
    private void ReplaceRows(IReadOnlyList<CollectionGridRow> wanted)
    {
        var keep = new HashSet<CollectionGridRow>(wanted);
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i])) Rows.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= Rows.Count || !Equals(Rows[i], wanted[i])) Rows.Insert(i, wanted[i]);
        while (Rows.Count > wanted.Count) Rows.RemoveAt(Rows.Count - 1);
    }

    private CollectionRow? FindCollection(string id) =>
        _collections?.FirstOrDefault(c => string.Equals(c.CollectionId, id, StringComparison.OrdinalIgnoreCase));

    private string CollectionText(string id, string fallbackName) =>
        FindCollection(id) is { } row ? CollectionsText.Target(row) : CollectionsText.Target(fallbackName, id);

    [RelayCommand]
    private async Task OpenById()
    {
        var id = OpenByIdText.Trim().ToUpperInvariant();
        if (!CollectionIdPattern.IsMatch(id))
        {
            StatusMessage = InvalidCollectionId;
            return;
        }
        if (_collections is not null && !_partialList && FindCollection(id) is { } known)
        {
            if (Rows.FirstOrDefault(r => r.CollectionId == known.CollectionId) is null)
            {
                TextFilter = "";
                TypeFilter = "";
                OnlyEmpty = OnlyWithDirectMembers = OnlyWithQueryRules = false;
                FolderFilter = "";
            }
            SelectedRow = Rows.FirstOrDefault(r => r.CollectionId == known.CollectionId);
            StatusMessage = "";
            return;
        }
        var generation = _generation;
        CollectionRow? row;
        try
        {
            row = await Task.Run(async () =>
            {
                if (ConnectionProblem(_services.LoadSettings()) is { } problem) throw new CollectionServiceException(problem);
                return await _services.Collections.GetCollectionAsync(id, CancellationToken.None);
            });
        }
        catch (Exception ex)
        {
            StatusMessage = PlainMessage(ex, LoadFailed);
            _services.Log.Warning(LogArea, $"A collection could not be opened by ID ({FailureName(ex)}).");
            return;
        }
        if (generation != _generation) return;
        if (row is null)
        {
            StatusMessage = CollectionNotFound(id);
            return;
        }
        _collections = [row];
        _partialList = true;
        StatusMessage = "";
        _services.Log.Info(LogArea, "Opened 1 collection by ID.");
        Rebuild();
        SelectedRow = Rows.FirstOrDefault();
        SetSelectedRows(SelectedRow is null ? [] : [SelectedRow]);
    }

    [RelayCommand]
    private async Task ConnectSite()
    {
        var settings = _services.LoadSettings();
        if (ConnectionProblem(settings) is { } problem)
        {
            ConnectionMessage = problem;
            return;
        }
        StatusMessage = "Testing the connection to the site.";
        try
        {
            await Task.Run(() => _services.Collections.GetCollectionAsync("SMS00001", CancellationToken.None));
        }
        catch (Exception ex)
        {
            ConnectionMessage = PlainMessage(ex, ConnectFailed);
            StatusMessage = "";
            _services.Log.Warning(LogArea, $"The connection test failed ({FailureName(ex)}).");
            return;
        }
        ConnectionMessage = "";
        StatusMessage = Connected(settings.SiteCode.Trim());
        _services.Log.Info(LogArea, "The connection test succeeded.");
    }

    [RelayCommand]
    private async Task OpenComplianceBaselines()
    {
        string? message;
        try { message = await _services.OpenComplianceTool(); }
        catch (Exception ex)
        {
            message = "Collection and Compliance Manager could not be started. The log has the details.";
            _services.Log.Warning(LogArea, $"The compliance tool could not be started ({ex.GetType().Name}).");
        }
        StatusMessage = message ?? ComplianceStarted;
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync("CSV", _services.Reports.WriteCsv);

    [RelayCommand]
    private Task ExportHtml() => ExportAsync("HTML", _services.Reports.WriteHtml);

    private async Task ExportAsync(string format, Func<IReadOnlyList<CollectionRow>, string> write)
    {
        var rows = Rows.Select(r => r.Row).ToList();
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
            StatusMessage = ex is CollectionServiceException c ? SecretMask.Apply(c.Message) : ReportFailed(format);
            _services.Log.Warning(LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        StatusMessage = $"Saved the {format} report to {path}.";
        _services.Log.Info(LogArea, $"Saved a {format} report with {rows.Count} collections.");
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) _services.Prompts.OpenFolder(folder);
    }

    /// <summary>
    /// Reads the current state of each request, builds the plan from it and shows the plan. The
    /// writer applies only the changes of the plan the operator confirms. Null when no plan was
    /// shown; the reason is in <see cref="StatusMessage"/> and passed to <paramref name="report"/>.
    /// With more than one request, a request whose state cannot be read becomes a Skip line.
    /// <paramref name="checkState"/> false sends the requests as they are: the service checks the
    /// state of each change again when it writes it.
    /// </summary>
    private async Task<(CollectionPlan Plan, ChangePlanOutcome Outcome)?> RunPlanAsync(
        IReadOnlyList<CollectionChange> requests,
        Func<IReadOnlyList<CollectionChange>, (CollectionPlan? Plan, string? Problem)> build,
        Action<string>? report = null,
        bool checkState = true,
        IReadOnlyList<CollectionPlanEntry>? extraSkips = null)
    {
        if (_writing) return null;
        _writing = true;
        _lastRunPlan = null;
        NotifyWriteCommands();
        try
        {
            var generation = _generation;
            void Refuse(string message)
            {
                StatusMessage = message;
                report?.Invoke(message);
            }

            var prepared = new List<CollectionChange>(requests.Count);
            var skips = new List<CollectionPlanEntry>(extraSkips ?? []);
            if (!checkState) prepared.AddRange(requests);
            else
            {
                var checks = await Task.Run(async () =>
                {
                    var list = new List<(CollectionChange Request, CollectionChange? Prepared, Exception? Failure)>(requests.Count);
                    foreach (var request in requests)
                    {
                        try { list.Add((request, await _services.Collections.PrepareAsync(request, CancellationToken.None), null)); }
                        catch (Exception ex) { list.Add((request, null, ex)); }
                    }
                    return list;
                });
                var failed = new List<CollectionPlanEntry>();
                foreach (var (request, ready, failure) in checks)
                {
                    if (failure is null)
                    {
                        prepared.Add(ready!);
                        continue;
                    }
                    _services.Log.Warning(LogArea, $"The state before a write could not be read ({FailureName(failure)}).");
                    var reason = PlainMessage(failure, PrepareFailed);
                    failed.Add(CollectionPlanEntry.Skip(new ChangePlanItem("Skip", CollectionText(request.CollectionId, request.CollectionName), reason), reason));
                }
                if (failed.Count > 0 && (requests.Count == 1 || prepared.Count == 0))
                {
                    Refuse(failed[0].SkipReason!);
                    return null;
                }
                skips.AddRange(failed);
            }
            if (generation != _generation) return null;

            var (built, problem) = build(prepared);
            if (built is null)
            {
                if (problem is not null) Refuse(problem);
                return null;
            }
            var plan = skips.Count == 0 ? built
                : new CollectionPlan(built.Action, built.Plan.Title, built.Plan.ActionName, [.. skips, .. built.Entries], built.Plan.IsDestructive, built.Plan.HasUndo) { IsUndo = built.IsUndo };

            var outcome = _services.Plans.Show(plan.Plan, _services.Writer.RunFor(plan), plan.Plan.HasUndo ? _services.Writer.UndoFor(plan) : null);
            if (outcome.Confirmed) _lastRunPlan = plan;
            ShowOutcome(plan, outcome);
            return (plan, outcome);
        }
        finally
        {
            _writing = false;
            NotifyWriteCommands();
        }
    }

    private void NotifyWriteCommands()
    {
        EvaluateCommand.NotifyCanExecuteChanged();
        RemoveCollectionCommand.NotifyCanExecuteChanged();
        RemoveDirectMembersCommand.NotifyCanExecuteChanged();
        ApplyTemplateCommand.NotifyCanExecuteChanged();
        UndoLastCommand.NotifyCanExecuteChanged();
    }

    private void ShowOutcome(CollectionPlan plan, ChangePlanOutcome outcome)
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

    /// <summary>Reads the list, the selected detail, the WQL rules and the saved queries again after a write.</summary>
    private async Task AfterWriteAsync()
    {
        var status = StatusMessage;
        // A load that started before the write can return the state before it; the list is read
        // again once that load ends.
        while (_loading is { IsCompleted: false } pending) await pending;
        if (_partialList && _collections is [var opened])
        {
            try
            {
                var row = await Task.Run(() => _services.Collections.GetCollectionAsync(opened.CollectionId, CancellationToken.None));
                _collections = row is null ? [] : [row];
                Rebuild();
            }
            catch (Exception ex)
            {
                _services.Log.Warning(LogArea, $"The collection could not be read again ({FailureName(ex)}).");
            }
        }
        else await LoadAsync();
        if (StatusMessage.Length == 0) StatusMessage = status;
        if (SelectedRow is { } selected) StartDetail(selected, 0);
        if (WqlCollection is { } wql) StartWqlRules(Rows.FirstOrDefault(r => r.CollectionId == wql.CollectionId) ?? wql);
        if (_queriesLoaded) await StartQueriesLoad();
        await Task.WhenAll(DetailLoad, WqlRulesLoad);
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
            var entries = prepared.Select((change, i) => CollectionPlanEntry.Write(new ChangePlanItem("Undo " + undo[i].Entry.Item.Action,
                undo[i].Entry.Item.Target, UndoDetail(change)), change)).ToList();
            var destructive = prepared.Any(c => c.Kind is CollectionChangeKind.Remove or CollectionChangeKind.RemoveRule or CollectionChangeKind.DeleteQuery);
            return (new CollectionPlan("Undo " + last.Action, "Undo: " + last.Plan.Title, $"Undo {CollectionsText.Count(entries.Count, "change")}", entries, destructive) { IsUndo = true }, null);
        }, checkState: false);
        if (run is { Outcome.Confirmed: true }) await AfterWriteAsync();
    }

    private static string UndoDetail(CollectionChange change)
    {
        var rule = change.RuleAfter ?? change.RuleBefore;
        var query = change.QueryAfter ?? change.QueryBefore;
        return change.Kind switch
        {
            CollectionChangeKind.Remove => $"Removes {change.BeforeRow?.Name ?? change.CollectionName}.",
            CollectionChangeKind.Create => $"Recreates {change.AfterRow?.Name ?? change.CollectionName} with its properties and rules. {CollectionWriter.RecreatedNote}",
            CollectionChangeKind.SetProperties => "Sets the old values back.",
            CollectionChangeKind.AddRule => $"Adds the rule {rule?.RuleName} again.",
            CollectionChangeKind.RemoveRule => $"Removes the rule {rule?.RuleName}.",
            CollectionChangeKind.UpdateRule => $"Sets the rule {change.RuleAfter?.RuleName} back to: {change.RuleAfter?.Expression}",
            CollectionChangeKind.SaveQuery => $"Saves the query {query?.Name} again. {CollectionWriter.NewQueryIdNote}",
            CollectionChangeKind.UpdateQuery => $"Sets the query {query?.Name} back to: {change.QueryAfter?.Expression}",
            CollectionChangeKind.DeleteQuery => $"Deletes the query {query?.Name}.",
            _ => "Reverses the change."
        };
    }
}
