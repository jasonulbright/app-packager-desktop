using System.ComponentModel;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class ApplicationsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly PackagerDiscoveryService _discoveryService;
    private readonly ISnackbarService _snackbarService;
    private readonly PackagesViewModel _packagesViewModel;

    [ObservableProperty] private ObservableCollection<VendorGroup> _vendorGroups = [];
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _visibleCount;

    public ApplicationsViewModel(SettingsService settingsService, PackagerDiscoveryService discoveryService, ISnackbarService snackbarService, PackagesViewModel packagesViewModel)
    {
        _settingsService = settingsService;
        _discoveryService = discoveryService;
        _snackbarService = snackbarService;
        _packagesViewModel = packagesViewModel;
        // A save from this page writes every row's state: rows read before a discovery changed
        // the hidden list would hide the enabled applications again.
        packagesViewModel.HiddenApplicationsChanged += (_, _) => _ = LoadAsync();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var settings = _settingsService.Load();
        var hidden = new HashSet<string>(settings.HiddenApplications, StringComparer.OrdinalIgnoreCase);

        var root = _discoveryService.ResolvePackagersRoot();
        if (root is null) return;

        var allPackagers = await _discoveryService.DiscoverAllAsync();

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
        var s = _settingsService.Load();
        s.HiddenApplications = VendorGroups
            .SelectMany(g => g.Apps)
            .Where(a => !a.IsVisible)
            .Select(a => a.Uid)
            .ToArray();
        _settingsService.Save(s);

        // Refresh the Packages grid to reflect visibility changes
        await _packagesViewModel.LoadPackagersCommand.ExecuteAsync(null);

        _snackbarService.Show("Applications", $"Saved. {VisibleCount} of {TotalCount} visible.",
            Wpf.Ui.Controls.ControlAppearance.Success,
            new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(2));
    }
}
