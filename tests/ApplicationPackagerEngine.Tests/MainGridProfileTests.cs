using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Which source a grid Stage or Publish of a JSON definition builds from, for each state
/// of the workbench store: no entry, the Migrated profile active, an operator profile
/// active, and the default chosen after migration.
/// </summary>
public sealed class ProfileSourceTests : IDisposable
{
    private const string DefinitionId = "widget";
    private const string ApplicationId = "catalog:widget";
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private static JsonNode LegacySettings() => WorkbenchTestRoot.Json("""
        { "DeploymentConditions": { "widget": { "Architecture": "x64", "InstallMode": "CurrentUser" } },
          "CommandOverrides": { "widget": { "Install": "legacy-install.cmd", "Uninstall": "" } } }
        """);

    private void Migrate() =>
        LegacyPreferenceMigration.Run(_t.Store, LegacySettings(), new WorkbenchDiscoveryRoots());

    private static StageManifest Build(string? profileId) => new()
    {
        AppName = "Widget",
        SoftwareVersion = "1.2.3",
        BuildId = profileId is null ? "" : "20261003-120000-abcdef01",
        ApplicationId = profileId is null ? "" : ApplicationId,
        ProfileId = profileId ?? ""
    };

    [Fact]
    public void NoStoreEntryKeepsTheLegacyMapsAndNoProfile()
    {
        var source = ProfileSource.Resolve(_t.Store, DefinitionId);

        Assert.Equal(new ProfileSource(ApplicationId, null, true), source);
        Assert.True(ProfileSource.LegacyMapsApplyTo(Build(null), source));
    }

    [Fact]
    public void AStoreThatCannotBeReadKeepsTheLegacyMaps() =>
        Assert.Equal(new ProfileSource(ApplicationId, null, true), ProfileSource.Resolve(null, DefinitionId));

    [Fact]
    public void TheMigratedProfileActiveBuildsFromItAndNotFromTheMaps()
    {
        Migrate();
        var migrated = _t.Store.GetApplicationDefinition(ApplicationId).ActiveProfileId;
        Assert.Equal("legacy-install.cmd", _t.Store.GetProfile(ApplicationId, migrated).Get("Install.Command").Text);

        var source = ProfileSource.Resolve(_t.Store, DefinitionId);

        Assert.Equal(new ProfileSource(ApplicationId, migrated, false), source);
        Assert.False(ProfileSource.LegacyMapsApplyTo(Build(migrated), source));
    }

    [Fact]
    public void AnOperatorProfileActiveBuildsFromItAndNotFromTheMaps()
    {
        Migrate();
        var operatorProfile = _t.NewProfile(ApplicationId, "Pilot Ring");

        var source = ProfileSource.Resolve(_t.Store, DefinitionId);

        Assert.Equal(new ProfileSource(ApplicationId, operatorProfile.ProfileId, false), source);
        Assert.False(ProfileSource.LegacyMapsApplyTo(Build(operatorProfile.ProfileId), source));
    }

    [Fact]
    public void TheDefaultChosenAfterMigrationAppliesNeitherAProfileNorTheMaps()
    {
        Migrate();
        _t.Store.SetActiveProfile(ApplicationId, ApplicationIds.DefaultProfileId);

        var source = ProfileSource.Resolve(_t.Store, DefinitionId);

        Assert.Equal(new ProfileSource(ApplicationId, null, false), source);
        Assert.False(ProfileSource.LegacyMapsApplyTo(Build(null), source));
        Assert.False(ProfileSource.LegacyMapsApplyTo(Build(ApplicationIds.DefaultProfileId), source));
    }

    [Fact]
    public void ADefaultProfileBuildWithoutAMigratedProfileKeepsTheMaps()
    {
        var source = ProfileSource.Resolve(_t.Store, DefinitionId);

        Assert.True(ProfileSource.LegacyMapsApplyTo(Build(ApplicationIds.DefaultProfileId), source));
        Assert.False(ProfileSource.LegacyMapsApplyTo(Build("0123456789abcdef"), source));
    }

    [Fact]
    public void AMigratedProfileRenamedByTheOperatorIsStillFoundByItsMarker()
    {
        Migrate();
        var migrated = _t.Store.GetApplicationDefinition(ApplicationId).ActiveProfileId;
        var renamed = _t.Store.GetProfile(ApplicationId, migrated).Clone();
        renamed.Data["Name"] = "Renamed";
        _t.Store.SaveProfile(renamed);
        _t.Store.SetActiveProfile(ApplicationId, ApplicationIds.DefaultProfileId);

        Assert.True(ProfileSource.HasMigratedProfile(_t.Store, ApplicationId, DefinitionId));
        Assert.False(ProfileSource.Resolve(_t.Store, DefinitionId).LegacyMapsApply);
    }
}

/// <summary>
/// A grid Stage of a JSON definition through PackagerExecutionService: a non-default
/// active profile builds from a run snapshot of that profile; the default profile stages
/// without a snapshot and writes the same manifest a stage with no profile writes.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class MainGridProfileStageTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly Dictionary<string, string?> _saved = [];
    private readonly string _downloadRoot;

    public MainGridProfileStageTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, WorkbenchPaths.DataRootVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _t.NewFolder("appdata"));
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, _t.Store.DataRoot);
        _downloadRoot = _t.NewFolder("downloads");
        var settings = new SettingsService();
        var s = settings.Load();
        s.DownloadRoot = _downloadRoot;
        s.CommandOverrides["widget"] = new CommandOverrideEntry { Install = "legacy-install.cmd" };
        settings.SaveOperatorEdit(s);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        _t.Dispose();
    }

    private static PackagerExecutionService Service()
    {
        var settings = new SettingsService();
        return new PackagerExecutionService(settings, ProfileStageFixture.Service(),
            new IntuneDeploymentService(settings), new DeploymentConditionsService(settings));
    }

    private PackagerInfo Row(LoopbackFileServer server)
    {
        var def = ProfileStageFixture.Definition(server);
        var path = Path.Combine(_t.NewFolder("defs"), "widget.json");
        File.WriteAllText(path, JsonSerializer.Serialize(def));
        return new PackagerInfo { Uid = "widget", Application = "Widget", IsJsonDefined = true, DefinitionPath = path, LatestVersion = "1.2.3" };
    }

    private static JsonObject Manifest(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("StagedAt");
        return node;
    }

    [Fact]
    public async Task ASavedOperatorProfileChangesWhatAGridStageProduces()
    {
        using var server = ProfileStageFixture.Server();
        var edit = _t.NewProfile(ProfileStageFixture.ApplicationId, "Pilot Ring").Clone();
        edit.Set("Install.Command", JsonValue.Create("pilot-install.cmd /quiet"));
        var profile = _t.Store.SaveProfile(edit, setActive: true);

        var result = await Service().StageAsync(Row(server));

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith(Path.Combine(_downloadRoot, "profiles", profile.ProfileId), result.OutLogPath);
        var manifest = new ManifestService().Read(result.OutLogPath!);
        Assert.Equal(profile.ProfileId, manifest.ProfileId);
        Assert.Equal(profile.Revision, manifest.ProfileRevision);
        Assert.False(string.IsNullOrWhiteSpace(manifest.BuildId));
        Assert.Equal("pilot-install.cmd /quiet", manifest.CommandOverrides?.Install);
        Assert.Single(BuildRecords.List(_t.Store.DataRoot, ProfileStageFixture.ApplicationId, profile.ProfileId));
    }

    [Fact]
    public async Task TheDefaultProfileStagesWithoutASnapshotAndWritesTodaysManifest()
    {
        using var server = ProfileStageFixture.Server();
        var row = Row(server);

        var grid = await Service().StageAsync(row);
        Assert.Equal(0, grid.ExitCode);
        var gridManifest = Manifest(grid.OutLogPath!);
        var gridBytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(grid.OutLogPath)!, "install.ps1"));

        var direct = await ProfileStageFixture.Service().StageAsync(ProfileStageFixture.Definition(server), _t.NewFolder("direct"),
            knownVersion: "1.2.3", inputs: new StageInputs { SigningPolicy = new SettingsService().Load().ScriptSigning });
        Assert.True(direct.Success, direct.ErrorMessage);

        Assert.Equal(Path.Combine(_downloadRoot, "Widget-x64", "1.2.3", "stage-manifest.json"), grid.OutLogPath);
        Assert.True(string.IsNullOrEmpty(gridManifest["BuildId"]?.GetValue<string>()));
        Assert.Equal(Manifest(direct.ManifestPath!).ToJsonString(), gridManifest.ToJsonString());
        Assert.Equal(File.ReadAllBytes(Path.Combine(direct.ContentPath!, "install.ps1")), gridBytes);
        Assert.Empty(Directory.Exists(_t.Store.RunsRoot) ? Directory.GetFiles(_t.Store.RunsRoot) : []);
    }
}
