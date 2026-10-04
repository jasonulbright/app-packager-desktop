using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The hop patterns of multi-hop scraper definitions, run against canned
/// response fragments. Guards the regexes and the definitions
/// together without touching the network.
/// </summary>
public class CatalogPortScraperTests
{
    private static ScraperHop Hop(string definition, int index)
    {
        var config = DefinitionFixture.Load(definition).VersionScraper;
        Assert.NotNull(config.Hops);
        return config.Hops![index];
    }

    private static Match Run(ScraperHop hop, string content)
    {
        var match = Regex.Match(content, hop.Pattern, RegexOptions.Multiline);
        Assert.True(match.Success, $"Pattern did not match: {hop.Pattern}");
        return match;
    }

    private const string OmnissaDlgList =
        """{"dlgEditionsLists":[{"name":"Omnissa Horizon Client for Windows","dlgList":[{"name":"Omnissa Horizon Client for Windows","code":"CART27FQ2_WIN_2606","releaseDate":"2026-08-24 15:00:00.0","productId":"1616"}]}]}""";

    private const string OmnissaDlgDetails =
        """{"downloadFiles":[{"fileName":"Omnissa-Horizon-Client-2606-8.19.0-32215845441.exe","sha256checksum":"4e8e54","fileType":"exe","version":"2606","thirdPartyDownloadUrl":"https://download3.omnissa.com/software/CART27FQ2_WIN_2606/Omnissa-Horizon-Client-2606-8.19.0-32215845441.exe"}]}""";

    [Fact]
    public void Omnissa_FirstHopCapturesTheDownloadGroupCode()
    {
        var match = Run(Hop("omnissahorizonclient.json", 0), OmnissaDlgList);
        Assert.Equal("CART27FQ2_WIN_2606", match.Groups["capture"].Value);
    }

    [Fact]
    public void Omnissa_SecondHopCapturesVersionAndDownloadUrl()
    {
        var match = Run(Hop("omnissahorizonclient.json", 1), OmnissaDlgDetails);
        Assert.Equal("8.19.0", match.Groups["version"].Value);
        Assert.Equal(
            "https://download3.omnissa.com/software/CART27FQ2_WIN_2606/Omnissa-Horizon-Client-2606-8.19.0-32215845441.exe",
            match.Groups["url"].Value);
    }

    [Fact]
    public void Omnissa_DefinitionCarriesNoPinnedDownloadGroup()
    {
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "omnissahorizonclient.json"));
        Assert.DoesNotContain("CART27FQ2_WIN_2606", json);
    }

    private const string MariaDbIndex =
        """{"major_releases": [{"release_id": "13.1", "release_status": "Preview", "release_support_type": null}, {"release_id": "13.0", "release_status": "RC", "release_support_type": "Rolling"}, {"release_id": "12.3", "release_status": "Stable", "release_support_type": "Long Term Support"}, {"release_id": "11.8", "release_status": "Stable", "release_support_type": "Long Term Support"}, {"release_id": "10.6", "release_status": "Stable", "release_support_type": "Short Term Support"}]}""";

    [Fact]
    public void MariaDb_FirstHopSkipsPreviewRcAndShortTermSeries()
    {
        var hop = Hop("mariadb-server.json", 0);
        var captures = Regex.Matches(MariaDbIndex, hop.Pattern, RegexOptions.Multiline)
            .Select(m => m.Groups["capture"].Value)
            .ToList();

        Assert.Equal(["12.3", "11.8"], captures);
    }

    [Fact]
    public void MariaDb_FirstHopSelectsTheHighestSeries()
    {
        var hop = Hop("mariadb-server.json", 0);
        Assert.True(hop.SelectHighestVersion);
    }

    [Fact]
    public void MariaDb_SecondHopCapturesThePointRelease()
    {
        var content = """{"releases": {"12.3.3": {"files": [{"file_name": "mariadb-12.3.3-winx64.msi"}]}}}""";
        Assert.Equal("12.3.3", Run(Hop("mariadb-server.json", 1), content).Groups["version"].Value);
    }

    [Fact]
    public void MariaDb_DefinitionPinsNoSeriesInTheScraperUrl()
    {
        var config = DefinitionFixture.Load("mariadb-server.json").VersionScraper;
        Assert.Equal("https://downloads.mariadb.org/rest-api/mariadb/", config.Url);
    }

    private const string AgentRansackPage =
        """<a href="//download.mythicsoft.com/flp/3562/X4aef2vyu.bzx/agentransack_x86_msi_3562.zip">x86</a><a href="//download.mythicsoft.com/flp/3562/X4aef2vyu.bzx/agentransack_x64_msi_3562.zip">x64</a>""";

    [Fact]
    public void AgentRansack_HopCapturesTokenizedHrefAndVersion()
    {
        var match = Run(Hop("agentransack.json", 0), AgentRansackPage);
        Assert.Equal("3562", match.Groups["version"].Value);
        Assert.Equal(
            "//download.mythicsoft.com/flp/3562/X4aef2vyu.bzx/agentransack_x64_msi_3562.zip",
            match.Groups["url"].Value);
    }

    [Fact]
    public void AgentRansack_ProtocolRelativeHrefResolvesAgainstThePage()
    {
        var match = Run(Hop("agentransack.json", 0), AgentRansackPage);
        var resolved = Scrapers.ChainedHttpRegexScraper.ResolveUrl(
            "https://www.mythicsoft.com/agentransack/download/", match.Groups["url"].Value);

        Assert.Equal(
            "https://download.mythicsoft.com/flp/3562/X4aef2vyu.bzx/agentransack_x64_msi_3562.zip",
            resolved);
    }

    [Fact]
    public void PdfStudioViewer_YearTokenAgreesAcrossEveryFieldThatCarriesIt()
    {
        var def = DefinitionFixture.Load("pdfstudioviewer.json");
        var years = new[]
        {
            def.InstallArgs,
            def.UninstallCommand,
            def.Detection?.FilePath,
            def.Detection?.FileName,
            def.RunningProcess.FirstOrDefault()
        }.Select(field => Regex.Match(field ?? "", @"PDFStudioViewer(\d{4})").Groups[1].Value).ToList();

        Assert.DoesNotContain("", years);
        Assert.Single(years.Distinct());
    }

    [Fact]
    public void SalesforceCli_KeepsTheNoUserAgentFeedFlag()
    {
        // The feed answers an HTML error page to a browser User-Agent.
        Assert.True(DefinitionFixture.Load("salesforcecli.json").VersionScraper.NoUserAgent);
    }

    [Fact]
    public void RvTools_ProbesTheDownloadHostFromAFloorVersion()
    {
        var config = DefinitionFixture.Load("rvtools.json").VersionScraper;
        Assert.Equal("HeadProbe", config.Type);
        Assert.Equal("4.8.1", config.Pattern);
        Assert.Contains("{version}", config.Url);
        Assert.True(config.MaxVersionHops > 0);
    }
}
