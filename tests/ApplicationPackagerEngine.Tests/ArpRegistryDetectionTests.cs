using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Each of these installers writes its version under one fixed key in one
/// registry view, so detection reads that key directly instead of scanning
/// every uninstall entry by display name.
/// </summary>
public class ArpRegistryDetectionTests
{
    private const string Arp = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    [Theory]
    [InlineData("anyburn.json", Arp + "AnyBurn", false)]
    [InlineData("bulkrenameutility.json", Arp + "Bulk Rename Utility Installation_is1", true)]
    [InlineData("ocenaudio.json", Arp + "ocenaudio", false)]
    [InlineData("openshot.json", Arp + "{4BB0DCDC-BC24-49EC-8937-72956C33A470}_is1", true)]
    [InlineData("picpick.json", Arp + "PicPick", false)]
    [InlineData("pidgin.json", Arp + "Pidgin", false)]
    public void TheArpDisplayVersionIsReadFromOneViewBoundKey(string fileName, string key, bool is64Bit)
    {
        var detection = DefinitionFixture.Load(fileName).Detection;

        Assert.Equal("RegistryKeyValue", detection.Type);
        Assert.Equal("HKLM", detection.RegistryHive);
        Assert.Equal(key, detection.RegistryKeyTemplate);
        Assert.Equal("DisplayVersion", detection.ValueName);
        Assert.Equal("Version", detection.PropertyType);
        Assert.Equal("GreaterEquals", detection.Operator);
        Assert.Equal(is64Bit, detection.Is64Bit);
    }

    [Theory]
    [InlineData("anyburn.json", "AnyBurn")]
    [InlineData("openshot.json", "{4BB0DCDC-BC24-49EC-8937-72956C33A470}_is1")]
    public void TheStagedRuleKeepsTheKeyAndNamesItForTheUninstallLookup(string fileName, string keyName)
    {
        var staged = StagingService.BuildDetection(DefinitionFixture.Load(fileName), null, "6.2");

        Assert.Equal(Arp + keyName, staged.RegistryKeyRelative);
        Assert.Equal("6.2", staged.ExpectedValue);
        Assert.Equal("LocalMachine", staged.Hive);
        Assert.Equal(keyName, WrapperGeneratorService.ResolveUninstallHints(staged).ArpKeyName);
    }

    /// <summary>
    /// The TeraCopy uninstall entry is named by the wrapped MSI's ProductCode,
    /// which changes every build, so detection reads the product's own key.
    /// </summary>
    [Fact]
    public void TeraCopy_ReadsTheProductVersionValueOutsideTheUninstallTree()
    {
        var detection = DefinitionFixture.Load("teracopy.json").Detection;

        Assert.Equal("RegistryKeyValue", detection.Type);
        Assert.Equal("HKLM", detection.RegistryHive);
        Assert.Equal(@"SOFTWARE\Code Sector\TeraCopy", detection.RegistryKeyTemplate);
        Assert.DoesNotContain(@"\Uninstall\", detection.RegistryKeyTemplate);
        Assert.Equal("Version", detection.ValueName);
        Assert.Equal("Version", detection.PropertyType);
        Assert.Equal("GreaterEquals", detection.Operator);
        Assert.True(detection.Is64Bit);
    }

    [Fact]
    public void TheExistingUninstallCommandsAreKept()
    {
        Assert.Contains(@"Uninstall\ocenaudio", DefinitionFixture.Load("ocenaudio.json").UninstallCommand);
        Assert.Contains("'OpenShot Video Editor*'", DefinitionFixture.Load("openshot.json").UninstallCommand);
    }
}
