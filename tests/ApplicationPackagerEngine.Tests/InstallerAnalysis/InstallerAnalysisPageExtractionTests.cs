using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;
using static ApplicationPackagerEngine.Tests.InstallerAnalysis.InstallerAnalysisTestFiles;

namespace ApplicationPackagerEngine.Tests.InstallerAnalysis;

/// <summary>The page over the real analysis service and sandbox, on a generated zip: the extracted files exist on disk.</summary>
public sealed class InstallerAnalysisPageExtractionTests : IDisposable
{
    private readonly InstallerAnalysisTempFolder _temp = new();
    private readonly AnalysisSandbox _sandbox;
    private readonly FakeInstallerAnalysisPrompts _prompts = new();
    private readonly RecordingAnalysisLog _log = new();
    private readonly InstallerAnalysisViewModel _viewModel;
    private readonly string _installer;

    public InstallerAnalysisPageExtractionTests()
    {
        _sandbox = new AnalysisSandbox(_temp.PathOf("sandbox"));
        _installer = WriteZip(_temp.PathOf("bundle.zip"),
            ("app/contoso.exe", Text("MZ contoso")),
            ("docs/readme.txt", Text("read me")),
            ("tools/setup.msi", Text("inner installer bytes")));
        _viewModel = new InstallerAnalysisViewModel(new InstallerAnalysisPageServices(
            Analysis: new InstallerAnalysisService(_sandbox, () => null),
            Sandbox: _sandbox,
            Reports: new FakeInstallerReportWriter(_temp.PathOf("reports")),
            Prompts: _prompts,
            LoadPreferences: () => null,
            SavePreferences: _ => { },
            Log: _log,
            Limits: AnalysisLimits.Default,
            SandboxRoot: () => _sandbox.Root));
    }

    public void Dispose()
    {
        _sandbox.ReleaseAll();
        _temp.Dispose();
    }

    private async Task OpenWithPayloadAsync()
    {
        await _viewModel.OnNavigatedToAsync();
        await _viewModel.OpenAsync(_installer);
        Assert.True(_viewModel.HasReport, _viewModel.StatusMessage);
        _viewModel.SelectedTabIndex = InstallerAnalysisViewModel.InnerTab;
        await Wait.UntilAsync(() => _viewModel.PayloadRows.Count > 0 && !_viewModel.IsBusy, "the payload");
    }

    [Fact]
    public async Task ExtractCopiesTheInnerInstallerIntoANewPayloadFolderOfTheChosenFolder()
    {
        await OpenWithPayloadAsync();
        var target = Directory.CreateDirectory(_temp.PathOf("chosen")).FullName;
        _prompts.Folder = target;
        _viewModel.SelectedInner = _viewModel.InnerRows.Single(r => r.Name == "setup.msi");

        await _viewModel.ExtractInnerCommand.ExecuteAsync(null);

        var copied = Path.Combine(target, "bundle payload", "tools", "setup.msi");
        Assert.True(File.Exists(copied), _viewModel.StatusMessage);
        Assert.Equal("inner installer bytes", File.ReadAllText(copied));
        Assert.False(File.Exists(Path.Combine(target, "bundle payload", "app", "contoso.exe")));
        Assert.Equal(Path.Combine(target, "bundle payload"), _viewModel.LastExtractFolder);
        Assert.Equal($"Copied setup.msi to {Path.Combine(target, "bundle payload")}.", _viewModel.StatusMessage);
    }

    [Fact]
    public async Task ExtractPayloadWritesEveryFileAndASecondRunGetsItsOwnFolder()
    {
        await OpenWithPayloadAsync();
        var target = Directory.CreateDirectory(_temp.PathOf("chosen")).FullName;
        _prompts.Folder = target;

        await _viewModel.ExtractPayloadCommand.ExecuteAsync(null);
        await _viewModel.ExtractPayloadCommand.ExecuteAsync(null);

        foreach (var folder in new[] { "bundle payload", "bundle payload (2)" })
        {
            Assert.Equal("MZ contoso", File.ReadAllText(Path.Combine(target, folder, "app", "contoso.exe")));
            Assert.Equal("read me", File.ReadAllText(Path.Combine(target, folder, "docs", "readme.txt")));
            Assert.True(File.Exists(Path.Combine(target, folder, "tools", "setup.msi")));
        }
        Assert.StartsWith("Extracted 3 files (", _viewModel.StatusMessage);
        Assert.EndsWith($"to {Path.Combine(target, "bundle payload (2)")}.", _viewModel.StatusMessage);
        Assert.Empty(Directory.Exists(_sandbox.Root) ? Directory.GetDirectories(_sandbox.Root) : []);
    }

    [Fact]
    public async Task ExtractToTempWritesOnlyIntoTheSandboxAndLeavingThePageRemovesIt()
    {
        await OpenWithPayloadAsync();
        var row = _viewModel.PayloadRows.Single(r => r.Name == "contoso.exe");

        await _viewModel.ExtractToTempCommand.ExecuteAsync(row);

        var session = _viewModel.LastExtractFolder!;
        Assert.True(AppDataPaths.IsWithin(session, _sandbox.Root), session);
        var extracted = Directory.GetFiles(_sandbox.Root, "contoso.exe", SearchOption.AllDirectories);
        Assert.Equal("MZ contoso", File.ReadAllText(Assert.Single(extracted)));

        await _viewModel.OnNavigatedFromAsync();
        Assert.False(File.Exists(extracted[0]));
    }

    [Fact]
    public async Task TheServiceRefusesAPayloadFolderInsideTheSandboxWithItsPlainMessage()
    {
        await OpenWithPayloadAsync();
        var session = _sandbox.NewSession();
        var service = new InstallerAnalysisService(_sandbox, () => null);
        var failure = await Assert.ThrowsAsync<InstallerAnalysisException>(() =>
            service.ExtractPayloadAsync(_installer, null, session, AnalysisLimits.Default, CancellationToken.None));
        Assert.Equal(failure.Message, InstallerAnalysisViewModel.PlainMessage(failure, "unused"));
        Assert.Equal("unused", InstallerAnalysisViewModel.PlainMessage(new IOException(@"C:\secret"), "unused"));
    }
}
