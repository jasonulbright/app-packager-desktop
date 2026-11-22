using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>A parsed site folder name: the site code and the ordinal (1 for PCM, 2 for PCM~2).</summary>
public sealed record SiteFolderName(string SiteCode, int Ordinal)
{
    public string FolderName => Ordinal > 1 ? $"{SiteCode}~{Ordinal}" : SiteCode;
}

/// <summary>The active site and where its value came from.</summary>
public sealed record ActiveSite(string FolderName, string Source);

/// <summary>
/// Paths and names of the per-site data layout (contract schemaVersion 1, Q-112b). The twin of the
/// SuiteCommon functions Get-SuiteSiteFolder and Get-SuiteActiveSite. Both read the shared fixtures.
/// No code builds a path here and creates it. Nothing in the app calls this class yet.
/// </summary>
public static partial class SiteDataPaths
{
    public const int SchemaVersion = 1;
    public const string DataRootFolderName = "AppPackagerData";
    public const string SuiteSiteVariable = "SUITE_CM_SITECODE";
    public const string AppSiteVariable = "APP_PACKAGER_SITE";
    public const string ActiveSiteFileName = "active-site.json";

    /// <summary>Names that ConfigMgr and Windows reserve. They are not valid site codes.</summary>
    public static readonly IReadOnlyList<string> ReservedSiteCodes = new[] { "AUX", "CON", "ENV", "NUL", "PRN", "SMS" };

    [GeneratedRegex("^[A-Za-z0-9]{3}$")]
    private static partial Regex SiteCodePattern();

    [GeneratedRegex("^([A-Za-z0-9]{3})(?:~([2-9]|[1-9][0-9]+))?$")]
    private static partial Regex FolderNamePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SegmentPattern();

    /// <summary>The data root of the site layout: APP_PACKAGER_DATA_ROOT scoped to the user, else %LOCALAPPDATA%\AppPackagerData.</summary>
    public static string DataRoot => ResolveDataRoot(Environment.GetEnvironmentVariable(AppDataPaths.DataRootVariable));

    public static string ResolveDataRoot(string? overrideRoot)
    {
        var path = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataRootFolderName)
            : AppDataPaths.ScopeRootToCurrentUser(Path.GetFullPath(overrideRoot.Trim()));
        return AppDataPaths.EnsureOutsideProgramFiles(path);
    }

    /// <summary>True for three characters A-Z or 0-9 (either case) that are not a reserved name.</summary>
    public static bool IsValidSiteCode(string? value) =>
        value is not null
        && SiteCodePattern().IsMatch(value)
        && !ReservedSiteCodes.Contains(value.ToUpperInvariant(), StringComparer.Ordinal);

    /// <summary>Parses PCM or PCM~2. Returns false for an invalid name.</summary>
    public static bool TryParseFolderName(string? value, out SiteFolderName? parsed)
    {
        parsed = null;
        if (string.IsNullOrEmpty(value)) return false;
        var m = FolderNamePattern().Match(value);
        if (!m.Success || !IsValidSiteCode(m.Groups[1].Value)) return false;
        var ordinal = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        parsed = new SiteFolderName(m.Groups[1].Value.ToUpperInvariant(), ordinal);
        return true;
    }

    public static bool IsValidFolderName(string? value) => TryParseFolderName(value, out _);

    /// <summary>The first free folder name for a site code: PCM, PCM~2, PCM~3. Names compare without regard to case.</summary>
    public static string NextFreeFolderName(string siteCode, IEnumerable<string>? existingNames = null)
    {
        if (!IsValidSiteCode(siteCode)) throw new ArgumentException($"Invalid site code: '{siteCode}'.", nameof(siteCode));
        var code = siteCode.ToUpperInvariant();
        var taken = new HashSet<string>(existingNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(code)) return code;
        var i = 2;
        while (taken.Contains($"{code}~{i}")) i++;
        return $"{code}~{i}";
    }

    /// <summary>A tool or area segment: lower case letters, digits and hyphens, not starting with a hyphen.</summary>
    public static bool IsValidPathSegment(string? value) => !string.IsNullOrEmpty(value) && SegmentPattern().IsMatch(value);

    /// <summary>
    /// The folder of a site, sites\&lt;FOLDER&gt;, with an optional tools\&lt;tool&gt; or area below it. Creates nothing.
    /// Throws ArgumentException for an invalid value, or when both tool and area are given.
    /// </summary>
    public static string GetSiteFolder(string siteCode, string? tool = null, string? area = null, string? dataRoot = null)
    {
        if (!TryParseFolderName(siteCode, out var parsed) || parsed is null)
            throw new ArgumentException($"Invalid site folder name: '{siteCode}'.", nameof(siteCode));
        if (!string.IsNullOrEmpty(tool) && !string.IsNullOrEmpty(area))
            throw new ArgumentException("Use tool or area, not both.");
        var path = Path.Combine(dataRoot ?? DataRoot, "sites", parsed.FolderName);
        if (!string.IsNullOrEmpty(tool))
        {
            if (!IsValidPathSegment(tool)) throw new ArgumentException($"Invalid tool name: '{tool}'.", nameof(tool));
            return Path.Combine(path, "tools", tool);
        }
        if (!string.IsNullOrEmpty(area))
        {
            foreach (var segment in area.Split('\\'))
                if (!IsValidPathSegment(segment)) throw new ArgumentException($"Invalid area: '{area}'.", nameof(area));
            return Path.Combine(path, area);
        }
        return path;
    }

    /// <summary>
    /// The active site. Order: SUITE_CM_SITECODE, APP_PACKAGER_SITE, then active-site.json in the data root.
    /// A value that is not a valid folder name is skipped. Reads only. Returns null when nothing is usable.
    /// </summary>
    public static ActiveSite? GetActiveSite(string? dataRoot = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        foreach (var name in new[] { SuiteSiteVariable, AppSiteVariable })
        {
            if (TryParseFolderName(getEnvironmentVariable(name), out var fromEnv) && fromEnv is not null)
                return new ActiveSite(fromEnv.FolderName, name);
        }
        try
        {
            var file = Path.Combine(dataRoot ?? DataRoot, ActiveSiteFileName);
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var v) || v != SchemaVersion) return null;
            if (!root.TryGetProperty("siteCode", out var code) || code.ValueKind != JsonValueKind.String) return null;
            return TryParseFolderName(code.GetString(), out var fromFile) && fromFile is not null
                ? new ActiveSite(fromFile.FolderName, ActiveSiteFileName)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
