using System.Text.Json.Nodes;
using ApplicationPackagerBuild;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerEngine.Tests.CommandLine;

public sealed class ApplicationResolverTests
{
    private static WorkbenchApplication App(string id, ApplicationOrigin origin, string? source) =>
        new(id, ApplicationIds.ToKey(id), origin, id, null, source, true, ApplicationIds.DefaultProfileId, null, false);

    private static readonly IReadOnlyList<WorkbenchApplication> Discovered =
    [
        App("catalog:656d16fead444d4e", ApplicationOrigin.Catalog, @"C:\app\Definitions\putty.json"),
        App("catalog:7zip", ApplicationOrigin.Catalog, @"C:\app\Definitions\7zip.json"),
        App("catalog:package-git", ApplicationOrigin.Catalog, @"C:\pk\package-git.ps1"),
        App("custom:inhouse", ApplicationOrigin.Custom, @"C:\data\Workbench\definitions\inhouse-tool.json"),
        App("custom:7zip", ApplicationOrigin.Custom, @"C:\data\Workbench\definitions\7zip.json"),
        App("byo:0123456789abcdef0123456789abcdef", ApplicationOrigin.Byo, null)
    ];

    [Theory]
    [InlineData("catalog:7zip", "catalog:7zip")]
    [InlineData("custom:inhouse", "custom:inhouse")]
    [InlineData("byo:0123456789abcdef0123456789abcdef", "byo:0123456789abcdef0123456789abcdef")]
    [InlineData("CATALOG:7zip", "catalog:7zip")]
    [InlineData("custom:not-listed", "custom:not-listed")]
    public void APrefixedIdIsTakenAsGiven(string value, string expected) =>
        Assert.Equal(expected, ApplicationResolver.Resolve(value, Discovered));

    [Fact]
    public void AnInvalidPrefixedIdIsRefused() =>
        Assert.Equal("Invalid ApplicationId 'catalog:a b'; expected catalog:<name>, custom:<name> or byo:<id>.",
            Assert.Throws<BuildRefusal>(() => ApplicationResolver.Resolve("catalog:a b", Discovered)).Message);

    [Theory]
    [InlineData("7zip", "catalog:7zip")]
    [InlineData("7zip.json", "catalog:7zip")]
    [InlineData("656d16fead444d4e", "catalog:656d16fead444d4e")]
    [InlineData("putty", "catalog:656d16fead444d4e")]
    [InlineData("PuTTY.json", "catalog:656d16fead444d4e")]
    [InlineData(@"C:\anywhere\putty.json", "catalog:656d16fead444d4e")]
    [InlineData("package-git.ps1", "catalog:package-git")]
    [InlineData("package-git", "catalog:package-git")]
    [InlineData("inhouse", "custom:inhouse")]
    [InlineData("inhouse-tool.json", "custom:inhouse")]
    [InlineData("unknown-app", "catalog:unknown-app")]
    public void ABareValueIsCatalogThenCustomThenTheCatalogId(string value, string expected) =>
        Assert.Equal(expected, ApplicationResolver.Resolve(value, Discovered));

    [Fact]
    public void SourceFindsCatalogAndCustomApplicationsOnly()
    {
        Assert.Equal(@"C:\pk\package-git.ps1", ApplicationResolver.Source("CATALOG:PACKAGE-GIT", Discovered)?.SourcePath);
        Assert.Equal(@"C:\data\Workbench\definitions\7zip.json", ApplicationResolver.Source("custom:7zip", Discovered)?.SourcePath);
        Assert.Null(ApplicationResolver.Source("byo:0123456789abcdef0123456789abcdef", Discovered));
        Assert.Null(ApplicationResolver.Source("catalog:missing", Discovered));
    }
}

public sealed class ProfileResolverTests : IDisposable
{
    private const string App = "catalog:7zip";
    private readonly WorkbenchTestRoot _t = new();

    public void Dispose() => _t.Dispose();

    private void Save(string profileId, string name) => _t.Store.SaveProfile(WorkbenchProfile.CreateNew(App, profileId, name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("Default")]
    public void TheDefaultProfileNeedsNoStore(string? value) =>
        Assert.Equal(ApplicationIds.DefaultProfileId, ProfileResolver.Resolve(_t.Store, App, value));

    [Fact]
    public void AProfileResolvesByIdThenByName()
    {
        Save("managed01", "Managed");
        Save("pilot", "Ring 1");
        Save("ring1", "pilot");
        Assert.Equal("managed01", ProfileResolver.Resolve(_t.Store, App, "managed01"));
        Assert.Equal("managed01", ProfileResolver.Resolve(_t.Store, App, "managed"));
        Assert.Equal("pilot", ProfileResolver.Resolve(_t.Store, App, "pilot"));
        Assert.Equal("pilot", ProfileResolver.Resolve(_t.Store, App, "Ring 1"));
    }

    [Fact]
    public void AMissingProfileIsRefused() =>
        Assert.Equal("Profile 'Managed' was not found for application 'catalog:7zip'.",
            Assert.Throws<BuildRefusal>(() => ProfileResolver.Resolve(_t.Store, App, "Managed")).Message);

    [Fact]
    public void ADuplicateNameIsAmbiguous()
    {
        Save("pilot-a", "Pilot");
        Save("pilot-b", "Pilot");
        Assert.Equal("Profile name 'Pilot' is ambiguous for application 'catalog:7zip'; pass the profile id.",
            Assert.Throws<BuildRefusal>(() => ProfileResolver.Resolve(_t.Store, App, "Pilot")).Message);
        Assert.Equal("pilot-b", ProfileResolver.Resolve(_t.Store, App, "pilot-b"));
    }
}

public sealed class WsusSettingsResolverTests
{
    private static readonly WsusSettings Stored = new()
    {
        ServerName = "wsus01.lab", PortNumber = 8530, UseSsl = false, Classification = "Tools", ApprovalGroup = "Pilot", DeclineSuperseded = true
    };

    private static BuildOptions Options(Func<BuildOptions, BuildOptions>? change = null) =>
        (change ?? (o => o))(new BuildOptions { Application = "a", Stage = true });

    [Fact]
    public void WithoutOverridesTheStoredSettingsApply()
    {
        var s = WsusSettingsResolver.Resolve(Stored, Options());
        Assert.Equal(("wsus01.lab", 8530, false, "Tools", "Pilot", true),
            (s.ServerName, s.PortNumber, s.UseSsl, s.Classification, s.ApprovalGroup, s.DeclineSuperseded));
    }

    [Fact]
    public void EachArgumentOutranksTheStoredValue()
    {
        var s = WsusSettingsResolver.Resolve(Stored, Options(o => o with
        {
            WsusServer = "wsus02.lab", WsusPort = 9000, WsusUseSsl = true, WsusClassification = "SecurityUpdates",
            WsusApprovalGroup = "", WsusDeclineSuperseded = false
        }));
        Assert.Equal(("wsus02.lab", 9000, true, "SecurityUpdates", "", false),
            (s.ServerName, s.PortNumber, s.UseSsl, s.Classification, s.ApprovalGroup, s.DeclineSuperseded));
    }

    [Fact]
    public void AStoredPortBelongsToTheStoredSslChoice()
    {
        Assert.Equal(8531, WsusSettingsResolver.Resolve(Stored, Options(o => o with { WsusUseSsl = true })).PortNumber);
        var ssl = new WsusSettings { ServerName = "w", PortNumber = 443, UseSsl = true };
        Assert.Equal(443, WsusSettingsResolver.Resolve(ssl, Options(o => o with { WsusUseSsl = true })).PortNumber);
        Assert.Equal(8530, WsusSettingsResolver.Resolve(ssl, Options(o => o with { WsusUseSsl = false })).PortNumber);
        Assert.Equal(8080, WsusSettingsResolver.Resolve(ssl, Options(o => o with { WsusUseSsl = false, WsusPort = 8080 })).PortNumber);
    }

    [Fact]
    public void NoStoredSectionReadsAsTheDefaults()
    {
        var s = WsusSettingsResolver.Resolve(null, Options(o => o with { WsusServer = "wsus01.lab" }));
        Assert.Equal(("wsus01.lab", 8530, WsusClassifications.Updates), (s.ServerName, s.PortNumber, s.Classification));
    }
}

public sealed class BuildRunnerRulesTests
{
    [Theory]
    [InlineData("ContentOnly", "")]
    [InlineData("MECM", "ConfigMgr")]
    [InlineData("MECMAndIntune", "ConfigMgr,Intune")]
    [InlineData("IntuneOnly", "Intune")]
    [InlineData("MECMAndWSUS", "ConfigMgr,WSUS")]
    [InlineData("WSUSOnly", "WSUS")]
    public void ATargetPublishesToItsDestinationsConfigMgrFirst(string target, string expected) =>
        Assert.Equal(expected, string.Join(",", BuildRunner.Destinations(target)));

    [Fact]
    public void RunOverridesApplyToTheLoadedSettingsOnly()
    {
        var settings = new AppSettings();
        BuildRunner.ApplyOverrides(settings, new BuildOptions
        {
            DownloadRoot = @"D:\ap", SiteCode = "LAB", ProviderMachineName = "cm01.lab", FileServerPath = @"\\fs\c$",
            PackagersRoot = @"D:\pk", EstimatedMinutes = 7, MaximumMinutes = 9, WsusServer = "wsus01.lab", LocalSourceFolder = @"D:\src"
        }, "def-id");
        Assert.Equal((@"D:\ap", "LAB", "cm01.lab", @"\\fs\c$", @"D:\pk", 7, 9, "wsus01.lab", @"D:\src"),
            (settings.DownloadRoot, settings.SiteCode, settings.ProviderMachine, settings.FileShareRoot, settings.PackagersRoot,
             settings.EstimatedRuntimeMins, settings.MaximumRuntimeMins, settings.Wsus.ServerName, settings.LocalSourceFolders["def-id"]));

        var untouched = new AppSettings { SiteCode = "MCM", DownloadRoot = @"C:\temp\ap" };
        BuildRunner.ApplyOverrides(untouched, new BuildOptions { LocalSourceFolder = @"D:\src" });
        Assert.Equal(("MCM", @"C:\temp\ap", 15, 30), (untouched.SiteCode, untouched.DownloadRoot, untouched.EstimatedRuntimeMins, untouched.MaximumRuntimeMins));
        Assert.Empty(untouched.LocalSourceFolders);
    }

    [Fact]
    public void TheSnapshotRecordsTimingAndTheVersionOfARunThatStagesIt()
    {
        var options = new BuildOptions { EstimatedMinutes = 10, MaximumMinutes = 25, Version = "1.2" };
        Assert.Equal("""{"EstimatedMinutes":10,"MaximumMinutes":25,"PinnedVersion":"1.2"}""", BuildRunner.RunOverrides(options, pinVersion: true).ToJsonString());
        Assert.Equal("""{"EstimatedMinutes":10,"MaximumMinutes":25}""", BuildRunner.RunOverrides(options, pinVersion: false).ToJsonString());
        Assert.Equal("{}", BuildRunner.RunOverrides(new BuildOptions(), pinVersion: true).ToJsonString());
    }

    [Fact]
    public void PackageAloneTakesTheNewestSealedBuildOrTheNewestOfTheVersion()
    {
        JsonObject Record(string id, string version) => new() { ["BuildId"] = id, ["SoftwareVersion"] = version };
        IReadOnlyList<JsonObject> records = [Record("20261003-120000-c", "2.0"), Record("20261002-120000-b", "1.9"), Record("20261001-120000-a", "1.9")];
        Assert.Equal("20261003-120000-c", BuildRunner.SelectSealedBuild(records, null)!["BuildId"]!.GetValue<string>());
        Assert.Equal("20261002-120000-b", BuildRunner.SelectSealedBuild(records, "1.9")!["BuildId"]!.GetValue<string>());
        Assert.Null(BuildRunner.SelectSealedBuild(records, "3.0"));
        Assert.Null(BuildRunner.SelectSealedBuild([], null));
    }
}
