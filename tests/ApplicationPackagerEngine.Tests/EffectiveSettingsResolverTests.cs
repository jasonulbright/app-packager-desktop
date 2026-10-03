using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services.Workbench;

namespace ApplicationPackagerEngine.Tests;

public sealed class EffectiveSettingsResolverTests
{
    private static JsonObject J(string json) => WorkbenchTestRoot.Json(json);

    private static readonly JsonObject GlobalDefaults =
        J("""{ "Description": "global text", "EstimatedRuntimeMins": 15, "MaximumRuntimeMins": 30, "TitleMode": "IncludeVersion" }""");

    private static readonly JsonObject BaseManifest = J("""
        {
          "AppName": "Contoso Reader", "Publisher": "Contoso", "Description": "packager text",
          "SoftwareVersion": "3.1", "InstallCommandLine": "install.bat",
          "InstallationBehaviorType": "InstallForSystem",
          "Detection": { "Type": "RegistryKeyValue", "RegistryKeyRelative": "SOFTWARE\\Contoso" }
        }
        """);

    [Fact]
    public void FieldMap_HasTheEighteenFieldsInTableOrder()
    {
        Assert.Equal(
            ["DisplayName", "Publisher", "Description", "TitleMode", "Icon", "PinnedVersion", "InstallMode",
             "InstallCommand", "UninstallCommand", "InstallScriptMode", "UninstallScriptMode", "EstimatedMinutes",
             "MaximumMinutes", "Context", "LogonRequirement", "UserInteraction", "ScriptHost", "Detection"],
            EffectiveSettingsResolver.FieldMap.Select(f => f.Field));
        Assert.Equal(7, EffectiveSettingsResolver.FieldMap.Count(f => f.NeedsStaging));
        Assert.Equal(4, EffectiveSettingsResolver.FieldMap.Count(f => f.GlobalPath is not null));
        Assert.Equal(6, EffectiveSettingsResolver.FieldMap.Count(f => f.PackagerPath is null));
    }

    [Fact]
    public void Resolve_ReportsTheSourceOfEachPrecedenceLevel()
    {
        var result = EffectiveSettingsResolver.Resolve(
            globalDefaults: J("""{ "EstimatedRuntimeMins": 15, "MaximumRuntimeMins": 30 }"""),
            baseManifest: J("""{ "AppName": "Base App", "InstallCommandLine": "install.bat" }"""),
            profile: J("""{ "Application": { "DisplayName": "Profile App" }, "Timing": { "EstimatedMinutes": 20 } }"""),
            runOverrides: J("""{ "MaximumMinutes": 45 }"""));

        Assert.Equal("Profile App", result["DisplayName"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Profile, result["DisplayName"].Source);
        Assert.Equal(SettingSource.Packager, result["InstallCommand"].Source);
        Assert.Equal(20, result["EstimatedMinutes"].Value!.GetValue<int>());
        Assert.Equal(SettingSource.Profile, result["EstimatedMinutes"].Source);
        Assert.Equal(45, result["MaximumMinutes"].Value!.GetValue<int>());
        Assert.Equal(SettingSource.Run, result["MaximumMinutes"].Source);
    }

    [Fact]
    public void Resolve_ReportsNeedsStagingForAnUnresolvedStageTimeField()
    {
        var result = EffectiveSettingsResolver.Resolve();
        Assert.Equal(SettingSource.NeedsStaging, result["PinnedVersion"].Source);
        Assert.Equal(SettingSource.NeedsStaging, result["Detection"].Source);
        Assert.Equal(SettingSource.Global, result["ScriptHost"].Source);
        Assert.Null(result["ScriptHost"].Value);
    }

    [Fact]
    public void Resolve_ReportsNeedsStagingForTheIconAndGlobalForTheTitleMode()
    {
        var result = EffectiveSettingsResolver.Resolve(GlobalDefaults);
        Assert.Equal(SettingSource.NeedsStaging, result["Icon"].Source);
        Assert.Equal(SettingSource.Global, result["TitleMode"].Source);
        Assert.Equal("IncludeVersion", result["TitleMode"].Value!.GetValue<string>());
    }

    [Fact]
    public void Resolve_LetsAVariantOutrankTheProfileAndATargetOutrankTheVariant()
    {
        var profile = J("""{ "Timing": { "EstimatedMinutes": 10 }, "Variants": { "Overrides": { "x64": { "EstimatedMinutes": 20 } } } }""");

        var variant = EffectiveSettingsResolver.Resolve(profile: profile, variant: "x64");
        Assert.Equal(20, variant["EstimatedMinutes"].Value!.GetValue<int>());
        Assert.Equal(SettingSource.Variant, variant["EstimatedMinutes"].Source);

        var target = EffectiveSettingsResolver.Resolve(profile: profile, variant: "x64", targetOverrides: J("""{ "EstimatedMinutes": 25 }"""));
        Assert.Equal(25, target["EstimatedMinutes"].Value!.GetValue<int>());
        Assert.Equal(SettingSource.Target, target["EstimatedMinutes"].Source);
    }

    [Fact]
    public void Resolve_LetsARunOverrideOutrankTheVariant()
    {
        var profile = J("""{ "Install": { "Command": "profile.bat" }, "Variants": { "Overrides": { "x64": { "InstallCommand": "variant.bat" } } } }""");

        var variant = EffectiveSettingsResolver.Resolve(baseManifest: BaseManifest, profile: profile, variant: "x64");
        Assert.Equal("variant.bat", variant["InstallCommand"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Variant, variant["InstallCommand"].Source);

        var run = EffectiveSettingsResolver.Resolve(baseManifest: BaseManifest, profile: profile, variant: "x64",
            runOverrides: J("""{ "InstallCommand": "run.bat" }"""));
        Assert.Equal("run.bat", run["InstallCommand"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Run, run["InstallCommand"].Source);
    }

    [Fact]
    public void Resolve_TreatsAnExplicitNullRunOverrideAsAValue()
    {
        var profile = J("""{ "Install": { "Command": "setup.exe /S" }, "Timing": { "EstimatedMinutes": 10 } }""");
        var result = EffectiveSettingsResolver.Resolve(profile: profile, runOverrides: J("""{ "InstallCommand": null, "EstimatedMinutes": null }"""));

        Assert.Equal(SettingSource.Run, result["InstallCommand"].Source);
        Assert.Null(result["InstallCommand"].Value);
        Assert.Equal(SettingSource.Run, result["EstimatedMinutes"].Source);
        Assert.Null(result["EstimatedMinutes"].Value);
    }

    [Fact]
    public void Resolve_TreatsAnExplicitProfileNullAsAValue()
    {
        var result = EffectiveSettingsResolver.Resolve(baseManifest: J("""{ "Icon": "app-icon.ico" }"""),
            profile: J("""{ "Application": { "Icon": null } }"""));
        Assert.Equal(SettingSource.Profile, result["Icon"].Source);
        Assert.Null(result["Icon"].Value);
    }

    [Fact]
    public void Resolve_TakesThePackagerValueOverTheGlobalDefault()
    {
        var result = EffectiveSettingsResolver.Resolve(GlobalDefaults, BaseManifest, profile: null);
        Assert.Equal("packager text", result["Description"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Packager, result["Description"].Source);
    }

    [Fact]
    public void Resolve_TakesTheProfileValueOverThePackagerValue()
    {
        var result = EffectiveSettingsResolver.Resolve(GlobalDefaults, BaseManifest,
            J("""{ "Application": { "DisplayName": "Reader (Managed)" } }"""));
        Assert.Equal("Reader (Managed)", result["DisplayName"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Profile, result["DisplayName"].Source);
    }

    [Fact]
    public void Resolve_DistinguishesAnExplicitNullFromAnAbsentKey()
    {
        var none = EffectiveSettingsResolver.Resolve(GlobalDefaults, BaseManifest, J("""{ "Application": { "Description": null } }"""));
        Assert.Null(none["Description"].Value);
        Assert.Equal(SettingSource.Profile, none["Description"].Source);

        var inherited = EffectiveSettingsResolver.Resolve(GlobalDefaults, BaseManifest, J("""{ "Application": { "Publisher": "Contoso Ltd" } }"""));
        Assert.Equal("packager text", inherited["Description"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Packager, inherited["Description"].Source);
    }

    [Fact]
    public void Resolve_TreatsAnExplicitEmptyStringAsAnOverride()
    {
        var result = EffectiveSettingsResolver.Resolve(GlobalDefaults, BaseManifest, J("""{ "Application": { "Description": "" } }"""));
        Assert.Equal("", result["Description"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Profile, result["Description"].Source);
    }

    [Fact]
    public void Resolve_SkipsAnEmptyPackagerValueAndKeepsAnEmptyGlobalValue()
    {
        var result = EffectiveSettingsResolver.Resolve(J("""{ "Description": "" }"""), J("""{ "Description": "" }"""));
        Assert.Equal(SettingSource.Global, result["Description"].Source);
        Assert.Equal("", result["Description"].Value!.GetValue<string>());
    }

    [Fact]
    public void Resolve_ReadsADesktopStageManifest()
    {
        var manifest = System.Text.Json.JsonSerializer.SerializeToNode(new ApplicationPackagerEngine.Models.StageManifest
        {
            AppName = "7-Zip", Publisher = "Igor Pavlov", SoftwareVersion = "24.09", Icon = "app-icon.ico"
        });
        var result = EffectiveSettingsResolver.Resolve(baseManifest: manifest);
        Assert.Equal(SettingSource.Packager, result["DisplayName"].Source);
        Assert.Equal("24.09", result["PinnedVersion"].Value!.GetValue<string>());
        Assert.Equal(SettingSource.Packager, result["Detection"].Source);
        Assert.Equal(SettingSource.NeedsStaging, result["InstallCommand"].Source);
    }
}
