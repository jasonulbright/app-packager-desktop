using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

public class SettingsService : IRunSettingsReader<AppSettings>
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

    private Action<AppSettings>? _runOverrides;

    /// <summary>
    /// Values every later Load applies over the stored settings, for this instance only.
    /// An instance with run overrides refuses to write: a save would persist values the
    /// operator never saved.
    /// </summary>
    public void ApplyRunOverrides(Action<AppSettings> overrides) => _runOverrides = overrides;

    /// <summary>
    /// The longest a load waits for settings.json while another writer's replace side file
    /// shows the name is only briefly absent, or while the file is held without read sharing.
    /// </summary>
    public static readonly TimeSpan MissingFileWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MissingFileQuietPeriod = TimeSpan.FromMilliseconds(100);

    private bool _readOnce;
    private (bool Read, string? Text, string? Error) _onceRead;

    /// <summary>
    /// Reads settings.json at the first Load and serves that read to every later Load of
    /// this instance, so one command-line run uses one set of values even when another
    /// process saves the file during the run. Each Load still returns a new object.
    /// </summary>
    public void ReadFileOncePerRun()
    {
        lock (_gate) _readOnce = true;
    }

    public AppSettings Load()
    {
        bool readOnce;
        (bool Read, string? Text, string? Error) cached;
        lock (_gate) (readOnce, cached) = (_readOnce, _onceRead);
        var (json, readError) = cached.Read ? (cached.Text, cached.Error) : ReadSettingsText();
        if (readOnce && !cached.Read)
        {
            lock (_gate)
            {
                if (_onceRead.Read) (json, readError) = (_onceRead.Text, _onceRead.Error);
                else _onceRead = (true, json, readError);
            }
        }

        AppSettings settings;
        var unreadable = false;
        if (readError is null && json is null)
        {
            settings = new AppSettings { FirstRunCompleted = false, SiteCodeSaved = false, Systems = new SystemsInUse() };
        }
        else
        {
            try
            {
                if (readError is not null) throw new IOException(readError);
                settings = JsonSerializer.Deserialize<AppSettings>(json!, JsonOptions) ?? new AppSettings();
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
                settings.SiteCodeSaved ??= JsonNode.Parse(json!) is JsonObject root && root.ContainsKey(nameof(AppSettings.SiteCode));
            }
            catch (Exception ex)
            {
                MarkUnreadable(ex.Message);
                unreadable = true;
                settings = new AppSettings { FirstRunCompleted = false, SiteCodeSaved = false, Systems = new SystemsInUse() };
            }
        }

        if (!unreadable)
            lock (_gate) _unreadableReason = null;
        ApplyLauncherDefaults(settings);
        _runOverrides?.Invoke(settings);
        return settings;
    }

    /// <summary>
    /// The text of settings.json; null text and null error when there is no file. A writer
    /// that replaces the file through a side file can leave the name absent for a moment,
    /// and a writer or reader without read sharing holds it for a moment: both are retried
    /// up to <see cref="MissingFileWait"/>. A missing file with no visible side file must
    /// stay absent for a short quiet period before it is treated as a first run; a replace
    /// can briefly hide both names while the filesystem completes the rename.
    /// </summary>
    private static (string? Text, string? Error) ReadSettingsText()
    {
        var deadline = DateTime.UtcNow + MissingFileWait;
        var missingQuietDeadline = DateTime.UtcNow + MissingFileQuietPeriod;
        while (true)
        {
            try
            {
                return (File.ReadAllText(SettingsPath), null);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                var now = DateTime.UtcNow;
                if (now >= deadline) return (null, null);
                if (ReplaceSideFilePresent()) missingQuietDeadline = now + MissingFileQuietPeriod;
                else if (now >= missingQuietDeadline) return (null, null);
            }
            catch (IOException ex) when (FileRetry.IsSharingViolation(ex))
            {
                if (DateTime.UtcNow >= deadline) return (null, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, ex.Message);
            }
            Thread.Sleep(10);
        }
    }

    private static bool ReplaceSideFilePresent()
    {
        try
        {
            var name = Path.GetFileName(SettingsPath);
            return Directory.Exists(SettingsDir)
                   && Directory.EnumerateFiles(SettingsDir, name + ".*")
                       .Any(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
        if (_runOverrides is not null)
            throw new InvalidOperationException("These settings carry run overrides for this process and are not written.");
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
