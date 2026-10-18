using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Logging;

/// <summary>Replaces secret values in free text before the text is stored anywhere.</summary>
public static partial class SecretMask
{
    public const string Mask = "***";

    private const string Keys = @"password|passwd|pwd|secret|clientsecret|client_secret|token|accesstoken|access_token|apikey|api_key|api-key|credential";
    private const string Value = @"(?<value>'[^']*'|""[^""]*""|[^\s,;&]+)";

    [GeneratedRegex(@"(?<key>\b(?:" + Keys + @")\b)(?<sep>\s*[=:]\s*)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    // Only the PowerShell parameter form takes a space as the separator; prose such as
    // "enter the password in Settings" stays readable.
    [GeneratedRegex(@"(?<key>(?<![\w-])-(?:" + Keys + @"))(?<sep>\s+)" + Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Parameter();

    public static string Apply(string? text, IEnumerable<string>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (knownSecrets is not null)
            foreach (var secret in knownSecrets.Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => s.Length))
                text = text.Replace(secret, Mask, StringComparison.Ordinal);
        text = Assignment().Replace(text, Replace);
        return Parameter().Replace(text, Replace);
    }

    private static string Replace(Match m) =>
        m.Groups["value"].Value == Mask ? m.Value : m.Groups["key"].Value + m.Groups["sep"].Value + Mask;
}
