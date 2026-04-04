using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// Converts a step number to Visibility. Shows the element only when CurrentStep matches the ConverterParameter.
/// Usage: Visibility="{Binding CurrentStep, Converter={StaticResource StepVisibilityConverter}, ConverterParameter=1}"
/// </summary>
public sealed class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int currentStep && parameter is string paramStr && int.TryParse(paramStr, out var targetStep))
            return currentStep == targetStep ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
