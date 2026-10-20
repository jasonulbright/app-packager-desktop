using ApplicationPackagerEngine.Services.Logging;
using ApplicationPackagerUI.Models;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// The Deployment Helper page: the tabs Deploy, History and Options over one shared state. A tab
/// reads its data on its first visit; leaving the page releases the data of every tab. The
/// constructor reads nothing.
/// </summary>
public sealed partial class DeploymentHelperViewModel : ObservableObject
{
    public const int DeployTab = 0;
    public const int HistoryTab = 1;
    public const int OptionsTab = 2;
    public const string RingStarted = "Deployment Helper (ring deployments) was started from the AppPackager Suite.";

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _preferencesLoaded;
    private bool _applying;
    private Task _preferencesLoad = Task.CompletedTask;
    private DeploymentHelperSettings? _stored;
    private string _auditLogPath = "";
    private bool _active;

    public DeploymentHelperViewModel(DeploymentPageServices services)
    {
        Shared = new DeploymentShared(services);
        Deploy = new DeploymentFormViewModel(Shared);
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
            }
        };
        Deploy.ChoicesChanged += (_, _) => SaveLater();
        Options.TemplatesChanged += (_, _) =>
        {
            if (Deploy.IsLoaded) _ = Deploy.ReloadTemplatesAsync();
        };
        Shared.OnAfterWrite(Deploy.RefreshAfterDeployAsync);
        Shared.OnAfterWrite(History.RefreshAfterWriteAsync);
    }

    public DeploymentShared Shared { get; }
    public DeploymentFormViewModel Deploy { get; }
    public DeploymentHistoryViewModel History { get; }
    public DeploymentOptionsViewModel Options { get; }

    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRingToolCommand))]
    private bool _isStartingRingTool;

    public DeploymentTabViewModel CurrentTab => SelectedTabIndex switch
    {
        HistoryTab => History,
        OptionsTab => Options,
        _ => Deploy
    };

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentTab));
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
                SelectedTabIndex = saved.SelectedTab is >= DeployTab and <= OptionsTab ? saved.SelectedTab : DeployTab;
                Deploy.ApplyChoices(saved.ObjectType, saved.TemplateId);
                _auditLogPath = saved.AuditLogPath ?? "";
            }
            finally { _applying = false; }
        }
        _stored = Current();
    }

    private DeploymentHelperSettings Current() => new()
    {
        SelectedTab = SelectedTabIndex,
        ObjectType = Deploy.TypeIndex,
        TemplateId = Deploy.TemplateId ?? "",
        AuditLogPath = _auditLogPath
    };

    private static bool Same(DeploymentHelperSettings? a, DeploymentHelperSettings b) =>
        a is not null && a.SelectedTab == b.SelectedTab && a.ObjectType == b.ObjectType
        && string.Equals(a.TemplateId, b.TemplateId, StringComparison.Ordinal) && string.Equals(a.AuditLogPath, b.AuditLogPath, StringComparison.Ordinal);

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

    private bool CanStartRingTool() => !IsStartingRingTool;

    /// <summary>Starts the suite's Deployment Helper for ring deployments; a second click while it starts does nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanStartRingTool))]
    private async Task StartRingTool()
    {
        if (IsStartingRingTool) return;
        IsStartingRingTool = true;
        try
        {
            string? problem;
            try { problem = await Shared.Services.StartRingTool(); }
            catch (Exception ex)
            {
                problem = "Deployment Helper (ring deployments) could not start. The log has the details.";
                Shared.Services.Log.Warning(DeploymentShared.LogArea, $"The ring deployment tool could not start ({ex.GetType().Name}).");
            }
            Shared.StatusMessage = problem ?? RingStarted;
        }
        finally { IsStartingRingTool = false; }
    }
}
