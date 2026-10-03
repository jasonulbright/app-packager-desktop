using System.Windows.Controls;
using ApplicationPackagerEngine.Models.Workbench;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.Controls;

/// <summary>
/// Review of a replacement installer for a BYO application: the identity differences
/// from the current revision, then the drop review of the new installer.
/// </summary>
public partial class ByoReplacementPanel : UserControl
{
    public ByoReplacementPanel()
    {
        InitializeComponent();
        Review.ShowSaveAsApplication = false;
        // The dialog focuses the first text box when it opens, which scrolls the identity differences out of view.
        Loaded += (_, _) => Dispatcher.BeginInvoke(Scroller.ScrollToTop, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    public void Load(DroppedInstallerAnalysis analysis, IReadOnlyList<ByoIdentityChange> changes, int revision,
        int previousRevision, string displayName, string publisher)
    {
        HeadingText.Text = $"{analysis.FileName} becomes source revision {revision} of {displayName}. Revision {previousRevision} stays stored and is not overwritten.";
        var blocking = changes.Count(c => c.Severity == ByoChangeSeverity.Blocking);
        SummaryText.Text = changes.Count == 0
            ? $"None. Installer type, architecture, product code, name and publisher match revision {previousRevision}."
            : blocking > 0
                ? $"{changes.Count} field(s) differ from revision {previousRevision}; {blocking} Blocking. The existing package does not match this installer."
                : $"{changes.Count} field(s) differ from revision {previousRevision}, for review.";
        ChangeList.ItemsSource = changes;
        Review.Load(analysis);
        Review.KeepIdentity(displayName, publisher);
        Review.SetGatedActionsText("Confirmed. Add revision is available.", "Add revision appears once this is confirmed.");
    }

    public DropReviewPanel ReviewPanel => Review;
}
