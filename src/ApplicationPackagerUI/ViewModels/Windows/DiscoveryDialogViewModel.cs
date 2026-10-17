using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>What the operator chose in the Discover unmanaged software prompt.</summary>
public enum DiscoveryAction
{
    NotNow,
    EnableSelected,
    EnableAll,
    Dismiss
}

/// <summary>The prompt's result: the action and the definition ids it applies to.</summary>
public sealed record DiscoveryChoice(DiscoveryAction Action, IReadOnlyList<string> Ids)
{
    public static DiscoveryChoice NotNow { get; } = new(DiscoveryAction.NotNow, []);
}

/// <summary>One discovered application in the prompt's checklist.</summary>
public sealed partial class DiscoveryCandidateRow : ObservableObject
{
    [ObservableProperty] private bool _selected;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public required int ClientCount { get; init; }
}

/// <summary>
/// The Discover unmanaged software prompt: a checklist of applications with their client
/// counts and four buttons. A button that needs a checked application and finds none keeps
/// the prompt open, changes nothing and says so on the note line.
/// </summary>
public sealed partial class DiscoveryDialogViewModel : ObservableObject
{
    public const string NothingCheckedToDismiss = "Check the apps to leave out of later discoveries. Nothing changed.";
    public const string NothingCheckedToEnable = "Check at least one app, or click Enable all.";

    public ObservableCollection<DiscoveryCandidateRow> Rows { get; }
    public string PromptText { get; }
    public string StatusText { get; }

    [ObservableProperty] private string _noteText = "";

    public bool HasNote => NoteText.Length > 0;
    partial void OnNoteTextChanged(string value) => OnPropertyChanged(nameof(HasNote));

    /// <summary>NotNow until a button closes the prompt with another action.</summary>
    public DiscoveryChoice Choice { get; private set; } = DiscoveryChoice.NotNow;

    /// <summary>Raised when a button settles the choice; the window closes on it.</summary>
    public event EventHandler? CloseRequested;

    public DiscoveryDialogViewModel(IEnumerable<DiscoveryCandidate> candidates, string statusText)
    {
        Rows = new ObservableCollection<DiscoveryCandidateRow>(candidates
            .OrderByDescending(c => c.ClientCount)
            .ThenBy(c => c.CMName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new DiscoveryCandidateRow
            {
                Id = c.Id,
                Name = string.IsNullOrWhiteSpace(c.Application) ? c.CMName : c.Application,
                Vendor = c.Vendor,
                ClientCount = c.ClientCount
            }));
        PromptText = PromptFor(Rows.Count);
        StatusText = statusText ?? "";
    }

    public static string PromptFor(int count) =>
        $"Discovered {count} {(count == 1 ? "app" : "apps")}. Enable console view?";

    [RelayCommand]
    private void EnableSelected() => Settle(DiscoveryAction.EnableSelected, CheckedIds(), NothingCheckedToEnable);

    [RelayCommand]
    private void EnableAll() => Settle(DiscoveryAction.EnableAll, Rows.Select(r => r.Id).ToList(), NothingCheckedToEnable);

    [RelayCommand]
    private void NotNow()
    {
        Choice = DiscoveryChoice.NotNow;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DismissSelected() => Settle(DiscoveryAction.Dismiss, CheckedIds(), NothingCheckedToDismiss);

    private List<string> CheckedIds() => Rows.Where(r => r.Selected).Select(r => r.Id).ToList();

    private void Settle(DiscoveryAction action, List<string> ids, string emptyNote)
    {
        if (ids.Count == 0)
        {
            NoteText = emptyNote;
            return;
        }
        NoteText = "";
        Choice = new DiscoveryChoice(action, ids);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
