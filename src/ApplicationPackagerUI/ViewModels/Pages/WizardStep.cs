namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>One step of the step list of a wizard.</summary>
public sealed partial class WizardStep(int number, string title, int count) : ObservableObject
{
    public int Number { get; } = number;

    public string Title { get; } = title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName), nameof(ItemStatus))]
    private bool _isCurrent;

    /// <summary>What a screen reader reads for the step, with the current one named, so the state is not shown by color alone.</summary>
    public string AutomationName => $"Step {Number} of {count}: {Title}" + (IsCurrent ? ", current step" : "");

    public string ItemStatus => IsCurrent ? "Current step" : "";
}
