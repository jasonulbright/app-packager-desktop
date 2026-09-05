using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Scrapers;

/// <summary>
/// Multi-hop page crawl. Each hop fetches a URL and applies a regex; the capture
/// substitutes into the next hop's URL template as {capture} (or {captureN} for an
/// earlier hop). Named groups "version" and "url" collect the results, a later hop
/// overriding an earlier one.
///
/// Derives from HttpRegexScraper so that download-URL resolution treats it as a
/// regex scraper; every fetch and extraction path is overridden.
/// </summary>
public sealed class ChainedHttpRegexScraper : HttpRegexScraper
{
    public override string Type => "ChainedHttpRegex";

    public override async Task<string?> GetLatestVersionAsync(VersionScraperConfig config, CancellationToken ct = default)
    {
        var (version, _) = await CrawlAsync(config, ct);
        return version;
    }

    public override async Task<(string? Version, string? DownloadUrl)> GetVersionAndDownloadUrlAsync(
        VersionScraperConfig config, CancellationToken ct = default)
        => await CrawlAsync(config, ct);

    /// <summary>
    /// Runs the hops of <paramref name="config"/>. Shared with the FileVersion
    /// scraper, whose hops locate the installer whose resource carries the version.
    /// </summary>
    public static async Task<(string? Version, string? DownloadUrl)> CrawlAsync(
        VersionScraperConfig config, CancellationToken ct)
    {
        if (config.Hops is null || config.Hops.Count == 0)
            return (null, null);

        var ua = config.NoUserAgent ? null : UserAgent;
        var captures = new List<string>();
        string? version = null;
        string? downloadUrl = null;
        string? currentUrl = null;

        for (var i = 0; i < config.Hops.Count; i++)
        {
            var hop = config.Hops[i];
            var template = string.IsNullOrWhiteSpace(hop.Url)
                ? (i == 0 ? config.Url : null)
                : hop.Url;
            if (string.IsNullOrWhiteSpace(template)) return (null, null);

            var url = Substitute(template, captures, version);
            url = ResolveUrl(currentUrl, url);
            if (url is null) return (null, null);

            var content = await FetchViaIwrAsync(url, ct, ua);
            if (content is null) return (null, null);

            var match = SelectMatch(content, hop);
            if (match is null) return (null, null);

            currentUrl = url;
            captures.Add(GroupValue(match, "capture") ?? (match.Groups.Count > 1 ? match.Groups[1].Value : ""));

            var hopVersion = GroupValue(match, "version");
            if (hopVersion is not null) version = hopVersion;

            var hopUrl = GroupValue(match, "url");
            if (hopUrl is not null) downloadUrl = ResolveUrl(url, WebUtilityDecode(hopUrl));
        }

        return (version, downloadUrl);
    }

    private static Match? SelectMatch(string content, ScraperHop hop)
    {
        if (string.IsNullOrWhiteSpace(hop.Pattern)) return null;

        var matches = Regex.Matches(content, hop.Pattern, RegexOptions.Multiline);
        if (matches.Count == 0) return null;
        if (!hop.SelectHighestVersion) return matches[0];

        Match? best = null;
        LooseVersion? bestVersion = null;
        foreach (Match m in matches)
        {
            var key = GroupValue(m, "capture") ?? (m.Groups.Count > 1 ? m.Groups[1].Value : null);
            var parsed = LooseVersion.TryParse(GroupValue(m, "version") ?? key);
            if (parsed is null) continue;
            if (bestVersion is null || parsed.CompareTo(bestVersion) > 0)
            {
                bestVersion = parsed;
                best = m;
            }
        }
        return best ?? matches[0];
    }

    private static string? GroupValue(Match match, string name)
    {
        var group = match.Groups[name];
        return group.Success ? group.Value : null;
    }

    public static string Substitute(string template, IReadOnlyList<string> captures, string? version)
    {
        var result = template;
        for (var i = 0; i < captures.Count; i++)
            result = result.Replace($"{{capture{i}}}", captures[i], StringComparison.OrdinalIgnoreCase);
        if (captures.Count > 0)
            result = result.Replace("{capture}", captures[^1], StringComparison.OrdinalIgnoreCase);
        if (version is not null)
            result = result.Replace("{version}", version, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Resolves a possibly relative URL against the URL of the page it came from.
    /// </summary>
    public static string? ResolveUrl(string? baseUrl, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.Contains("://")) return url;
        if (baseUrl is null) return null;
        return Uri.TryCreate(new Uri(baseUrl), url, out var absolute) ? absolute.AbsoluteUri : null;
    }

    private static string WebUtilityDecode(string value)
        => value.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
}
