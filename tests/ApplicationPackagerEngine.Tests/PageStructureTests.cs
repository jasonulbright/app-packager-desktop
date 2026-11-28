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

    // The plain title of the mock (Compact) draws no icon; every header with a card does.
    [Theory]
    [MemberData(nameof(Pages))]
    public void EveryPageHeaderShowsAnIcon(string file)
    {
        var xaml = File.ReadAllText(file);
        foreach (Match header in Regex.Matches(xaml, @"<controls:PageHeader\s[^>]*>", RegexOptions.Singleline))
            if (!header.Value.Contains("Compact=\"True\"", StringComparison.Ordinal))
                Assert.Matches(@"\sIcon=""[A-Za-z0-9]+24""", header.Value);
    }

    [Theory]
    [InlineData("Staged", "checkmark_circle", "good")]
    [InlineData("Up to date", "checkmark_circle", "good")]
    [InlineData("Update available", "arrow_up", "warn")]
    [InlineData(ApplicationPackagerUI.Helpers.PackagesTab.NotFoundStatus, "info", "info")]
    [InlineData("Download error: timeout", "error_circle", "bad")]
    [InlineData("Ready", "clock", "")]
    public void TheStatusColumnShowsTheRightIconForEachClass(string status, string glyph, string kind)
    {
        Assert.Equal(glyph, ApplicationPackagerUI.Helpers.PackagesCells.StatusIcon(status));
        Assert.Equal(kind, ApplicationPackagerUI.Helpers.PackagesCells.StatusKind(status));
        // The icon font is read by a pack address. A test that runs first in its process must register that address scheme, else the type of the
        // icons fails for the whole run.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        _ = System.Windows.Application.Current;
        Assert.True(ApplicationPackagerUI.Controls.Mock.Ico.Knows(glyph), $"The icon font has no glyph {glyph}.");
        // The cell of the grid takes its glyph from this conversion.
        var cell = XDocument.Load(Src("Views", "Pages", "PackagesPage.xaml")).Descendants()
            .Where(e => e.Name.LocalName == "Ico" && Attr(e, "Glyph").Contains("ConverterParameter=StatusIcon", StringComparison.Ordinal));
        Assert.Single(cell);
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

    // The files that draw the approved mock images. Their sizes and offsets are those of the images, which the image proof
    // (tools/MockProof) compares; the type scale and the 4 px grid hold for every other file.
    private static readonly string[] MockFiles =
    [
        "ContextStrip.xaml", "PageHeader.xaml", "SetupChecklistPanel.xaml", "StepList.xaml", "WorkbenchSidePanel.xaml",
        "AddAppPage.xaml", "DeploymentHelperPage.xaml", "DeploymentRingsView.xaml", "PackagesPage.xaml", "SettingsPage.xaml",
        "Mock.xaml", "Mock.Dialogs.xaml", "Mock.Settings.xaml", "Mock.WebDeploy.xaml", "Shell.xaml",
        "MainWindow.xaml", "OneClickPlanWindow.xaml", "PublishResultsWindow.xaml", "PublishReviewWindow.xaml", "WorkbenchWindow.xaml"
    ];

    private static IEnumerable<string> GridXaml() => AllXaml().Where(file => !MockFiles.Contains(Path.GetFileName(file)));

    [Fact]
    public void EveryFileThatLeavesTheScaleAndTheGridDrawsTheMock()
    {
        foreach (var name in MockFiles)
        {
            var file = Assert.Single(AllXaml(), f => Path.GetFileName(f) == name);
            Assert.True(Regex.IsMatch(File.ReadAllText(file), @"\bMk[.\w]*(?:Brush|Button|Grid|:)|mk:"), $"{name} uses nothing of the mock styles.");
        }
        Assert.True(GridXaml().Count() > 30, "Too few files are left for the scale and the grid checks.");
    }

    [Fact]
    public void EveryFontSizeIsOnTheTypeScale()
    {
        var off = GridXaml().SelectMany(file => OffScaleSizes(File.ReadAllText(file)).Select(size => $"{Path.GetFileName(file)}: {size}")).ToList();
        Assert.True(off.Count == 0, "Font sizes off the scale: " + string.Join(", ", off));
    }

    [Fact]
    public void EveryMarginAndPaddingIsOnTheGrid()
    {
        var off = GridXaml().SelectMany(file => OffGridSpacing(File.ReadAllText(file)).Select(v => $"{Path.GetFileName(file)}: {v}")).Distinct().ToList();
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

    // The App Packager actions as the mock draws them: one row with the three checks, Stage, Review publish and One Click; Cancel is in
    // the line under the grid. The reports and the ways to add an application are in menus.
    [Fact]
    public void TheAppPackagerActionsAreInTheActionRowAndTheLogLine()
    {
        var doc = Xaml("Views", "Pages", "PackagesPage.xaml");
        XElement Named(string name) => doc.Descendants().Single(e => Attr(e, "{http://schemas.microsoft.com/winfx/2006/xaml}Name") == name);
        string[] Buttons(string name) => Named(name).Descendants().Where(e => e.Name.LocalName == "Button").Select(b => Attr(b, "Content")).ToArray();
        Assert.Equal(new[] { "Check latest", "Check ConfigMgr", "Check CVEs", "Stage", "Review publish", "One Click" }, Buttons("AppPackagerToolbar"));
        Assert.Equal(new[] { "Logs", "Operation history", "Cancel" }, Buttons("LogBar"));
        var commands = Named("AppPackagerToolbar").Descendants().Where(e => e.Name.LocalName == "Button").Select(b => Attr(b, "Command")).ToArray();
        Assert.Equal(new[] { "{Binding CheckLatestCommand}", "{Binding CheckMecmCommand}", "{Binding CheckCvesCommand}", "{Binding StageCommand}",
            "{Binding PublishAllCommand}", "{Binding OneClickCommand}" }, commands);
        Assert.Equal("{Binding CancelOperationCommand}", Attr(Named("btnCancelOperation"), "Command"));
        foreach (var menu in new[] { "btnMore", "btnColumns" }) Assert.NotEmpty(Attr(Named(menu), "Click"));
        // The per-system publish buttons are gone (D-47), and so are the buttons that moved into a menu.
        var all = doc.Descendants().Where(e => e.Name.LocalName == "Button").Select(b => Attr(b, "Content")).ToList();
        foreach (var gone in new[] { "CVE Report", "Ops History", "Export Check Results", "Import From URL",
                     "Convert from ConfigMgr", "Discover unmanaged software", "Supersedence", "WSUS Updates", "Publish to ConfigMgr", "Publish to Intune", "Publish to WSUS" })
            Assert.DoesNotContain(gone, all);
    }

    private static IReadOnlyList<XElement> ToolbarGroups(XDocument doc, string key, string value) =>
        doc.Descendants().Single(e => e.Name.LocalName == "WrapPanel" && (Attr(e, key) == value))
            .Elements().Where(e => e.Name.LocalName != "WrapPanel.Resources").ToList();

    [Fact]
    public void TheInstallerAnalysisAndCollectionToolbarsWrapByGroup()
    {
        var analysis = ToolbarGroups(Xaml("Views", "Pages", "InstallerAnalysisPage.xaml"), "AutomationProperties.AutomationId", "pnlAnalysisTools");
        var collections = ToolbarGroups(Xaml("Views", "Pages", "CollectionCompliancePage.xaml"), "{http://schemas.microsoft.com/winfx/2006/xaml}Name", "pnlCollectionTools");
        foreach (var groups in new[] { analysis, collections })
        {
            Assert.Equal(3, groups.Count);
            Assert.All(groups, g => Assert.Equal("WrapPanel", g.Name.LocalName));
            Assert.All(groups, g => Assert.Equal("Horizontal", Attr(g, "Orientation")));
        }
    }

    [Fact]
    public void ThePivotsLibrariesHaveHeadings()
    {
        var texts = Xaml("Views", "Pages", "PivotsScriptsPage.xaml").Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => Attr(e, "Text")).ToList();
        Assert.Contains("Query library", texts);
        Assert.Contains("Script library", texts);
    }

    // Keyboard focus stays visible. A style of the mock may drop the focus rectangle only when its own template draws the focus, or when
    // it is the grid row, whose focused cell draws it.
    [Fact]
    public void NoStyleRemovesTheFocusVisual()
    {
        var off = new List<string>();
        foreach (var file in AllXaml())
        {
            var all = XDocument.Load(file, LoadOptions.SetLineInfo).Descendants().ToList();
            var removed = all.Where(e => Attr(e, "FocusVisualStyle") == "{x:Null}"
                || (e.Name.LocalName == "Setter" && Attr(e, "Property") == "FocusVisualStyle" && Attr(e, "Value") == "{x:Null}"));
            foreach (var element in removed)
            {
                var style = element.Name.LocalName == "Setter" ? element.Parent! : element;
                if (Attr(style, "{http://schemas.microsoft.com/winfx/2006/xaml}Key") == "Mk.GridRow" || DrawsTheKeyboardFocus(style)) continue;
                off.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)element).LineNumber}");
            }
        }
        Assert.True(off.Count == 0, "A style removes the focus visual: " + string.Join(", ", off));
        // The grid cell draws the focus that the row style leaves to it.
        var cell = Xaml("Views", "Styles", "Mock.xaml").Descendants()
            .Single(e => e.Name.LocalName == "Style" && Attr(e, "{http://schemas.microsoft.com/winfx/2006/xaml}Key") == "Mk.GridCell");
        Assert.True(DrawsTheKeyboardFocus(cell), "The grid cell does not show the keyboard focus.");
    }

    private static bool DrawsTheKeyboardFocus(XElement style) =>
        style.Descendants().Any(t => t.Name.LocalName == "Trigger" && Attr(t, "Property") == "IsKeyboardFocused" && Attr(t, "Value") == "True"
                                     && t.Elements().Any(s => Attr(s, "Property") is "BorderBrush" or "BorderThickness"));

    // True when a XAML text sets a FocusVisualStyle to {x:Null}, as an attribute or in a setter.
    internal static bool RemovesTheFocusVisual(string xaml) =>
        Regex.IsMatch(xaml, @"FocusVisualStyle""?\s*(?:=\s*|Value=\s*)""\{x:Null\}""");

    [Fact]
    public void TheFocusCheckSeesARemovedFocusVisualAndAcceptsAKeptOne()
    {
        Assert.True(RemovesTheFocusVisual("<Button FocusVisualStyle=\"{x:Null}\" />"));
        Assert.True(RemovesTheFocusVisual("<Setter Property=\"FocusVisualStyle\" Value=\"{x:Null}\" />"));
        Assert.False(RemovesTheFocusVisual("<Setter Property=\"FocusVisualStyle\" Value=\"{DynamicResource DefaultControlFocusVisualStyle}\" />"));
        Assert.False(RemovesTheFocusVisual("<Button Focusable=\"False\" />"));
    }
}
