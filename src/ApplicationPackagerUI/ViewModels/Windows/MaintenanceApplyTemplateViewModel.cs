using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>A target collection in the Apply template dialog.</summary>
public sealed partial class TemplateTargetRow(CollectionInfo collection, int windowCount) : ObservableObject
{
    public CollectionInfo Collection { get; } = collection;
    public string Name => Collection.Name;
    public string CollectionId => Collection.CollectionId;
    public string FolderPath => Collection.FolderPath;
    public int WindowCount { get; } = windowCount;

    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// The Apply template dialog: a template and the target collections (built-in collections
/// are not offered). Apply hands the choice to the page, which shows the change plan; the
/// dialog closes only after the page reports a write.
/// </summary>
public sealed partial class MaintenanceApplyTemplateViewModel : ObservableObject
{
    public const string TemplateRequired = "Select a template.";
    public const string TargetRequired = "Select at least one collection.";

    private readonly IReadOnlyList<TemplateTargetRow> _all;
    private readonly Func<MaintenanceApplyTemplateViewModel, Task<bool>> _apply;

    public MaintenanceApplyTemplateViewModel(
        IEnumerable<MaintenanceTemplate> templates,
        IEnumerable<CollectionInfo> collections,
        IReadOnlyDictionary<string, int> windowCounts,
        Func<MaintenanceApplyTemplateViewModel, Task<bool>> apply,
        MaintenanceTemplate? template = null,
        IEnumerable<string>? checkedCollectionIds = null)
    {
        _apply = apply;
        foreach (var t in templates) Templates.Add(t);
        SelectedTemplate = template is null ? Templates.FirstOrDefault() : Templates.FirstOrDefault(t => t.Id == template.Id) ?? template;
        var preselected = new HashSet<string>(checkedCollectionIds ?? [], StringComparer.OrdinalIgnoreCase);
        _all = collections.Where(c => !c.IsBuiltIn).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new TemplateTargetRow(c, windowCounts.TryGetValue(c.CollectionId, out var n) ? n : 0) { IsChecked = preselected.Contains(c.CollectionId) })
            .ToList();
        foreach (var row in _all)
        {
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CheckedCount));
            Targets.Add(row);
        }
    }

    public ObservableCollection<MaintenanceTemplate> Templates { get; } = [];
    public ObservableCollection<TemplateTargetRow> Targets { get; } = [];

    public bool Applied { get; private set; }
    public event EventHandler? CloseRequested;

    [ObservableProperty] private MaintenanceTemplate? _selectedTemplate;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _onlyWithoutWindow;

    /// <summary>True renames the new window on a collection that already has one with the template's name; false skips that collection.</summary>
    [ObservableProperty] private bool _renameExisting;

    [ObservableProperty] private string _validationMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isApplying;

    public int CheckedCount => _all.Count(r => r.IsChecked);

    public IReadOnlyList<CollectionInfo> CheckedCollections => _all.Where(r => r.IsChecked).Select(r => r.Collection).ToList();

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnOnlyWithoutWindowChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = Filter.Trim();
        Targets.Clear();
        foreach (var row in _all)
        {
            if (OnlyWithoutWindow && row.WindowCount > 0) continue;
            if (filter.Length > 0 && !row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !row.CollectionId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !row.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            Targets.Add(row);
        }
    }

    [RelayCommand]
    private void CheckShown()
    {
        foreach (var row in Targets) row.IsChecked = true;
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (var row in _all) row.IsChecked = false;
    }

    private bool CanApply() => !IsApplying;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        if (IsApplying) return;
        if (SelectedTemplate is null)
        {
            ValidationMessage = TemplateRequired;
            return;
        }
        if (CheckedCount == 0)
        {
            ValidationMessage = TargetRequired;
            return;
        }
        ValidationMessage = "";
        IsApplying = true;
        try
        {
            if (!await _apply(this)) return;
        }
        finally { IsApplying = false; }
        Applied = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsApplying) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowMessage(string message) => ValidationMessage = message;
}
