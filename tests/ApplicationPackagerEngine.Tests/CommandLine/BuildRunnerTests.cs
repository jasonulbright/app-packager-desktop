using System.Text.Json.Nodes;
using ApplicationPackagerBuild;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerEngine.Services.Wsus;
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

    private async Task<BuildResult> RunAsync(params string[] args)
    {
        var parsed = BuildArguments.Parse(args);
        Assert.True(parsed.Ok, parsed.Error);
        await using var services = new ServiceCollection()
            .AddPackagerServices()
            .AddSingleton<IBuildOutput>(_output)
            .AddSingleton<BuildRunner>()
            .BuildServiceProvider();
        return await services.GetRequiredService<BuildRunner>().RunAsync(parsed.Options!, CancellationToken.None);
    }

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
        // A computer with the WSUS console would go on to stage; the refusal under test needs it absent.
        if (WsusApiProbe.Probe().Available) return;
        var result = await RunAsync("--application", "putty", "--package", "--target", "WSUSOnly", "--wsus-server", "wsus01.contoso.com");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("WSUS publishing is unavailable: " + WsusApiProbe.NotInstalledMessage, Assert.Single(_output.Errors));
        Assert.True(NothingStaged);
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
        await RunAsync("--application", "putty", "--package", "--target", "MECM", "--site-code", "LAB", "--file-server-path", @"\\fs\sccm$",
            "--wsus-server", "wsus01.lab", "--download-root", Path.Combine(_root, "other"));
        Assert.Equal(_settingsJson, File.ReadAllText(SettingsService.SettingsPath));

        var service = new SettingsService();
        service.ApplyRunOverrides(s => s.SiteCode = "LAB");
        Assert.Equal("LAB", service.Load().SiteCode);
        Assert.Throws<InvalidOperationException>(() => service.Save(service.Load()));
        Assert.Equal(_settingsJson, File.ReadAllText(SettingsService.SettingsPath));
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
