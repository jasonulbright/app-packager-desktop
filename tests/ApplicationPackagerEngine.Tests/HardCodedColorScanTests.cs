using System.Text.RegularExpressions;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>A color that is written as a literal does not follow the theme, so these views take every color from a theme brush.</summary>
public sealed partial class HardCodedColorScanTests
{
    [GeneratedRegex("=\"(#[0-9A-Fa-f]{6,8}|White|Black)\"")]
    private static partial Regex Literal();

    private static readonly string[] Views =
    [
        Path.Combine("Controls", "ByoReplacementPanel.xaml"),
        Path.Combine("Controls", "DropReviewPanel.xaml"),
        Path.Combine("Views", "Pages", "PackagesPage.xaml")
    ];

    [Fact]
    public void TheListedViewsWriteNoColorLiteral()
    {
        var root = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");
        var found = Views.SelectMany(v => File.ReadAllLines(Path.Combine(root, v)).Select((line, i) => (v, line, n: i + 1)))
            .Where(x => Literal().IsMatch(x.line)).Select(x => $"{x.v}:{x.n}: {x.line.Trim()}").ToList();
        Assert.True(found.Count == 0, "Color literals:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }
}
