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
/// Puts operator values into the placeholders of a template. A value becomes a WQL string
/// literal: inside a literal of the template it takes that literal's quote character, otherwise
/// it is wrapped in single quotes. The quote character is doubled; a backslash is doubled
/// because WQL reads a backslash in a literal as an escape, so a value cannot end the literal.
/// </summary>
public static class WqlParameters
{
    public const int MaxLength = 256;
    public const string Required = "Enter a value for each parameter.";
    public const string ControlCharacter = "A parameter value cannot contain a line break, a tab or another control character.";
    public const string TooLong = "A parameter value can have at most 256 characters.";

    /// <summary>Why the value cannot be used, or null.</summary>
    public static string? Problem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Required;
        if (value.Any(char.IsControl)) return ControlCharacter;
        if (value.Length > MaxLength) return TooLong;
        return null;
    }

    /// <summary>The value as a single-quoted WQL string literal.</summary>
    public static string Literal(string value) => "'" + Escape(value, '\'') + "'";

    public static string Label(string placeholder) => placeholder.Trim().TrimStart('{').TrimEnd('}');

    public static string Placeholder(string name) => name.StartsWith('{') && name.EndsWith('}') ? name : "{" + name + "}";

    public static string Insert(string expression, IEnumerable<(string Placeholder, string Value)> values)
    {
        var list = values.Where(v => v.Placeholder.Length > 0).OrderByDescending(v => v.Placeholder.Length).ToList();
        foreach (var (_, value) in list)
            if (Problem(value) is { } problem) throw new ArgumentException(problem, nameof(values));
        var output = new StringBuilder(expression.Length + 32);
        var quote = '\0';
        for (var i = 0; i < expression.Length;)
        {
            var match = list.FirstOrDefault(v => string.CompareOrdinal(expression, i, v.Placeholder, 0, v.Placeholder.Length) == 0);
            if (match.Placeholder is not null)
            {
                if (quote == '\0') output.Append('\'').Append(Escape(match.Value, '\'')).Append('\'');
                else output.Append(Escape(match.Value, quote));
                i += match.Placeholder.Length;
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
            if (quote == '\0' && c is '\'' or '"') quote = c;
            else if (quote != '\0' && c == quote) quote = '\0';
            i++;
        }
        return output.ToString();
    }

    private static string Escape(string value, char quote) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(quote.ToString(), new string(quote, 2), StringComparison.Ordinal);
}
