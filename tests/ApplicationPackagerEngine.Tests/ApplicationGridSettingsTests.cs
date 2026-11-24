using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Supersedence;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-106: the per-application grid of Settings. Each column writes the stored value that the package run reads, and the package run
/// then does what the column says. The tests read the value back through the same code that packaging uses.
/// </summary>
public class ApplicationGridSettingsTests
{
    private const string Definition = "7zip";
    private const string AppId = "catalog:aaaaaaaaaaaaaaaa";

    private static Dictionary<string, DeploymentConditionEntry> NewMap() => new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheArchitectureChoiceBecomesTheRequirementThatThePackageRunWrites()
    {
        var map = NewMap();
        Assert.True(ApplicationGridSettings.SetArchitecture(map, Definition, ApplicationGridSettings.ArchitectureArm64));
        var rules = DeploymentConditionsService.BuildRequirements(ApplicationGridSettings.Find(map, Definition));
        var rule = Assert.Single(rules);
        Assert.Equal("cpu-arch", rule.ConditionId);
        Assert.Equal("ARM64", rule.Value);

        ApplicationGridSettings.SetArchitecture(map, Definition, ApplicationGridSettings.ArchitectureX64);
        Assert.Equal("x64", Assert.Single(DeploymentConditionsService.BuildRequirements(ApplicationGridSettings.Find(map, Definition))).Value);
    }

    [Fact]
    public void TheArchitectureSplitMakesOneDeploymentTypeForEachArchitecture()
    {
        var map = NewMap();
        ApplicationGridSettings.SetArchitecture(map, Definition, ApplicationGridSettings.ArchitectureSplit);
        var entry = ApplicationGridSettings.Find(map, Definition);
        Assert.Equal("Architecture", entry!.Split);
        Assert.Equal(ApplicationGridSettings.ArchitectureSplit, ApplicationGridSettings.ArchitectureOf(entry));
        Assert.Empty(DeploymentConditionsService.BuildRequirements(entry));

        var manifest = new StageManifest();
        var dir = Directory.CreateTempSubdirectory().FullName;
        try { DeploymentConditionsService.ApplyVariantSplit(manifest, entry, dir); }
        finally { Directory.Delete(dir, true); }
        Assert.Equal(["ARM64", "x64"], manifest.DeploymentTypes!.Select(d => d.NameSuffix).ToArray());
    }

    [Fact]
    public void LeavingTheSplitRemovesIt()
    {
        var map = NewMap();
        ApplicationGridSettings.SetArchitecture(map, Definition, ApplicationGridSettings.ArchitectureSplit);
        ApplicationGridSettings.SetArchitecture(map, Definition, ApplicationGridSettings.PackagerDefault);
        Assert.Null(ApplicationGridSettings.Find(map, Definition));
    }

    [Theory]
    [InlineData(ApplicationGridSettings.ScopeSystem, "AllUsers")]
    [InlineData(ApplicationGridSettings.ScopeUser, "CurrentUser")]
    public void TheInstallScopeIsTheInstallModeThatStagingReads(string choice, string installMode)
    {
        var map = NewMap();
        Assert.True(ApplicationGridSettings.SetScope(map, Definition, choice));
        Assert.Equal(installMode, ApplicationGridSettings.Find(map, Definition)!.InstallMode);
        Assert.True(DeploymentConditionValues.IsInstallMode(ApplicationGridSettings.Find(map, Definition)!.InstallMode));
        Assert.Equal(choice, ApplicationGridSettings.ScopeOf(ApplicationGridSettings.Find(map, Definition)));

        ApplicationGridSettings.SetScope(map, Definition, ApplicationGridSettings.InstallerDefault);
        Assert.Null(ApplicationGridSettings.Find(map, Definition));
    }

    [Theory]
    [InlineData(ApplicationGridSettings.IncludeVersion, TitleMode.IncludeVersion, "Audacity 3.7.9")]
    [InlineData(ApplicationGridSettings.NoVersion, TitleMode.NoVersion, "Audacity")]
    [InlineData(ApplicationGridSettings.FollowDefault, TitleMode.Default, "Audacity 3.7.9")]
    public void TheVersionInTitleChoiceIsTheTitleModeOfThePackageRun(string choice, TitleMode expected, string title)
    {
        var map = NewMap();
        ApplicationGridSettings.SetTitle(map, Definition, choice);
        var stored = ApplicationGridSettings.Find(map, Definition)?.TitleMode;
        var mode = ApplicationTitle.ResolveMode(null, stored, false, false);
        Assert.Equal(expected, mode);
        Assert.Equal(title, ApplicationTitle.Resolve("Audacity 3.7.9", "3.7.9", mode));
    }

    [Fact]
    public void AWordThatIsNotAChoiceChangesNothing()
    {
        var map = NewMap();
        Assert.False(ApplicationGridSettings.SetArchitecture(map, Definition, "x86 only"));
        Assert.False(ApplicationGridSettings.SetScope(map, Definition, "Both"));
        Assert.False(ApplicationGridSettings.SetTitle(map, Definition, "Always"));
        Assert.False(ApplicationGridSettings.SetScope(map, null, ApplicationGridSettings.ScopeUser));
        Assert.Empty(map);
    }

    [Fact]
    public void AnEditKeepsTheConditionsThatTheGridDoesNotShow()
    {
        var map = NewMap();
        map[Definition] = new DeploymentConditionEntry { Network = "VpnOnly", Languages = ["de-DE"] };
        ApplicationGridSettings.SetScope(map, Definition, ApplicationGridSettings.ScopeUser);
        ApplicationGridSettings.SetScope(map, Definition, ApplicationGridSettings.InstallerDefault);
        var entry = ApplicationGridSettings.Find(map, Definition)!;
        Assert.Equal("VpnOnly", entry.Network);
        Assert.Equal(["de-DE"], entry.Languages);
    }

    [Fact]
    public void ACopyDoesNotChangeTheStoredMapUntilTheCallerWritesItBack()
    {
        var stored = NewMap();
        stored[Definition] = new DeploymentConditionEntry { TitleMode = "NoVersion" };
        var work = ApplicationGridSettings.Copy(stored);
        ApplicationGridSettings.SetTitle(work, Definition, ApplicationGridSettings.IncludeVersion);
        Assert.Equal("NoVersion", stored[Definition].TitleMode);
        Assert.Equal("IncludeVersion", work[Definition].TitleMode);
    }

    [Theory]
    [InlineData("Minimum", VersionRule.Minimum)]
    [InlineData("Exact", VersionRule.Exact)]
    public void TheVersionRuleChoiceIsTheRuleThatTheDetectionUses(string choice, VersionRule expected)
    {
        var settings = new SupersedenceSettings { ConfigMgrVersionRule = expected == VersionRule.Minimum ? "Exact" : "Minimum" };
        Assert.True(SupersedenceGrid.SetRuleChoice(settings, AppId, choice));
        Assert.Equal(choice, SupersedenceGrid.RuleChoiceOf(settings, AppId));
        Assert.Equal(expected, SupersedenceResolver.Resolve(settings, AppId, null).ConfigMgrRule.Value);

        Assert.True(SupersedenceGrid.SetRuleChoice(settings, AppId, SupersedenceGrid.FollowDefault));
        Assert.Equal(SupersedenceGrid.FollowDefault, SupersedenceGrid.RuleChoiceOf(settings, AppId));
        Assert.Empty(settings.TitleOverrides);
    }

    [Fact]
    public void TheRuleAndTheModeOfOneApplicationDoNotEraseEachOther()
    {
        var settings = new SupersedenceSettings();
        SupersedenceGrid.SetChoice(settings, AppId, "Replace");
        SupersedenceGrid.SetRuleChoice(settings, AppId, "Exact");
        SupersedenceGrid.SetChoice(settings, AppId, SupersedenceGrid.FollowDefault);
        Assert.Equal("Exact", SupersedenceGrid.RuleChoiceOf(settings, AppId));
        SupersedenceGrid.SetChoice(settings, AppId, "Replace");
        SupersedenceGrid.SetRuleChoice(settings, AppId, SupersedenceGrid.FollowDefault);
        Assert.Equal("Replace", SupersedenceGrid.ChoiceOf(settings, AppId));
        Assert.False(SupersedenceGrid.SetRuleChoice(settings, AppId, "Equals or newer"));
        Assert.False(SupersedenceGrid.SetRuleChoice(settings, null, "Exact"));
    }

    [Fact]
    public void SupersedenceOnForcesTheVersionIntoTheTitleWhateverTheColumnSays()
    {
        var settings = new AppSettings();
        SupersedenceGrid.SetChoice(settings.Supersedence, AppId, "Replace");
        var map = NewMap();
        ApplicationGridSettings.SetTitle(map, Definition, ApplicationGridSettings.NoVersion);
        settings.DeploymentConditions = map;

        var manifest = new StageManifest { AppName = "Audacity", SoftwareVersion = "3.7.9", ApplicationId = AppId };
        var title = ApplicationPackagerUI.Services.PackagerExecutionService.RunApplicationTitle(
            manifest, settings.DeploymentConditions[Definition].TitleMode, settings, AppId);
        Assert.Equal("Audacity - 3.7.9", title.Replace(" (x64)", ""));

        SupersedenceGrid.SetChoice(settings.Supersedence, AppId, "Off");
        var off = ApplicationPackagerUI.Services.PackagerExecutionService.RunApplicationTitle(
            manifest, settings.DeploymentConditions[Definition].TitleMode, settings, AppId);
        Assert.Equal("Audacity", off.Replace(" (x64)", ""));
    }

    [Fact]
    public void ABulkChoiceOnManyApplicationsWritesEachOne()
    {
        var map = NewMap();
        foreach (var id in new[] { "a", "b", "c" }) ApplicationGridSettings.SetScope(map, id, ApplicationGridSettings.ScopeUser);
        Assert.All(map.Values, e => Assert.Equal("CurrentUser", e.InstallMode));
        foreach (var id in new[] { "a", "b", "c" }) ApplicationGridSettings.SetScope(map, id, ApplicationGridSettings.InstallerDefault);
        Assert.Empty(map);
    }

    [Fact]
    public void StoredValuesOfAnEarlierVersionStillShowTheirChoice()
    {
        var entry = new DeploymentConditionEntry { Architecture = "ARM64", InstallMode = "AllUsers", TitleMode = "IncludeVersion" };
        Assert.Equal(ApplicationGridSettings.ArchitectureArm64, ApplicationGridSettings.ArchitectureOf(entry));
        Assert.Equal(ApplicationGridSettings.ScopeSystem, ApplicationGridSettings.ScopeOf(entry));
        Assert.Equal(ApplicationGridSettings.IncludeVersion, ApplicationGridSettings.TitleOf(entry));
        Assert.Equal(ApplicationGridSettings.PackagerDefault, ApplicationGridSettings.ArchitectureOf(null));
    }
}
