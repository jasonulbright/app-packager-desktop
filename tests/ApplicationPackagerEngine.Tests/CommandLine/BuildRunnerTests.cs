using System.Text.Json.Nodes;
using ApplicationPackagerBuild;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Inventory;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ApplicationPackagerEngine.Tests.CommandLine;

/// <summary>
/// The command-line build against a throwaway data root whose settings carry no site
/// code and no share: every case ends at a refusal or a gate before anything is
/// downloaded, staged or published.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class BuildRunnerTests : IDisposable
{
    private const string Putty = "catalog:656d16fead444d4e";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-cli-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];
    private readonly RecordingOutput _output = new();
    private readonly string _settingsJson;

    public BuildRunnerTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, WorkbenchPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(WorkbenchPaths.DataRootVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
        Directory.CreateDirectory(_root);
        _settingsJson = new JsonObject
        {
            ["SiteCode"] = "",
            ["SiteCodeSaved"] = true,
            ["ProviderMachine"] = "",
            ["FileShareRoot"] = "",
            ["DownloadRoot"] = Path.Combine(_root, "dl"),
            ["FirstRunCompleted"] = true
        }.ToJsonString();
        File.WriteAllText(SettingsService.SettingsPath, _settingsJson);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class RecordingOutput : IBuildOutput
    {
        public List<string> Info { get; } = [];
        public List<string> Errors { get; } = [];
        void IBuildOutput.Info(string line) => Info.Add(line);
        void IBuildOutput.Error(string line) => Errors.Add(line);
    }

    private Task<BuildResult> RunAsync(params string[] args) => RunAsync(null, null, args);

    private async Task<BuildResult> RunAsync(Action<BuildRunner>? configure, Action<IServiceProvider>? afterRun, params string[] args)
    {
        var parsed = BuildArguments.Parse(args);
        Assert.True(parsed.Ok, parsed.Error);
        await using var services = new ServiceCollection()
            .AddPackagerServices()
            .AddSingleton<IBuildOutput>(_output)
            .AddSingleton<BuildRunner>()
            .BuildServiceProvider();
        var runner = services.GetRequiredService<BuildRunner>();
        configure?.Invoke(runner);
        var result = await runner.RunAsync(parsed.Options!, CancellationToken.None);
        afterRun?.Invoke(services);
        return result;
    }

    private static readonly WsusApiStatus ApiMissing = new(false, "", "", WsusApiProbe.NotInstalledMessage);
    private static readonly WsusApiStatus ApiPresent = new(true, "10.0", @"C:\Program Files\Update Services\Api\" + WsusApiProbe.AssemblyFileName, "");

    private string WorkbenchRoot => Path.Combine(_root, "Workbench");

    private bool NothingStaged =>
        !Directory.Exists(Path.Combine(WorkbenchRoot, "runs")) && !Directory.Exists(Path.Combine(_root, "dl"));

    [Fact]
    public async Task AConfigMgrPackageStopsAtTheSiteGateBeforeAnythingStages()
    {
        var expected = PublishRunService.RunGate(PublishRunTargets.ConfigMgr, new SettingsService().Load());
        Assert.NotNull(expected);

        var result = await RunAsync("--application", "putty", "--stage", "--package", "--target", "MECM");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(expected!, Assert.Single(_output.Errors));
        Assert.Equal(Putty, result.ApplicationId);
        Assert.Empty(result.Phases);
        Assert.True(NothingStaged);
    }

    [Fact]
    public async Task AnIntunePackageWithoutCredentialsStopsAtItsGate()
    {
        var result = await RunAsync("--application", "putty", "--package", "--target", "IntuneOnly");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Publish requires Intune credentials. Open Settings, Intune, to configure Tenant ID, Client ID, and Client Secret.", Assert.Single(_output.Errors));
    }

    [Fact]
    public async Task AWsusTargetWithoutAServerNamesTheArgument()
    {
        var result = await RunAsync("--application", "putty", "--package", "--target", "WSUSOnly");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("No WSUS server: pass --wsus-server or set the server in Settings, WSUS Publishing.", Assert.Single(_output.Errors));
        Assert.Null(result.Wsus);
        Assert.True(NothingStaged);
    }

    [Fact]
    public async Task AMalformedWsusServerIsRefusedBeforeItConnects()
    {
        var result = await RunAsync("--application", "putty", "--package", "--target", "WSUSOnly", "--wsus-server", "bad_name!");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("WSUS server name 'bad_name!' is not a host name.", Assert.Single(_output.Errors));
    }

    [Fact]
    public async Task WithoutTheWsusApiAWsusTargetIsRefusedBeforeItStages()
    {
        var result = await RunAsync(runner => runner.WsusProbe = () => ApiMissing, null,
            "--application", "putty", "--package", "--target", "WSUSOnly", "--wsus-server", "wsus01.contoso.com");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("WSUS publishing is unavailable: " + WsusApiProbe.NotInstalledMessage, Assert.Single(_output.Errors));
        Assert.True(NothingStaged);
    }

    [Fact]
    public void TheWsusGateFollowsTheProbeEitherWay()
    {
        var settings = new SettingsService().Load();
        settings.Wsus.ServerName = "wsus01.contoso.com";

        Assert.Equal("WSUS publishing is unavailable: " + WsusApiProbe.NotInstalledMessage,
            BuildRunner.GateRefusal(["WSUS"], settings, () => ApiMissing));
        Assert.Null(BuildRunner.GateRefusal(["WSUS"], settings, () => ApiPresent));
    }

    [Fact]
    public async Task AnAmbiguousProfileNameIsRefused()
    {
        var store = WorkbenchStore.Open();
        store.SaveProfile(WorkbenchProfile.CreateNew(Putty, "pilot-a", "Pilot"));
        store.SaveProfile(WorkbenchProfile.CreateNew(Putty, "pilot-b", "Pilot"));

        var result = await RunAsync("--application", "putty", "--profile", "Pilot", "--stage");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Profile name 'Pilot' is ambiguous for application 'catalog:656d16fead444d4e'; pass the profile id.", Assert.Single(_output.Errors));
    }

    [Fact]
    public async Task AnUnknownApplicationIsRefused()
    {
        var result = await RunAsync("--application", "no-such-app", "--stage");
        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("No definition or packager script for application 'catalog:no-such-app' under ", Assert.Single(_output.Errors));
    }

    [Fact]
    public async Task AByoIdThatIsNotStoredIsRefused()
    {
        var result = await RunAsync("--application", "byo:0123456789abcdef0123456789abcdef", "--stage");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("BYO application 'byo:0123456789abcdef0123456789abcdef' is not stored in this data root.", Assert.Single(_output.Errors));
    }

    [Fact]
    public async Task AVersionDoesNotPinADefinitionStage()
    {
        var result = await RunAsync("--application", "putty", "--stage", "--version", "0.80");
        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("--version does not apply to staging a definition", Assert.Single(_output.Errors));
        Assert.True(NothingStaged);
    }

    [Fact]
    public async Task PackageAloneWithoutASealedBuildIsRefused()
    {
        var settings = JsonNode.Parse(_settingsJson)!.AsObject();
        settings["Intune"] = new JsonObject { ["TenantId"] = "t", ["ClientId"] = "c", ["ClientSecretProtected"] = "not-a-secret" };
        File.WriteAllText(SettingsService.SettingsPath, settings.ToJsonString());

        var result = await RunAsync("--application", "putty", "--package", "--target", "IntuneOnly");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(WorkbenchBuildLauncher.NoSealedBuildMessage, Assert.Single(_output.Errors));
    }

    [Fact]
    public async Task ADefinitionThatNeedsALocalFolderNamesTheArgumentInsteadOfPrompting()
    {
        var definitions = Path.Combine(WorkbenchRoot, "definitions");
        Directory.CreateDirectory(definitions);
        File.WriteAllText(Path.Combine(definitions, "signin-tool.json"), new JsonObject
        {
            ["Schema"] = 1,
            ["Id"] = "signintool",
            ["Vendor"] = "Contoso",
            ["Application"] = "Sign-in Tool",
            ["Architecture"] = "x64",
            ["InstallerType"] = "MSI",
            ["LocalSource"] = new JsonObject { ["Mode"] = "Required" }
        }.ToJsonString());

        var result = await RunAsync("--application", "signin-tool.json", "--stage");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("custom:signintool", result.ApplicationId);
        Assert.Equal("Sign-in Tool stages from a local installer folder, and no folder exists for it: pass --local-source-folder <folder>, or set the folder in Settings.", Assert.Single(_output.Errors));
        Assert.Contains("Skipped Sign-in Tool: no installer source folder chosen.", _output.Info);
        Assert.True(NothingStaged);
    }

    [Fact]
    public async Task UiAndCliStageOperationsShareInventoryAfterRunnerRestart()
    {
        var uiSettings = new SettingsService();
        var uiExecution = new PackagerExecutionService(uiSettings, ProfileStageFixture.Service(),
            new IntuneDeploymentService(uiSettings), new DeploymentConditionsService(uiSettings),
            new ApplicationInventoryService());
        uiExecution.RecordExternalStage(new PackagerInfo { Uid = "signintool", Application = "Sign-in Tool" },
            new PackagerExecutionResult { ExitCode = 1, StdErr = "UI stage refused." },
            new RunSnapshot { ApplicationId = "custom:signintool" });

        var definitions = Path.Combine(WorkbenchRoot, "definitions");
        Directory.CreateDirectory(definitions);
        File.WriteAllText(Path.Combine(definitions, "signin-tool.json"), new JsonObject
        {
            ["Schema"] = 1,
            ["Id"] = "signintool",
            ["Vendor"] = "Contoso",
            ["Application"] = "Sign-in Tool",
            ["Architecture"] = "x64",
            ["InstallerType"] = "MSI",
            ["LocalSource"] = new JsonObject { ["Mode"] = "Required" }
        }.ToJsonString());
        var localSource = Path.Combine(_root, "local-source");
        Directory.CreateDirectory(localSource);
        File.WriteAllBytes(Path.Combine(localSource, "setup.msi"), [1, 2, 3, 4]);

        var result = await RunAsync("--application", "signin-tool.json", "--stage", "--local-source-folder", localSource);

        Assert.Equal(1, result.ExitCode);
        var reopenedOperations = new ApplicationInventoryService().GetOperations("signintool");
        Assert.Equal(2, reopenedOperations.Count);
        Assert.All(reopenedOperations, operation =>
        {
            Assert.Equal("Stage", operation.Kind);
            Assert.Equal(InventoryOutcome.Failed, operation.Outcome);
            Assert.NotNull(operation.FinishedAt);
        });
        Assert.Contains(reopenedOperations, operation => operation.Detail == "UI stage refused.");
        Assert.Contains(reopenedOperations, operation => operation.Detail?.StartsWith("No installer matching", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ContentOnlyPublishesNowhereAndExitsZero()
    {
        var result = await RunAsync("--application", "putty", "--package", "--target", "ContentOnly");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Package skipped: target ContentOnly publishes to no destination.", _output.Info);
        Assert.Empty(_output.Errors);
        Assert.True(NothingStaged);
    }

    [Fact]
    public async Task RunOverridesAreNeverWrittenToTheSettingsFile()
    {
        SettingsService? used = null;
        await RunAsync(null, services => used = services.GetRequiredService<SettingsService>(),
            "--application", "putty", "--package", "--target", "MECM", "--site-code", "LAB", "--file-server-path", @"\\fs\sccm$",
            "--wsus-server", "wsus01.lab", "--download-root", Path.Combine(_root, "other"));

        // The run's own settings instance is the one every service of the run saves through.
        var loaded = used!.Load();
        Assert.Equal("LAB", loaded.SiteCode);
        Assert.Equal(Path.Combine(_root, "other"), loaded.DownloadRoot);
        Assert.Throws<InvalidOperationException>(() => used.Save(loaded));
        Assert.Throws<InvalidOperationException>(() => used.SaveOperatorEdit(loaded));
        Assert.Equal(_settingsJson, File.ReadAllText(SettingsService.SettingsPath));
    }

    [Fact]
    public async Task AnUnreadableSettingsFileIsReportedAndTheDownloadRootArgumentStillApplies()
    {
        File.WriteAllText(SettingsService.SettingsPath, "{ not json");

        var result = await RunAsync("--application", "putty", "--package", "--target", "ContentOnly", "--download-root", Path.Combine(_root, "dl2"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_output.Errors, e => e.StartsWith("Settings file could not be read: ", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(_root, "dl2"), result.DownloadRoot);
    }

    [Fact]
    public async Task APackageOnlyTimingOverrideIsRefusedWhenTheSealedBuildCarriesTiming()
    {
        var settings = JsonNode.Parse(_settingsJson)!.AsObject();
        settings["Intune"] = new JsonObject { ["TenantId"] = "t", ["ClientId"] = "c", ["ClientSecretProtected"] = "not-a-secret" };
        File.WriteAllText(SettingsService.SettingsPath, settings.ToJsonString());
        var store = WorkbenchStore.Open();
        var snapshot = store.CreateRunSnapshot(new RunSnapshotRequest { ApplicationId = Putty, Target = RunTargets.IntuneOnly });
        BuildRecords.Write(store.DataRoot, Path.Combine(_root, "stage"), new StageManifest
        {
            AppName = "PuTTY", SoftwareVersion = "0.83", ApplicationId = Putty, BuildId = snapshot.BuildId,
            Timing = new StageTiming { EstimatedMinutes = 10, MaximumMinutes = 30 }
        });

        var result = await RunAsync("--application", "putty", "--package", "--target", "IntuneOnly", "--estimated-minutes", "5");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"--estimated-minutes and --maximum-minutes do not apply to build {snapshot.BuildId}: its sealed manifest records estimated 10 and maximum 30 minutes. Stage with them, or leave them out.",
            Assert.Single(_output.Errors));
        Assert.Empty(result.Phases);
    }

    [Fact]
    public async Task ADataRootWithoutADownloadRootNamesTheArgument()
    {
        File.WriteAllText(SettingsService.SettingsPath, """{"SiteCode":"","SiteCodeSaved":true,"DownloadRoot":""}""");
        var result = await RunAsync("--application", "putty", "--stage");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("DownloadRoot is not configured: pass --download-root or set it in Settings.", Assert.Single(_output.Errors));
    }
}
