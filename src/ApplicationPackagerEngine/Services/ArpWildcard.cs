using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services;

/// <summary>
/// The ArpName and ArpVersion wildcard syntax of PowerShell -like: * any run, ? one
/// character, [abc] and [a-z] one character of a set, a backtick escapes the next
/// character. Matching ignores case with invariant culture rules.
/// </summary>
public static class ArpWildcard
{
    public const string None = "none";

    /// <summary>True for an empty value or "none", which no inventory row matches.</summary>
    public static bool IsNone(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), None, StringComparison.OrdinalIgnoreCase);

    /// <summary>The patterns of a " | " list, without empty parts and "none".</summary>
    public static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('|').Select(p => p.Trim()).Where(p => p.Length > 0 && !IsNone(p)).ToList();

    /// <summary>The number of characters before the first wildcard character.</summary>
    public static int LiteralPrefixLength(string pattern)
    {
        var index = pattern.IndexOfAny(['*', '?', '[']);
        return index < 0 ? pattern.Length : index;
    }

    /// <summary>
    /// Builds an anchored regular expression for <paramref name="pattern"/>. False for an
    /// unclosed, empty or reversed character set, which PowerShell rejects on first use.
    /// </summary>
    public static bool TryCreate(string pattern, [NotNullWhen(true)] out Regex? regex)
    {
        regex = null;
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                case '`' when i + 1 < pattern.Length:
                    sb.Append(Regex.Escape(pattern[++i].ToString()));
                    break;
                case '[':
                    {
                        var set = new StringBuilder();
                        var j = i + 1;
                        var closed = false;
                        for (; j < pattern.Length; j++)
                        {
                            var d = pattern[j];
                            if (d == ']') { closed = true; break; }
                            if (d == '`' && j + 1 < pattern.Length) { set.Append(EscapeSetChar(pattern[++j])); continue; }
                            if (d == '-' && set.Length > 0 && j + 1 < pattern.Length && pattern[j + 1] != ']') { set.Append('-'); continue; }
                            set.Append(EscapeSetChar(d));
                        }
                        if (!closed || set.Length == 0) return false;
                        sb.Append('[').Append(set).Append(']');
                        i = j;
                        break;
                    }
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append(@"\z");
        try
        {
            regex = new Regex(sb.ToString(),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            // A reversed range such as [z-a] passes the set scan and fails only in the regex parser.
            return false;
        }
        return true;
    }

    private static string EscapeSetChar(char c) => c is '\\' or ']' or '[' or '^' or '-' ? "\\" + c : c.ToString();
}
