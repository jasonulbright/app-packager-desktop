namespace ApplicationPackagerEngine.Services;

/// <summary>The Configuration Manager console's PowerShell module, which every ConfigMgr step runs.</summary>
public static class ConfigMgrConsole
{
    /// <summary>The ConfigurationManager.psd1 the console installed, or null when the console is absent.</summary>
    public static string? ModulePath(string? adminUiPath = null)
    {
        adminUiPath ??= Environment.GetEnvironmentVariable("SMS_ADMIN_UI_PATH");
        if (string.IsNullOrWhiteSpace(adminUiPath)) return null;
        try
        {
            var module = Path.GetFullPath(Path.Combine(adminUiPath, "..", "ConfigurationManager.psd1"));
            return File.Exists(module) ? module : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsInstalled() => ModulePath() is not null;
}
