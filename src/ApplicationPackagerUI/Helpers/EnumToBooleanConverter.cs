using System.Globalization;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            // Settings that store their choice as a token string bind through the same converter.
            return targetType == typeof(string)
                ? parameter.ToString()!
                : Enum.Parse(targetType, parameter.ToString()!);
        }
        return Binding.DoNothing;
    }
}
