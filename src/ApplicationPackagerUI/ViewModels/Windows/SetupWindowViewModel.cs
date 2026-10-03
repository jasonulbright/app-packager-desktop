using ApplicationPackagerEngine.Models;
using ApplicationPackagerUI.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// First-run setup. Step one picks the deployment target, step two shows only the fields that
/// target needs. Save writes through the same settings persistence the Settings page uses, so it
/// applies without a restart.
/// </summary>
public partial class SetupWindowViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly PackagesViewModel _packagesViewModel;
    private readonly SettingsViewModel _settingsViewModel;

    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private string _deploymentTarget = DeploymentTargets.Mecm;
    [ObservableProperty] private bool _dontShowAgain;

    // MECM
    [ObservableProperty] private string _siteCode = "";
    [ObservableProperty] private string _providerMachine = "";
    [ObservableProperty] private string _fileShareRoot = "";
    [ObservableProperty] private string _downloadRoot = "";

    // Intune
    [ObservableProperty] private string _intuneTenantId = "";
    [ObservableProperty] private string _intuneClientId = "";

    /// <summary>Entry only. Empty on save keeps the stored secret; a typed value replaces it.</summary>
    [ObservableProperty] private string _intuneClientSecret = "";

    [ObservableProperty] private bool _hasStoredIntuneSecret;

    public bool ShowMecmFields => !DeploymentTargets.SkipsMecm(DeploymentTarget);
    public bool ShowIntuneFields => DeploymentTargets.PublishesToIntune(DeploymentTarget);

    public string IntuneSecretHint => HasStoredIntuneSecret
        ? "Stored — leave empty to keep it"
        : "Not set";

    /// <summary>Raised when the window should close; true when settings were saved.</summary>
    public event EventHandler<bool>? CloseRequested;

    public SetupWindowViewModel(
        SettingsService settingsService,
        PackagesViewModel packagesViewModel,
        SettingsViewModel settingsViewModel)
    {
        _settingsService = settingsService;
        _packagesViewModel = packagesViewModel;
        _settingsViewModel = settingsViewModel;

        var s = _settingsService.Load();
        DeploymentTarget = DeploymentTargets.Normalize(s.Intune.DeploymentTarget);
        SiteCode = s.SiteCode;
        ProviderMachine = s.ProviderMachine;
        FileShareRoot = s.FileShareRoot;
        DownloadRoot = s.DownloadRoot;
        IntuneTenantId = s.Intune.TenantId;
        IntuneClientId = s.Intune.ClientId;
        HasStoredIntuneSecret = !string.IsNullOrWhiteSpace(s.Intune.ClientSecretProtected);
    }

    partial void OnDeploymentTargetChanged(string value)
    {
        OnPropertyChanged(nameof(ShowMecmFields));
        OnPropertyChanged(nameof(ShowIntuneFields));
    }

    partial void OnHasStoredIntuneSecretChanged(bool value) => OnPropertyChanged(nameof(IntuneSecretHint));

    [RelayCommand]
    private void Next() => CurrentStep = 2;

    [RelayCommand]
    private void Back() => CurrentStep = 1;

    [RelayCommand]
    private void Save()
    {
        var s = _settingsService.Load();

        s.Intune.DeploymentTarget = DeploymentTargets.Normalize(DeploymentTarget);

        if (ShowMecmFields)
        {
            s.SiteCode = SiteCode.Trim();
            s.ProviderMachine = ProviderMachine.Trim();
            s.FileShareRoot = FileShareRoot.Trim();
            s.DownloadRoot = DownloadRoot.Trim();
        }

        if (ShowIntuneFields)
        {
            s.Intune.TenantId = IntuneTenantId.Trim();
            s.Intune.ClientId = IntuneClientId.Trim();
            // The plaintext never reaches settings.json; an empty box keeps the stored secret.
            if (!string.IsNullOrEmpty(IntuneClientSecret))
            {
                s.Intune.ClientSecretProtected = IntuneSecretProtector.Protect(IntuneClientSecret);
                IntuneClientSecret = "";
            }
        }

        s.FirstRunCompleted = true;
        if (ShowMecmFields) _settingsService.SaveOperatorEdit(s);
        else _settingsService.Save(s);

        _settingsViewModel.ReloadFromStore();
        _packagesViewModel.RefreshDeploymentTargetState();

        CloseRequested?.Invoke(this, true);
    }

    /// <summary>
    /// Skip leaves the wizard due next launch unless "Don't show this again" is checked, which
    /// suppresses it without configuring anything.
    /// </summary>
    [RelayCommand]
    private void Skip()
    {
        if (DontShowAgain)
        {
            var s = _settingsService.Load();
            s.FirstRunCompleted = true;
            _settingsService.Save(s);
        }

        CloseRequested?.Invoke(this, false);
    }
}
