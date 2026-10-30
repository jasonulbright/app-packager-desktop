using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// A text box or an editable combo box cannot trim with an ellipsis, so the field that clips its value shows the whole
/// value as a tooltip.
/// </summary>
public sealed class ClippedEntryTooltipTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static IEnumerable<XElement> Elements(string relative) =>
        XDocument.Load(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", relative)).Descendants();

    private static bool Has(XElement e, string attribute, string contains) => ((string?)e.Attribute(attribute) ?? "").Contains(contains, StringComparison.Ordinal);

    [Fact]
    public void EveryDownloadRootBoxShowsItsWholePathAsATooltip()
    {
        var wrong = new List<string>();
        foreach (var file in new[] { Path.Combine("Views", "Windows", "SetupWindow.xaml"), Path.Combine("Views", "Pages", "SettingsPage.xaml") })
            foreach (var box in Elements(file).Where(e => e.Name.LocalName == "TextBox" && Has(e, "Text", "DownloadRoot")))
                if (!Has(box, "ToolTip", "DownloadRoot")) wrong.Add(file);
        Assert.True(wrong.Count == 0, "No tooltip with the whole path: " + string.Join(", ", wrong));
    }

    [Fact]
    public void TheWorkbenchApplicationAndProfileBoxesShowTheirWholeSelectionAsATooltip()
    {
        var elements = Elements(Path.Combine("Views", "Windows", "WorkbenchWindow.xaml")).ToList();
        Assert.Contains(elements, e => (string?)e.Attribute(Xaml + "Name") == "cboApplication" && Has(e, "ToolTip", "SelectedApplication"));
        Assert.Contains(elements, e => (string?)e.Attribute(Xaml + "Name") == "cboProfile" && Has(e, "ToolTip", "SelectedProfile"));
    }

    [Fact]
    public void TheMaintenanceStartDateShowsItsWholeDateAsATooltip() =>
        Assert.Contains(Elements(Path.Combine("Views", "Windows", "MaintenanceWindowEditorWindow.xaml")),
            e => e.Name.LocalName == "DatePicker" && Has(e, "ToolTip", "StartDate"));
}
