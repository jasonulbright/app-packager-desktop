using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

public enum OutcomeKind { None, Success, Caution, Critical }

/// <summary>
/// Gives the outcome of a status cell text. The glyph in front of the text decides first; a text without a glyph is read by its words.
/// Styles pick the theme brush of each outcome with a DynamicResource, so the brush follows a theme change.
/// </summary>
public sealed class OutcomeKindConverter : IValueConverter
{
    public static OutcomeKindConverter Instance { get; } = new();

    private static readonly string[] CriticalWords = ["fail", "error", "denied", "timed out", "rejected", "unhealthy"];
    private static readonly string[] CautionWords = ["warn", "partial", "skipped"];
    private static readonly string[] SuccessWords = ["completed", "succeeded", "success", "healthy", "approved", "passed", "verified", "installed",
        "changed", "done", "requested", "confirmed", "submitted", "removed", "sent"];

    public static OutcomeKind Classify(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0) return OutcomeKind.None;
        switch (value[0])
        {
            case '✓' or '✔': return OutcomeKind.Success;
            case '✗' or '✖' or '✘': return OutcomeKind.Critical;
            case '!' or '▲' or '⚠': return OutcomeKind.Caution;
            case '⋯' or '?': return OutcomeKind.None;
        }
        var lower = value.ToLowerInvariant();
        if (CriticalWords.Any(lower.Contains)) return OutcomeKind.Critical;
        if (CautionWords.Any(lower.Contains)) return OutcomeKind.Caution;
        return SuccessWords.Any(w => lower.StartsWith(w, StringComparison.Ordinal)) ? OutcomeKind.Success : OutcomeKind.None;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Classify(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
