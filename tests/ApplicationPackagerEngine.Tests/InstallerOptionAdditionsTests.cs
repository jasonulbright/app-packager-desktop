using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The installer options that the audit of installer options listed as missing, added to the Adobe Reader, Beyond Compare 5 and SSMS definitions.</summary>
public sealed class InstallerOptionAdditionsTests
{
    private static string Args(string file, Dictionary<string, string>? values = null)
    {
        var def = DefinitionFixture.Load(file);
        return StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values);
    }

    // ---- Adobe Acrobat Reader ----

    [Fact]
    public void AdobeReaderByDefaultKeepsItsInstallArguments() =>
        Assert.Equal("/sAll /rs /rps /msi /qn /norestart", Args("adobereader.json"));

    [Theory]
    [InlineData("AcceptEula", "EULA_ACCEPT=YES")]
    [InlineData("NoDesktopShortcut", "DISABLEDESKTOPSHORTCUT=1")]
    [InlineData("NoChromeExtension", "ENABLE_CHROMEEXT=0")]
    [InlineData("NoBrowserIntegration", "DISABLE_BROWSER_INTEGRATION=YES")]
    public void AdobeReaderPassesOneMsiPropertyForEachToggleThatIsOn(string key, string property)
    {
        var args = Args("adobereader.json", new Dictionary<string, string> { [key] = "true" });

        Assert.Equal("/sAll /rs /rps /msi /qn /norestart " + property, args);
    }

    [Fact]
    public void AdobeReaderPropertiesComeAfterTheMsiSwitchSoTheyAreMsiProperties()
    {
        var args = Args("adobereader.json", new Dictionary<string, string>
        {
            ["AcceptEula"] = "true", ["NoDesktopShortcut"] = "true", ["NoChromeExtension"] = "true", ["NoBrowserIntegration"] = "true"
        });

        var msi = args.IndexOf("/msi", StringComparison.Ordinal);
        foreach (var property in new[] { "EULA_ACCEPT=YES", "DISABLEDESKTOPSHORTCUT=1", "ENABLE_CHROMEEXT=0", "DISABLE_BROWSER_INTEGRATION=YES" })
            Assert.True(args.IndexOf(property, StringComparison.Ordinal) > msi, property);
    }

    // ---- Beyond Compare 5 ----

    [Fact]
    public void BeyondCompareByDefaultKeepsItsInstallArguments() =>
        Assert.Equal("/VERYSILENT /NORESTART /ALLUSERS /DISABLEUPDATES /SUPPRESSMSGBOXES", Args("beyondcompare5.json"));

    [Theory]
    [InlineData("DisableRemoteProfiles", "/DisableRemoteProfiles")]
    [InlineData("DisableSavedPasswords", "/DisableSavedPasswords")]
    public void BeyondComparePassesTheSwitchOfAToggleThatIsOn(string key, string switchText) =>
        Assert.Equal("/VERYSILENT /NORESTART /ALLUSERS /DISABLEUPDATES /SUPPRESSMSGBOXES " + switchText,
            Args("beyondcompare5.json", new Dictionary<string, string> { [key] = "true" }));

    // ---- SSMS ----

    [Fact]
    public void SsmsByDefaultPassesNeitherALanguageNorTheCacheSwitch()
    {
        var args = Args("ssms.json");

        Assert.DoesNotContain("--addProductLang", args);
        Assert.DoesNotContain("--nocache", args);
    }

    [Fact]
    public void SsmsPassesTheLanguageAndTheCacheSwitchWhenChosen()
    {
        var args = Args("ssms.json", new Dictionary<string, string> { ["ProductLanguage"] = "de-de", ["NoCache"] = "true" });

        Assert.Contains("--addProductLang de-de", args);
        Assert.Contains("--nocache", args);
    }

    [Fact]
    public void EveryNewSettingHasAUniqueKeyAndATooltip()
    {
        foreach (var file in new[] { "adobereader.json", "beyondcompare5.json", "ssms.json" })
        {
            var settings = DefinitionFixture.Load(file).InstallerSettings!;
            Assert.Equal(settings.Length, settings.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(settings, s => Assert.False(string.IsNullOrWhiteSpace(s.Tooltip), $"{file}: {s.Key} has no tooltip."));
        }
    }
}
