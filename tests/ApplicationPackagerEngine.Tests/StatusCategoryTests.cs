using System.Xml.Linq;
using ApplicationPackagerUI.Helpers;

namespace ApplicationPackagerEngine.Tests;

/// <summary>The status column takes its color from the class of the status, and the class sets a theme brush by a style trigger.</summary>
public sealed class StatusCategoryTests
{
    [Theory]
    [InlineData("Staged", "Success")]
    [InlineData("Packaged", "Success")]
    [InlineData("Up to date", "Success")]
    [InlineData("Latest retrieved", "Success")]
    [InlineData("ConfigMgr version retrieved", "Success")]
    [InlineData("Update available", "Warning")]
    [InlineData("Update available (16 CVEs, 1 in KEV)", "Warning")]
    [InlineData("Absent from ConfigMgr", "Info")]
    [InlineData("Current newer", "Info")]
    [InlineData("Download error: timeout", "Error")]
    [InlineData("Ready", "Neutral")]
    [InlineData("Checking latest...", "Neutral")]
    [InlineData("", "Neutral")]
    [InlineData(null, "Neutral")]
    public void AStatusHasOneColorClass(string? status, string expected) => Assert.Equal(expected, StatusCategory.Of(status));

    [Fact]
    public void TheStatusCellHasOneBrushTriggerForEachClassAndNoFixedColor()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        string Relative(DirectoryInfo f) => Path.Combine(f.FullName, "src", "ApplicationPackagerUI", "Views", "Pages", "PackagesPage.xaml");
        while (folder is not null && !File.Exists(Relative(folder))) folder = folder.Parent;
        Assert.NotNull(folder);
        var doc = XDocument.Load(Relative(folder));
        var style = doc.Descendants().Single(e => e.Name.LocalName == "Style" && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "StatusText"));
        var brushes = style.Descendants().Where(e => e.Name.LocalName == "DataTrigger" && ((string?)e.Attribute("Binding") ?? "").Contains("ConverterParameter=StatusKind"))
            .ToDictionary(t => (string)t.Attribute("Value")!, t => (string)t.Descendants().Single(e => (string?)e.Attribute("Property") == "Foreground").Attribute("Value")!);
        Assert.Equal(4, brushes.Count);
        Assert.Equal("{DynamicResource MkOkBrush}", brushes["good"]);
        Assert.Equal("{DynamicResource MkWarnBrush}", brushes["warn"]);
        Assert.Equal("{DynamicResource MkAccentBrush}", brushes["info"]);
        Assert.Equal("{DynamicResource MkBadBrush}", brushes["bad"]);
    }

    [Theory]
    [InlineData("Up to date", "good")]
    [InlineData("Absent from ConfigMgr", "info")]
    [InlineData("Update available", "warn")]
    [InlineData("Stage error", "bad")]
    [InlineData("Ready", "")]
    public void EachStatusClassHasItsCellColorAndAGlyph(string status, string kind)
    {
        Assert.Equal(kind, PackagesCells.StatusKind(status));
        Assert.NotEmpty(PackagesCells.StatusIcon(status));
    }
}
