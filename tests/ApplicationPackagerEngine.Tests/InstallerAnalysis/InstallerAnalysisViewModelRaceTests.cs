using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>Selections across filters, late results, exports that wait for data, and the files a drill-down leaves.</summary>
public sealed class InstallerAnalysisViewModelRaceTests : IDisposable
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
    public async Task ARegistryOrStringFilterKeepsTheSelectedRowWhenItIsStillShown()
    {
        var same = FakeInstallerAnalysisService.Write("NsisScript", "HKCU", @"Software\Contoso", "Path", @"C:\Contoso");
        _h.Service.Registry = [FakeInstallerAnalysisService.Write("MsiRegistryTable", "HKLM", @"SOFTWARE\Contoso", "Version", "1"), same, same with { }];
        _h.Service.Strings = [FakeInstallerAnalysisService.Found("alpha one", "Ascii", 0, "Other"), FakeInstallerAnalysisService.Found("alpha one", "Ascii", 0, "Other"),
            FakeInstallerAnalysisService.Found("beta", "Ascii", 8, "Other")];
        var (viewModel, _) = await AnalyzedAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.RegistryTab;
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.StringsTab;
        await Wait.UntilAsync(() => viewModel.RegistryRows.Count == 3 && viewModel.StringRows.Count == 3 && !viewModel.IsBusy, "the views");

        Assert.NotSame(viewModel.RegistryRows[1], viewModel.RegistryRows[2]);
        Assert.NotEqual(viewModel.RegistryRows[1], viewModel.RegistryRows[2]);
        var registry = viewModel.RegistryRows[2];
        viewModel.SelectedRegistry = registry;
        viewModel.RegistryFilter = "Contoso";
        Assert.Same(registry, viewModel.SelectedRegistry);
        Assert.Contains(viewModel.RegistryRows, r => ReferenceEquals(r, registry));
        viewModel.CopyRegistryValueCommand.Execute(null);
        Assert.Equal([@"C:\Contoso"], _h.Prompts.Copied);

        var text = viewModel.StringRows[1];
        viewModel.SelectedString = text;
        viewModel.StringsFilter = "alpha";
        Assert.Same(text, viewModel.SelectedString);
        Assert.Contains(viewModel.StringRows, r => ReferenceEquals(r, text));
        viewModel.StringsFilter = "beta";
        Assert.Null(viewModel.SelectedString);
    }

    [Fact]
    public async Task APayloadFilterRebuildsOnlyThePayloadListAndTheFolderChoiceStays()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.FilesTab;
        viewModel.SelectedFolder = "app";
        var files = viewModel.FileRows;
        var inner = viewModel.InnerRows;
        var folders = viewModel.FileFolders;

        viewModel.PayloadFilter = "redist";

        Assert.Single(viewModel.PayloadRows);
        Assert.Same(files, viewModel.FileRows);
        Assert.Same(inner, viewModel.InnerRows);
        Assert.Same(folders, viewModel.FileFolders);
        Assert.Equal("app", viewModel.SelectedFolder);
        var row = viewModel.FileRows[0];
        viewModel.SelectedFile = row;
        viewModel.FileFilter = "contoso";
        Assert.Same(row, viewModel.SelectedFile);
    }

    [Fact]
    public async Task TheDrillDownLogsTheDepthAndNeverAnEntryNameOrTheTextOfAFailure()
    {
        _h.Service.Payload =
        [
            FakeInstallerAnalysisService.Entry("evil\r\n2026-10-19 [Error] forged.msi", 10, kind: "InnerInstaller", innerType: "MSI"),
            FakeInstallerAnalysisService.Entry("second-secret.msi", 10, kind: "InnerInstaller", innerType: "MSI")
        ];
        _h.Service.ExtractTo = (entry, folder) =>
        {
            var target = Path.Combine(folder, "inner" + _h.Service.Extractions.Count + ".msi");
            File.WriteAllText(target, "inner");
            return target;
        };
        var (viewModel, _) = await AnalyzedAsync();
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.InnerTab;
        await Wait.UntilAsync(() => viewModel.InnerRows.Count == 2 && !viewModel.IsBusy, "the inner installers");
        viewModel.SelectedInner = viewModel.InnerRows[0];
        await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.Depth);

        await Wait.UntilAsync(() => viewModel.InnerRows.Count == 2 && !viewModel.IsBusy, "the inner installers of level 1");
        viewModel.SelectedInner = viewModel.InnerRows[1];
        _h.Service.AnalyzeFailure = new InvalidDataException(@"bad header in second-secret.msi at C:\Users\op\secret");
        await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);
        Assert.Equal(InstallerAnalysisViewModel.FailedMessage, viewModel.StatusMessage);

        var log = string.Join("\n", _h.Log.Entries.Select(e => e.Message));
        Assert.Contains("file=setup.exe", log);
        Assert.Contains("depth=1", log);
        Assert.Contains("InvalidDataException", log);
        Assert.DoesNotContain("evil", log);
        Assert.DoesNotContain("forged", log);
        Assert.DoesNotContain("second-secret", log);
        Assert.DoesNotContain("bad header", log);
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain('\r', e.Message));
        Assert.All(_h.Log.Entries, e => Assert.DoesNotContain('\n', e.Message));
    }

    [Fact]
    public async Task AnHtmlExportWaitingForThePayloadWritesNothingWhenTheListIsCancelled()
    {
        _h.Service.Payload = InstallerAnalysisHarness.SamplePayload;
        var (viewModel, path) = await AnalyzedAsync();
        _h.Service.Hold("payload", path);

        var export = viewModel.ExportHtmlCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _h.Service.PayloadCalls == 1 && viewModel.IsBusy, "the payload list");
        viewModel.CancelCommand.Execute(null);
        await export;

        Assert.Empty(_h.Writer.Written);
        Assert.Equal(0, _h.Writer.WritesStarted);
        Assert.Null(viewModel.LastReportFolder);
        Assert.StartsWith("The HTML report was not written.", viewModel.StatusMessage);
        Assert.Contains("cancelled", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ACsvExportWritesNothingWhenTheRegistryReadFails()
    {
        _h.Service.RegistryFailure = new InstallerAnalysisException("The MSI Registry table could not be read.");
        var (viewModel, _) = await AnalyzedAsync();
        await viewModel.ExportCsvCommand.ExecuteAsync(null);
        Assert.Empty(_h.Writer.Written);
        Assert.Equal("The CSV report was not written. The MSI Registry table could not be read.", viewModel.StatusMessage);
        Assert.Null(viewModel.LastReportFolder);
    }

    [Fact]
    public async Task AnExportThatFinishesAfterAnotherOpenLeavesTheNewAnalysisAlone()
    {
        var (viewModel, _) = await AnalyzedAsync();
        _h.Writer.Gate = new ManualResetEventSlim(false);
        var export = viewModel.ExportCsvCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _h.Writer.WritesStarted == 1, "the write");
        Assert.True(viewModel.IsBusy);

        await viewModel.OpenAsync(_h.Installer("next.msi"));
        _h.Writer.Gate.Set();
        await export;

        Assert.Equal("Analyzed next.msi.", viewModel.StatusMessage);
        Assert.Null(viewModel.LastReportFolder);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task LeavingThePageDuringAnExportWaitsForItAndShowsNothingAfterwards()
    {
        var (viewModel, _) = await AnalyzedAsync();
        _h.Writer.Gate = new ManualResetEventSlim(false);
        var export = viewModel.ExportCsvCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _h.Writer.WritesStarted == 1, "the write");
        var leaving = viewModel.OnNavigatedFromAsync();
        Assert.False(leaving.IsCompleted);
        _h.Writer.Gate.Set();
        await leaving;
        await export;
        Assert.Equal("", viewModel.StatusMessage);
        Assert.Null(viewModel.LastReportFolder);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("folder")]
    [InlineData("invalid")]
    [InlineData("empty")]
    [InlineData("recent")]
    public async Task EveryOpenAttemptCancelsTheRunningAnalysisAndItsResultNeverReplacesTheMessage(string attempt)
    {
        var viewModel = await _h.OpenPageAsync();
        var first = _h.Installer("first.exe");
        _h.Service.HoldsIgnoreCancel = true;
        var hold = _h.Service.Hold("analyze", first);
        var running = viewModel.OpenAsync(first);
        await Wait.UntilAsync(() => _h.Service.AnalyzeCalls == 1, "the first analysis");

        var missing = Path.Combine(_h.Files, "gone.exe");
        switch (attempt)
        {
            case "missing": await viewModel.OpenAsync(missing); break;
            case "folder": await viewModel.OpenAsync(_h.Files); break;
            case "invalid": await viewModel.OpenAsync("bad|name<>.exe"); break;
            case "empty": await viewModel.OpenAsync("  "); break;
            case "recent":
                viewModel.RecentFiles.Add(missing);
                viewModel.SelectedRecent = missing;
                await viewModel.OpenRecentCommand.ExecuteAsync(null);
                break;
        }
        var message = viewModel.StatusMessage;
        hold.SetResult();
        await running;

        Assert.False(viewModel.HasReport);
        Assert.Equal(message, viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ADroppedShortcutIsRefusedWithAPlainMessage()
    {
        var viewModel = await _h.OpenPageAsync();
        await viewModel.DropAsync([_h.Installer("Contoso Setup.lnk")]);
        Assert.Equal(InstallerAnalysisViewModel.ShortcutMessage, viewModel.StatusMessage);
        Assert.Empty(_h.Service.Calls);
        Assert.Empty(viewModel.RecentFiles);
    }

    [Fact]
    public async Task LateTabResultsOfTheFirstInstallerNeverReachTheSecond()
    {
        var (viewModel, first) = await AnalyzedAsync("first.exe");
        var second = _h.Installer("second.exe");
        _h.Service.PayloadByPath[first] = [FakeInstallerAnalysisService.Entry("first-only.dll", 1)];
        _h.Service.PayloadByPath[second] = [FakeInstallerAnalysisService.Entry("second-only.dll", 1)];
        _h.Service.HoldsIgnoreCancel = true;
        var payload = _h.Service.Hold("payload", first);
        var registry = _h.Service.Hold("registry", first);
        var strings = _h.Service.Hold("strings", first);
        _h.Service.Registry = [FakeInstallerAnalysisService.Write("NsisScript", "HKLM", "Software\\Late", "x", "y")];
        _h.Service.Strings = [FakeInstallerAnalysisService.Found("late string", "Ascii", 0, "Other")];
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.RegistryTab;
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.StringsTab;
        await Wait.UntilAsync(() => _h.Service.PayloadCalls == 1 && _h.Service.RegistryCalls == 1 && _h.Service.StringsCalls == 1, "the three loads");

        _h.Service.Hold("registry", second);
        _h.Service.Hold("strings", second);
        await viewModel.OpenAsync(second);
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        await Wait.UntilAsync(() => viewModel.PayloadRows.Count == 1, "the second payload");
        payload.SetResult();
        registry.SetResult();
        strings.SetResult();
        await Wait.UntilAsync(() => _h.Service.Calls.Count(c => c.StartsWith("payload:")) == 2, "the calls");
        await Task.Delay(50);

        Assert.Equal("second-only.dll", Assert.Single(viewModel.PayloadRows).Name);
        Assert.Empty(viewModel.RegistryRows);
        Assert.Empty(viewModel.StringRows);
        Assert.Equal("Analyzed second.exe.", viewModel.StatusMessage);
        Assert.Equal("second.exe", viewModel.Report!.FileName);
    }

    [Fact]
    public async Task ALateHashOrDrillResultOfTheFirstInstallerNeverReachesTheSecond()
    {
        var viewModel = await WithPayloadAsync();
        var row = viewModel.PayloadRows.Single(r => r.Name == "contoso.exe");
        await viewModel.ExtractToTempCommand.ExecuteAsync(row);
        var session = Assert.Single(_h.Sandbox.Created);
        _h.Service.HoldsIgnoreCancel = true;
        var hash = _h.Service.Hold("hash", Path.Combine(session, "contoso.exe"));
        var hashing = viewModel.HashCommand.ExecuteAsync(row);
        await Wait.UntilAsync(() => _h.Service.Calls.Any(c => c.StartsWith("hash:")), "the hash");

        var inner = viewModel.PayloadRows.Single(r => r.Name == "setup.msi");
        var drill = _h.Service.Hold("analyze", Path.Combine(session, "setup.msi"));
        viewModel.SelectedPayload = inner;
        var drilling = viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _h.Service.Calls.Contains("analyze:" + Path.Combine(session, "setup.msi")), "the drill-down");

        await viewModel.OpenAsync(_h.Installer("second.exe"));
        hash.SetResult();
        drill.SetResult();
        await hashing;
        await drilling;

        Assert.Equal("Analyzed second.exe.", viewModel.StatusMessage);
        Assert.Equal("second.exe", viewModel.Report!.FileName);
        Assert.Equal(0, viewModel.Depth);
        Assert.Equal("", viewModel.HashResult);
    }

    [Fact]
    public async Task TheHashShowsInAReadOnlyBoxThatCanBeCopiedAndClearsWithTheAnalysis()
    {
        var viewModel = await WithPayloadAsync();
        Assert.False(viewModel.HasHash);
        await viewModel.HashCommand.ExecuteAsync(viewModel.PayloadRows.Single(r => r.Name == "contoso.exe"));
        Assert.True(viewModel.HasHash);
        Assert.Equal(new string('F', 64), viewModel.HashResult);
        Assert.Equal("SHA-256 of contoso.exe", viewModel.HashLabel);
        viewModel.CopyHashCommand.Execute(null);
        Assert.Equal([new string('F', 64)], _h.Prompts.Copied);
        await viewModel.OpenAsync(_h.Installer("next.exe"));
        Assert.False(viewModel.HasHash);
        Assert.Equal("", viewModel.HashResult);
    }

    [Fact]
    public async Task BackRemovesTheFilesExtractedInTheLevelsItLeavesAndKeepsTheParentFiles()
    {
        var viewModel = await WithPayloadAsync();
        viewModel.SelectedPayload = viewModel.PayloadRows.Single(r => r.Name == "setup.msi");
        await viewModel.AnalyzeSelectedCommand.ExecuteAsync(null);
        Assert.Equal(1, viewModel.Depth);
        var parentSession = Assert.Single(_h.Sandbox.Created);
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        await Wait.UntilAsync(() => viewModel.PayloadRows.Count == 5 && !viewModel.IsBusy, "the level 1 payload");
        await viewModel.ExtractToTempCommand.ExecuteAsync(viewModel.PayloadRows.Single(r => r.Name == "readme.txt"));
        Assert.Equal(2, _h.Sandbox.Created.Count);
        var childSession = _h.Sandbox.Created[1];
        Assert.True(File.Exists(Path.Combine(childSession, "readme.txt")));

        viewModel.BackCommand.Execute(null);
        await Wait.UntilAsync(() => _h.Sandbox.Released.Count == 1, "the release");

        Assert.Equal([childSession], _h.Sandbox.Released);
        Assert.False(Directory.Exists(childSession));
        Assert.True(File.Exists(Path.Combine(parentSession, "setup.msi")));
        await viewModel.OnNavigatedFromAsync();
        Assert.Equal([childSession, parentSession], _h.Sandbox.Released);
    }

    [Fact]
    public async Task EverySessionTheShownPageCreatesHoldsTheOwnerOfThisProcess()
    {
        var viewModel = await WithPayloadAsync();
        await viewModel.ExtractToTempCommand.ExecuteAsync(viewModel.PayloadRows.Single(r => r.Name == "contoso.exe"));
        var session = Assert.Single(_h.Sandbox.Created);
        var owner = InstallerAnalysisSessions.ReadOwner(session);
        Assert.NotNull(owner);
        Assert.Equal(Environment.ProcessId, owner.Value.ProcessId);
        Assert.True(InstallerAnalysisSessions.IsAlive(owner.Value.ProcessId, owner.Value.StartTicks));
    }

    [Fact]
    public async Task LeavingThePageWhileTheSessionIsCreatedReleasesItAndExtractsNothing()
    {
        _h.Service.Payload = InstallerAnalysisHarness.SamplePayload;
        var slow = new SlowSandbox(_h.Sandbox);
        var page = new InstallerAnalysisViewModel(new InstallerAnalysisPageServices(_h.Service, slow, _h.Writer, _h.Prompts, () => null, _ => { }, _h.Log, _h.Limits, () => _h.SandboxRoot));
        await page.OnNavigatedToAsync();
        await page.OpenAsync(_h.Installer("slow.exe"));
        page.SelectedTabIndex = InstallerAnalysisViewModel.PayloadTab;
        await Wait.UntilAsync(() => page.PayloadRows.Count == 5 && !page.IsBusy, "the payload");
        var extracting = page.ExtractToTempCommand.ExecuteAsync(page.PayloadRows.Single(r => r.Name == "contoso.exe"));
        await Wait.UntilAsync(() => slow.Entered.IsSet, "the session creation");

        var leaving = page.OnNavigatedFromAsync();
        slow.Release.Set();
        await leaving;
        await extracting;
        await Wait.UntilAsync(() => _h.Sandbox.Released.Count == 1, "the release");

        Assert.Empty(_h.Service.Extractions);
        Assert.Equal(_h.Sandbox.Created, _h.Sandbox.Released);
        Assert.Equal("", page.StatusMessage);
        Assert.Null(page.LastExtractFolder);
    }

    private sealed class SlowSandbox(IAnalysisSandbox inner) : IAnalysisSandbox
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public string NewSession()
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(10));
            return inner.NewSession();
        }

        void IAnalysisSandbox.Release(string session) => inner.Release(session);
        public void ReleaseAll() => inner.ReleaseAll();
        public void CleanOldSessions(TimeSpan olderThan) => inner.CleanOldSessions(olderThan);
        public string Root => inner.Root;
    }

    [Fact]
    public async Task ACancelledPayloadExtractionNamesTheFolderItLeavesBehind()
    {
        var viewModel = await WithPayloadAsync();
        var target = Directory.CreateDirectory(Path.Combine(_h.Root, "out")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "setup payload"));
        _h.Prompts.Folder = target;
        _h.Service.PayloadExtractLeavesPartial = true;
        _h.Service.PayloadExtractGate = new TaskCompletionSource();
        var running = viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        await Wait.UntilAsync(() => _h.Service.PayloadExtractions.Count == 1 && Directory.Exists(Path.Combine(target, "setup payload (2)")), "the extraction");
        viewModel.CancelCommand.Execute(null);
        await running;

        var partial = Path.Combine(target, "setup payload (2)");
        Assert.Equal($"The extraction was cancelled. The folder {partial} holds the files extracted before the stop.", viewModel.StatusMessage);
        Assert.True(Directory.Exists(partial));
        Assert.True(Directory.Exists(Path.Combine(target, "setup payload")));

        _h.Service.PayloadExtractGate = null;
        _h.Service.PayloadExtractFailure = new InstallerAnalysisException("The payload expands beyond the extraction limit.");
        await viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        Assert.Equal($"The payload expands beyond the extraction limit. The folder {Path.Combine(target, "setup payload (3)")} holds the files extracted before the stop.",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task ATabOrFileChosenWhileTheChoicesLoadIsKept()
    {
        var old = _h.Installer("old.exe");
        _h.Saved = new InstallerAnalysisSettings { SelectedTab = InstallerAnalysisViewModel.StringsTab, RegistryFilter = "Run", RecentFiles = [old] };
        _h.PreferencesGate = new ManualResetEventSlim(false);
        var viewModel = _h.Create();
        var loading = viewModel.OnNavigatedToAsync();
        await Wait.UntilAsync(() => _h.PreferenceReads == 1, "the choices read");
        viewModel.SelectedTabIndex = InstallerAnalysisViewModel.MsiTab;
        var chosen = _h.Installer("chosen.exe");
        await viewModel.OpenAsync(chosen);
        _h.PreferencesGate.Set();
        await loading;

        Assert.Equal(InstallerAnalysisViewModel.MsiTab, viewModel.SelectedTabIndex);
        Assert.Equal([chosen, old], viewModel.RecentFiles);
        Assert.Equal("Run", viewModel.RegistryFilter);
    }

    [Fact]
    public async Task LeavingThePageClearsThePathBox()
    {
        var (viewModel, _) = await AnalyzedAsync();
        await viewModel.OnNavigatedFromAsync();
        Assert.Equal("", viewModel.PathText);
    }

    [Fact]
    public void TheLoggedFileNameStaysOnOneLine()
    {
        Assert.Equal("a?b?c?d", InstallerAnalysisViewModel.LogText("a\rb\nc\u2028d"));
        Assert.Equal("plain.exe", InstallerAnalysisViewModel.LogText("plain.exe"));
    }
}
