using System.Text;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Scrapers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A GitHub API request refused for the rate limit names the refusal and the token
/// remedy instead of ending as a scrape that found no version.
/// </summary>
public sealed class GitHubRateLimitTests
{
    private const string RateLimitBody =
        """{"message":"API rate limit exceeded for 203.0.113.7. (But here's the good news: Authenticated requests get a higher rate limit.)","documentation_url":"https://docs.github.com/rest/overview/resources-in-the-rest-api#rate-limiting"}""";

    private static (string, byte[]) RateLimited(int status) => (
        $"HTTP/1.1 {status} {(status == 403 ? "Forbidden" : "Too Many Requests")}\r\n" +
        "Content-Type: application/json; charset=utf-8\r\n" +
        "x-github-request-id: C0DE:1234:5678:9ABC:6720F00D\r\n" +
        "x-ratelimit-limit: 60\r\nx-ratelimit-remaining: 0\r\nx-ratelimit-used: 60\r\nx-ratelimit-reset: 1790000000\r\n" +
        "x-ratelimit-resource: core",
        Encoding.UTF8.GetBytes(RateLimitBody));

    [Theory]
    [InlineData("JsonQuery", "tag_name", 403)]
    [InlineData("HttpRegex", "\"tag_name\":\\s*\"v?([\\d.]+)\"", 403)]
    [InlineData("JsonQuery", "tag_name", 429)]
    public async Task ARateLimitedScrapeSaysSoAndNamesTheTokenRemedy(string type, string pattern, int status)
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]>(),
            new Dictionary<string, (string, byte[])> { ["repos/o/r/releases/latest"] = RateLimited(status) });
        var registry = new ScraperRegistry();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.GetLatestVersionAsync(
            new VersionScraperConfig { Type = type, Url = server.Url("repos/o/r/releases/latest"), Pattern = pattern }));

        Assert.StartsWith("GitHub API rate limit reached for " + server.Url("repos/o/r/releases/latest") + $" (HTTP {status}", ex.Message);
        Assert.Contains("GITHUB_TOKEN", ex.Message);
        Assert.Contains("gh auth login", ex.Message);
    }

    [Fact]
    public async Task AnOrdinaryForbiddenAnswerStaysANoVersionResult()
    {
        using var server = new LoopbackFileServer(new Dictionary<string, byte[]>(),
            new Dictionary<string, (string, byte[])> { ["page"] = ("HTTP/1.1 403 Forbidden\r\nContent-Type: text/html", Encoding.UTF8.GetBytes("<html>denied</html>")) });

        var version = await new ScraperRegistry().GetLatestVersionAsync(
            new VersionScraperConfig { Type = "HttpRegex", Url = server.Url("page"), Pattern = "v([\\d.]+)" });

        Assert.Null(version);
    }

    [Fact]
    public void OnlyTheFinalHopOfARedirectDecides()
    {
        const string headers = "HTTP/1.1 302 Found\r\nLocation: /x\r\n\r\nHTTP/1.1 200 OK\r\nx-github-request-id: 1\r\nx-ratelimit-remaining: 0\r\n\r\n";
        Assert.Null(GitHubRateLimit.Refusal("https://api.github.com/repos/o/r/releases/latest", headers, "{}"));
    }
}
