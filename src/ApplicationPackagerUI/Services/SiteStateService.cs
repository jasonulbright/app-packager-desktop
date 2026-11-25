using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The state of the site and of the systems for the context strip (overhaul V1, phase 2). It reads the saved settings, the
/// ConfigMgr console and the last ConfigMgr connection test, and it raises <see cref="Changed"/> when one of them changes: after the
/// operator saves Settings, after the active site changes, and after the Packages page writes a connection test. The strip on any page
/// reads this service, never the Packages view model, so the state is the same on every page.
/// </summary>
public sealed class SiteStateService
{
    private readonly Func<AppSettings> _load;
    private readonly Func<bool> _consoleFound;
    private ConfigMgrConnectionState _connection = ConfigMgrConnectionState.Unknown;
    private ContextStripState _current;

    /// <param name="load">Reads the saved settings.</param>
    /// <param name="consoleFound">True when the ConfigMgr console is installed on this computer.</param>
    /// <param name="onSettingsSaved">Registers the handler that runs after the operator saved Settings.</param>
    /// <param name="onSiteChanged">Registers the handler that runs after the active site changed.</param>
    public SiteStateService(Func<AppSettings> load, Func<bool> consoleFound,
        Action<Action>? onSettingsSaved = null, Action<Action>? onSiteChanged = null)
    {
        _load = load;
        _consoleFound = consoleFound;
        _current = Read();
        onSettingsSaved?.Invoke(Refresh);
        onSiteChanged?.Invoke(() =>
        {
            // A connection test belongs to one site.
            _connection = ConfigMgrConnectionState.Unknown;
            Refresh();
        });
    }

    /// <summary>The settings, the console and the connection test of the active site as the strip shows them.</summary>
    public ContextStripState Current => _current;

    public ConfigMgrConnectionState ConfigMgr => _connection;

    /// <summary>Raised after <see cref="Current"/> changed. It can run on any thread; a view marshals to its own.</summary>
    public event Action? Changed;

    /// <summary>The Packages page writes the result of its connection test here.</summary>
    public void SetConfigMgrConnection(ConfigMgrConnectionState connection)
    {
        if (_connection == connection) return;
        _connection = connection;
        Refresh();
    }

    /// <summary>Reads the settings again. A change of the state raises <see cref="Changed"/>.</summary>
    public void Refresh()
    {
        var next = Read();
        if (Same(next, _current)) return;
        _current = next;
        Changed?.Invoke();
    }

    private static bool Same(ContextStripState a, ContextStripState b) =>
        a.SiteCode == b.SiteCode && a.Host == b.Host && a.Systems.SequenceEqual(b.Systems);

    private ContextStripState Read()
    {
        var settings = _load();
        var readiness = new PublishReadiness(
            settings.Systems ?? new SystemsInUse(), _consoleFound(),
            settings.SiteCode, settings.FileShareRoot,
            settings.Intune.TenantId, settings.Intune.ClientId, !string.IsNullOrWhiteSpace(settings.Intune.ClientSecretProtected),
            settings.Wsus.ServerName);
        return ContextStripState.From(readiness, _connection, settings.SiteCode, settings.ProviderMachine);
    }
}
