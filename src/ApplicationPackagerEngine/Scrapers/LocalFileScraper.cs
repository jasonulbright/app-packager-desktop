using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// The version scraper of a definition that has no web version source (LocalSource.VersionFromFile): the vendor gives
/// the installer only through a sign-in or behind a bot protection, so the version is the version of the file the
/// operator puts in the installer source folder. A caller that reaches this scraper gets a plain message, never an
/// empty answer.
/// </summary>
public sealed class LocalFileScraper : IVersionScraper
{
    public string Type => "LocalFile";

    public Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "This application has no web version source: its version is read from the installer in the installer source folder (Settings > Local Installer Sources).");
}
