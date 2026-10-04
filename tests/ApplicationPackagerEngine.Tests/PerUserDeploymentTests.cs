using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A per-user product registers under HKCU and installs in the user's
/// session. The hive travels from the definition through the stage manifest
/// into the MECM detection clause and the Intune registry rule, and the
/// Intune install runs as the user. A manifest without the hive field reads
/// as the machine hive.
/// </summary>
public class PerUserDeploymentTests
{
    private static AppDefinition Def(string? hive) => new()
    {
        Application = "Per User App",
        CMName = "Per User App",
        InstallContext = "User",
        Detection = new DetectionConfig
        {
            Type = "RegistryKeyValue",
            RegistryHive = hive,
            RegistryKeyTemplate = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PerUserApp",
            ValueName = "DisplayVersion",
            Operator = "GreaterEquals",
            PropertyType = "Version",
            Is64Bit = true
        }
    };

    [Theory]
    [InlineData("HKCU", "CurrentUser")]
    [InlineData("HKEY_CURRENT_USER", "CurrentUser")]
    [InlineData("CurrentUser", "CurrentUser")]
    [InlineData("HKLM", "LocalMachine")]
    [InlineData(null, "LocalMachine")]
    [InlineData("", "LocalMachine")]
    public void Staging_MapsTheDefinitionHive(string? hive, string expected)
    {
        var detection = StagingService.BuildDetection(Def(hive), null, "1.2.3");

        Assert.Equal(expected, detection.Hive);
        Assert.Equal(expected == "CurrentUser", detection.IsCurrentUserHive);
    }

    [Fact]
    public void Staging_KeyExistenceDetectionCarriesTheHiveToo()
    {
        var def = Def("HKCU");
        def.Detection.Type = "RegistryKey";

        Assert.Equal("CurrentUser", StagingService.BuildDetection(def, null, "1.2.3").Hive);
    }

    [Fact]
    public void ManifestWithoutTheField_ReadsAsTheMachineHive()
    {
        var json = """{"Type":"RegistryKeyValue","RegistryKeyRelative":"SOFTWARE\\Vendor\\App","ValueName":"DisplayVersion"}""";
        var detection = JsonSerializer.Deserialize<StageDetection>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Null(detection.Hive);
        Assert.False(detection.IsCurrentUserHive);

        var roundTrip = JsonSerializer.Serialize(detection);
        Assert.DoesNotContain("IsCurrentUserHive", roundTrip);
    }

    private static StageManifest PerUserManifest(StageDetection detection) => new()
    {
        AppName = "Per User App",
        Publisher = "Vendor",
        SoftwareVersion = "1.0",
        InstallContext = "User",
        Detection = detection
    };

    [Fact]
    public void MecmScript_UsesTheCurrentUserHiveAndUserInstallBehavior()
    {
        var manifest = PerUserManifest(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"Software\Vendor\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0",
            Hive = "CurrentUser"
        });

        var script = MecmTarget.BuildPackageScript(manifest, manifest.AppName, @"\\server\share\App\1.0", "P01", "comment",
            15, 30, ConditionTemplateService.Defaults());

        Assert.Contains("New-CMDetectionClauseRegistryKeyValue -Hive CurrentUser -KeyName 'Software\\Vendor\\App'", script);
        Assert.Contains("-InstallationBehaviorType InstallForUser", script);
        Assert.DoesNotContain("-LogonRequirementType", script);
    }

    [Fact]
    public void MecmScript_DefaultsToTheMachineHive()
    {
        var manifest = PerUserManifest(new StageDetection
        {
            Type = "RegistryKey",
            RegistryKeyRelative = @"Software\Vendor\App"
        });
        manifest.InstallContext = "Machine";

        var script = MecmTarget.BuildPackageScript(manifest, manifest.AppName, @"\\server\share\App\1.0", "P01", "comment",
            15, 30, ConditionTemplateService.Defaults());

        Assert.Contains("New-CMDetectionClauseRegistryKey -Hive LocalMachine -KeyName 'Software\\Vendor\\App' -Existence", script);
        Assert.Contains("-InstallationBehaviorType InstallForSystem -LogonRequirementType WhetherOrNotUserLoggedOn", script);
    }

    [Fact]
    public void IntuneRule_UsesTheCurrentUserRoot()
    {
        var rules = IntunePublishService.BuildRules(new StageDetection
        {
            Type = "RegistryKeyValue",
            RegistryKeyRelative = @"Software\Vendor\App",
            ValueName = "DisplayVersion",
            ExpectedValue = "1.0.0",
            Operator = "GreaterEquals",
            Hive = "CurrentUser"
        });

        Assert.Equal(@"HKEY_CURRENT_USER\Software\Vendor\App", (string?)((JsonObject)rules[0]!)["keyPath"]);

        var existence = IntunePublishService.BuildRules(new StageDetection
        {
            Type = "RegistryKey",
            RegistryKeyRelative = @"Software\Vendor\App"
        });
        Assert.Equal(@"HKEY_LOCAL_MACHINE\Software\Vendor\App", (string?)((JsonObject)existence[0]!)["keyPath"]);
    }
}
