using System.IO;
using ApplicationPackagerEngine.Models;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;
using ApplicationPackagerUI.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>
/// Add from web: a wizard that writes a user definition with a remote version source. After the
/// download step it can fetch the installer once and analyse it; the user accepts proposals into
/// the install and detection fields. Each analysis run has a generation number; progress and
/// results of an older run are dropped.
/// </summary>
public partial class AddAppViewModel : ObservableObject
{
    public const int StepCount = 7;
    public const int AnalysisStep = 4;
    public const int DetectionStep = 6;
    public const int ReviewStep = 7;

    private static readonly string[] StepTitles =
        ["Basic information", "Version source", "Download configuration", "Installer analysis", "Install and uninstall", "Detection", "Review and save"];

    private readonly SettingsService _settingsService;
    private readonly ISnackbarService _snackbarService;
    private readonly IAddFromInstallerService _service;
    private CancellationTokenSource? _analysis;
    private int _generation;
    private AddAnalysisResult? _analysisResult;
    private string? _rawVersion;
    private string? _displayVersion;
    private bool _applying;
    private bool _accepted;

    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private string _stepTitle = Title(1);
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoNext = true;
    [ObservableProperty] private bool _canSave;

    [ObservableProperty] private string _vendor = "";
    [ObservableProperty] private string _applicationName = "";
    [ObservableProperty] private string _architecture = "x64";
    [ObservableProperty] private string _vendorUrl = "";
    [ObservableProperty] private string _cmName = "";

    [ObservableProperty] private string _scraperType = "HttpRegex";
    [ObservableProperty] private string _scraperUrl = "";
    [ObservableProperty] private string _scraperPattern = "";
    [ObservableProperty] private string _versionTransform = "";
    [ObservableProperty] private string _testVersionResult = "";

    [ObservableProperty] private string _downloadUrl = "";
    [ObservableProperty] private string _installerFileName = "";
    [ObservableProperty] private string _installerType = "EXE";
    [ObservableProperty] private string _installerFramework = "";

    [ObservableProperty] private string _analysisStatus = "";
    [ObservableProperty] private string _analysisProgress = "";
    [ObservableProperty] private string _analyzedFile = "";
    [ObservableProperty] private bool _hasAnalysis;
    [ObservableProperty] private bool _acceptDetection;
    [ObservableProperty] private string _proposedDetectionText = "";
    [ObservableProperty] private string _analysisWarning = "";

    [ObservableProperty] private string _installArgs = "";
    [ObservableProperty] private string _uninstallArgs = "";
    [ObservableProperty] private string _installContext = "Machine";
    [ObservableProperty] private Evidence? _installArgsEvidence;
    [ObservableProperty] private Evidence? _uninstallArgsEvidence;
    [ObservableProperty] private Evidence? _installContextEvidence;

    [ObservableProperty] private string _reviewSummary = "";
    [ObservableProperty] private bool _showDetectionWarning;
    [ObservableProperty] private string _detectionWarning = "";
    [ObservableProperty] private bool _isResultVisible;

    [ObservableProperty] private string _statusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelAnalysisCommand), nameof(SaveDefinitionCommand), nameof(SaveAndOpenWorkbenchCommand))]
    private bool _isBusy;

    public string[] ArchitectureOptions { get; } = ["x64", "x86"];
    public string[] ScraperTypeOptions { get; } = ["HttpRegex", "JsonQuery", "FileVersion"];
    public string[] InstallerTypeOptions { get; } = ["MSI", "EXE", "ZIP", "MSIX", "ODT"];
    public string[] InstallContextOptions { get; } = ["Machine", "User"];

    public ObservableCollection<ProposalRow> AnalysisRows { get; } = [];
    public ObservableCollection<string> AnalysisNotes { get; } = [];
    public DetectionEditorViewModel Detection { get; } = new();
    public AddResultViewModel Result { get; }

    /// <summary>The folder the definition is written to.</summary>
    public string DefinitionsRoot { get; init; } = AppDataPaths.UserDefinitionsRoot;

    /// <summary>Reads the latest version with the scraper settings: the raw and the transformed version.</summary>
    public Func<VersionScraperConfig, CancellationToken, Task<(string? Raw, string? Display)>> Scrape { get; init; } =
        (config, ct) => new ApplicationPackagerEngine.Scrapers.ScraperRegistry().GetLatestVersionWithTransformAsync(config, ct);

    public string InstallArgsMarker => Marker(InstallArgsEvidence);
    public string UninstallArgsMarker => Marker(UninstallArgsEvidence);
    public string InstallContextMarker => Marker(InstallContextEvidence);
    public bool HasAnalysisWarning => AnalysisWarning.Length > 0;
    public bool IsWizardVisible => !IsResultVisible;

    public AddAppViewModel(
        SettingsService settingsService,
        ISnackbarService snackbarService,
        IAddFromInstallerService service,
        AddResultViewModel results)
    {
        _settingsService = settingsService;
        _snackbarService = snackbarService;
        _service = service;
        Result = results;
        Result.Closed += (_, _) => Reset();
        Detection.PropertyChanged += (_, _) => RefreshDetectionWarning();
        RefreshDetectionWarning();
    }

    private static string Title(int step) => $"Step {step} of {StepCount}: {StepTitles[step - 1]}";

    private static string Marker(Evidence? evidence) => evidence is { } e ? EvidenceMarkers.Text(e) : "";

    partial void OnInstallArgsEvidenceChanged(Evidence? value) => OnPropertyChanged(nameof(InstallArgsMarker));
    partial void OnUninstallArgsEvidenceChanged(Evidence? value) => OnPropertyChanged(nameof(UninstallArgsMarker));
    partial void OnInstallContextEvidenceChanged(Evidence? value) => OnPropertyChanged(nameof(InstallContextMarker));
    partial void OnAnalysisWarningChanged(string value) => OnPropertyChanged(nameof(HasAnalysisWarning));
    partial void OnIsResultVisibleChanged(bool value) => OnPropertyChanged(nameof(IsWizardVisible));

    partial void OnInstallArgsChanged(string value)
    {
        if (!_applying && InstallArgsEvidence is not null) InstallArgsEvidence = Evidence.Manual;
    }

    partial void OnUninstallArgsChanged(string value)
    {
        if (!_applying && UninstallArgsEvidence is not null) UninstallArgsEvidence = Evidence.Manual;
    }

    partial void OnInstallContextChanged(string value)
    {
        if (!_applying && InstallContextEvidence is not null) InstallContextEvidence = Evidence.Manual;
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep >= StepCount) return;
        CurrentStep++;
        UpdateStepState();
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep <= 1) return;
        CurrentStep--;
        UpdateStepState();
    }

    private void UpdateStepState()
    {
        CanGoBack = CurrentStep > 1;
        CanGoNext = CurrentStep < StepCount;
        CanSave = CurrentStep == ReviewStep;
        StepTitle = Title(CurrentStep);
        if (CurrentStep == ReviewStep) BuildReviewSummary();
    }

    [RelayCommand]
    private async Task TestScrapeAsync()
    {
        if (string.IsNullOrWhiteSpace(ScraperUrl) || string.IsNullOrWhiteSpace(ScraperPattern))
        {
            TestVersionResult = "Type the scraper URL and the pattern.";
            return;
        }

        StatusText = "Reading the latest version.";
        try
        {
            var (raw, display) = await Scrape(ScraperConfig(), CancellationToken.None);
            _rawVersion = raw;
            _displayVersion = display;
            TestVersionResult = display is not null
                ? $"Version: {display}" + (raw != display ? $" (raw: {raw})" : "")
                : "No version was found. Check the URL and the pattern.";
        }
        catch (Exception ex)
        {
            TestVersionResult = $"Error: {ex.Message}";
        }
        finally
        {
            StatusText = "";
        }
    }

    private VersionScraperConfig ScraperConfig() => new()
    {
        Type = ScraperType,
        Url = ScraperUrl,
        Pattern = ScraperPattern,
        VersionTransform = string.IsNullOrWhiteSpace(VersionTransform) ? null : VersionTransform
    };

    private static bool HasVersionToken(string text) =>
        text.Contains("{version}", StringComparison.OrdinalIgnoreCase) || text.Contains("{display_version}", StringComparison.OrdinalIgnoreCase);

    private string ResolveTokens(string text) => text
        .Replace("{display_version}", _displayVersion ?? "", StringComparison.OrdinalIgnoreCase)
        .Replace("{version}", _rawVersion ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the installer once to the download root, then analyses it.</summary>
    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        var downloadRoot = _settingsService.Load().DownloadRoot;
        if (string.IsNullOrWhiteSpace(downloadRoot))
        {
            AnalysisStatus = "Set the download root in Settings. The installer is downloaded to the download root.";
            return;
        }
        if (string.IsNullOrWhiteSpace(DownloadUrl))
        {
            AnalysisStatus = "Type the download URL on step 3.";
            return;
        }
        if ((HasVersionToken(DownloadUrl) || HasVersionToken(InstallerFileName)) && _displayVersion is null)
        {
            AnalysisStatus = "Select Test Scrape on step 2 first. The download URL needs the version.";
            return;
        }

        _analysis?.Cancel();
        var operation = _analysis = new CancellationTokenSource();
        var generation = ++_generation;
        var context = SynchronizationContext.Current;
        ClearAnalysis();
        IsBusy = true;
        AnalysisStatus = "Downloading the installer.";
        var progress = new CurrentProgress(report: text =>
        {
            void Show()
            {
                if (generation == _generation) AnalysisProgress = text;
            }
            if (context is null) Show(); else context.Post(_ => Show(), null);
        });

        var request = new DownloadRequest(
            Url: HasVersionToken(DownloadUrl) ? AddFromInstallerService.ResolveDownloadUrl(DownloadUrl.Trim(), _displayVersion ?? "", _rawVersion) : DownloadUrl.Trim(),
            FileName: string.IsNullOrWhiteSpace(InstallerFileName) ? null : ResolveTokens(InstallerFileName.Trim()),
            DownloadRoot: downloadRoot);

        AddAnalysisResult? result = null;
        string? failure = null;
        try
        {
            var path = await Task.Run(() => _service.FetchInstallerAsync(request: request, progress: progress, ct: operation.Token), operation.Token);
            if (generation != _generation) return;
            AnalysisStatus = $"Analyzing {Path.GetFileName(path)}.";
            result = await Task.Run(() => _service.AnalyzeAsync(installerPath: path, progress: progress, ct: operation.Token), operation.Token);
        }
        catch (OperationCanceledException) { failure = "The analysis was cancelled."; }
        catch (AddFromInstallerRefusedException ex) { failure = ex.Message; }
        catch (Exception ex) { failure = "The analysis failed: " + ex.Message; }

        if (generation != _generation) return;
        IsBusy = false;
        AnalysisProgress = "";
        if (result is null)
        {
            AnalysisStatus = failure ?? "";
            return;
        }
        LoadAnalysis(result);
    }

    private bool CanCancelAnalysis() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancelAnalysis))]
    private void CancelAnalysis() => _analysis?.Cancel();

    private void ClearAnalysis()
    {
        _analysisResult = null;
        _accepted = false;
        HasAnalysis = false;
        AnalyzedFile = "";
        AnalysisRows.Clear();
        AnalysisNotes.Clear();
        AnalysisWarning = "";
        AcceptDetection = false;
    }

    private void LoadAnalysis(AddAnalysisResult result)
    {
        _analysisResult = result;
        AnalyzedFile = result.FilePath;
        AnalysisRows.Clear();
        AnalysisRows.Add(new ProposalRow(label: "Installer type", value: result.InstallerType, evidence: Evidence.Confirmed, source: null,
            options: InstallerTypeOptions.Contains(result.InstallerType) ? InstallerTypeOptions : null, automationId: "cboAddWebProposedType"));
        AnalysisRows.Add(Row("Framework", result.Framework, "txtAddWebProposedFramework"));
        AnalysisRows.Add(Row("Install arguments", result.InstallArguments, "txtAddWebProposedInstall"));
        AnalysisRows.Add(Row("Uninstall arguments", result.UninstallArguments, "txtAddWebProposedUninstall"));
        AnalysisRows.Add(new ProposalRow(label: "Install context", value: result.InstallContext.Value, evidence: result.InstallContext.Evidence,
            source: result.InstallContext.Source, options: InstallContextOptions, automationId: "cboAddWebProposedContext"));
        foreach (var note in result.Notes) AnalysisNotes.Add(note);
        AcceptDetection = result.Detection.Kind != DetectionKind.None;
        ProposedDetectionText = "Use the proposed detection: " + (string.IsNullOrWhiteSpace(result.Detection.Description) ? "none" : result.Detection.Description)
            + " (" + EvidenceMarkers.Text(result.Detection.Evidence) + ")";
        AnalysisWarning = result.NeedsReview
            ? "This installer needs review. " + (string.IsNullOrWhiteSpace(result.ReviewReason) ? "The analysis has low confidence." : result.ReviewReason)
            : "";
        HasAnalysis = true;
        AnalysisStatus = "Select the values to use, then select Use selected values.";
    }

    private static ProposalRow Row(string label, ProposedValue<string> proposed, string automationId) =>
        new(label: label, value: proposed.Value, evidence: proposed.Evidence, source: proposed.Source, automationId: automationId);

    /// <summary>Copies the accepted proposals into the install, uninstall and detection fields.</summary>
    [RelayCommand]
    private void AcceptProposals()
    {
        if (_analysisResult is null) return;
        _applying = true;
        try
        {
            foreach (var row in AnalysisRows.Where(r => r.IsAccepted))
            {
                switch (row.Label)
                {
                    case "Installer type" when InstallerTypeOptions.Contains(row.Value):
                        InstallerType = row.Value;
                        break;
                    case "Framework":
                        InstallerFramework = row.Value;
                        break;
                    case "Install arguments":
                        InstallArgs = row.Value;
                        InstallArgsEvidence = row.Evidence;
                        break;
                    case "Uninstall arguments":
                        UninstallArgs = row.Value;
                        UninstallArgsEvidence = row.Evidence;
                        break;
                    case "Install context" when InstallContextOptions.Contains(row.Value):
                        InstallContext = row.Value;
                        InstallContextEvidence = row.Evidence;
                        break;
                }
            }
        }
        finally { _applying = false; }
        if (AcceptDetection) Detection.Load(_analysisResult.Detection);
        _accepted = true;
        AnalysisStatus = "The selected values are in steps 5 and 6. Review them there.";
    }

    private void RefreshDetectionWarning()
    {
        ShowDetectionWarning = Detection.IsDefault;
        DetectionWarning = Detection.IsDefault
            ? "Detection is still the default: the definition has no registry key or file to detect. Set a rule on step 6, or open the definition in the Workbench after the save."
            : "";
    }

    private void BuildReviewSummary()
    {
        var detection = DetectionEditorViewModel.ToConfig(Detection.ToProposal());
        var detectionText = detection is null
            ? "(default, needs review)"
            : detection.Type == "File" ? $"File {detection.FilePath}\\{detection.FileName} ({detection.PropertyType})"
            : $"{detection.Type} {detection.RegistryHive}\\{detection.RegistryKeyTemplate}" + (detection.ValueName is null ? "" : $" [{detection.ValueName}]");
        ReviewSummary = $"""
            Vendor:              {Vendor}
            Application:         {ApplicationName}
            CM name:             {(string.IsNullOrWhiteSpace(CmName) ? ApplicationName : CmName)}
            Architecture:        {Architecture}
            Vendor URL:          {VendorUrl}

            Scraper type:        {ScraperType}
            Scraper URL:         {ScraperUrl}
            Pattern:             {ScraperPattern}
            Version transform:   {(string.IsNullOrWhiteSpace(VersionTransform) ? "(none)" : VersionTransform)}

            Download URL:        {DownloadUrl}
            Installer type:      {InstallerType}
            Installer framework: {InstallerFramework}
            Install context:     {InstallContext}

            Install arguments:   {InstallArgs}
            Uninstall arguments: {UninstallArgs}
            Detection:           {detectionText}
            """;
    }

    private bool CanSaveDefinition() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSaveDefinition))]
    private Task SaveDefinitionAsync() => SaveCoreAsync(openWorkbench: false);

    [RelayCommand(CanExecute = nameof(CanSaveDefinition))]
    private Task SaveAndOpenWorkbenchAsync() => SaveCoreAsync(openWorkbench: true);

    private async Task SaveCoreAsync(bool openWorkbench)
    {
        if (string.IsNullOrWhiteSpace(Vendor) || string.IsNullOrWhiteSpace(ApplicationName))
        {
            Warn("Save", "Type the vendor and the application name on step 1.");
            return;
        }

        var saved = _accepted && _analysisResult is not null ? await SaveAnalyzedAsync(_analysisResult) : SaveManual();
        if (saved is null) return;

        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(saved.ReviewReason)) reasons.Add(saved.ReviewReason!);
        if (_analysisResult?.NeedsReview == true && reasons.Count == 0)
            reasons.Add(string.IsNullOrWhiteSpace(_analysisResult.ReviewReason) ? "The analysis has low confidence." : _analysisResult.ReviewReason!);
        if (Detection.IsDefault && !reasons.Any(r => r.Contains("Detection", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("Detection needs review.");
        var needsReview = saved.NeedsReview || reasons.Count > 0;

        StatusText = "";
        IsResultVisible = true;
        await Result.ShowAsync(new AddedDefinition(DefinitionId: saved.DefinitionId, DefinitionPath: saved.FilePath, DisplayName: ApplicationName,
            NeedsReview: needsReview, ReviewReason: needsReview ? string.Join(" ", reasons) : null, Warnings: saved.Warnings));
        if (openWorkbench) Result.OpenInWorkbenchCommand.Execute(null);
    }

    /// <summary>
    /// Saves through the analysis service. The wizard values go in the options and win over the analysis; the
    /// detection of step 6 is the reviewed rule. Install arguments are saved as typed.
    /// </summary>
    private async Task<AddSaveResult?> SaveAnalyzedAsync(AddAnalysisResult analysis)
    {
        var version = string.IsNullOrWhiteSpace(analysis.Version.Value) && _displayVersion is not null
            ? new ProposedValue<string?>(Value: _displayVersion, Evidence: Evidence.Manual, Source: null)
            : analysis.Version;
        var reviewed = analysis with { Version = version, Detection = Detection.ToProposal() };
        var options = new AddSaveOptions(DefinitionsFolder: DefinitionsRoot)
        {
            ApplicationName = ApplicationName,
            Vendor = Vendor,
            Architecture = Architecture,
            InstallerFileName = string.IsNullOrWhiteSpace(InstallerFileName) ? null : InstallerFileName,
            InstallerType = InstallerType,
            InstallerFramework = string.IsNullOrWhiteSpace(InstallerFramework) ? null : InstallerFramework,
            InstallContext = InstallContext,
            InstallArgs = InstallArgs,
            UninstallArgs = UninstallArgs,
            DownloadUrl = DownloadUrl,
            VersionScraper = ScraperConfig(),
            VendorUrl = string.IsNullOrWhiteSpace(VendorUrl) ? null : VendorUrl,
            CMName = string.IsNullOrWhiteSpace(CmName) ? null : CmName
        };
        IsBusy = true;
        try
        {
            return await Task.Run(() => _service.SaveAsync(reviewed: reviewed, options: options, ct: CancellationToken.None));
        }
        catch (AddFromInstallerRefusedException ex)
        {
            Warn("Save refused", ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Warn("Save failed", ex.Message);
            return null;
        }
        finally { IsBusy = false; }
    }

    /// <summary>Writes the wizard fields as a definition when no analysis was accepted. A rule without a key or a file is saved as the empty default.</summary>
    private AddSaveResult? SaveManual()
    {
        var detection = DetectionEditorViewModel.ToConfig(Detection.ToProposal());
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
            VersionScraper = ScraperConfig(),
            DownloadUrl = DownloadUrl,
            InstallerFileName = string.IsNullOrWhiteSpace(InstallerFileName) ? null : InstallerFileName,
            InstallArgs = InstallArgs,
            UninstallArgs = UninstallArgs,
            Detection = detection ?? new DetectionConfig { RegistryKeyTemplate = null },
            RunningProcess = []
        };

        string filePath;
        try
        {
            filePath = UserDefinitionFiles.GetPath(DefinitionsRoot, ApplicationName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            Warn("Save failed", ex.Message);
            return null;
        }

        var exists = $"A user definition named {Path.GetFileName(filePath)} already exists. Existing definitions are never overwritten.";
        if (File.Exists(filePath))
        {
            Warn("Save refused", exists);
            return null;
        }

        try
        {
            filePath = UserDefinitionFiles.SaveNew(DefinitionsRoot, ApplicationName, def);
        }
        catch (IOException) when (File.Exists(filePath))
        {
            Warn("Save refused", exists);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Warn("Save failed", ex.Message);
            return null;
        }
        return new AddSaveResult(FilePath: filePath, DefinitionId: def.Id, Warnings: []) { NeedsReview = detection is null };
    }
    private void Warn(string title, string message)
    {
        _snackbarService.Show(title, message, ControlAppearance.Caution, new SymbolIcon(SymbolRegular.Warning24), TimeSpan.FromSeconds(4));
        StatusText = message;
    }

    /// <summary>Starts a new wizard after Done on the result panel.</summary>
    private void Reset()
    {
        _analysis?.Cancel();
        _generation++;
        IsBusy = false;
        _rawVersion = _displayVersion = null;
        Vendor = ApplicationName = VendorUrl = CmName = "";
        Architecture = "x64";
        ScraperType = "HttpRegex";
        ScraperUrl = ScraperPattern = VersionTransform = TestVersionResult = "";
        DownloadUrl = InstallerFileName = InstallerFramework = "";
        InstallerType = "EXE";
        _applying = true;
        try
        {
            InstallArgs = UninstallArgs = "";
            InstallContext = "Machine";
        }
        finally { _applying = false; }
        InstallArgsEvidence = UninstallArgsEvidence = InstallContextEvidence = null;
        ClearAnalysis();
        AnalysisStatus = AnalysisProgress = "";
        Detection.Load(new DetectionProposal(Kind: DetectionKind.None, Description: "", Values: new Dictionary<string, string>(), Evidence: Evidence.None, Source: null));
        IsResultVisible = false;
        CurrentStep = 1;
        UpdateStepState();
    }

    private sealed class CurrentProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
