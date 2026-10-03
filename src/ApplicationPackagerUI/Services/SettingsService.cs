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
            catch (Exception ex)
            {
                MarkUnreadable(ex.Message);
                settings = new AppSettings { FirstRunCompleted = false, SiteCodeSaved = false, Systems = new SystemsInUse() };
                ApplyLauncherDefaults(settings);
                return settings;
            }
        }

        lock (_gate) _unreadableReason = null;
        ApplyLauncherDefaults(settings);
        return settings;
    }

    // Set while the file on disk failed to load: an automatic save would replace the
    // operator's One Click history and refusal memory with defaults.
    private string? _unreadableReason;
    private string? _pendingWarning;
    private bool _warned;

    /// <summary>True while the settings file on disk failed to load; no automatic save writes it.</summary>
    public bool IsFileUnreadable
    {
        get { lock (_gate) return _unreadableReason is not null; }
    }

    private void MarkUnreadable(string reason)
    {
        lock (_gate)
        {
            _unreadableReason = reason;
            if (_warned) return;
            _warned = true;
            _pendingWarning = $"Settings file could not be read: {SettingsPath} ({reason}). Defaults are in use and nothing is written to that file " +
                              "until Settings is saved; that save first keeps the unreadable file as " + Path.GetFileName(SettingsPath) + ".unreadable-<timestamp>.";
        }
    }

    /// <summary>The unreadable-file notice, once per process; null when there is none or it was taken.</summary>
    public string? TakeLoadWarning()
    {
        lock (_gate)
        {
            var warning = _pendingWarning;
            _pendingWarning = null;
            return warning;
        }
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

    /// <summary>
    /// Replaces the file atomically. While the file on disk failed to load, an automatic
    /// save writes nothing; an operator save first copies the unreadable file to
    /// settings.json.unreadable-&lt;timestamp&gt;, then replaces it.
    /// </summary>
    private void Write(AppSettings settings, bool operatorEdit)
    {
        Directory.CreateDirectory(SettingsDir);
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        lock (_gate)
        {
            if (_unreadableReason is not null)
            {
                if (!operatorEdit) return;
                if (File.Exists(SettingsPath)) PreserveUnreadable();
                _unreadableReason = null;
            }
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
            ApplicationPackagerEngine.Services.Workbench.WorkbenchJson.WriteText(SettingsPath, node.ToJsonString(JsonOptions));
        }
    }

    private static void PreserveUnreadable()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var copy = $"{SettingsPath}.unreadable-{stamp}";
        for (var n = 2; File.Exists(copy); n++) copy = $"{SettingsPath}.unreadable-{stamp}-{n}";
        File.Copy(SettingsPath, copy);
    }
}
