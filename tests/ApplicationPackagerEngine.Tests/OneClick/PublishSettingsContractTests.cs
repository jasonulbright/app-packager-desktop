using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.OneClick;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerEngine.Tests.OneClick;

/// <summary>
/// The stored side of publishing per destination: Systems in use, the One Click
/// destinations and policy, the per-destination history, the retired .intunewin
/// option, and what Setup saves. Each test points APP_PACKAGER_DATA_ROOT at its own folder.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PublishSettingsContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "apkg-publish-settings-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = [];

    public PublishSettingsContractTests()
    {
        foreach (var name in new[] { AppDataPaths.DataRootVariable, SettingsService.LauncherSiteCodeVariable, SettingsService.LauncherProviderVariable })
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(AppDataPaths.DataRootVariable, _root);
        Environment.SetEnvironmentVariable(SettingsService.LauncherSiteCodeVariable, null);
        Environment.SetEnvironmentVariable(SettingsService.LauncherProviderVariable, null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static JsonObject Stored() => JsonNode.Parse(File.ReadAllText(SettingsService.SettingsPath))!.AsObject();

    [Fact]
    public void TestCollectionTypeDefaultsToDevicesAndRoundTripsIntoThePlan()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath,
            """{"ContentDistribution":{"AutoDistribute":true,"DeployToTestCollection":true,"TestCollectionName":"Pilot"}}""");
        var service = new SettingsService();
        var settings = service.Load();
        Assert.False(settings.ContentDistribution.TestCollectionIsUser);
        settings.ContentDistribution.TestCollectionIsUser = true;
        service.SaveOperatorEdit(settings);

        var restored = new SettingsService().Load();
        Assert.True(restored.ContentDistribution.TestCollectionIsUser);
        var scope = OneClickSettingsMap.PlanSettings(restored, consoleFound: true).Scope;
        Assert.True(scope.TestCollectionIsUser);
        Assert.Contains("deploy to user collection Pilot", ApplicationPackagerEngine.Services.OneClick.OneClickPlanner.Scope("ConfigMgr", scope));
    }

    private sealed class Host : IPublishActionHost, ISettingsReload
    {
        public List<string> Log { get; } = [];
        public int Refreshes { get; private set; }
        public int Reloads { get; private set; }
        public void RefreshPublishActionState() => Refreshes++;
        public void AppendLog(string message) => Log.Add(message);
        public void ReloadFromStore() => Reloads++;
    }

    [Fact]
    public void AStoredCreateIntuneWinValueIsIgnoredAndDroppedOnSave()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, """{ "Intune": { "DeploymentTarget": "MECM", "CreateIntuneWin": true }, "SiteCode": "MCM" }""");
        var service = new SettingsService();
        var s = service.Load();
        Assert.Equal("MECM", s.Intune.DeploymentTarget);
        service.Save(s);
        Assert.False(Stored()["Intune"]!.AsObject().ContainsKey("CreateIntuneWin"));
    }

    [Fact]
    public void TheOneClickDestinationsDefaultsAndPolicyRoundTrip()
    {
        var service = new SettingsService();
        var s = service.Load();
        s.OneClick.Destinations["package-a"] = new DestinationSet(false, true, false);
        s.OneClick.DefaultDestinations = new DestinationSet(true, true, false);
        s.OneClick.OnExisting = "Overwrite";
        service.SaveOperatorEdit(s);

        var stored = Stored()["OneClick"]!;
        Assert.True((bool)stored["Destinations"]!["package-a"]!["WSUS"]!);
        Assert.True((bool)stored["DefaultDestinations"]!["WSUS"]!);
        var back = new SettingsService().Load().OneClick;
        Assert.True(back.Destinations["PACKAGE-A"].Wsus);
        Assert.Equal("Overwrite", back.OnExisting);

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, """{ "OneClick": { "OnExisting": "Ask", "Destinations": null } }""");
        var odd = new SettingsService().Load().OneClick;
        Assert.Equal("Skip", odd.OnExisting);
        Assert.Empty(odd.Destinations);
        Assert.Null(odd.DefaultDestinations);
    }

    [Fact]
    public void ARowThatMatchesTheDefaultStoresNothingAndFollowsTheDefault()
    {
        var defaults = new DestinationSet(true, true, false);
        var rows = new[] { ("package-a", new DestinationSet(true, true, false)), ("package-b", new DestinationSet(true, false, false)) };
        var stored = OneClickSettingsMap.RowsThatDiffer(rows, defaults);
        Assert.Equal(["package-b"], stored.Keys);
        Assert.False(stored["package-b"].Wsus);
    }

    [Fact]
    public void ThePlanSettingsReadTheDefaultOrTheLegacyTargetOfAnOlderFile()
    {
        var s = new AppSettings { Intune = { DeploymentTarget = "MECMAndIntune" } };
        var (set, source) = OneClickPlanner.DestinationsFor(OneClickSettingsMap.PlanSettings(s, true), "x");
        Assert.Equal((new DestinationSet(true, false, true), "Default"), (set, source));
        s.OneClick.DefaultDestinations = new DestinationSet(false, true, false);
        Assert.Equal(new DestinationSet(false, true, false), OneClickPlanner.DestinationsFor(OneClickSettingsMap.PlanSettings(s, true), "x").Set);
    }

    [Fact]
    public void ASettingsFileWithoutSystemsInfersThemFromTheTenantAndTheServer()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsService.SettingsPath)!);
        File.WriteAllText(SettingsService.SettingsPath, """{ "Intune": { "TenantId": "contoso" }, "Wsus": { "ServerName": "wsus01" } }""");
        var systems = new SettingsService().Load().Systems!;
        Assert.True(systems.ConfigMgr && systems.Intune && systems.Wsus);
    }

    [Fact]
    public void SetupWithoutConfigMgrStoresAnEmptySiteCodeAndNamesEveryDefaultDestination()
    {
        var service = new SettingsService();
        var host = new Host();
        var setup = new SetupWindowViewModel(service, host, host)
        {
            UseConfigMgr = false,
            UseWsus = true,
            UseIntune = true,
            WsusServerName = " wsus01 ",
            IntuneTenantId = "t",
            IntuneClientId = "c"
        };
        setup.WsusUseSsl = true;
        Assert.Equal("8531", setup.WsusPort);
        setup.SaveCommand.Execute(null);

        var stored = Stored();
        Assert.Equal("", (string?)stored["SiteCode"]);
        var s = new SettingsService().Load();
        Assert.Equal((false, true, true), (s.Systems!.ConfigMgr, s.Systems.Intune, s.Systems.Wsus));
        Assert.Equal(("wsus01", 8531, true), (s.Wsus.ServerName, s.Wsus.PortNumber, s.Wsus.UseSsl));
        Assert.Equal(new DestinationSet(false, true, true), s.OneClick.DefaultDestinations);
        Assert.True(s.FirstRunCompleted);
        Assert.Equal(["Setup complete. One Click publishes to WSUS and Intune; change it in Settings, One Click Settings."], host.Log);
        Assert.Equal((1, 1), (host.Refreshes, host.Reloads));
    }

    [Fact]
    public void SetupWithConfigMgrKeepsTheSiteCodeAndAnEmptySelectionNamesNoDestination()
    {
        var host = new Host();
        var setup = new SetupWindowViewModel(new SettingsService(), host, host) { UseConfigMgr = true, SiteCode = " PS1 " };
        setup.SaveCommand.Execute(null);
        Assert.Equal("PS1", (string?)Stored()["SiteCode"]);
        Assert.Equal("Setup complete. One Click publishes to ConfigMgr; change it in Settings, One Click Settings.", host.Log[0]);
        Assert.Equal("Setup complete. One Click publishes to no destination; change it in Settings, One Click Settings.",
            OneClickSettingsMap.SetupCompleteLine(new DestinationSet()));
    }

    [Fact]
    public void APs1StageNamesItsManifestByTheWrittenLineOrTheStageCompleteFolder()
    {
        Assert.Equal(@"C:\dl\App\1.0\stage-manifest.json",
            PackagerStageOutput.ManifestPath("x\r\nWrote stage manifest         : C:\\dl\\App\\1.0\\stage-manifest.json\r\nStage complete               : C:\\other"));
        Assert.Equal(@"C:\dl\App\2.0\stage-manifest.json", PackagerStageOutput.ManifestPath("Stage complete               : C:\\dl\\App\\2.0\r\n"));
        Assert.Equal(@"C:\m.json", PackagerStageOutput.ManifestPath("Read stage manifest          : C:\\m.json"));
        Assert.Null(PackagerStageOutput.ManifestPath("Downloaded installer\r\nDone"));
        Assert.Null(PackagerStageOutput.ExistingManifest("Stage complete : " + Path.Combine(_root, "missing")));

        var folder = Directory.CreateDirectory(Path.Combine(_root, "App", "3.0")).FullName;
        File.WriteAllText(Path.Combine(folder, "stage-manifest.json"), "{}");
        Assert.Equal(Path.Combine(folder, "stage-manifest.json"), PackagerStageOutput.ExistingManifest("Stage complete : " + folder));
        Assert.Contains("no stage manifest exists", PackagerStageOutput.NoManifestMessage);
    }

    [Fact]
    public void AnIntunewinNewerThanTheManifestIsReusedAndAnOlderOneIsNot()
    {
        var content = Directory.CreateDirectory(Path.Combine(_root, "stage", "Contoso", "1.0")).FullName;
        var manifestPath = Path.Combine(content, "stage-manifest.json");
        File.WriteAllText(manifestPath, "{}");
        var manifest = new StageManifest { AppName = "Contoso Tool", SoftwareVersion = "1.0" };
        var intunewin = IntuneWinArtifact.PathFor(manifest, content);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(content)!, "Contoso_Tool-1.0.intunewin"), intunewin);
        Assert.Null(IntuneWinArtifact.Reusable(manifest, content));

        File.WriteAllText(intunewin, "x");
        File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(intunewin, DateTime.UtcNow);
        Assert.Equal(intunewin, IntuneWinArtifact.Reusable(manifest, content));

        File.SetLastWriteTimeUtc(intunewin, DateTime.UtcNow.AddMinutes(-10));
        Assert.Null(IntuneWinArtifact.Reusable(manifest, content));
    }

    [Fact]
    public void APs1VersionCheckWithoutASiteCodePassesThePackagerDefault()
    {
        Assert.Equal("MCM", PackagerExecutionService.PackagerSiteCode(""));
        Assert.Equal("MCM", PackagerExecutionService.PackagerSiteCode(null));
        Assert.Equal("PS1", PackagerExecutionService.PackagerSiteCode(" PS1 "));
    }
}
