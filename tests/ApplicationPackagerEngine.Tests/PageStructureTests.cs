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

    private static XDocument Xaml(params string[] parts) => XDocument.Load(Src(parts));

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? "";

    // The header band: padding of at least 12 across and 8 down, and a gap of at least 8 to the content below it.
    [Fact]
    public void TheHeaderBandHasPaddingAndAGapToTheContent()
    {
        var doc = Xaml("Controls", "PageHeader.xaml");
        var band = doc.Descendants().Single(e => e.Name.LocalName == "Border");
        // A WPF thickness has one value (all sides), two (across, down) or four (left, top, right, bottom).
        static double[] Sides(string thickness)
        {
            var v = thickness.Split(',').Select(x => double.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return v.Length switch { 1 => [v[0], v[0], v[0], v[0]], 2 => [v[0], v[1], v[0], v[1]], _ => v };
        }
        var padding = Sides(Attr(band, "Padding"));
        Assert.True(padding[0] >= 12 && padding[2] >= 12 && padding[1] >= 8 && padding[3] >= 8, $"The band padding is {Attr(band, "Padding")}.");
        var margin = Sides(Attr(doc.Root!, "Margin"));
        Assert.True(margin[3] >= 8, $"The gap under the band is {margin[3]} px.");
    }

    // The description and the status line start where the title starts, not under the icon.
    [Fact]
    public void TheDescriptionAndTheStatusLineAlignWithTheTitle()
    {
        var doc = Xaml("Controls", "PageHeader.xaml");
        string Column(string name) => Attr(doc.Descendants().Single(e => Attr(e, "{http://schemas.microsoft.com/winfx/2006/xaml}Name") == name), "Grid.Column");
        Assert.Equal(Column("TitleText"), Column("DescriptionText"));
        Assert.Equal(Column("TitleText"), Column("StatusText"));
    }

    // The App Packager toolbar wraps by group: each child of the wrap panel is a group, so a row never breaks inside a group or starts with a separator.
    [Fact]
    public void TheAppPackagerToolbarWrapsByGroup()
    {
        var doc = Xaml("Views", "Pages", "PackagesPage.xaml");
        var toolbar = doc.Descendants().Single(e => e.Name.LocalName == "WrapPanel" && Attr(e, "{http://schemas.microsoft.com/winfx/2006/xaml}Name") == "AppPackagerToolbar");
        var groups = toolbar.Elements().Where(e => e.Name.LocalName != "WrapPanel.Resources").ToList();
        Assert.Equal(3, groups.Count);
        Assert.All(groups, g => Assert.Equal("StackPanel", g.Name.LocalName));
        Assert.All(groups, g => Assert.Equal("Horizontal", Attr(g, "Orientation")));
        Assert.All(groups, g => Assert.True(g.Elements().Count() >= 3));
    }
}
