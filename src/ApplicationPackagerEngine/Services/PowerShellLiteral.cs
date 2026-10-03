using System.Text;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// Text placed inside generated PowerShell string literals. Windows PowerShell
/// ends a single-quoted string at any of five characters (U+0027, U+2018,
/// U+2019, U+201A, U+201B) and a double-quoted string at any of four (U+0022,
/// U+201C, U+201D, U+201E); escaping only the ASCII quote leaves the other
/// characters free to close the literal and start a statement.
/// </summary>
public static class PowerShellLiteral
{
    /// <summary>The characters that open and close a single-quoted string.</summary>
    public const string SingleQuoteCharacters = "'‘’‚‛";

    /// <summary>The characters that open and close a double-quoted string.</summary>
    public const string DoubleQuoteCharacters = "\"“”„";

    public static bool IsSingleQuote(char c) => SingleQuoteCharacters.Contains(c);

    /// <summary>
    /// The content of a single-quoted literal: every quote character is
    /// doubled, and the tokenizer reads a doubled quote character as that
    /// one character.
    /// </summary>
    public static string EscapeSingleQuoted(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.IndexOfAny(SingleQuoteCharacters.ToCharArray()) < 0) return value;
        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            sb.Append(c);
            if (IsSingleQuote(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A complete single-quoted literal for the value.</summary>
    public static string SingleQuoted(string? value) => "'" + EscapeSingleQuoted(value) + "'";

    /// <summary>
    /// The content of a double-quoted (expandable) string: the backtick,
    /// the dollar sign and every double-quote character take a backtick, so
    /// the value is neither expanded nor able to close the string.
    /// </summary>
    public static string EscapeDoubleQuoted(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (c is '`' or '$' || DoubleQuoteCharacters.Contains(c)) sb.Append('`');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
