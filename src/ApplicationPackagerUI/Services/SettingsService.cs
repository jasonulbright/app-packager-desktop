using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

public class SettingsService
{
    public const string LauncherSiteCodeVariable = "SUITE_CM_SITECODE";
    public const string LauncherProviderVariable = "SUITE_CM_PROVIDER";

    private static string SettingsDir => AppDataPaths.Root;

    public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();

    // Launcher values applied in memory by Load. Each holds the value applied and the
    // value it replaced; a save that is not an operator edit writes the replaced value.
    private (string Applied, string Original)? _launcherSite;
    private (string Applied, string Original)? _launcherProvider;

    public AppSettings Load()
    {
        AppSettings settings;
        if (!File.Exists(SettingsPath))
        {
            settings = new AppSettings { FirstRunCompleted = false, SiteCodeSaved = false, Systems = new SystemsInUse() };
        }
        else
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
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
                settings.Systems = SystemsInUse.Resolve(settings.Systems, settings.Intune.TenantId, settings.Wsus?.ServerName);
                settings.Wsus = WsusSettings.FromStored(settings.Wsus);
                settings.OneClick ??= new OneClickSettings();
                settings.OneClick.Destinations = (settings.OneClick.Destinations ?? [])
                    .Where(kv => kv.Value is not null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                settings.OneClick.OnExisting = ApplicationPackagerEngine.Services.OneClick.OneClickOnExisting.Normalize(settings.OneClick.OnExisting);
                settings.AppStates ??= [];
                foreach (var state in settings.AppStates.Values.Where(s => s is not null))
                {
                    state.Published ??= [];
                    state.NotSupported ??= [];
                }
                // A file written before the flag existed saved its site code with every save.
                settings.SiteCodeSaved ??= JsonNode.Parse(json) is JsonObject root && root.ContainsKey(nameof(AppSettings.SiteCode));
            }
            catch
            {
                settings = new AppSettings { FirstRunCompleted = false, SiteCodeSaved = false, Systems = new SystemsInUse() };
            }
        }

        ApplyLauncherDefaults(settings);
        return settings;
    }

    /// <summary>
    /// Fills an empty provider and a site code the operator never saved from the
    /// suite launcher's variables. The values live in memory only until an operator save.
    /// </summary>
    private void ApplyLauncherDefaults(AppSettings settings)
    {
        lock (_gate)
        {
            _launcherSite = null;
            _launcherProvider = null;
            var site = Environment.GetEnvironmentVariable(LauncherSiteCodeVariable)?.Trim();
            var provider = Environment.GetEnvironmentVariable(LauncherProviderVariable)?.Trim();
            if (!string.IsNullOrEmpty(site) && (settings.SiteCodeSaved != true || string.IsNullOrWhiteSpace(settings.SiteCode)))
            {
                _launcherSite = (site, settings.SiteCode);
                settings.SiteCode = site;
            }
            if (!string.IsNullOrEmpty(provider) && string.IsNullOrWhiteSpace(settings.ProviderMachine))
            {
                _launcherProvider = (provider, settings.ProviderMachine);
                settings.ProviderMachine = provider;
            }
        }
    }

    /// <summary>
    /// Writes the settings. A launcher-supplied site code or provider still unchanged is
    /// written as the value it replaced, so only an operator save persists it.
    /// </summary>
    public void Save(AppSettings settings) => Write(settings, operatorEdit: false);

    /// <summary>Writes the settings as the operator edited them, launcher values included.</summary>
    public void SaveOperatorEdit(AppSettings settings)
    {
        settings.SiteCodeSaved = true;
        Write(settings, operatorEdit: true);
    }

    private void Write(AppSettings settings, bool operatorEdit)
    {
        Directory.CreateDirectory(SettingsDir);
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        lock (_gate)
        {
            if (operatorEdit)
            {
                _launcherSite = null;
                _launcherProvider = null;
            }
            else
            {
                if (_launcherSite is { } site && settings.SiteCodeSaved != true && settings.SiteCode == site.Applied)
                    node[nameof(AppSettings.SiteCode)] = site.Original;
                if (_launcherProvider is { } provider && settings.ProviderMachine == provider.Applied)
                    node[nameof(AppSettings.ProviderMachine)] = provider.Original;
            }
        }
        File.WriteAllText(SettingsPath, node.ToJsonString(JsonOptions));
    }
}
