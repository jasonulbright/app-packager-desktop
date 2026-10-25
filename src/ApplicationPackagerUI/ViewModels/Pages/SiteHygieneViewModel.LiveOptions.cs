using System.Globalization;
using System.Text;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Live and Alerts groups of the Options tab and the dashboard import. A value the operator never set is stored as
/// null and uses the default, so an import fills only the values that were never set.
/// </summary>
public sealed partial class SiteHygieneViewModel
{
    public const string ImportNothing = "The Health Dashboard import found nothing to copy.";
    public const string ImportPlanFailed = "The Health Dashboard folder could not be read. Nothing was copied. The log has the details.";
    public const string ImportFailed = "The import did not end. The old files were not changed. The log has the details.";
    public const string ImportIntro =
        "The import copies the values below from the retired Health Dashboard. It reads the old files only: it does not move, change or delete them. A setting fills only a value that was never set on this page.";
    public const string LiveSaveFailed = "The Live options could not be saved. The log has the details.";
    public const string ImportNotSaved = "The Live options could not be saved, so the import did not start. Nothing was copied. The log has the details.";
    public const string SqlTesting = "Testing the SQL connection.";

    private const string KeyTrust = LiveSettingKeys.TrustSqlCertificate;
    private const string KeyRefresh = LiveSettingKeys.RefreshMinutes;
    private const string KeyInactive = LiveSettingKeys.InactiveDays;
    private const string KeyAlerts = LiveSettingKeys.AlertsEnabled;
    private const string KeyFloor = LiveSettingKeys.CompliancePctFloor;
    private const string KeySiteCritical = "AlertSiteCritical";
    private const string KeyDpCritical = "AlertDpCritical";
    private const string KeyContentFailed = "AlertContentFailed";
    private const string KeyCompliance = "AlertCompliance";

    /// <summary>The Live values the operator or an import set; a value not in the set is stored as null.</summary>
    private readonly HashSet<string> _liveSet = new(StringComparer.Ordinal);
    /// <summary>The Live options and SQL values as the settings file holds them; a save runs only when they differ.</summary>
    private string _storedLive = LiveStoreKey(new SiteHygieneLiveSettings());
    private bool _importChecked;
    private LegacyImportPlan? _importOffered;

    [ObservableProperty] private string _liveSqlServer = "";
    [ObservableProperty] private string _liveSqlDatabase = "";
    [ObservableProperty] private bool _trustSqlCertificate;
    [ObservableProperty] private int _refreshMinutes = LiveSettings.DefaultRefreshMinutes;
    [ObservableProperty] private int _inactiveDays = LiveSettings.DefaultInactiveDays;
    [ObservableProperty] private bool _alertsEnabled = AlertSettings.Default.Enabled;
    [ObservableProperty] private bool _alertSiteCritical = AlertSettings.Default.SiteCritical;
    [ObservableProperty] private bool _alertDpCritical = AlertSettings.Default.DpCritical;
    [ObservableProperty] private bool _alertContentFailed = AlertSettings.Default.ContentFailed;
    [ObservableProperty] private bool _alertCompliance = AlertSettings.Default.CompliancePctFloor is not null;
    [ObservableProperty] private int _compliancePctFloor = AlertSettings.Default.CompliancePctFloor ?? AlertSettings.DefaultCompliancePctFloor;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSqlCommand))]
    private bool _isTestingSql;

    [ObservableProperty] private string _sqlTestResult = "";

    /// <summary>The offer of the first visit to import from the retired Health Dashboard; empty when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportOffer))]
    private string _importOffer = "";

    public bool HasImportOffer => ImportOffer.Length > 0;

    [ObservableProperty] private bool _importOfferDismissed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportDashboardCommand))]
    private bool _isImporting;

    /// <summary>A new interval restarts an armed timer, so the next refresh waits the new interval.</summary>
    partial void OnRefreshMinutesChanged(int value)
    {
        LiveSet(KeyRefresh);
        if (IsAutoRefreshArmed)
        {
            StopTimer();
            ArmTimer();
        }
        ShowAutoRefresh();
    }

    partial void OnTrustSqlCertificateChanged(bool value) => LiveSet(KeyTrust);
    partial void OnInactiveDaysChanged(int value) => LiveSet(KeyInactive);
    partial void OnAlertsEnabledChanged(bool value) => LiveSet(KeyAlerts);
    partial void OnAlertSiteCriticalChanged(bool value) => LiveSet(KeySiteCritical);
    partial void OnAlertDpCriticalChanged(bool value) => LiveSet(KeyDpCritical);
    partial void OnAlertContentFailedChanged(bool value) => LiveSet(KeyContentFailed);
    partial void OnAlertComplianceChanged(bool value) => LiveSet(KeyCompliance);
    partial void OnCompliancePctFloorChanged(int value) => LiveSet(KeyFloor);
    partial void OnLiveSqlServerChanged(string value) => SaveLater();
    partial void OnLiveSqlDatabaseChanged(string value) => SaveLater();
    partial void OnImportOfferDismissedChanged(bool value) => SaveLater();

    private void LiveSet(string key)
    {
        if (_applying) return;
        _liveSet.Add(key);
        SaveLater();
    }

    /// <summary>
    /// Shows the Live and Alerts options of the settings file: the shared SQL values and <see cref="AppSettings.SiteHygieneLive"/>.
    /// Runs on the first visit and after an import filled values; it does not save.
    /// </summary>
    private void LoadStoredLive(AppSettings settings)
    {
        _applying = true;
        try
        {
            LiveSqlServer = (settings.DiscoverySqlServer ?? "").Trim();
            LiveSqlDatabase = (settings.DiscoverySqlDatabase ?? "").Trim();
            ApplyLiveOptions(settings.SiteHygieneLive);
        }
        finally { _applying = false; }
        _loadedSql = (LiveSqlServer, LiveSqlDatabase);
        _storedLive = LiveStoreKey(settings.SiteHygieneLive ?? new SiteHygieneLiveSettings());
        _liveLoaded = true;
    }

    /// <summary>False until the stored Live options were read; a save before that would replace them with the defaults.</summary>
    private bool _liveLoaded;

    /// <summary>
    /// The SQL server and database as the settings file held them at the last load or save. Discover and the Settings
    /// page write the same values, so the page writes them back only when the operator changed them here.
    /// </summary>
    private (string Server, string Database) _loadedSql = ("", "");

    /// <summary>True when the operator changed the SQL server or database on this page since the last load or save.</summary>
    private bool SqlEdited => !string.Equals((LiveSqlServer ?? "").Trim(), _loadedSql.Server, StringComparison.Ordinal)
        || !string.Equals((LiveSqlDatabase ?? "").Trim(), _loadedSql.Database, StringComparison.Ordinal);

    /// <summary>
    /// Reads the SQL server and database from the settings file again when the page has no SQL edit of its own, so a
    /// value changed on the Settings page or by Discover shows here and a refresh reads with it.
    /// </summary>
    private async Task ReloadSqlAsync()
    {
        if (Services.Live is null || SqlEdited) return;
        AppSettings settings;
        try { settings = await Task.Run(Services.LoadSettings); }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The SQL settings could not be read ({ex.GetType().Name}).");
            return;
        }
        if (SqlEdited) return;
        if (!_liveLoaded)
        {
            if (LiveOptions() is var options && LiveStoreKey(options) == _storedLive) LoadStoredLive(settings);
            return;
        }
        var server = (settings.DiscoverySqlServer ?? "").Trim();
        var database = (settings.DiscoverySqlDatabase ?? "").Trim();
        if (string.Equals(server, _loadedSql.Server, StringComparison.Ordinal) && string.Equals(database, _loadedSql.Database, StringComparison.Ordinal)) return;
        _applying = true;
        try
        {
            LiveSqlServer = server;
            LiveSqlDatabase = database;
        }
        finally { _applying = false; }
        _loadedSql = (server, database);
    }

    /// <summary>Takes the stored value or the default; a stored value is clamped to its range and marked as set.</summary>
    private void ApplyLiveOptions(SiteHygieneLiveSettings? live)
    {
        _liveSet.Clear();
        void Mark(bool set, string key)
        {
            if (set) _liveSet.Add(key);
        }
        var defaults = AlertSettings.Default;
        TrustSqlCertificate = live?.TrustSqlCertificate ?? LiveSettings.Default.TrustSqlCertificate;
        Mark(live?.TrustSqlCertificate is not null, KeyTrust);
        RefreshMinutes = Nearest(RefreshChoices, LiveSettings.ClampRefresh(live?.RefreshMinutes ?? LiveSettings.DefaultRefreshMinutes));
        Mark(live?.RefreshMinutes is not null, KeyRefresh);
        InactiveDays = Nearest(InactiveChoices, LiveSettings.ClampInactive(live?.InactiveDays ?? LiveSettings.DefaultInactiveDays));
        Mark(live?.InactiveDays is not null, KeyInactive);
        AlertsEnabled = live?.AlertsEnabled ?? defaults.Enabled;
        Mark(live?.AlertsEnabled is not null, KeyAlerts);
        AlertSiteCritical = live?.AlertSiteCritical ?? defaults.SiteCritical;
        Mark(live?.AlertSiteCritical is not null, KeySiteCritical);
        AlertDpCritical = live?.AlertDpCritical ?? defaults.DpCritical;
        Mark(live?.AlertDpCritical is not null, KeyDpCritical);
        AlertContentFailed = live?.AlertContentFailed ?? defaults.ContentFailed;
        Mark(live?.AlertContentFailed is not null, KeyContentFailed);
        AlertCompliance = live?.AlertCompliance ?? defaults.CompliancePctFloor is not null;
        Mark(live?.AlertCompliance is not null, KeyCompliance);
        CompliancePctFloor = Nearest(FloorChoices, Math.Clamp(live?.CompliancePctFloor ?? AlertSettings.DefaultCompliancePctFloor, 0, 100));
        Mark(live?.CompliancePctFloor is not null, KeyFloor);
    }

    /// <summary>The choice nearest to a stored value, the larger one on a tie, so an imported value shows in its list.</summary>
    public static int Nearest(IReadOnlyList<int> choices, int value) =>
        choices.OrderBy(c => Math.Abs(c - value)).ThenByDescending(c => c).First();

    /// <summary>The Live and Alerts options as the settings file stores them: a value the operator never set is null.</summary>
    public SiteHygieneLiveSettings LiveOptions()
    {
        T? If<T>(string key, T value) where T : struct => _liveSet.Contains(key) ? value : null;
        return new SiteHygieneLiveSettings
        {
            TrustSqlCertificate = If(KeyTrust, TrustSqlCertificate),
            RefreshMinutes = If(KeyRefresh, RefreshMinutes),
            InactiveDays = If(KeyInactive, InactiveDays),
            AlertsEnabled = If(KeyAlerts, AlertsEnabled),
            AlertSiteCritical = If(KeySiteCritical, AlertSiteCritical),
            AlertDpCritical = If(KeyDpCritical, AlertDpCritical),
            AlertContentFailed = If(KeyContentFailed, AlertContentFailed),
            AlertCompliance = If(KeyCompliance, AlertCompliance),
            CompliancePctFloor = If(KeyFloor, CompliancePctFloor)
        };
    }

    private static string LiveStoreKey(SiteHygieneLiveSettings live) => System.Text.Json.JsonSerializer.Serialize(live);

    /// <summary>
    /// Saves the Live and Alerts options when they changed, and the SQL server or database only when the operator changed
    /// it on this page. The SQL values are the Discover settings, so Discover uses the same site database, and a value
    /// that Discover or the Settings page wrote after the page loaded stays.
    /// </summary>
    /// <returns>False when a change could not be written; the settings file then holds older values than the page.</returns>
    private async Task<bool> SaveLiveAsync()
    {
        if (Services.Live is not { } live) return true;
        var options = LiveOptions();
        var server = (LiveSqlServer ?? "").Trim();
        var database = (LiveSqlDatabase ?? "").Trim();
        var key = LiveStoreKey(options);
        string? newServer = string.Equals(server, _loadedSql.Server, StringComparison.Ordinal) ? null : server;
        string? newDatabase = string.Equals(database, _loadedSql.Database, StringComparison.Ordinal) ? null : database;
        if (key == _storedLive && newServer is null && newDatabase is null) return true;
        if (!_liveLoaded)
        {
            StatusMessage = LiveSaveFailed;
            Services.Log.Warning(LogArea, "The Live options were not saved: the stored options were not read.");
            return false;
        }
        bool saved;
        try { saved = await Task.Run(() => live.SaveLive(options, newServer, newDatabase)); }
        catch (Exception ex)
        {
            saved = false;
            Services.Log.Warning(LogArea, $"The Live options could not be saved ({ex.GetType().Name}).");
        }
        if (saved)
        {
            _storedLive = key;
            _loadedSql = (newServer ?? _loadedSql.Server, newDatabase ?? _loadedSql.Database);
        }
        else StatusMessage = LiveSaveFailed;
        return saved;
    }

    /// <summary>Reads the Live and Alerts options back from the settings file, as an import wrote them.</summary>
    private async Task ReloadLiveAsync()
    {
        AppSettings settings;
        try { settings = await Task.Run(Services.LoadSettings); }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The Live options could not be read after the import ({ex.GetType().Name}).");
            return;
        }
        LoadStoredLive(settings);
    }

    private bool CanTestSql() => HasLive && !IsTestingSql;

    [RelayCommand(CanExecute = nameof(CanTestSql))]
    private async Task TestSql()
    {
        if (Services.Live is not { } live) return;
        IsTestingSql = true;
        SqlTestResult = SqlTesting;
        try
        {
            await SaveLiveAsync();
            var settings = CurrentLiveSettings();
            SqlTestResult = SecretMask.Apply(await Task.Run(() => live.Live.TestSqlAsync(settings, CancellationToken.None)));
        }
        catch (Exception ex)
        {
            SqlTestResult = "The SQL test did not end. The log has the details.";
            Services.Log.Warning(LogArea, $"The SQL test failed ({ex.GetType().Name}).");
        }
        finally { IsTestingSql = false; }
    }

    /// <summary>On the first visit with no metrics history, offers an import when the old dashboard holds something to copy; it copies nothing by itself.</summary>
    private async Task CheckImportOfferAsync()
    {
        if (_importChecked || ImportOfferDismissed || Services.Live is not { } live) return;
        _importChecked = true;
        LegacyImportPlan plan;
        try
        {
            if (await Task.Run(() => live.History.Exists)) return;
            plan = await Task.Run(live.Import.Plan);
        }
        catch (Exception ex)
        {
            Services.Log.Warning(LogArea, $"The Health Dashboard folder could not be checked ({ex.GetType().Name}).");
            return;
        }
        if (plan.Refusal is not null || plan.IsEmpty || ImportOfferDismissed) return;
        _importOffered = plan;
        ImportOffer = $"A retired Health Dashboard folder was found: {plan.SourceFolder}. Select Import to see what it copies and confirm it, or Not now.";
    }

    [RelayCommand]
    private void DismissImportOffer()
    {
        ImportOffer = "";
        ImportOfferDismissed = true;
    }

    private bool CanImportDashboard() => HasLive && !IsImporting;

    [RelayCommand(CanExecute = nameof(CanImportDashboard))]
    private async Task ImportDashboard()
    {
        if (Services.Live is not { } live) return;
        IsImporting = true;
        try
        {
            // The plan compares with the settings file and the import reloads the page from it, so the values chosen on the page are written first.
            await SaveAsync();
            if (!await SaveLiveAsync())
            {
                StatusMessage = ImportNotSaved;
                return;
            }
            LegacyImportPlan plan;
            try { plan = await Task.Run(live.Import.Plan); }
            catch (Exception ex)
            {
                Services.Log.Warning(LogArea, $"The Health Dashboard plan failed ({ex.GetType().Name}).");
                StatusMessage = ImportPlanFailed;
                return;
            }
            if (plan.Refusal is { } refusal)
            {
                StatusMessage = SecretMask.Apply(refusal);
                return;
            }
            var fills = Fills(plan);
            if (!plan.HistoryWillBeCopied && fills.Count == 0)
            {
                StatusMessage = ImportNothing;
                ImportOffer = "";
                return;
            }
            var dialog = new HygieneImportViewModel(ImportIntro, PlanText(plan, fills));
            Services.Dialogs.ConfirmImport(dialog);
            if (!dialog.Confirmed)
            {
                StatusMessage = NotConfirmed;
                return;
            }
            LegacyImportResult result;
            try { result = await Task.Run(() => live.Import.Apply(plan)); }
            catch (Exception ex)
            {
                Services.Log.Warning(LogArea, $"The Health Dashboard import failed ({ex.GetType().Name}).");
                StatusMessage = ImportFailed;
                return;
            }
            // The import is the only writer of the imported values; the page shows what it stored.
            await ReloadLiveAsync();
            ImportOffer = "";
            ImportOfferDismissed = true;
            await SaveAsync();
            var filled = result.SettingKeys.Select(SettingName).ToList();
            StatusMessage = SecretMask.Apply(result.Message) + (filled.Count > 0 ? $" Settings filled: {string.Join(", ", filled)}." : "");
            Services.Log.Info(LogArea, $"Health Dashboard import: history rows={result.HistoryRows} settings={filled.Count}.");
            _trendsStale = true;
            if (IsTrendsPane && _active) _ = LoadTrendsAsync();
        }
        finally { IsImporting = false; }
    }

    /// <summary>The label of a setting key of an import plan.</summary>
    private static string SettingName(string key) => key switch
    {
        LiveSettingKeys.SqlServer => "SQL server",
        LiveSettingKeys.TrustSqlCertificate => "Trust the SQL server certificate",
        LiveSettingKeys.RefreshMinutes => "Refresh interval",
        LiveSettingKeys.InactiveDays => "Inactivity threshold",
        LiveSettingKeys.AlertsEnabled => "Alerts",
        LiveSettingKeys.CompliancePctFloor => "Compliance floor",
        _ => key
    };

    /// <summary>The settings the plan fills, each with the value the page shows after the import; the plan holds only values that are empty in the settings file.</summary>
    private static List<(string Name, string Value)> Fills(LegacyImportPlan plan)
    {
        var values = plan.Values;
        var fills = new List<(string Name, string Value)>();
        foreach (var key in plan.SettingKeys)
        {
            string? value = key switch
            {
                LiveSettingKeys.SqlServer => values.SqlServer is { Length: > 0 } server ? server.Trim() : null,
                LiveSettingKeys.TrustSqlCertificate => values.TrustSqlCertificate is { } trust ? (trust ? "Yes" : "No") : null,
                LiveSettingKeys.RefreshMinutes => values.RefreshMinutes is { } minutes ? SiteHygieneLabels.Count(Nearest(RefreshChoices, LiveSettings.ClampRefresh(minutes)), "minute") : null,
                LiveSettingKeys.InactiveDays => values.InactiveDays is { } days ? SiteHygieneLabels.Count(Nearest(InactiveChoices, LiveSettings.ClampInactive(days)), "day") : null,
                LiveSettingKeys.AlertsEnabled => values.AlertsEnabled is { } enabled ? (enabled ? "On" : "Off") : null,
                LiveSettingKeys.CompliancePctFloor => values.CompliancePctFloor is { } floor ? Nearest(FloorChoices, Math.Clamp(floor, 0, 100)).ToString(CultureInfo.CurrentCulture) + " %" : null,
                _ => null
            };
            if (value is not null) fills.Add((SettingName(key), value));
        }
        return fills;
    }

    private static string PlanText(LegacyImportPlan plan, IReadOnlyList<(string Name, string Value)> fills)
    {
        var text = new StringBuilder();
        text.AppendLine($"Folder: {plan.SourceFolder}");
        text.AppendLine(plan.HistoryWillBeCopied
            ? $"Metrics history: {SiteHygieneLabels.Count(plan.HistoryRows, "row")}{(plan.HistoryFile.Length > 0 ? " from " + plan.HistoryFile : "")}."
            : "Metrics history: not copied.");
        if (plan.SkippedLines > 0) text.AppendLine($"The history file has {SiteHygieneLabels.Count(plan.SkippedLines, "line")} that cannot be read. The import skips them.");
        if (fills.Count == 0) text.AppendLine("Settings: none. Every setting of the old dashboard has a value on this page already.");
        foreach (var (name, value) in fills) text.AppendLine($"Setting {name}: {value}{(plan.SettingsFile.Length > 0 ? " from " + plan.SettingsFile : "")}.");
        return text.ToString().TrimEnd();
    }
}
