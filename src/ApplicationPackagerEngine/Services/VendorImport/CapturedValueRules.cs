using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.VendorImport;

/// <summary>
/// The rules for a value that a scraper captured from one host and that goes into the address of another ("Validation" rules 7 and 8, and "Imported
/// definitions run with no ambient credentials", rule 2): such a value could send what a first request read to the vendor, with the token of the
/// administrator or from an intranet page that needs no credential. A captured value goes to another host only when it is a short version.
/// A placeholder of the version family ({version}, {display_version}, {display_version_dashed}, {version_base}, {revision_dash} and the like) is
/// checked when the definition runs; every other placeholder (a capture) is blocked at import when it crosses to another host.
/// </summary>
public static partial class CapturedValueRules
{
    [GeneratedRegex(@"^\d{1,10}(\.\d{1,10}){0,3}([.+_-][A-Za-z0-9]{1,16})?\z")]
    private static partial Regex ShortVersion();

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^\{(?:(?:display_)?version|revision)[a-z0-9_:]*\}\z", RegexOptions.IgnoreCase)]
    private static partial Regex VersionFamily();

    /// <summary>The short version grammar of the specification, rule 8.</summary>
    public static bool IsShortVersion(string? value) => value is not null && ShortVersion().IsMatch(value);

    public static bool IsVersionPlaceholder(string placeholder) => VersionFamily().IsMatch(placeholder);

    /// <summary>The placeholders of an address template that are not of the version family: a captured value that no grammar covers statically.</summary>
    public static IReadOnlyList<string> OtherPlaceholders(string template) =>
        Placeholder().Matches(template).Select(m => m.Value).Where(p => !IsVersionPlaceholder(p)).ToList();

    private static string? HostOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var concrete = Placeholder().Replace(address.Trim(), "x");
        return Uri.TryCreate(concrete, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    /// <summary>The hosts that a value can be read from: the version source and every hop.</summary>
    private static HashSet<string> SourceHosts(AppDefinition d)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (HostOf(d.VersionScraper?.Url) is { } first) hosts.Add(first);
        foreach (var hop in d.VersionScraper?.Hops ?? [])
            if (HostOf(hop.Url) is { } host) hosts.Add(host);
        return hosts;
    }

    /// <summary>
    /// The addresses of the definition that take a captured value and sit on a host other than the one that the value was read from, with the
    /// placeholder that no grammar covers. For a hop the origin of {captureN} is the host of hop N, and of {capture} the host of the hop before;
    /// for any other address the origin is any host of the version source and the hops, and an unknown source counts as another host.
    /// </summary>
    public static IReadOnlyList<(string Field, string Placeholder)> CrossHostCaptures(AppDefinition d)
    {
        var found = new List<(string, string)>();
        var hopHosts = new List<string?>();
        foreach (var (hop, index) in (d.VersionScraper?.Hops ?? []).Select((h, i) => (h, i)))
        {
            var template = string.IsNullOrWhiteSpace(hop.Url) ? (index == 0 ? d.VersionScraper?.Url : null) : hop.Url;
            var host = HostOf(template) ?? (index > 0 ? hopHosts[index - 1] : null);
            hopHosts.Add(host);
            if (template is null || HostOf(template) is not { } own) continue;
            foreach (var p in OtherPlaceholders(template))
            {
                string? origin = null;
                var m = Regex.Match(p, @"^\{capture(\d+)\}\z", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out var k) && k < hopHosts.Count) origin = hopHosts[k];
                else if (p.Equals("{capture}", StringComparison.OrdinalIgnoreCase) && index > 0) origin = hopHosts[index - 1];
                if (origin is null || !origin.Equals(own, StringComparison.OrdinalIgnoreCase)) found.Add(($"VersionScraper.Hops[{index}].Url", p));
            }
        }
        var sources = SourceHosts(d);
        void Check(string field, string? address)
        {
            var host = HostOf(address);
            if (host is null || address is null || (sources.Count > 0 && sources.Contains(host))) return;
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

    /// <summary>The placeholders of the version family that go into an address on a host other than the version source.</summary>
    public static IReadOnlyList<string> CrossHostVersionTokens(AppDefinition d)
    {
        var sources = SourceHosts(d);
        var tokens = new List<string>();
        foreach (var address in new[] { d.DownloadUrl, d.PayloadSha256Url, d.VersionScraper?.ServedUrlTemplate, d.BootstrapperLayout?.BootstrapperUrl }
                     .Concat((d.PayloadVariants ?? []).Select(v => v.DownloadUrl)))
        {
            var host = HostOf(address);
            if (host is null || address is null || (sources.Count > 0 && sources.Contains(host))) continue;
            tokens.AddRange(Placeholder().Matches(address).Select(m => m.Value).Where(IsVersionPlaceholder));
        }
        return tokens;
    }

    /// <summary>True when a version placeholder goes into an address on another host than the version source.</summary>
    public static bool VersionCrossesHosts(AppDefinition d) => CrossHostVersionTokens(d).Count > 0;

    /// <summary>
    /// The run-time check: the raw version for {version}, the transformed version for the other placeholders of the family, must be a short version
    /// when the definition sends them to another host.
    /// </summary>
    public static string? RuntimeRefusal(AppDefinition d, string rawVersion, string transformedVersion)
    {
        var tokens = CrossHostVersionTokens(d);
        if (tokens.Any(t => t.Equals("{version}", StringComparison.OrdinalIgnoreCase)) && !IsShortVersion(rawVersion))
            return "{version}";
        if (tokens.Any(t => !t.Equals("{version}", StringComparison.OrdinalIgnoreCase)) && !IsShortVersion(transformedVersion))
            return "{display_version}";
        return null;
    }
}