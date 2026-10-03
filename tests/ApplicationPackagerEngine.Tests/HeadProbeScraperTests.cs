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
    public async Task BuildWalk_SkipsUpToTwoMissingBuildsAndStopsAfterThree()
    {
        var served = new HashSet<int> { 10, 11, 14, 18 };
        var probed = new List<string>();
        Task<bool> Probe(string url, CancellationToken _)
        {
            probed.Add(url);
            return Task.FromResult(served.Contains(int.Parse(url["https://host.test/s".Length..^4])));
        }

        Assert.Equal(14, await HeadProbeScraper.WalkBuildNumberAsync("https://host.test/s{build}.exe", "10", 12, Probe));
        Assert.DoesNotContain("https://host.test/s18.exe", probed);
    }

    [Fact]
    public async Task BuildWalk_StopsAtTheHopLimit()
    {
        Assert.Equal(5, await HeadProbeScraper.WalkBuildNumberAsync("https://host.test/s{build}.exe", "2", 3, (_, _) => Task.FromResult(true)));
    }

    [Theory]
    [InlineData("2.22")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task BuildWalk_RejectsAFloorThatIsNotAWholeNumber(string floor)
    {
        Assert.Null(await HeadProbeScraper.WalkBuildNumberAsync("https://host.test/s{build}.exe", floor, 3, (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public void Registry_ResolvesHeadProbe()
    {
        Assert.IsType<HeadProbeScraper>(new ScraperRegistry().Get("HeadProbe"));
    }
}
