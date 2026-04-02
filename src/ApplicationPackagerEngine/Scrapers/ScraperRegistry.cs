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

    /// <summary>
    /// Gets the latest version with VersionTransform applied for display/detection.
    /// Returns (rawVersion, displayVersion). Raw is used for download URLs, display for everything else.
    /// </summary>
    public async Task<(string? Raw, string? Display)> GetLatestVersionWithTransformAsync(
        VersionScraperConfig config, CancellationToken ct = default)
    {
        var raw = await GetLatestVersionAsync(config, ct);
        if (raw is null) return (null, null);

        var display = !string.IsNullOrEmpty(config.VersionTransform)
            ? ApplyTransform(raw, config.VersionTransform)
            : raw;

        return (raw, display);
    }

    private static string ApplyTransform(string version, string transform)
    {
        foreach (var step in transform.Split('|'))
        {
            var trimmed = step.Trim();

            if (trimmed.StartsWith("insert_dot:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(trimmed[11..], out var pos) && pos >= 0 && pos < version.Length)
                    version = version[..pos] + "." + version[pos..];
            }
            else if (trimmed.StartsWith("replace:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed[8..].Split(':', 2);
                if (parts.Length == 2)
                    version = version.Replace(parts[0], parts[1]);
            }
            else if (trimmed.StartsWith("strip_prefix:", StringComparison.OrdinalIgnoreCase))
            {
                var prefix = trimmed[13..];
                if (version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    version = version[prefix.Length..];
            }
            else if (trimmed.StartsWith("regex_replace:", StringComparison.OrdinalIgnoreCase))
            {
                // regex_replace:PATTERN:REPLACEMENT (empty replacement = delete)
                var parts = trimmed[14..].Split(':', 2);
                if (parts.Length >= 1)
                {
                    var replacement = parts.Length == 2 ? parts[1] : "";
                    version = System.Text.RegularExpressions.Regex.Replace(version, parts[0], replacement);
                }
            }
        }

        return version;
    }

    public IReadOnlyCollection<string> RegisteredTypes => _scrapers.Keys;
}
