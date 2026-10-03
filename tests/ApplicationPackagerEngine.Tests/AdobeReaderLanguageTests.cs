using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Adobe Acrobat Reader language selection: an Install Parameters locale list whose
/// default leaves the install command line exactly as it was, and whose selection passes
/// LANG_LIST and SUPPRESSLANGSELECTION after /msi, where the bootstrapper hands them to msiexec.
/// </summary>
public class AdobeReaderLanguageTests
{
    private const string TodaysInstallArgs = "/sAll /rs /rps /msi /qn /norestart";

    private static string Args(string? stored)
    {
        var def = DefinitionFixture.Load("adobereader.json");
        var values = stored is null ? null : new Dictionary<string, string> { ["Languages"] = stored };
        return StagingService.ResolveInstallerArgs(def.InstallArgs, def.InstallerSettings, values);
    }

    [Fact]
    public void TheDefaultProducesTodaysInstallCommandLine()
    {
        Assert.Equal(TodaysInstallArgs, Args(null));
        Assert.Equal(TodaysInstallArgs, Args(""));
    }

    [Fact]
    public void ASelectionAlwaysCarriesEnglishSortedAfterMsi()
    {
        Assert.Equal(TodaysInstallArgs + " LANG_LIST=\"de_DE,en_US,fr_FR\" SUPPRESSLANGSELECTION=1", Args("fr_FR,de_DE"));
    }

    [Fact]
    public void AllIsPassedAsAdobesAllKeyword()
    {
        Assert.Equal(TodaysInstallArgs + " LANG_LIST=\"ALL\" SUPPRESSLANGSELECTION=1", Args("All"));
        Assert.Equal(TodaysInstallArgs + " LANG_LIST=\"ALL\" SUPPRESSLANGSELECTION=1", Args("de_DE,All"));
    }

    [Fact]
    public void StoredValuesThatAreNotLocaleCodesAreDroppedAsTheStoredPreferenceIsFiltered()
    {
        Assert.Equal(["de_DE", "All"], StagingService.ParseLocaleList("de_DE; all ALL All dede de-DE"));
        Assert.Equal(TodaysInstallArgs, Args("de-DE,all"));
    }

    [Fact]
    public void AWellFormedCodeTheInstallerDoesNotCarryStopsTheStage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Args("xx_XX"));
        Assert.StartsWith("Unknown locale code(s) for Languages: xx_XX.", ex.Message);
    }

    [Fact]
    public void TheDefinitionKeepsItsUnifiedInstallerAndOffersTheMuiLocales()
    {
        var def = DefinitionFixture.Load("adobereader.json");
        var languages = Assert.Single(def.InstallerSettings!);

        Assert.Contains("_MUI.exe", def.DownloadUrl);
        Assert.Equal(StagingService.LocaleListControl, languages.ControlType);
        Assert.Equal("", languages.DefaultValue);
        Assert.Equal("en_US", languages.AlwaysInclude);
        Assert.Equal(26, languages.Options!.Length);
        Assert.DoesNotContain("en_US", languages.Options);
    }
}
