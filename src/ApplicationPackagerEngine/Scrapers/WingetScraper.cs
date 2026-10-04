using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Scraper type "Winget". It starts no process and returns null for every package, so a
/// caller that tries it first falls back to the definition's VersionScraper.
/// </summary>
public sealed class WingetScraper : IVersionScraper
{
    public string Type => "Winget";

    public Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }
}
