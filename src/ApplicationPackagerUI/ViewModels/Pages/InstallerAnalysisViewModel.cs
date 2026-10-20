using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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
    public const string ShortcutMessage = "A shortcut cannot be analyzed. Drop or open the installer file itself.";
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

    public const string SevenZipNotFoundReason = "7-Zip was not found";
    public const string PayloadExtractFailedMessage = "The payload could not be extracted. The log has the details.";

    /// <summary>The status line for an export whose data did not load; nothing is written.</summary>
    public static string NotWrittenMessage(string format, string reason) => $"The {format} report was not written. {reason}".TrimEnd();

    /// <summary>
    /// The text the status line shows for a failure: the fixed message of an <see cref="InstallerAnalysisException"/>,
    /// else <paramref name="fallback"/>. The text of any other exception can hold paths and installer content.
    /// </summary>
    public static string PlainMessage(Exception? failure, string fallback) =>
        failure is InstallerAnalysisException { Message.Length: > 0 } plain ? plain.Message : fallback;

    /// <summary>The text with every control and line-separator character replaced, so one log entry stays one line.</summary>
    public static string LogText(string? text) =>
        string.Create((text ?? "").Length, text ?? "", static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                var category = char.GetUnicodeCategory(c);
                span[i] = char.IsControl(c) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? '?' : c;
            }
        });

    private readonly InstallerAnalysisPageServices _services;
    private readonly List<AnalysisFrame> _frames = [];
    private readonly List<Operation> _running = [];
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private int _generation;
    private bool _preferencesLoaded;
    private bool _applyingPreferences;
    private bool _tabChosen;
    private Task _preferencesLoad = Task.CompletedTask;
    private InstallerAnalysisSettings? _storedChoices;

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

        /// <summary>The sandbox session that holds what this level extracted; released when the level is left.</summary>
        public string? Session { get; set; }

        public List<FactRow>? MsiRows { get; set; }
        public IReadOnlyList<PayloadEntry>? Payload { get; set; } = report.Payload;
        public List<PayloadRow>? PayloadRows { get; set; }

        /// <summary>Set when the payload could not be listed for a reason the HTML report states in place of the payload.</summary>
        public string? PayloadLeftOutReason { get; set; }
        public List<PayloadRow> InnerRows { get; set; } = [];
        public List<PayloadRow> FileRows { get; set; } = [];
        public IReadOnlyList<string> Folders { get; set; } = [AllFolders];
        public string PayloadMessage { get; set; } = "";
        public Task? PayloadLoad { get; set; }
        public IReadOnlyList<RegistryWrite>? Registry { get; set; }
        public List<RegistryRow> RegistryRows { get; set; } = [];
        public IReadOnlyList<string> RegistryNotes { get; set; } = [];
        public string RegistryMessage { get; set; } = "";
        public Task? RegistryLoad { get; set; }
        public IReadOnlyList<InterestingString>? Strings { get; set; }
        public List<StringRow> StringRows { get; set; } = [];
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
    [NotifyCanExecuteChangedFor(nameof(CopySummaryCommand), nameof(CopyJsonCommand), nameof(ExportCsvCommand), nameof(ExportHtmlCommand))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRegistryNotes))]
    private IReadOnlyList<string> _registryNotes = [];

    [ObservableProperty] private IReadOnlyList<StringRow> _stringRows = [];
    [ObservableProperty] private StringRow? _selectedString;
    [ObservableProperty] private string _stringsFilter = "";
    [ObservableProperty] private string _stringsCategory = AllCategories;
    [ObservableProperty] private string _stringsStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHash))]
    [NotifyCanExecuteChangedFor(nameof(CopyHashCommand))]
    private string _hashResult = "";

    [ObservableProperty] private string _hashLabel = "";

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
    public bool HasRegistryNotes => RegistryNotes.Count > 0;
    public bool HasBreadcrumb => Breadcrumb.Count > 1;
    public bool HasHash => HashResult.Length > 0;

    /// <summary>The number of drill-down levels below the installer the user opened.</summary>
    public int Depth => Math.Max(0, _frames.Count - 1);

    private AnalysisFrame? Current => _frames.Count == 0 ? null : _frames[^1];

    /// <summary>True while the frame is shown and no later request replaced the one that started the work.</summary>
    private bool IsCurrent(AnalysisFrame frame, int generation) => generation == _generation && ReferenceEquals(frame, Current);

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

    /// <summary>Cancels the work, waits for it to stop, releases every sandbox session and every result.</summary>
    public async Task OnNavigatedFromAsync()
    {
        var stopping = CancelAll();
        _generation++;
        var sessions = TakeSessions(_frames);
        _frames.Clear();
        ClearView();
        StatusMessage = "";
        PathText = "";
        LastExtractFolder = null;
        LastReportFolder = null;
        await WaitForAsync(stopping);
        foreach (var session in sessions) await ReleaseAsync(session);
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

    private static List<string> TakeSessions(IEnumerable<AnalysisFrame> frames)
    {
        var sessions = new List<string>();
        foreach (var frame in frames)
        {
            if (frame.Session is { } session) sessions.Add(session);
            frame.Session = null;
            frame.Extracted.Clear();
        }
        return sessions;
    }

    /// <summary>Releases the sessions of frames that are no longer shown once the cancelled work that may still write into them stops.</summary>
    private void ReleaseLater(IEnumerable<AnalysisFrame> dropped, IReadOnlyList<Task> stopping)
    {
        var sessions = TakeSessions(dropped);
        if (sessions.Count > 0) _ = ReleaseAfterAsync(stopping, sessions);
    }

    private async Task ReleaseAfterAsync(IReadOnlyList<Task> stopping, List<string> sessions)
    {
        await WaitForAsync(stopping);
        foreach (var session in sessions) await ReleaseAsync(session);
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

    private enum PathKind { File, Folder, Missing, Cancelled }

    /// <summary>Checks the path on a pool thread: a share that does not answer blocks there, not in the window, and Cancel ends the wait.</summary>
    private async Task<PathKind> CheckPathAsync(string path)
    {
        var operation = Begin("Checking the path.");
        try
        {
            return await Task.Run(() => Directory.Exists(path) ? PathKind.Folder : File.Exists(path) ? PathKind.File : PathKind.Missing)
                .WaitAsync(operation.Cancel.Token);
        }
        catch (OperationCanceledException) { return PathKind.Cancelled; }
        finally { End(operation); }
    }

    /// <summary>
    /// Checks the path and analyzes the file; the drill-down stack starts again from it. Every attempt,
    /// a refused one too, cancels the work of the previous request so its late result is discarded.
    /// </summary>
    public async Task OpenAsync(string? input)
    {
        var stopping = CancelAll();
        var generation = ++_generation;
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
        if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = ShortcutMessage;
            return;
        }
        var kind = await CheckPathAsync(path);
        if (generation != _generation) return;
        switch (kind)
        {
            case PathKind.Cancelled:
                StatusMessage = CancelledMessage;
                return;
            case PathKind.Folder:
                StatusMessage = FolderMessage;
                return;
            case PathKind.Missing:
                StatusMessage = MissingFileMessage(path);
                return;
        }

        PathText = path;
        ReleaseLater(_frames.ToList(), stopping);
        _frames.Clear();
        ClearView();
        AddRecent(path);

        var name = Path.GetFileName(path);
        var report = await AnalyzeCoreAsync(path, name, generation, depth: 0);
        if (report is null) return;
        _frames.Add(new AnalysisFrame(path, name, report));
        ShowFrame(_frames[^1], restoreTab: false);
        StatusMessage = $"Analyzed {name}.";
    }

    /// <summary>Runs one analysis off the UI thread; null when it failed, was cancelled or was superseded.</summary>
    private async Task<AnalysisReport?> AnalyzeCoreAsync(string path, string name, int generation, int depth)
    {
        // Below the top level the name comes from inside the installer; the log names only the depth.
        var subject = depth == 0 ? $"file={LogText(name)}" : $"depth={depth}";
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
            report = await Task.Run(() => _services.Analysis.AnalyzeAsync(path: path, limits: _services.Limits, progress: progress, ct: operation.Cancel.Token), operation.Cancel.Token);
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
            StatusMessage = PlainMessage(failure, FailedMessage);
            _services.Log.Warning(LogArea, $"Analysis failed: {subject} error={failure?.GetType().Name}.");
            return null;
        }
        _services.Log.Info(LogArea,
            $"Analyzed: type={LogText(report.Type)} architecture={LogText(report.Architecture)} modes={report.Modes.Count} msiProperties={report.MsiProperties.Count} notes={report.Notes.Count} {subject}.");
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
        RegistryNotes = [];
        StringRows = [];
        SelectedString = null;
        StringsStatus = "";
        ClearHash();
        OnPropertyChanged(nameof(Depth));
    }

    private void ClearHash()
    {
        HashResult = "";
        HashLabel = "";
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
                new("Base version", patch.BaseVersion.Length > 0 ? patch.BaseVersion : "Not read"),
                new("Patch file", patch.PatchFile.Length > 0 ? patch.PatchFile : "Not named"),
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
            new("Size", $"{InstallerAnalysisText.Size(report.Size)} ({report.Size.ToString("N0", CultureInfo.InvariantCulture)} bytes)"),
            new("SHA-256", report.Sha256),
            new("Type", report.TypeName.Length > 0 ? report.TypeName : report.Type),
            new("Architecture", report.Architecture)
        };
        if (report.RequestedExecutionLevel.Length > 0) rows.Add(new("Requested execution level", report.RequestedExecutionLevel));
        if (report.DetectionHint.Length > 0) rows.Add(new("Detection hint", report.DetectionHint));
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
        if (!_applyingPreferences) _tabChosen = true;
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
    // Each filter rebuilds only its own list. The rows of a frame are built once, so a filtered list
    // holds the same row objects and a grid keeps its selection on them.

    partial void OnMsiFilterChanged(string value) => ApplyMsiFilter();
    partial void OnPayloadFilterChanged(string value) => ApplyPayloadFilter();
    partial void OnFileFilterChanged(string value) => ApplyFileFilter();
    partial void OnRegistryFilterChanged(string value) => ShowRegistry();
    partial void OnStringsFilterChanged(string value) => ShowStrings();

    partial void OnSelectedFolderChanged(string value)
    {
        // A combo box whose item list is replaced writes null back through its binding.
        if (value is null)
        {
            SelectedFolder = AllFolders;
            return;
        }
        ApplyFileFilter();
    }

    partial void OnStringsCategoryChanged(string value)
    {
        ShowStrings();
        SaveChoicesLater();
    }

    private static bool Has(string? text, string filter) => (text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string Counted(int shown, int all, string singular, string? plural = null) =>
        shown == all ? InstallerAnalysisText.Count(all, singular, plural) : $"{shown} of {InstallerAnalysisText.Count(all, singular, plural)}";

    private void ApplyMsiFilter()
    {
        if (Current is not { } frame)
        {
            MsiRows = [];
            MsiCount = "";
            return;
        }
        var all = frame.MsiRows ??= frame.Report.MsiProperties.Select(FactRow.From).ToList();
        var filter = MsiFilter.Trim();
        MsiRows = filter.Length == 0 ? all : all.Where(p => Has(p.Name, filter) || Has(p.Value, filter)).ToList();
        MsiCount = Counted(MsiRows.Count, all.Count, "property", "properties");
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
        if (frame.PayloadRows is null)
        {
            var rows = payload.Select(e => new PayloadRow(e)).ToList();
            frame.PayloadRows = rows;
            frame.InnerRows = rows.Where(r => r.IsInnerInstaller).ToList();
            frame.FileRows = rows.Where(r => !r.Entry.IsDirectory).ToList();
            frame.Folders = [AllFolders, .. frame.FileRows.Select(r => r.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f == InstallerAnalysisText.TopLevel ? "" : f, StringComparer.OrdinalIgnoreCase)];
        }
        ApplyPayloadFilter();
        InnerRows = Keep(frame.InnerRows, SelectedInner, s => SelectedInner = s);
        InnerStatus = InnerRows.Count == 0 ? "The payload holds no inner installer." : InstallerAnalysisText.Count(InnerRows.Count, "inner installer");
        if (!FileFolders.SequenceEqual(frame.Folders, StringComparer.Ordinal)) FileFolders = frame.Folders;
        if (!FileFolders.Contains(SelectedFolder)) SelectedFolder = AllFolders;
        ApplyFileFilter();
    }

    private void ApplyPayloadFilter()
    {
        if (Current?.PayloadRows is not { } rows) return;
        var filter = PayloadFilter.Trim();
        PayloadRows = Keep(filter.Length == 0 ? rows : rows.Where(r => Has(r.Path, filter) || Has(r.Type, filter)).ToList(), SelectedPayload, s => SelectedPayload = s);
        PayloadStatus = Counted(PayloadRows.Count, rows.Count, "entry", "entries");
    }

    private void ApplyFileFilter()
    {
        if (Current is not { PayloadRows: not null } frame) return;
        var files = frame.FileRows;
        var filter = FileFilter.Trim();
        var folder = SelectedFolder ?? AllFolders;
        var shown = filter.Length == 0 && folder == AllFolders
            ? files
            : files.Where(r => (folder == AllFolders || string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)) && (filter.Length == 0 || Has(r.Name, filter))).ToList();
        FileRows = Keep(shown, SelectedFile, s => SelectedFile = s);
        FileStatus = Counted(FileRows.Count, files.Count, "file");
    }

    private void ShowRegistry()
    {
        var frame = Current;
        RegistryNotes = frame?.RegistryNotes ?? [];
        if (frame?.Registry is not { } writes)
        {
            RegistryRows = [];
            RegistryStatus = frame is null ? "" : frame.RegistryLoad is not null ? "Reading the registry settings." : frame.RegistryMessage.Length > 0 ? frame.RegistryMessage : LoadOnShowMessage;
            return;
        }
        var filter = RegistryFilter.Trim();
        var rows = frame.RegistryRows;
        RegistryRows = Keep(filter.Length == 0 ? rows : rows.Where(r => Has(r.FullKey, filter) || Has(r.Name, filter) || Has(r.Value, filter) || Has(r.Origin, filter)
            || Has(r.Condition, filter) || Has(r.Action, filter) || Has(r.Section, filter)).ToList(), SelectedRegistry, s => SelectedRegistry = s);
        RegistryStatus = writes.Count == 0 ? "The installer records no registry writes that can be read." : Counted(RegistryRows.Count, rows.Count, "registry write");
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
        var rows = frame.StringRows;
        StringRows = Keep(category.Length == 0 && filter.Length == 0 ? rows
            : rows.Where(s => (category.Length == 0 || string.Equals(s.Category, category, StringComparison.OrdinalIgnoreCase)) && (filter.Length == 0 || Has(s.Text, filter))).ToList(),
            SelectedString, s => SelectedString = s);
        StringsStatus = Counted(StringRows.Count, strings.Count, "string");
        if (frame.StringsMessage.Length > 0) StringsStatus = $"{StringsStatus} {frame.StringsMessage}";
    }

    /// <summary>Clears the selection when the selected row is no longer listed.</summary>
    private static IReadOnlyList<T> Keep<T>(IReadOnlyList<T> rows, T? selected, Action<T?> select) where T : class
    {
        if (selected is not null && !rows.Any(r => ReferenceEquals(r, selected))) select(null);
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
            var entries = await Task.Run(() => _services.Analysis.ListPayloadAsync(path: frame.Path, limits: _services.Limits, ct: operation.Cancel.Token), operation.Cancel.Token);
            frame.Payload = entries;
            frame.PayloadRows = null;
            frame.PayloadMessage = "";
            frame.PayloadLeftOutReason = null;
            _services.Log.Info(LogArea, $"Listed the payload: entries={entries.Count} innerInstallers={entries.Count(e => string.Equals(e.Kind, "InnerInstaller", StringComparison.OrdinalIgnoreCase))}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException || operation.Cancel.IsCancellationRequested)
        {
            frame.PayloadMessage = "Listing the payload was cancelled. Open the tab again to list it.";
            frame.PayloadLeftOutReason = null;
        }
        catch (Exception ex)
        {
            frame.PayloadMessage = PlainMessage(ex, "The payload could not be listed. The log has the details.");
            frame.PayloadLeftOutReason = ex is SevenZipNotFoundException ? SevenZipNotFoundReason : null;
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
            var result = await Task.Run(() => _services.Analysis.ReadRegistryAsync(path: frame.Path, limits: _services.Limits, ct: operation.Cancel.Token), operation.Cancel.Token);
            frame.RegistryRows = result.Writes.Select(w => new RegistryRow(w)).ToList();
            frame.Registry = result.Writes;
            frame.RegistryNotes = result.Notes;
            frame.RegistryMessage = "";
            _services.Log.Info(LogArea, $"Read the registry settings: writes={result.Writes.Count} notes={result.Notes.Count}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException || operation.Cancel.IsCancellationRequested)
        {
            frame.RegistryMessage = "Reading the registry settings was cancelled. Open the tab again to read them.";
        }
        catch (Exception ex)
        {
            frame.RegistryMessage = PlainMessage(ex, "The registry settings could not be read. The log has the details.");
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
            var strings = await Task.Run(() => _services.Analysis.ReadStringsAsync(path: frame.Path, limits: _services.Limits, ct: operation.Cancel.Token), operation.Cancel.Token);
            frame.StringRows = strings.Select(s => new StringRow(s)).ToList();
            frame.Strings = strings;
            frame.StringsMessage = strings.Count >= _services.Limits.MaxStrings
                ? $"The list stops at {_services.Limits.MaxStrings.ToString("N0", CultureInfo.InvariantCulture)} strings."
                : "";
            _services.Log.Info(LogArea, $"Read the strings: count={strings.Count}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException || operation.Cancel.IsCancellationRequested)
        {
            frame.StringsMessage = "Reading the strings was cancelled. Open the tab again to read them.";
        }
        catch (Exception ex)
        {
            frame.StringsMessage = PlainMessage(ex, "The strings could not be read. The log has the details.");
            _services.Log.Warning(LogArea, $"The strings could not be read ({ex.GetType().Name}).");
        }
        finally
        {
            End(operation);
            frame.StringsLoad = null;
        }
        if (ReferenceEquals(frame, Current)) ShowStrings();
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

    /// <summary>
    /// The frame's sandbox session, created on first use with this process as its owner. A session
    /// created after the frame was left, or after its operation was cancelled, is released at once.
    /// </summary>
    private async Task<string?> SessionAsync(AnalysisFrame frame, Operation operation)
    {
        if (frame.Session is { } existing) return existing;
        string created;
        try
        {
            created = await Task.Run(() =>
            {
                var session = _services.Sandbox.NewSession();
                try { InstallerAnalysisSessions.MarkOwner(session); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _services.Log.Warning(LogArea, $"The analysis session owner could not be written ({ex.GetType().Name}).");
                }
                return session;
            });
        }
        catch (Exception ex)
        {
            if (!operation.Cancel.IsCancellationRequested && _frames.Contains(frame))
                StatusMessage = "The temporary analysis folder could not be created. The log has the details.";
            _services.Log.Warning(LogArea, $"The analysis session could not be created ({ex.GetType().Name}).");
            return null;
        }
        if (operation.Cancel.IsCancellationRequested || !_frames.Contains(frame))
        {
            _ = ReleaseAsync(created);
            return null;
        }
        if (frame.Session is { } raced)
        {
            _ = ReleaseAsync(created);
            return raced;
        }
        frame.Session = created;
        return created;
    }

    /// <summary>The entry's copy inside the frame's sandbox session, extracted once per frame; null when it failed or was cancelled.</summary>
    private async Task<string?> ExtractToSessionAsync(AnalysisFrame frame, PayloadEntry entry, Func<bool> stillWanted)
    {
        if (frame.Extracted.TryGetValue(entry.Path, out var known) && File.Exists(known)) return known;
        var operation = Begin($"Extracting {InstallerAnalysisText.EntryName(entry.Path)}.");
        string? extracted = null;
        Exception? failure = null;
        var noSession = false;
        try
        {
            var session = await SessionAsync(frame, operation);
            if (session is null) noSession = true;
            else
            {
                extracted = await Task.Run(() => _services.Analysis.ExtractEntryAsync(path: frame.Path, entry: entry, sessionFolder: session, limits: _services.Limits, ct: operation.Cancel.Token),
                    operation.Cancel.Token);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        var cancelled = operation.Cancel.IsCancellationRequested || failure is OperationCanceledException;
        End(operation);
        if (!stillWanted() || !_frames.Contains(frame)) return null;
        if (cancelled)
        {
            StatusMessage = "The extraction was cancelled.";
            return null;
        }
        if (noSession) return null;
        if (extracted is null)
        {
            StatusMessage = PlainMessage(failure, ExtractFailedMessage);
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
        var report = await AnalyzeCoreAsync(extracted, row.Name, generation, depth: _frames.Count);
        if (report is null || !IsCurrent(frame, generation)) return;
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

    /// <summary>Shows a parent level; the levels below it are dropped and what they extracted is removed.</summary>
    private void GoTo(int level)
    {
        var stopping = CancelAll();
        _generation++;
        var dropped = _frames.GetRange(level + 1, _frames.Count - level - 1);
        _frames.RemoveRange(level + 1, _frames.Count - level - 1);
        ReleaseLater(dropped, stopping);
        ClearHash();
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
        try { hash = await Task.Run(() => _services.Analysis.ComputeSha256Async(path: extracted, ct: operation.Cancel.Token), operation.Cancel.Token); }
        catch (Exception ex) { failure = ex; }
        var cancelled = operation.Cancel.IsCancellationRequested || failure is OperationCanceledException;
        End(operation);
        if (!ReferenceEquals(frame, Current)) return;
        if (cancelled) StatusMessage = "The SHA-256 computation was cancelled.";
        else if (hash is null)
        {
            StatusMessage = PlainMessage(failure, "The SHA-256 could not be computed. The log has the details.");
            _services.Log.Warning(LogArea, $"The SHA-256 could not be computed ({failure?.GetType().Name}).");
        }
        else
        {
            HashLabel = $"SHA-256 of {row.Name}";
            HashResult = hash;
            StatusMessage = $"SHA-256 of {row.Name}: {hash}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasHash))]
    private void CopyHash()
    {
        if (!HasHash) return;
        Copy(HashResult, "Copied the SHA-256.");
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
        var (result, failure, cancelled, partial) = await ExtractToFolderAsync(frame, [row.Entry], folder, $"Copying {row.Name}.");
        if (!_frames.Contains(frame)) return;
        if (cancelled) StatusMessage = "The copy was cancelled." + PartialText(partial);
        else if (result is null)
        {
            StatusMessage = PlainMessage(failure, ExtractFailedMessage) + PartialText(partial);
            _services.Log.Warning(LogArea, $"An inner installer could not be copied ({failure?.GetType().Name}).");
        }
        else if (result.Files == 0)
        {
            StatusMessage = $"{row.Name} was not copied.{SkippedText(result.Skipped)}";
            _services.Log.Info(LogArea, $"An inner installer was not copied: skipped={result.Skipped.Count}.");
        }
        else
        {
            LastExtractFolder = result.Folder;
            StatusMessage = $"Copied {row.Name} to {result.Folder}.{SkippedText(result.Skipped)}";
            _services.Log.Info(LogArea, "Copied an inner installer to a chosen folder.");
        }
    }

    /// <summary>Extracts every file of the payload to a new folder inside the folder the user chooses; nothing is started.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ExtractPayload()
    {
        if (Current is not { } frame)
        {
            StatusMessage = NoAnalysisMessage;
            return;
        }
        if (PickTarget("Extract the payload to a folder") is not { } folder) return;
        var (result, failure, cancelled, partial) = await ExtractToFolderAsync(frame, null, folder, $"Extracting the payload of {frame.Name}.");
        if (!_frames.Contains(frame)) return;
        if (cancelled) StatusMessage = "The extraction was cancelled." + PartialText(partial);
        else if (result is null)
        {
            StatusMessage = PlainMessage(failure, PayloadExtractFailedMessage) + PartialText(partial);
            _services.Log.Warning(LogArea, $"The payload could not be extracted ({failure?.GetType().Name}).");
        }
        else if (result.Files == 0)
        {
            StatusMessage = NoPayloadMessage + SkippedText(result.Skipped);
            _services.Log.Info(LogArea, $"The payload extraction wrote no file: skipped={result.Skipped.Count}.");
        }
        else
        {
            LastExtractFolder = result.Folder;
            StatusMessage = $"Extracted {InstallerAnalysisText.Count(result.Files, "file")} ({InstallerAnalysisText.Size(result.Bytes)}) to {result.Folder}.{SkippedText(result.Skipped)}";
            _services.Log.Info(LogArea, $"Extracted the payload: files={result.Files} skipped={result.Skipped.Count}.");
        }
    }

    private static string PartialText(string? partial) =>
        partial is null ? "" : $" The folder {partial} holds the files extracted before the stop.";

    /// <summary>The name of the folder <see cref="IInstallerAnalysisService.ExtractPayloadAsync"/> creates: '&lt;file name&gt; payload', then ' (2)' and up.</summary>
    private static string PayloadFolderBase(string installer)
    {
        var baseName = Path.GetFileNameWithoutExtension(installer).TrimEnd('.', ' ');
        return SandboxPath.Normalize(baseName, out _) is null ? "payload" : baseName;
    }

    private static HashSet<string> PayloadFolders(string target, string baseName)
    {
        var pattern = new Regex("^" + Regex.Escape(baseName + " payload") + @"( \(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        try
        {
            return Directory.EnumerateDirectories(target).Where(d => pattern.IsMatch(Path.GetFileName(d))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Runs <see cref="IInstallerAnalysisService.ExtractPayloadAsync"/>: the service writes into a new
    /// '&lt;file name&gt; payload' folder of <paramref name="folder"/> and refuses sandbox and linked folders.
    /// After a cancel or a failure, Partial names the payload folder that appeared during this run, when exactly one did.
    /// </summary>
    private async Task<(PayloadExtractResult? Result, Exception? Failure, bool Cancelled, string? Partial)> ExtractToFolderAsync(AnalysisFrame frame,
        IReadOnlyList<PayloadEntry>? entries, string folder, string text)
    {
        var operation = Begin(text);
        var baseName = PayloadFolderBase(frame.Path);
        PayloadExtractResult? result = null;
        Exception? failure = null;
        HashSet<string> before = [];
        try
        {
            before = await Task.Run(() => PayloadFolders(folder, baseName));
            result = await Task.Run(() => _services.Analysis.ExtractPayloadAsync(path: frame.Path, entries: entries, targetFolder: folder, limits: _services.Limits,
                ct: operation.Cancel.Token), operation.Cancel.Token);
        }
        catch (Exception ex) { failure = ex; }
        var cancelled = operation.Cancel.IsCancellationRequested || failure is OperationCanceledException;
        string? partial = null;
        if (cancelled || result is null)
        {
            var created = (await Task.Run(() => PayloadFolders(folder, baseName))).Except(before, StringComparer.OrdinalIgnoreCase).ToList();
            if (created.Count == 1) partial = created[0];
        }
        End(operation);
        return (cancelled ? null : result, failure, cancelled, partial);
    }

    /// <summary>The skipped entries as one plain sentence after the result; the service words each reason.</summary>
    private static string SkippedText(IReadOnlyList<string> skipped) => skipped.Count switch
    {
        0 => "",
        1 => $" 1 entry was skipped: {skipped[0]}",
        _ => $" {skipped.Count.ToString("N0", CultureInfo.InvariantCulture)} entries were skipped. The first: {skipped[0]}"
    };

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
        var lines = RegistryRows.Select(r => string.Join('\t', r.Origin, r.Action, r.FullKey, r.Name, r.Value, r.Type, r.View, r.Section, r.Condition));
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

    private bool CanExport() => HasReport;

    [RelayCommand(CanExecute = nameof(CanExport))]
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
    [RelayCommand(CanExecute = nameof(CanExport))]
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

    [RelayCommand(CanExecute = nameof(CanExport), AllowConcurrentExecutions = true)]
    private async Task ExportCsv()
    {
        if (Current is not { } frame) { StatusMessage = NoAnalysisMessage; return; }
        var generation = _generation;
        await EnsureRegistryAsync();
        if (!IsCurrent(frame, generation)) return;
        if (frame.Registry is not { } registry)
        {
            StatusMessage = NotWrittenMessage("CSV", frame.RegistryMessage.Length > 0 ? frame.RegistryMessage : "The registry settings were not read.");
            return;
        }
        await ExportAsync(frame, generation, "CSV", () => _services.Reports.WriteCsv(frame.Report, registry));
    }

    [RelayCommand(CanExecute = nameof(CanExport), AllowConcurrentExecutions = true)]
    private async Task ExportHtml()
    {
        if (Current is not { } frame) { StatusMessage = NoAnalysisMessage; return; }
        var generation = _generation;
        await EnsurePayloadAsync();
        if (!IsCurrent(frame, generation)) return;
        var payload = frame.Payload;
        var leftOut = payload is null ? frame.PayloadLeftOutReason : null;
        if (payload is null && leftOut is null)
        {
            StatusMessage = NotWrittenMessage("HTML", frame.PayloadMessage.Length > 0 ? frame.PayloadMessage : "The payload was not listed.");
            return;
        }
        await EnsureRegistryAsync();
        if (!IsCurrent(frame, generation)) return;
        if (frame.Registry is not { } registry)
        {
            StatusMessage = NotWrittenMessage("HTML", frame.RegistryMessage.Length > 0 ? frame.RegistryMessage : "The registry settings were not read.");
            return;
        }
        await ExportAsync(frame, generation, "HTML", () => _services.Reports.WriteHtml(frame.Report, payload, registry, leftOut ?? ""),
            leftOut is null ? "" : $" The payload was left out: {leftOut}.");
    }

    /// <summary>Writes a report as tracked work; the result shows only while the same analysis is shown.</summary>
    private async Task ExportAsync(AnalysisFrame frame, int generation, string format, Func<string> write, string savedNote = "")
    {
        var operation = Begin($"Writing the {format} report.");
        string? path = null;
        Exception? failure = null;
        try { path = await Task.Run(write); }
        catch (Exception ex) { failure = ex; }
        End(operation);
        if (!IsCurrent(frame, generation)) return;
        if (path is null)
        {
            StatusMessage = PlainMessage(failure, $"The {format} report could not be written. The log has the details.");
            _services.Log.Warning(LogArea, $"The {format} report could not be written ({failure?.GetType().Name}).");
            return;
        }
        LastReportFolder = Path.GetDirectoryName(path);
        StatusMessage = $"Saved the {format} report to {path}.{savedNote}";
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

    /// <summary>
    /// Applies the saved choices. A tab, filter or file the user chose while the read ran is kept:
    /// the saved value applies only where the page still holds its default.
    /// </summary>
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
            if (!_tabChosen) SelectedTabIndex = saved.SelectedTab is >= 0 and < TabCount ? saved.SelectedTab : OverviewTab;
            if (MsiFilter.Length == 0) MsiFilter = saved.MsiFilter ?? "";
            if (PayloadFilter.Length == 0) PayloadFilter = saved.PayloadFilter ?? "";
            if (FileFilter.Length == 0) FileFilter = saved.FileFilter ?? "";
            if (RegistryFilter.Length == 0) RegistryFilter = saved.RegistryFilter ?? "";
            if (StringsFilter.Length == 0) StringsFilter = saved.StringsFilter ?? "";
            if (StringsCategory == AllCategories)
                StringsCategory = StringCategories.FirstOrDefault(c => string.Equals(c, saved.StringsCategory, StringComparison.OrdinalIgnoreCase)) ?? AllCategories;
            var merged = RecentFiles.Concat((saved.RecentFiles ?? []).Where(p => !string.IsNullOrWhiteSpace(p)))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentLimit).ToList();
            RecentFiles.Clear();
            foreach (var path in merged) RecentFiles.Add(path);
        }
        finally { _applyingPreferences = false; }
        _storedChoices = saved;
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
