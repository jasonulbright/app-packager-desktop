using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// What a package run does when the site already holds an application of the
/// same name at the same version.
/// </summary>
public enum OnExistingBehavior
{
    /// <summary>Leave the application as it stands and emit the conflict marker.</summary>
    Skip,

    /// <summary>Replace the deployment types in place, keeping the application object.</summary>
    Overwrite,

    /// <summary>Throw naming the application and write nothing.</summary>
    Fail
}

/// <summary>
/// The machine-readable line a Skip run emits, and its parser. The format is
/// shared with the PowerShell engine so one parser serves both.
/// </summary>
public static partial class OnExistingConflict
{
    public const string Marker = "[APP_PACKAGER_CONFLICT] existing-same-version";

    [GeneratedRegex(@"\[APP_PACKAGER_CONFLICT\] existing-same-version app='(?<app>.*)' version='(?<version>.*)'$")]
    private static partial Regex MarkerPattern();

    /// <summary>Formats the marker line for an application and version.</summary>
    public static string Format(string appName, string version) =>
        $"{Marker} app='{appName}' version='{version}'";

    /// <summary>
    /// Parses one output line. Returns false for every line that is not a
    /// conflict marker, so a caller can feed it the whole stream.
    /// </summary>
    public static bool TryParse(string? line, out string appName, out string version)
    {
        appName = "";
        version = "";
        if (string.IsNullOrEmpty(line)) return false;

        var match = MarkerPattern().Match(line);
        if (!match.Success) return false;

        appName = match.Groups["app"].Value;
        version = match.Groups["version"].Value;
        return true;
    }
}
