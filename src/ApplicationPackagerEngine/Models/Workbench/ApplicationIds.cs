using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Models.Workbench;

/// <summary>Where an application comes from.</summary>
public enum ApplicationOrigin
{
    /// <summary>A shipped JSON definition or a packager script under the packagers root.</summary>
    Catalog,

    /// <summary>A user-authored JSON definition in the user-writable definitions folder.</summary>
    Custom,

    /// <summary>A persisted dropped installer with no definition behind it.</summary>
    Byo
}

/// <summary>
/// ApplicationId taxonomy: <c>catalog:&lt;name&gt;</c>, <c>custom:&lt;name&gt;</c> and
/// <c>byo:&lt;32 hex&gt;</c>. The storage key replaces the colon with an underscore.
/// </summary>
public static partial class ApplicationIds
{
    public const string CatalogPrefix = "catalog";
    public const string CustomPrefix = "custom";
    public const string ByoPrefix = "byo";

    /// <summary>The virtual profile that applies the packager or definition as shipped.</summary>
    public const string DefaultProfileId = "default";

    [GeneratedRegex(@"^(catalog|custom|byo):[A-Za-z0-9._-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ApplicationIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex ProfileIdPattern();

    /// <summary>
    /// Builds an ApplicationId. A catalog or custom id takes the name, or the file
    /// name without extension of the path; a BYO id is a new random id.
    /// </summary>
    public static string New(ApplicationOrigin origin, string? path = null, string? name = null)
    {
        if (origin == ApplicationOrigin.Byo) return $"{ByoPrefix}:{Guid.NewGuid():N}";

        var baseName = name;
        if (string.IsNullOrWhiteSpace(baseName) && !string.IsNullOrWhiteSpace(path))
            baseName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(baseName))
            throw new ArgumentException("An application id needs a script path or a name.");
        baseName = Path.GetFileNameWithoutExtension(baseName);
        return $"{PrefixOf(origin)}:{baseName}";
    }

    public static string PrefixOf(ApplicationOrigin origin) => origin switch
    {
        ApplicationOrigin.Catalog => CatalogPrefix,
        ApplicationOrigin.Custom => CustomPrefix,
        _ => ByoPrefix
    };

    public static bool IsValid(string? applicationId) =>
        applicationId is not null && ApplicationIdPattern().IsMatch(applicationId)
        && IsPlainFolderName(applicationId[(applicationId.IndexOf(':') + 1)..]);

    /// <summary>
    /// True for a name Windows stores under exactly that name: a name of dots
    /// alone names the folder itself or its parent, and Windows strips a
    /// trailing dot or space, so two ids would share one folder.
    /// </summary>
    public static bool IsPlainFolderName(string name) =>
        name.Length > 0 && name.Trim('.').Length > 0 && !name.EndsWith('.') && !name.EndsWith(' ');

    /// <summary>Converts an ApplicationId into its folder-safe storage key.</summary>
    public static string ToKey(string applicationId)
    {
        if (!IsValid(applicationId))
            throw new ArgumentException(
                $"Invalid ApplicationId '{applicationId}'; expected catalog:<name>, custom:<name> or byo:<id>.");
        return applicationId.Replace(':', '_');
    }

    public static string FromKey(string applicationKey)
    {
        var index = applicationKey.IndexOf('_');
        if (index < 1) throw new ArgumentException($"Invalid ApplicationKey '{applicationKey}'.");
        return applicationKey[..index] + ":" + applicationKey[(index + 1)..];
    }

    /// <summary>The prefix before the colon: catalog, custom or byo.</summary>
    public static string OriginOf(string applicationId)
    {
        var index = applicationId.IndexOf(':');
        return index < 0 ? applicationId : applicationId[..index];
    }

    public static bool IsByo(string applicationId) =>
        applicationId.StartsWith(ByoPrefix + ":", StringComparison.OrdinalIgnoreCase);

    public static bool IsDefaultProfile(string? profileId) =>
        string.Equals(profileId, DefaultProfileId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Throws for a profile id that is not a safe folder name.</summary>
    public static void AssertProfileId(string profileId)
    {
        if (!ProfileIdPattern().IsMatch(profileId ?? "") || !IsPlainFolderName(profileId!))
            throw new ArgumentException($"Invalid ProfileId '{profileId}'.");
    }
}
