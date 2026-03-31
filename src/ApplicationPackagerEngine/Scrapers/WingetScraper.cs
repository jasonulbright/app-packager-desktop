using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Queries winget for the latest version of a package.
/// Requires winget.exe installed and repository access (Microsoft Store GPO or standalone).
/// Non-functional stub — implementation pending.
/// </summary>
public sealed class WingetScraper : IVersionScraper
{
    public string Type => "Winget";

    public Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        // TODO: implement winget show --id <WingetId> --source winget
        // Parse version from output
        // For now, return null (caller falls back to static URL scraper)
        return Task.FromResult<string?>(null);
    }
}
