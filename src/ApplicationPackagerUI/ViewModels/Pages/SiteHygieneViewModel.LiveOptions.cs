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
    public const string SqlSaveFailed = "The SQL server and database could not be saved. The log has the details.";
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
    private (string Server, string Database) _storedSql = ("", "");
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

    /// <summary>The shared SQL values of the settings file, read on the first visit.</summary>
    private void LoadSql(AppSettings settings)
    {
        _applying = true;
        try
        {
            LiveSqlServer = (settings.DiscoverySqlServer ?? "").Trim();
            LiveSqlDatabase = (settings.DiscoverySqlDatabase ?? "").Trim();
        }
        finally { _applying = false; }
        _storedSql = (LiveSqlServer, LiveSqlDatabase);
    }

    /// <summary>Takes the stored value or the default; a stored value is clamped to its range and marked as set.</summary>
    private void ApplyLiveOptions(SiteHygieneSettings saved)
    {
        _liveSet.Clear();
        void Mark(bool set, string key)
        {
            if (set) _liveSet.Add(key);
        }
        TrustSqlCertificate = saved.TrustSqlCertificate ?? false;
        Mark(saved.TrustSqlCertificate is not null, KeyTrust);
        RefreshMinutes = Nearest(RefreshChoices, LiveSettings.ClampRefresh(saved.RefreshMinutes ?? LiveSettings.DefaultRefreshMinutes));
        Mark(saved.RefreshMinutes is not null, KeyRefresh);
        InactiveDays = Nearest(InactiveChoices, LiveSettings.ClampInactive(saved.InactiveDays ?? LiveSettings.DefaultInactiveDays));
        Mark(saved.InactiveDays is not null, KeyInactive);
        var alerts = saved.Alerts;
        var defaults = AlertSettings.Default;
        AlertsEnabled = alerts?.Enabled ?? defaults.Enabled;
        Mark(alerts?.Enabled is not null, KeyAlerts);
        AlertSiteCritical = alerts?.SiteCritical ?? defaults.SiteCritical;
        Mark(alerts?.SiteCritical is not null, KeySiteCritical);
        AlertDpCritical = alerts?.DpCritical ?? defaults.DpCritical;
        Mark(alerts?.DpCritical is not null, KeyDpCritical);
        AlertContentFailed = alerts?.ContentFailed ?? defaults.ContentFailed;
        Mark(alerts?.ContentFailed is not null, KeyContentFailed);
        AlertCompliance = alerts?.Compliance ?? defaults.CompliancePctFloor is not null;
        Mark(alerts?.Compliance is not null, KeyCompliance);
        CompliancePctFloor = Nearest(FloorChoices, Math.Clamp(alerts?.CompliancePctFloor ?? AlertSettings.DefaultCompliancePctFloor, 0, 100));
        Mark(alerts?.CompliancePctFloor is not null, KeyFloor);
    }

    /// <summary>The choice nearest to a stored value, the larger one on a tie, so an imported value shows in its list.</summary>
    public static int Nearest(IReadOnlyList<int> choices, int value) =>
        choices.OrderBy(c => Math.Abs(c - value)).ThenByDescending(c => c).First();

    private void WriteLiveOptions(SiteHygieneSettings settings)
    {
        T? If<T>(string key, T value) where T : struct => _liveSet.Contains(key) ? value : null;
        settings.TrustSqlCertificate = If(KeyTrust, TrustSqlCertificate);
        settings.RefreshMinutes = If(KeyRefresh, RefreshMinutes);
        settings.InactiveDays = If(KeyInactive, InactiveDays);
        var alerts = new SiteHygieneAlertChoices
        {
            Enabled = If(KeyAlerts, AlertsEnabled),
            SiteCritical = If(KeySiteCritical, AlertSiteCritical),
            DpCritical = If(KeyDpCritical, AlertDpCritical),
            ContentFailed = If(KeyContentFailed, AlertContentFailed),
            Compliance = If(KeyCompliance, AlertCompliance),
            CompliancePctFloor = If(KeyFloor, CompliancePctFloor)
        };
        settings.Alerts = alerts is { Enabled: null, SiteCritical: null, DpCritical: null, ContentFailed: null, Compliance: null, CompliancePctFloor: null } ? null : alerts;
    }

    /// <summary>Saves the SQL server and database when they changed; they are the Discover settings, so Discover uses the same site database.</summary>
    private async Task SaveSqlAsync()
    {
        var sql = ((LiveSqlServer ?? "").Trim(), (LiveSqlDatabase ?? "").Trim());
        if (sql == _storedSql || Services.Live is not { } live) return;
        bool saved;
        try { saved = await Task.Run(() => live.SaveSql(sql.Item1, sql.Item2)); }
        catch (Exception ex)
        {
            saved = false;
            Services.Log.Warning(LogArea, $"The SQL settings could not be saved ({ex.GetType().Name}).");
        }
        if (saved) _storedSql = sql;
        else StatusMessage = SqlSaveFailed;
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
            await SaveSqlAsync();
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
            Fill(fills);
            ImportOffer = "";
            ImportOfferDismissed = true;
            await SaveAsync();
            StatusMessage = SecretMask.Apply(result.Message) + (fills.Count > 0 ? $" Settings filled: {string.Join(", ", fills.Select(f => f.Name))}." : "");
            Services.Log.Info(LogArea, $"Health Dashboard import: history rows={result.HistoryRows} settings={fills.Count}.");
            _trendsStale = true;
            if (IsTrendsPane && _active) _ = LoadTrendsAsync();
        }
        finally { IsImporting = false; }
    }

    /// <summary>One setting an import fills: its key, its label and the value as text.</summary>
    private sealed record ImportFill(string Key, string Name, string Value, Action Apply);

    /// <summary>The settings of the plan whose value on this page was never set; every other setting stays.</summary>
    private List<ImportFill> Fills(LegacyImportPlan plan)
    {
        var values = plan.Values;
        var fills = new List<ImportFill>();
        var culture = CultureInfo.CurrentCulture;
        foreach (var key in plan.SettingKeys)
        {
            switch (key)
            {
                case LiveSettingKeys.SqlServer when values.SqlServer is { Length: > 0 } server && (LiveSqlServer ?? "").Trim().Length == 0:
                    fills.Add(new ImportFill(key, "SQL server", server, () => LiveSqlServer = server.Trim()));
                    break;
                case LiveSettingKeys.TrustSqlCertificate when values.TrustSqlCertificate is { } trust && !_liveSet.Contains(KeyTrust):
                    fills.Add(new ImportFill(key, "Trust the SQL server certificate", trust ? "Yes" : "No", () => TrustSqlCertificate = trust));
                    break;
                case LiveSettingKeys.RefreshMinutes when values.RefreshMinutes is { } minutes && !_liveSet.Contains(KeyRefresh):
                    var refresh = Nearest(RefreshChoices, LiveSettings.ClampRefresh(minutes));
                    fills.Add(new ImportFill(key, "Refresh interval", SiteHygieneLabels.Count(refresh, "minute"), () => RefreshMinutes = refresh));
                    break;
                case LiveSettingKeys.InactiveDays when values.InactiveDays is { } days && !_liveSet.Contains(KeyInactive):
                    var inactive = Nearest(InactiveChoices, LiveSettings.ClampInactive(days));
                    fills.Add(new ImportFill(key, "Inactivity threshold", SiteHygieneLabels.Count(inactive, "day"), () => InactiveDays = inactive));
                    break;
                case LiveSettingKeys.AlertsEnabled when values.AlertsEnabled is { } enabled && !_liveSet.Contains(KeyAlerts):
                    fills.Add(new ImportFill(key, "Alerts", enabled ? "On" : "Off", () => AlertsEnabled = enabled));
                    break;
                case LiveSettingKeys.CompliancePctFloor when values.CompliancePctFloor is { } floor && !_liveSet.Contains(KeyFloor) && !_liveSet.Contains(KeyCompliance):
                    var nearest = Nearest(FloorChoices, Math.Clamp(floor, 0, 100));
                    fills.Add(new ImportFill(key, "Compliance floor", nearest.ToString(culture) + " %", () =>
                    {
                        CompliancePctFloor = nearest;
                        AlertCompliance = true;
                    }));
                    break;
            }
        }
        return fills;
    }

    private static void Fill(IEnumerable<ImportFill> fills)
    {
        foreach (var fill in fills) fill.Apply();
    }

    private string PlanText(LegacyImportPlan plan, IReadOnlyList<ImportFill> fills)
    {
        var text = new StringBuilder();
        text.AppendLine($"Folder: {plan.SourceFolder}");
        text.AppendLine(plan.HistoryWillBeCopied
            ? $"Metrics history: {SiteHygieneLabels.Count(plan.HistoryRows, "row")}{(plan.HistoryFile.Length > 0 ? " from " + plan.HistoryFile : "")}."
            : "Metrics history: not copied.");
        if (plan.SkippedLines > 0) text.AppendLine($"The history file has {SiteHygieneLabels.Count(plan.SkippedLines, "line")} that cannot be read. The import skips them.");
        if (fills.Count == 0) text.AppendLine("Settings: none. Every setting of the old dashboard has a value on this page already.");
        foreach (var fill in fills) text.AppendLine($"Setting {fill.Name}: {fill.Value}{(plan.SettingsFile.Length > 0 ? " from " + plan.SettingsFile : "")}.");
        var kept = plan.SettingKeys.Count - fills.Count;
        if (kept > 0) text.AppendLine($"{SiteHygieneLabels.Count(kept, "setting")} of the old dashboard {(kept == 1 ? "is" : "are")} not copied, as this page has a value already.");
        return text.ToString().TrimEnd();
    }
}
