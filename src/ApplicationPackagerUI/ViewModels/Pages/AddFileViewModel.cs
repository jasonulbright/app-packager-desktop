using System.IO;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerEngine.Services.AddFromInstaller;

namespace ApplicationPackagerUI.ViewModels.Pages;

public enum AddFileStage { Pick, Analyzing, Review, Result }

/// <summary>The file types that Add from file analyses; each has its own flow in the analysis service.</summary>
public static class AddFileFormats
{
    public static IReadOnlyList<string> Extensions { get; } = [".msi", ".exe", ".msp", ".msix", ".appx", ".msixbundle", ".appxbundle"];

    public static string DialogFilter { get; } =
        "Installers (*.msi;*.exe;*.msp;*.msix;*.appx;*.msixbundle;*.appxbundle)|*.msi;*.exe;*.msp;*.msix;*.appx;*.msixbundle;*.appxbundle";

    public static bool IsAccepted(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string RefusalText(string path) =>
        $"{Path.GetFileName(path)} cannot be added. Add from file takes .msi, .exe, .msp, .msix, .appx, .msixbundle and .appxbundle files.";
}

/// <summary>The file dialog of the Add from file page, so its view model runs without a window.</summary>
public interface IAddFilePrompts
{
    /// <summary>The chosen installer, or null when the user cancels.</summary>
    string? PickInstaller();
}

/// <summary>
/// Add from file: a local installer is analysed off the UI thread, the user reviews and edits the
/// proposals, the definition is saved and the result panel shows. Each analysis has a generation
/// number; progress and results of an older generation are dropped.
/// </summary>
public sealed partial class AddFileViewModel : ObservableObject
{
    public static IReadOnlyList<string> InstallContextOptions { get; } = ["Machine", "User"];

    private readonly IAddFromInstallerService _service;
    private readonly IAddFilePrompts _prompts;
    private CancellationTokenSource? _operation;
    private int _generation;
    private AddAnalysisResult? _analysis;

    public AddFileViewModel(IAddFromInstallerService service, IAddFilePrompts prompts, AddResultViewModel results)
    {
        _service = service;
        _prompts = prompts;
        Result = results;
        Result.Closed += (_, _) => Reset();
    }

    /// <summary>The folder SaveAsync writes to; the user definitions folder of the data root.</summary>
    public string DefinitionsRoot { get; init; } = AppDataPaths.UserDefinitionsRoot;

    public AddResultViewModel Result { get; }
    public DetectionEditorViewModel Detection { get; } = new();
    public ObservableCollection<ProposalRow> Rows { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPickStage), nameof(IsAnalyzingStage), nameof(IsReviewStage), nameof(IsResultStage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
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

    public bool IsPickStage => Stage == AddFileStage.Pick;
    public bool IsAnalyzingStage => Stage == AddFileStage.Analyzing;
    public bool IsReviewStage => Stage == AddFileStage.Review;
    public bool IsResultStage => Stage == AddFileStage.Result;

    private bool CanOpen() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void OpenInstaller()
    {
        var path = _prompts.PickInstaller();
        if (path is not null) _ = AnalyzeFileAsync(path);
    }

    /// <summary>A drop on this page or on the App Packager grid. One installer is analysed at a time.</summary>
    public void DropFiles(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return;
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
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!AddFileFormats.IsAccepted(path))
        {
            StatusText = AddFileFormats.RefusalText(path);
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
    private void Discard()
    {
        _operation?.Cancel();
        _generation++;
        IsBusy = false;
        Reset();
    }

    private void LoadReview(AddAnalysisResult analysis)
    {
        _analysis = analysis;
        InstallerType = analysis.InstallerType;
        Framework = new ProposalRow(label: "Framework", value: analysis.Framework.Value, evidence: analysis.Framework.Evidence, source: analysis.Framework.Source);
        Rows.Clear();
        Rows.Add(Row("Vendor", analysis.Vendor, "txtAddFileVendor"));
        Rows.Add(Row("Name", analysis.Name, "txtAddFileName"));
        Rows.Add(Row("Version", analysis.Version, "txtAddFileVersion"));
        Rows.Add(Row("Install arguments", analysis.InstallArguments, "txtAddFileInstallArguments"));
        Rows.Add(Row("Uninstall arguments", analysis.UninstallArguments, "txtAddFileUninstallArguments"));
        Rows.Add(new ProposalRow(label: "Install context", value: analysis.InstallContext.Value, evidence: analysis.InstallContext.Evidence,
            source: analysis.InstallContext.Source, options: InstallContextOptions, automationId: "cboAddFileInstallContext"));
        foreach (var row in Rows) row.PropertyChanged += (_, _) => RefreshReviewState();
        Notes.Clear();
        foreach (var note in analysis.Notes) Notes.Add(note);
        Detection.Load(analysis.Detection);
        Detection.PropertyChanged += OnDetectionChanged;
        PredictedAccepted = false;
        Stage = AddFileStage.Review;
        StatusText = $"Review the proposed values for {Path.GetFileName(analysis.FilePath)}.";
        RefreshReviewState();
    }

    private static ProposalRow Row<T>(string label, ProposedValue<T> proposed, string automationId) =>
        new(label: label, value: proposed.Value?.ToString(), evidence: proposed.Evidence, source: proposed.Source, automationId: automationId);

    private void OnDetectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshReviewState();

    private void RefreshReviewState()
    {
        HasPredicted = Rows.Any(r => r.IsPredicted) || Framework.IsPredicted || Detection.Evidence == Evidence.Predicted;
        var (needsReview, reason) = ReviewState();
        ShowReviewWarning = needsReview;
        ReviewWarning = needsReview ? "This definition needs review. " + reason : "";
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The analysis verdict, plus a default detection rule and an install command without a value.</summary>
    private (bool NeedsReview, string Reason) ReviewState()
    {
        var reasons = new List<string>();
        if (_analysis?.NeedsReview == true)
            reasons.Add(string.IsNullOrWhiteSpace(_analysis.ReviewReason) ? "The analysis has low confidence." : _analysis.ReviewReason!);
        if (Detection.IsDefault && !reasons.Any(r => r.Contains("Detection", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("Detection needs review.");
        if (Rows.FirstOrDefault(r => r.Label == "Install arguments") is { } install && install.Evidence == Evidence.None && string.IsNullOrWhiteSpace(install.Value)
            && !reasons.Any(r => r.Contains("silent switch", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("No silent switch could be derived. Type the install arguments.");
        return (reasons.Count > 0, string.Join(" ", reasons));
    }

    private bool CanSave() => Stage == AddFileStage.Review && !IsBusy && (!HasPredicted || PredictedAccepted);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_analysis is null) return;
        var name = Rows.Single(r => r.Label == "Name").Value.Trim();
        if (name.Length == 0)
        {
            StatusText = "Type the application name.";
            return;
        }

        var (needsReview, reason) = ReviewState();
        var reviewed = _analysis with
        {
            Vendor = Rows.Single(r => r.Label == "Vendor").ToOptionalProposed(),
            Name = Rows.Single(r => r.Label == "Name").ToOptionalProposed(),
            Version = Rows.Single(r => r.Label == "Version").ToOptionalProposed(),
            InstallArguments = Rows.Single(r => r.Label == "Install arguments").ToProposed(),
            UninstallArguments = Rows.Single(r => r.Label == "Uninstall arguments").ToProposed(),
            InstallContext = Rows.Single(r => r.Label == "Install context").ToProposed(),
            Detection = Detection.ToProposal(),
            NeedsReview = needsReview,
            ReviewReason = needsReview ? reason : null
        };

        IsBusy = true;
        StatusText = "Saving the definition.";
        _operation = new CancellationTokenSource();
        AddSaveResult saved;
        try
        {
            var options = new AddSaveOptions(DefinitionsRoot: DefinitionsRoot, AcceptPredicted: PredictedAccepted);
            saved = await Task.Run(() => _service.SaveAsync(reviewed: reviewed, options: options, ct: _operation.Token), _operation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "The save was cancelled.";
            return;
        }
        catch (Exception ex)
        {
            StatusText = "The definition was not saved: " + ex.Message;
            return;
        }
        finally { IsBusy = false; }

        Stage = AddFileStage.Result;
        StatusText = "";
        await Result.ShowAsync(new AddedDefinition(DefinitionId: saved.DefinitionId, DefinitionPath: saved.DefinitionPath, DisplayName: name,
            NeedsReview: needsReview, ReviewReason: needsReview ? reason : null, Warnings: saved.Warnings));
    }

    private void ClearReview()
    {
        _analysis = null;
        Detection.PropertyChanged -= OnDetectionChanged;
        Rows.Clear();
        Notes.Clear();
        InstallerType = "";
        ShowReviewWarning = false;
        ReviewWarning = "";
        HasPredicted = false;
        PredictedAccepted = false;
    }

    private void Reset()
    {
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
