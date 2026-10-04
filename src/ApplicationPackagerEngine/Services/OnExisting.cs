using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// What a package run does when the site already holds an application whose
/// title matches exactly, at any version.
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
/// The machine-readable line a Skip run emits, and its parser. The generated
/// ConfigMgr script and a PS1 packager child both write the line, so one parser
/// reads both outputs.
/// </summary>
public static partial class OnExistingConflict
{
    public const string Marker = "[APP_PACKAGER_CONFLICT] existing";

    [GeneratedRegex(@"\[APP_PACKAGER_CONFLICT\] existing app='(?<app>.*)' version='(?<version>.*)' incoming='(?<incoming>.*)'$")]
    private static partial Regex MarkerPattern();

    /// <summary>Formats the marker line for an application, its site version and the staged version.</summary>
    public static string Format(string appName, string version, string incomingVersion) =>
        $"{Marker} app='{appName}' version='{version}' incoming='{incomingVersion}'";

    /// <summary>
    /// Parses one output line. Returns false for every line that is not a
    /// conflict marker, so a caller can feed it the whole stream.
    /// </summary>
    public static bool TryParse(string? line, out string appName, out string version, out string incomingVersion)
    {
        appName = "";
        version = "";
        incomingVersion = "";
        if (string.IsNullOrEmpty(line)) return false;

        var match = MarkerPattern().Match(line);
        if (!match.Success) return false;

        appName = match.Groups["app"].Value;
        version = match.Groups["version"].Value;
        incomingVersion = match.Groups["incoming"].Value;
        return true;
    }
}
