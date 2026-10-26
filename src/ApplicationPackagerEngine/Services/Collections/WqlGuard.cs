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

        // The SMS Provider rejects a doubled quote ('O''Brien'): it reads it as a closed value
        // followed by a new one. The same reading here keeps the guard's view of the literal
        // boundaries equal to the provider's.
        // A select inside parentheses is a subselect of an in clause; one outside them starts a
        // second statement.
        char? quote = null;
        var depth = 0;
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
                else if (c == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                        return $"Two quotes in a row end one value and start another, which WQL does not accept. Write a quote inside a value as \\{quote}.";
                    quote = null;
                }
                continue;
            }
            if (c is '\'' or '"') quote = c;
            else if (c == ';') return "The query can hold only one statement. Remove the semicolon.";
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-') return "The query cannot hold a comment. Remove -- and the text after it.";
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') return "The query cannot hold a comment. Remove /* and */ and the text between them.";
            else if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (i > 0 && depth == 0 && IsSelectAt(text, i))
                return "The query can hold only one statement. Remove the second select or put it in parentheses as part of an in clause.";
        }
        return quote is null ? null : "A quoted value in the query is not closed.";
    }

    private static bool IsSelectAt(string text, int i) =>
        i + 6 < text.Length
        && string.Compare(text, i, "select", 0, 6, StringComparison.OrdinalIgnoreCase) == 0
        && !IsWordChar(text[i - 1]) && (char.IsWhiteSpace(text[i + 6]) || text[i + 6] == '*');

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';

    /// <summary>
    /// Null when <paramref name="name"/> can name a query rule. The SMS Provider stores * ? [ and ]
    /// in a rule name, and the rule cmdlets match a name literally, so these characters are allowed.
    /// </summary>
    public static string? CheckRuleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a rule name.";
        if (name.Length > MaxRuleNameLength) return $"The rule name is longer than {MaxRuleNameLength} characters.";
        if (name.Any(char.IsControl)) return "The rule name contains a control character.";
        return null;
    }
}
