using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

internal sealed class FakeDetectionTestService : IDetectionTestService
{
    public List<DetectionTestRequest> Requests { get; } = [];
    public Func<DetectionTestRequest, DetectionTestResult>? Answer { get; set; }
    public TaskCompletionSource<DetectionTestResult>? Pending { get; set; }
    public Exception? Failure { get; set; }

    /// <summary>When true, a pending run ignores the token and ends only when the test completes <see cref="Pending"/>.</summary>
    public bool IgnoreCancel { get; set; }

    public async Task<DetectionTestResult> RunAsync(DetectionTestRequest request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        if (Failure is not null) throw Failure;
        if (Pending is not null)
        {
            if (IgnoreCancel) return await Pending.Task;
            await using var registration = ct.Register(() => Pending.TrySetException(new OperationCanceledException(ct)));
            return await Pending.Task;
        }
        return Answer?.Invoke(request) ?? Result(detected: !request.Negative, passed: true);
    }

    public static DetectionTestResult Result(bool detected, bool passed, string summary = "Detected.", string? stdout = null,
        string? stderr = null, int? exitCode = null, string context = "Windows PowerShell 5.1, current user, not SYSTEM") =>
        new(detected, passed, summary, [new DetectionClauseResult("clause one", detected, "detail one")], stdout, stderr, exitCode, context);
}

internal sealed class FakeDetectionHistoryStore : IDetectionHistoryStore
{
    public List<DetectionHistoryEntry> Entries { get; } = [];
    public int ReadCount { get; private set; }
    public int ClearCount { get; private set; }
    public Exception? ReadFailure { get; set; }
    public Exception? AddFailure { get; set; }
    public Exception? ClearFailure { get; set; }

    public IReadOnlyList<DetectionHistoryEntry> All()
    {
        ReadCount++;
        if (ReadFailure is not null) throw ReadFailure;
        return Entries.ToList();
    }

    public void Add(DetectionHistoryEntry entry)
    {
        if (AddFailure is not null) throw AddFailure;
        Entries.Add(entry);
    }

    public void Clear()
    {
        ClearCount++;
        if (ClearFailure is not null) throw ClearFailure;
        Entries.Clear();
    }
}

internal sealed class FakeDetectionReportWriter : IDetectionReportWriter
{
    public List<(string Format, IReadOnlyList<DetectionHistoryEntry> Entries)> Calls { get; } = [];
    public Exception? Failure { get; set; }

    public string WriteCsv(IReadOnlyList<DetectionHistoryEntry> entries) => Write("csv", entries);

    public string WriteHtml(IReadOnlyList<DetectionHistoryEntry> entries) => Write("html", entries);

    private string Write(string format, IReadOnlyList<DetectionHistoryEntry> entries)
    {
        Calls.Add((format, entries));
        if (Failure is not null) throw Failure;
        return $@"C:\Data\DetectionTester\Reports\detection-history-20261018-101500.{format}";
    }
}

internal sealed class FakeInstalledApplicationsService : IInstalledApplicationsService
{
    public List<InstalledApplication> Applications { get; } = [];
    public List<bool> Includes { get; } = [];
    public int Calls { get { lock (Includes) return Includes.Count; } }

    /// <summary>When set, Enumerate blocks until the test releases it.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public Exception? Failure { get; set; }

    public IReadOnlyList<InstalledApplication> Enumerate(bool includeSystemComponents = false)
    {
        lock (Includes) Includes.Add(includeSystemComponents);
        Gate?.Wait(TimeSpan.FromSeconds(10));
        if (Failure is not null) throw Failure;
        return Applications.ToList();
    }
}

internal sealed class FakeDetectionTesterPrompts : IDetectionTesterPrompts
{
    public string? ManifestPath { get; set; }
    public bool ConfirmAnswer { get; set; }
    public List<int> ConfirmRequests { get; } = [];
    public List<string> Copied { get; } = [];
    public List<string> OpenedFolders { get; } = [];

    public string? PickManifest() => ManifestPath;

    public Task<bool> ConfirmClearHistoryAsync(int count)
    {
        ConfirmRequests.Add(count);
        return Task.FromResult(ConfirmAnswer);
    }

    public bool CopyFails { get; set; }

    public bool CopyText(string text)
    {
        if (CopyFails) return false;
        Copied.Add(text);
        return true;
    }

    public void OpenFolder(string folder) => OpenedFolders.Add(folder);
}

internal sealed class RecordingDetectionLog : IAppLog
{
    public List<AppLogEntry> Entries { get; } = [];
    public string? SessionFile => null;
    public AppLogLevel MinimumLevel { get; set; }
    public event EventHandler<AppLogEntry>? Written;

    public void Write(AppLogLevel level, string area, string message)
    {
        var entry = new AppLogEntry(DateTimeOffset.Now, level, area, message);
        Entries.Add(entry);
        Written?.Invoke(this, entry);
    }

    public void RegisterSecret(string value) { }

    public IReadOnlyList<AppLogEntry> Tail() => Entries;
}

internal sealed class DetectionTesterHarness
{
    public FakeDetectionTestService Tests { get; } = new();
    public FakeDetectionHistoryStore History { get; } = new();
    public FakeDetectionReportWriter Reports { get; } = new();
    public FakeInstalledApplicationsService Applications { get; } = new();
    public FakeDetectionTesterPrompts Prompts { get; } = new();
    public RecordingDetectionLog Log { get; } = new();
    public DetectionTesterSettings? Saved { get; set; }
    public int PreferenceReads { get; private set; }
    public int? PreferenceThreadId { get; private set; }

    /// <summary>When set, the saved-choices read blocks until the test releases it.</summary>
    public ManualResetEventSlim? PreferenceGate { get; set; }
    public int SaveCount { get; private set; }
    public int? SaveThreadId { get; private set; }
    public Exception? SaveFailure { get; set; }
    public bool SaveResult { get; set; } = true;
    public int ManifestReads { get; private set; }
    public Func<string, StageManifest> ReadManifest { get; set; } = path => new ManifestService().Read(path);

    public DetectionTesterViewModel Create() => new(new DetectionTesterPageServices(
        Tests, History, Reports, Applications, Prompts,
        () =>
        {
            PreferenceReads++;
            PreferenceThreadId = Environment.CurrentManagedThreadId;
            PreferenceGate?.Wait(TimeSpan.FromSeconds(10));
            return Saved;
        },
        settings =>
        {
            SaveCount++;
            SaveThreadId = Environment.CurrentManagedThreadId;
            if (SaveFailure is not null) throw SaveFailure;
            Saved = settings;
            return SaveResult;
        },
        Log,
        path => { ManifestReads++; return ReadManifest(path); }));

    public async Task<DetectionTesterViewModel> OpenAsync()
    {
        var viewModel = Create();
        await viewModel.OnNavigatedToAsync();
        return viewModel;
    }

    public static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DetectionTester", "stage-manifest.json");

    public static DetectionHistoryEntry Entry(DateTimeOffset time, StageDetection detection, bool negative = false, bool passed = true, string description = "entry") =>
        new(Guid.NewGuid(), time, detection.Type, description, negative, passed, passed ? "Detected." : "Not detected.", detection);
}

public sealed class DetectionTesterViewModelTests
{
    private readonly DetectionTesterHarness _h = new();

    [Fact]
    public void TheConstructorReadsNothing()
    {
        var viewModel = _h.Create();
        Assert.Equal(0, _h.History.ReadCount);
        Assert.Equal(0, _h.Applications.Calls);
        Assert.Equal(0, _h.PreferenceReads);
        Assert.Empty(viewModel.History);
    }

    [Fact]
    public async Task OpeningThePageLoadsTheHistoryNewestFirstAndTheSavedChoices()
    {
        var older = DetectionTesterHarness.Entry(DateTimeOffset.Now.AddHours(-2), new StageDetection { Type = "RegistryKey", RegistryKeyRelative = @"SOFTWARE\A" }, description: "older");
        var newer = DetectionTesterHarness.Entry(DateTimeOffset.Now.AddHours(-1), new StageDetection { Type = "File", FilePath = @"C:\X", FileName = "x.exe" }, negative: true, passed: false, description: "newer");
        _h.History.Entries.AddRange([older, newer]);
        _h.Saved = new DetectionTesterSettings { ClauseType = "File", Hive = "HKCU", View = "32-bit" };

        var viewModel = await _h.OpenAsync();
        await viewModel.OnNavigatedToAsync();

        Assert.Equal(["newer", "older"], viewModel.History.Select(r => r.Description));
        Assert.Equal(1, _h.History.ReadCount);
        var top = viewModel.History[0];
        Assert.Equal("File", top.Kind);
        Assert.Equal("Yes", top.Negative);
        Assert.Equal("Failed", top.Result);
        Assert.Equal(newer.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), top.Time);
        Assert.Equal("File", viewModel.SelectedClauseType);
        Assert.True(viewModel.IsFile);
        Assert.Equal("HKCU", viewModel.SelectedHive);
        Assert.Equal("32-bit", viewModel.SelectedView);
    }

    [Fact]
    public async Task WithoutSavedChoicesThePageStartsOnARegistryValueInTheMachineHiveAndThe64BitView()
    {
        var viewModel = await _h.OpenAsync();
        Assert.Equal(["RegistryKeyValue", "RegistryKey", "File", "Script"], viewModel.ClauseTypes);
        Assert.Equal("RegistryKeyValue", viewModel.SelectedClauseType);
        Assert.True(viewModel.IsRegistryKeyValue);
        Assert.Equal("HKLM", viewModel.SelectedHive);
        Assert.Equal("64-bit", viewModel.SelectedView);
        Assert.Equal("IsEquals", viewModel.RegistryOperator);
        Assert.Equal(DetectionTesterResultState.None, viewModel.ResultState);
    }

    [Fact]
    public async Task ASavedChoiceThatIsNotOfferedFallsBackToTheDefault()
    {
        _h.Saved = new DetectionTesterSettings { ClauseType = "Compound", Hive = "HKU", View = "16-bit" };
        var viewModel = await _h.OpenAsync();
        Assert.Equal("RegistryKeyValue", viewModel.SelectedClauseType);
        Assert.Equal("HKLM", viewModel.SelectedHive);
        Assert.Equal("64-bit", viewModel.SelectedView);
    }

    [Fact]
    public async Task RunSendsTheRegistryValueClauseWithTheHiveTheViewAndTheThirtySecondTimeout()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedHive = "HKCU";
        viewModel.SelectedView = "32-bit";
        viewModel.KeyPath = @"SOFTWARE\Contoso\App";
        viewModel.ValueName = "Version";
        viewModel.RegistryOperator = "GreaterEquals";
        viewModel.RegistryPropertyType = "Version";
        viewModel.ExpectedValue = "2.0";
        viewModel.Negative = true;

        await viewModel.RunCommand.ExecuteAsync(null);

        var request = Assert.Single(_h.Tests.Requests);
        Assert.True(request.Negative);
        Assert.Equal(RegistryViewChoice.Registry32, request.View);
        Assert.Equal(TimeSpan.FromSeconds(30), request.ScriptTimeout);
        var d = request.Detection;
        Assert.Equal("RegistryKeyValue", d.Type);
        Assert.Equal("CurrentUser", d.Hive);
        Assert.False(d.Is64Bit);
        Assert.Equal(@"SOFTWARE\Contoso\App", d.RegistryKeyRelative);
        Assert.Equal("Version", d.ValueName);
        Assert.Equal("GreaterEquals", d.Operator);
        Assert.Equal("Version", d.PropertyType);
        Assert.Equal("2.0", d.ExpectedValue);
    }

    [Fact]
    public async Task APastedFullKeyPathSetsTheHiveAndKeepsTheRelativePath()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = @"HKEY_CURRENT_USER\Software\Contoso";
        await viewModel.RunCommand.ExecuteAsync(null);
        var d = Assert.Single(_h.Tests.Requests).Detection;
        Assert.Equal("CurrentUser", d.Hive);
        Assert.Equal(@"Software\Contoso", d.RegistryKeyRelative);
        Assert.Equal("HKCU", viewModel.SelectedHive);

        viewModel.KeyPath = @"HKLM:\SOFTWARE\Contoso";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal("LocalMachine", _h.Tests.Requests[1].Detection.Hive);
        Assert.Equal(@"SOFTWARE\Contoso", _h.Tests.Requests[1].Detection.RegistryKeyRelative);
    }

    [Fact]
    public async Task RunSendsFileAndScriptClauses()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "File";
        viewModel.FilePath = @"%ProgramFiles%\Contoso";
        viewModel.FileName = "app.exe";
        viewModel.FileProperty = "Version";
        viewModel.FileOperator = "GreaterThan";
        viewModel.FileExpectedValue = "1.2.3";
        await viewModel.RunCommand.ExecuteAsync(null);

        viewModel.SelectedClauseType = "Script";
        viewModel.ScriptText = "if (Test-Path C:\\x) { 'found' }";
        await viewModel.RunCommand.ExecuteAsync(null);

        var file = _h.Tests.Requests[0].Detection;
        Assert.Equal("File", file.Type);
        Assert.Equal(@"%ProgramFiles%\Contoso", file.FilePath);
        Assert.Equal("app.exe", file.FileName);
        Assert.Equal("Version", file.PropertyType);
        Assert.Equal("GreaterThan", file.Operator);
        Assert.Equal("1.2.3", file.ExpectedValue);
        var script = _h.Tests.Requests[1].Detection;
        Assert.Equal("Script", script.Type);
        Assert.Equal("PowerShell", script.ScriptLanguage);
        Assert.Equal("if (Test-Path C:\\x) { 'found' }", script.ScriptText);
    }

    [Fact]
    public async Task AFileExistenceClauseSendsNoOperatorAndNoExpectedValue()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "File";
        viewModel.FilePath = @"C:\Windows\System32";
        viewModel.FileName = "notepad.exe";
        viewModel.FileProperty = "Existence";
        viewModel.FileExpectedValue = "9.9";
        await viewModel.RunCommand.ExecuteAsync(null);
        var file = Assert.Single(_h.Tests.Requests).Detection;
        Assert.Null(file.ExpectedValue);
        Assert.Null(file.Operator);
        Assert.Equal("Existence", file.PropertyType);
    }

    [Fact]
    public async Task APassedRunShowsTheResultAndAddsOneHistoryRowOnTop()
    {
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now.AddDays(-1), new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "X" }, description: "old"));
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(DetectionTesterResultState.Passed, viewModel.ResultState);
        Assert.Equal("Passed", viewModel.ResultHeadline);
        Assert.Equal("Detected.", viewModel.ResultSummary);
        Assert.Equal("clause one", Assert.Single(viewModel.ResultClauses).Description);
        Assert.Equal("Windows PowerShell 5.1, current user, not SYSTEM", viewModel.RunContext);
        Assert.False(viewModel.ShowScriptNote);
        Assert.Equal(2, _h.History.Entries.Count);
        var added = _h.History.Entries[^1];
        Assert.Equal("RegistryKey", added.Kind);
        Assert.False(added.Negative);
        Assert.True(added.Passed);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", added.Detection.RegistryKeyRelative);
        Assert.Equal(2, viewModel.History.Count);
        Assert.Same(added, viewModel.History[0].Entry);
        Assert.Contains(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", viewModel.History[0].Description);
    }

    [Fact]
    public async Task AFailedRunShowsFailed()
    {
        _h.Tests.Answer = _ => FakeDetectionTestService.Result(detected: true, passed: false, summary: "Detected, and the test expects no detection.");
        var viewModel = await _h.OpenAsync();
        viewModel.KeyPath = @"SOFTWARE\A";
        viewModel.ValueName = "V";
        viewModel.Negative = true;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(DetectionTesterResultState.Failed, viewModel.ResultState);
        Assert.Equal("Failed", viewModel.ResultHeadline);
        Assert.False(_h.History.Entries.Single().Passed);
        Assert.True(_h.History.Entries.Single().Negative);
    }

    [Theory]
    [InlineData("RegistryKeyValue", "The key path is required.")]
    [InlineData("RegistryKey", "The key path is required.")]
    [InlineData("File", "The folder path is required.")]
    [InlineData("Script", "The script is required.")]
    public async Task AnIncompleteClauseIsNotRunAndNotRecorded(string type, string message)
    {
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = type;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Empty(_h.Tests.Requests);
        Assert.Empty(_h.History.Entries);
        Assert.Equal(DetectionTesterResultState.Invalid, viewModel.ResultState);
        Assert.Equal("Invalid clause", viewModel.ResultHeadline);
        Assert.Equal(message, viewModel.ResultSummary);
    }

    [Fact]
    public async Task ARegistryValueNeedsAValueNameAndAFileVersionNeedsAnExpectedVersion()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.KeyPath = @"SOFTWARE\A";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal("The value name is required.", viewModel.ResultSummary);

        viewModel.SelectedClauseType = "File";
        viewModel.FilePath = @"C:\A";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal("The file name is required.", viewModel.ResultSummary);
        viewModel.FileName = "a.exe";
        viewModel.FileProperty = "Version";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal("The expected version is required when the property is Version.", viewModel.ResultSummary);
        Assert.Empty(_h.Tests.Requests);
    }

    [Fact]
    public async Task AScriptResultShowsItsOutputExitCodeAndTheSystemNote()
    {
        _h.Tests.Answer = _ => FakeDetectionTestService.Result(true, true, "Detected.", "installed\r\n", "warning text", 3);
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "Script";
        viewModel.ScriptText = "'installed'";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal("installed\r\n", viewModel.ScriptStdout);
        Assert.Equal("warning text", viewModel.ScriptStderr);
        Assert.Equal("3", viewModel.ScriptExitCode);
        Assert.True(viewModel.ShowScriptOutput);
        Assert.True(viewModel.ShowScriptNote);
        Assert.Equal("ConfigMgr runs detection as SYSTEM and also reads the exit code.", DetectionTesterViewModel.ScriptSystemNote);
        Assert.Equal("This script runs on this computer as the current user.", DetectionTesterViewModel.ScriptEditorNote);

        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        _h.Tests.Answer = null;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.False(viewModel.ShowScriptNote);
        Assert.False(viewModel.ShowScriptOutput);
        Assert.Equal("", viewModel.ScriptExitCode);
    }

    [Fact]
    public async Task RunIsDisabledDuringARunAndCancelEndsItWithoutAHistoryRow()
    {
        _h.Tests.Pending = new TaskCompletionSource<DetectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "Script";
        viewModel.ScriptText = "Start-Sleep 60";

        var run = viewModel.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => _h.Tests.Requests.Count == 1);
        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        Assert.True(viewModel.CancelCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);
        await run;

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
        Assert.Equal(DetectionTesterResultState.Cancelled, viewModel.ResultState);
        Assert.Equal("The test was cancelled.", viewModel.ResultSummary);
        Assert.Empty(_h.History.Entries);
    }

    [Fact]
    public async Task AServiceFailureShowsAnErrorAndTheLogNamesNoScriptText()
    {
        _h.Tests.Failure = new InvalidOperationException("boom with Secret-Value-42");
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "Script";
        viewModel.ScriptText = "$password = 'Secret-Value-42'; 'x'";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(DetectionTesterResultState.Error, viewModel.ResultState);
        Assert.Equal("Error", viewModel.ResultHeadline);
        Assert.Equal("The test could not run. The log has the details.", viewModel.ResultSummary);
        Assert.Empty(_h.History.Entries);
        Assert.NotEmpty(_h.Log.Entries);
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain("Secret-Value-42", e.Message));
        Assert.All(_h.Log.Entries, e => Assert.Equal("DetectionTester", e.Area));
    }

    [Fact]
    public async Task ARunLogsTheKindAndTheResultButNeverTheScriptOrTheValues()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "Script";
        viewModel.ScriptText = "'token-abc-123'";
        await viewModel.RunCommand.ExecuteAsync(null);
        viewModel.SelectedClauseType = "RegistryKeyValue";
        viewModel.KeyPath = @"SOFTWARE\Hidden-Key-Name";
        viewModel.ValueName = "Hidden-Value-Name";
        viewModel.ExpectedValue = "Hidden-Expected";
        await viewModel.RunCommand.ExecuteAsync(null);

        var messages = _h.Log.Entries.Select(e => e.Message).ToList();
        Assert.Contains(messages, m => m.Contains("kind=Script") && m.Contains("result=Passed"));
        Assert.Contains(messages, m => m.Contains("kind=RegistryKeyValue"));
        Assert.All(messages, m =>
        {
            Assert.DoesNotContain("token-abc-123", m);
            Assert.DoesNotContain("Hidden-", m);
        });
    }

    [Fact]
    public async Task ARunSavesTheClauseTypeTheHiveAndTheView()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.SelectedHive = "HKCU";
        viewModel.SelectedView = "32-bit";
        viewModel.KeyPath = "SOFTWARE";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.NotNull(_h.Saved);
        Assert.Equal("RegistryKey", _h.Saved!.ClauseType);
        Assert.Equal("HKCU", _h.Saved.Hive);
        Assert.Equal("32-bit", _h.Saved.View);
    }

    [Fact]
    public async Task ImportingTheStageManifestShowsTheCompoundTreeAndRunsTheWholeDetection()
    {
        _h.Prompts.ManifestPath = DetectionTesterHarness.FixturePath;
        var viewModel = await _h.OpenAsync();
        Assert.DoesNotContain("Compound", viewModel.ClauseTypes);

        await viewModel.ImportManifestCommand.ExecuteAsync(null);

        Assert.Equal(["RegistryKeyValue", "RegistryKey", "File", "Script", "Compound"], viewModel.ClauseTypes);
        Assert.Equal("Compound", viewModel.SelectedClauseType);
        Assert.True(viewModel.IsCompound);
        Assert.Contains("7-Zip 24.09", viewModel.StatusMessage);
        var root = Assert.Single(viewModel.ImportedTree);
        Assert.Equal("Any of (Or)", root.Text);
        Assert.Equal(["All of (And)", "All of (And)"], root.Children.Select(c => c.Text));
        Assert.Equal(2, root.Children[0].Children.Count);
        Assert.StartsWith(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{23170F69", root.Children[0].Children[0].Text);
        Assert.StartsWith(@"%ProgramFiles%\7-Zip\7z.exe", root.Children[0].Children[1].Text);
        Assert.StartsWith(@"HKCU\SOFTWARE\7-Zip", Assert.Single(root.Children[1].Children).Text);
        Assert.Empty(_h.Tests.Requests);

        await viewModel.RunCommand.ExecuteAsync(null);

        var detection = Assert.Single(_h.Tests.Requests).Detection;
        Assert.Equal("Compound", detection.Type);
        Assert.Equal("Or", detection.Connector);
        Assert.Equal([2, 1], detection.GroupSizes!);
        Assert.Equal(["RegistryKeyValue", "File", "RegistryKey"], detection.Clauses!.Select(c => c.Type));
        Assert.Equal("Compound", _h.History.Entries.Single().Kind);
    }

    [Fact]
    public async Task APlainCompoundShowsItsConnectorAsTheRoot()
    {
        _h.Prompts.ManifestPath = "plain.json";
        _h.ReadManifest = _ => new StageManifest
        {
            AppName = "App",
            SoftwareVersion = "1.0",
            Detection = new StageDetection
            {
                Type = "Compound",
                Connector = "And",
                Clauses = [new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }, new StageDetection { Type = "Script", ScriptText = "'x'\n'y'" }]
            }
        };
        var viewModel = await _h.OpenAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        var root = Assert.Single(viewModel.ImportedTree);
        Assert.Equal("All of (And)", root.Text);
        Assert.Equal([@"HKLM\A exists (64-bit view)", "Script (2 lines)"], root.Children.Select(c => c.Text));
    }

    [Fact]
    public async Task ImportingASingleClauseFillsItsInputs()
    {
        _h.Prompts.ManifestPath = "single.json";
        _h.ReadManifest = _ => new StageManifest
        {
            AppName = "App",
            Detection = new StageDetection
            {
                Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\App", ValueName = "DisplayVersion", ExpectedValue = "3.1",
                Operator = "IsEquals", PropertyType = "Version", Is64Bit = false, Hive = "CurrentUser"
            }
        };
        var viewModel = await _h.OpenAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.Equal("RegistryKeyValue", viewModel.SelectedClauseType);
        Assert.Equal(@"SOFTWARE\App", viewModel.KeyPath);
        Assert.Equal("DisplayVersion", viewModel.ValueName);
        Assert.Equal("3.1", viewModel.ExpectedValue);
        Assert.Equal("Version", viewModel.RegistryPropertyType);
        Assert.Equal("HKCU", viewModel.SelectedHive);
        Assert.Equal("32-bit", viewModel.SelectedView);
        Assert.DoesNotContain("Compound", viewModel.ClauseTypes);
    }

    [Fact]
    public async Task ACancelledPickImportsNothing()
    {
        var viewModel = await _h.OpenAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.ImportedTree);
        Assert.Equal(DetectionTesterResultState.None, viewModel.ResultState);
    }

    [Fact]
    public async Task AFileThatIsNotAStageManifestReportsTheImportFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "apkg-dt-bad-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ not json");
        try
        {
            _h.Prompts.ManifestPath = path;
            var viewModel = await _h.OpenAsync();
            await viewModel.ImportManifestCommand.ExecuteAsync(null);
            Assert.Equal(DetectionTesterResultState.Invalid, viewModel.ResultState);
            Assert.Equal("Import failed", viewModel.ResultHeadline);
            Assert.Equal("The file could not be read as a stage manifest.", viewModel.ResultSummary);
            Assert.Empty(viewModel.ImportedTree);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UseSelectedFillsTheInputsFromAHistoryRow()
    {
        var detection = new StageDetection { Type = "File", FilePath = @"C:\Tools", FileName = "t.exe", PropertyType = "Version", Operator = "LessEquals", ExpectedValue = "5.0", Is64Bit = false };
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, detection, negative: true));
        var viewModel = await _h.OpenAsync();
        Assert.False(viewModel.UseSelectedCommand.CanExecute(null));
        viewModel.SelectedHistory = viewModel.History[0];
        Assert.True(viewModel.UseSelectedCommand.CanExecute(null));

        viewModel.UseSelectedCommand.Execute(null);

        Assert.Equal("File", viewModel.SelectedClauseType);
        Assert.Equal(@"C:\Tools", viewModel.FilePath);
        Assert.Equal("t.exe", viewModel.FileName);
        Assert.Equal("Version", viewModel.FileProperty);
        Assert.Equal("LessEquals", viewModel.FileOperator);
        Assert.Equal("5.0", viewModel.FileExpectedValue);
        Assert.Equal("32-bit", viewModel.SelectedView);
        Assert.True(viewModel.Negative);
    }

    [Fact]
    public async Task UseSelectedOnACompoundRowRestoresTheCompoundClause()
    {
        var compound = new StageDetection { Type = "Compound", Connector = "Or", Clauses = [new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "B" }] };
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, compound));
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedHistory = viewModel.History[0];
        viewModel.UseSelectedCommand.Execute(null);
        Assert.Equal("Compound", viewModel.SelectedClauseType);
        Assert.Contains("Compound", viewModel.ClauseTypes);
        Assert.Equal("Any of (Or)", Assert.Single(viewModel.ImportedTree).Text);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("html")]
    public async Task ExportCallsTheWriterWithTheHistoryReportsThePathAndOpensTheFolder(string format)
    {
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }));
        var viewModel = await _h.OpenAsync();

        await (format == "csv" ? viewModel.ExportCsvCommand : viewModel.ExportHtmlCommand).ExecuteAsync(null);

        var call = Assert.Single(_h.Reports.Calls);
        Assert.Equal(format, call.Format);
        Assert.Same(_h.History.Entries[0], Assert.Single(call.Entries));
        var path = $@"C:\Data\DetectionTester\Reports\detection-history-20261018-101500.{format}";
        Assert.Equal($"Saved the {format.ToUpperInvariant()} report to {path}.", viewModel.StatusMessage);
        Assert.Equal([@"C:\Data\DetectionTester\Reports"], _h.Prompts.OpenedFolders);
    }

    [Fact]
    public async Task AFailedExportReportsItAndOpensNothing()
    {
        _h.Reports.Failure = new IOException("The disk is full.");
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }));
        var viewModel = await _h.OpenAsync();
        await viewModel.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal("The CSV report could not be written.", viewModel.StatusMessage);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("disk is full"));
        Assert.Contains(_h.Log.Entries, e => e.Message.Contains("IOException"));
        Assert.Empty(_h.Prompts.OpenedFolders);
    }

    [Fact]
    public async Task AReportsFolderThatIsALinkShowsTheRefusal()
    {
        _h.Reports.Failure = new DetectionFolderRefusedException(DetectionFolderRefusedException.ReportsMessage);
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }));
        var viewModel = await _h.OpenAsync();
        await viewModel.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal(DetectionFolderRefusedException.ReportsMessage, viewModel.StatusMessage);
        Assert.Empty(_h.Prompts.OpenedFolders);
    }

    [Fact]
    public async Task AHistoryFolderThatIsALinkShowsTheRefusalOnLoadAndOnSave()
    {
        _h.History.ReadFailure = new DetectionFolderRefusedException(DetectionFolderRefusedException.HistoryMessage);
        var viewModel = await _h.OpenAsync();
        Assert.Equal(DetectionFolderRefusedException.HistoryMessage, viewModel.StatusMessage);

        viewModel.StatusMessage = "";
        _h.History.AddFailure = new DetectionFolderRefusedException(DetectionFolderRefusedException.HistoryMessage);
        viewModel.KeyPath = @"SOFTWARE\A";
        viewModel.SelectedClauseType = DetectionTesterViewModel.RegistryKey;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(DetectionFolderRefusedException.HistoryMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task AnImportReadFailureShowsFixedTextAndLogsOnlyTheType()
    {
        _h.Prompts.ManifestPath = "locked.json";
        _h.ReadManifest = _ => throw new IOException(@"The process cannot access \\server\share\locked.json");
        var viewModel = await _h.OpenAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.Equal("The file could not be read as a stage manifest.", viewModel.ResultSummary);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("server"));
    }

    [Fact]
    public async Task TheFirstVisitReadsTheSavedChoicesOffTheCallingThread()
    {
        using var gate = new ManualResetEventSlim(false);
        _h.PreferenceGate = gate;
        _h.Saved = new DetectionTesterSettings { ClauseType = "File", Hive = "HKCU", View = "32-bit" };
        var viewModel = _h.Create();

        var visit = viewModel.OnNavigatedToAsync();

        Assert.False(visit.IsCompleted);
        Assert.Equal("RegistryKeyValue", viewModel.SelectedClauseType);
        gate.Set();
        await visit.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("File", viewModel.SelectedClauseType);
        Assert.Equal("HKCU", viewModel.SelectedHive);
        Assert.Equal("32-bit", viewModel.SelectedView);
        Assert.NotEqual(Environment.CurrentManagedThreadId, _h.PreferenceThreadId);
    }

    [Fact]
    public async Task AnEmptyHistoryExportsNothing()
    {
        var viewModel = await _h.OpenAsync();
        await viewModel.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Empty(_h.Reports.Calls);
        Assert.Equal("The history is empty.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ClearHistoryAsksFirstAndADeclineKeepsEveryRow()
    {
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }));
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "B" }));
        var viewModel = await _h.OpenAsync();

        await viewModel.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Equal([2], _h.Prompts.ConfirmRequests);
        Assert.Equal(0, _h.History.ClearCount);
        Assert.Equal(2, viewModel.History.Count);

        _h.Prompts.ConfirmAnswer = true;
        await viewModel.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Equal(1, _h.History.ClearCount);
        Assert.Empty(viewModel.History);
        Assert.Equal("Cleared the history.", viewModel.StatusMessage);
    }

    private static InstalledApplication App(string name, string publisher = "Contoso", string version = "1.0", string source = "HKLM 64-bit",
        string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App") =>
        new(name, publisher, version, "20261001", source, key, "uninstall.exe", "uninstall.exe /S", @"C:\Program Files\App");

    [Fact]
    public async Task TheInstalledApplicationsLoadWhenTheTabOpensAndTheFilterMatchesNamePublisherAndVersion()
    {
        _h.Applications.Applications.AddRange([App("7-Zip", "Igor Pavlov", "24.09"), App("Notepad++", "Notepad++ Team", "8.7"), App("Contoso Agent", "Contoso", "2.0")]);
        var viewModel = await _h.OpenAsync();
        Assert.Equal(0, _h.Applications.Calls);

        viewModel.SelectedTabIndex = 1;
        await WaitUntil(() => viewModel.ApplicationCount == "3 applications");
        Assert.Equal(1, _h.Applications.Calls);
        Assert.Equal("3 applications", viewModel.ApplicationCount);

        viewModel.ApplicationFilter = "igor";
        Assert.Equal(["7-Zip"], viewModel.Applications.Select(a => a.Name));
        viewModel.ApplicationFilter = "8.7";
        Assert.Equal(["Notepad++"], viewModel.Applications.Select(a => a.Name));
        Assert.Equal("1 of 3 applications", viewModel.ApplicationCount);
        viewModel.ApplicationFilter = "";
        Assert.Equal(3, viewModel.Applications.Count);

        viewModel.SelectedTabIndex = 0;
        viewModel.SelectedTabIndex = 1;
        await Task.Delay(50);
        Assert.Equal(1, _h.Applications.Calls);
        await viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        Assert.Equal(2, _h.Applications.Calls);
    }

    [Fact]
    public async Task CopyDetailsPutsEveryFieldOnTheClipboard()
    {
        _h.Applications.Applications.Add(App("7-Zip", "Igor Pavlov", "24.09"));
        var viewModel = await _h.OpenAsync();
        await viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        Assert.False(viewModel.CopyDetailsCommand.CanExecute(null));
        viewModel.SelectedApplication = viewModel.Applications[0];
        viewModel.CopyDetailsCommand.Execute(null);
        var text = Assert.Single(_h.Prompts.Copied);
        foreach (var expected in new[] { "Name: 7-Zip", "Publisher: Igor Pavlov", "Version: 24.09", "Install date: 20261001", "Source: HKLM 64-bit",
                     @"Registry key: HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App", "Uninstall string: uninstall.exe",
                     "Quiet uninstall string: uninstall.exe /S", @"Install location: C:\Program Files\App" })
            Assert.Contains(expected, text);
    }

    [Fact]
    public async Task UseForDetectionFillsARegistryValueClauseFor32BitApplications()
    {
        _h.Applications.Applications.Add(App("Old Tool", version: "4.2", source: "HKLM 32-bit",
            key: @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\OldTool"));
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedTabIndex = 1;
        await WaitUntil(() => viewModel.Applications.Count == 1);
        viewModel.Negative = true;
        viewModel.SelectedApplication = viewModel.Applications[0];

        viewModel.UseForDetectionCommand.Execute(null);

        Assert.Equal(0, viewModel.SelectedTabIndex);
        Assert.Equal("RegistryKeyValue", viewModel.SelectedClauseType);
        Assert.Equal("HKLM", viewModel.SelectedHive);
        Assert.Equal("32-bit", viewModel.SelectedView);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OldTool", viewModel.KeyPath);
        Assert.Equal("DisplayVersion", viewModel.ValueName);
        Assert.Equal("IsEquals", viewModel.RegistryOperator);
        Assert.Equal("String", viewModel.RegistryPropertyType);
        Assert.Equal("4.2", viewModel.ExpectedValue);
        Assert.False(viewModel.Negative);
    }

    [Fact]
    public async Task UseForDetectionReadsTheUserHiveAndFallsBackToTheDisplayName()
    {
        _h.Applications.Applications.Add(App("User App", version: "", source: "HKCU",
            key: @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\UserApp"));
        var viewModel = await _h.OpenAsync();
        await viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        viewModel.SelectedApplication = viewModel.Applications[0];
        viewModel.UseForDetectionCommand.Execute(null);
        Assert.Equal("HKCU", viewModel.SelectedHive);
        Assert.Equal("64-bit", viewModel.SelectedView);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\UserApp", viewModel.KeyPath);
        Assert.Equal("DisplayName", viewModel.ValueName);
        Assert.Equal("User App", viewModel.ExpectedValue);
    }

    [Fact]
    public async Task LeavingThePageReleasesTheInstalledApplications()
    {
        _h.Applications.Applications.Add(App("7-Zip"));
        var viewModel = await _h.OpenAsync();
        await viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        viewModel.SelectedApplication = viewModel.Applications[0];

        await viewModel.OnNavigatedFromAsync();

        Assert.Empty(viewModel.Applications);
        Assert.Null(viewModel.SelectedApplication);
        Assert.Equal("", viewModel.ApplicationCount);

        viewModel.SelectedTabIndex = 1;
        await viewModel.OnNavigatedToAsync();
        await WaitUntil(() => viewModel.Applications.Count == 1);
        Assert.Equal(2, _h.Applications.Calls);
    }

    [Fact]
    public async Task AnInstalledApplicationsFailureIsReportedAndLogged()
    {
        var viewModel = new DetectionTesterViewModel(new DetectionTesterPageServices(
            _h.Tests, _h.History, _h.Reports, new ThrowingApplications(), _h.Prompts, () => null, _ => true, _h.Log, _ => new StageManifest()));
        await viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.Applications);
        Assert.Equal("The installed applications could not be read.", viewModel.ApplicationStatus);
        Assert.Contains(_h.Log.Entries, e => e.Level == AppLogLevel.Warning);
    }

    private sealed class ThrowingApplications : IInstalledApplicationsService
    {
        public IReadOnlyList<InstalledApplication> Enumerate(bool includeSystemComponents = false) => throw new UnauthorizedAccessException("denied");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold within ten seconds.");
            await Task.Delay(5);
        }
    }
}
