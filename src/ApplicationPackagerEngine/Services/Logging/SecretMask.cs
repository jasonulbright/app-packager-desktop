using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Logging;

/// <summary>Replaces secret values in free text before the text is stored anywhere.</summary>
public static partial class SecretMask
{
    public const string Mask = "***";

    private const string Keys = @"password|passwd|pwd|secret|clientsecret|client_secret|token|accesstoken|access_token|apikey|api_key|api-key|credential";
    private const string Value = @"(?<value>'[^']*'|""[^""]*""|[^\s,;&]+)";

    // A prefix such as DB_ or user and a suffix such as Value belong to the key; the key
    // must end at a non-word character, so TokenCount and Passwordless stay readable. The
    // optional quote after the key is the closing quote of a JSON property name.
    [GeneratedRegex(@"(?<key>(?<![\w-])[\w-]*?(?:" + Keys + @")(?:[_-]?(?:value|text|string))?(?![\w-]))(?<sep>""?\s*[=:]\s*)" + Value,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    // Only the PowerShell parameter form takes a space as the separator; prose such as
    // "enter the password in Settings" stays readable.
    [GeneratedRegex(@"(?<key>(?<![\w-])-(?:" + Keys + @"))(?<sep>\s+)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Parameter();

    [GeneratedRegex(@"(?<key>\bAuthorization""?\s*[=:]\s*(?:(?:Basic|Bearer|Digest|NTLM|Negotiate)\s+)?)(?<sep>)(?<value>[^\s,;""']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    // A bearer token holds a digit or token punctuation; "bearer instruments" is prose.
    [GeneratedRegex(@"(?<key>\bBearer\s+)(?<sep>)(?<value>(?=[A-Za-z0-9\-._~+/]*[0-9\-._~+/])[A-Za-z0-9\-._~+/]{8,}=*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    public static string Apply(string? text, IEnumerable<string>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (knownSecrets is not null)
            foreach (var secret in knownSecrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
                text = text.Replace(secret, Mask, StringComparison.Ordinal);
        text = AuthorizationHeader().Replace(text, Replace);
        text = BearerToken().Replace(text, Replace);
        text = Assignment().Replace(text, Replace);
        return Parameter().Replace(text, Replace);
    }

    private static string Replace(Match m)
    {
        var value = m.Groups["value"].Value;
        if (value == Mask || value is ['"', .., '"'] && value[1..^1] == Mask || value is ['\'', .., '\''] && value[1..^1] == Mask) return m.Value;
        var masked = value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[0] + Mask + value[0] : Mask;
        return m.Groups["key"].Value + m.Groups["sep"].Value + masked;
    }
}
