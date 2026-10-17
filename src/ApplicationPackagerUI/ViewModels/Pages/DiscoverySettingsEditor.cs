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
        DismissedCount = (settings.DiscoveryDismissed ?? []).Count(id => !string.IsNullOrWhiteSpace(id));
        ClearPending = false;
    }

    /// <summary>Writes the fields; the enabled list and, unless a clear is pending, the dismissed list stay as stored.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.DiscoverySqlServer = (SqlServer ?? "").Trim();
        settings.DiscoverySqlDatabase = (SqlDatabase ?? "").Trim();
        if (ClearPending) settings.DiscoveryDismissed = [];
        SqlServer = settings.DiscoverySqlServer;
        SqlDatabase = settings.DiscoverySqlDatabase;
        DismissedCount = (settings.DiscoveryDismissed ?? []).Length;
        ClearPending = false;
    }

    [RelayCommand(CanExecute = nameof(CanClearDismissed))]
    private void ClearDismissed() => ClearPending = true;
}
