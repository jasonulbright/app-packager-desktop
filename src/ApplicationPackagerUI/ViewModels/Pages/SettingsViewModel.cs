using System.Diagnostics;
using System.IO;
using System.Reflection;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Wpf.Ui.Appearance;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly PackagerPreferencesService _prefsSync;
    private readonly ISnackbarService _snackbarService;
    private readonly PackagesViewModel _packagesViewModel;
    private readonly PackagerDiscoveryService _discoveryService;

    // Install parameters per definition
    public ObservableCollection<InstallParameterGroup> InstallParameterGroups { get; } = [];
    [ObservableProperty] private bool _skipConfigReview;
    [ObservableProperty] private string _installParameterSummary = "";

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

    // Icon pack
    [ObservableProperty] private string _iconPackStatus = "";
    [ObservableProperty] private bool _iconPackBusy;

    public bool IconPackIdle => !IconPackBusy;

    partial void OnIconPackBusyChanged(bool value) => OnPropertyChanged(nameof(IconPackIdle));

    // GitHub API
    [ObservableProperty] private string _gitHubApiStatus = "Checking...";
    [ObservableProperty] private bool _gitHubApiBusy;

    public bool GitHubApiIdle => !GitHubApiBusy;

    partial void OnGitHubApiBusyChanged(bool value) => OnPropertyChanged(nameof(GitHubApiIdle));

    // UI
    [ObservableProperty] private ApplicationTheme _currentTheme = ApplicationTheme.Dark;

    // Content layout and the steps after the MECM application exists
    [ObservableProperty] private string _contentLayout = ContentLayouts.Nested;
    [ObservableProperty] private bool _autoDistribute;
    [ObservableProperty] private string _dpGroupName = "";
    [ObservableProperty] private bool _deployToTestCollection;
    [ObservableProperty] private string _testCollectionName = "";
    [ObservableProperty] private bool _createTestCollectionIfMissing;
    [ObservableProperty] private bool _createIntuneWin;

    /// <summary>The test deployment needs distributed content, so its fields follow the auto-distribute switch.</summary>
    public bool TestDeploymentAvailable => AutoDistribute;
    partial void OnAutoDistributeChanged(bool value) => OnPropertyChanged(nameof(TestDeploymentAvailable));

    /// <summary>Targets that publish to Intune build the .intunewin regardless, so the switch only matters for MECM only.</summary>
    public bool CreateIntuneWinApplies => !DeploymentTargets.PublishesToIntune(DeploymentTarget);
    partial void OnDeploymentTargetChanged(string value) => OnPropertyChanged(nameof(CreateIntuneWinApplies));

    // One Click
    [ObservableProperty] private string _oneClickAction = OneClickActions.Report;
    [ObservableProperty] private bool _oneClickForce;
    [ObservableProperty] private double? _oneClickCadenceDays = 7;
    public ObservableCollection<OneClickRow> OneClickRows { get; } = [];
    public int OneClickTrackedCount => OneClickRows.Count(r => r.Tracked);
    /// <summary>Size of the application list the grid mirrors.</summary>
    public int PackagerCount => _packagesViewModel.Packagers.Count;

    // Detected tools
    [ObservableProperty] private string _consoleStatus = "";
    [ObservableProperty] private string _sevenZipStatus = "";
    [ObservableProperty] private string _contentPrepStatus = "";
    [ObservableProperty] private bool _contentPrepBusy;
    public bool ContentPrepIdle => !ContentPrepBusy;
    partial void OnContentPrepBusyChanged(bool value) => OnPropertyChanged(nameof(ContentPrepIdle));

    public string AppVersion { get; } = ReadAppVersion();

    public string IntuneSecretHint => HasStoredIntuneSecret
        ? "Stored — leave empty to keep it"
        : "Not set";

    partial void OnHasStoredIntuneSecretChanged(bool value) => OnPropertyChanged(nameof(IntuneSecretHint));

    public SettingsViewModel(SettingsService settingsService, PackagerPreferencesService prefsSync, ISnackbarService snackbarService,
        PackagesViewModel packagesViewModel, PackagerDiscoveryService discoveryService)
    {
        _settingsService = settingsService;
        _prefsSync = prefsSync;
        _snackbarService = snackbarService;
        _packagesViewModel = packagesViewModel;
        _discoveryService = discoveryService;

        // Migrate from PS1 GUI preferences on first launch
        prefsSync.MigrateFromPs1IfNeeded();
        LoadSettings();
        RefreshIconPackStatus();
        RefreshToolStatus();
        _ = RefreshGitHubApiStatusAsync();
        _ = LoadInstallParameterGroupsAsync();
    }

    /// <summary>
    /// One group per definition that declares install parameters, each starting from
    /// the saved values so the panels show what the next stage will use.
    /// </summary>
    private async Task LoadInstallParameterGroupsAsync()
    {
        var saved = _settingsService.Load().InstallerSettingValues;
        var definitions = await _discoveryService.LoadDefinitionsAsync();
        InstallParameterGroups.Clear();
        foreach (var def in definitions.Where(d => d.InstallerSettings is { Length: > 0 })
                                       .OrderBy(d => d.Application, StringComparer.OrdinalIgnoreCase))
        {
            var values = saved.TryGetValue(def.Id, out var stored)
                ? new Dictionary<string, string>(stored)
                : new Dictionary<string, string>();
            InstallParameterGroups.Add(new InstallParameterGroup
            {
                DefinitionId = def.Id,
                Application = def.Application,
                Vendor = def.Vendor,
                Parameters = def.InstallerSettings!,
                Values = values
            });
        }
        InstallParameterSummary = InstallParameterGroups.Count == 0
            ? "No definition declares install parameters."
            : $"{InstallParameterGroups.Count} applications carry install parameters.";
    }

    private void RefreshIconPackStatus() =>
        IconPackStatus = IconPackService.StatusText(
            IconPackService.ReadStatus(IconPackService.DefaultDefinitionsDirectory()));

    private static string ReadAppVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // The informational version carries the pre-release suffix; the build stamps a source hash after '+'.
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];
        return assembly?.GetName().Version?.ToString(3) ?? "unknown";
    }

    /// <summary>
    /// Presence of the three external tools the flows shell out to: the Configuration
    /// Manager console module, 7-Zip, and the Win32 Content Prep Tool. Read once when the
    /// page opens; the prep tool row refreshes after a download.
    /// </summary>
    private void RefreshToolStatus()
    {
        var adminUi = Environment.GetEnvironmentVariable("SMS_ADMIN_UI_PATH");
        var module = string.IsNullOrWhiteSpace(adminUi) ? null : Path.GetFullPath(Path.Combine(adminUi, "..", "ConfigurationManager.psd1"));
        ConsoleStatus = module is not null && File.Exists(module)
            ? $"Found: {module}"
            : "Not found. Install the Configuration Manager console; MECM packaging runs its PowerShell module.";

        var sevenZip = ArchiveExtractionService.FindSevenZip();
        SevenZipStatus = sevenZip is not null
            ? $"Found: {sevenZip}"
            : "Not found. Install 7-Zip; installer analysis and archive extraction need 7z.exe.";

        var prep = IntuneDeploymentService.InstalledToolPath();
        ContentPrepStatus = prep is not null
            ? $"Found: {prep}"
            : "Not downloaded yet. Fetched on first Intune build, or download it now.";
    }

    /// <summary>Downloads the Win32 Content Prep Tool ahead of the first Intune build.</summary>
    [RelayCommand]
    private async Task DownloadContentPrepAsync()
    {
        if (ContentPrepBusy) return;
        ContentPrepBusy = true;
        try
        {
            var path = await IntuneDeploymentService.DownloadToolAsync();
            ContentPrepStatus = $"Found: {path}";
            _snackbarService.Show("Content Prep Tool", "IntuneWinAppUtil.exe downloaded and verified.",
                Wpf.Ui.Controls.ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            ContentPrepStatus = $"Download failed: {ex.Message}";
            _snackbarService.Show("Content Prep Tool", ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(6));
        }
        finally
        {
            ContentPrepBusy = false;
        }
    }

    [RelayCommand]
    private void OpenReleases()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/jasonulbright/app-packager-desktop/releases") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _snackbarService.Show("Releases", ex.Message,
                Wpf.Ui.Controls.ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>
    /// Asks api.github.com for the quota of the credentials the scrapers use.
    /// The query is the only network call on this page and runs when it opens,
    /// so a sweep about to hit the anonymous budget is visible beforehand.
    /// </summary>
    [RelayCommand]
    private async Task RefreshGitHubApiStatusAsync()
    {
        if (GitHubApiBusy) return;
        GitHubApiBusy = true;
        try
        {
            var status = await GitHubApiStatusService.QueryAsync();
            GitHubApiStatus = status.StatusText();
        }
        finally
        {
            GitHubApiBusy = false;
        }
    }

    /// <summary>
    /// Downloads and installs the packager icon pack. The service reports every failure
    /// as a message, so the button surfaces an outcome rather than an error dialog.
    /// </summary>
    [RelayCommand]
    private async Task DownloadIconPackAsync()
    {
        IconPackBusy = true;
        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
            var result = await new IconPackService().InstallAsync(
                IconPackService.DefaultDefinitionsDirectory(), version);

            RefreshIconPackStatus();

            _snackbarService.Show(
                "Icon pack", result.Message,
                result.Installed ? Wpf.Ui.Controls.ControlAppearance.Success : Wpf.Ui.Controls.ControlAppearance.Caution,
                new SymbolIcon(result.Installed ? SymbolRegular.Checkmark24 : SymbolRegular.Warning24),
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            IconPackBusy = false;
        }
    }

    /// <summary>
    /// Installs the icon pack from a local or UNC zip, for hosts whose proxy blocks the
    /// release download. Same outcome-as-message handling as the download button.
    /// </summary>
    [RelayCommand]
    private async Task InstallIconPackFromFileAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select icon pack zip",
            Filter = "Icon pack (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        IconPackBusy = true;
        try
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
            var result = await new IconPackService().InstallFromFileAsync(
                dialog.FileName, IconPackService.DefaultDefinitionsDirectory(), version);

            RefreshIconPackStatus();

            _snackbarService.Show(
                "Icon pack", result.Message,
                result.Installed ? Wpf.Ui.Controls.ControlAppearance.Success : Wpf.Ui.Controls.ControlAppearance.Caution,
                new SymbolIcon(result.Installed ? SymbolRegular.Checkmark24 : SymbolRegular.Warning24),
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            IconPackBusy = false;
        }
    }

    /// <summary>Re-reads the store after another surface (the setup window) has written to it.</summary>
    public void ReloadFromStore() => LoadSettings();

    /// <summary>
    /// Rebuilds the One Click grid from the applications the Packages page shows, so a
    /// hidden application cannot be tracked. Called when the page opens and on demand.
    /// </summary>
    [RelayCommand]
    public void RefreshOneClickRows()
    {
        var oneClick = _settingsService.Load().OneClick;
        var tracked = new HashSet<string>(oneClick.TrackedApplications, StringComparer.OrdinalIgnoreCase);
        OneClickRows.Clear();
        foreach (var p in _packagesViewModel.Packagers.OrderBy(p => p.Application, StringComparer.OrdinalIgnoreCase))
        {
            var row = new OneClickRow
            {
                Uid = p.Uid,
                Application = p.Application,
                Vendor = p.Vendor,
                Tracked = tracked.Contains(p.Uid),
                CadenceDisplay = oneClick.CadenceOverrides.TryGetValue(p.Uid, out var days) ? days.ToString() : ""
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OneClickRow.Tracked)) OnPropertyChanged(nameof(OneClickTrackedCount));
            };
            OneClickRows.Add(row);
        }
        OnPropertyChanged(nameof(OneClickTrackedCount));
    }

    [RelayCommand]
    private void TrackAllOneClick()
    {
        foreach (var row in OneClickRows) row.Tracked = true;
    }

    [RelayCommand]
    private void TrackNoneOneClick()
    {
        foreach (var row in OneClickRows) row.Tracked = false;
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

        ContentLayout = ContentLayouts.Normalize(s.ContentLayout);
        AutoDistribute = s.ContentDistribution.AutoDistribute;
        DpGroupName = s.ContentDistribution.DpGroupName;
        DeployToTestCollection = s.ContentDistribution.DeployToTestCollection;
        TestCollectionName = s.ContentDistribution.TestCollectionName;
        CreateTestCollectionIfMissing = s.ContentDistribution.CreateTestCollectionIfMissing;
        CreateIntuneWin = s.Intune.CreateIntuneWin;

        OneClickAction = OneClickActions.Normalize(s.OneClick.Action);
        OneClickForce = s.OneClick.ForceOnLaunch;
        OneClickCadenceDays = s.OneClick.DefaultCadenceDays > 0 ? s.OneClick.DefaultCadenceDays : 7;
        RefreshOneClickRows();
        SkipConfigReview = s.SkipConfigReview;

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

        s.ContentLayout = ContentLayouts.Normalize(ContentLayout);
        s.ContentDistribution = new ContentDistributionSettings
        {
            AutoDistribute = AutoDistribute,
            DpGroupName = DpGroupName.Trim(),
            DeployToTestCollection = DeployToTestCollection,
            TestCollectionName = TestCollectionName.Trim(),
            CreateTestCollectionIfMissing = CreateTestCollectionIfMissing,
        };
        s.Intune.CreateIntuneWin = CreateIntuneWin;

        s.SkipConfigReview = SkipConfigReview;
        foreach (var group in InstallParameterGroups)
            s.InstallerSettingValues[group.DefinitionId] = new Dictionary<string, string>(group.Values);

        var defaultCadence = (int)(OneClickCadenceDays ?? 7);
        s.OneClick = new OneClickSettings
        {
            Action = OneClickActions.Normalize(OneClickAction),
            ForceOnLaunch = OneClickForce,
            DefaultCadenceDays = defaultCadence > 0 ? defaultCadence : 7,
            TrackedApplications = OneClickRows.Where(r => r.Tracked).Select(r => r.Uid).ToArray(),
            CadenceOverrides = OneClickRows
                .Where(r => r.CadenceOverride is { } days && days != defaultCadence)
                .ToDictionary(r => r.Uid, r => r.CadenceOverride!.Value)
        };

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

        _packagesViewModel.RefreshDeploymentTargetState();

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
