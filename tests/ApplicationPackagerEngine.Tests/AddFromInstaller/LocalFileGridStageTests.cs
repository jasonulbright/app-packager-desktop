using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests.AddFromInstaller;

/// <summary>
/// Stage now on the result panel and a Workbench run both stage through PackagerExecutionService. A definition that
/// Add from file saved stages there from its saved installer, with no Check Latest, after a Check Latest, and from a
/// run snapshot of an active Workbench profile.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LocalFileGridStageTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly AddFromInstallerTestBed _bed = new();
    private readonly Dictionary<string, string?> _saved = [];
    private readonly string _downloadRoot;

    public LocalFileGridStageTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, WorkbenchPaths.DataRootVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _t.NewFolder("appdata"));
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, _t.Store.DataRoot);
        _downloadRoot = _t.NewFolder("downloads");
        var settings = new SettingsService();
        var s = settings.Load();
        s.DownloadRoot = _downloadRoot;
        settings.SaveOperatorEdit(s);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        _bed.Dispose();
        _t.Dispose();
    }

    private static PackagerExecutionService Service()
    {
        var settings = new SettingsService();
        return new PackagerExecutionService(settings, ProfileStageFixture.Service(),
            new IntuneDeploymentService(settings), new DeploymentConditionsService(settings));
    }

    private async Task<(PackagerInfo Row, string DefinitionId)> SavedRowAsync()
    {
        var service = _bed.Real();
        var r = await service.AnalyzeAsync(_bed.Msi(), new Progress<string>(), CancellationToken.None);
        var saved = await service.SaveAsync(r, new Services.AddFromInstaller.AddSaveOptions(DefinitionsFolder: _bed.Definitions), CancellationToken.None);
        return (new PackagerInfo { Uid = saved.DefinitionId, Application = "Contoso Tool", IsJsonDefined = true, DefinitionPath = saved.FilePath }, saved.DefinitionId);
    }

    [Fact]
    public async Task AGridStageWithNoCheckLatest_StagesTheSavedInstaller_AndAPublishFindsThatStage()
    {
        var (row, _) = await SavedRowAsync();
        var service = Service();

        var staged = await service.StageAsync(row);

        Assert.True(staged.ExitCode == 0, staged.StdErr);
        Assert.Equal(Path.Combine(_downloadRoot, "ContosoTool-x64", "4.5.6", "stage-manifest.json"), staged.OutLogPath);
        Assert.Equal(staged.OutLogPath, service.ExistingStageManifest(row));
    }

    [Fact]
    public async Task ApplySupersedence_RefusesWithoutAStage_ThenWithoutASiteOrTheMode_AndContactsNoSite()
    {
        var (row, _) = await SavedRowAsync();
        var service = Service();

        var unstaged = await service.ApplySupersedenceAsync(row, null, null, CancellationToken.None);
        Assert.False(unstaged.Ok);
        Assert.Contains("No stage manifest", unstaged.Message);

        Assert.True((await service.StageAsync(row)).ExitCode == 0);
        var noSite = await service.ApplySupersedenceAsync(row, null, null, CancellationToken.None);
        Assert.False(noSite.Ok);
        Assert.True(noSite.Message.Contains("site code") || noSite.Message.Contains("is off"), noSite.Message);
    }

    [Fact]
    public async Task CheckLatest_ReportsTheSavedVersion_AndTheGridStageUsesIt()
    {
        var (row, _) = await SavedRowAsync();
        var service = Service();

        row.LatestVersion = await service.GetLatestVersionAsync(row, CancellationToken.None) ?? "";
        var staged = await service.StageAsync(row);

        Assert.Equal("4.5.6", row.LatestVersion);
        Assert.True(staged.ExitCode == 0, staged.StdErr);
        Assert.Equal("4.5.6", Path.GetFileName(Path.GetDirectoryName(staged.OutLogPath)));
    }

    [Fact]
    public async Task AnActiveWorkbenchProfile_StagesTheSavedInstallerFromItsRunSnapshot()
    {
        var (row, definitionId) = await SavedRowAsync();
        var profile = _t.NewProfile(ApplicationIds.New(ApplicationOrigin.Catalog, name: definitionId), "Pilot Ring");

        var staged = await Service().StageAsync(row);

        Assert.True(staged.ExitCode == 0, staged.StdErr);
        Assert.StartsWith(Path.Combine(_downloadRoot, "profiles", profile.ProfileId), staged.OutLogPath);
        var manifest = new ManifestService().Read(staged.OutLogPath!);
        Assert.Equal(profile.ProfileId, manifest.ProfileId);
        Assert.Equal("contoso.msi", manifest.InstallerFile);
    }
}
