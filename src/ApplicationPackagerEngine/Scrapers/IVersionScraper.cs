using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Plugin interface for version scraping. Implementations are discovered
/// via reflection and registered by their Type string.
/// </summary>
public interface IVersionScraper
{
    /// <summary>
    /// Unique type identifier matching VersionScraperConfig.Type in JSON definitions.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Scrapes the latest available version from the configured source.
    /// </summary>
    Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default);
}
