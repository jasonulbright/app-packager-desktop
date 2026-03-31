using System.Reflection;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Discovers and registers IVersionScraper implementations via reflection.
/// Maps Type strings to scraper instances. Adding a new scraper = add a class
/// implementing IVersionScraper, it's automatically available.
/// </summary>
public sealed class ScraperRegistry
{
    private readonly Dictionary<string, IVersionScraper> _scrapers = new(StringComparer.OrdinalIgnoreCase);

    public ScraperRegistry()
    {
        // Discover all IVersionScraper implementations in this assembly
        var scraperTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetInterfaces().Contains(typeof(IVersionScraper)));

        foreach (var type in scraperTypes)
        {
            if (Activator.CreateInstance(type) is IVersionScraper scraper)
                _scrapers[scraper.Type] = scraper;
        }
    }

    /// <summary>
    /// Gets the scraper for the given type string, or null if not registered.
    /// </summary>
    public IVersionScraper? Get(string type)
    {
        _scrapers.TryGetValue(type, out var scraper);
        return scraper;
    }

    /// <summary>
    /// Scrapes the latest version using the config's specified scraper type.
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        var scraper = Get(config.Type);
        if (scraper is null)
            throw new InvalidOperationException($"No scraper registered for type '{config.Type}'");

        return await scraper.GetLatestVersionAsync(config, ct);
    }

    public IReadOnlyCollection<string> RegisteredTypes => _scrapers.Keys;
}
