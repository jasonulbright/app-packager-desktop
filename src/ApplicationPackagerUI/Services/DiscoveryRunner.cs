using System.Diagnostics;
using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.Services;

/// <summary>A CMSite drive the ConfigMgr module creates: its name is the site code, its root the SMS Provider.</summary>
public sealed record CmSiteDrive(string Name, string Root);

/// <summary>The end of one scan: the result, or the plain message that replaces it.</summary>
public sealed record DiscoveryScanOutcome(DiscoveryResult? Result, string Message, bool Cancelled = false)
{
    public bool Ok => Result is not null;
}

/// <summary>What applying a prompt choice changed, and the log and status lines that report it.</summary>
public sealed record DiscoveryApplyOutcome(bool SettingsChanged, bool GridChanged, string LogLine, string StatusLine);

/// <summary>
/// Discover unmanaged software without any window: the availability rule, the provider name,
/// the scan off the UI thread under a time limit, and the settings change a choice makes.
/// </summary>
public sealed class DiscoveryRunner
{
    public const string LogPrefix = "Discover unmanaged software: ";
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromSeconds(600);

    private readonly IDiscoveryService _service;
    private readonly Func<AppSettings> _load;
    private readonly Action<AppSettings> _save;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiscoveryPackager>>> _packagers;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CmSiteDrive>>> _drives;
    private readonly Func<string?> _launcherProvider;

    public TimeSpan TimeLimit { get; }

    public DiscoveryRunner(
        IDiscoveryService service,
        Func<AppSettings> load,
        Action<AppSettings> save,
        Func<CancellationToken, Task<IReadOnlyList<DiscoveryPackager>>> packagers,
        Func<CancellationToken, Task<IReadOnlyList<CmSiteDrive>>> drives,
        Func<string?>? launcherProvider = null,
        TimeSpan? timeLimit = null,
        Func<bool>? settingsUnreadable = null)
    {
        _service = service;
        _load = load;
        _save = save;
        _packagers = packagers;
        _drives = drives;
        _launcherProvider = launcherProvider ?? (() => Environment.GetEnvironmentVariable(SettingsService.LauncherProviderVariable));
        TimeLimit = timeLimit ?? DefaultTimeLimit;
        _settingsUnreadable = settingsUnreadable ?? (() => false);
    }

    private readonly Func<bool> _settingsUnreadable;

    public const string NotSavedMessage = "the settings could not be saved. Nothing changed.";
    public const string NoPromptMessage = "the prompt could not be shown. Nothing changed.";
    public const string NotRefreshedMessage = "the choice was saved, but the lists could not be refreshed. Select Catalog on the App Packager page to see them.";

    /// <summary>
    /// The site database is read over SQL, so the console is not needed: ConfigMgr in use and
    /// either a site code or both SQL overrides.
    /// </summary>
    public static (bool Enabled, string ToolTip) Availability(AppSettings settings)
    {
        var configMgr = settings.Systems?.ConfigMgr ?? true;
        var overrides = !string.IsNullOrWhiteSpace(settings.DiscoverySqlServer) && !string.IsNullOrWhiteSpace(settings.DiscoverySqlDatabase);
        var reason = !configMgr ? "ConfigMgr is " + PublishActionState.NotInUse
            : string.IsNullOrWhiteSpace(settings.SiteCode) && !overrides
                ? "Set the site code, or the SQL server and database, in Settings, ConfigMgr Site Settings."
                : "";
        return reason.Length == 0
            ? (true, "Find hidden apps that hardware inventory shows installed on clients and that have no ConfigMgr application")
            : (false, LogPrefix + reason);
    }

    /// <summary>
    /// The SMS Provider for the site lookup, first match wins: the stored preference, the
    /// suite launcher's provider that Check ConfigMgr also takes, the root of the CMSite drive
    /// named for the site code, the root of the first CMSite drive. Empty when none applies.
    /// </summary>
    public static string ResolveProviderName(string? preference, string? launcherProvider, string? siteCode, IReadOnlyList<CmSiteDrive> drives)
    {
        if (!string.IsNullOrWhiteSpace(preference)) return preference.Trim();
        if (!string.IsNullOrWhiteSpace(launcherProvider)) return launcherProvider.Trim();
        var usable = drives.Where(d => !string.IsNullOrWhiteSpace(d.Root)).ToList();
        var site = siteCode?.Trim() ?? "";
        var drive = usable.FirstOrDefault(d => site.Length > 0 && string.Equals(d.Name?.Trim(), site, StringComparison.OrdinalIgnoreCase))
                    ?? usable.FirstOrDefault();
        return drive?.Root.Trim().TrimEnd('\\') ?? "";
    }

    /// <summary>
    /// Reads the CMSite drives only when neither the preference nor the launcher names a
    /// provider and the SQL overrides leave the site lookup needed.
    /// </summary>
    public async Task<string> ResolveProviderNameAsync(AppSettings settings, CancellationToken ct)
    {
        var launcher = _launcherProvider();
        var overrides = !string.IsNullOrWhiteSpace(settings.DiscoverySqlServer) && !string.IsNullOrWhiteSpace(settings.DiscoverySqlDatabase);
        if (overrides || !string.IsNullOrWhiteSpace(settings.ProviderMachine) || !string.IsNullOrWhiteSpace(launcher))
            return ResolveProviderName(settings.ProviderMachine, launcher, settings.SiteCode, []);
        IReadOnlyList<CmSiteDrive> drives;
        try { drives = await _drives(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { drives = []; }
        return ResolveProviderName(null, null, settings.SiteCode, drives);
    }

    /// <summary>
    /// Runs one scan on a worker thread. Returns at the time limit or on <paramref name="ct"/>
    /// even when the service ignores its token; the abandoned scan finishes on its own.
    /// Never throws: every failure comes back as a plain message without exception detail.
    /// </summary>
    public async Task<DiscoveryScanOutcome> ScanAsync(CancellationToken ct)
    {
        using var limit = new CancellationTokenSource(TimeLimit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        try
        {
            var settings = _load();
            var (enabled, toolTip) = Availability(settings);
            if (!enabled) return new DiscoveryScanOutcome(null, toolTip[LogPrefix.Length..]);

            var packagers = await _packagers(linked.Token);
            var provider = await ResolveProviderNameAsync(settings, linked.Token);
            var request = new DiscoveryRequest(
                settings.SiteCode?.Trim() ?? "",
                provider,
                settings.DiscoverySqlServer ?? "",
                settings.DiscoverySqlDatabase ?? "",
                packagers,
                settings.HiddenApplications ?? [],
                settings.DiscoveryDismissed ?? [],
                (int)Math.Ceiling(TimeLimit.TotalSeconds));

            var token = linked.Token;
            var result = await Task.Run(() => _service.ScanAsync(request, token), token).WaitAsync(token);
            return result is null
                ? new DiscoveryScanOutcome(null, "the query returned no result.")
                : new DiscoveryScanOutcome(result, result.StatusText ?? "");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new DiscoveryScanOutcome(null, "the query was stopped.", Cancelled: true);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
            return new DiscoveryScanOutcome(null, $"the query did not finish within {TimeLimit.TotalSeconds:0} seconds and was stopped.");
        }
        catch (DiscoveryException ex)
        {
            return new DiscoveryScanOutcome(null, ex.Message);
        }
        catch (Exception)
        {
            return new DiscoveryScanOutcome(null, "the query failed with an unexpected error. Nothing changed.");
        }
    }

    /// <summary>
    /// The whole action: scan, prompt when there are candidates, apply the choice, reload the
    /// grid when an application became visible. Awaited from the UI thread, the prompt and the
    /// callbacks run there; only the scan runs on a worker thread. Returns the applied choice,
    /// or null when no prompt was shown.
    /// </summary>
    public async Task<DiscoveryChoice?> RunAsync(IDiscoveryPrompt prompt, Action<string> log, Action<string> status,
        Func<Task> reloadGrid, Action settingsChanged, CancellationToken ct)
    {
        status("Reading Add/Remove Programs inventory from the ConfigMgr site database...");
        log(LogPrefix + "reading hardware inventory from the ConfigMgr site database.");
        var outcome = await ScanAsync(ct);
        if (outcome.Result is not { } result)
        {
            log(LogPrefix + outcome.Message);
            status(LogPrefix + outcome.Message);
            return null;
        }

        log(LogPrefix + result.StatusText);
        status(result.StatusText);
        if (result.Candidates.Count == 0) return null;

        void Report(string message)
        {
            log(LogPrefix + message);
            status(LogPrefix + message);
        }

        DiscoveryChoice choice;
        try
        {
            choice = prompt.Show(new DiscoveryDialogViewModel(result.Candidates, result.StatusText));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(NoPromptMessage);
            return null;
        }

        DiscoveryApplyOutcome applied;
        try
        {
            applied = Apply(choice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Report(NotSavedMessage);
            return choice;
        }
        log(applied.LogLine);
        try
        {
            if (applied.GridChanged) await reloadGrid();
            if (applied.SettingsChanged) settingsChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(NotRefreshedMessage);
            return choice;
        }
        status(applied.StatusLine);
        return choice;
    }

    /// <summary>
    /// Applies the prompt's choice to the stored settings and saves them when anything changed.
    /// Throws <see cref="InvalidOperationException"/> while the settings file is unreadable:
    /// the load returns defaults then and the automatic save writes nothing.
    /// </summary>
    public DiscoveryApplyOutcome Apply(DiscoveryChoice choice)
    {
        if (choice.Action != DiscoveryAction.NotNow && _settingsUnreadable())
            throw new InvalidOperationException("The settings file is unreadable; an automatic save writes nothing.");
        switch (choice.Action)
        {
            case DiscoveryAction.EnableSelected:
            case DiscoveryAction.EnableAll:
            {
                var settings = _load();
                var count = DiscoveryPreferences.Enable(settings, choice.Ids);
                _save(settings);
                return new DiscoveryApplyOutcome(true, count > 0,
                    LogPrefix + $"{count} app(s) added to the grid. Show or hide them with Catalog on the App Packager page.",
                    $"{count} app(s) added to the grid.");
            }
            case DiscoveryAction.Dismiss:
            {
                var settings = _load();
                var count = DiscoveryPreferences.Dismiss(settings, choice.Ids);
                _save(settings);
                return new DiscoveryApplyOutcome(true, false,
                    LogPrefix + $"{count} app(s) will not be offered again. Clear the list in Settings, ConfigMgr Site Settings.",
                    $"{count} app(s) will not be offered again.");
            }
            default:
                return new DiscoveryApplyOutcome(false, false, LogPrefix + "no change.", "Discover unmanaged software: no change.");
        }
    }

    /// <summary>
    /// Lists the CMSite drives the ConfigMgr module creates in a Windows PowerShell child.
    /// Output lines are DRIVE|name|root; any failure gives an empty list.
    /// </summary>
    public const string DriveScript = """
        if (-not (Get-Command -Name Get-CMSite -ErrorAction SilentlyContinue)) {
            try {
                if ($env:SMS_ADMIN_UI_PATH) {
                    $cmModule = Join-Path (Split-Path $env:SMS_ADMIN_UI_PATH) 'ConfigurationManager.psd1'
                    if (Test-Path -LiteralPath $cmModule) { Import-Module $cmModule -ErrorAction Stop }
                }
            } catch {}
        }
        try {
            foreach ($drive in @(Get-PSDrive -PSProvider CMSite -ErrorAction Stop)) {
                Write-Output ('DRIVE|{0}|{1}' -f $drive.Name, $drive.Root)
            }
        } catch {}
        exit 0
        """;

    private static readonly TimeSpan DriveListTimeout = TimeSpan.FromSeconds(60);

    public static IReadOnlyList<CmSiteDrive> ParseDrives(string? output) =>
        (output ?? "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("DRIVE|", StringComparison.Ordinal))
            .Select(line => line.Split('|'))
            .Where(parts => parts.Length >= 3 && parts[1].Length > 0 && parts[2].Length > 0)
            .Select(parts => new CmSiteDrive(parts[1], parts[2]))
            .ToList();

    public static async Task<IReadOnlyList<CmSiteDrive>> ListCmSiteDrivesAsync(CancellationToken ct)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"packr-cmdrives-{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(scriptFile, DriveScript, ct).ConfigureAwait(false);
            var startInfo = new ProcessStartInfo { FileName = WindowsPowerShellHost.ExecutableName };
            WindowsPowerShellHost.ApplyLogonModulePath(startInfo);
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptFile })
                startInfo.ArgumentList.Add(argument);
            var run = await BoundedProcessRunner.RunAsync(startInfo, DriveListTimeout, ct).ConfigureAwait(false);
            return run.TimedOut ? [] : ParseDrives(run.StdOut);
        }
        finally
        {
            try { File.Delete(scriptFile); } catch (IOException) { }
        }
    }
}
