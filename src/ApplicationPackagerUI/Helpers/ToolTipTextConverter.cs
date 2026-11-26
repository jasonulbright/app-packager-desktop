using System.Globalization;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

/// <summary>
/// The help text of a field from its tooltip. A tooltip is empty while a style applies before the field sets it, and it can hold an element in
/// place of a text. The help text is a text, so each of those gives an empty one and no binding error.
/// </summary>
public sealed class ToolTipTextConverter : IValueConverter
{
    public static ToolTipTextConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value as string ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
