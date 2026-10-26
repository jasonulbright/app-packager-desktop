namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>The plan of a dashboard import: what will be copied, then one confirmation.</summary>
public sealed partial class HygieneImportViewModel(string intro, string plan) : ObservableObject
{
    public const string WindowTitle = "Import from the Health Dashboard";

    public string Title => WindowTitle;
    public string Intro { get; } = intro;
    public string Plan { get; } = plan;

    /// <summary>True once the operator selected Import.</summary>
    public bool Confirmed { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Import()
    {
        Confirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Confirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
