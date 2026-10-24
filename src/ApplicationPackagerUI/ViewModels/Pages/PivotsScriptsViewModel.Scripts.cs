using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class PivotsScriptsViewModel
{
    public const string ScriptsFailed = "The site scripts could not be read. The log has the details.";
    public const string LibraryFailed = "The library could not be read. The built-in samples and the library folder were not loaded. The log has the details.";
    public const string SelectApproved = "Select an approved script first.";
    public const string SelectSiteScript = "Select a site script first.";
    public const string NeedsParameters = "The script has a required parameter without a value. Select Parameters first.";
    public const string CheckFailed = "The syntax check could not run. The log has the details.";
    public const string CheckPassed = "The script text parses. The check finds syntax errors only; it does not run the script.";
    public const string DraftNeedsName = "Type a name for the new script first.";
    public const string DraftNeedsText = "Type or load the script text first.";
    public const string SiteScriptFilterKey = "siteScripts";
    public const int MaxScriptNameLength = 200;

    public static readonly IReadOnlyList<string> PivotWords =
        ["Device", "OS", "Bios", "Disk", "LogicalDisk", "Service", "Process", "InstalledSoftware", "User", "Administrators", "File('')", "Registry('')",
         "where", "project", "summarize", "count()", "by", "distinct", "take", "top", "order by", "join", "extend", "==", "!=", "contains", "startswith", "and", "or", "not"];

    public static readonly IReadOnlyList<string> ScriptWords =
        ["param()", "Get-CimInstance", "Get-Service", "Get-ItemProperty", "Select-Object", "Where-Object", "ForEach-Object", "ConvertTo-Json -Compress",
         "foreach", "try", "catch", "[pscustomobject]", "Write-Output"];

    private int _scriptsGeneration;
    private int _libraryGeneration;
    private IReadOnlyList<ScriptGridRow> _allScripts = [];
    private string? _selectedApprovedKey;
    private string? _selectedSiteScriptKey;
    private IReadOnlyDictionary<string, string>? _parameters;
    private string? _parametersFor;

    public bool IsScriptsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshScriptsCommand))]
    private bool _isScriptsLoading;

    [ObservableProperty] private IReadOnlyList<ScriptGridRow> _approvedScripts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptDetail), nameof(ParametersText))]
    [NotifyCanExecuteChangedFor(nameof(EditParametersCommand))]
    private ScriptGridRow? _selectedApproved;

    [ObservableProperty] private IReadOnlyList<ScriptGridRow> _siteScriptRows = [];
    [ObservableProperty] private string _siteScriptText = "";
    [ObservableProperty] private string _siteScriptCount = "";
    [ObservableProperty] private string _scriptsCount = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SiteScriptDetail))]
    private ScriptGridRow? _selectedSiteScript;

    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private string _draftName = "";
    [ObservableProperty] private string _draftText = "";
    [ObservableProperty] private string _checkResult = "";

    [ObservableProperty] private IReadOnlyList<PivotsLibraryItem> _queryLibrary = [];
    [ObservableProperty] private IReadOnlyList<PivotsLibraryItem> _scriptLibrary = [];
    [ObservableProperty] private PivotsLibraryItem? _selectedQueryItem;
    [ObservableProperty] private PivotsLibraryItem? _selectedScriptItem;

    /// <summary>"Line 1, column 1" of the caret in the editor the operator types in.</summary>
    [ObservableProperty] private string _editorPosition = "Line 1, column 1";

    /// <summary>The GUID of a script this page created whose later step failed; Remove created script offers to remove only it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLeftover))]
    private string? _leftoverGuid;

    public bool HasLeftover => LeftoverGuid is not null;

    public string ScriptDetail => SelectedApproved is { } row
        ? $"{row.Name}, version {row.Version}, GUID {row.Guid}." + (HashOf(row.Script) is { Length: > 0 } hash ? $" SHA-256 {hash}." : "") + $" {PivotsLabels.Count(row.Script.Parameters.Count, "parameter")}."
            + (row.Script.ParametersUnreadable ? " " + ParametersUnreadable : "")
        : "Select an approved script.";

    public const string ParametersUnreadable = "The site parameter definitions of the script could not be read, so the script cannot run from this page.";

    private static string HashOf(SiteScript script) => script.Hash.Length > 0 ? script.Hash : script.SiteHash;

    public string ParametersText => SelectedApproved is not { } row ? ""
        : row.Script.Parameters.Count == 0 ? "The script has no parameters."
        : ParametersFor(row.Script) is { } values ? PivotsScriptsText.ParameterText(values)
        : "Parameters: not set. Select Parameters to type the values; an empty value uses the default of the script.";

    public string SiteScriptDetail => SelectedSiteScript is { } row
        ? $"{row.Name}: {row.State}, version {row.Version}, author {row.Author}" + (row.Approver.Length > 0 ? $", approver {row.Approver}" : "") + $". GUID {row.Guid}."
        : "Select a script to approve, deny or remove it.";

    /// <summary>The values typed for the script, when they were typed for this GUID and this script text.</summary>
    private IReadOnlyDictionary<string, string>? ParametersFor(SiteScript script) => _parametersFor == ParameterKey(script) ? _parameters : null;

    private static string ParameterKey(SiteScript script) => $"{script.Guid.ToUpperInvariant()}|{script.Version}|{script.SiteHash.ToUpperInvariant()}|{script.Hash.ToUpperInvariant()}";

    public void SetCaret(int line, int column) => EditorPosition = $"Line {line}, column {column}";

    partial void OnSelectedApprovedChanged(ScriptGridRow? value)
    {
        if (value is not null) _selectedApprovedKey = value.Key;
    }

    partial void OnSelectedSiteScriptChanged(ScriptGridRow? value)
    {
        if (value is not null) _selectedSiteScriptKey = value.Key;
    }

    partial void OnSiteScriptTextChanged(string value) => Debounce(SiteScriptFilterKey, ApplySiteScriptFilter);

    private void ReleaseScripts()
    {
        _scriptsGeneration++;
        _scriptsTask = null;
        IsScriptsLoading = false;
        IsScriptsLoaded = false;
        _allScripts = [];
        ApprovedScripts = [];
        SiteScriptRows = [];
        SelectedApproved = null;
        SelectedSiteScript = null;
        ScriptsCount = SiteScriptCount = "";
    }

    private void ReleaseLibraries()
    {
        _libraryGeneration++;
        _libraryTask = null;
        IsLibraryLoaded = false;
        QueryLibrary = [];
        ScriptLibrary = [];
        SelectedQueryItem = null;
        SelectedScriptItem = null;
    }

    private Task? _scriptsTask;

    /// <summary>The read that runs now, or a new one when the scripts are not loaded; loaded scripts need no read.</summary>
    private Task EnsureScriptsAsync()
    {
        if (IsScriptsLoaded) return Task.CompletedTask;
        if (_scriptsTask is { IsCompleted: false } pending) return pending;
        var task = LoadScriptsAsync();
        _scriptsTask = task.IsCompleted ? null : task;
        return task;
    }

    private bool CanRefreshScripts() => !IsScriptsLoading;

    [RelayCommand(CanExecute = nameof(CanRefreshScripts))]
    private Task RefreshScripts() => LoadScriptsAsync();

    /// <summary>Reads the site scripts with one read; the selection stays on the same GUID.</summary>
    public async Task LoadScriptsAsync()
    {
        if (ConnectionProblem() is { } problem)
        {
            StatusMessage = problem;
            return;
        }
        var generation = Generation;
        var mine = ++_scriptsGeneration;
        var visit = VisitToken;
        IsScriptsLoading = true;
        try
        {
            var scripts = await Task.Run(() => Services.Service.LoadScriptsAsync(visit), visit);
            if (generation != Generation || mine != _scriptsGeneration) return;
            _allScripts = scripts.Select(s => new ScriptGridRow(s)).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            IsScriptsLoaded = true;
            var approved = _allScripts.Where(r => r.IsApproved).ToList();
            ApprovedScripts = approved;
            SelectedApproved = _selectedApprovedKey is null ? null : approved.FirstOrDefault(r => r.Key == _selectedApprovedKey);
            ScriptsCount = $"{PivotsLabels.Count(approved.Count, "approved script")} of {PivotsLabels.Count(_allScripts.Count, "script")} on the site.";
            ApplySiteScriptFilter();
            Services.Log.Info(LogArea, $"Read {scripts.Count} site scripts, {approved.Count} approved.");
        }
        catch (OperationCanceledException)
        {
            if (generation == Generation && mine == _scriptsGeneration) StatusMessage = ReadCancelled;
        }
        catch (Exception ex)
        {
            if (generation != Generation || mine != _scriptsGeneration) return;
            StatusMessage = PlainMessage(ex, ScriptsFailed);
            Services.Log.Warning(LogArea, $"The site scripts could not be read ({FailureName(ex)}).");
        }
        finally
        {
            if (mine == _scriptsGeneration) IsScriptsLoading = false;
        }
    }

    private void ApplySiteScriptFilter()
    {
        var text = SiteScriptText.Trim();
        var list = _allScripts.Where(r => text.Length == 0 || Has(r.Name, text) || Has(r.Guid, text) || Has(r.Author, text) || Has(r.State, text)).ToList();
        SiteScriptRows = list;
        SelectedSiteScript = _selectedSiteScriptKey is null ? null : list.FirstOrDefault(r => r.Key == _selectedSiteScriptKey);
        SiteScriptCount = list.Count == _allScripts.Count ? PivotsLabels.Count(list.Count, "script") : $"{PivotsLabels.Number(list.Count)} of {PivotsLabels.Count(_allScripts.Count, "script")}";
    }

    public const string QueryKind = "Query";
    public const string ScriptKind = "Script";

    private Task? _libraryTask;

    private Task EnsureLibraryAsync()
    {
        if (IsLibraryLoaded) return Task.CompletedTask;
        if (_libraryTask is { IsCompleted: false } pending) return pending;
        var task = LoadLibraryAsync();
        _libraryTask = task.IsCompleted ? null : task;
        return task;
    }

    public bool IsLibraryLoaded { get; private set; }

    /// <summary>Reads the built-in samples and the files of the library folder with one call; it needs no site.</summary>
    public async Task LoadLibraryAsync()
    {
        var generation = Generation;
        var mine = ++_libraryGeneration;
        var folder = string.IsNullOrWhiteSpace(LibraryFolder) ? null : LibraryFolder.Trim();
        try
        {
            var items = await Task.Run(() => Services.Library.Load(folder));
            if (generation != Generation || mine != _libraryGeneration) return;
            QueryLibrary = items.Where(i => string.Equals(i.Kind, QueryKind, StringComparison.OrdinalIgnoreCase)).ToList();
            ScriptLibrary = items.Where(i => string.Equals(i.Kind, ScriptKind, StringComparison.OrdinalIgnoreCase)).ToList();
            IsLibraryLoaded = true;
        }
        catch (Exception ex)
        {
            if (generation != Generation || mine != _libraryGeneration) return;
            StatusMessage = PlainMessage(ex, LibraryFailed);
            Services.Log.Warning(LogArea, $"The library could not be read ({ex.GetType().Name}).");
        }
    }

    [RelayCommand]
    private void UseQueryItem()
    {
        if (SelectedQueryItem is not { } item) return;
        QueryText = item.Text;
        StatusMessage = $"The query {item.Name} is in the editor.";
    }

    [RelayCommand]
    private void UseScriptItem()
    {
        if (SelectedScriptItem is not { } item) return;
        DraftText = item.Text;
        if (DraftName.Trim().Length == 0) DraftName = item.Name;
        CheckResult = "";
        StatusMessage = $"The script {item.Name} is in the editor.";
    }

    private bool CanEditParameters() => SelectedApproved is not null;

    /// <summary>Shows one field per parameter definition the site holds; the values are checked against the type of each.</summary>
    [RelayCommand(CanExecute = nameof(CanEditParameters))]
    private void EditParameters()
    {
        if (SelectedApproved is not { } row)
        {
            StatusMessage = SelectApproved;
            return;
        }
        var dialog = new PivotsParametersViewModel(row.Script, ParametersFor(row.Script));
        Services.Dialogs.ShowParameters(dialog);
        if (dialog.Result is not { } values) return;
        _parameters = values;
        _parametersFor = ParameterKey(row.Script);
        OnPropertyChanged(nameof(ParametersText));
    }

    /// <summary>The checked values for a run of the script, or null with the reason.</summary>
    private (IReadOnlyDictionary<string, string>? Values, string? Problem) RunParameters(SiteScript script)
    {
        var values = ParametersFor(script) ?? new Dictionary<string, string>();
        foreach (var parameter in script.Parameters)
        {
            values.TryGetValue(parameter.Name, out var value);
            var (_, error) = PivotsParameterCheck.Check(parameter, value);
            if (error.Length > 0) return (null, NeedsParameters);
        }
        return (values, null);
    }

    /// <summary>The result of a syntax check: the errors and the Run Scripts problems, or the parameters the site will store.</summary>
    public static string CheckText(ScriptCheck check)
    {
        var lines = new List<string>();
        if (check.Errors.Count > 0) lines.Add($"{PivotsLabels.Count(check.Errors.Count, "syntax error")}: " + string.Join(" ", check.Errors.Take(10).Select(e => SecretMask.Apply(e))));
        if (check.Problems.Count > 0) lines.Add($"The site cannot store the parameters: " + string.Join(" ", check.Problems.Take(10).Select(p => SecretMask.Apply(p))));
        if (lines.Count > 0) return string.Join(" ", lines);
        return CheckPassed + (check.Parameters.Count == 0 ? " The script has no parameters."
            : " Parameters the site will store: " + string.Join(", ", check.Parameters.Select(p => $"{p.Name} ({p.Type}{(p.Required ? ", required" : "")})")) + ".");
    }

    private bool CanCheck() => !IsWriting;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckDraft()
    {
        var text = DraftText;
        if (text.Trim().Length == 0)
        {
            CheckResult = DraftNeedsText;
            return;
        }
        var generation = Generation;
        var visit = VisitToken;
        CheckResult = "Checking the syntax.";
        try
        {
            var check = await Task.Run(() => Services.Service.CheckScriptAsync(text, visit), visit);
            if (generation != Generation) return;
            CheckResult = CheckText(check);
        }
        catch (OperationCanceledException)
        {
            if (generation == Generation) CheckResult = "";
        }
        catch (Exception ex)
        {
            if (generation != Generation) return;
            CheckResult = PlainMessage(ex, CheckFailed);
            Services.Log.Warning(LogArea, $"The syntax check failed ({FailureName(ex)}).");
        }
    }
}