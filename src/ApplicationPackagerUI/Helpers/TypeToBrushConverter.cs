using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ApplicationPackagerUI.Helpers;

public class TypeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush JsonBrush = new(Color.FromRgb(0x2D, 0x7D, 0x46)); // Green
    private static readonly SolidColorBrush Ps1Brush = new(Color.FromRgb(0x5B, 0x6B, 0x7A));  // Gray-blue

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? JsonBrush : Ps1Brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
