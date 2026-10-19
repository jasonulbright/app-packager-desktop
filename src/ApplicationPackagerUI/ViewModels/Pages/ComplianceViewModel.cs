using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Compliance section of the Collection and Compliance page: the tabs Catalog, Live CI/CB,
/// Editor and Auditing over one shared state. A tab reads its data on its first visit while the
/// section shows; leaving the page releases the data of every tab.
/// </summary>
public sealed partial class ComplianceViewModel : ObservableObject
{
    public const int CatalogTab = 0;
    public const int LiveTab = 1;
    public const int EditorTab = 2;
    public const int AuditingTab = 3;

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _preferencesLoaded;
    private bool _applying;
    private Task _preferencesLoad = Task.CompletedTask;
    private ComplianceSettings? _stored;
    private bool _active;
    private bool _shown;

    public ComplianceViewModel(CompliancePageServices services)
    {
        Shared = new ComplianceShared(services);
        Catalog = new ComplianceCatalogViewModel(Shared);
        Live = new ComplianceLiveViewModel(Shared);
        Editor = new ComplianceEditorViewModel(Shared);
        Auditing = new ComplianceAuditingViewModel(Shared);
        Catalog.ChoicesChanged += (_, _) => SaveLater();
        Live.ChoicesChanged += (_, _) => SaveLater();
        Auditing.ChoicesChanged += (_, _) => SaveLater();
        Live.EditSettingsRequested += (_, item) =>
        {
            Editor.Select(item);
            SelectedTabIndex = EditorTab;
        };
        Shared.OnAfterWrite(Catalog.RefreshAfterWriteAsync);
        Shared.OnAfterWrite(Live.RefreshAfterWriteAsync);
        Shared.OnAfterWrite(async () =>
        {
            await Editor.RefreshAfterWriteAsync();
            await Editor.RefreshSettingsAsync();
        });
        Shared.OnAfterWrite(Auditing.RefreshAfterWriteAsync);
    }

    public ComplianceShared Shared { get; }
    public ComplianceCatalogViewModel Catalog { get; }
    public ComplianceLiveViewModel Live { get; }
    public ComplianceEditorViewModel Editor { get; }
    public ComplianceAuditingViewModel Auditing { get; }

    [ObservableProperty] private int _selectedTabIndex;

    public ComplianceTabViewModel CurrentTab => SelectedTabIndex switch
    {
        LiveTab => Live,
        EditorTab => Editor,
        AuditingTab => Auditing,
        _ => Catalog
    };

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentTab));
        SaveLater();
        if (_active && _shown && !_applying) _ = CurrentTab.EnsureLoadedAsync();
    }

    /// <summary>Reads the saved choices on the first visit; with the section shown, the current tab reads its data.</summary>
    public async Task OnNavigatedToAsync(bool shown)
    {
        _active = true;
        Shared.IsActive = true;
        if (!_preferencesLoaded)
        {
            _preferencesLoaded = true;
            _preferencesLoad = ApplyPreferencesAsync();
        }
        await _preferencesLoad;
        if (shown) await ShowAsync();
    }

    /// <summary>The section became visible: the current tab reads its data when it has none.</summary>
    public async Task ShowAsync()
    {
        _shown = true;
        if (!_active) return;
        await _preferencesLoad;
        if (_active) await CurrentTab.EnsureLoadedAsync();
    }

    public void Hide() => _shown = false;

    /// <summary>Releases the data of every tab; a read or a run that ends later is discarded.</summary>
    public async Task OnNavigatedFromAsync()
    {
        _active = false;
        Shared.Release();
        Catalog.Release();
        Live.Release();
        Editor.Release();
        Auditing.Release();
        await SaveAsync();
    }

    private async Task ApplyPreferencesAsync()
    {
        ComplianceSettings? saved;
        try { saved = await Task.Run(Shared.Services.LoadPreferences); }
        catch (Exception ex)
        {
            saved = null;
            Shared.Services.Log.Warning(ComplianceShared.LogArea, $"The section choices could not be read ({ex.GetType().Name}).");
        }
        if (saved is null) return;
        _applying = true;
        try
        {
            SelectedTabIndex = saved.SelectedTab is >= CatalogTab and <= AuditingTab ? saved.SelectedTab : CatalogTab;
            Catalog.ApplyChoices(saved);
            Live.ApplyChoices(saved);
            Auditing.ApplyChoices(saved);
        }
        finally { _applying = false; }
        _stored = Current();
    }

    private ComplianceSettings Current()
    {
        var choices = new ComplianceSettings { SelectedTab = SelectedTabIndex };
        Catalog.StoreChoices(choices);
        Live.StoreChoices(choices);
        Auditing.StoreChoices(choices);
        return choices;
    }

    private static bool Same(ComplianceSettings? a, ComplianceSettings b) =>
        a is not null && System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    private void SaveLater()
    {
        if (_applying || !_preferencesLoaded) return;
        _ = SaveAsync();
    }

    /// <summary>Saves run one at a time and each reads the choices when its turn comes, so the last write holds the newest.</summary>
    public async Task SaveAsync()
    {
        await _preferencesLoad;
        await _saveLock.WaitAsync();
        try
        {
            var choices = Current();
            if (Same(_stored, choices)) return;
            _stored = choices;
            try { await Task.Run(() => Shared.Services.SavePreferences(choices)); }
            catch (Exception ex)
            {
                _stored = null;
                Shared.Services.Log.Warning(ComplianceShared.LogArea, $"The section choices could not be saved ({ex.GetType().Name}).");
            }
        }
        finally { _saveLock.Release(); }
    }
}
