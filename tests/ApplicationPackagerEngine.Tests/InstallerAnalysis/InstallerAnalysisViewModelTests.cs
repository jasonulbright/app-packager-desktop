using System.Text.Json;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

public sealed class InstallerAnalysisViewModelTests : IDisposable
{
    private readonly InstallerAnalysisHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<(InstallerAnalysisViewModel ViewModel, string Path)> AnalyzedAsync(string name = "setup.exe")
    {
        var viewModel = await _h.OpenPageAsync();
        var path = _h.Installer(name);
        await viewModel.OpenAsync(path);
        Assert.True(viewModel.HasReport, viewModel.StatusMessage);
        return (viewModel, path);
    }

    private async Task<InstallerAnalysisViewModel> WithPayloadAsync()
    {
        _h.Service.Payload = InstallerAnalysisHarness.SamplePayload;
        var (viewModel, _) = await AnalyzedAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        await Wait.UntilAsync(() => viewModel.PayloadRows.Count == 5 && !viewModel.IsBusy, "the payload");
        return viewModel;
    }

    [Fact]
    public void TheConstructorReadsNothing()
    {
        var viewModel = _h.Create();
        Assert.Equal(0, _h.PreferenceReads);
        Assert.Empty(_h.Service.Calls);
        Assert.Empty(_h.Sandbox.Created);
        Assert.False(viewModel.HasReport);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task OpeningThePageReadsOnlyTheChoicesAndNoInstaller()
    {
        var recent = _h.Installer("old.msi");
        _h.Saved = new InstallerAnalysisSettings { SelectedTab = 5, RegistryFilter = "Run", StringsCategory = "Url", RecentFiles = [recent, @"C:\gone\missing.exe"] };
        var viewModel = await _h.OpenPageAsync();
        await viewModel.OnNavigatedToAsync();

        Assert.Equal(1, _h.PreferenceReads);
        Assert.Empty(_h.Service.Calls);
        Assert.Equal(InstallerAnalysisViewModel.RegistryTab, viewModel.SelectedTabIndex);
        Assert.Equal("Run", viewModel.RegistryFilter);
        Assert.Equal("Url", viewModel.StringsCategory);
        Assert.Equal([recent, @"C:\gone\missing.exe"], viewModel.RecentFiles);
    }

    [Fact]
    public async Task OpenAnalyzesTheFileOffTheCallingThreadAndFillsTheOverview()
    {
        var viewModel = await _h.OpenPageAsync();
        var path = _h.Installer();
        _h.Service.Reports[path] = FakeInstallerAnalysisService.Report(path, notes: ["The NSIS header uses bzip2 and is not decoded."],
            postPatch: new EffectiveDetection(ProductCode: "{AAAA}", DisplayName: "Contoso Tool", DisplayVersion: "1.2.4", ArpKey: @"HKLM\...\{AAAA}", Explanation: "The patch raises the version."));

        await viewModel.OpenAsync("  \"" + path + "\" ");

        Assert.Equal(path, viewModel.PathText);
        Assert.Equal(1, _h.Service.AnalyzeCalls);
        Assert.Equal("Analyzed setup.exe.", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
        Assert.Contains(viewModel.SourceRows, r => r.Name == "SHA-256" && r.Value == new string('A', 64));
        Assert.Contains(viewModel.SourceRows, r => r.Name == "Size" && r.Value == "1.2 MB (1,234,567 bytes)");
        Assert.Contains(viewModel.SourceRows, r => r.Name == "Signer" && r.Value == "CN=Contoso Ltd");
        Assert.Equal(["Confirmed", "Predicted", "Not known"], viewModel.DeploymentRows.Select(r => r.Confidence));
        Assert.Equal(["Per machine", "Per user"], viewModel.ModeRows.Select(m => m.Mode));
        Assert.Equal(2, viewModel.MetadataRows.Count);
        Assert.Equal("NSIS script", viewModel.MetadataRows[0].Section);
        Assert.True(viewModel.HasNotes);
        Assert.Equal(["The NSIS header uses bzip2 and is not decoded."], viewModel.Notes);
        Assert.True(viewModel.HasPostPatch);
        Assert.Contains(viewModel.PostPatchRows, r => r.Name == "Display version" && r.Value == "1.2.4");
        Assert.Equal("3 MSI properties. The MSI Properties tab lists them.", viewModel.MsiSummary);
        Assert.Equal(3, viewModel.MsiRows.Count);
        Assert.Equal([path], viewModel.RecentFiles);
        Assert.Single(viewModel.Breadcrumb);
        Assert.False(viewModel.CanGoBack);
        Assert.Contains("Reading the header.", _h.Service.Progress);
    }

    [Fact]
    public async Task ThePayloadRegistryAndStringsLoadWhenTheirTabFirstShowsAndOnlyOnce()
    {
        _h.Service.Payload = InstallerAnalysisHarness.SamplePayload;
        _h.Service.Registry = [FakeInstallerAnalysisService.Write("MsiRegistryTable", "HKLM", @"SOFTWARE\Contoso", "Version", "1.2.3", condition: "NOT Installed")];
        _h.Service.Strings = [FakeInstallerAnalysisService.Found("https://contoso.example/update", "Ascii", 4096, "Url"), FakeInstallerAnalysisService.Found(@"C:\Program Files\Contoso", "Utf16", 8192, "Path")];
        var (viewModel, _) = await AnalyzedAsync();
        Assert.Equal(0, _h.Service.PayloadCalls + _h.Service.RegistryCalls + _h.Service.StringsCalls);
        Assert.Equal(InstallerAnalysisViewModel.LoadOnShowMessage, viewModel.RegistryStatus);

        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.InnerTab;
        await Wait.UntilAsync(() => viewModel.InnerRows.Count == 2, "the inner installers");
        Assert.Equal(["vc_redist.x64.exe", "setup.msi"], viewModel.InnerRows.Select(r => r.Name));
        Assert.Equal("2 inner installers", viewModel.InnerStatus);
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.FilesTab;
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        Assert.Equal(1, _h.Service.PayloadCalls);
        Assert.Equal(4, viewModel.FileRows.Count);
        Assert.Equal([InstallerAnalysisViewModel.AllFolders, InstallerAnalysisText.TopLevel, "app", "redist"], viewModel.FileFolders);

        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.RegistryTab;
        await Wait.UntilAsync(() => viewModel.RegistryRows.Count == 1, "the registry rows");
        Assert.Equal("MSI Registry table", viewModel.RegistryRows[0].Origin);
        Assert.Equal(@"HKLM\SOFTWARE\Contoso", viewModel.RegistryRows[0].FullKey);
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.StringsTab;
        await Wait.UntilAsync(() => viewModel.StringRows.Count == 2, "the strings");
        Assert.Equal("UTF-16", viewModel.StringRows[1].Encoding);
        Assert.Equal("0x00001000", viewModel.StringRows[0].Offset);
        viewModel.StringsCategory = "Url";
        Assert.Single(viewModel.StringRows);
        Assert.Equal("1 of 2 strings", viewModel.StringsStatus);
        Assert.Equal(1, _h.Service.RegistryCalls);
        Assert.Equal(1, _h.Service.StringsCalls);
    }

    [Fact]
    public async Task TheFiltersNarrowEachListAndTheFolderChoiceNarrowsTheFileBrowser()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.PayloadFilter = "redist";
        Assert.Equal([@"redist\vc_redist.x64.exe"], viewModel.PayloadRows.Select(r => r.Path));
        Assert.Equal("1 of 5 entries", viewModel.PayloadStatus);
        viewModel.SelectedFolder = "app";
        Assert.Equal(["contoso.exe", "readme.txt"], viewModel.FileRows.Select(r => r.Name));
        viewModel.FileFilter = "read";
        Assert.Equal(["readme.txt"], viewModel.FileRows.Select(r => r.Name));
        Assert.Equal("1 of 4 files", viewModel.FileStatus);
        viewModel.MsiFilter = "productcode";
        Assert.Single(viewModel.MsiRows);
        Assert.Equal("1 of 3 properties", viewModel.MsiCount);
    }

    [Fact]
    public async Task AFilterKeepsTheSelectedEntryWhenItIsStillShown()
    {
        var viewModel = await WithPayloadAsync();
        var selected = viewModel.PayloadRows.Single(r => r.Name == "setup.msi");
        viewModel.SelectedPayload = selected;
        viewModel.PayloadFilter = "setup";
        Assert.Same(selected, viewModel.SelectedPayload);
        Assert.Contains(selected, viewModel.PayloadRows);
        viewModel.PayloadFilter = "redist";
        Assert.Null(viewModel.SelectedPayload);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DroppingSeveralItemsGivesAPlainMessageAndAnalyzesNothing(int count)
    {
        var viewModel = await _h.OpenPageAsync();
        var paths = Enumerable.Range(0, count).Select(i => _h.Installer($"f{i}.exe")).ToList();
        await viewModel.DropAsync(paths);
        Assert.Equal(InstallerAnalysisViewModel.OneFileMessage, viewModel.StatusMessage);
        Assert.Empty(_h.Service.Calls);
    }

    [Fact]
    public async Task DroppingAFolderGivesAPlainMessageAndAnalyzesNothing()
    {
        var viewModel = await _h.OpenPageAsync();
        await viewModel.DropAsync([_h.Files]);
        Assert.Equal(InstallerAnalysisViewModel.FolderMessage, viewModel.StatusMessage);
        Assert.Empty(_h.Service.Calls);
        Assert.Empty(viewModel.RecentFiles);
    }

    [Fact]
    public async Task DroppingOneFileAnalyzesItAndAMissingOrEmptyPathGivesAPlainMessage()
    {
        var viewModel = await _h.OpenPageAsync();
        var path = _h.Installer("dropped.msi");
        await viewModel.DropAsync([path]);
        Assert.True(viewModel.HasReport);
        Assert.Equal("Analyzed dropped.msi.", viewModel.StatusMessage);

        var missing = Path.Combine(_h.Files, "gone.exe");
        await viewModel.OpenAsync(missing);
        Assert.Equal(InstallerAnalysisViewModel.MissingFileMessage(missing), viewModel.StatusMessage);
        Assert.True(viewModel.HasReport);
        await viewModel.OpenAsync("   ");
        Assert.Equal(InstallerAnalysisViewModel.EnterPathMessage, viewModel.StatusMessage);
        await viewModel.OpenAsync("bad|name<>.exe");
        Assert.Equal(1, _h.Service.AnalyzeCalls);
    }

    [Fact]
    public async Task CancelStopsTheAnalysisAndShowsAPlainMessage()
    {
        var viewModel = await _h.OpenPageAsync();
        var path = _h.Installer();
        _h.Service.Gates[path] = new TaskCompletionSource();
        var run = viewModel.OpenAsync(path);
        await Wait.UntilAsync(() => _h.Service.AnalyzeCalls == 1 && viewModel.IsBusy, "the analysis to start");
        Assert.True(viewModel.CancelCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);
        await run;

        Assert.Equal(InstallerAnalysisViewModel.CancelledMessage, viewModel.StatusMessage);
        Assert.False(viewModel.HasReport);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("", viewModel.ProgressText);
        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task ASecondOpenDuringARunCancelsTheFirstAndItsLateResultIsDiscarded()
    {
        var viewModel = await _h.OpenPageAsync();
        var first = _h.Installer("first.exe");
        var second = _h.Installer("second.msi");
        var firstGate = new TaskCompletionSource();
        _h.Service.Gates[first] = firstGate;
        _h.Service.Reports[first] = FakeInstallerAnalysisService.Report(first, "NSIS");
        _h.Service.Reports[second] = FakeInstallerAnalysisService.Report(second, "MSI");

        var running = viewModel.OpenAsync(first);
        await Wait.UntilAsync(() => _h.Service.AnalyzeCalls == 1, "the first analysis");
        Assert.True(viewModel.AnalyzeCommand.CanExecute(null));
        await viewModel.OpenAsync(second);
        firstGate.SetResult();
        await running;

        Assert.Equal("MSI", viewModel.Report!.Type);
        Assert.Equal("second.msi", viewModel.Report.FileName);
        Assert.Equal("Analyzed second.msi.", viewModel.StatusMessage);
        Assert.Equal([second, first], viewModel.RecentFiles);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task AResultThatArrivesAfterLeavingThePageIsDiscarded()
    {
        var viewModel = await _h.OpenPageAsync();
        var path = _h.Installer();
        var gate = new TaskCompletionSource();
        _h.Service.Gates[path] = gate;
        var running = viewModel.OpenAsync(path);
        await Wait.UntilAsync(() => _h.Service.AnalyzeCalls == 1, "the analysis");

        await viewModel.OnNavigatedFromAsync();
        gate.TrySetResult();
        await running;

        Assert.False(viewModel.HasReport);
        Assert.Equal("", viewModel.StatusMessage);
        Assert.Empty(viewModel.SourceRows);
    }

    [Fact]
    public async Task AFailedAnalysisShowsAPlainMessageAndLogsTheTypeOnly()
    {
        _h.Service.AnalyzeFailure = new InvalidDataException(@"Bad header at C:\Users\op\secret\setup.exe");
        var viewModel = await _h.OpenPageAsync();
        await viewModel.OpenAsync(_h.Installer());
        Assert.Equal(InstallerAnalysisViewModel.FailedMessage, viewModel.StatusMessage);
        Assert.False(viewModel.HasReport);
        var entry = _h.Log.Entries.Single(e => e.Level == AppLogLevel.Warning);
        Assert.Contains("InvalidDataException", entry.Message);
        Assert.DoesNotContain("secret", entry.Message);
        Assert.DoesNotContain("Bad header", entry.Message);
    }

    [Fact]
    public async Task TheLogNamesTheFileButNeverItsFolderOrItsContent()
    {
        _h.Service.Payload = InstallerAnalysisHarness.SamplePayload;
        _h.Service.Registry = [FakeInstallerAnalysisService.Write("NsisScript", "HKLM", @"SOFTWARE\Secret", "Token", "s3cr3t-value")];
        _h.Service.Strings = [FakeInstallerAnalysisService.Found("password=hunter2", "Ascii", 1, "Other")];
        var (viewModel, path) = await AnalyzedAsync();
        foreach (var tab in new[] { InstallerAnalysisViewModel.PayloadTab, InstallerAnalysisViewModel.RegistryTab, InstallerAnalysisViewModel.StringsTab })
            viewModel.SelectedTabIndex = tab;
        await Wait.UntilAsync(() => viewModel.StringRows.Count == 1 && viewModel.RegistryRows.Count == 1 && viewModel.PayloadRows.Count == 5 && !viewModel.IsBusy, "every view");

        var log = string.Join("\n", _h.Log.Entries.Select(e => e.Message));
        Assert.All(_h.Log.Entries, e => Assert.Equal(InstallerAnalysisViewModel.LogArea, e.Area));
        Assert.Contains("file=setup.exe", log);
        Assert.Contains("type=NSIS", log);
        Assert.Contains("entries=5", log);
        Assert.DoesNotContain(_h.Files, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s3cr3t", log);
        Assert.DoesNotContain("hunter2", log);
        Assert.DoesNotContain("vc_redist", log);
        Assert.DoesNotContain(@"SOFTWARE\Secret", log);
    }

    [Fact]
    public async Task AnalyzeSelectedDrillsIntoAnInnerInstallerAndBackReturnsWithTheParentData()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.InnerTab;
        viewModel.SelectedInner = viewModel.InnerRows.Single(r => r.Name == "setup.msi");
        _h.Service.ExtractTo = (entry, folder) =>
        {
            var target = Path.Combine(folder, InstallerAnalysisText.EntryName(entry.Path));
            File.WriteAllText(target, "inner");
            return target;
        };

        await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);

        var session = Assert.Single(_h.Sandbox.Created);
        var extracted = Path.Combine(session, "setup.msi");
        Assert.Equal(("analyze:" + extracted), _h.Service.Calls.Last(c => c.StartsWith("analyze:")));
        Assert.Equal(1, viewModel.Depth);
        Assert.Equal(["setup.exe", "setup.msi"], viewModel.Breadcrumb.Select(b => b.Name));
        Assert.True(viewModel.Breadcrumb[1].IsCurrent);
        Assert.True(viewModel.CanGoBack);
        Assert.Equal("setup.msi", viewModel.Report!.FileName);
        Assert.Equal("Analyzed setup.msi, level 1 of 5.", viewModel.StatusMessage);
        await Wait.UntilAsync(() => _h.Service.PayloadCalls == 2 && !viewModel.IsBusy, "the inner payload");

        viewModel.BackCommand.Execute(null);
        Assert.Equal(0, viewModel.Depth);
        Assert.Equal("setup.exe", viewModel.Report!.FileName);
        Assert.Equal(InstallerAnalysisViewModel.InnerTab, viewModel.SelectedTabIndex);
        Assert.Equal(2, viewModel.InnerRows.Count);
        Assert.Equal(2, _h.Service.PayloadCalls);
        Assert.Equal("Back to setup.exe.", viewModel.StatusMessage);
        Assert.False(viewModel.CanGoBack);
    }

    [Fact]
    public async Task TheDrillDownStopsAtTheDepthLimitWithAPlainMessageAndTheBreadcrumbJumpsBack()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.InnerTab;
        for (var level = 1; level <= 5; level++)
        {
            await Wait.UntilAsync(() => viewModel.InnerRows.Count == 2 && !viewModel.IsBusy, "the inner installers");
            viewModel.SelectedInner = viewModel.InnerRows[1];
            await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);
            Assert.Equal(level, viewModel.Depth);
        }
        await Wait.UntilAsync(() => viewModel.InnerRows.Count == 2 && !viewModel.IsBusy, "the inner installers");
        viewModel.SelectedInner = viewModel.InnerRows[1];
        var analyses = _h.Service.AnalyzeCalls;

        await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);

        Assert.Equal(InstallerAnalysisViewModel.DepthLimitMessage(5), viewModel.StatusMessage);
        Assert.Equal(5, viewModel.Depth);
        Assert.Equal(analyses, _h.Service.AnalyzeCalls);
        Assert.Equal(6, viewModel.Breadcrumb.Count);

        viewModel.GoToLevelCommand.Execute(viewModel.Breadcrumb[2]);
        Assert.Equal(2, viewModel.Depth);
        Assert.Equal(3, viewModel.Breadcrumb.Count);
        viewModel.GoToLevelCommand.Execute(viewModel.Breadcrumb[2]);
        Assert.Equal(2, viewModel.Depth);
    }

    [Fact]
    public async Task AnalyzeSelectedOnAFolderOrWithoutASelectionGivesAPlainMessage()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedPayload = null;
        await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.SelectEntryMessage, viewModel.StatusMessage);
        viewModel.SelectedPayload = viewModel.PayloadRows.Single(r => r.Entry.IsDirectory);
        await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.FolderEntryMessage, viewModel.StatusMessage);
        Assert.Empty(_h.Sandbox.Created);
        Assert.Equal(0, viewModel.Depth);
    }

    [Fact]
    public async Task ExtractToTempWritesIntoTheSessionAndTheHashIsComputedOnDemandFromThatCopy()
    {
        var viewModel = await WithPayloadAsync();
        var row = viewModel.PayloadRows.Single(r => r.Name == "contoso.exe");

        await viewModel.ExtractToTempCommand.ExecuteAsync(row);
        var session = Assert.Single(_h.Sandbox.Created);
        Assert.Equal(session, _h.Service.Extractions.Single().Folder);
        Assert.Equal(session, viewModel.LastExtractFolder);
        Assert.StartsWith("Extracted contoso.exe to the temporary analysis folder.", viewModel.StatusMessage);
        Assert.DoesNotContain(_h.Service.Calls, c => c.StartsWith("hash:"));

        await viewModel.HashCommand.ExecuteAsync(row);
        Assert.Single(_h.Service.Extractions);
        Assert.Equal("hash:" + Path.Combine(session, "contoso.exe"), _h.Service.Calls.Last());
        Assert.Equal($"SHA-256 of contoso.exe: {new string('F', 64)}", viewModel.StatusMessage);

        viewModel.OpenExtractFolderCommand.Execute(null);
        Assert.Equal([session], _h.Prompts.OpenedFolders);
    }

    [Fact]
    public async Task ExtractToTempShowsThePlainServiceMessageAndNeverTheTextOfAnotherFailure()
    {
        var viewModel = await WithPayloadAsync();
        var row = viewModel.PayloadRows.Single(r => r.Name == "contoso.exe");
        _h.Service.ExtractFailure = new InstallerAnalysisException("The entry is larger than the extraction limit; it was not extracted.");
        await viewModel.ExtractToTempCommand.ExecuteAsync(row);
        Assert.Equal("The entry is larger than the extraction limit; it was not extracted.", viewModel.StatusMessage);
        _h.Service.ExtractFailure = new IOException(@"disk full at C:\Users\op\secret");
        await viewModel.ExtractToTempCommand.ExecuteAsync(row);
        Assert.Equal(InstallerAnalysisViewModel.ExtractFailedMessage, viewModel.StatusMessage);
        Assert.Null(viewModel.LastExtractFolder);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("secret") || e.Message.Contains("extraction limit"));
    }

    [Fact]
    public async Task TheHashCanBeCancelled()
    {
        var viewModel = await WithPayloadAsync();
        _h.Service.HashGate = new TaskCompletionSource();
        var row = viewModel.PayloadRows.Single(r => r.Name == "contoso.exe");
        var hashing = viewModel.HashCommand.ExecuteAsync(row);
        await Wait.UntilAsync(() => _h.Service.Calls.Any(c => c.StartsWith("hash:")), "the hash");
        viewModel.CancelCommand.Execute(null);
        await hashing;
        Assert.Equal("The SHA-256 computation was cancelled.", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task CopyNameAndPathUseTheSelectionOfTheShownTabAndAFullClipboardSaysSo()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.FilesTab;
        viewModel.SelectedFile = viewModel.FileRows.Single(r => r.Name == "readme.txt");
        viewModel.CopyNameCommand.Execute(null);
        viewModel.CopyPathCommand.Execute(null);
        Assert.Equal(["readme.txt", @"app\readme.txt"], _h.Prompts.Copied);
        _h.Prompts.CopyFails = true;
        viewModel.CopyNameCommand.Execute(null);
        Assert.Equal(InstallerAnalysisViewModel.ClipboardBusyMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task ExtractPayloadRefusesAFolderInsideTheSandbox()
    {
        var viewModel = await WithPayloadAsync();
        var inside = Directory.CreateDirectory(Path.Combine(_h.SandboxRoot, "s9", "deep")).FullName;
        _h.Prompts.Folder = inside;
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.SandboxFolderMessage, viewModel.StatusMessage);
        _h.Prompts.Folder = _h.SandboxRoot;
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.SandboxFolderMessage, viewModel.StatusMessage);
        Assert.Empty(_h.Service.Extractions);
    }

    [Fact]
    public async Task ExtractPayloadRefusesAJunctionAndAMissingFolder()
    {
        var viewModel = await WithPayloadAsync();
        var target = Directory.CreateDirectory(Path.Combine(_h.Root, "target")).FullName;
        var link = Path.Combine(_h.Root, "link");
        DetectionTester.Junctions.Create(link, target);
        _h.Prompts.Folder = link;
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.LinkedFolderMessage, viewModel.StatusMessage);
        _h.Prompts.Folder = Directory.CreateDirectory(Path.Combine(link, "below")).FullName;
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.LinkedFolderMessage, viewModel.StatusMessage);
        _h.Prompts.Folder = Path.Combine(_h.Root, "missing");
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.MissingFolderMessage, viewModel.StatusMessage);
        Assert.Empty(_h.Service.Extractions);
    }

    [Fact]
    public async Task ExtractPayloadAsksTheServiceForEveryEntryAndOpensTheNewPayloadFolder()
    {
        var viewModel = await WithPayloadAsync();
        var folder = Directory.CreateDirectory(Path.Combine(_h.Root, "out")).FullName;
        _h.Prompts.Folder = folder;
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);

        var call = Assert.Single(_h.Service.PayloadExtractions);
        Assert.Null(call.Entries);
        Assert.Equal(folder, call.Target);
        Assert.Empty(_h.Service.Extractions);
        Assert.Empty(_h.Sandbox.Created);
        var payloadFolder = Path.Combine(folder, "setup payload");
        Assert.True(File.Exists(Path.Combine(payloadFolder, @"app\contoso.exe")));
        Assert.Equal($"Extracted 4 files (28 MB) to {payloadFolder}.", viewModel.StatusMessage);
        Assert.Equal(payloadFolder, viewModel.LastExtractFolder);
        viewModel.OpenExtractFolderCommand.Execute(null);
        Assert.Equal([payloadFolder], _h.Prompts.OpenedFolders);
    }

    [Fact]
    public async Task ExtractPayloadNeedsNoPayloadListAndNamesTheSkippedEntries()
    {
        var (viewModel, _) = await AnalyzedAsync();
        _h.Service.Payload = InstallerAnalysisHarness.SamplePayload;
        _h.Service.PayloadExtractSkipped = [@"..\evil.dll: The path leaves the target folder.", "b: The entry is a symbolic link."];
        _h.Prompts.Folder = Directory.CreateDirectory(Path.Combine(_h.Root, "out")).FullName;
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);

        Assert.Equal(0, _h.Service.PayloadCalls);
        Assert.EndsWith(@" 2 entries were skipped. The first: ..\evil.dll: The path leaves the target folder.", viewModel.StatusMessage);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("evil"));

        _h.Service.Payload = [];
        _h.Service.PayloadExtractSkipped = [];
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.NoPayloadMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task ExtractPayloadShowsTheServiceMessageOfAPlainFailureAndNeverTheTextOfAnyOtherFailure()
    {
        var viewModel = await WithPayloadAsync();
        _h.Prompts.Folder = Directory.CreateDirectory(Path.Combine(_h.Root, "out")).FullName;
        _h.Service.PayloadExtractFailure = new InstallerAnalysisException("7-Zip is not installed; the payload of this installer type is listed with 7-Zip. Install 7-Zip to list and extract it.");
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal("7-Zip is not installed; the payload of this installer type is listed with 7-Zip. Install 7-Zip to list and extract it.", viewModel.StatusMessage);

        _h.Service.PayloadExtractFailure = new IOException(@"disk full at C:\Users\op\secret");
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.PayloadExtractFailedMessage, viewModel.StatusMessage);
        Assert.Null(viewModel.LastExtractFolder);
        Assert.Contains(_h.Log.Entries, e => e.Message.Contains("IOException"));
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("secret") || e.Message.Contains("7-Zip is not installed"));
    }

    [Fact]
    public async Task ExtractPayloadCanBeCancelled()
    {
        var viewModel = await WithPayloadAsync();
        _h.Prompts.Folder = Directory.CreateDirectory(Path.Combine(_h.Root, "out")).FullName;
        _h.Service.PayloadExtractGate = new TaskCompletionSource();
        var running = viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _h.Service.PayloadExtractions.Count == 1 && viewModel.IsBusy, "the extraction");
        Assert.Equal("Extracting the payload of setup.exe.", viewModel.ProgressText);
        viewModel.CancelCommand.Execute(null);
        await running;
        Assert.Equal("The extraction was cancelled.", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ExtractCopiesTheSelectedInnerInstallerToTheChosenFolderAndACancelledPickerDoesNothing()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.InnerTab;
        viewModel.SelectedInner = viewModel.InnerRows.Single(r => r.Name == "setup.msi");
        _h.Prompts.Folder = null;
        await viewModel.ExtractInnerCommand.ExecuteAsync(null);
        Assert.Empty(_h.Service.PayloadExtractions);

        var folder = Directory.CreateDirectory(Path.Combine(_h.Root, "inner-out")).FullName;
        _h.Prompts.Folder = folder;
        await viewModel.ExtractInnerCommand.ExecuteAsync(null);
        var call = Assert.Single(_h.Service.PayloadExtractions);
        Assert.Equal(folder, call.Target);
        Assert.Same(viewModel.SelectedInner.Entry, Assert.Single(call.Entries!));
        Assert.Empty(_h.Service.Extractions);
        Assert.Empty(_h.Sandbox.Created);
        var payloadFolder = Path.Combine(folder, "setup payload");
        Assert.True(File.Exists(Path.Combine(payloadFolder, "setup.msi")));
        Assert.Equal($"Copied setup.msi to {payloadFolder}.", viewModel.StatusMessage);
        Assert.Equal(payloadFolder, viewModel.LastExtractFolder);
        Assert.Equal(["Copy setup.msi to a folder", "Copy setup.msi to a folder"], _h.Prompts.FolderTitles);
    }

    [Fact]
    public async Task ExtractOfAnInnerInstallerThatTheServiceSkipsSaysSoAndAFailureShowsOnlyAPlainMessage()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.InnerTab;
        viewModel.SelectedInner = viewModel.InnerRows.Single(r => r.Name == "setup.msi");
        _h.Prompts.Folder = Directory.CreateDirectory(Path.Combine(_h.Root, "inner-out")).FullName;
        _h.Service.PayloadExtractWritesNothing = true;
        _h.Service.PayloadExtractSkipped = ["setup.msi: The name is a device name."];
        await viewModel.ExtractInnerCommand.ExecuteAsync(null);
        Assert.Equal("setup.msi was not copied. 1 entry was skipped: setup.msi: The name is a device name.", viewModel.StatusMessage);
        Assert.Null(viewModel.LastExtractFolder);

        _h.Service.PayloadExtractFailure = new UnauthorizedAccessException(@"Access to C:\Users\op\secret denied");
        await viewModel.ExtractInnerCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.ExtractFailedMessage, viewModel.StatusMessage);
        _h.Service.PayloadExtractFailure = new InstallerAnalysisException("The target folder is a junction or symbolic link; choose another folder.");
        await viewModel.ExtractInnerCommand.ExecuteAsync(null);
        Assert.Equal("The target folder is a junction or symbolic link; choose another folder.", viewModel.StatusMessage);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("secret"));
    }

    [Fact]
    public async Task APlainServiceFailureIsShownAsItIsOnEveryViewAndAnyOtherFailureGivesTheFixedMessage()
    {
        var viewModel = await _h.OpenPageAsync();
        _h.Service.AnalyzeFailure = new InstallerAnalysisException("The file is larger than the analysis limit of 8 GB.");
        await viewModel.OpenAsync(_h.Installer());
        Assert.Equal("The file is larger than the analysis limit of 8 GB.", viewModel.StatusMessage);

        _h.Service.AnalyzeFailure = null;
        _h.Service.PayloadFailure = new InstallerAnalysisException("7-Zip cannot open this file as an archive; it has no payload to list.");
        _h.Service.RegistryFailure = new InvalidDataException(@"bad table at C:\Users\op\secret");
        await viewModel.OpenAsync(_h.Installer());
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.RegistryTab;
        await Wait.UntilAsync(() => !viewModel.IsBusy && viewModel.RegistryStatus.Contains("could not"), "the failures");
        Assert.Equal("7-Zip cannot open this file as an archive; it has no payload to list.", viewModel.InnerStatus);
        Assert.Equal("The registry settings could not be read. The log has the details.", viewModel.RegistryStatus);
        Assert.DoesNotContain(_h.Log.Entries, e => e.Message.Contains("secret") || e.Message.Contains("7-Zip cannot"));
    }

    [Fact]
    public async Task TheRegistryTabShowsTheReadNotesAndTheActionViewAndSectionOfEachChange()
    {
        _h.Service.Registry =
        [
            FakeInstallerAnalysisService.Write("NsisScript", "HKLM", @"Software\Contoso", "Version", "1.2.3") with { View = "32", Section = "Main" },
            FakeInstallerAnalysisService.Write("MsiRegistryTable", "HKCU", @"Software\Contoso\Old", "", "") with { Action = "delete key", Section = "RegComponent" }
        ];
        _h.Service.RegistryNotes = ["The NSIS header uses bzip2 and is not decoded; registry changes may be missing."];
        var (viewModel, _) = await AnalyzedAsync();
        Assert.False(viewModel.HasRegistryNotes);
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.RegistryTab;
        await Wait.UntilAsync(() => viewModel.RegistryRows.Count == 2 && !viewModel.IsBusy, "the registry rows");

        Assert.True(viewModel.HasRegistryNotes);
        Assert.Equal(["The NSIS header uses bzip2 and is not decoded; registry changes may be missing."], viewModel.RegistryNotes);
        Assert.Equal(["Write", "Delete key"], viewModel.RegistryRows.Select(r => r.Action));
        Assert.Equal(["32-bit", ""], viewModel.RegistryRows.Select(r => r.View));
        Assert.Equal(["Main", "RegComponent"], viewModel.RegistryRows.Select(r => r.Section));
        viewModel.RegistryFilter = "regcomp";
        Assert.Single(viewModel.RegistryRows);
        viewModel.RegistryFilter = "";
        viewModel.CopyRegistryRowsCommand.Execute(null);
        Assert.Equal(string.Join('\t', "NSIS script", "Write", @"HKLM\Software\Contoso", "Version", "1.2.3", "REG_SZ", "32-bit", "Main", ""),
            _h.Prompts.Copied.Single().Split(Environment.NewLine)[0]);

        _h.Service.RegistryNotes = [];
        await viewModel.OpenAsync(_h.Installer("next.exe"));
        await Wait.UntilAsync(() => !viewModel.IsBusy, "the registry read of the next installer");
        Assert.False(viewModel.HasRegistryNotes);
    }

    [Fact]
    public async Task TheOverviewShowsTheTypeNameExecutionLevelDetectionHintModeDetailsAndPatchBase()
    {
        var viewModel = await _h.OpenPageAsync();
        var path = _h.Installer("update.msp");
        _h.Service.Reports[path] = FakeInstallerAnalysisService.Report(path, "MSP",
            postPatch: new EffectiveDetection(ProductCode: "{AAAA}", DisplayName: "Contoso Tool", DisplayVersion: "1.2.4", ArpKey: @"HKLM\...\{AAAA}", Explanation: "From the patch.")
            { BaseVersion = "1.2.0", PatchFile = "update.msp" });
        await viewModel.OpenAsync(path);

        Assert.Equal("Nullsoft Scriptable Install System", viewModel.SourceRows.Single(r => r.Name == "Type").Value);
        Assert.Equal("requireAdministrator", viewModel.SourceRows.Single(r => r.Name == "Requested execution level").Value);
        Assert.StartsWith(@"Registry: HKLM\SOFTWARE", viewModel.SourceRows.Single(r => r.Name == "Detection hint").Value);
        Assert.Equal(["Yes", "No"], viewModel.ModeRows.Select(m => m.Default));
        Assert.Equal(["64-bit", ""], viewModel.ModeRows.Select(m => m.View));
        Assert.Equal(["setup.exe /S /AllUsers", "setup.exe /S /CurrentUser"], viewModel.ModeRows.Select(m => m.InstallCommand));
        Assert.Contains(viewModel.PostPatchRows, r => r.Name == "Base version" && r.Value == "1.2.0");
        Assert.Contains(viewModel.PostPatchRows, r => r.Name == "Patch file" && r.Value == "update.msp");

        var plain = _h.Installer("plain.exe");
        var report = FakeInstallerAnalysisService.Report(plain);
        _h.Service.Reports[plain] = report with { TypeName = "", DetectionHint = "", RequestedExecutionLevel = "" };
        await viewModel.OpenAsync(plain);
        Assert.Equal("NSIS", viewModel.SourceRows.Single(r => r.Name == "Type").Value);
        Assert.DoesNotContain(viewModel.SourceRows, r => r.Name is "Requested execution level" or "Detection hint");
    }

    [Fact]
    public async Task CopyJsonNeverAsksForKeyMaterialAndCopySummaryCopiesTheSummary()
    {
        var (viewModel, _) = await AnalyzedAsync("app.intunewin");
        viewModel.CopyJsonCommand.Execute(null);
        viewModel.CopySummaryCommand.Execute(null);
        Assert.Equal([false], _h.Writer.JsonKeyMaterial);
        Assert.Equal(["{\"FileName\":\"app.intunewin\"}", "Summary of app.intunewin"], _h.Prompts.Copied);
        Assert.Equal("Copied the summary.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task TheExportsReadTheViewsTheyNeedAndTheOpenFolderButtonOpensTheLastReportFolder()
    {
        _h.Service.Payload = InstallerAnalysisHarness.SamplePayload;
        _h.Service.Registry = [FakeInstallerAnalysisService.Write("MsiRegistryTable", "HKLM", @"SOFTWARE\Contoso", "Version", "1.2.3")];
        var (viewModel, _) = await AnalyzedAsync();
        Assert.False(viewModel.OpenReportsFolderCommand.CanExecute(null));

        var csv = viewModel.ExportCsvCommand.ExecuteAsync(null);
        var html = viewModel.ExportHtmlCommand.ExecuteAsync(null);
        await Task.WhenAll(csv, html);

        Assert.Equal([("csv", 0, 1), ("html", 5, 1)], _h.Writer.Written.OrderBy(w => w.Format));
        Assert.Equal(1, _h.Service.RegistryCalls);
        Assert.Equal(_h.Reports, viewModel.LastReportFolder);
        Assert.StartsWith("Saved the ", viewModel.StatusMessage);
        Assert.Empty(_h.Prompts.OpenedFolders);
        viewModel.OpenReportsFolderCommand.Execute(null);
        Assert.Equal([_h.Reports], _h.Prompts.OpenedFolders);
    }

    [Fact]
    public async Task AFailedExportShowsAPlainMessageAndTheExportsNeedAnAnalysis()
    {
        var viewModel = await _h.OpenPageAsync();
        Assert.False(viewModel.ExportCsvCommand.CanExecute(null));
        Assert.False(viewModel.ExportHtmlCommand.CanExecute(null));
        Assert.False(viewModel.CopySummaryCommand.CanExecute(null));
        Assert.False(viewModel.CopyJsonCommand.CanExecute(null));
        await viewModel.ExportCsvCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.NoAnalysisMessage, viewModel.StatusMessage);
        viewModel.CopyJsonCommand.Execute(null);
        Assert.Equal(InstallerAnalysisViewModel.NoAnalysisMessage, viewModel.StatusMessage);

        await viewModel.OpenAsync(_h.Installer());
        Assert.True(viewModel.ExportCsvCommand.CanExecute(null));
        Assert.True(viewModel.ExportHtmlCommand.CanExecute(null));
        Assert.True(viewModel.CopySummaryCommand.CanExecute(null));
        Assert.True(viewModel.CopyJsonCommand.CanExecute(null));
        _h.Writer.Failure = new UnauthorizedAccessException(@"Access to C:\Users\op\AppPackager\Reports denied");
        await viewModel.ExportHtmlCommand.ExecuteAsync(null);
        Assert.Equal("The HTML report could not be written. The log has the details.", viewModel.StatusMessage);
        Assert.Null(viewModel.LastReportFolder);
    }

    [Fact]
    public async Task LeavingThePageCancelsTheWorkReleasesTheSessionAndEveryResult()
    {
        var viewModel = await WithPayloadAsync();
        await viewModel.ExtractToTempCommand.ExecuteAsync(viewModel.PayloadRows.Single(r => r.Name == "contoso.exe"));
        var session = Assert.Single(_h.Sandbox.Created);
        _h.Service.HashGate = new TaskCompletionSource();
        var hashing = viewModel.HashCommand.ExecuteAsync(viewModel.PayloadRows.Single(r => r.Name == "contoso.exe"));
        await Wait.UntilAsync(() => _h.Service.Calls.Any(c => c.StartsWith("hash:")), "the hash");

        await viewModel.OnNavigatedFromAsync();
        await hashing;

        Assert.Equal([session], _h.Sandbox.Released);
        Assert.False(Directory.Exists(session));
        Assert.False(viewModel.HasReport);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(viewModel.PayloadRows);
        Assert.Empty(viewModel.SourceRows);
        Assert.Empty(viewModel.Breadcrumb);
        Assert.Null(viewModel.LastExtractFolder);
        Assert.Equal("", viewModel.StatusMessage);
        Assert.Single(viewModel.RecentFiles);
    }

    [Fact]
    public async Task OpeningAnotherInstallerReleasesTheSessionOfThePreviousOne()
    {
        var viewModel = await WithPayloadAsync();
        await viewModel.ExtractToTempCommand.ExecuteAsync(viewModel.PayloadRows.Single(r => r.Name == "contoso.exe"));
        var session = Assert.Single(_h.Sandbox.Created);
        await viewModel.OpenAsync(_h.Installer("next.msi"));
        await Wait.UntilAsync(() => _h.Sandbox.Released.Count == 1, "the release");
        Assert.Equal([session], _h.Sandbox.Released);
    }

    [Fact]
    public async Task TheRecentFilesKeepTenNewestFirstAMissingOneSaysSoAndClearEmptiesThem()
    {
        var viewModel = await _h.OpenPageAsync();
        var paths = Enumerable.Range(0, 12).Select(i => _h.Installer($"app{i}.exe")).ToList();
        foreach (var path in paths) await viewModel.OpenAsync(path);
        await viewModel.OpenAsync(paths[5]);

        Assert.Equal(10, viewModel.RecentFiles.Count);
        Assert.Equal(paths[5], viewModel.RecentFiles[0]);
        Assert.Equal(paths[11], viewModel.RecentFiles[1]);
        Assert.DoesNotContain(paths[0], viewModel.RecentFiles);

        File.Delete(paths[9]);
        viewModel.SelectedRecent = paths[9];
        var calls = _h.Service.AnalyzeCalls;
        await viewModel.OpenRecentCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.MissingFileMessage(paths[9]), viewModel.StatusMessage);
        Assert.Equal(calls, _h.Service.AnalyzeCalls);

        viewModel.SelectedRecent = paths[8];
        await viewModel.OpenRecentCommand.ExecuteAsync(null);
        Assert.Equal("app8.exe", viewModel.Report!.FileName);

        viewModel.ClearRecentCommand.Execute(null);
        Assert.Empty(viewModel.RecentFiles);
        await viewModel.OnNavigatedFromAsync();
        Assert.Empty(_h.Saved!.RecentFiles!);
    }

    [Fact]
    public async Task TheTabFiltersAndRecentFilesAreSavedAndComeBackOnTheNextVisit()
    {
        var viewModel = await _h.OpenPageAsync();
        var path = _h.Installer();
        await viewModel.OpenAsync(path);
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.StringsTab;
        viewModel.StringsFilter = "http";
        viewModel.StringsCategory = "Url";
        viewModel.RegistryFilter = "Uninstall";
        await viewModel.OnNavigatedFromAsync();

        Assert.Equal(InstallerAnalysisViewModel.StringsTab, _h.Saved!.SelectedTab);
        Assert.Equal("http", _h.Saved.StringsFilter);
        Assert.Equal("Url", _h.Saved.StringsCategory);
        Assert.Equal("Uninstall", _h.Saved.RegistryFilter);
        Assert.Equal([path], _h.Saved.RecentFiles);

        var next = await _h.OpenPageAsync();
        Assert.Equal(InstallerAnalysisViewModel.StringsTab, next.SelectedTabIndex);
        Assert.Equal("Url", next.StringsCategory);
        Assert.Equal([path], next.RecentFiles);
        Assert.False(next.HasReport);
    }

    [Fact]
    public async Task VisitingAndLeavingWithoutAChangeWritesNothing()
    {
        var viewModel = await _h.OpenPageAsync();
        await viewModel.OnNavigatedFromAsync();
        Assert.Equal(0, _h.SaveCount);
        Assert.Null(_h.Saved);
    }

    [Fact]
    public async Task ASettingsFileFromBeforeThePageLoadsWithoutTheObjectAndOutOfRangeValuesFallBack()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "CompanyName": "Contoso", "DetectionTester": { "Hive": "HKCU" } }""")!;
        Assert.Null(settings.InstallerAnalysis);
        Assert.DoesNotContain("InstallerAnalysis", JsonSerializer.Serialize(settings));

        var stored = JsonSerializer.Deserialize<AppSettings>("""{ "InstallerAnalysis": { "SelectedTab": 42, "StringsCategory": "nope", "RecentFiles": null } }""")!;
        _h.Saved = stored.InstallerAnalysis;
        var viewModel = await _h.OpenPageAsync();
        Assert.Equal(InstallerAnalysisViewModel.OverviewTab, viewModel.SelectedTabIndex);
        Assert.Equal(InstallerAnalysisViewModel.AllCategories, viewModel.StringsCategory);
        Assert.Empty(viewModel.RecentFiles);
    }

    [Fact]
    public async Task AFailedPayloadListGivesAPlainMessageOnTheThreeTabsAndTheNextShowTriesAgain()
    {
        _h.Service.PayloadFailure = new InvalidOperationException("7z.exe crashed reading C:\\x");
        var (viewModel, _) = await AnalyzedAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        await Wait.UntilAsync(() => !viewModel.IsBusy && viewModel.PayloadStatus.Contains("could not"), "the failure");
        Assert.Equal("The payload could not be listed. The log has the details.", viewModel.InnerStatus);
        Assert.Equal(viewModel.PayloadStatus, viewModel.FileStatus);
    }

    [Fact]
    public async Task ACancelledTabLoadSaysSoAndLoadsAgainWhenTheTabOpensAgain()
    {
        _h.Service.Payload = InstallerAnalysisHarness.SamplePayload;
        _h.Service.PayloadGate = new TaskCompletionSource();
        var (viewModel, _) = await AnalyzedAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        await Wait.UntilAsync(() => _h.Service.PayloadCalls == 1 && viewModel.IsBusy, "the payload list");
        Assert.Equal("Listing the payload.", viewModel.PayloadStatus);
        viewModel.CancelCommand.Execute(null);
        await Wait.UntilAsync(() => !viewModel.IsBusy, "the cancel");
        Assert.Equal("Listing the payload was cancelled. Open the tab again to list it.", viewModel.PayloadStatus);

        _h.Service.PayloadGate = null;
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.OverviewTab;
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        await Wait.UntilAsync(() => viewModel.PayloadRows.Count == 5, "the second list");
        Assert.Equal(2, _h.Service.PayloadCalls);
    }

    [Fact]
    public async Task ReanalyzeStartsAgainFromTheInstallerTheUserOpened()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedPayload = viewModel.PayloadRows.Single(r => r.Name == "setup.msi");
        await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.Depth);
        await viewModel.ReanalyzeCommand.ExecuteAsync(null);
        Assert.Equal(0, viewModel.Depth);
        Assert.Equal("setup.exe", viewModel.Report!.FileName);
    }

    [Fact]
    public async Task TheRegistryAndStringsCopyCommandsCopyTheSelectionOrTheShownRows()
    {
        _h.Service.Registry =
        [
            FakeInstallerAnalysisService.Write("MsiRegistryTable", "HKLM", @"SOFTWARE\Contoso", "", "1", "REG_DWORD"),
            FakeInstallerAnalysisService.Write("NsisScript", "HKCU", @"Software\Contoso", "Path", @"C:\Contoso")
        ];
        _h.Service.Strings = [FakeInstallerAnalysisService.Found("one", "Ascii", 0, "Other"), FakeInstallerAnalysisService.Found("two", "Ascii", 4, "Other")];
        var (viewModel, _) = await AnalyzedAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.RegistryTab;
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.StringsTab;
        await Wait.UntilAsync(() => viewModel.RegistryRows.Count == 2 && viewModel.StringRows.Count == 2, "the views");

        Assert.Equal("(Default)", viewModel.RegistryRows[0].Name);
        viewModel.SelectedRegistry = viewModel.RegistryRows[1];
        viewModel.CopyRegistryKeyCommand.Execute(null);
        viewModel.CopyRegistryValueCommand.Execute(null);
        viewModel.SelectedString = viewModel.StringRows[1];
        viewModel.CopyStringCommand.Execute(null);
        viewModel.CopyStringsCommand.Execute(null);
        viewModel.CopyRegistryRowsCommand.Execute(null);

        Assert.Equal(@"HKCU\Software\Contoso", _h.Prompts.Copied[0]);
        Assert.Equal(@"C:\Contoso", _h.Prompts.Copied[1]);
        Assert.Equal("two", _h.Prompts.Copied[2]);
        Assert.Equal("one" + Environment.NewLine + "two", _h.Prompts.Copied[3]);
        Assert.Equal(2, _h.Prompts.Copied[4].Split(Environment.NewLine).Length);
        Assert.Equal("Copied 2 registry writes.", viewModel.StatusMessage);
    }
}
