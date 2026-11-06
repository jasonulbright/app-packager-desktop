using System.Globalization;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

/// <summary>The status of a grid row as a color class: Success, Info, Warning, Error or Neutral.</summary>
public static class StatusCategory
{
    public const string Success = "Success", Info = "Info", Warning = "Warning", Error = "Error", Neutral = "Neutral";

    public static string Of(string? status) => status switch
    {
        "Staged" or "Packaged" or "Up to date" or "Latest retrieved" or "ConfigMgr version retrieved" => Success,
        var s when Services.CveRowState.IsUpdateAvailable(s) => Warning,
        "Not found in ConfigMgr" or "Current newer" => Info,
        var s when s is not null && s.Contains("error", StringComparison.OrdinalIgnoreCase) => Error,
        _ => Neutral
    };
}

/// <summary>Converts a status to its color class. A style with one trigger for each class sets the brush, so the brush follows the theme.</summary>
public class StatusCategoryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => StatusCategory.Of(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
