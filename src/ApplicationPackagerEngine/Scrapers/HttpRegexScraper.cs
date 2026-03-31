using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Scrapes a web page via HTTP GET and extracts a version string
/// using a regex pattern. Covers ~80% of apps (vendor download pages,
/// API endpoints returning JSON/HTML with version strings).
/// </summary>
public sealed class HttpRegexScraper : IVersionScraper
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "ApplicationPackager/2.1" }
        }
    };

    public string Type => "HttpRegex";

    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url) || string.IsNullOrWhiteSpace(config.Pattern))
            return null;

        var content = await Http.GetStringAsync(config.Url, ct);

        var match = Regex.Match(content, config.Pattern);
        if (match.Success && match.Groups.Count > 1)
            return match.Groups[1].Value;

        return null;
    }
}
