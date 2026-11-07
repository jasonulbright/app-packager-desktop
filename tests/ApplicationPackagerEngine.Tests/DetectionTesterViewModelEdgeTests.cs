using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

/// <summary>Imports, failures, page changes and repeated commands on the Detection Tester view model.</summary>
public sealed class DetectionTesterViewModelEdgeTests
{
    private readonly DetectionTesterHarness _h = new();

    private static StageDetection RegistryValue(string key = @"SOFTWARE\Contoso", string value = "Version") =>
        new() { Type = "RegistryKeyValue", RegistryKeyRelative = key, ValueName = value, ExpectedValue = "1.0", Operator = "IsEquals", PropertyType = "String" };

    private async Task<DetectionTesterViewModel> OpenWithInputsAsync()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.KeyPath = @"SOFTWARE\Kept";
        viewModel.ValueName = "KeptValue";
        viewModel.ExpectedValue = "kept";
        return viewModel;
    }

    private static void AssertInputsKept(DetectionTesterViewModel viewModel)
    {
        Assert.Equal("RegistryKeyValue", viewModel.SelectedClauseType);
        Assert.Equal(@"SOFTWARE\Kept", viewModel.KeyPath);
        Assert.Equal("KeptValue", viewModel.ValueName);
        Assert.Equal("kept", viewModel.ExpectedValue);
    }

    // Import

    [WindowFact]
    public async Task AManifestWithoutADetectionSaysSoAndLeavesTheInputs()
    {
        _h.Prompts.ManifestPath = "empty.json";
        _h.ReadManifest = _ => new StageManifest { AppName = "App", SoftwareVersion = "1.0" };
        var viewModel = await OpenWithInputsAsync();

        await viewModel.ImportManifestCommand.ExecuteAsync(null);

        Assert.Equal("This manifest has no detection to import.", viewModel.StatusMessage);
        AssertInputsKept(viewModel);
        Assert.DoesNotContain("Compound", viewModel.ClauseTypes);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.StartsWith("Imported", StringComparison.Ordinal));
    }

    [WindowFact]
    public async Task AManifestWithDeploymentTypesImportsTheFirstAndSaysSo()
    {
        _h.Prompts.ManifestPath = "dts.json";
        _h.ReadManifest = _ => new StageManifest
        {
            AppName = "App",
            SoftwareVersion = "2.0",
            DeploymentTypes =
            [
                new StageDeploymentType { NameSuffix = "x64", Detection = RegistryValue(@"SOFTWARE\First") },
                new StageDeploymentType { NameSuffix = "x86", Detection = RegistryValue(@"SOFTWARE\Second") }
            ]
        };
        var viewModel = await _h.OpenAsync();

        await viewModel.ImportManifestCommand.ExecuteAsync(null);

        Assert.Equal(@"SOFTWARE\First", viewModel.KeyPath);
        Assert.Contains("2 deployment types", viewModel.StatusMessage);
        Assert.Contains("x64", viewModel.StatusMessage);
        Assert.Contains("App 2.0", viewModel.StatusMessage);
    }

    [WindowFact]
    public async Task ADeploymentTypeWithoutItsOwnDetectionUsesTheManifestDetection()
    {
        _h.Prompts.ManifestPath = "dts.json";
        _h.ReadManifest = _ => new StageManifest
        {
            AppName = "App",
            Detection = RegistryValue(@"SOFTWARE\Base"),
            DeploymentTypes = [new StageDeploymentType { NameSuffix = "Main" }]
        };
        var viewModel = await _h.OpenAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.Equal(@"SOFTWARE\Base", viewModel.KeyPath);
        Assert.StartsWith("Imported the detection of App.", viewModel.StatusMessage);
    }

    [WindowFact]
    public async Task DeploymentTypesWithoutAnyDetectionImportNothing()
    {
        _h.Prompts.ManifestPath = "dts.json";
        _h.ReadManifest = _ => new StageManifest
        {
            AppName = "App",
            DeploymentTypes = [new StageDeploymentType { NameSuffix = "A" }, new StageDeploymentType { NameSuffix = "B", Detection = new StageDetection() }]
        };
        var viewModel = await OpenWithInputsAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.Equal("This manifest has no detection to import.", viewModel.StatusMessage);
        AssertInputsKept(viewModel);
    }

    [WindowFact]
    public async Task AnUnsupportedClauseTypeIsReportedWithoutASuccessMessageOrLogLine()
    {
        _h.Prompts.ManifestPath = "wmi.json";
        _h.ReadManifest = _ => new StageManifest { AppName = "App", Detection = new StageDetection { Type = "Wmi", RegistryKeyRelative = "x" } };
        var viewModel = await OpenWithInputsAsync();

        await viewModel.ImportManifestCommand.ExecuteAsync(null);

        Assert.Equal("The clause type Wmi is not supported.", viewModel.StatusMessage);
        AssertInputsKept(viewModel);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.StartsWith("Imported", StringComparison.Ordinal));
    }

    [WindowFact]
    public void LoadDetectionReturnsFalseForAnUnsupportedTypeAndTrueOtherwise()
    {
        var viewModel = _h.Create();
        Assert.False(viewModel.LoadDetection(new StageDetection { Type = "Wmi" }));
        Assert.Equal("RegistryKeyValue", viewModel.SelectedClauseType);
        Assert.True(viewModel.LoadDetection(new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }));
        Assert.Equal("RegistryKey", viewModel.SelectedClauseType);
    }

    [WindowFact]
    public void LoadDetectionRejectsAnUnsupportedScriptLanguageWithoutChangingTheInputs()
    {
        var viewModel = _h.Create();
        viewModel.SelectedClauseType = DetectionTesterViewModel.Script;
        viewModel.ScriptText = "kept";

        Assert.False(viewModel.LoadDetection(new StageDetection
        {
            Type = DetectionTesterViewModel.Script,
            ScriptLanguage = "VBScript",
            ScriptText = "WScript.Echo 'not PowerShell'"
        }));

        Assert.Equal(DetectionTesterViewModel.Script, viewModel.SelectedClauseType);
        Assert.Equal("kept", viewModel.ScriptText);
        Assert.Contains("VBScript", viewModel.StatusMessage);
    }

    [WindowFact]
    public void LoadDetectionRejectsAnUnsupportedScriptNestedInACompoundTree()
    {
        var viewModel = _h.Create();
        viewModel.SelectedClauseType = DetectionTesterViewModel.RegistryKeyValue;
        viewModel.KeyPath = @"SOFTWARE\Kept";
        var detection = new StageDetection
        {
            Type = DetectionTesterViewModel.Compound,
            Connector = "Or",
            Clauses =
            [
                new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" },
                new StageDetection
                {
                    Type = DetectionTesterViewModel.Compound,
                    Connector = "And",
                    Clauses = [new StageDetection { Type = DetectionTesterViewModel.Script, ScriptLanguage = "VBScript", ScriptText = "WScript.Echo 'unsupported'" }]
                }
            ]
        };

        Assert.False(viewModel.LoadDetection(detection));

        Assert.Equal(DetectionTesterViewModel.RegistryKeyValue, viewModel.SelectedClauseType);
        Assert.Equal(@"SOFTWARE\Kept", viewModel.KeyPath);
        Assert.DoesNotContain(DetectionTesterViewModel.Compound, viewModel.ClauseTypes);
        Assert.Empty(viewModel.ImportedTree);
        Assert.Contains("VBScript", viewModel.StatusMessage);
    }

    [WindowFact]
    public async Task UseSelectedOnAnUnsupportedRowKeepsTheUnsupportedMessage()
    {
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "Wmi" }, negative: true));
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedHistory = viewModel.History[0];
        viewModel.UseSelectedCommand.Execute(null);
        Assert.Equal("The clause type Wmi is not supported.", viewModel.StatusMessage);
        Assert.False(viewModel.Negative);
    }

    [WindowTheory]
    [InlineData("Matches", "String", "The operator Matches is not offered. The inputs use IsEquals.")]
    [InlineData("IsEquals", "Binary", "The data type Binary is not offered. The inputs use String.")]
    public async Task AnUnknownOperatorOrDataTypeFallsBackAndSaysSo(string op, string propertyType, string note)
    {
        _h.Prompts.ManifestPath = "odd.json";
        _h.ReadManifest = _ => new StageManifest
        {
            AppName = "App",
            Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = "A", ValueName = "V", Operator = op, PropertyType = propertyType }
        };
        var viewModel = await _h.OpenAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.StartsWith("Imported the detection of App.", viewModel.StatusMessage);
        Assert.EndsWith(note, viewModel.StatusMessage);
    }

    [WindowFact]
    public async Task AnUnknownFileOperatorFallsBackAndSaysSo()
    {
        var detection = new StageDetection { Type = "File", FilePath = @"C:\A", FileName = "a.exe", PropertyType = "Version", Operator = "Between", ExpectedValue = "1.0" };
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, detection));
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedHistory = viewModel.History[0];
        viewModel.UseSelectedCommand.Execute(null);
        Assert.Equal("GreaterEquals", viewModel.FileOperator);
        Assert.Equal("Filled the inputs from the selected test. The operator Between is not offered. The inputs use GreaterEquals.", viewModel.StatusMessage);
    }

    [WindowFact]
    public async Task AKnownOperatorWithOtherCaseIsNotReported()
    {
        _h.Prompts.ManifestPath = "case.json";
        _h.ReadManifest = _ => new StageManifest
        {
            AppName = "App",
            Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = "A", ValueName = "V", Operator = "greaterequals", PropertyType = "version" }
        };
        var viewModel = await _h.OpenAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.Equal("GreaterEquals", viewModel.RegistryOperator);
        Assert.Equal("Imported the detection of App. Press Run to test it.", viewModel.StatusMessage);
    }

    [WindowFact]
    public async Task AManifestOverTheSizeLimitIsNotRead()
    {
        var path = Path.Combine(Path.GetTempPath(), "apkg-dt-huge-" + Guid.NewGuid().ToString("N") + ".json");
        using (var stream = File.Create(path)) stream.SetLength(DetectionTesterViewModel.MaxManifestBytes + 1);
        try
        {
            _h.Prompts.ManifestPath = path;
            var viewModel = await OpenWithInputsAsync();
            await viewModel.ImportManifestCommand.ExecuteAsync(null);
            Assert.Equal(0, _h.ManifestReads);
            Assert.Equal(DetectionTesterResultState.Invalid, viewModel.ResultState);
            Assert.Equal("Import failed", viewModel.ResultHeadline);
            Assert.Equal("The file is too large for a stage manifest.", viewModel.ResultSummary);
            AssertInputsKept(viewModel);
        }
        finally { File.Delete(path); }
    }

    [WindowFact]
    public async Task APathToAFolderReportsTheImportFailure()
    {
        var folder = Path.Combine(Path.GetTempPath(), "apkg-dt-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            _h.Prompts.ManifestPath = folder;
            var viewModel = await OpenWithInputsAsync();
            await viewModel.ImportManifestCommand.ExecuteAsync(null);
            Assert.Equal(DetectionTesterResultState.Invalid, viewModel.ResultState);
            Assert.Equal("Import failed", viewModel.ResultHeadline);
            AssertInputsKept(viewModel);
        }
        finally { Directory.Delete(folder); }
    }

    [WindowFact]
    public async Task AnUnexpectedReadFailureIsReportedWithoutItsText()
    {
        _h.Prompts.ManifestPath = "boom.json";
        _h.ReadManifest = _ => throw new FormatException("secret-path-text");
        var viewModel = await OpenWithInputsAsync();
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.Equal("Import failed", viewModel.ResultHeadline);
        Assert.Equal("The file could not be read as a stage manifest.", viewModel.ResultSummary);
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain("secret-path-text", e.Message));
        Assert.Contains(_h.Log.Entries, e => e.Message.Contains("FormatException"));
    }

    // History failures

    [WindowTheory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FormatException))]
    public async Task AHistoryReadFailureIsReportedAndLoggedByTypeOnly(Type failure)
    {
        _h.History.ReadFailure = (Exception)Activator.CreateInstance(failure, "hidden-detail")!;
        var viewModel = await _h.OpenAsync();
        Assert.Equal("The history could not be read.", viewModel.StatusMessage);
        Assert.Empty(viewModel.History);
        Assert.Contains(_h.Log.Entries, e => e.Message.Contains(failure.Name));
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain("hidden-detail", e.Message));
    }

    [WindowTheory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    public async Task AHistorySaveFailureKeepsTheResultAndReportsIt(Type failure)
    {
        _h.History.AddFailure = (Exception)Activator.CreateInstance(failure, "hidden-detail")!;
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(DetectionTesterResultState.Passed, viewModel.ResultState);
        Assert.Equal("The test result could not be saved to the history.", viewModel.StatusMessage);
        Assert.Single(viewModel.History);
        Assert.Contains(_h.Log.Entries, e => e.Message.Contains(failure.Name));
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain("hidden-detail", e.Message));
    }

    [WindowTheory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(NotSupportedException))]
    public async Task AHistoryClearFailureKeepsTheRows(Type failure)
    {
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }));
        _h.History.ClearFailure = (Exception)Activator.CreateInstance(failure, "hidden-detail")!;
        _h.Prompts.ConfirmAnswer = true;
        var viewModel = await _h.OpenAsync();
        await viewModel.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Equal("The history could not be cleared.", viewModel.StatusMessage);
        Assert.Single(viewModel.History);
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain("hidden-detail", e.Message));
    }

    [WindowFact]
    public async Task AnUnexpectedReportFailureIsReportedWithoutItsText()
    {
        _h.Reports.Failure = new FormatException("hidden-detail");
        _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now, new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }));
        var viewModel = await _h.OpenAsync();
        await viewModel.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal("The HTML report could not be written.", viewModel.StatusMessage);
        Assert.Empty(_h.Prompts.OpenedFolders);
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain("hidden-detail", e.Message));
    }

    [WindowFact]
    public async Task AnUnexpectedEnumerateFailureIsReported()
    {
        _h.Applications.Failure = new FormatException("hidden-detail");
        var viewModel = await _h.OpenAsync();
        await viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        Assert.Equal("The installed applications could not be read.", viewModel.ApplicationStatus);
        Assert.False(viewModel.IsLoadingApplications);
        Assert.Contains(_h.Log.Entries, e => e.Message.Contains("FormatException"));
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain("hidden-detail", e.Message));
    }

    [WindowFact]
    public async Task HistoryInMemoryKeepsTheNewest500()
    {
        for (var i = 0; i < DetectionTesterViewModel.HistoryLimit; i++)
            _h.History.Entries.Add(DetectionTesterHarness.Entry(DateTimeOffset.Now.AddMinutes(-1000 + i), new StageDetection { Type = "RegistryKey", RegistryKeyRelative = "A" }, description: $"old {i}"));
        var viewModel = await _h.OpenAsync();
        Assert.Equal(500, viewModel.History.Count);
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";

        await viewModel.RunCommand.ExecuteAsync(null);
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(500, viewModel.History.Count);
        Assert.Contains("SOFTWARE", viewModel.History[0].Description);
        Assert.Equal("old 2", viewModel.History[^1].Description);
    }

    [WindowFact]
    public void TheHistoryNoteSaysWhatIsKept() =>
        Assert.Equal("History keeps the clause text, including script text, on this computer.", DetectionTesterViewModel.HistoryNote);

    // Clipboard

    [WindowFact]
    public async Task AClipboardFailureIsReported()
    {
        _h.Applications.Applications.Add(new InstalledApplication("7-Zip", "Igor Pavlov", "24.09", "", "HKLM 64-bit", @"HKLM\SOFTWARE\X", "", "", ""));
        _h.Prompts.CopyFails = true;
        var viewModel = await _h.OpenAsync();
        await viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        viewModel.SelectedApplication = viewModel.Applications[0];
        viewModel.CopyDetailsCommand.Execute(null);
        Assert.Equal("The details could not be copied. Another program holds the clipboard.", viewModel.ApplicationStatus);

        _h.Prompts.CopyFails = false;
        viewModel.CopyDetailsCommand.Execute(null);
        Assert.Equal("Copied the details of 7-Zip.", viewModel.ApplicationStatus);
    }

    // Preferences

    [WindowFact]
    public async Task ChoicesAreSavedOffTheUiThreadAndOnlyWhenTheyChange()
    {
        _h.Saved = new DetectionTesterSettings { ClauseType = "RegistryKey", Hive = "HKLM", View = "64-bit" };
        var viewModel = await _h.OpenAsync();
        viewModel.KeyPath = "SOFTWARE";

        await viewModel.RunCommand.ExecuteAsync(null);
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(0, _h.SaveCount);

        viewModel.SelectedView = "32-bit";
        await viewModel.RunCommand.ExecuteAsync(null);
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(1, _h.SaveCount);
        Assert.Equal("32-bit", _h.Saved!.View);
    }

    [WindowFact]
    public void ChoicesAreSavedOnAPoolThreadWhenTheRunStartsOnTheUiThread()
    {
        StaTestHelper.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
            var uiThread = Environment.CurrentManagedThreadId;
            var viewModel = _h.Create();
            viewModel.SelectedClauseType = "RegistryKey";
            viewModel.KeyPath = "SOFTWARE";
            var run = viewModel.RunCommand.ExecuteAsync(null);
            StaTestHelper.PumpUntil(() => run.IsCompleted);
            Assert.Equal(1, _h.SaveCount);
            Assert.NotEqual(uiThread, _h.SaveThreadId);
        });
    }

    [WindowFact]
    public async Task ASaveFailureDoesNotStopTheRunAndIsTriedAgain()
    {
        _h.SaveFailure = new FormatException("x");
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(DetectionTesterResultState.Passed, viewModel.ResultState);
        _h.SaveFailure = null;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(2, _h.SaveCount);
        Assert.Equal("RegistryKey", _h.Saved!.ClauseType);
    }

    [WindowFact]
    public async Task AFalsePreferenceSaveDoesNotStopTheRunAndIsTriedAgain()
    {
        _h.SaveResult = false;
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(DetectionTesterResultState.Passed, viewModel.ResultState);
        Assert.Equal(1, _h.SaveCount);

        _h.SaveResult = true;
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, _h.SaveCount);
        Assert.Equal("RegistryKey", _h.Saved!.ClauseType);
    }

    [WindowFact]
    public async Task ConcurrentChoiceChangesSaveTheLatestSnapshot()
    {
        var viewModel = await _h.OpenAsync();
        _h.SaveGate = new ManualResetEventSlim(false);
        _h.SaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            viewModel.ShowSystemComponents = true;
            await _h.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.ShowSystemComponents = false;
            _h.SaveGate.Set();
            await WaitUntil(() => _h.SaveCount == 2 && _h.SaveCompletedCount == 2);

            await viewModel.RunCommand.ExecuteAsync(null);

            Assert.Equal(2, _h.SaveCount);
            Assert.False(_h.Saved!.ShowSystemComponents);
        }
        finally
        {
            _h.SaveGate.Set();
        }
    }

    // Show system components

    [WindowFact]
    public async Task ShowSystemComponentsIsOffByDefaultReloadsOnToggleAndIsRemembered()
    {
        _h.Saved = new DetectionTesterSettings { ClauseType = "File", Hive = "HKCU", View = "32-bit" };
        var viewModel = await _h.OpenAsync();
        Assert.False(viewModel.ShowSystemComponents);
        viewModel.SelectedTabIndex = 1;
        await WaitUntil(() => !viewModel.IsLoadingApplications && _h.Applications.Calls == 1);
        Assert.Equal([false], _h.Applications.Includes);

        viewModel.ShowSystemComponents = true;
        await WaitUntil(() => _h.Applications.Calls == 2 && !viewModel.IsLoadingApplications);
        Assert.Equal([false, true], _h.Applications.Includes);
        await WaitUntil(() => _h.Saved!.ShowSystemComponents);
        Assert.Equal("File", _h.Saved!.ClauseType);
        Assert.Equal("HKCU", _h.Saved.Hive);
        Assert.Equal("32-bit", _h.Saved.View);

        var reopened = new DetectionTesterHarness { Saved = _h.Saved };
        var next = await reopened.OpenAsync();
        Assert.True(next.ShowSystemComponents);
        Assert.Equal(0, reopened.Applications.Calls);
        Assert.Equal(0, reopened.SaveCount);
        await next.RefreshApplicationsCommand.ExecuteAsync(null);
        Assert.Equal([true], reopened.Applications.Includes);
    }

    [WindowFact]
    public async Task TogglingBeforeTheTabOpensReadsNothing()
    {
        var viewModel = await _h.OpenAsync();
        viewModel.ShowSystemComponents = true;
        await Task.Delay(50);
        Assert.Equal(0, _h.Applications.Calls);
        viewModel.SelectedTabIndex = 1;
        await WaitUntil(() => _h.Applications.Calls == 1 && !viewModel.IsLoadingApplications);
        Assert.Equal([true], _h.Applications.Includes);
    }

    [WindowFact]
    public void ASettingsFileWithoutTheFlagReadsAsOff()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<DetectionTesterSettings>("{\"ClauseType\":\"Script\"}")!;
        Assert.False(settings.ShowSystemComponents);
    }

    // Leaving the page, repeated commands, cancel order

    [WindowFact]
    public async Task LeavingThePageDuringTheLoadDiscardsTheResultAndTheNextVisitReadsAgain()
    {
        _h.Applications.Applications.Add(new InstalledApplication("7-Zip", "", "", "", "HKLM 64-bit", @"HKLM\SOFTWARE\X", "", "", ""));
        using var gate = new ManualResetEventSlim(false);
        _h.Applications.Gate = gate;
        var viewModel = await _h.OpenAsync();
        var load = viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        await WaitUntil(() => _h.Applications.Calls == 1);

        await viewModel.OnNavigatedFromAsync();
        Assert.False(viewModel.IsLoadingApplications);
        gate.Set();
        await load;

        Assert.Empty(viewModel.Applications);
        Assert.Equal("", viewModel.ApplicationCount);
        Assert.Equal("", viewModel.ApplicationStatus);
        Assert.False(viewModel.IsLoadingApplications);

        viewModel.SelectedTabIndex = 1;
        await viewModel.OnNavigatedToAsync();
        await WaitUntil(() => viewModel.Applications.Count == 1);
        Assert.Equal(2, _h.Applications.Calls);
    }

    [WindowFact]
    public async Task LeavingAndReturningDuringTheLoadStartsAFreshLoad()
    {
        _h.Applications.Applications.Add(new InstalledApplication("7-Zip", "", "", "", "HKLM 64-bit", @"HKLM\SOFTWARE\X", "", "", ""));
        using var gate = new ManualResetEventSlim(false);
        _h.Applications.Gate = gate;
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedTabIndex = 1;
        await WaitUntil(() => _h.Applications.Calls == 1);

        await viewModel.OnNavigatedFromAsync();
        var back = viewModel.OnNavigatedToAsync();
        await WaitUntil(() => _h.Applications.Calls == 2);
        gate.Set();
        await back;

        await WaitUntil(() => viewModel.Applications.Count == 1 && !viewModel.IsLoadingApplications);
        Assert.Equal("1 applications", viewModel.ApplicationCount);
    }

    [WindowFact]
    public async Task LeavingThePageDuringARunKeepsTheRunAndRecordsItsResult()
    {
        _h.Tests.Pending = new TaskCompletionSource<DetectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        var run = viewModel.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => _h.Tests.Requests.Count == 1);

        await viewModel.OnNavigatedFromAsync();
        Assert.True(viewModel.IsRunning);
        _h.Tests.Pending.SetResult(FakeDetectionTestService.Result(true, true));
        await run;

        Assert.False(viewModel.IsRunning);
        Assert.Equal(DetectionTesterResultState.Passed, viewModel.ResultState);
        Assert.Single(_h.History.Entries);
    }

    [WindowFact]
    public async Task ASecondRunDuringARunStartsNothing()
    {
        _h.Tests.Pending = new TaskCompletionSource<DetectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        var first = viewModel.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => _h.Tests.Requests.Count == 1);

        viewModel.RunCommand.Execute(null);
        await viewModel.RunCommand.ExecuteAsync(null);
        await Task.Delay(50);
        Assert.Single(_h.Tests.Requests);

        _h.Tests.Pending.SetResult(FakeDetectionTestService.Result(true, true));
        await first;
        Assert.Single(_h.History.Entries);
    }

    [WindowFact]
    public async Task ACancelBeforeTheServiceCompletesShowsCancelledAndRecordsNothing()
    {
        _h.Tests.Pending = new TaskCompletionSource<DetectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Tests.IgnoreCancel = true;
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "Script";
        viewModel.ScriptText = "Start-Sleep 60";
        var run = viewModel.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => _h.Tests.Requests.Count == 1);

        viewModel.CancelCommand.Execute(null);
        _h.Tests.Pending.SetResult(FakeDetectionTestService.Result(false, false, "Not detected."));
        await run;

        Assert.Equal(DetectionTesterResultState.Cancelled, viewModel.ResultState);
        Assert.Empty(_h.History.Entries);
        Assert.Empty(viewModel.History);
    }

    [WindowFact]
    public async Task ACompletionBeforeTheCancelKeepsTheResult()
    {
        _h.Tests.Pending = new TaskCompletionSource<DetectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        var run = viewModel.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => _h.Tests.Requests.Count == 1);

        _h.Tests.Pending.SetResult(FakeDetectionTestService.Result(true, true));
        await run;
        viewModel.CancelCommand.Execute(null);

        Assert.Equal(DetectionTesterResultState.Passed, viewModel.ResultState);
        Assert.Single(_h.History.Entries);
    }

    [WindowFact]
    public async Task ACancellationTheOperatorDidNotAskForIsAnError()
    {
        _h.Tests.Failure = new OperationCanceledException();
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(DetectionTesterResultState.Error, viewModel.ResultState);
        Assert.False(viewModel.IsRunning);
        Assert.Empty(_h.History.Entries);
    }

    [WindowFact]
    public async Task AnOperatorCancelThatTheServiceReportsAsCancelledShowsCancelled()
    {
        _h.Tests.Pending = new TaskCompletionSource<DetectionTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = await _h.OpenAsync();
        viewModel.SelectedClauseType = "RegistryKey";
        viewModel.KeyPath = "SOFTWARE";
        var run = viewModel.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => _h.Tests.Requests.Count == 1);
        viewModel.CancelCommand.Execute(null);
        await run;
        Assert.Equal(DetectionTesterResultState.Cancelled, viewModel.ResultState);
        Assert.Contains(_h.Log.Entries, e => e.Level == AppLogLevel.Info && e.Message.StartsWith("Test cancelled", StringComparison.Ordinal));
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
