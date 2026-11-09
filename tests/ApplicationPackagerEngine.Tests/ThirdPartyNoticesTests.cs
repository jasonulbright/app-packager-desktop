using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The notices page and the licenses folder agree: every file that the page names exists, every file in the folder is named,
/// the release stage copies the folder, and the installer's own toolkit (NSIS, with its LZMA module) is named with its license text.
/// </summary>
public sealed class ThirdPartyNoticesTests
{
    private static string RepoFile(params string[] parts)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "installer", "AppPackagerDesktop.nsi"))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path.Combine([folder.FullName, .. parts]);
    }

    private static readonly string Notices = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));

    private static HashSet<string> NamedFiles() =>
        Regex.Matches(Notices, @"`([A-Za-z0-9._-]+\.(?:txt|md))`").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EveryFileThatTheNoticesNameExistsInTheLicensesFolder()
    {
        var missing = NamedFiles().Where(name => !File.Exists(RepoFile("licenses", name))).ToList();

        Assert.True(missing.Count == 0, "THIRD-PARTY-NOTICES.md names files that are not in licenses: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryFileInTheLicensesFolderIsNamedInTheNotices()
    {
        var named = NamedFiles();
        var unnamed = Directory.GetFiles(RepoFile("licenses")).Select(Path.GetFileName).Where(name => !named.Contains(name!)).ToList();

        Assert.True(unnamed.Count == 0, "licenses holds files that THIRD-PARTY-NOTICES.md does not name: " + string.Join(", ", unnamed));
    }

    [Fact]
    public void TheNsisLicenseTextIsInTheFolderAndTheNoticesNameItsThreeLicenses()
    {
        var text = File.ReadAllText(RepoFile("licenses", "nsis-COPYING.txt"));

        Assert.Contains("ZLIB/LIBPNG LICENSE", text);
        Assert.Contains("COMMON PUBLIC LICENSE VERSION 1.0", text);
        Assert.Contains("BZIP2 LICENSE", text);
        Assert.Contains("NSIS (Nullsoft Scriptable Install System)", Notices);
        Assert.Contains("Common Public License 1.0 for the LZMA compression module", Notices);
        Assert.Contains("`nsis-COPYING.txt`", Notices);
    }

    [Fact]
    public void TheReleaseStageCopiesTheLicensesFolderAndTheInstallerCompressesWithLzma()
    {
        Assert.Contains("Join-Path $repoRoot 'licenses'", File.ReadAllText(RepoFile("tools", "build-release.ps1")));
        Assert.Contains("SetCompressor /SOLID lzma", File.ReadAllText(RepoFile("installer", "AppPackagerDesktop.nsi")));
    }
}
