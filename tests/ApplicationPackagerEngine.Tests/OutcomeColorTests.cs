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

    [Theory]
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
    [InlineData("", OutcomeKind.None)]
    [InlineData(null, OutcomeKind.None)]
    public void TheTextOfACellGivesItsOutcome(string? text, OutcomeKind expected) => Assert.Equal(expected, OutcomeBrushConverter.Classify(text));

    [Theory]
    [InlineData(ApplicationTheme.Light)]
    [InlineData(ApplicationTheme.Dark)]
    public void EveryOutcomeBrushIsReadableOnTheThemeBackground(ApplicationTheme theme)
    {
        WithApplication(theme, () =>
        {
            var background = PageChromeKit.ColorOf(System.Windows.Application.Current.FindResource("ApplicationBackgroundBrush"));
            foreach (var kind in new[] { OutcomeKind.Success, OutcomeKind.Caution, OutcomeKind.Critical })
            {
                var brush = Assert.IsAssignableFrom<System.Windows.Media.SolidColorBrush>(OutcomeBrushConverter.Instance.Convert(
                    kind switch { OutcomeKind.Success => "✓ ok", OutcomeKind.Caution => "! check", _ => "✗ bad" }, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture));
                Assert.True(Contrast(brush.Color, background) >= PageChromeKit.TextContrastMinimum, $"{theme} {kind}: contrast {Contrast(brush.Color, background):0.00}.");
            }
        });
    }

    [Fact]
    public void EveryOutcomeColumnOfThePagesUsesAStatusCellStyle()
    {
        var pages = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages");
        var wrong = new List<string>();
        foreach (var name in new[] { "DpContentManagerPage", "SiteHygienePage", "PivotsScriptsPage", "PivotsResultsPanel" })
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
