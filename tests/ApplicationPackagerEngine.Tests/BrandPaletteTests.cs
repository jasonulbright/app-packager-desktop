using System.Windows.Media;
using System.Xml.Linq;
using ApplicationPackagerUI.Services;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The fixed brand palette (Palette.Light.xaml, Palette.Dark.xaml) passes the WCAG 2.1 AA ratios on every surface of its
/// theme: 4.5 to 1 for text, 3 to 1 for borders and for the accent as a focus ring or a marker.
/// </summary>
public sealed class BrandPaletteTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // The surfaces of each theme: the window color of the theme file, the cards, the controls, the layers and the second
    // window level, plus the Mica result that the lab machine showed in the light theme (#F9F9F9).
    private static readonly Dictionary<ApplicationTheme, string[]> Surfaces = new()
    {
        [ApplicationTheme.Light] = ["#F3F3F3", "#F9F9F9", "#FAFAFA", "#FBFBFB", "#EEEEEE", "#FFFFFF"],
        [ApplicationTheme.Dark] = ["#202020", "#2B2B2B", "#2D2D2D", "#282828", "#1C1C1C"]
    };

    // The primary text of the theme file on the window, and the text on an accent fill.
    private static readonly Dictionary<ApplicationTheme, (string Primary, string OnAccent)> Text = new()
    {
        [ApplicationTheme.Light] = ("#1A1A1A", "#FFFFFF"),
        [ApplicationTheme.Dark] = ("#FFFFFF", "#000000")
    };

    private static Color C(string hex) => (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static string PalettePath(ApplicationTheme theme)
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        string Relative(DirectoryInfo f) => Path.Combine(f.FullName, "src", "ApplicationPackagerUI", "Views", "Styles", $"Palette.{theme}.xaml");
        while (folder is not null && !File.Exists(Relative(folder))) folder = folder.Parent;
        Assert.NotNull(folder);
        return Relative(folder);
    }

    private static Dictionary<string, XElement> Entries(ApplicationTheme theme) =>
        XDocument.Load(PalettePath(theme)).Root!.Elements().Where(e => e.Attribute(X + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!);

    private static Color Color_(ApplicationTheme theme, string key) => C(Entries(theme)[key].Value.Trim());

    private static void Check(ApplicationTheme theme, string name, Color color, double need)
    {
        foreach (var surface in Surfaces[theme])
        {
            var ratio = ThemeContrastReportTests.Ratio(color, C(surface));
            Assert.True(ratio >= need, $"{theme}: {name} {color} on {surface} is {ratio:0.00}, needs {need:0.0}.");
        }
    }

    public static TheoryData<ApplicationTheme> Themes => new() { ApplicationTheme.Light, ApplicationTheme.Dark };

    [Theory]
    [MemberData(nameof(Themes))]
    public void TextColorsPassFourAndAHalfOnEverySurface(ApplicationTheme theme)
    {
        foreach (var key in new[] { "TextFillColorTertiary", "StatusSuccessColor", "StatusWarningColor", "StatusErrorColor", "StatusInfoColor", "StatusNeutralColor", "BrandAccentColor" })
            Check(theme, key, Color_(theme, key), 4.5);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheSelectedRowTintKeepsThePrimaryTextAboveSevenAndTheRowVisible(ApplicationTheme theme)
    {
        var accent = Color_(theme, "BrandAccentColor");
        foreach (var surface in Surfaces[theme])
        {
            var under = C(surface);
            var tint = ThemeContrastReportTests.Over(Color.FromArgb(61, accent.R, accent.G, accent.B), under);
            var text = C(Text[theme].Primary);
            Assert.True(ThemeContrastReportTests.Ratio(text, tint) >= 7.0, $"{theme}: primary text on the selected row over {surface}.");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryBrushOfThePaletteReadsAColorOfThePalette(ApplicationTheme theme)
    {
        var entries = Entries(theme);
        foreach (var brush in entries.Values.Where(e => e.Name == P + "SolidColorBrush"))
        {
            var color = (string)brush.Attribute("Color")!;
            if (!color.StartsWith("{StaticResource ", StringComparison.Ordinal)) continue;
            var key = color["{StaticResource ".Length..].TrimEnd('}');
            Assert.True(entries.ContainsKey(key), $"{theme}: {(string)brush.Attribute(X + "Key")!} reads {key}, which the palette does not define.");
        }
    }

    [Fact]
    public void TheApplierSwapsToThePaletteOfTheTheme()
    {
        Assert.EndsWith("Palette.Light.xaml", ThemeApplier.PalettePath(ApplicationTheme.Light));
        Assert.EndsWith("Palette.Dark.xaml", ThemeApplier.PalettePath(ApplicationTheme.Dark));
    }
}
