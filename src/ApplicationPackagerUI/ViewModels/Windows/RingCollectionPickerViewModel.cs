using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>A device collection of the ring collection picker; in the multi-select picker it can be checked.</summary>
public sealed partial class RingCollectionChoice(CollectionRow row) : ObservableObject
{
    public CollectionRow Row { get; } = row;
    public string Name => Row.Name;
    public string CollectionId => Row.CollectionId;
    public int MemberCount => Row.MemberCount;
    public string FolderPath => Row.FolderPath.Length == 0 ? "(root)" : Row.FolderPath;
    public bool IsBuiltIn => Row.IsBuiltIn;
    public bool CanCheck => !Row.IsBuiltIn;
    public string Kind => Row.IsBuiltIn ? "Built-in" : "Custom";

    [ObservableProperty] private bool _isChecked;

    public override string ToString() => $"{Name} ({CollectionId})";
}

/// <summary>
/// A dialog that picks device collections: filter as you type, the folder shown, built-in collections listed and refused.
/// The single picker returns the selected collection; the multi-select picker returns the checked ones in the order shown.
/// </summary>
public sealed partial class RingCollectionPickerViewModel : ObservableObject
{
    public const string NoneChecked = "Check at least one collection.";

    private readonly IReadOnlyList<RingCollectionChoice> _all;

    public RingCollectionPickerViewModel(string title, IReadOnlyList<CollectionRow> collections, string? currentId, bool multiSelect = false)
    {
        Title = title;
        MultiSelect = multiSelect;
        _all = collections.OrderBy(c => c.IsBuiltIn ? 1 : 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new RingCollectionChoice(c)).ToList();
        foreach (var row in _all) row.PropertyChanged += (_, _) => UpdateCount();
        ApplyFilter();
        SelectedCollection = Collections.FirstOrDefault(c => string.Equals(c.CollectionId, currentId, StringComparison.OrdinalIgnoreCase));
    }

    public string Title { get; }
    public bool MultiSelect { get; }
    public string PickText => MultiSelect ? "Add rings" : "Pick";
    public ObservableCollection<RingCollectionChoice> Collections { get; } = [];

    /// <summary>The collection the single picker picked; null until Pick succeeded.</summary>
    public CollectionRow? Result { get; private set; }

    /// <summary>The checked collections of the multi-select picker, in the order the list showed them at Pick.</summary>
    public IReadOnlyList<CollectionRow> Results { get; private set; } = [];

    public event EventHandler? CloseRequested;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _count = "";
    [ObservableProperty] private string _message = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickCommand))]
    private RingCollectionChoice? _selectedCollection;

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedCollectionChanged(RingCollectionChoice? value) => Message = !MultiSelect && value is { IsBuiltIn: true } ? DeploymentFormViewModel.BuiltInRefused : "";

    private void ApplyFilter()
    {
        var selected = SelectedCollection?.CollectionId;
        var text = Filter.Trim();
        Collections.Clear();
        foreach (var row in _all.Where(c => text.Length == 0 || c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                     || c.CollectionId.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Row.FolderPath.Contains(text, StringComparison.OrdinalIgnoreCase)))
            Collections.Add(row);
        SelectedCollection = Collections.FirstOrDefault(c => string.Equals(c.CollectionId, selected, StringComparison.OrdinalIgnoreCase));
        UpdateCount();
    }

    private void UpdateCount()
    {
        var shown = Collections.Count == _all.Count ? DeploymentText.Count(_all.Count, "collection") : $"{Collections.Count} of {DeploymentText.Count(_all.Count, "collection")}";
        Count = MultiSelect ? $"{shown}, {_all.Count(c => c.IsChecked)} checked" : shown;
    }

    /// <summary>Checks every collection the filter shows that is not built in.</summary>
    [RelayCommand]
    private void CheckShown()
    {
        foreach (var row in Collections.Where(c => c.CanCheck)) row.IsChecked = true;
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (var row in _all) row.IsChecked = false;
    }

    private bool CanPick() => MultiSelect || SelectedCollection is not null;

    [RelayCommand(CanExecute = nameof(CanPick))]
    private void Pick()
    {
        if (MultiSelect)
        {
            // The order of the list as shown, then the checked rows the filter hides, in the same sort order.
            var checkedRows = Collections.Concat(_all.Except(Collections)).Where(c => c.IsChecked && c.CanCheck).Select(c => c.Row).ToList();
            if (checkedRows.Count == 0)
            {
                Message = NoneChecked;
                return;
            }
            Results = checkedRows;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (SelectedCollection is not { } row) return;
        if (row.IsBuiltIn)
        {
            Message = DeploymentFormViewModel.BuiltInRefused;
            return;
        }
        Result = row.Row;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
