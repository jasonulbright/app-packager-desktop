using System.Reflection;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerEngine.Tests.AddFromInstaller;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A run that follows a review publishes the reviewed version or nothing. The stage here is real: a definition with a saved installer stages
/// version 4.5.6 whatever the row says, as a vendor feed or a local installer that moved on does.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ReviewedVersionPublishTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly AddFromInstallerTestBed _bed = new();
    private readonly Dictionary<string, string?> _saved = [];

    public ReviewedVersionPublishTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, WorkbenchPaths.DataRootVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _t.NewFolder("appdata"));
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, _t.Store.DataRoot);
        var settings = new SettingsService();
        var s = settings.Load();
        s.DownloadRoot = _t.NewFolder("downloads");
        s.Wsus.ServerName = "wsus01.contoso.com";
        settings.SaveOperatorEdit(s);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        _bed.Dispose();
        _t.Dispose();
    }

    private sealed class CountingPublisher : IWsusPublisher
    {
        public List<string> Versions { get; } = [];

        public Task<WsusPublishResult> PublishAsync(StageManifest manifest, string contentFolder, WsusSettings settings,
            Action<string>? onProgress = null, CancellationToken ct = default, string? supportUrl = null)
        {
            Versions.Add(manifest.SoftwareVersion);
            return Task.FromResult(new WsusPublishResult(Guid.NewGuid(), "Contoso Tool", WsusPublishOutcome.Published, "", [], [], [],
                "published", WsusRuleSource.Detection, []));
        }
    }

    private static PackagerExecutionService Execution()
    {
        var settings = new SettingsService();
        return new PackagerExecutionService(settings, ProfileStageFixture.Service(),
            new IntuneDeploymentService(settings), new DeploymentConditionsService(settings));
    }

    private async Task<PackagerInfo> RowAsync(string latest)
    {
        var service = _bed.Real();
        var analyzed = await service.AnalyzeAsync(_bed.Msi(), new Progress<string>(), CancellationToken.None);
        var saved = await service.SaveAsync(analyzed, new Services.AddFromInstaller.AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None);
        return new PackagerInfo
        {
            Uid = saved.DefinitionId, Application = "Contoso Tool", Script = "contoso.json", IsJsonDefined = true, DefinitionPath = saved.FilePath,
            LatestVersion = latest
        };
    }

    [Theory]
    [InlineData("4.0", "Staged 4.5.6, reviewed 4.0; check the version and review again. Nothing was published.")]
    [InlineData("", "Staged 4.5.6, reviewed no version; check the version and review again. Nothing was published.")]
    public async Task AFirstWsusRunPublishesNothingWhenTheStageItMakesIsAnotherVersion(string reviewed, string expected)
    {
        var publisher = new CountingPublisher();
        var execution = Execution();
        var row = await RowAsync(reviewed);

        var run = await new PublishRunService(execution, publisher).PublishWsusRowAsync(row, null, reviewed, _ => { }, CancellationToken.None);

        Assert.Empty(publisher.Versions);
        Assert.False(run.StageFailed);
        Assert.Equal(WsusStepStatus.Failed, run.Step!.Status);
        Assert.Equal(expected, run.Step.Message);
        Assert.Equal("Publish error", row.Status);
    }

    [Theory]
    [InlineData("4.5.6")]
    [InlineData(null)]
    public async Task TheReviewedVersionAndARunWithNoReviewArePublished(string? reviewed)
    {
        var publisher = new CountingPublisher();
        var row = await RowAsync("4.5.6");

        var run = await new PublishRunService(Execution(), publisher).PublishWsusRowAsync(row, null, reviewed, _ => { }, CancellationToken.None);

        Assert.Equal(["4.5.6"], publisher.Versions);
        Assert.Equal(WsusStepStatus.Published, run.Step!.Status);
    }

    [Fact]
    public async Task AWsusRetryAfterAStageFailurePublishesNothingWhenTheNewStageIsAnotherVersion()
    {
        var publisher = new CountingPublisher();
        var execution = Execution();
        // The first run reviewed 4.0 and its stage failed, so the row still names 4.0. The retry stages again and gets 4.5.6.
        var row = await RowAsync("4.0");
        var viewModel = PublishRunKit.Create(new SettingsService(), new PublishRunKit.Dialogs(), row);
        PublishRunKit.Set(viewModel, "_executionService", execution);
        PublishRunKit.Set(viewModel, "_publishRuns", new PublishRunService(execution, publisher));
        var build = Activator.CreateInstance(typeof(PackagesViewModel).GetNestedType("PublishRunBuild", BindingFlags.NonPublic)!,
            new List<PackagerInfo> { row }, null, null, null)!;

        var cell = await (Task<PublishDestinationResult?>)typeof(PackagesViewModel)
            .GetMethod("RunRetryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [row, "WSUS", "4.0", build])!;

        Assert.Empty(publisher.Versions);
        Assert.Equal(PublishDestinationKind.Failed, cell!.Kind);
        Assert.StartsWith("Staged 4.5.6, reviewed 4.0;", cell.Message);
    }

    [Fact]
    public async Task AnIntuneRunPublishesNothingWhenTheStageIsAnotherVersionThanTheReview()
    {
        var execution = Execution();
        var row = await RowAsync("4.5.6");
        Assert.Equal(0, (await execution.StageAsync(row)).ExitCode);

        var result = await execution.PackageAsync(row, "", targetOverride: PublishRunTargets.IntuneOnly, reviewedVersion: "4.0");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Staged 4.5.6, reviewed 4.0; check the version and review again. Nothing was published.", result.StdErr);
    }

    [Fact]
    public async Task AScriptPackagerWithNoReviewedVersionIsNotRun()
    {
        var row = new PackagerInfo { Uid = "s", Application = "Script Tool", Script = "package-tool.ps1", FullPath = Path.Combine(_t.NewFolder("scripts"), "package-tool.ps1") };

        var result = await Execution().PackageAsync(row, "", targetOverride: PublishRunTargets.ConfigMgr, reviewedVersion: "");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("reviewed no version", result.StdErr);
    }

    [Fact]
    public void TheVersionOfARowThatStagesALocalInstallerIsTheVersionOfTheFile()
    {
        var folder = _t.NewFolder("local-source");
        var installer = Path.Combine(folder, "tool-setup.exe");
        File.Copy(typeof(StagingService).Assembly.Location, installer);
        var version = LocalSourceResolver.ReadVersion(installer);
        AppDefinition Definition(string mode) => new()
        {
            Id = "tool", Application = "Tool", InstallerType = "EXE",
            LocalSource = new LocalSourceConfig { Mode = mode, Filter = "tool-*.exe" }
        };

        Assert.Equal(version, StagingService.LocalStageVersion(Definition("Required"), folder));
        // An optional local source wins when a folder is set, and the download runs otherwise.
        Assert.Equal(version, StagingService.LocalStageVersion(Definition("Optional"), folder));
        Assert.Null(StagingService.LocalStageVersion(Definition("Optional"), null));
        Assert.Null(StagingService.LocalStageVersion(Definition("Required"), null));
        Assert.Null(StagingService.LocalStageVersion(new AppDefinition { Id = "web", Application = "Web" }, folder));

        // The publish review of such a row shows the version of the file, not the website version.
        var definitionPath = Path.Combine(_t.NewFolder("definitions"), "tool.json");
        File.WriteAllText(definitionPath, System.Text.Json.JsonSerializer.Serialize(Definition("Required")));
        var settings = new SettingsService();
        var stored = settings.Load();
        stored.LocalSourceFolders["tool"] = folder;
        settings.SaveOperatorEdit(stored);
        var row = new PackagerInfo { Uid = "tool", Application = "Tool", IsJsonDefined = true, DefinitionPath = definitionPath, LatestVersion = "999.0" };
        Assert.Equal(version, Execution().RunVersion(row));
        Assert.Equal("999.0", PackagerExecutionService.PublishVersion(row));
    }

    [Theory]
    [InlineData(null, "9", true)]
    [InlineData("1.0", "1.0", true)]
    [InlineData("1.0", "1.1", false)]
    [InlineData("", "1.1", false)]
    [InlineData("1.0", "", false)]
    public void OnlyTheReviewedVersionPasses(string? reviewed, string staged, bool passes) =>
        Assert.Equal(passes, ReviewedVersion.Refusal(reviewed, staged) is null);
}
