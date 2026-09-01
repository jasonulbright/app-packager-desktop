using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Floor parsing and probe-URL construction for the version walk.
/// The probes themselves are not exercised — no network in unit tests.
/// </summary>
public class HeadProbeScraperTests
{
    [Fact]
    public void ParseTriple_ReadsMajorMinorPatch()
    {
        Assert.Equal((4, 8, 1), HeadProbeScraper.ParseTriple("4.8.1"));
    }

    [Theory]
    [InlineData("4.8")]
    [InlineData("4.8.1.2")]
    [InlineData("4.8.x")]
    [InlineData("")]
    public void ParseTriple_RejectsAnythingButThreeNumericParts(string version)
    {
        Assert.Null(HeadProbeScraper.ParseTriple(version));
    }

    [Fact]
    public void BuildUrl_SubstitutesVersionPlaceholder()
    {
        Assert.Equal(
            "https://host.test/rvtools4.8.2.msi",
            HeadProbeScraper.BuildUrl("https://host.test/rvtools{version}.msi", "4.8.2"));
    }

    [Fact]
    public void BuildUrl_PlaceholderIsCaseInsensitive()
    {
        Assert.Equal("https://host.test/a-1.0.0.msi",
            HeadProbeScraper.BuildUrl("https://host.test/a-{VERSION}.msi", "1.0.0"));
    }

    [Fact]
    public void Registry_ResolvesHeadProbe()
    {
        Assert.IsType<HeadProbeScraper>(new ScraperRegistry().Get("HeadProbe"));
    }
}
