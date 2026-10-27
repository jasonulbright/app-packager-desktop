using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerUI.ViewModels.Pages;

public enum AddFileStage { Pick, Analyzing, Review, Result }

/// <summary>The file dialog filter of the extensions the analysis service accepts.</summary>
public static class AddFileFormats
{
    public static string DialogFilter(IReadOnlyList<string> extensions)
    {
        var patterns = string.Join(";", extensions.Select(e => "*" + e));
        return $"Installers ({patterns})|{patterns}";
    }

    public static string ExtensionList(IReadOnlyList<string> extensions) =>
        extensions.Count < 2 ? string.Join("", extensions) : string.Join(", ", extensions.Take(extensions.Count - 1)) + " or " + extensions[^1];
}

/// <summary>The file dialog of the Add from file page, so its view model runs without a window.</summary>
public interface IAddFilePrompts
{
    /// <summary>The chosen installer, or null when the user cancels.</summary>
    string? PickInstaller(string filter);
}

/// <summary>A fact the analysis read from the file; shown, not edited.</summary>
public sealed record AddFactRow(string Label, string Value, string AutomationId);

/// <summary>
/// Add from file: a local installer is analysed off the UI thread, the user reviews and edits the
/// proposals, the definition is saved and the result panel shows. Each analysis has a generation
/// number; progress and results of an older generation are dropped.
/// </summary>
public sealed partial class AddFileViewModel : ObservableObject
{
    public const string NoSilentSwitchText =
        "No silent switch could be derived. Type the silent install arguments of this installer. Test them on a test device before you deploy.";

    public static IReadOnlyList<string> InstallContextOptions { get; } = ["Machine", "User"];

    private readonly IAddFromInstallerService _service;
    private readonly IAddFilePrompts _prompts;
    private CancellationTokenSource? _operation;
    private int _generation;
    private AddAnalysisResult? _analysis;
    private bool _saving;

    public AddFileViewModel(IAddFromInstallerService service, IAddFilePrompts prompts, AddResultViewModel results)
    {
        _service = service;
        _prompts = prompts;
        Result = results;
        Result.Closed += (_, _) => Reset();
        Detection.PropertyChanged += (_, _) => RefreshReviewState();
        FormatsText = AddFileFormats.ExtensionList(service.AcceptedExtensions);
    }

    /// <summary>The folder SaveAsync writes to; the user definitions folder of the data root.</summary>
    public string DefinitionsRoot { get; init; } = AppDataPaths.UserDefinitionsRoot;

    public AddResultViewModel Result { get; }
    public DetectionEditorViewModel Detection { get; } = new();
    public ObservableCollection<ProposalRow> Rows { get; } = [];
    public ObservableCollection<AddFactRow> PatchFacts { get; } = [];
    public ObservableCollection<AddFactRow> PackageFacts { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>The accepted file types for the drop zone text.</summary>
    public string FormatsText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickStage), nameof(IsAnalyzingStage), nameof(IsReviewStage), nameof(IsResultStage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(OpenInstallerCommand))]
    private AddFileStage _stage = AddFileStage.Pick;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInstallerCommand), nameof(SaveCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _installerType = "";
    [ObservableProperty] private ProposalRow _framework = new(label: "Framework", value: "", evidence: Evidence.None, source: null);
    [ObservableProperty] private bool _hasPredicted;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _predictedAccepted;

    [ObservableProperty] private bool _showReviewWarning;
    [ObservableProperty] private string _reviewWarning = "";
    [ObservableProperty] private bool _showNoSilentSwitch;
    [ObservableProperty] private bool _hasPatch;
    [ObservableProperty] private string _patchWarning = "";
    [ObservableProperty] private bool _hasPackage;
    [ObservableProperty] private string _trustWarning = "";
    [ObservableProperty] private bool _hasTrustWarning;

    public bool IsPickStage => Stage == AddFileStage.Pick;
    public bool IsAnalyzingStage => Stage == AddFileStage.Analyzing;
    public bool IsReviewStage => Stage == AddFileStage.Review;
    public bool IsResultStage => Stage == AddFileStage.Result;

    private bool CanOpen() => !IsBusy && Stage != AddFileStage.Result;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenInstaller()
    {
        var path = _prompts.PickInstaller(AddFileFormats.DialogFilter(_service.AcceptedExtensions));
        if (path is not null) _ = AnalyzeFileAsync(path);
    }

    /// <summary>A drop on this page or on the App Packager grid. One installer is analysed at a time.</summary>
    public void DropFiles(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0 || Stage == AddFileStage.Result) return;
        if (paths.Count > 1)
        {
            StatusText = $"{paths.Count} files were dropped. Drop one installer at a time.";
            return;
        }
        _ = AnalyzeFileAsync(paths[0]);
    }

    /// <summary>Starts the analysis of one file.</summary>
    [RelayCommand]
    private async Task AnalyzeFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Stage == AddFileStage.Result) return;
        if (_saving)
        {
            StatusText = "The definition is being saved. Add the next file when the save ends.";
            return;
        }
        if (_service.RefusalReason(path) is { } refusal)
        {
            StatusText = refusal;
            return;
        }

        _operation?.Cancel();
        var operation = _operation = new CancellationTokenSource();
        var generation = ++_generation;
        var context = SynchronizationContext.Current;

        ClearReview();
        FilePath = path;
        Stage = AddFileStage.Analyzing;
        IsBusy = true;
        StatusText = $"Analyzing {Path.GetFileName(path)}.";
        ProgressText = "";
        var progress = new CurrentProgress(report: text =>
        {
            void Show()
            {
                if (generation == _generation) ProgressText = text;
            }
            if (context is null) Show(); else context.Post(_ => Show(), null);
        });

        AddAnalysisResult? analysis = null;
        string? failure = null;
        try
        {
            analysis = await Task.Run(() => _service.AnalyzeAsync(installerPath: path, progress: progress, ct: operation.Token), operation.Token);
        }
        catch (OperationCanceledException) { failure = "The analysis was cancelled."; }
        catch (AddFromInstallerRefusedException ex) { failure = ex.Message; }
        catch (Exception ex) { failure = "The analysis failed: " + ex.Message; }

        if (generation != _generation) return;
        IsBusy = false;
        ProgressText = "";
        if (analysis is null)
        {
            Stage = AddFileStage.Pick;
            StatusText = failure ?? "";
            return;
        }
        LoadReview(analysis);
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _operation?.Cancel();

    /// <summary>Leaves the review without saving.</summary>
    [RelayCommand]
    private void Discard() => Reset();

    private void LoadReview(AddAnalysisResult analysis)
    {
        _analysis = analysis;
        InstallerType = string.IsNullOrWhiteSpace(analysis.InstallerTypeName) ? analysis.InstallerType : analysis.InstallerTypeName!;
        Framework = new ProposalRow(label: "Framework", value: analysis.Framework.Value, evidence: analysis.Framework.Evidence, source: analysis.Framework.Source);
        Rows.Clear();
        Rows.Add(Row("Vendor", analysis.Vendor, "txtAddFileVendor"));
        Rows.Add(Row("Name", analysis.Name, "txtAddFileName"));
        Rows.Add(Row("Version", analysis.Version, "txtAddFileVersion"));
        Rows.Add(Row("Install arguments", analysis.InstallArguments, "txtAddFileInstallArguments"));
        Rows.Add(Row("Uninstall arguments", analysis.UninstallArguments, "txtAddFileUninstallArguments"));
        if (analysis.UninstallCommand is { } command) Rows.Add(Row("Uninstall command", command, "txtAddFileUninstallCommand"));
        Rows.Add(new ProposalRow(label: "Install context", value: analysis.InstallContext.Value, evidence: analysis.InstallContext.Evidence,
            source: analysis.InstallContext.Source, options: InstallContextOptions, automationId: "cboAddFileInstallContext"));
        if (analysis.InstallModes.Count > 1)
            Rows.Add(new ProposalRow(label: "Install for", value: analysis.DefaultInstallMode ?? analysis.InstallModes[0], evidence: Evidence.Confirmed,
                source: null, options: analysis.InstallModes, automationId: "cboAddFileInstallMode"));
        if (analysis.Architecture is { } architecture) Rows.Add(Row("Architecture", architecture, "txtAddFileArchitecture", isReadOnly: true));
        if (analysis.ProductCode is { Value: { Length: > 0 } } productCode) Rows.Add(Row("Product code", productCode, "txtAddFileProductCode", isReadOnly: true));
        if (analysis.UninstallRegistryKey is { } key) Rows.Add(Row("Uninstall registry key", key, "txtAddFileUninstallKey", isReadOnly: true));
        foreach (var row in Rows) row.PropertyChanged += (_, _) => RefreshReviewState();

        LoadPatch(analysis.Patch);
        LoadPackage(analysis.Package);
        Notes.Clear();
        foreach (var note in analysis.Notes) Notes.Add(note);
        Detection.Load(analysis.Detection);
        PredictedAccepted = false;
        Stage = AddFileStage.Review;
        StatusText = $"Review the proposed values for {Path.GetFileName(analysis.FilePath)}.";
        RefreshReviewState();
    }

    private void LoadPatch(PatchFacts? patch)
    {
        PatchFacts.Clear();
        HasPatch = patch is not null;
        PatchWarning = patch?.BaseProductWarning ?? "";
        if (patch is null) return;
        PatchFacts.Add(new AddFactRow(Label: "Patch code", Value: patch.PatchCode, AutomationId: "txtAddFilePatchCode"));
        PatchFacts.Add(new AddFactRow(Label: "Title", Value: patch.Title, AutomationId: "txtAddFilePatchTitle"));
        PatchFacts.Add(new AddFactRow(Label: "Target product codes", Value: string.Join(Environment.NewLine, patch.TargetProductCodes), AutomationId: "txtAddFilePatchTargets"));
        if (!string.IsNullOrWhiteSpace(patch.TargetProductName))
            PatchFacts.Add(new AddFactRow(Label: "Target product", Value: patch.TargetProductName!, AutomationId: "txtAddFilePatchProduct"));
        if (!string.IsNullOrWhiteSpace(patch.PostPatchVersion))
            PatchFacts.Add(new AddFactRow(Label: "Version after the patch", Value: patch.PostPatchVersion!, AutomationId: "txtAddFilePatchVersion"));
        if (!string.IsNullOrWhiteSpace(patch.Classification))
            PatchFacts.Add(new AddFactRow(Label: "Classification", Value: patch.Classification!, AutomationId: "txtAddFilePatchClassification"));
    }

    private void LoadPackage(PackageFacts? package)
    {
        PackageFacts.Clear();
        HasPackage = package is not null;
        TrustWarning = package?.TrustWarning ?? "";
        HasTrustWarning = TrustWarning.Length > 0;
        if (package is null) return;
        PackageFacts.Add(new AddFactRow(Label: "Identity name", Value: package.IdentityName, AutomationId: "txtAddFilePackageName"));
        PackageFacts.Add(new AddFactRow(Label: "Publisher", Value: package.Publisher, AutomationId: "txtAddFilePackagePublisher"));
        if (!string.IsNullOrWhiteSpace(package.PublisherDisplayName))
            PackageFacts.Add(new AddFactRow(Label: "Publisher name", Value: package.PublisherDisplayName!, AutomationId: "txtAddFilePackagePublisherName"));
        PackageFacts.Add(new AddFactRow(Label: "Version", Value: package.Version, AutomationId: "txtAddFilePackageVersion"));
        PackageFacts.Add(new AddFactRow(Label: "Architecture", Value: package.Architecture, AutomationId: "txtAddFilePackageArchitecture"));
        PackageFacts.Add(new AddFactRow(Label: "Dependencies", Value: package.Dependencies.Count == 0 ? "None" : string.Join(Environment.NewLine, package.Dependencies),
            AutomationId: "txtAddFilePackageDependencies"));
        PackageFacts.Add(new AddFactRow(Label: "Kind", Value: package.IsBundle ? "Bundle" : "Package", AutomationId: "txtAddFilePackageKind"));
        PackageFacts.Add(new AddFactRow(Label: "Signature", Value: TrustText(package.Trust), AutomationId: "txtAddFilePackageTrust"));
        if (!string.IsNullOrWhiteSpace(package.SignerSubject))
            PackageFacts.Add(new AddFactRow(Label: "Signer", Value: package.SignerSubject!, AutomationId: "txtAddFilePackageSigner"));
    }

    public static string TrustText(PackageTrust trust) => trust switch
    {
        PackageTrust.Trusted => "Signed and trusted on this computer",
        PackageTrust.Untrusted => "Signed, not trusted on this computer",
        PackageTrust.Unsigned => "Not signed",
        PackageTrust.Invalid => "The signature is not valid",
        PackageTrust.Expired => "Signed, the certificate expired and has no valid timestamp",
        _ => "Not known"
    };

    private static ProposalRow Row<T>(string label, ProposedValue<T> proposed, string automationId, bool isReadOnly = false) =>
        new(label: label, value: proposed.Value?.ToString(), evidence: proposed.Evidence, source: proposed.Source, automationId: automationId, isReadOnly: isReadOnly);

    private ProposalRow? RowOf(string label) => Rows.FirstOrDefault(r => r.Label == label);

    private bool NeedsSilentSwitch =>
        _analysis is { InstallerType: not ("MSI" or "MSP" or "MSIX") } && RowOf("Install arguments") is { } install && string.IsNullOrWhiteSpace(install.Value);

    private void RefreshReviewState()
    {
        if (_analysis is null) return;
        HasPredicted = Rows.Any(r => r.IsPredicted) || Framework.IsPredicted || Detection.Evidence == Evidence.Predicted;
        ShowNoSilentSwitch = NeedsSilentSwitch;
        var (needsReview, reason) = ReviewState();
        ShowReviewWarning = needsReview;
        ReviewWarning = needsReview ? "This definition needs review. " + reason : "";
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The analysis verdict, a default detection rule and an install command without a value.</summary>
    private (bool NeedsReview, string Reason) ReviewState()
    {
        var reasons = new List<string>();
        if (_analysis?.NeedsReview == true && !string.IsNullOrWhiteSpace(_analysis.ReviewReason)) reasons.Add(_analysis.ReviewReason!);
        else if (_analysis?.NeedsReview == true) reasons.Add("The analysis has low confidence.");
        if (Detection.IsDefault && !reasons.Any(r => r.Contains("Detection", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("Detection needs review.");
        if (NeedsSilentSwitch && !reasons.Any(r => r.Contains("silent switch", StringComparison.OrdinalIgnoreCase)))
            reasons.Add(AddFromInstallerService.NoSilentSwitchReason);
        return (reasons.Count > 0, string.Join(" ", reasons));
    }

    private bool CanSave() => Stage == AddFileStage.Review && !IsBusy && (!HasPredicted || PredictedAccepted);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_analysis is null) return;
        var name = RowOf("Name")!.Value.Trim();
        if (name.Length == 0)
        {
            StatusText = "Type the application name.";
            return;
        }

        var (needsReview, reason) = ReviewState();
        var reviewed = _analysis with
        {
            Vendor = RowOf("Vendor")!.ToOptionalProposed(),
            Name = RowOf("Name")!.ToOptionalProposed(),
            Version = RowOf("Version")!.ToOptionalProposed(),
            InstallArguments = RowOf("Install arguments")!.ToProposed(),
            UninstallArguments = RowOf("Uninstall arguments")!.ToProposed(),
            UninstallCommand = RowOf("Uninstall command")?.ToProposed() ?? _analysis.UninstallCommand,
            InstallContext = RowOf("Install context")!.ToProposed(),
            Detection = Detection.ToProposal(),
            NeedsReview = needsReview,
            ReviewReason = needsReview ? reason : null
        };
        var options = new AddSaveOptions(DefinitionsFolder: DefinitionsRoot) { InstallMode = RowOf("Install for")?.Value };

        IsBusy = true;
        _saving = true;
        StatusText = "Saving the definition.";
        _operation = new CancellationTokenSource();
        AddSaveResult saved;
        try
        {
            saved = await Task.Run(() => _service.SaveAsync(reviewed: reviewed, options: options, ct: _operation.Token), _operation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "The save was cancelled.";
            return;
        }
        catch (AddFromInstallerRefusedException ex)
        {
            StatusText = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            StatusText = "The definition was not saved: " + ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
            _saving = false;
        }

        var review = saved.NeedsReview || needsReview;
        var why = !string.IsNullOrWhiteSpace(saved.ReviewReason) ? saved.ReviewReason : needsReview ? reason : null;
        Stage = AddFileStage.Result;
        StatusText = "";
        await Result.ShowAsync(new AddedDefinition(DefinitionId: saved.DefinitionId, DefinitionPath: saved.FilePath, DisplayName: name,
            NeedsReview: review, ReviewReason: review ? why : null, Warnings: saved.Warnings));
    }

    private void ClearReview()
    {
        _analysis = null;
        Rows.Clear();
        Notes.Clear();
        LoadPatch(null);
        LoadPackage(null);
        InstallerType = "";
        ShowReviewWarning = false;
        ShowNoSilentSwitch = false;
        ReviewWarning = "";
        HasPredicted = false;
        PredictedAccepted = false;
    }

    private void Reset()
    {
        _operation?.Cancel();
        _generation++;
        IsBusy = false;
        ProgressText = "";
        ClearReview();
        FilePath = "";
        StatusText = "";
        Stage = AddFileStage.Pick;
    }

    private sealed class CurrentProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
