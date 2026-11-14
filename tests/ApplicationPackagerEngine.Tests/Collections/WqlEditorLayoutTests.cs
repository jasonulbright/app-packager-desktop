using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ApplicationPackagerUI.ViewModels.Pages;
using ApplicationPackagerUI.Views.Pages;
using Wpf.Ui.Appearance;
using static ApplicationPackagerEngine.Tests.Compliance.RenderKit;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace ApplicationPackagerEngine.Tests.Collections;

/// <summary>
/// The WQL Editor tab on the 4 px grid, in both themes and at the widths of the main window: the gap from a label to its box, the
/// gaps between the blocks, the size of the query box, the alignment of the two boxes of the rule row and of the buttons, and no
/// control outside the tab.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WqlEditorLayoutTests
{
    private const double Grid = 4;
    private const double Tolerance = 0.51;

    private static Rect Bounds(FrameworkElement element, FrameworkElement root) =>
        new(element.TranslatePoint(new Point(0, 0), root), new Size(element.ActualWidth, element.ActualHeight));

    private static TextBlock Label(DependencyObject root, string text) => Descendants(root).OfType<TextBlock>().Single(t => t.Text == text);

    private static void AssertOnGrid(double gap, string what) =>
        Assert.True(Math.Abs(gap / Grid - Math.Round(gap / Grid)) * Grid < Tolerance, $"{what}: {gap} px is not a multiple of {Grid} px.");

    private static void AssertGap(double actual, double expected, string what) =>
        Assert.True(Math.Abs(actual - expected) < Tolerance, $"{what}: {actual} px, expected {expected} px.");

    [Theory]
    [InlineData(ApplicationTheme.Light, 1440 - 230, 900)]
    [InlineData(ApplicationTheme.Dark, 1440 - 230, 900)]
    [InlineData(ApplicationTheme.Light, 1024 - 230, 720)]
    [InlineData(ApplicationTheme.Dark, 1024 - 230, 720)]
    public void TheWqlEditorTabLaysOutOnTheGrid(ApplicationTheme theme, double width, double height)
    {
        WithApplication(theme, () =>
        {
            var harness = new CollectionComplianceHarness();
            var page = new CollectionCompliancePage(harness.Create(), new Compliance.ComplianceHarness().Create());
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, Content = page };
            Show(window);
            try
            {
                Run(page.OnNavigatedToAsync());
                ById<TabControl>(page, "tabCollectionCompliance").SelectedIndex = CollectionComplianceViewModel.WqlTab;
                Settle(window);
                var name = $"WQL editor {theme} {width}x{height}";

                var ruleLabel = Bounds(Label(page, "Query rule of the collection"), page);
                var rules = Bounds(ById<ComboBox>(page, "cboWqlRule"), page);
                var nameLabel = Bounds(Label(page, "Rule name"), page);
                var ruleName = Bounds(ById<FrameworkElement>(page, "txtWqlRuleName"), page);
                var query = ById<TextBox>(page, "txtWql");
                var editor = Bounds(query, page);
                var collection = Bounds(ById<FrameworkElement>(page, "txtWqlCollectionNote"), page);

                // The label of a box sits 4 px above it.
                AssertGap(rules.Top - ruleLabel.Bottom, Grid, $"{name}: label to rule box");
                AssertGap(ruleName.Top - nameLabel.Bottom, Grid, $"{name}: label to rule name box");
                // The two boxes of the rule row start at the same height, have the same height and do not touch.
                AssertGap(ruleName.Top, rules.Top, $"{name}: top of the rule name box against the rule box");
                AssertGap(ruleName.Height, rules.Height, $"{name}: height of the rule name box against the rule box");
                Assert.True(ruleName.Left - rules.Right >= 8 - Tolerance, $"{name}: the two boxes are {ruleName.Left - rules.Right} px apart.");
                AssertOnGrid(ruleName.Left - rules.Right, $"{name}: gap between the two boxes");
                // The blocks are 8 px apart.
                AssertOnGrid(ruleLabel.Top - collection.Bottom, $"{name}: collection block to rule row");
                AssertOnGrid(editor.Top - rules.Bottom, $"{name}: rule row to the query box");
                Assert.True(editor.Top - rules.Bottom >= 8 - Tolerance, $"{name}: the query box is {editor.Top - rules.Bottom} px below the rule row.");

                // The query box keeps room for a query: it shows at least five lines and 360 px.
                Assert.True(editor.Height >= 120 - Tolerance, $"{name}: the query box is {editor.Height} px high.");
                Assert.True(editor.Width >= 360 - Tolerance, $"{name}: the query box is {editor.Width} px wide.");
                Assert.True(query.ExtentHeight > 0 && query.FontSize >= 14, $"{name}: the query box font is {query.FontSize}.");

                // The buttons: one height, one top per row, 8 px apart, 8 px below the query box, none cut off or outside the page.
                var buttons = new[] { "btnValidateWql", "btnPreviewWql", "btnAddRule", "btnUpdateRule", "btnRemoveRule" }
                    .Select(id => ById<Button>(page, id)).ToList();
                var boxes = buttons.Select(b => Bounds(b, page)).ToList();
                Assert.All(boxes, b => AssertGap(b.Height, boxes[0].Height, $"{name}: button height"));
                AssertGap(boxes.Min(b => b.Top) - editor.Bottom, 8, $"{name}: query box to the first button row");
                foreach (var row in boxes.GroupBy(b => Math.Round(b.Top)))
                {
                    var ordered = row.OrderBy(b => b.Left).ToList();
                    Assert.All(ordered, b => AssertGap(b.Top, ordered[0].Top, $"{name}: tops of one button row"));
                    for (var i = 1; i < ordered.Count; i++)
                        AssertGap(ordered[i].Left - ordered[i - 1].Right, 8, $"{name}: gap between buttons");
                }
                var pageBounds = new Rect(0, 0, page.ActualWidth, page.ActualHeight);
                for (var i = 0; i < buttons.Count; i++)
                {
                    Assert.True(pageBounds.Contains(boxes[i]), $"{name}: {AutomationProperties.GetAutomationId(buttons[i])} lies outside the page.");
                    Assert.True(buttons[i].ActualWidth + buttons[i].Margin.Left + buttons[i].Margin.Right + Tolerance >= buttons[i].DesiredSize.Width, $"{name}: {AutomationProperties.GetAutomationId(buttons[i])} is cut off.");
                }
            }
            finally { window.Close(); }
        });
    }
}
