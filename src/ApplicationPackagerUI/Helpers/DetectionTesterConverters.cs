using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

public static class DetectionTesterConverters
{
    /// <summary>Visible only when every bound value is false.</summary>
    public static IMultiValueConverter NeitherVisible { get; } = new NeitherVisibleConverter();

    /// <summary>Visible when the bound value is false.</summary>
    public static IValueConverter NotVisible { get; } = new NotVisibleConverter();

    /// <summary>"Detected." or "Not detected." for a clause result.</summary>
    public static IValueConverter DetectedText { get; } = new DetectedTextConverter();

    private sealed class NeitherVisibleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.Any(v => v is true) ? Visibility.Collapsed : Visibility.Visible;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class NotVisibleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class DetectedTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? "Detected." : "Not detected.";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
