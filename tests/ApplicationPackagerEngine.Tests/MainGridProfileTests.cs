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
    public void AStoreThatCannotBeOpenedCarriesTheReason()
    {
        var source = ProfileSource.Resolve(null, DefinitionId);

        Assert.Equal(ApplicationId, source.ApplicationId);
        Assert.Null(source.ActiveProfileId);
        Assert.NotNull(source.Failure);
        Assert.Contains("could not be read for catalog:widget", source.RefusalMessage());
    }

    [Fact]
    public void AnApplicationFileThatCannotBeParsedCarriesTheReasonInsteadOfTheLegacyMaps()
    {
        var folder = _t.Store.GetApplicationFolder(ApplicationId, create: true);
        File.WriteAllText(Path.Combine(folder, "application.json"), "{ not json");

        var source = ProfileSource.Resolve(_t.Store, DefinitionId);

        Assert.NotNull(source.Failure);
        Assert.Null(source.ActiveProfileId);
    }

    [Fact]
    public void AnApplicationFileHeldForAMomentByASaveIsReadOnceItIsReleased()
    {
        var profile = _t.NewProfile(ApplicationId, "Pilot Ring");
        var path = Path.Combine(_t.Store.GetApplicationFolder(ApplicationId), "application.json");
        var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // A dedicated thread: a thread-pool timer under a busy pool can fire after the
        // read's bound, and the read then reports the file as held.
        using var started = new ManualResetEventSlim();
        var release = new Thread(() =>
        {
            started.Set();
            Thread.Sleep(100);
            held.Dispose();
        });
        release.Start();
        started.Wait();

        var source = ProfileSource.Resolve(_t.Store, DefinitionId);
        release.Join();

        Assert.Null(source.Failure);
        Assert.Equal(profile.ProfileId, source.ActiveProfileId);
    }

    [Fact]
    public void AnApplicationFileHeldPastTheBoundStillReportsTheFailure()
    {
        _t.NewProfile(ApplicationId, "Pilot Ring");
        var path = Path.Combine(_t.Store.GetApplicationFolder(ApplicationId), "application.json");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var source = ProfileSource.Resolve(_t.Store, DefinitionId);

        Assert.NotNull(source.Failure);
    }

    [Fact]
    public void ADeletedMigratedProfileTurnsTheLegacyMapsOnAgainAsBeforeMigration()
    {
        Migrate();
        var migrated = _t.Store.GetApplicationDefinition(ApplicationId).ActiveProfileId;
        _t.Store.SetActiveProfile(ApplicationId, ApplicationIds.DefaultProfileId);
        Directory.Delete(_t.Store.GetProfileFolder(ApplicationId, migrated), recursive: true);

        var source = ProfileSource.Resolve(_t.Store, DefinitionId);

        Assert.Null(source.Failure);
        Assert.True(source.LegacyMapsApply);
    }

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

/// <summary>
/// A grid Package after a grid Stage of a local-source definition: the local installer
/// stages under its own file version, and that stage is the one the publish uses.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class LocalSourceGridPublishTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly Dictionary<string, string?> _saved = [];
    private readonly string _downloadRoot;
    private readonly string _sourceFolder;

    public LocalSourceGridPublishTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, WorkbenchPaths.DataRootVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _t.NewFolder("appdata"));
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, _t.Store.DataRoot);
        _downloadRoot = _t.NewFolder("downloads");
        _sourceFolder = _t.NewFolder("local-installers");
        var settings = new SettingsService();
        var s = settings.Load();
        s.DownloadRoot = _downloadRoot;
        s.LocalSourceFolders["localsourcetest"] = _sourceFolder;
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

    private PackagerInfo Row()
    {
        var definition = new AppDefinition
        {
            Id = "localsourcetest",
            Vendor = "Contoso",
            Application = "Widget",
            Architecture = "x64",
            InstallerType = "EXE",
            InstallArgs = "/silent",
            VersionScraper = new VersionScraperConfig { Type = "HttpRegex", Url = "http://127.0.0.1:9/never", Pattern = "(x)" },
            DownloadUrl = "http://127.0.0.1:9/never.exe",
            InstallerFileName = "Widget-{version}.exe",
            LocalSource = new LocalSourceConfig { Mode = "Required", Filter = "Widget*.exe" },
            Detection = new DetectionConfig
            {
                Type = "RegistryKeyValue",
                RegistryKeyTemplate = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Widget",
                PropertyType = "Version",
                Operator = "GreaterEquals"
            }
        };
        var path = Path.Combine(_t.NewFolder("defs"), "localsourcetest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(definition));
        return new PackagerInfo { Uid = "localsourcetest", Application = "Widget", IsJsonDefined = true, DefinitionPath = path, LatestVersion = "999.0.0" };
    }

    [Fact]
    public async Task APackageAfterALocalSourceStage_UsesTheStageOfTheLocalInstallersVersion()
    {
        File.Copy(typeof(object).Assembly.Location, Path.Combine(_sourceFolder, "Widget-setup.exe"));
        var row = Row();
        var service = Service();

        var staged = await service.StageAsync(row);

        Assert.Equal(0, staged.ExitCode);
        Assert.NotEqual("999.0.0", Path.GetFileName(Path.GetDirectoryName(staged.OutLogPath)));
        Assert.Equal(staged.OutLogPath, service.ExistingStageManifest(row));
        Assert.Equal(Path.GetFileName(Path.GetDirectoryName(staged.OutLogPath)), PackagerExecutionService.PublishVersion(row));
    }

    [Fact]
    public void WithoutALocalSourceStage_AnOlderStagedVersionIsNotPublishedAsTheLatest()
    {
        var row = Row();
        row.StagedVersion = "1.0.0";
        var older = Path.Combine(_downloadRoot, "Widget-x64", "1.0.0", "stage-manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        new ManifestService().Write(older, new StageManifest { AppName = "Widget", SoftwareVersion = "1.0.0" });

        Assert.Null(Service().ExistingStageManifest(row));
        Assert.Equal("999.0.0", PackagerExecutionService.PublishVersion(row));
    }
}
