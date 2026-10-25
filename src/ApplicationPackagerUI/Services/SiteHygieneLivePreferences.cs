using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.Services;

/// <summary>
/// The Live settings of the Site Hygiene page in the settings file: the SQL Server and database are the
/// Discover settings, the rest is <see cref="AppSettings.SiteHygieneLive"/>. A missing value gives the default.
/// </summary>
public static class SiteHygieneLivePreferences
{
    private static readonly object FileLock = new();

    public static LiveSettings ToLiveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var live = settings.SiteHygieneLive ?? new SiteHygieneLiveSettings();
        var d = LiveSettings.Default;
        var a = AlertSettings.Default;
        var floor = live.CompliancePctFloor is { } f ? Math.Clamp(f, 0, 100) : AlertSettings.DefaultCompliancePctFloor;
        return new LiveSettings(
            SqlServer: (settings.DiscoverySqlServer ?? "").Trim(),
            SqlDatabase: (settings.DiscoverySqlDatabase ?? "").Trim(),
            TrustSqlCertificate: live.TrustSqlCertificate ?? d.TrustSqlCertificate,
            RefreshMinutes: LiveSettings.ClampRefresh(live.RefreshMinutes ?? d.RefreshMinutes),
            InactiveDays: LiveSettings.ClampInactive(live.InactiveDays ?? d.InactiveDays),
            Alerts: new AlertSettings(
                Enabled: live.AlertsEnabled ?? a.Enabled,
                SiteCritical: live.AlertSiteCritical ?? a.SiteCritical,
                DpCritical: live.AlertDpCritical ?? a.DpCritical,
                ContentFailed: live.AlertContentFailed ?? a.ContentFailed,
                CompliancePctFloor: live.AlertCompliance == false ? null : floor));
    }

    /// <summary>The stored values as the dashboard import sees them; null is a value that was never set.</summary>
    public static LiveSettingValues Values(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var live = settings.SiteHygieneLive;
        return new LiveSettingValues(
            SqlServer: string.IsNullOrWhiteSpace(settings.DiscoverySqlServer) ? null : settings.DiscoverySqlServer.Trim(),
            TrustSqlCertificate: live?.TrustSqlCertificate,
            RefreshMinutes: live?.RefreshMinutes,
            InactiveDays: live?.InactiveDays,
            AlertsEnabled: live?.AlertsEnabled,
            CompliancePctFloor: live?.CompliancePctFloor);
    }

    /// <summary>Stores each non-null value of <paramref name="values"/> whose stored value is still empty. False when nothing was written.</summary>
    public static bool Fill(SettingsService service, LiveSettingValues values)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(values);
        lock (FileLock)
        {
            try
            {
                var stored = service.Load();
                if (service.IsFileUnreadable) return false;
                FillValues(stored, values);
                service.Save(stored);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Sets each non-null value of <paramref name="values"/> whose value in <paramref name="stored"/> is still empty.</summary>
    public static void FillValues(AppSettings stored, LiveSettingValues values)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(values);
        var live = stored.SiteHygieneLive ?? new SiteHygieneLiveSettings();
        if (values.SqlServer is { } server && string.IsNullOrWhiteSpace(stored.DiscoverySqlServer)) stored.DiscoverySqlServer = server.Trim();
        live.TrustSqlCertificate ??= values.TrustSqlCertificate;
        live.RefreshMinutes ??= values.RefreshMinutes;
        live.InactiveDays ??= values.InactiveDays;
        live.AlertsEnabled ??= values.AlertsEnabled;
        live.CompliancePctFloor ??= values.CompliancePctFloor;
        stored.SiteHygieneLive = live;
    }

    /// <summary>Stores the Live and Alerts options of the page. False when nothing was written; an unreadable settings file is never replaced.</summary>
    public static bool Save(SettingsService service, SiteHygieneLiveSettings live, string? sqlServer = null, string? sqlDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(live);
        lock (FileLock)
        {
            try
            {
                var stored = service.Load();
                if (service.IsFileUnreadable) return false;
                stored.SiteHygieneLive = live;
                if (sqlServer is not null) stored.DiscoverySqlServer = sqlServer.Trim();
                if (sqlDatabase is not null) stored.DiscoverySqlDatabase = sqlDatabase.Trim();
                service.Save(stored);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }
}
