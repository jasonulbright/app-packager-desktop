using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ApplicationPackagerEngine.Tests.Compliance;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using StackPanel = System.Windows.Controls.StackPanel;
using UiButton = Wpf.Ui.Controls.Button;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Overhaul V1 phase 1 (Q-126): the shared foundations of the V1 workspace. The three surfaces, the spacing scale, the page title
/// size, the glyph table and the Cancel slot are pinned here, so a later phase reads them and cannot move them by accident.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class FoundationsTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    public static TheoryData<ApplicationTheme> Themes => new() { ApplicationTheme.Light, ApplicationTheme.Dark };

    private static string Styles(string file) =>
        Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Styles", file);

    private static Dictionary<string, XElement> Entries(string file) =>
        XDocument.Load(Styles(file)).Root!.Elements().Where(e => e.Attribute(X + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!);

    private static Color C(string hex) => (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static Color Palette(ApplicationTheme theme, string key) => C(Entries($"Palette.{theme}.xaml")[key].Value.Trim());

    private static readonly string[] SurfaceKeys = ["SurfaceBaseColor", "SurfacePageColor", "SurfaceSubColor"];

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    // Surfaces

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryThemeDefinesTheThreeSurfacesAndTheirBrushes(ApplicationTheme theme)
    {
        var entries = Entries($"Palette.{theme}.xaml");
        foreach (var key in SurfaceKeys) Assert.True(entries.ContainsKey(key), $"{theme}: {key} is missing.");
        foreach (var key in new[] { "SurfaceBaseBrush", "SurfacePageBrush", "SurfaceSubBrush" })
            Assert.True(entries.ContainsKey(key), $"{theme}: {key} is missing.");
        Assert.Equal(3, SurfaceKeys.Select(k => Palette(theme, k)).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheBaseIsDarkerThanThePageSurfaceInBothThemes(ApplicationTheme theme)
    {
        // R-07, R-08: the less important surface is the darker shade, the important one the lighter shade.
        Assert.True(Luminance(Palette(theme, "SurfaceBaseColor")) < Luminance(Palette(theme, "SurfacePageColor")),
            $"{theme}: the base is not darker than the page surface.");
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextPassesFourAndAHalfOnEachSurfaceAndBordersAndTheAccentPassThree(ApplicationTheme theme)
    {
        var primary = theme == ApplicationTheme.Light ? C("#1A1A1A") : C("#FFFFFF");
        var failures = new List<string>();
        foreach (var surfaceKey in SurfaceKeys)
        {
            var surface = Palette(theme, surfaceKey);
            void Need(string name, Color color, double need)
            {
                var ratio = ThemeContrastReportTests.Ratio(color, surface);
                if (ratio < need) failures.Add($"{theme}: {name} on {surfaceKey} is {ratio:0.00}, needs {need:0.0}");
            }
            Need("primary text", primary, 4.5);
            foreach (var key in new[] { "TextFillColorTertiary", "StatusSuccessColor", "StatusWarningColor", "StatusErrorColor", "StatusInfoColor", "StatusNeutralColor", "BrandAccentColor" })
                Need(key, Palette(theme, key), 4.5);
            Need("BorderStrongColor", Palette(theme, "BorderStrongColor"), 3.0);
            Need("BrandAccentColor as a marker", Palette(theme, "BrandAccentColor"), 3.0);
        }
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheSecondaryTextOfTheLibraryPassesOnEachSurface(ApplicationTheme theme)
    {
        // The library secondary text is a translucent color of the primary text; it is read over each surface.
        var library = ThemeContrastReportTests.Load($"<ui:ThemesDictionary Theme=\"{theme}\" />");
        var secondary = ThemeContrastReportTests.Read(library, "TextFillColorSecondaryBrush");
        Assert.NotNull(secondary);
        foreach (var key in SurfaceKeys)
        {
            var surface = Palette(theme, key);
            var shown = ThemeContrastReportTests.Over(secondary!.Value, surface);
            Assert.True(ThemeContrastReportTests.Ratio(shown, surface) >= 4.5, $"{theme}: the secondary text on {key}.");
        }
    }

    // Spacing

    [Fact]
    public void TheSpacingScaleIsFourEightTwelveSixteenTwentyFourAndEveryValueIsAMultipleOfFour()
    {
        var entries = Entries("Spacing.xaml");
        foreach (var step in new[] { 4, 8, 12, 16, 24 })
        {
            Assert.Equal(step, double.Parse(entries[$"Space{step}"].Value, CultureInfo.InvariantCulture));
            Assert.Equal(step, double.Parse(entries[$"Inset{step}"].Value, CultureInfo.InvariantCulture));
        }
        Assert.Equal(10, entries.Count);
        Assert.All(entries.Values, e => Assert.Equal(0, double.Parse(e.Value, CultureInfo.InvariantCulture) % 4));
    }

    // Type

    [Fact]
    public void ThePageTitleOfTheWorkspaceIs28AndThePageTitleOfTheHeaderCardStays14()
    {
        string Size(string key) => Entries("Typography.xaml")[key].Elements(P + "Setter")
            .Single(s => (string?)s.Attribute("Property") == "FontSize").Attribute("Value")!.Value;
        Assert.Equal("28", Size("PageTitleLarge"));
        Assert.Equal("14", Size("PageTitle"));
        Assert.Equal("16", Size("CardTitleText"));
        Assert.Equal("20", Size("SectionTitle"));
    }

    // Glyphs

    private static readonly Dictionary<string, string> LabelToKey = new()
    {
        ["One Click"] = "Glyph.OneClick",
        ["Check"] = "Glyph.Check",
        ["Check Latest"] = "Glyph.CheckLatest",
        ["Check ConfigMgr"] = "Glyph.CheckConfigMgr",
        ["Check CVEs"] = "Glyph.CheckCves",
        ["Stage"] = "Glyph.Stage",
        ["Publish All"] = "Glyph.PublishAll",
        ["Columns"] = "Glyph.Columns",
        ["More"] = "Glyph.More",
        ["Add or import"] = "Glyph.AddOrImport",
        ["Open in Workbench"] = "Glyph.OpenInWorkbench"
    };

    [Fact]
    public void EveryGlyphOfTheTableIsASymbolOfTheLibrary()
    {
        var entries = Entries("Glyphs.xaml");
        Assert.Equal(LabelToKey.Values.OrderBy(k => k), entries.Keys.OrderBy(k => k));
        foreach (var (key, element) in entries)
            Assert.True(Enum.TryParse<SymbolRegular>(element.Value.Trim(), out _), $"{key}: {element.Value} is not a SymbolRegular.");
    }

    [Fact]
    public void EveryLabeledButtonOfTheTableShowsTheGlyphOfTheTable()
    {
        var table = Entries("Glyphs.xaml").ToDictionary(e => e.Key, e => e.Value.Value.Trim());
        var ui = Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI");
        var button = new Regex(@"<ui:(?:Button|SplitButton|DropDownButton)\b[^>]*>", RegexOptions.Singleline);
        var content = new Regex(@"\bContent=""([^""]*)""");
        var glyph = new Regex(@"\bIcon=""\{ui:SymbolIcon\s+([A-Za-z0-9]+)\}""");
        var seen = 0;
        var wrong = new List<string>();
        foreach (var file in Directory.EnumerateFiles(ui, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            foreach (Match tag in button.Matches(File.ReadAllText(file)))
            {
                var c = content.Match(tag.Value);
                var g = glyph.Match(tag.Value);
                if (!c.Success || !g.Success) continue;
                var label = c.Groups[1].Value.Replace("_", "").TrimEnd('.', '…').Trim();
                if (!LabelToKey.TryGetValue(label, out var key)) continue;
                seen++;
                if (table[key] != g.Groups[1].Value) wrong.Add($"{Path.GetRelativePath(ui, file)}: {label} shows {g.Groups[1].Value}, the table says {table[key]}");
            }
        }
        // The buttons of the mock name a glyph of the icon font of the mock.
        var mockTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["One Click"] = "play", ["Check"] = "checkmark_circle", ["Check latest"] = "arrow_sync", ["Check ConfigMgr"] = "server",
            ["Check CVEs"] = "shield", ["Stage"] = "box", ["Review publish"] = "arrow_right"
        };
        var mockButton = new Regex(@"<Button\b[^>]*>", RegexOptions.Singleline);
        var mockGlyph = new Regex(@"\bmk:Mk\.Icon=""([a-z_]+)""");
        foreach (var file in Directory.EnumerateFiles(ui, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
            foreach (Match tag in mockButton.Matches(File.ReadAllText(file)))
            {
                var c = content.Match(tag.Value);
                var g = mockGlyph.Match(tag.Value);
                if (!c.Success || !g.Success || !mockTable.TryGetValue(c.Groups[1].Value, out var expected)) continue;
                seen++;
                Assert.True(ApplicationPackagerUI.Controls.Mock.Ico.Knows(g.Groups[1].Value), $"The icon font has no glyph {g.Groups[1].Value}.");
                if (expected != g.Groups[1].Value) wrong.Add($"{Path.GetRelativePath(ui, file)}: {c.Groups[1].Value} shows {g.Groups[1].Value}, the table says {expected}");
            }
        }
        Assert.True(seen >= 12, $"Only {seen} labeled buttons were found.");
        Assert.True(wrong.Count == 0, string.Join(" | ", wrong));
    }

    // Cancel slot

    [Theory]
    [InlineData("Views/Pages/SettingsPage.xaml", "btnWsusCancel")]
    public void ACancelButtonOfAHostThatTakesTheSlotUsesTheStyle(string file, string id)
    {
        var text = File.ReadAllText(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", file));
        var tag = Regex.Matches(text, @"<ui:Button\b[^>]*>", RegexOptions.Singleline).Single(m => m.Value.Contains($"\"{id}\""));
        Assert.Contains("Style=\"{StaticResource CancelSlot}\"", tag.Value);
    }

    // The footer of the One Click window is that of the mock: Cancel shows only during a run. It is the first button of a row that is
    // aligned to the right, so Close and Run keep their place when it appears.
    [Fact]
    public void TheCancelButtonOfTheOneClickWindowMovesNoOtherButton()
    {
        var doc = XDocument.Load(Path.Combine(CatalogServiceTests.RepositoryRoot(), "src", "ApplicationPackagerUI", "Views", "Windows", "OneClickPlanWindow.xaml"));
        var cancel = doc.Descendants(P + "Button").Single(b => (string?)b.Attribute(X + "Name") == "btnCancelRun");

        Assert.Equal("{Binding CancelCommand}", (string?)cancel.Attribute("Command"));
        Assert.Contains("IsRunning", (string?)cancel.Attribute("Visibility"));
        Assert.Same(cancel, cancel.Parent!.Elements(P + "Button").First());
        Assert.EndsWith("DialogFrame.FootRight", cancel.Parent.Parent!.Name.LocalName);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheCancelSlotSharesTheTopAndTheBottomOfItsNeighborAndKeepsItsRoomWhileHidden(ApplicationTheme theme)
    {
        RenderKit.WithApplication(theme, () =>
        {
            var neighbor = new UiButton { Content = "Stage", Icon = new SymbolIcon(SymbolRegular.ArrowCircleDown24), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new UiButton { Content = "Cancel", Icon = new SymbolIcon(SymbolRegular.Dismiss24), VerticalAlignment = VerticalAlignment.Center };
            cancel.SetResourceReference(FrameworkElement.StyleProperty, "CancelSlot");
            var row = new StackPanel { Orientation = Orientation.Horizontal, Height = 48, Children = { neighbor, cancel } };
            row.UseLayoutRounding = true;

            void Lay()
            {
                row.Measure(new Size(400, 48));
                row.Arrange(new Rect(0, 0, 400, 48));
                row.UpdateLayout();
            }

            Lay();
            var (neighborTop, cancelTop) = (neighbor.TranslatePoint(new Point(0, 0), row).Y, cancel.TranslatePoint(new Point(0, 0), row).Y);
            Assert.Equal(neighborTop, cancelTop, 0.01);
            Assert.Equal(neighbor.ActualHeight, cancel.ActualHeight, 0.01);
            Assert.True(cancel.ActualWidth >= 90, "The Cancel slot is narrower than 90.");
            Assert.Equal(ControlAppearance.Danger, cancel.Appearance);

            var (neighborWidth, desired) = (neighbor.ActualWidth, row.DesiredSize);
            cancel.Visibility = Visibility.Hidden;
            Lay();
            Assert.Equal(neighborWidth, neighbor.ActualWidth);
            Assert.Equal(desired, row.DesiredSize);
        });
    }
}
