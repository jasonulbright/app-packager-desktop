namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>A definition that Add from file or Add from web saved.</summary>
public sealed record AddedDefinition(
    string DefinitionId,
    string DefinitionPath,
    string DisplayName,
    bool NeedsReview,
    string? ReviewReason,
    IReadOnlyList<string> Warnings);

/// <summary>What the result panel does on the App Packager grid and in the Workbench.</summary>
public interface IAddResultActions
{
    /// <summary>Loads the grid again and selects the row of the definition.</summary>
    Task SelectRowAsync(string definitionId);

    /// <summary>Opens the Workbench with the definition loaded.</summary>
    void OpenWorkbench(string definitionId);

    /// <summary>Runs the stage command of the grid on the row of the definition; returns the status line.</summary>
    Task<string> StageAsync(string definitionId);
}

/// <summary>The end of both add flows: what was added, and Open in Workbench, Stage now and Done.</summary>
public sealed partial class AddResultViewModel(IAddResultActions actions) : ObservableObject
{
    private const string DefaultReason = "Detection needs review.";

    private AddedDefinition? _added;

    public event EventHandler? Closed;

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _definitionPath = "";
    [ObservableProperty] private string _reasonText = "";
    [ObservableProperty] private string _warningsText = "";
    [ObservableProperty] private string _stageStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDoneDefault))]
    private bool _isWorkbenchDefault;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StageNowCommand))]
    private bool _isStaging;

    public bool IsDoneDefault => !IsWorkbenchDefault;
    public bool HasReason => ReasonText.Length > 0;
    public bool HasWarnings => WarningsText.Length > 0;

    partial void OnReasonTextChanged(string value) => OnPropertyChanged(nameof(HasReason));
    partial void OnWarningsTextChanged(string value) => OnPropertyChanged(nameof(HasWarnings));

    public async Task ShowAsync(AddedDefinition added)
    {
        _added = added;
        Summary = $"{added.DisplayName} was added to the App Packager as a user definition.";
        DefinitionPath = added.DefinitionPath;
        IsWorkbenchDefault = added.NeedsReview;
        ReasonText = added.NeedsReview ? (string.IsNullOrWhiteSpace(added.ReviewReason) ? DefaultReason : added.ReviewReason!) : "";
        WarningsText = string.Join(Environment.NewLine, added.Warnings);
        StageStatus = "";
        IsVisible = true;
        await actions.SelectRowAsync(added.DefinitionId);
    }

    [RelayCommand]
    private void OpenInWorkbench()
    {
        if (_added is not null) actions.OpenWorkbench(_added.DefinitionId);
    }

    private bool CanStageNow() => !IsStaging;

    [RelayCommand(CanExecute = nameof(CanStageNow))]
    private async Task StageNowAsync()
    {
        if (_added is null) return;
        IsStaging = true;
        StageStatus = "Staging...";
        try { StageStatus = await actions.StageAsync(_added.DefinitionId); }
        finally { IsStaging = false; }
    }

    [RelayCommand]
    private void Done()
    {
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
