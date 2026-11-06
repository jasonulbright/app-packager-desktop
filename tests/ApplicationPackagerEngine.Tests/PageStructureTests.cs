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

    private static readonly HashSet<string> TypeScale = ["12", "14", "16", "20", "28", "32", "40", "48"];

    // The font sizes of a XAML text that are not on the type scale: 12, 14, 16, 20, 28 and the 32 of a result headline; 40 and 48 size a symbol, not text.
    internal static IEnumerable<string> OffScaleSizes(string xaml) =>
        Regex.Matches(xaml, @"FontSize=""(\d+(?:\.\d+)?)""|Property=""FontSize"" Value=""(\d+(?:\.\d+)?)""")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(size => !TypeScale.Contains(size));

    // The margin and padding attributes of a XAML text with a value that is neither 0 to 3 (hairline and optical) nor a multiple of 4.
    internal static IEnumerable<string> OffGridSpacing(string xaml) =>
        Regex.Matches(xaml, @"\b(?:Margin|Padding)=""([-0-9., ]+)""|Property=""(?:Margin|Padding)"" Value=""([-0-9., ]+)""")
            .Where(m => Regex.Matches(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, @"\d+(?:\.\d+)?")
                .Select(n => double.Parse(n.Value, System.Globalization.CultureInfo.InvariantCulture))
                .Any(v => v > 3 && v % 4 != 0))
            .Select(m => m.Value);

    [Fact]
    public void EveryFontSizeIsOnTheTypeScale()
    {
        var off = AllXaml().SelectMany(file => OffScaleSizes(File.ReadAllText(file)).Select(size => $"{Path.GetFileName(file)}: {size}")).ToList();
        Assert.True(off.Count == 0, "Font sizes off the scale: " + string.Join(", ", off));
    }

    [Fact]
    public void EveryMarginAndPaddingIsOnTheGrid()
    {
        var off = AllXaml().SelectMany(file => OffGridSpacing(File.ReadAllText(file)).Select(v => $"{Path.GetFileName(file)}: {v}")).Distinct().ToList();
        Assert.True(off.Count == 0, "Spacing off the grid: " + string.Join("; ", off));
    }

    [Fact]
    public void TheScaleAndGridChecksSeeAnOffValueAndAcceptAnOnValue()
    {
        Assert.Empty(OffScaleSizes("<TextBlock FontSize=\"14\" />"));
        Assert.Equal(["13"], OffScaleSizes("<TextBlock FontSize=\"13\" />"));
        Assert.Equal(["11"], OffScaleSizes("<Setter Property=\"FontSize\" Value=\"11\" />"));
        Assert.Empty(OffGridSpacing("<Border Margin=\"0,4,8,2\" Padding=\"12\" />"));
        Assert.Equal(["Margin=\"0,6,0,0\""], OffGridSpacing("<Border Margin=\"0,6,0,0\" />"));
        Assert.Equal(["Padding=\"10\""], OffGridSpacing("<Border Padding=\"10\" />"));
        Assert.Equal(["Property=\"Margin\" Value=\"0,10\""], OffGridSpacing("<Setter Property=\"Margin\" Value=\"0,10\" />"));
    }
}
