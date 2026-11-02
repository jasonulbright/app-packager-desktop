using System.Diagnostics;
using System.IO;
using System.Reflection;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Models;
using ApplicationPackagerUI.Services;
using Wpf.Ui.Appearance;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class SettingsViewModel : ObservableObject, ISettingsReload
{
    private readonly SettingsService _settingsService;
    private readonly PackagerPreferencesService _prefsSync;
    private readonly ISnackbarService _snackbarService;
    private readonly PackagesViewModel _packagesViewModel;
    private readonly PackagerDiscoveryService _discoveryService;

    public ObservableCollection<InstallParameterGroup> InstallParameterGroups { get; } = [];
    [ObservableProperty] private bool _skipConfigReview;
    [ObservableProperty] private string _installParameterSummary = "";

    [ObservableProperty] private string _companyName = "";

    [ObservableProperty] private string _siteCode = "MCM";
    [ObservableProperty] private string _providerMachine = "";
    [ObservableProperty] private string _fileShareRoot = @"\\fileserver\sccm$";
    [ObservableProperty] private string _downloadRoot = @"C:\temp\ap";
    [ObservableProperty] private string _packagersRoot = "";

    [ObservableProperty] private double? _estimatedRuntimeMins = 15;
    [ObservableProperty] private double? _maximumRuntimeMins = 30;
    [ObservableProperty] private string _contentFallback = DeploymentTypeContentOptions.Allow;
    [ObservableProperty] private string _slowNetworkDeploymentMode = DeploymentTypeContentOptions.Download;

    /// <summary>The global application title defaults.</summary>
    public ApplicationTitleOptions TitleOptions { get; } = new();

    /// <summary>The Script Signing section; written to the store only by Save Settings.</summary>
    public ScriptSigningEditor Signing { get; } = new();

    /// <summary>The Discover unmanaged software fields; written to the store only by Save Settings.</summary>
    public DiscoverySettingsEditor Discovery { get; } = new();

    /// <summary>The Sidebar section; it applies each change at once and is not part of Save Settings.</summary>
    public SidebarSettingsEditor Sidebar { get; }

    [ObservableProperty] private string _m365Channel = "MonthlyEnterprise";

    // Per-app: Citrix Workspace switches. The controls bind into this object;
    // an indeterminate box or an empty choice is "not set".
    [ObservableProperty] private CitrixWorkspaceSwitches _cwaSwitches = CompleteSwitches(null);
    [ObservableProperty] private string _cwaPreview = "";

    // Local installer folders and side files, one row per definition or key
    public ObservableCollection<PathSettingRow> LocalSourceRows { get; } = [];
    public ObservableCollection<PathSettingRow> SideFileRows { get; } = [];

    // Systems in use: a publish button whose system is not in use stays disabled.
    [ObservableProperty] private bool _useConfigMgr = true;
    [ObservableProperty] private bool _useIntune;
    [ObservableProperty] private bool _useWsus;

    /// <summary>The WSUS Publishing section; written to the store only by Save Settings.</summary>
    public WsusPublishingEditor Wsus { get; }

    [ObservableProperty] private string _intuneTenantId = "";
    [ObservableProperty] private string _intuneClientId = "";

    /// <summary>
    /// Secret entry only. Never loaded from settings and never persisted in this form: an empty
    /// box on save keeps the stored secret, a typed value replaces it DPAPI-protected.
    /// </summary>
    [ObservableProperty] private string _intuneClientSecret = "";

    [ObservableProperty] private bool _hasStoredIntuneSecret;

    /// <summary>
    /// Key entry only. Never loaded from settings: an empty box on save keeps the stored
    /// key, a typed value replaces it DPAPI-protected.
    /// </summary>
    [ObservableProperty] private string _nvdApiKey = "";
    [ObservableProperty] private bool _hasStoredNvdApiKey;
    [ObservableProperty] private bool _removeNvdApiKey;
    [ObservableProperty] private bool _lookupCvesAfterCheckMecm;

    public string NvdApiKeyHint => HasStoredNvdApiKey ? "Stored — leave empty to keep it" : "Not set";

    partial void OnHasStoredNvdApiKeyChanged(bool value) => OnPropertyChanged(nameof(NvdApiKeyHint));

    [ObservableProperty] private string _iconPackStatus = "";
    [ObservableProperty] private bool _iconPackBusy;

    public bool IconPackIdle => !IconPackBusy;

    partial void OnIconPackBusyChanged(bool value) => OnPropertyChanged(nameof(IconPackIdle));

    [ObservableProperty] private string _gitHubApiStatus = "Checking...";
    [ObservableProperty] private bool _gitHubApiBusy;

    public bool GitHubApiIdle => !GitHubApiBusy;

    partial void OnGitHubApiBusyChanged(bool value) => OnPropertyChanged(nameof(GitHubApiIdle));

    [ObservableProperty] private ApplicationTheme _currentTheme = ApplicationTheme.Dark;

    [ObservableProperty] private string _contentLayout = ContentLayouts.Nested;
    [ObservableProperty] private bool _autoDistribute;
    [ObservableProperty] private string _dpGroupName = "";
    [ObservableProperty] private bool _deployToTestCollection;
    [ObservableProperty] private string _testCollectionName = "";
    [ObservableProperty] private bool _testCollectionIsUser;
    [ObservableProperty] private bool _createTestCollectionIfMissing;

    /// <summary>The test deployment needs distributed content, so its fields follow the auto-distribute switch.</summary>
    public bool TestDeploymentAvailable => AutoDistribute;
    partial void OnAutoDistributeChanged(bool value) => OnPropertyChanged(nameof(TestDeploymentAvailable));

    [ObservableProperty] private string _oneClickAction = OneClickActions.Report;
    [ObservableProperty] private bool _oneClickForce;
    [ObservableProperty] private double? _oneClickCadenceDays = 7;
    [ObservableProperty] private bool _oneClickDefaultConfigMgr = true;
    [ObservableProperty] private bool _oneClickDefaultWsus;
    [ObservableProperty] private bool _oneClickDefaultIntune;
    /// <summary>Skip or Overwrite.</summary>
    [ObservableProperty] private string _oneClickOnExisting = ApplicationPackagerEngine.Services.OneClick.OneClickOnExisting.Skip;

    /// <summary>The per-application destinations of applications the grid does not list; a save keeps them.</summary>
    private Dictionary<string, DestinationSet> _unlistedDestinations = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<OneClickRow> OneClickRows { get; } = [];
    public int OneClickTrackedCount => OneClickRows.Count(r => r.Tracked);
    /// <summary>Size of the application list the grid mirrors.</summary>
    public int PackagerCount => _packagesViewModel.Packagers.Count;

    [ObservableProperty] private string _consoleStatus = "";
    [ObservableProperty] private string _sevenZipStatus = "";
    [ObservableProperty] private string _contentPrepStatus = "";
    [ObservableProperty] private bool _contentPrepBusy;
    public bool ContentPrepIdle => !ContentPrepBusy;
    partial void OnContentPrepBusyChanged(bool value) => OnPropertyChanged(nameof(ContentPrepIdle));

    public string AppVersion { get; } = ReadAppVersion();

    /// <summary>The folder that holds the settings, which APP_PACKAGER_DATA_ROOT moves.</summary>
    public string SettingsFolder { get; } = ReadSettingsFolder();

    private static string ReadSettingsFolder()
    {
        try { return AppDataPaths.Root; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppPackager");
        }
    }

    public string IntuneSecretHint => HasStoredIntuneSecret
        ? "Stored — leave empty to keep it"
        : "Not set";

    partial void OnHasStoredIntuneSecretChanged(bool value) => OnPropertyChanged(nameof(IntuneSecretHint));

    public SettingsViewModel(SettingsService settingsService, PackagerPreferencesService prefsSync, ISnackbarService snackbarService,
        PackagesViewModel packagesViewModel, PackagerDiscoveryService discoveryService,
        ApplicationPackagerEngine.Services.Wsus.IWsusAdminClient wsusClient, IWsusPrompts wsusPrompts, SidebarService sidebarService)
    {
        _settingsService = settingsService;
        Sidebar = new SidebarSettingsEditor(sidebarService);
        _prefsSync = prefsSync;
        _snackbarService = snackbarService;
        _packagesViewModel = packagesViewModel;
        _discoveryService = discoveryService;
        Wsus = new WsusPublishingEditor(wsusClient, prompts: wsusPrompts);

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

        LoadPathRows(definitions);
    }

    /// <summary>
    /// One folder row per definition with a local installer source and one
    /// file row per side-file key, each starting from the saved path.
    /// </summary>
    private void LoadPathRows(IReadOnlyList<AppDefinition> definitions)
    {
        var s = _settingsService.Load();
        LocalSourceRows.Clear();
        foreach (var def in definitions.Where(d => d.LocalSource is not null)
                                       .OrderBy(d => d.Application, StringComparer.OrdinalIgnoreCase))
        {
            LocalSourceRows.Add(new PathSettingRow
            {
                Key = def.Id,
                Label = def.Application,
                Hint = def.LocalSource!.IsRequired
                    ? $"Required. Stage takes the newest {def.LocalSource.Filter} from this folder."
                    : $"Optional. With a folder set, Stage takes the newest {def.LocalSource.Filter} from it instead of downloading.",
                IsFolder = true,
                Path = s.LocalSourceFolders.TryGetValue(def.Id, out var folder) ? folder : ""
            });
        }

        SideFileRows.Clear();
        foreach (var group in definitions.SelectMany(d => (d.SideFiles ?? []).Select(f => (Definition: d, File: f)))
                                         .GroupBy(x => x.File.Key, StringComparer.OrdinalIgnoreCase)
                                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            SideFileRows.Add(new PathSettingRow
            {
                Key = first.File.Key,
                Label = $"{string.Join(", ", group.Select(x => x.Definition.Application).Distinct())} - {first.File.Label}",
                Hint = $"Copied beside the installer as {first.File.FileName}. Stage refuses to run without it.",
                FileFilter = $"{first.File.FileName}|{first.File.FileName}|All files (*.*)|*.*",
                Path = s.SideFiles.TryGetValue(first.File.Key, out var file) ? file : ""
            });
        }
    }

    private static void SetOrRemove(Dictionary<string, string> map, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) map.Remove(key);
        else map[key] = value.Trim();
    }

    /// <summary>A copy with every section present and empty strings for unset choices, so the controls can bind.</summary>
    private static CitrixWorkspaceSwitches CompleteSwitches(CitrixWorkspaceSwitches? source)
    {
        var copy = source is null
            ? new CitrixWorkspaceSwitches()
            : System.Text.Json.JsonSerializer.Deserialize<CitrixWorkspaceSwitches>(
                System.Text.Json.JsonSerializer.Serialize(source))!;
        copy.Store ??= new CwaStore();
        copy.Installation ??= new CwaInstallation();
        copy.Plugins ??= new CwaPlugins();
        copy.UpdateAndTelemetry ??= new CwaUpdateAndTelemetry();
        copy.StorePolicy ??= new CwaStorePolicy();
        copy.Components ??= new CwaComponents();
        copy.Store.Name ??= "";
        copy.Store.Url ??= "";
        copy.UpdateAndTelemetry.AutoUpdateCheck ??= "";
        copy.StorePolicy.AllowAddStore ??= "";
        copy.StorePolicy.AllowSavePwd ??= "";
        return copy;
    }

    /// <summary>The stored form: empty strings become null, which the builder and the PS1 packagers read as not set.</summary>
    private static CitrixWorkspaceSwitches TrimSwitches(CitrixWorkspaceSwitches edited)
    {
        var copy = CompleteSwitches(edited);
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        copy.Store!.Name = Text(copy.Store.Name);
        copy.Store.Url = Text(copy.Store.Url);
        copy.UpdateAndTelemetry!.AutoUpdateCheck = Text(copy.UpdateAndTelemetry.AutoUpdateCheck);
        copy.StorePolicy!.AllowAddStore = Text(copy.StorePolicy.AllowAddStore);
        copy.StorePolicy.AllowSavePwd = Text(copy.StorePolicy.AllowSavePwd);
        return copy;
    }

    /// <summary>Shows the Current and LTSR command lines the current choices produce, with each skipped setting.</summary>
    [RelayCommand]
    private void PreviewCwa()
    {
        var switches = TrimSwitches(CwaSwitches);
        var lines = new List<string>();
        foreach (var stream in new[] { CitrixInstallArguments.Current, CitrixInstallArguments.Ltsr })
        {
            var built = CitrixInstallArguments.Build(switches, stream);
            lines.Add($"{stream}: {string.Join(" ", built.Arguments)}");
            lines.AddRange(built.Warnings.Select(w => $"  {stream} warning: {w}"));
        }
        CwaPreview = string.Join(Environment.NewLine, lines);
    }

    private void RefreshIconPackStatus() =>
        IconPackStatus = IconPackService.StatusText(
            IconPackService.ReadStatus(IconPackService.UserDefinitionsDirectory()));

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
            : "Not found. Install the Configuration Manager console; ConfigMgr packaging runs its PowerShell module.";

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
                IconPackService.UserDefinitionsDirectory(), version,
                definitionSourceDirectories: [
                    IconPackService.UserDefinitionsDirectory(),
                    IconPackService.BundledDefinitionsDirectory()
                ]);

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
                dialog.FileName, IconPackService.UserDefinitionsDirectory(), version,
                definitionSourceDirectories: [
                    IconPackService.UserDefinitionsDirectory(),
                    IconPackService.BundledDefinitionsDirectory()
                ]);

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

    /// <summary>Discards every unsaved edit on the page by reading the store again.</summary>
    [RelayCommand]
    private void DiscardChanges() => LoadSettings();

    /// <summary>Re-reads the store after another surface (the setup window) has written to it.</summary>
    public void ReloadFromStore() => LoadSettings();

    /// <summary>
    /// Rebuilds the One Click grid from the applications the Packages page shows, so a
    /// hidden application cannot be tracked. Called when the page opens and on demand.
    /// </summary>
    [RelayCommand]
    public void RefreshOneClickRows()
    {
        var stored = _settingsService.Load();
        var oneClick = stored.OneClick;
        var tracked = new HashSet<string>(oneClick.TrackedApplications, StringComparer.OrdinalIgnoreCase);
        var planSettings = OneClickSettingsMap.PlanSettings(stored, consoleFound: true);
        OneClickRows.Clear();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _packagesViewModel.Packagers.OrderBy(p => p.Application, StringComparer.OrdinalIgnoreCase))
        {
            listed.Add(p.Uid);
            var (set, _) = ApplicationPackagerEngine.Services.OneClick.OneClickPlanner.DestinationsFor(planSettings, p.Uid);
            var row = new OneClickRow
            {
                Uid = p.Uid,
                Application = p.Application,
                Vendor = p.Vendor,
                Tracked = tracked.Contains(p.Uid),
                CadenceDisplay = oneClick.CadenceOverrides.TryGetValue(p.Uid, out var days) ? days.ToString() : "",
                ConfigMgr = set.ConfigMgr,
                Wsus = set.Wsus,
                Intune = set.Intune
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OneClickRow.Tracked)) OnPropertyChanged(nameof(OneClickTrackedCount));
            };
            OneClickRows.Add(row);
        }
        _unlistedDestinations = oneClick.Destinations.Where(kv => !listed.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(OneClickTrackedCount));
    }

    /// <summary>The One Click default destinations the boxes hold.</summary>
    public DestinationSet OneClickDefaultDestinations => new(OneClickDefaultConfigMgr, OneClickDefaultWsus, OneClickDefaultIntune);

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
        Discovery.Load(s);
        FileShareRoot = s.FileShareRoot;
        DownloadRoot = s.DownloadRoot;
        PackagersRoot = s.PackagersRoot;
        EstimatedRuntimeMins = s.EstimatedRuntimeMins;
        MaximumRuntimeMins = s.MaximumRuntimeMins;
        var content = DeploymentTypeContentOptions.Resolve(s.ContentFallback, s.SlowNetworkDeploymentMode);
        ContentFallback = content.ContentFallback ? DeploymentTypeContentOptions.Allow : DeploymentTypeContentOptions.Deny;
        SlowNetworkDeploymentMode = content.SlowNetworkDeploymentMode;
        TitleOptions.Load(s.IncludeVersionInTitle, s.RemoveVersionFromTitle);
        Signing.Load(s.ScriptSigning);
        M365Channel = s.M365Channel;

        ContentLayout = ContentLayouts.Normalize(s.ContentLayout);
        AutoDistribute = s.ContentDistribution.AutoDistribute;
        DpGroupName = s.ContentDistribution.DpGroupName;
        DeployToTestCollection = s.ContentDistribution.DeployToTestCollection;
        TestCollectionName = s.ContentDistribution.TestCollectionName;
        TestCollectionIsUser = s.ContentDistribution.TestCollectionIsUser;
        CreateTestCollectionIfMissing = s.ContentDistribution.CreateTestCollectionIfMissing;

        var systems = s.Systems ?? new SystemsInUse();
        UseConfigMgr = systems.ConfigMgr;
        UseIntune = systems.Intune;
        UseWsus = systems.Wsus;
        Wsus.Load(s.Wsus);

        OneClickAction = OneClickActions.Normalize(s.OneClick.Action);
        OneClickForce = s.OneClick.ForceOnLaunch;
        OneClickCadenceDays = s.OneClick.DefaultCadenceDays > 0 ? s.OneClick.DefaultCadenceDays : 7;
        var defaults = s.OneClick.DefaultDestinations ?? DestinationSet.FromTarget(s.Intune.DeploymentTarget);
        OneClickDefaultConfigMgr = defaults.ConfigMgr;
        OneClickDefaultWsus = defaults.Wsus;
        OneClickDefaultIntune = defaults.Intune;
        OneClickOnExisting = ApplicationPackagerEngine.Services.OneClick.OneClickOnExisting.Normalize(s.OneClick.OnExisting);
        RefreshOneClickRows();
        SkipConfigReview = s.SkipConfigReview;

        CwaSwitches = CompleteSwitches(s.CitrixSwitches);
        CwaPreview = "";
        foreach (var row in LocalSourceRows)
            row.Path = s.LocalSourceFolders.TryGetValue(row.Key, out var folder) ? folder : "";
        foreach (var row in SideFileRows)
            row.Path = s.SideFiles.TryGetValue(row.Key, out var file) ? file : "";

        IntuneTenantId = s.Intune.TenantId;
        IntuneClientId = s.Intune.ClientId;
        IntuneClientSecret = "";
        HasStoredIntuneSecret = !string.IsNullOrWhiteSpace(s.Intune.ClientSecretProtected);

        s.Vulnerability ??= new VulnerabilitySettings();
        NvdApiKey = "";
        RemoveNvdApiKey = false;
        HasStoredNvdApiKey = !string.IsNullOrWhiteSpace(s.Vulnerability.NvdApiKeyProtected);
        LookupCvesAfterCheckMecm = s.Vulnerability.LookupAfterCheckCm;

        if (Enum.TryParse<ApplicationTheme>(s.Theme, out var theme))
            CurrentTheme = theme;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var s = _settingsService.Load();
        var siteBefore = SiteIdentity.Key(s.SiteCode, s.ProviderMachine);

        s.CompanyName = CompanyName;
        s.SiteCode = SiteCode;
        s.ProviderMachine = ProviderMachine.Trim();
        Discovery.ApplyTo(s);
        s.FileShareRoot = FileShareRoot;
        s.DownloadRoot = DownloadRoot;
        s.PackagersRoot = PackagersRoot;
        s.EstimatedRuntimeMins = (int)(EstimatedRuntimeMins ?? 15);
        s.MaximumRuntimeMins = (int)(MaximumRuntimeMins ?? 30);
        s.ContentFallback = ContentFallback == DeploymentTypeContentOptions.Deny ? DeploymentTypeContentOptions.Deny : DeploymentTypeContentOptions.Allow;
        s.SlowNetworkDeploymentMode = SlowNetworkDeploymentMode == DeploymentTypeContentOptions.DoNothing ? DeploymentTypeContentOptions.DoNothing : DeploymentTypeContentOptions.Download;
        (s.IncludeVersionInTitle, s.RemoveVersionFromTitle) = TitleOptions.ToSettings();
        s.ScriptSigning = Signing.ToPolicy();
        s.M365Channel = M365Channel;
        s.Theme = CurrentTheme.ToString();

        s.ContentLayout = ContentLayouts.Normalize(ContentLayout);
        s.ContentDistribution = new ContentDistributionSettings
        {
            AutoDistribute = AutoDistribute,
            DpGroupName = DpGroupName.Trim(),
            DeployToTestCollection = DeployToTestCollection,
            TestCollectionName = TestCollectionName.Trim(),
            TestCollectionIsUser = TestCollectionIsUser,
            CreateTestCollectionIfMissing = CreateTestCollectionIfMissing,
        };

        s.Systems = new SystemsInUse { ConfigMgr = UseConfigMgr, Intune = UseIntune, Wsus = UseWsus };
        s.Wsus = Wsus.ToSettings();

        s.SkipConfigReview = SkipConfigReview;
        foreach (var group in InstallParameterGroups)
            s.InstallerSettingValues[group.DefinitionId] = new Dictionary<string, string>(group.Values);

        var defaultCadence = (int)(OneClickCadenceDays ?? 7);
        var defaults = OneClickDefaultDestinations;
        var destinations = OneClickSettingsMap.RowsThatDiffer(OneClickRows.Select(r => (r.Uid, r.Destinations)), defaults);
        foreach (var (key, set) in _unlistedDestinations) destinations.TryAdd(key, set);
        var (tracked, cadenceOverrides) = OneClickSettingsMap.TrackedAndCadence(
            OneClickRows.Select(r => (r.Uid, r.Tracked, r.CadenceOverride)).ToList(), defaultCadence, s.OneClick);
        s.OneClick = new OneClickSettings
        {
            Action = OneClickActions.Normalize(OneClickAction),
            ForceOnLaunch = OneClickForce,
            DefaultCadenceDays = defaultCadence > 0 ? defaultCadence : 7,
            TrackedApplications = tracked,
            CadenceOverrides = cadenceOverrides,
            Destinations = destinations,
            DefaultDestinations = defaults,
            OnExisting = ApplicationPackagerEngine.Services.OneClick.OneClickOnExisting.Normalize(OneClickOnExisting)
        };
        // The nearest single target, for the code paths that read one target.
        s.Intune.DeploymentTarget = DeploymentTargets.Normalize(defaults.ToTargetName());

        s.CitrixSwitches = TrimSwitches(CwaSwitches);
        s.Cwa = null;
        foreach (var row in LocalSourceRows)
            SetOrRemove(s.LocalSourceFolders, row.Key, row.Path);
        foreach (var row in SideFileRows)
            SetOrRemove(s.SideFiles, row.Key, row.Path);
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

        s.Vulnerability ??= new VulnerabilitySettings();
        s.Vulnerability.LookupAfterCheckCm = LookupCvesAfterCheckMecm;
        // The plaintext key never reaches settings.json.
        if (!string.IsNullOrWhiteSpace(NvdApiKey)) s.Vulnerability.NvdApiKeyProtected = NvdApiKeyStore.Protect(NvdApiKey);
        else if (RemoveNvdApiKey) s.Vulnerability.NvdApiKeyProtected = "";
        NvdApiKey = "";
        RemoveNvdApiKey = false;
        HasStoredNvdApiKey = !string.IsNullOrWhiteSpace(s.Vulnerability.NvdApiKeyProtected);

        _settingsService.SaveOperatorEdit(s);

        ThemeApplier.Apply(s.Theme);
        _packagesViewModel.RefreshPublishActionState();
        // ConfigMgr versions and compare results describe one site.
        if (SiteIdentity.Key(s.SiteCode, s.ProviderMachine) != siteBefore)
            PackagesViewModel.ClearSiteResults(_packagesViewModel.Packagers);

        _prefsSync.SyncPackagerPreferences(s.CompanyName, s.SideFiles);
        _prefsSync.SyncCwaSettings(s.CitrixSwitches);

        _snackbarService.Show("Settings", "Settings saved.", Wpf.Ui.Controls.ControlAppearance.Success,
            new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(2));
    }

    [RelayCommand]
    private void ChangeTheme(string themeParameter)
    {
        if (ThemeApplier.Resolve(themeParameter) is { } theme)
        {
            CurrentTheme = theme;
            ThemeApplier.Apply(theme);
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
