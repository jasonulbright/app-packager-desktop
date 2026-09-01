using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// URL templating and relative-URL resolution for multi-hop scrapes.
/// The fetch itself is not exercised — no network in unit tests.
/// </summary>
public class ChainedHttpRegexScraperTests
{
    [Theory]
    [InlineData("{capture}", "/products/node-122")]
    [InlineData("https://host.test{capture}", "https://host.test/products/node-122")]
    [InlineData("https://host.test/send/{capture0}", "https://host.test/send//products/node-122")]
    [InlineData("no placeholders", "no placeholders")]
    public void Substitute_ReplacesCapturePlaceholders(string template, string expected)
    {
        Assert.Equal(expected, ChainedHttpRegexScraper.Substitute(template, ["/products/node-122"], null));
    }

    [Fact]
    public void Substitute_CaptureRefersToMostRecentHop()
    {
        var result = ChainedHttpRegexScraper.Substitute("{capture0}|{capture1}|{capture}", ["first", "second"], null);
        Assert.Equal("first|second|second", result);
    }

    [Fact]
    public void Substitute_ReplacesVersionPlaceholder()
    {
        Assert.Equal("app-1.2.3.msi", ChainedHttpRegexScraper.Substitute("app-{version}.msi", [], "1.2.3"));
    }

    [Theory]
    [InlineData("https://host.test/a/b.html", "/c/d.exe", "https://host.test/c/d.exe")]
    [InlineData("https://host.test/a/b.html", "d.exe", "https://host.test/a/d.exe")]
    [InlineData("https://host.test/a/b.html", "https://other.test/x.exe", "https://other.test/x.exe")]
    [InlineData(null, "https://other.test/x.exe", "https://other.test/x.exe")]
    public void ResolveUrl_ResolvesAgainstSourcePage(string? baseUrl, string url, string expected)
    {
        Assert.Equal(expected, ChainedHttpRegexScraper.ResolveUrl(baseUrl, url));
    }

    [Theory]
    [InlineData(null, "/relative.exe")]
    [InlineData("https://host.test/a.html", "")]
    public void ResolveUrl_Unresolvable_ReturnsNull(string? baseUrl, string url)
    {
        Assert.Null(ChainedHttpRegexScraper.ResolveUrl(baseUrl, url));
    }

    [Fact]
    public void ResolveUrl_PreservesPercentEncoding()
    {
        var result = ChainedHttpRegexScraper.ResolveUrl(
            "https://host.test/downloads/index.html", "/files/App%20Setup%2012.2.exe");
        Assert.Equal("https://host.test/files/App%20Setup%2012.2.exe", result);
    }

    [Fact]
    public async Task Crawl_NoHops_ReturnsNull()
    {
        var scraper = new ChainedHttpRegexScraper();
        var config = new VersionScraperConfig { Type = "ChainedHttpRegex", Url = "https://host.test/" };

        Assert.Null(await scraper.GetLatestVersionAsync(config));
    }

    [Fact]
    public async Task Crawl_HopWithoutUrlAfterFirst_ReturnsNull()
    {
        var scraper = new ChainedHttpRegexScraper();
        var config = new VersionScraperConfig
        {
            Type = "ChainedHttpRegex",
            Url = "",
            Hops = [new ScraperHop { Pattern = "x" }]
        };

        Assert.Null(await scraper.GetLatestVersionAsync(config));
    }
}
