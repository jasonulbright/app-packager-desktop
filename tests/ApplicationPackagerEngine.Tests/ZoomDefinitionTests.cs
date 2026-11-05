using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Without archType=x64 both "latest" URLs of Zoom serve the older 32-bit build,
/// so a check and a stage took the stale release (PowerShell commit d43f9b7).
/// </summary>
public sealed class ZoomDefinitionTests
{
    [Fact]
    public void BothLatestUrlsRequestTheX64Build()
    {
        var def = DefinitionFixture.Load("zoom.json");
        Assert.Equal("https://zoom.us/client/latest/ZoomInstaller.exe?archType=x64", def.VersionScraper.Url);
        Assert.Equal("https://zoom.us/client/latest/ZoomInstallerFull.msi?archType=x64", def.DownloadUrl);
    }

    [Fact]
    public void TheVersionPatternReadsTheX64RedirectTarget()
    {
        var pattern = DefinitionFixture.Load("zoom.json").VersionScraper.Pattern["REDIR:".Length..];
        var match = Regex.Match("https://cdn.zoom.us/prod/7.2.5.51704/x64/ZoomInstaller.exe", pattern);
        Assert.True(match.Success);
        Assert.Equal("7.2.5.51704", match.Groups[1].Value);
    }
}
