using ApplicationPackagerEngine.Services;
using ApplicationPackagerUI.ViewModels.Windows;

namespace ApplicationPackagerUI.ViewModels.Pages;

public sealed partial class SiteHygieneViewModel
{
    public const string BlockersSelectOne = "Select one finding of an application first.";
    public const string BlockersNotAnApplication = "The selected finding is not about an application. Select a finding of an application, for example APP-08 or TSQ-05.";
    public const string BlockersOneOnly = "Select one finding. The window shows one application.";

    /// <summary>
    /// The application a finding is about: the ModelName and the name. An application finding holds the ModelName as its key; a task sequence finding of
    /// TSQ-05 holds the package ID and the ModelName ("PackageID|ModelName"). Null for any other finding.
    /// </summary>
    public static (string Model, string Name)? ApplicationOf(HygieneFinding finding)
    {
        if (string.Equals(finding.ObjectType, "Application", StringComparison.OrdinalIgnoreCase) && finding.ObjectKey.Contains("/Application_", StringComparison.Ordinal))
            return (finding.ObjectKey, finding.ObjectName);
        if (string.Equals(finding.CheckId, "TSQ-05", StringComparison.OrdinalIgnoreCase) && finding.ObjectKey.IndexOf('|') is > 0 and var bar)
        {
            var name = finding.ObjectName.LastIndexOf(" -> ", StringComparison.Ordinal) is >= 0 and var arrow ? finding.ObjectName[(arrow + 4)..] : finding.ObjectKey[(bar + 1)..];
            return (finding.ObjectKey[(bar + 1)..], name);
        }
        return null;
    }

    /// <summary>The blocker window of the selected application. It reads the data of the last scan and makes no site call.</summary>
    [RelayCommand]
    private void ShowBlockers()
    {
        if (_result is null)
        {
            StatusMessage = NotScanned;
            return;
        }
        var selected = SelectedFindings;
        if (selected.Count == 0)
        {
            StatusMessage = BlockersSelectOne;
            return;
        }
        if (selected.Count > 1)
        {
            StatusMessage = BlockersOneOnly;
            return;
        }
        if (ApplicationOf(selected[0].Finding) is not { } app)
        {
            StatusMessage = BlockersNotAnApplication;
            return;
        }
        IReadOnlyList<HygieneBlocker>? blockers = null;
        if (_result.Blockers is { } map) blockers = map.TryGetValue(app.Model, out var list) ? list : [];
        Services.Dialogs.ShowBlockers(new HygieneBlockersViewModel(app.Name, app.Model, blockers, _result.BlockersNote));
        FindingsFocusRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "";
    }
}
