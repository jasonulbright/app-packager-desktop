using System.Text.RegularExpressions;

namespace ApplicationPackagerEngine.Services.Wsus;

/// <summary>
/// What an application declares about a WSUS publish of its default build:
/// "Yes", or "No (Code)" with the blocking refusal codes joined by '|'. The
/// plan reads it before a run; the publish itself still decides from the
/// staged build.
/// </summary>
public sealed partial record WsusSupportTag(bool Supported, IReadOnlyList<string> Codes)
{
    [GeneratedRegex(@"^\s*(Yes|No)\s*(?:\(\s*([A-Za-z|]+)\s*\))?\s*$")]
    private static partial Regex Shape();

    /// <summary>The codes joined with '|'; empty for a supported application.</summary>
    public string CodeText => string.Join("|", Codes);

    /// <summary>"Yes", "No (Code|Code)", or "No" for a refusal without a code.</summary>
    public string Text => Supported ? "Yes" : Codes.Count == 0 ? "No" : $"No ({CodeText})";

    /// <summary>
    /// Reads a declared value. Null for an absent value and for any text that
    /// is not "Yes" or "No" with an optional code list; the application then
    /// declares nothing.
    /// </summary>
    public static WsusSupportTag? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Shape().Match(text);
        if (!match.Success) return null;
        if (match.Groups[1].Value == "Yes") return match.Groups[2].Success ? null : new WsusSupportTag(true, []);
        var codes = match.Groups[2].Success
            ? match.Groups[2].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new WsusSupportTag(false, codes);
    }

    /// <summary>
    /// The value an application carries into the One Click plan: the codes of
    /// a declared refusal, "declared" for a refusal without a code, and empty
    /// when the application is supported or declares nothing.
    /// </summary>
    public static string NotSupportedCodes(string? declared) =>
        Parse(declared) is { Supported: false } tag ? (tag.Codes.Count > 0 ? tag.CodeText : "declared") : "";

    /// <summary>The tag for the blocking codes of a compatibility check, in the order found and each once.</summary>
    public static WsusSupportTag FromBlockingCodes(IEnumerable<string> codes)
    {
        var distinct = codes.Distinct(StringComparer.Ordinal).ToList();
        return new WsusSupportTag(distinct.Count == 0, distinct);
    }
}
