using ApplicationPackagerEngine.Models;
using ScopeSource = ApplicationPackagerEngine.Models.ScopeSource;

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

    public string ScopeText => Plan.RetryDestination.Length > 0
        ? $"Retry of a failed destination: {Plan.RetryDestination} only."
        : "Publish All: " + ApplicationPackagerEngine.Models.ScopeText.For(
            new DestinationSet(Plan.Systems.Contains(OneClickDestinations.ConfigMgr), Plan.Systems.Contains(OneClickDestinations.Wsus),
                Plan.Systems.Contains(OneClickDestinations.Intune)), ScopeSource.SystemsInUse, Plan.SiteCode);

    public string HeadingText => $"Review {Plan.Rows.Count} {(Plan.Rows.Count == 1 ? "application" : "applications")}";

    /// <summary>The site line of the window, as the footer of the main window writes it; empty when the plan has no site.</summary>
    public string SiteLine => Plan.Site.Length == 0 ? "" : SiteFooter.Format(Plan.SiteCode, Plan.Provider);

    /// <summary>"Planned from the last check at 2026-11-26 14:05." The oldest check of the listed applications is the one named.</summary>
    public string LastCheckText
    {
        get
        {
            var times = Plan.Rows.Select(r => r.App.CheckedAt).ToList();
            var unknown = times.Count(t => t is null);
            if (unknown == times.Count) return "Planned from the last check. No check time is stored.";
            var oldest = times.Where(t => t is not null).Min()!.Value.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            var text = times.Distinct().Count() == 1 ? $"Planned from the last check at {oldest}." : $"Planned from the last check at {oldest} (the oldest of the listed checks).";
            return unknown == 0 ? text : $"{text} No check time is stored for {unknown} {(unknown == 1 ? "application" : "applications")}.";
        }
    }

    /// <summary>What is read again before the first write, and what the run does when a destination already has the application.</summary>
    public string PlanNoteText => Plan.RetryDestination == OneClickDestinations.ConfigMgr
        ? LastCheckText + " ConfigMgr is read again when you confirm. The retry does not replace an application. It publishes nothing when ConfigMgr has this version."
        : Plan.Systems.Contains(OneClickDestinations.ConfigMgr)
        ? LastCheckText + " ConfigMgr is read again when you confirm. If an application exists, the run asks."
        : LastCheckText + " The settings and the versions are read again when you confirm."
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
