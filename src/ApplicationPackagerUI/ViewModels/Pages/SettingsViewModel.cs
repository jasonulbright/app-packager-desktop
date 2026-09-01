using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Wpf.Ui.Appearance;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly PackagerPreferencesService _prefsSync;
    private readonly ISnackbarService _snackbarService;

    // Organization
    [ObservableProperty] private string _companyName = "";

    // MECM Site
    [ObservableProperty] private string _siteCode = "MCM";
    [ObservableProperty] private string _providerMachine = "";
    [ObservableProperty] private string _fileShareRoot = @"\\fileserver\sccm$";
    [ObservableProperty] private string _downloadRoot = @"C:\temp\ap";
    [ObservableProperty] private string _packagersRoot = "";

    // Deployment Type defaults
    [ObservableProperty] private double? _estimatedRuntimeMins = 15;
    [ObservableProperty] private double? _maximumRuntimeMins = 30;

    // Acquisition
    [ObservableProperty] private string _acquisitionMethod = "StaticUrl";

    // Per-app: M365
    [ObservableProperty] private string _m365Channel = "MonthlyEnterprise";

    // Per-app: CWA
    [ObservableProperty] private string _cwaStoreName = "";
    [ObservableProperty] private string _cwaStoreUrl = "";
    [ObservableProperty] private bool _cwaClean;
    [ObservableProperty] private bool _cwaSingleSignOn;
    [ObservableProperty] private bool _cwaAppProtection;
    [ObservableProperty] private bool _cwaPreLaunch;
    [ObservableProperty] private bool _cwaSelfService;
    [ObservableProperty] private bool _cwaPluginTeams;
    [ObservableProperty] private bool _cwaPluginZoom;
    [ObservableProperty] private bool _cwaPluginWebEx;
    [ObservableProperty] private bool _cwaPluginUber;
    [ObservableProperty] private bool _cwaPluginUberSkip;
    [ObservableProperty] private bool _cwaPluginEpa;
    [ObservableProperty] private bool _cwaPluginStorageResync;
    [ObservableProperty] private string _cwaAutoUpdate = "Auto";
    [ObservableProperty] private string _cwaAddStorePolicy = "None";
    [ObservableProperty] private string _cwaSavePassword = "None";

    // Intune
    [ObservableProperty] private string _deploymentTarget = DeploymentTargets.Mecm;
    [ObservableProperty] private string _intuneTenantId = "";
    [ObservableProperty] private string _intuneClientId = "";

    /// <summary>
    /// Secret entry only. Never loaded from settings and never persisted in this form: an empty
    /// box on save keeps the stored secret, a typed value replaces it DPAPI-protected.
    /// </summary>
    [ObservableProperty] private string _intuneClientSecret = "";

    [ObservableProperty] private bool _hasStoredIntuneSecret;

    // UI
    [ObservableProperty] private ApplicationTheme _currentTheme = ApplicationTheme.Dark;

    public string IntuneSecretHint => HasStoredIntuneSecret
        ? "Stored — leave empty to keep it"
        : "Not set";

    partial void OnHasStoredIntuneSecretChanged(bool value) => OnPropertyChanged(nameof(IntuneSecretHint));

    public SettingsViewModel(SettingsService settingsService, PackagerPreferencesService prefsSync, ISnackbarService snackbarService)
    {
        _settingsService = settingsService;
        _prefsSync = prefsSync;
        _snackbarService = snackbarService;

        // Migrate from PS1 GUI preferences on first launch
        prefsSync.MigrateFromPs1IfNeeded();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _settingsService.Load();

        CompanyName = s.CompanyName;
        SiteCode = s.SiteCode;
        ProviderMachine = s.ProviderMachine;
        FileShareRoot = s.FileShareRoot;
        DownloadRoot = s.DownloadRoot;
        PackagersRoot = s.PackagersRoot;
        EstimatedRuntimeMins = s.EstimatedRuntimeMins;
        MaximumRuntimeMins = s.MaximumRuntimeMins;
        AcquisitionMethod = s.AcquisitionMethod;
        M365Channel = s.M365Channel;

        CwaStoreName = s.Cwa.StoreName;
        CwaStoreUrl = s.Cwa.StoreUrl;
        CwaClean = s.Cwa.Clean;
        CwaSingleSignOn = s.Cwa.SingleSignOn;
        CwaAppProtection = s.Cwa.AppProtection;
        CwaPreLaunch = s.Cwa.PreLaunch;
        CwaSelfService = s.Cwa.SelfService;
        CwaPluginTeams = s.Cwa.PluginTeams;
        CwaPluginZoom = s.Cwa.PluginZoom;
        CwaPluginWebEx = s.Cwa.PluginWebEx;
        CwaPluginUber = s.Cwa.PluginUber;
        CwaPluginUberSkip = s.Cwa.PluginUberSkip;
        CwaPluginEpa = s.Cwa.PluginEpa;
        CwaPluginStorageResync = s.Cwa.PluginStorageResync;
        CwaAutoUpdate = s.Cwa.AutoUpdate;
        CwaAddStorePolicy = s.Cwa.AddStorePolicy;
        CwaSavePassword = s.Cwa.SavePassword;

        DeploymentTarget = DeploymentTargets.Normalize(s.Intune.DeploymentTarget);
        IntuneTenantId = s.Intune.TenantId;
        IntuneClientId = s.Intune.ClientId;
        IntuneClientSecret = "";
        HasStoredIntuneSecret = !string.IsNullOrWhiteSpace(s.Intune.ClientSecretProtected);

        if (Enum.TryParse<ApplicationTheme>(s.Theme, out var theme))
            CurrentTheme = theme;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var s = _settingsService.Load();

        s.CompanyName = CompanyName;
        s.SiteCode = SiteCode;
        s.ProviderMachine = ProviderMachine.Trim();
        s.FileShareRoot = FileShareRoot;
        s.DownloadRoot = DownloadRoot;
        s.PackagersRoot = PackagersRoot;
        s.EstimatedRuntimeMins = (int)(EstimatedRuntimeMins ?? 15);
        s.MaximumRuntimeMins = (int)(MaximumRuntimeMins ?? 30);
        s.M365Channel = M365Channel;
        s.AcquisitionMethod = AcquisitionMethod;
        s.Theme = CurrentTheme.ToString();

        s.Cwa = new CwaInstallSettings
        {
            StoreName = CwaStoreName,
            StoreUrl = CwaStoreUrl,
            Clean = CwaClean,
            SingleSignOn = CwaSingleSignOn,
            AppProtection = CwaAppProtection,
            PreLaunch = CwaPreLaunch,
            SelfService = CwaSelfService,
            PluginTeams = CwaPluginTeams,
            PluginZoom = CwaPluginZoom,
            PluginWebEx = CwaPluginWebEx,
            PluginUber = CwaPluginUber,
            PluginUberSkip = CwaPluginUberSkip,
            PluginEpa = CwaPluginEpa,
            PluginStorageResync = CwaPluginStorageResync,
            AutoUpdate = CwaAutoUpdate,
            AddStorePolicy = CwaAddStorePolicy,
            SavePassword = CwaSavePassword,
        };

        s.Intune.DeploymentTarget = DeploymentTargets.Normalize(DeploymentTarget);
        s.Intune.TenantId = IntuneTenantId.Trim();
        s.Intune.ClientId = IntuneClientId.Trim();
        // An empty box keeps the stored secret; a typed value replaces it, DPAPI-protected for
        // the current Windows user. The plaintext never reaches settings.json.
        if (!string.IsNullOrEmpty(IntuneClientSecret))
        {
            s.Intune.ClientSecretProtected = IntuneSecretProtector.Protect(IntuneClientSecret);
            IntuneClientSecret = "";
        }
        HasStoredIntuneSecret = !string.IsNullOrWhiteSpace(s.Intune.ClientSecretProtected);

        _settingsService.Save(s);

        // Sync to PS1 packager JSON files
        _prefsSync.SyncCompanyName(s.CompanyName);
        _prefsSync.SyncCwaSettings(s.Cwa);

        _snackbarService.Show("Settings", "Settings saved.", Wpf.Ui.Controls.ControlAppearance.Success,
            new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(2));
    }

    [RelayCommand]
    private void ChangeTheme(string themeParameter)
    {
        if (Enum.TryParse<ApplicationTheme>(themeParameter, out var theme))
        {
            CurrentTheme = theme;
            ApplicationThemeManager.Apply(theme);
        }
    }

    [RelayCommand]
    private void BrowseDownloadRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Download Root Folder",
            InitialDirectory = DownloadRoot
        };
        if (dialog.ShowDialog() == true)
            DownloadRoot = dialog.FolderName;
    }

    [RelayCommand]
    private void BrowsePackagersRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Packagers Folder",
            InitialDirectory = PackagersRoot
        };
        if (dialog.ShowDialog() == true)
            PackagersRoot = dialog.FolderName;
    }
}
