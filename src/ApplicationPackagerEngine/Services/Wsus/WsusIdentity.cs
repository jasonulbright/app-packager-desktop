using System.Text;
using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>An identity line read back out of an update description; Version is empty when the line carries none.</summary>
public sealed record WsusUpdateIdentity(string IdentityTag, string PackageType, string Version);

/// <summary>
/// The identity an update carries on the WSUS server, the title and description
/// it is published with, and the version order supersedence follows.
/// </summary>
public static partial class WsusIdentity
{
    public const string IdentityMarker = "AppPackager identity:";

    /// <summary>
    /// One vendor and one product category for every application: a server
    /// refuses local publishing past a small number of distinct categories, and
    /// vendor names such as Microsoft Corporation are reserved for Microsoft.
    /// </summary>
    public const string VendorName = "AppPackager";
    public const string ProductName = "AppPackager Applications";

    /// <summary>The longest title the package schema takes.</summary>
    public const int MaxTitleLength = 79;

    [GeneratedRegex(@"[^\w.]+")]
    private static partial Regex NonKeyCharacters();

    [GeneratedRegex(@"^\d+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumericRun();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex RepeatedWhitespace();

    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f])")]
    private static partial Regex GuidText();

    [GeneratedRegex(@"(?m)^AppPackager identity: (?<tag>AppPackager:\S+); type (?<type>Update|Application)(; version (?<version>[^\r\n]+?))?[ \t]*\r?$")]
    private static partial Regex IdentityLinePattern();

    /// <summary>
    /// 'AppPackager:&lt;ApplicationId&gt;/&lt;ProfileId&gt;', the form the Intune
    /// publisher writes. A manifest staged without an ApplicationId falls back to
    /// 'legacy:&lt;Publisher&gt;:&lt;title&gt;' with the version in the title reduced to its
    /// major: every release of one major shares the identity, and side-by-side lines
    /// such as .NET 6 and .NET 8 keep their own, so one line never supersedes the other.
    /// </summary>
    public static string IdentityTag(StageManifest manifest)
    {
        var applicationId = manifest.ApplicationId ?? "";
        if (string.IsNullOrWhiteSpace(applicationId)) applicationId = LegacyKey(manifest);
        return $"AppPackager:{applicationId}/{ProfileOf(manifest)}";
    }

    /// <summary>
    /// For a manifest that carries an ApplicationId, the legacy tag the same manifest
    /// carried before it had one; an update published under it is this application's,
    /// so supersedence and a repeat publish take it into account. Empty for a manifest
    /// without an ApplicationId, whose identity already is that legacy tag.
    /// </summary>
    public static IReadOnlyList<string> FormerIdentityTags(StageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ApplicationId)) return [];
        var former = $"AppPackager:{LegacyKey(manifest)}/{ProfileOf(manifest)}";
        return former == IdentityTag(manifest) ? [] : [former];
    }

    private static string ProfileOf(StageManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.ProfileId) ? "default" : manifest.ProfileId;

    private static string LegacyKey(StageManifest manifest)
    {
        var title = (manifest.AppName ?? "").Trim();
        var version = (manifest.SoftwareVersion ?? "").Trim();
        if (version.Length > 0)
        {
            var major = LeadingNumber().Match(version).Value;
            title = Regex.Replace(title, @"(?<![\w.])v?" + Regex.Escape(version) + @"(?![\w.])",
                major.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
        var key = NonKeyCharacters().Replace(title, "-").Trim('-');
        var publisher = NonKeyCharacters().Replace(manifest.Publisher ?? "", "-").Trim('-');
        if (publisher.Length > 0) key = publisher + ":" + key;
        return "legacy:" + key;
    }

    /// <summary>The description line that marks an update as this identity's, with the version supersedence compares.</summary>
    public static string IdentityLine(string identityTag, string packageType, string? version = "")
    {
        var line = $"{IdentityMarker} {identityTag}; type {packageType}";
        if (!string.IsNullOrWhiteSpace(version)) line += $"; version {version.Trim()}";
        return line;
    }

    /// <summary>Null when the description carries no AppPackager identity line.</summary>
    public static WsusUpdateIdentity? ParseIdentity(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var match = IdentityLinePattern().Match(description);
        if (!match.Success) return null;
        return new WsusUpdateIdentity(match.Groups["tag"].Value, match.Groups["type"].Value, match.Groups["version"].Value);
    }

    /// <summary>
    /// Orders two vendor versions by their numeric runs, so 5.10 follows 5.9 and
    /// 2026.08.2-200 follows 2026.08.2-199; a missing run counts as zero. Null
    /// when either version carries no digits.
    /// </summary>
    public static int? CompareVersion(string? left, string? right)
    {
        var a = NumericRun().Matches(left ?? "").Select(m => m.Value.TrimStart('0')).ToList();
        var b = NumericRun().Matches(right ?? "").Select(m => m.Value.TrimStart('0')).ToList();
        if (a.Count == 0 || b.Count == 0) return null;
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : "";
            var y = i < b.Count ? b[i] : "";
            if (x.Length != y.Length) return x.Length < y.Length ? -1 : 1;
            var order = string.CompareOrdinal(x, y);
            if (order != 0) return Math.Sign(order);
        }
        return 0;
    }

    /// <summary>
    /// The update title: the version is always part of it, and a long title is
    /// cut with '...' before the version so it stays within 79 characters.
    /// </summary>
    public static string Title(StageManifest manifest)
    {
        var title = (manifest.AppName ?? "").Trim();
        var version = (manifest.SoftwareVersion ?? "").Trim();
        if (version.Length > 0 && title.IndexOf(version, StringComparison.OrdinalIgnoreCase) < 0) title = $"{title} {version}";
        if (title.Length > MaxTitleLength)
        {
            var suffix = version.Length > 0 ? " " + version : "";
            var baseText = title;
            if (version.Length > 0)
                baseText = RepeatedWhitespace().Replace(
                    Regex.Replace(baseText, Regex.Escape(version), "", RegexOptions.IgnoreCase), " ").Trim();
            var length = Math.Max(0, Math.Min(baseText.Length, MaxTitleLength - suffix.Length - 3));
            title = baseText[..length].TrimEnd() + "..." + suffix;
        }
        return title;
    }

    /// <summary>
    /// '&lt;AppName&gt; &lt;version&gt; from &lt;Publisher&gt;, published by AppPackager from
    /// build &lt;BuildId&gt;.', a blank line, then the identity line of type Update.
    /// </summary>
    public static string Description(StageManifest manifest)
    {
        var version = manifest.SoftwareVersion ?? "";
        var appName = manifest.AppName ?? "";
        var first = new StringBuilder(appName);
        if (version.Length > 0 && appName.IndexOf(version, StringComparison.OrdinalIgnoreCase) < 0) first.Append(' ').Append(version);
        if (!string.IsNullOrEmpty(manifest.Publisher)) first.Append(" from ").Append(manifest.Publisher);
        first.Append(", published by AppPackager");
        if (!string.IsNullOrEmpty(manifest.BuildId)) first.Append(" from build ").Append(manifest.BuildId);
        return string.Join("\r\n", first + ".", "", IdentityLine(IdentityTag(manifest), WsusSettings.UpdatePackageType, version));
    }

    /// <summary>
    /// Microsoft Update Catalog update ids in pasted text: bare ids and catalog
    /// links that carry one, any number per line, each distinct id once, in
    /// input order.
    /// </summary>
    public static IReadOnlyList<Guid> CatalogIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var seen = new HashSet<Guid>();
        var ids = new List<Guid>();
        foreach (Match match in GuidText().Matches(text))
        {
            var id = Guid.Parse(match.Value);
            if (seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// A vendor version as the four numeric parts WSUS comparisons take, a
    /// missing part padded with zero. Null for a version that is not up to four
    /// numbers or has a part above 65535.
    /// </summary>
    public static string? VersionString(string? version)
    {
        var trimmed = (version ?? "").Trim();
        if (!Regex.IsMatch(trimmed, @"^\d+(\.\d+){0,3}$")) return null;
        var parts = new List<string>();
        foreach (var part in trimmed.Split('.'))
        {
            if (!long.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                || number > 65535) return null;
            parts.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        while (parts.Count < 4) parts.Add("0");
        return string.Join(".", parts);
    }
}
