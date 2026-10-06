using System.IO;
using System.Text.Json;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

public partial class AddAppViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly ISnackbarService _snackbarService;
    private readonly INavigationService _navigationService;

    // Wizard step (1-5)
    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private string _stepTitle = "Step 1: Basic Information";
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoNext = true;
    [ObservableProperty] private bool _canSave;

    // Step 1: Basic Info
    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private string _applicationName = "";
    [ObservableProperty] private string _architecture = "x64";
    [ObservableProperty] private string _vendorUrl = "";
    [ObservableProperty] private string _cmName = "";

    // Step 2: Version Source
    [ObservableProperty] private string _scraperType = "HttpRegex";
    [ObservableProperty] private string _scraperUrl = "";
    [ObservableProperty] private string _scraperPattern = "";
    [ObservableProperty] private string _versionTransform = "";
    [ObservableProperty] private string _testVersionResult = "";

    // Step 3: Download
    [ObservableProperty] private string _downloadUrl = "";
    [ObservableProperty] private string _installerFileName = "";
    [ObservableProperty] private string _installerType = "EXE";
    [ObservableProperty] private string _installerFramework = "";

    // Step 4: Install/Uninstall
    [ObservableProperty] private string _installArgs = "";
    [ObservableProperty] private string _uninstallArgs = "";
    [ObservableProperty] private string _installContext = "Machine";

    // Step 5: Review
    [ObservableProperty] private string _reviewSummary = "";

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public string[] ArchitectureOptions { get; } = ["x64", "x86"];
    public string[] ScraperTypeOptions { get; } = ["HttpRegex", "JsonQuery", "FileVersion"];
    public string[] InstallerTypeOptions { get; } = ["MSI", "EXE", "ZIP", "MSIX", "ODT"];
    public string[] InstallContextOptions { get; } = ["Machine", "User"];

    public AddAppViewModel(
        SettingsService settingsService,
        ISnackbarService snackbarService,
        INavigationService navigationService)
    {
        _settingsService = settingsService;
        _snackbarService = snackbarService;
        _navigationService = navigationService;
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep < 5)
        {
            CurrentStep++;
            UpdateStepState();
        }
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep > 1)
        {
            CurrentStep--;
            UpdateStepState();
        }
    }

    private void UpdateStepState()
    {
        CanGoBack = CurrentStep > 1;
        CanGoNext = CurrentStep < 5;
        CanSave = CurrentStep == 5;

        StepTitle = CurrentStep switch
        {
            1 => "Step 1: Basic Information",
            2 => "Step 2: Version Source",
            3 => "Step 3: Download Configuration",
            4 => "Step 4: Install & Uninstall",
            5 => "Step 5: Review & Save",
            _ => ""
        };

        if (CurrentStep == 5)
            BuildReviewSummary();
    }

    [RelayCommand]
    private async Task TestScrapeAsync()
    {
        if (string.IsNullOrWhiteSpace(ScraperUrl) || string.IsNullOrWhiteSpace(ScraperPattern))
        {
            TestVersionResult = "URL and Pattern are required.";
            return;
        }

        IsBusy = true;
        StatusText = "Testing version scrape...";
        try
        {
            var scraperConfig = new VersionScraperConfig
            {
                Type = ScraperType,
                Url = ScraperUrl,
                Pattern = ScraperPattern,
                VersionTransform = string.IsNullOrWhiteSpace(VersionTransform) ? null : VersionTransform
            };

            var registry = new ApplicationPackagerEngine.Scrapers.ScraperRegistry();
            var (raw, display) = await registry.GetLatestVersionWithTransformAsync(scraperConfig, CancellationToken.None);
            TestVersionResult = display is not null
                ? $"Version: {display}" + (raw != display ? $" (raw: {raw})" : "")
                : "No version found. Check URL and Pattern.";
        }
        catch (Exception ex)
        {
            TestVersionResult = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            StatusText = "";
        }
    }

    private void BuildReviewSummary()
    {
        ReviewSummary = $"""
            Vendor:             {Vendor}
            Application:        {ApplicationName}
            CM Name:            {(string.IsNullOrWhiteSpace(CmName) ? ApplicationName : CmName)}
            Architecture:       {Architecture}
            Vendor URL:         {VendorUrl}

            Scraper Type:       {ScraperType}
            Scraper URL:        {ScraperUrl}
            Pattern:            {ScraperPattern}
            Version Transform:  {(string.IsNullOrWhiteSpace(VersionTransform) ? "(none)" : VersionTransform)}

            Download URL:       {DownloadUrl}
            Installer Type:     {InstallerType}
            Installer Framework:{InstallerFramework}
            Install Context:    {InstallContext}

            Install Args:       {InstallArgs}
            Uninstall Args:     {UninstallArgs}
            """;
    }

    [RelayCommand]
    private void SaveDefinition()
    {
        if (string.IsNullOrWhiteSpace(Vendor) || string.IsNullOrWhiteSpace(ApplicationName))
        {
            _snackbarService.Show("Save", "Vendor and Application Name are required.",
                ControlAppearance.Danger, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(3));
            return;
        }

        var def = new AppDefinition
        {
            Schema = 1,
            Id = Guid.NewGuid().ToString("N")[..16],
            Vendor = Vendor,
            Application = ApplicationName,
            CMName = string.IsNullOrWhiteSpace(CmName) ? ApplicationName : CmName,
            Architecture = Architecture,
            VendorUrl = VendorUrl,
            InstallerType = InstallerType,
            InstallerFramework = string.IsNullOrWhiteSpace(InstallerFramework) ? null : InstallerFramework,
            InstallContext = InstallContext,
            VersionScraper = new VersionScraperConfig
            {
                Type = ScraperType,
                Url = ScraperUrl,
                Pattern = ScraperPattern,
                VersionTransform = string.IsNullOrWhiteSpace(VersionTransform) ? null : VersionTransform
            },
            DownloadUrl = DownloadUrl,
            InstallerFileName = string.IsNullOrWhiteSpace(InstallerFileName) ? null : InstallerFileName,
            InstallArgs = InstallArgs,
            UninstallArgs = UninstallArgs,
            Detection = new DetectionConfig(),
            RunningProcess = []
        };

        var definitionsRoot = AppDataPaths.UserDefinitionsRoot;
        string filePath;
        try
        {
            filePath = UserDefinitionFiles.GetPath(definitionsRoot, ApplicationName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            _snackbarService.Show("Save failed", ex.Message, ControlAppearance.Caution,
                new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
            StatusText = ex.Message;
            return;
        }

        if (File.Exists(filePath))
        {
            var message = $"A user definition named {Path.GetFileName(filePath)} already exists. Existing definitions are never overwritten.";
            _snackbarService.Show("Save refused", message, ControlAppearance.Caution,
                new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
            StatusText = message;
            return;
        }

        try
        {
            filePath = UserDefinitionFiles.SaveNew(definitionsRoot, ApplicationName, def);
        }
        catch (IOException) when (File.Exists(filePath))
        {
            var message = $"A user definition named {Path.GetFileName(filePath)} already exists. Existing definitions are never overwritten.";
            _snackbarService.Show("Save refused", message, ControlAppearance.Caution,
                new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
            StatusText = message;
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _snackbarService.Show("Save failed", ex.Message, ControlAppearance.Caution,
                new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
            StatusText = ex.Message;
            return;
        }

        _snackbarService.Show("Saved", $"Definition saved: {Path.GetFileName(filePath)}",
            ControlAppearance.Success, new SymbolIcon(SymbolRegular.Checkmark24), TimeSpan.FromSeconds(3));

        StatusText = $"Saved to {filePath}";
    }
}
