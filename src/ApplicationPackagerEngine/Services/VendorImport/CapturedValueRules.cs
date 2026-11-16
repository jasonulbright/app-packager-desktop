using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>
/// The rules for a value that a scraper captured from one host and that goes into the address of another ("Validation" rules 7 and 8, and "Imported
/// definitions run with no ambient credentials", rule 2): such a value could send what a first request read to the vendor, with the token of the
/// administrator or from an intranet page that needs no credential. A captured value goes to another host only when it is a short version.
/// </summary>
public static partial class CapturedValueRules
{
    [GeneratedRegex(@"^\d{1,10}(\.\d{1,10}){0,3}([.+_-][A-Za-z0-9]{1,16})?\z")]
    private static partial Regex ShortVersion();

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Placeholder();

    /// <summary>The short version grammar of the specification, rule 8.</summary>
    public static bool IsShortVersion(string? value) => value is not null && ShortVersion().IsMatch(value);

    /// <summary>The placeholders of an address template that are not {version} or {display_version}: a captured value that no grammar covers statically.</summary>
    public static IReadOnlyList<string> OtherPlaceholders(string template) =>
        Placeholder().Matches(template).Select(m => m.Value)
            .Where(p => !p.Equals("{version}", StringComparison.OrdinalIgnoreCase) && !p.Equals("{display_version}", StringComparison.OrdinalIgnoreCase)).ToList();

    private static string? HostOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var concrete = Placeholder().Replace(address.Trim(), "x");
        return Uri.TryCreate(concrete, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>
    /// The addresses of the definition that take a captured value and sit on a host other than the one that the value was read from, with the
    /// placeholder that no grammar covers. The source host is the host of the version source (and, for a hop, the host of the hop before it).
    /// </summary>
    public static IReadOnlyList<(string Field, string Placeholder)> CrossHostCaptures(AppDefinition d)
    {
        var found = new List<(string, string)>();
        var sourceHosts = new List<string>();
        if (HostOf(d.VersionScraper?.Url) is { } first) sourceHosts.Add(first);
        var hopHosts = new List<string>(sourceHosts);
        foreach (var (hop, index) in (d.VersionScraper?.Hops ?? []).Select((h, i) => (h, i)))
        {
            var host = HostOf(hop.Url);
            if (host is not null && index > 0 && !hopHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                foreach (var p in OtherPlaceholders(hop.Url)) found.Add(($"VersionScraper.Hops[{index}].Url", p));
            if (host is not null) hopHosts.Add(host);
        }
        void Check(string field, string? address)
        {
            var host = HostOf(address);
            if (host is null || address is null || sourceHosts.Count == 0 || sourceHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) return;
            foreach (var p in OtherPlaceholders(address)) found.Add((field, p));
        }
        Check("DownloadUrl", d.DownloadUrl);
        Check("PayloadSha256Url", d.PayloadSha256Url);
        Check("VersionScraper.ServedUrlTemplate", d.VersionScraper?.ServedUrlTemplate);
        Check("BootstrapperLayout.BootstrapperUrl", d.BootstrapperLayout?.BootstrapperUrl);
        foreach (var (variant, index) in (d.PayloadVariants ?? []).Select((v, i) => (v, i)))
            Check($"PayloadVariants[{index}].DownloadUrl", variant.DownloadUrl);
        return found;
    }

    /// <summary>True when the version goes into an address on another host than the version source, so that it must be a short version.</summary>
    public static bool VersionCrossesHosts(AppDefinition d)
    {
        var source = HostOf(d.VersionScraper?.Url);
        if (source is null) return false;
        return new[] { d.DownloadUrl, d.PayloadSha256Url, d.VersionScraper?.ServedUrlTemplate }.Concat((d.PayloadVariants ?? []).Select(v => v.DownloadUrl))
            .Where(a => !string.IsNullOrWhiteSpace(a) && a.Contains("{version}", StringComparison.OrdinalIgnoreCase))
            .Select(HostOf).Any(h => h is not null && !h.Equals(source, StringComparison.OrdinalIgnoreCase));
    }
}