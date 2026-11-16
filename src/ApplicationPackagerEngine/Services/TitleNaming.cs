using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The architecture tag of an application title, for example "(x64)". A name that already
/// carries the tag never gets a second one, and a variant definition (one application with
/// a deployment type for each architecture) is named without any architecture, as the
/// PowerShell app names it.
/// </summary>
public static partial class TitleNaming
{
    [GeneratedRegex(@"\(\s*(x64|x86|arm64|arm|32-bit|64-bit)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureTag();

    [GeneratedRegex(@"(\(\s*(?:x64|x86|arm64|arm|32-bit|64-bit)\s*\))(\s+\1)+", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedArchitectureTag();

    [GeneratedRegex(@"\s*\(\s*(x64|x86|arm64|arm|32-bit|64-bit)\s*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingArchitectureTag();

    /// <summary>True when the definition is one application with a deployment type for each architecture.</summary>
    public static bool IsVariantDefinition(AppDefinition def) =>
        def.PayloadVariants is { Length: > 0 }
        && def.SupportsVariants?.Contains("Architecture", StringComparer.OrdinalIgnoreCase) == true
        && !string.IsNullOrWhiteSpace(def.CMName);

    /// <summary>The name that a stage uses when the installer gives none: the definition name, tagged with the architecture only when it has no tag yet.</summary>
    public static string BaseName(AppDefinition def) => IsVariantDefinition(def) ? def.CMName.Trim() : def.Application;

    public static string FallbackAppName(AppDefinition def, string version) =>
        $"{WithArchitecture(BaseName(def), def)} - {version}";

    public static string FallbackDisplayName(AppDefinition def) => WithArchitecture(BaseName(def), def);

    private static string WithArchitecture(string name, AppDefinition def)
    {
        if (IsVariantDefinition(def) || string.IsNullOrWhiteSpace(def.Architecture)) return name;
        return ArchitectureTag().IsMatch(name) ? name : $"{name} ({def.Architecture})";
    }

    /// <summary>Applies the architecture rule to a final title: a repeated tag becomes one, and a trailing tag goes when the option is off.</summary>
    public static string Finish(string title, bool appendArchitecture)
    {
        var result = RepeatedArchitectureTag().Replace(title, "$1");
        if (!appendArchitecture) result = TrailingArchitectureTag().Replace(result, "");
        return result.Trim();
    }
}
