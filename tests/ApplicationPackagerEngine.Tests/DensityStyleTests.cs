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
    public void EveryGridCellStyleHasTheSameCompactPaddingAndMinimumHeight()
    {
        var shared = Styles("Density.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute("TargetType") == "DataGridCell" && s.Attribute(Xaml + "Key") is null);
        var focusable = Styles("SelectableRows.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "FocusableCell");
        var listItem = Styles("SelectableRows.xaml").Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "SelectableListItem");

        foreach (var style in new[] { shared, focusable, listItem })
        {
            Assert.Equal("8,4", Setter(style, "Padding"));
            Assert.Equal("28", Setter(style, "MinHeight"));
        }
    }

    [Fact]
    public void TheDensityStylesAreLoadedWithTheButtonStyles()
    {
        var merged = Styles("ButtonText.xaml").Descendants(Presentation + "ResourceDictionary").Select(d => (string?)d.Attribute("Source"));

        Assert.Contains(merged, s => s is not null && s.EndsWith("Styles/Density.xaml", StringComparison.Ordinal));
    }
}
