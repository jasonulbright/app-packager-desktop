using System.Text;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Workbench;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Q-106: the requirement columns of the Application Settings grid (free disk space, memory, minimum OS build). The tests follow one value
/// from the cell to the stored operation, to the manifest rules, to the ConfigMgr script text and to what Intune gets.
/// </summary>
public sealed class GridRequirementTests : IDisposable
{
    private const string Definition = "7zip";
    private const string Content = @"\\server\share\Apps\Vendor\App\1.0";
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly WorkbenchStore _store;
    private readonly string _appId = ApplicationIds.New(ApplicationOrigin.Catalog, name: Definition);

    public GridRequirementTests() => _store = new WorkbenchStore(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private ApplicationPackagerUI.Models.SupersedenceRow Row() =>
        new() { Uid = "u", ApplicationId = _appId, DefinitionId = Definition, Application = "7-Zip", Vendor = "v" };

    private static Dictionary<string, string> Fields(params (string Path, string Value)[] pairs) => pairs.ToDictionary(p => p.Path, p => p.Value);

    private static JsonArray Operations(WorkbenchProfile profile) => (JsonArray)profile.Get("Requirements.Operations").Value!;

    private static StageManifest Manifest() => new()
    {
        AppName = "App", Publisher = "Vendor", SoftwareVersion = "1.0",
        Detection = new StageDetection { Type = "RegistryKeyValue", RegistryKeyRelative = @"SOFTWARE\Vendor\App", ValueName = "DisplayVersion", ExpectedValue = "1.0" }
    };

    /// <summary>The path of a value: the grid writes the profile, the stage applies its operations to the manifest.</summary>
    private StageManifest StagedFromGrid(params (string Path, string Value)[] pairs)
    {
        var profile = GridProfileFields.Write(_store, _appId, Definition, Fields(pairs));
        var manifest = Manifest();
        RequirementOperations.Apply(manifest, profile.Get("Requirements.Operations").Value);
        return manifest;
    }

    private static string ScriptOf(StageManifest manifest) =>
        MecmTarget.BuildPackageScript(manifest, manifest.AppName, Content, "P01", "comment", 15, 30, ConditionTemplateService.Defaults(), null, null, null, null);

    private static ConditionTemplateDocument Templates() => ConditionTemplateService.Defaults();

    // ---- the cell and the stored operation ----

    [Fact]
    public void EachValueIsOneAddOperationThatAppliesToEveryDeploymentType()
    {
        var saved = GridProfileFields.Write(_store, _appId, Definition,
            Fields((GridRequirementFields.DiskSpace, "2048"), (GridRequirementFields.Memory, "4096"), (GridRequirementFields.OsBuild, "19045")));

        var operations = Operations(saved);
        Assert.Equal(["min-disk-space", "min-memory", "min-os-build"], operations.Select(o => (string?)o!["RuleId"]));
        Assert.All(operations, o =>
        {
            Assert.Equal("Add", (string?)o!["Op"]);
            Assert.Equal("*", (string?)o["AppliesTo"]![0]);
            Assert.Equal((string?)o["RuleId"], (string?)o["Rule"]!["ConditionId"]);
        });
        Assert.Equal(["2048", "4096", "19045"], operations.Select(o => (string?)o!["Rule"]!["Value"]));
        var state = GridProfileFields.Read(_store, _appId, Definition);
        Assert.Equal("2048", state.Fields[GridRequirementFields.DiskSpace]);
        Assert.Equal("4096", state.Fields[GridRequirementFields.Memory]);
        Assert.Equal("19045", state.Fields[GridRequirementFields.OsBuild]);
    }

    [Fact]
    public void AnEmptyCellOrInheritRemovesTheOperation()
    {
        GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.DiskSpace, "2048"), (GridRequirementFields.Memory, "4096")));
        var saved = GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.DiskSpace, ""), (GridRequirementFields.Memory, GridProfileFields.Inherit)));

        Assert.Empty(Operations(saved));
        Assert.All(GridProfileFields.Read(_store, _appId, Definition).Fields.Values, v => Assert.Equal("", v));
    }

    [Theory]
    [InlineData(GridRequirementFields.DiskSpace, "2048", true)]
    [InlineData(GridRequirementFields.DiskSpace, "", true)]
    [InlineData(GridRequirementFields.DiskSpace, "0", false)]
    [InlineData(GridRequirementFields.DiskSpace, "-5", false)]
    [InlineData(GridRequirementFields.DiskSpace, "2 GB", false)]
    [InlineData(GridRequirementFields.DiskSpace, "2.5", false)]
    [InlineData(GridRequirementFields.DiskSpace, "2147483647", true)]
    [InlineData(GridRequirementFields.DiskSpace, "2147483648", false)]
    [InlineData(GridRequirementFields.Memory, "4096", true)]
    [InlineData(GridRequirementFields.Memory, "lots", false)]
    [InlineData(GridRequirementFields.OsBuild, "19045", true)]
    [InlineData(GridRequirementFields.OsBuild, "22631", true)]
    [InlineData(GridRequirementFields.OsBuild, "1909", false)]
    [InlineData(GridRequirementFields.OsBuild, "10239", false)]
    [InlineData(GridRequirementFields.OsBuild, "100000", false)]
    [InlineData(GridRequirementFields.OsBuild, "10.0.19045", false)]
    public void ANumberThatIsNotAWholeNumberInRangeIsRefused(string path, string value, bool valid) =>
        Assert.Equal(valid, GridProfileFields.IsValid(path, value));

    [Fact]
    public void TheColumnsRefuseAnInvalidNumberAndKeepTheStoredValue()
    {
        var work = new ApplicationWork();
        var row = Row();
        var memory = ApplicationColumns.Find("memory")!;

        Assert.False(memory.Set(work, row, "plenty"));
        Assert.False(memory.Set(work, row, "0"));
        Assert.Empty(work.ProfilePending);

        Assert.True(memory.Set(work, row, " 8192 "));
        Assert.Equal("8192", memory.Get(work, row));
        Assert.Equal("8192", work.ProfilePending[_appId][GridRequirementFields.Memory]);
    }

    [Fact]
    public void TheThreeColumnsAreRegisteredWithNumberCellsAndAConfigMgrTooltip()
    {
        foreach (var (id, header) in new[] { ("diskSpace", "Free disk space (MB)"), ("memory", "Memory (MB)"), ("osBuild", "OS build (minimum)") })
        {
            var column = ApplicationColumns.Find(id)!;
            Assert.Equal(header, column.Header);
            Assert.Null(column.Choices);
            Assert.False(column.DefaultVisible);
            Assert.Contains("global condition", column.Tooltip);
            Assert.Contains("Leave it empty", column.Tooltip);
        }
    }

    [Fact]
    public void AnotherActiveProfileGreysTheCellsWithTheProfileName()
    {
        var work = new ApplicationWork { ProfileOf = _ => new GridProfileFields.State(GridProfileFields.State.Empty.Fields, "Pilot") };
        var row = Row();

        Assert.All(new[] { "diskSpace", "memory", "osBuild" }, id => Assert.Contains("Pilot", ApplicationColumns.Find(id)!.NotApplicable(work, row)));
    }

    // ---- merge with the other requirement operations ----

    [Fact]
    public void AWriteKeepsTheOtherOperationsOfTheProfileInTheirPlace()
    {
        var profile = GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.Memory, "2048"))).Clone();
        var operations = Operations(profile);
        operations.Insert(0, WorkbenchJson.Parse("""{ "Op": "Add", "RuleId": "lang", "Rule": { "ConditionId": "os-language", "Cultures": ["de-DE"] }, "AppliesTo": ["x64"] }"""));
        operations.Add(WorkbenchJson.Parse("""{ "Op": "Remove", "RuleId": "min-memory" }"""));
        _store.SaveProfile(profile, setActive: true);

        var saved = GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.Memory, "4096"), (GridRequirementFields.DiskSpace, "500")));

        Assert.Equal(["lang", "min-memory", "min-memory", "min-disk-space"], Operations(saved).Select(o => (string?)o!["RuleId"]));
        Assert.Equal("Remove", (string?)Operations(saved)[2]!["Op"]);
        Assert.Equal("4096", GridProfileFields.Read(_store, _appId, Definition).Fields[GridRequirementFields.Memory]);

        var cleared = GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.Memory, "")));
        Assert.Equal(["lang", "min-memory", "min-disk-space"], Operations(cleared).Select(o => (string?)o!["RuleId"]));
        Assert.Equal("Remove", (string?)Operations(cleared)[1]!["Op"]);
    }

    [Fact]
    public void ANewValueChangesTheValueOfAnOperationThatAPersonShapedInTheWorkbench()
    {
        var profile = GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.OsBuild, "19045"))).Clone();
        var operation = (JsonObject)Operations(profile)[0]!;
        operation["Op"] = "Replace";
        operation["AppliesTo"] = new JsonArray("x64");
        _store.SaveProfile(profile, setActive: true);

        var saved = GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.OsBuild, "22631")));

        var kept = Assert.Single(Operations(saved));
        Assert.Equal("Replace", (string?)kept!["Op"]);
        Assert.Equal("x64", (string?)kept["AppliesTo"]![0]);
        Assert.Equal("22631", (string?)kept["Rule"]!["Value"]);
    }

    [Fact]
    public void TheOperationsApplyToTheDeploymentTypesLikeAnyOtherOperation()
    {
        var profile = GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.Memory, "2048"))).Clone();
        Operations(profile).Add(WorkbenchJson.Parse("""{ "Op": "Replace", "RuleId": "min-memory", "Rule": { "ConditionId": "min-memory", "Value": "8192" }, "AppliesTo": ["arm64"] }"""));
        var manifest = new StageManifest
        {
            DeploymentTypes = [new StageDeploymentType { NameSuffix = "ARM64" }, new StageDeploymentType { NameSuffix = "x64" }]
        };

        RequirementOperations.Apply(manifest, profile.Get("Requirements.Operations").Value);

        Assert.Equal("2048", manifest.Requirements!.Single().Value!.ToString());
        Assert.Equal("8192", manifest.DeploymentTypes[0].Requirements!.Single().Value!.ToString());
        Assert.Equal("2048", manifest.DeploymentTypes[1].Requirements!.Single().Value!.ToString());
    }

    [Fact]
    public void ARemoveOperationDropsARequirementThatAnEarlierLevelSupplied()
    {
        var manifest = Manifest();
        manifest.Requirements = [new StageRequirement { ConditionId = "min-memory", Value = "1024" }];

        RequirementOperations.Apply(manifest, WorkbenchJson.Parse("""[ { "Op": "Remove", "RuleId": "min-memory" } ]"""));

        Assert.Empty(manifest.Requirements);
    }

    [Fact]
    public void TheMigrationOfTheSavedConditionsKeepsTheOperationsOfTheGrid()
    {
        GridProfileFields.Write(_store, _appId, Definition, Fields((GridRequirementFields.Memory, "2048"), (GridRequirementFields.OsBuild, "22621")));
        var settings = WorkbenchTestRoot.Json("""{ "DeploymentConditions": { "7zip": { "Architecture": "x64", "Network": "VpnOnly" } } }""");
        var roots = new WorkbenchDiscoveryRoots();

        LegacyPreferenceMigration.Run(_store, settings, roots);

        var profile = GridProfileFields.Find(_store, _appId, Definition)!;
        var ids = Operations(profile).Select(o => (string?)o!["RuleId"]).ToList();
        Assert.Contains("cpu-arch", ids);
        Assert.Contains("vpn-connected", ids);
        Assert.Contains("min-memory", ids);
        Assert.Contains("min-os-build", ids);
        Assert.Equal(4, ids.Count);
    }

    // ---- the ConfigMgr rule builder ----

    [Fact]
    public void TheDiskSpaceValueBecomesAFreeDiskSpaceRuleOnTheSystemDriveWithGreaterEquals()
    {
        var script = ScriptOf(StagedFromGrid((GridRequirementFields.DiskSpace, "2048")));

        Assert.Contains("-Name 'Disk space' -PlatformType 1 -Kind 'BuiltIn'", script);
        Assert.Contains("New-CMRequirementRuleFreeDiskSpaceValue -InputObject $dt0_gc0 -PartitionOption System -RuleOperator GreaterEquals -Value1 2048", script);
        Assert.Contains("-AddRequirement", script);
    }

    [Fact]
    public void TheMemoryValueBecomesACommonValueRuleOnTheTotalPhysicalMemoryCondition()
    {
        var script = ScriptOf(StagedFromGrid((GridRequirementFields.Memory, "4096")));

        Assert.Contains("-Name 'Total physical memory' -PlatformType 1 -Kind 'BuiltIn'", script);
        Assert.Contains("New-CMRequirementRuleCommonValue -InputObject $dt0_gc0 -RuleOperator GreaterEquals -Value1 '4096'", script);
    }

    [Fact]
    public void TheBuildBecomesAVersionRuleOnAWqlConditionOfTheOperatingSystemVersion()
    {
        var script = ScriptOf(StagedFromGrid((GridRequirementFields.OsBuild, "19045")));

        Assert.Contains("-Name 'APKG - Operating System Build'", script);
        Assert.Contains("-Kind 'Wql' -DataType 'Version' -Namespace 'root\\cimv2' -Class 'Win32_OperatingSystem' -Property 'Version'", script);
        Assert.Contains("New-CMRequirementRuleCommonValue -InputObject $dt0_gc0 -RuleOperator GreaterEquals -Value1 '10.0.19045'", script);
    }

    [Fact]
    public void AllThreeValuesGiveThreeRulesOnTheDeploymentTypeAndTheEqualsRulesStayEquals()
    {
        var manifest = StagedFromGrid((GridRequirementFields.DiskSpace, "2048"), (GridRequirementFields.Memory, "4096"), (GridRequirementFields.OsBuild, "22631"));
        manifest.Requirements = [new StageRequirement { ConditionId = "cpu-arch", Value = "x64" }, .. manifest.Requirements!];

        var script = ScriptOf(manifest);

        Assert.Contains("New-CMRequirementRuleCommonValue -InputObject $dt0_gc0 -RuleOperator IsEquals -Value1 '9'", script);
        Assert.Contains("New-CMRequirementRuleFreeDiskSpaceValue -InputObject $dt0_gc1 -PartitionOption System -RuleOperator GreaterEquals -Value1 2048", script);
        Assert.Contains("New-CMRequirementRuleCommonValue -InputObject $dt0_gc2 -RuleOperator GreaterEquals -Value1 '4096'", script);
        Assert.Contains("New-CMRequirementRuleCommonValue -InputObject $dt0_gc3 -RuleOperator GreaterEquals -Value1 '10.0.22631'", script);
        Assert.Contains("-AddRequirement @($dt0_req0, $dt0_req1, $dt0_req2, $dt0_req3)", script.Replace(", ", ", "));
    }

    [Theory]
    [InlineData("min-memory", "0")]
    [InlineData("min-memory", "lots")]
    [InlineData("min-disk-space", "-1")]
    [InlineData("min-os-build", "19045.1")]
    [InlineData("min-os-build", "")]
    public void AValueThatIsNotAWholeNumberStopsTheRuleBuilder(string id, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RequirementRuleBuilder.Resolve([new StageRequirement { ConditionId = id, Value = value }], Templates()));
        Assert.Contains("whole number", ex.Message);
    }

    [Fact]
    public void ATemplateWithAnOperatorThatTheCmdletDoesNotKnowIsRefusedAndNeverReachesTheScript()
    {
        var doc = new ConditionTemplateDocument
        {
            Conditions = [new ConditionTemplate { Id = "x", GlobalConditionName = "X", Kind = "BuiltIn", RuleType = "CommonValue", Operator = "GreaterEquals'; calc; '" }]
        };

        var ex = Assert.Throws<InvalidOperationException>(() => RequirementRuleBuilder.Resolve([new StageRequirement { ConditionId = "x", Value = "1" }], doc));
        Assert.Contains("unsupported Operator", ex.Message);
    }

    [Fact]
    public void TheNumberIsWrittenWithoutLeadingZerosSoTheScriptHoldsOneForm()
    {
        var resolved = RequirementRuleBuilder.Resolve([new StageRequirement { ConditionId = "min-memory", Value = "004096" }], Templates()).Single();
        Assert.Equal("4096", resolved.Value);
        Assert.Equal(4096, resolved.Number);
    }

    [Fact]
    public void AFileWrittenBeforeTheRequirementTemplatesExistedStillResolvesThem()
    {
        var path = Path.Combine(_root, "condition-templates.json");
        var old = new ConditionTemplateDocument { Conditions = [.. ConditionTemplateService.Defaults().Conditions.Take(3)] };
        old.Conditions[0].GlobalConditionName = "Site CPU";
        var service = new ConditionTemplateService(path);
        service.Save(old);

        var loaded = service.Load();

        Assert.Equal(6, loaded.Conditions.Length);
        Assert.Equal("Site CPU", loaded.Conditions.Single(c => c.Id == "cpu-arch").GlobalConditionName);
        Assert.NotEmpty(RequirementRuleBuilder.Resolve([new StageRequirement { ConditionId = "min-os-build", Value = "19045" }], loaded));
    }

    // ---- what Intune gets ----

    private static IntuneRequirementTranslation One(string id, string value, ConditionTemplateDocument? doc = null) =>
        IntuneRequirementRules.Translate([new StageRequirement { ConditionId = id, Value = value }], doc ?? Templates()).Single();

    [Fact]
    public void TheDiskSpaceAndTheMemoryBecomeTheMinimumPropertiesOfTheApp()
    {
        var disk = One("min-disk-space", "2048");
        var memory = One("min-memory", "4096");

        Assert.True(disk.Translated, disk.Gap);
        Assert.Null(disk.Rule);
        Assert.Equal("minimumFreeDiskSpaceInMB", disk.Setting!.Property);
        Assert.Equal(2048, disk.Setting.Value.GetValue<int>());
        Assert.Equal("minimumMemoryInMB", memory.Setting!.Property);
        Assert.Equal(4096, memory.Setting.Value.GetValue<int>());
    }

    [Theory]
    [InlineData("19045", "Windows10_22H2")]
    [InlineData("19044", "Windows10_21H2")]
    [InlineData("22000", "Windows11_21H2")]
    [InlineData("22631", "Windows11_23H2")]
    [InlineData("26100", "Windows11_24H2")]
    public void ABuildThatIsTheBuildOfAReleaseBecomesTheMinimumWindowsRelease(string build, string release)
    {
        var t = One("min-os-build", build);

        Assert.True(t.Translated, t.Gap);
        Assert.Equal("minimumSupportedWindowsRelease", t.Setting!.Property);
        Assert.Equal(release, t.Setting.Value.GetValue<string>());
    }

    [Fact]
    public void ABuildThatIsNotTheBuildOfAReleaseBecomesARequirementScriptSoItKeepsItsExactMeaning()
    {
        var t = One("min-os-build", "19046");

        Assert.True(t.Translated, t.Gap);
        Assert.Null(t.Setting);
        Assert.Equal("#microsoft.graph.win32LobAppPowerShellScriptRule", (string?)t.Rule!["@odata.type"]);
        Assert.Equal("requirement", (string?)t.Rule["ruleType"]);
        Assert.Equal("True", (string?)t.Rule["comparisonValue"]);
        var script = Encoding.UTF8.GetString(Convert.FromBase64String((string)t.Rule["scriptContent"]!));
        Assert.Contains("(Get-CimInstance -ClassName Win32_OperatingSystem).BuildNumber -ge 19046", script);
    }

    [Theory]
    [InlineData("min-memory", "LessEquals", "4096", "only GreaterEquals has an Intune form")]
    [InlineData("min-disk-space", "NotEquals", "4096", "only GreaterEquals has an Intune form")]
    [InlineData("min-memory", "GreaterEquals", "3000000000", "larger than the Intune setting")]
    public void AMinimumThatTheAppPropertyCannotExpressHasAGapThatNamesIt(string id, string op, string value, string reason)
    {
        var doc = Templates();
        doc.Conditions.Single(c => c.Id == id).Operator = op;

        var t = One(id, value, doc);

        Assert.False(t.Translated);
        Assert.Contains(reason, t.Gap);
        Assert.Contains(id, t.Gap);
    }

    [Fact]
    public void TheGapBlocksThePublishUntilTheDropIsAcceptedThenItIsAReviewFinding()
    {
        var doc = Templates();
        doc.Conditions.Single(c => c.Id == "min-memory").Operator = "LessEquals";
        var manifest = Manifest();
        manifest.Requirements = [new StageRequirement { ConditionId = "min-memory", Value = "4096" }];

        var blocking = IntuneCompatibilityFindings.Build(manifest, null, doc);
        var finding = Assert.Single(blocking, f => f.Code == "RequirementNotTranslatable");
        Assert.Contains("min-memory", finding.Message);
        Assert.Contains("only GreaterEquals", finding.Message);
        Assert.True(finding.CanAcceptDrop);

        var accepted = IntuneCompatibilityFindings.Build(manifest, null, doc, [finding.DropKey!]);
        Assert.Contains(accepted, f => f.Severity == IntuneCompatibilityFindings.Review && f.Code == "RequirementDropped");
        Assert.Null(IntuneCompatibilityFindings.RefusalMessage(accepted));
    }

    [Fact]
    public void ATranslatedMinimumIsInformationThatNamesTheIntuneSetting()
    {
        var manifest = Manifest();
        manifest.Requirements = [new StageRequirement { ConditionId = "min-memory", Value = "4096" }];

        var finding = Assert.Single(IntuneCompatibilityFindings.Build(manifest), f => f.Code == "RequirementTranslated");

        Assert.Equal(IntuneCompatibilityFindings.Info, finding.Severity);
        Assert.Contains("minimumMemoryInMB (4096)", finding.Message);
    }

    [Fact]
    public void TheMinimumWindowsReleaseNeverGoesBelowTheReleaseOfTheSettings()
    {
        var body = new JsonObject { ["minimumSupportedWindowsRelease"] = "Windows11_23H2" };

        IntunePublishService.ApplyRequirementSettings(body, [new IntuneAppSetting("minimumSupportedWindowsRelease", JsonValue.Create("Windows10_22H2")!)]);
        Assert.Equal("Windows11_23H2", (string?)body["minimumSupportedWindowsRelease"]);

        IntunePublishService.ApplyRequirementSettings(body, [new IntuneAppSetting("minimumSupportedWindowsRelease", JsonValue.Create("Windows11_24H2")!)]);
        Assert.Equal("Windows11_24H2", (string?)body["minimumSupportedWindowsRelease"]);
    }

    [Fact]
    public void TheStricterOfTwoMinimumsWinsAndAnUnnamedPropertyIsNotWritten()
    {
        var body = new JsonObject();

        IntunePublishService.ApplyRequirementSettings(body,
        [
            new IntuneAppSetting("minimumMemoryInMB", JsonValue.Create(2048)!),
            new IntuneAppSetting("minimumMemoryInMB", JsonValue.Create(8192)!)
        ]);

        Assert.Equal(8192, (int?)body["minimumMemoryInMB"]);
        Assert.False(body.ContainsKey("minimumFreeDiskSpaceInMB"));
    }

    [Fact]
    public void TheReleaseOfABuildAndTheOrderOfReleasesFollowTheKnownList()
    {
        Assert.Equal("Windows10_1607", IntuneWindowsReleases.OfBuild(14393));
        Assert.Equal("Windows11_25H2", IntuneWindowsReleases.OfBuild(26200));
        Assert.Null(IntuneWindowsReleases.OfBuild(19046));
        Assert.Null(IntuneWindowsReleases.OfBuild(0));
        Assert.Equal("Windows11_22H2", IntuneWindowsReleases.Later("Windows10_22H2", "Windows11_22H2"));
        Assert.Equal("Windows11_26H1", IntuneWindowsReleases.Later("Windows11_26H1", "Windows10_22H2"));
        Assert.All(IntuneWindowsReleases.Known, k => Assert.NotEqual(-1, IntuneWindowsReleases.Rank(k)));
    }
}
