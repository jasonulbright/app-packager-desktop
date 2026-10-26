using System.Globalization;
using System.Text;
using ApplicationPackagerEngine.Services;

namespace ApplicationPackagerUI.ViewModels.Pages;

/// <summary>Plain texts of the Collection and Compliance page.</summary>
public static class CollectionsText
{
    public static string Target(CollectionRow row) => Target(row.Name, row.CollectionId);

    public static string Target(string name, string id) => string.IsNullOrEmpty(id) ? name : $"{name} ({id})";

    public static string Count(int n, string noun, string? plural = null) =>
        n == 1 ? $"1 {noun}" : $"{n.ToString(CultureInfo.InvariantCulture)} {plural ?? noun + "s"}";

    public static string Rules(CollectionRow row)
    {
        var parts = new List<string>();
        if (row.QueryRuleCount > 0) parts.Add($"{row.QueryRuleCount} query");
        if (row.DirectRuleCount > 0) parts.Add($"{row.DirectRuleCount} direct");
        if (row.IncludeRuleCount > 0) parts.Add($"{row.IncludeRuleCount} include");
        if (row.ExcludeRuleCount > 0) parts.Add($"{row.ExcludeRuleCount} exclude");
        return parts.Count == 0 ? "None" : string.Join(", ", parts);
    }

    public static string Limiting(CollectionRow row) =>
        string.IsNullOrEmpty(row.LimitingCollectionId) ? "" : Target(row.LimitingCollectionName, row.LimitingCollectionId);

    public static string OrNone(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    public static string Every(RefreshInterval interval)
    {
        var unit = interval.Unit.TrimEnd('s').ToLowerInvariant();
        return interval.Count == 1 ? $"every 1 {unit}" : $"every {interval.Count.ToString(CultureInfo.InvariantCulture)} {unit}s";
    }

    /// <summary>
    /// The interval of a single ConfigMgr interval schedule token (recurrence flag 2: minutes,
    /// hours and days spans at bits 13, 8 and 3); null for any other token or a token with more
    /// than one span set.
    /// </summary>
    public static RefreshInterval? IntervalOf(string? token)
    {
        if (token is null || token.Length != 16
            || !ulong.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var v))
            return null;
        int Bits(int shift, int width) => (int)((v >> shift) & ((1UL << width) - 1));
        if (Bits(19, 3) != 2) return null;
        var spans = new[] { (Bits(13, 6), "Minutes"), (Bits(8, 5), "Hours"), (Bits(3, 5), "Days") }.Where(s => s.Item1 > 0).ToList();
        return spans.Count == 1 ? new RefreshInterval(spans[0].Item1, spans[0].Item2) : null;
    }
}

/// <summary>One row of the Collections grid.</summary>
public sealed record CollectionGridRow(CollectionRow Row)
{
    public string Name => Row.Name;
    public string CollectionId => Row.CollectionId;
    public int MemberCount => Row.MemberCount;
    public string LimitingText => CollectionsText.Limiting(Row);
    public string RefreshType => Row.RefreshType;
    public string FolderPath => Row.FolderPath;
    public bool IsBuiltIn => Row.IsBuiltIn;
    public string TypeText => Row.IsBuiltIn ? "Built-in" : "Custom";
    public string RulesText => CollectionsText.Rules(Row);

    public override string ToString() => CollectionsText.Target(Row);
}

/// <summary>One member of the Direct Members tab.</summary>
public sealed record MemberRow(CollectionMember Member)
{
    public string Name => Member.Name;
    public string ResourceId => Member.ResourceId;
    public string ClientState => Member.ClientState;
    public bool IsDirect => Member.IsDirect;
    public string Membership => Member.IsDirect ? "Direct" : "From a rule";

    public override string ToString() => $"{Name} ({ResourceId}), {Membership}";
}

/// <summary>One rule of a detail tab or of the WQL Editor's rule list.</summary>
public sealed record RuleRow(CollectionRule Rule, string Text)
{
    public string RuleName => Rule.RuleName;
    public string Expression => Rule.Expression;
    public CollectionRuleKind Kind => Rule.Kind;

    public override string ToString() => Text;
}

/// <summary>A folder or a collection of the WQL Editor's collection tree.</summary>
public sealed partial class CollectionTreeNode(string text, CollectionGridRow? collection) : ObservableObject
{
    public string Text { get; } = text;
    public CollectionGridRow? Collection { get; } = collection;
    public bool IsFolder => Collection is null;
    public ObservableCollection<CollectionTreeNode> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    public override string ToString() => IsFolder ? $"Folder {Text}" : Text;
}

/// <summary>One row of the preview grid; columns bind to the indexer.</summary>
public sealed class WqlPreviewRow(IReadOnlyList<string> values)
{
    public IReadOnlyList<string> Values { get; } = values;

    public string this[int index] => index >= 0 && index < Values.Count ? Values[index] : "";

    public override string ToString() => string.Join(", ", Values);
}

/// <summary>One row of the Templates grid.</summary>
public sealed record TemplateItemRow(QueryTemplate Template)
{
    public string Name => Template.Name;
    public string Category => Template.Category;
    public string Description => Template.Description;
    public string Source => Template.IsShipped ? "Shipped" : "User";
    public string ParameterText => Template.Parameters.Count == 0 ? "None" : Template.Parameters.Count.ToString(CultureInfo.InvariantCulture);
    public string LimitingText => Template.LimitingCollectionName;

    public override string ToString() => $"{Name}, {Category}";
}

/// <summary>A value the operator gives for a placeholder of a parameterized template.</summary>
public sealed partial class TemplateParameterRow(string placeholder, string label, string helpText, string value) : ObservableObject
{
    public string Placeholder { get; } = placeholder;
    public string Label { get; } = label;
    public string HelpText { get; } = helpText;

    [ObservableProperty] private string _value = value;

    public override string ToString() => Label;
}

/// <summary>One row of the Site Queries grid.</summary>
public sealed record SavedQueryRow(SavedQuery Query)
{
    public string Name => Query.Name;
    public string QueryId => Query.QueryId;
    public string TargetClassName => Query.TargetClassName;
    public string Comment => Query.Comment;
    public string Expression => Query.Expression;

    public override string ToString() => $"{Name} ({QueryId})";
}

/// <summary>
/// Puts operator values into the placeholders of a template, by the place of each placeholder
/// in the template text. Inside a quoted literal the value keeps that literal's quote character;
/// WQL reads a backslash in a literal as an escape, so a backslash and the quote character get
/// a backslash in front and a value cannot end the literal. Inside a literal compared with like,
/// % _ and [ are wildcards and are refused. Outside a literal the value must be a whole number,
/// or whole numbers separated by commas directly inside "in ( )".
/// </summary>
public static class WqlParameters
{
    public const int MaxLength = 256;
    public const string Required = "Enter a value for each parameter.";
    public const string ControlCharacter = "A parameter value cannot contain a line break, a tab or another control character.";
    public const string TooLong = "A parameter value can have at most 256 characters.";

    public static string WholeNumber(string label) => $"Enter a whole number for {label}, for example 14.";

    public static string WholeNumbers(string label) => $"Enter whole numbers separated by commas for {label}, for example 8,9,10.";

    public static string Wildcard(string label, char wildcard) =>
        $"The value for {label} cannot contain {wildcard}. The query compares it with like, where {wildcard} is a wildcard.";

    private const int MaxDigits = 18;

    private enum Place { Bare, List, Literal, LikeLiteral }

    /// <summary>Why the value cannot be used anywhere, or null.</summary>
    public static string? Problem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Required;
        if (value.Any(c => char.IsControl(c) || c is (char)0x2028 or (char)0x2029)) return ControlCharacter;
        if (value.Length > MaxLength) return TooLong;
        return null;
    }

    /// <summary>The value as a single-quoted WQL string literal.</summary>
    public static string Literal(string value) => "'" + Escape(value, '\'') + "'";

    public static string Label(string placeholder) => placeholder.Trim().TrimStart('{').TrimEnd('}');

    public static string Placeholder(string name) => name.StartsWith('{') && name.EndsWith('}') ? name : "{" + name + "}";

    /// <summary>The template with the values inserted; throws <see cref="ArgumentException"/> with the plain reason when a value does not fit its place.</summary>
    public static string Insert(string expression, IEnumerable<(string Placeholder, string Value)> values)
    {
        var (wql, problem) = Build(expression, values.Select(v => (v.Placeholder, Label(v.Placeholder), v.Value)));
        return wql ?? throw new ArgumentException(problem, nameof(values));
    }

    /// <summary>The template with the values inserted, or the plain reason the first value that does not fit its place cannot be used.</summary>
    public static (string? Wql, string? Problem) Build(string expression, IEnumerable<(string Placeholder, string Label, string Value)> values)
    {
        var list = values.Where(v => v.Placeholder.Length > 0).OrderByDescending(v => v.Placeholder.Length).ToList();
        foreach (var v in list)
            if (Problem(v.Value) is { } problem) return (null, problem);
        var output = new StringBuilder(expression.Length + 32);
        var quote = '\0';
        var like = false;
        for (var i = 0; i < expression.Length;)
        {
            var match = list.FirstOrDefault(v => string.CompareOrdinal(expression, i, v.Placeholder, 0, v.Placeholder.Length) == 0);
            if (match.Placeholder is not null)
            {
                var end = i + match.Placeholder.Length;
                var place = quote != '\0' ? like ? Place.LikeLiteral : Place.Literal
                    : InList(expression, i, end) ? Place.List : Place.Bare;
                var (text, problem) = Value(match.Value, match.Label, place, quote);
                if (text is null) return (null, problem);
                output.Append(text);
                i = end;
                continue;
            }
            var c = expression[i];
            output.Append(c);
            if (quote != '\0' && c == '\\' && i + 1 < expression.Length)
            {
                output.Append(expression[i + 1]);
                i += 2;
                continue;
            }
            if (quote == '\0' && c is '\'' or '"')
            {
                quote = c;
                like = PrecedingWord(expression, i).Equals("like", StringComparison.OrdinalIgnoreCase);
            }
            else if (quote != '\0' && c == quote) quote = '\0';
            i++;
        }
        return (output.ToString(), null);
    }

    private static (string? Text, string? Problem) Value(string value, string label, Place place, char quote)
    {
        if (place == Place.Bare)
            return WholeNumberText(value) is { } number ? (number, null) : (null, WholeNumber(label));
        if (place == Place.List)
        {
            var items = value.Split(',').Select(item => WholeNumberText(Unquoted(item.Trim()))).ToList();
            return items.Any(item => item is null) ? (null, WholeNumbers(label)) : (string.Join(",", items.Select(item => $"'{item}'")), null);
        }
        var wildcard = value.IndexOfAny(['%', '_', '[']);
        if (place == Place.LikeLiteral && wildcard >= 0) return (null, Wildcard(label, value[wildcard]));
        return (Escape(value, quote), null);
    }

    private static string Unquoted(string item) =>
        item.Length >= 2 && item[0] == item[^1] && item[0] is '\'' or '"' ? item[1..^1] : item;

    private static string? WholeNumberText(string value)
    {
        var text = value.Trim();
        return text.Length is > 0 and <= MaxDigits && text.All(char.IsAsciiDigit)
            ? long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The word that ends before <paramref name="index"/>, white space skipped; empty when a symbol comes first.</summary>
    private static string PrecedingWord(string text, int index)
    {
        var end = index;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        return text[start..end];
    }

    /// <summary>True when the placeholder from <paramref name="start"/> to <paramref name="end"/> is the only content of "in ( )".</summary>
    private static bool InList(string text, int start, int end)
    {
        var before = start;
        while (before > 0 && char.IsWhiteSpace(text[before - 1])) before--;
        if (before == 0 || text[before - 1] != '(') return false;
        var after = end;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        return after < text.Length && text[after] == ')' && PrecedingWord(text, before - 1).Equals("in", StringComparison.OrdinalIgnoreCase);
    }

    private static string Escape(string value, char quote) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(quote.ToString(), "\\" + quote, StringComparison.Ordinal);
}
