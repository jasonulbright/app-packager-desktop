using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// The build fails when a brush pair of a page falls below WCAG 2.1 AA: 4.5 to 1 for text, 3 to 1 for borders and focus rings.
/// The brushes are the ones in effect in the app: the library theme file with the palette of the app on top.
/// Disabled text is exempt (WCAG 1.4.3 does not apply to inactive components). The accent pairs depend on the accent color
/// that the accent manager derives from the palette; BrandPaletteTests checks the accent itself.
/// </summary>
public sealed class ThemeContrastEnforcementTests
{
    public static TheoryData<ApplicationTheme> Themes => new() { ApplicationTheme.Light, ApplicationTheme.Dark };

    private static ResourceDictionary LoadInEffect(ApplicationTheme theme)
    {
        var library = ThemeContrastReportTests.Load($"<ui:ThemesDictionary Theme=\"{theme}\" />");
        ResourceDictionary palette;
        using (var stream = File.OpenRead(BrandPaletteTests.PalettePath(theme)))
            palette = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
        var inEffect = new ResourceDictionary();
        inEffect.MergedDictionaries.Add(library);
        inEffect.MergedDictionaries.Add(palette);
        return inEffect;
    }

    private static bool IsExempt(ThemeContrastReportTests.Pair pair) =>
        pair.Foreground.Contains("Disabled", StringComparison.Ordinal) || pair.Foreground.Contains("Accent", StringComparison.Ordinal)
        || pair.Background.Contains("Accent", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryBrushPairOfThePagesPassesTheRatios(ApplicationTheme theme)
    {
        var resources = LoadInEffect(theme);
        var window = ThemeContrastReportTests.Read(resources, "SolidBackgroundFillColorBaseBrush");
        Assert.NotNull(window);
        var failures = new List<string>();
        var checkedPairs = 0;
        foreach (var pair in ThemeContrastReportTests.Pairs.Where(p => !IsExempt(p)))
        {
            var fg = ThemeContrastReportTests.Read(resources, pair.Foreground);
            var bg = ThemeContrastReportTests.Read(resources, pair.Background);
            if (fg is null || bg is null) { failures.Add($"{pair.Use}: {pair.Foreground} or {pair.Background} is not readable."); continue; }
            var under = ThemeContrastReportTests.Over(bg.Value, window!.Value);
            var shown = ThemeContrastReportTests.Over(fg.Value, under);
            var ratio = ThemeContrastReportTests.Ratio(shown, under);
            var need = pair.Kind == ThemeContrastReportTests.Kind.Component ? 3.0 : 4.5;
            checkedPairs++;
            if (ratio < need) failures.Add($"{pair.Use}: {ratio:0.00}, needs {need:0.0}");
        }
        Assert.True(checkedPairs >= 15, $"Only {checkedPairs} pairs were checked.");
        Assert.True(failures.Count == 0, $"{theme}: " + string.Join("; ", failures));
    }
}
