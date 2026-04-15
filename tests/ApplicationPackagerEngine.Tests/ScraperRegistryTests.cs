using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

public class ScraperRegistryTests
{
    private readonly ScraperRegistry _registry = new();

    [Fact]
    public void Constructor_DiscoversScraper()
    {
        Assert.NotEmpty(_registry.RegisteredTypes);
    }

    [Theory]
    [InlineData("HttpRegex")]
    [InlineData("JsonQuery")]
    [InlineData("FileVersion")]
    public void Get_KnownType_ReturnsScraper(string type)
    {
        var scraper = _registry.Get(type);
        Assert.NotNull(scraper);
    }

    [Theory]
    [InlineData("httpregex")]
    [InlineData("JSONQUERY")]
    [InlineData("fileversion")]
    public void Get_CaseInsensitive(string type)
    {
        var scraper = _registry.Get(type);
        Assert.NotNull(scraper);
    }

    [Fact]
    public void Get_UnknownType_ReturnsNull()
    {
        var scraper = _registry.Get("NonExistentScraper");
        Assert.Null(scraper);
    }
}
