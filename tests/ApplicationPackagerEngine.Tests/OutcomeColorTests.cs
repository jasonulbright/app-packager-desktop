using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;
using ApplicationPackagerUI.Helpers;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The status cells of the DP Content, Site Hygiene and Pivots grids take their color from the outcome, with theme brushes.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OutcomeColorTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly string[] OutcomeHeaders = ["Status", "State", "Result", "Verified"];

    [WindowTheory]
    [InlineData("✓ Installed", OutcomeKind.Success)]
    [InlineData("✓ Verified", OutcomeKind.Success)]
    [InlineData("✗ Failed", OutcomeKind.Critical)]
    [InlineData("✖ Failed", OutcomeKind.Critical)]
    [InlineData("✖ Deleted", OutcomeKind.Critical)]
    [InlineData("! Not verified", OutcomeKind.Caution)]
    [InlineData("▲ Warning", OutcomeKind.Caution)]
    [InlineData("⋯ In progress", OutcomeKind.None)]
    [InlineData("? Not known", OutcomeKind.None)]
    [InlineData("Failed", OutcomeKind.Critical)]
    [InlineData("Timed out", OutcomeKind.Critical)]
    [InlineData("Install Failed", OutcomeKind.Critical)]
    [InlineData("Completed", OutcomeKind.Success)]
    [InlineData("Partial", OutcomeKind.Caution)]
    [InlineData("Changed", OutcomeKind.Success)]
    [InlineData("Requested", OutcomeKind.Success)]
    [InlineData("Unchanged", OutcomeKind.None)]
    [InlineData("", OutcomeKind.None)]
    [InlineData(null, OutcomeKind.None)]
    public void TheTextOfACellGivesItsOutcome(string? text, OutcomeKind expected) => Assert.Equal(expected, OutcomeKindConverter.Classify(text));

    [WindowTheory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryOutcomeBrushIsReadableOnTheThemeBackground(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var background = PageChromeKit.ColorOf(System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush"));
            foreach (var status in new[] { "✓ ok", "! check", "✗ bad" })
            {
                var window = new System.Windows.Window();
                try
                {
                    var color = PageChromeKit.ColorOf(RenderStatusCell(window, "StatusCell", status).Foreground);
                    Assert.True(Contrast(color, background) >= PageChromeKit.TextContrastMinimum, $"{theme} {status}: contrast {Contrast(color, background):0.00}.");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    public sealed record StatusRow(string Status);

    private static System.Windows.Controls.TextBlock RenderStatusCell(System.Windows.Window window, string styleKey, string status)
    {
        var rows = new[] { new StatusRow(status) };
        var grid = new System.Windows.Controls.DataGrid { AutoGenerateColumns = false, ItemsSource = rows };
        var styles = new System.Windows.ResourceDictionary { Source = new Uri("pack://application:,,,/ApplicationPackagerUI;component/Views/Styles/SelectableRows.xaml", UriKind.Absolute) };
        window.Resources.MergedDictionaries.Add(styles);
        grid.Columns.Add(new System.Windows.Controls.DataGridTextColumn
        {
            Binding = new System.Windows.Data.Binding(nameof(StatusRow.Status)),
            ElementStyle = (System.Windows.Style)styles[styleKey]
        });
        window.Content = grid;
        Show(window);
        return Descendants(grid).OfType<System.Windows.Controls.TextBlock>().Single(t => t.Text == status);
    }

    [WindowTheory]
    [InlineData("StatusCell", "✓ Installed", "SystemFillColorSuccessBrush")]
    [InlineData("StatusCell", "! Not verified", "SystemFillColorCautionBrush")]
    [InlineData("StatusCell", "✗ Failed", "SystemFillColorCriticalBrush")]
    [InlineData("StatusCell", "⋯ In progress", "TextFillColorPrimaryBrush")]
    public void TheColorOfAStatusCellFollowsAThemeChangeAtRuntime(string styleKey, string status, string brushKey)
    {
        WithApplication(ApplicationTheme.Light, () =>
        {
            var window = new System.Windows.Window();
            try
            {
                var cell = RenderStatusCell(window, styleKey, status);
                var light = PageChromeKit.ColorOf(cell.Foreground);
                Assert.Equal(PageChromeKit.ColorOf(System.Windows.Application.Current.FindResource(brushKey)), light);

                var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
                var themes = merged.OfType<Wpf.Ui.Markup.ThemesDictionary>().Single();
                merged[merged.IndexOf(themes)] = new Wpf.Ui.Markup.ThemesDictionary { Theme = ApplicationTheme.Dark };
                Settle(window);

                var dark = PageChromeKit.ColorOf(cell.Foreground);
                Assert.Equal(PageChromeKit.ColorOf(System.Windows.Application.Current.FindResource(brushKey)), dark);
                Assert.NotEqual(light, dark);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [WindowFact]
    public void EveryOutcomeColumnOfThePagesUsesAStatusCellStyle()
    {
        var pages = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages");
        var wrong = new List<string>();
        foreach (var name in new[] { "DpContentManagerPage", "SiteHygienePage", "PivotsScriptsPage", "PivotsResultsPanel", "ComplianceSectionView", "CollectionCompliancePage", "DeploymentHelperPage", "DeploymentRingsView", "MaintenanceWindowsPage" })
        {
            var file = Path.Combine(pages, name + ".xaml");
            foreach (var column in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants(Presentation + "DataGridTextColumn"))
            {
                if (!OutcomeHeaders.Contains((string?)column.Attribute("Header") ?? "")) continue;
                if (((string?)column.Attribute("ElementStyle") ?? "").Contains("StatusCell", StringComparison.Ordinal)) continue;
                wrong.Add($"{name}.xaml:{((System.Xml.IXmlLineInfo)column).LineNumber} {(string?)column.Attribute("Header")}");
            }
        }
        Assert.True(wrong.Count == 0, "These outcome columns show no outcome color: " + string.Join(", ", wrong));
    }
}
