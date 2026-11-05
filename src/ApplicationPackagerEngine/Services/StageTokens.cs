using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Tokens resolved at stage time beyond {version} and {ProductCode}:
/// parts of the staged version, the payload's own ProductVersion, the
/// compiled-script DisplayVersion, and values read from ZIP entry names.
/// Text without one of these tokens is returned unchanged.
/// </summary>
public sealed partial class StageTokens
{
    public const string ProductVersionToken = "{ProductVersion}";
    public const string HeaderDisplayVersionToken = "{HeaderDisplayVersion}";
    public const string BundleIdToken = "{BundleId}";
    public const string FeedBuildToken = "{FeedBuild}";
    public const string NumericVersionToken = "{version_numeric}";

    /// <summary>The staged display version the version-part tokens read.</summary>
    public string Version { get; init; } = "";

    /// <summary>MSI ProductVersion, or the EXE version-resource ProductVersion.</summary>
    public string? ProductVersion { get; init; }

    /// <summary>DisplayVersion the NSIS or Inno Setup script registers.</summary>
    public string? HeaderDisplayVersion { get; init; }

    /// <summary>Bundle id of a WiX Burn installer, braced, as its .wixburn header names it.</summary>
    public string? BundleId { get; init; }

    /// <summary>Build number the version feed publishes for the staged version.</summary>
    public string? FeedBuild { get; init; }

    /// <summary>Values read from ZIP entry names, by token name.</summary>
    public IReadOnlyDictionary<string, string> Zip { get; init; } = new Dictionary<string, string>();

    [GeneratedRegex(@"\{version_parts:(\d)\}", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPartsToken();

    [GeneratedRegex(@"\{version_part:(\d)\}", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPartToken();

    [GeneratedRegex(@"^\d+(\.\d+)*")]
    private static partial Regex NumericPrefix();

    [GeneratedRegex(@"\{zip:([A-Za-z0-9_]+)\}")]
    private static partial Regex ZipToken();

    /// <summary>
    /// Resolves only the version-part tokens. Each one is computed from
    /// <paramref name="version"/>: {version_parts:N} is the first N dotted
    /// parts with missing parts as 0, {version_part:N} the Nth part.
    /// </summary>
    public static string? ApplyVersionParts(string? text, string version)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // {version_numeric} is the leading dotted number: a packaging suffix
        // ("2026.10.1-2") is no part of the version the product reports.
        if (text.Contains(NumericVersionToken, StringComparison.OrdinalIgnoreCase))
        {
            var numeric = NumericPrefix().Match(version);
            if (!numeric.Success)
                throw new InvalidOperationException($"Version '{version}' has no dotted number for {NumericVersionToken}.");
            text = text.Replace(NumericVersionToken, numeric.Value, StringComparison.OrdinalIgnoreCase);
        }

        if (!text.Contains("{version_part", StringComparison.OrdinalIgnoreCase))
            return text;

        var parts = version.Split('.');
        text = VersionPartsToken().Replace(text, m =>
        {
            var count = int.Parse(m.Groups[1].Value);
            if (count < 1) throw new InvalidOperationException($"{m.Value} needs a count of 1 or more.");
            return string.Join(".", Enumerable.Range(0, count).Select(i => i < parts.Length ? parts[i] : "0"));
        });
        return VersionPartToken().Replace(text, m =>
        {
            var index = int.Parse(m.Groups[1].Value);
            if (index < 1 || index > parts.Length || parts[index - 1].Length == 0)
                throw new InvalidOperationException($"Version '{version}' has no part {index} for {m.Value}.");
            return parts[index - 1];
        });
    }

    /// <summary>Resolves every token this class owns. An unavailable value refuses the stage.</summary>
    public string? Apply(string? text)
    {
        text = ApplyVersionParts(text, Version);
        if (string.IsNullOrEmpty(text) || !text.Contains('{')) return text;

        if (text.Contains(ProductVersionToken, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(ProductVersion))
                throw new InvalidOperationException("The definition uses {ProductVersion}, but the payload carries no ProductVersion.");
            text = text.Replace(ProductVersionToken, ProductVersion, StringComparison.OrdinalIgnoreCase);
        }

        // A DisplayVersion the script computes at run time, or one that is not
        // a version, falls back to the staged version.
        if (text.Contains(HeaderDisplayVersionToken, StringComparison.OrdinalIgnoreCase))
        {
            var header = System.Version.TryParse(HeaderDisplayVersion ?? "", out _) ? HeaderDisplayVersion! : Version;
            text = text.Replace(HeaderDisplayVersionToken, header, StringComparison.OrdinalIgnoreCase);
        }

        if (text.Contains(BundleIdToken, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(BundleId))
                throw new InvalidOperationException("The definition uses {BundleId}, but the installer carries no WiX Burn bundle id.");
            text = text.Replace(BundleIdToken, BundleId, StringComparison.OrdinalIgnoreCase);
        }

        if (text.Contains(FeedBuildToken, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(FeedBuild))
                throw new InvalidOperationException("The definition uses {FeedBuild}, but the version feed gave no build number for the staged version.");
            text = text.Replace(FeedBuildToken, FeedBuild, StringComparison.OrdinalIgnoreCase);
        }

        return ZipToken().Replace(text, m => Zip.TryGetValue(m.Groups[1].Value, out var value)
            ? value
            : throw new InvalidOperationException($"No ZIP entry token named '{m.Groups[1].Value}' was read for {m.Value}."));
    }

    /// <summary>A copy of a detection config with every owned token resolved in its text fields.</summary>
    public DetectionConfig Apply(DetectionConfig det) => Rewrite(det, Apply);

    /// <summary>A copy of a detection config with only the version-part tokens resolved.</summary>
    public static DetectionConfig ApplyVersionParts(DetectionConfig det, string version) =>
        Rewrite(det, text => ApplyVersionParts(text, version));

    private static DetectionConfig Rewrite(DetectionConfig det, Func<string?, string?> apply) => new()
    {
        Type = det.Type,
        RegistryHive = det.RegistryHive,
        RegistryKeyTemplate = apply(det.RegistryKeyTemplate),
        ValueName = det.ValueName,
        Operator = det.Operator,
        FilePath = apply(det.FilePath),
        FileName = apply(det.FileName),
        PropertyType = det.PropertyType,
        Is64Bit = det.Is64Bit,
        ExpectedValue = apply(det.ExpectedValue),
        ScriptLanguage = det.ScriptLanguage,
        ScriptText = apply(det.ScriptText),
        Connector = det.Connector,
        Clauses = det.Clauses?.Select(c => Rewrite(c, apply)).ToArray(),
        GroupSizes = det.GroupSizes,
        ArpEntry = det.ArpEntry?.With(apply)
    };

    /// <summary>True when any detection text field carries a token this class owns.</summary>
    public static bool HasTokens(DetectionConfig det)
    {
        static bool Has(string? s) => s is not null
            && (s.Contains("{version_part", StringComparison.OrdinalIgnoreCase)
                || s.Contains(NumericVersionToken, StringComparison.OrdinalIgnoreCase)
                || s.Contains(BundleIdToken, StringComparison.OrdinalIgnoreCase)
                || s.Contains(FeedBuildToken, StringComparison.OrdinalIgnoreCase)
                || s.Contains(ProductVersionToken, StringComparison.OrdinalIgnoreCase)
                || s.Contains(HeaderDisplayVersionToken, StringComparison.OrdinalIgnoreCase)
                || s.Contains("{zip:", StringComparison.Ordinal));
        return Has(det.RegistryKeyTemplate) || Has(det.FilePath) || Has(det.FileName)
            || Has(det.ExpectedValue) || Has(det.ScriptText)
            || Has(det.ArpEntry?.Version) || Has(det.ArpEntry?.DisplayName)
            || Has(det.ArpEntry?.DisplayNamePrefix) || Has(det.ArpEntry?.DisplayNameSuffix)
            || det.Clauses?.Any(HasTokens) == true;
    }
}
