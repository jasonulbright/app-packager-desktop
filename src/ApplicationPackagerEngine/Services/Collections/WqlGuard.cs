namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The checks a WQL expression and a rule name pass before they reach the SMS Provider.
/// </summary>
public static class WqlGuard
{
    public const int MaxLength = 16384;

    public const int MaxRuleNameLength = 256;

    /// <summary>
    /// Null when <paramref name="expression"/> is one read-only select statement; otherwise a
    /// plain reason. A backslash inside a quoted literal escapes the next character, as in WQL.
    /// </summary>
    public static string? Check(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "Enter a WQL query.";
        if (expression.Length > MaxLength) return $"The query is longer than {MaxLength} characters.";
        foreach (var c in expression)
        {
            if (c is '\t' or '\r' or '\n') continue;
            if (char.IsControl(c) || c is (char)0x2028 or (char)0x2029)
                return "The query contains a control character. Remove it and try again.";
        }

        var text = expression.TrimStart();
        if (text.Length < 7 || !text.StartsWith("select", StringComparison.OrdinalIgnoreCase) || !char.IsWhiteSpace(text[6]))
            return "Only a select query can be used here. The query must start with select.";

        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '\\')
                {
                    if (i + 1 >= text.Length) return "A quoted value in the query is not closed.";
                    i++;
                }
                else if (c == quote) quote = null;
                continue;
            }
            if (c is '\'' or '"') quote = c;
            else if (c == ';') return "The query can hold only one statement. Remove the semicolon.";
        }
        return quote is null ? null : "A quoted value in the query is not closed.";
    }

    /// <summary>
    /// Null when <paramref name="name"/> can name a new query rule. The rule cmdlets read
    /// * ? [ and ] in a rule name as wildcards, so a remove by such a name can match other rules.
    /// </summary>
    public static string? CheckRuleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a rule name.";
        if (name.Length > MaxRuleNameLength) return $"The rule name is longer than {MaxRuleNameLength} characters.";
        if (name.Any(char.IsControl)) return "The rule name contains a control character.";
        if (name.IndexOfAny(['*', '?', '[', ']']) >= 0) return "A rule name cannot contain * ? [ or ].";
        return null;
    }
}
