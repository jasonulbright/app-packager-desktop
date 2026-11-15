using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The Microsoft 365 install options that were fixed in the code and are settings now: languages, OfficeMgmtCOM, updates, forced shutdown, RemoveMSI, device-based licensing.</summary>
public sealed class M365OptionTests
{
    private static string Xml(string file, Dictionary<string, string>? values = null, string edition = "64")
    {
        var def = DefinitionFixture.Load(file);
        return StagingService.GenerateOdtXml(edition, "16.0.18000.20000", StagingService.ResolveOdtConfig(def, values), null);
    }

    private static readonly string[] Files =
        ["m365apps-x64.json", "m365apps-x86.json", "m365project-x64.json", "m365project-x86.json", "m365visio-x64.json", "m365visio-x86.json"];

    public static TheoryData<string> AllFiles() => new(Files);

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void ByDefaultTheInstallXmlHasTheLinesThatItAlwaysHad(string file)
    {
        var xml = Xml(file);

        Assert.Contains("OfficeMgmtCOM=\"TRUE\"", xml);
        Assert.Contains("<Language ID=\"en-us\" />", xml);
        Assert.Contains("<Property Name=\"FORCEAPPSHUTDOWN\" Value=\"TRUE\" />", xml);
        Assert.Contains("<Property Name=\"DeviceBasedLicensing\" Value=\"0\" />", xml);
        Assert.Contains("<RemoveMSI />", xml);
        Assert.DoesNotContain("<Updates", xml);
        Assert.DoesNotContain("de-de", xml);
    }

    [Fact]
    public void EachLanguageGetsALanguageLineInEveryProduct()
    {
        var xml = Xml("m365apps-x64.json", new Dictionary<string, string> { [StagingService.OdtLanguagesKey] = "en-us, de-de;fr-fr" });

        Assert.Contains("<Language ID=\"en-us\" />", xml);
        Assert.Contains("<Language ID=\"de-de\" />", xml);
        Assert.Contains("<Language ID=\"fr-fr\" />", xml);
        // The main language comes first.
        Assert.True(xml.IndexOf("en-us", StringComparison.Ordinal) < xml.IndexOf("de-de", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("en-us<x>")]
    [InlineData("en-us,")]
    [InlineData("de_DE")]
    public void ALanguageThatIsNotAnOdtLanguageIdStopsTheStage(string value)
    {
        var def = DefinitionFixture.Load("m365apps-x64.json");

        Assert.Throws<InvalidOperationException>(() =>
            StagingService.ResolveOdtConfig(def, new Dictionary<string, string> { [StagingService.OdtLanguagesKey] = value }));
    }

    [Fact]
    public void TheTogglesThatAreTurnedOffRemoveTheirLines()
    {
        var xml = Xml("m365apps-x64.json", new Dictionary<string, string>
        {
            [StagingService.OdtOfficeMgmtComKey] = "false", [StagingService.OdtForceAppShutdownKey] = "false",
            [StagingService.OdtRemoveMsiKey] = "false", [StagingService.OdtEnableUpdatesKey] = "false"
        });

        Assert.DoesNotContain("OfficeMgmtCOM", xml);
        Assert.DoesNotContain("FORCEAPPSHUTDOWN", xml);
        Assert.DoesNotContain("RemoveMSI", xml);
        Assert.Contains("<Updates Enabled=\"FALSE\" />", xml);
    }

    [Fact]
    public void DeviceBasedLicensingWritesOneWhenOn()
    {
        var xml = Xml("m365apps-x64.json", new Dictionary<string, string> { [StagingService.OdtDeviceBasedKey] = "true" });

        Assert.Contains("<Property Name=\"DeviceBasedLicensing\" Value=\"1\" />", xml);
        Assert.DoesNotContain("Value=\"0\"", xml);
    }

    [Fact]
    public void TheModelDefaultsAreTheDefaultsOfTheSettings()
    {
        var odt = new ApplicationPackagerEngine.Models.OdtConfig { ProductIds = ["O365ProPlusRetail"] };

        Assert.Equal(["en-us"], odt.Languages);
        Assert.True(odt.OfficeMgmtCom);
        Assert.True(odt.ForceAppShutdown);
        Assert.True(odt.RemoveMsi);
        Assert.False(odt.DeviceBasedLicensing);
        Assert.True(odt.EnableUpdates);
    }

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void EverySettingDefaultOfADefinitionEqualsTheModelDefault(string file)
    {
        var def = DefinitionFixture.Load(file);
        string Default(string key) => def.InstallerSettings!.Single(s => s.Key == key).DefaultValue;

        Assert.Equal("en-us", Default(StagingService.OdtLanguagesKey));
        Assert.Equal("true", Default(StagingService.OdtOfficeMgmtComKey));
        Assert.Equal("true", Default(StagingService.OdtForceAppShutdownKey));
        Assert.Equal("true", Default(StagingService.OdtRemoveMsiKey));
        Assert.Equal("true", Default(StagingService.OdtEnableUpdatesKey));
        Assert.Equal("false", Default(StagingService.OdtDeviceBasedKey));
    }
}
