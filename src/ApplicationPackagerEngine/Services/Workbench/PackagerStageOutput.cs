using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Workbench;

/// <summary>
/// The stage manifest a PS1 packager's stage produced, read from the lines it
/// logs: the manifest it wrote, else the content folder of "Stage complete",
/// else the manifest it read.
/// </summary>
public static partial class PackagerStageOutput
{
    public const string ManifestFileName = "stage-manifest.json";

    public const string NoManifestMessage =
        "The packager ended with exit code 0 but no stage manifest exists. The stage is incomplete: the packager wrote no stage-manifest.json, so nothing can be published from it.";

    [GeneratedRegex(@"Stage complete\s*:\s*(?<path>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex StageCompleteLine();

    [GeneratedRegex(@"Read stage manifest\s*:\s*(?<path>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ReadManifestLine();

    /// <summary>The manifest path the output names, or null when it names none.</summary>
    public static string? ManifestPath(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        if (PackagerStageSeal.ManifestPathFromOutput(output) is { } written) return written;
        var complete = StageCompleteLine().Matches(output);
        if (complete.Count > 0) return Path.Combine(complete[^1].Groups["path"].Value, ManifestFileName);
        var read = ReadManifestLine().Matches(output);
        return read.Count > 0 ? read[^1].Groups["path"].Value : null;
    }

    /// <summary>The manifest path named by the output when that file exists; null otherwise.</summary>
    public static string? ExistingManifest(string? output)
    {
        var path = ManifestPath(output);
        try { return path is not null && File.Exists(path) ? Path.GetFullPath(path) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
