using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Discover unmanaged software fields of the Settings page: the SQL overrides and the
/// dismissed list. Written to the store only by Save Settings.
/// </summary>
public sealed partial class DiscoverySettingsEditor : ObservableObject
{
    [ObservableProperty] private string _sqlServer = "";
    [ObservableProperty] private string _sqlDatabase = "";
    [ObservableProperty] private bool _encryptConnection;
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private int _dismissedCount;
    [ObservableProperty] private bool _clearPending;

    public string DismissedSummary => ClearPending ? "0 app(s) after Save" : $"{DismissedCount} app(s)";

    public bool CanClearDismissed => DismissedCount > 0 && !ClearPending;

    partial void OnDismissedCountChanged(int value) => NotifySummary();
    partial void OnClearPendingChanged(bool value) => NotifySummary();

    private void NotifySummary()
    {
        OnPropertyChanged(nameof(DismissedSummary));
        OnPropertyChanged(nameof(CanClearDismissed));
        ClearDismissedCommand.NotifyCanExecuteChanged();
    }

    public void Load(AppSettings settings)
    {
        SqlServer = settings.DiscoverySqlServer ?? "";
        SqlDatabase = settings.DiscoverySqlDatabase ?? "";
        EncryptConnection = settings.SiteHygieneLive?.EncryptSql ?? false;
        TrustServerCertificate = settings.SiteHygieneLive?.TrustSqlCertificate ?? false;
        DismissedCount = (settings.DiscoveryDismissed ?? []).Count(id => !string.IsNullOrWhiteSpace(id));
        ClearPending = false;
    }

    /// <summary>The settings as the page shows them now, for Test connection: nothing is stored.</summary>
    public AppSettings Snapshot(AppSettings basis)
    {
        var copy = new AppSettings { SiteCode = basis.SiteCode, ProviderMachine = basis.ProviderMachine, Systems = basis.Systems,
            SiteHygieneLive = new SiteHygieneLiveSettings { EncryptSql = EncryptConnection, TrustSqlCertificate = TrustServerCertificate } };
        copy.DiscoverySqlServer = (SqlServer ?? "").Trim();
        copy.DiscoverySqlDatabase = (SqlDatabase ?? "").Trim();
        return copy;
    }

    /// <summary>Writes the fields; the enabled list and, unless a clear is pending, the dismissed list stay as stored.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.DiscoverySqlServer = (SqlServer ?? "").Trim();
        settings.DiscoverySqlDatabase = (SqlDatabase ?? "").Trim();
        // The certificate option is the one of Site Hygiene, Live: one stored value per site. Unset stays unset until the user changes it.
        var live = settings.SiteHygieneLive;
        if (live is not null || EncryptConnection || TrustServerCertificate)
        {
            live ??= new SiteHygieneLiveSettings();
            if (EncryptConnection || live.EncryptSql is not null) live.EncryptSql = EncryptConnection;
            if (TrustServerCertificate || live.TrustSqlCertificate is not null) live.TrustSqlCertificate = TrustServerCertificate;
            settings.SiteHygieneLive = live;
        }
        if (ClearPending) settings.DiscoveryDismissed = [];
        SqlServer = settings.DiscoverySqlServer;
        SqlDatabase = settings.DiscoverySqlDatabase;
        DismissedCount = (settings.DiscoveryDismissed ?? []).Length;
        ClearPending = false;
    }

    [RelayCommand(CanExecute = nameof(CanClearDismissed))]
    private void ClearDismissed() => ClearPending = true;
}
