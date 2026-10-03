using System.Text.Json;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Definitions whose vendor changed the download shape (installer type, asset
/// name, page layout), run against canned response fragments so the patterns
/// and the definition files are guarded together without the network.
/// </summary>
public class VendorSourceChangeTests
{
    private static string? Evaluate(string json, string query)
    {
        using var doc = JsonDocument.Parse(json);
        var method = typeof(JsonQueryScraper).GetMethod(
            "EvaluateQuery",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, [doc.RootElement, query]) as string;
    }

    /// <summary>Mirrors HttpRegexScraper's DownloadUrlQuery evaluation for a captured version.</summary>
    private static string? DownloadUrlFor(VersionScraperConfig config, string content, string version)
    {
        var pattern = config.DownloadUrlQuery!.Replace("{version}", Regex.Escape(version));
        var match = Regex.Match(content, pattern, RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── Slack: machine-wide MSI withdrawn, MSIX is the enterprise install ──

    private const string SlackRelease =
        """{"ok":true,"version":"4.52.155","download_url":"https:\/\/downloads.slack-edge.com\/desktop-releases\/windows\/x64\/4.52.155\/Slack.msix","platform":"win32","arch":"x64"}""";

    [Fact]
    public void Slack_StagesTheMsixTheReleaseApiNames()
    {
        var def = DefinitionFixture.Load("slack.json");

        Assert.Equal("MSIX", def.InstallerType);
        Assert.Equal("MSIX", def.InstallerFramework);
        Assert.Contains("variant=msix", def.VersionScraper.Url);
        Assert.Equal("4.52.155", Evaluate(SlackRelease, def.VersionScraper.Pattern));
        Assert.Equal(
            "https://downloads.slack-edge.com/desktop-releases/windows/x64/4.52.155/Slack.msix",
            Evaluate(SlackRelease, def.VersionScraper.DownloadUrlQuery!));
        Assert.Equal("Slack.msix", def.InstallerFileName);
        Assert.DoesNotContain(".msi\"", File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "slack.json")));
    }

    // ── Audacity: 4.0 ships an MSI, the Inno EXE asset is gone ──

    private const string AudacityRelease =
        """{"tag_name":"Audacity-4.0.0","assets":[{"name":"audacity-win-4.0.0-arm64.msi"},{"name":"audacity-win-4.0.0-x86_64.msi"}]}""";

    [Fact]
    public void Audacity_TakesTheX64MsiWithProductCodeDetection()
    {
        var def = DefinitionFixture.Load("audacity.json");

        Assert.Equal("MSI", def.InstallerType);
        Assert.Equal("Audacity-4.0.0", Evaluate(AudacityRelease, def.VersionScraper.Pattern));
        Assert.Equal("strip_prefix:Audacity-", def.VersionScraper.VersionTransform);

        var url = def.DownloadUrl.Replace("{display_version}", "4.0.0");
        Assert.Equal(
            "https://github.com/audacity/audacity/releases/download/Audacity-4.0.0/audacity-win-4.0.0-x86_64.msi",
            url);
        Assert.Equal("RegistryKeyValue", def.Detection.Type);
        Assert.Contains("{ProductCode}", def.Detection.RegistryKeyTemplate);
        Assert.True(def.Detection.Is64Bit);
    }

    // ── ShareX: 21.0 splits the setup by architecture ──

    private const string ShareXRelease = """
        {"tag_name":"v21.0.0","assets":[
          {"name":"ShareX-21.0.0-portable-x64.zip","browser_download_url":"https://github.com/ShareX/ShareX/releases/download/v21.0.0/ShareX-21.0.0-portable-x64.zip"},
          {"name":"ShareX-21.0.0-setup-x64.exe","browser_download_url":"https://github.com/ShareX/ShareX/releases/download/v21.0.0/ShareX-21.0.0-setup-x64.exe"}]}
        """;

    private const string ShareXOlderRelease = """
        {"tag_name":"v18.0.1","assets":[
          {"name":"ShareX-18.0.1-portable.zip","browser_download_url":"https://github.com/ShareX/ShareX/releases/download/v18.0.1/ShareX-18.0.1-portable.zip"},
          {"name":"ShareX-18.0.1-setup.exe","browser_download_url":"https://github.com/ShareX/ShareX/releases/download/v18.0.1/ShareX-18.0.1-setup.exe"}]}
        """;

    [Fact]
    public void ShareX_PrefersTheX64SetupAsset()
    {
        var config = DefinitionFixture.Load("sharex.json").VersionScraper;

        Assert.Equal("21.0.0", HttpRegexScraper.ExtractVersion(ShareXRelease, config.Pattern, false));
        Assert.Equal(
            "https://github.com/ShareX/ShareX/releases/download/v21.0.0/ShareX-21.0.0-setup-x64.exe",
            DownloadUrlFor(config, ShareXRelease, "21.0.0"));
    }

    [Fact]
    public void ShareX_FallsBackToTheSingleSetupOfOlderReleases()
    {
        var def = DefinitionFixture.Load("sharex.json");

        Assert.Equal("18.0.1", HttpRegexScraper.ExtractVersion(ShareXOlderRelease, def.VersionScraper.Pattern, false));
        Assert.Null(DownloadUrlFor(def.VersionScraper, ShareXOlderRelease, "18.0.1"));
        Assert.Equal(
            "https://github.com/ShareX/ShareX/releases/download/v18.0.1/ShareX-18.0.1-setup.exe",
            def.DownloadUrl.Replace("{version}", "18.0.1"));
    }

    // ── Apache NetBeans: Windows installer from the codelerity packages ──

    private const string NetBeansRelease = """
        {"tag_name":"v31-build1","assets":[
          {"name":"apache-netbeans-31-0.x86_64.rpm","browser_download_url":"https://github.com/codelerity/netbeans-packages/releases/download/v31-build1/apache-netbeans-31-0.x86_64.rpm"},
          {"name":"Apache-NetBeans-31-x86_64.pkg","browser_download_url":"https://github.com/codelerity/netbeans-packages/releases/download/v31-build1/Apache-NetBeans-31-x86_64.pkg"},
          {"name":"Apache-NetBeans-31.exe","browser_download_url":"https://github.com/codelerity/netbeans-packages/releases/download/v31-build1/Apache-NetBeans-31.exe"}]}
        """;

    [Fact]
    public void NetBeans_TakesTheVersionFromTheExeAssetNotTheTag()
    {
        var def = DefinitionFixture.Load("netbeans.json");

        Assert.Equal("31", HttpRegexScraper.ExtractVersion(NetBeansRelease, def.VersionScraper.Pattern, false));
        Assert.Equal(
            "https://github.com/codelerity/netbeans-packages/releases/download/v31-build1/Apache-NetBeans-31.exe",
            DownloadUrlFor(def.VersionScraper, NetBeansRelease, "31"));
        Assert.Equal("InnoSetup", def.InstallerFramework);
        Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Apache NetBeans_is1", def.Detection.RegistryKeyTemplate);
        Assert.True(def.Detection.Is64Bit);
    }

    // ── Defraggler: the builds page is gone, the download host names the build, the resource carries the version ──

    [Fact]
    public void Defraggler_ProbesTheDownloadHostNotTheRemovedBuildsPage()
    {
        var config = DefinitionFixture.Load("defraggler.json").VersionScraper;

        Assert.Equal("FileVersion", config.Type);
        Assert.Equal("CHAINED", config.DownloadUrlQuery);
        Assert.Null(config.Hops);
        Assert.DoesNotContain("/builds", config.Url);
        Assert.Equal("https://download.ccleaner.com/dfsetup{build}.exe", config.Url);
        Assert.Equal("222", config.ProbeFloor);
        Assert.True(FileVersionScraper.DiscoversUrl(config));
    }

    [Fact]
    public async Task Defraggler_TheNewestServedBuildNamesTheDownload()
    {
        var config = DefinitionFixture.Load("defraggler.json").VersionScraper;
        var served = new HashSet<string>
        {
            "https://download.ccleaner.com/dfsetup222.exe",
            "https://download.ccleaner.com/dfsetup223.exe",
            "https://download.ccleaner.com/dfsetup225.exe"
        };

        var url = await FileVersionScraper.ResolveDownloadUrlAsync(config, default, (u, _) => Task.FromResult(served.Contains(u)));

        Assert.Equal("https://download.ccleaner.com/dfsetup225.exe", url);
    }

    [Fact]
    public async Task Defraggler_AFloorTheHostNoLongerServesFailsTheScrape()
    {
        var config = DefinitionFixture.Load("defraggler.json").VersionScraper;

        Assert.Null(await FileVersionScraper.ResolveDownloadUrlAsync(config, default, (_, _) => Task.FromResult(false)));
    }

    [Fact]
    public async Task Defraggler_ResourceVersionCollapsesToTheVendorForm()
    {
        var config = DefinitionFixture.Load("defraggler.json").VersionScraper;
        var method = typeof(ScraperRegistry).GetMethod("ApplyTransform",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Equal("2.22.995", method!.Invoke(null, ["2.22.33.995", config.VersionTransform!]));
        Assert.Equal("2.23.1000", method.Invoke(null, ["2.23.0.1000", config.VersionTransform!]));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FileVersionScraper_WithoutHopsUsesTheConfiguredUrl()
    {
        var plain = new VersionScraperConfig { Type = "FileVersion", Url = "https://example.test/setup.exe", Pattern = "FileVersion" };
        Assert.Equal("https://example.test/setup.exe", await FileVersionScraper.ResolveDownloadUrlAsync(plain));

        var empty = new VersionScraperConfig { Type = "FileVersion", Url = "", Pattern = "FileVersion" };
        Assert.Null(await FileVersionScraper.ResolveDownloadUrlAsync(empty));
    }

    // ── Obsidian: the release tagged latest can be mobile-only ──

    private const string ObsidianReleases = """
        [{"tag_name":"v1.13.8","prerelease":false,"assets":[{"name":"obsidian-1.13.8.apk","browser_download_url":"https://github.com/obsidianmd/obsidian-releases/releases/download/v1.13.8/obsidian-1.13.8.apk"}]},
         {"tag_name":"v1.13.7","prerelease":false,"assets":[{"name":"Obsidian-1.13.7.exe","browser_download_url":"https://github.com/obsidianmd/obsidian-releases/releases/download/v1.13.7/Obsidian-1.13.7.exe"},{"name":"Obsidian-1.13.7-arm64.exe","browser_download_url":"https://github.com/obsidianmd/obsidian-releases/releases/download/v1.13.7/Obsidian-1.13.7-arm64.exe"}]},
         {"tag_name":"v1.13.6","prerelease":false,"assets":[{"name":"Obsidian-1.13.6.exe","browser_download_url":"https://github.com/obsidianmd/obsidian-releases/releases/download/v1.13.6/Obsidian-1.13.6.exe"}]}]
        """;

    [Fact]
    public void Obsidian_TakesTheNewestReleaseThatCarriesTheWindowsInstaller()
    {
        var def = DefinitionFixture.Load("obsidian.json");

        Assert.True(def.VersionScraper.SelectHighestVersion);
        Assert.Equal("1.13.7", HttpRegexScraper.ExtractVersion(ObsidianReleases, def.VersionScraper.Pattern, true));
        Assert.Equal(
            "https://github.com/obsidianmd/obsidian-releases/releases/download/v1.13.7/Obsidian-1.13.7.exe",
            def.DownloadUrl.Replace("{version}", "1.13.7"));
    }

    // ── WizTree: the file host moved and the page quotes an old name in prose ──

    private const string WizTreeDownloadPage = """
        <p>The installer is named like <samp>wiztree_4_14_setup.exe</samp>.</p>
        <a href="files/wiztree_4_32_setup.exe">Download installer</a>
        <a href="files/wiztree_4_32_portable.zip">Download portable</a>
        """;

    [Fact]
    public void WizTree_ReadsTheLinkedSetupNotTheProseExample()
    {
        var def = DefinitionFixture.Load("wiztree.json");

        Assert.Equal("4_32", HttpRegexScraper.ExtractVersion(WizTreeDownloadPage, def.VersionScraper.Pattern, false));
        Assert.Equal("replace:_:.", def.VersionScraper.VersionTransform);
        Assert.Equal("https://diskanalyzer.com/files/wiztree_4_32_setup.exe", def.DownloadUrl.Replace("{version}", "4_32"));
        Assert.Equal("InnoSetup", def.InstallerFramework);
    }

    // ── Registry view corrections from real installs ──

    [Theory]
    [InlineData("gpg4win.json", true)]
    [InlineData("kdiff3.json", false)]
    [InlineData("rstudio.json", true)]
    public void DetectionView_MatchesTheInstalledKey(string definition, bool is64Bit)
    {
        var det = DefinitionFixture.Load(definition).Detection;
        Assert.Equal(is64Bit, det.Is64Bit);
        Assert.DoesNotContain("WOW6432Node", det.RegistryKeyTemplate);
    }

    [Fact]
    public void RStudio_InstallsForAllUsers()
    {
        var def = DefinitionFixture.Load("rstudio.json");
        Assert.Equal("/S /allusers", def.InstallArgs);
        Assert.Equal("/S /allusers", def.UninstallArgs);
        Assert.Contains("\"/allusers\"", def.UninstallCommand);
    }
}
