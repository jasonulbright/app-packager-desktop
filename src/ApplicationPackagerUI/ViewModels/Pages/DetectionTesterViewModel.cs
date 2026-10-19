using System.IO;
using System.Text;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Detection Tester page reads and writes; the page logic depends on nothing else.</summary>
public sealed record DetectionTesterPageServices(
    IDetectionTestService Tests,
    IDetectionHistoryStore History,
    IDetectionReportWriter Reports,
    IInstalledApplicationsService InstalledApplications,
    IDetectionTesterPrompts Prompts,
    Func<DetectionTesterSettings?> LoadPreferences,
    Action<DetectionTesterSettings> SavePreferences,
    IAppLog Log,
    Func<string, StageManifest> ReadManifest);

public enum DetectionTesterResultState
{
    None,
    Passed,
    Failed,
    Invalid,
    Error,
    Cancelled
}

/// <summary>One history row as the grid shows it.</summary>
public sealed record DetectionHistoryRow(DetectionHistoryEntry Entry)
{
    public string Time => Entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Kind => Entry.Kind;
    public string Description => Entry.Description;
    public string Negative => Entry.Negative ? "Yes" : "No";
    public string Result => Entry.Passed ? "Passed" : "Failed";
}

public partial class DetectionTesterViewModel : ObservableObject
{
    public const string LogArea = "DetectionTester";
    public const string RegistryKeyValue = "RegistryKeyValue";
    public const string RegistryKey = "RegistryKey";
    public const string File = "File";
    public const string Script = "Script";
    public const string Compound = "Compound";
    public const string View64 = "64-bit";
    public const string View32 = "32-bit";
    public const string ScriptSystemNote = "ConfigMgr runs detection as SYSTEM and also reads the exit code.";
    public const string ScriptEditorNote = "This script runs on this computer as the current user.";
    public const string EditorKeyNote = "Press Ctrl+Tab to leave the editor.";
    public const string HistoryNote = "History keeps the clause text, including script text, on this computer.";
    public const string NoDetectionMessage = "This manifest has no detection to import.";

    /// <summary>The number of history rows the page keeps; the history store keeps the same number.</summary>
    public const int HistoryLimit = 500;

    /// <summary>A stage manifest is a few kilobytes; a larger file is refused before it is read.</summary>
    public const long MaxManifestBytes = 8L * 1024 * 1024;

    public static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] BaseClauseTypes = [RegistryKeyValue, RegistryKey, File, Script];

    private readonly DetectionTesterPageServices _services;
    private readonly List<string> _fallbackNotes = [];
    private CancellationTokenSource? _run;
    private bool _historyLoaded;
    private bool _applyingPreferences;
    private Task _preferencesLoad = Task.CompletedTask;
    private DetectionTesterSettings? _storedChoices;
    private IReadOnlyList<InstalledApplication>? _allApplications;
    private Task? _applicationsLoad;
    private int _applicationsGeneration;

    public DetectionTesterViewModel(IDetectionTestService tests, IDetectionHistoryStore history, IDetectionReportWriter reports,
        IInstalledApplicationsService installedApplications, IDetectionTesterPrompts prompts, SettingsService settings, IAppLog log,
        ManifestService manifests)
        : this(new DetectionTesterPageServices(tests, history, reports, installedApplications, prompts,
            () => DetectionTesterPreferences.Load(settings),
            choices => DetectionTesterPreferences.Save(settings, choices),
            log,
            manifests.Read))
    {
    }

    public DetectionTesterViewModel(DetectionTesterPageServices services)
    {
        _services = services;
    }

    public ObservableCollection<string> ClauseTypes { get; } = new(BaseClauseTypes);
    public IReadOnlyList<string> Hives { get; } = [DetectionClauseText.Hklm, DetectionClauseText.Hkcu];
    public IReadOnlyList<string> Views { get; } = [View64, View32];
    public IReadOnlyList<string> RegistryOperators { get; } =
        ["IsEquals", "NotEquals", "GreaterThan", "GreaterEquals", "LessThan", "LessEquals", "BeginsWith", "EndsWith", "Contains"];
    public IReadOnlyList<string> RegistryPropertyTypes { get; } = ["String", "Version"];
    public IReadOnlyList<string> FileProperties { get; } = ["Existence", "Version"];
    public IReadOnlyList<string> FileOperators { get; } = ["IsEquals", "NotEquals", "GreaterThan", "GreaterEquals", "LessThan", "LessEquals"];

    public string EditorNote => ScriptEditorNote;
    public string SystemNote => ScriptSystemNote;
    public string EditorKeyHint => EditorKeyNote;
    public string HistoryStorageNote => HistoryNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegistryKeyValue), nameof(IsRegistryKey), nameof(IsRegistry), nameof(IsFile), nameof(IsScript), nameof(IsCompound))]
    private string _selectedClauseType = RegistryKeyValue;

    [ObservableProperty] private string _selectedHive = DetectionClauseText.Hklm;
    [ObservableProperty] private string _selectedView = View64;
    [ObservableProperty] private string _keyPath = "";
    [ObservableProperty] private string _valueName = "";
    [ObservableProperty] private string _registryOperator = "IsEquals";
    [ObservableProperty] private string _registryPropertyType = "String";
    [ObservableProperty] private string _expectedValue = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _fileName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileVersion))]
    private string _fileProperty = "Existence";

    [ObservableProperty] private string _fileOperator = "GreaterEquals";
    [ObservableProperty] private string _fileExpectedValue = "";
    [ObservableProperty] private string _scriptText = "";
    [ObservableProperty] private bool _negative;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private DetectionTesterResultState _resultState;

    [ObservableProperty] private string _resultHeadline = "";
    [ObservableProperty] private string _resultSummary = "";
    [ObservableProperty] private string _runContext = "";
    [ObservableProperty] private string _scriptStdout = "";
    [ObservableProperty] private string _scriptStderr = "";
    [ObservableProperty] private string _scriptExitCode = "";
    [ObservableProperty] private bool _showScriptOutput;
    [ObservableProperty] private bool _showScriptNote;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private StageDetection? _importedDetection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseSelectedCommand))]
    private DetectionHistoryRow? _selectedHistory;

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private string _applicationFilter = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyDetailsCommand), nameof(UseForDetectionCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedApplication))]
    private InstalledApplication? _selectedApplication;

    [ObservableProperty] private string _applicationCount = "";
    [ObservableProperty] private string _applicationStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshApplicationsCommand))]
    private bool _isLoadingApplications;

    [ObservableProperty] private bool _showSystemComponents;

    public bool IsRegistryKeyValue => SelectedClauseType == RegistryKeyValue;
    public bool IsRegistryKey => SelectedClauseType == RegistryKey;
    public bool IsRegistry => IsRegistryKeyValue || IsRegistryKey;
    public bool IsFile => SelectedClauseType == File;
    public bool IsScript => SelectedClauseType == Script;
    public bool IsCompound => SelectedClauseType == Compound;
    public bool IsFileVersion => FileProperty == "Version";
    public bool HasResult => ResultState != DetectionTesterResultState.None;
    public bool HasSelectedApplication => SelectedApplication is not null;

    public ObservableCollection<DetectionClauseResult> ResultClauses { get; } = [];
    public ObservableCollection<DetectionClauseNode> ImportedTree { get; } = [];
    public ObservableCollection<DetectionHistoryRow> History { get; } = [];
    public ObservableCollection<InstalledApplication> Applications { get; } = [];

    /// <summary>Loads the history and the saved choices on the first visit, and the applications when their tab is open.</summary>
    public async Task OnNavigatedToAsync()
    {
        if (!_historyLoaded)
        {
            _historyLoaded = true;
            _preferencesLoad = ApplyPreferencesAsync();
            await _preferencesLoad;
            await LoadHistoryAsync();
        }
        if (SelectedTabIndex == 1 && _allApplications is null) await LoadApplicationsAsync();
    }

    /// <summary>Releases the installed applications; the page reads them again on the next visit.</summary>
    public Task OnNavigatedFromAsync()
    {
        // A load still running belongs to the visit that started it; its result is discarded.
        _applicationsGeneration++;
        _applicationsLoad = null;
        IsLoadingApplications = false;
        _allApplications = null;
        SelectedApplication = null;
        Applications.Clear();
        ApplicationCount = "";
        ApplicationStatus = "";
        return Task.CompletedTask;
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 1 && _allApplications is null && _applicationsLoad is null) _ = LoadApplicationsAsync();
    }

    partial void OnApplicationFilterChanged(string value) => ApplyFilter();

    partial void OnShowSystemComponentsChanged(bool value)
    {
        if (_applyingPreferences) return;
        _ = SavePreferencesAsync();
        if (_allApplications is null && _applicationsLoad is null && SelectedTabIndex != 1) return;
        _applicationsGeneration++;
        _applicationsLoad = null;
        _ = LoadApplicationsAsync();
    }

    /// <summary>Reads the saved choices on a pool thread; the inputs are set on the calling thread.</summary>
    private async Task ApplyPreferencesAsync()
    {
        DetectionTesterSettings? saved;
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
            if (BaseClauseTypes.Contains(saved.ClauseType)) SelectedClauseType = saved.ClauseType;
            if (Hives.Contains(saved.Hive)) SelectedHive = saved.Hive;
            if (Views.Contains(saved.View)) SelectedView = saved.View;
            ShowSystemComponents = saved.ShowSystemComponents;
        }
        finally { _applyingPreferences = false; }
        _storedChoices = CurrentChoices();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var entries = await Task.Run(_services.History.All);
            History.Clear();
            foreach (var entry in entries.OrderByDescending(e => e.Time).Take(HistoryLimit)) History.Add(new DetectionHistoryRow(entry));
        }
        catch (Exception ex)
        {
            StatusMessage = ex is DetectionFolderRefusedException ? ex.Message : "The history could not be read.";
            _services.Log.Warning(LogArea, $"The history could not be read ({ex.GetType().Name}).");
        }
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        if (IsRunning) return;
        var (detection, error) = BuildDetection();
        if (detection is null)
        {
            ShowOutcome(DetectionTesterResultState.Invalid, "Invalid clause", error ?? "");
            return;
        }

        var request = new DetectionTestRequest(detection, Negative, SelectedView == View32 ? RegistryViewChoice.Registry32 : RegistryViewChoice.Registry64, ScriptTimeout);
        using var run = new CancellationTokenSource();
        _run = run;
        IsRunning = true;
        RunCommand.NotifyCanExecuteChanged();
        ShowOutcome(DetectionTesterResultState.None, "", "");
        DetectionTestResult? result = null;
        Exception? failure = null;
        try
        {
            await SavePreferencesAsync();
            result = await Task.Run(() => _services.Tests.RunAsync(request, run.Token), run.Token);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _run = null;
            IsRunning = false;
            RunCommand.NotifyCanExecuteChanged();
        }

        // A cancelled script is killed, so a result that arrives after Cancel is not the clause's verdict.
        if (run.IsCancellationRequested)
        {
            ShowOutcome(DetectionTesterResultState.Cancelled, "Cancelled", "The test was cancelled.");
            _services.Log.Info(LogArea, $"Test cancelled: kind={detection.Type}.");
            return;
        }
        if (result is null)
        {
            ShowOutcome(DetectionTesterResultState.Error, "Error", "The test could not run. The log has the details.");
            _services.Log.Error(LogArea, $"Test failed: kind={detection.Type} error={failure?.GetType().Name}.");
            return;
        }

        ShowResult(result, detection);
        _services.Log.Info(LogArea,
            $"Test: kind={detection.Type} clauses={DetectionClauseText.ClauseCount(detection)} negative={Negative} result={(result.Passed ? "Passed" : "Failed")}.");
        await RecordAsync(new DetectionHistoryEntry(Guid.NewGuid(), DateTimeOffset.Now, detection.Type, DetectionClauseText.Describe(detection),
            Negative, result.Passed, result.Summary, detection));
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _run?.Cancel();

    private void ShowOutcome(DetectionTesterResultState state, string headline, string summary)
    {
        ResultState = state;
        ResultHeadline = headline;
        ResultSummary = summary;
        ResultClauses.Clear();
        RunContext = "";
        ScriptStdout = "";
        ScriptStderr = "";
        ScriptExitCode = "";
        ShowScriptOutput = false;
        ShowScriptNote = false;
    }

    private void ShowResult(DetectionTestResult result, StageDetection detection)
    {
        ShowOutcome(result.Passed ? DetectionTesterResultState.Passed : DetectionTesterResultState.Failed,
            result.Passed ? "Passed" : "Failed", result.Summary);
        foreach (var clause in result.Clauses) ResultClauses.Add(clause);
        RunContext = result.RunContext;
        var hasScript = DetectionClauseText.ContainsScript(detection);
        ShowScriptNote = hasScript;
        ShowScriptOutput = hasScript;
        ScriptStdout = result.ScriptStdout ?? "";
        ScriptStderr = result.ScriptStderr ?? "";
        ScriptExitCode = result.ScriptExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private async Task RecordAsync(DetectionHistoryEntry entry)
    {
        History.Insert(0, new DetectionHistoryRow(entry));
        while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
        try
        {
            await Task.Run(() => _services.History.Add(entry));
        }
        catch (Exception ex)
        {
            StatusMessage = ex is DetectionFolderRefusedException ? ex.Message : "The test result could not be saved to the history.";
            _services.Log.Warning(LogArea, $"The history could not be saved ({ex.GetType().Name}).");
        }
    }

    private DetectionTesterSettings CurrentChoices() => new()
    {
        ClauseType = BaseClauseTypes.Contains(SelectedClauseType) ? SelectedClauseType : RegistryKeyValue,
        Hive = SelectedHive,
        View = SelectedView,
        ShowSystemComponents = ShowSystemComponents
    };

    private static bool SameChoices(DetectionTesterSettings? a, DetectionTesterSettings b) =>
        a is not null && a.ClauseType == b.ClauseType && a.Hive == b.Hive && a.View == b.View && a.ShowSystemComponents == b.ShowSystemComponents;

    /// <summary>Writes the choices on a pool thread when they differ from the stored ones; a failure is logged and tried again at the next change or run.</summary>
    private async Task SavePreferencesAsync()
    {
        // A save before the stored choices are known would replace them with the defaults.
        await _preferencesLoad;
        var choices = CurrentChoices();
        if (SameChoices(_storedChoices, choices)) return;
        try
        {
            await Task.Run(() => _services.SavePreferences(choices));
            _storedChoices = choices;
        }
        catch (Exception ex)
        {
            _services.Log.Warning(LogArea, $"The page choices could not be saved ({ex.GetType().Name}).");
        }
    }

    /// <summary>The clause the inputs describe, or the reason it cannot run.</summary>
    public (StageDetection? Detection, string? Error) BuildDetection()
    {
        switch (SelectedClauseType)
        {
            case RegistryKeyValue:
            case RegistryKey:
            {
                var relative = ApplyKeyPath();
                if (relative.Length == 0) return (null, "The key path is required.");
                var detection = new StageDetection
                {
                    Type = SelectedClauseType,
                    Hive = SelectedHive == DetectionClauseText.Hkcu ? "CurrentUser" : "LocalMachine",
                    Is64Bit = SelectedView != View32,
                    RegistryKeyRelative = relative
                };
                if (SelectedClauseType == RegistryKey) return (detection, null);
                if (string.IsNullOrWhiteSpace(ValueName)) return (null, "The value name is required.");
                detection.ValueName = ValueName.Trim();
                detection.Operator = RegistryOperator;
                detection.PropertyType = RegistryPropertyType;
                detection.ExpectedValue = string.IsNullOrEmpty(ExpectedValue) ? null : ExpectedValue;
                return (detection, null);
            }
            case File:
            {
                if (string.IsNullOrWhiteSpace(FilePath)) return (null, "The folder path is required.");
                if (string.IsNullOrWhiteSpace(FileName)) return (null, "The file name is required.");
                if (IsFileVersion && string.IsNullOrWhiteSpace(FileExpectedValue))
                    return (null, "The expected version is required when the property is Version.");
                return (new StageDetection
                {
                    Type = File,
                    FilePath = FilePath.Trim(),
                    FileName = FileName.Trim(),
                    PropertyType = FileProperty,
                    Operator = IsFileVersion ? FileOperator : null,
                    ExpectedValue = IsFileVersion ? FileExpectedValue.Trim() : null,
                    Is64Bit = SelectedView != View32
                }, null);
            }
            case Script:
                if (string.IsNullOrWhiteSpace(ScriptText)) return (null, "The script is required.");
                return (new StageDetection { Type = Script, ScriptLanguage = "PowerShell", ScriptText = ScriptText }, null);
            case Compound:
                return ImportedDetection is null ? (null, "Import a stage-manifest.json with a compound detection first.") : (ImportedDetection, null);
            default:
                return (null, "Select a clause type.");
        }
    }

    /// <summary>A pasted full key path selects its hive; the input keeps the path below the hive.</summary>
    private string ApplyKeyPath()
    {
        var (hive, relative) = DetectionClauseText.SplitKeyPath(KeyPath);
        if (hive is not null)
        {
            SelectedHive = hive;
            KeyPath = relative;
        }
        return relative.TrimEnd('\\');
    }

    [RelayCommand]
    private async Task ImportManifest()
    {
        var path = _services.Prompts.PickManifest();
        if (string.IsNullOrEmpty(path)) return;
        StageManifest? manifest = null;
        try
        {
            manifest = await Task.Run(() => IsTooLarge(path) ? null : _services.ReadManifest(path));
        }
        catch (Exception ex)
        {
            ShowOutcome(DetectionTesterResultState.Invalid, "Import failed", "The file could not be read as a stage manifest.");
            _services.Log.Warning(LogArea, $"Import failed ({ex.GetType().Name}).");
            return;
        }
        if (manifest is null)
        {
            ShowOutcome(DetectionTesterResultState.Invalid, "Import failed", "The file is too large for a stage manifest.");
            _services.Log.Warning(LogArea, "Import refused: the file is over the size limit.");
            return;
        }

        var (detection, deploymentType, deploymentTypeCount) = ImportableDetection(manifest);
        if (detection is null)
        {
            StatusMessage = NoDetectionMessage;
            _services.Log.Warning(LogArea, "Import found no detection in the stage manifest.");
            return;
        }
        if (!LoadDetection(detection)) return;

        var name = string.Join(" ", new[] { manifest.AppName, manifest.SoftwareVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (name.Length == 0) name = Path.GetFileName(path);
        var source = deploymentTypeCount > 1
            ? $"{name}, deployment type {deploymentType} (the first of {deploymentTypeCount} deployment types)"
            : name;
        StatusMessage = WithFallbackNotes($"Imported the detection of {source}. Press Run to test it.");
        ShowOutcome(DetectionTesterResultState.None, "", "");
        _services.Log.Info(LogArea,
            $"Imported a stage manifest: kind={detection.Type} clauses={DetectionClauseText.ClauseCount(detection)} deploymentTypes={deploymentTypeCount}.");
    }

    private static bool IsTooLarge(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > MaxManifestBytes;
    }

    /// <summary>
    /// The detection ConfigMgr gives the first deployment type: its own, or the manifest's when it has none.
    /// Without deployment types, the manifest's own detection. Null when neither is usable.
    /// </summary>
    private static (StageDetection? Detection, string DeploymentType, int DeploymentTypeCount) ImportableDetection(StageManifest manifest)
    {
        var entries = (manifest.DeploymentTypes ?? []).Where(e => e is not null).ToList();
        var manifestDetection = DeploymentTypeSpecResolver.IsUsable(manifest.Detection) ? manifest.Detection : null;
        if (entries.Count == 0) return (manifestDetection, "", 0);
        var first = entries[0];
        var detection = DeploymentTypeSpecResolver.IsUsable(first.Detection) ? first.Detection : manifestDetection;
        return (detection, first.NameSuffix, entries.Count);
    }

    private string WithFallbackNotes(string message) =>
        _fallbackNotes.Count == 0 ? message : $"{message} {string.Join(" ", _fallbackNotes)}";

    /// <summary>
    /// Fills the inputs from a clause; a compound clause becomes the Compound type with its tree.
    /// False, with the inputs unchanged, when the clause type is not supported.
    /// </summary>
    public bool LoadDetection(StageDetection detection)
    {
        _fallbackNotes.Clear();
        var hive = detection.IsCurrentUserHive ? DetectionClauseText.Hkcu : DetectionClauseText.Hklm;
        var view = detection.Is64Bit ? View64 : View32;
        switch (detection.Type)
        {
            case RegistryKeyValue:
                KeyPath = detection.RegistryKeyRelative ?? "";
                ValueName = detection.ValueName ?? "";
                ExpectedValue = detection.ExpectedValue ?? "";
                RegistryOperator = Pick(RegistryOperators, detection.Operator, DetectionEvaluator.RegistryDefaultOperator, "operator");
                RegistryPropertyType = Pick(RegistryPropertyTypes, detection.PropertyType, "String", "data type");
                SelectedHive = hive;
                SelectedView = view;
                break;
            case RegistryKey:
                KeyPath = detection.RegistryKeyRelative ?? "";
                SelectedHive = hive;
                SelectedView = view;
                break;
            case File:
                FilePath = detection.FilePath ?? "";
                FileName = detection.FileName ?? "";
                FileProperty = string.Equals(detection.PropertyType, "Version", StringComparison.OrdinalIgnoreCase) ? "Version" : "Existence";
                if (FileProperty == "Version") FileOperator = Pick(FileOperators, detection.Operator, DetectionEvaluator.FileDefaultOperator, "operator");
                else FileOperator = DetectionEvaluator.FileDefaultOperator;
                FileExpectedValue = detection.ExpectedValue ?? "";
                SelectedView = view;
                break;
            case Script:
                ScriptText = detection.ScriptText ?? "";
                break;
            case Compound:
                if (!ClauseTypes.Contains(Compound)) ClauseTypes.Add(Compound);
                ImportedDetection = detection;
                ImportedTree.Clear();
                ImportedTree.Add(DetectionClauseText.Tree(detection));
                break;
            default:
                StatusMessage = $"The clause type {detection.Type} is not supported.";
                return false;
        }
        SelectedClauseType = detection.Type;
        return true;
    }

    /// <summary>The offered entry that matches the value; an unknown value takes the fallback and adds a note for the status line.</summary>
    private string Pick(IReadOnlyList<string> offered, string? value, string fallback, string what)
    {
        var match = offered.FirstOrDefault(o => string.Equals(o, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        if (!string.IsNullOrWhiteSpace(value)) _fallbackNotes.Add($"The {what} {value.Trim()} is not offered. The inputs use {fallback}.");
        return fallback;
    }

    private bool HasSelectedHistory() => SelectedHistory is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedHistory))]
    private void UseSelected()
    {
        if (SelectedHistory is not { } row) return;
        if (!LoadDetection(row.Entry.Detection)) return;
        Negative = row.Entry.Negative;
        StatusMessage = WithFallbackNotes("Filled the inputs from the selected test.");
    }

    [RelayCommand]
    private Task ExportCsv() => ExportAsync("CSV", _services.Reports.WriteCsv);

    [RelayCommand]
    private Task ExportHtml() => ExportAsync("HTML", _services.Reports.WriteHtml);

    private async Task ExportAsync(string format, Func<IReadOnlyList<DetectionHistoryEntry>, string> write)
    {
        if (History.Count == 0)
        {
            StatusMessage = "The history is empty.";
            return;
        }
        var entries = History.Select(r => r.Entry).ToList();
        string path;
        try
        {
            path = await Task.Run(() => write(entries));
        }
        catch (Exception ex)
        {
            StatusMessage = ex is DetectionFolderRefusedException ? ex.Message : $"The {format} report could not be written.";
            _services.Log.Warning(LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        StatusMessage = $"Saved the {format} report to {path}.";
        _services.Log.Info(LogArea, $"Saved a {format} report with {entries.Count} tests.");
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) _services.Prompts.OpenFolder(folder);
    }

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (History.Count == 0) return;
        if (!await _services.Prompts.ConfirmClearHistoryAsync(History.Count)) return;
        try
        {
            await Task.Run(_services.History.Clear);
        }
        catch (Exception ex)
        {
            StatusMessage = ex is DetectionFolderRefusedException ? ex.Message : "The history could not be cleared.";
            _services.Log.Warning(LogArea, $"The history could not be cleared ({ex.GetType().Name}).");
            return;
        }
        History.Clear();
        StatusMessage = "Cleared the history.";
        _services.Log.Info(LogArea, "Cleared the history.");
    }

    private bool CanRefreshApplications() => !IsLoadingApplications;

    [RelayCommand(CanExecute = nameof(CanRefreshApplications))]
    private Task RefreshApplications() => LoadApplicationsAsync();

    private async Task LoadApplicationsAsync()
    {
        if (_applicationsLoad is { } pending)
        {
            await pending;
            return;
        }
        var load = LoadApplicationsCoreAsync(_applicationsGeneration);
        _applicationsLoad = load;
        try { await load; }
        finally
        {
            if (ReferenceEquals(_applicationsLoad, load)) _applicationsLoad = null;
        }
    }

    /// <summary>Reads the applications; a result that arrives after the generation changed (page left, choice toggled) is discarded.</summary>
    private async Task LoadApplicationsCoreAsync(int generation)
    {
        IsLoadingApplications = true;
        ApplicationStatus = "Reading the installed applications.";
        var include = ShowSystemComponents;
        IReadOnlyList<InstalledApplication> applications;
        string status = "";
        try
        {
            applications = await Task.Run(() => _services.InstalledApplications.Enumerate(include));
            if (generation == _applicationsGeneration)
                _services.Log.Info(LogArea, $"Read {applications.Count} installed applications (system components {(include ? "shown" : "hidden")}).");
        }
        catch (Exception ex)
        {
            applications = [];
            status = "The installed applications could not be read.";
            if (generation == _applicationsGeneration)
                _services.Log.Warning(LogArea, $"The installed applications could not be read ({ex.GetType().Name}).");
        }
        if (generation != _applicationsGeneration) return;
        _allApplications = applications;
        ApplicationStatus = status;
        IsLoadingApplications = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_allApplications is null) return;
        var filter = ApplicationFilter.Trim();
        var selected = SelectedApplication;
        Applications.Clear();
        foreach (var application in _allApplications)
        {
            if (filter.Length == 0
                || application.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || application.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || application.Version.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Applications.Add(application);
        }
        SelectedApplication = selected is not null && Applications.Contains(selected) ? selected : null;
        ApplicationCount = Applications.Count == _allApplications.Count
            ? $"{_allApplications.Count} applications"
            : $"{Applications.Count} of {_allApplications.Count} applications";
    }

    public static string DetailsText(InstalledApplication application)
    {
        var text = new StringBuilder();
        text.AppendLine($"Name: {application.Name}");
        text.AppendLine($"Publisher: {application.Publisher}");
        text.AppendLine($"Version: {application.Version}");
        text.AppendLine($"Install date: {application.InstallDate}");
        text.AppendLine($"Source: {application.Source}");
        text.AppendLine($"Registry key: {application.KeyPath}");
        text.AppendLine($"Uninstall string: {application.UninstallString}");
        text.AppendLine($"Quiet uninstall string: {application.QuietUninstallString}");
        text.Append($"Install location: {application.InstallLocation}");
        return text.ToString();
    }

    private bool HasApplication() => SelectedApplication is not null;

    [RelayCommand(CanExecute = nameof(HasApplication))]
    private void CopyDetails()
    {
        if (SelectedApplication is not { } application) return;
        ApplicationStatus = _services.Prompts.CopyText(DetailsText(application))
            ? $"Copied the details of {application.Name}."
            : "The details could not be copied. Another program holds the clipboard.";
    }

    /// <summary>
    /// Fills a registry value clause on the application's uninstall key: DisplayVersion equal to
    /// its version, or DisplayName equal to its name when it records no version.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasApplication))]
    private void UseForDetection()
    {
        if (SelectedApplication is not { } application) return;
        var (hive, is64Bit, relative) = DetectionClauseText.UninstallKey(application);
        var hasVersion = !string.IsNullOrWhiteSpace(application.Version);
        LoadDetection(new StageDetection
        {
            Type = RegistryKeyValue,
            Hive = hive == DetectionClauseText.Hkcu ? "CurrentUser" : "LocalMachine",
            Is64Bit = is64Bit,
            RegistryKeyRelative = relative,
            ValueName = hasVersion ? "DisplayVersion" : "DisplayName",
            Operator = "IsEquals",
            PropertyType = "String",
            ExpectedValue = hasVersion ? application.Version : application.Name
        });
        Negative = false;
        SelectedTabIndex = 0;
        StatusMessage = "Filled a registry value clause from the selected application.";
    }
}
