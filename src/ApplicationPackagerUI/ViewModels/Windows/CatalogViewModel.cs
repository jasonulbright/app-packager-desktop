using System.ComponentModel;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>What the Catalog window reads and writes; the window logic depends on nothing else.</summary>
/// <param name="LoadPackagers">Every application, hidden ones included; null leaves the list unchanged.</param>
public sealed record CatalogServices(
    Func<AppSettings> LoadSettings,
    Action<AppSettings> SaveSettings,
    Func<Task<IReadOnlyList<PackagerInfo>?>> LoadPackagers,
    Func<Task> RefreshGrid,
    Action<string> ShowSaved,
    Action<string> Log)
{
    /// <summary>True while the settings file on disk failed to load: a save then stores nothing.</summary>
    public Func<bool>? IsSettingsUnreadable { get; init; }
}

/// <summary>Shows and hides applications in the App Packager grid. Save writes the hidden list and reloads the grid.</summary>
public partial class CatalogViewModel : ObservableObject
{
    public const string Title = "Catalog";

    private readonly CatalogServices _services;
    private readonly object _reloadLock = new();
    private Task _reload = Task.CompletedTask;
    private int _reloadRequest;

    [ObservableProperty] private ObservableCollection<VendorGroup> _vendorGroups = [];
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _matchCount;

    public CatalogViewModel(SettingsService settingsService, PackagerDiscoveryService discoveryService, ISnackbarService snackbarService, PackagesViewModel packagesViewModel)
        : this(new CatalogServices(
            LoadSettings: settingsService.Load,
            SaveSettings: settingsService.Save,
            LoadPackagers: LoadPackagersFrom(discoveryService),
            RefreshGrid: () => packagesViewModel.LoadPackagersCommand.ExecuteAsync(null),
            ShowSaved: message => snackbarService.Show(Title, message,
                Wpf.Ui.Controls.ControlAppearance.Success,
                new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(2)),
            Log: packagesViewModel.AppendLog)
        { IsSettingsUnreadable = () => settingsService.IsFileUnreadable })
    {
        packagesViewModel.HiddenApplicationsChanged += (_, _) => ReloadAfterSettingsChange();
    }

    public CatalogViewModel(CatalogServices services)
    {
        _services = services;
        ReloadAfterSettingsChange();
    }

    /// <summary>The hidden list the settings hold; the grid shows this state until Save.</summary>
    private HashSet<string> _savedHidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the window shows a hidden list that differs from the saved one.</summary>
    [ObservableProperty] private bool _hasUnsavedChanges;

    /// <summary>The line under the window title.</summary>
    public string CountText
    {
        get
        {
            var state = HasUnsavedChanges
                ? $"{VisibleCount} of {TotalCount} applications are selected to show. The App Packager grid changes when you select Save."
                : $"{VisibleCount} of {TotalCount} applications are visible in the App Packager grid.";
            return SearchText.Trim().Length == 0 ? state : $"{state} {MatchCount} match the search.";
        }
    }

    partial void OnHasUnsavedChangesChanged(bool value) => OnPropertyChanged(nameof(CountText));
    partial void OnVisibleCountChanged(int value) => OnPropertyChanged(nameof(CountText));
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(CountText));
    partial void OnMatchCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    partial void OnSearchTextChanged(string value)
    {
        ApplySearch();
        OnPropertyChanged(nameof(CountText));
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
            log("Catalog: the list could not be reloaded. Open Catalog again to retry.");
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

    /// <summary>Completes when the last queued reload has ended.</summary>
    public async Task WaitForReloadAsync()
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
        _savedHidden = hidden;

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
        ApplySearch();
        UpdateCounts();
    }

    /// <summary>
    /// An application matches when its name or its vendor contains the search text. A vendor
    /// group shows while one of its applications matches.
    /// </summary>
    private void ApplySearch()
    {
        var text = SearchText.Trim();
        var matches = 0;
        foreach (var group in VendorGroups)
        {
            var vendorMatches = text.Length == 0 || group.Vendor.Contains(text, StringComparison.OrdinalIgnoreCase);
            foreach (var app in group.Apps)
            {
                app.MatchesSearch = vendorMatches || app.Application.Contains(text, StringComparison.OrdinalIgnoreCase);
                if (app.MatchesSearch) matches++;
            }
            group.MatchesSearch = group.Apps.Any(a => a.MatchesSearch);
        }
        MatchCount = matches;
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

    /// <summary>The vendor checkbox changes the applications of the vendor that match the search, as Show all and Hide all do.</summary>
    public void SetVendor(VendorGroup group, bool visible)
    {
        foreach (var app in group.Apps.Where(a => a.MatchesSearch))
            app.IsVisible = visible;
        group.UpdateGroupState();
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var all = VendorGroups.SelectMany(g => g.Apps).ToList();
        TotalCount = all.Count;
        VisibleCount = all.Count(a => a.IsVisible);
        HasUnsavedChanges = all.Any(a => a.IsVisible == _savedHidden.Contains(a.Uid));
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

    /// <summary>Show all and Hide all change the applications that match the search, every application when the search is empty.</summary>
    private void SetMatching(bool visible)
    {
        foreach (var group in VendorGroups)
        {
            foreach (var app in group.Apps.Where(a => a.MatchesSearch))
                app.IsVisible = visible;
            group.UpdateGroupState();
        }
        UpdateCounts();
    }

    [RelayCommand]
    private void ShowAll() => SetMatching(visible: true);

    [RelayCommand]
    private void HideAll() => SetMatching(visible: false);

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
        if (_services.IsSettingsUnreadable?.Invoke() == true)
        {
            // The automatic save writes nothing over a file that failed to load, so nothing was stored.
            _services.ShowSaved("Not saved. The settings file could not be read. Open Settings, select Save, then change the Catalog again.");
            return;
        }
        _savedHidden = new HashSet<string>(s.HiddenApplications, StringComparer.OrdinalIgnoreCase);
        UpdateCounts();

        await _services.RefreshGrid();

        _services.ShowSaved($"Saved. {VisibleCount} of {TotalCount} visible.");
    }
}
