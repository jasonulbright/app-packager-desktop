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

    private static IEnumerable<string> AllXaml()
    {
        var root = Path.GetDirectoryName(Src("ApplicationPackagerUI.csproj"))!;
        return Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
    }

    // The type scale: 12, 14, 16, 20, 28 and the 32 of a result headline; 40 and 48 size a symbol, not text.
    [Fact]
    public void EveryFontSizeIsOnTheTypeScale()
    {
        var scale = new HashSet<string> { "12", "14", "16", "20", "28", "32", "40", "48" };
        var off = new List<string>();
        foreach (var file in AllXaml())
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"FontSize=""(d+(?:.d+)?)""|Property=""FontSize"" Value=""(d+(?:.d+)?)"""))
            {
                var size = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!scale.Contains(size)) off.Add($"{Path.GetFileName(file)}: {size}");
            }
        Assert.True(off.Count == 0, "Font sizes off the scale: " + string.Join(", ", off));
    }

    // The spacing grid: a margin or padding value is 0 to 3 (hairline and optical) or a multiple of 4.
    [Fact]
    public void EveryMarginAndPaddingIsOnTheGrid()
    {
        var off = new List<string>();
        foreach (var file in AllXaml())
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"(?:Margin|Padding)=""([-0-9., ]+)""|Property=""(?:Margin|Padding)"" Value=""([-0-9., ]+)"""))
                foreach (Match n in Regex.Matches(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, @"d+(?:.d+)?"))
                {
                    var v = double.Parse(n.Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (v > 3 && v % 4 != 0) off.Add($"{Path.GetFileName(file)}: {m.Value}");
                }
        Assert.True(off.Count == 0, "Spacing off the grid: " + string.Join("; ", off.Distinct()));
    }
}
