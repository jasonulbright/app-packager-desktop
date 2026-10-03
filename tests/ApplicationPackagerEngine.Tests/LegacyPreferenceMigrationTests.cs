using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class LegacyPreferenceMigrationTests : IDisposable
{
    private readonly WorkbenchTestRoot _t = new();
    private readonly WorkbenchDiscoveryRoots _roots;

    // The desktop settings.json shape: definition id -> entry, with every field present.
    private const string SettingsJson = """
        {
          "SiteCode": "MCM",
          "DeploymentConditions": {
            "aaaa000000000001": { "Architecture": "x64", "Languages": ["en-US", "de-DE"], "Network": "VpnOnly",
                                  "Split": "Architecture", "InstallMode": "CurrentUser", "TitleMode": "NoVersion" },
            "aaaa000000000002": { "Architecture": "Any", "Languages": [], "Network": "Any", "Split": "None",
                                  "InstallMode": "", "TitleMode": "" }
          },
          "CommandOverrides": {
            "aaaa000000000001": { "Install": "setup.exe /S /custom", "Uninstall": "uninst.exe /S" },
            "cccc000000000003": { "Install": " npp.exe /S ", "Uninstall": "" }
          }
        }
        """;

    public LegacyPreferenceMigrationTests()
    {
        var catalog = _t.NewFolder("catalog");
        _t.NewFile(Path.Combine(catalog, "a1.json"), """{ "Id": "aaaa000000000001", "Application": "Alpha" }""");
        _t.NewFile(Path.Combine(catalog, "a2.json"), """{ "Id": "aaaa000000000002", "Application": "Beta" }""");
        _t.NewFile(Path.Combine(_t.Store.CustomDefinitionsRoot, "c3.json"), """{ "Id": "cccc000000000003", "Application": "Mine" }""");
        _roots = new WorkbenchDiscoveryRoots { CatalogDefinitionsRoot = catalog };
    }

    public void Dispose() => _t.Dispose();

    private LegacyMigrationResult Migrate(JsonNode? settings = null) =>
        LegacyPreferenceMigration.Run(_t.Store, settings ?? WorkbenchTestRoot.Json(SettingsJson), _roots);

    private WorkbenchProfile Active(string applicationId) =>
        _t.Store.GetProfile(applicationId, _t.Store.GetApplicationDefinition(applicationId).ActiveProfileId);

    [Fact]
    public void Run_CreatesOneActiveMigratedProfilePerLegacyApplication()
    {
        Assert.Equal(3, Migrate().MigratedCount);
        foreach (var id in new[] { "catalog:aaaa000000000001", "catalog:aaaa000000000002", "custom:cccc000000000003" })
        {
            Assert.NotEqual("default", _t.Store.GetApplicationDefinition(id).ActiveProfileId);
            Assert.Equal("Migrated", Active(id).Name);
        }
    }

    [Fact]
    public void Run_MapsConditionsSplitInstallModeAndTitleMode()
    {
        Migrate();
        var profile = Active("catalog:aaaa000000000001");

        Assert.Equal("aaaa000000000001", profile.Get("MigratedFrom").Text);
        var operations = profile.Get("Requirements.Operations").Value!.AsArray();
        Assert.Equal(["cpu-arch", "os-language", "vpn-connected"], operations.Select(o => o!["RuleId"]!.GetValue<string>()));
        Assert.All(operations, o => Assert.Equal("Add", o!["Op"]!.GetValue<string>()));
        Assert.All(operations, o => Assert.Equal("*", o!["AppliesTo"]![0]!.GetValue<string>()));
        Assert.Equal("x64", operations[0]!["Rule"]!["Value"]!.GetValue<string>());
        Assert.Equal(["en-US", "de-DE"], operations[1]!["Rule"]!["Cultures"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.True(operations[2]!["Rule"]!["Value"]!.GetValue<bool>());
        Assert.Equal("Architecture", profile.Get("Variants.Split.Split").Text);
        Assert.Equal("CurrentUser", profile.Get("InstallMode").Text);
        Assert.Equal("NoVersion", profile.Get("Application.TitleMode").Text);
    }

    [Fact]
    public void Run_MapsOnSiteOnlyToAVpnRuleThatIsFalseAndALanguageSplitWithItsLanguages()
    {
        Migrate(WorkbenchTestRoot.Json("""
            { "DeploymentConditions": { "aaaa000000000002": { "Network": "OnSiteOnly", "Split": "Language", "Languages": ["fr-FR"] } } }
            """));
        var profile = Active("catalog:aaaa000000000002");
        var operations = profile.Get("Requirements.Operations").Value!.AsArray();
        Assert.Equal(["os-language", "vpn-connected"], operations.Select(o => o!["RuleId"]!.GetValue<string>()));
        Assert.False(operations[1]!["Rule"]!["Value"]!.GetValue<bool>());
        Assert.Equal("fr-FR", profile.Get("Variants.Split.Languages").Value!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void Run_GivesAUserAuthoredDefinitionACustomIdNotACatalogOne()
    {
        Migrate();
        Assert.True(_t.Store.GetApplicationDefinition("custom:cccc000000000003").Persisted);
        Assert.False(_t.Store.GetApplicationDefinition("catalog:cccc000000000003").Persisted);
    }

    [Fact]
    public void Run_MigratesCommandOverridesIntoTheProfile()
    {
        Migrate();
        var profile = Active("catalog:aaaa000000000001");
        Assert.Equal("setup.exe /S /custom", profile.Get("Install.Command").Text);
        Assert.Equal("uninst.exe /S", profile.Get("Uninstall.Command").Text);

        var custom = Active("custom:cccc000000000003");
        Assert.Equal("npp.exe /S", custom.Get("Install.Command").Text);
        Assert.False(custom.Get("Uninstall.Command").Found);
    }

    [Fact]
    public void Run_ProducesEffectiveSettingsThatCarryTheMigratedOverrides()
    {
        Migrate();
        var profile = Active("catalog:aaaa000000000001");
        var effective = EffectiveSettingsResolver.Resolve(
            baseManifest: WorkbenchTestRoot.Json("""{ "AppName": "7-Zip", "InstallCommandLine": "install.bat", "InstallMode": "AllUsers" }"""),
            profile: profile.Data);

        Assert.Equal("setup.exe /S /custom", effective["InstallCommand"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Profile, effective["InstallCommand"].Source);
        Assert.Equal("CurrentUser", effective["InstallMode"].Value!.GetValue<string>());
        Assert.Equal("NoVersion", effective["TitleMode"].Value!.GetValue<string>());
    }

    [Fact]
    public void Run_IsIdempotentOnASecondRun()
    {
        Migrate();
        var second = Migrate();
        Assert.Equal(0, second.MigratedCount);
        Assert.Equal(3, second.SkippedCount);
    }

    [Fact]
    public void Run_StaysIdempotentAfterTheMigratedProfileIsRenamed()
    {
        Migrate();
        var id = "catalog:aaaa000000000001";
        var renamed = Active(id);
        renamed.Name = "Renamed by hand";
        _t.Store.SaveProfile(renamed);

        Assert.Equal(0, Migrate().MigratedCount);
        Assert.Single(_t.Store.GetProfiles(id), p => !p.IsDefault);
    }

    [Fact]
    public void Run_UpdatesTheSameProfileWhenTheLegacyContentChanges()
    {
        Migrate();
        var id = "catalog:aaaa000000000001";
        var before = Active(id);
        var changed = WorkbenchTestRoot.Json(SettingsJson);
        changed["CommandOverrides"]!["aaaa000000000001"]!["Install"] = "setup.exe /S /changed";

        var result = Migrate(changed);

        Assert.Equal(1, result.MigratedCount);
        var after = Active(id);
        Assert.Equal(before.ProfileId, after.ProfileId);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal("setup.exe /S /changed", after.Get("Install.Command").Text);
    }

    [Fact]
    public void Run_DoesNotResetAnActiveProfileTheOperatorChose()
    {
        var id = "catalog:aaaa000000000001";
        var chosen = _t.NewProfile(id, "Operator choice");
        Migrate();
        Assert.Equal(chosen.ProfileId, _t.Store.GetApplicationDefinition(id).ActiveProfileId);
        Assert.Single(_t.Store.GetProfiles(id), p => p.Name == "Migrated");
    }

    [Fact]
    public void Run_LeavesTheLegacySettingsUnchanged()
    {
        var settings = WorkbenchTestRoot.Json(SettingsJson);
        var before = settings.ToJsonString();
        Migrate(settings);
        Assert.Equal(before, settings.ToJsonString());
        Assert.Equal("NoVersion", settings["DeploymentConditions"]!["aaaa000000000001"]!["TitleMode"]!.GetValue<string>());
    }

    [Fact]
    public void Run_MigratesNothingForAnApplicationWithNoLegacyEntry()
    {
        Migrate();
        var profiles = _t.Store.GetProfiles("catalog:bbbb000000000009");
        Assert.Single(profiles);
        Assert.True(profiles[0].IsDefault);
    }

    [Fact]
    public void Run_WithNoLegacyMapsMigratesNothing()
    {
        var result = Migrate(WorkbenchTestRoot.Json("""{ "SiteCode": "MCM" }"""));
        Assert.Equal(0, result.MigratedCount);
        Assert.Equal(0, result.SkippedCount);
    }
}
