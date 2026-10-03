using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

public class SettingsService
{
    private static readonly string SettingsDir = AppDataPaths.Root;

    public static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings { FirstRunCompleted = false };

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            // Settings written before the Intune target existed carry no section, or a section
            // with no target; both resolve to MECM only.
            settings.Intune ??= new IntuneSettings();
            settings.Intune.DeploymentTarget = DeploymentTargets.Normalize(settings.Intune.DeploymentTarget);
            settings.FirstRunCompleted = FirstRunDecision.ResolveCompleted(settings.FirstRunCompleted, settingsFileExisted: true);
            if (settings.CitrixSwitches is null && settings.Cwa is not null)
                settings.CitrixSwitches = settings.Cwa.ToSwitches();
            settings.Cwa = null;
            settings.LocalSourceFolders ??= [];
            settings.SideFiles ??= [];
            settings.ScriptSigning = ApplicationPackagerEngine.Models.Signing.SigningPolicy.FromSettings(settings.ScriptSigning);
            return settings;
        }
        catch
        {
            return new AppSettings { FirstRunCompleted = false };
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }
}
