using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ApplicationPackagerUI.Helpers;

public class StatusToBrushConverter : IValueConverter
{
    // The fallback colors are the light palette. They show when the application resources have no palette, as in a test host.
    // With a palette, the converter returns the brush of the palette: it follows the theme when the theme changes.
    private static readonly SolidColorBrush FallbackGreen = Frozen(Color.FromRgb(0x0B, 0x6A, 0x0B));
    private static readonly SolidColorBrush FallbackBlue = Frozen(Color.FromRgb(0x00, 0x57, 0x9E));
    private static readonly SolidColorBrush FallbackAmber = Frozen(Color.FromRgb(0x8A, 0x4B, 0x00));
    private static readonly SolidColorBrush FallbackRed = Frozen(Color.FromRgb(0xB3, 0x26, 0x1E));
    private static readonly SolidColorBrush FallbackGray = Frozen(Color.FromRgb(0x5C, 0x5C, 0x5C));

    private static Brush Themed(string key, Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static Brush Green => Themed("StatusSuccessBrush", FallbackGreen);
    private static Brush Blue => Themed("StatusInfoBrush", FallbackBlue);
    private static Brush Amber => Themed("StatusWarningBrush", FallbackAmber);
    private static Brush Red => Themed("StatusErrorBrush", FallbackRed);
    private static Brush Gray => Themed("StatusNeutralBrush", FallbackGray);

    /// <summary>A shared brush that is not frozen belongs to the thread that made it; an element on another UI thread then throws when it renders.</summary>
    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string ?? "";

        return status switch
        {
            "Staged" or "Packaged" or "Up to date" or "Latest retrieved" or "ConfigMgr version retrieved" => Green,
            var s when Services.CveRowState.IsUpdateAvailable(s) => Amber,
            "Not found in ConfigMgr" or "Current newer" => Blue,
            "Staging..." or "Packaging..." or "Checking latest..." or "Querying ConfigMgr..." => Gray,
            var s when s.Contains("error", StringComparison.OrdinalIgnoreCase) => Red,
            "Ready" or "Missing metadata (Vendor/App)" => Gray,
            _ => Gray
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
