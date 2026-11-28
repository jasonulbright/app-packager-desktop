using System.Globalization;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

/// <summary>What the cells of the App Packager grid show, as the mock names and draws it. The row keeps its own status text; the cell shows the label.</summary>
public static class PackagesCells
{
    /// <summary>The mark of an empty version cell.</summary>
    public const string NoValue = "—";

    public const string NotCheckedLabel = "Not checked";

    /// <summary>The label of a status: a row that nothing compared with the site reads "Not checked".</summary>
    public static string StatusLabel(string? status) =>
        PackagesTab.IsUncheckedStatus(status) ? NotCheckedLabel : status!;

    /// <summary>The glyph of a status: the mock's arrow, information, check and clock.</summary>
    public static string StatusIcon(string? status) => StatusKind(status) switch
    {
        "warn" => Services.CveRowState.IsUpdateAvailable(status) ? "arrow_up" : "warning",
        "info" => "info",
        "good" => "checkmark_circle",
        "bad" => "error_circle",
        _ => "clock"
    };

    /// <summary>The color class of a status: good, info, warn, bad or none.</summary>
    public static string StatusKind(string? status) => StatusCategory.Of(status) switch
    {
        StatusCategory.Success => "good",
        StatusCategory.Info => "info",
        StatusCategory.Warning => "warn",
        StatusCategory.Error => "bad",
        _ => ""
    };

    /// <summary>
    /// The two characters of the mark before the application name, for a row without an icon of the icon pack: the first letters of its
    /// first two words, else its first two letters.
    /// </summary>
    public static string MarkOf(string? name)
    {
        // "Notepad++" is two words in one.
        if (name?.StartsWith("Notepad++", StringComparison.OrdinalIgnoreCase) == true) return "NP";
        var words = (name ?? "").Split(new[] { ' ', '(', ')', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("x64" or "x86" or "ARM64" or "64" or "32" or "bit"))
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "";
        if (words.Count > 1 && char.IsLetter(words[1][0])) return $"{words[0][0]}{words[1][0]}".ToUpperInvariant();
        return (words[0].Length > 1 ? words[0][..2] : words[0]).ToUpperInvariant();
    }
}

/// <summary>Converts a row value for a cell. The parameter names the conversion: Dash, StatusLabel, StatusIcon or StatusKind.</summary>
public sealed class PackagesCellConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => parameter as string switch
    {
        "Dash" => string.IsNullOrEmpty(value as string) ? PackagesCells.NoValue : value!,
        "StatusLabel" => PackagesCells.StatusLabel(value as string),
        "StatusIcon" => PackagesCells.StatusIcon(value as string),
        "StatusKind" => PackagesCells.StatusKind(value as string),
        _ => value ?? ""
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}

/// <summary>True for a text that is not empty.</summary>
public sealed class HasTextConverter : IValueConverter
{
    public static readonly HasTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => System.Windows.Data.Binding.DoNothing;
}
