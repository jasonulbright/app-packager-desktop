using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

public static class MaintenanceWindowsConverters
{
    /// <summary>Visible when the bound value is null.</summary>
    public static IValueConverter NullVisible { get; } = new NullConverter(true);

    /// <summary>Visible when the bound value is not null.</summary>
    public static IValueConverter NotNullVisible { get; } = new NullConverter(false);

    private sealed class NullConverter(bool visibleWhenNull) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            (value is null) == visibleWhenNull ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
