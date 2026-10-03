using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Git for Windows names its installer with three version parts for a first
/// build of a release (Git-2.56.0-64-bit.exe) and four for a rebuild
/// (Git-2.53.0.2-64-bit.exe). The download URL repeats the captured text, so
/// the capture has to be the exact text of the asset name.
/// </summary>
public sealed class GitDefinitionTests
{
    [Theory]
    [InlineData("Git-2.56.0-64-bit.exe", "2.56.0")]
    [InlineData("Git-2.53.0.2-64-bit.exe", "2.53.0.2")]
    public void TheScraperCapturesTheVersionTextOfTheAssetName(string asset, string expected)
    {
        var def = DefinitionFixture.Load("git.json");
        var json = $"{{\"assets\":[{{\"name\": \"Git-9.9.9-32-bit.exe\"}},{{\"name\": \"{asset}\"}}]}}";

        var match = Regex.Match(json, def.VersionScraper.Pattern!);

        Assert.True(match.Success);
        Assert.Equal(expected, match.Groups[1].Value);
        Assert.Equal($"https://github.com/git-for-windows/git/releases/latest/download/{asset}",
            def.DownloadUrl!.Replace("{version}", match.Groups[1].Value));
    }

    [Theory]
    [InlineData("Git-2.56.0-64-bit.tar.bz2")]
    [InlineData("PortableGit-2.56.0-64-bit.7z.exe")]
    [InlineData("Git-2.56.0-arm64.exe")]
    public void OtherAssetsOfTheRelease_DoNotMatch(string asset)
    {
        var def = DefinitionFixture.Load("git.json");

        Assert.False(Regex.IsMatch($"\"name\": \"{asset}\"", def.VersionScraper.Pattern!));
    }
}
