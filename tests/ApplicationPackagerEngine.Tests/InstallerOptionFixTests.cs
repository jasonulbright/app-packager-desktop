using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The installer options that were wrong (Microsoft 365 MigrateArch, shared computer activation and the Bing ExcludeApp ID; SSMS
/// includeRecommended and includeOptional without a workload).
/// </summary>
public sealed class InstallerOptionFixTests
{
    private static readonly string[] Microsoft365 =
        ["m365apps-x64.json", "m365apps-x86.json", "m365project-x64.json", "m365project-x86.json", "m365visio-x64.json", "m365visio-x86.json"];

    [Theory]
    [InlineData("m365apps-x64.json", "64")]
    [InlineData("m365apps-x86.json", "32")]
    [InlineData("m365project-x86.json", "32")]
    [InlineData("m365visio-x86.json", "32")]
    public void ByDefaultNoPackageChangesTheArchitectureOrTurnsOnSharedComputerActivation(string file, string edition)
    {
        var def = DefinitionFixture.Load(file);

        var xml = StagingService.GenerateOdtXml(edition, "16.0.18000.20000", StagingService.ResolveOdtConfig(def, null), null);

        Assert.DoesNotContain("MigrateArch", xml);
        Assert.DoesNotContain("SharedComputerLicensing", xml);
        Assert.DoesNotContain("SCLCacheOverride", xml);
    }

    [Fact]
    public void MigrateArchAndSharedComputerActivationAreEmittedOnlyWhenTheOperatorTurnsThemOn()
    {
        var def = DefinitionFixture.Load("m365apps-x64.json");
        var values = new Dictionary<string, string>
        {
            [StagingService.OdtMigrateArchKey] = "true",
            [StagingService.OdtSharedComputerKey] = "true"
        };

        var xml = StagingService.GenerateOdtXml("64", "16.0.18000.20000", StagingService.ResolveOdtConfig(def, values), null);

        Assert.Contains("MigrateArch=\"TRUE\"", xml);
        Assert.Contains("<Property Name=\"SharedComputerLicensing\" Value=\"1\" />", xml);
    }

    [Fact]
    public void TheModelDefaultsMatchTheOfficeDefaults()
    {
        var odt = new OdtConfig { ProductIds = ["O365ProPlusRetail"] };

        var xml = StagingService.GenerateOdtXml("64", "1", odt, null);

        Assert.False(odt.MigrateArch);
        Assert.False(odt.SharedComputerLicensing);
        Assert.DoesNotContain("MigrateArch", xml);
        Assert.DoesNotContain("SharedComputerLicensing", xml);
        Assert.DoesNotContain("Bing", string.Join(",", odt.ExcludeApps));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void NoMicrosoft365DefinitionExcludesTheUndocumentedBingId(string file)
    {
        var def = DefinitionFixture.Load(file);

        Assert.DoesNotContain("Bing", def.Odt!.ExcludeApps);
        Assert.DoesNotContain(def.InstallerSettings!, p => p.Key.Contains("Bing", StringComparison.OrdinalIgnoreCase));
        var xml = StagingService.GenerateOdtXml("64", "1", StagingService.ResolveOdtConfig(def, null), null);
        Assert.DoesNotContain("Bing", xml);
        Assert.Contains(def.InstallerSettings!, p => p.Key == StagingService.OdtMigrateArchKey && p.DefaultValue == "false");
        Assert.Contains(def.InstallerSettings!, p => p.Key == StagingService.OdtSharedComputerKey && p.DefaultValue == "false");
    }

    public static TheoryData<string> Files() => new(Microsoft365);

    [Fact]
    public void SsmsDoesNotPassTheIncludeSwitchesWithoutAWorkload()
    {
        var def = DefinitionFixture.Load("ssms.json");
        var values = new Dictionary<string, string> { ["IncludeRecommended"] = "true", ["IncludeOptional"] = "true" };

        var args = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values);

        Assert.DoesNotContain("--includeRecommended", args);
        Assert.DoesNotContain("--includeOptional", args);
        Assert.DoesNotContain("--add", args);
    }

    [Fact]
    public void SsmsPassesTheIncludeSwitchesWithTheWorkloadsThatAreOn()
    {
        var def = DefinitionFixture.Load("ssms.json");
        var values = new Dictionary<string, string>
        {
            ["IncludeRecommended"] = "true", ["IncludeOptional"] = "true", ["Workload_BI"] = "true", ["Workload_DevOps"] = "true"
        };

        var args = StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values);

        Assert.Contains("--add Microsoft.SqlServer.Workload.SSMS.BI", args);
        Assert.Contains("--add Microsoft.SqlServer.Workload.SSMS.DevOps", args);
        Assert.DoesNotContain("Workload.SSMS.AI", args);
        Assert.Contains("--includeRecommended", args);
        Assert.Contains("--includeOptional", args);
    }

    [Fact]
    public void EveryRequiredSettingOfAConditionNamesAToggleOfTheSameDefinition()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Definitions");
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var found = 0;
        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            var def = System.Text.Json.JsonSerializer.Deserialize<AppDefinition>(File.ReadAllText(file), options)!;
            foreach (var setting in (def.InstallerSettings ?? []).Where(p => p.RequiresAnyOf is { Length: > 0 }))
            {
                found++;
                foreach (var key in setting.RequiresAnyOf!)
                    Assert.Contains(def.InstallerSettings!, p => p.Key == key && p.ControlType == "Toggle");
            }
        }
        Assert.True(found >= 2);
    }
}
