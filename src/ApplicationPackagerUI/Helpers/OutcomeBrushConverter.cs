using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ApplicationPackagerUI.Helpers;

public enum OutcomeKind { None, Success, Caution, Critical }

/// <summary>
/// Gives the text of a status cell the theme brush of its outcome. The glyph in front of the text decides first; a text
/// without a glyph is read by its words. No other text changes color, so the cell keeps the foreground of its row.
/// </summary>
public sealed class OutcomeBrushConverter : IValueConverter
{
    public static OutcomeBrushConverter Instance { get; } = new();

    private static readonly string[] CriticalWords = ["fail", "error", "denied", "timed out", "rejected", "unhealthy"];
    private static readonly string[] CautionWords = ["warn", "partial", "skipped"];
    private static readonly string[] SuccessWords = ["completed", "succeeded", "success", "healthy", "approved", "passed", "verified", "installed"];

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

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = Classify(value as string) switch
        {
            OutcomeKind.Success => "SystemFillColorSuccessBrush",
            OutcomeKind.Caution => "SystemFillColorCautionBrush",
            OutcomeKind.Critical => "SystemFillColorCriticalBrush",
            _ => null
        };
        return key is not null && Application.Current?.TryFindResource(key) is { } brush ? brush : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
