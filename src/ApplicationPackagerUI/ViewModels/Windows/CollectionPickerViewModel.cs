using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>A collection the operator can check in the picker.</summary>
public sealed partial class PickerRow(CollectionRow row) : ObservableObject
{
    public CollectionRow Row { get; } = row;
    public string Name => Row.Name;
    public string CollectionId => Row.CollectionId;
    public string FolderPath => Row.FolderPath;
    public string Text => CollectionsText.Target(Row);

    [ObservableProperty] private bool _isChecked;

    public override string ToString() => Text;
}

/// <summary>
/// Checks one or more collections, for example the targets of a template or of an include rule.
/// Accept hands the choice to the page, which shows the change plan; the dialog closes only
/// after the page reports a write.
/// </summary>
public sealed partial class CollectionPickerViewModel : ObservableObject
{
    public const string TargetRequired = "Check at least one collection.";

    private readonly IReadOnlyList<PickerRow> _all;
    private readonly Func<CollectionPickerViewModel, Task<bool>> _accept;

    public CollectionPickerViewModel(string title, string prompt, string actionText, IEnumerable<CollectionRow> collections,
        Func<CollectionPickerViewModel, Task<bool>> accept)
    {
        Title = title;
        Prompt = prompt;
        ActionText = actionText;
        _accept = accept;
        _all = collections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new PickerRow(c)).ToList();
        foreach (var row in _all)
        {
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CheckedCount));
            Items.Add(row);
        }
    }

    public string Title { get; }
    public string Prompt { get; }
    public string ActionText { get; }

    public ObservableCollection<PickerRow> Items { get; } = [];

    public bool Accepted { get; private set; }
    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private bool _isBusy;

    public int CheckedCount => _all.Count(r => r.IsChecked);

    public IReadOnlyList<CollectionRow> Checked => _all.Where(r => r.IsChecked).Select(r => r.Row).ToList();

    partial void OnFilterChanged(string value)
    {
        var filter = value.Trim();
        Items.Clear();
        foreach (var row in _all)
            if (filter.Length == 0 || row.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) || row.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Items.Add(row);
    }

    [RelayCommand]
    private void CheckShown()
    {
        foreach (var row in Items) row.IsChecked = true;
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (var row in _all) row.IsChecked = false;
    }

    private bool CanAccept() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private async Task Accept()
    {
        if (IsBusy) return;
        if (CheckedCount == 0)
        {
            ValidationMessage = TargetRequired;
            return;
        }
        ValidationMessage = "";
        IsBusy = true;
        try
        {
            if (!await _accept(this)) return;
        }
        finally { IsBusy = false; }
        Accepted = true;
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

/// <summary>
/// Asks for one text, for example the name of a saved query or a list of device names. Accept
/// hands the text to the page; the dialog closes only after the page reports success.
/// </summary>
public sealed partial class TextPromptViewModel : ObservableObject
{
    public const string Required = "Enter a value.";

    private readonly Func<string, string?> _validate;
    private readonly Func<TextPromptViewModel, Task<bool>> _accept;

    public TextPromptViewModel(string title, string label, string note, string actionText, bool multiline,
        Func<string, string?> validate, Func<TextPromptViewModel, Task<bool>> accept, string text = "")
    {
        Title = title;
        Label = label;
        Note = note;
        ActionText = actionText;
        Multiline = multiline;
        _validate = validate;
        _accept = accept;
        _text = text;
    }

    public string Title { get; }
    public string Label { get; }
    public string Note { get; }
    public string ActionText { get; }
    public bool Multiline { get; }
    public bool HasNote => Note.Length > 0;

    public bool Accepted { get; private set; }
    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _text;
    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    private bool _isBusy;

    /// <summary>Why the text cannot be used, or null.</summary>
    public string? Validate() => string.IsNullOrWhiteSpace(Text) ? Required : _validate(Text);

    private bool CanAccept() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private async Task Accept()
    {
        if (IsBusy) return;
        if (Validate() is { } problem)
        {
            ValidationMessage = problem;
            return;
        }
        ValidationMessage = "";
        IsBusy = true;
        try
        {
            if (!await _accept(this)) return;
        }
        finally { IsBusy = false; }
        Accepted = true;
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
