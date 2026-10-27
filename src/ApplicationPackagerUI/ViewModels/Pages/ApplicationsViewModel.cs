using System.ComponentModel;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>What the Applications page reads and writes; the page logic depends on nothing else.</summary>
/// <param name="LoadPackagers">Every application, hidden ones included; null leaves the page unchanged.</param>
public sealed record ApplicationsPageServices(
    Func<AppSettings> LoadSettings,
    Action<AppSettings> SaveSettings,
    Func<Task<IReadOnlyList<PackagerInfo>?>> LoadPackagers,
    Func<Task> RefreshGrid,
    Action<string> ShowSaved,
    Action<string> Log);

public partial class ApplicationsViewModel : ObservableObject
{
    private readonly ApplicationsPageServices _services;
    private readonly object _reloadLock = new();
    private Task _reload = Task.CompletedTask;
    private int _reloadRequest;

    [ObservableProperty] private ObservableCollection<VendorGroup> _vendorGroups = [];
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _visibleCount;

    public ApplicationsViewModel(SettingsService settingsService, PackagerDiscoveryService discoveryService, ISnackbarService snackbarService, PackagesViewModel packagesViewModel)
        : this(new ApplicationsPageServices(
            settingsService.Load,
            settingsService.Save,
            LoadPackagersFrom(discoveryService),
            () => packagesViewModel.LoadPackagersCommand.ExecuteAsync(null),
            message => snackbarService.Show("Catalog", message,
                Wpf.Ui.Controls.ControlAppearance.Success,
                new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(2)),
            packagesViewModel.AppendLog))
    {
        packagesViewModel.HiddenApplicationsChanged += (_, _) => ReloadAfterSettingsChange();
    }

    public ApplicationsViewModel(ApplicationsPageServices services)
    {
        _services = services;
        ReloadAfterSettingsChange();
    }

    /// <summary>Every application the discovery service finds, hidden ones included.</summary>
    public static Func<Task<IReadOnlyList<PackagerInfo>?>> LoadPackagersFrom(PackagerDiscoveryService discoveryService) =>
        async () => await discoveryService.DiscoverAllAsync();

    /// <summary>Runs a reload nobody awaits; a failure becomes one log line without exception text.</summary>
    public static async Task ReloadLoggedAsync(Func<Task> load, Action<string> log)
    {
        try
        {
            await load();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log("Catalog page: the list could not be reloaded. Open the page again to retry.");
        }
    }

    /// <summary>
    /// Queues a reload behind the pending one. A save writes every row's state, so it waits for
    /// the last queued reload: rows read before the stored lists changed would undo that change.
    /// </summary>
    public void ReloadAfterSettingsChange()
    {
        lock (_reloadLock)
        {
            var request = ++_reloadRequest;
            _reload = ReloadAfterAsync(_reload, request);
        }
    }

    private async Task ReloadAfterAsync(Task previous, int request)
    {
        await IgnoreFailureAsync(previous);
        if (request != Volatile.Read(ref _reloadRequest)) return;
        await ReloadLoggedAsync(LoadAsync, _services.Log);
    }

    private async Task WaitForReloadAsync()
    {
        while (true)
        {
            Task pending;
            lock (_reloadLock) pending = _reload;
            await IgnoreFailureAsync(pending);
            lock (_reloadLock)
            {
                if (ReferenceEquals(pending, _reload)) return;
            }
        }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task; }
        catch (Exception) { }
    }

    private async Task LoadAsync()
    {
        var settings = _services.LoadSettings();
        var hidden = new HashSet<string>(settings.HiddenApplications, StringComparer.OrdinalIgnoreCase);

        var allPackagers = await _services.LoadPackagers();
        if (allPackagers is null) return;

        var groups = allPackagers
            .GroupBy(p => p.Vendor, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var group = new VendorGroup
                {
                    Vendor = g.Key,
                    Apps = new ObservableCollection<AppVisibilityItem>(
                        g.OrderBy(a => a.Application, StringComparer.OrdinalIgnoreCase)
                         .Select(a =>
                         {
                             var item = new AppVisibilityItem
                             {
                                 Uid = a.Uid,
                                 Application = a.Application,
                                 Script = a.Script,
                                 IsJsonDefined = a.IsJsonDefined,
                                 IsVisible = !hidden.Contains(a.Uid)
                             };
                             item.PropertyChanged += OnAppVisibilityChanged;
                             return item;
                         }))
                };
                group.UpdateGroupState();
                return group;
            })
            .ToList();

        VendorGroups = new ObservableCollection<VendorGroup>(groups);
        UpdateCounts();
    }

    private void OnAppVisibilityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppVisibilityItem.IsVisible)) return;

        if (sender is AppVisibilityItem item)
        {
            foreach (var group in VendorGroups)
            {
                if (group.Apps.Contains(item))
                {
                    group.UpdateGroupState();
                    break;
                }
            }
        }
        UpdateCounts();
    }

    public void UpdateCountsPublic() => UpdateCounts();

    private void UpdateCounts()
    {
        var all = VendorGroups.SelectMany(g => g.Apps).ToList();
        TotalCount = all.Count;
        VisibleCount = all.Count(a => a.IsVisible);
    }

    [RelayCommand]
    private void ToggleVendorGroup(VendorGroup? group)
    {
        if (group is null) return;
        var newState = group.IsAllVisible != true;
        foreach (var app in group.Apps)
            app.IsVisible = newState;
        group.UpdateGroupState();
        UpdateCounts();
    }

    [RelayCommand]
    private void ShowAll()
    {
        foreach (var group in VendorGroups)
        {
            foreach (var app in group.Apps)
                app.IsVisible = true;
            group.UpdateGroupState();
        }
        UpdateCounts();
    }

    [RelayCommand]
    private void HideAll()
    {
        foreach (var group in VendorGroups)
        {
            foreach (var app in group.Apps)
                app.IsVisible = false;
            group.UpdateGroupState();
        }
        UpdateCounts();
    }

    [RelayCommand]
    private async Task Save()
    {
        await WaitForReloadAsync();

        var s = _services.LoadSettings();
        s.HiddenApplications = VendorGroups
            .SelectMany(g => g.Apps)
            .Where(a => !a.IsVisible)
            .Select(a => a.Uid)
            .ToArray();
        _services.SaveSettings(s);

        await _services.RefreshGrid();

        _services.ShowSaved($"Saved. {VisibleCount} of {TotalCount} visible.");
    }
}
