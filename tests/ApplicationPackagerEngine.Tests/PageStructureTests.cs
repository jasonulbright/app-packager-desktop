using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The page structure of step 4: every page header shows an icon, and the status column shows the class by an icon as well as by color.
/// </summary>
public sealed class PageStructureTests
{
    private static string Src(params string[] parts)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        string Path_(DirectoryInfo f) => Path.Combine([f.FullName, "src", "ApplicationPackagerUI", .. parts]);
        while (folder is not null && !File.Exists(Path_(folder))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Path_(folder);
    }

    public static TheoryData<string> Pages()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(Src("Views", "Pages", "SettingsPage.xaml"))!, "*.xaml")
                     .Concat(Directory.GetFiles(Path.GetDirectoryName(Src("Views", "Windows", "CatalogWindow.xaml"))!, "*.xaml")))
            if (File.ReadAllText(file).Contains("<controls:PageHeader ", StringComparison.Ordinal)) data.Add(file);
        return data;
    }

    [Fact]
    public void ThePageHeaderPagesAreFound() => Assert.True(Pages().Count >= 13);

    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryPageHeaderShowsAnIcon(string file)
    {
        var xaml = File.ReadAllText(file);
        foreach (Match header in Regex.Matches(xaml, @"<controls:PageHeader\s[^>]*>", RegexOptions.Singleline))
            Assert.Matches(@"\sIcon=""[A-Za-z0-9]+24""", header.Value);
    }


    [Fact]
    public void TheStatusColumnShowsTheRightIconForEachClass()
    {
        var doc = XDocument.Load(Src("Views", "Pages", "PackagesPage.xaml"));
        var style = doc.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "ui:SymbolIcon");
        var neutral = style.Elements().Single(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "Symbol");
        Assert.Equal("Circle24", (string?)neutral.Attribute("Value"));

        var bySymbol = style.Descendants().Where(e => e.Name.LocalName == "DataTrigger")
            .ToDictionary(t => (string)t.Attribute("Value")!, t => (string)t.Descendants().Single(s => s.Name.LocalName == "Setter").Attribute("Value")!);
        Assert.Equal("CheckmarkCircle24", bySymbol["Success"]);
        Assert.Equal("Warning24", bySymbol["Warning"]);
        Assert.Equal("Info24", bySymbol["Info"]);
        Assert.Equal("DismissCircle24", bySymbol["Error"]);
        Assert.Equal(4, bySymbol.Count);
    }
}
