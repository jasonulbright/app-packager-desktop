using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ApplicationPackagerUI.Helpers;

public class StatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Green  = new(Color.FromRgb(0x2D, 0x7D, 0x46));
    private static readonly SolidColorBrush Blue   = new(Color.FromRgb(0x5B, 0x9B, 0xD5));
    private static readonly SolidColorBrush Amber  = new(Color.FromRgb(0xE8, 0xA3, 0x17));
    private static readonly SolidColorBrush Red    = new(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly SolidColorBrush Gray   = new(Color.FromRgb(0x99, 0x99, 0x99));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as string ?? "";

        return status switch
        {
            "Staged" or "Packaged" or "Up to date" or "Latest retrieved" or "ConfigMgr version retrieved" => Green,
            "Update available" => Amber,
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
