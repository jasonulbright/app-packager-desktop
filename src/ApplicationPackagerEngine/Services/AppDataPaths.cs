namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Per-user data folders. APP_PACKAGER_DATA_ROOT replaces both the AppPackager folder
/// (settings, templates, workbench) and the SRLPackr folder (logs, caches, tools), which
/// then sits beneath it, so a process started with the variable writes nothing under
/// %LOCALAPPDATA%.
/// </summary>
public static class AppDataPaths
{
    public const string DataRootVariable = "APP_PACKAGER_DATA_ROOT";

    private static string? Override
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(DataRootVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>The folder that holds settings.json.</summary>
    public static string Root => Override ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackager");

    /// <summary>The folder that holds session logs, the icon cache, the history database and downloaded tools.</summary>
    public static string LegacyRoot => Override is { } root
        ? Path.Combine(root, "SRLPackr")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRLPackr");
}
