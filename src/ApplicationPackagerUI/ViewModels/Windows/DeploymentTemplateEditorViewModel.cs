using System.Globalization;
using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>
/// The New and Edit dialog of a user template. Save hands the template to the page; the dialog
/// closes only after the page reports that the store saved it.
/// </summary>
public sealed partial class DeploymentTemplateEditorViewModel : ObservableObject
{
    public const string NameRequired = "Enter a name.";
    public const string OffsetInvalid = "Enter the deadline offset as whole hours from 0 to 8760.";

    private readonly IReadOnlyList<DeploymentTemplate> _existing;
    private readonly Func<DeploymentTemplateEditorViewModel, DeploymentTemplate, Task<bool>> _save;

    public DeploymentTemplateEditorViewModel(DeploymentTemplate? original, IReadOnlyList<DeploymentTemplate> existing,
        Func<DeploymentTemplateEditorViewModel, DeploymentTemplate, Task<bool>> save)
    {
        Original = original;
        _existing = existing;
        _save = save;
        Title = original is null ? "New template" : $"Edit template {original.Name}";
        _name = original?.Name ?? "";
        _isRequired = string.Equals(original?.Purpose, DeploymentText.Required, StringComparison.OrdinalIgnoreCase);
        _notification = DeploymentText.Notifications.FirstOrDefault(n => string.Equals(n.Value, original?.UserNotification, StringComparison.OrdinalIgnoreCase))
            ?? DeploymentText.Notifications[1];
        _utcBasis = original?.UtcBasis ?? false;
        _overrideServiceWindow = original?.OverrideServiceWindow ?? false;
        _rebootOutsideServiceWindow = original?.RebootOutsideServiceWindow ?? false;
        _allowMetered = original?.AllowMetered ?? false;
        _allowBoundaryFallback = original?.AllowBoundaryFallback ?? true;
        _allowMicrosoftUpdate = original?.AllowMicrosoftUpdate ?? false;
        _postRebootScan = original?.PostRebootScan ?? true;
        _deadlineOffsetText = (original?.DeadlineOffsetHours ?? 0).ToString(CultureInfo.InvariantCulture);
    }

    public DeploymentTemplate? Original { get; }
    public string Title { get; }
    public IReadOnlyList<DeploymentChoice> Notifications => DeploymentText.Notifications;

    public bool Saved { get; private set; }
    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isRequired;
    [ObservableProperty] private DeploymentChoice _notification;
    [ObservableProperty] private bool _utcBasis;
    [ObservableProperty] private bool _overrideServiceWindow;
    [ObservableProperty] private bool _rebootOutsideServiceWindow;
    [ObservableProperty] private bool _allowMetered;
    [ObservableProperty] private bool _allowBoundaryFallback;
    [ObservableProperty] private bool _allowMicrosoftUpdate;
    [ObservableProperty] private bool _postRebootScan;
    [ObservableProperty] private string _deadlineOffsetText;
    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    /// <summary>The template of the inputs, or null with the reason in <paramref name="problem"/>.</summary>
    public DeploymentTemplate? Build(out string? problem)
    {
        problem = null;
        var name = Name.Trim();
        var id = DeploymentFormViewModel.TemplateIdFor(name);
        if (name.Length == 0) problem = NameRequired;
        else if (id.Length == 0) problem = "Use letters or digits in the name.";
        else if (_existing.Any(t => t.IsSeeded && string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))) problem = DeploymentFormViewModel.TemplateNameTaken;
        else if (_existing.Any(t => !t.IsSeeded && string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase) && !string.Equals(t.Id, Original?.Id, StringComparison.OrdinalIgnoreCase)))
            problem = "Another user template has this name. Choose another name.";
        else if (!int.TryParse(DeadlineOffsetText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset > 8760) problem = OffsetInvalid;
        else if (!IsRequired && string.Equals(Notification.Value, DeploymentText.HideAll, StringComparison.OrdinalIgnoreCase))
            problem = "An Available template cannot hide all notifications: users could not find the deployment in Software Center.";
        else
            return new DeploymentTemplate(id, name, IsRequired ? DeploymentText.Required : DeploymentText.Available, Notification.Value, UtcBasis,
                OverrideServiceWindow, RebootOutsideServiceWindow, AllowMetered, AllowBoundaryFallback, AllowMicrosoftUpdate, PostRebootScan,
                IsRequired ? offset : 0, false);
        return null;
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (IsBusy) return;
        if (Build(out var problem) is not { } template)
        {
            ValidationMessage = problem ?? "";
            return;
        }
        ValidationMessage = "";
        IsBusy = true;
        try
        {
            if (!await _save(this, template)) return;
        }
        finally { IsBusy = false; }
        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowMessage(string message) => ValidationMessage = message;
}
