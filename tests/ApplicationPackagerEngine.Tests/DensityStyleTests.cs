using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The grids and lists of the app have one density: rows of 28 to 32 px at 14 pt (decision D-20), from shared styles.</summary>
public sealed class DensityStyleTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Styles(string file) => XDocument.Load(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Styles", file));

    private static string? Setter(XElement style, string property) =>
        (string?)style.Elements(Presentation + "Setter").SingleOrDefault(s => (string?)s.Attribute("Property") == property)?.Attribute("Value");

    [Fact]
    public void EveryGridCellAndListItemStyleHasTheSameMinimumHeightAndKeepsTheThemePadding()
    {
        var shared = Styles("Density.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "DensityCell");
        var focusable = Styles("SelectableRows.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "FocusableCell");
        var listItem = Styles("SelectableRows.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "SelectableListItem");

        foreach (var style in new[] { shared, focusable, listItem })
        {
            Assert.Null(Setter(style, "Padding"));
            Assert.Equal("28", Setter(style, "MinHeight"));
        }
    }

    [Fact]
    public void TheDensityStylesAreLoadedWithTheButtonStyles()
    {
        var merged = Styles("ButtonText.xaml").Descendants(Presentation + "ResourceDictionary").Select(d => (string?)d.Attribute("Source"));

        Assert.Contains(merged, s => s is not null && s.EndsWith("Styles/Density.xaml", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DataGrid")]
    [InlineData("ui:DataGrid")]
    public void EveryGridGetsTheDensityCellStyleFromItsImplicitStyle(string target)
    {
        var style = Styles("Density.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute("TargetType") == target && s.Attribute(Xaml + "Key") is null);

        Assert.Equal("{StaticResource DensityCell}", Setter(style, "CellStyle"));
        if (target == "ui:DataGrid") Assert.Equal("{StaticResource DensityTextCell}", Setter(style, "TextColumnElementStyle"));
    }

    [Fact]
    public void NoGridStyleSetsItsCellStyleThroughAnImplicitCellStyle()
    {
        // An implicit DataGridCell style outranks the CellStyle that a grid style sets (priority of an implicit style over a style setter), so none may exist.
        var root = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");
        var offenders = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => XDocument.Load(f).Descendants(Presentation + "Style").Any(s => (string?)s.Attribute("TargetType") == "DataGridCell" && s.Attribute(Xaml + "Key") is null && s.Parent?.Name.LocalName.EndsWith(".CellStyle", StringComparison.Ordinal) != true))
            .Select(f => Path.GetRelativePath(root, f)).ToList();

        Assert.True(offenders.Count == 0, "These files have an implicit DataGridCell style: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ACellStyleOfAColumnIsBasedOnTheDensityCell()
    {
        var root = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");
        var offenders = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => XDocument.Load(f).Descendants(Presentation + "Style")
                .Where(s => (string?)s.Attribute("TargetType") == "DataGridCell" && s.Parent?.Name.LocalName.EndsWith(".CellStyle", StringComparison.Ordinal) == true
                            && !((string?)s.Attribute("BasedOn") ?? "").Contains("DensityCell", StringComparison.Ordinal)
                            // The grids of the mock take their cell from the mock, which replaces the template and sets no minimum height.
                            && (string?)s.Attribute("BasedOn") != "{StaticResource Mk.GridCell}")
                .Select(_ => Path.GetRelativePath(root, f))).ToList();

        Assert.True(offenders.Count == 0, "A column cell style of the theme keeps the 40 px minimum height of the theme: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheCellOfTheMockGridKeepsNoMinimumHeightOfTheTheme()
    {
        var cell = Styles("Mock.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "Mk.GridCell");

        Assert.Null(cell.Attribute("BasedOn"));
        Assert.Null(Setter(cell, "MinHeight"));
        Assert.Contains(cell.Elements(Presentation + "Setter"), s => (string?)s.Attribute("Property") == "Template");
    }

    [Fact]
    public void ThePageTitleAndItsIconAreAsLargeAsTheButtonText()
    {
        var title = Styles("Typography.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "PageTitle");
        Assert.Equal("14", Setter(title, "FontSize"));

        var header = XDocument.Load(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Controls", "PageHeader.xaml"));
        var icon = header.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "IconHost");
        Assert.Equal("14", (string?)icon.Attribute("FontSize"));
    }
}
