using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The inputs of Options have one size, from one style of the page; the theme choices are named after the theme.</summary>
public sealed class SettingsInputStyleTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace WpfUi = "http://schemas.lepo.co/wpfui/2022/xaml";

    private static XDocument Page() => XDocument.Load(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Pages", "SettingsPage.xaml"));

    [Theory]
    [InlineData("ui:TextBox")]
    [InlineData("ui:PasswordBox")]
    [InlineData("ComboBox")]
    public void OneImplicitStyleGivesEveryInputTheSameMinimumHeight(string target)
    {
        var style = Page().Descendants(Presentation + "Style").Single(s => (string?)s.Attribute("TargetType") == target && s.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Key") is null);

        var minHeight = style.Elements(Presentation + "Setter").Single(x => (string?)x.Attribute("Property") == "MinHeight");
        Assert.Equal("40", (string?)minHeight.Attribute("Value"));
    }

    [Fact]
    public void NoInputOfOptionsSetsItsOwnHeight()
    {
        var offenders = Page().Descendants()
            .Where(e => (e.Name == WpfUi + "TextBox" || e.Name == WpfUi + "PasswordBox" || e.Name == Presentation + "ComboBox")
                        && (e.Attribute("Height") is not null || e.Attribute("MinHeight") is not null || e.Attribute("MaxHeight") is not null))
            // The list of applications that One Click tracks is a list box, not a single-line input; a drop-down with a fixed height would be.
            .Select(e => $"{e.Name.LocalName}:{((System.Xml.IXmlLineInfo)e).LineNumber}").ToList();

        Assert.True(offenders.Count == 0, "These inputs set their own height; the page style sets it: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheThemeChoicesAreNamedAfterTheTheme()
    {
        var choices = Page().Descendants(Presentation + "RadioButton").Select(r => (string?)r.Attribute("Content")).Where(c => c is not null && c.EndsWith("Theme")).ToList();

        Assert.Equal(["Dark Theme", "Light Theme"], choices);
    }
}