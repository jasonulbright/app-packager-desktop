using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Deployment Helper page: the tabs Deploy, Rings, History and Options over one shared state. A
/// tab reads its data on its first visit; leaving the page releases the data of every tab. The
/// constructor reads nothing.
/// </summary>
public sealed partial class DeploymentHelperViewModel : ObservableObject
{
    public const int DeployTab = 0;
    public const int RingsTab = 1;
    public const int HistoryTab = 2;
    public const int OptionsTab = 3;

    // The settings file keeps the tab numbers of the three-tab page, so a file written before the Rings tab opens the same tab.
    private static readonly int[] StoredTabs = [0, 3, 1, 2];

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _preferencesLoaded;
    private bool _applying;
    private Task _preferencesLoad = Task.CompletedTask;
    private DeploymentHelperSettings? _stored;
    private string _auditLogPath = "";
    private string _ringsFolder = "";
    private string _runsFolder = "";
    private bool _active;

    public DeploymentHelperViewModel(DeploymentPageServices services)
    {
        Shared = new DeploymentShared(services);
        Deploy = new DeploymentFormViewModel(Shared);
        Rings = new DeploymentRingsViewModel(Shared);
        History = new DeploymentHistoryViewModel(Shared);
        Options = new DeploymentOptionsViewModel(Shared)
        {
            SavedAuditPath = () => _auditLogPath,
            SaveAuditPath = async path =>
            {
                var previous = _auditLogPath;
                _auditLogPath = path;
                if (await SaveAsync()) return true;
                _auditLogPath = previous;
                return false;
            },
            RingFolders = new RingFolderOptionsViewModel(Shared)
            {
                Saved = () => (_ringsFolder, _runsFolder),
                Save = async (rings, runs) =>
                {
                    var previous = (_ringsFolder, _runsFolder);
                    (_ringsFolder, _runsFolder) = (rings, runs);
                    if (await SaveAsync()) return true;
                    (_ringsFolder, _runsFolder) = previous;
                    return false;
                },
                FoldersChanged = () => Rings.Release()
            }
        };
        Shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeploymentShared.HasResultPanel)) OnPropertyChanged(nameof(ShowsResultPanel));
        };
        Deploy.ChoicesChanged += (_, _) => SaveLater();
        Rings.ChoicesChanged += (_, _) => SaveLater();
        Options.TemplatesChanged += (_, _) =>
        {
            if (Deploy.IsLoaded) _ = Deploy.ReloadTemplatesAsync();
        };
        Shared.OnAfterWrite(Deploy.RefreshAfterDeployAsync);
        Shared.OnAfterWrite(History.RefreshAfterWriteAsync);
    }

    public DeploymentShared Shared { get; }
    public DeploymentFormViewModel Deploy { get; }
    public DeploymentRingsViewModel Rings { get; }
    public DeploymentHistoryViewModel History { get; }
    public DeploymentOptionsViewModel Options { get; }

    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>
    /// The result of the last change sits under the Deploy, Rings and History tabs, which make changes. The Options tab
    /// makes none and its Templates list needs the room, so the panel is not shown there.
    /// </summary>
    public bool ShowsResultPanel => Shared.HasResultPanel && SelectedTabIndex != OptionsTab;

    public DeploymentTabViewModel CurrentTab => SelectedTabIndex switch
    {
        RingsTab => Rings,
        HistoryTab => History,
        OptionsTab => Options,
        _ => Deploy
    };

    /// <summary>The number the settings file keeps for a tab.</summary>
    public static int StoredTab(int tab) => StoredTabs[Math.Clamp(tab, DeployTab, OptionsTab)];

    /// <summary>The tab of a number the settings file keeps; an unknown number opens Deploy.</summary>
    public static int TabOf(int stored) => Array.IndexOf(StoredTabs, stored) is var tab and >= 0 ? tab : DeployTab;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentTab));
        OnPropertyChanged(nameof(ShowsResultPanel));
        SaveLater();
        if (_active && !_applying) _ = CurrentTab.EnsureLoadedAsync();
    }

    /// <summary>Reads the saved choices on the first visit, then the data of the current tab.</summary>
    public async Task OnNavigatedToAsync()
    {
        _active = true;
        Shared.IsActive = true;
        Shared.ConnectionProblem();
        if (!_preferencesLoaded)
        {
            _preferencesLoaded = true;
            _preferencesLoad = ApplyPreferencesAsync();
        }
        await _preferencesLoad;
        if (_active) await CurrentTab.EnsureLoadedAsync();
    }

    /// <summary>Releases the data of every tab; a read or a run that ends later is discarded.</summary>
    public async Task OnNavigatedFromAsync()
    {
        _active = false;
        Shared.Release();
        Deploy.Release();
        Rings.Release();
        History.Release();
        Options.Release();
        await SaveAsync();
    }

    private async Task ApplyPreferencesAsync()
    {
        DeploymentHelperSettings? saved;
        try { saved = await Task.Run(Shared.Services.LoadPreferences); }
        catch (Exception ex)
        {
            saved = null;
            Shared.Services.Log.Warning(DeploymentShared.LogArea, $"The page choices could not be read ({ex.GetType().Name}).");
        }
        if (saved is not null)
        {
            _applying = true;
            try
            {
                SelectedTabIndex = TabOf(saved.SelectedTab);
                Deploy.ApplyChoices(saved.ObjectType, saved.TemplateId);
                Rings.ApplyChoices(saved.RingPlanName);
                _auditLogPath = saved.AuditLogPath ?? "";
                _ringsFolder = saved.RingsFolder ?? "";
                _runsFolder = saved.RunsFolder ?? "";
            }
            finally { _applying = false; }
        }
        _stored = Current();
    }

    private DeploymentHelperSettings Current() => new()
    {
        SelectedTab = StoredTab(SelectedTabIndex),
        ObjectType = Deploy.TypeIndex,
        TemplateId = Deploy.TemplateId ?? "",
        AuditLogPath = _auditLogPath,
        RingPlanName = Rings.PlanName ?? "",
        RingsFolder = _ringsFolder,
        RunsFolder = _runsFolder
    };

    private static bool Same(DeploymentHelperSettings? a, DeploymentHelperSettings b) =>
        a is not null && a.SelectedTab == b.SelectedTab && a.ObjectType == b.ObjectType
        && string.Equals(a.TemplateId, b.TemplateId, StringComparison.Ordinal) && string.Equals(a.AuditLogPath, b.AuditLogPath, StringComparison.Ordinal)
        && string.Equals(a.RingPlanName, b.RingPlanName, StringComparison.Ordinal) && string.Equals(a.RingsFolder, b.RingsFolder, StringComparison.Ordinal)
        && string.Equals(a.RunsFolder, b.RunsFolder, StringComparison.Ordinal);

    private void SaveLater()
    {
        if (_applying || !_preferencesLoaded) return;
        _ = SaveAsync();
    }

    /// <summary>
    /// Saves run one at a time and each reads the choices when its turn comes, so the last write
    /// holds the newest. False when nothing was written; the stored choices stay as they were, so
    /// the next save tries again.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        await _preferencesLoad;
        await _saveLock.WaitAsync();
        try
        {
            var choices = Current();
            if (Same(_stored, choices)) return true;
            bool saved;
            try { saved = await Task.Run(() => Shared.Services.SavePreferences(choices)); }
            catch (Exception ex)
            {
                saved = false;
                Shared.Services.Log.Warning(DeploymentShared.LogArea, $"The page choices could not be saved ({ex.GetType().Name}).");
            }
            if (saved) _stored = choices;
            else Shared.Services.Log.Warning(DeploymentShared.LogArea, "The page choices were not saved.");
            return saved;
        }
        finally { _saveLock.Release(); }
    }
}
