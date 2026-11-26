using ApplicationPackagerEngine.Models;

namespace ApplicationPackagerUI.ViewModels.Windows;

/// <summary>One row of the review grid: the application and one text for each system, in the order of <see cref="PublishPlan.Systems"/>.</summary>
public sealed record PublishReviewRow(string Application, string Version, string Checked, IReadOnlyList<string> Cells);

/// <summary>
/// The publish review window (overhaul V1, phase 5a): what Publish All will write for the checked applications, the comment of the run, and the
/// button that starts it. Back closes the window and nothing runs. The comment moves here from a dialog that asked after the click.
/// </summary>
public partial class PublishReviewViewModel : ObservableObject
{
    public PublishReviewViewModel(PublishPlan plan, string comment, IReadOnlyDictionary<string, string>? changes = null)
    {
        Plan = plan;
        Changes = changes ?? new Dictionary<string, string>();
        _comment = comment ?? "";
        Rows = plan.Rows.Select(r => new PublishReviewRow(r.App.Application, r.App.Version.Length > 0 ? r.App.Version : "Not checked",
            string.Join(", ", new[] { Changes.ContainsKey(r.App.Uid) ? "Changed" : "", r.App.Hidden ? "Outside the view" : "" }.Where(t => t.Length > 0)),
            r.Cells.Select(c => c.Text).ToList())).ToList();
    }

    /// <summary>What differs from the review the operator confirmed before; empty on the first review.</summary>
    public IReadOnlyDictionary<string, string> Changes { get; }

    /// <summary>Why this review opened again, with each difference; empty on the first review.</summary>
    public string ChangedText => Changes.Count == 0 ? ""
        : "Nothing was published. The plan changed after you confirmed it. Check it and confirm again. " + string.Join(" ", Changes.Values);

    public PublishPlan Plan { get; }

    public IReadOnlyList<PublishReviewRow> Rows { get; }

    public string CountsText => Plan.CountsText;

    public string ConfirmText => Plan.ConfirmText;

    public bool CanRun => Plan.CanRun;

    public string ScopeText => "Systems: " + (Plan.Systems.Count == 0 ? "none in use" : string.Join(", ", Plan.Systems)) + " (Settings, Systems in Use)";

    /// <summary>What is read again before the first write, and what the run does when a destination already has the application.</summary>
    public string PlanNoteText => Plan.Systems.Contains(OneClickDestinations.ConfigMgr)
        ? "Planned from the last check. ConfigMgr is read again when you confirm. If an application exists, the run asks."
        : "Planned from the last check. The settings and the versions are read again when you confirm."
          + (Plan.Systems.Contains(OneClickDestinations.Wsus)
              ? " The publish does not change an update that WSUS already has for a version. It refuses a stage that differs from it." : "");

    /// <summary>ConfigMgr and Intune write the comment to the application. WSUS does not.</summary>
    public bool HasComment => Plan.Systems.Contains(OneClickDestinations.ConfigMgr) || Plan.Systems.Contains(OneClickDestinations.Intune);

    /// <summary>Why the run cannot start: each system that is in use and not ready, with its reason. Empty when it can start.</summary>
    public string BlockedText => string.Join(" ", Plan.Blocked.Select(b => b.Destination + ": " + b.Reason));

    public bool HasBlocked => Plan.Blocked.Count > 0;

    /// <summary>"N checked applications are outside the current view. They are published too."; empty when none is.</summary>
    public string HiddenText => Plan.HiddenCount == 0 ? ""
        : $"{Plan.HiddenCount} checked {(Plan.HiddenCount == 1 ? "application is" : "applications are")} outside the current view. {(Plan.HiddenCount == 1 ? "It is" : "They are")} published too.";

    [ObservableProperty] private string _comment;

    /// <summary>True after the confirm button was pressed.</summary>
    public bool Accepted { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Accept()
    {
        if (!CanRun) return;
        Comment = Comment.Trim();
        Accepted = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Back()
    {
        Accepted = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
