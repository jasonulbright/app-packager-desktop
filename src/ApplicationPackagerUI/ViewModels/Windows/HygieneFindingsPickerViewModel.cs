using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Pages;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One finding the operator can check or clear before the plan is read.</summary>
public sealed partial class HygieneFindingChoice(HygieneFinding finding) : ObservableObject
{
    public HygieneFinding Finding { get; } = finding;
    public string Key { get; } = SiteHygieneLabels.Key(finding);
    public string CheckId => Finding.CheckId;
    public string ObjectText => SiteHygieneText.Target(Finding);
    public string Evidence => FindingGridRow.FirstLine(Finding.Evidence);
    public string CheckName => $"Include {Finding.ObjectName} ({Finding.ObjectKey})";

    [ObservableProperty] private bool _isChecked = true;

    public override string ToString() => $"{CheckId} {ObjectText}";
}

/// <summary>
/// The findings of a fix: the operator clears the objects that must not change, then continues to
/// the plan. Only the checked findings are checked again by the service and planned.
/// </summary>
public sealed partial class HygieneFindingsPickerViewModel : ObservableObject
{
    private readonly List<HygieneFindingChoice> _all;

    public HygieneFindingsPickerViewModel(string title, string intro, IReadOnlyList<HygieneFinding> findings)
    {
        Title = title;
        Intro = intro;
        _all = findings.Select(f => new HygieneFindingChoice(f)).ToList();
        // A check box change adjusts the count by one, so checking every shown row costs one pass.
        foreach (var choice in _all) choice.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName != nameof(HygieneFindingChoice.IsChecked) || sender is not HygieneFindingChoice changed) return;
            CheckedCount += changed.IsChecked ? 1 : -1;
            ShowCount();
        };
        Rows = _all;
        Counted();
    }

    public string Title { get; }
    public string Intro { get; }

    [ObservableProperty] private IReadOnlyList<HygieneFindingChoice> _rows;

    [ObservableProperty] private HygieneFindingChoice? _selectedRow;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private int _checkedCount;

    [ObservableProperty] private string _countText = "";

    [ObservableProperty] private string _filterText = "";

    /// <summary>The checked findings in list order after Continue; null when the operator cancelled.</summary>
    public IReadOnlyList<HygieneFinding>? Result { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnFilterTextChanged(string value)
    {
        var text = value.Trim();
        Rows = text.Length == 0
            ? _all
            : _all.Where(r => r.CheckId.Contains(text, StringComparison.OrdinalIgnoreCase) || r.ObjectText.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.Evidence.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
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
            ? $"{SiteHygieneLabels.Number(CheckedCount)} of {SiteHygieneLabels.Count(_all.Count, "object")} checked."
            : $"{SiteHygieneLabels.Number(CheckedCount)} of {SiteHygieneLabels.Count(_all.Count, "object")} checked. The filter shows {SiteHygieneLabels.Number(Rows.Count)}.";
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
        Result = _all.Where(r => r.IsChecked).Select(r => r.Finding).ToList();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
