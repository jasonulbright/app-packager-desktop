using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Installer Analysis page reads and writes; the page logic depends on nothing else.</summary>
public sealed record InstallerAnalysisPageServices(
    IInstallerAnalysisService Analysis,
    IAnalysisSandbox Sandbox,
    IInstallerReportWriter Reports,
    IInstallerAnalysisPrompts Prompts,
    Func<InstallerAnalysisSettings?> LoadPreferences,
    Action<InstallerAnalysisSettings> SavePreferences,
    IAppLog Log,
    AnalysisLimits Limits,
    Func<string> SandboxRoot);

/// <summary>
/// The Installer Analysis page: one analysis at a time, a drill-down stack of inner installers and
/// views that load when their tab first shows. Nothing is read at construction and nothing is kept
/// after the user leaves the page except the recent files and the page choices.
/// </summary>
public partial class InstallerAnalysisViewModel : ObservableObject
{
    public const string LogArea = "InstallerAnalysis";

    public const int OverviewTab = 0;
    public const int MsiTab = 1;
    public const int PayloadTab = 2;
    public const int InnerTab = 3;
    public const int FilesTab = 4;
    public const int RegistryTab = 5;
    public const int StringsTab = 6;
    private const int TabCount = 7;

    public const int RecentLimit = 10;
    public const string AllFolders = "All folders";
    public const string AllCategories = "All categories";

    public const string EnterPathMessage = "Enter the path of an installer, or press Open.";
    public const string InvalidPathMessage = "The path is not valid.";
    public const string OneFileMessage = "Drop one file at a time.";
    public const string FolderMessage = "A folder cannot be analyzed. Open one installer file.";
    public const string NoAnalysisMessage = "Open an installer first.";
    public const string SelectEntryMessage = "Select an entry first.";
    public const string FolderEntryMessage = "Select a file, not a folder.";
    public const string SandboxFolderMessage = "Choose a folder outside the temporary analysis folder.";
    public const string LinkedFolderMessage = "The folder is a junction or a symbolic link. Choose a plain folder.";
    public const string MissingFolderMessage = "The folder does not exist.";
    public const string CancelledMessage = "The analysis was cancelled.";
    public const string FailedMessage = "The installer could not be analyzed. The log has the details.";
    public const string ExtractFailedMessage = "The entry could not be extracted. The log has the details.";
    public const string NoPayloadMessage = "The installer has no payload to extract.";
    public const string LoadOnShowMessage = "The list loads when its tab opens.";
    public const string ClipboardBusyMessage = "The text could not be copied. Another program holds the clipboard.";

    public static string MissingFileMessage(string path) => $"The file {path} does not exist.";

    public static string DepthLimitMessage(int depth) =>
        $"The drill-down limit of {depth} levels is reached. Use Extract to copy the inner installer to a folder.";

    /// <summary>The bounds every parser of an analysis keeps to.</summary>
    public static readonly AnalysisLimits DefaultLimits = new(
        MaxFileBytes: 8L * 1024 * 1024 * 1024,
        MaxEntries: 100_000,
        MaxExtractBytes: 8L * 1024 * 1024 * 1024,
        MaxDepth: 5,
        Timeout: TimeSpan.FromMinutes(10),
        MaxStrings: 50_000);

    private readonly InstallerAnalysisPageServices _services;
    private readonly List<AnalysisFrame> _frames = [];
    private readonly List<Operation> _running = [];
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private int _generation;
    private string? _session;
    private bool _preferencesLoaded;
    private bool _applyingPreferences;
    private Task _preferencesLoad = Task.CompletedTask;
    private InstallerAnalysisSettings? _storedChoices;

    public InstallerAnalysisViewModel(IInstallerAnalysisService analysis, IAnalysisSandbox sandbox, IInstallerReportWriter reports,
        IInstallerAnalysisPrompts prompts, SettingsService settings, IAppLog log)
        : this(new InstallerAnalysisPageServices(analysis, sandbox, reports, prompts,
            () => InstallerAnalysisPreferences.Load(settings),
            choices => InstallerAnalysisPreferences.Save(settings, choices),
            log, DefaultLimits, () => Path.Combine(AppDataPaths.Root, "InstallerAnalysis", "Temp")))
    {
    }

    public InstallerAnalysisViewModel(InstallerAnalysisPageServices services)
    {
        _services = services;
    }

    private sealed class AnalysisFrame(string path, string name, AnalysisReport report)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public AnalysisReport Report { get; } = report;
        public int SelectedTab { get; set; }
        public IReadOnlyList<PayloadEntry>? Payload { get; set; }
        public string PayloadMessage { get; set; } = "";
        public Task? PayloadLoad { get; set; }
        public IReadOnlyList<RegistryWrite>? Registry { get; set; }
        public string RegistryMessage { get; set; } = "";
        public Task? RegistryLoad { get; set; }
        public IReadOnlyList<InterestingString>? Strings { get; set; }
        public string StringsMessage { get; set; } = "";
        public Task? StringsLoad { get; set; }
        public Dictionary<string, string> Extracted { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A running piece of work; Cancel cancels every one, and leaving the page waits for them to stop.</summary>
    private sealed class Operation(string text)
    {
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Text { get; set; } = text;
    }

    /// <summary>Reports progress on the thread that created it, and drops a line from superseded work.</summary>
    private sealed class StepProgress(Action<string> report) : IProgress<string>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public void Report(string value)
        {
            if (_context is null) report(value);
            else _context.Post(_ => report(value), null);
        }
    }

    [ObservableProperty] private string _pathText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private int _runningCount;

    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport), nameof(HasNotes), nameof(HasPostPatch), nameof(HasSignature))]
    private AnalysisReport? _report;

    [ObservableProperty] private IReadOnlyList<BreadcrumbSegment> _breadcrumb = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    [NotifyPropertyChangedFor(nameof(HasBreadcrumb))]
    private bool _canGoBack;

    [ObservableProperty] private IReadOnlyList<NameValueRow> _sourceRows = [];
    [ObservableProperty] private IReadOnlyList<FactRow> _deploymentRows = [];
    [ObservableProperty] private IReadOnlyList<ModeRow> _modeRows = [];
    [ObservableProperty] private IReadOnlyList<MetadataRow> _metadataRows = [];
    [ObservableProperty] private IReadOnlyList<NameValueRow> _postPatchRows = [];
    [ObservableProperty] private string _postPatchExplanation = "";
    [ObservableProperty] private IReadOnlyList<string> _notes = [];
    [ObservableProperty] private string _msiSummary = "";

    [ObservableProperty] private IReadOnlyList<FactRow> _msiRows = [];
    [ObservableProperty] private string _msiFilter = "";
    [ObservableProperty] private string _msiCount = "";

    [ObservableProperty] private IReadOnlyList<PayloadRow> _payloadRows = [];
    [ObservableProperty] private PayloadRow? _selectedPayload;
    [ObservableProperty] private string _payloadFilter = "";
    [ObservableProperty] private string _payloadStatus = "";

    [ObservableProperty] private IReadOnlyList<PayloadRow> _innerRows = [];
    [ObservableProperty] private PayloadRow? _selectedInner;
    [ObservableProperty] private string _innerStatus = "";

    [ObservableProperty] private IReadOnlyList<PayloadRow> _fileRows = [];
    [ObservableProperty] private PayloadRow? _selectedFile;
    [ObservableProperty] private IReadOnlyList<string> _fileFolders = [AllFolders];
    [ObservableProperty] private string _selectedFolder = AllFolders;
    [ObservableProperty] private string _fileFilter = "";
    [ObservableProperty] private string _fileStatus = "";

    [ObservableProperty] private IReadOnlyList<RegistryRow> _registryRows = [];
    [ObservableProperty] private RegistryRow? _selectedRegistry;
    [ObservableProperty] private string _registryFilter = "";
    [ObservableProperty] private string _registryStatus = "";

    [ObservableProperty] private IReadOnlyList<StringRow> _stringRows = [];
    [ObservableProperty] private StringRow? _selectedString;
    [ObservableProperty] private string _stringsFilter = "";
    [ObservableProperty] private string _stringsCategory = AllCategories;
    [ObservableProperty] private string _stringsStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRecentCommand))]
    private string? _selectedRecent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportsFolderCommand))]
    private string? _lastReportFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenExtractFolderCommand))]
    private string? _lastExtractFolder;

    public ObservableCollection<string> RecentFiles { get; } = [];

    public IReadOnlyList<string> StringCategories { get; } = [AllCategories, "Url", "Path", "Registry", "Command", "Guid", "Version", "Other"];

    public bool IsBusy => RunningCount > 0;
    public bool HasReport => Report is not null;
    public bool HasNotes => Notes.Count > 0;
    public bool HasPostPatch => Report?.PostPatch is not null;
    public bool HasSignature => Report?.Signature is not null;
    public bool HasBreadcrumb => Breadcrumb.Count > 1;

    /// <summary>The number of drill-down levels below the installer the user opened.</summary>
    public int Depth => Math.Max(0, _frames.Count - 1);

    private AnalysisFrame? Current => _frames.Count == 0 ? null : _frames[^1];

    partial void OnNotesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasNotes));
    partial void OnBreadcrumbChanged(IReadOnlyList<BreadcrumbSegment> value) => OnPropertyChanged(nameof(HasBreadcrumb));

    // ---- Navigation ----

    /// <summary>Reads the saved choices and the recent files on the first visit. No installer is read.</summary>
    public async Task OnNavigatedToAsync()
    {
        if (_preferencesLoaded) return;
        _preferencesLoaded = true;
        _preferencesLoad = ApplyPreferencesAsync();
        await _preferencesLoad;
    }

    /// <summary>Cancels the work, waits for it to stop, releases the sandbox session and every result.</summary>
    public async Task OnNavigatedFromAsync()
    {
        var stopping = CancelAll();
        _generation++;
        _frames.Clear();
        ClearView();
        StatusMessage = "";
        LastExtractFolder = null;
        LastReportFolder = null;
        await WaitForAsync(stopping);
        var session = _session;
        _session = null;
        if (session is not null) await ReleaseAsync(session);
        await SavePreferencesAsync();
    }

    private static async Task WaitForAsync(IReadOnlyList<Task> stopping)
    {
        if (stopping.Count == 0) return;
        await Task.WhenAny(Task.WhenAll(stopping), Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private async Task ReleaseAsync(string session)
    {
        try { await Task.Run(() => _services.Sandbox.Release(session)); }
        catch (Exception ex)
        {
            _services.Log.Warning(LogArea, $"The analysis session could not be removed ({ex.GetType().Name}).");
        }
    }

    // ---- Work tracking ----

    private Operation Begin(string text)
    {
        var operation = new Operation(text);
        lock (_running) _running.Add(operation);
        RunningCount = Count();
        ProgressText = text;
        return operation;
    }

    private void End(Operation operation)
    {
        Operation? latest;
        lock (_running)
        {
            _running.Remove(operation);
            latest = _running.Count == 0 ? null : _running[^1];
        }
        operation.Done.TrySetResult();
        operation.Cancel.Dispose();
        RunningCount = Count();
        ProgressText = latest?.Text ?? "";
    }

    private int Count()
    {
        lock (_running) return _running.Count;
    }

    /// <summary>Cancels every running piece of work and returns tasks that finish when each has stopped.</summary>
    private IReadOnlyList<Task> CancelAll()
    {
        List<Operation> running;
        lock (_running) running = [.. _running];
        foreach (var operation in running)
        {
            try { operation.Cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return running.Select(o => o.Done.Task).ToList();
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => CancelAll();

    // ---- Open ----

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Analyze() => OpenAsync(PathText);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Browse()
    {
        var path = _services.Prompts.PickInstaller();
        if (string.IsNullOrEmpty(path)) return;
        await OpenAsync(path);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Reanalyze() => _frames.Count > 0 ? OpenAsync(_frames[0].Path) : OpenAsync(PathText);

    /// <summary>Analyzes the dropped item when it is one file; a folder or several items give a plain message. Nothing dropped is started.</summary>
    public Task DropAsync(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return Task.CompletedTask;
        if (paths.Count > 1)
        {
            StatusMessage = OneFileMessage;
            return Task.CompletedTask;
        }
        return OpenAsync(paths[0]);
    }

    private static string Clean(string? input) => (input ?? "").Trim().Trim('"').Trim();

    /// <summary>Checks the path and analyzes the file; the drill-down stack starts again from it.</summary>
    public async Task OpenAsync(string? input)
    {
        var path = Clean(input);
        if (path.Length == 0)
        {
            StatusMessage = EnterPathMessage;
            return;
        }
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            StatusMessage = InvalidPathMessage;
            return;
        }
        if (Directory.Exists(path))
        {
            StatusMessage = FolderMessage;
            return;
        }
        if (!File.Exists(path))
        {
            StatusMessage = MissingFileMessage(path);
            return;
        }

        PathText = path;
        CancelAll();
        var generation = ++_generation;
        var old = _session;
        _session = null;
        if (old is not null) _ = ReleaseAsync(old);
        _frames.Clear();
        ClearView();
        AddRecent(path);

        var name = Path.GetFileName(path);
        var report = await AnalyzeCoreAsync(path, name, generation);
        if (report is null) return;
        _frames.Add(new AnalysisFrame(path, name, report));
        ShowFrame(_frames[^1], restoreTab: false);
        StatusMessage = $"Analyzed {name}.";
    }

    /// <summary>Runs one analysis off the UI thread; null when it failed, was cancelled or was superseded.</summary>
    private async Task<AnalysisReport?> AnalyzeCoreAsync(string path, string name, int generation)
    {
        var operation = Begin($"Analyzing {name}.");
        var progress = new StepProgress(step =>
        {
            if (generation != _generation) return;
            operation.Text = step;
            ProgressText = step;
        });
        AnalysisReport? report = null;
        Exception? failure = null;
        try
        {
            report = await Task.Run(() => _services.Analysis.AnalyzeAsync(path, _services.Limits, progress, operation.Cancel.Token), operation.Cancel.Token);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        var cancelled = operation.Cancel.IsCancellationRequested;
        End(operation);
        if (generation != _generation) return null;
        if (cancelled || failure is OperationCanceledException)
        {
            StatusMessage = CancelledMessage;
            _services.Log.Info(LogArea, "Analysis cancelled.");
            return null;
        }
        if (report is null)
        {
            StatusMessage = FailedMessage;
            _services.Log.Warning(LogArea, $"Analysis failed: file={name} error={failure?.GetType().Name}.");
            return null;
        }
        _services.Log.Info(LogArea,
            $"Analyzed: type={report.Type} architecture={report.Architecture} modes={report.Modes.Count} msiProperties={report.MsiProperties.Count} notes={report.Notes.Count} depth={_frames.Count} file={name}.");
        return report;
    }

    // ---- Recent files ----

    private void AddRecent(string path)
    {
        var existing = RecentFiles.FirstOrDefault(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) RecentFiles.Remove(existing);
        RecentFiles.Insert(0, path);
        while (RecentFiles.Count > RecentLimit) RecentFiles.RemoveAt(RecentFiles.Count - 1);
        SaveChoicesLater();
    }

    private bool HasSelectedRecent() => !string.IsNullOrEmpty(SelectedRecent);

    [RelayCommand(CanExecute = nameof(HasSelectedRecent), AllowConcurrentExecutions = true)]
    private async Task OpenRecent()
    {
        if (SelectedRecent is not { Length: > 0 } path) return;
        if (!File.Exists(path))
        {
            StatusMessage = MissingFileMessage(path);
            return;
        }
        await OpenAsync(path);
    }

    [RelayCommand]
    private void ClearRecent()
    {
        RecentFiles.Clear();
        SelectedRecent = null;
        StatusMessage = "Cleared the recent files.";
        SaveChoicesLater();
    }

    // ---- Showing a frame ----

    private void ClearView()
    {
        Report = null;
        Breadcrumb = [];
        CanGoBack = false;
        SourceRows = [];
        DeploymentRows = [];
        ModeRows = [];
        MetadataRows = [];
        PostPatchRows = [];
        PostPatchExplanation = "";
        Notes = [];
        MsiSummary = "";
        MsiRows = [];
        MsiCount = "";
        PayloadRows = [];
        SelectedPayload = null;
        PayloadStatus = "";
        InnerRows = [];
        SelectedInner = null;
        InnerStatus = "";
        FileRows = [];
        SelectedFile = null;
        FileFolders = [AllFolders];
        SelectedFolder = AllFolders;
        FileStatus = "";
        RegistryRows = [];
        SelectedRegistry = null;
        RegistryStatus = "";
        StringRows = [];
        SelectedString = null;
        StringsStatus = "";
        OnPropertyChanged(nameof(Depth));
    }

    private void ShowFrame(AnalysisFrame frame, bool restoreTab)
    {
        var report = frame.Report;
        Report = report;
        SourceRows = SourceFacts(report);
        DeploymentRows = report.Deployment.Select(FactRow.From).ToList();
        ModeRows = report.Modes.Select(ModeRow.From).ToList();
        MetadataRows = report.Metadata.SelectMany(b => b.Facts.Select(f => new MetadataRow(b.Title, f.Name, f.Value))).ToList();
        PostPatchRows = report.PostPatch is { } patch
            ?
            [
                new("Product code", patch.ProductCode),
                new("Display name", patch.DisplayName),
                new("Display version", patch.DisplayVersion),
                new("ARP key", patch.ArpKey)
            ]
            : [];
        PostPatchExplanation = report.PostPatch?.Explanation ?? "";
        Notes = report.Notes.ToList();
        MsiSummary = report.MsiProperties.Count == 0 ? "" : $"{InstallerAnalysisText.Count(report.MsiProperties.Count, "MSI property", "MSI properties")}. The MSI Properties tab lists them.";
        ApplyMsiFilter();
        ShowPayload();
        ShowRegistry();
        ShowStrings();
        Breadcrumb = _frames.Select((f, i) => new BreadcrumbSegment(i, f.Name, i == _frames.Count - 1)).ToList();
        CanGoBack = _frames.Count > 1;
        OnPropertyChanged(nameof(Depth));
        if (restoreTab) SelectedTabIndex = frame.SelectedTab;
        LoadForTab(SelectedTabIndex);
    }

    private static List<NameValueRow> SourceFacts(AnalysisReport report)
    {
        var rows = new List<NameValueRow>
        {
            new("File name", report.FileName),
            new("Size", $"{InstallerAnalysisText.Size(report.Size)} ({report.Size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes)"),
            new("SHA-256", report.Sha256),
            new("Type", report.Type),
            new("Architecture", report.Architecture)
        };
        if (report.Signature is { } signature)
        {
            rows.Add(new("Signature", signature.Status));
            rows.Add(new("Signer", signature.Subject));
            rows.Add(new("Issuer", signature.Issuer));
            rows.Add(new("Thumbprint", signature.Thumbprint));
            rows.Add(new("Timestamped", signature.Timestamped ? "Yes" : "No"));
        }
        else rows.Add(new("Signature", "Not read"));
        return rows;
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (Current is { } frame) frame.SelectedTab = value;
        LoadForTab(value);
        SaveChoicesLater();
    }

    private void LoadForTab(int tab)
    {
        if (Current is null) return;
        switch (tab)
        {
            case PayloadTab or InnerTab or FilesTab:
                _ = EnsurePayloadAsync();
                break;
            case RegistryTab:
                _ = EnsureRegistryAsync();
                break;
            case StringsTab:
                _ = EnsureStringsAsync();
                break;
        }
    }

    // ---- Filters ----

    partial void OnMsiFilterChanged(string value) => ApplyMsiFilter();
    partial void OnPayloadFilterChanged(string value) => ShowPayload();
    partial void OnFileFilterChanged(string value) => ApplyFileFilter();
    partial void OnSelectedFolderChanged(string value) => ApplyFileFilter();
    partial void OnRegistryFilterChanged(string value) => ShowRegistry();
    partial void OnStringsFilterChanged(string value) => ShowStrings();

    partial void OnStringsCategoryChanged(string value)
    {
        ShowStrings();
        SaveChoicesLater();
    }

    private static bool Has(string? text, string filter) => (text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void ApplyMsiFilter()
    {
        var all = Report?.MsiProperties ?? [];
        var filter = MsiFilter.Trim();
        MsiRows = all.Where(p => filter.Length == 0 || Has(p.Name, filter) || Has(p.Value, filter)).Select(FactRow.From).ToList();
        MsiCount = Report is null ? "" : MsiRows.Count == all.Count
            ? InstallerAnalysisText.Count(all.Count, "property", "properties")
            : $"{MsiRows.Count} of {InstallerAnalysisText.Count(all.Count, "property", "properties")}";
    }

    private void ShowPayload()
    {
        var frame = Current;
        if (frame?.Payload is not { } payload)
        {
            PayloadRows = [];
            InnerRows = [];
            FileRows = [];
            FileFolders = [AllFolders];
            var message = frame is null ? "" : frame.PayloadLoad is not null ? "Listing the payload." : frame.PayloadMessage.Length > 0 ? frame.PayloadMessage : LoadOnShowMessage;
            PayloadStatus = message;
            InnerStatus = message;
            FileStatus = message;
            return;
        }
        var rows = payload.Select(e => new PayloadRow(e)).ToList();
        var filter = PayloadFilter.Trim();
        PayloadRows = Keep(rows.Where(r => filter.Length == 0 || Has(r.Path, filter) || Has(r.Type, filter)).ToList(), SelectedPayload, s => SelectedPayload = s);
        PayloadStatus = PayloadRows.Count == rows.Count
            ? InstallerAnalysisText.Count(rows.Count, "entry", "entries")
            : $"{PayloadRows.Count} of {InstallerAnalysisText.Count(rows.Count, "entry", "entries")}";
        InnerRows = Keep(rows.Where(r => r.IsInnerInstaller).ToList(), SelectedInner, s => SelectedInner = s);
        InnerStatus = InnerRows.Count == 0 ? "The payload holds no inner installer." : InstallerAnalysisText.Count(InnerRows.Count, "inner installer");
        var folders = rows.Where(r => !r.Entry.IsDirectory).Select(r => r.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f == InstallerAnalysisText.TopLevel ? "" : f, StringComparer.OrdinalIgnoreCase).ToList();
        FileFolders = [AllFolders, .. folders];
        if (!FileFolders.Contains(SelectedFolder)) SelectedFolder = AllFolders;
        ApplyFileFilter();
    }

    private void ApplyFileFilter()
    {
        if (Current?.Payload is not { } payload) return;
        var files = payload.Where(e => !e.IsDirectory).Select(e => new PayloadRow(e)).ToList();
        var filter = FileFilter.Trim();
        var folder = SelectedFolder;
        FileRows = Keep(files.Where(r => (folder == AllFolders || string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase))
            && (filter.Length == 0 || Has(r.Name, filter))).ToList(), SelectedFile, s => SelectedFile = s);
        FileStatus = FileRows.Count == files.Count
            ? InstallerAnalysisText.Count(files.Count, "file")
            : $"{FileRows.Count} of {InstallerAnalysisText.Count(files.Count, "file")}";
    }

    private void ShowRegistry()
    {
        var frame = Current;
        if (frame?.Registry is not { } writes)
        {
            RegistryRows = [];
            RegistryStatus = frame is null ? "" : frame.RegistryLoad is not null ? "Reading the registry settings." : frame.RegistryMessage.Length > 0 ? frame.RegistryMessage : LoadOnShowMessage;
            return;
        }
        var filter = RegistryFilter.Trim();
        var rows = writes.Select(w => new RegistryRow(w)).ToList();
        RegistryRows = rows.Where(r => filter.Length == 0 || Has(r.FullKey, filter) || Has(r.Name, filter) || Has(r.Value, filter) || Has(r.Origin, filter) || Has(r.Condition, filter)).ToList();
        if (SelectedRegistry is { } selected && !RegistryRows.Contains(selected)) SelectedRegistry = null;
        RegistryStatus = writes.Count == 0 ? "The installer records no registry writes that can be read."
            : RegistryRows.Count == rows.Count ? InstallerAnalysisText.Count(rows.Count, "registry write")
            : $"{RegistryRows.Count} of {InstallerAnalysisText.Count(rows.Count, "registry write")}";
    }

    private void ShowStrings()
    {
        var frame = Current;
        if (frame?.Strings is not { } strings)
        {
            StringRows = [];
            StringsStatus = frame is null ? "" : frame.StringsLoad is not null ? "Reading the strings." : frame.StringsMessage.Length > 0 ? frame.StringsMessage : LoadOnShowMessage;
            return;
        }
        var filter = StringsFilter.Trim();
        var category = StringsCategory == AllCategories ? "" : StringsCategory;
        var rows = strings.Where(s => (category.Length == 0 || string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase))
            && (filter.Length == 0 || Has(s.Text, filter))).Select(s => new StringRow(s)).ToList();
        StringRows = rows;
        if (SelectedString is { } selected && !rows.Contains(selected)) SelectedString = null;
        StringsStatus = rows.Count == strings.Count ? InstallerAnalysisText.Count(strings.Count, "string")
            : $"{rows.Count} of {InstallerAnalysisText.Count(strings.Count, "string")}";
    }

    /// <summary>Keeps the selection when the same entry is still listed; the row objects are new after every filter.</summary>
    private static IReadOnlyList<PayloadRow> Keep(List<PayloadRow> rows, PayloadRow? selected, Action<PayloadRow?> select)
    {
        if (selected is not null)
        {
            var same = rows.FirstOrDefault(r => ReferenceEquals(r.Entry, selected.Entry));
            if (same is null) select(null);
            else rows[rows.IndexOf(same)] = selected;
        }
        return rows;
    }

    // ---- Views that load on first show ----

    private Task EnsurePayloadAsync()
    {
        var frame = Current;
        if (frame is null || frame.Payload is not null) return Task.CompletedTask;
        if (frame.PayloadLoad is { } pending) return pending;
        var load = LoadPayloadAsync(frame);
        if (!load.IsCompleted) frame.PayloadLoad = load;
        ShowPayload();
        return load;
    }

    private async Task LoadPayloadAsync(AnalysisFrame frame)
    {
        var operation = Begin("Listing the payload.");
        try
        {
            var entries = await Task.Run(() => _services.Analysis.ListPayloadAsync(frame.Path, _services.Limits, operation.Cancel.Token), operation.Cancel.Token);
            frame.Payload = entries;
            frame.PayloadMessage = "";
            _services.Log.Info(LogArea, $"Listed the payload: entries={entries.Count} innerInstallers={entries.Count(e => string.Equals(e.Kind, "InnerInstaller", StringComparison.OrdinalIgnoreCase))}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException || operation.Cancel.IsCancellationRequested)
        {
            frame.PayloadMessage = "Listing the payload was cancelled. Open the tab again to list it.";
        }
        catch (Exception ex)
        {
            frame.PayloadMessage = "The payload could not be listed. The log has the details.";
            _services.Log.Warning(LogArea, $"The payload could not be listed ({ex.GetType().Name}).");
        }
        finally
        {
            End(operation);
            frame.PayloadLoad = null;
        }
        if (ReferenceEquals(frame, Current)) ShowPayload();
    }

    private Task EnsureRegistryAsync()
    {
        var frame = Current;
        if (frame is null || frame.Registry is not null) return Task.CompletedTask;
        if (frame.RegistryLoad is { } pending) return pending;
        var load = LoadRegistryAsync(frame);
        if (!load.IsCompleted) frame.RegistryLoad = load;
        ShowRegistry();
        return load;
    }

    private async Task LoadRegistryAsync(AnalysisFrame frame)
    {
        var operation = Begin("Reading the registry settings.");
        try
        {
            var writes = await Task.Run(() => _services.Analysis.ReadRegistryWritesAsync(frame.Path, _services.Limits, operation.Cancel.Token), operation.Cancel.Token);
            frame.Registry = writes;
            frame.RegistryMessage = "";
            _services.Log.Info(LogArea, $"Read the registry settings: writes={writes.Count}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException || operation.Cancel.IsCancellationRequested)
        {
            frame.RegistryMessage = "Reading the registry settings was cancelled. Open the tab again to read them.";
        }
        catch (Exception ex)
        {
            frame.RegistryMessage = "The registry settings could not be read. The log has the details.";
            _services.Log.Warning(LogArea, $"The registry settings could not be read ({ex.GetType().Name}).");
        }
        finally
        {
            End(operation);
            frame.RegistryLoad = null;
        }
        if (ReferenceEquals(frame, Current)) ShowRegistry();
    }

    private Task EnsureStringsAsync()
    {
        var frame = Current;
        if (frame is null || frame.Strings is not null) return Task.CompletedTask;
        if (frame.StringsLoad is { } pending) return pending;
        var load = LoadStringsAsync(frame);
        if (!load.IsCompleted) frame.StringsLoad = load;
        ShowStrings();
        return load;
    }

    private async Task LoadStringsAsync(AnalysisFrame frame)
    {
        var operation = Begin("Reading the strings.");
        try
        {
            var strings = await Task.Run(() => _services.Analysis.ReadStringsAsync(frame.Path, _services.Limits, operation.Cancel.Token), operation.Cancel.Token);
            frame.Strings = strings;
            frame.StringsMessage = strings.Count >= _services.Limits.MaxStrings
                ? $"The list stops at {_services.Limits.MaxStrings.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} strings."
                : "";
            _services.Log.Info(LogArea, $"Read the strings: count={strings.Count}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException || operation.Cancel.IsCancellationRequested)
        {
            frame.StringsMessage = "Reading the strings was cancelled. Open the tab again to read them.";
        }
        catch (Exception ex)
        {
            frame.StringsMessage = "The strings could not be read. The log has the details.";
            _services.Log.Warning(LogArea, $"The strings could not be read ({ex.GetType().Name}).");
        }
        finally
        {
            End(operation);
            frame.StringsLoad = null;
        }
        if (ReferenceEquals(frame, Current))
        {
            var limit = frame.Strings is not null ? frame.StringsMessage : "";
            ShowStrings();
            if (limit.Length > 0) StringsStatus = $"{StringsStatus} {limit}";
        }
    }

    // ---- Entry actions ----

    /// <summary>The row an entry action works on: the parameter, else the selection on the tab that shows.</summary>
    private PayloadRow? ActiveRow(PayloadRow? parameter) => parameter ?? SelectedTabIndex switch
    {
        InnerTab => SelectedInner,
        FilesTab => SelectedFile,
        _ => SelectedPayload
    };

    private (AnalysisFrame Frame, PayloadRow Row)? FileEntry(PayloadRow? parameter)
    {
        if (Current is not { } frame)
        {
            StatusMessage = NoAnalysisMessage;
            return null;
        }
        if (ActiveRow(parameter) is not { } row)
        {
            StatusMessage = SelectEntryMessage;
            return null;
        }
        if (row.Entry.IsDirectory)
        {
            StatusMessage = FolderEntryMessage;
            return null;
        }
        return (frame, row);
    }

    private async Task<string?> SessionAsync()
    {
        if (_session is { } existing) return existing;
        try
        {
            var created = await Task.Run(_services.Sandbox.NewSession);
            if (_session is { } raced)
            {
                _ = ReleaseAsync(created);
                return raced;
            }
            _session = created;
            return created;
        }
        catch (Exception ex)
        {
            StatusMessage = "The temporary analysis folder could not be created. The log has the details.";
            _services.Log.Warning(LogArea, $"The analysis session could not be created ({ex.GetType().Name}).");
            return null;
        }
    }

    /// <summary>The entry's copy inside the sandbox session, extracted once per frame; null when it failed or was cancelled.</summary>
    private async Task<string?> ExtractToSessionAsync(AnalysisFrame frame, PayloadEntry entry, Func<bool> stillWanted)
    {
        if (frame.Extracted.TryGetValue(entry.Path, out var known) && File.Exists(known)) return known;
        var session = await SessionAsync();
        if (session is null) return null;
        var operation = Begin($"Extracting {InstallerAnalysisText.EntryName(entry.Path)}.");
        string? extracted = null;
        Exception? failure = null;
        try
        {
            extracted = await Task.Run(() => _services.Analysis.ExtractEntryAsync(frame.Path, entry, session, _services.Limits, operation.Cancel.Token), operation.Cancel.Token);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        var cancelled = operation.Cancel.IsCancellationRequested || failure is OperationCanceledException;
        End(operation);
        if (!stillWanted()) return null;
        if (cancelled)
        {
            StatusMessage = "The extraction was cancelled.";
            return null;
        }
        if (extracted is null)
        {
            StatusMessage = ExtractFailedMessage;
            _services.Log.Warning(LogArea, $"An entry could not be extracted ({failure?.GetType().Name}).");
            return null;
        }
        frame.Extracted[entry.Path] = extracted;
        return extracted;
    }

    /// <summary>Extracts the selected inner installer into the sandbox and analyzes it as the next drill-down level.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task AnalyzeSelected(PayloadRow? parameter)
    {
        if (FileEntry(parameter) is not ({ } frame, { } row)) return;
        if (Depth >= _services.Limits.MaxDepth)
        {
            StatusMessage = DepthLimitMessage(_services.Limits.MaxDepth);
            _services.Log.Info(LogArea, $"Drill-down refused at the depth limit {_services.Limits.MaxDepth}.");
            return;
        }
        CancelAll();
        var generation = ++_generation;
        frame.SelectedTab = SelectedTabIndex;
        var extracted = await ExtractToSessionAsync(frame, row.Entry, () => generation == _generation);
        if (extracted is null || generation != _generation) return;
        var report = await AnalyzeCoreAsync(extracted, row.Name, generation);
        if (report is null || !ReferenceEquals(frame, Current)) return;
        _frames.Add(new AnalysisFrame(extracted, row.Name, report));
        ShowFrame(_frames[^1], restoreTab: false);
        StatusMessage = $"Analyzed {row.Name}, level {Depth} of {_services.Limits.MaxDepth}.";
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (_frames.Count > 1) GoTo(_frames.Count - 2);
    }

    [RelayCommand]
    private void GoToLevel(BreadcrumbSegment? segment)
    {
        if (segment is null || segment.Level < 0 || segment.Level >= _frames.Count - 1) return;
        GoTo(segment.Level);
    }

    private void GoTo(int level)
    {
        CancelAll();
        _generation++;
        _frames.RemoveRange(level + 1, _frames.Count - level - 1);
        ShowFrame(_frames[^1], restoreTab: true);
        StatusMessage = $"Back to {_frames[^1].Name}.";
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExtractToTemp(PayloadRow? parameter)
    {
        if (FileEntry(parameter) is not ({ } frame, { } row)) return;
        var extracted = await ExtractToSessionAsync(frame, row.Entry, () => ReferenceEquals(frame, Current));
        if (extracted is null) return;
        LastExtractFolder = Path.GetDirectoryName(extracted);
        StatusMessage = $"Extracted {row.Name} to the temporary analysis folder. The folder is removed when you leave the page.";
        _services.Log.Info(LogArea, "Extracted an entry to the analysis session.");
    }

    [RelayCommand]
    private void CopyName(PayloadRow? parameter)
    {
        if (ActiveRow(parameter) is not { } row)
        {
            StatusMessage = Current is null ? NoAnalysisMessage : SelectEntryMessage;
            return;
        }
        Copy(row.Name, $"Copied the name {row.Name}.");
    }

    [RelayCommand]
    private void CopyPath(PayloadRow? parameter)
    {
        if (ActiveRow(parameter) is not { } row)
        {
            StatusMessage = Current is null ? NoAnalysisMessage : SelectEntryMessage;
            return;
        }
        Copy(row.Path, $"Copied the path {row.Path}.");
    }

    private void Copy(string text, string done) => StatusMessage = _services.Prompts.CopyText(text) ? done : ClipboardBusyMessage;

    /// <summary>Computes the SHA-256 of the entry on demand from its copy in the sandbox.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Hash(PayloadRow? parameter)
    {
        if (FileEntry(parameter) is not ({ } frame, { } row)) return;
        var extracted = await ExtractToSessionAsync(frame, row.Entry, () => ReferenceEquals(frame, Current));
        if (extracted is null) return;
        var operation = Begin($"Computing the SHA-256 of {row.Name}.");
        string? hash = null;
        Exception? failure = null;
        try { hash = await Task.Run(() => _services.Analysis.ComputeSha256Async(extracted, operation.Cancel.Token), operation.Cancel.Token); }
        catch (Exception ex) { failure = ex; }
        var cancelled = operation.Cancel.IsCancellationRequested || failure is OperationCanceledException;
        End(operation);
        if (!ReferenceEquals(frame, Current)) return;
        if (cancelled) StatusMessage = "The SHA-256 computation was cancelled.";
        else if (hash is null)
        {
            StatusMessage = "The SHA-256 could not be computed. The log has the details.";
            _services.Log.Warning(LogArea, $"The SHA-256 could not be computed ({failure?.GetType().Name}).");
        }
        else StatusMessage = $"SHA-256 of {row.Name}: {hash}";
    }

    /// <summary>A plain reason the folder cannot take extracted files, or null when it can.</summary>
    public string? RefusedFolder(string folder)
    {
        string full;
        try { full = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidPathMessage;
        }
        if (!Directory.Exists(full)) return MissingFolderMessage;
        if (AppDataPaths.IsWithin(full, _services.SandboxRoot())) return SandboxFolderMessage;
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (AppDataPaths.IsReparsePointDirectory(current)) return LinkedFolderMessage;
        return null;
    }

    private string? PickTarget(string title)
    {
        var folder = _services.Prompts.PickFolder(title);
        if (string.IsNullOrEmpty(folder)) return null;
        if (RefusedFolder(folder) is { } reason)
        {
            StatusMessage = reason;
            _services.Log.Info(LogArea, "Extraction refused: the chosen folder is not usable.");
            return null;
        }
        return Path.GetFullPath(folder);
    }

    /// <summary>Copies the selected inner installer to a folder the user chooses; nothing is started.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExtractInner(PayloadRow? parameter)
    {
        if (FileEntry(parameter) is not ({ } frame, { } row)) return;
        if (PickTarget($"Copy {row.Name} to a folder") is not { } folder) return;
        var operation = Begin($"Copying {row.Name}.");
        Exception? failure = null;
        try { await Task.Run(() => _services.Analysis.ExtractEntryAsync(frame.Path, row.Entry, folder, _services.Limits, operation.Cancel.Token), operation.Cancel.Token); }
        catch (Exception ex) { failure = ex; }
        var cancelled = operation.Cancel.IsCancellationRequested || failure is OperationCanceledException;
        End(operation);
        if (cancelled) StatusMessage = "The copy was cancelled.";
        else if (failure is not null)
        {
            StatusMessage = ExtractFailedMessage;
            _services.Log.Warning(LogArea, $"An inner installer could not be copied ({failure.GetType().Name}).");
        }
        else
        {
            LastExtractFolder = folder;
            StatusMessage = $"Copied {row.Name} to {folder}.";
            _services.Log.Info(LogArea, "Copied an inner installer to a chosen folder.");
        }
    }

    /// <summary>Extracts every file of the payload to a folder the user chooses; nothing is started.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExtractPayload()
    {
        if (Current is not { } frame)
        {
            StatusMessage = NoAnalysisMessage;
            return;
        }
        if (PickTarget("Extract the payload to a folder") is not { } folder) return;
        await EnsurePayloadAsync();
        if (frame.Payload is not { } payload)
        {
            StatusMessage = frame.PayloadMessage.Length > 0 ? frame.PayloadMessage : "The payload could not be listed. The log has the details.";
            return;
        }
        var files = payload.Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            StatusMessage = NoPayloadMessage;
            return;
        }
        var operation = Begin($"Extracting {InstallerAnalysisText.Count(files.Count, "file")}.");
        var done = 0;
        Exception? failure = null;
        try
        {
            foreach (var entry in files)
            {
                operation.Cancel.Token.ThrowIfCancellationRequested();
                operation.Text = $"Extracting file {done + 1} of {files.Count}.";
                ProgressText = operation.Text;
                await Task.Run(() => _services.Analysis.ExtractEntryAsync(frame.Path, entry, folder, _services.Limits, operation.Cancel.Token), operation.Cancel.Token);
                done++;
            }
        }
        catch (Exception ex) { failure = ex; }
        var cancelled = operation.Cancel.IsCancellationRequested || failure is OperationCanceledException;
        End(operation);
        LastExtractFolder = done > 0 ? folder : LastExtractFolder;
        if (cancelled) StatusMessage = $"Extracted {done} of {InstallerAnalysisText.Count(files.Count, "file")} to {folder} before the cancel.";
        else if (failure is not null)
        {
            StatusMessage = $"Extracted {done} of {InstallerAnalysisText.Count(files.Count, "file")} to {folder}. The next file could not be extracted. The log has the details.";
            _services.Log.Warning(LogArea, $"The payload extraction stopped after {done} of {files.Count} files ({failure.GetType().Name}).");
        }
        else
        {
            StatusMessage = $"Extracted {InstallerAnalysisText.Count(files.Count, "file")} to {folder}.";
            _services.Log.Info(LogArea, $"Extracted the payload: files={files.Count}.");
        }
    }

    private bool CanOpenExtractFolder() => LastExtractFolder is not null;

    [RelayCommand(CanExecute = nameof(CanOpenExtractFolder))]
    private void OpenExtractFolder()
    {
        if (LastExtractFolder is { } folder) _services.Prompts.OpenFolder(folder);
    }

    // ---- Registry and strings actions ----

    [RelayCommand]
    private void CopyRegistryKey(RegistryRow? row)
    {
        if ((row ?? SelectedRegistry) is not { } r) { StatusMessage = SelectEntryMessage; return; }
        Copy(r.FullKey, "Copied the key path.");
    }

    [RelayCommand]
    private void CopyRegistryValue(RegistryRow? row)
    {
        if ((row ?? SelectedRegistry) is not { } r) { StatusMessage = SelectEntryMessage; return; }
        Copy(r.Value, "Copied the value.");
    }

    [RelayCommand]
    private void CopyRegistryRows()
    {
        if (RegistryRows.Count == 0) { StatusMessage = "No registry write is shown."; return; }
        var lines = RegistryRows.Select(r => string.Join('\t', r.Origin, r.FullKey, r.Name, r.Value, r.Type, r.Condition));
        Copy(string.Join(Environment.NewLine, lines), $"Copied {InstallerAnalysisText.Count(RegistryRows.Count, "registry write")}.");
    }

    [RelayCommand]
    private void CopyString(StringRow? row)
    {
        if ((row ?? SelectedString) is not { } s) { StatusMessage = SelectEntryMessage; return; }
        Copy(s.Text, "Copied the string.");
    }

    [RelayCommand]
    private void CopyStrings()
    {
        if (StringRows.Count == 0) { StatusMessage = "No string is shown."; return; }
        Copy(string.Join(Environment.NewLine, StringRows.Select(s => s.Text)), $"Copied {InstallerAnalysisText.Count(StringRows.Count, "string")}.");
    }

    // ---- Exports ----

    [RelayCommand]
    private void CopySummary()
    {
        if (Report is not { } report) { StatusMessage = NoAnalysisMessage; return; }
        string text;
        try { text = _services.Reports.Summary(report); }
        catch (Exception ex)
        {
            StatusMessage = "The summary could not be built. The log has the details.";
            _services.Log.Warning(LogArea, $"The summary could not be built ({ex.GetType().Name}).");
            return;
        }
        Copy(text, "Copied the summary.");
    }

    /// <summary>Copies the ConfigMgr digest; key material of a .intunewin package is never included.</summary>
    [RelayCommand]
    private void CopyJson()
    {
        if (Report is not { } report) { StatusMessage = NoAnalysisMessage; return; }
        string text;
        try { text = _services.Reports.Json(report, includeKeyMaterial: false); }
        catch (Exception ex)
        {
            StatusMessage = "The JSON could not be built. The log has the details.";
            _services.Log.Warning(LogArea, $"The JSON could not be built ({ex.GetType().Name}).");
            return;
        }
        Copy(text, "Copied the JSON digest.");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExportCsv()
    {
        if (Current is not { } frame) { StatusMessage = NoAnalysisMessage; return; }
        await EnsureRegistryAsync();
        IReadOnlyList<RegistryWrite> registry = frame.Registry ?? [];
        await ExportAsync("CSV", () => _services.Reports.WriteCsv(frame.Report, registry));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExportHtml()
    {
        if (Current is not { } frame) { StatusMessage = NoAnalysisMessage; return; }
        await EnsurePayloadAsync();
        await EnsureRegistryAsync();
        IReadOnlyList<PayloadEntry> payload = frame.Payload ?? [];
        IReadOnlyList<RegistryWrite> registry = frame.Registry ?? [];
        await ExportAsync("HTML", () => _services.Reports.WriteHtml(frame.Report, payload, registry));
    }

    private async Task ExportAsync(string format, Func<string> write)
    {
        string path;
        try { path = await Task.Run(write); }
        catch (Exception ex)
        {
            StatusMessage = $"The {format} report could not be written. The log has the details.";
            _services.Log.Warning(LogArea, $"The {format} report could not be written ({ex.GetType().Name}).");
            return;
        }
        LastReportFolder = Path.GetDirectoryName(path);
        StatusMessage = $"Saved the {format} report to {path}.";
        _services.Log.Info(LogArea, $"Saved a {format} report.");
    }

    private bool CanOpenReportsFolder() => LastReportFolder is not null;

    // An Explorer window opened by each export covers the page, and the next click on the page lands in that window.
    [RelayCommand(CanExecute = nameof(CanOpenReportsFolder))]
    private void OpenReportsFolder()
    {
        if (LastReportFolder is { } folder) _services.Prompts.OpenFolder(folder);
    }

    // ---- Page choices ----

    private void SaveChoicesLater()
    {
        if (_applyingPreferences || !_preferencesLoaded) return;
        _ = SavePreferencesAsync();
    }

    private async Task ApplyPreferencesAsync()
    {
        InstallerAnalysisSettings? saved;
        try { saved = await Task.Run(_services.LoadPreferences); }
        catch (Exception ex)
        {
            saved = null;
            _services.Log.Warning(LogArea, $"The page choices could not be read ({ex.GetType().Name}).");
        }
        if (saved is null)
        {
            _storedChoices = CurrentChoices();
            return;
        }
        _applyingPreferences = true;
        try
        {
            SelectedTabIndex = saved.SelectedTab is >= 0 and < TabCount ? saved.SelectedTab : OverviewTab;
            MsiFilter = saved.MsiFilter ?? "";
            PayloadFilter = saved.PayloadFilter ?? "";
            FileFilter = saved.FileFilter ?? "";
            RegistryFilter = saved.RegistryFilter ?? "";
            StringsFilter = saved.StringsFilter ?? "";
            StringsCategory = StringCategories.FirstOrDefault(c => string.Equals(c, saved.StringsCategory, StringComparison.OrdinalIgnoreCase)) ?? AllCategories;
            RecentFiles.Clear();
            foreach (var path in (saved.RecentFiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentLimit))
                RecentFiles.Add(path);
        }
        finally { _applyingPreferences = false; }
        _storedChoices = CurrentChoices();
    }

    private InstallerAnalysisSettings CurrentChoices() => new()
    {
        SelectedTab = SelectedTabIndex,
        MsiFilter = MsiFilter,
        PayloadFilter = PayloadFilter,
        FileFilter = FileFilter,
        RegistryFilter = RegistryFilter,
        StringsFilter = StringsFilter,
        StringsCategory = StringsCategory == AllCategories ? "" : StringsCategory,
        RecentFiles = [.. RecentFiles]
    };

    private static bool SameChoices(InstallerAnalysisSettings? a, InstallerAnalysisSettings b) =>
        a is not null && a.SelectedTab == b.SelectedTab && a.MsiFilter == b.MsiFilter && a.PayloadFilter == b.PayloadFilter
        && a.FileFilter == b.FileFilter && a.RegistryFilter == b.RegistryFilter && a.StringsFilter == b.StringsFilter
        && a.StringsCategory == b.StringsCategory && (a.RecentFiles ?? []).SequenceEqual(b.RecentFiles ?? []);

    /// <summary>
    /// Writes the choices on a pool thread when they differ from the stored ones. Saves run one at a
    /// time and each reads the choices when its turn comes, so the last write holds the newest choices.
    /// </summary>
    private async Task SavePreferencesAsync()
    {
        // A save before the stored choices are known would replace them with the defaults.
        await _preferencesLoad;
        if (!_preferencesLoaded) return;
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
}
