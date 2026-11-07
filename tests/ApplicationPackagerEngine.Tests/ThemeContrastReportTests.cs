using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit.Abstractions;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace ApplicationPackagerEngine.Tests;

/// <summary>
/// Reads the brush pairs that the pages use, in the Light and Dark theme dictionaries, and reports
/// the WCAG 2.1 contrast ratio of each pair. Text needs 4.5 to 1, borders and focus rings need 3 to 1. This test
/// reports only. It fails when a brush cannot be read. A later test enforces the ratios.
/// </summary>
public sealed class ThemeContrastReportTests(ITestOutputHelper output)
{
    internal enum Kind { Text, LargeText, Component }

    internal sealed record Pair(string Use, string Foreground, string Background, Kind Kind);

    // The keys are the ones the XAML and the helpers reference. Each use names where the pair shows on a page.
    internal static readonly Pair[] Pairs =
    [
        new("Body text on the window", "TextFillColorPrimaryBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Secondary text (hints, captions) on the window", "TextFillColorSecondaryBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Tertiary text (notes, group captions) on the window", "TextFillColorTertiaryBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Disabled text on the window", "TextFillColorDisabledBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Body text on a card", "TextFillColorPrimaryBrush", "CardBackgroundFillColorDefaultBrush", Kind.Text),
        new("Secondary text on a card", "TextFillColorSecondaryBrush", "CardBackgroundFillColorDefaultBrush", Kind.Text),
        new("Tertiary text on a card", "TextFillColorTertiaryBrush", "CardBackgroundFillColorDefaultBrush", Kind.Text),
        new("Body text in a control (text box, grid row)", "TextFillColorPrimaryBrush", "ControlFillColorDefaultBrush", Kind.Text),
        new("Secondary text in a control", "TextFillColorSecondaryBrush", "ControlFillColorDefaultBrush", Kind.Text),
        new("Placeholder and hint text in a text box", "TextFillColorSecondaryBrush", "ControlFillColorDefaultBrush", Kind.Text),
        new("Text on the sidebar and layers", "TextFillColorPrimaryBrush", "LayerFillColorDefaultBrush", Kind.Text),
        new("Text on the second window surface", "TextFillColorPrimaryBrush", "SolidBackgroundFillColorSecondaryBrush", Kind.Text),
        new("Secondary text on the second window surface", "TextFillColorSecondaryBrush", "SolidBackgroundFillColorSecondaryBrush", Kind.Text),
        new("Text on a primary button", "TextOnAccentFillColorPrimaryBrush", "AccentFillColorDefaultBrush", Kind.Text),
        new("Link and accent text on the window", "AccentTextFillColorPrimaryBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Success text on the window", "SystemFillColorSuccessBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Warning text on the window (Update available, notes)", "SystemFillColorCautionBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Error text on the window", "SystemFillColorCriticalBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Information text on the window", "SystemFillColorAttentionBrush", "SolidBackgroundFillColorBaseBrush", Kind.Text),
        new("Warning text on a card", "SystemFillColorCautionBrush", "CardBackgroundFillColorDefaultBrush", Kind.Text),
        new("Border of a control on the window", "ControlStrokeColorDefaultBrush", "SolidBackgroundFillColorBaseBrush", Kind.Component),
        new("Border of a control on a card", "ControlStrokeColorDefaultBrush", "CardBackgroundFillColorDefaultBrush", Kind.Component),
        new("Border of a card on the window", "CardStrokeColorDefaultBrush", "SolidBackgroundFillColorBaseBrush", Kind.Component),
        new("Strong border (check box, radio button) on the window", "ControlStrongStrokeColorDefaultBrush", "SolidBackgroundFillColorBaseBrush", Kind.Component),
        new("Accent (focus ring, selected marker) on the window", "AccentFillColorDefaultBrush", "SolidBackgroundFillColorBaseBrush", Kind.Component),
        new("Accent (focus ring, selected marker) on a card", "AccentFillColorDefaultBrush", "CardBackgroundFillColorDefaultBrush", Kind.Component),
        new("Text box focus underline on a control", "AccentFillColorDefaultBrush", "ControlFillColorDefaultBrush", Kind.Component)
    ];

    // The status colors of the grid status column (StatusToBrushConverter): fixed in code, the same in both themes.
    private static readonly (string Name, Color Color)[] StatusColors =
    [
        ("Green (Staged, Packaged, Up to date)", Color.FromRgb(0x2D, 0x7D, 0x46)),
        ("Blue (Not found in ConfigMgr, Current newer)", Color.FromRgb(0x5B, 0x9B, 0xD5)),
        ("Amber (Update available)", Color.FromRgb(0xE8, 0xA3, 0x17)),
        ("Red (error)", Color.FromRgb(0xC0, 0x39, 0x2B)),
        ("Gray (Ready, running, unknown)", Color.FromRgb(0x99, 0x99, 0x99))
    ];

    private static double Channel(byte value)
    {
        var s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    internal static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    internal static double Ratio(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The color that shows when <paramref name="top"/> is drawn over the opaque <paramref name="under"/>.</summary>
    internal static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    // A key holds a brush or a plain color; the accent keys of the library are colors.
    internal static Color? Read(ResourceDictionary resources, string key) => resources[key] switch
    {
        SolidColorBrush brush => Color.FromArgb((byte)Math.Round(brush.Color.A * brush.Opacity), brush.Color.R, brush.Color.G, brush.Color.B),
        Color color => color,
        _ => null
    };

    // The Light and Dark theme files of the control library. The High Contrast files read the system colors of the machine
    // and are not loadable on their own; Windows High Contrast is measured on a lab machine in step 7 of the pass.
    private static readonly (string Name, string Dictionary)[] Themes =
    [
        ("Light", "<ui:ThemesDictionary Theme=\"Light\" />"),
        ("Dark", "<ui:ThemesDictionary Theme=\"Dark\" />")
    ];

    internal static ResourceDictionary Load(string themeDictionary)
    {
        // The same way the window tests load the theme: the XAML reader resolves the pack: address of the theme file.
        var markup =
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\">"
            + "<ResourceDictionary.MergedDictionaries>"
            + themeDictionary + "<ui:ControlsDictionary />"
            + "</ResourceDictionary.MergedDictionaries></ResourceDictionary>";
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(markup);
    }

    private static string Report(string theme, ResourceDictionary resources, List<string> unreadable)
    {
        if (Read(resources, "SolidBackgroundFillColorBaseBrush") is not { } window || Read(resources, "CardBackgroundFillColorDefaultBrush") is not { } cardFill)
        {
            unreadable.Add($"{theme}: the window or the card color");
            return $"### {theme}{Environment.NewLine}{Environment.NewLine}The window or the card color could not be read; no ratio is reported.{Environment.NewLine}{Environment.NewLine}";
        }
        var text = new StringBuilder();
        text.AppendLine($"### {theme}");
        text.AppendLine();
        text.AppendLine("| Use | Foreground | Background | Ratio | Need | Result |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var pair in Pairs)
        {
            var fg = Read(resources, pair.Foreground);
            var bg = Read(resources, pair.Background);
            if (fg is null || bg is null)
            {
                unreadable.Add($"{theme}: {pair.Use} ({pair.Foreground} on {pair.Background})");
                text.AppendLine($"| {pair.Use} | {pair.Foreground} | {pair.Background} | not read | | |");
                continue;
            }
            var under = Over(bg.Value, window);
            var shown = Over(fg.Value, under);
            var ratio = Ratio(shown, under);
            var need = pair.Kind == Kind.Component ? 3.0 : 4.5;
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {pair.Use} | {pair.Foreground} {Hex(shown)} | {pair.Background} {Hex(under)} | {ratio:0.00} | {need:0.0} | {(ratio >= need ? "pass" : "FAIL")} |"));
        }

        text.AppendLine();
        text.AppendLine("Status colors (text in the grid status column) on the window and on a card:");
        text.AppendLine();
        text.AppendLine("| Status color | On the window | On a card | Need | Result |");
        text.AppendLine("|---|---|---|---|---|");
        var card = Over(cardFill, window);
        foreach (var (name, color) in StatusColors)
        {
            var onWindow = Ratio(color, window);
            var onCard = Ratio(color, card);
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {name} {Hex(color)} | {onWindow:0.00} | {onCard:0.00} | 4.5 | {(Math.Min(onWindow, onCard) >= 4.5 ? "pass" : "FAIL")} |"));
        }

        var accent = Read(resources, "SystemAccentColor");
        var primaryText = Read(resources, "TextFillColorPrimaryBrush");
        text.AppendLine();
        if (accent is not { } accentColor || primaryText is not { } primaryColor)
        {
            unreadable.Add($"{theme}: SystemAccentColor or TextFillColorPrimaryBrush");
            text.AppendLine("Selected grid row: the accent or the primary text could not be read; no ratio is reported.");
        }
        else
        {
            var selected = Over(Color.FromArgb(61, accentColor.R, accentColor.G, accentColor.B), window);
            var primary = Over(primaryColor, selected);
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Selected grid row (accent tint 0.24 over the window): primary text {Ratio(primary, selected):0.00}, row against window {Ratio(selected, window):0.00}. Accent of the theme file: {Hex(accentColor)}; with `updateAccent: true` the Windows accent replaces it."));
        }
        text.AppendLine();
        return text.ToString();
    }

    // Windows default accent colors. The app asks Windows for the accent (updateAccent: true), so any of these can reach the pages.
    private static readonly (string Name, Color Color)[] Accents =
    [
        ("Gold", Color.FromRgb(0xFF, 0xB9, 0x00)), ("Orange", Color.FromRgb(0xF7, 0x63, 0x0C)), ("Red", Color.FromRgb(0xE8, 0x11, 0x23)),
        ("Rose", Color.FromRgb(0xE3, 0x00, 0x8C)), ("Purple", Color.FromRgb(0x88, 0x17, 0x98)), ("Blue (Windows default)", Color.FromRgb(0x00, 0x78, 0xD4)),
        ("Teal", Color.FromRgb(0x00, 0x99, 0xBC)), ("Green", Color.FromRgb(0x10, 0x7C, 0x10)), ("Gray", Color.FromRgb(0x76, 0x76, 0x76))
    ];

    // The window color that Mica gave on the lab machine CM01 in the light theme, read from a guide capture
    // (docs/images/core-settings-top.png, a pixel of the page area). The token window color of the theme is #F3F3F3.
    private static readonly Color MeasuredLightWindow = Color.FromRgb(0xF9, 0xF9, 0xF9);

    private static string AccentSweep()
    {
        var text = new StringBuilder();
        text.AppendLine("### Windows accent sweep");
        text.AppendLine();
        text.AppendLine("The accent is the Windows accent of the user. Each row is a Windows default accent as a focus ring or selected marker (needs 3.0), as the fill of a primary button with white text (needs 4.5), and as the fill of the selected grid row tint (0.24) with the primary text over it.");
        text.AppendLine();
        text.AppendLine("| Accent | On the light window | On the dark window | White text on it | Black text on it | Light row tint: text ratio |");
        text.AppendLine("|---|---|---|---|---|---|");
        foreach (var (name, accent) in Accents)
        {
            var tint = Over(Color.FromArgb(61, accent.R, accent.G, accent.B), MeasuredLightWindow);
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {name} {Hex(accent)} | {Ratio(accent, MeasuredLightWindow):0.00} | {Ratio(accent, Color.FromRgb(0x20, 0x20, 0x20)):0.00} | {Ratio(Colors.White, accent):0.00} | {Ratio(Colors.Black, accent):0.00} | {Ratio(Color.FromRgb(0x1A, 0x1A, 0x1A), tint):0.00} |"));
        }
        text.AppendLine();
        text.AppendLine("### Measured light window (Mica on CM01)");
        text.AppendLine();
        text.AppendLine($"The page area measured {Hex(MeasuredLightWindow)} in a lab capture; the theme token is #F3F3F3. Ratios of the text and status colors on the measured color:");
        text.AppendLine();
        text.AppendLine("| Color | On the measured window | Need | Result |");
        text.AppendLine("|---|---|---|---|");
        foreach (var (name, color) in new[] { ("Primary text #1A1A1A", Color.FromRgb(0x1A, 0x1A, 0x1A)), ("Secondary text #5C5C5C", Color.FromRgb(0x5C, 0x5C, 0x5C)), ("Tertiary text #868686", Color.FromRgb(0x86, 0x86, 0x86)) }
                     .Concat(StatusColors))
        {
            var ratio = Ratio(color, MeasuredLightWindow);
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {name} | {ratio:0.00} | 4.5 | {(ratio >= 4.5 ? "pass" : "FAIL")} |"));
        }
        text.AppendLine();
        return text.ToString();
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    [WindowFact]
    public void ReportsTheContrastOfEveryBrushPairInEveryTheme()
    {
        var unreadable = new List<string>();
        var report = new StringBuilder();
        report.AppendLine("# Contrast report");
        report.AppendLine();
        report.AppendLine("Ratios are WCAG 2.1 relative luminance ratios. A brush with alpha is composited over the window color of its theme. Mica draws the real window color from the wallpaper; the window color here is the solid fallback of the theme.");
        report.AppendLine();
        StaTestHelper.Run(() =>
        {
            foreach (var (name, dictionary) in Themes)
                report.Append(Report(name, Load(dictionary), unreadable));
        });

        report.Append(AccentSweep());

        var path = Environment.GetEnvironmentVariable("APD_CONTRAST_REPORT");
        if (string.IsNullOrWhiteSpace(path)) path = Path.Combine(AppContext.BaseDirectory, "contrast-report.md");
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        File.WriteAllText(path, report.ToString());
        output.WriteLine(report.ToString());

        Assert.True(unreadable.Count == 0, "These brushes could not be read: " + string.Join("; ", unreadable));
    }
}
