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
    [InlineData("Not found in ConfigMgr", "Info")]
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
        var triggers = doc.Descendants().Where(e => e.Name.LocalName == "DataTrigger" && ((string?)e.Attribute("Binding") ?? "").Contains("StatusCategoryConverter") && e.Descendants().Any(s => s.Name.LocalName == "Setter" && (string?)s.Attribute("Property") == "Foreground")).ToList();
        var brushes = triggers.ToDictionary(t => (string)t.Attribute("Value")!, t => (string)t.Descendants().Single(e => e.Name.LocalName == "Setter").Attribute("Value")!);
        Assert.Equal("{DynamicResource StatusSuccessBrush}", brushes["Success"]);
        Assert.Equal("{DynamicResource StatusWarningBrush}", brushes["Warning"]);
        Assert.Equal("{DynamicResource StatusInfoBrush}", brushes["Info"]);
        Assert.Equal("{DynamicResource StatusErrorBrush}", brushes["Error"]);
    }
}
