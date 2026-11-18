using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Scrapers;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The Bruno and PowerShell 7 (Devolutions) definitions read the GitHub releases API, take the asset digest GitHub lists
/// and refuse a payload without one. The Devolutions definition picks the Microsoft-payload MSI, which only some releases of
/// Devolutions/pwsh-distro carry, and detects on the version of pwsh.exe.
/// </summary>
public sealed class BrunoAndDevolutionsPowerShellDefinitionTests
{
    private const string Hash1 = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Hash2 = "2222222222222222222222222222222222222222222222222222222222222222";

    private const string BrunoRelease = """
        {"tag_name":"v3.1.0","assets":[
          {"name":"bruno_3.1.0_arm64_win.msi","digest":"sha256:2222222222222222222222222222222222222222222222222222222222222222","browser_download_url":"https://github.com/usebruno/bruno/releases/download/v3.1.0/bruno_3.1.0_arm64_win.msi"},
          {"name":"bruno_3.1.0_x64_win.msi","digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","browser_download_url":"https://github.com/usebruno/bruno/releases/download/v3.1.0/bruno_3.1.0_x64_win.msi"}]}
        """;

    private const string DevolutionsReleases = """
        [
          {"tag_name":"v7.7.1.0","assets":[
            {"name":"PowerShell-7.7.1.0-win-x64.zip","digest":"sha256:2222222222222222222222222222222222222222222222222222222222222222","browser_download_url":"https://github.com/Devolutions/pwsh-distro/releases/download/v7.7.1.0/PowerShell-7.7.1.0-win-x64.zip"}]},
          {"tag_name":"v7.7.0.1","assets":[
            {"name":"Devolutions.PowerShell-7.7.0.1-win-x64.msi","digest":"sha256:2222222222222222222222222222222222222222222222222222222222222222","browser_download_url":"https://github.com/Devolutions/pwsh-distro/releases/download/v7.7.0.1/Devolutions.PowerShell-7.7.0.1-win-x64.msi"},
            {"name":"Microsoft.PowerShell-7.7.0.1-win-x64.msi","digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111","browser_download_url":"https://github.com/Devolutions/pwsh-distro/releases/download/v7.7.0.1/Microsoft.PowerShell-7.7.0.1-win-x64.msi"}]}
        ]
        """;

    [Fact]
    public void BrunoTakesTheX64MsiVersionAndItsOwnDigest()
    {
        var def = DefinitionFixture.Load("bruno.json");
        var config = def.VersionScraper;

        Assert.Equal("3.1.0", HttpRegexScraper.ExtractVersion(BrunoRelease, config.Pattern, config.SelectHighestVersion));
        Assert.Equal(Hash1, StagingService.ExtractFeedHash(BrunoRelease, config, "3.1.0"), ignoreCase: true);
        Assert.True(def.RequirePayloadHash);
        Assert.Contains("v{version}/bruno_{version}_x64_win.msi", def.DownloadUrl);
    }

    [Fact]
    public void DevolutionsTakesTheMicrosoftPayloadMsiAndNeverTheDevolutionsFlavourOrAZip()
    {
        var def = DefinitionFixture.Load("devolutionspowershell.json");
        var config = def.VersionScraper;

        Assert.Equal("7.7.0.1", HttpRegexScraper.ExtractVersion(DevolutionsReleases, config.Pattern, config.SelectHighestVersion));
        Assert.Equal(Hash1, StagingService.ExtractFeedHash(DevolutionsReleases, config, "7.7.0.1"), ignoreCase: true);

        var url = Regex.Match(DevolutionsReleases, config.DownloadUrlQuery!);
        Assert.True(url.Success);
        Assert.Equal("https://github.com/Devolutions/pwsh-distro/releases/download/v7.7.0.1/Microsoft.PowerShell-7.7.0.1-win-x64.msi", url.Groups[1].Value);
        Assert.True(def.RequirePayloadHash);
        Assert.True(def.RequireValidSignature);
    }

    [Fact]
    public void DevolutionsDetectsOnThePwshFileVersionAndInstallsWithThePathOption()
    {
        var def = DefinitionFixture.Load("devolutionspowershell.json");

        Assert.Equal("File", def.Detection.Type);
        Assert.Equal("pwsh.exe", def.Detection.FileName);
        Assert.Equal(@"C:\Program Files\PowerShell\7", def.Detection.FilePath);
        Assert.Equal("GreaterEquals", def.Detection.Operator);
        Assert.Contains("ADD_PATH=1", def.InstallArgs);
        Assert.Equal("{version}", def.Detection.ExpectedValue);
    }
}
