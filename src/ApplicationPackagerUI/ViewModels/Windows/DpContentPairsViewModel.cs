using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One pair the operator can check or clear before the plan is read.</summary>
public sealed partial class DpContentPairChoice(PairRow pair, bool isChecked) : ObservableObject
{
    public PairRow Pair { get; } = pair;
    public string Key { get; } = DpContentLabels.Key(pair);
    public string ContentName => Pair.ContentName;
    public string PackageId => Pair.PackageId;
    public string DpName => Pair.DpName;
    public string StateName => Pair.StateName;
    public string Status => DpContentLabels.PairLabel(Pair.State);
    public string CheckName => $"Include {Pair.ContentName} on {Pair.DpName}";

    [ObservableProperty] private bool _isChecked = isChecked;

    public override string ToString() => $"{ContentName} ({PackageId}) on {DpName}, {StateName}";
}

/// <summary>
/// The pairs of an action: the operator clears the pairs that must not change, then continues to
/// the plan. Only the checked pairs are read again and planned.
/// </summary>
public sealed partial class DpContentPairsViewModel : ObservableObject
{
    private readonly List<DpContentPairChoice> _all;

    public DpContentPairsViewModel(string title, string intro, IReadOnlyList<PairRow> pairs, IReadOnlySet<string>? checkedKeys = null)
    {
        Title = title;
        Intro = intro;
        _all = pairs.Select(p => new DpContentPairChoice(p, checkedKeys is null || checkedKeys.Contains(DpContentLabels.Key(p)))).ToList();
        // A check box change adjusts the count by one, so checking every shown row costs one pass.
        foreach (var choice in _all) choice.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName != nameof(DpContentPairChoice.IsChecked) || sender is not DpContentPairChoice changed) return;
            CheckedCount += changed.IsChecked ? 1 : -1;
            ShowCount();
        };
        Rows = _all;
        Counted();
    }

    public string Title { get; }
    public string Intro { get; }

    /// <summary>The rows the filter shows.</summary>
    [ObservableProperty] private IReadOnlyList<DpContentPairChoice> _rows;

    [ObservableProperty] private DpContentPairChoice? _selectedRow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private int _checkedCount;

    [ObservableProperty] private string _countText = "";

    [ObservableProperty] private string _filterText = "";

    /// <summary>The checked pairs in list order after Continue; null when the operator cancelled.</summary>
    public IReadOnlyList<PairRow>? Result { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnFilterTextChanged(string value)
    {
        var text = value.Trim();
        Rows = text.Length == 0
            ? _all
            : _all.Where(r => r.ContentName.Contains(text, StringComparison.OrdinalIgnoreCase) || r.PackageId.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.DpName.Contains(text, StringComparison.OrdinalIgnoreCase) || r.StateName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        Counted();
    }

    private void Counted()
    {
        CheckedCount = _all.Count(r => r.IsChecked);
        ShowCount();
    }

    private void ShowCount()
    {
        CountText = Rows.Count == _all.Count
            ? $"{DpContentLabels.Number(CheckedCount)} of {DpContentLabels.Count(_all.Count, "pair")} checked."
            : $"{DpContentLabels.Number(CheckedCount)} of {DpContentLabels.Count(_all.Count, "pair")} checked. The filter shows {DpContentLabels.Number(Rows.Count)}.";
    }

    [RelayCommand]
    private void CheckShown()
    {
        foreach (var row in Rows) row.IsChecked = true;
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var row in _all) row.IsChecked = false;
    }

    /// <summary>Checks or clears the selected row, for the keyboard.</summary>
    public void ToggleSelected()
    {
        if (SelectedRow is { } row) row.IsChecked = !row.IsChecked;
    }

    private bool CanContinue() => CheckedCount > 0;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        if (!CanContinue()) return;
        Result = _all.Where(r => r.IsChecked).Select(r => r.Pair).ToList();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
