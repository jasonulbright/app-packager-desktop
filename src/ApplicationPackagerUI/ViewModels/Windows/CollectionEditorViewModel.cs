using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

public enum CollectionEditorMode
{
    New,
    Edit,
    Copy
}

/// <summary>A collection offered as a limiting collection.</summary>
public sealed record CollectionChoiceRow(CollectionRow Row)
{
    public string Text => CollectionsText.Target(Row);
    public string FolderPath => Row.FolderPath;

    public override string ToString() => Text;
}

/// <summary>
/// The New, Edit and Copy dialog. Save hands the values to the page, which shows the change
/// plan; the dialog closes only after the page reports a write.
/// </summary>
public sealed partial class CollectionEditorViewModel : ObservableObject
{
    public const string NameRequired = "Enter a name.";
    public const string NameInvalid = "The name cannot contain a line break, a tab or another control character.";
    public const string NameTooLong = "The name can have at most 255 characters.";
    public const string LimitingRequired = "Select a limiting collection.";
    public const string LimitingSelf = "A collection cannot be its own limiting collection.";
    public const string CommentInvalid = "The comment cannot contain a control character other than a line break.";
    public const string RefreshIntervalInvalid = "Enter a refresh interval of 1 to 59 minutes, 1 to 23 hours or 1 to 31 days, or leave it empty.";

    public static string NameTaken(string name) => $"A collection named {name} already exists.";

    public static IReadOnlyList<string> RefreshTypes { get; } = ["Manual", "Periodic", "Continuous", "Both"];

    public static IReadOnlyList<string> RefreshUnits { get; } = ["Minutes", "Hours", "Days"];

    /// <summary>The largest count of each unit that a ConfigMgr interval schedule holds.</summary>
    private static int MaxCount(string unit) => unit switch
    {
        "Minutes" => 59,
        "Hours" => 23,
        "Days" => 31,
        _ => 0
    };

    private readonly IReadOnlyList<CollectionRow> _collections;
    private readonly IReadOnlyList<CollectionChoiceRow> _choices;
    private readonly Func<CollectionEditorViewModel, Task<bool>> _save;

    public CollectionEditorViewModel(CollectionEditorMode mode, IReadOnlyList<CollectionRow> collections, Func<CollectionEditorViewModel, Task<bool>> save, CollectionRow? original = null)
    {
        Mode = mode;
        Original = original;
        _collections = collections;
        _save = save;
        var excluded = original is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : LimitedBy(original.CollectionId);
        _choices = collections.Where(c => !excluded.Contains(c.CollectionId))
            .OrderBy(c => c.IsBuiltIn ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CollectionChoiceRow(c)).ToList();
        foreach (var choice in _choices) LimitingChoices.Add(choice);

        if (original is null)
        {
            SelectedLimiting = _choices.FirstOrDefault(c => string.Equals(c.Row.CollectionId, "SMS00001", StringComparison.OrdinalIgnoreCase)) ?? _choices.FirstOrDefault();
            return;
        }
        _name = mode == CollectionEditorMode.Copy ? "Copy of " + original.Name : original.Name;
        _comment = original.Comment;
        _refreshType = RefreshTypes.Contains(original.RefreshType) ? original.RefreshType : "Periodic";
        CurrentInterval = CollectionsText.IntervalOf(original.RefreshSchedule);
        if (CurrentInterval is { } current)
        {
            _refreshCount = current.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _refreshUnit = current.Unit;
        }
        SelectedLimiting = _choices.FirstOrDefault(c => string.Equals(c.Row.CollectionId, original.LimitingCollectionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The collection and every collection limited by it, directly or through others: none of them can limit it.</summary>
    private HashSet<string> LimitedBy(string id)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
        for (var added = true; added;)
        {
            added = false;
            foreach (var c in _collections)
                if (!found.Contains(c.CollectionId) && found.Contains(c.LimitingCollectionId)) added |= found.Add(c.CollectionId);
        }
        return found;
    }

    public CollectionEditorMode Mode { get; }
    public CollectionRow? Original { get; }

    public string Title => Mode switch
    {
        CollectionEditorMode.Edit => "Edit collection",
        CollectionEditorMode.Copy => "Copy collection",
        _ => "New collection"
    };

    public bool CanEditProperties => Mode != CollectionEditorMode.Copy;
    public bool IsCopy => Mode == CollectionEditorMode.Copy;
    public string SourceText => Original is null ? "" : CollectionsText.Target(Original);

    public ObservableCollection<CollectionChoiceRow> LimitingChoices { get; } = [];

    public bool Saved { get; private set; }
    public event EventHandler? CloseRequested;

    /// <summary>The refresh interval of the original collection's schedule; null when it has none or one this dialog cannot show.</summary>
    public RefreshInterval? CurrentInterval { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _comment = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRefreshIntervalVisible))]
    private string _refreshType = "Periodic";

    [ObservableProperty] private string _refreshCount = "";
    [ObservableProperty] private string _refreshUnit = "Days";

    /// <summary>True for a refresh type with a periodic schedule.</summary>
    public bool IsRefreshIntervalVisible => RefreshType is "Periodic" or "Both";
    [ObservableProperty] private string _limitingFilter = "";
    [ObservableProperty] private CollectionChoiceRow? _selectedLimiting;
    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    /// <summary>The smallest window that shows every control of the Periodic editor above the footer.</summary>
    public const double MinimumWidth = 560;
    public const double MinimumHeight = 640;

    public double Width { get; set; } = 640;
    public double Height { get; set; } = 680;

    /// <summary>A remembered size, raised to the minimum; a value that is not a finite size keeps the default.</summary>
    public void Restore(double width, double height)
    {
        if (double.IsFinite(width) && width > 0) Width = Math.Max(width, MinimumWidth);
        if (double.IsFinite(height) && height > 0) Height = Math.Max(height, MinimumHeight);
    }

    public void FitTo(double workAreaWidth, double workAreaHeight)
    {
        if (double.IsFinite(workAreaWidth) && workAreaWidth > 0 && !(Width <= workAreaWidth)) Width = workAreaWidth;
        if (double.IsFinite(workAreaHeight) && workAreaHeight > 0 && !(Height <= workAreaHeight)) Height = workAreaHeight;
    }

    partial void OnLimitingFilterChanged(string value)
    {
        var filter = value.Trim();
        LimitingChoices.Clear();
        foreach (var choice in _choices)
            if (filter.Length == 0 || choice.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) || choice.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                LimitingChoices.Add(choice);
    }

    /// <summary>The selected limiting collection as the dialog shows it under the list, also while the filter hides it.</summary>
    public string SelectedLimitingText => SelectedLimiting?.Text ?? "(none)";

    partial void OnSelectedLimitingChanged(CollectionChoiceRow? value) => OnPropertyChanged(nameof(SelectedLimitingText));

    /// <summary>Why the values cannot be saved, or null.</summary>
    public string? Validate()
    {
        var name = Name.Trim();
        if (name.Length == 0) return NameRequired;
        if (name.Any(char.IsControl)) return NameInvalid;
        if (name.Length > 255) return NameTooLong;
        var self = Mode == CollectionEditorMode.Edit ? Original?.CollectionId : null;
        if (_collections.Any(c => string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase) && !string.Equals(c.CollectionId, self, StringComparison.OrdinalIgnoreCase)))
            return NameTaken(name);
        if (Mode == CollectionEditorMode.Copy) return null;
        if (Comment.Any(c => char.IsControl(c) && c is not '\r' and not '\n')) return CommentInvalid;
        if (SelectedLimiting is not { } limiting) return LimitingRequired;
        if (self is not null && string.Equals(limiting.Row.CollectionId, self, StringComparison.OrdinalIgnoreCase)) return LimitingSelf;
        if (IsRefreshIntervalVisible && RefreshCount.Trim().Length > 0 && Interval() is null) return RefreshIntervalInvalid;
        return null;
    }

    /// <summary>The interval the count and unit give; null when the count is empty or out of range.</summary>
    private RefreshInterval? Interval()
    {
        var text = RefreshCount.Trim();
        if (text.Length is 0 or > 2 || !text.All(char.IsAsciiDigit)) return null;
        var count = int.Parse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        return count >= 1 && count <= MaxCount(RefreshUnit) ? new RefreshInterval(count, RefreshUnit) : null;
    }

    /// <summary>The collection as the dialog describes it; null while the values are not valid.</summary>
    public CollectionRow? BuildRow()
    {
        if (Validate() is not null) return null;
        var name = Name.Trim();
        if (Mode == CollectionEditorMode.Copy) return Original! with { Name = name };
        var limiting = SelectedLimiting!.Row;
        var basis = Original ?? new CollectionRow("", name, "", "", "", 0, false, RefreshType, "", 0, 0, 0, 0);
        var interval = IsRefreshIntervalVisible ? Interval() : null;
        return basis with
        {
            Name = name,
            Comment = Comment.Trim(),
            RefreshType = RefreshType,
            LimitingCollectionId = limiting.CollectionId,
            LimitingCollectionName = limiting.Name,
            RefreshInterval = interval == CurrentInterval ? null : interval
        };
    }

    private bool CanSave() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (IsSaving) return;
        if (Validate() is { } problem)
        {
            ValidationMessage = problem;
            return;
        }
        ValidationMessage = "";
        IsSaving = true;
        try
        {
            if (!await _save(this)) return;
        }
        finally { IsSaving = false; }
        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsSaving) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowMessage(string message) => ValidationMessage = message;
}
