namespace ApplicationPackagerEngine.Services;

/// <summary>The per-user folder that holds settings.json, condition-templates.json and the default Workbench root.</summary>
public static class AppDataPaths
{
    /// <summary>Moves the whole per-user folder, for isolated runs and tests.</summary>
    public const string RootVariable = "APP_PACKAGER_DATA_ROOT";

    /// <summary>APP_PACKAGER_DATA_ROOT when set, otherwise %LOCALAPPDATA%\AppPackager.</summary>
    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(RootVariable);
            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackager")
                : Path.GetFullPath(overridden);
        }
    }
}
